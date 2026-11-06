using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Nexora.Application.Abstractions;
using Nexora.Application.Reporting;
using Nexora.Contracts.Reporting;
using Nexora.Infrastructure.Persistence;
using Nexora.Domain.Common;
using Nexora.Domain.Accounting;

namespace Nexora.Infrastructure.Reporting;

public sealed class SqlAgingReports(NexoraDbContext db, ITenantContext tenant) : IAgingReports
{
    private Guid Company => tenant.ActiveCompanyId ?? throw TenantErrors.ActiveCompanyRequired();
    private sealed record Bucket(string label, int from_days, int? to_days);
    private sealed record AgingRaw(string Party, decimal Remaining, int DaysOverdue);

    private const string DefaultBuckets =
        """[{"label":"Non échu","from_days":-100000,"to_days":0},{"label":"1-30 j","from_days":1,"to_days":30},{"label":"31-60 j","from_days":31,"to_days":60},{"label":"61-90 j","from_days":61,"to_days":90},{"label":"> 90 j","from_days":91,"to_days":null}]""";

    public Task<AgingResponse> ReceivablesAsync(DateOnly? asOf = null, CancellationToken ct = default) => Build("receivable", asOf, ct);
    public Task<AgingResponse> PayablesAsync(DateOnly? asOf = null, CancellationToken ct = default) => Build("payable", asOf, ct);

    private async Task<AgingResponse> Build(string kind, DateOnly? asOfOrNull, CancellationToken ct)
    {
        var asOf = asOfOrNull ?? await ReportingClock.TodayAsync(db, ct);
        var currency = await (from c in db.Companies.AsNoTracking()
            join cur in db.Currencies.AsNoTracking() on c.DefaultCurrencyId equals cur.Id
            where c.Id == Company select cur.Code).FirstAsync(ct);

        var bucketsJson = await db.Database.SqlQuery<string>($"""
            SELECT buckets::text AS "Value" FROM nexora.aging_bucket_set
            WHERE company_id = {Company} AND kind = {kind} AND is_default LIMIT 1
            """).FirstOrDefaultAsync(ct);
        var buckets = JsonSerializer.Deserialize<List<Bucket>>(bucketsJson ?? DefaultBuckets)!;

        var table = kind == "receivable" ? "customer_receivable" : "supplier_payable";
        var partyTable = kind == "receivable" ? "customer" : "supplier";
        var partyFk = kind == "receivable" ? "customer_id" : "supplier_id";

        // Reste dû = remaining_amount, avoirs fournisseurs déduits (audit 2026-10 éd. 2, A-022 résidu : original − payé
        // ignorait les avoirs), converti dans la devise de l'entreprise (A-046).
        var sql =
            "SELECT p.name AS \"Party\", " +
            "nexora.convert_amount(r.remaining_amount, r.currency_id, (SELECT default_currency_id FROM nexora.company WHERE id = {1})) AS \"Remaining\", " +
            "GREATEST(-100000, ({0}::date - COALESCE(r.due_date, r.issue_date)))::int AS \"DaysOverdue\" " +
            $"FROM nexora.{table} r JOIN nexora.{partyTable} p ON p.id = r.{partyFk} " +
            "WHERE r.company_id = {1} AND r.status IN ('OPEN','PARTIALLY_PAID','OVERDUE') AND r.remaining_amount > 0 " +
            "AND nexora.convert_amount(r.remaining_amount, r.currency_id, (SELECT default_currency_id FROM nexora.company WHERE id = {1})) IS NOT NULL";
        var raw = await db.Database.SqlQueryRaw<AgingRaw>(sql, asOf, Company).ToListAsync(ct);

        int BucketIndex(int days) =>
            buckets.FindIndex(b => days >= b.from_days && (b.to_days is null || days <= b.to_days)) is var i && i >= 0 ? i : buckets.Count - 1;

        var rows = raw.GroupBy(x => x.Party).Select(g =>
        {
            var arr = new decimal[buckets.Count];
            foreach (var x in g) arr[BucketIndex(x.DaysOverdue)] += Math.Round(x.Remaining, 2);
            return new AgingLine(g.Key, Math.Round(g.Sum(x => x.Remaining), 2), arr);
        }).OrderByDescending(r => r.Total).ToList();

        return new AgingResponse(kind, asOf, currency, buckets.Select(b => b.label).ToList(), rows, rows.Sum(r => r.Total));
    }
}

public sealed class SqlBudgetReports(NexoraDbContext db, ITenantContext tenant) : IBudgetReports
{
    private Guid Company => tenant.ActiveCompanyId ?? throw TenantErrors.ActiveCompanyRequired();
    private sealed record Row(string AccountCode, string AccountName, string AccountType, decimal Budget, decimal Actual);
    private sealed record BudgetRef(Guid Id, string Name);
    private sealed record Range(DateOnly From, DateOnly To);

    /// <summary>
    /// Budget contre réalisé (audit 2026-10 éd. 2, A-047). Avant : toutes les versions de l'exercice étaient additionnées
    /// — brouillons abandonnés compris, et chaque révision doublait le budget ; le réalisé était pris en valeur absolue
    /// et sur l'année civile. Désormais : le budget en vigueur seul (actif le plus récemment approuvé, sinon le dernier
    /// clos), le réalisé signé selon la nature du compte (produits au crédit, charges au débit) et mesuré sur les dates
    /// de l'exercice comptable.
    /// </summary>
    public async Task<BudgetVsActualResponse> BudgetVsActualAsync(int fiscalYear, ReportFilter filter, CancellationToken ct = default)
    {
        var currency = await (from c in db.Companies.AsNoTracking()
            join cur in db.Currencies.AsNoTracking() on c.DefaultCurrencyId equals cur.Id
            where c.Id == Company select cur.Code).FirstAsync(ct);

        var budget = (await db.Database.SqlQuery<BudgetRef>($"""
            SELECT id AS "Id", name AS "Name" FROM nexora.budget
            WHERE company_id = {Company} AND fiscal_year = {fiscalYear} AND status IN ('active', 'closed')
            ORDER BY (status = 'active') DESC, approved_at DESC NULLS LAST, created_at DESC
            LIMIT 1
            """).ToListAsync(ct)).FirstOrDefault();

        var range = (await db.Database.SqlQuery<Range>($"""
            SELECT start_date AS "From", end_date AS "To" FROM nexora.accounting_period
            WHERE company_id = {Company} AND EXTRACT(YEAR FROM start_date) = {fiscalYear}
            ORDER BY start_date LIMIT 1
            """).ToListAsync(ct)).FirstOrDefault() ?? new Range(new DateOnly(fiscalYear, 1, 1), new DateOnly(fiscalYear, 12, 31));

        var rows = await db.Database.SqlQuery<Row>($"""
            SELECT a.code AS "AccountCode", a.name AS "AccountName", a.account_type AS "AccountType",
                   COALESCE(bl.budget, 0) AS "Budget",
                   COALESCE(act.actual, 0) AS "Actual"
            FROM nexora.account a
            LEFT JOIN LATERAL (
                SELECT SUM(l.amount) budget FROM nexora.budget_line l
                WHERE l.budget_id = {budget?.Id}::uuid AND l.account_id = a.id
            ) bl ON true
            LEFT JOIN LATERAL (
                SELECT SUM(jl.base_debit - jl.base_credit) actual
                FROM nexora.journal_entry_line jl JOIN nexora.journal_entry je ON je.id = jl.journal_entry_id
                WHERE je.company_id = {Company} AND je.status = ANY({LedgerStatuses.InLedger})
                  AND je.entry_date BETWEEN {range.From} AND {range.To} AND jl.account_id = a.id
                  AND je.origin <> 'closing'
            ) act ON true
            WHERE a.company_id = {Company} AND a.account_type IN ('revenue','expense')
              AND (COALESCE(bl.budget,0) <> 0 OR COALESCE(act.actual,0) <> 0)
            ORDER BY a.code
            """).ToListAsync(ct);

        var result = rows.Select(r =>
        {
            // Solde débiteur − créditeur : positif pour une charge, négatif pour un produit — un produit se lit au crédit.
            var actual = r.AccountType == "revenue" ? -r.Actual : r.Actual;
            var variance = actual - r.Budget;
            return new BudgetVsActualRow(r.AccountCode, r.AccountName, Math.Round(r.Budget, 2), Math.Round(actual, 2),
                Math.Round(variance, 2), r.Budget == 0 ? 0m : Math.Round(variance / r.Budget * 100m, 1));
        }).ToList();

        return new BudgetVsActualResponse(budget?.Name ?? "—", fiscalYear, currency, result,
            Math.Round(result.Sum(r => r.Budget), 2), Math.Round(result.Sum(r => r.Actual), 2));
    }
}

/// <summary>Drill-down (Phase 7 §29) : CA → jour → vente → ligne ; créance → client → factures.</summary>
public sealed class SqlDrillDown(NexoraDbContext db, ITenantContext tenant) : IDrillDown
{
    private Guid Company => tenant.ActiveCompanyId ?? throw TenantErrors.ActiveCompanyRequired();
    private sealed record Node(string Level, string Id, string Label, decimal Value);

    public async Task<DrillResponse> ExpandAsync(string path, CancellationToken ct = default)
    {
        var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var head = parts.Length > 0 ? parts[0] : "revenue";
        var key = parts.Length > 1 ? parts[^1] : null;

        return (head, parts.Length) switch
        {
            ("revenue", 1) => await Days(ct),
            ("revenue", 2) => await SalesOfDay(DateOnly.Parse(key!), ct),
            ("revenue", 3) => await LinesOfSale(Guid.Parse(key!), ct),
            ("receivable", 1) => await CustomersWithReceivables(ct),
            ("receivable", 2) => await ReceivablesOfCustomer(Guid.Parse(key!), ct),
            _ => new DrillResponse(path, "leaf", [], null),
        };
    }

    private async Task<DrillResponse> Days(CancellationToken ct)
    {
        var nodes = await db.Database.SqlQuery<Node>($"""
            SELECT 'day' AS "Level", sold_at::date::text AS "Id", sold_at::date::text AS "Label",
                   SUM(subtotal - discount_total) AS "Value"
            FROM nexora.sale WHERE company_id = {Company} AND status IN ('completed','partially_refunded','refunded')
              AND sold_at >= now() - interval '30 days'
            GROUP BY sold_at::date ORDER BY 1 DESC
            """).ToListAsync(ct);
        return Wrap("revenue", nodes, "revenue/{day}");
    }

    private async Task<DrillResponse> SalesOfDay(DateOnly day, CancellationToken ct)
    {
        var nodes = await db.Database.SqlQuery<Node>($"""
            SELECT 'sale' AS "Level", id::text AS "Id", document_number AS "Label", (subtotal - discount_total) AS "Value"
            FROM nexora.sale WHERE company_id = {Company} AND sold_at::date = {day}
              AND status IN ('completed','partially_refunded','refunded')
            ORDER BY document_number
            """).ToListAsync(ct);
        return Wrap("day", nodes, "revenue/{day}/{sale}");
    }

    private async Task<DrillResponse> LinesOfSale(Guid saleId, CancellationToken ct)
    {
        var nodes = await db.Database.SqlQuery<Node>($"""
            SELECT 'line' AS "Level", sl.id::text AS "Id", p.name AS "Label", (sl.line_total - sl.tax_amount) AS "Value"
            FROM nexora.sale_line sl JOIN nexora.product p ON p.id = sl.product_id
            WHERE sl.sale_id = {saleId}
            """).ToListAsync(ct);
        return Wrap("sale", nodes, null);
    }

    private async Task<DrillResponse> CustomersWithReceivables(CancellationToken ct)
    {
        var nodes = await db.Database.SqlQuery<Node>($"""
            SELECT 'customer' AS "Level", cu.id::text AS "Id", cu.name AS "Label",
                   SUM(r.original_amount - r.paid_amount) AS "Value"
            FROM nexora.customer_receivable r JOIN nexora.customer cu ON cu.id = r.customer_id
            WHERE r.company_id = {Company} AND r.status IN ('OPEN','PARTIALLY_PAID','OVERDUE')
            GROUP BY cu.id, cu.name ORDER BY 4 DESC
            """).ToListAsync(ct);
        return Wrap("receivable", nodes, "receivable/{customer}");
    }

    private async Task<DrillResponse> ReceivablesOfCustomer(Guid customerId, CancellationToken ct)
    {
        var nodes = await db.Database.SqlQuery<Node>($"""
            SELECT 'receivable' AS "Level", r.id::text AS "Id",
                   COALESCE(s.document_number, r.id::text) AS "Label", (r.original_amount - r.paid_amount) AS "Value"
            FROM nexora.customer_receivable r LEFT JOIN nexora.sale s ON s.id = r.sale_id
            WHERE r.company_id = {Company} AND r.customer_id = {customerId}
              AND r.status IN ('OPEN','PARTIALLY_PAID','OVERDUE')
            ORDER BY r.issue_date
            """).ToListAsync(ct);
        return Wrap("customer", nodes, null);
    }

    private static DrillResponse Wrap(string level, IReadOnlyList<Node> nodes, string? next)
        => new(level, level, nodes.Select(n => new DrillNode(n.Level, n.Id, n.Label, Math.Round(n.Value, 2))).ToList(), next);
}
