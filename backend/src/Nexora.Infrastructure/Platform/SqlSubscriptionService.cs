using System.Text.Json;
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
// Cycle de vie de l'abonnement (Phase 38 §10-19, §29-33).
//   ESSAI → ABONNEMENT → PAIEMENT → ACTIF → RENOUVELLEMENT
//                                     └→ ÉCHEC → GRÂCE → SUSPENSION
// Le ledger d'événements (billing_event) est séparé des factures et des écritures (§44).
// EF `SqlQuery` traite chaque `{…}` comme un paramètre : le bloc SELECT réutilisé passe donc
// par `SqlQueryRaw` (texte brut + paramètres positionnels `{0}`, `{1}`…).
// =============================================================================
public sealed class SqlSubscriptionService(
    NexoraDbContext db,
    ITenantContext tenant,
    IUnitOfWork uow,
    IDateTimeProvider clock,
    INotificationService notifications) : ISubscriptionService
{
    /// <summary>Période de grâce par défaut avant suspension (§17 — configurable en exploitation).</summary>
    private const int GraceDays = 7;
    private const int TrialWarnDays = 3;

    private sealed record SubRow(Guid Id, Guid CustomerId, Guid PlanId, string Status, string Period, decimal PriceAmount,
        string CurrencyCode, string? QuotasOverride, DateTimeOffset? TrialEndsAt, DateTimeOffset CurrentPeriodStart,
        DateTimeOffset CurrentPeriodEnd, bool AutoRenew, bool CancelAtPeriodEnd, DateTimeOffset? CanceledAt,
        DateTimeOffset? GraceEndsAt, DateTimeOffset? SuspendedAt, string? SuspendReason, int PaymentFailures,
        DateTimeOffset? ActivatedAt, DateTimeOffset CreatedAt, string PlanCode, string PlanName, string Tier,
        string PlanQuotas, string PlanFeatures, decimal PlanPrice, string PlanCurrency, string PlanPeriod, int TrialDays);

    private const string SubSelect = """
        SELECT s.id AS "Id", s.customer_id AS "CustomerId", s.plan_id AS "PlanId", s.status AS "Status", s.period AS "Period",
               s.price_amount AS "PriceAmount", s.currency_code AS "CurrencyCode", s.quotas_override::text AS "QuotasOverride",
               s.trial_ends_at AS "TrialEndsAt", s.current_period_start AS "CurrentPeriodStart", s.current_period_end AS "CurrentPeriodEnd",
               s.auto_renew AS "AutoRenew", s.cancel_at_period_end AS "CancelAtPeriodEnd", s.canceled_at AS "CanceledAt",
               s.grace_ends_at AS "GraceEndsAt", s.suspended_at AS "SuspendedAt", s.suspend_reason AS "SuspendReason",
               s.payment_failures AS "PaymentFailures", s.activated_at AS "ActivatedAt", s.created_at AS "CreatedAt",
               p.code AS "PlanCode", p.name AS "PlanName", p.tier AS "Tier", p.quotas::text AS "PlanQuotas",
               p.features::text AS "PlanFeatures", p.price_amount AS "PlanPrice", p.currency_code AS "PlanCurrency",
               p.period AS "PlanPeriod", p.trial_days AS "TrialDays"
        FROM nexora.subscription s JOIN nexora.platform_plan p ON p.id = s.plan_id
        """;

    private sealed record CustomerLite(Guid Id, string Status, string Currency);
    private sealed record PlanLite(Guid Id, string Code, decimal Price, string Currency, string Period, int TrialDays, string Status, string Quotas, string Features);
    private sealed record AddonLite(Guid Id, string Code, decimal Price, string Currency, string Status);
    private sealed record IdQty(Guid Id, int Qty);
    private sealed record CouponLite(string Kind, decimal Value, int? Max, int Redeemed, DateTimeOffset? Expires, string Status);
    private sealed record ResourceValue(string Resource, long Value);
    private sealed record SubIdCust(Guid Id, Guid CustomerId);
    private sealed record TrialWarn(Guid Id, Guid CustomerId, DateTimeOffset TrialEndsAt);

    // ------------------------------------------------------------- Lecture

    public async Task<Result<SubscriptionDto>> GetAsync(Guid id, CancellationToken ct = default)
    {
        var row = await db.Database.SqlQueryRaw<SubRow>(SubSelect + " WHERE s.id = {0}", id).FirstOrDefaultAsync(ct);
        return row is null ? Error.NotFound("platform.subscription", "Abonnement introuvable.") : Result.Success(await ToDtoAsync(row, ct));
    }

    public async Task<IReadOnlyList<SubscriptionDto>> ListForCustomerAsync(Guid customerId, CancellationToken ct = default)
    {
        var rows = await db.Database.SqlQueryRaw<SubRow>(SubSelect + " WHERE s.customer_id = {0} ORDER BY s.created_at DESC", customerId).ToListAsync(ct);
        var result = new List<SubscriptionDto>(rows.Count);
        foreach (var r in rows) result.Add(await ToDtoAsync(r, ct));
        return result;
    }

    // ------------------------------------------------------------- Création

    public async Task<Result<SubscriptionDto>> CreateAsync(Guid customerId, CreateSubscriptionRequest r, CancellationToken ct = default)
    {
        var customer = await db.Database.SqlQuery<CustomerLite>($"""
            SELECT id AS "Id", status AS "Status", billing_currency AS "Currency" FROM nexora.platform_customer WHERE id = {customerId}
            """).FirstOrDefaultAsync(ct);
        if (customer is null) return Error.NotFound("platform.customer", "Client introuvable.");
        if (customer.Status == "closed") return Error.Conflict("platform.customer_closed", "Compte client clôturé.");

        var live = await db.Database.SqlQuery<Guid?>($"""
            SELECT id AS "Value" FROM nexora.subscription
            WHERE customer_id = {customerId} AND status IN ('trialing','active','past_due','grace','suspended')
            """).FirstOrDefaultAsync(ct);
        if (live is not null) return Error.Conflict("platform.subscription_exists", "Ce client a déjà un abonnement en cours.");

        var plan = await db.Database.SqlQuery<PlanLite>($"""
            SELECT id AS "Id", code AS "Code", price_amount AS "Price", currency_code AS "Currency", period AS "Period",
                   trial_days AS "TrialDays", status AS "Status", quotas::text AS "Quotas", features::text AS "Features"
            FROM nexora.platform_plan WHERE id = {r.PlanId}
            """).FirstOrDefaultAsync(ct);
        if (plan is null) return Error.NotFound("platform.plan", "Plan introuvable.");
        if (plan.Status != "active") return Error.Conflict("platform.plan_retired", "Ce plan n'est plus proposé.");

        var period = r.Period is "annual" or "monthly" ? r.Period : plan.Period;
        var price = NormalizeToSubPeriod(r.PriceOverride ?? plan.Price, r.PriceOverride is null ? plan.Period : period, period);
        var now = clock.UtcNow;
        var withTrial = r.StartTrial && plan.TrialDays > 0;
        var periodLen = period == "annual" ? TimeSpan.FromDays(365) : TimeSpan.FromDays(30);

        DateTimeOffset periodStart = now, periodEnd, trialEnds = now;
        string status;
        if (withTrial)
        {
            trialEnds = now.AddDays(plan.TrialDays);
            periodEnd = trialEnds;
            status = "trialing";
        }
        else
        {
            periodEnd = now.Add(periodLen);
            status = price <= 0m ? "active" : "past_due";
        }

        var quotasOverride = r.QuotasOverride is { Count: > 0 } ? JsonSerializer.Serialize(r.QuotasOverride, PlatformJson.Web) : null;
        var id = Guid.NewGuid();

        await uow.ExecuteInTransactionAsync(async token =>
        {
            await db.Database.ExecuteSqlAsync($"""
                INSERT INTO nexora.subscription
                    (id, customer_id, plan_id, status, period, price_amount, currency_code, quotas_override,
                     trial_ends_at, current_period_start, current_period_end, activated_at, created_by)
                VALUES ({id}, {customerId}, {plan.Id}, {status}, {period}, {price}, {plan.Currency}, {quotasOverride}::jsonb,
                        {(withTrial ? trialEnds : (DateTimeOffset?)null)}, {periodStart}, {periodEnd},
                        {(status == "active" ? now : (DateTimeOffset?)null)}, {tenant.UserId})
                """, token);

            await PlatformInvoices.LogEventAsync(db, customerId, id,
                withTrial ? PlatformEvents.TrialStarted : PlatformEvents.SubscriptionCreated,
                new { plan = plan.Code, period, price, fromTrial = false }, tenant.UserId, now, token);

            if (!withTrial && price > 0m)
            {
                var (discount, coupon) = await ResolveCouponAsync(r.CouponCode, price, token);
                var invoiceId = await PlatformInvoices.IssueAsync(db, customerId, id, "invoice",
                    DateOnly.FromDateTime(periodStart.UtcDateTime), DateOnly.FromDateTime(periodEnd.UtcDateTime), plan.Currency,
                    [new InvoiceLineSpec("plan", $"Abonnement {plan.Code} ({period})", 1, price, price)],
                    discount, coupon, PlatformInvoices.DefaultTaxRatePct, null, 14, now, token);
                await PlatformInvoices.LogEventAsync(db, customerId, id, PlatformEvents.InvoiceIssued, new { invoiceId }, tenant.UserId, now, token);
            }

            if (status == "active") await EnsureLicenseAsync(customerId, id, periodEnd, now, token);
            return 0;
        }, ct);

        await uow.SaveChangesAsync(ct);
        return await GetAsync(id, ct);
    }

    // ------------------------------------------------------------- Activation (§19, §29)

    public async Task<Result<SubscriptionDto>> ActivateAsync(Guid id, CancellationToken ct = default)
    {
        var s = await LoadRawAsync(id, ct);
        if (s is null) return Error.NotFound("platform.subscription", "Abonnement introuvable.");
        if (s.Status is "canceled" or "expired") return Error.Conflict("platform.subscription_closed", "Abonnement clos — utilisez la réactivation.");

        var now = clock.UtcNow;
        var wasDown = s.Status is "suspended" or "grace" or "past_due";
        var periodLen = s.Period == "annual" ? TimeSpan.FromDays(365) : TimeSpan.FromDays(30);
        var newEnd = s.CurrentPeriodEnd < now ? now.Add(periodLen) : s.CurrentPeriodEnd;
        var newStart = s.CurrentPeriodEnd < now ? now : s.CurrentPeriodStart;

        await uow.ExecuteInTransactionAsync(async token =>
        {
            await db.Database.ExecuteSqlAsync($"""
                UPDATE nexora.subscription SET
                    status = 'active', payment_failures = 0, grace_ends_at = NULL, suspended_at = NULL, suspend_reason = NULL,
                    cancel_at_period_end = false, canceled_at = NULL, cancel_reason = NULL,
                    current_period_start = {newStart}, current_period_end = {newEnd},
                    activated_at = COALESCE(activated_at, {now}), updated_at = now()
                WHERE id = {id}
                """, token);
            await EnsureLicenseAsync(s.CustomerId, id, newEnd, now, token);
            if (wasDown)
                await PlatformInvoices.LogEventAsync(db, s.CustomerId, id, PlatformEvents.SubscriptionReactivated, new { from = s.Status }, tenant.UserId, now, token);
            return 0;
        }, ct);

        if (wasDown) await NotifyCustomerAsync(s.CustomerId, "Abonnement réactivé", "Votre abonnement NEXORA est de nouveau actif.", 3, ct);
        await uow.SaveChangesAsync(ct);
        return await GetAsync(id, ct);
    }

    // ------------------------------------------------------------- Changement de plan (§14, §18-19)

    public async Task<Result<PlanChangeImpactDto>> PreviewPlanChangeAsync(Guid id, Guid targetPlanId, Guid? restrictToCustomer, CancellationToken ct = default)
    {
        var s = await LoadRawAsync(id, ct);
        if (s is null) return Error.NotFound("platform.subscription", "Abonnement introuvable.");
        if (restrictToCustomer is { } rc && rc != s.CustomerId) return Error.Forbidden("platform.not_your_subscription", "Abonnement hors de votre périmètre.");
        var impact = await ComputeImpactAsync(s, targetPlanId, ct);
        return impact is null ? Error.NotFound("platform.plan", "Plan cible introuvable.") : Result.Success(impact);
    }

    public Task<Result<SubscriptionDto>> UpgradeAsync(Guid id, ChangePlanRequest r, Guid? restrictToCustomer, CancellationToken ct = default)
        => ChangePlanAsync(id, r, restrictToCustomer, expectUpgrade: true, ct);

    public Task<Result<SubscriptionDto>> DowngradeAsync(Guid id, ChangePlanRequest r, Guid? restrictToCustomer, CancellationToken ct = default)
        => ChangePlanAsync(id, r, restrictToCustomer, expectUpgrade: false, ct);

    private async Task<Result<SubscriptionDto>> ChangePlanAsync(Guid id, ChangePlanRequest r, Guid? restrictToCustomer, bool expectUpgrade, CancellationToken ct)
    {
        var s = await LoadRawAsync(id, ct);
        if (s is null) return Error.NotFound("platform.subscription", "Abonnement introuvable.");
        if (restrictToCustomer is { } rc && rc != s.CustomerId) return Error.Forbidden("platform.not_your_subscription", "Abonnement hors de votre périmètre.");
        if (s.Status is "canceled" or "expired") return Error.Conflict("platform.subscription_closed", "Abonnement clos.");

        var impact = await ComputeImpactAsync(s, r.PlanId, ct);
        if (impact is null) return Error.NotFound("platform.plan", "Plan cible introuvable.");
        if (r.PreviewOnly) return Error.Validation("platform.preview_only", "Prévisualisation demandée — utilisez la route de prévisualisation.");
        if (expectUpgrade && impact.Direction == "downgrade")
            return Error.Validation("platform.wrong_direction", "Ce changement est un downgrade — passez par la route de downgrade.");
        if (!expectUpgrade && impact.Direction == "upgrade")
            return Error.Validation("platform.wrong_direction", "Ce changement est un upgrade — passez par la route d'upgrade.");

        var target = await db.Database.SqlQuery<PlanLite>($"""
            SELECT id AS "Id", code AS "Code", price_amount AS "Price", currency_code AS "Currency", period AS "Period",
                   trial_days AS "TrialDays", status AS "Status", quotas::text AS "Quotas", features::text AS "Features"
            FROM nexora.platform_plan WHERE id = {r.PlanId}
            """).FirstAsync(ct);

        var now = clock.UtcNow;
        var newSubPrice = NormalizeToSubPeriod(target.Price, target.Period, s.Period);
        var proration = Proration.Adjustment(s.PriceAmount, newSubPrice, s.CurrentPeriodStart, s.CurrentPeriodEnd, now);

        await uow.ExecuteInTransactionAsync(async token =>
        {
            await db.Database.ExecuteSqlAsync($"""
                UPDATE nexora.subscription SET plan_id = {target.Id}, price_amount = {newSubPrice}, quotas_override = NULL, updated_at = now()
                WHERE id = {id}
                """, token);

            if (impact.Direction == "upgrade" && proration > 0m)
                await PlatformInvoices.IssueAsync(db, s.CustomerId, id, "invoice", null, null, target.Currency,
                    [new InvoiceLineSpec("proration", $"Prorata upgrade vers {target.Code}", 1, proration, proration)],
                    0m, null, PlatformInvoices.DefaultTaxRatePct, null, 7, now, token);
            else if (impact.Direction == "downgrade" && proration < 0m)
                await PlatformInvoices.IssueAsync(db, s.CustomerId, id, "credit_note", null, null, target.Currency,
                    [new InvoiceLineSpec("proration", $"Avoir prorata downgrade vers {target.Code}", 1, proration, proration)],
                    0m, null, 0m, null, 0, now, token);

            await PlatformInvoices.LogEventAsync(db, s.CustomerId, id,
                impact.Direction == "upgrade" ? PlatformEvents.SubscriptionUpgraded : PlatformEvents.SubscriptionDowngraded,
                new { from = s.PlanCode, to = target.Code, proration, quotaBreaches = impact.QuotaBreaches, featuresLost = impact.FeaturesLost },
                tenant.UserId, now, token);
            return 0;
        }, ct);

        await uow.SaveChangesAsync(ct);
        return await GetAsync(id, ct);
    }

    // ------------------------------------------------------------- Add-ons (§20)

    public async Task<Result<SubscriptionDto>> SetAddonAsync(Guid id, AddonChangeRequest r, Guid? restrictToCustomer, CancellationToken ct = default)
    {
        var s = await LoadRawAsync(id, ct);
        if (s is null) return Error.NotFound("platform.subscription", "Abonnement introuvable.");
        if (restrictToCustomer is { } rc && rc != s.CustomerId) return Error.Forbidden("platform.not_your_subscription", "Abonnement hors de votre périmètre.");
        if (r.Quantity < 0) return Error.Validation("platform.addon_qty", "Quantité négative.");

        var addon = await db.Database.SqlQuery<AddonLite>($"""
            SELECT id AS "Id", code AS "Code", unit_price AS "Price", currency_code AS "Currency", status AS "Status"
            FROM nexora.platform_addon WHERE id = {r.AddonId}
            """).FirstOrDefaultAsync(ct);
        if (addon is null) return Error.NotFound("platform.addon", "Add-on introuvable.");
        if (addon.Status != "active" && r.Quantity > 0) return Error.Conflict("platform.addon_retired", "Add-on non disponible.");

        var now = clock.UtcNow;
        var existing = await db.Database.SqlQuery<IdQty>($"""
            SELECT id AS "Id", quantity AS "Qty" FROM nexora.subscription_addon
            WHERE subscription_id = {id} AND addon_id = {r.AddonId} AND removed_at IS NULL
            """).FirstOrDefaultAsync(ct);
        var oldQty = existing?.Qty ?? 0;
        var delta = r.Quantity - oldQty;

        await uow.ExecuteInTransactionAsync(async token =>
        {
            if (r.Quantity == 0 && existing is not null)
                await db.Database.ExecuteSqlAsync($"UPDATE nexora.subscription_addon SET removed_at = {now}, updated_at = now() WHERE id = {existing.Id}", token);
            else if (existing is not null)
                await db.Database.ExecuteSqlAsync($"UPDATE nexora.subscription_addon SET quantity = {r.Quantity}, updated_at = now() WHERE id = {existing.Id}", token);
            else if (r.Quantity > 0)
            {
                var saId = Guid.NewGuid();
                await db.Database.ExecuteSqlAsync($"""
                    INSERT INTO nexora.subscription_addon (id, subscription_id, addon_id, quantity, unit_price, currency_code)
                    VALUES ({saId}, {id}, {r.AddonId}, {r.Quantity}, {addon.Price}, {addon.Currency})
                    """, token);
            }

            if (delta > 0)
            {
                var frac = Proration.RemainingFraction(s.CurrentPeriodStart, s.CurrentPeriodEnd, now);
                var amount = Math.Round(addon.Price * delta * frac, 2);
                if (amount > 0m)
                    await PlatformInvoices.IssueAsync(db, s.CustomerId, id, "invoice", null, null, addon.Currency,
                        [new InvoiceLineSpec("addon", $"{addon.Code} × {delta} (prorata)", delta, addon.Price, amount)],
                        0m, null, PlatformInvoices.DefaultTaxRatePct, null, 7, now, token);
            }
            await PlatformInvoices.LogEventAsync(db, s.CustomerId, id,
                delta >= 0 ? PlatformEvents.SubscriptionUpgraded : PlatformEvents.SubscriptionDowngraded,
                new { addon = addon.Code, from = oldQty, to = r.Quantity }, tenant.UserId, now, token);
            return 0;
        }, ct);

        await uow.SaveChangesAsync(ct);
        return await GetAsync(id, ct);
    }

    // ------------------------------------------------------------- Annulation / réactivation / suspension (§30-33)

    public async Task<Result<SubscriptionDto>> CancelAsync(Guid id, CancelSubscriptionRequest r, Guid? restrictToCustomer, CancellationToken ct = default)
    {
        var s = await LoadRawAsync(id, ct);
        if (s is null) return Error.NotFound("platform.subscription", "Abonnement introuvable.");
        if (restrictToCustomer is { } rc && rc != s.CustomerId) return Error.Forbidden("platform.not_your_subscription", "Abonnement hors de votre périmètre.");
        if (s.Status is "canceled" or "expired") return Error.Conflict("platform.already_canceled", "Abonnement déjà clos.");

        var now = clock.UtcNow;
        await uow.ExecuteInTransactionAsync(async token =>
        {
            if (r.Immediate)
            {
                await db.Database.ExecuteSqlAsync($"""
                    UPDATE nexora.subscription SET status = 'canceled', canceled_at = {now}, cancel_reason = {r.Reason},
                        auto_renew = false, cancel_at_period_end = true, updated_at = now()
                    WHERE id = {id}
                    """, token);
                await db.Database.ExecuteSqlAsync($"""
                    UPDATE nexora.platform_license SET status = 'revoked', revoked_at = {now}, revoke_reason = 'subscription canceled', updated_at = now()
                    WHERE subscription_id = {id} AND status = 'active'
                    """, token);
            }
            else
            {
                await db.Database.ExecuteSqlAsync($"""
                    UPDATE nexora.subscription SET cancel_at_period_end = true, auto_renew = false, cancel_reason = {r.Reason}, updated_at = now()
                    WHERE id = {id}
                    """, token);
            }
            await PlatformInvoices.LogEventAsync(db, s.CustomerId, id, PlatformEvents.SubscriptionCanceled,
                new { immediate = r.Immediate, reason = r.Reason }, tenant.UserId, now, token);
            return 0;
        }, ct);

        await NotifyCustomerAsync(s.CustomerId, "Abonnement annulé",
            r.Immediate ? "Votre abonnement NEXORA a été annulé. Vos données restent exportables (§32)."
                        : "Votre abonnement NEXORA ne sera pas renouvelé à la fin de la période en cours.", 3, ct);
        await uow.SaveChangesAsync(ct);
        return await GetAsync(id, ct);
    }

    public async Task<Result<SubscriptionDto>> ReactivateAsync(Guid id, Guid? restrictToCustomer, CancellationToken ct = default)
    {
        var s = await LoadRawAsync(id, ct);
        if (s is null) return Error.NotFound("platform.subscription", "Abonnement introuvable.");
        if (restrictToCustomer is { } rc && rc != s.CustomerId) return Error.Forbidden("platform.not_your_subscription", "Abonnement hors de votre périmètre.");
        if (s.Status is "active" && !s.CancelAtPeriodEnd) return Error.Conflict("platform.already_active", "Abonnement déjà actif.");
        if (s.Status == "expired") return Error.Conflict("platform.expired", "Abonnement expiré — souscrivez un nouvel abonnement.");

        var now = clock.UtcNow;
        var stillInPeriod = s.CurrentPeriodEnd > now && s.Status is "active" or "canceled";
        await uow.ExecuteInTransactionAsync(async token =>
        {
            if (stillInPeriod)
            {
                await db.Database.ExecuteSqlAsync($"""
                    UPDATE nexora.subscription SET status = 'active', cancel_at_period_end = false, canceled_at = NULL,
                        cancel_reason = NULL, auto_renew = true, updated_at = now()
                    WHERE id = {id}
                    """, token);
                await EnsureLicenseAsync(s.CustomerId, id, s.CurrentPeriodEnd, now, token);
            }
            else
            {
                await db.Database.ExecuteSqlAsync($"""
                    UPDATE nexora.subscription SET status = 'past_due', cancel_at_period_end = false, canceled_at = NULL,
                        cancel_reason = NULL, auto_renew = true, grace_ends_at = {now.AddDays(GraceDays)}, updated_at = now()
                    WHERE id = {id}
                    """, token);
                if (s.PriceAmount > 0m)
                    await PlatformInvoices.IssueAsync(db, s.CustomerId, id, "invoice", null, null, s.CurrencyCode,
                        [new InvoiceLineSpec("plan", $"Réactivation {s.PlanCode} ({s.Period})", 1, s.PriceAmount, s.PriceAmount)],
                        0m, null, PlatformInvoices.DefaultTaxRatePct, null, 7, now, token);
            }
            await PlatformInvoices.LogEventAsync(db, s.CustomerId, id, PlatformEvents.SubscriptionReactivated, new { stillInPeriod }, tenant.UserId, now, token);
            return 0;
        }, ct);

        await NotifyCustomerAsync(s.CustomerId, "Réactivation en cours",
            stillInPeriod ? "Votre abonnement NEXORA est de nouveau actif." : "Une facture de réactivation a été émise — réglez-la pour retrouver l'accès complet.", 3, ct);
        await uow.SaveChangesAsync(ct);
        return await GetAsync(id, ct);
    }

    public async Task<Result<SubscriptionDto>> SuspendAsync(Guid id, SuspendSubscriptionRequest r, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(r.Reason)) return Error.Validation("platform.suspend_reason", "Motif de suspension requis.");
        var s = await LoadRawAsync(id, ct);
        if (s is null) return Error.NotFound("platform.subscription", "Abonnement introuvable.");
        if (s.Status is "canceled" or "expired" or "suspended") return Error.Conflict("platform.subscription_state", "Abonnement déjà suspendu ou clos.");

        var now = clock.UtcNow;
        await db.Database.ExecuteSqlAsync($"""
            UPDATE nexora.subscription SET status = 'suspended', suspended_at = {now}, suspend_reason = {r.Reason}, updated_at = now()
            WHERE id = {id}
            """, ct);
        await PlatformInvoices.LogEventAsync(db, s.CustomerId, id, PlatformEvents.SubscriptionSuspended, new { reason = r.Reason, manual = true }, tenant.UserId, now, ct);
        await uow.SaveChangesAsync(ct);
        await NotifyCustomerAsync(s.CustomerId, "Abonnement suspendu", $"Votre abonnement NEXORA a été suspendu : {r.Reason}", 1, ct);
        return await GetAsync(id, ct);
    }

    // ------------------------------------------------------------- Balayage périodique (§11, §17)

    public async Task<int> RunDueAsync(CancellationToken ct = default)
    {
        using var _ = tenant.BeginSystemScope();
        var now = clock.UtcNow;
        var today = DateOnly.FromDateTime(now.UtcDateTime);
        var transitions = 0;

        // 1. Essais qui se terminent bientôt → rappel (§12, §23).
        var warnSoon = await db.Database.SqlQuery<TrialWarn>($"""
            SELECT s.id AS "Id", s.customer_id AS "CustomerId", s.trial_ends_at AS "TrialEndsAt"
            FROM nexora.subscription s
            WHERE s.status = 'trialing' AND s.trial_ends_at BETWEEN {now} AND {now.AddDays(TrialWarnDays)}
              AND NOT EXISTS (SELECT 1 FROM nexora.billing_event e
                              WHERE e.subscription_id = s.id AND e.event_type = 'TrialEndingSoon'
                                AND e.occurred_at > {now.AddDays(-TrialWarnDays - 1)})
            """).ToListAsync(ct);
        foreach (var w in warnSoon)
        {
            await PlatformInvoices.LogEventAsync(db, w.CustomerId, w.Id, PlatformEvents.TrialEndingSoon, new { w.TrialEndsAt }, null, now, ct);
            await NotifyCustomerAsync(w.CustomerId, "Votre essai NEXORA se termine bientôt",
                "Ajoutez un moyen de paiement pour convertir votre essai en abonnement.", 2, ct);
            transitions++;
        }

        // 2. Essais expirés → en attente de paiement + première facture + fenêtre de grâce.
        var trialExpired = await db.Database.SqlQueryRaw<SubRow>(SubSelect + " WHERE s.status = 'trialing' AND s.trial_ends_at < {0}", now).ToListAsync(ct);
        foreach (var s in trialExpired)
        {
            await uow.ExecuteInTransactionAsync(async token =>
            {
                var end = now.Add(s.Period == "annual" ? TimeSpan.FromDays(365) : TimeSpan.FromDays(30));
                var status = s.PriceAmount <= 0m ? "active" : "past_due";
                await db.Database.ExecuteSqlAsync($"""
                    UPDATE nexora.subscription SET status = {status}, current_period_start = {now}, current_period_end = {end},
                        grace_ends_at = {(status == "past_due" ? now.AddDays(GraceDays) : (DateTimeOffset?)null)},
                        activated_at = COALESCE(activated_at, {(status == "active" ? now : (DateTimeOffset?)null)}), updated_at = now()
                    WHERE id = {s.Id}
                    """, token);
                await PlatformInvoices.LogEventAsync(db, s.CustomerId, s.Id, PlatformEvents.TrialExpired, new { converted = status == "active" }, null, now, token);
                if (status == "past_due")
                    await PlatformInvoices.IssueAsync(db, s.CustomerId, s.Id, "invoice",
                        DateOnly.FromDateTime(now.UtcDateTime), DateOnly.FromDateTime(end.UtcDateTime), s.CurrencyCode,
                        [new InvoiceLineSpec("plan", $"Abonnement {s.PlanCode} ({s.Period})", 1, s.PriceAmount, s.PriceAmount)],
                        0m, null, PlatformInvoices.DefaultTaxRatePct, null, GraceDays, now, token);
                else
                    await EnsureLicenseAsync(s.CustomerId, s.Id, end, now, token);
                return 0;
            }, ct);
            await NotifyCustomerAsync(s.CustomerId, "Votre essai NEXORA a pris fin", "Réglez la facture d'abonnement pour conserver l'accès.", 2, ct);
            transitions++;
        }

        // 3. Renouvellements dus (§11) → facture + en attente de paiement + grâce.
        var renewals = await db.Database.SqlQueryRaw<SubRow>(
            SubSelect + " WHERE s.status = 'active' AND s.current_period_end < {0} AND s.auto_renew AND NOT s.cancel_at_period_end", now).ToListAsync(ct);
        foreach (var s in renewals)
        {
            await uow.ExecuteInTransactionAsync(async token =>
            {
                var end = s.CurrentPeriodEnd.Add(s.Period == "annual" ? TimeSpan.FromDays(365) : TimeSpan.FromDays(30));
                var status = s.PriceAmount <= 0m ? "active" : "past_due";
                await db.Database.ExecuteSqlAsync($"""
                    UPDATE nexora.subscription SET status = {status},
                        current_period_start = {s.CurrentPeriodEnd}, current_period_end = {end},
                        grace_ends_at = {(status == "active" ? (DateTimeOffset?)null : now.AddDays(GraceDays))}, updated_at = now()
                    WHERE id = {s.Id}
                    """, token);
                if (status == "past_due")
                    await PlatformInvoices.IssueAsync(db, s.CustomerId, s.Id, "invoice",
                        DateOnly.FromDateTime(s.CurrentPeriodEnd.UtcDateTime), DateOnly.FromDateTime(end.UtcDateTime), s.CurrencyCode,
                        [new InvoiceLineSpec("plan", $"Renouvellement {s.PlanCode} ({s.Period})", 1, s.PriceAmount, s.PriceAmount)],
                        0m, null, PlatformInvoices.DefaultTaxRatePct, null, GraceDays, now, token);
                else
                    await EnsureLicenseAsync(s.CustomerId, s.Id, end, now, token);
                return 0;
            }, ct);
            transitions++;
        }

        // 4. Annulation programmée arrivée à échéance.
        var toClose = await db.Database.SqlQuery<SubIdCust>($"""
            SELECT id AS "Id", customer_id AS "CustomerId" FROM nexora.subscription
            WHERE cancel_at_period_end AND status IN ('active','past_due','grace') AND current_period_end < {now}
            """).ToListAsync(ct);
        foreach (var c in toClose)
        {
            await db.Database.ExecuteSqlAsync($"""
                UPDATE nexora.subscription SET status = 'canceled', canceled_at = COALESCE(canceled_at, {now}), updated_at = now() WHERE id = {c.Id}
                """, ct);
            await db.Database.ExecuteSqlAsync($"""
                UPDATE nexora.platform_license SET status = 'revoked', revoked_at = {now}, revoke_reason = 'subscription ended', updated_at = now()
                WHERE subscription_id = {c.Id} AND status = 'active'
                """, ct);
            await PlatformInvoices.LogEventAsync(db, c.CustomerId, c.Id, PlatformEvents.SubscriptionCanceled, new { scheduled = true }, null, now, ct);
            transitions++;
        }

        // 5. Fin de la période de grâce → suspension (§17). Déclenchée par l'échéance ferme, jamais
        //    par un événement de paiement ambigu (§54).
        var toSuspend = await db.Database.SqlQuery<SubIdCust>($"""
            SELECT id AS "Id", customer_id AS "CustomerId" FROM nexora.subscription
            WHERE status IN ('past_due','grace') AND grace_ends_at IS NOT NULL AND grace_ends_at < {now}
            """).ToListAsync(ct);
        foreach (var su in toSuspend)
        {
            await db.Database.ExecuteSqlAsync($"""
                UPDATE nexora.subscription SET status = 'suspended', suspended_at = {now},
                    suspend_reason = 'Non-paiement — fin de la période de grâce', updated_at = now()
                WHERE id = {su.Id}
                """, ct);
            await PlatformInvoices.LogEventAsync(db, su.CustomerId, su.Id, PlatformEvents.SubscriptionSuspended, new { reason = "grace_expired" }, null, now, ct);
            await NotifyCustomerAsync(su.CustomerId, "Abonnement suspendu", "Votre abonnement NEXORA est suspendu pour non-paiement. Réglez la facture pour le réactiver.", 1, ct);
            transitions++;
        }

        // 6. past_due sans échéance de grâce fixée → on la pose.
        await db.Database.ExecuteSqlAsync($"""
            UPDATE nexora.subscription SET grace_ends_at = {now.AddDays(GraceDays)}, updated_at = now()
            WHERE status = 'past_due' AND grace_ends_at IS NULL
            """, ct);

        // 7. Factures échues non réglées → overdue (§13).
        await db.Database.ExecuteSqlAsync($"""
            UPDATE nexora.platform_invoice SET status = 'overdue', updated_at = now()
            WHERE status = 'issued' AND due_date < {today}
            """, ct);

        await uow.SaveChangesAsync(ct);
        return transitions;
    }

    // ------------------------------------------------------------- Helpers

    private async Task<SubRow?> LoadRawAsync(Guid id, CancellationToken ct)
        => await db.Database.SqlQueryRaw<SubRow>(SubSelect + " WHERE s.id = {0}", id).FirstOrDefaultAsync(ct);

    private static decimal NormalizeToSubPeriod(decimal price, string fromPeriod, string toPeriod)
    {
        if (fromPeriod == toPeriod) return Math.Round(price, 2);
        return toPeriod == "annual" ? Math.Round(price * 12, 2) : Math.Round(price / 12, 2);
    }

    private async Task<PlanChangeImpactDto?> ComputeImpactAsync(SubRow s, Guid targetPlanId, CancellationToken ct)
    {
        var target = await db.Database.SqlQuery<PlanLite>($"""
            SELECT id AS "Id", code AS "Code", price_amount AS "Price", currency_code AS "Currency", period AS "Period",
                   trial_days AS "TrialDays", status AS "Status", quotas::text AS "Quotas", features::text AS "Features"
            FROM nexora.platform_plan WHERE id = {targetPlanId}
            """).FirstOrDefaultAsync(ct);
        if (target is null) return null;

        var now = clock.UtcNow;
        var newSubPrice = NormalizeToSubPeriod(target.Price, target.Period, s.Period);
        var direction = newSubPrice > s.PriceAmount ? "upgrade" : newSubPrice < s.PriceAmount ? "downgrade" : "lateral";
        var proration = Proration.Adjustment(s.PriceAmount, newSubPrice, s.CurrentPeriodStart, s.CurrentPeriodEnd, now);

        var targetQuotas = PlatformJson.Quotas(target.Quotas);
        var usage = await CurrentUsageAsync(s.CustomerId, ct);
        var breaches = new List<QuotaBreachDto>();
        foreach (var (res, newLimit) in targetQuotas)
        {
            if (newLimit <= 0) continue;   // 0 / absent = illimité
            var used = usage.GetValueOrDefault(res, 0);
            if (used > newLimit) breaches.Add(new QuotaBreachDto(res, newLimit, used));
        }

        var currentFeatures = PlatformJson.Features(s.PlanFeatures);
        var targetFeatures = PlatformJson.Features(target.Features);
        var lost = currentFeatures
            .Where(kv => kv.Value == "included" && targetFeatures.TryGetValue(kv.Key, out var v) && v == "unavailable")
            .Select(kv => kv.Key).ToList();

        var summary = direction switch
        {
            "upgrade" => $"Upgrade {s.PlanCode} → {target.Code}. Ajustement au prorata : {proration:+0.00;-0.00} {target.Currency}.",
            "downgrade" => $"Downgrade {s.PlanCode} → {target.Code}. Avoir au prorata : {proration:+0.00;-0.00} {target.Currency}. "
                           + $"{breaches.Count} quota(s) dépassé(s) — les excédents sont désactivés, aucune donnée supprimée (§18).",
            _ => $"Changement latéral {s.PlanCode} → {target.Code}.",
        };
        return new PlanChangeImpactDto(true, direction, proration, target.Currency, breaches, lost, summary);
    }

    private async Task<Dictionary<string, long>> CurrentUsageAsync(Guid customerId, CancellationToken ct)
    {
        var monthStart = new DateOnly(clock.UtcNow.Year, clock.UtcNow.Month, 1);
        var rows = await db.Database.SqlQuery<ResourceValue>($"""
            SELECT resource AS "Resource", value AS "Value" FROM nexora.usage_counter
            WHERE customer_id = {customerId} AND period_start = {monthStart}
            """).ToListAsync(ct);
        return rows.ToDictionary(r => r.Resource, r => r.Value);
    }

    private async Task EnsureLicenseAsync(Guid customerId, Guid subscriptionId, DateTimeOffset periodEnd, DateTimeOffset now, CancellationToken ct)
    {
        var expires = periodEnd.AddDays(GraceDays);
        var existing = await db.Database.SqlQuery<Guid?>($"""
            SELECT id AS "Value" FROM nexora.platform_license
            WHERE subscription_id = {subscriptionId} AND scope = 'organization' AND status <> 'revoked'
            """).FirstOrDefaultAsync(ct);
        if (existing is { } lid)
            await db.Database.ExecuteSqlAsync($"""
                UPDATE nexora.platform_license SET status = 'active', expires_at = {expires}, revoked_at = NULL, updated_at = now()
                WHERE id = {lid}
                """, ct);
        else
        {
            var id = Guid.NewGuid();
            await db.Database.ExecuteSqlAsync($"""
                INSERT INTO nexora.platform_license (id, customer_id, subscription_id, scope, status, grace_days, activated_at, expires_at)
                VALUES ({id}, {customerId}, {subscriptionId}, 'organization', 'active', {GraceDays}, {now}, {expires})
                """, ct);
            await PlatformInvoices.LogEventAsync(db, customerId, subscriptionId, PlatformEvents.LicenseActivated, new { id }, tenant.UserId, now, ct);
        }
    }

    private async Task NotifyCustomerAsync(Guid customerId, string title, string message, int priority, CancellationToken ct)
    {
        try
        {
            var users = await db.Database.SqlQuery<Guid>($"""
                SELECT DISTINCT ur.user_id AS "Value"
                FROM nexora.platform_customer_company pcc
                JOIN nexora.access_scope sc ON sc.company_id = pcc.company_id
                JOIN nexora.user_role ur ON ur.scope_id = sc.id AND ur.revoked_at IS NULL
                WHERE pcc.customer_id = {customerId}
                LIMIT 25
                """).ToListAsync(ct);
            if (users.Count == 0) return;
            await notifications.NotifyAsync(new NotifyRequest(
                Category: "platform.billing",
                Recipients: users,
                TemplateKey: "platform.billing",
                Variables: new Dictionary<string, string?> { ["title"] = title, ["message"] = message },
                Priority: priority,
                Severity: priority == 1 ? "warning" : "info",
                Source: "system",
                DedupKey: $"platform.billing:{customerId}:{title}:{clock.UtcNow:yyyyMMddHH}"), ct);
        }
        catch { /* best-effort — billing_event reste la trace fiable (§44, §54) */ }
    }

    private async Task<(decimal Discount, string? Code)> ResolveCouponAsync(string? code, decimal amount, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(code)) return (0m, null);
        var c = code.Trim().ToUpperInvariant();
        var row = await db.Database.SqlQuery<CouponLite>($"""
            SELECT kind AS "Kind", value AS "Value", max_redemptions AS "Max", redeemed_count AS "Redeemed",
                   expires_at AS "Expires", status AS "Status"
            FROM nexora.platform_coupon WHERE code = {c}
            """).FirstOrDefaultAsync(ct);
        if (row is null || row.Status != "active") return (0m, null);
        if (row.Expires is { } e && e < clock.UtcNow) return (0m, null);
        if (row.Max is { } m && row.Redeemed >= m) return (0m, null);

        var discount = row.Kind == "percent" ? Math.Round(amount * row.Value / 100m, 2) : Math.Min(amount, row.Value);
        if (discount <= 0m) return (0m, null);
        await db.Database.ExecuteSqlAsync($"UPDATE nexora.platform_coupon SET redeemed_count = redeemed_count + 1, updated_at = now() WHERE code = {c}", ct);
        return (discount, c);
    }

    private async Task<SubscriptionDto> ToDtoAsync(SubRow s, CancellationToken ct)
    {
        var addons = await db.Database.SqlQuery<SubscriptionAddonDto>($"""
            SELECT sa.id AS "Id", sa.addon_id AS "AddonId", a.code AS "AddonCode", sa.quantity AS "Quantity", sa.unit_price AS "UnitPrice"
            FROM nexora.subscription_addon sa JOIN nexora.platform_addon a ON a.id = sa.addon_id
            WHERE sa.subscription_id = {s.Id} AND sa.removed_at IS NULL ORDER BY a.code
            """).ToListAsync(ct);

        return new SubscriptionDto(s.Id, s.CustomerId, s.PlanId, s.PlanCode, s.PlanName, s.Tier, s.Status, s.Period,
            s.PriceAmount, s.CurrencyCode, s.TrialEndsAt, s.CurrentPeriodStart, s.CurrentPeriodEnd, s.AutoRenew,
            s.CancelAtPeriodEnd, s.CanceledAt, s.GraceEndsAt, s.SuspendedAt, s.SuspendReason, s.PaymentFailures,
            s.ActivatedAt, addons, s.CreatedAt);
    }
}
