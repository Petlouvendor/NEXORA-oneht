using Nexora.Domain.Common;

namespace Nexora.Domain.Purchasing;

// ---- Demande d'achat (§22) ------------------------------------------------

public sealed class PurchaseRequest : AuditableEntity
{
    public Guid CompanyId { get; set; }
    public Guid? EstablishmentId { get; set; }
    public string DocumentNumber { get; set; } = string.Empty;
    public string Status { get; set; } = "draft";   // draft|submitted|approved|rejected|ordered|cancelled
    public string Priority { get; set; } = "normal";
    public DateOnly? NeededBy { get; set; }
    public string? Reason { get; set; }
    public Guid? RequestedBy { get; set; }
    public List<PurchaseRequestLine> Lines { get; set; } = [];
}

public sealed class PurchaseRequestLine : AuditableEntity
{
    public Guid PurchaseRequestId { get; set; }
    public Guid CompanyId { get; set; }
    public Guid ProductId { get; set; }
    public decimal Quantity { get; set; }
    public decimal EstimatedPrice { get; set; }
    public string? Notes { get; set; }
}

// ---- Commande fournisseur (§9-10) -----------------------------------

public static class PurchaseOrderStatus
{
    public const string Draft = "draft";
    public const string Submitted = "submitted";
    public const string Approved = "approved";
    public const string Sent = "sent";
    public const string PartiallyReceived = "partially_received";
    public const string Received = "received";
    public const string Invoiced = "invoiced";
    public const string Closed = "closed";
    public const string Cancelled = "cancelled";

    /// <summary>Transitions manuelles autorisées (Phase 26 §10). Les états de réception
    /// (`partially_received` / `received`) sont posés par le moteur de réception.</summary>
    public static bool CanTransition(string from, string to) => (from, to) switch
    {
        (Draft, Submitted) or (Draft, Cancelled) => true,
        (Submitted, Approved) or (Submitted, Draft) or (Submitted, Cancelled) => true,
        (Approved, Sent) or (Approved, Cancelled) => true,
        (Sent, Cancelled) or (Sent, Closed) => true,
        (PartiallyReceived, Closed) or (PartiallyReceived, Cancelled) => true,
        (Received, Closed) or (Received, Invoiced) => true,
        (Invoiced, Closed) => true,
        _ => false,
    };

    public static readonly string[] Receivable = [Approved, Sent, PartiallyReceived];
}

public sealed class PurchaseOrder : AuditableEntity
{
    public Guid CompanyId { get; set; }
    public Guid SupplierId { get; set; }
    public Guid? EstablishmentId { get; set; }
    public Guid? WarehouseId { get; set; }
    public string DocumentNumber { get; set; } = string.Empty;
    public string Status { get; set; } = "draft";
    public Guid CurrencyId { get; set; }
    public DateOnly OrderDate { get; set; }
    public DateOnly? ExpectedDate { get; set; }
    public decimal Subtotal { get; set; }
    public decimal DiscountTotal { get; set; }
    public decimal TaxTotal { get; set; }
    public decimal Total { get; set; }
    public string? Notes { get; set; }
    public Guid? CreatedBy { get; set; }
    public List<PurchaseOrderLine> Lines { get; set; } = [];
}

public sealed class PurchaseOrderLine : AuditableEntity
{
    public Guid PurchaseOrderId { get; set; }
    public Guid CompanyId { get; set; }
    public Guid ProductId { get; set; }
    public decimal QuantityOrdered { get; set; }
    public decimal QuantityReceived { get; set; }
    public decimal UnitPrice { get; set; }
    public Guid? TaxId { get; set; }
    public decimal Discount { get; set; }
    public decimal LineTotal { get; set; }
    /// <summary>Taxe de la ligne, calculée par le serveur (V081).</summary>
    public decimal TaxAmount { get; set; }
    /// <summary>Unité et quantité saisies (ex. 10 cartons) ; QuantityOrdered = équivalent en unité de base (V081).</summary>
    public Guid? PurchaseUnitId { get; set; }
    public decimal? PurchaseQuantity { get; set; }
}

// ---- Réception (§24) ------------------------------------------------

public sealed class GoodsReceipt : AuditableEntity
{
    public Guid CompanyId { get; set; }
    public Guid SupplierId { get; set; }
    public Guid? WarehouseId { get; set; }
    public Guid? InventoryLocationId { get; set; }
    public Guid? PurchaseOrderId { get; set; }
    public string DocumentNumber { get; set; } = string.Empty;
    public string Status { get; set; } = "draft";   // draft|posted|cancelled
    public DateTimeOffset ReceivedAt { get; set; }
    public string? Notes { get; set; }
    public Guid? CreatedBy { get; set; }
    public List<GoodsReceiptLine> Lines { get; set; } = [];
}

public sealed class GoodsReceiptLine : AuditableEntity
{
    public Guid GoodsReceiptId { get; set; }
    public Guid CompanyId { get; set; }
    public Guid? PurchaseOrderLineId { get; set; }
    public Guid ProductId { get; set; }
    public decimal QuantityReceived { get; set; }
    public decimal UnitCost { get; set; }
    public Guid? LotId { get; set; }
    public Guid? PurchaseUnitId { get; set; }
    public decimal? PurchaseQuantity { get; set; }
    /// <summary>Refusé au contrôle (unité de base) — jamais entré en stock (V081).</summary>
    public decimal QuantityRejected { get; set; }
    public string? RejectionReason { get; set; }
}

// ---- Retour fournisseur (§28) ----------------------------------

public sealed class SupplierReturn : AuditableEntity
{
    public Guid CompanyId { get; set; }
    public Guid SupplierId { get; set; }
    public Guid? GoodsReceiptId { get; set; }
    public Guid? SupplierInvoiceId { get; set; }
    /// <summary>Dette sur laquelle le retour est imputé (avoir) — V082.</summary>
    public Guid? PayableId { get; set; }
    public Guid? WarehouseLocationId { get; set; }
    public string DocumentNumber { get; set; } = string.Empty;
    public string Status { get; set; } = "draft";   // draft|posted|cancelled
    public string? Reason { get; set; }
    public Guid CurrencyId { get; set; }
    public decimal Subtotal { get; set; }
    public decimal TaxTotal { get; set; }
    public decimal Total { get; set; }
    public DateTimeOffset ReturnedAt { get; set; }
    public Guid? CreatedBy { get; set; }
    public List<SupplierReturnLine> Lines { get; set; } = [];
}

public sealed class SupplierReturnLine : AuditableEntity
{
    public Guid SupplierReturnId { get; set; }
    public Guid CompanyId { get; set; }
    public Guid ProductId { get; set; }
    public Guid? LotId { get; set; }
    public decimal Quantity { get; set; }
    public decimal UnitPrice { get; set; }
    public Guid? TaxId { get; set; }
    public decimal LineTotal { get; set; }
}
