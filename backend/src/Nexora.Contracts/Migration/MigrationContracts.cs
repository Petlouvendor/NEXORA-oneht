namespace Nexora.Contracts.Migration;

// =============================================================================
// Migration, importation & onboarding avancé (Phase 39). Pipeline générique :
// SOURCE → PROFILAGE → MAPPING → VALIDATION → SIMULATION → IMPORT → RAPPROCHEMENT.
// =============================================================================

// --- §5, §36 Job d'import -------------------------------------------

public sealed record ImportJobDto(
    Guid Id, string Kind, string SourceType, string? FileName, string Status, string Mode, int BatchSize,
    int TotalRows, int ProcessedRows, int OkRows, int WarningRows, int ErrorRows, int DuplicateRows,
    int CreatedCount, int UpdatedCount, int SkippedCount, int RejectedCount,
    ImportProfileDto? Profile, IReadOnlyList<ColumnMappingDto>? Mapping, SimulationDto? Simulation,
    ReconciliationDto? Reconciliation, string? ErrorMessage, DateTimeOffset? FinalizedAt,
    Guid? CreatedBy, DateTimeOffset CreatedAt);

public sealed record CreateImportRequest(
    string Kind, string SourceType, string Content, string? FileName = null, string Mode = "upsert", int BatchSize = 500);

// --- §6 Profilage --------------------------------------------------

/// <summary><c>detectedType</c> ∈ text | integer | decimal | date | boolean | email | phone | empty.</summary>
public sealed record ColumnProfileDto(
    string Name, string DetectedType, int NonEmpty, int Empty, int DistinctValues,
    IReadOnlyList<string> Samples, int InvalidFormat);

public sealed record ImportProfileDto(
    int RowCount, IReadOnlyList<string> Columns, IReadOnlyList<ColumnProfileDto> ColumnProfiles,
    int DuplicateRows, string? DetectedCurrency, DateTimeOffset ProfiledAt);

// --- §7 Mapping --------------------------------------------------

/// <summary>Champ NEXORA cible d'un import (décrit à l'assistant).</summary>
public sealed record TargetFieldDto(string Field, bool Required, string Type, string Description, bool IsKey);

public sealed record ColumnMappingDto(
    string TargetField, string? SourceColumn = null, string? DefaultValue = null, string? Transform = null);

public sealed record SetMappingRequest(IReadOnlyList<ColumnMappingDto> Mapping, string? SaveAsTemplate = null);

public sealed record MappingTemplateDto(Guid Id, string Kind, string Name, IReadOnlyList<ColumnMappingDto> Mapping);

// --- §24 Simulation / prévisualisation --------------------------

public sealed record SimulationDto(
    int RowsOk, int Warnings, int Errors, int Duplicates, int WouldCreate, int WouldUpdate, int WouldReject,
    IReadOnlyList<ImportRowDto> Sample, DateTimeOffset SimulatedAt);

// --- §26 Lignes & erreurs -------------------------------------

public sealed record RowMessageDto(string Level, string? Field, string Message);

public sealed record ImportRowDto(
    int RowNumber, string Status, string MatchType, string? DedupKey, Guid? TargetId,
    IReadOnlyDictionary<string, string?> Source, IReadOnlyList<RowMessageDto> Messages);

public sealed record CorrectRowRequest(int RowNumber, IReadOnlyDictionary<string, string?> Source);

// --- §35 Rapprochement ---------------------------------------

public sealed record ReconciliationLineDto(string Metric, decimal SourceValue, decimal NexoraValue, decimal Delta, bool Ok);

public sealed record ReconciliationDto(
    IReadOnlyList<ReconciliationLineDto> Lines, bool AllReconciled, DateTimeOffset GeneratedAt);

// --- §46 Événements ----------------------------------------

public sealed record ImportEventDto(string EventType, DateTimeOffset OccurredAt);

// --- §31-34 Onboarding -------------------------------------

/// <summary><c>status</c> ∈ done | pending | skipped. <c>source</c> ∈ auto | manual.</summary>
public sealed record OnboardingItemDto(
    string Key, string Label, string Category, string Status, string Source, string Detail);

public sealed record MigrationScoreDto(int Data, int Mapping, int Quality, int Accounting, bool Ready);

public sealed record OnboardingChecklistDto(
    int Done, int Total, decimal ProgressPct, MigrationScoreDto Score,
    IReadOnlyList<OnboardingItemDto> Items, DateTimeOffset GeneratedAt);

public sealed record CompleteOnboardingTaskRequest(string Key, string Status = "done", string? Note = null);
