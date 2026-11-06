namespace Nexora.Contracts.Finance;

// Gestion de la trésorerie et des dépenses (finalisation produit, phase 7).

/// <summary>Compte du plan comptable proposé dans un formulaire (jamais de solde).</summary>
public sealed record LedgerAccountOptionDto(Guid Id, string Code, string Name, string AccountType);

/// <summary>Création / modification d'un compte de trésorerie. Un nouveau compte part à zéro : l'argent y entre
/// par un transfert, un encaissement ou un rapprochement — jamais par un solde saisi sans écriture.</summary>
public sealed record SaveFinancialAccountRequest(
    string Name, string AccountType, string Currency, string? AccountNumberMasked, Guid? LedgerAccountId);

public sealed record FinancialAccountStatusRequest(string Status, string Reason);

public sealed record FinancialAccountDetailDto(
    Guid Id, string Name, string AccountType, string Currency, string? AccountNumberMasked,
    Guid? LedgerAccountId, string? LedgerAccountCode, string? LedgerAccountName,
    decimal Balance, string Status, int TransactionCount, DateTimeOffset? LastMovementAt);

/// <summary>Mouvement d'un compte : montant signé (+ entrée, − sortie), pièce d'origine, écriture comptable.</summary>
public sealed record FinancialTransactionDto(
    Guid Id, DateTimeOffset OccurredAt, string TransactionType, decimal Amount, string Currency,
    string? ReferenceType, Guid? ReferenceId, string? Label, string? CounterpartyAccountName,
    string? JournalEntryNumber, string Status, string? CreatedByName);

/// <summary>Solde d'ouverture d'un compte encore sans mouvement (audit 2026-10, A-023). Sans date : maintenant.</summary>
public sealed record TreasuryOpeningBalanceRequest(Guid OperationUuid, decimal Amount, DateOnly? Date = null, string? Note = null);

public sealed record TreasuryTransferRequest(Guid OperationUuid, Guid FromAccountId, Guid ToAccountId, decimal Amount, string? Note);

public sealed record TreasuryChargeRequest(Guid OperationUuid, Guid AccountId, decimal Amount, string Description);

/// <summary>Listes de référence des formulaires financiers (sans ouvrir le catalogue ni les achats au comptable).</summary>
public sealed record FinanceCurrencyOptionDto(string Code, string Name, bool IsCompanyDefault);
public sealed record FinanceSupplierOptionDto(Guid Id, string Name);
public sealed record FinanceLookupsDto(IReadOnlyList<FinanceCurrencyOptionDto> Currencies, IReadOnlyList<FinanceSupplierOptionDto> Suppliers);

// --- Dépenses ---------------------------------------------------------------

public sealed record ExpenseCategoryDto(Guid Id, string Name, string? Code, Guid? LedgerAccountId, string? LedgerAccountCode,
    string? LedgerAccountName, string Status);

public sealed record SaveExpenseCategoryRequest(string Name, string? Code, Guid? LedgerAccountId, string Status = "active");

/// <summary>Dépense : montant HT, taxe récupérable, fournisseur facultatif. <c>Submit</c> = soumise à approbation
/// tout de suite (sinon brouillon modifiable).</summary>
public sealed record SaveExpenseRequest(
    string Description, Guid CategoryId, Guid? SupplierId, decimal Amount, decimal TaxAmount, string Currency,
    DateOnly ExpenseDate, string? Reference, bool Submit = true);

public sealed record ExpenseDecisionRequest(string? Reason);

public sealed record PayExpenseRequest(Guid OperationUuid, Guid FinancialAccountId);

public sealed record ExpenseDto(
    Guid Id, string DocumentNumber, string Description, Guid? CategoryId, string? CategoryName,
    Guid? SupplierId, string? SupplierName, decimal Amount, decimal TaxAmount, decimal Total, string Currency,
    DateOnly ExpenseDate, string? Reference, string Status, Guid? FinancialAccountId, string? FinancialAccountName,
    Guid? CreatedBy, string? CreatedByName, string? ApprovedByName, DateTimeOffset? ApprovedAt,
    DateTimeOffset? PaidAt, string? PaidByName, string? JournalEntryNumber, string? DecisionReason, DateTimeOffset CreatedAt);
