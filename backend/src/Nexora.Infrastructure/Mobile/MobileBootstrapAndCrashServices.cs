using Microsoft.EntityFrameworkCore;
using Nexora.Application.Abstractions;
using Nexora.Application.Intelligence;
using Nexora.Application.Mobile;
using Nexora.Application.Notifications;
using Nexora.Contracts.Mobile;
using Nexora.Contracts.Notifications;
using Nexora.Domain.Common;
using Nexora.Infrastructure.Persistence;

namespace Nexora.Infrastructure.Mobile;

/// <summary>Diagnostics minimisés (Phase 44 §46) — journal d'ajout seul, comme <c>assistant_query</c> (Phase 43).</summary>
public sealed class SqlCrashReportService(NexoraDbContext db, ITenantContext tenant, IUnitOfWork uow) : ICrashReportService
{
    private Guid Company => tenant.ActiveCompanyId ?? throw new InvalidOperationException("Entreprise active requise.");

    private sealed record Row(Guid Id, Guid? DeviceId, string AppVersion, string Os, string Message, DateTimeOffset OccurredAt);

    public async Task<Result<CrashReportDto>> ReportAsync(ReportCrashRequest request, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(request.Message)) return Error.Validation("crash_report.message", "Message requis.");

        var id = Guid.NewGuid();
        await db.Database.ExecuteSqlAsync($"""
            INSERT INTO nexora.crash_report
                (id, company_id, device_id, app_version, os, os_version, correlation_id, message, stack_excerpt)
            VALUES ({id}, {Company}, {request.DeviceId}, {request.AppVersion}, {request.Os}, {request.OsVersion},
                    {request.CorrelationId}, {request.Message}, {request.StackExcerpt ?? ""})
            """, ct);
        await uow.SaveChangesAsync(ct);

        var occurredAt = await db.Database.SqlQuery<DateTimeOffset>($"SELECT occurred_at AS \"Value\" FROM nexora.crash_report WHERE id = {id}").FirstAsync(ct);
        return Result.Success(new CrashReportDto(id, request.DeviceId, request.AppVersion, request.Os, request.Message, occurredAt));
    }

    public async Task<IReadOnlyList<CrashReportDto>> ListAsync(int limit, CancellationToken ct = default)
    {
        var take = Math.Clamp(limit, 1, 500);
        var rows = await db.Database.SqlQueryRaw<Row>("""
            SELECT id AS "Id", device_id AS "DeviceId", app_version AS "AppVersion", os AS "Os", message AS "Message", occurred_at AS "OccurredAt"
            FROM nexora.crash_report WHERE company_id = {0} ORDER BY occurred_at DESC LIMIT {1}
            """, Company, take).ToListAsync(ct);
        return rows.Select(r => new CrashReportDto(r.Id, r.DeviceId, r.AppVersion, r.Os, r.Message, r.OccurredAt)).ToList();
    }
}

/// <summary>
/// Configuration & bootstrap mobile (Phase 44 §25, §48). <see cref="ConfigAsync"/> lit la politique
/// offline existante (Phase 10) sans la redéfinir. <see cref="PullAsync"/> compose des services déjà
/// présents (livraisons, interventions, tâches Phase 43, notifications Phase 34) filtrés au
/// périmètre strict de l'appelant — jamais une nouvelle requête d'agrégation dupliquée.
/// </summary>
public sealed class SqlMobileBootstrapService(
    NexoraDbContext db, ITenantContext tenant, IDateTimeProvider clock,
    IDeliveryService delivery, IFieldInterventionService interventions, ITaskService tasks, INotificationService notifications)
    : IMobileBootstrapService
{
    private Guid Company => tenant.ActiveCompanyId ?? throw new InvalidOperationException("Entreprise active requise.");
    private sealed record PolicyRow(bool AllowOfflineSales, bool AllowOfflineCredit, decimal OfflineCreditLimit, int MaxOfflineMinutes);

    public async Task<MobileConfigDto> ConfigAsync(CancellationToken ct = default)
    {
        var p = await db.Database.SqlQuery<PolicyRow>($"""
            SELECT allow_offline_sales AS "AllowOfflineSales", allow_offline_credit AS "AllowOfflineCredit",
                   offline_credit_limit AS "OfflineCreditLimit", max_offline_minutes AS "MaxOfflineMinutes"
            FROM nexora.offline_policy WHERE company_id = {Company}
            """).FirstOrDefaultAsync(ct) ?? new PolicyRow(true, false, 0m, 720);

        return new MobileConfigDto(p.AllowOfflineSales, p.AllowOfflineCredit, p.OfflineCreditLimit, p.MaxOfflineMinutes,
            ["sale", "delivery_stop", "field_intervention"], PushEnabled: true, MinAppVersion: "1.0.0");
    }

    public async Task<MobileBootstrapDto> PullAsync(CancellationToken ct = default)
    {
        var stops = await delivery.MyStopsAsync(ct);
        var mine = await interventions.MineAsync(ct);
        var myTasks = (await tasks.ListAsync("open", ct)).Where(t => t.AssignedTo == tenant.UserId).ToList();
        var page = await notifications.ListAsync(new NotificationQuery(PageSize: 1), ct);
        return new MobileBootstrapDto(stops, mine, myTasks, page.Unread, clock.UtcNow);
    }
}
