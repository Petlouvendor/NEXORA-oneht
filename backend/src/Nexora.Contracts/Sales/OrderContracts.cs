namespace Nexora.Contracts.Sales;

// --- Commandes clients (Phase 25 §18-20, §43) ---------------------------

public sealed record CreateOrderLineInput(Guid ProductId, decimal Quantity, decimal? UnitPriceOverride, decimal LineDiscount, Guid? TaxId);

public sealed record CreateOrderRequest(
    Guid? CustomerId, string Currency, string OrderType, Guid? InventoryLocationId,
    bool OnCredit, DateTimeOffset? ExpectedAt, DateOnly? DueDate, string? DeliveryAddress, string? Notes,
    IReadOnlyList<CreateOrderLineInput> Lines);

public sealed record OrderDepositRequest(Guid OperationUuid, decimal Amount, Guid TenderFinancialAccountId);

public sealed record OrderTenderInput(Guid FinancialAccountId, Guid? PaymentMethodId, decimal Amount, string? Reference);

/// <summary>Exécution de la commande : crée la vente réelle pour la quantité livrée (§18).
/// <paramref name="LineQuantities"/> nul ⇒ tout le reliquat.</summary>
public sealed record CompleteOrderRequest(
    Guid OperationUuid, Guid? CashSessionId, bool OnCredit, DateOnly? CreditDueDate,
    IReadOnlyList<OrderTenderInput> Tenders,
    IReadOnlyDictionary<Guid, decimal>? LineQuantities);

public sealed record OrderSummaryDto(
    Guid Id, string DocumentNumber, string OrderType, string Status, string? CustomerName,
    string Currency, decimal Total, decimal DepositAmount, decimal RemainingBalance,
    DateTimeOffset? ExpectedAt, DateTimeOffset CreatedAt);

public sealed record OrderLineDto(
    Guid Id, Guid ProductId, string Description, decimal Quantity, decimal QuantityFulfilled,
    decimal UnitPrice, decimal Discount, decimal TaxAmount, decimal LineTotal);

public sealed record OrderDto(
    Guid Id, string DocumentNumber, string OrderType, string Status, Guid? CustomerId,
    string Currency, decimal Subtotal, decimal DiscountTotal, decimal TaxTotal, decimal Total,
    decimal DepositAmount, decimal RemainingBalance, bool OnCredit,
    DateTimeOffset? ExpectedAt, DateOnly? DueDate, string? DeliveryAddress, string? Notes,
    DateTimeOffset? ReservedAt, DateTimeOffset? FulfilledAt,
    IReadOnlyList<OrderLineDto> Lines, IReadOnlyList<Guid> SaleIds,
    // Phase 4 : fiche complète.
    string? CustomerName = null, Guid? InventoryLocationId = null, string? LocationName = null,
    decimal DepositAvailable = 0m, IReadOnlyList<OrderSaleRefDto>? Sales = null, string? CancelledReason = null,
    DateTimeOffset? CreatedAt = null);

/// <summary>Vente issue de l'exécution (totale ou partielle) d'une commande.</summary>
public sealed record OrderSaleRefDto(Guid Id, string DocumentNumber, DateTimeOffset SoldAt, decimal Total, decimal AmountDue, string Status);

// --- Lecture d'une vente (Phase 25 §9, §43) --------------------------

/// <summary><c>UnitCost</c> = <c>null</c> sans <c>catalog.view_cost</c> (guide §7.2 : pas de coût au POS).</summary>
public sealed record SaleLineDto(
    Guid Id, Guid ProductId, string Description, decimal Quantity, decimal UnitPrice,
    decimal Discount, decimal TaxAmount, decimal LineTotal, decimal? UnitCost, string? LotNumber);

public sealed record SalePaymentDto(Guid PaymentId, string Method, decimal Amount, DateTimeOffset ReceivedAt);

public sealed record SaleDto(
    Guid Id, string DocumentNumber, string Status, string SaleType, Guid? CustomerId,
    string Currency, DateTimeOffset SoldAt, decimal Subtotal, decimal DiscountTotal, decimal TaxTotal,
    decimal Total, decimal AmountPaid, decimal AmountDue, decimal ChangeGiven,
    Guid? SalesOrderId, Guid? CashSessionId,
    IReadOnlyList<SaleLineDto> Lines, IReadOnlyList<SalePaymentDto> Payments);
