using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Nexora.Application.Abstractions;
using Nexora.Application.Operations;
using Nexora.Contracts.Operations;
using Nexora.Domain.Common;
using Nexora.Infrastructure.Persistence;

namespace Nexora.Infrastructure.Operations;

/// <summary>
/// Rétention &amp; purge de données strictement techniques (Phase 41 §16-19, §22, §46). La liste
/// des types purgeables est fermée — <b>jamais de donnée métier ou comptable</b> (§17). Chaque
/// exécution est précédée d'un aperçu à blanc (<see cref="PreviewAsync"/>, aucune écriture) et
/// laisse une trace dans <c>purge_run</c> même en aperçu.
///
/// Protections concrètes appliquées par type (§18-19) :
///   completed_job_runs      — seuls les passages <c>succeeded</c> sont purgeables ; les
///                              <c>failed</c> passés le seuil restent (postmortem) → comptés à part.
///   resolved_notifications  — seules les notifications archivées ou expirées sont purgeables ;
///                              une notification non lue et non archivée n'est jamais supprimée.
/// </summary>
public sealed class SqlRetentionPurgeService(NexoraDbContext db, ITenantContext tenant, IUnitOfWork uow, IDateTimeProvider clock,
    IPlatformOperationGuard guard)
    : IRetentionPurgeService
{
    private const string Manage = Nexora.Application.Security.Permissions.Operations.Manage;

    /// <summary>
    /// Seules les notifications se purgent par entreprise ; sessions, défis MFA et historiques de jobs sont des
    /// données de l'installation entière. Une opération qui dépasse l'entreprise active exige l'exploitant (A-031).
    /// </summary>
    private bool OwnCompanyOnly(string dataType, Guid? companyId)
        => dataType == "resolved_notifications" && companyId is { } c && c == tenant.ActiveCompanyId;

    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);
    private static readonly string[] DataTypes =
        ["expired_sessions", "expired_mfa_challenges", "completed_job_runs", "resolved_notifications"];

    // ------------------------------------------------------------- Politiques de rétention (§17, §22)

    private sealed record PolicyRow(Guid Id, string DataType, Guid? CompanyId, int DurationDays,
        string Action, string? LegalReference, DateTimeOffset UpdatedAt);

    private const string PolicySelect = """
        SELECT id AS "Id", data_type AS "DataType", company_id AS "CompanyId", duration_days AS "DurationDays",
               action AS "Action", legal_reference AS "LegalReference", updated_at AS "UpdatedAt"
        FROM nexora.retention_policy
        """;

    public async Task<IReadOnlyList<RetentionPolicyDto>> ListPoliciesAsync(CancellationToken ct = default)
    {
        var platformWide = await guard.RequirePlatformWideAsync(Manage, ct) is null;
        using var _ = tenant.BeginSystemScope();
        var rows = await db.Database.SqlQueryRaw<PolicyRow>(PolicySelect + " ORDER BY data_type, company_id NULLS FIRST").ToListAsync(ct);
        return rows.Where(r => platformWide || r.CompanyId is null || r.CompanyId == tenant.ActiveCompanyId)
            .Select(ToPolicyDto).ToList();
    }

    public async Task<Result<RetentionPolicyDto>> SavePolicyAsync(SaveRetentionPolicyRequest request, CancellationToken ct = default)
    {
        var dataType = request.DataType.Trim().ToLowerInvariant();
        if (!DataTypes.Contains(dataType)) return Error.Validation("retention.data_type", "Type de donnée invalide.");
        if (request.DurationDays <= 0) return Error.Validation("retention.duration", "Durée invalide.");
        var action = request.Action.Trim().ToLowerInvariant();
        if (action is not ("archive" or "delete")) return Error.Validation("retention.action", "Action invalide (archive/delete).");
        if (!OwnCompanyOnly(dataType, request.CompanyId) && await guard.RequirePlatformWideAsync(Manage, ct) is { } refused)
            return refused;

        using var _ = tenant.BeginSystemScope();
        var id = Guid.NewGuid();
        await db.Database.ExecuteSqlAsync($"""
            INSERT INTO nexora.retention_policy (id, data_type, company_id, duration_days, action, legal_reference, created_by)
            VALUES ({id}, {dataType}, {request.CompanyId}, {request.DurationDays}, {action}, {request.LegalReference}, {tenant.UserId})
            ON CONFLICT ON CONSTRAINT retention_policy_uk DO UPDATE SET
                duration_days = EXCLUDED.duration_days, action = EXCLUDED.action,
                legal_reference = EXCLUDED.legal_reference, updated_at = now()
            """, ct);
        await uow.SaveChangesAsync(ct);

        var saved = await db.Database.SqlQueryRaw<PolicyRow>(
            PolicySelect + " WHERE data_type = {0} AND company_id IS NOT DISTINCT FROM {1}",
            dataType, (object?)request.CompanyId ?? DBNull.Value)
            .FirstAsync(ct);
        return Result.Success(ToPolicyDto(saved));
    }

    private async Task<int> EffectiveDurationDaysAsync(string dataType, Guid? companyId, CancellationToken ct)
    {
        using var _ = tenant.BeginSystemScope();
        return await db.Database.SqlQuery<int>($"""
            SELECT duration_days AS "Value" FROM nexora.retention_policy
            WHERE data_type = {dataType} AND (company_id = {companyId} OR company_id IS NULL)
            ORDER BY company_id NULLS LAST LIMIT 1
            """).FirstAsync(ct);
    }

    private async Task<string> EffectiveActionAsync(string dataType, Guid? companyId, CancellationToken ct)
    {
        using var _ = tenant.BeginSystemScope();
        return await db.Database.SqlQuery<string>($"""
            SELECT action AS "Value" FROM nexora.retention_policy
            WHERE data_type = {dataType} AND (company_id = {companyId} OR company_id IS NULL)
            ORDER BY company_id NULLS LAST LIMIT 1
            """).FirstAsync(ct);
    }

    // ------------------------------------------------------------- Purge (§16-19)

    private sealed record RunRow(Guid Id, string DataType, Guid? CompanyId, bool DryRun, DateTimeOffset CutoffDate,
        int ObjectCount, int ProtectedExcludedCount, string Status, Guid? ExecutedBy, DateTimeOffset? ExecutedAt);

    private const string RunSelect = """
        SELECT id AS "Id", data_type AS "DataType", company_id AS "CompanyId", dry_run AS "DryRun",
               cutoff_date AS "CutoffDate", object_count AS "ObjectCount",
               protected_excluded_count AS "ProtectedExcludedCount", status AS "Status",
               executed_by AS "ExecutedBy", executed_at AS "ExecutedAt"
        FROM nexora.purge_run
        """;

    public async Task<Result<PurgeRunDto>> PreviewAsync(PurgePreviewRequest request, CancellationToken ct = default)
    {
        var dataType = request.DataType?.Trim().ToLowerInvariant() ?? "";
        if (!DataTypes.Contains(dataType)) return Error.Validation("purge.data_type", "Type de donnée invalide.");
        // Les comptages couvrent toute l'installation hors purge de notifications de l'entreprise active (A-031).
        if (!OwnCompanyOnly(dataType, request.CompanyId) && await guard.RequirePlatformWideAsync(Manage, ct) is { } refused)
            return refused;
        return Result.Success(await RunAsync(request, dryRun: true, ct));
    }

    public async Task<Result<PurgeRunDto>> ExecuteAsync(PurgePreviewRequest request, CancellationToken ct = default)
    {
        var dataType = request.DataType?.Trim().ToLowerInvariant() ?? "";
        if (!DataTypes.Contains(dataType)) return Error.Validation("purge.data_type", "Type de donnée invalide.");
        if (!OwnCompanyOnly(dataType, request.CompanyId) && await guard.RequirePlatformWideAsync(Manage, ct) is { } refused)
            return refused;
        return Result.Success(await RunAsync(request, dryRun: false, ct));
    }

    private async Task<PurgeRunDto> RunAsync(PurgePreviewRequest request, bool dryRun, CancellationToken ct)
    {
        var dataType = request.DataType.Trim().ToLowerInvariant();
        using var _ = tenant.BeginSystemScope();

        // La date limite ne dépasse jamais celle de la politique (audit 2026-10 éd. 2) : une date future fournie par
        // l'appelant purgeait tout ce qui était expiré ou archivé, en deçà de la durée de conservation.
        var policyCutoff = clock.UtcNow.AddDays(-await EffectiveDurationDaysAsync(dataType, request.CompanyId, ct));
        var cutoff = request.CutoffDate is { } asked && asked < policyCutoff ? asked : policyCutoff;
        var action = await EffectiveActionAsync(dataType, request.CompanyId, ct);
        var companyId = request.CompanyId;

        var (objectCount, protectedCount) = dataType switch
        {
            "expired_sessions" => (await CountAsync($"""
                    SELECT COUNT(*)::int AS "Value" FROM nexora.user_session
                    WHERE (expires_at IS NOT NULL AND expires_at < {cutoff}) OR (revoked_at IS NOT NULL AND revoked_at < {cutoff})
                    """, ct), 0),

            "expired_mfa_challenges" => (await CountAsync($"""
                    SELECT COUNT(*)::int AS "Value" FROM nexora.mfa_challenge WHERE expires_at < {cutoff}
                    """, ct), 0),

            "completed_job_runs" => (
                await CountAsync($"""
                    SELECT COUNT(*)::int AS "Value" FROM nexora.job_run
                    WHERE status = 'succeeded' AND finished_at IS NOT NULL AND finished_at < {cutoff}
                    """, ct),
                await CountAsync($"""
                    SELECT COUNT(*)::int AS "Value" FROM nexora.job_run
                    WHERE status = 'failed' AND finished_at IS NOT NULL AND finished_at < {cutoff}
                    """, ct)),

            "resolved_notifications" => (
                await CountAsync($"""
                    SELECT COUNT(*)::int AS "Value" FROM nexora.notification
                    WHERE ((archived_at IS NOT NULL AND archived_at < {cutoff}) OR (expires_at IS NOT NULL AND expires_at < {cutoff}))
                      AND ({companyId}::uuid IS NULL OR company_id = {companyId})
                    """, ct),
                await CountAsync($"""
                    SELECT COUNT(*)::int AS "Value" FROM nexora.notification
                    WHERE read_at IS NULL AND archived_at IS NULL AND created_at < {cutoff}
                      AND ({companyId}::uuid IS NULL OR company_id = {companyId})
                    """, ct)),

            _ => throw new InvalidOperationException("Type de donnée inconnu."),
        };

        var status = "previewed";
        if (!dryRun)
        {
            // Politique « archive » (avant : ignorée, tout était supprimé) : les notifications sont archivées, jamais
            // supprimées ; pour les autres données techniques, archiver n'a pas de sens — rien n'est supprimé.
            if (action == "archive")
            {
                if (dataType == "resolved_notifications")
                    await db.Database.ExecuteSqlAsync($"""
                        UPDATE nexora.notification SET archived_at = now()
                        WHERE archived_at IS NULL AND ((expires_at IS NOT NULL AND expires_at < {cutoff}) OR (read_at IS NOT NULL AND created_at < {cutoff}))
                          AND ({companyId}::uuid IS NULL OR company_id = {companyId})
                        """, ct);
            }
            else
                await ExecuteDeleteAsync(dataType, cutoff, companyId, ct);
            status = "executed";
        }

        var id = Guid.NewGuid();
        var journal = JsonSerializer.Serialize(new { dataType, cutoff, action, objectCount, protectedCount, companyId }, Web);
        Guid? executedBy = dryRun ? null : tenant.UserId;
        DateTimeOffset? executedAt = dryRun ? null : clock.UtcNow;
        await db.Database.ExecuteSqlAsync($"""
            INSERT INTO nexora.purge_run
                (id, data_type, company_id, dry_run, cutoff_date, object_count, protected_excluded_count, status,
                 executed_by, executed_at, journal)
            VALUES ({id}, {dataType}, {companyId}, {dryRun}, {cutoff}, {objectCount}, {protectedCount}, {status},
                    {executedBy}, {executedAt}, {journal}::jsonb)
            """, ct);
        await uow.SaveChangesAsync(ct);

        return new PurgeRunDto(id, dataType, companyId, dryRun, cutoff, objectCount, protectedCount, status, executedBy, executedAt);
    }

    private async Task ExecuteDeleteAsync(string dataType, DateTimeOffset cutoff, Guid? companyId, CancellationToken ct)
    {
        switch (dataType)
        {
            case "expired_sessions":
                await db.Database.ExecuteSqlAsync($"""
                    DELETE FROM nexora.user_session
                    WHERE (expires_at IS NOT NULL AND expires_at < {cutoff}) OR (revoked_at IS NOT NULL AND revoked_at < {cutoff})
                    """, ct);
                break;
            case "expired_mfa_challenges":
                await db.Database.ExecuteSqlAsync($"DELETE FROM nexora.mfa_challenge WHERE expires_at < {cutoff}", ct);
                break;
            case "completed_job_runs":
                await db.Database.ExecuteSqlAsync($"""
                    DELETE FROM nexora.job_run WHERE status = 'succeeded' AND finished_at IS NOT NULL AND finished_at < {cutoff}
                    """, ct);
                break;
            case "resolved_notifications":
                await db.Database.ExecuteSqlAsync($"""
                    DELETE FROM nexora.notification
                    WHERE ((archived_at IS NOT NULL AND archived_at < {cutoff}) OR (expires_at IS NOT NULL AND expires_at < {cutoff}))
                      AND ({companyId}::uuid IS NULL OR company_id = {companyId})
                    """, ct);
                break;
        }
    }

    public async Task<IReadOnlyList<PurgeRunDto>> HistoryAsync(CancellationToken ct = default)
    {
        var platformWide = await guard.RequirePlatformWideAsync(Manage, ct) is null;
        using var _ = tenant.BeginSystemScope();
        var rows = await db.Database.SqlQueryRaw<RunRow>(RunSelect + " ORDER BY created_at DESC LIMIT 100").ToListAsync(ct);
        return rows.Where(r => platformWide || r.CompanyId == tenant.ActiveCompanyId).Select(ToRunDto).ToList();
    }

    /// <summary>Purge automatique selon les politiques système dues, au plus une fois par jour (§46).</summary>
    public async Task<int> RunDueAsync(CancellationToken ct = default)
    {
        using var _ = tenant.BeginSystemScope();
        var total = 0;
        foreach (var dataType in DataTypes)
        {
            var lastRun = await db.Database.SqlQuery<DateTimeOffset?>($"""
                SELECT MAX(created_at) AS "Value" FROM nexora.purge_run
                WHERE data_type = {dataType} AND dry_run = false AND company_id IS NULL
                """).FirstAsync(ct);
            if (lastRun is { } lr && lr > clock.UtcNow.AddDays(-1)) continue;

            var run = await RunAsync(new PurgePreviewRequest(dataType, null, null), dryRun: false, ct);
            total += run.ObjectCount;
        }
        return total;
    }

    private async Task<int> CountAsync(FormattableString sql, CancellationToken ct) => await db.Database.SqlQuery<int>(sql).FirstAsync(ct);

    private static RetentionPolicyDto ToPolicyDto(PolicyRow r) =>
        new(r.Id, r.DataType, r.CompanyId, r.DurationDays, r.Action, r.LegalReference, r.UpdatedAt);

    private static PurgeRunDto ToRunDto(RunRow r) =>
        new(r.Id, r.DataType, r.CompanyId, r.DryRun, r.CutoffDate, r.ObjectCount, r.ProtectedExcludedCount, r.Status, r.ExecutedBy, r.ExecutedAt);
}
