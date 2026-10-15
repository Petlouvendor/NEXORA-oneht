using Nexora.Contracts.Finance;
using Nexora.Domain.Common;

namespace Nexora.Application.Finance;

/// <summary>
/// Comptes de trésorerie et mouvements entre comptes (finalisation produit, phase 7). Les écritures restent
/// produites par le moteur financier (<see cref="IFinanceOperations"/>) : ce service ajoute les contrôles
/// métier que l'écriture brute ne faisait pas (devise, compte actif, solde suffisant, compte identique).
/// </summary>
public interface ITreasuryManagementService
{
    Task<IReadOnlyList<FinancialAccountDetailDto>> AccountsAsync(CancellationToken ct = default);
    Task<Result<Guid>> CreateAccountAsync(SaveFinancialAccountRequest request, CancellationToken ct = default);
    Task<Result> UpdateAccountAsync(Guid id, SaveFinancialAccountRequest request, CancellationToken ct = default);
    Task<Result> SetAccountStatusAsync(Guid id, FinancialAccountStatusRequest request, CancellationToken ct = default);
    Task<Result<IReadOnlyList<FinancialTransactionDto>>> TransactionsAsync(Guid accountId, DateOnly? from, DateOnly? to, int limit, CancellationToken ct = default);
    Task<IReadOnlyList<LedgerAccountOptionDto>> LedgerOptionsAsync(string accountType, CancellationToken ct = default);
    Task<FinanceLookupsDto> LookupsAsync(CancellationToken ct = default);
    Task<Result<Contracts.Finance.PostingResultResponse>> TransferAsync(TreasuryTransferRequest request, CancellationToken ct = default);
    /// <summary>Solde d'ouverture d'un compte sans mouvement : trésorerie + écriture contre le report à nouveau.</summary>
    Task<Result<Contracts.Finance.PostingResultResponse>> RecordOpeningBalanceAsync(Guid accountId, TreasuryOpeningBalanceRequest request,
        CancellationToken ct = default);
    Task<Result<Contracts.Finance.PostingResultResponse>> ChargeAsync(TreasuryChargeRequest request, CancellationToken ct = default);
}

/// <summary>
/// Dépenses (phase 7) : brouillon → soumise → approuvée (par un autre que le demandeur) → payée (écriture
/// Dr charge / Dr taxe récupérable / Cr trésorerie) ; refus et annulation motivés. Rien n'est payé deux fois.
/// </summary>
public interface IExpenseService
{
    Task<IReadOnlyList<ExpenseCategoryDto>> CategoriesAsync(CancellationToken ct = default);
    Task<Result<Guid>> CreateCategoryAsync(SaveExpenseCategoryRequest request, CancellationToken ct = default);
    Task<Result> UpdateCategoryAsync(Guid id, SaveExpenseCategoryRequest request, CancellationToken ct = default);

    Task<IReadOnlyList<ExpenseDto>> ListAsync(string? status, DateOnly? from, DateOnly? to, int limit, CancellationToken ct = default);
    Task<Result<ExpenseDto>> GetAsync(Guid id, CancellationToken ct = default);
    Task<Result<Guid>> CreateAsync(SaveExpenseRequest request, CancellationToken ct = default);
    Task<Result> UpdateAsync(Guid id, SaveExpenseRequest request, CancellationToken ct = default);
    Task<Result> SubmitAsync(Guid id, CancellationToken ct = default);
    Task<Result> ApproveAsync(Guid id, CancellationToken ct = default);
    Task<Result> RejectAsync(Guid id, ExpenseDecisionRequest request, CancellationToken ct = default);
    Task<Result> CancelAsync(Guid id, ExpenseDecisionRequest request, CancellationToken ct = default);
    Task<Result<ExpenseDto>> PayAsync(Guid id, PayExpenseRequest request, CancellationToken ct = default);
}
