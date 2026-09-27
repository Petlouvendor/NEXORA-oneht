namespace Nexora.Contracts.Group;

// --- Plan comptable groupe & mappings (§12) ----------------------------

public sealed record GroupAccountDto(
    Guid Id, string Code, string Name, string AccountType, bool IsEliminable, int SortOrder, string Status);

public sealed record UpsertGroupAccountRequest(
    string Code, string Name, string AccountType, bool IsEliminable = false, int SortOrder = 0);

public sealed record SetMappingRequest(Guid CompanyId, string LocalAccountCode, string GroupAccountCode);

public sealed record MappingCoverageRow(
    Guid CompanyId, string CompanyName, int MappedAccounts, int UnmappedWithActivity, IReadOnlyList<string> UnmappedCodes);

// --- Intercompany (§14-15) -------------------------------------------

public sealed record RecordIntercompanyRequest(
    Guid SourceCompanyId, Guid TargetCompanyId, string TransactionType,
    decimal Amount, string Currency, DateOnly OccurredAt,
    string? SourceReference, string? TargetReference);

public sealed record IntercompanyTransactionDto(
    Guid Id, Guid SourceCompanyId, string SourceCompany, Guid TargetCompanyId, string TargetCompany,
    string TransactionType, decimal Amount, string Currency, DateOnly OccurredAt, string Status, string MatchStatus);

public sealed record ReconcileIntercompanyRequest(Guid SourceTransactionId, Guid? CounterpartTransactionId);

public sealed record IntercompanyMatchDto(
    Guid Id, Guid SourceTransactionId, Guid? CounterpartTransactionId, string Status,
    decimal AmountDifference, bool CurrencyMatch, int DateDifferenceDays, string? Notes);

// --- Consolidation (§17-20) -----------------------------------------

public sealed record CreateConsolidationRunRequest(
    string PeriodLabel, DateOnly FromDate, DateOnly ToDate, IReadOnlyList<Guid> CompanyIds, Guid? ProfileId);

public sealed record ConsolidationRunDto(
    Guid Id, Guid GroupId, string PeriodLabel, DateOnly FromDate, DateOnly ToDate, string Currency,
    string Status, bool CoherenceOk, decimal TotalDebit, decimal TotalCredit, decimal EliminationTotal,
    IReadOnlyList<string> Diagnostics, IReadOnlyList<ConsolidationCompanyDto> Companies,
    DateTimeOffset? GeneratedAt, DateTimeOffset? ValidatedAt, DateTimeOffset? PublishedAt);

public sealed record ConsolidationCompanyDto(
    Guid CompanyId, string CompanyName, bool Included, string FunctionalCurrency, decimal FxRate,
    bool PeriodClosed, bool MappingComplete, decimal ContributedDebit, decimal ContributedCredit);

public sealed record ConsolidationLineDto(
    string GroupAccountCode, string GroupAccountName, string AccountType,
    decimal AggregatedDebit, decimal AggregatedCredit,
    decimal EliminationDebit, decimal EliminationCredit,
    decimal ConsolidatedDebit, decimal ConsolidatedCredit);

public sealed record ConsolidatedStatementsDto(
    Guid RunId, string PeriodLabel, string Currency, string Status,
    IReadOnlyList<ConsolidationLineDto> TrialBalance,
    decimal TotalAssets, decimal TotalLiabilities, decimal TotalEquity,
    decimal TotalRevenue, decimal TotalExpense, decimal NetResult,
    bool BalanceSheetBalanced, bool TrialBalanceBalanced,
    IReadOnlyList<ConsolidationCompanyDto> ByCompany);

public sealed record ReopenRunRequest(string Reason);

// --- Centre de contrôle comptable en chef (§24) --------------------

public sealed record GroupControlCenterDto(
    Guid GroupId, string Currency, DateTimeOffset GeneratedAt,
    IReadOnlyList<GroupCompanyStatusDto> Companies,
    int UnreconciledIntercompany, int RunsReadyForValidation, int OpenTasks,
    decimal GroupReceivables, decimal GroupPayables);

public sealed record GroupCompanyStatusDto(
    Guid CompanyId, string CompanyName, string? Sector, string Status,
    string LatestPeriod, string LatestPeriodStatus,
    int UnmappedAccounts, int UnreconciledIntercompany,
    decimal Receivables, decimal Payables);

// --- Tâches groupe (§25) ------------------------------------------

public sealed record GroupTaskDto(
    Guid Id, Guid? CompanyId, string? CompanyName, string Title, string? Description,
    Guid? AssigneeUserId, string Priority, DateOnly? DueDate, string Status, string? Notes);

public sealed record CreateGroupTaskRequest(
    string Title, string? Description, Guid? CompanyId, Guid? AssigneeUserId,
    string Priority, DateOnly? DueDate);

public sealed record UpdateGroupTaskRequest(string Status, string? Notes);

// --- Provisioning / archivage d'entreprise (§28-29) --------------

public sealed record ProvisionCompanyRequest(
    string Name, string ActivityType, string Currency, Guid? GroupId);

public sealed record ArchiveCompanyRequest(string Reason);

public sealed record GroupPolicyDto(
    Guid GroupId, string ConsolidationCurrency, string FxMethod, decimal IntercompanyTolerance);

public sealed record SetGroupPolicyRequest(string? ConsolidationCurrency, string? FxMethod, decimal? IntercompanyTolerance);
