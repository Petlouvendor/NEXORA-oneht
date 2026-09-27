using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Nexora.Application.Abstractions;
using Nexora.Application.Platform;
using Nexora.Contracts.Platform;
using Nexora.Domain.Common;
using Nexora.Infrastructure.Persistence;

namespace Nexora.Infrastructure.Platform;

/// <summary>Options JSON partagées par la couche plateforme (Phase 38).</summary>
internal static class PlatformJson
{
    public static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    public static Dictionary<string, long> Quotas(string? raw)
        => string.IsNullOrWhiteSpace(raw) || raw == "null"
            ? new()
            : JsonSerializer.Deserialize<Dictionary<string, long>>(raw, Web) ?? new();

    public static Dictionary<string, string> Features(string? raw)
        => string.IsNullOrWhiteSpace(raw) || raw == "null"
            ? new()
            : JsonSerializer.Deserialize<Dictionary<string, string>>(raw, Web) ?? new();
}

// =============================================================================
// Catalogue commercial : plans, add-ons, coupons (§4-6, §20, §39-40).
// =============================================================================
public sealed class SqlPlatformCatalogService(NexoraDbContext db, IUnitOfWork uow)
    : IPlatformCatalogService
{
    private static readonly string[] Tiers = ["starter", "business", "professional", "enterprise", "custom"];
    private static readonly string[] Periods = ["monthly", "annual"];
    private static readonly string[] AddonKinds =
        ["users", "companies", "establishments", "terminals", "storage", "api", "connectors", "support", "feature"];

    private sealed record PlanRow(Guid Id, string Code, string Name, string Tier, string? Description, decimal PriceAmount,
        string CurrencyCode, string Period, int TrialDays, string Quotas, string Features, bool IsPublic, int SortOrder, string Status);

    public async Task<IReadOnlyList<PlanDto>> ListPlansAsync(bool publicOnly, CancellationToken ct = default)
    {
        var rows = await db.Database.SqlQuery<PlanRow>($"""
            SELECT id AS "Id", code AS "Code", name AS "Name", tier AS "Tier", description AS "Description",
                   price_amount AS "PriceAmount", currency_code AS "CurrencyCode", period AS "Period", trial_days AS "TrialDays",
                   quotas::text AS "Quotas", features::text AS "Features", is_public AS "IsPublic", sort_order AS "SortOrder", status AS "Status"
            FROM nexora.platform_plan
            WHERE ({publicOnly} = false OR (is_public AND status = 'active'))
            ORDER BY sort_order, price_amount
            """).ToListAsync(ct);
        return rows.Select(ToDto).ToList();
    }

    public async Task<Result<PlanDto>> UpsertPlanAsync(UpsertPlanRequest r, CancellationToken ct = default)
    {
        var code = (r.Code ?? "").Trim().ToLowerInvariant();
        if (code.Length is < 2 or > 40) return Error.Validation("platform.plan_code", "Code de plan invalide (2–40 caractères).");
        if (!Tiers.Contains(r.Tier)) return Error.Validation("platform.plan_tier", "Palier invalide.");
        if (!Periods.Contains(r.Period)) return Error.Validation("platform.plan_period", "Périodicité invalide.");
        if (r.PriceAmount < 0) return Error.Validation("platform.plan_price", "Prix négatif.");
        if (r.TrialDays is < 0 or > 365) return Error.Validation("platform.plan_trial", "Essai hors bornes (0–365 j).");

        var quotas = JsonSerializer.Serialize(r.Quotas ?? new Dictionary<string, long>(), PlatformJson.Web);
        var features = JsonSerializer.Serialize(r.Features ?? new Dictionary<string, string>(), PlatformJson.Web);
        var status = r.Status == "retired" ? "retired" : "active";

        await db.Database.ExecuteSqlAsync($"""
            INSERT INTO nexora.platform_plan
                (code, name, tier, description, price_amount, currency_code, period, trial_days, quotas, features, is_public, sort_order, status)
            VALUES ({code}, {r.Name}, {r.Tier}, {r.Description}, {r.PriceAmount}, {r.CurrencyCode.ToUpperInvariant()},
                    {r.Period}, {r.TrialDays}, {quotas}::jsonb, {features}::jsonb, {r.IsPublic}, {r.SortOrder}, {status})
            ON CONFLICT ON CONSTRAINT platform_plan_code_uk DO UPDATE SET
                name = EXCLUDED.name, tier = EXCLUDED.tier, description = EXCLUDED.description,
                price_amount = EXCLUDED.price_amount, currency_code = EXCLUDED.currency_code, period = EXCLUDED.period,
                trial_days = EXCLUDED.trial_days, quotas = EXCLUDED.quotas, features = EXCLUDED.features,
                is_public = EXCLUDED.is_public, sort_order = EXCLUDED.sort_order, status = EXCLUDED.status, updated_at = now()
            """, ct);
        await uow.SaveChangesAsync(ct);
        return Result.Success((await ListPlansAsync(false, ct)).First(p => p.Code == code));
    }

    private sealed record AddonRow(Guid Id, string Code, string Name, string Kind, string? QuotaKey, long QuotaDelta,
        decimal UnitPrice, string CurrencyCode, string Period, bool IsPublic, string Status);

    public async Task<IReadOnlyList<AddonDto>> ListAddonsAsync(bool publicOnly, CancellationToken ct = default)
    {
        var rows = await db.Database.SqlQuery<AddonRow>($"""
            SELECT id AS "Id", code AS "Code", name AS "Name", kind AS "Kind", quota_key AS "QuotaKey", quota_delta AS "QuotaDelta",
                   unit_price AS "UnitPrice", currency_code AS "CurrencyCode", period AS "Period", is_public AS "IsPublic", status AS "Status"
            FROM nexora.platform_addon
            WHERE ({publicOnly} = false OR (is_public AND status = 'active'))
            ORDER BY kind, code
            """).ToListAsync(ct);
        return rows.Select(a => new AddonDto(a.Id, a.Code, a.Name, a.Kind, a.QuotaKey, a.QuotaDelta, a.UnitPrice,
            a.CurrencyCode, a.Period, a.IsPublic, a.Status)).ToList();
    }

    public async Task<Result<AddonDto>> UpsertAddonAsync(UpsertAddonRequest r, CancellationToken ct = default)
    {
        var code = (r.Code ?? "").Trim().ToLowerInvariant();
        if (code.Length is < 2 or > 40) return Error.Validation("platform.addon_code", "Code d'add-on invalide.");
        if (!AddonKinds.Contains(r.Kind)) return Error.Validation("platform.addon_kind", "Type d'add-on invalide.");
        if (!Periods.Contains(r.Period)) return Error.Validation("platform.addon_period", "Périodicité invalide.");
        if (r.UnitPrice < 0) return Error.Validation("platform.addon_price", "Prix négatif.");
        var status = r.Status == "retired" ? "retired" : "active";

        await db.Database.ExecuteSqlAsync($"""
            INSERT INTO nexora.platform_addon
                (code, name, kind, quota_key, quota_delta, unit_price, currency_code, period, is_public, status)
            VALUES ({code}, {r.Name}, {r.Kind}, {r.QuotaKey}, {r.QuotaDelta}, {r.UnitPrice},
                    {r.CurrencyCode.ToUpperInvariant()}, {r.Period}, {r.IsPublic}, {status})
            ON CONFLICT ON CONSTRAINT platform_addon_code_uk DO UPDATE SET
                name = EXCLUDED.name, kind = EXCLUDED.kind, quota_key = EXCLUDED.quota_key, quota_delta = EXCLUDED.quota_delta,
                unit_price = EXCLUDED.unit_price, currency_code = EXCLUDED.currency_code, period = EXCLUDED.period,
                is_public = EXCLUDED.is_public, status = EXCLUDED.status, updated_at = now()
            """, ct);
        await uow.SaveChangesAsync(ct);
        return Result.Success((await ListAddonsAsync(false, ct)).First(a => a.Code == code));
    }

    private sealed record CouponRow(Guid Id, string Code, string? Description, string Kind, decimal Value, string? CurrencyCode,
        string Duration, int? MaxRedemptions, int RedeemedCount, DateTimeOffset? ExpiresAt, string Status);

    public async Task<IReadOnlyList<CouponDto>> ListCouponsAsync(CancellationToken ct = default)
    {
        var rows = await db.Database.SqlQuery<CouponRow>($"""
            SELECT id AS "Id", code AS "Code", description AS "Description", kind AS "Kind", value AS "Value",
                   currency_code AS "CurrencyCode", duration AS "Duration", max_redemptions AS "MaxRedemptions",
                   redeemed_count AS "RedeemedCount", expires_at AS "ExpiresAt", status AS "Status"
            FROM nexora.platform_coupon ORDER BY created_at DESC
            """).ToListAsync(ct);
        return rows.Select(c => new CouponDto(c.Id, c.Code, c.Description, c.Kind, c.Value, c.CurrencyCode, c.Duration,
            c.MaxRedemptions, c.RedeemedCount, c.ExpiresAt, c.Status)).ToList();
    }

    public async Task<Result<CouponDto>> UpsertCouponAsync(UpsertCouponRequest r, CancellationToken ct = default)
    {
        var code = (r.Code ?? "").Trim().ToUpperInvariant();
        if (code.Length is < 2 or > 40) return Error.Validation("platform.coupon_code", "Code de coupon invalide.");
        if (r.Kind is not ("percent" or "fixed")) return Error.Validation("platform.coupon_kind", "Type invalide (percent / fixed).");
        if (r.Value < 0 || (r.Kind == "percent" && r.Value > 100)) return Error.Validation("platform.coupon_value", "Valeur hors bornes.");
        if (r.Duration is not ("once" or "first_period" or "forever")) return Error.Validation("platform.coupon_duration", "Durée invalide.");
        var status = r.Status == "disabled" ? "disabled" : "active";

        await db.Database.ExecuteSqlAsync($"""
            INSERT INTO nexora.platform_coupon (code, description, kind, value, currency_code, duration, max_redemptions, expires_at, status)
            VALUES ({code}, {r.Description}, {r.Kind}, {r.Value}, {r.CurrencyCode}, {r.Duration}, {r.MaxRedemptions}, {r.ExpiresAt}, {status})
            ON CONFLICT ON CONSTRAINT platform_coupon_code_uk DO UPDATE SET
                description = EXCLUDED.description, kind = EXCLUDED.kind, value = EXCLUDED.value,
                currency_code = EXCLUDED.currency_code, duration = EXCLUDED.duration, max_redemptions = EXCLUDED.max_redemptions,
                expires_at = EXCLUDED.expires_at, status = EXCLUDED.status, updated_at = now()
            """, ct);
        await uow.SaveChangesAsync(ct);
        return Result.Success((await ListCouponsAsync(ct)).First(c => c.Code == code));
    }

    private static PlanDto ToDto(PlanRow p) => new(p.Id, p.Code, p.Name, p.Tier, p.Description, p.PriceAmount,
        p.CurrencyCode, p.Period, p.TrialDays, PlatformJson.Quotas(p.Quotas), PlatformJson.Features(p.Features),
        p.IsPublic, p.SortOrder, p.Status);
}

// =============================================================================
// Comptes clients / organisations (§7-9).
// =============================================================================
public sealed class SqlCustomerAccountService(NexoraDbContext db, ITenantContext tenant, IUnitOfWork uow)
    : ICustomerAccountService
{
    private sealed record CustomerRow(Guid Id, string CustomerRef, string LegalName, string? PrimaryContactName,
        string? PrimaryContactEmail, string? BillingEmail, string? Phone, string CountryCode, string BillingCurrency,
        string? TaxId, bool TaxExempt, string? CommercialTerms, string Status, DateTimeOffset CreatedAt);

    public async Task<IReadOnlyList<CustomerDto>> ListAsync(string? status, CancellationToken ct = default)
    {
        var rows = await db.Database.SqlQuery<CustomerRow>($"""
            SELECT id AS "Id", customer_ref AS "CustomerRef", legal_name AS "LegalName", primary_contact_name AS "PrimaryContactName",
                   primary_contact_email AS "PrimaryContactEmail", billing_email AS "BillingEmail", phone AS "Phone",
                   country_code AS "CountryCode", billing_currency AS "BillingCurrency", tax_id AS "TaxId",
                   tax_exempt AS "TaxExempt", commercial_terms AS "CommercialTerms", status AS "Status", created_at AS "CreatedAt"
            FROM nexora.platform_customer
            WHERE ({status}::text IS NULL OR status = {status}::text)
            ORDER BY created_at DESC
            """).ToListAsync(ct);
        var result = new List<CustomerDto>(rows.Count);
        foreach (var c in rows) result.Add(await ToDtoAsync(c, ct));
        return result;
    }

    public async Task<Result<CustomerDto>> GetAsync(Guid id, CancellationToken ct = default)
    {
        var c = await LoadAsync(id, ct);
        return c is null ? Error.NotFound("platform.customer", "Client introuvable.") : Result.Success(await ToDtoAsync(c, ct));
    }

    public async Task<Result<CustomerDto>> CreateAsync(CreateCustomerRequest r, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(r.LegalName)) return Error.Validation("platform.customer_name", "Raison sociale requise.");

        var id = Guid.NewGuid();
        var seq = await db.Database.SqlQuery<long>($"""
            SELECT nextval('nexora.platform_customer_seq')::bigint AS "Value"
            """).FirstAsync(ct);
        var reference = $"NXC-{seq:D6}";

        await db.Database.ExecuteSqlAsync($"""
            INSERT INTO nexora.platform_customer
                (id, customer_ref, legal_name, primary_contact_name, primary_contact_email, billing_email, phone,
                 country_code, billing_currency, tax_id, tax_exempt, commercial_terms, created_by)
            VALUES ({id}, {reference}, {r.LegalName.Trim()}, {r.PrimaryContactName}, {r.PrimaryContactEmail}, {r.BillingEmail}, {r.Phone},
                    {(r.CountryCode ?? "HT").ToUpperInvariant()}, {(r.BillingCurrency ?? "USD").ToUpperInvariant()}, {r.TaxId},
                    {r.TaxExempt}, {r.CommercialTerms}, {tenant.UserId})
            """, ct);
        await uow.SaveChangesAsync(ct);
        return Result.Success(await ToDtoAsync((await LoadAsync(id, ct))!, ct));
    }

    public async Task<Result<CustomerDto>> UpdateAsync(Guid id, UpdateCustomerRequest r, CancellationToken ct = default)
    {
        var c = await LoadAsync(id, ct);
        if (c is null) return Error.NotFound("platform.customer", "Client introuvable.");
        if (r.Status is { } st && st is not ("active" or "suspended" or "closed"))
            return Error.Validation("platform.customer_status", "Statut invalide.");

        await db.Database.ExecuteSqlAsync($"""
            UPDATE nexora.platform_customer SET
                legal_name = COALESCE({r.LegalName}, legal_name),
                primary_contact_name = COALESCE({r.PrimaryContactName}, primary_contact_name),
                primary_contact_email = COALESCE({r.PrimaryContactEmail}, primary_contact_email),
                billing_email = COALESCE({r.BillingEmail}, billing_email),
                phone = COALESCE({r.Phone}, phone),
                country_code = COALESCE({r.CountryCode}, country_code),
                billing_currency = COALESCE({r.BillingCurrency}, billing_currency),
                tax_id = COALESCE({r.TaxId}, tax_id),
                tax_exempt = COALESCE({r.TaxExempt}, tax_exempt),
                commercial_terms = COALESCE({r.CommercialTerms}, commercial_terms),
                status = COALESCE({r.Status}, status),
                updated_at = now()
            WHERE id = {id}
            """, ct);
        await uow.SaveChangesAsync(ct);
        return Result.Success(await ToDtoAsync((await LoadAsync(id, ct))!, ct));
    }

    public async Task<Result<CustomerDto>> LinkCompanyAsync(Guid id, Guid companyId, CancellationToken ct = default)
    {
        var c = await LoadAsync(id, ct);
        if (c is null) return Error.NotFound("platform.customer", "Client introuvable.");

        var companyExists = await db.Database.SqlQuery<bool>($"""
            SELECT EXISTS(SELECT 1 FROM nexora.company WHERE id = {companyId}) AS "Value"
            """).FirstAsync(ct);
        if (!companyExists) return Error.NotFound("platform.company", "Entreprise introuvable.");

        var takenBy = await db.Database.SqlQuery<Guid?>($"""
            SELECT customer_id AS "Value" FROM nexora.platform_customer_company WHERE company_id = {companyId}
            """).FirstOrDefaultAsync(ct);
        if (takenBy is { } t && t != id)
            return Error.Conflict("platform.company_linked", "Cette entreprise est déjà rattachée à un autre client.");

        await db.Database.ExecuteSqlAsync($"""
            INSERT INTO nexora.platform_customer_company (customer_id, company_id)
            VALUES ({id}, {companyId})
            ON CONFLICT DO NOTHING
            """, ct);
        await uow.SaveChangesAsync(ct);
        return Result.Success(await ToDtoAsync(c, ct));
    }

    public async Task<Result> UnlinkCompanyAsync(Guid id, Guid companyId, CancellationToken ct = default)
    {
        var n = await db.Database.ExecuteSqlAsync($"""
            DELETE FROM nexora.platform_customer_company WHERE customer_id = {id} AND company_id = {companyId}
            """, ct);
        await uow.SaveChangesAsync(ct);
        return n == 0 ? Error.NotFound("platform.link", "Rattachement introuvable.") : Result.Success();
    }

    private async Task<CustomerRow?> LoadAsync(Guid id, CancellationToken ct)
        => await db.Database.SqlQuery<CustomerRow>($"""
            SELECT id AS "Id", customer_ref AS "CustomerRef", legal_name AS "LegalName", primary_contact_name AS "PrimaryContactName",
                   primary_contact_email AS "PrimaryContactEmail", billing_email AS "BillingEmail", phone AS "Phone",
                   country_code AS "CountryCode", billing_currency AS "BillingCurrency", tax_id AS "TaxId",
                   tax_exempt AS "TaxExempt", commercial_terms AS "CommercialTerms", status AS "Status", created_at AS "CreatedAt"
            FROM nexora.platform_customer WHERE id = {id}
            """).FirstOrDefaultAsync(ct);

    private async Task<CustomerDto> ToDtoAsync(CustomerRow c, CancellationToken ct)
    {
        var companies = await db.Database.SqlQuery<Guid>($"""
            SELECT company_id AS "Value" FROM nexora.platform_customer_company WHERE customer_id = {c.Id} ORDER BY linked_at
            """).ToListAsync(ct);
        return new CustomerDto(c.Id, c.CustomerRef, c.LegalName, c.PrimaryContactName, c.PrimaryContactEmail, c.BillingEmail,
            c.Phone, c.CountryCode, c.BillingCurrency, c.TaxId, c.TaxExempt, c.CommercialTerms, c.Status, companies, c.CreatedAt);
    }
}

// =============================================================================
// Portail : résout l'organisation cliente de l'entreprise active (§45).
// =============================================================================
public sealed class PlatformScope(NexoraDbContext db, ITenantContext tenant) : IPlatformScope
{
    public async Task<Guid?> CurrentCustomerIdAsync(CancellationToken ct = default)
    {
        if (tenant.ActiveCompanyId is not { } company)
        {
            // Aucune entreprise active : si l'utilisateur n'appartient qu'à une seule, on la prend.
            var only = tenant.CompanyIds.Count == 1 ? tenant.CompanyIds.First() : (Guid?)null;
            if (only is null) return null;
            company = only.Value;
        }
        return await db.Database.SqlQuery<Guid?>($"""
            SELECT customer_id AS "Value" FROM nexora.platform_customer_company WHERE company_id = {company}
            """).FirstOrDefaultAsync(ct);
    }
}

// =============================================================================
// Métriques SaaS (§55) — pilotage de la plateforme, jamais les KPI des clients.
// =============================================================================
public sealed class SqlSaasMetricsService(NexoraDbContext db, IDateTimeProvider clock) : ISaasMetricsService
{
    private sealed record MetricRow(decimal Mrr, int Active, int Trialing, int PastDue, int Suspended,
        int Canceled30, int TrialsEnded30, int TrialsConverted30, int PaymentsOk30, int PaymentsTotal30, int OpenTickets, int Review);

    public async Task<SaasMetricsDto> GetAsync(CancellationToken ct = default)
    {
        var m = await db.Database.SqlQuery<MetricRow>($"""
            SELECT
              COALESCE((SELECT SUM(CASE WHEN period = 'annual' THEN price_amount / 12 ELSE price_amount END)
                        FROM nexora.subscription WHERE status = 'active'), 0) AS "Mrr",
              (SELECT COUNT(*)::int FROM nexora.subscription WHERE status = 'active')     AS "Active",
              (SELECT COUNT(*)::int FROM nexora.subscription WHERE status = 'trialing')   AS "Trialing",
              (SELECT COUNT(*)::int FROM nexora.subscription WHERE status = 'past_due')   AS "PastDue",
              (SELECT COUNT(*)::int FROM nexora.subscription WHERE status = 'suspended')  AS "Suspended",
              (SELECT COUNT(*)::int FROM nexora.subscription WHERE status = 'canceled' AND canceled_at > now() - interval '30 days') AS "Canceled30",
              (SELECT COUNT(*)::int FROM nexora.billing_event WHERE event_type = 'TrialExpired' AND occurred_at > now() - interval '30 days') AS "TrialsEnded30",
              (SELECT COUNT(*)::int FROM nexora.billing_event WHERE event_type = 'SubscriptionCreated' AND (payload->>'fromTrial') = 'true' AND occurred_at > now() - interval '30 days') AS "TrialsConverted30",
              (SELECT COUNT(*)::int FROM nexora.platform_payment WHERE status = 'succeeded' AND received_at > now() - interval '30 days') AS "PaymentsOk30",
              (SELECT COUNT(*)::int FROM nexora.platform_payment WHERE status IN ('succeeded','failed') AND received_at > now() - interval '30 days') AS "PaymentsTotal30",
              (SELECT COUNT(*)::int FROM nexora.support_ticket WHERE status IN ('open','pending')) AS "OpenTickets",
              (SELECT COUNT(*)::int FROM nexora.platform_payment WHERE review_required) AS "Review"
            """).FirstAsync(ct);

        var churnBase = m.Active + m.Canceled30;
        var churn = churnBase > 0 ? Math.Round(100m * m.Canceled30 / churnBase, 2) : 0m;
        var conv = m.TrialsEnded30 + m.TrialsConverted30 > 0
            ? Math.Round(100m * m.TrialsConverted30 / (m.TrialsEnded30 + m.TrialsConverted30), 2) : 0m;
        var arpu = m.Active > 0 ? Math.Round(m.Mrr / m.Active, 2) : 0m;
        var paySuccess = m.PaymentsTotal30 > 0 ? Math.Round(100m * m.PaymentsOk30 / m.PaymentsTotal30, 2) : 0m;

        return new SaasMetricsDto(
            Math.Round(m.Mrr, 2), Math.Round(m.Mrr * 12, 2), "USD",
            m.Active, m.Trialing, m.PastDue, m.Suspended, m.Canceled30, churn, conv, arpu, paySuccess,
            m.OpenTickets, m.Review, clock.UtcNow);
    }
}
