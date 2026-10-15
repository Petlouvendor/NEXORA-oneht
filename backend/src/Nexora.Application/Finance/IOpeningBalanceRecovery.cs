namespace Nexora.Application.Finance;

/// <summary>
/// Reprise des soldes d'ouverture saisis avant l'audit 2026-10 (A-023). La colonne
/// <c>financial_account.opening_balance</c> entrait dans le solde de trésorerie sans aucune écriture comptable :
/// trésorerie et grand livre différaient de ce montant. La reprise convertit chacun en solde d'ouverture du modèle
/// actuel — transaction de trésorerie « opening » datée avant le premier mouvement et écriture D trésorerie / C report
/// à nouveau, par le moteur financier — puis remet la colonne à zéro. Le solde affiché ne change pas.
/// Idempotente : un compte repris n'a plus de colonne à reprendre.
/// </summary>
public interface IOpeningBalanceRecovery
{
    /// <param name="apply">Faux : rien n'est écrit, le résultat dit ce qui serait fait.</param>
    /// <param name="accountId">Un seul compte (sinon tous ceux de l'installation).</param>
    Task<IReadOnlyList<OpeningBalanceRecoveryLine>> RunAsync(bool apply, Guid? accountId = null, CancellationToken ct = default);
}

/// <param name="Status"><c>a_reprendre</c> (simulation), <c>repris</c>, ou <c>refuse</c> (voir <paramref name="Detail"/>).</param>
public sealed record OpeningBalanceRecoveryLine(
    Guid CompanyId, string CompanyName, Guid AccountId, string AccountName, string Currency, decimal Amount,
    DateTimeOffset OpeningAt, string Status, string? EntryNumber = null, string? Detail = null);
