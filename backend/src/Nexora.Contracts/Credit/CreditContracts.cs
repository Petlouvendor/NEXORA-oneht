namespace Nexora.Contracts.Credit;

// --- Promesses de paiement (Phase 27 §24) -----------------------------

public sealed record CreatePromiseRequest(
    Guid CustomerId, decimal Amount, DateOnly PromisedDate, string Channel,
    IReadOnlyList<Guid> ReceivableIds, string? Note);

public sealed record PromiseDto(
    Guid Id, Guid CustomerId, string CustomerName, decimal Amount, decimal KeptAmount,
    DateOnly PromisedDate, string Channel, string Status, string? Note,
    IReadOnlyList<Guid> ReceivableIds, DateTimeOffset CreatedAt);

// --- Litiges (Phase 27 §25) ----------------------------------------

public sealed record OpenDisputeRequest(Guid CustomerId, Guid? ReceivableId, decimal DisputedAmount, string Reason);
public sealed record ResolveDisputeRequest(string Outcome, string Resolution);

public sealed record DisputeDto(
    Guid Id, Guid CustomerId, string CustomerName, Guid? ReceivableId, decimal DisputedAmount,
    string Reason, string Status, string? Resolution, DateTimeOffset CreatedAt, DateTimeOffset? ResolvedAt);

// --- Blocages (Phase 27 §26-27) -----------------------------------

public sealed record BlockCustomerRequest(Guid CustomerId, string BlockType, string Reason);
public sealed record ReleaseBlockRequest(string Reason);

public sealed record CustomerBlockDto(
    Guid Id, Guid CustomerId, string CustomerName, string BlockType, string Reason,
    bool IsActive, DateTimeOffset BlockedAt, DateTimeOffset? ReleasedAt, string? ReleaseReason);

// --- Radiation (Phase 27 §37) ------------------------------------

public sealed record WriteOffReceivableRequest(Guid OperationUuid, decimal Amount, string Reason);

public sealed record WriteOffResultDto(Guid WriteOffId, Guid ReceivableId, decimal Amount, Guid JournalEntryId);

// --- Portefeuille & recouvrement (Phase 27 §15, §23, §29) --------

public sealed record CollectionsRowDto(
    Guid CustomerId, string CustomerName, string Currency,
    decimal TotalOutstanding, decimal Current, decimal Bucket1To30, decimal Bucket31To60,
    decimal Bucket61To90, decimal Bucket90Plus, decimal DisputedAmount,
    int OpenPromises, int OpenDisputes, bool Blocked, int OldestOverdueDays);

public sealed record AgingSummaryDto(
    string Currency, decimal Total, decimal Current, decimal Bucket1To30, decimal Bucket31To60,
    decimal Bucket61To90, decimal Bucket90Plus, decimal TotalOverdue, decimal? Dso);

// --- Relevé client (Phase 27 §35) ------------------------------

public sealed record CustomerStatementLineDto(
    DateOnly Date, string DocumentType, string DocumentNumber, string? Reference,
    decimal Charge, decimal Payment, decimal RunningBalance);

public sealed record CustomerStatementDto(
    Guid CustomerId, string CustomerName, string Currency,
    decimal OpeningBalance, decimal ClosingBalance, decimal TotalOverdue,
    IReadOnlyList<CustomerStatementLineDto> Lines);
