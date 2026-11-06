namespace Nexora.Application.Finance;

/// <summary>
/// Correction des écritures de vente et de retour en devise passées avant le correctif A-052 (audit 2026-10 éd. 2) :
/// leur coût des marchandises, déjà en devise fonctionnelle, avait été converti une seconde fois au taux de la vente.
/// Une écriture corrective (journal OD, origine <c>adjustment</c>) retire l'excédent des comptes de coût des ventes et
/// de stock ; l'écriture d'origine n'est pas modifiée. Datée comme l'origine si sa période est ouverte, sinon du jour.
/// Idempotente : une pièce corrigée porte un lien vers sa correction et n'est plus proposée.
/// </summary>
public interface ICostOfGoodsCurrencyRecovery
{
    /// <param name="apply">Faux : rien n'est écrit, le résultat dit ce qui serait corrigé.</param>
    Task<IReadOnlyList<CostOfGoodsCorrectionLine>> RunAsync(bool apply, CancellationToken ct = default);
}

/// <param name="Excess">Montant retiré du coût des ventes (positif pour une vente, négatif pour un retour).</param>
/// <param name="Status"><c>a_corriger</c> (simulation), <c>corrige</c>, ou <c>refuse</c> (voir <paramref name="Detail"/>).</param>
public sealed record CostOfGoodsCorrectionLine(
    Guid CompanyId, string CompanyName, Guid EntryId, string EntryNumber, DateOnly EntryDate, string SourceType,
    decimal Excess, string Currency, string Status, string? CorrectionNumber = null, string? Detail = null);
