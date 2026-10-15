using Microsoft.EntityFrameworkCore;
using Nexora.Application.Abstractions;
using Nexora.Application.Finance.Abstractions;
using Nexora.Application.Finance.Reports;
using Nexora.Contracts.Finance;
using Nexora.Infrastructure.Persistence;
using Nexora.Domain.Common;

namespace Nexora.Infrastructure.Finance;

/// <summary>
/// Rapports financiers dérivés des écritures validées et des sous-livres (Phase 4 §1, §34).
/// Toutes les lectures sont en devise fonctionnelle (base_debit / base_credit).
/// </summary>
public sealed class SqlFinancialReports(
    NexoraDbContext db, ITenantContext tenant, ICompanyCurrencyContext currency) : IFinancialReports
{
    private Guid Company => tenant.ActiveCompanyId
        ?? throw TenantErrors.ActiveCompanyRequired();

    private sealed record BalanceRow(string Code, string Name, string AccountType, decimal Debit, decimal Credit);
    private sealed record LedgerRow(DateOnly EntryDate, string EntryNumber, string Journal, string? Description, decimal Debit, decimal Credit);
    private sealed record AgedRawRow(string Party, decimal Remaining, int DaysOverdue);
    private sealed record DueRow(DateOnly DueDate, decimal Amount);

    public async Task<TrialBalanceResponse> TrialBalanceAsync(DateOnly asOf, CancellationToken ct = default)
    {
        var func = await currency.FunctionalCurrencyAsync(ct);
        var rows = await db.Database.SqlQuery<BalanceRow>($"""
            SELECT a.code AS "Code", a.name AS "Name", a.account_type AS "AccountType",
                   COALESCE(SUM(l.base_debit),0) AS "Debit", COALESCE(SUM(l.base_credit),0) AS "Credit"
            FROM nexora.journal_entry_line l
            JOIN nexora.journal_entry e ON e.id = l.journal_entry_id
            JOIN nexora.account a ON a.id = l.account_id
            WHERE e.company_id = {Company} AND e.status = 'posted' AND e.entry_date <= {asOf}
            GROUP BY a.code, a.name, a.account_type
            HAVING COALESCE(SUM(l.base_debit),0) <> COALESCE(SUM(l.base_credit),0)
            ORDER BY a.code
            """).ToListAsync(ct);

        var trial = new List<TrialBalanceRow>();
        decimal td = 0, tc = 0;
        foreach (var r in rows)
        {
            var net = r.Debit - r.Credit;
            var d = net > 0 ? net : 0m;
            var c = net < 0 ? -net : 0m;
            td += d; tc += c;
            trial.Add(new TrialBalanceRow(r.Code, r.Name, r.AccountType, d, c));
        }
        return new TrialBalanceResponse(asOf, func, trial, td, tc);
    }

    public async Task<GeneralLedgerResponse> GeneralLedgerAsync(string accountCode, DateOnly from, DateOnly to, CancellationToken ct = default)
    {
        var meta = await db.Accounts.AsNoTracking()
            .Where(a => a.CompanyId == Company && a.Code == accountCode)
            .Select(a => new { a.Name })
            .FirstOrDefaultAsync(ct) ?? throw new InvalidOperationException($"Compte {accountCode} introuvable.");

        var opening = await db.Database.SqlQuery<decimal>($"""
            SELECT COALESCE(SUM(l.base_debit - l.base_credit),0) AS "Value"
            FROM nexora.journal_entry_line l
            JOIN nexora.journal_entry e ON e.id = l.journal_entry_id
            JOIN nexora.account a ON a.id = l.account_id
            WHERE e.company_id = {Company} AND e.status = 'posted'
              AND a.code = {accountCode} AND e.entry_date < {from}
            """).FirstAsync(ct);

        var lines = await db.Database.SqlQuery<LedgerRow>($"""
            SELECT e.entry_date AS "EntryDate", e.entry_number AS "EntryNumber", j.code AS "Journal",
                   e.description AS "Description", l.base_debit AS "Debit", l.base_credit AS "Credit"
            FROM nexora.journal_entry_line l
            JOIN nexora.journal_entry e ON e.id = l.journal_entry_id
            JOIN nexora.journal j ON j.id = e.journal_id
            JOIN nexora.account a ON a.id = l.account_id
            WHERE e.company_id = {Company} AND e.status = 'posted'
              AND a.code = {accountCode} AND e.entry_date BETWEEN {from} AND {to}
            ORDER BY e.entry_date, e.entry_number, l.line_number
            """).ToListAsync(ct);

        var running = opening;
        var result = new List<LedgerLine>();
        foreach (var l in lines)
        {
            running += l.Debit - l.Credit;
            result.Add(new LedgerLine(l.EntryDate, l.EntryNumber, l.Journal, l.Description, l.Debit, l.Credit, running));
        }
        return new GeneralLedgerResponse(accountCode, meta.Name, from, to, opening, result, running);
    }

    public async Task<IncomeStatementResponse> IncomeStatementAsync(DateOnly from, DateOnly to, CancellationToken ct = default)
    {
        var func = await currency.FunctionalCurrencyAsync(ct);
        // Le compte de résultat exclut les écritures de clôture (§18) : il montre toujours
        // le résultat opérationnel, que l'exercice soit soldé ou non.
        var rows = await PeriodBalancesAsync(from, to, excludeClosing: true, ct);

        var revenue = Section("Produits", rows.Where(r => r.AccountType == "revenue"), r => r.Credit - r.Debit);
        var expenses = Section("Charges", rows.Where(r => r.AccountType == "expense"), r => r.Debit - r.Credit);
        return new IncomeStatementResponse(from, to, func, revenue, expenses, revenue.Subtotal - expenses.Subtotal);
    }

    public async Task<BalanceSheetResponse> BalanceSheetAsync(DateOnly asOf, CancellationToken ct = default)
    {
        var func = await currency.FunctionalCurrencyAsync(ct);
        var rows = await PeriodBalancesAsync(DateOnly.MinValue, asOf, excludeClosing: false, ct);

        var assets = Section("Actif", rows.Where(r => r.AccountType == "asset"), r => r.Debit - r.Credit);
        var liabilities = Section("Passif", rows.Where(r => r.AccountType == "liability"), r => r.Credit - r.Debit);
        var equity = Section("Capitaux propres", rows.Where(r => r.AccountType == "equity"), r => r.Credit - r.Debit);

        var result = rows.Where(r => r.AccountType == "revenue").Sum(r => r.Credit - r.Debit)
                   - rows.Where(r => r.AccountType == "expense").Sum(r => r.Debit - r.Credit);

        var balanced = Math.Round(assets.Subtotal - (liabilities.Subtotal + equity.Subtotal + result), 2) == 0m;
        return new BalanceSheetResponse(asOf, func, assets, liabilities, equity, result, balanced);
    }

    public Task<AgedResponse> AgedReceivablesAsync(DateOnly asOf, CancellationToken ct = default)
        => AgedAsync(asOf, receivables: true, ct);

    public Task<AgedResponse> AgedPayablesAsync(DateOnly asOf, CancellationToken ct = default)
        => AgedAsync(asOf, receivables: false, ct);

    public async Task<CashFlowForecastResponse> CashFlowForecastAsync(DateOnly from, DateOnly to, CancellationToken ct = default)
    {
        var func = await currency.FunctionalCurrencyAsync(ct);
        var opening = await db.FinancialAccounts.AsNoTracking()
            .Where(a => a.CompanyId == Company && a.Status == "active"
                        && (a.AccountType == "cash" || a.AccountType == "bank" || a.AccountType == "mobile_money"))
            .SumAsync(a => a.CurrentBalance, ct);

        var inflows = await db.Database.SqlQuery<DueRow>($"""
            SELECT COALESCE(due_date, {to}) AS "DueDate", SUM(remaining_amount) AS "Amount"
            FROM nexora.customer_receivable
            WHERE company_id = {Company} AND status IN ('OPEN','PARTIALLY_PAID','OVERDUE')
            GROUP BY COALESCE(due_date, {to})
            """).ToListAsync(ct);

        var outflows = await db.Database.SqlQuery<DueRow>($"""
            SELECT COALESCE(due_date, {to}) AS "DueDate", SUM(remaining_amount) AS "Amount"
            FROM nexora.supplier_payable
            WHERE company_id = {Company} AND status IN ('OPEN','PARTIALLY_PAID','OVERDUE')
            GROUP BY COALESCE(due_date, {to})
            """).ToListAsync(ct);

        var days = Enumerable.Range(0, to.DayNumber - from.DayNumber + 1).Select(from.AddDays).ToList();
        var running = opening;
        var points = new List<CashFlowForecastPoint>();
        foreach (var day in days)
        {
            var inflow = inflows.Where(x => x.DueDate == day).Sum(x => x.Amount);
            var outflow = outflows.Where(x => x.DueDate == day).Sum(x => x.Amount);
            running += inflow - outflow;
            if (inflow != 0 || outflow != 0 || day == from || day == to)
                points.Add(new CashFlowForecastPoint(day, inflow, outflow, running));
        }
        return new CashFlowForecastResponse(from, to, func, opening, points);
    }

    // --- helpers ---------------------------------------------------------

    private Task<List<BalanceRow>> PeriodBalancesAsync(DateOnly from, DateOnly to, bool excludeClosing, CancellationToken ct) =>
        db.Database.SqlQuery<BalanceRow>($"""
            SELECT a.code AS "Code", a.name AS "Name", a.account_type AS "AccountType",
                   COALESCE(SUM(l.base_debit),0) AS "Debit", COALESCE(SUM(l.base_credit),0) AS "Credit"
            FROM nexora.journal_entry_line l
            JOIN nexora.journal_entry e ON e.id = l.journal_entry_id
            JOIN nexora.account a ON a.id = l.account_id
            WHERE e.company_id = {Company} AND e.status = 'posted'
              AND e.entry_date BETWEEN {from} AND {to}
              AND (NOT {excludeClosing} OR e.origin <> 'closing')
            GROUP BY a.code, a.name, a.account_type
            ORDER BY a.code
            """).ToListAsync(ct);

    private static StatementSection Section(string title, IEnumerable<BalanceRow> rows, Func<BalanceRow, decimal> amount)
    {
        var lines = rows.Select(r => new StatementLine($"{r.Code} {r.Name}", amount(r)))
            .Where(l => l.Amount != 0m).ToList();
        return new StatementSection(title, lines, lines.Sum(l => l.Amount));
    }

    private async Task<AgedResponse> AgedAsync(DateOnly asOf, bool receivables, CancellationToken ct)
    {
        var func = await currency.FunctionalCurrencyAsync(ct);
        // Noms de tables issus d'un booléen contrôlé (aucune entrée utilisateur).
        var table = receivables ? "customer_receivable" : "supplier_payable";
        var partyTable = receivables ? "customer" : "supplier";
        var partyFk = receivables ? "customer_id" : "supplier_id";

        var sql =
            "SELECT p.name AS \"Party\", " +
            "r.remaining_amount AS \"Remaining\", " +
            "GREATEST(0, ({0}::date - COALESCE(r.due_date, r.issue_date)))::int AS \"DaysOverdue\" " +
            $"FROM nexora.{table} r " +
            $"JOIN nexora.{partyTable} p ON p.id = r.{partyFk} " +
            "WHERE r.company_id = {1} AND r.status IN ('OPEN','PARTIALLY_PAID','OVERDUE') " +
            "AND r.remaining_amount > 0";
        var raw = await db.Database.SqlQueryRaw<AgedRawRow>(sql, asOf, Company).ToListAsync(ct);

        static string Bucket(int d) => d <= 0 ? "Courant" : d <= 30 ? "1-30 j" : d <= 60 ? "31-60 j" : d <= 90 ? "61-90 j" : "> 90 j";
        var labels = new[] { "Courant", "1-30 j", "31-60 j", "61-90 j", "> 90 j" };

        var rows = raw.GroupBy(r => r.Party).Select(g =>
        {
            var buckets = labels.Select(lbl => new AgedBucket(lbl,
                g.Where(x => Bucket(x.DaysOverdue) == lbl).Sum(x => x.Remaining))).ToList();
            return new AgedRow(g.Key, g.Sum(x => x.Remaining), buckets);
        }).OrderByDescending(r => r.Total).ToList();

        return new AgedResponse(asOf, func, rows, rows.Sum(r => r.Total));
    }
}
