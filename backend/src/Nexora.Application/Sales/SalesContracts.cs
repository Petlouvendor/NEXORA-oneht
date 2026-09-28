namespace Nexora.Application.Sales;

public sealed record CheckoutLine(
    Guid ProductId, decimal Quantity, decimal? UnitPriceOverride, decimal LineDiscount, Guid? TaxId);

public sealed record TenderInput(
    Guid FinancialAccountId, Guid? PaymentMethodId, decimal Amount, string? Reference);

/// <summary>Compte d'encaissement vu par le contrôle des paiements (phase 5).</summary>
public sealed record TenderAccountInfo(Guid Id, string Name, string AccountType, string Currency, string Status);

/// <summary>Moyen de paiement vu par le contrôle des paiements (phase 5).</summary>
public sealed record TenderMethodInfo(Guid Id, string Name, string Type, string? Currency, bool RequiresReference, string Status);

public sealed record CheckoutCommand(
    Guid OperationUuid,
    Guid InventoryLocationId,
    Guid? StoreId,
    Guid? PosTerminalId,
    Guid? CashSessionId,
    Guid? CustomerId,
    string Currency,
    IReadOnlyList<CheckoutLine> Lines,
    IReadOnlyList<TenderInput> Tenders,
    decimal AmountTendered,
    DateOnly? CreditDueDate,
    string? CouponCode,
    string? CreditOverrideReason,
    DateTimeOffset? SoldAt,
    // Module clients §7, §15 : part payée avec l'avoir disponible du client (imputé par le serveur).
    decimal StoreCreditAmount = 0m,
    // Phase 4 : acompte d'une commande — ses avoirs sont imputés en premier ; le montant est plafonné au
    // total de la vente (le total exact n'est connu qu'après tarification).
    Guid? StoreCreditSalesOrderId = null,
    bool CapStoreCreditToTotal = false);

public sealed record CheckoutLineResult(
    Guid ProductId, decimal Quantity, decimal UnitPrice, decimal Discount, decimal TaxAmount, decimal LineTotal);

public sealed record CheckoutResult(
    Guid SaleId, string DocumentNumber, string SaleType,
    decimal Subtotal, decimal DiscountTotal, decimal TaxTotal, decimal Total,
    decimal AmountPaid, decimal CreditAmount, decimal ChangeGiven,
    Guid JournalEntryId, Guid? ReceivableId, bool CreditOverrideApplied,
    IReadOnlyList<CheckoutLineResult> Lines, decimal StoreCreditUsed = 0m);

public sealed record ParkCartCommand(
    Guid InventoryLocationId, Guid? CustomerId, string Currency, string Reference,
    IReadOnlyList<CheckoutLine> Lines);

// --- Retours (§22, §23) --------------------------------------------------

public sealed record ReturnLineInput(Guid SaleLineId, decimal Quantity, bool Restock);
public sealed record ReturnCommand(
    Guid OperationUuid, Guid SaleId, Guid InventoryLocationId, Guid? CashSessionId,
    string Reason, string RefundMethod, Guid? RefundFinancialAccountId,
    IReadOnlyList<ReturnLineInput> Lines);
public sealed record ReturnResult(
    Guid SaleReturnId, string DocumentNumber, decimal RefundNet, decimal RefundTax, decimal RefundTotal,
    Guid JournalEntryId);

// --- Sessions de caisse (§25) --------------------------------------------

public sealed record OpenSessionCommand(Guid CashRegisterId, Guid? PosTerminalId, decimal OpeningBalance);
public sealed record CloseSessionCommand(Guid SessionId, decimal CountedBalance, string? Notes);
public sealed record CashInOutCommand(Guid SessionId, string MovementType, decimal Amount, string? Note);

public sealed record ValidateCloseCommand(Guid SessionId, string? Note);

public sealed record SessionSummary(
    Guid SessionId, string Status, decimal OpeningBalance, decimal ExpectedBalance,
    decimal? CountedBalance, decimal? Difference, int SaleCount, decimal CashSales);
