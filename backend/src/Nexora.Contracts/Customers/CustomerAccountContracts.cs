namespace Nexora.Contracts.Customers;

// =============================================================================
// Module « Gestion clients & comptes clients » (docs/module-clients-comptes-clients.md).
// Le crédit (§8) est la créance client existante (customer_receivable) ; le versement (§9) est le
// paiement existant (payment + payment_allocation). Tous les montants sont calculés par le serveur.
// =============================================================================

// --- Recherche POS (§17) : uniquement les champs utiles à la vente -------------------------

/// <summary><c>PhoneHint</c> = 4 derniers chiffres ; <c>AvailableCredit</c> = <c>null</c> sans
/// <c>pos.view_customer_balance</c>.</summary>
public sealed record CustomerLookupDto(
    Guid Id, string CustomerNumber, string DisplayName, string? PhoneHint, string Status,
    bool CreditEnabled, decimal? AvailableCredit, string Currency, decimal? AvailableStoreCredit = null);

// --- Tableau de bord client (§15) ------------------------------------------------------------

/// <summary>Signal explicable (§27) : la règle et les données qui l'ont déclenché sont exposées ;
/// aucun signal ne modifie un solde ni n'exécute d'opération.</summary>
public sealed record CustomerSignalDto(
    string Code, string Level, string Title, string Explanation, IReadOnlyDictionary<string, string> Data);

public sealed record CustomerAccountSummaryDto(
    Guid CustomerId, string CustomerNumber, string Name, string Status, string Currency,
    DateOnly? PeriodFrom, DateOnly? PeriodTo,
    decimal TotalPurchases, int PurchaseCount, decimal CreditPurchases,
    decimal TotalPaid, decimal BalanceDue, decimal CreditLimit, decimal AvailableCredit,
    decimal ExpensesBilled, decimal ExpensesNotBilled, decimal ReturnsAmount, decimal ReturnsQuantity,
    decimal AvailableStoreCredit, decimal NetBalance, decimal OverdueAmount, int OverdueCredits, int OldestOverdueDays,
    DateTimeOffset? LastPaymentAt, decimal? LastPaymentAmount,
    decimal LedgerBalance, bool LedgerConsistent,
    int OpenPromises, int OpenDisputes, bool Blocked, int PendingExpenses, int PendingRefunds,
    IReadOnlyList<CustomerSignalDto> Signals,
    IReadOnlyList<CurrencyBalanceDto> Balances, decimal TotalExposure, bool ExchangeRateMissing);

/// <summary>Solde par devise (§21 « devise explicite ») : jamais deux devises additionnées ;
/// <c>ExposureInCustomerCurrency</c> = conversion au dernier taux connu (null si aucun taux).</summary>
public sealed record CurrencyBalanceDto(
    string Currency, decimal BalanceDue, decimal OverdueAmount, decimal AvailableStoreCredit, decimal NetBalance,
    decimal LedgerBalance, bool LedgerConsistent, decimal? ExposureInCustomerCurrency);

// --- Journal financier (§7) ------------------------------------------------------------------

public sealed record CustomerLedgerEntryDto(
    Guid Id, DateTimeOffset OccurredAt, string EntryType, string Label, decimal Debit, decimal Credit, decimal Balance,
    string Currency, string? Reference, string? Description, Guid? ReceivableId, Guid? PaymentId, Guid? CreditMemoId,
    string? SourceType, Guid? SourceId, string? CreatedByName, string? EstablishmentName);

public sealed record CustomerLedgerDto(
    Guid CustomerId, string Currency, DateOnly? From, DateOnly? To,
    decimal OpeningBalance, decimal TotalDebit, decimal TotalCredit, decimal ClosingBalance,
    IReadOnlyList<CustomerLedgerEntryDto> Entries);

// --- Crédits (§8) ----------------------------------------------------------------------------

/// <summary><c>DisplayStatus</c> : open / partial / paid / overdue / blocked (en litige) / cancelled /
/// written_off — « en retard » est dérivé de l'échéance, jamais stocké.</summary>
public sealed record CustomerCreditDto(
    Guid Id, string CreditNumber, Guid CustomerId, string CustomerName, string SourceType, Guid? SourceId,
    string? SourceReference, string Currency, decimal OriginalAmount, decimal PaidAmount, decimal RemainingAmount,
    DateOnly IssueDate, DateOnly? DueDate, string Status, string DisplayStatus, int DaysOverdue,
    string? EstablishmentName, string? CreatedByName, DateTimeOffset CreatedAt, bool HasSchedule = false);

/// <summary>Règlement d'un crédit : versement, avoir imputé ou radiation — chacun conservé, même annulé.</summary>
public sealed record CreditSettlementDto(
    string Kind, Guid? PaymentId, Guid? CreditMemoId, string Reference, DateTimeOffset At, decimal Amount,
    bool Voided, DateTimeOffset? VoidedAt, string? Method, string? ReceivedByName, string? Note);

public sealed record CustomerCreditDetailDto(
    CustomerCreditDto Credit, IReadOnlyList<CreditSettlementDto> Settlements, CreditScheduleDto? Schedule = null);

// --- Échéancier (§8 « échéance ou calendrier », §20 credit_installments) ---------------------

/// <summary><c>Status</c> : paid / partial / overdue / upcoming — la couverture est dérivée du réglé du crédit.</summary>
public sealed record CreditInstallmentDto(int Number, DateOnly DueDate, decimal Amount, decimal Covered, decimal Remaining, string Status);

public sealed record CreditScheduleDto(
    Guid Id, int Version, decimal PaidBefore, decimal TotalAmount, string Reason, string? CreatedByName,
    DateTimeOffset CreatedAt, IReadOnlyList<CreditInstallmentDto> Installments);

public sealed record InstallmentInput(DateOnly DueDate, decimal Amount);

/// <summary>Échéancier du reste dû : lignes explicites, ou <c>Count</c> échéances égales à partir de
/// <c>FirstDueDate</c>, mensuelles ou tous les <c>IntervalDays</c> jours.</summary>
public sealed record ScheduleInstallmentsRequest(
    string Reason, IReadOnlyList<InstallmentInput>? Installments = null, int? Count = null,
    DateOnly? FirstDueDate = null, bool Monthly = true, int IntervalDays = 30);

// --- Crédit hors vente (§22 POST /customers/{id}/credits) --------------------------------

/// <summary>Reprise de solde : dette antérieure du client, justifiée ; contrepartie en compte d'attente
/// jusqu'au rapprochement comptable.</summary>
public sealed record OpenCustomerCreditRequest(
    Guid OperationUuid, decimal Amount, string Reason, DateOnly? DueDate = null, string? Currency = null);

// --- Dépassement de crédit (§12 « workflow d'approbation configuré ») -----------------------

public sealed record CreditOverrideRequestInput(
    Guid CartOperationUuid, Guid CustomerId, decimal Amount, string Currency, string Reason);

public sealed record CreditOverrideDto(
    Guid Id, Guid CartOperationUuid, Guid CustomerId, string CustomerName, decimal Amount, string Currency, string Reason,
    string? LimitMessage, string Status, int CurrentLevel, int RequiredLevels, string? RequestedByName,
    DateTimeOffset RequestedAt, string? DecidedByName, string? DecisionNote, bool Consumed);

// --- Versements (§9) -------------------------------------------------------------------------

public sealed record PaymentAllocationLineDto(Guid ReceivableId, string CreditNumber, decimal Amount, bool Voided);

public sealed record CustomerPaymentDto(
    Guid Id, string PaymentNumber, Guid CustomerId, string CustomerName, decimal Amount, decimal AllocatedAmount,
    decimal UnallocatedAmount, string Currency, string Status, DateTimeOffset ReceivedAt,
    string? PaymentMethod, string? FinancialAccountName, Guid? CashSessionId, string? Reference, string? Note,
    string? ReceivedByName, DateTimeOffset? VoidedAt, string? VoidedByName, string? VoidReason,
    Guid? CreditMemoId, string? CreditMemoNumber, IReadOnlyList<PaymentAllocationLineDto> Allocations);

public sealed record PaymentAllocationInput(Guid ReceivableId, decimal Amount);

/// <summary>Comptes et moyens utilisables pour encaisser / rembourser — sans aucun solde de trésorerie.</summary>
public sealed record PaymentAccountOptionDto(Guid Id, string Name, string AccountType, string Currency);
public sealed record PaymentMethodOptionDto(Guid Id, string Name, string Type);
public sealed record CustomerPaymentOptionsDto(
    IReadOnlyList<PaymentAccountOptionDto> Accounts, IReadOnlyList<PaymentMethodOptionDto> Methods);

/// <summary>Versement sur le compte : réparti sur les crédits indiqués, sinon sur les plus anciens
/// (échéance puis date). Un excédent suit <c>credit.overpayment_policy</c> (refus ou avoir).</summary>
public sealed record RecordCustomerPaymentRequest(
    Guid OperationUuid, decimal Amount, Guid FinancialAccountId, Guid? PaymentMethodId = null,
    Guid? CashSessionId = null, string? Reference = null, string? Note = null,
    IReadOnlyList<PaymentAllocationInput>? Allocations = null, string? Currency = null);

/// <summary>Versement sur un crédit précis (§22 <c>POST /credits/{id}/payments</c>).</summary>
public sealed record RecordCreditPaymentRequest(
    Guid OperationUuid, decimal Amount, Guid FinancialAccountId, Guid? PaymentMethodId = null,
    Guid? CashSessionId = null, string? Reference = null, string? Note = null);

public sealed record VoidPaymentRequest(string Reason);

// --- Avoirs & remboursements (§11, §29) ------------------------------------------------------

public sealed record CreditMemoUseDto(string Kind, string Reference, decimal Amount, DateTimeOffset At, bool Voided);

public sealed record CreditMemoDto(
    Guid Id, string MemoNumber, Guid CustomerId, string CustomerName, string SourceType, string? SourceReference,
    string Currency, decimal Amount, decimal UsedAmount, decimal RemainingAmount, string Status, string? Reason,
    string? CreatedByName, DateTimeOffset CreatedAt, IReadOnlyList<CreditMemoUseDto> Uses);

/// <summary>Imputer un avoir disponible : sur le crédit indiqué, sinon sur les plus anciens.</summary>
public sealed record ApplyCreditMemoRequest(Guid OperationUuid, Guid? ReceivableId = null, decimal? Amount = null);

public sealed record ApplyCreditMemoResultDto(
    Guid CreditMemoId, decimal AppliedAmount, decimal RemainingAmount,
    IReadOnlyList<PaymentAllocationLineDto> Applications, Guid? JournalEntryId);

public sealed record CustomerRefundDto(
    Guid Id, string RefundNumber, Guid CustomerId, string CustomerName, Guid CreditMemoId, string MemoNumber,
    string Currency, decimal Amount, string RefundMethod, Guid? FinancialAccountId, string Status, string Reason,
    string? RequestedByName, DateTimeOffset RequestedAt, string? DecidedByName, DateTimeOffset? DecidedAt,
    string? DecisionNote, string? ExecutedByName, DateTimeOffset? ExecutedAt, Guid? JournalEntryId);

public sealed record RequestCustomerRefundRequest(
    Guid OperationUuid, Guid CreditMemoId, decimal Amount, string RefundMethod, string Reason);

public sealed record ExecuteCustomerRefundRequest(Guid FinancialAccountId, Guid? CashSessionId = null);

public sealed record CustomerDecisionRequest(string? Note);

public sealed record CustomerCancelRequest(string Reason);

// --- Dépenses imputées (§10) -----------------------------------------------------------------

public sealed record CustomerExpenseDto(
    Guid Id, string ExpenseNumber, Guid CustomerId, string CustomerName, string CustomerNumber,
    string Category, string Description, decimal Amount, string Currency, DateOnly ExpenseDate, bool Billable,
    DateOnly? DueDate, string Status, Guid? DocumentId, Guid? ReceivableId, string? CreditNumber,
    string? CreatedByName, DateTimeOffset CreatedAt, DateTimeOffset? SubmittedAt,
    string? DecidedByName, DateTimeOffset? DecidedAt, string? DecisionNote,
    string? CancelledByName, DateTimeOffset? CancelledAt, string? CancelReason);

/// <summary><c>Submit</c> = transmettre directement pour validation (sinon brouillon).</summary>
public sealed record CreateCustomerExpenseRequest(
    Guid OperationUuid, string Category, string Description, decimal Amount, DateOnly? ExpenseDate = null,
    bool Billable = true, DateOnly? DueDate = null, Guid? DocumentId = null, bool Submit = true);

// --- Recouvrement (§13) ----------------------------------------------------------------------

public sealed record CollectionActionDto(
    Guid Id, Guid CustomerId, string CustomerName, Guid? ReceivableId, string? CreditNumber, string ActionType,
    string? Outcome, string Note, DateOnly? NextActionDate, string? CreatedByName, DateTimeOffset CreatedAt);

public sealed record CreateCollectionActionRequest(
    string ActionType, string Note, string? Outcome = null, Guid? ReceivableId = null, DateOnly? NextActionDate = null);

// --- Achats & historique (§14) ---------------------------------------------------------------

public sealed record CustomerPurchaseDto(
    Guid SaleId, string DocumentNumber, DateTimeOffset SoldAt, string SaleType, string Status, decimal Total,
    decimal PaidAtSale, decimal CreditAmount, decimal ReturnedAmount, string Currency,
    string? EstablishmentName, string? CashierName, Guid? ReceivableId, string? CreditNumber);

public sealed record CustomerTimelineEventDto(
    DateTimeOffset At, string Category, string Title, string? Detail, decimal? Amount, string? Currency,
    string? Reference, string? ActorName, string? LinkType, Guid? LinkId);

// --- Rapports (§26) --------------------------------------------------------------------------

public sealed record CustomerReportDefinitionDto(string Key, string Title, string Description);

public sealed record CustomerReportDto(
    string Key, string Title, DateOnly? From, DateOnly? To, IReadOnlyList<string> Columns,
    IReadOnlyList<IReadOnlyList<string?>> Rows, IReadOnlyDictionary<string, decimal> Totals);
