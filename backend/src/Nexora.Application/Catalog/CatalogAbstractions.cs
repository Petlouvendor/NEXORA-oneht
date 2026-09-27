using Nexora.Contracts.Catalog;
using Nexora.Domain.Common;

namespace Nexora.Application.Catalog;

/// <summary>
/// Moteur de prix du catalogue (Phase 23 §22). Déterministe et <b>explicable</b> :
/// chaque devis restitue la suite ordonnée des règles appliquées
/// (prix de base → liste/client → quantité → promotion → coupon → remise → taxe).
/// C'est la même résolution que celle du POS ; aucune règle cachée.
/// </summary>
public interface ICatalogPricingEngine
{
    Task<Result<PriceQuoteDto>> QuoteAsync(PriceQuoteRequest request, CancellationToken ct = default);
}

/// <summary>Lectures catalogue : recherche tolérante, scan, fiche produit, référentiels (§37-39, §47).</summary>
public interface ICatalogQueries
{
    Task<IReadOnlyList<CatalogHitDto>> SearchAsync(string term, int limit, CancellationToken ct = default);

    /// <summary>Scan POS (§38) : correspondance exacte d'un code-barres → 0 ou 1 résultat.</summary>
    Task<CatalogHitDto?> ScanAsync(string barcode, CancellationToken ct = default);

    Task<IReadOnlyList<ProductListItemDto>> ListProductsAsync(
        string? term, Guid? categoryId, string? status, int limit, CancellationToken ct = default);

    Task<ProductDetailDto?> GetProductAsync(Guid productId, CancellationToken ct = default);

    Task<IReadOnlyList<CategoryDto>> ListCategoriesAsync(CancellationToken ct = default);
    Task<IReadOnlyList<PriceListDto>> ListPriceListsAsync(CancellationToken ct = default);
    Task<IReadOnlyList<TaxDto>> ListTaxesAsync(CancellationToken ct = default);
    Task<IReadOnlyList<PromotionDto>> ListPromotionsAsync(CancellationToken ct = default);
}
