using Nexora.Domain.Common;

namespace Nexora.Domain.Inventory;

// ---- Transferts (§14) -------------------------------------------------------

public sealed class StockTransfer : AuditableEntity
{
    public Guid CompanyId { get; set; }
    public string DocumentNumber { get; set; } = string.Empty;
    public Guid SourceLocationId { get; set; }
    public Guid DestLocationId { get; set; }
    public string Status { get; set; } = "draft";   // draft|in_transit|partially_received|completed|cancelled
    public DateTimeOffset? ShippedAt { get; set; }
    public DateTimeOffset? ReceivedAt { get; set; }
    public string? Notes { get; set; }
    public Guid? CreatedBy { get; set; }
    public List<StockTransferLine> Lines { get; set; } = [];
}

public sealed class StockTransferLine : AuditableEntity
{
    public Guid StockTransferId { get; set; }
    public Guid CompanyId { get; set; }
    public Guid ProductId { get; set; }
    public Guid? LotId { get; set; }
    public decimal Quantity { get; set; }
    public decimal QuantityShipped { get; set; }
    public decimal QuantityReceived { get; set; }
    public decimal UnitCost { get; set; }
}

// ---- Inventaire (§15) -----------------------------------------------------

public sealed class InventoryCount : AuditableEntity
{
    public Guid CompanyId { get; set; }
    public Guid InventoryLocationId { get; set; }
    public string DocumentNumber { get; set; } = string.Empty;
    public string CountType { get; set; } = "full";
    public string Status { get; set; } = "draft";   // draft|counting|review|validated|cancelled
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? ValidatedAt { get; set; }
    public Guid? ValidatedBy { get; set; }
    public string? Notes { get; set; }
    public List<InventoryCountLine> Lines { get; set; } = [];
}

public sealed class InventoryCountLine : AuditableEntity
{
    public Guid InventoryCountId { get; set; }
    public Guid CompanyId { get; set; }
    public Guid ProductId { get; set; }
    public Guid? LotId { get; set; }
    public decimal SystemQuantity { get; set; }
    public decimal? CountedQuantity { get; set; }
    public decimal Variance { get; set; }           // généré : counted - system
    public decimal UnitCost { get; set; }
    /// <summary>Justification de l'écart (V080).</summary>
    public string? Note { get; set; }
}

// ---- Ajustements / pertes / casse (§16) --------------------------------

public static class AdjustmentType
{
    public const string Count = "count";
    public const string Loss = "loss";
    public const string Breakage = "breakage";
    public const string Expiry = "expiry";
    /// <summary>Vol constaté (V080) — sortie de stock traitée comme une perte, distinguée dans les rapports.</summary>
    public const string Theft = "theft";
    public const string Correction = "correction";
    public const string Found = "found";
    public const string Opening = "opening";
}

public sealed class StockAdjustment : AuditableEntity
{
    public Guid CompanyId { get; set; }
    public Guid InventoryLocationId { get; set; }
    public string DocumentNumber { get; set; } = string.Empty;
    public string AdjustmentTypeValue { get; set; } = AdjustmentType.Correction;
    public string Status { get; set; } = "draft";   // draft|posted|cancelled
    public string? Reason { get; set; }
    public DateTimeOffset OccurredAt { get; set; }
    public string? SourceType { get; set; }
    public Guid? SourceId { get; set; }
    public Guid? ApprovedBy { get; set; }
    public Guid? JournalEntryId { get; set; }
    public Guid? CreatedBy { get; set; }
    public List<StockAdjustmentLine> Lines { get; set; } = [];
}

public sealed class StockAdjustmentLine : AuditableEntity
{
    public Guid StockAdjustmentId { get; set; }
    public Guid CompanyId { get; set; }
    public Guid ProductId { get; set; }
    public Guid? LotId { get; set; }
    public decimal QuantityDelta { get; set; }       // signé
    public decimal UnitCost { get; set; }
    /// <summary>Justification de la ligne (V080).</summary>
    public string? Note { get; set; }
}
