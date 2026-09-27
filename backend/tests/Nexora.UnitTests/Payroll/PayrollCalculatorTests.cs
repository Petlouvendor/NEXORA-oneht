using FluentAssertions;
using NSubstitute;
using Nexora.Application.Finance.Abstractions;
using Nexora.Application.Finance.Events;
using Nexora.Application.Finance.Posting;
using Nexora.Application.Payroll;
using Nexora.Domain.Accounting;
using Nexora.Domain.Common;
using Nexora.Infrastructure.Payroll;

namespace Nexora.UnitTests.Payroll;

/// <summary>
/// Moteur de paie pur (docs/module-paie-rh-avances.md §10, §11, §15) : chaque montant attendu est recalculé à la main
/// dans le test. Période de référence : septembre 2026 (30 jours), base journalière 30, 48 h hebdomadaires.
/// </summary>
public sealed class PayrollCalculatorTests
{
    private static readonly DateOnly Sep1 = new(2026, 9, 1);
    private static readonly DateOnly Sep30 = new(2026, 9, 30);
    private static readonly Guid ContractId = Guid.NewGuid();

    private static CalcComponent Earning(string code, string category, bool taxable = true, bool cash = true) =>
        new(Guid.NewGuid(), code, code, "earning", category, taxable, taxable, true, cash);

    private static PayrollCalcInput Input(
        decimal salary = 60_000m, DateOnly? hire = null, IReadOnlyList<CalcSalary>? salaries = null,
        IReadOnlyList<CalcLeave>? leaves = null, IReadOnlyList<CalcOvertime>? overtime = null, IReadOnlyList<CalcVariable>? variables = null,
        IReadOnlyList<CalcRule>? rules = null, IReadOnlyList<CalcAdvance>? advances = null, PayrollSettings? settings = null,
        IReadOnlyList<CalcAttendance>? attendance = null, decimal? previousNet = null)
    {
        var start = hire ?? new DateOnly(2026, 1, 1);
        return new PayrollCalcInput(
            new CalcEmployee(Guid.NewGuid(), "EMP-1", "Marie Joseph", "active", start, null, "cash", false, false),
            new CalcPeriod(Sep1, Sep30, "monthly", "regular"),
            "HTG",
            [new CalcContract(ContractId, "permanent", start, null, "monthly", "monthly", 48m, "HTG")],
            salaries ?? [new CalcSalary(Guid.NewGuid(), ContractId, start, "monthly", salary, null)],
            [], variables ?? [], leaves ?? [], attendance ?? [], overtime ?? [], [], [], rules ?? [], advances ?? [],
            settings ?? PayrollSettings.Default, PreviousNet: previousNet);
    }

    [Fact]
    public void Full_month_pays_the_contract_salary_and_flags_missing_legal_rules()
    {
        var r = PayrollCalculator.Calculate(Input());

        r.BaseAmount.Should().Be(60_000m);
        r.Gross.Should().Be(60_000m);
        r.Net.Should().Be(60_000m);
        r.Anomalies.Should().ContainSingle(a => a.Code == "no_legal_rules" && a.Severity == "warning");
        r.BlockingCount.Should().Be(0);
    }

    [Fact]
    public void Hire_in_the_middle_of_the_month_prorates_the_base_salary()
    {
        // Entrée le 16 : 15 jours sur 30 → 60 000 × 15/30 = 30 000.
        var r = PayrollCalculator.Calculate(Input(hire: new DateOnly(2026, 9, 16)));

        r.DaysEmployed.Should().Be(15);
        r.BaseAmount.Should().Be(30_000m);
        r.Prorata.Should().Be(0.5m);
    }

    [Fact]
    public void Salary_change_in_the_period_is_applied_from_its_effective_date()
    {
        // 60 000 du 1er au 15, 66 000 à partir du 16 : 30 000 + 33 000 = 63 000, deux lignes explicables.
        var salaries = new List<CalcSalary>
        {
            new(Guid.NewGuid(), ContractId, new DateOnly(2026, 1, 1), "monthly", 60_000m, null),
            new(Guid.NewGuid(), ContractId, new DateOnly(2026, 9, 16), "monthly", 66_000m, null),
        };
        var r = PayrollCalculator.Calculate(Input(salaries: salaries));

        r.BaseAmount.Should().Be(63_000m);
        r.Earnings.Where(e => e.Category == "base").Select(e => e.Amount).Should().Equal(30_000m, 33_000m);
    }

    [Fact]
    public void Unpaid_absence_bonus_and_validated_overtime_compose_gross_and_net()
    {
        var bonus = Earning("BONUS", "bonus");
        var r = PayrollCalculator.Calculate(Input(
            leaves: [new CalcLeave(Guid.NewGuid(), "unpaid", new DateOnly(2026, 9, 10), new DateOnly(2026, 9, 11), 2m, false, "approved")],
            overtime: [new CalcOvertime(Guid.NewGuid(), new DateOnly(2026, 9, 20), 10m, "standard", 1.5m, "approved"),
                       new CalcOvertime(Guid.NewGuid(), new DateOnly(2026, 9, 21), 5m, "standard", 1.5m, "requested")],
            variables: [new CalcVariable(Guid.NewGuid(), bonus, 25_000m, "Prime fixe")]));

        // Taux horaire 60 000 × 12 / 52 / 48 = 288,4615 ; 10 h × 1,5 → 4 326,92. Absence 2 j × 2 000 = 4 000.
        r.HourlyRate.Should().Be(288.4615m);
        r.Earnings.Single(e => e.Category == "overtime").Amount.Should().Be(4_326.92m);
        r.AbsenceDeduction.Should().Be(4_000m);
        r.Gross.Should().Be(89_326.92m);
        r.Net.Should().Be(85_326.92m);
        r.Anomalies.Should().Contain(a => a.Code == "pending_overtime");
        r.UsedOvertime.Should().HaveCount(1, "une heure non validée n'est jamais payée (§19)");
    }

    [Fact]
    public void Validated_unjustified_absence_from_attendance_is_deducted_pending_one_is_only_flagged()
    {
        var r = PayrollCalculator.Calculate(Input(attendance:
        [
            new CalcAttendance(new DateOnly(2026, 9, 3), "absent", null, false, "validated"),
            new CalcAttendance(new DateOnly(2026, 9, 4), "half_day", null, false, "validated"),
            new CalcAttendance(new DateOnly(2026, 9, 7), "absent", null, false, "pending"),
            new CalcAttendance(new DateOnly(2026, 9, 8), "absent", null, true, "validated"),
        ]));

        r.AbsenceDays.Should().Be(1.5m);
        r.AbsenceDeduction.Should().Be(3_000m);
        r.Anomalies.Should().Contain(a => a.Code == "pending_absences");
    }

    [Fact]
    public void Legal_rules_split_employee_and_employer_parts_and_progressive_tax_is_annualised()
    {
        var rules = new List<CalcRule>
        {
            new(Guid.NewGuid(), "SOC_E", "Cotisation salariale", 1, "employee_contribution", "social", "percentage", 6m, null, [], false, null, null, null, null),
            new(Guid.NewGuid(), "SOC_R", "Cotisation patronale", 1, "employer_contribution", "social", "percentage", 6m, null, [], false, null, null, null, null),
            new(Guid.NewGuid(), "IRI", "Impôt sur le revenu", 2, "income_tax", "taxable_after_social", "progressive", null, null,
                [new RuleBracket(0m, 60_000m, 0m), new RuleBracket(60_000m, 240_000m, 10m), new RuleBracket(240_000m, 480_000m, 15m),
                 new RuleBracket(480_000m, null, 25m)], true, null, null, null, null),
        };
        var r = PayrollCalculator.Calculate(Input(salary: 50_000m, rules: rules));

        // Social 6 % × 50 000 = 3 000 (salarié) et 3 000 (patronal, hors net). Base IRI = 50 000 − 3 000 = 47 000 → 564 000 / an :
        // 18 000 + 36 000 + 21 000 = 75 000 / an → 6 250 / mois. Net = 50 000 − 3 000 − 6 250 = 40 750.
        r.LegalDeductions.Should().Be(9_250m);
        r.EmployerContributions.Should().Be(3_000m);
        r.Net.Should().Be(40_750m);
        r.EmployerCost.Should().Be(53_000m);
        r.Deductions.Single(d => d.Code == "IRI").RuleVersion.Should().Be(2);
        r.Deductions.Where(d => d.Category == "employer").Should().OnlyContain(d => d.BorneBy == "employer");
        r.Anomalies.Should().NotContain(a => a.Code == "no_legal_rules");
    }

    [Fact]
    public void Advance_recovery_follows_the_due_installment_and_never_exceeds_the_balance()
    {
        var r = PayrollCalculator.Calculate(Input(advances:
        [
            new CalcAdvance(Guid.NewGuid(), "AVS-1", "salary_advance", "installments", 20_000m, 10_000m, null),
            new CalcAdvance(Guid.NewGuid(), "AVS-2", "salary_advance", "next_payroll", 3_000m, 5_000m, null),
        ]));

        r.AdvanceRecovery.Should().Be(13_000m, "10 000 d'échéance + 3 000 plafonnés au solde (jamais 5 000)");
        r.Deductions.Where(d => d.Category == "advance").Select(d => d.Amount).Should().Equal(10_000m, 3_000m);
        r.Net.Should().Be(47_000m);
    }

    [Fact]
    public void Recoveries_are_capped_to_preserve_the_minimum_net_and_the_rest_stays_due()
    {
        var settings = PayrollSettings.Default with { MinNetAmount = 45_000m };
        var r = PayrollCalculator.Calculate(Input(settings: settings, advances:
        [
            new CalcAdvance(Guid.NewGuid(), "PRT-1", "loan", "installments", 50_000m, 20_000m, null),
        ]));

        r.LoanRecovery.Should().Be(15_000m);
        r.Net.Should().Be(45_000m);
        r.Deductions.Single(d => d.Category == "loan").PlannedAmount.Should().Be(20_000m);
        r.Anomalies.Should().Contain(a => a.Code == "advance_capped" && a.Severity == "warning");
    }

    [Fact]
    public void Percent_of_net_recovery_uses_the_net_before_recoveries()
    {
        var r = PayrollCalculator.Calculate(Input(advances:
        [
            new CalcAdvance(Guid.NewGuid(), "AVS-3", "salary_advance", "percent_of_net", 30_000m, 0m, 25m),
        ]));

        r.AdvanceRecovery.Should().Be(15_000m);
        r.Net.Should().Be(45_000m);
    }

    [Fact]
    public void Deductions_above_gross_are_a_blocking_negative_net_anomaly()
    {
        var rules = new List<CalcRule>
        {
            new(Guid.NewGuid(), "FIX", "Retenue fixe", 1, "income_tax", "gross", "fixed", null, 70_000m, [], false, null, null, null, null),
        };
        var r = PayrollCalculator.Calculate(Input(rules: rules));

        r.Net.Should().BeNegative();
        r.Anomalies.Should().Contain(a => a.Code == "negative_net" && a.Severity == "blocking");
    }

    [Fact]
    public void Benefit_in_kind_is_taxed_but_not_paid_in_cash()
    {
        var housing = Earning("BENEFIT_KIND", "benefit_in_kind", taxable: true, cash: false);
        var r = PayrollCalculator.Calculate(Input(variables: [new CalcVariable(Guid.NewGuid(), housing, 10_000m, "Logement de fonction")]));

        r.Gross.Should().Be(70_000m);
        r.TaxableBase.Should().Be(70_000m);
        r.BenefitOffset.Should().Be(10_000m);
        r.Net.Should().Be(60_000m);
    }

    [Fact]
    public void Contract_without_salary_is_blocking_and_unusual_variance_is_flagged()
    {
        var noSalary = PayrollCalculator.Calculate(Input(salaries: []));
        noSalary.Anomalies.Should().Contain(a => a.Code == "no_salary" && a.Severity == "blocking");

        var variance = PayrollCalculator.Calculate(Input(previousNet: 40_000m));
        variance.Anomalies.Should().Contain(a => a.Code == "net_variance");
    }

    [Fact]
    public void Progressive_brackets_are_applied_slice_by_slice()
    {
        var brackets = new List<RuleBracket> { new(0m, 100m, 0m), new(100m, 200m, 10m), new(200m, null, 20m) };
        PayrollCalculator.Progressive(50m, brackets).Should().Be(0m);
        PayrollCalculator.Progressive(150m, brackets).Should().Be(5m);
        PayrollCalculator.Progressive(300m, brackets).Should().Be(30m);
    }

    // ------------------------------------------------------------------ Règles d'avance (§13.2, §15, §16)

    private static readonly AdvancePolicySettings Policy = new(50m, true, 3, 100m, 300m, 80m);

    [Fact]
    public void Advance_policy_checks_salary_share_multiple_advances_and_debt_ceiling()
    {
        var position = new EmployeeDebtPosition(60_000m, 1, 20_000m, 0m, 0);
        AdvanceRules.Evaluate("salary_advance", 20_000m, position, Policy, true, true)
            .Where(c => c.Blocking).Should().OnlyContain(c => c.Passed);

        var tooBig = AdvanceRules.Evaluate("salary_advance", 35_000m, position, Policy, true, true);
        tooBig.Single(c => c.Code == "max_percent_of_salary").Passed.Should().BeFalse();

        var overCeiling = AdvanceRules.Evaluate("salary_advance", 30_000m, new EmployeeDebtPosition(60_000m, 2, 45_000m, 0m, 0), Policy, true, true);
        overCeiling.Single(c => c.Code == "debt_ceiling").Passed.Should().BeFalse();

        var single = AdvanceRules.Evaluate("salary_advance", 1_000m, position, Policy with { AllowMultiple = false }, true, true);
        single.Single(c => c.Code == "multiple_advances").Passed.Should().BeFalse();

        var near = AdvanceRules.Evaluate("salary_advance", 30_000m, position, Policy, true, true);
        near.Should().Contain(c => c.Code == "near_limit" && !c.Blocking);

        AdvanceRules.Evaluate("salary_advance", 1_000m, position, Policy, false, true)
            .Single(c => c.Code == "employee_active").Passed.Should().BeFalse();
    }

    [Fact]
    public void Schedules_split_evenly_with_the_rounding_on_the_last_installment()
    {
        AdvanceRules.BuildSchedule(20_000m, "installments", 2, new DateOnly(2026, 9, 15), "monthly")
            .Should().Equal((1, new DateOnly(2026, 9, 15), 10_000m), (2, new DateOnly(2026, 10, 15), 10_000m));
        AdvanceRules.BuildSchedule(10_000m, "installments", 3, new DateOnly(2026, 9, 7), "weekly").Select(x => x.Amount)
            .Should().Equal(3_333.33m, 3_333.33m, 3_333.34m);
        AdvanceRules.BuildSchedule(5_000m, "percent_of_net", 1, new DateOnly(2026, 9, 7), "monthly").Should().BeEmpty();

        AdvanceRules.ValidateReschedule(10_000m, [(new DateOnly(2026, 11, 15), 4_000m), (new DateOnly(2026, 12, 15), 6_000m)]).Should().BeNull();
        AdvanceRules.ValidateReschedule(10_000m, [(new DateOnly(2026, 11, 15), 4_000m)]).Should().NotBeNull();
    }

    [Fact]
    public void Grouped_payment_instructions_are_deterministic_per_line()
    {
        var op = Guid.NewGuid();
        var item = Guid.NewGuid();
        PayrollRules.Derive(op, item).Should().Be(PayrollRules.Derive(op, item));
        PayrollRules.Derive(op, item).Should().NotBe(PayrollRules.Derive(op, Guid.NewGuid()));
    }

    // ------------------------------------------------------------------ Écriture de paie (§32)

    [Fact]
    public async Task Payroll_posting_is_balanced_and_an_advance_recovery_credits_the_advance_account_not_an_expense()
    {
        var accounts = Substitute.For<IAccountResolver>();
        var ids = new Dictionary<string, Guid>();
        accounts.ResolveAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(ci => ids.TryGetValue(ci.ArgAt<string>(0), out var id) ? id : ids[ci.ArgAt<string>(0)] = Guid.NewGuid());
        var ctx = new PostingContext(accounts, Substitute.For<IExchangeRateProvider>(), "HTG", 2);
        var employee = Guid.NewGuid();
        var evt = new PayrollApproved(Guid.NewGuid(), Guid.NewGuid(), DateTimeOffset.UnixEpoch, Guid.NewGuid(), "PAY-1", "Paie septembre 2026",
            [new PayrollEmployeePosting(employee, "EMP-1 Marie", "HTG", 60_000m, 0m, 3_000m, 3_000m, 10_000m, 0m, 0m, 47_000m)]);

        var draft = await new PostingRuleSet().BuildAsync(evt, ctx);

        draft.IsBalanced.Should().BeTrue();
        draft.JournalCode.Should().Be("PA");
        draft.Lines.Single(l => l.AccountId == ids[AccountKeys.EmployeeAdvances]).Credit.Should().Be(10_000m);
        draft.Lines.Single(l => l.AccountId == ids[AccountKeys.SalaryExpense]).Debit.Should().Be(60_000m);
        draft.Lines.Single(l => l.AccountId == ids[AccountKeys.SalariesPayable]).Should()
            .Match<DraftLine>(l => l.Credit == 47_000m && l.ThirdPartyType == "employee" && l.ThirdPartyId == employee);
        draft.Lines.Single(l => l.AccountId == ids[AccountKeys.PayrollWithholdings]).Credit.Should().Be(6_000m);
    }
}
