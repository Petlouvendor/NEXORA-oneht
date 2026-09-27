using Nexora.Domain.Common;

namespace Nexora.Domain.Sales;

public static class SaleStatus
{
    public const string Draft = "draft";
    public const string Parked = "parked";
    public const string Completed = "completed";
    public const string PartiallyRefunded = "partially_refunded";
    public const string Refunded = "refunded";
    public const string Void = "void";
}

public static class SaleType
{
    public const string Cash = "cash";
    public const string Credit = "credit";
    public const string Mixed = "mixed";
    public const string Quote = "quote";
    public const string Layaway = "layaway";
}

/// <summary>Vente — table nexora.sale (Phase 6). Saisie une fois, propagée ensuite (§2, §19).</summary>
public sealed class Sale : AuditableEntity
{
    public Guid CompanyId { get; set; }
    public Guid? EstablishmentId { get; set; }
    public Guid? StoreId { get; set; }
    public Guid? PosTerminalId { get; set; }
    public Guid? CustomerId { get; set; }
    public Guid? CashSessionId { get; set; }
    public string DocumentNumber { get; set; } = string.Empty;
    public string Status { get; set; } = SaleStatus.Draft;
    public string SaleTypeValue { get; set; } = SaleType.Cash;
    public Guid CurrencyId { get; set; }
    public Guid? ExchangeRateId { get; set; }
    public DateTimeOffset SoldAt { get; set; }
    public decimal Subtotal { get; set; }
    public decimal DiscountTotal { get; set; }
    public decimal TaxTotal { get; set; }
    public decimal Total { get; set; }
    public decimal AmountPaid { get; set; }
    public decimal AmountTendered { get; set; }
    public decimal ChangeGiven { get; set; }
    public Guid? SalespersonUserId { get; set; }
    public Guid? SourceOperationUuid { get; set; }
    public string? ParkedReference { get; set; }
    public DateTimeOffset? ParkedAt { get; set; }
    public Guid? CreditOverrideBy { get; set; }
    public string? CreditOverrideReason { get; set; }
    public DateTimeOffset? VoidedAt { get; set; }
    public string? VoidReason { get; set; }
    public Guid? ReversalJournalEntryId { get; set; }
    public Guid? SalesOrderId { get; set; }
    public Guid? CreatedBy { get; set; }

    public List<SaleLine> Lines { get; set; } = [];
}

public sealed class SaleLine : AuditableEntity
{
    public Guid SaleId { get; set; }
    public Guid CompanyId { get; set; }
    public Guid ProductId { get; set; }
    public string Description { get; set; } = string.Empty;
    public decimal Quantity { get; set; }
    public decimal UnitPrice { get; set; }
    public decimal? OriginalUnitPrice { get; set; }
    public decimal Discount { get; set; }
    public Guid? TaxId { get; set; }
    public decimal TaxAmount { get; set; }
    public decimal LineTotal { get; set; }
    public decimal CostSnapshot { get; set; }
    public decimal UnitCost { get; set; }
    public Guid? LotId { get; set; }
    public Guid? InventoryLocationId { get; set; }
    public Guid? PromotionId { get; set; }
}

public sealed class SalePayment : Entity
{
    public Guid SaleId { get; set; }
    public Guid CompanyId { get; set; }
    public Guid PaymentId { get; set; }
    public decimal Amount { get; set; }
    public Guid CurrencyId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class SalePromotion : Entity
{
    public Guid CompanyId { get; set; }
    public Guid SaleId { get; set; }
    public Guid? SaleLineId { get; set; }
    public Guid? PromotionId { get; set; }
    public Guid? CouponId { get; set; }
    public string PromoType { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
    public decimal Value { get; set; }
    public decimal DiscountAmount { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>Retour de vente — table nexora.sale_return (§22).</summary>
public sealed class SaleReturn : AuditableEntity
{
    public Guid CompanyId { get; set; }
    public Guid SaleId { get; set; }
    public string DocumentNumber { get; set; } = string.Empty;
    public string Status { get; set; } = "draft";
    public Guid CurrencyId { get; set; }
    public string? Reason { get; set; }
    public decimal RefundTotal { get; set; }
    public string RefundMethod { get; set; } = "cash";
    public Guid? RefundPaymentId { get; set; }
    public bool Restocked { get; set; } = true;
    public Guid? SourceOperationUuid { get; set; }
    public Guid? JournalEntryId { get; set; }
    public Guid? CashSessionId { get; set; }
    public DateTimeOffset ReturnedAt { get; set; }
    public Guid? CreatedBy { get; set; }

    public List<SaleReturnLine> Lines { get; set; } = [];
}

public sealed class SaleReturnLine : AuditableEntity
{
    public Guid SaleReturnId { get; set; }
    public Guid CompanyId { get; set; }
    public Guid? SaleLineId { get; set; }
    public Guid ProductId { get; set; }
    public decimal Quantity { get; set; }
    public decimal UnitPrice { get; set; }
    public decimal LineTotal { get; set; }
    public decimal TaxAmount { get; set; }
    public decimal UnitCost { get; set; }
    public Guid? LotId { get; set; }
    public bool Restock { get; set; } = true;
}

// --- Caisse (§25) -----------------------------------------------------

public sealed class CashRegister : AuditableEntity
{
    public Guid CompanyId { get; set; }
    public Guid? StoreId { get; set; }
    public Guid? FinancialAccountId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Code { get; set; } = string.Empty;
    public Guid CurrencyId { get; set; }
    public string Status { get; set; } = "active";
}

public sealed class CashSession : AuditableEntity
{
    public Guid CompanyId { get; set; }
    public Guid CashRegisterId { get; set; }
    public Guid? PosTerminalId { get; set; }
    public Guid UserId { get; set; }
    public DateTimeOffset OpenedAt { get; set; }
    public DateTimeOffset? ClosedAt { get; set; }
    public decimal OpeningBalance { get; set; }
    public decimal? ExpectedBalance { get; set; }
    public decimal? CountedBalance { get; set; }
    public decimal? Difference { get; set; }
    public string Status { get; set; } = "open";
    public string? Notes { get; set; }

    public bool IsOpen => Status is "open" or "closing";
}

public sealed class CashMovement : Entity
{
    public Guid CompanyId { get; set; }
    public Guid CashSessionId { get; set; }
    public string MovementType { get; set; } = "adjustment";
    public decimal Amount { get; set; }
    public Guid CurrencyId { get; set; }
    public string? ReferenceType { get; set; }
    public Guid? ReferenceId { get; set; }
    public string? Note { get; set; }
    public DateTimeOffset OccurredAt { get; set; }
    public Guid? UserId { get; set; }
    public Guid? SourceOperationUuid { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}
