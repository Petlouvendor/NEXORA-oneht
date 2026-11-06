using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Nexora.Application.Abstractions;
using Nexora.Application.Administration;
using Nexora.Application.Finance.Treasury;
using Nexora.Contracts.Finance;
using Nexora.Domain.Common;
using Nexora.Infrastructure.Persistence;
using Nexora.Infrastructure.Reporting;

namespace Nexora.Infrastructure.Finance;

// =============================================================================
// Cockpit de trésorerie (Phase 28 §26-28, §22-23, §32, §38). Lectures dérivées
// des comptes financiers, sous-livres et taux de change ; les écritures restent
// produites par le moteur financier centralisé (Phase 4).
// =============================================================================

/// <summary>Position, liquidité et prévision par maturité (§27-28, §22-23).</summary>
public sealed class SqlTreasuryQueries(
    NexoraDbContext db, ITenantContext tenant, ISettingsService settings) : ITreasuryQueries
{
    private Guid Company => tenant.ActiveCompanyId
        ?? throw TenantErrors.ActiveCompanyRequired();

    private static readonly (string Label, int From, int To)[] Maturities =
    [
        ("Aujourd'hui", -100_000, 0),
        ("7 jours", 1, 7),
        ("30 jours", 8, 30),
        ("60 jours", 31, 60),
        ("90 jours", 61, 90),
        ("Au-delà", 91, 100_000),
    ];

    private sealed record AccountRow(
        Guid AccountId, string Name, string AccountType, string Currency,
        decimal SystemBalance, decimal BaseBalance, string Status);
    private sealed record DueRow(DateOnly? DueDate, decimal Amount);

    private async Task<int> HorizonAsync(int? given, CancellationToken ct)
    {
        if (given is > 0) return given.Value;
        try
        {
            var s = await settings.ResolveAsync("treasury.liquidity_horizon_days", null, ct);
            return s.Value.TryGetInt32(out var d) && d > 0 ? d : 30;
        }
        catch { return 30; }
    }

    private async Task<decimal> SettingNumberAsync(string key, decimal fallback, CancellationToken ct)
    {
        try
        {
            var s = await settings.ResolveAsync(key, null, ct);
            return s.Value.TryGetDecimal(out var d) ? d : fallback;
        }
        catch { return fallback; }
    }

    private Task<string> CurrencyAsync(CancellationToken ct) =>
        db.Database.SqlQuery<string>($"""
            SELECT cur.code AS "Value" FROM nexora.company co
            JOIN nexora.currency cur ON cur.id = co.default_currency_id WHERE co.id = {Company}
            """).FirstAsync(ct);

    private Task<List<AccountRow>> LoadAccountsAsync(CancellationToken ct) =>
        db.Database.SqlQuery<AccountRow>($"""
            SELECT fa.id AS "AccountId", fa.name AS "Name", fa.account_type AS "AccountType",
                   cur.code AS "Currency", fa.current_balance AS "SystemBalance", fa.status AS "Status",
                   -- Même règle que le moteur (taux direct ou inverse, A-046) ; sans taux, 0 et non le solde brut :
                   -- un solde en dollars n'est plus compté comme des gourdes.
                   COALESCE(nexora.convert_amount(fa.current_balance, fa.currency_id, co.default_currency_id), 0) AS "BaseBalance"
            FROM nexora.financial_account fa
            JOIN nexora.currency cur ON cur.id = fa.currency_id
            JOIN nexora.company co ON co.id = fa.company_id
            WHERE fa.company_id = {Company}
            ORDER BY fa.account_type, fa.name
            """).ToListAsync(ct);

    private Task<decimal> ReceivablesDueByAsync(DateOnly by, CancellationToken ct) =>
        db.Database.SqlQuery<decimal>($"""
            -- Montants convertis en devise de l'entreprise au taux du jour (audit 2026-10 éd. 2, comme les soldes de comptes).
            SELECT COALESCE(SUM(COALESCE(nexora.convert_amount(r.remaining_amount, r.currency_id, co.default_currency_id), 0)), 0) AS "Value"
            FROM nexora.customer_receivable r JOIN nexora.company co ON co.id = r.company_id
            WHERE r.company_id = {Company} AND r.status IN ('OPEN','PARTIALLY_PAID','OVERDUE','IN_DISPUTE')
              AND r.remaining_amount > 0 AND (r.due_date IS NULL OR r.due_date <= {by})
            """).FirstAsync(ct);

    private Task<decimal> PayablesDueByAsync(DateOnly by, CancellationToken ct) =>
        db.Database.SqlQuery<decimal>($"""
            SELECT COALESCE(SUM(COALESCE(nexora.convert_amount(p.remaining_amount, p.currency_id, co.default_currency_id), 0)), 0) AS "Value"
            FROM nexora.supplier_payable p JOIN nexora.company co ON co.id = p.company_id
            WHERE p.company_id = {Company} AND p.status IN ('OPEN','PARTIALLY_PAID','OVERDUE')
              AND p.remaining_amount > 0 AND (p.due_date IS NULL OR p.due_date <= {by})
            """).FirstAsync(ct);

    private Task<decimal> UnpaidExpensesAsync(CancellationToken ct) =>
        db.Database.SqlQuery<decimal>($"""
            SELECT COALESCE(SUM(COALESCE(nexora.convert_amount(e.amount + e.tax_amount, e.currency_id, co.default_currency_id), 0)), 0) AS "Value"
            FROM nexora.expense e JOIN nexora.company co ON co.id = e.company_id
            WHERE e.company_id = {Company} AND e.status IN ('submitted','approved') AND e.paid_at IS NULL
            """).FirstAsync(ct);

    public async Task<IReadOnlyList<TreasuryAccountLineDto>> AccountsAsync(CancellationToken ct = default)
    {
        var accounts = await LoadAccountsAsync(ct);
        return accounts.Select(a => new TreasuryAccountLineDto(
            a.AccountId, a.Name, a.AccountType, a.Currency,
            Math.Round(a.SystemBalance, 2), Math.Round(a.BaseBalance, 2), a.Status)).ToList();
    }

    public async Task<TreasuryPositionDto> PositionAsync(CancellationToken ct = default)
    {
        var currency = await CurrencyAsync(ct);
        var today = await ReportingClock.TodayAsync(db, ct);
        var horizon = await HorizonAsync(null, ct);
        var accounts = await LoadAccountsAsync(ct);

        decimal ByType(params string[] types) => accounts
            .Where(a => a.Status == "active" && types.Contains(a.AccountType))
            .Sum(a => Math.Round(a.BaseBalance, 2));

        var cash = ByType("cash");
        var bank = ByType("bank");
        var mobile = ByType("mobile_money");
        var other = ByType("card_processor", "wallet", "other");
        var available = cash + bank + mobile + other;

        var pending = await db.Database.SqlQuery<decimal>($"""
            SELECT COALESCE(SUM(COALESCE(nexora.convert_amount(t.amount, t.currency_id, co.default_currency_id), 0)), 0) AS "Value"
            FROM nexora.financial_transaction t JOIN nexora.company co ON co.id = t.company_id
            WHERE t.company_id = {Company} AND t.status = 'pending'
            """).FirstAsync(ct);
        var suspense = await db.Database.SqlQuery<decimal>($"""
            SELECT COALESCE(SUM(COALESCE(nexora.convert_amount(ABS(si.amount), si.currency_id, co.default_currency_id), 0)), 0) AS "Value"
            FROM nexora.treasury_suspense_item si JOIN nexora.company co ON co.id = si.company_id
            WHERE si.company_id = {Company} AND si.status = 'open'
            """).FirstAsync(ct);

        var horizonDate = today.AddDays(horizon);
        var nearRec = await ReceivablesDueByAsync(horizonDate, ct);
        var nearPay = await PayablesDueByAsync(horizonDate, ct);

        var lines = accounts.Select(a => new TreasuryAccountLineDto(
            a.AccountId, a.Name, a.AccountType, a.Currency,
            Math.Round(a.SystemBalance, 2), Math.Round(a.BaseBalance, 2), a.Status)).ToList();

        return new TreasuryPositionDto(
            today, currency, Math.Round(available, 2),
            cash, bank, mobile, other,
            Math.Round(pending, 2), Math.Round(suspense, 2),
            Math.Round(nearRec, 2), Math.Round(nearPay, 2),
            Math.Round(available + nearRec - nearPay, 2), horizon, lines);
    }

    public async Task<LiquidityDto> LiquidityAsync(int? horizonDays, CancellationToken ct = default)
    {
        var currency = await CurrencyAsync(ct);
        var today = await ReportingClock.TodayAsync(db, ct);
        var horizon = await HorizonAsync(horizonDays, ct);
        var horizonDate = today.AddDays(horizon);
        var accounts = await LoadAccountsAsync(ct);

        var immediate = accounts
            .Where(a => a.Status == "active"
                        && new[] { "cash", "bank", "mobile_money", "card_processor", "wallet", "other" }.Contains(a.AccountType))
            .Sum(a => Math.Round(a.BaseBalance, 2));

        var expectedRec = await ReceivablesDueByAsync(horizonDate, ct);
        var nearPay = await PayablesDueByAsync(horizonDate, ct);
        var unpaidExpenses = await UnpaidExpensesAsync(ct);
        var commitments = nearPay + unpaidExpenses;
        var gap = immediate + expectedRec - commitments;

        var minCash = await SettingNumberAsync("treasury.min_cash_alert", 0m, ct);
        var maxAge = (int)await SettingNumberAsync("treasury.suspense_max_age_days", 30m, ct);

        var overduePay = await PayablesDueByAsync(today.AddDays(-1), ct);
        var oldSuspense = await db.Database.SqlQuery<int>($"""
            SELECT COUNT(*)::int AS "Value" FROM nexora.treasury_suspense_item
            WHERE company_id = {Company} AND status = 'open' AND created_at::date < {today.AddDays(-maxAge)}
            """).FirstAsync(ct);

        var alerts = new List<string>();
        if (gap < 0m)
            alerts.Add($"Trésorerie projetée négative à {horizon} j : {Math.Round(gap, 2)} {currency}.");
        if (immediate < minCash)
            alerts.Add($"Trésorerie disponible ({Math.Round(immediate, 2)} {currency}) sous le seuil d'alerte ({minCash} {currency}).");
        if (overduePay > 0m)
            alerts.Add($"{Math.Round(overduePay, 2)} {currency} de dettes fournisseurs échues.");
        if (oldSuspense > 0)
            alerts.Add($"{oldSuspense} élément(s) en suspens de plus de {maxAge} j — anti-fraude §48.");
        foreach (var a in accounts.Where(a => a.Status == "active" && a.BaseBalance < 0m))
            alerts.Add($"Compte « {a.Name} » à découvert ({Math.Round(a.SystemBalance, 2)} {a.Currency}).");
        // Sans taux de change, un compte en devise n'est pas compté : le dire plutôt que de le traiter à 1 pour 1.
        foreach (var a in accounts.Where(a => a.Status == "active" && a.Currency != currency && a.SystemBalance != 0m && a.BaseBalance == 0m))
            alerts.Add($"Aucun taux {a.Currency} → {currency} : le solde de « {a.Name} » ({Math.Round(a.SystemBalance, 2)} {a.Currency}) n'est pas compté.");

        return new LiquidityDto(
            today, currency, horizon,
            Math.Round(immediate, 2), Math.Round(expectedRec, 2),
            Math.Round(commitments, 2), Math.Round(gap, 2), alerts);
    }

    public async Task<CashflowForecastDto> MaturityForecastAsync(CancellationToken ct = default)
    {
        var currency = await CurrencyAsync(ct);
        var today = await ReportingClock.TodayAsync(db, ct);
        var accounts = await LoadAccountsAsync(ct);
        var opening = accounts
            .Where(a => a.Status == "active"
                        && new[] { "cash", "bank", "mobile_money", "card_processor", "wallet", "other" }.Contains(a.AccountType))
            .Sum(a => Math.Round(a.BaseBalance, 2));

        var recs = await db.Database.SqlQuery<DueRow>($"""
            SELECT r.due_date AS "DueDate", COALESCE(nexora.convert_amount(r.remaining_amount, r.currency_id, co.default_currency_id), 0) AS "Amount"
            FROM nexora.customer_receivable r JOIN nexora.company co ON co.id = r.company_id
            WHERE r.company_id = {Company} AND r.status IN ('OPEN','PARTIALLY_PAID','OVERDUE','IN_DISPUTE')
              AND r.remaining_amount > 0
            """).ToListAsync(ct);
        var pays = await db.Database.SqlQuery<DueRow>($"""
            SELECT p.due_date AS "DueDate", COALESCE(nexora.convert_amount(p.remaining_amount, p.currency_id, co.default_currency_id), 0) AS "Amount"
            FROM nexora.supplier_payable p JOIN nexora.company co ON co.id = p.company_id
            WHERE p.company_id = {Company} AND p.status IN ('OPEN','PARTIALLY_PAID','OVERDUE')
              AND p.remaining_amount > 0
            """).ToListAsync(ct);
        var unpaidExpenses = await UnpaidExpensesAsync(ct);

        int Offset(DateOnly? due) => due is { } d ? d.DayNumber - today.DayNumber : 0;

        var running = opening;
        var buckets = new List<MaturityBucketDto>();
        for (var i = 0; i < Maturities.Length; i++)
        {
            var (label, from, to) = Maturities[i];
            var inflow = recs.Where(r => Offset(r.DueDate) >= from && Offset(r.DueDate) <= to).Sum(r => r.Amount);
            var outflow = pays.Where(p => Offset(p.DueDate) >= from && Offset(p.DueDate) <= to).Sum(p => p.Amount);
            if (i == 0) outflow += unpaidExpenses;   // engagements immédiats (§28)
            running += inflow - outflow;
            buckets.Add(new MaturityBucketDto(label, from, to,
                Math.Round(inflow, 2), Math.Round(outflow, 2),
                Math.Round(inflow - outflow, 2), Math.Round(running, 2)));
        }

        return new CashflowForecastDto(today, currency, Math.Round(opening, 2), buckets);
    }
}

/// <summary>File d'attente des sommes non identifiées / non affectées (§31-32).</summary>
public sealed class SqlSuspenseService(NexoraDbContext db, ITenantContext tenant) : ISuspenseService
{
    private Guid Company => tenant.ActiveCompanyId
        ?? throw TenantErrors.ActiveCompanyRequired();

    private static readonly string[] Kinds =
    [
        "unidentified", "payment_without_invoice", "unallocated_fee",
        "bank_discrepancy", "mobile_money_discrepancy", "cash_variance", "other",
    ];

    public async Task<IReadOnlyList<SuspenseItemDto>> ListAsync(string? status, bool onlyOverdue, CancellationToken ct = default)
    {
        var today = await ReportingClock.TodayAsync(db, ct);
        return await db.Database.SqlQuery<SuspenseItemDto>($"""
            SELECT si.id AS "Id", si.financial_account_id AS "FinancialAccountId", fa.name AS "AccountName",
                   si.kind AS "Kind", si.amount AS "Amount", cur.code AS "Currency", si.reference AS "Reference",
                   si.reason AS "Reason", si.status AS "Status", si.resolution_due_date AS "ResolutionDueDate",
                   (si.status = 'open' AND si.resolution_due_date IS NOT NULL AND si.resolution_due_date < {today}) AS "Overdue",
                   u.full_name AS "ResponsibleUserName", si.resolution AS "Resolution",
                   si.created_at AS "CreatedAt", si.resolved_at AS "ResolvedAt"
            FROM nexora.treasury_suspense_item si
            JOIN nexora.currency cur ON cur.id = si.currency_id
            LEFT JOIN nexora.financial_account fa ON fa.id = si.financial_account_id
            LEFT JOIN nexora.app_user u ON u.id = si.responsible_user_id
            WHERE si.company_id = {Company}
              AND ({status}::text IS NULL OR si.status = {status})
              AND (NOT {onlyOverdue} OR (si.status = 'open' AND si.resolution_due_date IS NOT NULL AND si.resolution_due_date < {today}))
            ORDER BY si.created_at DESC
            """).ToListAsync(ct);
    }

    public async Task<Result<Guid>> OpenAsync(OpenSuspenseRequest r, CancellationToken ct = default)
    {
        if (r.Amount == 0m) return Error.Validation("finance.suspense_amount", "Le montant d'un suspens ne peut être nul.");
        if (string.IsNullOrWhiteSpace(r.Reason)) return Error.Validation("finance.suspense_reason", "Un motif est obligatoire (§32).");
        var kind = Kinds.Contains(r.Kind) ? r.Kind : "unidentified";

        var currencyId = await db.Database.SqlQuery<Guid>($"""
            SELECT id AS "Value" FROM nexora.currency WHERE code = {r.Currency}
            """).FirstOrDefaultAsync(ct);
        if (currencyId == Guid.Empty) return Error.Validation("finance.suspense_currency", $"Devise « {r.Currency} » inconnue.");

        if (r.FinancialAccountId is { } fa)
        {
            var ok = await db.Database.SqlQuery<int>($"""
                SELECT COUNT(*)::int AS "Value" FROM nexora.financial_account
                WHERE id = {fa} AND company_id = {Company}
                """).FirstAsync(ct);
            if (ok == 0) return Error.NotFound("finance.financial_account", "Compte financier introuvable.");
        }

        var id = Guid.NewGuid();
        await db.Database.ExecuteSqlAsync($"""
            INSERT INTO nexora.treasury_suspense_item
              (id, company_id, financial_account_id, kind, amount, currency_id, reference, reason,
               responsible_user_id, resolution_due_date, created_by)
            VALUES ({id}, {Company}, {r.FinancialAccountId}, {kind}, {r.Amount}, {currencyId}, {r.Reference},
                    {r.Reason.Trim()}, {r.ResponsibleUserId}, {r.ResolutionDueDate}, {tenant.UserId})
            """, ct);
        return Result.Success(id);
    }

    public async Task<Result> ResolveAsync(Guid id, ResolveSuspenseRequest r, CancellationToken ct = default)
    {
        var outcomes = new[] { "resolved", "written_off", "cancelled" };
        if (!outcomes.Contains(r.Outcome)) return Error.Validation("finance.suspense_outcome", "Issue de suspens inconnue.");
        if (string.IsNullOrWhiteSpace(r.Resolution)) return Error.Validation("finance.suspense_resolution", "Une note de résolution est obligatoire (§32).");

        var current = await db.Database.SqlQuery<string>($"""
            SELECT status AS "Value" FROM nexora.treasury_suspense_item WHERE id = {id} AND company_id = {Company}
            """).FirstOrDefaultAsync(ct);
        if (current is null) return Error.NotFound("finance.suspense_not_found", "Élément de suspens introuvable.");
        if (current != "open") return Error.Conflict("finance.suspense_closed", "Ce suspens est déjà résolu.");

        // Passage en perte ou profit adossé à une écriture validée (audit 2026-10 éd. 2) : le suspens se refermait sans
        // aucune trace au grand livre — la perte n'apparaissait nulle part dans les comptes.
        Guid? entryId = null;
        if (r.Outcome == "written_off")
        {
            var number = r.JournalEntryNumber?.Trim();
            if (string.IsNullOrEmpty(number))
                return Error.Validation("finance.suspense_entry", "Indiquez le numéro de l'écriture qui constate la perte ou le profit.");
            entryId = await db.Database.SqlQuery<Guid?>($"""
                SELECT id AS "Value" FROM nexora.journal_entry
                WHERE company_id = {Company} AND entry_number = {number} AND status = ANY({Nexora.Domain.Accounting.LedgerStatuses.InLedger})
                """).FirstOrDefaultAsync(ct);
            if (entryId is null)
                return Error.Validation("finance.suspense_entry", $"Écriture « {number} » introuvable ou non validée.");
        }

        // Une seule résolution l'emporte (le statut lu plus haut peut avoir changé entre-temps).
        var n = await db.Database.ExecuteSqlAsync($"""
            UPDATE nexora.treasury_suspense_item
               SET status = {r.Outcome}, resolution = {r.Resolution.Trim()}, resolved_at = now(),
                   resolved_by = {tenant.UserId}, journal_entry_id = {entryId}, updated_at = now()
             WHERE id = {id} AND company_id = {Company} AND status = 'open'
            """, ct);
        return n == 1 ? Result.Success() : Error.Conflict("finance.suspense_closed", "Ce suspens est déjà résolu.");
    }
}

/// <summary>Clôture financière d'une période : contrôles §38 puis enregistrement.</summary>
public sealed class SqlFinancialCloseService(NexoraDbContext db, ITenantContext tenant) : IFinancialCloseService
{
    private Guid Company => tenant.ActiveCompanyId
        ?? throw TenantErrors.ActiveCompanyRequired();

    private sealed record PeriodRow(Guid Id, string Name, DateOnly StartDate, DateOnly EndDate, string Status);
    private sealed record CountTotal(int Count, decimal Total);

    public async Task<Result<FinancialCloseReportDto>> PreviewAsync(Guid periodId, CancellationToken ct = default)
    {
        var report = await BuildReportAsync(periodId, ct);
        return report is null
            ? Error.NotFound("finance.period_not_found", "Période comptable introuvable.")
            : Result.Success(report);
    }

    public async Task<Result<FinancialCloseResultDto>> CompleteAsync(
        Guid periodId, CompleteFinancialCloseRequest r, CancellationToken ct = default)
    {
        var report = await BuildReportAsync(periodId, ct);
        if (report is null) return Error.NotFound("finance.period_not_found", "Période comptable introuvable.");
        if (report.Status == "completed")
            return Error.Conflict("finance.already_closed", $"La clôture financière au {report.AsOf} existe déjà.");

        if (!report.CanClose)
        {
            var blockers = string.Join(" ; ", report.Controls.Where(c => c.Blocking && c.Status == "fail").Select(c => c.Label));
            if (!r.Force)
                return Error.Conflict("finance.close_blocked", $"Clôture bloquée par : {blockers}. Lever les contrôles ou forcer avec motif (§38).");
            if (string.IsNullOrWhiteSpace(r.Note))
                return Error.Validation("finance.force_reason", "Un motif est obligatoire pour forcer la clôture.");
        }

        var forced = !report.CanClose && r.Force;
        var id = Guid.NewGuid();
        var controlsJson = JsonSerializer.Serialize(report.Controls);
        await db.Database.ExecuteSqlAsync($"""
            INSERT INTO nexora.financial_close
              (id, company_id, accounting_period_id, as_of_date, status, controls, forced, note, closed_by)
            VALUES ({id}, {Company}, {periodId}, {report.AsOf}, 'completed', {controlsJson}::jsonb, {forced}, {r.Note}, {tenant.UserId})
            """, ct);
        return Result.Success(new FinancialCloseResultDto(id, periodId, report.AsOf, forced));
    }

    private async Task<FinancialCloseReportDto?> BuildReportAsync(Guid periodId, CancellationToken ct)
    {
        var period = await db.Database.SqlQuery<PeriodRow>($"""
            SELECT id AS "Id", name AS "Name", start_date AS "StartDate", end_date AS "EndDate", status AS "Status"
            FROM nexora.accounting_period WHERE id = {periodId} AND company_id = {Company}
            """).FirstOrDefaultAsync(ct);
        if (period is null) return null;

        var today = await ReportingClock.TodayAsync(db, ct);
        var asOf = today < period.EndDate ? today : period.EndDate;

        var openSessions = await db.Database.SqlQuery<int>($"""
            SELECT COUNT(*)::int AS "Value" FROM nexora.cash_session
            WHERE company_id = {Company} AND status IN ('open','closing')
              AND opened_at::date BETWEEN {period.StartDate} AND {asOf}
            """).FirstAsync(ct);

        var bankUnmatched = await db.Database.SqlQuery<int>($"""
            SELECT COUNT(*)::int AS "Value"
            FROM nexora.bank_statement_line l JOIN nexora.bank_statement s ON s.id = l.bank_statement_id
            WHERE s.company_id = {Company} AND l.status = 'unmatched'
              AND s.statement_date BETWEEN {period.StartDate} AND {asOf}
            """).FirstAsync(ct);

        var suspenseOpen = await db.Database.SqlQuery<int>($"""
            SELECT COUNT(*)::int AS "Value" FROM nexora.treasury_suspense_item
            WHERE company_id = {Company} AND status = 'open'
            """).FirstAsync(ct);
        var suspenseOverdue = await db.Database.SqlQuery<int>($"""
            SELECT COUNT(*)::int AS "Value" FROM nexora.treasury_suspense_item
            WHERE company_id = {Company} AND status = 'open'
              AND resolution_due_date IS NOT NULL AND resolution_due_date < {today}
            """).FirstAsync(ct);

        var unposted = await db.Database.SqlQuery<int>($"""
            SELECT COUNT(*)::int AS "Value" FROM nexora.journal_entry
            WHERE company_id = {Company} AND status NOT IN ('posted','reversed')
              AND entry_date BETWEEN {period.StartDate} AND {asOf}
            """).FirstAsync(ct);

        var rec = await db.Database.SqlQuery<CountTotal>($"""
            SELECT COUNT(*)::int AS "Count", COALESCE(SUM(remaining_amount), 0) AS "Total"
            FROM nexora.customer_receivable
            WHERE company_id = {Company} AND status IN ('OPEN','PARTIALLY_PAID','OVERDUE') AND due_date < {today}
            """).FirstAsync(ct);
        var pay = await db.Database.SqlQuery<CountTotal>($"""
            SELECT COUNT(*)::int AS "Count", COALESCE(SUM(remaining_amount), 0) AS "Total"
            FROM nexora.supplier_payable
            WHERE company_id = {Company} AND status IN ('OPEN','PARTIALLY_PAID','OVERDUE') AND due_date < {today}
            """).FirstAsync(ct);

        static string Verdict(bool fail, bool warn) => fail ? "fail" : warn ? "warn" : "pass";

        var controls = new List<FinancialCloseControlDto>
        {
            new("cash_sessions_open", "Sessions de caisse ouvertes sur la période",
                Verdict(openSessions > 0, false), openSessions > 0 ? $"{openSessions} session(s) à clôturer" : "Aucune",
                openSessions, true),
            new("bank_unreconciled", "Lignes de relevé bancaire non rapprochées",
                Verdict(bankUnmatched > 0, false), bankUnmatched > 0 ? $"{bankUnmatched} ligne(s) à rapprocher" : "Relevés rapprochés",
                bankUnmatched, true),
            new("suspense_overdue", "Suspens en retard de résolution",
                Verdict(suspenseOverdue > 0, false), suspenseOverdue > 0 ? $"{suspenseOverdue} suspens en retard" : "Aucun suspens en retard",
                suspenseOverdue, true),
            new("suspense_open", "Suspens ouverts",
                Verdict(false, suspenseOpen > 0), suspenseOpen > 0 ? $"{suspenseOpen} suspens à traiter" : "Aucun",
                suspenseOpen, false),
            new("unposted_entries", "Écritures non validées sur la période",
                Verdict(false, unposted > 0), unposted > 0 ? $"{unposted} écriture(s) en brouillon" : "Toutes validées",
                unposted, false),
            new("receivables_overdue", "Créances clients échues",
                Verdict(false, rec.Count > 0), rec.Count > 0 ? $"{rec.Count} créance(s), {Math.Round(rec.Total, 2)}" : "Aucune",
                rec.Count, false),
            new("payables_overdue", "Dettes fournisseurs échues",
                Verdict(false, pay.Count > 0), pay.Count > 0 ? $"{pay.Count} dette(s), {Math.Round(pay.Total, 2)}" : "Aucune",
                pay.Count, false),
        };

        var alreadyClosed = await db.Database.SqlQuery<int>($"""
            SELECT COUNT(*)::int AS "Value" FROM nexora.financial_close
            WHERE company_id = {Company} AND accounting_period_id = {periodId}
              AND as_of_date = {asOf} AND status = 'completed'
            """).FirstAsync(ct);

        var canClose = controls.Where(c => c.Blocking).All(c => c.Status != "fail");
        return new FinancialCloseReportDto(
            period.Id, period.Name, asOf, alreadyClosed > 0 ? "completed" : "draft",
            canClose, controls, DateTimeOffset.UtcNow);
    }
}
