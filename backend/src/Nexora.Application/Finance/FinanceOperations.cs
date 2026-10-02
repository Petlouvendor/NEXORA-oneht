using Nexora.Application.Finance.Abstractions;
using Nexora.Application.Finance.Engine;
using Nexora.Application.Finance.Events;
using Nexora.Application.Abstractions;
using Nexora.Contracts.Finance;
using Nexora.Domain.Common;

namespace Nexora.Application.Finance;

/// <summary>
/// Façade des opérations financières exposées à l'API (Phase 4 §16 : point d'entrée unique,
/// POS / Achats / Dépenses passent par ici et ne dupliquent aucune règle).
/// </summary>
public interface IFinanceOperations
{
    Task<Result<PostingResultResponse>> RecordCashSaleAsync(RecordCashSaleRequest r, CancellationToken ct = default);
    Task<Result<PostingResultResponse>> RecordCreditSaleAsync(RecordCreditSaleRequest r, CancellationToken ct = default);
    Task<Result<PostingResultResponse>> RecordSupplierInvoiceAsync(RecordSupplierInvoiceRequest r, CancellationToken ct = default);
    Task<Result<PostingResultResponse>> RecordCashPurchaseAsync(RecordCashPurchaseRequest r, CancellationToken ct = default);
    Task<Result<PostingResultResponse>> RecordExpenseAsync(RecordExpenseRequest r, CancellationToken ct = default);
    Task<Result<PostingResultResponse>> RecordCustomerPaymentAsync(RecordPaymentRequest r, CancellationToken ct = default);
    Task<Result<PostingResultResponse>> RecordSupplierPaymentAsync(RecordPaymentRequest r, CancellationToken ct = default);
    Task<Result<PostingResultResponse>> RecordTransferAsync(RecordTransferRequest r, CancellationToken ct = default);
    Task<Result<PostingResultResponse>> RecordBankChargeAsync(RecordBankChargeRequest r, CancellationToken ct = default);
    Task<Result<PostingResultResponse>> RecordCashCountAsync(RecordCashCountRequest r, CancellationToken ct = default);
    Task<Result<PostingResultResponse>> RecordSupplierReturnAsync(RecordSupplierReturnRequest r, CancellationToken ct = default);
}

/// <summary>
/// Pièce de dépense (table <c>expense</c>) : ce que lisent les KPI « Dépenses » / « Bénéfice », les
/// rapports de dépenses et l'insight « hausse des dépenses ». Idempotente par identifiant.
/// </summary>
public interface IExpenseRegister
{
    /// <summary>Rattache l'écriture comptable à la pièce de dépense (phase 7).</summary>
    Task LinkJournalEntryAsync(Guid expenseId, Guid journalEntryId, CancellationToken ct = default);
    Task RegisterPaidAsync(Guid expenseId, DateOnly date, decimal net, decimal tax, string currency,
        Guid financialAccountId, string? description, CancellationToken ct = default);
}

public sealed class FinanceOperations(
    IFinancialEngine engine,
    IAccountResolver accounts,
    IExpenseRegister expenses,
    IUnitOfWork uow,
    ITenantContext tenant) : IFinanceOperations
{
    private Guid Company => tenant.ActiveCompanyId
        ?? throw new InvalidOperationException("Aucune entreprise active pour l'opération financière.");

    public Task<Result<PostingResultResponse>> RecordCashSaleAsync(RecordCashSaleRequest r, CancellationToken ct) =>
        Run(new CashSaleRecorded(Company, r.OperationUuid, r.OccurredAt, r.SaleId,
            new Money(r.NetAmount, r.Currency), new Money(r.TaxAmount, r.Currency),
            r.TenderFinancialAccountId,
            r.CostOfGoods is { } c ? new Money(c, r.Currency) : null) { UserId = tenant.UserId }, ct);

    public Task<Result<PostingResultResponse>> RecordCreditSaleAsync(RecordCreditSaleRequest r, CancellationToken ct) =>
        Run(new CreditSaleRecorded(Company, r.OperationUuid, r.OccurredAt, r.SaleId, r.CustomerId,
            new Money(r.NetAmount, r.Currency), new Money(r.TaxAmount, r.Currency), r.DueDate,
            r.CostOfGoods is { } c ? new Money(c, r.Currency) : null) { UserId = tenant.UserId }, ct);

    public Task<Result<PostingResultResponse>> RecordSupplierInvoiceAsync(RecordSupplierInvoiceRequest r, CancellationToken ct) =>
        Run(new SupplierInvoiceRecorded(Company, r.OperationUuid, r.OccurredAt, r.SupplierInvoiceId, r.SupplierId,
            new Money(r.NetAmount, r.Currency), new Money(r.TaxAmount, r.Currency), r.DueDate, r.CapitaliseToInventory)
            { UserId = tenant.UserId }, ct);

    public Task<Result<PostingResultResponse>> RecordCashPurchaseAsync(RecordCashPurchaseRequest r, CancellationToken ct) =>
        Run(new CashPurchaseRecorded(Company, r.OperationUuid, r.OccurredAt, r.ReferenceId,
            new Money(r.NetAmount, r.Currency), new Money(r.TaxAmount, r.Currency),
            r.TenderFinancialAccountId, r.CapitaliseToInventory) { UserId = tenant.UserId }, ct);

    /// <summary>
    /// Recette BUG-016 / résumé fonctionnel §11 : la pièce de dépense ET l'écriture sont créées
    /// ensemble, dans une seule transaction — jusque-là seule l'écriture existait, et les KPI
    /// « Dépenses » / « Bénéfice » restaient à zéro quelles que soient les dépenses réelles.
    /// </summary>
    public async Task<Result<PostingResultResponse>> RecordExpenseAsync(RecordExpenseRequest r, CancellationToken ct)
    {
        Guid? expenseAccount = null;
        if (!string.IsNullOrWhiteSpace(r.ExpenseAccountCode))
            expenseAccount = await accounts.ResolveByCodeAsync(r.ExpenseAccountCode, ct);

        try
        {
            return await uow.ExecuteInTransactionAsync<Result<PostingResultResponse>>(async token =>
            {
                await expenses.RegisterPaidAsync(r.ExpenseId, DateOnly.FromDateTime(r.OccurredAt.UtcDateTime), r.NetAmount, r.TaxAmount,
                    r.Currency, r.TenderFinancialAccountId, r.Description, token);
                var posted = await Run(new ExpenseRecorded(Company, r.OperationUuid, r.OccurredAt, r.ExpenseId,
                    new Money(r.NetAmount, r.Currency), new Money(r.TaxAmount, r.Currency),
                    r.TenderFinancialAccountId, expenseAccount) { UserId = tenant.UserId }, token);
                // Écriture refusée → la pièce ne doit pas rester seule : annulation de la transaction.
                if (!posted.IsSuccess) throw new ExpenseNotPostedException(posted.Error!);
                await expenses.LinkJournalEntryAsync(r.ExpenseId, posted.Value!.JournalEntryId, token);
                return posted;
            }, ct);
        }
        catch (ExpenseNotPostedException ex)
        {
            return ex.Error;
        }
    }

    private sealed class ExpenseNotPostedException(Error error) : Exception(error.Message)
    {
        public Error Error { get; } = error;
    }

    public Task<Result<PostingResultResponse>> RecordCustomerPaymentAsync(RecordPaymentRequest r, CancellationToken ct) =>
        Run(new CustomerPaymentReceived(Company, r.OperationUuid, r.OccurredAt, r.PartyId,
            new Money(r.Amount, r.Currency), r.TenderFinancialAccountId,
            r.Allocations.Select(a => new AllocationInstruction(a.TargetId, a.Amount, a.IsReceivable)).ToArray(),
            r.Reference) { UserId = tenant.UserId }, ct);

    public Task<Result<PostingResultResponse>> RecordSupplierPaymentAsync(RecordPaymentRequest r, CancellationToken ct) =>
        Run(new SupplierPaymentMade(Company, r.OperationUuid, r.OccurredAt, r.PartyId,
            new Money(r.Amount, r.Currency), r.TenderFinancialAccountId,
            r.Allocations.Select(a => new AllocationInstruction(a.TargetId, a.Amount, a.IsReceivable)).ToArray(),
            r.Reference) { UserId = tenant.UserId }, ct);

    public Task<Result<PostingResultResponse>> RecordTransferAsync(RecordTransferRequest r, CancellationToken ct) =>
        Run(new FinancialTransferRecorded(Company, r.OperationUuid, r.OccurredAt,
            r.FromFinancialAccountId, r.ToFinancialAccountId, new Money(r.Amount, r.Currency)) { UserId = tenant.UserId }, ct);

    public Task<Result<PostingResultResponse>> RecordBankChargeAsync(RecordBankChargeRequest r, CancellationToken ct) =>
        Run(new BankChargeRecorded(Company, r.OperationUuid, r.OccurredAt,
            r.BankFinancialAccountId, new Money(r.Amount, r.Currency), r.Description) { UserId = tenant.UserId }, ct);

    public Task<Result<PostingResultResponse>> RecordCashCountAsync(RecordCashCountRequest r, CancellationToken ct) =>
        Run(new CashCountRecorded(Company, r.OperationUuid, r.OccurredAt, r.CashSessionId, r.CashFinancialAccountId,
            new Money(r.ExpectedAmount, r.Currency), new Money(r.CountedAmount, r.Currency)) { UserId = tenant.UserId }, ct);

    public Task<Result<PostingResultResponse>> RecordSupplierReturnAsync(RecordSupplierReturnRequest r, CancellationToken ct) =>
        Run(new SupplierReturnRecorded(Company, r.OperationUuid, r.OccurredAt, r.SupplierReturnId, r.SupplierId,
            new Money(r.NetAmount, r.Currency), new Money(r.TaxAmount, r.Currency), r.AgainstPayable) { UserId = tenant.UserId }, ct);

    private async Task<Result<PostingResultResponse>> Run(FinancialEvent evt, CancellationToken ct)
    {
        var result = await engine.HandleAsync(evt, ct);
        if (!result.IsSuccess) return result.Error!;
        var p = result.Value!;
        return Result.Success(new PostingResultResponse(
            p.JournalEntryId, p.EntryNumber, p.PaymentId, p.ReceivableId, p.PayableId, p.WasReplay));
    }
}
