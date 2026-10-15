using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Nexora.Application.Abstractions;
using Nexora.Application.Finance.Abstractions;
using Nexora.Application.Finance.Engine;
using Nexora.Application.Finance.Reversals;
using Nexora.Application.Payroll;
using Nexora.Application.Printing;
using Nexora.Application.Security;
using Nexora.Contracts.Payroll;
using Nexora.Domain.Common;
using Nexora.Infrastructure.Persistence;
using Npgsql;

namespace Nexora.Infrastructure.Payroll;

/// <summary>
/// Cycle de paie (§3, §9, §22) : BROUILLON → CALCUL → CONTRÔLE → VALIDATION → APPROBATION FINALE → PAIEMENT →
/// BULLETINS → CLÔTURE. Chaque étape est une transaction PostgreSQL (§31), identifie son auteur (§22) et
/// respecte la séparation des tâches (§39). Une paie validée est verrouillée par la base ; une correction
/// ultérieure passe par une régularisation (§36).
/// </summary>
public sealed partial class SqlPayrollService(
    NexoraDbContext db,
    ITenantContext tenant,
    IUnitOfWork uow,
    PayrollAccess access,
    PayrollConfigurationReader config,
    PayrollNotifier notifier,
    IFinancialEngine engine,
    IReversalService reversals,
    IFinancialAccountStore financialAccounts,
    IPrintService print,
    IDateTimeProvider clock,
    ILogger<SqlPayrollService> logger) : IPayrollService
{
    private static readonly CultureInfo Fr = CultureInfo.GetCultureInfo("fr-FR");
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private Guid Company => PayrollRules.Company(tenant);
    private Guid UserId => PayrollRules.User(tenant);

    private static Error RunNotFound() => Error.NotFound("payroll.run_not_found", "Paie introuvable.");

    /// <summary>Erreur de la base (verrou, unicité, contrainte) ou du moteur comptable traduite en refus métier.</summary>
    private static Error Translate(Exception ex, string fallbackCode)
    {
        var pg = ex as PostgresException ?? ex.InnerException as PostgresException ?? ex.InnerException?.InnerException as PostgresException;
        var message = pg?.MessageText ?? ex.Message;
        if (pg?.ConstraintName == "payroll_period_no_overlap" || message.Contains("payroll_period_no_overlap", StringComparison.Ordinal))
            return Error.Conflict("payroll.period_overlap", "Une période de paie de même fréquence chevauche déjà ces dates.");
        if (pg?.ConstraintName == "salary_payment_item_active_uk" || message.Contains("salary_payment_item_active_uk", StringComparison.Ordinal))
            return Error.Conflict("payroll.already_paid", "Cette ligne de paie a déjà un paiement actif (une instruction = un paiement).");
        return Error.Conflict(fallbackCode, pg is null ? ex.Message : Persistence.DatabaseErrors.ClientMessage(pg));
    }

    // ================================================================== Périodes

    private sealed record PeriodRow(
        Guid Id, string Code, string Label, string Kind, string Frequency, DateOnly StartDate, DateOnly EndDate, DateOnly CutoffDate,
        DateOnly PaymentDate, string Status, Guid? RunId, string? RunNumber, string? RunStatus, int EmployeeCount, DateTimeOffset CreatedAt);

    private IQueryable<PeriodRow> PeriodQuery() => db.Database.SqlQuery<PeriodRow>($"""
        SELECT p.id AS "Id", p.code AS "Code", p.label AS "Label", p.kind AS "Kind", p.frequency AS "Frequency", p.start_date AS "StartDate",
               p.end_date AS "EndDate", p.cutoff_date AS "CutoffDate", p.payment_date AS "PaymentDate", p.status AS "Status",
               r.id AS "RunId", r.run_number AS "RunNumber", r.status AS "RunStatus",
               COALESCE((SELECT count(*)::int FROM nexora.payroll_item i WHERE i.run_id = r.id AND i.status NOT IN ('excluded','cancelled')), 0)
                   AS "EmployeeCount",
               p.created_at AS "CreatedAt"
        FROM nexora.payroll_period p
        LEFT JOIN LATERAL (SELECT * FROM nexora.payroll_run x WHERE x.period_id = p.id
                           ORDER BY (x.status <> 'cancelled') DESC, x.created_at DESC LIMIT 1) r ON true
        WHERE p.company_id = {Company}
        """);

    private sealed record TotalsRow(Guid RunId, string Currency, decimal Gross, decimal Deductions, decimal Net, decimal EmployerCost, decimal Paid);

    private async Task<Dictionary<Guid, List<CurrencyTotalsDto>>> TotalsAsync(IReadOnlyCollection<Guid> runIds, CancellationToken ct)
    {
        if (runIds.Count == 0) return [];
        var ids = runIds.ToArray();
        var rows = await db.Database.SqlQuery<TotalsRow>($"""
            SELECT i.run_id AS "RunId", cur.code AS "Currency", sum(i.gross_amount) AS "Gross", sum(i.total_deductions) AS "Deductions",
                   sum(i.net_amount) AS "Net", sum(i.employer_cost) AS "EmployerCost",
                   COALESCE(sum(sp.amount), 0) AS "Paid"
            FROM nexora.payroll_item i
            JOIN nexora.currency cur ON cur.id = i.currency_id
            LEFT JOIN nexora.salary_payment sp ON sp.payroll_item_id = i.id AND sp.status = 'completed'
            WHERE i.run_id = ANY({ids}) AND i.status NOT IN ('excluded', 'cancelled')
            GROUP BY i.run_id, cur.code
            """).ToListAsync(ct);
        return rows.GroupBy(r => r.RunId).ToDictionary(g => g.Key, g => g.Select(r =>
            new CurrencyTotalsDto(r.Currency, r.Gross, r.Deductions, r.Net, r.EmployerCost, r.Paid, r.Net - r.Paid)).ToList());
    }

    private async Task<bool> CanReadPayrollAsync(CancellationToken ct)
        => await access.HasAnyAsync([Permissions.Payroll.ViewSalaries, Permissions.Payroll.Calculate, Permissions.Payroll.Review,
            Permissions.Payroll.Validate, Permissions.Payroll.Approve, Permissions.Payroll.Pay, Permissions.Payroll.Close,
            Permissions.Payroll.Reports, Permissions.Payroll.Audit], ct);

    public async Task<IReadOnlyList<PayrollPeriodDto>> PeriodsAsync(int? year, string? status, CancellationToken ct = default)
    {
        if (!await CanReadPayrollAsync(ct)) return [];
        var q = PeriodQuery();
        if (year is { } y) q = q.Where(p => p.StartDate.Year == y);
        if (!string.IsNullOrWhiteSpace(status)) q = q.Where(p => p.Status == status);
        var rows = await q.OrderByDescending(p => p.StartDate).ThenByDescending(p => p.CreatedAt).Take(200).ToListAsync(ct);
        var totals = await TotalsAsync(rows.Where(r => r.RunId is not null).Select(r => r.RunId!.Value).ToList(), ct);
        return rows.Select(r => ToDto(r, r.RunId is { } run && totals.TryGetValue(run, out var t) ? t : [])).ToList();
    }

    private static PayrollPeriodDto ToDto(PeriodRow r, IReadOnlyList<CurrencyTotalsDto> totals) => new(r.Id, r.Code, r.Label, r.Kind, r.Frequency,
        r.StartDate, r.EndDate, r.CutoffDate, r.PaymentDate, r.Status, r.RunId, r.RunNumber, r.RunStatus, r.EmployeeCount, totals, r.CreatedAt);

    public async Task<Result<PayrollPeriodDto>> PeriodAsync(Guid periodId, CancellationToken ct = default)
    {
        if (!await CanReadPayrollAsync(ct)) return Error.NotFound("payroll.period_not_found", "Période introuvable.");
        var row = await PeriodQuery().Where(p => p.Id == periodId).FirstOrDefaultAsync(ct);
        if (row is null) return Error.NotFound("payroll.period_not_found", "Période introuvable.");
        var totals = row.RunId is { } run ? (await TotalsAsync([run], ct)).GetValueOrDefault(run) ?? [] : [];
        return Result.Success(ToDto(row, totals));
    }

    public async Task<Result<PayrollPeriodDto>> CreatePeriodAsync(CreatePeriodRequest r, CancellationToken ct = default)
    {
        var kind = r.Kind is "off_cycle" ? "off_cycle" : "regular";
        if (!PayrollRules.Frequencies.Contains(r.Frequency)) return Error.Validation("payroll.frequency", $"Fréquence « {r.Frequency} » inconnue.");
        if (r.EndDate < r.StartDate) return Error.Validation("payroll.period_dates", "La fin de période précède son début.");
        if (r.EndDate.DayNumber - r.StartDate.DayNumber > 31) return Error.Validation("payroll.period_dates", "Une période couvre 31 jours au plus.");
        var cutoff = r.CutoffDate ?? r.EndDate;
        var payment = r.PaymentDate ?? r.EndDate;
        if (cutoff < r.StartDate || payment < r.StartDate)
            return Error.Validation("payroll.period_dates", "Date de clôture et date de paiement ne peuvent précéder le début de période.");
        if (kind == "regular")
        {
            var expected = r.Frequency switch { "weekly" => 7, "biweekly" => 14, _ => 0 };
            var days = r.EndDate.DayNumber - r.StartDate.DayNumber + 1;
            if (expected > 0 && days != expected)
                return Error.Validation("payroll.period_dates", $"Une période {PayrollCalculator.FrequencyLabel(r.Frequency)} couvre {expected} jours.");
        }

        var label = !string.IsNullOrWhiteSpace(r.Label) ? r.Label.Trim()
            : kind == "off_cycle" ? $"Paie hors cycle du {r.StartDate:dd/MM/yyyy} au {r.EndDate:dd/MM/yyyy}"
            : r.Frequency == "monthly" && r.StartDate.Day == 1 && r.EndDate == r.StartDate.AddMonths(1).AddDays(-1)
                ? "Paie " + r.StartDate.ToString("MMMM yyyy", Fr)
                : $"Paie du {r.StartDate:dd/MM/yyyy} au {r.EndDate:dd/MM/yyyy}";
        var prefix = kind == "off_cycle" ? "H" : r.Frequency switch { "weekly" => "S", "biweekly" => "Q", _ => "M" };
        var baseCode = $"{prefix}{r.StartDate:yyyyMMdd}";
        var scope = (r.EmployeeIds ?? []).Distinct().ToArray();
        var periodId = Guid.NewGuid();
        try
        {
            await uow.ExecuteInTransactionAsync(async token =>
            {
                var taken = await db.Database.SqlQuery<string>($"""
                    SELECT code AS "Value" FROM nexora.payroll_period WHERE company_id = {Company} AND code LIKE {baseCode + "%"}
                    """).ToListAsync(token);
                var code = baseCode;
                for (var n = 2; taken.Contains(code); n++) code = $"{baseCode}-{n}";
                var runNumber = await PayrollRules.NextNumberAsync(db, Company, "payroll_run", "PAY-", r.StartDate.Year, token);
                await db.Database.ExecuteSqlAsync($"""
                    INSERT INTO nexora.payroll_period
                        (id, company_id, code, label, kind, frequency, start_date, end_date, cutoff_date, payment_date, employee_scope, status, created_by)
                    VALUES ({periodId}, {Company}, {code}, {label}, {kind}, {r.Frequency}, {r.StartDate}, {r.EndDate}, {cutoff}, {payment},
                            {scope}, 'draft', {UserId})
                    """, token);
                await db.Database.ExecuteSqlAsync($"""
                    INSERT INTO nexora.payroll_run (company_id, period_id, run_number, status, created_by)
                    VALUES ({Company}, {periodId}, {runNumber}, 'draft', {UserId})
                    """, token);
                return 0;
            }, ct);
        }
        catch (Exception ex) when (ex is InvalidOperationException or PostgresException or DbUpdateException)
        {
            return Translate(ex, "payroll.period_invalid");
        }
        logger.LogInformation("Période de paie {Period} créée ({Start} → {End})", periodId, r.StartDate, r.EndDate);
        return await PeriodAsync(periodId, ct);
    }

    // ================================================================== Paie (exécution) et lignes

    private sealed record RunRow(
        Guid Id, string RunNumber, Guid PeriodId, string PeriodCode, string PeriodLabel, string PeriodKind, string Frequency,
        DateOnly StartDate, DateOnly EndDate, DateOnly CutoffDate, DateOnly PaymentDate, string Status, bool Stale, int CalculationCount,
        Guid? CalculatedBy, string? CalculatedByName, DateTimeOffset? CalculatedAt, Guid? ReviewedBy, string? ReviewedByName,
        DateTimeOffset? ReviewedAt, string? ReviewNote, bool WarningsAcknowledged, Guid? ValidatedBy, string? ValidatedByName,
        DateTimeOffset? ValidatedAt, string? ValidationNote, string? ApprovedByName, DateTimeOffset? ApprovedAt, string? ApprovalNote,
        Guid? JournalEntryId, string? JournalEntryNumber, DateTimeOffset? PaidAt, string? ClosedByName, DateTimeOffset? ClosedAt,
        string? ReturnedNote, string? CancelReason);

    private IQueryable<RunRow> RunQuery() => db.Database.SqlQuery<RunRow>($"""
        SELECT r.id AS "Id", r.run_number AS "RunNumber", p.id AS "PeriodId", p.code AS "PeriodCode", p.label AS "PeriodLabel",
               p.kind AS "PeriodKind", p.frequency AS "Frequency", p.start_date AS "StartDate", p.end_date AS "EndDate",
               p.cutoff_date AS "CutoffDate", p.payment_date AS "PaymentDate", r.status AS "Status", r.stale AS "Stale",
               r.calculation_count AS "CalculationCount", r.calculated_by AS "CalculatedBy", uc.full_name AS "CalculatedByName",
               r.calculated_at AS "CalculatedAt", r.reviewed_by AS "ReviewedBy", ur.full_name AS "ReviewedByName", r.reviewed_at AS "ReviewedAt",
               r.review_note AS "ReviewNote", r.warnings_acknowledged AS "WarningsAcknowledged", r.validated_by AS "ValidatedBy",
               uv.full_name AS "ValidatedByName", r.validated_at AS "ValidatedAt", r.validation_note AS "ValidationNote",
               ua.full_name AS "ApprovedByName", r.approved_at AS "ApprovedAt", r.approval_note AS "ApprovalNote",
               r.journal_entry_id AS "JournalEntryId", je.entry_number AS "JournalEntryNumber", r.paid_at AS "PaidAt",
               ucl.full_name AS "ClosedByName", r.closed_at AS "ClosedAt", r.returned_note AS "ReturnedNote", r.cancel_reason AS "CancelReason"
        FROM nexora.payroll_run r
        JOIN nexora.payroll_period p ON p.id = r.period_id
        LEFT JOIN nexora.app_user uc ON uc.id = r.calculated_by
        LEFT JOIN nexora.app_user ur ON ur.id = r.reviewed_by
        LEFT JOIN nexora.app_user uv ON uv.id = r.validated_by
        LEFT JOIN nexora.app_user ua ON ua.id = r.approved_by
        LEFT JOIN nexora.app_user ucl ON ucl.id = r.closed_by
        LEFT JOIN nexora.journal_entry je ON je.id = r.journal_entry_id
        WHERE r.company_id = {Company}
        """);

    private sealed record ItemRow(
        Guid Id, Guid RunId, Guid EmployeeId, string EmployeeNumber, string EmployeeName, string? DepartmentName, string Currency,
        decimal Gross, decimal TotalDeductions, decimal Net, decimal EmployerCost, decimal AdvanceRecovery, decimal LoanRecovery,
        string Status, string? ExcludedReason, int BlockingCount, int WarningCount, string Anomalies, Guid? PaymentId, string? PaymentNumber,
        Guid? PayslipId, string? PayslipNumber, string PaymentMethod);

    private IQueryable<ItemRow> ItemQuery() => db.Database.SqlQuery<ItemRow>($"""
        SELECT i.id AS "Id", i.run_id AS "RunId", i.employee_id AS "EmployeeId", i.employee_number AS "EmployeeNumber",
               i.employee_name AS "EmployeeName", i.department_name AS "DepartmentName", cur.code AS "Currency", i.gross_amount AS "Gross",
               i.total_deductions AS "TotalDeductions", i.net_amount AS "Net", i.employer_cost AS "EmployerCost",
               i.advance_recovery AS "AdvanceRecovery", i.loan_recovery AS "LoanRecovery", i.status AS "Status",
               i.excluded_reason AS "ExcludedReason", i.blocking_count AS "BlockingCount", i.warning_count AS "WarningCount",
               i.anomalies::text AS "Anomalies", sp.id AS "PaymentId", sp.payment_number AS "PaymentNumber",
               ps.id AS "PayslipId", ps.payslip_number AS "PayslipNumber", COALESCE(i.payment_method, 'bank_transfer') AS "PaymentMethod"
        FROM nexora.payroll_item i
        JOIN nexora.currency cur ON cur.id = i.currency_id
        LEFT JOIN nexora.salary_payment sp ON sp.payroll_item_id = i.id AND sp.status = 'completed'
        LEFT JOIN nexora.payslip ps ON ps.payroll_item_id = i.id AND ps.status = 'issued'
        WHERE i.company_id = {Company}
        """);

    private static IReadOnlyList<AnomalyDto> ParseAnomalies(string json)
        => JsonSerializer.Deserialize<List<AnomalyDto>>(json, Json) ?? [];

    private static PayrollItemSummaryDto ToDto(ItemRow r) => new(r.Id, r.EmployeeId, r.EmployeeNumber, r.EmployeeName, r.DepartmentName,
        r.Currency, r.Gross, r.TotalDeductions, r.Net, r.EmployerCost, r.AdvanceRecovery, r.LoanRecovery, r.Status, r.ExcludedReason,
        r.BlockingCount, r.WarningCount, ParseAnomalies(r.Anomalies), r.PaymentId, r.PaymentNumber, r.PayslipId, r.PayslipNumber, r.PaymentMethod);

    /// <summary>Actions possibles sur la paie selon son statut ET les droits de l'appelant (affichage des boutons).</summary>
    private async Task<IReadOnlyList<string>> AllowedActionsAsync(RunRow r, CancellationToken ct)
    {
        var actions = new List<string>();
        async Task Add(string action, string permission, bool when)
        {
            if (when && await access.HasAsync(permission, ct)) actions.Add(action);
        }
        await Add("calculate", Permissions.Payroll.Calculate, r.Status is "draft" or "calculated" or "reviewed");
        await Add("exclude", Permissions.Payroll.Calculate, r.Status is "calculated" or "reviewed");
        await Add("review", Permissions.Payroll.Review, r.Status == "calculated");
        await Add("validate", Permissions.Payroll.Validate, r.Status == "reviewed");
        await Add("approve", Permissions.Payroll.Approve, r.Status == "validated");
        await Add("return", Permissions.Payroll.Review, r.Status is "reviewed" or "validated");
        await Add("pay", Permissions.Payroll.Pay, r.Status is "approved");
        await Add("close", Permissions.Payroll.Close, r.Status == "paid");
        await Add("cancel", Permissions.Payroll.Calculate, r.Status is "draft" or "calculated" or "reviewed" or "validated");
        await Add("adjust", Permissions.Payroll.Adjust, r.Status is "approved" or "paid" or "closed");
        return actions;
    }

    public async Task<Result<PayrollRunDto>> RunAsync(Guid runId, CancellationToken ct = default)
    {
        if (!await CanReadPayrollAsync(ct)) return RunNotFound();
        var r = await RunQuery().Where(x => x.Id == runId).FirstOrDefaultAsync(ct);
        if (r is null) return RunNotFound();
        var items = await ItemQuery().Where(i => i.RunId == runId).OrderBy(i => i.EmployeeName).ToListAsync(ct);
        var active = items.Where(i => i.Status is not ("excluded" or "cancelled")).ToList();
        var totals = (await TotalsAsync([runId], ct)).GetValueOrDefault(runId) ?? [];
        return Result.Success(new PayrollRunDto(r.Id, r.RunNumber, r.PeriodId, r.PeriodCode, r.PeriodLabel, r.PeriodKind, r.Frequency, r.StartDate,
            r.EndDate, r.CutoffDate, r.PaymentDate, r.Status, r.Stale, r.CalculationCount, r.CalculatedByName, r.CalculatedAt,
            r.ReviewedByName, r.ReviewedAt, r.ReviewNote, r.WarningsAcknowledged, r.ValidatedByName, r.ValidatedAt, r.ValidationNote,
            r.ApprovedByName, r.ApprovedAt, r.ApprovalNote, r.JournalEntryId, r.JournalEntryNumber, r.PaidAt, r.ClosedByName, r.ClosedAt,
            r.ReturnedNote, r.CancelReason, active.Count, items.Count(i => i.Status == "excluded"), active.Sum(i => i.BlockingCount),
            active.Sum(i => i.WarningCount), active.Count(i => i.Status == "paid"), totals, items.Select(ToDto).ToList(),
            await AllowedActionsAsync(r, ct)));
    }

    private sealed record ItemDetailRow(
        Guid Id, Guid RunId, string RunNumber, string RunStatus, string PeriodLabel, DateOnly PeriodStart, DateOnly PeriodEnd, Guid EmployeeId,
        string? JobTitle, string? ContractType, string? PayBasis, string? PayFrequency, decimal BaseSalary, int PeriodDays, decimal DaysEmployed,
        decimal AbsenceDays, decimal Prorata, decimal DailyRate, decimal HourlyRate, decimal BaseAmount, decimal Reimbursements,
        decimal TaxableBase, decimal SocialBase, decimal LegalDeductions, decimal AbsenceDeduction, decimal BenefitOffset,
        decimal OtherDeductions, decimal EmployerContributions, string Trace);

    private sealed record LineRow(
        Guid Id, string Code, string Category, string Label, decimal? Quantity, decimal? Rate, decimal? BaseAmount, decimal? PlannedAmount,
        decimal Amount, string? BorneBy, string SourceType, Guid? SourceId, Guid? RuleId, int? RuleVersion, bool? Taxable, bool? InGross,
        int SortOrder);

    /// <summary>Ligne de paie lisible par : droits paie, ou l'employé concerné une fois la paie approuvée.</summary>
    private async Task<bool> CanReadItemAsync(Guid employeeId, string runStatus, CancellationToken ct)
        => await CanReadPayrollAsync(ct)
           || (runStatus is "approved" or "paid" or "closed" && await access.HasAsync(Permissions.Payroll.SelfService, ct)
               && await access.OwnEmployeeIdAsync(ct) == employeeId);

    public async Task<Result<PayrollItemDetailDto>> ItemAsync(Guid itemId, CancellationToken ct = default)
    {
        var d = await db.Database.SqlQuery<ItemDetailRow>($"""
            SELECT i.id AS "Id", r.id AS "RunId", r.run_number AS "RunNumber", r.status AS "RunStatus", p.label AS "PeriodLabel",
                   p.start_date AS "PeriodStart", p.end_date AS "PeriodEnd", i.employee_id AS "EmployeeId", i.job_title AS "JobTitle",
                   i.contract_type AS "ContractType", i.pay_basis AS "PayBasis", i.pay_frequency AS "PayFrequency",
                   i.base_salary AS "BaseSalary", i.period_days AS "PeriodDays", i.days_employed AS "DaysEmployed",
                   i.absence_days AS "AbsenceDays", i.prorata AS "Prorata", i.daily_rate AS "DailyRate", i.hourly_rate AS "HourlyRate",
                   i.base_amount AS "BaseAmount", i.reimbursement_amount AS "Reimbursements", i.taxable_base AS "TaxableBase",
                   i.social_base AS "SocialBase", i.legal_deductions AS "LegalDeductions", i.absence_deduction AS "AbsenceDeduction",
                   i.benefit_offset AS "BenefitOffset", i.other_deductions AS "OtherDeductions",
                   i.employer_contributions AS "EmployerContributions", i.trace::text AS "Trace"
            FROM nexora.payroll_item i
            JOIN nexora.payroll_run r ON r.id = i.run_id
            JOIN nexora.payroll_period p ON p.id = i.period_id
            WHERE i.id = {itemId} AND i.company_id = {Company}
            """).FirstOrDefaultAsync(ct);
        if (d is null || !await CanReadItemAsync(d.EmployeeId, d.RunStatus, ct))
            return Error.NotFound("payroll.item_not_found", "Ligne de paie introuvable.");
        var summary = ToDto(await ItemQuery().Where(i => i.Id == itemId).FirstAsync(ct));
        var earnings = await db.Database.SqlQuery<LineRow>($"""
            SELECT id AS "Id", code AS "Code", category AS "Category", label AS "Label", quantity AS "Quantity", rate AS "Rate",
                   base_amount AS "BaseAmount", NULL::numeric AS "PlannedAmount", amount AS "Amount", NULL::text AS "BorneBy",
                   source_type AS "SourceType", source_id AS "SourceId", NULL::uuid AS "RuleId", NULL::int AS "RuleVersion",
                   taxable AS "Taxable", in_gross AS "InGross", sort_order AS "SortOrder"
            FROM nexora.payroll_earning WHERE payroll_item_id = {itemId} ORDER BY sort_order
            """).ToListAsync(ct);
        var deductions = await db.Database.SqlQuery<LineRow>($"""
            SELECT id AS "Id", code AS "Code", category AS "Category", label AS "Label", NULL::numeric AS "Quantity", rate AS "Rate",
                   base_amount AS "BaseAmount", planned_amount AS "PlannedAmount", amount AS "Amount", borne_by AS "BorneBy",
                   source_type AS "SourceType", source_id AS "SourceId", rule_id AS "RuleId", rule_version AS "RuleVersion",
                   NULL::boolean AS "Taxable", NULL::boolean AS "InGross", sort_order AS "SortOrder"
            FROM nexora.payroll_deduction WHERE payroll_item_id = {itemId} ORDER BY sort_order
            """).ToListAsync(ct);
        var payments = (await PaymentQuery().Where(p => p.PayrollItemId == itemId).OrderByDescending(p => p.PaidAt).ToListAsync(ct))
            .Select(ToDto).ToList();
        static PayrollLineDto Line(LineRow l) => new(l.Id, l.Code, l.Category, l.Label, l.Quantity, l.Rate, l.BaseAmount, l.PlannedAmount,
            l.Amount, l.BorneBy, l.SourceType, l.SourceId, l.RuleId, l.RuleVersion, l.Taxable, l.InGross);
        var trace = JsonSerializer.Deserialize<List<TraceStepDto>>(d.Trace, Json) ?? [];
        return Result.Success(new PayrollItemDetailDto(summary, d.RunId, d.RunNumber, d.RunStatus, d.PeriodLabel, d.PeriodStart, d.PeriodEnd,
            d.JobTitle, d.ContractType, d.PayBasis, d.PayFrequency, d.BaseSalary, d.PeriodDays, d.DaysEmployed, d.AbsenceDays, d.Prorata,
            d.DailyRate, d.HourlyRate, d.BaseAmount, d.Reimbursements, d.TaxableBase, d.SocialBase, d.LegalDeductions, d.AbsenceDeduction,
            d.BenefitOffset, d.OtherDeductions, d.EmployerContributions, earnings.Select(Line).ToList(), deductions.Select(Line).ToList(),
            trace, payments));
    }

    // ================================================================== Comptes de paiement

    private sealed record AccountOptionRow(Guid Id, string Name, string AccountType, string Currency);

    public async Task<IReadOnlyList<PaymentAccountOptionDto>> PaymentAccountsAsync(CancellationToken ct = default)
        => (await db.Database.SqlQuery<AccountOptionRow>($"""
            SELECT fa.id AS "Id", fa.name AS "Name", fa.account_type AS "AccountType", cur.code AS "Currency"
            FROM nexora.financial_account fa JOIN nexora.currency cur ON cur.id = fa.currency_id
            WHERE fa.company_id = {Company} AND fa.status = 'active'
            ORDER BY fa.account_type, fa.name
            """).ToListAsync(ct)).Select(r => new PaymentAccountOptionDto(r.Id, r.Name, r.AccountType, r.Currency)).ToList();
}
