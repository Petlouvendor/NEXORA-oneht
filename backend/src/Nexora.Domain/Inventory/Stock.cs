using Nexora.Domain.Common;

namespace Nexora.Domain.Inventory;

public static class MovementType
{
    public const string Receipt = "receipt";
    public const string Sale = "sale";
    public const string SaleReturn = "sale_return";
    public const string PurchaseReturn = "purchase_return";
    public const string TransferIn = "transfer_in";
    public const string TransferOut = "transfer_out";
    public const string Adjustment = "adjustment";
    public const string Loss = "loss";
    public const string Breakage = "breakage";
    public const string Expiry = "expiry";
    public const string Found = "found";
    public const string Count = "count";
    public const string Reservation = "reservation";
    public const string Release = "release";
    public const string Opening = "opening";
}

/// <summary>Emplacement de stock — table nexora.inventory_location (§5-6, §11).</summary>
public sealed class InventoryLocation : AuditableEntity
{
    public Guid CompanyId { get; set; }
    public Guid? EstablishmentId { get; set; }
    public Guid? WarehouseId { get; set; }
    public Guid? StoreId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string LocationType { get; set; } = "warehouse";
    public string Status { get; set; } = "active";
    public string? Code { get; set; }
    public string? Zone { get; set; }
    public string? Aisle { get; set; }
    public string? Shelf { get; set; }
    public string? Bin { get; set; }
    public decimal? Capacity { get; set; }
    public Guid? ManagerUserId { get; set; }
    public Guid? PreferredProductId { get; set; }

    public static readonly string[] Types =
        ["store", "warehouse", "reserve", "cold_room", "pharmacy", "secure",
         "kitchen", "production", "transit", "return", "quarantine", "virtual"];
    public static readonly string[] NonPhysicalTypes =
        ["virtual", "transit", "production", "return", "quarantine"];
}

/// <summary>Solde courant — table nexora.stock_balance (§12). Maintenu par trigger depuis les mouvements.</summary>
public sealed class StockBalance : AuditableEntity
{
    public Guid ProductId { get; set; }
    public Guid InventoryLocationId { get; set; }
    public Guid CompanyId { get; set; }
    public decimal QuantityOnHand { get; set; }
    public decimal QuantityReserved { get; set; }
    public decimal QuantityAvailable { get; set; }   // généré : on_hand - reserved
    public decimal AverageCost { get; set; }
}

/// <summary>Mouvement de stock — table nexora.stock_movement (§13). Immuable.</summary>
public sealed class StockMovement : Entity
{
    public Guid CompanyId { get; set; }
    public Guid ProductId { get; set; }
    public Guid InventoryLocationId { get; set; }
    public Guid? LotId { get; set; }
    public string MovementTypeValue { get; set; } = MovementType.Adjustment;
    public decimal Quantity { get; set; }              // signé : + entrée, - sortie
    public decimal UnitCost { get; set; }
    public string? ReferenceType { get; set; }
    public Guid? ReferenceId { get; set; }
    public DateTimeOffset OccurredAt { get; set; }
    public Guid? UserId { get; set; }
    public Guid? SourceOperationUuid { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>Lot — table nexora.lot (§17).</summary>
public sealed class Lot : AuditableEntity
{
    public Guid CompanyId { get; set; }
    public Guid ProductId { get; set; }
    public string LotNumber { get; set; } = string.Empty;
    public DateOnly? ExpirationDate { get; set; }
    public DateOnly? ManufactureDate { get; set; }
    public string Status { get; set; } = "active";
    public Guid? SupplierId { get; set; }
    public DateOnly? ReceivedAt { get; set; }
    public DateTimeOffset? BlockedAt { get; set; }
    public string? BlockedReason { get; set; }
    public Guid? BlockedBy { get; set; }
    public string? RecallReference { get; set; }

    public bool IsSellable => Status == "active";

    /// <summary>Statut de péremption dérivé (§13) : normal / proche / expiré.</summary>
    public string ExpiryStatus(DateOnly today, int alertDays) => ExpirationDate switch
    {
        null => "none",
        { } d when d < today => "expired",
        { } d when d <= today.AddDays(alertDays) => "near",
        _ => "normal",
    };
}

/// <summary>Stock par lot et emplacement — table nexora.lot_stock (§17-18).</summary>
public sealed class LotStock : AuditableEntity
{
    public Guid LotId { get; set; }
    public Guid InventoryLocationId { get; set; }
    public Guid CompanyId { get; set; }
    public decimal QuantityOnHand { get; set; }
    public decimal QuantityReserved { get; set; }
}
