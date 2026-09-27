namespace Nexora.Contracts.Purchasing;

// --- Rapprochement 3-way (Phase 26 §20) -------------------------------

public sealed record MatchInvoiceRequest(decimal QuantityTolerancePercent, decimal PriceTolerancePercent);

public sealed record MatchLineDto(
    Guid ProductId, string Description,
    decimal OrderedQuantity, decimal ReceivedQuantity, decimal InvoicedQuantity,
    decimal OrderedUnitPrice, decimal InvoicedUnitPrice,
    decimal QuantityVariancePercent, decimal PriceVariancePercent,
    string Status, string? Detail);

public sealed record ThreeWayMatchReport(
    Guid InvoiceId, string InvoiceNumber, Guid? PurchaseOrderId, string MatchStatus,
    bool PaymentBlocked, string? BlockReason, IReadOnlyList<MatchLineDto> Lines);

// --- Dettes fournisseurs (Phase 26 §24-27, §36) ---------------------

public sealed record PayableDto(
    Guid Id, Guid? SupplierInvoiceId, string? InvoiceNumber, string? SupplierReference,
    Guid SupplierId, string SupplierName, string Currency,
    decimal OriginalAmount, decimal PaidAmount, decimal RemainingAmount,
    DateOnly IssueDate, DateOnly? DueDate, int DaysOverdue, string Status,
    bool PaymentBlocked, string? BlockReason);

public sealed record PayPayableRequest(Guid OperationUuid, decimal Amount, Guid TenderFinancialAccountId, string? Reference);

// --- Relevé fournisseur (Phase 26 §36, §46) ------------------------

public sealed record SupplierStatementLineDto(
    DateOnly Date, string DocumentType, string DocumentNumber, string? Reference,
    decimal Charge, decimal Payment, decimal RunningBalance);

public sealed record SupplierStatementDto(
    Guid SupplierId, string SupplierName, string Currency,
    decimal OpeningBalance, decimal ClosingBalance, decimal TotalOverdue,
    IReadOnlyList<SupplierStatementLineDto> Lines);

// --- Commande fournisseur — détail (Phase 26 §9-11) ----------------

public sealed record PurchaseOrderLineDto(
    Guid Id, Guid ProductId, string Description, decimal QuantityOrdered, decimal QuantityReceived,
    decimal UnitPrice, decimal Discount, decimal LineTotal);

public sealed record PurchaseOrderDto(
    Guid Id, string DocumentNumber, string Status, Guid SupplierId, string SupplierName,
    string Currency, DateOnly OrderDate, DateOnly? ExpectedDate,
    decimal Subtotal, decimal TaxTotal, decimal Total,
    IReadOnlyList<PurchaseOrderLineDto> Lines);
