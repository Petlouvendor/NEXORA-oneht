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
    IReadOnlyList<ProductSupplierDto> Suppliers,
    // Finalisation produit (phase 1) : identifiants nécessaires aux formulaires de modification —
    // la fiche n'exposait que des libellés, un formulaire ne pouvait donc rien présélectionner.
    Guid? CategoryId = null, Guid? BrandId = null, Guid? UnitId = null, string? UnitCode = null,
    Guid? DefaultTaxId = null, ReorderRuleDto? ReorderRule = null, bool HasImage = false,
    IReadOnlyList<UnitConversionDto>? Conversions = null);

/// <summary>Seuils de réapprovisionnement d'un produit (règle générale, tous emplacements).</summary>
public sealed record ReorderRuleDto(
    decimal MinQuantity, decimal MaxQuantity, decimal ReorderPoint, decimal ReorderQuantity,
    int LeadTimeDays, bool AutoOrder);

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
/// <summary>Modification d'une catégorie. <paramref name="Status"/> : active | inactive.</summary>
public sealed record UpdateCategoryRequest(string Name, string? Code, Guid? ParentId, string Status);

// --- Marques & groupes de clients (référentiels des formulaires produit / prix) ----

public sealed record BrandDto(Guid Id, string Name, string Status);
public sealed record CustomerGroupDto(Guid Id, string Name, string? Description, string Status);
public sealed record CreateNamedItemRequest(string Name, string? Description);

// --- Unités & conversions (§13 ; table unit_conversion, V024) -------------

public sealed record UnitDto(Guid Id, string Code, string Name, short Precision, bool IsWeight);
public sealed record CreateUnitRequest(string Code, string Name, short Precision, bool IsWeight);

/// <summary>1 <paramref name="FromUnitCode"/> = <paramref name="Factor"/> <paramref name="ToUnitCode"/>.
/// <paramref name="ProductId"/> nul = conversion générique (toute l'entreprise).</summary>
public sealed record UnitConversionDto(
    Guid Id, Guid? ProductId, Guid FromUnitId, string FromUnitCode, Guid ToUnitId, string ToUnitCode, decimal Factor);
public sealed record CreateUnitConversionRequest(Guid? ProductId, Guid FromUnitId, Guid ToUnitId, decimal Factor);

/// <summary>Résultat d'une conversion : la règle utilisée est restituée (rien de caché).</summary>
public sealed record UnitConversionResultDto(decimal Quantity, decimal Converted, string Rule);

/// <summary>Image produit envoyée en base64 (PNG, JPEG ou WebP ; 2 Mo au plus).</summary>
public sealed record SetProductImageRequest(string ContentBase64, string ContentType);

public sealed record PriceListDto(
    Guid Id, string Code, string Name, string ListType, string? Currency,
    string? CustomerGroup, short Priority, string Status);
public sealed record CreatePriceListRequest(
    string Code, string Name, string ListType, string? Currency, Guid? CustomerGroupId, short Priority);

public sealed record TaxDto(Guid Id, string Code, string Name, decimal Rate, string Type, bool IsInclusive, string Status);

public sealed record PromotionDto(
    Guid Id, string Name, string PromoType, decimal Value, short Priority,
    DateTimeOffset? StartsAt, DateTimeOffset? EndsAt, string Status,
    PromotionConditionsDto? Conditions = null, IReadOnlyList<PromotionTargetDto>? Targets = null);

/// <summary>Conditions d'une promotion : quantité minimale ; « X achetés, Y offerts » (buy_x_get_y) ;
/// taille du lot (bundle, dont le prix est <c>Value</c>).</summary>
public sealed record PromotionConditionsDto(
    decimal? MinQuantity, decimal? BuyQuantity, decimal? GetQuantity, decimal? BundleQuantity);

/// <summary>Cible : all | product | category | brand (un seul identifiant renseigné selon le type).</summary>
public sealed record PromotionTargetDto(string TargetType, Guid? ProductId, Guid? CategoryId, Guid? BrandId, string? Label = null);

/// <summary>Création ou modification complète d'une promotion (les cibles sont remplacées).</summary>
public sealed record SavePromotionRequest(
    string Name, string PromoType, decimal Value, short Priority,
    DateTimeOffset? StartsAt, DateTimeOffset? EndsAt,
    PromotionConditionsDto? Conditions, IReadOnlyList<PromotionTargetDto> Targets);

public sealed record CouponDto(
    Guid Id, string Code, Guid? PromotionId, string DiscountType, decimal DiscountValue,
    int? UsageLimit, int UsedCount, DateTimeOffset? ValidFrom, DateTimeOffset? ValidTo, string Status);

public sealed record CreateCouponRequest(
    string Code, string DiscountType, decimal DiscountValue, int? UsageLimit,
    DateTimeOffset? ValidFrom, DateTimeOffset? ValidTo);

/// <summary>Changement de statut simple (promotion : active | inactive ; coupon : active | inactive).</summary>
public sealed record SetStatusRequest(string Status);

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
