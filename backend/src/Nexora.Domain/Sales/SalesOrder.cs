using Nexora.Domain.Common;

namespace Nexora.Domain.Sales;

public static class SalesOrderStatus
{
    public const string Draft = "draft";
    public const string Confirmed = "confirmed";
    public const string Reserved = "reserved";
    public const string PartiallyFulfilled = "partially_fulfilled";
    public const string Fulfilled = "fulfilled";
    public const string Cancelled = "cancelled";

    /// <summary>Transitions autorisées du cycle de commande (Phase 25 §4, §18).</summary>
    public static bool CanTransition(string from, string to) => (from, to) switch
    {
        (Draft, Confirmed) or (Draft, Cancelled) => true,
        (Confirmed, Reserved) or (Confirmed, Cancelled) or (Confirmed, PartiallyFulfilled) or (Confirmed, Fulfilled) => true,
        (Reserved, PartiallyFulfilled) or (Reserved, Fulfilled) or (Reserved, Cancelled) => true,
        (PartiallyFulfilled, Fulfilled) or (PartiallyFulfilled, Cancelled) => true,
        _ => false,
    };
}

public static class SalesOrderType
{
    public const string Pickup = "pickup";
    public const string Delivery = "delivery";
    public const string Wholesale = "wholesale";
    public const string Reservation = "reservation";

    public static readonly string[] All = [Pickup, Delivery, Wholesale, Reservation];
}

/// <summary>Commande client — table nexora.sales_order (Phase 25 §18-20).</summary>
public sealed class SalesOrder : AuditableEntity
{
    public Guid CompanyId { get; set; }
    public Guid? EstablishmentId { get; set; }
    public Guid? CustomerId { get; set; }
    public string DocumentNumber { get; set; } = string.Empty;
    public string OrderType { get; set; } = SalesOrderType.Pickup;
    public string Status { get; set; } = SalesOrderStatus.Draft;
    public Guid CurrencyId { get; set; }
    public Guid? InventoryLocationId { get; set; }
    public decimal Subtotal { get; set; }
    public decimal DiscountTotal { get; set; }
    public decimal TaxTotal { get; set; }
    public decimal Total { get; set; }
    public decimal DepositAmount { get; set; }
    public Guid? DepositPaymentId { get; set; }
    public bool OnCredit { get; set; }
    public DateTimeOffset? ExpectedAt { get; set; }
    public DateOnly? DueDate { get; set; }
    public string? DeliveryAddress { get; set; }
    public string? Notes { get; set; }
    public DateTimeOffset? ReservedAt { get; set; }
    public DateTimeOffset? FulfilledAt { get; set; }
    public string? CancelledReason { get; set; }
    public Guid? CreatedBy { get; set; }

    public List<SalesOrderLine> Lines { get; set; } = [];

    public decimal RemainingBalance => Math.Max(0m, Total - DepositAmount);
}

public sealed class SalesOrderLine : AuditableEntity
{
    public Guid SalesOrderId { get; set; }
    public Guid CompanyId { get; set; }
    public Guid ProductId { get; set; }
    public string Description { get; set; } = string.Empty;
    public decimal Quantity { get; set; }
    public decimal QuantityFulfilled { get; set; }
    public decimal UnitPrice { get; set; }
    public decimal Discount { get; set; }
    public Guid? TaxId { get; set; }
    public decimal TaxAmount { get; set; }
    public decimal LineTotal { get; set; }
    public decimal UnitCost { get; set; }

    public decimal QuantityOutstanding => Quantity - QuantityFulfilled;
}
