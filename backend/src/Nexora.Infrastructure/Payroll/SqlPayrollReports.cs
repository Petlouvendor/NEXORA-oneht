using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Nexora.Application.Abstractions;
using Nexora.Application.Payroll;
using Nexora.Application.Printing;
using Nexora.Application.Security;
using Nexora.Contracts.Payroll;
using Nexora.Contracts.Printing;
using Nexora.Domain.Common;
using Nexora.Infrastructure.Persistence;

namespace Nexora.Infrastructure.Payroll;

/// <summary>
/// Tableau de bord RH/paie (§28), rapports exportables (§27), historique financier de l'employé (§26) et
/// intelligence de paie (§33). Tous les chiffres sont calculés ici, en SQL, depuis les lignes de paie, avances
/// et paiements : l'assistant et les alertes ne font que les lire et les expliquer, jamais les inventer.
/// </summary>
public sealed class SqlPayrollReports(
    NexoraDbContext db,
    ITenantContext tenant,
    PayrollAccess access,
    PayrollConfigurationReader config,
    PayrollNotifier notifier,
    IPrintService print,
    ILogger<SqlPayrollReports> logger) : IPayrollReports, IPayrollAlerts
{
    private static readonly CultureInfo Fr = CultureInfo.GetCultureInfo("fr-FR");
    private Guid Company => PayrollRules.Company(tenant);

    private sealed record CurrencyAmountRow(string Currency, decimal Amount);
    private sealed record CurrencyCountRow(string Currency, decimal Amount, int Count);

    // ================================================================== Tableau de bord (§28)

    private sealed record LastPeriodRow(Guid RunId, string Label);
    private sealed record TotalsRow(string Currency, decimal Gross, decimal Deductions, decimal Net, decimal EmployerCost, decimal Paid);

    public async Task<PayrollDashboardDto> DashboardAsync(CancellationToken ct = default)
    {
        var active = await db.Database.SqlQuery<int>($"""
            SELECT count(*)::int AS "Value" FROM nexora.employee WHERE company_id = {Company} AND status IN ('active','on_leave')
            """).FirstAsync(ct);
        var last = await db.Database.SqlQuery<LastPeriodRow>($"""
            SELECT r.id AS "RunId", p.label AS "Label"
            FROM nexora.payroll_run r JOIN nexora.payroll_period p ON p.id = r.period_id
            WHERE r.company_id = {Company} AND r.status NOT IN ('draft','cancelled') AND p.kind = 'regular'
            ORDER BY p.end_date DESC, r.created_at DESC LIMIT 1
            """).FirstOrDefaultAsync(ct);
        var lastTotals = last is null ? [] : await db.Database.SqlQuery<TotalsRow>($"""
            SELECT cur.code AS "Currency", sum(i.gross_amount) AS "Gross", sum(i.total_deductions) AS "Deductions", sum(i.net_amount) AS "Net",
                   sum(i.employer_cost) AS "EmployerCost",
                   COALESCE(sum((SELECT sum(sp.amount) FROM nexora.salary_payment sp WHERE sp.payroll_item_id = i.id AND sp.status = 'completed')), 0) AS "Paid"
            FROM nexora.payroll_item i JOIN nexora.currency cur ON cur.id = i.currency_id
            WHERE i.run_id = {last.RunId} AND i.status NOT IN ('excluded','cancelled')
            GROUP BY cur.code
            """).ToListAsync(ct);
        var toPay = await db.Database.SqlQuery<CurrencyAmountRow>($"""
            SELECT cur.code AS "Currency", sum(i.net_amount) AS "Amount"
            FROM nexora.payroll_item i JOIN nexora.currency cur ON cur.id = i.currency_id
            WHERE i.company_id = {Company} AND i.status = 'approved' AND i.net_amount > 0
            GROUP BY cur.code
            """).ToListAsync(ct);
        var advances = await db.Database.SqlQuery<CurrencyCountRow>($"""
            SELECT cur.code AS "Currency", sum(a.balance) AS "Amount", count(*)::int AS "Count"
            FROM nexora.employee_advance a JOIN nexora.currency cur ON cur.id = a.currency_id
            WHERE a.company_id = {Company} AND a.status IN ('paid','in_repayment') AND a.kind <> 'loan'
            GROUP BY cur.code
            """).ToListAsync(ct);
        var loans = await db.Database.SqlQuery<CurrencyAmountRow>($"""
            SELECT cur.code AS "Currency", sum(a.balance) AS "Amount"
            FROM nexora.employee_advance a JOIN nexora.currency cur ON cur.id = a.currency_id
            WHERE a.company_id = {Company} AND a.status IN ('paid','in_repayment') AND a.kind = 'loan'
            GROUP BY cur.code
            """).ToListAsync(ct);
        var anomalies = await db.Database.SqlQuery<int>($"""
            SELECT COALESCE(sum(i.blocking_count + i.warning_count), 0)::int AS "Value"
            FROM nexora.payroll_item i JOIN nexora.payroll_run r ON r.id = i.run_id
            WHERE i.company_id = {Company} AND r.status IN ('calculated','reviewed','validated') AND i.status = 'calculated'
            """).FirstAsync(ct);
        var inValidation = await db.Database.SqlQuery<int>($"""
            SELECT count(*)::int AS "Value" FROM nexora.payroll_run WHERE company_id = {Company} AND status IN ('calculated','reviewed','validated')
            """).FirstAsync(ct);
        var payments = await db.Database.SqlQuery<CurrencyCountRow>($"""
            SELECT cur.code AS "Currency", sum(sp.amount) AS "Amount", count(*)::int AS "Count"
            FROM nexora.salary_payment sp JOIN nexora.currency cur ON cur.id = sp.currency_id
            WHERE sp.company_id = {Company} AND sp.status = 'completed' AND date_trunc('month', sp.paid_at) = date_trunc('month', now())
            GROUP BY cur.code
            """).ToListAsync(ct);
        var pending = await db.Database.SqlQuery<int>($"""
            SELECT ((SELECT count(*) FROM nexora.leave_record WHERE company_id = {Company} AND status = 'requested')
                  + (SELECT count(*) FROM nexora.overtime_record WHERE company_id = {Company} AND status = 'requested')
                  + (SELECT count(*) FROM nexora.payroll_variable WHERE company_id = {Company} AND status = 'submitted')
                  + (SELECT count(*) FROM nexora.employee_advance WHERE company_id = {Company} AND status = 'requested')
                  + (SELECT count(*) FROM nexora.employee_expense_claim WHERE company_id = {Company} AND status = 'submitted')
                  + (SELECT count(*) FROM nexora.payroll_adjustment WHERE company_id = {Company} AND status = 'requested'))::int AS "Value"
            """).FirstAsync(ct);
        return new PayrollDashboardDto(active, last?.Label,
            lastTotals.Select(t => new CurrencyTotalsDto(t.Currency, t.Gross, t.Deductions, t.Net, t.EmployerCost, t.Paid, t.Net - t.Paid)).ToList(),
            toPay.Select(x => new CurrencyAmountDto(x.Currency, x.Amount)).ToList(), advances.Sum(a => a.Count),
            advances.Select(x => new CurrencyAmountDto(x.Currency, x.Amount)).ToList(),
            loans.Select(x => new CurrencyAmountDto(x.Currency, x.Amount)).ToList(),
            lastTotals.Select(t => new CurrencyAmountDto(t.Currency, t.Deductions)).ToList(), anomalies, inValidation,
            payments.Select(x => new CurrencyAmountDto(x.Currency, x.Amount)).ToList(), payments.Sum(p => p.Count), pending,
            await InsightsAsync(ct));
    }

    // ================================================================== Rapports (§27)

    private static readonly IReadOnlyList<PayrollReportInfoDto> Reports =
    [
        new("payroll_mass", "Masse salariale par période", "Brut, retenues, net, charges et coût employeur de chaque paie.", false),
        new("net_by_period", "Net à payer par période", "Net approuvé, payé et restant à payer par période.", false),
        new("gross_by_department", "Brut par département", "Répartition de la paie d'une période par département.", true),
        new("deductions_by_category", "Retenues par catégorie", "Impôts, cotisations, absences, avances, prêts, autres retenues.", false),
        new("advances_open", "Avances en cours", "Avances, acomptes et prêts versés non soldés, avec prochaine échéance.", false),
        new("advances_settled", "Avances soldées", "Avances entièrement récupérées sur la période.", false),
        new("advances_by_employee", "Avances par employé", "Nombre, montants versés, récupérés et soldes par employé.", false),
        new("staff_debt", "Dette du personnel envers l'entreprise", "Soldes d'avances et de prêts restant dus, par employé.", false),
        new("unpaid", "Paies impayées", "Salaires approuvés non encore payés.", false),
        new("period_variance", "Écarts entre périodes", "Net de chaque employé comparé à sa paie précédente.", true),
        new("employer_cost", "Coût employeur", "Brut + charges patronales selon les règles validées.", false),
    ];

    public IReadOnlyList<PayrollReportInfoDto> Catalog() => Reports;

    private static PayrollReportColumnDto Text(string key, string label) => new(key, label, PrintKinds.Text);
    private static PayrollReportColumnDto Money(string key, string label) => new(key, label, PrintKinds.Money);
    private static PayrollReportColumnDto Int(string key, string label) => new(key, label, PrintKinds.Integer);
    private static PayrollReportColumnDto Date(string key, string label) => new(key, label, PrintKinds.Date);
    private static PayrollReportColumnDto Pct(string key, string label) => new(key, label, PrintKinds.Percent);

    private sealed record MassRow(string Code, string Label, DateOnly EndDate, string Currency, int Employees, decimal Gross, decimal Deductions,
        decimal Net, decimal Employer, decimal Cost, string Status, decimal Paid);
    private sealed record DeptRow(string Department, string Currency, int Employees, decimal Gross, decimal Net, decimal Cost);
    private sealed record DeductionRow(string Category, string BorneBy, string Currency, decimal Amount);
    private sealed record AdvanceReportRow(string Number, string Employee, string Kind, string Currency, decimal Paid, decimal Recovered,
        decimal Balance, DateOnly? NextDue, string Status, DateTimeOffset? PaidAt, DateTimeOffset? SettledAt);
    private sealed record AdvanceByEmployeeRow(string Employee, string Currency, int Count, decimal Paid, decimal Recovered, decimal Balance);
    private sealed record DebtRow(string EmployeeNumber, string Employee, string Currency, decimal Advances, decimal Loans);
    private sealed record UnpaidRow(string RunNumber, string Period, string Employee, string Currency, decimal Net, DateTimeOffset? ApprovedAt);
    private sealed record VarianceRow(string EmployeeNumber, string Employee, string Currency, decimal? Previous, decimal Current);

    public async Task<Result<PayrollReportDto>> ReportAsync(string key, PayrollReportQuery q, CancellationToken ct = default)
    {
        var info = Reports.FirstOrDefault(r => r.Key == key);
        if (info is null) return Error.NotFound("payroll.report_not_found", $"Rapport « {key} » inconnu.");
        var today = await PayrollRules.TodayAsync(db, ct);
        var from = q.From ?? today.AddMonths(-12);
        var to = q.To ?? today;
        var subtitle = $"Du {from:dd/MM/yyyy} au {to:dd/MM/yyyy}";

        switch (key)
        {
            case "payroll_mass":
            case "net_by_period":
            case "employer_cost":
            {
                var rows = await db.Database.SqlQuery<MassRow>($"""
                    SELECT p.code AS "Code", p.label AS "Label", p.end_date AS "EndDate", cur.code AS "Currency", count(*)::int AS "Employees",
                           sum(i.gross_amount) AS "Gross", sum(i.total_deductions) AS "Deductions", sum(i.net_amount) AS "Net",
                           sum(i.employer_contributions) AS "Employer", sum(i.employer_cost) AS "Cost", r.status AS "Status",
                           COALESCE(sum((SELECT sum(sp.amount) FROM nexora.salary_payment sp WHERE sp.payroll_item_id = i.id AND sp.status = 'completed')), 0) AS "Paid"
                    FROM nexora.payroll_item i
                    JOIN nexora.payroll_run r ON r.id = i.run_id
                    JOIN nexora.payroll_period p ON p.id = i.period_id
                    JOIN nexora.currency cur ON cur.id = i.currency_id
                    WHERE i.company_id = {Company} AND i.status NOT IN ('excluded','cancelled') AND r.status <> 'cancelled'
                      AND p.end_date BETWEEN {from} AND {to}
                    GROUP BY p.code, p.label, p.end_date, cur.code, r.status
                    ORDER BY p.end_date, cur.code
                    """).ToListAsync(ct);
                if (key == "payroll_mass")
                    return Table(info, subtitle,
                        [Text("period", "Période"), Text("currency", "Devise"), Int("employees", "Employés"), Money("gross", "Brut"),
                         Money("deductions", "Retenues"), Money("net", "Net"), Money("employer", "Charges patronales"),
                         Money("cost", "Coût employeur"), Text("status", "Statut")],
                        rows.Select(r => Row(r.Label, r.Currency, r.Employees, r.Gross, r.Deductions, r.Net, r.Employer, r.Cost, StatusLabel(r.Status))).ToList(),
                        rows.Select(r => r.Currency).Distinct().Count() == 1
                            ? Row("Total", rows.FirstOrDefault()?.Currency, null, rows.Sum(r => r.Gross), rows.Sum(r => r.Deductions),
                                rows.Sum(r => r.Net), rows.Sum(r => r.Employer), rows.Sum(r => r.Cost), null) : null);
                if (key == "net_by_period")
                    return Table(info, subtitle,
                        [Text("period", "Période"), Text("currency", "Devise"), Money("net", "Net approuvé / calculé"), Money("paid", "Payé"),
                         Money("unpaid", "Reste à payer"), Text("status", "Statut")],
                        rows.Select(r => Row(r.Label, r.Currency, r.Net, r.Paid, r.Net - r.Paid, StatusLabel(r.Status))).ToList(),
                        rows.Select(r => r.Currency).Distinct().Count() == 1
                            ? Row("Total", rows.FirstOrDefault()?.Currency, rows.Sum(r => r.Net), rows.Sum(r => r.Paid), rows.Sum(r => r.Net - r.Paid), null) : null);
                return Table(info, subtitle,
                    [Text("period", "Période"), Text("currency", "Devise"), Money("gross", "Brut"), Money("employer", "Charges patronales"),
                     Money("cost", "Coût employeur")],
                    rows.Select(r => Row(r.Label, r.Currency, r.Gross, r.Employer, r.Cost)).ToList(),
                    rows.Select(r => r.Currency).Distinct().Count() == 1
                        ? Row("Total", rows.FirstOrDefault()?.Currency, rows.Sum(r => r.Gross), rows.Sum(r => r.Employer), rows.Sum(r => r.Cost)) : null);
            }
            case "gross_by_department":
            {
                if (q.PeriodId is not { } periodId) return Error.Validation("payroll.report_period", "Choisir une période de paie.");
                var label = await db.Database.SqlQuery<string?>($"""
                    SELECT label AS "Value" FROM nexora.payroll_period WHERE id = {periodId} AND company_id = {Company}
                    """).FirstOrDefaultAsync(ct);
                if (label is null) return Error.NotFound("payroll.period_not_found", "Période introuvable.");
                var rows = await db.Database.SqlQuery<DeptRow>($"""
                    SELECT COALESCE(i.department_name, 'Sans département') AS "Department", cur.code AS "Currency", count(*)::int AS "Employees",
                           sum(i.gross_amount) AS "Gross", sum(i.net_amount) AS "Net", sum(i.employer_cost) AS "Cost"
                    FROM nexora.payroll_item i JOIN nexora.currency cur ON cur.id = i.currency_id
                    JOIN nexora.payroll_run r ON r.id = i.run_id
                    WHERE i.period_id = {periodId} AND i.status NOT IN ('excluded','cancelled') AND r.status <> 'cancelled'
                    GROUP BY 1, cur.code ORDER BY 1
                    """).ToListAsync(ct);
                return Table(info, label,
                    [Text("department", "Département"), Text("currency", "Devise"), Int("employees", "Employés"), Money("gross", "Brut"),
                     Money("net", "Net"), Money("cost", "Coût employeur")],
                    rows.Select(r => Row(r.Department, r.Currency, r.Employees, r.Gross, r.Net, r.Cost)).ToList(), null);
            }
            case "deductions_by_category":
            {
                var rows = await db.Database.SqlQuery<DeductionRow>($"""
                    SELECT d.category AS "Category", d.borne_by AS "BorneBy", cur.code AS "Currency", sum(d.amount) AS "Amount"
                    FROM nexora.payroll_deduction d
                    JOIN nexora.payroll_item i ON i.id = d.payroll_item_id
                    JOIN nexora.payroll_run r ON r.id = i.run_id
                    JOIN nexora.payroll_period p ON p.id = i.period_id
                    JOIN nexora.currency cur ON cur.id = i.currency_id
                    WHERE i.company_id = {Company} AND i.status NOT IN ('excluded','cancelled') AND r.status <> 'cancelled'
                      AND p.end_date BETWEEN {from} AND {to} AND d.amount > 0
                    GROUP BY d.category, d.borne_by, cur.code ORDER BY cur.code, 4 DESC
                    """).ToListAsync(ct);
                return Table(info, subtitle,
                    [Text("category", "Catégorie"), Text("borne", "À la charge de"), Text("currency", "Devise"), Money("amount", "Montant")],
                    rows.Select(r => Row(DeductionLabel(r.Category), r.BorneBy == "employer" ? "Employeur" : "Salarié", r.Currency, r.Amount)).ToList(),
                    null);
            }
            case "advances_open":
            case "advances_settled":
            {
                var settled = key == "advances_settled";
                var rows = await db.Database.SqlQuery<AdvanceReportRow>($"""
                    SELECT a.advance_number AS "Number", e.first_name || ' ' || e.last_name AS "Employee", a.kind AS "Kind", cur.code AS "Currency",
                           a.amount_paid AS "Paid", a.recovered_amount AS "Recovered", a.balance AS "Balance",
                           (SELECT min(x.due_date) FROM nexora.advance_installment x WHERE x.advance_id = a.id AND x.status IN ('pending','partial')) AS "NextDue",
                           a.status AS "Status", a.paid_at AS "PaidAt", a.settled_at AS "SettledAt"
                    FROM nexora.employee_advance a JOIN nexora.employee e ON e.id = a.employee_id JOIN nexora.currency cur ON cur.id = a.currency_id
                    WHERE a.company_id = {Company}
                      AND (({settled} = false AND a.status IN ('paid','in_repayment'))
                        OR ({settled} = true AND a.status = 'settled' AND a.settled_at::date BETWEEN {from} AND {to}))
                    ORDER BY a.paid_at
                    """).ToListAsync(ct);
                return Table(info, settled ? subtitle : $"Au {today:dd/MM/yyyy}",
                    [Text("number", "Référence"), Text("employee", "Employé"), Text("kind", "Type"), Text("currency", "Devise"),
                     Money("paid", "Versé"), Money("recovered", "Récupéré"), Money("balance", "Solde"),
                     settled ? Date("settled", "Soldée le") : Date("next", "Prochaine échéance")],
                    rows.Select(r => Row(r.Number, r.Employee, KindLabel(r.Kind), r.Currency, r.Paid, r.Recovered, r.Balance,
                        settled ? r.SettledAt?.ToString("yyyy-MM-dd") : r.NextDue?.ToString("yyyy-MM-dd"))).ToList(),
                    rows.Select(r => r.Currency).Distinct().Count() == 1
                        ? Row("Total", null, null, rows.FirstOrDefault()?.Currency, rows.Sum(r => r.Paid), rows.Sum(r => r.Recovered), rows.Sum(r => r.Balance), null)
                        : null);
            }
            case "advances_by_employee":
            {
                var rows = await db.Database.SqlQuery<AdvanceByEmployeeRow>($"""
                    SELECT e.first_name || ' ' || e.last_name AS "Employee", cur.code AS "Currency", count(*)::int AS "Count",
                           sum(a.amount_paid) AS "Paid", sum(a.recovered_amount) AS "Recovered", sum(a.balance) AS "Balance"
                    FROM nexora.employee_advance a JOIN nexora.employee e ON e.id = a.employee_id JOIN nexora.currency cur ON cur.id = a.currency_id
                    WHERE a.company_id = {Company} AND a.status IN ('paid','in_repayment','settled')
                      AND ({q.EmployeeId}::uuid IS NULL OR a.employee_id = {q.EmployeeId}::uuid)
                    GROUP BY 1, cur.code ORDER BY 6 DESC, 1
                    """).ToListAsync(ct);
                return Table(info, $"Au {today:dd/MM/yyyy}",
                    [Text("employee", "Employé"), Text("currency", "Devise"), Int("count", "Avances / prêts"), Money("paid", "Versé"),
                     Money("recovered", "Récupéré"), Money("balance", "Solde")],
                    rows.Select(r => Row(r.Employee, r.Currency, r.Count, r.Paid, r.Recovered, r.Balance)).ToList(), null);
            }
            case "staff_debt":
            {
                var rows = await db.Database.SqlQuery<DebtRow>($"""
                    SELECT e.employee_number AS "EmployeeNumber", e.first_name || ' ' || e.last_name AS "Employee", cur.code AS "Currency",
                           COALESCE(sum(a.balance) FILTER (WHERE a.kind <> 'loan'), 0) AS "Advances",
                           COALESCE(sum(a.balance) FILTER (WHERE a.kind = 'loan'), 0) AS "Loans"
                    FROM nexora.employee_advance a JOIN nexora.employee e ON e.id = a.employee_id JOIN nexora.currency cur ON cur.id = a.currency_id
                    WHERE a.company_id = {Company} AND a.status IN ('paid','in_repayment') AND a.balance > 0
                    GROUP BY 1, 2, cur.code ORDER BY 2
                    """).ToListAsync(ct);
                return Table(info, $"Au {today:dd/MM/yyyy}",
                    [Text("number", "Matricule"), Text("employee", "Employé"), Text("currency", "Devise"), Money("advances", "Avances et acomptes"),
                     Money("loans", "Prêts"), Money("total", "Total dû")],
                    rows.Select(r => Row(r.EmployeeNumber, r.Employee, r.Currency, r.Advances, r.Loans, r.Advances + r.Loans)).ToList(),
                    rows.Select(r => r.Currency).Distinct().Count() == 1
                        ? Row("Total", null, rows.FirstOrDefault()?.Currency, rows.Sum(r => r.Advances), rows.Sum(r => r.Loans),
                            rows.Sum(r => r.Advances + r.Loans)) : null);
            }
            case "unpaid":
            {
                var rows = await db.Database.SqlQuery<UnpaidRow>($"""
                    SELECT r.run_number AS "RunNumber", p.label AS "Period", i.employee_name AS "Employee", cur.code AS "Currency",
                           i.net_amount AS "Net", r.approved_at AS "ApprovedAt"
                    FROM nexora.payroll_item i
                    JOIN nexora.payroll_run r ON r.id = i.run_id
                    JOIN nexora.payroll_period p ON p.id = i.period_id
                    JOIN nexora.currency cur ON cur.id = i.currency_id
                    WHERE i.company_id = {Company} AND i.status = 'approved' AND i.net_amount > 0
                    ORDER BY r.approved_at, i.employee_name
                    """).ToListAsync(ct);
                return Table(info, $"Au {today:dd/MM/yyyy}",
                    [Text("run", "Paie"), Text("period", "Période"), Text("employee", "Employé"), Text("currency", "Devise"),
                     Money("net", "Net à payer"), new PayrollReportColumnDto("approved", "Approuvée le", PrintKinds.DateTime)],
                    rows.Select(r => Row(r.RunNumber, r.Period, r.Employee, r.Currency, r.Net, r.ApprovedAt?.ToString("o"))).ToList(), null);
            }
            case "period_variance":
            {
                if (q.PeriodId is not { } periodId) return Error.Validation("payroll.report_period", "Choisir une période de paie.");
                var rows = await db.Database.SqlQuery<VarianceRow>($"""
                    SELECT i.employee_number AS "EmployeeNumber", i.employee_name AS "Employee", cur.code AS "Currency",
                           (SELECT pi.net_amount FROM nexora.payroll_item pi
                              JOIN nexora.payroll_run pr ON pr.id = pi.run_id JOIN nexora.payroll_period pp ON pp.id = pi.period_id
                             WHERE pi.employee_id = i.employee_id AND pp.kind = 'regular' AND pp.end_date < p.start_date
                               AND pr.status IN ('approved','paid','closed') AND pi.status IN ('approved','paid')
                             ORDER BY pp.end_date DESC LIMIT 1) AS "Previous",
                           i.net_amount AS "Current"
                    FROM nexora.payroll_item i
                    JOIN nexora.payroll_period p ON p.id = i.period_id
                    JOIN nexora.payroll_run r ON r.id = i.run_id
                    JOIN nexora.currency cur ON cur.id = i.currency_id
                    WHERE i.period_id = {periodId} AND i.company_id = {Company} AND i.status NOT IN ('excluded','cancelled') AND r.status <> 'cancelled'
                    ORDER BY i.employee_name
                    """).ToListAsync(ct);
                return Table(info, null,
                    [Text("number", "Matricule"), Text("employee", "Employé"), Text("currency", "Devise"), Money("previous", "Net précédent"),
                     Money("current", "Net de la période"), Money("difference", "Écart"), Pct("variation", "Variation")],
                    rows.Select(r => Row(r.EmployeeNumber, r.Employee, r.Currency, r.Previous, r.Current,
                        r.Previous is { } prev ? r.Current - prev : null,
                        r.Previous is > 0m ? Math.Round((r.Current - r.Previous.Value) / r.Previous.Value * 100m, 1) : null)).ToList(), null);
            }
        }
        return Error.NotFound("payroll.report_not_found", $"Rapport « {key} » inconnu.");
    }

    private static IReadOnlyList<object?> Row(params object?[] values) => values;

    private static Result<PayrollReportDto> Table(PayrollReportInfoDto info, string? subtitle, IReadOnlyList<PayrollReportColumnDto> columns,
        IReadOnlyList<IReadOnlyList<object?>> rows, IReadOnlyList<object?>? totals)
        => Result.Success(new PayrollReportDto(info.Key, info.Title, subtitle, columns, rows, totals));

    public async Task<Result<RenderedFile>> ExportAsync(string key, PayrollReportQuery q, string format, CancellationToken ct = default)
    {
        var fmt = PrintFormats.Normalize(format);
        if (!PrintFormats.IsValid(fmt) || fmt == PrintFormats.Ticket) return Error.Validation("print.format", "Format : pdf, xlsx ou csv.");
        var report = await ReportAsync(key, q, ct);
        if (!report.IsSuccess) return report.Error!;
        var r = report.Value!;
        // Une seule devise dans le rapport → les montants portent son code ; sinon la colonne « Devise » l'indique.
        var currencyIndex = r.Columns.ToList().FindIndex(c => c.Key == "currency");
        var single = currencyIndex >= 0 ? r.Rows.Select(x => x[currencyIndex] as string).Where(x => x is not null).Distinct().ToList() : [];
        var document = new PrintDocument(
            Title: r.Title,
            Subtitle: r.Subtitle,
            Tables: [new PrintTable(null, r.Columns.Select(c => new PrintColumn(c.Label, c.Kind, single.Count == 1 && c.Kind == PrintKinds.Money ? single[0] : null)).ToList(),
                r.Rows, r.Totals)],
            Notes: ["Données confidentielles de paie — diffusion restreinte aux personnes autorisées."],
            Landscape: r.Columns.Count > 6);
        await access.LogSensitiveAccessAsync("payroll_report", Guid.Empty, $"export:{key}:{fmt}", ct);
        return Result.Success(await print.RenderAsync(document, fmt, $"payroll:{key}", $"paie-{key.Replace('_', '-')}", "restricted", null, null, ct));
    }

    private static string StatusLabel(string s) => s switch
    {
        "draft" => "Brouillon", "calculated" => "Calculée", "reviewed" => "Contrôlée", "validated" => "Validée", "approved" => "Approuvée",
        "paid" => "Payée", "closed" => "Clôturée", "cancelled" => "Annulée", _ => s,
    };

    private static string KindLabel(string k) => k switch { "loan" => "Prêt", "acompte" => "Acompte", _ => "Avance sur salaire" };

    private static string DeductionLabel(string c) => c switch
    {
        "tax" => "Impôts sur salaire", "social" => "Cotisations salariales", "employer" => "Cotisations patronales",
        "absence" => "Absences non rémunérées", "advance" => "Récupérations d'avances", "loan" => "Échéances de prêts",
        "benefit_offset" => "Avantages en nature", "adjustment" => "Régularisations", _ => "Autres retenues",
    };

    // ================================================================== Historique financier de l'employé (§26)

    private sealed record HistoryRow(DateTimeOffset OccurredAt, string Type, string Title, string? Detail, decimal? Amount, string? Currency,
        string? Reference, Guid? SourceId, string? ActorName);

    public async Task<Result<IReadOnlyList<EmployeeHistoryEntryDto>>> EmployeeHistoryAsync(Guid employeeId, CancellationToken ct = default)
    {
        var own = await access.HasAsync(Permissions.Payroll.SelfService, ct) && await access.OwnEmployeeIdAsync(ct) == employeeId;
        if (!own && !await access.CanViewSalariesAsync(ct)) return Error.NotFound("hr.employee_not_found", "Employé introuvable.");
        var rows = await db.Database.SqlQuery<HistoryRow>($"""
            SELECT r.approved_at AS "OccurredAt", 'salary_calculated' AS "Type", 'Salaire ' || p.label AS "Title",
                   'Brut ' || to_char(i.gross_amount, 'FM999G999G990D00') || ' — retenues ' || to_char(i.total_deductions, 'FM999G999G990D00') AS "Detail",
                   i.net_amount AS "Amount", cur.code AS "Currency", r.run_number AS "Reference", i.id AS "SourceId", u.full_name AS "ActorName"
            FROM nexora.payroll_item i JOIN nexora.payroll_run r ON r.id = i.run_id JOIN nexora.payroll_period p ON p.id = i.period_id
            JOIN nexora.currency cur ON cur.id = i.currency_id LEFT JOIN nexora.app_user u ON u.id = r.approved_by
            WHERE i.employee_id = {employeeId} AND i.company_id = {Company} AND i.status IN ('approved','paid') AND r.approved_at IS NOT NULL
            UNION ALL
            SELECT sp.paid_at, CASE WHEN sp.status = 'cancelled' THEN 'salary_payment_cancelled' ELSE 'salary_paid' END,
                   CASE WHEN sp.status = 'cancelled' THEN 'Paiement de salaire annulé' ELSE 'Salaire payé' END,
                   sp.payment_method || COALESCE(' — ' || sp.cancel_reason, ''), sp.amount, cur.code, sp.payment_number, sp.id, u.full_name
            FROM nexora.salary_payment sp JOIN nexora.currency cur ON cur.id = sp.currency_id LEFT JOIN nexora.app_user u ON u.id = sp.created_by
            WHERE sp.employee_id = {employeeId} AND sp.company_id = {Company}
            UNION ALL
            SELECT ev.occurred_at, 'advance_' || ev.event_type,
                   CASE a.kind WHEN 'loan' THEN 'Prêt ' WHEN 'acompte' THEN 'Acompte ' ELSE 'Avance ' END || a.advance_number || ' — ' ||
                   CASE ev.event_type WHEN 'requested' THEN 'demandé(e)' WHEN 'approved' THEN 'approuvé(e)' WHEN 'rejected' THEN 'refusé(e)'
                        WHEN 'paid' THEN 'versé(e)' WHEN 'recovered_payroll' THEN 'récupération sur paie'
                        WHEN 'recovered_manual' THEN 'remboursement direct' WHEN 'schedule_set' THEN 'échéancier'
                        WHEN 'schedule_changed' THEN 'échéancier modifié' WHEN 'settled' THEN 'soldé(e)' WHEN 'cancelled' THEN 'annulé(e)'
                        ELSE 'étape d''approbation' END,
                   ev.note || COALESCE(' — solde ' || to_char(ev.balance_after, 'FM999G999G990D00'), ''), ev.amount, cur.code, ev.reference, a.id,
                   u.full_name
            FROM nexora.employee_advance_event ev JOIN nexora.employee_advance a ON a.id = ev.advance_id
            JOIN nexora.currency cur ON cur.id = a.currency_id LEFT JOIN nexora.app_user u ON u.id = ev.actor_id
            WHERE a.employee_id = {employeeId} AND a.company_id = {Company}
            UNION ALL
            SELECT COALESCE(c.paid_at, c.decided_at, c.submitted_at), 'expense_' || c.status, 'Note de frais ' || c.claim_number,
                   c.description, c.amount, cur.code, c.claim_number, c.id, u.full_name
            FROM nexora.employee_expense_claim c JOIN nexora.currency cur ON cur.id = c.currency_id LEFT JOIN nexora.app_user u ON u.id = c.submitted_by
            WHERE c.employee_id = {employeeId} AND c.company_id = {Company}
            UNION ALL
            SELECT COALESCE(a.applied_at, a.decided_at, a.requested_at), 'adjustment_' || a.status,
                   'Régularisation ' || a.adjustment_number || CASE WHEN a.kind = 'earning' THEN ' (rappel)' ELSE ' (retenue)' END,
                   a.reason, a.amount, cur.code, a.adjustment_number, a.id, u.full_name
            FROM nexora.payroll_adjustment a JOIN nexora.payroll_item i ON i.id = a.original_item_id JOIN nexora.currency cur ON cur.id = i.currency_id
            LEFT JOIN nexora.app_user u ON u.id = a.requested_by
            WHERE a.employee_id = {employeeId} AND a.company_id = {Company}
            UNION ALL
            SELECT ps.issued_at, 'payslip', 'Bulletin ' || ps.payslip_number || CASE WHEN ps.status = 'cancelled' THEN ' (annulé)' ELSE '' END,
                   NULL, NULL, NULL, ps.payslip_number, ps.id, u.full_name
            FROM nexora.payslip ps LEFT JOIN nexora.app_user u ON u.id = ps.issued_by
            WHERE ps.employee_id = {employeeId} AND ps.company_id = {Company}
            UNION ALL
            SELECT h.created_at, 'salary_change', 'Rémunération au ' || to_char(h.effective_date, 'DD/MM/YYYY'),
                   h.reason || COALESCE(' — ancien : ' || to_char(h.previous_amount, 'FM999G999G990D00'), ''), h.amount, cur.code, NULL, h.id,
                   u.full_name
            FROM nexora.salary_history h JOIN nexora.currency cur ON cur.id = h.currency_id LEFT JOIN nexora.app_user u ON u.id = h.created_by
            WHERE h.employee_id = {employeeId} AND h.company_id = {Company}
            UNION ALL
            SELECT c.created_at, 'contract', 'Contrat ' || c.contract_number || ' (' || c.contract_type || ')',
                   'Du ' || to_char(c.start_date, 'DD/MM/YYYY') || COALESCE(' au ' || to_char(c.end_date, 'DD/MM/YYYY'), '')
                       || COALESCE(' — ' || c.end_reason, ''), NULL, NULL, c.contract_number, c.id, u.full_name
            FROM nexora.employee_contract c LEFT JOIN nexora.app_user u ON u.id = c.created_by
            WHERE c.employee_id = {employeeId} AND c.company_id = {Company}
            ORDER BY 1 DESC
            LIMIT 1000
            """).ToListAsync(ct);
        return Result.Success<IReadOnlyList<EmployeeHistoryEntryDto>>(rows.Select(r => new EmployeeHistoryEntryDto(r.OccurredAt, r.Type, r.Title,
            r.Detail, r.Amount, r.Currency, r.Reference, r.SourceId, r.ActorName)).ToList());
    }

    // ================================================================== Intelligence de paie (§33) — règles déterministes

    private sealed record PeriodGrossRow(string Label, string Currency, decimal Gross, decimal Cost, int Rank);
    private sealed record OvertimeRow(string Label, decimal Amount, decimal Hours, int Rank);
    private sealed record EmployeeCountRow(Guid EmployeeId, string EmployeeName, int Count);
    private sealed record DebtLimitRow(Guid EmployeeId, string EmployeeName, string Currency, decimal Outstanding, decimal? Salary, string? PayBasis);
    private sealed record DuplicateBeneficiaryRow(string BankName, string Last4, int Employees, string Names);
    private sealed record DuplicatePaymentRow(Guid EmployeeId, string EmployeeName, decimal Amount, string Currency, int Count);

    public async Task<IReadOnlyList<PayrollInsightDto>> InsightsAsync(CancellationToken ct = default)
    {
        var cfg = await config.ReadAsync(ct);
        var insights = new List<PayrollInsightDto>();
        string M(decimal v) => v.ToString("N2", Fr);

        // 1. Variation anormale de masse salariale et comparaison des coûts entre périodes.
        var periods = await db.Database.SqlQuery<PeriodGrossRow>($"""
            SELECT label AS "Label", currency AS "Currency", gross AS "Gross", cost AS "Cost",
                   (row_number() OVER (PARTITION BY currency ORDER BY end_date DESC))::int AS "Rank"
            FROM (SELECT p.label, p.end_date, cur.code AS currency, sum(i.gross_amount) AS gross, sum(i.employer_cost) AS cost
                  FROM nexora.payroll_item i JOIN nexora.payroll_run r ON r.id = i.run_id JOIN nexora.payroll_period p ON p.id = i.period_id
                  JOIN nexora.currency cur ON cur.id = i.currency_id
                  WHERE i.company_id = {Company} AND p.kind = 'regular' AND r.status NOT IN ('draft','cancelled')
                    AND i.status NOT IN ('excluded','cancelled')
                  GROUP BY p.label, p.end_date, cur.code) t
            """).ToListAsync(ct);
        foreach (var g in periods.GroupBy(p => p.Currency))
        {
            var cur = g.FirstOrDefault(x => x.Rank == 1);
            var prev = g.FirstOrDefault(x => x.Rank == 2);
            if (cur is null || prev is null || prev.Gross == 0m) continue;
            var variation = (cur.Gross - prev.Gross) / prev.Gross * 100m;
            if (Math.Abs(variation) > cfg.Calc.VarianceAlertPercent)
                insights.Add(new("payroll_variance", "warning", "Variation anormale de la masse salariale",
                    $"{cur.Label} : brut {M(cur.Gross)} {g.Key} contre {M(prev.Gross)} ({prev.Label}), soit {variation.ToString("+0.0;-0.0", Fr)} % " +
                    $"(seuil {cfg.Calc.VarianceAlertPercent.ToString("0.#", Fr)} %).", null, null, cur.Gross - prev.Gross, g.Key));
            insights.Add(new("cost_comparison", "info", "Coût de la paie par rapport à la période précédente",
                $"Coût employeur {cur.Label} : {M(cur.Cost)} {g.Key} ; {prev.Label} : {M(prev.Cost)} ({(cur.Cost - prev.Cost).ToString("+#,##0.00;-#,##0.00", Fr)}).",
                null, null, cur.Cost - prev.Cost, g.Key));
        }

        // 2. Augmentation inhabituelle des heures supplémentaires (période la plus récente vs précédente).
        var overtime = await db.Database.SqlQuery<OvertimeRow>($"""
            SELECT label AS "Label", amount AS "Amount", hours AS "Hours", (row_number() OVER (ORDER BY end_date DESC))::int AS "Rank"
            FROM (SELECT p.label, p.end_date, sum(e.amount) AS amount, COALESCE(sum(e.quantity), 0) AS hours
                  FROM nexora.payroll_earning e JOIN nexora.payroll_item i ON i.id = e.payroll_item_id
                  JOIN nexora.payroll_run r ON r.id = i.run_id JOIN nexora.payroll_period p ON p.id = i.period_id
                  WHERE i.company_id = {Company} AND e.category = 'overtime' AND r.status NOT IN ('draft','cancelled') AND p.kind = 'regular'
                    AND i.status NOT IN ('excluded','cancelled')
                  GROUP BY p.label, p.end_date) t
            """).ToListAsync(ct);
        if (overtime.FirstOrDefault(o => o.Rank == 1) is { } otNow && overtime.FirstOrDefault(o => o.Rank == 2) is { } otPrev
            && otPrev.Hours > 0m && otNow.Hours > otPrev.Hours * 1.5m && otNow.Hours - otPrev.Hours >= 10m)
            insights.Add(new("overtime_spike", "warning", "Augmentation inhabituelle des heures supplémentaires",
                $"{otNow.Label} : {otNow.Hours.ToString("N1", Fr)} h ({M(otNow.Amount)}) contre {otPrev.Hours.ToString("N1", Fr)} h à {otPrev.Label}.",
                null, null, otNow.Amount - otPrev.Amount, null));

        // 3. Avances fréquentes (§33 « fréquentes ou atypiques selon règles »).
        var frequent = await db.Database.SqlQuery<EmployeeCountRow>($"""
            SELECT a.employee_id AS "EmployeeId", e.first_name || ' ' || e.last_name AS "EmployeeName", count(*)::int AS "Count"
            FROM nexora.employee_advance a JOIN nexora.employee e ON e.id = a.employee_id
            WHERE a.company_id = {Company} AND a.kind <> 'loan' AND a.status NOT IN ('rejected','cancelled')
              AND a.requested_at >= now() - interval '90 days'
            GROUP BY a.employee_id, e.first_name, e.last_name
            HAVING count(*) >= {cfg.FrequentAdvanceCount}
            """).ToListAsync(ct);
        insights.AddRange(frequent.Select(f => new PayrollInsightDto("frequent_advances", "warning", "Avances fréquentes",
            $"{f.EmployeeName} : {f.Count} avances demandées sur 90 jours (seuil {cfg.FrequentAdvanceCount}).", f.EmployeeId, f.EmployeeName, null, null)));

        // 4. Encours proche du plafond d'endettement configuré (§16, §33).
        var debts = await db.Database.SqlQuery<DebtLimitRow>($"""
            SELECT a.employee_id AS "EmployeeId", e.first_name || ' ' || e.last_name AS "EmployeeName", cur.code AS "Currency",
                   sum(a.balance) AS "Outstanding",
                   (SELECT h.amount FROM nexora.salary_history h WHERE h.employee_id = a.employee_id AND h.effective_date <= current_date
                     ORDER BY h.effective_date DESC LIMIT 1) AS "Salary",
                   (SELECT h.pay_basis FROM nexora.salary_history h WHERE h.employee_id = a.employee_id AND h.effective_date <= current_date
                     ORDER BY h.effective_date DESC LIMIT 1) AS "PayBasis"
            FROM nexora.employee_advance a JOIN nexora.employee e ON e.id = a.employee_id JOIN nexora.currency cur ON cur.id = a.currency_id
            WHERE a.company_id = {Company} AND a.kind <> 'loan' AND a.status IN ('paid','in_repayment')
            GROUP BY a.employee_id, e.first_name, e.last_name, cur.code
            """).ToListAsync(ct);
        foreach (var d in debts.Where(d => d.Salary is > 0m))
        {
            var monthly = d.PayBasis switch
            {
                "daily" => d.Salary!.Value * cfg.Calc.DaysPerMonth,
                "hourly" => d.Salary!.Value * cfg.Calc.DefaultWeeklyHours * 52m / 12m,
                _ => d.Salary!.Value,
            };
            var ceiling = monthly * cfg.Advances.AdvanceCeilingPercent / 100m;
            if (ceiling > 0m && d.Outstanding >= ceiling * cfg.Advances.NearLimitPercent / 100m)
                insights.Add(new("advance_near_limit", "warning", "Avances proches du plafond",
                    $"{d.EmployeeName} : encours {M(d.Outstanding)} {d.Currency} pour un plafond de {M(Math.Round(ceiling, 2))} " +
                    $"({(d.Outstanding / ceiling * 100m).ToString("N0", Fr)} %).", d.EmployeeId, d.EmployeeName, d.Outstanding, d.Currency));
        }

        // 5. Anomalies de paiement : même compte bénéficiaire pour plusieurs employés, paiements en double.
        var sharedAccounts = await db.Database.SqlQuery<DuplicateBeneficiaryRow>($"""
            SELECT COALESCE(bank_name, '') AS "BankName", bank_account_last4 AS "Last4", count(*)::int AS "Employees",
                   string_agg(first_name || ' ' || last_name, ', ') AS "Names"
            FROM nexora.employee
            WHERE company_id = {Company} AND status <> 'terminated' AND bank_account_last4 IS NOT NULL
            GROUP BY COALESCE(bank_name, ''), bank_account_last4
            HAVING count(*) > 1
            """).ToListAsync(ct);
        insights.AddRange(sharedAccounts.Select(s => new PayrollInsightDto("shared_bank_account", "warning", "Compte bancaire possiblement partagé",
            $"{s.Employees} employés ont un compte {s.BankName} se terminant par {s.Last4} : {s.Names}. Vérifier les coordonnées de paiement.",
            null, null, null, null)));
        var duplicatePayments = await db.Database.SqlQuery<DuplicatePaymentRow>($"""
            SELECT sp.employee_id AS "EmployeeId", e.first_name || ' ' || e.last_name AS "EmployeeName", sp.amount AS "Amount",
                   cur.code AS "Currency", count(*)::int AS "Count"
            FROM nexora.salary_payment sp JOIN nexora.employee e ON e.id = sp.employee_id JOIN nexora.currency cur ON cur.id = sp.currency_id
            WHERE sp.company_id = {Company} AND sp.status = 'completed' AND sp.paid_at >= now() - interval '45 days'
            GROUP BY sp.employee_id, e.first_name, e.last_name, sp.amount, cur.code, sp.paid_at::date
            HAVING count(*) > 1
            """).ToListAsync(ct);
        insights.AddRange(duplicatePayments.Select(p => new PayrollInsightDto("duplicate_payment", "warning", "Paiement possiblement en double",
            $"{p.EmployeeName} : {p.Count} paiements de {M(p.Amount)} {p.Currency} le même jour.", p.EmployeeId, p.EmployeeName, p.Amount, p.Currency)));
        return insights;
    }

    // ================================================================== Assistant (§33)

    private sealed record OutstandingRow(string Currency, decimal Advances, decimal Loans, int Count);

    public async Task<IReadOnlyList<(string Currency, decimal Advances, decimal Loans, int Count)>> OutstandingAdvancesAsync(CancellationToken ct = default)
        => (await db.Database.SqlQuery<OutstandingRow>($"""
            SELECT cur.code AS "Currency", COALESCE(sum(a.balance) FILTER (WHERE a.kind <> 'loan'), 0) AS "Advances",
                   COALESCE(sum(a.balance) FILTER (WHERE a.kind = 'loan'), 0) AS "Loans", count(*)::int AS "Count"
            FROM nexora.employee_advance a JOIN nexora.currency cur ON cur.id = a.currency_id
            WHERE a.company_id = {Company} AND a.status IN ('paid','in_repayment') AND a.balance > 0
            GROUP BY cur.code
            """).ToListAsync(ct)).Select(r => (r.Currency, r.Advances, r.Loans, r.Count)).ToList();

    public async Task<IReadOnlyList<CurrencyTotalsDto>> PayrollMassAsync(DateOnly monthStart, CancellationToken ct = default)
    {
        var monthEnd = monthStart.AddMonths(1).AddDays(-1);
        return (await db.Database.SqlQuery<TotalsRow>($"""
            SELECT cur.code AS "Currency", sum(i.gross_amount) AS "Gross", sum(i.total_deductions) AS "Deductions", sum(i.net_amount) AS "Net",
                   sum(i.employer_cost) AS "EmployerCost",
                   COALESCE(sum((SELECT sum(sp.amount) FROM nexora.salary_payment sp WHERE sp.payroll_item_id = i.id AND sp.status = 'completed')), 0) AS "Paid"
            FROM nexora.payroll_item i
            JOIN nexora.payroll_run r ON r.id = i.run_id
            JOIN nexora.payroll_period p ON p.id = i.period_id
            JOIN nexora.currency cur ON cur.id = i.currency_id
            WHERE i.company_id = {Company} AND r.status NOT IN ('draft','cancelled') AND i.status NOT IN ('excluded','cancelled')
              AND p.end_date BETWEEN {monthStart} AND {monthEnd}
            GROUP BY cur.code
            """).ToListAsync(ct)).Select(t => new CurrencyTotalsDto(t.Currency, t.Gross, t.Deductions, t.Net, t.EmployerCost, t.Paid, t.Net - t.Paid))
            .ToList();
    }

    // ================================================================== Alertes planifiées (§34)

    private sealed record DueInstallmentRow(Guid InstallmentId, Guid AdvanceId, string AdvanceNumber, Guid EmployeeId, Guid? UserId, DateOnly DueDate,
        decimal Amount, string Currency);

    public async Task<int> ScanAsync(CancellationToken ct = default)
    {
        var cfg = await config.ReadAsync(ct);
        var sent = 0;
        // « Échéance d'avance proche » : l'employé et les gestionnaires de paie sont prévenus une fois par échéance.
        var due = await db.Database.SqlQuery<DueInstallmentRow>($"""
            SELECT x.id AS "InstallmentId", a.id AS "AdvanceId", a.advance_number AS "AdvanceNumber", a.employee_id AS "EmployeeId",
                   e.user_id AS "UserId", x.due_date AS "DueDate", x.amount - x.recovered_amount AS "Amount", cur.code AS "Currency"
            FROM nexora.advance_installment x
            JOIN nexora.employee_advance a ON a.id = x.advance_id
            JOIN nexora.employee e ON e.id = a.employee_id
            JOIN nexora.currency cur ON cur.id = a.currency_id
            WHERE a.company_id = {Company} AND x.status IN ('pending','partial') AND a.status IN ('paid','in_repayment')
              AND x.due_date BETWEEN current_date AND current_date + {cfg.AdvanceDueNoticeDays}
            """).ToListAsync(ct);
        foreach (var d in due)
        {
            await notifier.ToUsersAsync([d.UserId], "Échéance d'avance proche",
                $"{d.AdvanceNumber} : {d.Amount:N2} {d.Currency} seront retenus sur la paie couvrant le {d.DueDate:dd/MM/yyyy}.",
                "/me/hr?tab=advances", "advance_installment", d.InstallmentId, $"installment:{d.InstallmentId}:due", ct);
            sent++;
        }
        // Anomalies détectées (§33-§34) : une notification par constat et par jour aux détenteurs des rapports de paie.
        var day = await PayrollRules.TodayAsync(db, ct);
        foreach (var i in (await InsightsAsync(ct)).Where(i => i.Severity == "warning"))
        {
            await notifier.ToPermissionAsync(Permissions.Payroll.Reports, "Anomalie de paie détectée — " + i.Title, i.Detail, "/hr?tab=dashboard",
                "payroll_insight", i.EmployeeId ?? Guid.Empty, $"payroll_insight:{i.Code}:{i.EmployeeId}:{day:yyyyMMdd}", ct, "warning");
            sent++;
        }
        logger.LogInformation("Alertes de paie : {Count} notification(s) pour l'entreprise {Company}", sent, Company);
        return sent;
    }
}
