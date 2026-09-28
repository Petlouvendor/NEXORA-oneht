namespace Nexora.Contracts.Purchasing;

// Lectures du cycle d'achat (finalisation produit, phase 3) : commande détaillée, réceptions, factures,
// retours, historique fournisseur, conditions de paiement. Montants calculés par le serveur.

public sealed record PurchaseOrderDetailLineDto(
    Guid Id, Guid ProductId, string Sku, string ProductName, string? BaseUnitCode,
    decimal QuantityOrdered, decimal QuantityReceived, decimal Remaining, decimal UnitPrice,
    Guid? TaxId, decimal Discount, decimal LineTotal, decimal TaxAmount,
    Guid? PurchaseUnitId, string? PurchaseUnitCode, decimal? PurchaseQuantity);

public sealed record PurchaseOrderDetailDto(
    Guid Id, string DocumentNumber, string Status, Guid SupplierId, string SupplierName, Guid? WarehouseId,
    string Currency, DateOnly OrderDate, DateOnly? ExpectedDate, string? Notes,
    decimal Subtotal, decimal TaxTotal, decimal Total, string? CreatedByName,
    IReadOnlyList<PurchaseOrderDetailLineDto> Lines,
    IReadOnlyList<GoodsReceiptSummaryDto> Receipts,
    IReadOnlyList<SupplierInvoiceSummaryDto> Invoices);

public sealed record GoodsReceiptSummaryDto(
    Guid Id, string DocumentNumber, Guid SupplierId, string SupplierName, Guid? PurchaseOrderId, string? PurchaseOrderNumber,
    Guid? LocationId, string? LocationName, DateTimeOffset ReceivedAt, int LineCount,
    decimal QuantityAccepted, decimal QuantityRejected, decimal Value, string Status, string? Notes, string? CreatedByName);

public sealed record GoodsReceiptLineDto(
    Guid Id, Guid ProductId, string Sku, string ProductName, string? UnitCode,
    decimal QuantityReceived, decimal QuantityRejected, string? RejectionReason, decimal UnitCost,
    string? LotNumber, DateOnly? ExpirationDate, string? PurchaseUnitCode, decimal? PurchaseQuantity, Guid? PurchaseOrderLineId);

public sealed record GoodsReceiptDetailDto(GoodsReceiptSummaryDto Receipt, IReadOnlyList<GoodsReceiptLineDto> Lines);

public sealed record SupplierInvoiceSummaryDto(
    Guid Id, string DocumentNumber, string? SupplierReference, Guid SupplierId, string SupplierName,
    Guid? PurchaseOrderId, string? PurchaseOrderNumber, Guid? GoodsReceiptId,
    DateOnly InvoiceDate, DateOnly? DueDate, string Currency,
    decimal Subtotal, decimal TaxTotal, decimal Total, string Status, string MatchStatus, bool PaymentBlocked,
    decimal? RemainingAmount);

public sealed record SupplierInvoiceLineDto(
    Guid Id, Guid? ProductId, string? Sku, string Description, decimal Quantity, decimal UnitPrice,
    decimal Discount, decimal LineTotal, Guid? TaxId);

public sealed record SupplierInvoiceDetailDto(SupplierInvoiceSummaryDto Invoice, IReadOnlyList<SupplierInvoiceLineDto> Lines);

public sealed record SupplierReturnSummaryDto(
    Guid Id, string DocumentNumber, Guid SupplierId, string SupplierName, string? LocationName, string? Reason,
    DateTimeOffset? ReturnedAt, string Currency, decimal Total, string Status, int LineCount);

/// <summary>Prix d'achat constatés à réception (historique des coûts, fournisseur de la réception).</summary>
public sealed record SupplierPriceHistoryDto(
    Guid ProductId, string Sku, string ProductName, DateTimeOffset EffectiveAt, decimal UnitCost, string Currency, string DocumentNumber);

/// <summary>Synthèse fournisseur : volume d'achat, dettes, conditions, dernières opérations.</summary>
public sealed record SupplierOverviewDto(
    Guid SupplierId, string Name, string? ContactName, string? Notes, decimal CreditLimit,
    Guid? PaymentTermsId, string? PaymentTermsName, int? PaymentTermsDays,
    string Currency, decimal PurchasedLast12Months, decimal OpenPayables, decimal OverduePayables,
    int OpenOrders, DateTimeOffset? LastReceiptAt,
    IReadOnlyList<SupplierPriceHistoryDto> PriceHistory);

public sealed record SupplierPaymentTermsDto(
    Guid Id, string Name, int Days, decimal EarlyPaymentDiscountRate, int EarlyPaymentDiscountDays,
    decimal LatePenaltyRate, string? Description);

public sealed record SavePaymentTermsRequest(
    string Name, int Days, decimal EarlyPaymentDiscountRate, int EarlyPaymentDiscountDays, decimal LatePenaltyRate, string? Description);

/// <summary>Conditions commerciales d'un fournisseur (limite de crédit, conditions de paiement, contact, notes).</summary>
public sealed record SupplierTermsRequest(decimal CreditLimit, Guid? PaymentTermsId, string? ContactName, string? Notes);
