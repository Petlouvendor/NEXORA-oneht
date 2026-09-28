using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Nexora.Application.Abstractions;
using Nexora.Application.Reporting;
using Nexora.Contracts.Reporting;
using Nexora.Domain.Common;
using Nexora.Infrastructure.Persistence;

namespace Nexora.Infrastructure.Reporting;

// =============================================================================
// Reporting & analytics — couche de confiance (Phase 31 §44-46). Lectures dérivées
// des sources métier et comptables ; le snapshot est la seule persistance.
// =============================================================================

/// <summary>Réconciliation KPI (§46) : reporting == source métier / comptable.</summary>
public sealed class SqlKpiReconciliation(NexoraDbContext db, ITenantContext tenant) : IKpiReconciliation
{
    private Guid Company => tenant.ActiveCompanyId ?? throw new InvalidOperationException("Entreprise active requise.");
    private const decimal Tolerance = 0.01m;

    private Task<decimal> Val(FormattableString sql, CancellationToken ct)
        => db.Database.SqlQuery<decimal>(sql).FirstAsync(ct);

    /// <summary>Solde grand livre (net débit, signé) d'un compte identifié par sa clé de mapping, à la date <paramref name="asOf"/>.</summary>
    private async Task<decimal> GlByKey(string key, DateOnly asOf, bool debitNormal, CancellationToken ct)
    {
        var netDebit = await Val($"""
            SELECT COALESCE(SUM(l.base_debit - l.base_credit), 0) AS "Value"
            FROM nexora.journal_entry_line l
            JOIN nexora.journal_entry e ON e.id = l.journal_entry_id
            JOIN nexora.account_mapping m ON m.account_id = l.account_id AND m.company_id = e.company_id
            WHERE e.company_id = {Company} AND e.status = 'posted' AND m.mapping_key = {key} AND e.entry_date <= {asOf}
            """, ct);
        return debitNormal ? netDebit : -netDebit;
    }

    private async Task<decimal> GlByKeyPeriod(string key, DateOnly from, DateOnly to, bool debitNormal, CancellationToken ct)
    {
        var netDebit = await Val($"""
            SELECT COALESCE(SUM(l.base_debit - l.base_credit), 0) AS "Value"
            FROM nexora.journal_entry_line l
            JOIN nexora.journal_entry e ON e.id = l.journal_entry_id
            JOIN nexora.account_mapping m ON m.account_id = l.account_id AND m.company_id = e.company_id
            WHERE e.company_id = {Company} AND e.status = 'posted' AND m.mapping_key = {key}
              AND e.entry_date BETWEEN {from} AND {to}
            """, ct);
        return debitNormal ? netDebit : -netDebit;
    }

    public async Task<ReconciliationReportDto> ReconcileAsync(DateOnly? fromN, DateOnly? toN, CancellationToken ct = default)
    {
        var today = await ReportingClock.TodayAsync(db, ct);
        var to = toN ?? today;
        var from = fromN ?? new DateOnly(to.Year, 1, 1);
        var currency = await db.Database.SqlQuery<string>($"""
            SELECT cur.code AS "Value" FROM nexora.company co JOIN nexora.currency cur ON cur.id = co.default_currency_id
            WHERE co.id = {Company}
            """).FirstAsync(ct);

        var lines = new List<ReconciliationLineDto>();

        // 1. CA net HT : ventes opérationnelles vs produits comptables (§12, §46)
        var salesHt = await Val($"""
            SELECT COALESCE(SUM(subtotal - discount_total), 0) AS "Value" FROM nexora.sale
            WHERE company_id = {Company} AND status IN ('completed','partially_refunded','refunded')
              AND sold_at::date BETWEEN {from} AND {to}
            """, ct);
        var returnsHt = await Val($"""
            SELECT COALESCE(SUM(sr.refund_total - COALESCE(t.tax, 0)), 0) AS "Value"
            FROM nexora.sale_return sr
            LEFT JOIN LATERAL (SELECT SUM(tax_amount) tax FROM nexora.sale_return_line WHERE sale_return_id = sr.id) t ON true
            WHERE sr.company_id = {Company} AND sr.status = 'completed' AND sr.returned_at::date BETWEEN {from} AND {to}
            """, ct);
        var opCa = Math.Round(salesHt - returnsHt, 2);
        var glRevenue = await GlByKeyPeriod("sales_revenue", from, to, debitNormal: false, ct);
        var glReturns = await GlByKeyPeriod("sales_returns", from, to, debitNormal: true, ct);
        var acCa = Math.Round(glRevenue - glReturns, 2);
        lines.Add(Line("sales_revenue", "Chiffre d'affaires net HT",
            "Ventes complétées (subtotal − remise) − retours HT, vs comptes de produits nets des retours sur ventes",
            opCa, acCa));

        // 2. Encaissements : paiements confirmés vs mouvements de trésorerie (§46)
        var opCashIn = await Val($"""
            SELECT COALESCE(SUM(amount), 0) AS "Value" FROM nexora.payment
            WHERE company_id = {Company} AND direction = 'inbound' AND status = 'completed'
              AND received_at::date BETWEEN {from} AND {to}
            """, ct);
        var ftCashIn = await Val($"""
            SELECT COALESCE(SUM(amount), 0) AS "Value" FROM nexora.financial_transaction
            WHERE company_id = {Company} AND reference_type = 'payment' AND amount > 0
              AND occurred_at::date BETWEEN {from} AND {to}
            """, ct);
        lines.Add(Line("cash_in", "Encaissements clients",
            "Σ paiements entrants confirmés vs Σ mouvements de trésorerie liés aux paiements",
            Math.Round(opCashIn, 2), Math.Round(ftCashIn, 2)));

        // 3. Créances : sous-livre clients vs compte 4111 (§46)
        var opAr = await Val($"""
            SELECT COALESCE(SUM(remaining_amount), 0) AS "Value" FROM nexora.customer_receivable
            WHERE company_id = {Company} AND status IN ('OPEN','PARTIALLY_PAID','OVERDUE','IN_DISPUTE')
            """, ct);
        var glAr = await GlByKey("accounts_receivable", to, debitNormal: true, ct);
        lines.Add(Line("receivables", "Créances clients",
            "Σ soldes ouverts du sous-livre clients vs solde du compte collectif clients",
            Math.Round(opAr, 2), Math.Round(glAr, 2)));

        // 4. Dettes : sous-livre fournisseurs vs compte 4011 (§46)
        var opAp = await Val($"""
            SELECT COALESCE(SUM(remaining_amount), 0) AS "Value" FROM nexora.supplier_payable
            WHERE company_id = {Company} AND status IN ('OPEN','PARTIALLY_PAID','OVERDUE')
            """, ct);
        var glAp = await GlByKey("accounts_payable", to, debitNormal: false, ct);
        lines.Add(Line("payables", "Dettes fournisseurs",
            "Σ soldes ouverts du sous-livre fournisseurs vs solde du compte collectif fournisseurs",
            Math.Round(opAp, 2), Math.Round(glAp, 2)));

        // 5. Stock : valorisation ledger vs compte de stock (§46)
        var opStock = await Val($"""
            SELECT COALESCE(SUM(quantity_on_hand * average_cost), 0) AS "Value" FROM nexora.stock_balance
            WHERE company_id = {Company}
            """, ct);
        var glStock = await GlByKey("inventory", to, debitNormal: true, ct);
        lines.Add(Line("inventory", "Valeur du stock",
            "Σ (quantité × coût moyen) du ledger stock vs solde du compte de stock",
            Math.Round(opStock, 2), Math.Round(glStock, 2)));

        var reconciled = lines.Count(l => l.Reconciled);
        return new ReconciliationReportDto(from, to, currency, Tolerance,
            reconciled == lines.Count, reconciled, lines.Count, lines, DateTimeOffset.UtcNow);
    }

    private static ReconciliationLineDto Line(string metric, string label, string definition, decimal op, decimal ac)
    {
        var diff = Math.Round(op - ac, 2);
        var ok = Math.Abs(diff) <= Tolerance;
        return new ReconciliationLineDto(metric, label, definition, op, ac, diff, ok,
            ok ? null : $"Écart de {diff} — vérifier les comptabilisations manquantes ou doubles.");
    }
}

// -----------------------------------------------------------------------------

/// <summary>Contrôle qualité des données (§45).</summary>
public sealed class SqlDataQualityService(NexoraDbContext db, ITenantContext tenant) : IDataQualityService
{
    private Guid Company => tenant.ActiveCompanyId ?? throw new InvalidOperationException("Entreprise active requise.");

    private static readonly string[] RequiredMappings =
    [
        "sales_revenue", "sales_tax_output", "accounts_receivable", "accounts_payable",
        "cogs", "inventory", "purchase_tax_input", "expense_default", "cash", "bank",
    ];

    public async Task<DataQualityReportDto> EvaluateAsync(CancellationToken ct = default)
    {
        var checks = new List<DataQualityCheckDto>();

        async Task Add(string key, string label, string cat, string sev, FormattableString count, FormattableString? samples, string? detail = null)
        {
            var n = await db.Database.SqlQuery<int>(count).FirstAsync(ct);
            var ids = n > 0 && samples is not null
                ? await db.Database.SqlQuery<Guid>(samples).ToListAsync(ct)
                : new List<Guid>();
            checks.Add(new DataQualityCheckDto(key, label, cat, sev, n, n > 0 ? detail : null, ids));
        }

        await Add("unposted_entries", "Écritures comptables non validées", "unposted", "warn",
            $"""SELECT COUNT(*)::int AS "Value" FROM nexora.journal_entry WHERE company_id = {Company} AND status = 'draft'""",
            $"""SELECT id AS "Value" FROM nexora.journal_entry WHERE company_id = {Company} AND status = 'draft' LIMIT 5""",
            "Brouillons à valider ou supprimer avant clôture (§8, §45).");

        await Add("bank_unreconciled", "Lignes de relevé bancaire non rapprochées", "unreconciled", "warn",
            $"""
             SELECT COUNT(*)::int AS "Value" FROM nexora.bank_statement_line l
             JOIN nexora.bank_statement s ON s.id = l.bank_statement_id
             WHERE s.company_id = {Company} AND l.status = 'unmatched'
             """,
            $"""
             SELECT l.id AS "Value" FROM nexora.bank_statement_line l
             JOIN nexora.bank_statement s ON s.id = l.bank_statement_id
             WHERE s.company_id = {Company} AND l.status = 'unmatched' LIMIT 5
             """);

        await Add("suspense_open", "Éléments de suspens non résolus", "unreconciled", "info",
            $"""SELECT COUNT(*)::int AS "Value" FROM nexora.treasury_suspense_item WHERE company_id = {Company} AND status = 'open'""",
            $"""SELECT id AS "Value" FROM nexora.treasury_suspense_item WHERE company_id = {Company} AND status = 'open' LIMIT 5""");

        await Add("negative_stock", "Soldes de stock négatifs", "integrity", "error",
            $"""SELECT COUNT(*)::int AS "Value" FROM nexora.stock_balance WHERE company_id = {Company} AND quantity_on_hand < 0""",
            $"""SELECT id AS "Value" FROM nexora.stock_balance WHERE company_id = {Company} AND quantity_on_hand < 0 LIMIT 5""");

        await Add("products_no_price", "Produits actifs sans prix de vente", "missing", "warn",
            $"""
             SELECT COUNT(*)::int AS "Value" FROM nexora.product p
             WHERE p.company_id = {Company} AND p.status = 'active' AND p.product_type <> 'service'
               AND NOT EXISTS (SELECT 1 FROM nexora.product_price pp WHERE pp.product_id = p.id AND pp.price_type = 'sale')
             """,
            $"""
             SELECT p.id AS "Value" FROM nexora.product p
             WHERE p.company_id = {Company} AND p.status = 'active' AND p.product_type <> 'service'
               AND NOT EXISTS (SELECT 1 FROM nexora.product_price pp WHERE pp.product_id = p.id AND pp.price_type = 'sale')
             LIMIT 5
             """);

        await Add("sold_products_no_cost", "Produits vendus sans coût figé", "missing", "error",
            $"""
             SELECT COUNT(DISTINCT sl.product_id)::int AS "Value"
             FROM nexora.sale_line sl JOIN nexora.sale s ON s.id = sl.sale_id
             WHERE s.company_id = {Company} AND s.status = 'completed' AND COALESCE(sl.unit_cost, 0) = 0
             """,
            $"""
             SELECT DISTINCT sl.product_id AS "Value"
             FROM nexora.sale_line sl JOIN nexora.sale s ON s.id = sl.sale_id
             WHERE s.company_id = {Company} AND s.status = 'completed' AND COALESCE(sl.unit_cost, 0) = 0
             LIMIT 5
             """,
            "Marge faussée : aucun coût des ventes pour ces produits (§14, §45).");

        await Add("customers_no_terms", "Clients à crédit sans conditions de paiement", "missing", "info",
            $"""
             SELECT COUNT(*)::int AS "Value" FROM nexora.customer c
             WHERE c.company_id = {Company} AND COALESCE(c.credit_limit, 0) > 0
               AND NOT EXISTS (SELECT 1 FROM nexora.customer_account ca
                               WHERE ca.customer_id = c.id AND ca.company_id = c.company_id AND ca.payment_terms_id IS NOT NULL)
             """,
            $"""
             SELECT c.id AS "Value" FROM nexora.customer c
             WHERE c.company_id = {Company} AND COALESCE(c.credit_limit, 0) > 0
               AND NOT EXISTS (SELECT 1 FROM nexora.customer_account ca
                               WHERE ca.customer_id = c.id AND ca.company_id = c.company_id AND ca.payment_terms_id IS NOT NULL)
             LIMIT 5
             """);

        await Add("documents_no_verification", "Documents émis sans code de vérification", "integrity", "warn",
            $"""
             SELECT COUNT(*)::int AS "Value" FROM nexora.issued_document
             WHERE company_id = {Company} AND status IN ('issued','sent') AND verification_code IS NULL
             """,
            $"""
             SELECT id AS "Value" FROM nexora.issued_document
             WHERE company_id = {Company} AND status IN ('issued','sent') AND verification_code IS NULL LIMIT 5
             """);

        await Add("sales_no_lines", "Ventes complétées sans ligne", "integrity", "error",
            $"""
             SELECT COUNT(*)::int AS "Value" FROM nexora.sale s
             WHERE s.company_id = {Company} AND s.status = 'completed'
               AND NOT EXISTS (SELECT 1 FROM nexora.sale_line sl WHERE sl.sale_id = s.id)
             """,
            $"""
             SELECT s.id AS "Value" FROM nexora.sale s
             WHERE s.company_id = {Company} AND s.status = 'completed'
               AND NOT EXISTS (SELECT 1 FROM nexora.sale_line sl WHERE sl.sale_id = s.id) LIMIT 5
             """);

        // Mappings comptables requis manquants
        var mapped = await db.Database.SqlQuery<string>($"""
            SELECT mapping_key AS "Value" FROM nexora.account_mapping WHERE company_id = {Company}
            """).ToListAsync(ct);
        var missing = RequiredMappings.Where(k => !mapped.Contains(k)).ToArray();
        checks.Add(new DataQualityCheckDto("missing_account_mappings", "Mappings comptables essentiels manquants",
            "mapping", "error", missing.Length,
            missing.Length > 0 ? $"Clés absentes : {string.Join(", ", missing)}" : null, []));

        // Score : pénalité une fois par contrôle en échec, selon la sévérité.
        decimal score = 100m;
        foreach (var c in checks.Where(c => c.Count > 0))
            score -= c.Severity switch { "error" => 15m, "warn" => 6m, _ => 2m };
        score = Math.Max(0m, Math.Round(score, 2));
        var grade = score >= 90m ? "A" : score >= 75m ? "B" : score >= 60m ? "C" : score >= 40m ? "D" : "E";

        return new DataQualityReportDto(score, grade, checks.Count, checks.Count(c => c.Count > 0),
            checks, DateTimeOffset.UtcNow);
    }
}

// -----------------------------------------------------------------------------

/// <summary>Photos analytiques reproductibles (§44).</summary>
public sealed class SqlAnalyticsSnapshotService(
    NexoraDbContext db, ITenantContext tenant,
    IKpiReconciliation reconciliation, IDataQualityService quality) : IAnalyticsSnapshotService
{
    private Guid Company => tenant.ActiveCompanyId ?? throw new InvalidOperationException("Entreprise active requise.");

    // Sérialisation alignée sur la convention HTTP (camelCase) → le snapshot se relit à l'identique.
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    private sealed record HeadRow(
        Guid Id, string Kind, string? Label, DateOnly AsOfDate, DateOnly? PeriodFrom, DateOnly? PeriodTo,
        string CalculationVersion, decimal? Score, DateTimeOffset CreatedAt);

    public async Task<Result<AnalyticsSnapshotDto>> CreateAsync(CreateSnapshotRequest r, CancellationToken ct = default)
    {
        if (r.Kind is not ("reconciliation" or "data_quality"))
            return Error.Validation("reporting.snapshot_kind", "Type de snapshot inconnu (reconciliation | data_quality).");

        var today = await ReportingClock.TodayAsync(db, ct);
        var id = Guid.NewGuid();
        string json;
        decimal? score = null;
        DateOnly? from = null, to = null;

        if (r.Kind == "reconciliation")
        {
            var rep = await reconciliation.ReconcileAsync(r.From, r.To, ct);
            json = JsonSerializer.Serialize(rep, JsonOpts);
            from = rep.From; to = rep.To;
        }
        else
        {
            var rep = await quality.EvaluateAsync(ct);
            json = JsonSerializer.Serialize(rep, JsonOpts);
            score = rep.Score;
        }

        await db.Database.ExecuteSqlAsync($"""
            INSERT INTO nexora.analytics_snapshot
              (id, company_id, kind, label, as_of_date, period_from, period_to, calculation_version, score, result, created_by)
            VALUES ({id}, {Company}, {r.Kind}, {r.Label}, {today}, {from}, {to}, 'v1', {score}, {json}::jsonb, {tenant.UserId})
            """, ct);

        return Result.Success(new AnalyticsSnapshotDto(id, r.Kind, r.Label, today, from, to, "v1", score, DateTimeOffset.UtcNow));
    }

    public async Task<IReadOnlyList<AnalyticsSnapshotDto>> ListAsync(string? kind, CancellationToken ct = default)
    {
        var rows = await db.Database.SqlQuery<HeadRow>($"""
            SELECT id AS "Id", kind AS "Kind", label AS "Label", as_of_date AS "AsOfDate",
                   period_from AS "PeriodFrom", period_to AS "PeriodTo", calculation_version AS "CalculationVersion",
                   score AS "Score", created_at AS "CreatedAt"
            FROM nexora.analytics_snapshot
            WHERE company_id = {Company} AND ({kind}::text IS NULL OR kind = {kind})
            ORDER BY created_at DESC LIMIT 100
            """).ToListAsync(ct);
        return rows.Select(h => new AnalyticsSnapshotDto(h.Id, h.Kind, h.Label, h.AsOfDate, h.PeriodFrom, h.PeriodTo,
            h.CalculationVersion, h.Score, h.CreatedAt)).ToList();
    }

    public async Task<Result<AnalyticsSnapshotDetailDto>> GetAsync(Guid id, CancellationToken ct = default)
    {
        var h = await db.Database.SqlQuery<HeadRow>($"""
            SELECT id AS "Id", kind AS "Kind", label AS "Label", as_of_date AS "AsOfDate",
                   period_from AS "PeriodFrom", period_to AS "PeriodTo", calculation_version AS "CalculationVersion",
                   score AS "Score", created_at AS "CreatedAt"
            FROM nexora.analytics_snapshot WHERE id = {id} AND company_id = {Company}
            """).FirstOrDefaultAsync(ct);
        if (h is null) return Error.NotFound("reporting.snapshot_not_found", "Snapshot introuvable.");

        var result = await db.Database.SqlQuery<string>($"""
            SELECT result::text AS "Value" FROM nexora.analytics_snapshot WHERE id = {id}
            """).FirstAsync(ct);
        var header = new AnalyticsSnapshotDto(h.Id, h.Kind, h.Label, h.AsOfDate, h.PeriodFrom, h.PeriodTo,
            h.CalculationVersion, h.Score, h.CreatedAt);
        return Result.Success(new AnalyticsSnapshotDetailDto(header, JsonDocument.Parse(result).RootElement.Clone()));
    }
}
