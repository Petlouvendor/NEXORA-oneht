using System.Globalization;

namespace Nexora.Application.Payroll;

/// <summary>Paramètres de la politique d'avances de l'entreprise (V076).</summary>
public sealed record AdvancePolicySettings(
    decimal MaxPercentOfSalary,
    bool AllowMultiple,
    int MaxOpenAdvances,
    decimal AdvanceCeilingPercent,
    decimal LoanCeilingPercent,
    decimal NearLimitPercent);

/// <summary>Encours d'un employé au moment d'une demande (avances/acomptes et prêts séparés, §16).</summary>
public sealed record EmployeeDebtPosition(
    decimal MonthlySalary, int OpenAdvances, decimal AdvanceOutstanding, decimal LoanOutstanding, int OpenLoans);

public sealed record AdvanceCheck(string Code, bool Passed, bool Blocking, string Message);

/// <summary>
/// Règles déterministes d'une demande d'avance (§13.2 « le système vérifie les règles : salaire, avances existantes,
/// plafond et période ») et construction de l'échéancier (§15). Pures : testées unitairement.
/// </summary>
public static class AdvanceRules
{
    private static readonly CultureInfo Fr = CultureInfo.GetCultureInfo("fr-FR");

    public static IReadOnlyList<AdvanceCheck> Evaluate(
        string kind, decimal amount, EmployeeDebtPosition position, AdvancePolicySettings policy, bool employeeActive, bool hasSalary)
    {
        string M(decimal v) => v.ToString("N2", Fr);
        var checks = new List<AdvanceCheck>
        {
            new("employee_active", employeeActive, true,
                employeeActive ? "Employé actif avec un contrat en cours." : "Employé inactif ou sans contrat en cours : aucune avance possible."),
            new("salary_known", hasSalary, true,
                hasSalary ? "Salaire applicable connu." : "Aucun salaire applicable : plafonds impossibles à vérifier."),
        };
        if (!hasSalary) return checks;

        var isLoan = kind == "loan";
        if (!isLoan)
        {
            var max = Math.Round(position.MonthlySalary * policy.MaxPercentOfSalary / 100m, 2);
            checks.Add(new("max_percent_of_salary", amount <= max, true, amount <= max
                ? $"Montant dans la limite de {policy.MaxPercentOfSalary.ToString("0.##", Fr)} % du salaire mensuel."
                : $"Montant supérieur à {policy.MaxPercentOfSalary.ToString("0.##", Fr)} % du salaire mensuel (maximum {M(max)})."));

            var multipleOk = policy.AllowMultiple || position.OpenAdvances == 0;
            checks.Add(new("multiple_advances", multipleOk, true, multipleOk
                ? "Politique d'avances multiples respectée."
                : "Une avance est déjà en cours et la politique n'autorise pas plusieurs avances simultanées."));
            var countOk = position.OpenAdvances < policy.MaxOpenAdvances;
            checks.Add(new("max_open_advances", countOk, true, countOk
                ? $"{position.OpenAdvances} avance(s) en cours sur {policy.MaxOpenAdvances} autorisée(s)."
                : $"Nombre maximal d'avances en cours atteint ({policy.MaxOpenAdvances})."));
        }

        var ceilingPercent = isLoan ? policy.LoanCeilingPercent : policy.AdvanceCeilingPercent;
        var ceiling = Math.Round(position.MonthlySalary * ceilingPercent / 100m, 2);
        var outstanding = isLoan ? position.LoanOutstanding : position.AdvanceOutstanding;
        var projected = outstanding + amount;
        var ceilingOk = projected <= ceiling;
        checks.Add(new("debt_ceiling", ceilingOk, true, ceilingOk
            ? $"Encours après demande {M(projected)} dans le plafond {M(ceiling)}."
            : $"Plafond d'endettement dépassé : encours après demande {M(projected)} pour un plafond de {M(ceiling)} " +
              $"({ceilingPercent.ToString("0.##", Fr)} % du salaire mensuel)."));
        if (ceilingOk && ceiling > 0m && projected >= ceiling * policy.NearLimitPercent / 100m)
            checks.Add(new("near_limit", false, false,
                $"Encours proche du plafond : {(projected / ceiling * 100m).ToString("N0", Fr)} % du plafond consommé."));
        return checks;
    }

    /// <summary>
    /// Échéancier de récupération (§15) : « prochaine paie » = une échéance ; « échéancier » = n parts égales (le reste
    /// d'arrondi sur la dernière) espacées selon la fréquence de paie ; « % du net » et « manuel » n'ont pas de dates.
    /// </summary>
    public static IReadOnlyList<(int Seq, DateOnly DueDate, decimal Amount)> BuildSchedule(
        decimal amount, string method, int installments, DateOnly firstDueDate, string frequency)
    {
        if (amount <= 0m || method is "percent_of_net" or "manual") return [];
        var n = method == "installments" ? Math.Max(1, installments) : 1;
        var part = Math.Floor(amount / n * 100m) / 100m;
        var result = new List<(int, DateOnly, decimal)>();
        for (var i = 0; i < n; i++)
        {
            var due = frequency switch
            {
                "weekly" => firstDueDate.AddDays(7 * i),
                "biweekly" => firstDueDate.AddDays(14 * i),
                _ => firstDueDate.AddMonths(i),
            };
            var value = i == n - 1 ? amount - part * (n - 1) : part;
            result.Add((i + 1, due, value));
        }
        return result;
    }

    /// <summary>Un nouvel échéancier doit couvrir exactement le solde restant, en montants positifs (§15, §16).</summary>
    public static string? ValidateReschedule(decimal balance, IReadOnlyList<(DateOnly DueDate, decimal Amount)> installments)
    {
        if (installments.Count == 0) return "L'échéancier doit comporter au moins une échéance.";
        if (installments.Any(i => i.Amount <= 0m)) return "Chaque échéance doit avoir un montant positif.";
        if (installments.Select(i => i.DueDate).Distinct().Count() != installments.Count) return "Deux échéances ont la même date.";
        var total = installments.Sum(i => i.Amount);
        return total == balance ? null
            : $"Le total des échéances ({total.ToString("N2", Fr)}) doit égaler le solde restant ({balance.ToString("N2", Fr)}).";
    }
}
