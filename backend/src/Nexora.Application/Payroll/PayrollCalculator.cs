using System.Globalization;

namespace Nexora.Application.Payroll;

// =============================================================================================
// Moteur de calcul de paie (docs/module-paie-rh-avances.md §10, §11, §15, §23).
// Fonction PURE : mêmes entrées → même bulletin, sans base ni horloge. Chaque composant est une ligne
// séparée (gain ou retenue) avec sa source, sa règle et son explication : le calcul est explicable
// (§11 « séparer les composants », §40 « comprendre chaque élément du calcul »). L'IA n'y intervient pas.
// =============================================================================================

/// <summary>Paramètres de paie de l'entreprise (V076), résolus avant le calcul.</summary>
public sealed record PayrollSettings(
    string DayCountBasis,
    decimal DaysPerMonth,
    IReadOnlySet<int> WorkingWeekDays,
    decimal DefaultWeeklyHours,
    decimal MaxRecoveryPercentOfNet,
    decimal MinNetAmount,
    decimal VarianceAlertPercent,
    decimal OvertimeAlertHours,
    int Decimals = 2)
{
    public static PayrollSettings Default { get; } = new("calendar", 30m, new HashSet<int> { 1, 2, 3, 4, 5, 6 }, 48m, 100m, 0m, 20m, 40m);
}

public sealed record CalcPeriod(DateOnly Start, DateOnly End, string Frequency, string Kind);

public sealed record CalcEmployee(
    Guid Id, string Number, string Name, string Status, DateOnly HireDate, DateOnly? TerminationDate,
    string PaymentMethod, bool HasBankAccount, bool HasMobileMoney);

public sealed record CalcContract(
    Guid Id, string ContractType, DateOnly Start, DateOnly? End, string PayFrequency, string PayBasis, decimal WeeklyHours, string Currency);

public sealed record CalcSalary(Guid Id, Guid ContractId, DateOnly EffectiveDate, string PayBasis, decimal Amount, decimal? HourlyRate);

/// <summary>Composant de paie. Pour une retenue, <c>Taxable</c>/<c>Social</c> = vient en déduction des assiettes.</summary>
public sealed record CalcComponent(
    Guid? Id, string Code, string Name, string Kind, string Category, bool Taxable, bool Social, bool InGross, bool Cash);

public sealed record CalcPayElement(Guid Id, CalcComponent Component, string Method, decimal? Amount, decimal? Rate, DateOnly Start, DateOnly? End);

public sealed record CalcVariable(Guid Id, CalcComponent Component, decimal Amount, string Description);

public sealed record CalcLeave(Guid Id, string Type, DateOnly Start, DateOnly End, decimal Days, bool Paid, string Status);

public sealed record CalcAttendance(DateOnly Date, string Status, decimal? Hours, bool Justified, string ValidationStatus);

public sealed record CalcOvertime(Guid Id, DateOnly Date, decimal Hours, string Type, decimal Coefficient, string Status);

public sealed record CalcAdjustment(Guid Id, string Number, string Kind, CalcComponent Component, decimal Amount, bool Taxable, string Reason);

public sealed record CalcExpenseClaim(Guid Id, string Number, decimal Amount, string Description);

public sealed record RuleBracket(decimal From, decimal? To, decimal Rate);

public sealed record CalcRule(
    Guid Id, string Code, string Name, int Version, string RuleType, string BaseType, string Method,
    decimal? Rate, decimal? FixedAmount, IReadOnlyList<RuleBracket> Brackets, bool Annualize,
    decimal? BaseFloor, decimal? BaseCeiling, decimal? MinAmount, decimal? MaxAmount);

/// <summary>Avance / acompte / prêt en cours. <c>DueNow</c> = échéances ouvertes dont la date tombe au plus tard
/// à la fin de la période (retards compris).</summary>
public sealed record CalcAdvance(
    Guid Id, string Number, string Kind, string RecoveryMethod, decimal Balance, decimal DueNow, decimal? RecoveryPercent);

public sealed record PayrollCalcInput(
    CalcEmployee Employee,
    CalcPeriod Period,
    string FallbackCurrency,
    IReadOnlyList<CalcContract> Contracts,
    IReadOnlyList<CalcSalary> Salaries,
    IReadOnlyList<CalcPayElement> Elements,
    IReadOnlyList<CalcVariable> Variables,
    IReadOnlyList<CalcLeave> Leaves,
    IReadOnlyList<CalcAttendance> Attendance,
    IReadOnlyList<CalcOvertime> Overtime,
    IReadOnlyList<CalcAdjustment> Adjustments,
    IReadOnlyList<CalcExpenseClaim> ExpenseClaims,
    IReadOnlyList<CalcRule> Rules,
    IReadOnlyList<CalcAdvance> Advances,
    PayrollSettings Settings,
    decimal SalesForCommission = 0m,
    decimal? PreviousNet = null,
    int PendingVariables = 0,
    string? DuplicateRunNumber = null);

public sealed record CalcEarning(
    string Code, Guid? ComponentId, string Category, string Label, decimal? Quantity, decimal? Rate, decimal? BaseAmount,
    decimal Amount, bool Taxable, bool Social, bool InGross, bool Cash, string SourceType, Guid? SourceId);

public sealed record CalcDeduction(
    string Code, Guid? ComponentId, string Category, string BorneBy, string Label, decimal? BaseAmount, decimal? Rate,
    decimal? PlannedAmount, decimal Amount, string SourceType, Guid? SourceId, Guid? RuleId, int? RuleVersion);

public sealed record CalcAnomaly(string Code, string Severity, string Message);

public sealed record CalcTraceStep(string Step, string Detail, decimal? Amount);

public sealed record PayrollCalcResult(
    Guid? ContractId, Guid? SalaryId, string Currency, string? ContractType, string? PayBasis, string? PayFrequency,
    decimal BaseSalary, int PeriodDays, decimal DaysEmployed, decimal AbsenceDays, decimal Prorata, decimal DailyRate, decimal HourlyRate,
    decimal BaseAmount, decimal Gross, decimal Reimbursements, decimal TaxableBase, decimal SocialBase,
    decimal LegalDeductions, decimal AbsenceDeduction, decimal BenefitOffset, decimal AdvanceRecovery, decimal LoanRecovery,
    decimal OtherDeductions, decimal TotalDeductions, decimal Net, decimal EmployerContributions, decimal EmployerCost,
    IReadOnlyList<CalcEarning> Earnings, IReadOnlyList<CalcDeduction> Deductions, IReadOnlyList<CalcAnomaly> Anomalies,
    IReadOnlyList<CalcTraceStep> Trace, IReadOnlyList<Guid> UsedOvertime, IReadOnlyList<Guid> UsedVariables,
    IReadOnlyList<Guid> UsedAdjustments, IReadOnlyList<Guid> UsedExpenseClaims)
{
    public int BlockingCount => Anomalies.Count(a => a.Severity == PayrollAnomalies.Blocking);
    public int WarningCount => Anomalies.Count(a => a.Severity == PayrollAnomalies.Warning);
}

public static class PayrollAnomalies
{
    public const string Blocking = "blocking";
    public const string Warning = "warning";
}

/// <summary>Codes des composants système (V076) utilisés par le moteur.</summary>
public static class PayCodes
{
    public const string Base = "BASE";
    public const string Overtime = "OVERTIME";
    public const string Absence = "ABSENCE";
    public const string Advance = "ADVANCE";
    public const string Loan = "LOAN";
    public const string Reimbursement = "REIMBURSEMENT";
    public const string AdjustmentEarning = "ADJ_EARNING";
    public const string AdjustmentDeduction = "ADJ_DEDUCTION";
    public const string BenefitOffset = "BENEFIT_OFFSET";
}

public static class PayrollCalculator
{
    private static readonly CultureInfo Fr = CultureInfo.GetCultureInfo("fr-FR");

    public static int PeriodsPerYear(string frequency) => frequency switch
    {
        "weekly" => 52,
        "biweekly" => 26,
        _ => 12,
    };

    /// <summary>Part mensuelle ramenée à la période : 1 (mensuel), 12/26 (bihebdomadaire), 12/52 (hebdomadaire).</summary>
    public static decimal MonthlyToPeriodFactor(string frequency) => frequency switch
    {
        "weekly" => 12m / 52m,
        "biweekly" => 12m / 26m,
        _ => 1m,
    };

    public static PayrollCalcResult Calculate(PayrollCalcInput input)
    {
        var s = input.Settings;
        var p = input.Period;
        var e = input.Employee;
        decimal R(decimal v) => Math.Round(v, s.Decimals, MidpointRounding.AwayFromZero);
        string M(decimal v) => v.ToString("N2", Fr);
        string D(DateOnly d) => d.ToString("dd/MM/yyyy", Fr);

        var anomalies = new List<CalcAnomaly>();
        var trace = new List<CalcTraceStep>();
        var earnings = new List<CalcEarning>();
        var deductions = new List<CalcDeduction>();
        void Block(string code, string message) => anomalies.Add(new(code, PayrollAnomalies.Blocking, message));
        void Warn(string code, string message) => anomalies.Add(new(code, PayrollAnomalies.Warning, message));

        var periodDays = p.End.DayNumber - p.Start.DayNumber + 1;
        var offCycle = p.Kind == "off_cycle";

        if (input.DuplicateRunNumber is { } dup)
            Block("duplicate_employee", $"L'employé figure déjà dans la paie {dup} pour une période qui chevauche celle-ci.");
        if (e.Status == "suspended")
            Warn("employee_suspended", "Employé suspendu : vérifier que la période doit être rémunérée.");

        // ---- 1. Fenêtre d'emploi et contrats de la période -----------------------------------------------
        var windowStart = Max(p.Start, e.HireDate);
        var windowEnd = e.TerminationDate is { } td ? Min(p.End, td) : p.End;
        var contracts = input.Contracts
            .Where(c => (offCycle || c.PayFrequency == p.Frequency)
                        && c.Start <= windowEnd && (c.End is null || c.End >= windowStart))
            .OrderBy(c => c.Start).ToList();
        var latest = contracts.LastOrDefault()
                     ?? input.Contracts.OrderBy(c => c.Start).LastOrDefault();
        var currency = latest?.Currency ?? input.FallbackCurrency;
        if (contracts.Select(c => c.Currency).Distinct().Count() > 1)
            Block("currency_mismatch", "Les contrats de la période ne sont pas dans la même devise.");

        var covered = new List<DateOnly>();
        if (!offCycle)
        {
            if (contracts.Count == 0)
                Block("no_contract", $"Aucun contrat valide ({FrequencyLabel(p.Frequency)}) sur la période {D(p.Start)} → {D(p.End)}.");
            for (var d = windowStart; d <= windowEnd; d = d.AddDays(1))
                if (contracts.Any(c => c.Start <= d && (c.End is null || c.End >= d)))
                    covered.Add(d);
        }

        // ---- 2. Salaire applicable jour par jour, segments de rémunération ---------------------------------
        CalcSalary? SalaryOn(DateOnly d)
        {
            var contract = contracts.LastOrDefault(c => c.Start <= d && (c.End is null || c.End >= d));
            if (contract is null) return null;
            return input.Salaries.Where(x => x.ContractId == contract.Id && x.EffectiveDate <= d)
                .OrderByDescending(x => x.EffectiveDate).FirstOrDefault()
                ?? input.Salaries.Where(x => x.EffectiveDate <= d).OrderByDescending(x => x.EffectiveDate).FirstOrDefault();
        }

        var segments = new List<(CalcSalary Salary, CalcContract Contract, DateOnly From, DateOnly To)>();
        var daysWithoutSalary = 0;
        foreach (var d in covered)
        {
            var sal = SalaryOn(d);
            if (sal is null) { daysWithoutSalary++; continue; }
            var contract = contracts.Last(c => c.Start <= d && (c.End is null || c.End >= d));
            if (segments.Count > 0 && segments[^1].Salary.Id == sal.Id && segments[^1].To.AddDays(1) == d)
                segments[^1] = segments[^1] with { To = d };
            else
                segments.Add((sal, contract, d, d));
        }
        if (!offCycle && contracts.Count > 0)
        {
            if (segments.Count == 0)
                Block("no_salary", "Aucune rémunération applicable : saisir un salaire avec sa date d'effet.");
            else if (daysWithoutSalary > 0)
                Warn("salary_gap", $"{daysWithoutSalary} jour(s) couverts par un contrat sans salaire applicable (non rémunérés).");
        }

        int Units(DateOnly a, DateOnly b) => s.DayCountBasis == "working" ? WorkingDays(a, b, s.WorkingWeekDays) : b.DayNumber - a.DayNumber + 1;
        bool CountsAsUnit(DateOnly d) => s.DayCountBasis != "working" || s.WorkingWeekDays.Contains(IsoDay(d));
        var periodUnits = Math.Max(1, Units(p.Start, p.End));
        var workingDaysPerWeek = Math.Max(1, s.WorkingWeekDays.Count);

        var lastSegment = segments.Count > 0 ? segments[^1] : default;
        var lastSalary = segments.Count > 0 ? lastSegment.Salary : null;
        var lastContract = segments.Count > 0 ? lastSegment.Contract : latest;
        var weeklyHours = lastContract is { WeeklyHours: > 0 } lc ? lc.WeeklyHours : s.DefaultWeeklyHours;
        var dailyHours = weeklyHours / workingDaysPerWeek;

        decimal dailyRate = 0m, hourlyRate = 0m;
        if (lastSalary is not null)
        {
            (dailyRate, hourlyRate) = lastSalary.PayBasis switch
            {
                "daily" => (lastSalary.Amount, lastSalary.Amount / dailyHours),
                "hourly" => (lastSalary.Amount * dailyHours, lastSalary.Amount),
                _ => (lastSalary.Amount / s.DaysPerMonth, lastSalary.Amount * 12m / 52m / weeklyHours),
            };
            if (lastSalary.HourlyRate is { } forced) hourlyRate = forced;
            dailyRate = R(dailyRate);
            hourlyRate = Math.Round(hourlyRate, 4, MidpointRounding.AwayFromZero);
        }

        // ---- 3. Salaire de base proratisé (§10, §11) -----------------------------------------------------
        var attendanceByDay = input.Attendance.GroupBy(a => a.Date).ToDictionary(g => g.Key, g => g.First());
        var hourlyFromAttendance = false;
        decimal baseAmount = 0m;
        foreach (var seg in segments)
        {
            var sal = seg.Salary;
            decimal amount;
            string label;
            decimal? qty;
            decimal? rate;
            switch (sal.PayBasis)
            {
                case "daily":
                {
                    var days = WorkingDays(seg.From, seg.To, s.WorkingWeekDays);
                    amount = R(sal.Amount * days);
                    qty = days; rate = sal.Amount;
                    label = $"Salaire journalier — {days} jour(s) ouvré(s) × {M(sal.Amount)} ({D(seg.From)} → {D(seg.To)})";
                    break;
                }
                case "hourly":
                {
                    var recorded = input.Attendance.Where(a => a.Date >= seg.From && a.Date <= seg.To
                                                               && a.ValidationStatus != "rejected" && a.Hours is > 0).ToList();
                    decimal hours;
                    if (recorded.Count > 0)
                    {
                        hours = recorded.Sum(a => a.Hours!.Value);
                        hourlyFromAttendance = true;
                    }
                    else
                    {
                        hours = Math.Round(dailyHours * WorkingDays(seg.From, seg.To, s.WorkingWeekDays), 2);
                        Warn("standard_hours_used", $"Aucune heure de présence saisie du {D(seg.From)} au {D(seg.To)} : horaire contractuel appliqué ({hours.ToString("N2", Fr)} h).");
                    }
                    amount = R(sal.Amount * hours);
                    qty = hours; rate = sal.Amount;
                    label = $"Salaire horaire — {hours.ToString("N2", Fr)} h × {M(sal.Amount)} ({D(seg.From)} → {D(seg.To)})";
                    break;
                }
                default:
                {
                    var perPeriod = sal.Amount * MonthlyToPeriodFactor(p.Frequency);
                    var units = Units(seg.From, seg.To);
                    amount = R(perPeriod * units / periodUnits);
                    qty = units; rate = R(perPeriod);
                    label = units == periodUnits
                        ? $"Salaire de base ({M(sal.Amount)} / mois)"
                        : $"Salaire de base proratisé — {M(R(perPeriod))} × {units}/{periodUnits} j ({D(seg.From)} → {D(seg.To)})";
                    break;
                }
            }
            baseAmount += amount;
            earnings.Add(new(PayCodes.Base, null, "base", label, qty, rate, sal.Amount, amount, true, true, true, true, "contract", seg.Contract.Id));
            trace.Add(new("Salaire de base", label, amount));
        }
        var daysEmployed = covered.Count;
        var prorata = periodUnits == 0 ? 0m : Math.Round((decimal)covered.Count(CountsAsUnit) / periodUnits, 6);

        // ---- 4. Absences non rémunérées (§10, §18) -------------------------------------------------------
        decimal absenceUnits = 0m;
        decimal absenceDeduction = 0m;
        var absenceLines = new List<(decimal Units, string Label, string Source, Guid? SourceId)>();
        if (!offCycle && covered.Count > 0 && !(lastSalary?.PayBasis == "hourly" && hourlyFromAttendance))
        {
            var coveredSet = covered.ToHashSet();
            foreach (var leave in input.Leaves.Where(l => l.Status == "approved" && !l.Paid))
            {
                var spanUnits = Math.Max(1, Units(leave.Start, leave.End));
                var inWindow = 0;
                for (var d = Max(leave.Start, p.Start); d <= Min(leave.End, p.End); d = d.AddDays(1))
                    if (coveredSet.Contains(d) && CountsAsUnit(d)) inWindow++;
                if (inWindow == 0) continue;
                var units = Math.Round(leave.Days * inWindow / spanUnits, 2);
                absenceLines.Add((units, $"Absence non rémunérée ({LeaveLabel(leave.Type)}) du {D(leave.Start)} au {D(leave.End)} — {units.ToString("N2", Fr)} j", "leave", leave.Id));
            }
            var leaveDays = input.Leaves.Where(l => l.Status == "approved")
                .SelectMany(l => Enumerable.Range(0, l.End.DayNumber - l.Start.DayNumber + 1).Select(i => l.Start.AddDays(i)))
                .ToHashSet();
            decimal attendanceUnits = 0m;
            var pendingAbsences = 0;
            foreach (var a in input.Attendance.Where(a => coveredSet.Contains(a.Date) && CountsAsUnit(a.Date)))
            {
                if (a.Justified || leaveDays.Contains(a.Date) || a.ValidationStatus == "rejected") continue;
                var units = a.Status switch { "absent" => 1m, "half_day" => 0.5m, _ => 0m };
                if (units == 0m) continue;
                if (a.ValidationStatus != "validated") { pendingAbsences++; continue; }
                attendanceUnits += units;
            }
            if (attendanceUnits > 0m)
                absenceLines.Add((attendanceUnits, $"Absences injustifiées relevées aux présences — {attendanceUnits.ToString("N2", Fr)} j", "attendance", null));
            var pendingLeaves = input.Leaves.Count(l => l.Status == "requested" && l.Start <= p.End && l.End >= p.Start);
            if (pendingLeaves + pendingAbsences > 0)
                Warn("pending_absences", $"{pendingLeaves + pendingAbsences} absence(s)/congé(s) non validé(s) sur la période : non intégré(s).");

            var remainingBase = baseAmount;
            foreach (var line in absenceLines)
            {
                var planned = R(line.Units * dailyRate);
                var amount = Math.Min(planned, remainingBase);
                remainingBase -= amount;
                absenceUnits += line.Units;
                absenceDeduction += amount;
                deductions.Add(new(PayCodes.Absence, null, "absence", "employee", line.Label, dailyRate, line.Units, planned, amount,
                    line.Source, line.SourceId, null, null));
                trace.Add(new("Absence", $"{line.Label} × {M(dailyRate)} / jour", -amount));
            }
        }

        // ---- 5. Heures supplémentaires validées (§19) -----------------------------------------------------
        var usedOvertime = new List<Guid>();
        if (!offCycle)
        {
            var approved = input.Overtime.Where(o => o.Status == "approved" && o.Date >= p.Start && o.Date <= p.End).ToList();
            foreach (var o in approved)
            {
                var amount = R(o.Hours * hourlyRate * o.Coefficient);
                var label = $"Heures supplémentaires {OvertimeLabel(o.Type)} du {D(o.Date)} — {o.Hours.ToString("N2", Fr)} h × {hourlyRate.ToString("N4", Fr)} × {o.Coefficient.ToString("0.###", Fr)}";
                earnings.Add(new(PayCodes.Overtime, null, "overtime", label, o.Hours, R(hourlyRate * o.Coefficient), hourlyRate, amount,
                    true, true, true, true, "overtime", o.Id));
                usedOvertime.Add(o.Id);
                trace.Add(new("Heures supplémentaires", label, amount));
            }
            var totalHours = approved.Sum(o => o.Hours);
            if (totalHours > s.OvertimeAlertHours)
                Warn("overtime_high", $"{totalHours.ToString("N2", Fr)} h supplémentaires sur la période (seuil {s.OvertimeAlertHours.ToString("N0", Fr)} h).");
            var pendingOt = input.Overtime.Count(o => o.Status == "requested" && o.Date >= p.Start && o.Date <= p.End);
            if (pendingOt > 0)
                Warn("pending_overtime", $"{pendingOt} saisie(s) d'heures supplémentaires non validée(s) : non intégrée(s).");
        }

        // ---- 6. Éléments récurrents, variables, régularisations, frais (§20, §21, §36, §17) ----------------
        var otherDeductionsPlanned = new List<CalcDeduction>();
        void AddElementLine(CalcComponent c, decimal amount, string label, decimal? qty, decimal? rate, decimal? baseAmt,
            string source, Guid sourceId, bool? taxableOverride = null)
        {
            if (amount <= 0m) return;
            if (c.Kind == "earning")
            {
                var taxable = taxableOverride ?? c.Taxable;
                earnings.Add(new(c.Code, c.Id, c.Category, label, qty, rate, baseAmt, amount, taxable, taxableOverride ?? c.Social, c.InGross, c.Cash, source, sourceId));
                trace.Add(new(CategoryLabel(c.Category), label, amount));
            }
            else
            {
                var category = c.Category == "adjustment" ? "adjustment" : "other";
                otherDeductionsPlanned.Add(new(c.Code, c.Id, category, "employee", label, baseAmt, rate, amount, amount, source, sourceId, null, null));
            }
        }

        if (!offCycle)
        {
            var employedRatio = periodDays == 0 ? 0m : (decimal)daysEmployed / periodDays;
            var workedDays = Math.Max(0m, WorkingDays(windowStart, windowEnd, s.WorkingWeekDays) - absenceUnits);
            foreach (var el in input.Elements.Where(x => x.Start <= p.End && (x.End is null || x.End >= p.Start)))
            {
                switch (el.Method)
                {
                    case "fixed":
                    {
                        var amount = R((el.Amount ?? 0m) * Math.Min(1m, employedRatio));
                        var label = employedRatio >= 1m ? el.Component.Name : $"{el.Component.Name} (prorata {daysEmployed}/{periodDays} j)";
                        AddElementLine(el.Component, amount, label, null, null, el.Amount, "pay_element", el.Id);
                        break;
                    }
                    case "percent_of_base":
                    {
                        var amount = R(baseAmount * (el.Rate ?? 0m) / 100m);
                        AddElementLine(el.Component, amount, $"{el.Component.Name} — {(el.Rate ?? 0m).ToString("0.##", Fr)} % du salaire de base",
                            null, el.Rate, baseAmount, "pay_element", el.Id);
                        break;
                    }
                    case "percent_of_sales":
                    {
                        var amount = R(input.SalesForCommission * (el.Rate ?? 0m) / 100m);
                        AddElementLine(el.Component, amount, $"{el.Component.Name} — {(el.Rate ?? 0m).ToString("0.##", Fr)} % de {M(input.SalesForCommission)} de ventes",
                            null, el.Rate, input.SalesForCommission, "pay_element", el.Id);
                        break;
                    }
                    case "per_day_worked":
                    {
                        var amount = R((el.Amount ?? 0m) * workedDays);
                        AddElementLine(el.Component, amount, $"{el.Component.Name} — {workedDays.ToString("N2", Fr)} j × {M(el.Amount ?? 0m)}",
                            workedDays, el.Amount, null, "pay_element", el.Id);
                        break;
                    }
                }
            }
        }

        var usedVariables = new List<Guid>();
        foreach (var v in input.Variables)
        {
            AddElementLine(v.Component, R(v.Amount), $"{v.Component.Name} — {v.Description}", null, null, null, "variable", v.Id);
            usedVariables.Add(v.Id);
        }
        if (input.PendingVariables > 0)
            Warn("pending_variables", $"{input.PendingVariables} élément(s) variable(s) non approuvé(s) : non intégré(s).");

        var usedAdjustments = new List<Guid>();
        foreach (var a in input.Adjustments)
        {
            var label = $"Régularisation {a.Number} — {a.Reason}";
            AddElementLine(a.Component with { Taxable = a.Taxable, Social = a.Taxable }, R(a.Amount), label, null, null, null,
                "adjustment", a.Id, a.Taxable);
            usedAdjustments.Add(a.Id);
        }

        var usedClaims = new List<Guid>();
        foreach (var c in input.ExpenseClaims)
        {
            var amount = R(c.Amount);
            earnings.Add(new(PayCodes.Reimbursement, null, "reimbursement", $"Remboursement de frais {c.Number} — {c.Description}", null, null, null,
                amount, false, false, false, true, "expense_claim", c.Id));
            usedClaims.Add(c.Id);
            trace.Add(new("Frais remboursés", $"{c.Number} — {c.Description} (hors brut, non imposable)", amount));
        }

        // ---- 7. Brut, frais, avantages en nature, assiettes (§11) ------------------------------------------
        var gross = R(earnings.Where(x => x.InGross).Sum(x => x.Amount));
        var reimbursements = R(earnings.Where(x => !x.InGross && x.Cash).Sum(x => x.Amount));
        var benefitInKind = R(earnings.Where(x => x.InGross && !x.Cash).Sum(x => x.Amount));
        var baseReductionsTax = otherDeductionsPlanned.Where(x => ComponentReducesTax(x, input)).Sum(x => x.Amount);
        var baseReductionsSocial = otherDeductionsPlanned.Where(x => ComponentReducesSocial(x, input)).Sum(x => x.Amount);
        var taxableBase = R(Math.Max(0m, earnings.Where(x => x.Taxable).Sum(x => x.Amount) - absenceDeduction - baseReductionsTax));
        var socialBase = R(Math.Max(0m, earnings.Where(x => x.Social).Sum(x => x.Amount) - absenceDeduction - baseReductionsSocial));
        trace.Add(new("Brut", $"Somme des gains du brut (salaire de base, heures sup., primes, commissions, avantages)", gross));
        if (benefitInKind > 0m)
        {
            deductions.Add(new(PayCodes.BenefitOffset, null, "benefit_offset", "employee", "Avantages en nature (non versés en espèces)",
                null, null, benefitInKind, benefitInKind, "benefit_in_kind", null, null, null));
            trace.Add(new("Avantages en nature", "Inclus dans le brut et les assiettes, déduits du net (non versés)", -benefitInKind));
        }
        trace.Add(new("Assiettes", $"Imposable {M(taxableBase)} — sociale {M(socialBase)} (après absences et régularisations)", null));

        // ---- 8. Retenues légales et cotisations — règles validées et versionnées (§11, §21) -----------------
        decimal employeeSocial = 0m, legal = 0m, employer = 0m;
        var rules = input.Rules.OrderBy(r => r.RuleType switch { "employee_contribution" => 0, "employer_contribution" => 1, _ => 2 })
            .ThenBy(r => r.Code).ToList();
        if (rules.Count == 0 && !offCycle)
            Warn("no_legal_rules", "Aucune règle légale validée : aucune retenue fiscale ou sociale n'est appliquée.");
        foreach (var rule in rules)
        {
            var ruleBase = rule.BaseType switch
            {
                "social" => socialBase,
                "gross" => Math.Max(0m, gross - absenceDeduction),
                "taxable_after_social" => Math.Max(0m, taxableBase - employeeSocial),
                _ => taxableBase,
            };
            if (offCycle && rule.Method == "fixed") continue;   // montant par période : déjà retenu par la paie régulière
            if (rule.BaseFloor is { } floor && ruleBase <= floor) continue;
            if (rule.BaseCeiling is { } ceiling) ruleBase = Math.Min(ruleBase, ceiling);
            decimal amount;
            string detail;
            switch (rule.Method)
            {
                case "fixed":
                    amount = rule.FixedAmount ?? 0m;
                    detail = $"montant fixe {M(amount)}";
                    break;
                case "progressive":
                {
                    var periods = PeriodsPerYear(p.Frequency);
                    var taxedBase = rule.Annualize ? ruleBase * periods : ruleBase;
                    var tax = Progressive(taxedBase, rule.Brackets);
                    amount = rule.Annualize ? tax / periods : tax;
                    detail = rule.Annualize
                        ? $"barème annuel sur {M(R(taxedBase))} (= {M(ruleBase)} × {periods}) → {M(R(tax))} / an ÷ {periods}"
                        : $"barème progressif sur {M(ruleBase)}";
                    break;
                }
                default:
                    amount = ruleBase * (rule.Rate ?? 0m) / 100m;
                    detail = $"{(rule.Rate ?? 0m).ToString("0.####", Fr)} % × {M(ruleBase)}";
                    break;
            }
            if (rule.MinAmount is { } min && amount > 0m) amount = Math.Max(amount, min);
            if (rule.MaxAmount is { } max) amount = Math.Min(amount, max);
            amount = R(Math.Max(0m, amount));
            if (amount == 0m) { trace.Add(new(rule.Name, $"{detail} → 0", 0m)); continue; }

            var isEmployer = rule.RuleType == "employer_contribution";
            var category = isEmployer ? "employer" : rule.RuleType == "income_tax" ? "tax" : "social";
            deductions.Add(new(rule.Code, null, category, isEmployer ? "employer" : "employee",
                $"{rule.Name} (v{rule.Version})", R(ruleBase), rule.Rate, amount, amount, "rule", null, rule.Id, rule.Version));
            if (isEmployer) employer += amount;
            else
            {
                legal += amount;
                if (rule.RuleType == "employee_contribution") employeeSocial += amount;
            }
            trace.Add(new(rule.Name, $"v{rule.Version} — {detail}{(isEmployer ? " (part patronale, hors net)" : "")}", isEmployer ? amount : -amount));
        }

        // ---- 9. Net avant récupérations ; autres retenues plafonnées au net minimal (§15, §21) --------------
        var netBeforeOthers = gross + reimbursements - legal - absenceDeduction - benefitInKind;
        if (netBeforeOthers < 0m)
            Block("negative_net", $"Net négatif ({M(R(netBeforeOthers))}) avant toute récupération : retenues légales et absences supérieures au brut.");
        var available = netBeforeOthers - s.MinNetAmount;
        decimal otherDeductions = 0m;
        foreach (var d in otherDeductionsPlanned)
        {
            var applied = Math.Max(0m, Math.Min(d.Amount, available));
            if (applied < d.Amount)
                Warn("deduction_capped", $"Retenue « {d.Label} » réduite de {M(d.Amount - applied)} pour préserver le net minimal.");
            available -= applied;
            otherDeductions += applied;
            deductions.Add(d with { Amount = applied, PlannedAmount = d.Amount });
            trace.Add(new(d.Category == "adjustment" ? "Régularisation" : "Autre retenue", d.Label, -applied));
        }
        var netBeforeRecoveries = netBeforeOthers - otherDeductions;

        // ---- 10. Récupérations d'avances, d'acomptes et de prêts (§13.8, §15, §16) ---------------------------
        decimal advanceRecovery = 0m, loanRecovery = 0m;
        var budget = Math.Max(0m, Math.Min(netBeforeRecoveries - s.MinNetAmount, netBeforeRecoveries * s.MaxRecoveryPercentOfNet / 100m));
        var ordered = input.Advances
            .Where(a => a.Balance > 0m && a.RecoveryMethod != "manual" && !offCycle)
            .OrderBy(a => a.Kind switch { "acompte" => 0, "salary_advance" => 1, _ => 2 }).ToList();
        foreach (var adv in ordered)
        {
            var planned = adv.RecoveryMethod == "percent_of_net"
                ? R(Math.Max(0m, netBeforeRecoveries) * (adv.RecoveryPercent ?? 0m) / 100m)
                : adv.DueNow;
            planned = Math.Min(planned, adv.Balance);
            if (planned <= 0m) continue;
            var applied = R(Math.Min(planned, budget));
            budget -= applied;
            var isLoan = adv.Kind == "loan";
            if (applied < planned)
                Warn("advance_capped", $"Récupération {adv.Number} réduite de {M(planned - applied)} (net minimal préservé) : le reste demeure dû.");
            var kindLabel = adv.Kind switch { "loan" => "Échéance de prêt", "acompte" => "Récupération d'acompte", _ => "Récupération d'avance" };
            deductions.Add(new(isLoan ? PayCodes.Loan : PayCodes.Advance, null, isLoan ? "loan" : "advance", "employee",
                $"{kindLabel} {adv.Number} (solde avant retenue {M(adv.Balance)})", adv.Balance, null, planned, applied, "advance", adv.Id, null, null));
            if (isLoan) loanRecovery += applied; else advanceRecovery += applied;
            trace.Add(new(kindLabel, $"{adv.Number} : prévu {M(planned)}, retenu {M(applied)}, solde après {M(adv.Balance - applied)}", -applied));
        }

        // ---- 11. Net à payer, coût employeur, contrôles (§11, §23) -------------------------------------------
        legal = R(legal); employer = R(employer); absenceDeduction = R(absenceDeduction); otherDeductions = R(otherDeductions);
        advanceRecovery = R(advanceRecovery); loanRecovery = R(loanRecovery);
        var totalDeductions = legal + absenceDeduction + benefitInKind + advanceRecovery + loanRecovery + otherDeductions;
        var net = gross + reimbursements - totalDeductions;
        trace.Add(new("Net à payer", $"Brut {M(gross)} + frais {M(reimbursements)} − retenues {M(totalDeductions)}", net));
        trace.Add(new("Coût employeur", $"Brut {M(gross)} + charges patronales {M(employer)}", gross + employer));

        if (net == 0m && gross > 0m) Warn("zero_net", "Net à payer nul.");
        if (input.PreviousNet is { } prev && prev > 0m && net >= 0m)
        {
            var variance = Math.Abs(net - prev) / prev * 100m;
            if (variance > s.VarianceAlertPercent)
                Warn("net_variance", $"Écart inhabituel : net {M(net)} contre {M(prev)} à la paie précédente ({variance.ToString("N1", Fr)} %).");
        }
        if (net > 0m && e.PaymentMethod == "bank_transfer" && !e.HasBankAccount)
            Warn("missing_bank_account", "Paiement par virement sans compte bancaire enregistré.");
        if (net > 0m && e.PaymentMethod == "mobile_money" && !e.HasMobileMoney)
            Warn("missing_mobile_money", "Paiement mobile money sans numéro enregistré.");

        return new PayrollCalcResult(
            lastContract?.Id, lastSalary?.Id, currency, lastContract?.ContractType, lastSalary?.PayBasis ?? lastContract?.PayBasis,
            lastContract?.PayFrequency, lastSalary?.Amount ?? 0m, periodDays, daysEmployed, absenceUnits, prorata, dailyRate, hourlyRate,
            R(baseAmount), gross, reimbursements, taxableBase, socialBase, legal, absenceDeduction, benefitInKind, advanceRecovery, loanRecovery,
            otherDeductions, totalDeductions, net, employer, gross + employer,
            earnings, deductions, anomalies, trace, usedOvertime, usedVariables, usedAdjustments, usedClaims);
    }

    /// <summary>Impôt d'un barème progressif : chaque tranche [from, to[ taxée à son taux.</summary>
    public static decimal Progressive(decimal amount, IReadOnlyList<RuleBracket> brackets)
    {
        decimal tax = 0m;
        foreach (var b in brackets.OrderBy(b => b.From))
        {
            if (amount <= b.From) break;
            var upper = b.To is { } to ? Math.Min(amount, to) : amount;
            if (upper > b.From) tax += (upper - b.From) * b.Rate / 100m;
        }
        return tax;
    }

    public static int WorkingDays(DateOnly from, DateOnly to, IReadOnlySet<int> workingWeekDays)
    {
        var n = 0;
        for (var d = from; d <= to; d = d.AddDays(1))
            if (workingWeekDays.Contains(IsoDay(d))) n++;
        return n;
    }

    public static int IsoDay(DateOnly d) => d.DayOfWeek == DayOfWeek.Sunday ? 7 : (int)d.DayOfWeek;

    private static bool ComponentReducesTax(CalcDeduction d, PayrollCalcInput input) => d.SourceType switch
    {
        "adjustment" => input.Adjustments.FirstOrDefault(a => a.Id == d.SourceId)?.Taxable ?? false,
        "variable" => input.Variables.FirstOrDefault(v => v.Id == d.SourceId)?.Component.Taxable ?? false,
        "pay_element" => input.Elements.FirstOrDefault(x => x.Id == d.SourceId)?.Component.Taxable ?? false,
        _ => false,
    };

    private static bool ComponentReducesSocial(CalcDeduction d, PayrollCalcInput input) => d.SourceType switch
    {
        "adjustment" => input.Adjustments.FirstOrDefault(a => a.Id == d.SourceId)?.Taxable ?? false,
        "variable" => input.Variables.FirstOrDefault(v => v.Id == d.SourceId)?.Component.Social ?? false,
        "pay_element" => input.Elements.FirstOrDefault(x => x.Id == d.SourceId)?.Component.Social ?? false,
        _ => false,
    };

    private static DateOnly Max(DateOnly a, DateOnly b) => a > b ? a : b;
    private static DateOnly Min(DateOnly a, DateOnly b) => a < b ? a : b;

    public static string FrequencyLabel(string f) => f switch
    {
        "weekly" => "hebdomadaire",
        "biweekly" => "bihebdomadaire",
        _ => "mensuelle",
    };

    public static string LeaveLabel(string t) => t switch
    {
        "annual" => "congé annuel",
        "sick" => "maladie",
        "maternity" => "maternité",
        "paternity" => "paternité",
        "unpaid" => "congé sans solde",
        "justified_absence" => "absence justifiée",
        "unjustified_absence" => "absence injustifiée",
        _ => "autre",
    };

    public static string OvertimeLabel(string t) => t switch
    {
        "night" => "de nuit",
        "weekend" => "de fin de semaine",
        "holiday" => "de jour férié",
        _ => "",
    };

    public static string CategoryLabel(string c) => c switch
    {
        "bonus" => "Prime",
        "commission" => "Commission",
        "allowance" => "Indemnité",
        "benefit_in_kind" => "Avantage en nature",
        "reimbursement" => "Frais remboursés",
        "adjustment" => "Régularisation",
        "overtime" => "Heures supplémentaires",
        _ => "Gain",
    };
}
