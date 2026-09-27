using Microsoft.EntityFrameworkCore;
using Nexora.Application.Abstractions;
using Nexora.Application.Notifications;
using Nexora.Application.Platform;
using Nexora.Contracts.Notifications;
using Nexora.Contracts.Platform;
using Nexora.Domain.Common;
using Nexora.Infrastructure.Persistence;

namespace Nexora.Infrastructure.Platform;

// =============================================================================
// Quotas & usage (Phase 38 §21-24). Limites = plan + surcharge négociée + add-ons.
// Usage compté par ressource et par mois ; alertes à 80 / 90 / 100 % (§23).
// =============================================================================
public sealed class SqlQuotaService(
    NexoraDbContext db,
    IUnitOfWork uow,
    IDateTimeProvider clock,
    INotificationService notifications) : IQuotaService
{
    public static readonly string[] Resources =
    [
        "users", "companies", "establishments", "terminals", "transactions",
        "storage_mb", "api_requests", "webhooks", "exports", "documents", "automations",
    ];

    private sealed record SubQuotaRow(Guid Id, string PlanCode, string PlanQuotas, string? QuotasOverride);
    private sealed record AddonQuotaRow(string? QuotaKey, long QuotaDelta, int Quantity);
    private sealed record UsageRow(string Resource, long Value, int LastAlertPct);

    public async Task<Result<QuotaReportDto>> GetReportAsync(Guid customerId, CancellationToken ct = default)
    {
        var sub = await LoadSubAsync(customerId, ct);
        if (sub is null) return Error.NotFound("platform.subscription", "Aucun abonnement actif pour ce client.");

        var limits = await EffectiveLimitsAsync(sub, ct);
        var usage = await UsageAsync(customerId, ct);
        var monthStart = MonthStart();

        var lines = Resources.Select(res =>
        {
            var limit = limits.GetValueOrDefault(res, 0);
            var used = usage.TryGetValue(res, out var u) ? u.Value : 0;
            var pct = limit > 0 ? Math.Round(100m * used / limit, 1) : 0m;
            return new QuotaLineDto(res, limit, used, pct, limit > 0 && used > limit);
        }).ToList();

        return Result.Success(new QuotaReportDto(customerId, sub.PlanCode, monthStart, lines));
    }

    public async Task<QuotaCheckResult> CheckAsync(Guid customerId, string resource, long requested, CancellationToken ct = default)
    {
        if (!Resources.Contains(resource))
            return new QuotaCheckResult(false, resource, 0, 0, requested, "Ressource inconnue.");

        var sub = await LoadSubAsync(customerId, ct);
        if (sub is null)
            return new QuotaCheckResult(false, resource, 0, 0, requested, "Aucun abonnement actif — souscrivez un plan.");

        var limits = await EffectiveLimitsAsync(sub, ct);
        var limit = limits.GetValueOrDefault(resource, 0);
        var usage = await UsageAsync(customerId, ct);
        var used = usage.TryGetValue(resource, out var u) ? u.Value : 0;

        if (limit <= 0) return new QuotaCheckResult(true, resource, 0, used, requested, "Illimité pour ce plan.");
        if (used + requested > limit)
            return new QuotaCheckResult(false, resource, limit, used, requested,
                $"Quota {resource} atteint ({used}/{limit}). Ajoutez un add-on ou changez de plan.");
        return new QuotaCheckResult(true, resource, limit, used, requested, null);
    }

    public async Task<Result> RecordUsageAsync(Guid customerId, string resource, long delta, CancellationToken ct = default)
    {
        if (!Resources.Contains(resource)) return Error.Validation("platform.resource", "Ressource inconnue.");
        var monthStart = MonthStart();

        long newValue = 0;
        await uow.ExecuteInTransactionAsync(async token =>
        {
            await db.Database.ExecuteSqlAsync($"""
                INSERT INTO nexora.usage_counter (customer_id, resource, period_start, value)
                VALUES ({customerId}, {resource}, {monthStart}, GREATEST(0, {delta}))
                ON CONFLICT ON CONSTRAINT usage_counter_uk
                DO UPDATE SET value = GREATEST(0, nexora.usage_counter.value + {delta}), updated_at = now()
                """, token);
            newValue = await db.Database.SqlQuery<long>($"""
                SELECT value AS "Value" FROM nexora.usage_counter
                WHERE customer_id = {customerId} AND resource = {resource} AND period_start = {monthStart}
                """).FirstAsync(token);
            return 0;
        }, ct);
        await uow.SaveChangesAsync(ct);

        if (delta > 0) await MaybeAlertAsync(customerId, resource, newValue, ct);
        return Result.Success();
    }

    // ------------------------------------------------------------- Helpers

    private static DateOnly MonthStart() => new(DateTime.UtcNow.Year, DateTime.UtcNow.Month, 1);

    private async Task<SubQuotaRow?> LoadSubAsync(Guid customerId, CancellationToken ct)
        => await db.Database.SqlQuery<SubQuotaRow>($"""
            SELECT s.id AS "Id", p.code AS "PlanCode", p.quotas::text AS "PlanQuotas", s.quotas_override::text AS "QuotasOverride"
            FROM nexora.subscription s JOIN nexora.platform_plan p ON p.id = s.plan_id
            WHERE s.customer_id = {customerId} AND s.status IN ('trialing','active','past_due','grace')
            ORDER BY s.created_at DESC LIMIT 1
            """).FirstOrDefaultAsync(ct);

    private async Task<Dictionary<string, long>> EffectiveLimitsAsync(SubQuotaRow sub, CancellationToken ct)
    {
        var limits = PlatformJson.Quotas(sub.PlanQuotas);
        if (!string.IsNullOrWhiteSpace(sub.QuotasOverride) && sub.QuotasOverride != "null")
            foreach (var (k, v) in PlatformJson.Quotas(sub.QuotasOverride)) limits[k] = v;

        var addons = await db.Database.SqlQuery<AddonQuotaRow>($"""
            SELECT a.quota_key AS "QuotaKey", a.quota_delta AS "QuotaDelta", sa.quantity AS "Quantity"
            FROM nexora.subscription_addon sa JOIN nexora.platform_addon a ON a.id = sa.addon_id
            WHERE sa.subscription_id = {sub.Id} AND sa.removed_at IS NULL AND a.quota_key IS NOT NULL
            """).ToListAsync(ct);
        foreach (var a in addons)
            if (a.QuotaKey is { } key && limits.TryGetValue(key, out var cur) && cur > 0)
                limits[key] = cur + a.QuotaDelta * a.Quantity;
            else if (a.QuotaKey is { } k2)
                limits[k2] = a.QuotaDelta * a.Quantity;

        return limits;
    }

    private async Task<Dictionary<string, UsageRow>> UsageAsync(Guid customerId, CancellationToken ct)
    {
        var rows = await db.Database.SqlQuery<UsageRow>($"""
            SELECT resource AS "Resource", value AS "Value", last_alert_pct AS "LastAlertPct"
            FROM nexora.usage_counter WHERE customer_id = {customerId} AND period_start = {MonthStart()}
            """).ToListAsync(ct);
        return rows.ToDictionary(r => r.Resource, r => r);
    }

    private async Task MaybeAlertAsync(Guid customerId, string resource, long value, CancellationToken ct)
    {
        var sub = await LoadSubAsync(customerId, ct);
        if (sub is null) return;
        var limits = await EffectiveLimitsAsync(sub, ct);
        var limit = limits.GetValueOrDefault(resource, 0);
        if (limit <= 0) return;

        var pct = (int)(100L * value / limit);
        var threshold = pct >= 100 ? 100 : pct >= 90 ? 90 : pct >= 80 ? 80 : 0;
        if (threshold == 0) return;

        var usage = await UsageAsync(customerId, ct);
        var last = usage.TryGetValue(resource, out var u) ? u.LastAlertPct : 0;
        if (threshold <= last) return;

        await db.Database.ExecuteSqlAsync($"""
            UPDATE nexora.usage_counter SET last_alert_pct = {threshold}, updated_at = now()
            WHERE customer_id = {customerId} AND resource = {resource} AND period_start = {MonthStart()}
            """, ct);
        await PlatformInvoices.LogEventAsync(db, customerId, null, PlatformEvents.QuotaThresholdReached,
            new { resource, value, limit, threshold }, null, clock.UtcNow, ct);
        await uow.SaveChangesAsync(ct);

        try
        {
            var users = await db.Database.SqlQuery<Guid>($"""
                SELECT DISTINCT ur.user_id AS "Value"
                FROM nexora.platform_customer_company pcc
                JOIN nexora.access_scope sc ON sc.company_id = pcc.company_id
                JOIN nexora.user_role ur ON ur.scope_id = sc.id AND ur.revoked_at IS NULL
                WHERE pcc.customer_id = {customerId} LIMIT 25
                """).ToListAsync(ct);
            if (users.Count > 0)
                await notifications.NotifyAsync(new NotifyRequest("platform.quota", users, "platform.quota",
                    new Dictionary<string, string?>
                    {
                        ["title"] = $"Quota {resource} à {threshold} %",
                        ["message"] = $"Consommation {value}/{limit} pour {resource} ce mois-ci. Pensez à un add-on ou un upgrade.",
                    },
                    Priority: threshold >= 100 ? 1 : 2, Severity: threshold >= 100 ? "warning" : "info", Source: "system",
                    DedupKey: $"platform.quota:{customerId}:{resource}:{threshold}:{clock.UtcNow:yyyyMM}"), ct);
        }
        catch { /* best-effort */ }
    }
}
