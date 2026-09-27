using System.Globalization;
using Nexora.Contracts.Printing;

namespace Nexora.Infrastructure.Payroll;

// Bulletin de paie (docs/module-paie-rh-avances.md §25) : un instantané figé au moment du paiement, haché
// (SHA-256) et rendu à la demande en vrai PDF. Réimprimer rend le même contenu, marqué « DUPLICATA ».

public sealed record PayslipLine(string Label, decimal? Base, decimal? Rate, decimal Amount, string Category);

public sealed record PayslipSnapshot(
    string PayslipNumber,
    DateTimeOffset IssuedAt,
    string EmployerName,
    string? EmployerTaxId,
    string EmployeeNumber,
    string EmployeeName,
    string? JobTitle,
    string? Department,
    string? ContractType,
    DateOnly? HireDate,
    string? TaxIdMasked,
    string PeriodLabel,
    DateOnly PeriodStart,
    DateOnly PeriodEnd,
    string Currency,
    decimal BaseSalary,
    string? PayBasis,
    decimal DaysEmployed,
    decimal AbsenceDays,
    IReadOnlyList<PayslipLine> Earnings,
    decimal Gross,
    decimal Reimbursements,
    IReadOnlyList<PayslipLine> Deductions,
    IReadOnlyList<PayslipLine> EmployerContributions,
    decimal AdvanceRecovered,
    decimal LoanRecovered,
    decimal RemainingAdvanceBalance,
    decimal TotalDeductions,
    decimal Net,
    decimal EmployerCost,
    string? PaymentNumber,
    DateTimeOffset? PaymentDate,
    string? PaymentMethod,
    string? PaymentReference,
    string? BeneficiaryAccount,
    string RunNumber,
    string? LegalMentions);

internal static class PayslipDocuments
{
    private static readonly CultureInfo Fr = CultureInfo.GetCultureInfo("fr-FR");

    public static string MethodLabel(string? m) => m switch
    {
        "bank_transfer" => "Virement bancaire",
        "cash" => "Espèces",
        "mobile_money" => "Mobile money",
        "check" => "Chèque",
        "other" => "Autre",
        _ => m ?? "—",
    };

    public static string ContractLabel(string? t) => t switch
    {
        "permanent" => "Durée indéterminée",
        "fixed_term" => "Durée déterminée",
        "temporary" => "Temporaire",
        "internship" => "Stage",
        "apprenticeship" => "Apprentissage",
        "consultant" => "Consultant",
        "daily" => "Journalier",
        _ => t ?? "—",
    };

    public static PrintDocument Build(PayslipSnapshot s, bool cancelled, string sha256)
    {
        var employee = new List<string> { $"{s.EmployeeName} — matricule {s.EmployeeNumber}" };
        if (s.JobTitle is { Length: > 0 }) employee.Add($"Poste : {s.JobTitle}{(s.Department is { Length: > 0 } d ? " — " + d : "")}");
        employee.Add($"Contrat : {ContractLabel(s.ContractType)}{(s.HireDate is { } h ? $" — entrée le {h.ToString("dd/MM/yyyy", Fr)}" : "")}");
        if (s.TaxIdMasked is { Length: > 0 } tax) employee.Add($"Identifiant fiscal : {tax}");

        var details = new List<PrintFigure>
        {
            new("Période", $"{s.PeriodLabel} ({s.PeriodStart.ToString("dd/MM/yyyy", Fr)} → {s.PeriodEnd.ToString("dd/MM/yyyy", Fr)})"),
            new("Salaire de base", s.BaseSalary, PrintKinds.Money, s.Currency),
            new("Jours rémunérés", s.DaysEmployed - s.AbsenceDays, PrintKinds.Number),
            new("Date de paiement", s.PaymentDate, PrintKinds.DateTime),
            new("Référence du paiement", s.PaymentNumber is null ? "—" : $"{s.PaymentNumber}{(s.PaymentReference is { Length: > 0 } r ? " / " + r : "")}"),
            new("Moyen de paiement", $"{MethodLabel(s.PaymentMethod)}{(s.BeneficiaryAccount is { Length: > 0 } a ? " — " + a : "")}"),
            new("Paie", s.RunNumber),
        };

        PrintTable Lines(string title, IReadOnlyList<PayslipLine> lines, decimal total) => new(title,
            [new PrintColumn("Libellé"), new PrintColumn("Base", PrintKinds.Money, s.Currency), new PrintColumn("Taux / qté", PrintKinds.Number),
             new PrintColumn("Montant", PrintKinds.Money, s.Currency)],
            lines.Select(l => (IReadOnlyList<object?>)[l.Label, l.Base, l.Rate, l.Amount]).ToList(),
            ["Total", null, null, total]);

        var tables = new List<PrintTable>
        {
            Lines("Gains", s.Earnings, s.Gross + s.Reimbursements),
            Lines("Retenues", s.Deductions, s.TotalDeductions),
        };
        if (s.EmployerContributions.Count > 0)
            tables.Add(Lines("Cotisations patronales (à la charge de l'employeur, hors net)", s.EmployerContributions,
                s.EmployerContributions.Sum(x => x.Amount)));

        var summary = new List<PrintFigure>
        {
            new("Salaire brut", s.Gross, PrintKinds.Money, s.Currency),
            new("Total des retenues", s.TotalDeductions, PrintKinds.Money, s.Currency),
        };
        if (s.Reimbursements > 0m) summary.Add(new("Frais remboursés (hors brut)", s.Reimbursements, PrintKinds.Money, s.Currency));
        if (s.AdvanceRecovered > 0m) summary.Add(new("dont avances récupérées", s.AdvanceRecovered, PrintKinds.Money, s.Currency));
        if (s.LoanRecovered > 0m) summary.Add(new("dont échéances de prêt", s.LoanRecovered, PrintKinds.Money, s.Currency));
        if (s.AdvanceRecovered > 0m || s.LoanRecovered > 0m || s.RemainingAdvanceBalance > 0m)
            summary.Add(new("Solde restant dû (avances et prêts)", s.RemainingAdvanceBalance, PrintKinds.Money, s.Currency));
        summary.Add(new("Net à payer", s.Net, PrintKinds.Money, s.Currency, Emphasis: true));
        summary.Add(new("Coût employeur", s.EmployerCost, PrintKinds.Money, s.Currency));

        var notes = new List<string>();
        if (s.LegalMentions is { Length: > 0 } legal) notes.Add(legal);
        notes.Add($"Identifiant unique du bulletin : {s.PayslipNumber} — émis le {s.IssuedAt.ToString("dd/MM/yyyy HH:mm", Fr)} UTC — empreinte SHA-256 {sha256[..16]}….");

        return new PrintDocument(
            Title: "Bulletin de paie",
            Reference: s.PayslipNumber,
            Subtitle: s.PeriodLabel,
            Parties: [new PrintParty("Employeur", [s.EmployerName, .. s.EmployerTaxId is { Length: > 0 } t ? new[] { "NIF : " + t } : []]),
                      new PrintParty("Salarié", employee)],
            Details: details,
            Tables: tables,
            Summary: summary,
            Notes: notes,
            Stamp: cancelled ? "ANNULÉ" : null);
    }
}
