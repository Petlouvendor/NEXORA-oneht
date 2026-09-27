using Nexora.Domain.Common;

namespace Nexora.Domain.Catalog;

public static class ProductType
{
    public const string Stockable = "stockable";
    public const string Service = "service";
    public const string Composite = "composite";
    public const string Weight = "weight";
    public const string NonStock = "non_stock";
}

/// <summary>Workflow produit — table nexora.product.status (Phase 23 §45).</summary>
public static class ProductStatus
{
    public const string Draft = "draft";
    public const string Review = "review";
    public const string Active = "active";
    public const string Suspended = "suspended";
    public const string Inactive = "inactive";
    public const string Archived = "archived";

    public static readonly string[] All = [Draft, Review, Active, Suspended, Inactive, Archived];

    /// <summary>Transitions autorisées du workflow (§45).</summary>
    public static bool CanTransition(string from, string to) => (from, to) switch
    {
        (Draft, Review) or (Draft, Active) => true,
        (Review, Active) or (Review, Draft) => true,
        (Active, Suspended) or (Active, Inactive) or (Active, Archived) => true,
        (Suspended, Active) or (Suspended, Archived) => true,
        (Inactive, Active) or (Inactive, Archived) => true,
        _ => false,
    };
}

/// <summary>Produit — table nexora.product (Phase 5 §4-6).</summary>
public sealed class Product : AuditableEntity
{
    public Guid CompanyId { get; set; }
    public string Sku { get; set; } = string.Empty;
    public string? Barcode { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public Guid? CategoryId { get; set; }
    public Guid? BrandId { get; set; }
    public Guid UnitId { get; set; }
    public Guid? PurchaseUnitId { get; set; }
    public Guid? DefaultTaxId { get; set; }
    public Guid? ParentProductId { get; set; }
    public string VariantAttributes { get; set; } = "{}";
    public string ProductType { get; set; } = Catalog.ProductType.Stockable;
    public string CostMethod { get; set; } = "average";
    public string ValuationMethod { get; set; } = "average";
    public string Status { get; set; } = ProductStatus.Active;
    public bool TrackLots { get; set; }
    public bool IsWeighed { get; set; }
    public bool IsSellable { get; set; } = true;
    public bool IsPurchasable { get; set; } = true;

    // Identité commerciale & POS (Phase 23 §4-5, §31, §40).
    public string? ShortName { get; set; }
    public string? PluCode { get; set; }
    public string? ManufacturerRef { get; set; }
    public string? InternalRef { get; set; }
    public string? SearchAliases { get; set; }
    public string? ImageUrl { get; set; }
    public string? Notes { get; set; }

    public bool RequiresStock => ProductType is Catalog.ProductType.Stockable or Catalog.ProductType.Weight;
}

/// <summary>Liste tarifaire nommée — table nexora.price_list (Phase 23 §18).</summary>
public sealed class PriceList : AuditableEntity
{
    public Guid CompanyId { get; set; }
    public string Code { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string ListType { get; set; } = "custom";   // cost|retail|wholesale|member|promotional|custom
    public Guid? CurrencyId { get; set; }
    public Guid? CustomerGroupId { get; set; }
    public short Priority { get; set; }
    public string Status { get; set; } = "active";
}

/// <summary>Prix produit historisé — table nexora.product_price (§8, §18-20, §25).</summary>
public sealed class ProductPrice : AuditableEntity
{
    public Guid ProductId { get; set; }
    public Guid CompanyId { get; set; }
    public string PriceType { get; set; } = "sale";     // sale | purchase | promo
    public Guid? CustomerGroupId { get; set; }
    public Guid? PriceListId { get; set; }
    public Guid? EstablishmentId { get; set; }
    public decimal QuantityMin { get; set; }
    public Guid CurrencyId { get; set; }
    public decimal Amount { get; set; }
    public bool TaxIncluded { get; set; }               // §25 : le montant est TTC
    public decimal? MinPrice { get; set; }              // §46 : plancher (remise maximale)
    public DateTimeOffset ValidFrom { get; set; }
    public DateTimeOffset? ValidTo { get; set; }
}

/// <summary>Fournisseur d'un produit — table nexora.product_supplier (§10).</summary>
public sealed class ProductSupplier : AuditableEntity
{
    public Guid ProductId { get; set; }
    public Guid CompanyId { get; set; }
    public Guid SupplierId { get; set; }
    public string? SupplierSku { get; set; }
    public decimal PurchasePrice { get; set; }
    public Guid CurrencyId { get; set; }
    public int LeadTimeDays { get; set; }
    public bool IsPreferred { get; set; }
}

/// <summary>Code-barres additionnel — table nexora.product_barcode (Phase 23 §6).</summary>
public sealed class ProductBarcode : AuditableEntity
{
    public Guid ProductId { get; set; }
    public Guid CompanyId { get; set; }
    public string Barcode { get; set; } = string.Empty;
    public string BarcodeType { get; set; } = "ean13";
    public bool IsPrimary { get; set; }
    public string Status { get; set; } = "active";
    public decimal PackSize { get; set; } = 1m;
    public bool IsWeightEmbedded { get; set; }
    public DateTimeOffset ActivatedAt { get; set; }
}

/// <summary>Conversion d'unités — table nexora.unit_conversion (§6).</summary>
public sealed class UnitConversion : AuditableEntity
{
    public Guid CompanyId { get; set; }
    public Guid? ProductId { get; set; }
    public Guid FromUnitId { get; set; }
    public Guid ToUnitId { get; set; }
    public decimal Factor { get; set; }   // 1 from = Factor to
}

/// <summary>Règle de réapprovisionnement — table nexora.product_reorder_rule (§12, §19).</summary>
public sealed class ProductReorderRule : AuditableEntity
{
    public Guid CompanyId { get; set; }
    public Guid ProductId { get; set; }
    public Guid? InventoryLocationId { get; set; }
    public decimal MinQuantity { get; set; }
    public decimal MaxQuantity { get; set; }
    public decimal ReorderPoint { get; set; }
    public decimal ReorderQuantity { get; set; }
    public int LeadTimeDays { get; set; }
    public bool AutoOrder { get; set; }
    public string Status { get; set; } = "active";
}

/// <summary>Historique du coût unitaire — table nexora.product_cost_history (§8, §29).</summary>
public sealed class ProductCostHistory : Entity
{
    public Guid CompanyId { get; set; }
    public Guid ProductId { get; set; }
    public string SourceType { get; set; } = string.Empty;
    public Guid? SourceId { get; set; }
    public decimal UnitCost { get; set; }
    public Guid CurrencyId { get; set; }
    public decimal? AverageCostAfter { get; set; }
    public string Method { get; set; } = "average";
    public DateTimeOffset EffectiveAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}
