namespace Nexora.Contracts.Sales;

// Caisse hors ligne (finalisation produit, phase 6).

/// <summary>Enregistrement automatique du navigateur de caisse. <c>DeviceIdentifier</c> : identifiant aléatoire
/// conservé par le navigateur (8 à 100 caractères, lettres, chiffres, tirets).</summary>
public sealed record EnrollPosDeviceRequest(string DeviceIdentifier, string? Name);

public sealed record PosDeviceDto(Guid Id, string Name, string Status);

/// <summary>Article vendable tel que la caisse le cherche hors ligne : jamais de coût, seulement le prix de vente
/// en vigueur. <c>Codes</c> = codes-barres, PLU ; <c>SearchText</c> = nom, SKU, nom court, alias.</summary>
public sealed record PosOfflineCatalogItemDto(
    Guid ProductId, string Name, string Sku, string? Barcode, string[] Codes, string SearchText,
    decimal? SalePrice, string? Currency, string Availability, decimal TaxRate = 0m, bool PriceIncludesTax = false);

public sealed record PosOfflinePolicyDto(
    bool AllowOfflineSales, bool AllowOfflineCredit, decimal OfflineCreditLimit, bool AllowOfflineDiscount,
    bool AllowOfflineRefund, bool AllowOfflineCustomerPayment, int MaxOfflineMinutes, IReadOnlyList<string> AllowedPaymentMethods);

/// <summary>Tout ce dont la caisse a besoin pour vendre sans réseau. <c>Truncated</c> : catalogue plafonné.</summary>
public sealed record PosOfflineProfileDto(
    DateTimeOffset GeneratedAt, PosOfflinePolicyDto Policy, IReadOnlyList<PosOfflineCatalogItemDto> Catalog, bool Truncated, int MaxItems);

// --- Aperçu du panier calculé par le serveur (phase 6) --------------------------------------

public sealed record CartPreviewLineInput(Guid ProductId, decimal Quantity, decimal LineDiscount);
public sealed record CartPreviewRequest(Guid InventoryLocationId, Guid? CustomerId, string Currency,
    IReadOnlyList<CartPreviewLineInput> Lines, string? CouponCode = null);

/// <summary>Ligne telle que la vente l'enregistrera : prix HT, remise manuelle, promotion, taxe, total TTC.</summary>
public sealed record CartPreviewLineDto(Guid ProductId, decimal Quantity, decimal UnitPrice, decimal ManualDiscount,
    decimal PromotionDiscount, string? PromotionLabel, decimal TaxAmount, decimal LineTotal);

public sealed record CartPreviewDto(decimal Subtotal, decimal DiscountTotal, decimal TaxTotal, decimal Total,
    IReadOnlyList<CartPreviewLineDto> Lines);
