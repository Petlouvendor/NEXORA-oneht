namespace Nexora.Contracts.Finance;

// Gestion du plan comptable (finalisation produit, phase 8).

/// <summary>Compte avec son usage : solde (écritures validées, devise de l'entreprise), mouvements, et ce qui l'utilise
/// (écritures automatiques, comptes de trésorerie, catégories de dépense).</summary>
public sealed record ChartAccountDto(
    Guid Id, string Code, string Name, string AccountType, bool IsPostable, string Status,
    Guid? ParentAccountId, string? ParentCode, string? Currency, decimal Balance, int LineCount,
    IReadOnlyList<string> UsedBy);

/// <summary><c>ParentAccountId</c> : compte de regroupement (non mouvementable) du même type.</summary>
public sealed record SaveChartAccountRequest(
    string Code, string Name, string AccountType, bool IsPostable, Guid? ParentAccountId, string? Currency);

public sealed record ChartAccountStatusRequest(string Status, string Reason);
