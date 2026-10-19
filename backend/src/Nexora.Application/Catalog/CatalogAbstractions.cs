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

    /// <summary>Page de produits (<paramref name="offset"/> : produits déjà affichés) — un catalogue de supermarché
    /// dépasse la limite d'une page.</summary>
    Task<IReadOnlyList<ProductListItemDto>> ListProductsAsync(
        string? term, Guid? categoryId, string? status, int limit, CancellationToken ct = default, int offset = 0);

    Task<ProductDetailDto?> GetProductAsync(Guid productId, CancellationToken ct = default);

    Task<IReadOnlyList<CategoryDto>> ListCategoriesAsync(CancellationToken ct = default);
    Task<IReadOnlyList<PriceListDto>> ListPriceListsAsync(CancellationToken ct = default);
    Task<IReadOnlyList<TaxDto>> ListTaxesAsync(CancellationToken ct = default);
}

/// <summary>
/// Référentiels du catalogue gérés depuis l'interface (finalisation produit, phase 1) : unités,
/// conversions, modification des catégories, image produit. Chaque écriture est tracée par le
/// trigger d'audit générique (<c>nexora.audit_row</c>, V079) ; aucune ne touche au stock.
/// </summary>
public interface ICatalogReferenceService
{
    Task<IReadOnlyList<CurrencyDto>> ListCurrenciesAsync(CancellationToken ct = default);
    Task<IReadOnlyList<BrandDto>> ListBrandsAsync(CancellationToken ct = default);
    Task<Result<Guid>> CreateBrandAsync(CreateNamedItemRequest request, CancellationToken ct = default);
    Task<IReadOnlyList<CustomerGroupDto>> ListCustomerGroupsAsync(CancellationToken ct = default);
    Task<Result<Guid>> CreateCustomerGroupAsync(CreateNamedItemRequest request, CancellationToken ct = default);

    Task<IReadOnlyList<UnitDto>> ListUnitsAsync(CancellationToken ct = default);
    Task<Result<Guid>> CreateUnitAsync(CreateUnitRequest request, CancellationToken ct = default);

    Task<IReadOnlyList<UnitConversionDto>> ListConversionsAsync(Guid? productId, CancellationToken ct = default);
    Task<Result<Guid>> CreateConversionAsync(CreateUnitConversionRequest request, CancellationToken ct = default);
    Task<Result> DeleteConversionAsync(Guid id, CancellationToken ct = default);

    Task<Result> UpdateCategoryAsync(Guid id, UpdateCategoryRequest request, CancellationToken ct = default);

    Task<Result> SetProductImageAsync(Guid productId, SetProductImageRequest request, CancellationToken ct = default);
    Task<Result<(byte[] Content, string ContentType)>> GetProductImageAsync(Guid productId, CancellationToken ct = default);
}

/// <summary>
/// Conversion d'unités déterministe, toujours côté serveur (§13 : « 1 carton = 24 bouteilles »).
/// Ordre de résolution figé : même unité → conversion propre au produit (sens direct, puis inverse)
/// → conversion générique (direct, puis inverse). Pas de chaînage implicite : une conversion
/// absente est un refus explicite, jamais une estimation. Résultat arrondi à 6 décimales
/// (domaine <c>nexora.quantity</c>).
/// </summary>
public interface IUnitConverter
{
    Task<Result<UnitConversionResultDto>> ConvertAsync(
        Guid? productId, Guid fromUnitId, Guid toUnitId, decimal quantity, CancellationToken ct = default);
}

/// <summary>
/// Promotions et coupons gérés depuis l'interface. Le calcul reste celui de
/// <see cref="PromotionCalculator"/> (encaissement et simulateur) : ce service ne fait qu'écrire
/// des règles valides — une promotion incohérente est refusée, jamais « corrigée » en silence.
/// </summary>
public interface IPromotionService
{
    Task<IReadOnlyList<PromotionDto>> ListAsync(CancellationToken ct = default);
    Task<Result<Guid>> CreateAsync(SavePromotionRequest request, CancellationToken ct = default);
    Task<Result> UpdateAsync(Guid id, SavePromotionRequest request, CancellationToken ct = default);
    Task<Result> SetStatusAsync(Guid id, string status, CancellationToken ct = default);

    Task<IReadOnlyList<CouponDto>> ListCouponsAsync(CancellationToken ct = default);
    Task<Result<Guid>> CreateCouponAsync(CreateCouponRequest request, CancellationToken ct = default);
    Task<Result> SetCouponStatusAsync(Guid id, string status, CancellationToken ct = default);
}
