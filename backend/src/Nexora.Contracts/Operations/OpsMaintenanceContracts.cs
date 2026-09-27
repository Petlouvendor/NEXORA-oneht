namespace Nexora.Contracts.Operations;

// =============================================================================
// Administration opérationnelle & maintenance (Phase 41 §3-51). Complète
// OperationsContracts.cs (Phase 37 : état système, Go-Live, déploiements).
// =============================================================================

// --- §7-9, §51 Jobs planifiés ------------------------------------------

/// <summary><c>category</c> ∈ business | technical. <c>lastStatus</c> ∈ running | succeeded | failed.</summary>
public sealed record JobStatusDto(
    string Key, string Name, string Category, string Description, bool Enabled, string Owner,
    DateTimeOffset? LastRunAt, DateTimeOffset? LastFinishedAt, string? LastStatus,
    int? LastAffectedCount, string? LastError, DateTimeOffset? NextRunEstimate,
    double? SuccessRatePct, int RunsLast30Days);

/// <summary><c>status</c> ∈ running | succeeded | failed. <c>triggeredBy</c> ∈ scheduler | manual.</summary>
public sealed record JobRunDto(
    Guid Id, string JobKey, DateTimeOffset StartedAt, DateTimeOffset? FinishedAt, string Status,
    int? AffectedCount, string? Error, string TriggeredBy, Guid? TriggeredByUser);

// --- §10-12, §51 File d'attente & dead-letter --------------------------

/// <summary>Un canal de remise externe agrégé (<c>queue</c> ∈ notifications | webhooks).</summary>
public sealed record QueueStatsDto(
    string Queue, int Pending, int Failed, int Dead, int DeliveredLastHour, double? OldestPendingAgeMinutes);

public sealed record DeadLetterItemDto(
    Guid Id, string Queue, string Subject, string? Cause, int Attempts,
    DateTimeOffset CreatedAt, DateTimeOffset? LastAttemptAt, Guid? CompanyId);

// --- §5-6, §13-15 Diagnostics, base & stockage -------------------------

/// <summary><c>status</c> ∈ ok | warn | fail | skipped.</summary>
public sealed record DiagnosticCheckDto(string Key, string Label, string Status, string Detail, int? DurationMs);

public sealed record TableSizeDto(string Table, long SizeBytes, long RowEstimate);

public sealed record DatabaseMaintenanceDto(
    long DatabaseSizeBytes, int ActiveConnections, int LongRunningTransactions, int Locks,
    IReadOnlyList<TableSizeDto> TopTables, DateTimeOffset MeasuredAt);

public sealed record StorageStatsDto(
    long DatabaseSizeBytes, int BackupCount, long BackupTotalSizeBytes, int DocumentCount,
    DateTimeOffset MeasuredAt);

// --- §27 Feature flags ---------------------------------------------------

/// <summary><c>scope</c> ∈ global | company | user.</summary>
public sealed record FeatureFlagDto(
    Guid Id, string Key, string Scope, Guid? CompanyId, Guid? UserId, bool Enabled,
    string? Description, DateTimeOffset? ExpiresAt, DateTimeOffset UpdatedAt);

public sealed record SaveFeatureFlagRequest(
    Guid? Id, string Key, string Scope, Guid? CompanyId, Guid? UserId, bool Enabled,
    string? Description = null, DateTimeOffset? ExpiresAt = null);

// --- §23-24 Maintenance planifiée & mode maintenance --------------------

/// <summary><c>status</c> ∈ planned | notified | backed_up | in_progress | verifying | closed | canceled.</summary>
public sealed record MaintenanceWindowDto(
    Guid Id, DateTimeOffset ScheduledAt, int DurationMinutes, string Impact,
    IReadOnlyList<string> Services, string? Responsible, string? RollbackPlan, string Status,
    DateTimeOffset? StartedAt, DateTimeOffset? EndedAt, string? Notes);

public sealed record ScheduleMaintenanceRequest(
    DateTimeOffset ScheduledAt, int DurationMinutes, string Impact, IReadOnlyList<string> Services,
    string? Responsible = null, string? RollbackPlan = null, string? Notes = null);

public sealed record TransitionMaintenanceRequest(string Status, string? Notes = null);

// --- §16-19, §22 Rétention & purge ---------------------------------------

/// <summary><c>action</c> ∈ archive | delete.</summary>
public sealed record RetentionPolicyDto(
    Guid Id, string DataType, Guid? CompanyId, int DurationDays, string Action,
    string? LegalReference, DateTimeOffset UpdatedAt);

public sealed record SaveRetentionPolicyRequest(
    string DataType, int DurationDays, string Action = "delete",
    string? LegalReference = null, Guid? CompanyId = null);

public sealed record PurgePreviewRequest(string DataType, Guid? CompanyId = null, DateTimeOffset? CutoffDate = null);

/// <summary><c>status</c> ∈ previewed | executed | failed.</summary>
public sealed record PurgeRunDto(
    Guid Id, string DataType, Guid? CompanyId, bool DryRun, DateTimeOffset CutoffDate,
    int ObjectCount, int ProtectedExcludedCount, string Status, Guid? ExecutedBy, DateTimeOffset? ExecutedAt);

// --- §44 Correction de données contrôlée ---------------------------------

public sealed record RequestDataCorrectionRequest(string TableName, Guid RecordId, string FieldName, string NewValue, string Reason);

public sealed record DecideDataCorrectionRequest(bool Approve, string? Notes = null);

/// <summary><c>status</c> ∈ requested | approved | rejected | applied.</summary>
public sealed record DataCorrectionDto(
    Guid Id, string TableName, Guid RecordId, string FieldName, string? OldValue, string NewValue,
    string Reason, string Status, Guid RequestedBy, Guid? ApprovedBy, string? Notes,
    DateTimeOffset? AppliedAt, DateTimeOffset CreatedAt);
