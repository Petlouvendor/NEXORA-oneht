namespace Nexora.Contracts.Finance;

public sealed record AllocationInput(Guid TargetId, decimal Amount, bool IsReceivable);

public sealed record RecordCashSaleRequest(
    Guid OperationUuid, Guid SaleId, DateTimeOffset OccurredAt,
    decimal NetAmount, decimal TaxAmount, string Currency,
    Guid TenderFinancialAccountId, decimal? CostOfGoods);

public sealed record RecordCreditSaleRequest(
    Guid OperationUuid, Guid SaleId, Guid CustomerId, DateTimeOffset OccurredAt,
    decimal NetAmount, decimal TaxAmount, string Currency, DateOnly? DueDate, decimal? CostOfGoods);

public sealed record RecordSupplierInvoiceRequest(
    Guid OperationUuid, Guid SupplierInvoiceId, Guid SupplierId, DateTimeOffset OccurredAt,
    decimal NetAmount, decimal TaxAmount, string Currency, DateOnly? DueDate, bool CapitaliseToInventory);

public sealed record RecordCashPurchaseRequest(
    Guid OperationUuid, Guid ReferenceId, DateTimeOffset OccurredAt,
    decimal NetAmount, decimal TaxAmount, string Currency, Guid TenderFinancialAccountId, bool CapitaliseToInventory);

/// <summary>Dépense payée : crée la pièce <c>expense</c> (id = <paramref name="ExpenseId"/>) ET son écriture.</summary>
public sealed record RecordExpenseRequest(
    Guid OperationUuid, Guid ExpenseId, DateTimeOffset OccurredAt,
    decimal NetAmount, decimal TaxAmount, string Currency,
    Guid TenderFinancialAccountId, string? ExpenseAccountCode, string? Description = null);

public sealed record RecordPaymentRequest(
    Guid OperationUuid, DateTimeOffset OccurredAt, Guid PartyId,
    decimal Amount, string Currency, Guid TenderFinancialAccountId,
    IReadOnlyList<AllocationInput> Allocations, string? Reference);

public sealed record RecordTransferRequest(
    Guid OperationUuid, DateTimeOffset OccurredAt,
    Guid FromFinancialAccountId, Guid ToFinancialAccountId, decimal Amount, string Currency);

public sealed record RecordBankChargeRequest(
    Guid OperationUuid, DateTimeOffset OccurredAt,
    Guid BankFinancialAccountId, decimal Amount, string Currency, string? Description);

public sealed record RecordCashCountRequest(
    Guid OperationUuid, DateTimeOffset OccurredAt,
    Guid CashSessionId, Guid CashFinancialAccountId,
    decimal ExpectedAmount, decimal CountedAmount, string Currency);

public sealed record RecordSupplierReturnRequest(
    Guid OperationUuid, Guid SupplierReturnId, Guid SupplierId, DateTimeOffset OccurredAt,
    decimal NetAmount, decimal TaxAmount, string Currency, bool AgainstPayable);

public sealed record ReverseEntryRequest(string Reason, DateOnly? ReversalDate);

public sealed record PostingResultResponse(
    Guid JournalEntryId, string EntryNumber, Guid? PaymentId, Guid? ReceivableId, Guid? PayableId, bool WasReplay);
