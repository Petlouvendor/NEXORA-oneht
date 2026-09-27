namespace Nexora.Contracts.Finance;

// =============================================================================
// Finance & trésorerie — cockpit de pilotage (Phase 28 §27-28, §22-23, §32, §38).
// Vue opérationnelle dérivée des comptes financiers, sous-livres et écritures ;
// aucun solde n'est stocké en double (§1).
// =============================================================================

// --- §27 Position de trésorerie ----------------------------------------------

/// <summary>Un compte financier dans la position (solde système + solde converti en devise fonctionnelle).</summary>
public sealed record TreasuryAccountLineDto(
    Guid AccountId, string Name, string AccountType, string Currency,
    decimal SystemBalance, decimal BaseBalance, string Status);

/// <summary>Photographie de la trésorerie disponible et des flux proches (§27).</summary>
public sealed record TreasuryPositionDto(
    DateOnly AsOf, string Currency,
    decimal Available,
    decimal Cash, decimal Bank, decimal MobileMoney, decimal Other,
    decimal Pending, decimal SuspenseExposure,
    decimal NearReceivables, decimal NearPayables, decimal NetPosition,
    int HorizonDays,
    IReadOnlyList<TreasuryAccountLineDto> Accounts);

// --- §28 Liquidité & gap ----------------------------------------------------

public sealed record LiquidityDto(
    DateOnly AsOf, string Currency, int HorizonDays,
    decimal ImmediateLiquidity,
    decimal ExpectedReceivables,
    decimal ShortTermCommitments,
    decimal ProjectedGap,
    IReadOnlyList<string> Alerts);

// --- §22-23 Prévision par maturité ----------------------------------------

public sealed record MaturityBucketDto(
    string Label, int DayFrom, int DayTo,
    decimal Inflow, decimal Outflow, decimal Net, decimal ProjectedBalance);

public sealed record CashflowForecastDto(
    DateOnly AsOf, string Currency, decimal OpeningBalance,
    IReadOnlyList<MaturityBucketDto> Buckets);

// --- §31-32 Comptes de suspense -------------------------------------------

public sealed record SuspenseItemDto(
    Guid Id, Guid? FinancialAccountId, string? AccountName, string Kind,
    decimal Amount, string Currency, string? Reference, string Reason,
    string Status, DateOnly? ResolutionDueDate, bool Overdue,
    string? ResponsibleUserName, string? Resolution,
    DateTimeOffset CreatedAt, DateTimeOffset? ResolvedAt);

/// <summary>Ouvre un suspens (§32). <c>kind</c> ∈ unidentified | payment_without_invoice |
/// unallocated_fee | bank_discrepancy | mobile_money_discrepancy | cash_variance | other.</summary>
public sealed record OpenSuspenseRequest(
    Guid? FinancialAccountId, string Kind, decimal Amount, string Currency,
    string Reason, string? Reference, DateOnly? ResolutionDueDate, Guid? ResponsibleUserId);

/// <summary>Résout un suspens (§32). <c>outcome</c> ∈ resolved | written_off | cancelled.</summary>
public sealed record ResolveSuspenseRequest(string Outcome, string Resolution);

// --- §38 Clôture financière ---------------------------------------------

public sealed record FinancialCloseControlDto(
    string Key, string Label, string Status, string? Detail, int Count, bool Blocking);

public sealed record FinancialCloseReportDto(
    Guid PeriodId, string PeriodName, DateOnly AsOf, string Status, bool CanClose,
    IReadOnlyList<FinancialCloseControlDto> Controls, DateTimeOffset GeneratedAt);

public sealed record CompleteFinancialCloseRequest(bool Force = false, string? Note = null);

public sealed record FinancialCloseResultDto(Guid CloseId, Guid PeriodId, DateOnly AsOf, bool Forced);
