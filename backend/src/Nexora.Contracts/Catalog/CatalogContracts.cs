namespace Nexora.Contracts.Catalog;

// --- Recherche catalogue (Phase 23 §37-38) ---------------------------------

/// <summary>Résultat de recherche catalogue. <paramref name="ExactBarcode"/> = correspondance
/// exacte d'un code-barres scanné (§38 : « SCAN → EXACT MATCH → ADD TO CART »).</summary>
public sealed record CatalogHitDto(
    Guid ProductId, string Sku, string Name, string? ShortName,
    string? Barcode, string? PluCode, string ProductType, string Status,
    string? CategoryName, string? BrandName,
    decimal? SalePrice, string? Currency, bool ExactBarcode);

// --- Fiche produit (Phase 23 §39) -----------------------------------------

public sealed record ProductListItemDto(
    Guid Id, string Sku, string Name, string? ShortName, string ProductType, string Status,
    string? CategoryName, string? BrandName, string? PrimaryBarcode, decimal? SalePrice, string? Currency);

public sealed record ProductDetailDto(
    Guid Id, string Sku, string Name, string? ShortName, string? Description, string ProductType,
    string Status, string? PluCode, string? ManufacturerRef, string? InternalRef, string? ImageUrl,
    string? CategoryName, string? BrandName, bool TrackLots, bool IsWeighed,
    IReadOnlyList<ProductPriceDto> Prices,
    IReadOnlyList<ProductBarcodeDto> Barcodes,
    IReadOnlyList<ProductSupplierDto> Suppliers);

public sealed record ProductPriceDto(
    Guid Id, string PriceType, string? PriceListCode, decimal Amount, string Currency,
    bool TaxIncluded, decimal QuantityMin, string? CustomerGroup,
    DateTimeOffset ValidFrom, DateTimeOffset? ValidTo);

public sealed record ProductBarcodeDto(
    Guid Id, string Barcode, string BarcodeType, bool IsPrimary, string Status, decimal PackSize);

public sealed record ProductSupplierDto(
    Guid Id, string SupplierName, string? SupplierSku, decimal PurchasePrice, string Currency,
    int LeadTimeDays, bool IsPreferred);

// --- Catégories / listes tarifaires / taxes / promotions -----------------

public sealed record CategoryDto(Guid Id, Guid? ParentId, string Name, string? Code, string Status);
public sealed record CreateCategoryRequest(string Name, string? Code, Guid? ParentId);

public sealed record PriceListDto(
    Guid Id, string Code, string Name, string ListType, string? Currency,
    string? CustomerGroup, short Priority, string Status);
public sealed record CreatePriceListRequest(
    string Code, string Name, string ListType, string? Currency, Guid? CustomerGroupId, short Priority);

public sealed record TaxDto(Guid Id, string Code, string Name, decimal Rate, string Type, bool IsInclusive, string Status);

public sealed record PromotionDto(
    Guid Id, string Name, string PromoType, decimal Value, short Priority,
    DateTimeOffset? StartsAt, DateTimeOffset? EndsAt, string Status);

// --- Moteur de prix explicable (Phase 23 §22) ---------------------------

/// <summary>Demande de devis prix. L'ordre des règles est figé (§22) :
/// prix de base → liste/client → quantité → promotion → coupon → remise → taxe.</summary>
public sealed record PriceQuoteRequest(
    Guid ProductId, decimal Quantity, string Currency,
    Guid? CustomerId, Guid? PriceListId, Guid? InventoryLocationId,
    DateTimeOffset? At, decimal ManualDiscount, string? CouponCode);

/// <summary>Une étape du calcul de prix — restituée telle quelle au POS (§22 : rien de caché).</summary>
public sealed record PriceStepDto(string Rule, string Label, decimal Delta, decimal RunningUnit, string? Reference);

public sealed record PriceQuoteDto(
    Guid ProductId, string Description, decimal Quantity, string Currency,
    decimal BaseUnitPrice, decimal NetUnitPrice, decimal TaxRate, string? TaxCode,
    decimal UnitTax, decimal GrossUnitPrice,
    decimal LineNet, decimal LineDiscount, decimal LineTax, decimal LineGross,
    IReadOnlyList<PriceStepDto> Steps);
