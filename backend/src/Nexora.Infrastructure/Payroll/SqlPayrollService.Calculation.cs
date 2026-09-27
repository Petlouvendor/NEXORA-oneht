using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Nexora.Application.Payroll;
using Nexora.Application.Security;
using Nexora.Contracts.Payroll;
using Nexora.Domain.Common;

namespace Nexora.Infrastructure.Payroll;

/// <summary>
/// Calcul de la paie (§11, §23) : les données de la période sont chargées en une passe, le moteur PUR
/// <see cref="PayrollCalculator"/> produit chaque ligne employé, puis le tout est enregistré dans une seule
/// transaction. Recalculer remplace les lignes tant que la paie n'est pas validée ; une exclusion décidée
/// par l'utilisateur est conservée.
/// </summary>
public sealed partial class SqlPayrollService
{
    private sealed record RunLockRow(Guid Id, string Status, string RunNumber);
    private sealed record PeriodCalcRow(Guid Id, string Label, string Kind, string Frequency, DateOnly StartDate, DateOnly EndDate, Guid[] EmployeeScope);
    private sealed record EmployeeCalcRow(
        Guid Id, string Number, string Name, string Status, DateOnly HireDate, DateOnly? TerminationDate, string PaymentMethod,
        bool HasBank, bool HasMobile, Guid? UserId, string? JobTitle, Guid? DepartmentId, string? DepartmentName);
    private sealed record ContractCalcRow(
        Guid Id, Guid EmployeeId, string ContractType, DateOnly StartDate, DateOnly? EndDate, string PayFrequency, string PayBasis,
        decimal WeeklyHours, string Currency);
    private sealed record SalaryCalcRow(Guid Id, Guid EmployeeId, Guid ContractId, DateOnly EffectiveDate, string PayBasis, decimal Amount, decimal? HourlyRate);
    private sealed record ComponentCalcRow(
        Guid Id, string Code, string Name, string Kind, string Category, bool Taxable, bool Social, bool InGross, bool Cash);
    private sealed record ElementCalcRow(
        Guid Id, Guid EmployeeId, Guid ComponentId, string Method, decimal? Amount, decimal? Rate, DateOnly StartDate, DateOnly? EndDate);
    private sealed record VariableCalcRow(Guid Id, Guid EmployeeId, Guid ComponentId, decimal Amount, string Description, string Status);
    private sealed record LeaveCalcRow(Guid Id, Guid EmployeeId, string Type, DateOnly StartDate, DateOnly EndDate, decimal Days, bool Paid, string Status);
    private sealed record AttendanceCalcRow(Guid EmployeeId, DateOnly WorkDate, string Status, decimal? Hours, bool Justified, string ValidationStatus);
    private sealed record OvertimeCalcRow(Guid Id, Guid EmployeeId, DateOnly WorkDate, decimal Hours, string Type, decimal Coefficient, string Status);
    private sealed record AdjustmentCalcRow(
        Guid Id, Guid EmployeeId, string Number, string Kind, Guid? ComponentId, decimal Amount, bool Taxable, string Reason, string Currency);
    private sealed record ClaimCalcRow(Guid Id, Guid EmployeeId, string Number, decimal Amount, string Description, string Currency);
    private sealed record RuleCalcRow(
        Guid Id, string Code, string Name, int Version, string RuleType, string BaseType, string Method, decimal? Rate, decimal? FixedAmount,
        string Brackets, bool Annualize, decimal? BaseFloor, decimal? BaseCeiling, decimal? MinAmount, decimal? MaxAmount);
    private sealed record AdvanceCalcRow(
        Guid Id, Guid EmployeeId, string Number, string Kind, string RecoveryMethod, decimal Balance, decimal DueNow, decimal? RecoveryPercent,
        string Currency);
    private sealed record PreviousNetRow(Guid EmployeeId, decimal Net);
    private sealed record DuplicateRow(Guid EmployeeId, string RunNumber);
    private sealed record SalesRow(Guid UserId, decimal Amount);
    private sealed record ExcludedRow(Guid EmployeeId, string Reason);

    private static string PeriodStatusFor(string runStatus) => runStatus switch
    {
        "reviewed" or "validated" => "in_validation",
        var s => s,
    };

    public async Task<Result<PayrollRunDto>> CalculateAsync(Guid periodId, CancellationToken ct = default)
    {
        var cfg = await config.ReadAsync(ct);
        Guid runId;
        int blocking, warnings, employees;
        try
        {
            (runId, blocking, warnings, employees) = await uow.ExecuteInTransactionAsync(async token =>
            {
                var period = await db.Database.SqlQuery<PeriodCalcRow>($"""
                    SELECT id AS "Id", label AS "Label", kind AS "Kind", frequency AS "Frequency", start_date AS "StartDate",
                           end_date AS "EndDate", employee_scope AS "EmployeeScope"
                    FROM nexora.payroll_period WHERE id = {periodId} AND company_id = {Company}
                    """).FirstOrDefaultAsync(token)
                    ?? throw new PayrollRefusal(Error.NotFound("payroll.period_not_found", "Période introuvable."));
                var run = await db.Database.SqlQuery<RunLockRow>($"""
                    SELECT id AS "Id", status AS "Status", run_number AS "RunNumber" FROM nexora.payroll_run
                    WHERE period_id = {periodId} AND company_id = {Company} AND status <> 'cancelled'
                    FOR UPDATE
                    """).FirstOrDefaultAsync(token)
                    ?? throw new PayrollRefusal(Error.Conflict("payroll.run_cancelled", "La paie de cette période a été annulée."));
                if (run.Status is not ("draft" or "calculated" or "reviewed"))
                    throw new PayrollRefusal(Error.Conflict("payroll.run_locked",
                        $"Paie {run.RunNumber} au statut « {run.Status} » : elle ne peut plus être recalculée (régularisation)."));

                var results = await ComputeAsync(period, run.Id, cfg, token);
                var excluded = (await db.Database.SqlQuery<ExcludedRow>($"""
                    SELECT employee_id AS "EmployeeId", excluded_reason AS "Reason" FROM nexora.payroll_item
                    WHERE run_id = {run.Id} AND status = 'excluded'
                    """).ToListAsync(token)).ToDictionary(x => x.EmployeeId, x => x.Reason);

                await db.Database.ExecuteSqlAsync($"DELETE FROM nexora.payroll_item WHERE run_id = {run.Id}", token);
                var componentIds = (await db.Database.SqlQuery<ComponentCalcRow>($"""
                    SELECT id AS "Id", code AS "Code", name AS "Name", kind AS "Kind", category AS "Category", taxable AS "Taxable",
                           subject_to_social AS "Social", in_gross AS "InGross", cash AS "Cash"
                    FROM nexora.pay_component WHERE company_id IS NULL
                    """).ToListAsync(token)).ToDictionary(c => c.Code, c => c.Id);

                foreach (var (emp, result) in results)
                    await PersistItemAsync(run.Id, periodId, emp, result, componentIds,
                        excluded.TryGetValue(emp.Id, out var reason) ? reason : null, token);

                await db.Database.ExecuteSqlAsync($"""
                    UPDATE nexora.payroll_run
                       SET status = 'calculated', stale = false, calculation_count = calculation_count + 1, calculated_by = {UserId},
                           calculated_at = now(), reviewed_by = NULL, reviewed_at = NULL, review_note = NULL, warnings_acknowledged = false
                     WHERE id = {run.Id}
                    """, token);
                await db.Database.ExecuteSqlAsync($"UPDATE nexora.payroll_period SET status = 'calculated' WHERE id = {periodId}", token);
                var active = results.Where(x => !excluded.ContainsKey(x.Employee.Id)).ToList();
                return (run.Id, active.Sum(x => x.Result.BlockingCount), active.Sum(x => x.Result.WarningCount), active.Count);
            }, ct);
        }
        catch (PayrollRefusal refusal)
        {
            return refusal.Error;
        }
        catch (InvalidOperationException ex)
        {
            logger.LogWarning(ex, "Calcul de paie refusé pour la période {Period}", periodId);
            return Translate(ex, "payroll.calculation_failed");
        }

        var run2 = (await RunAsync(runId, ct)).Value!;
        await notifier.ToPermissionAsync(Permissions.Payroll.Review, "Paie à contrôler",
            $"{run2.RunNumber} — {run2.PeriodLabel} : {employees} employé(s), {blocking} anomalie(s) bloquante(s), {warnings} avertissement(s).",
            $"/hr/payroll/{runId}", "payroll_run", runId, $"payroll:{runId}:calculated:{run2.CalculationCount}", ct,
            blocking > 0 ? "warning" : "info");
        if (blocking > 0)
            await notifier.ToPermissionAsync(Permissions.Payroll.Calculate, "Anomalies de paie détectées",
                $"{run2.RunNumber} : {blocking} anomalie(s) bloquante(s) à corriger avant validation.",
                $"/hr/payroll/{runId}", "payroll_run", runId, $"payroll:{runId}:anomalies:{run2.CalculationCount}", ct, "warning");
        logger.LogInformation("Paie {Run} calculée : {Count} employé(s), {Blocking} bloquante(s)", run2.RunNumber, employees, blocking);
        return Result.Success(run2);
    }

    /// <summary>Charge les données de la période pour tous les employés concernés et appelle le moteur pur.</summary>
    private async Task<List<(EmployeeCalcRow Employee, PayrollCalcResult Result)>> ComputeAsync(
        PeriodCalcRow p, Guid runId, PayrollConfiguration cfg, CancellationToken ct)
    {
        var offCycle = p.Kind == "off_cycle";
        var scope = p.EmployeeScope ?? [];
        var employees = await db.Database.SqlQuery<EmployeeCalcRow>($"""
            SELECT e.id AS "Id", e.employee_number AS "Number", e.first_name || ' ' || e.last_name AS "Name", e.status AS "Status",
                   e.hire_date AS "HireDate", e.termination_date AS "TerminationDate", e.payment_method AS "PaymentMethod",
                   (e.bank_account_encrypted IS NOT NULL) AS "HasBank", (e.mobile_money_encrypted IS NOT NULL) AS "HasMobile",
                   e.user_id AS "UserId", e.job_title AS "JobTitle", e.department_id AS "DepartmentId", d.name AS "DepartmentName"
            FROM nexora.employee e
            LEFT JOIN nexora.department d ON d.id = e.department_id
            WHERE e.company_id = {Company}
              AND e.hire_date <= {p.EndDate} AND (e.termination_date IS NULL OR e.termination_date >= {p.StartDate})
              AND (
                   ({offCycle} = false AND EXISTS (
                        SELECT 1 FROM nexora.employee_contract c
                        WHERE c.employee_id = e.id AND c.pay_frequency = {p.Frequency} AND c.status IN ('active','ended','terminated')
                          AND c.start_date <= {p.EndDate} AND (c.end_date IS NULL OR c.end_date >= {p.StartDate})))
                OR ({offCycle} = true AND cardinality({scope}::uuid[]) > 0 AND e.id = ANY({scope}))
                OR ({offCycle} = true AND cardinality({scope}::uuid[]) = 0 AND (
                        EXISTS (SELECT 1 FROM nexora.payroll_adjustment a WHERE a.employee_id = e.id AND a.status = 'approved')
                     OR EXISTS (SELECT 1 FROM nexora.employee_expense_claim c WHERE c.employee_id = e.id AND c.status = 'approved'
                                  AND c.settlement = 'payroll')
                     OR EXISTS (SELECT 1 FROM nexora.payroll_variable v WHERE v.employee_id = e.id AND v.period_id = {p.Id}
                                  AND v.status = 'approved'))))
            ORDER BY e.last_name, e.first_name
            """).ToListAsync(ct);
        if (employees.Count == 0) return [];
        var ids = employees.Select(e => e.Id).ToArray();

        var contracts = await db.Database.SqlQuery<ContractCalcRow>($"""
            SELECT c.id AS "Id", c.employee_id AS "EmployeeId", c.contract_type AS "ContractType", c.start_date AS "StartDate",
                   c.end_date AS "EndDate", c.pay_frequency AS "PayFrequency", c.pay_basis AS "PayBasis", c.weekly_hours AS "WeeklyHours",
                   cur.code AS "Currency"
            FROM nexora.employee_contract c JOIN nexora.currency cur ON cur.id = c.currency_id
            WHERE c.employee_id = ANY({ids}) AND c.status IN ('active','ended','terminated')
            """).ToListAsync(ct);
        var salaries = await db.Database.SqlQuery<SalaryCalcRow>($"""
            SELECT id AS "Id", employee_id AS "EmployeeId", contract_id AS "ContractId", effective_date AS "EffectiveDate",
                   pay_basis AS "PayBasis", amount AS "Amount", hourly_rate AS "HourlyRate"
            FROM nexora.salary_history WHERE employee_id = ANY({ids}) AND effective_date <= {p.EndDate}
            """).ToListAsync(ct);
        var components = (await db.Database.SqlQuery<ComponentCalcRow>($"""
            SELECT id AS "Id", code AS "Code", name AS "Name", kind AS "Kind", category AS "Category", taxable AS "Taxable",
                   subject_to_social AS "Social", in_gross AS "InGross", cash AS "Cash"
            FROM nexora.pay_component WHERE company_id = {Company} OR company_id IS NULL
            """).ToListAsync(ct)).ToDictionary(c => c.Id);
        CalcComponent Comp(Guid id)
        {
            var c = components[id];
            return new CalcComponent(c.Id, c.Code, c.Name, c.Kind, c.Category, c.Taxable, c.Social, c.InGross, c.Cash);
        }
        CalcComponent CompByCode(string code) => components.Values.Where(c => c.Code == code).Select(c => Comp(c.Id)).First();

        var elements = offCycle ? [] : await db.Database.SqlQuery<ElementCalcRow>($"""
            SELECT id AS "Id", employee_id AS "EmployeeId", component_id AS "ComponentId", calc_method AS "Method", amount AS "Amount",
                   rate AS "Rate", start_date AS "StartDate", end_date AS "EndDate"
            FROM nexora.employee_pay_element
            WHERE employee_id = ANY({ids}) AND start_date <= {p.EndDate} AND (end_date IS NULL OR end_date >= {p.StartDate})
            """).ToListAsync(ct);
        var variables = await db.Database.SqlQuery<VariableCalcRow>($"""
            SELECT id AS "Id", employee_id AS "EmployeeId", component_id AS "ComponentId", amount AS "Amount", description AS "Description",
                   status AS "Status"
            FROM nexora.payroll_variable
            WHERE period_id = {p.Id} AND employee_id = ANY({ids}) AND status IN ('approved','submitted') AND payroll_item_id IS NULL
            """).ToListAsync(ct);
        var leaves = offCycle ? [] : await db.Database.SqlQuery<LeaveCalcRow>($"""
            SELECT id AS "Id", employee_id AS "EmployeeId", leave_type AS "Type", start_date AS "StartDate", end_date AS "EndDate",
                   days AS "Days", paid AS "Paid", status AS "Status"
            FROM nexora.leave_record
            WHERE employee_id = ANY({ids}) AND status IN ('approved','requested') AND start_date <= {p.EndDate} AND end_date >= {p.StartDate}
            """).ToListAsync(ct);
        var attendance = offCycle ? [] : await db.Database.SqlQuery<AttendanceCalcRow>($"""
            SELECT employee_id AS "EmployeeId", work_date AS "WorkDate", status AS "Status", hours_worked AS "Hours",
                   justified AS "Justified", validation_status AS "ValidationStatus"
            FROM nexora.attendance_record WHERE employee_id = ANY({ids}) AND work_date BETWEEN {p.StartDate} AND {p.EndDate}
            """).ToListAsync(ct);
        var overtime = offCycle ? [] : await db.Database.SqlQuery<OvertimeCalcRow>($"""
            SELECT id AS "Id", employee_id AS "EmployeeId", work_date AS "WorkDate", hours AS "Hours", overtime_type AS "Type",
                   coefficient AS "Coefficient", status AS "Status"
            FROM nexora.overtime_record
            WHERE employee_id = ANY({ids}) AND work_date BETWEEN {p.StartDate} AND {p.EndDate} AND status IN ('approved','requested')
              AND payroll_item_id IS NULL
            """).ToListAsync(ct);
        var adjustments = await db.Database.SqlQuery<AdjustmentCalcRow>($"""
            SELECT a.id AS "Id", a.employee_id AS "EmployeeId", a.adjustment_number AS "Number", a.kind AS "Kind",
                   a.component_id AS "ComponentId", a.amount AS "Amount", a.taxable AS "Taxable", a.reason AS "Reason", cur.code AS "Currency"
            FROM nexora.payroll_adjustment a
            JOIN nexora.payroll_item i ON i.id = a.original_item_id
            JOIN nexora.currency cur ON cur.id = i.currency_id
            WHERE a.employee_id = ANY({ids}) AND a.status = 'approved' AND a.applied_item_id IS NULL
            """).ToListAsync(ct);
        var claims = await db.Database.SqlQuery<ClaimCalcRow>($"""
            SELECT c.id AS "Id", c.employee_id AS "EmployeeId", c.claim_number AS "Number", c.amount AS "Amount",
                   c.description AS "Description", cur.code AS "Currency"
            FROM nexora.employee_expense_claim c JOIN nexora.currency cur ON cur.id = c.currency_id
            WHERE c.employee_id = ANY({ids}) AND c.status = 'approved' AND c.settlement = 'payroll' AND c.payroll_item_id IS NULL
            """).ToListAsync(ct);
        var rules = (await db.Database.SqlQuery<RuleCalcRow>($"""
            SELECT id AS "Id", code AS "Code", name AS "Name", version AS "Version", rule_type AS "RuleType", base_type AS "BaseType",
                   method AS "Method", rate AS "Rate", fixed_amount AS "FixedAmount", brackets::text AS "Brackets", annualize AS "Annualize",
                   base_floor AS "BaseFloor", base_ceiling AS "BaseCeiling", min_amount AS "MinAmount", max_amount AS "MaxAmount"
            FROM nexora.payroll_rule
            WHERE company_id = {Company} AND status IN ('validated','retired') AND valid_from <= {p.EndDate}
              AND (valid_to IS NULL OR valid_to >= {p.EndDate})
            """).ToListAsync(ct)).Select(r => new CalcRule(r.Id, r.Code, r.Name, r.Version, r.RuleType, r.BaseType, r.Method, r.Rate,
                r.FixedAmount, SqlPayrollRuleService.ParseBrackets(r.Brackets).Select(b => new RuleBracket(b.From, b.To, b.Rate)).ToList(),
                r.Annualize, r.BaseFloor, r.BaseCeiling, r.MinAmount, r.MaxAmount)).ToList();
        var advances = offCycle ? [] : await db.Database.SqlQuery<AdvanceCalcRow>($"""
            SELECT a.id AS "Id", a.employee_id AS "EmployeeId", a.advance_number AS "Number", a.kind AS "Kind",
                   a.recovery_method AS "RecoveryMethod", a.balance AS "Balance",
                   COALESCE((SELECT sum(x.amount - x.recovered_amount) FROM nexora.advance_installment x
                             WHERE x.advance_id = a.id AND x.status IN ('pending','partial') AND x.due_date <= {p.EndDate}), 0) AS "DueNow",
                   a.recovery_percent AS "RecoveryPercent", cur.code AS "Currency"
            FROM nexora.employee_advance a JOIN nexora.currency cur ON cur.id = a.currency_id
            WHERE a.employee_id = ANY({ids}) AND a.status IN ('paid','in_repayment') AND a.balance > 0
            ORDER BY a.paid_at, a.advance_number
            """).ToListAsync(ct);
        var previous = (await db.Database.SqlQuery<PreviousNetRow>($"""
            SELECT DISTINCT ON (i.employee_id) i.employee_id AS "EmployeeId", i.net_amount AS "Net"
            FROM nexora.payroll_item i
            JOIN nexora.payroll_run r ON r.id = i.run_id
            JOIN nexora.payroll_period pp ON pp.id = i.period_id
            WHERE i.employee_id = ANY({ids}) AND r.status IN ('approved','paid','closed') AND pp.kind = 'regular'
              AND pp.end_date < {p.StartDate} AND i.status IN ('approved','paid')
            ORDER BY i.employee_id, pp.end_date DESC
            """).ToListAsync(ct)).ToDictionary(x => x.EmployeeId, x => x.Net);
        var duplicates = offCycle ? [] : (await db.Database.SqlQuery<DuplicateRow>($"""
            SELECT DISTINCT ON (i.employee_id) i.employee_id AS "EmployeeId", r.run_number AS "RunNumber"
            FROM nexora.payroll_item i
            JOIN nexora.payroll_run r ON r.id = i.run_id
            JOIN nexora.payroll_period pp ON pp.id = i.period_id
            WHERE i.employee_id = ANY({ids}) AND r.id <> {runId} AND r.status <> 'cancelled' AND pp.kind = 'regular'
              AND i.status NOT IN ('excluded','cancelled')
              AND daterange(pp.start_date, pp.end_date, '[]') && daterange({p.StartDate}, {p.EndDate}, '[]')
            """).ToListAsync(ct)).ToDictionary(x => x.EmployeeId, x => x.RunNumber);
        var commissionUsers = elements.Where(e => e.Method == "percent_of_sales").Select(e => e.EmployeeId).ToHashSet();
        var userIds = employees.Where(e => commissionUsers.Contains(e.Id) && e.UserId is not null).Select(e => e.UserId!.Value).ToArray();
        // Base de commission (§20) : ventes nettes hors taxes réalisées par l'employé sur la période, retours déduits.
        var sales = userIds.Length == 0 ? [] : (await db.Database.SqlQuery<SalesRow>($"""
            SELECT s.salesperson_user_id AS "UserId",
                   sum(s.total - s.tax_total) - COALESCE(sum((SELECT sum(sr.refund_total) * (s.total - s.tax_total) / NULLIF(s.total, 0)
                                                               FROM nexora.sale_return sr
                                                               WHERE sr.sale_id = s.id AND sr.status = 'completed'
                                                                 AND sr.returned_at::date BETWEEN {p.StartDate} AND {p.EndDate})), 0) AS "Amount"
            FROM nexora.sale s
            WHERE s.company_id = {Company} AND s.salesperson_user_id = ANY({userIds})
              AND s.status IN ('completed','partially_refunded','refunded') AND s.sold_at::date BETWEEN {p.StartDate} AND {p.EndDate}
            GROUP BY s.salesperson_user_id
            """).ToListAsync(ct)).ToDictionary(x => x.UserId, x => Math.Max(0m, Math.Round(x.Amount, 2)));
        var fallbackCurrency = await db.Database.SqlQuery<string>($"""
            SELECT cur.code AS "Value" FROM nexora.company c JOIN nexora.currency cur ON cur.id = c.default_currency_id WHERE c.id = {Company}
            """).FirstAsync(ct);

        var results = new List<(EmployeeCalcRow, PayrollCalcResult)>();
        foreach (var e in employees)
        {
            var empContracts = contracts.Where(c => c.EmployeeId == e.Id)
                .Select(c => new CalcContract(c.Id, c.ContractType, c.StartDate, c.EndDate, c.PayFrequency, c.PayBasis, c.WeeklyHours, c.Currency))
                .ToList();
            var currency = empContracts.Where(c => offCycle || c.PayFrequency == p.Frequency).OrderBy(c => c.Start).LastOrDefault()?.Currency
                           ?? empContracts.OrderBy(c => c.Start).LastOrDefault()?.Currency ?? fallbackCurrency;
            var input = new PayrollCalcInput(
                new CalcEmployee(e.Id, e.Number, e.Name, e.Status, e.HireDate, e.TerminationDate, e.PaymentMethod, e.HasBank, e.HasMobile),
                new CalcPeriod(p.StartDate, p.EndDate, p.Frequency, p.Kind),
                fallbackCurrency,
                empContracts,
                salaries.Where(s => s.EmployeeId == e.Id)
                    .Select(s => new CalcSalary(s.Id, s.ContractId, s.EffectiveDate, s.PayBasis, s.Amount, s.HourlyRate)).ToList(),
                elements.Where(x => x.EmployeeId == e.Id)
                    .Select(x => new CalcPayElement(x.Id, Comp(x.ComponentId), x.Method, x.Amount, x.Rate, x.StartDate, x.EndDate)).ToList(),
                variables.Where(v => v.EmployeeId == e.Id && v.Status == "approved")
                    .Select(v => new CalcVariable(v.Id, Comp(v.ComponentId), v.Amount, v.Description)).ToList(),
                leaves.Where(l => l.EmployeeId == e.Id)
                    .Select(l => new CalcLeave(l.Id, l.Type, l.StartDate, l.EndDate, l.Days, l.Paid, l.Status)).ToList(),
                attendance.Where(a => a.EmployeeId == e.Id)
                    .Select(a => new CalcAttendance(a.WorkDate, a.Status, a.Hours, a.Justified, a.ValidationStatus)).ToList(),
                overtime.Where(o => o.EmployeeId == e.Id)
                    .Select(o => new CalcOvertime(o.Id, o.WorkDate, o.Hours, o.Type, o.Coefficient, o.Status)).ToList(),
                adjustments.Where(a => a.EmployeeId == e.Id && a.Currency == currency)
                    .Select(a => new CalcAdjustment(a.Id, a.Number, a.Kind,
                        a.ComponentId is { } cid ? Comp(cid) : CompByCode(a.Kind == "earning" ? PayCodes.AdjustmentEarning : PayCodes.AdjustmentDeduction),
                        a.Amount, a.Taxable, a.Reason)).ToList(),
                claims.Where(c => c.EmployeeId == e.Id && c.Currency == currency)
                    .Select(c => new CalcExpenseClaim(c.Id, c.Number, c.Amount, c.Description)).ToList(),
                rules,
                advances.Where(a => a.EmployeeId == e.Id && a.Currency == currency)
                    .Select(a => new CalcAdvance(a.Id, a.Number, a.Kind, a.RecoveryMethod, a.Balance, a.DueNow, a.RecoveryPercent)).ToList(),
                cfg.Calc,
                e.UserId is { } uid && sales.TryGetValue(uid, out var sold) ? sold : 0m,
                previous.TryGetValue(e.Id, out var prev) ? prev : null,
                variables.Count(v => v.EmployeeId == e.Id && v.Status == "submitted"),
                duplicates.TryGetValue(e.Id, out var dup) ? dup : null);
            results.Add((e, PayrollCalculator.Calculate(input)));
        }
        return results;
    }

    private async Task PersistItemAsync(Guid runId, Guid periodId, EmployeeCalcRow e, PayrollCalcResult r,
        IReadOnlyDictionary<string, Guid> systemComponents, string? excludedReason, CancellationToken ct)
    {
        var itemId = Guid.NewGuid();
        var currencyId = await db.Database.SqlQuery<Guid>($"""SELECT id AS "Value" FROM nexora.currency WHERE code = {r.Currency}""").FirstAsync(ct);
        var anomalies = JsonSerializer.Serialize(r.Anomalies.Select(a => new AnomalyDto(a.Code, a.Severity, a.Message)), Json);
        var trace = JsonSerializer.Serialize(r.Trace.Select(t => new TraceStepDto(t.Step, t.Detail, t.Amount)), Json);
        var rulesSnapshot = JsonSerializer.Serialize(r.Deductions.Where(d => d.RuleId is not null)
            .Select(d => new { ruleId = d.RuleId, code = d.Code, version = d.RuleVersion }), Json);
        var status = excludedReason is null ? "calculated" : "excluded";
        await db.Database.ExecuteSqlAsync($"""
            INSERT INTO nexora.payroll_item
                (id, company_id, run_id, period_id, employee_id, contract_id, salary_history_id, currency_id, employee_number, employee_name,
                 job_title, department_id, department_name, contract_type, pay_basis, pay_frequency, payment_method, base_salary, period_days,
                 days_employed, absence_days, prorata, daily_rate, hourly_rate, base_amount, gross_amount, reimbursement_amount, taxable_base,
                 social_base, legal_deductions, absence_deduction, benefit_offset, advance_recovery, loan_recovery, other_deductions,
                 total_deductions, net_amount, employer_contributions, employer_cost, status, excluded_reason, anomalies, blocking_count,
                 warning_count, trace, rules_snapshot)
            VALUES ({itemId}, {Company}, {runId}, {periodId}, {e.Id}, {r.ContractId}, {r.SalaryId}, {currencyId}, {e.Number}, {e.Name},
                    {e.JobTitle}, {e.DepartmentId}, {e.DepartmentName}, {r.ContractType}, {r.PayBasis}, {r.PayFrequency}, {e.PaymentMethod},
                    {r.BaseSalary}, {r.PeriodDays}, {r.DaysEmployed}, {r.AbsenceDays}, {r.Prorata}, {r.DailyRate}, {r.HourlyRate}, {r.BaseAmount},
                    {r.Gross}, {r.Reimbursements}, {r.TaxableBase}, {r.SocialBase}, {r.LegalDeductions}, {r.AbsenceDeduction}, {r.BenefitOffset},
                    {r.AdvanceRecovery}, {r.LoanRecovery}, {r.OtherDeductions}, {r.TotalDeductions}, {r.Net}, {r.EmployerContributions},
                    {r.EmployerCost}, {status}, {excludedReason}, {anomalies}::jsonb, {r.BlockingCount}, {r.WarningCount}, {trace}::jsonb,
                    {rulesSnapshot}::jsonb)
            """, ct);

        Guid? Sys(string code) => systemComponents.TryGetValue(code, out var id) ? id : null;
        var earnings = JsonSerializer.Serialize(r.Earnings.Select((x, i) => new
        {
            component_id = x.ComponentId ?? Sys(x.Code), code = x.Code, category = x.Category, label = x.Label, quantity = x.Quantity,
            rate = x.Rate, base_amount = x.BaseAmount, amount = x.Amount, taxable = x.Taxable, subject_to_social = x.Social,
            in_gross = x.InGross, cash = x.Cash, source_type = x.SourceType, source_id = x.SourceId, sort_order = i + 1,
        }));
        var deductions = JsonSerializer.Serialize(r.Deductions.Select((x, i) => new
        {
            component_id = x.ComponentId ?? Sys(x.Code), code = x.Code, category = x.Category, borne_by = x.BorneBy, label = x.Label,
            base_amount = x.BaseAmount, rate = x.Rate, planned_amount = x.PlannedAmount, amount = x.Amount, source_type = x.SourceType,
            source_id = x.SourceId, rule_id = x.RuleId, rule_version = x.RuleVersion, sort_order = i + 1,
        }));
        await db.Database.ExecuteSqlAsync($"""
            INSERT INTO nexora.payroll_earning
                (company_id, payroll_item_id, component_id, code, category, label, quantity, rate, base_amount, amount, taxable,
                 subject_to_social, in_gross, cash, source_type, source_id, sort_order)
            SELECT {Company}, {itemId}, x.component_id, x.code, x.category, x.label, x.quantity, x.rate, x.base_amount, x.amount, x.taxable,
                   x.subject_to_social, x.in_gross, x.cash, x.source_type, x.source_id, x.sort_order
            FROM jsonb_to_recordset({earnings}::jsonb) AS x(component_id uuid, code text, category text, label text, quantity numeric,
                 rate numeric, base_amount numeric, amount numeric, taxable boolean, subject_to_social boolean, in_gross boolean, cash boolean,
                 source_type text, source_id uuid, sort_order int)
            """, ct);
        await db.Database.ExecuteSqlAsync($"""
            INSERT INTO nexora.payroll_deduction
                (company_id, payroll_item_id, component_id, code, category, borne_by, label, base_amount, rate, planned_amount, amount,
                 source_type, source_id, rule_id, rule_version, sort_order)
            SELECT {Company}, {itemId}, x.component_id, x.code, x.category, x.borne_by, x.label, x.base_amount, x.rate, x.planned_amount,
                   x.amount, x.source_type, x.source_id, x.rule_id, x.rule_version, x.sort_order
            FROM jsonb_to_recordset({deductions}::jsonb) AS x(component_id uuid, code text, category text, borne_by text, label text,
                 base_amount numeric, rate numeric, planned_amount numeric, amount numeric, source_type text, source_id uuid, rule_id uuid,
                 rule_version int, sort_order int)
            """, ct);
    }
}
