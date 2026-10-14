using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Nexora.Application.Abstractions;
using Nexora.Application.Operations;
using Nexora.Contracts.Operations;
using Nexora.Domain.Common;
using Nexora.Infrastructure.Persistence;

namespace Nexora.Infrastructure.Operations;

/// <summary>
/// Fenêtres de maintenance planifiée (Phase 41 §23-24, §51) — table plateforme
/// <c>maintenance_window</c>. État machine <c>planned → notified → backed_up → in_progress →
/// verifying → closed</c> (ou <c>canceled</c> avant le démarrage) ; une seule fenêtre
/// <c>in_progress</c> à la fois (index unique partiel §24) — c'est elle que consulte
/// <see cref="Nexora.Api.Middleware.MaintenanceModeMiddleware"/> pour dégrader l'API.
/// </summary>
public sealed class SqlMaintenanceService(NexoraDbContext db, ITenantContext tenant, IUnitOfWork uow, IPlatformOperationGuard guard)
    : IMaintenanceService
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    private static readonly Dictionary<string, string[]> Transitions = new()
    {
        ["planned"] = ["notified", "canceled"],
        ["notified"] = ["backed_up", "canceled"],
        ["backed_up"] = ["in_progress", "canceled"],
        ["in_progress"] = ["verifying"],
        ["verifying"] = ["closed", "in_progress"],
        ["closed"] = [],
        ["canceled"] = [],
    };

    private sealed record WindowRow(Guid Id, DateTimeOffset ScheduledAt, int DurationMinutes, string Impact,
        string Services, string? Responsible, string? RollbackPlan, string Status,
        DateTimeOffset? StartedAt, DateTimeOffset? EndedAt, string? Notes);

    private const string Select = """
        SELECT id AS "Id", scheduled_at AS "ScheduledAt", duration_minutes AS "DurationMinutes", impact AS "Impact",
               services::text AS "Services", responsible AS "Responsible", rollback_plan AS "RollbackPlan",
               status AS "Status", started_at AS "StartedAt", ended_at AS "EndedAt", notes AS "Notes"
        FROM nexora.maintenance_window
        """;

    public async Task<IReadOnlyList<MaintenanceWindowDto>> ListAsync(CancellationToken ct = default)
    {
        using var _ = tenant.BeginSystemScope();
        var rows = await db.Database.SqlQueryRaw<WindowRow>(Select + " ORDER BY scheduled_at DESC LIMIT 100").ToListAsync(ct);
        return rows.Select(ToDto).ToList();
    }

    public async Task<Result<MaintenanceWindowDto>> ScheduleAsync(ScheduleMaintenanceRequest request, CancellationToken ct = default)
    {
        if (request.DurationMinutes <= 0) return Error.Validation("maintenance.duration", "Durée invalide.");
        if (string.IsNullOrWhiteSpace(request.Impact)) return Error.Validation("maintenance.impact", "Impact requis.");
        // Une fenêtre en cours suspend les écritures de TOUTES les entreprises (audit 2026-10, A-031).
        if (await guard.RequirePlatformWideAsync(Nexora.Application.Security.Permissions.Operations.Manage, ct) is { } refused) return refused;

        using var _ = tenant.BeginSystemScope();
        var id = Guid.NewGuid();
        var servicesJson = JsonSerializer.Serialize(request.Services, Web);
        await db.Database.ExecuteSqlAsync($"""
            INSERT INTO nexora.maintenance_window
                (id, scheduled_at, duration_minutes, impact, services, responsible, rollback_plan, notes, created_by)
            VALUES ({id}, {request.ScheduledAt}, {request.DurationMinutes}, {request.Impact}, {servicesJson}::jsonb,
                    {request.Responsible}, {request.RollbackPlan}, {request.Notes}, {tenant.UserId})
            """, ct);
        await uow.SaveChangesAsync(ct);

        var saved = await db.Database.SqlQueryRaw<WindowRow>(Select + " WHERE id = {0}", id).FirstAsync(ct);
        return Result.Success(ToDto(saved));
    }

    public async Task<Result<MaintenanceWindowDto>> TransitionAsync(Guid id, TransitionMaintenanceRequest request, CancellationToken ct = default)
    {
        var target = request.Status.Trim().ToLowerInvariant();
        if (!Transitions.ContainsKey(target)) return Error.Validation("maintenance.status", "Statut invalide.");
        if (await guard.RequirePlatformWideAsync(Nexora.Application.Security.Permissions.Operations.Manage, ct) is { } refused) return refused;

        using var _ = tenant.BeginSystemScope();
        var current = await db.Database.SqlQueryRaw<WindowRow>(Select + " WHERE id = {0}", id).FirstOrDefaultAsync(ct);
        if (current is null) return Error.NotFound("maintenance.not_found", "Fenêtre de maintenance introuvable.");
        if (!Transitions.TryGetValue(current.Status, out var allowed) || !allowed.Contains(target))
            return Error.Conflict("maintenance.invalid_transition", $"Transition « {current.Status} → {target} » non autorisée.");

        if (target == "in_progress")
        {
            var alreadyActive = await db.Database.SqlQuery<bool>($"""
                SELECT EXISTS(SELECT 1 FROM nexora.maintenance_window WHERE status = 'in_progress' AND id <> {id}) AS "Value"
                """).FirstAsync(ct);
            if (alreadyActive) return Error.Conflict("maintenance.already_active", "Une fenêtre de maintenance est déjà en cours.");
        }

        await db.Database.ExecuteSqlAsync($"""
            UPDATE nexora.maintenance_window
            SET status = {target},
                notes = COALESCE({request.Notes}, notes),
                started_at = CASE WHEN {target} = 'in_progress' THEN now() ELSE started_at END,
                ended_at   = CASE WHEN {target} = 'closed'      THEN now() ELSE ended_at   END,
                updated_at = now()
            WHERE id = {id}
            """, ct);
        await uow.SaveChangesAsync(ct);

        var saved = await db.Database.SqlQueryRaw<WindowRow>(Select + " WHERE id = {0}", id).FirstAsync(ct);
        return Result.Success(ToDto(saved));
    }

    public async Task<bool> IsActiveAsync(CancellationToken ct = default)
    {
        using var _ = tenant.BeginSystemScope();
        return await db.Database.SqlQuery<bool>($"""
            SELECT EXISTS(SELECT 1 FROM nexora.maintenance_window WHERE status = 'in_progress') AS "Value"
            """).FirstAsync(ct);
    }

    private static MaintenanceWindowDto ToDto(WindowRow r) => new(r.Id, r.ScheduledAt, r.DurationMinutes, r.Impact,
        JsonSerializer.Deserialize<List<string>>(r.Services, Web) ?? [], r.Responsible, r.RollbackPlan, r.Status,
        r.StartedAt, r.EndedAt, r.Notes);
}
