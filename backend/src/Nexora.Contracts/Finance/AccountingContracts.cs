namespace Nexora.Contracts.Finance;

// =============================================================================
// Comptabilité générale — workflow manuel & clôture d'exercice (Phase 29 §9-19, §47, §51).
// Les états (balance, grand livre, résultat, bilan) restent servis par les contrats
// de Phase 4 (ReportContracts.cs).
// =============================================================================

// --- Plan comptable & journaux (§4-8) ------------------------------------

public sealed record AccountDto(
    Guid Id, string Code, string Name, string AccountType, bool IsPostable,
    string Status, Guid? ParentAccountId, string? Currency);

public sealed record ChartOfAccountsDto(Guid Id, string Name, string Status, int AccountCount);

public sealed record JournalDto(Guid Id, string Code, string Name, string JournalType, string Status);

// --- Écriture manuelle (§9-12) -----------------------------------------

public sealed record JournalEntryLineInput(
    string AccountCode, decimal Debit, decimal Credit,
    string? Description = null, string? ThirdPartyType = null, Guid? ThirdPartyId = null, Guid? CostCenterId = null);

public sealed record CreateJournalEntryRequest(
    string JournalCode, DateOnly EntryDate, string Currency,
    string? Description, IReadOnlyList<JournalEntryLineInput> Lines);

public sealed record JournalEntryLineDto(
    int LineNumber, string AccountCode, string AccountName, string? Description,
    decimal Debit, decimal Credit, decimal BaseDebit, decimal BaseCredit,
    string? ThirdPartyType, Guid? ThirdPartyId, Guid? CostCenterId);

/// <summary><c>TotalDebit</c> / <c>TotalCredit</c> sont dans la devise de l'écriture (<c>Currency</c>) ;
/// <c>BaseTotal</c> = contre-valeur en devise de l'entreprise (<c>FunctionalCurrency</c>), celle de la balance.</summary>
public sealed record JournalEntryDto(
    Guid Id, string JournalCode, string EntryNumber, DateOnly EntryDate, string Status, string Origin,
    string? Description, string Currency, decimal TotalDebit, decimal TotalCredit, bool IsBalanced,
    Guid? ReversalOfEntryId, DateTimeOffset? PostedAt, DateTimeOffset CreatedAt,
    IReadOnlyList<JournalEntryLineDto> Lines,
    decimal BaseTotal = 0m, string? FunctionalCurrency = null, string? SourceType = null, Guid? SourceId = null,
    string? CreatedByName = null, string? PostedByName = null, Guid? ReversedByEntryId = null);

// --- Contrôles comptables (§47) ---------------------------------------

/// <summary><c>status</c> ∈ pass | warn | fail.</summary>
public sealed record AccountingControlDto(string Key, string Label, string Status, string? Detail);

public sealed record PostJournalEntryResult(
    Guid EntryId, string EntryNumber, IReadOnlyList<AccountingControlDto> Controls);

public sealed record ReverseJournalEntryRequest(string Reason, DateOnly? ReversalDate = null);

// --- Clôture d'exercice & à-nouveaux (§16, §18-19) --------------------

public sealed record YearEndControlDto(string Key, string Label, string Status, string? Detail, int Count, bool Blocking);

public sealed record YearEndCloseReportDto(
    Guid PeriodId, string PeriodName, DateOnly EndDate, string Status, bool CanClose,
    decimal Revenue, decimal Expenses, decimal NetResult, string Currency,
    IReadOnlyList<YearEndControlDto> Controls, DateTimeOffset GeneratedAt);

public sealed record CloseYearRequest(bool Force = false, string? Note = null);

public sealed record CloseYearResultDto(
    Guid CloseId, Guid PeriodId, decimal NetResult, Guid ResultEntryId, string ResultEntryNumber, bool Forced);

public sealed record GenerateOpeningBalancesRequest(Guid FromPeriodId);

public sealed record OpeningBalancesResultDto(
    Guid EntryId, string EntryNumber, Guid TargetPeriodId, DateOnly EntryDate, int LineCount, decimal Total);
