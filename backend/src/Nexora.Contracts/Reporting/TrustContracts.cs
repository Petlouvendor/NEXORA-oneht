namespace Nexora.Contracts.Reporting;

// =============================================================================
// Reporting & analytics — couche de confiance (Phase 31 §2, §44-46).
// =============================================================================

// --- §46 Réconciliation KPI ---------------------------------------------

/// <summary>Une ligne de réconciliation : valeur opérationnelle vs valeur comptable / confirmée.</summary>
public sealed record ReconciliationLineDto(
    string Metric, string Label, string Definition,
    decimal OperationalValue, decimal AccountingValue, decimal Difference,
    bool Reconciled, string? Note);

public sealed record ReconciliationReportDto(
    DateOnly From, DateOnly To, string Currency, decimal Tolerance,
    bool AllReconciled, int ReconciledCount, int TotalCount,
    IReadOnlyList<ReconciliationLineDto> Lines, DateTimeOffset GeneratedAt);

// --- §45 Data quality --------------------------------------------------

/// <summary><c>severity</c> ∈ info | warn | error ; <c>category</c> ∈ missing | duplicate | unreconciled | unposted | mapping | integrity.</summary>
public sealed record DataQualityCheckDto(
    string Key, string Label, string Category, string Severity,
    int Count, string? Detail, IReadOnlyList<Guid> SampleIds);

public sealed record DataQualityReportDto(
    decimal Score, string Grade, int ChecksRun, int ChecksFailed,
    IReadOnlyList<DataQualityCheckDto> Checks, DateTimeOffset GeneratedAt);

// --- §44 Snapshots ---------------------------------------------------

public sealed record CreateSnapshotRequest(string Kind, string? Label, DateOnly? From, DateOnly? To);

public sealed record AnalyticsSnapshotDto(
    Guid Id, string Kind, string? Label, DateOnly AsOfDate, DateOnly? PeriodFrom, DateOnly? PeriodTo,
    string CalculationVersion, decimal? Score, DateTimeOffset CreatedAt);

public sealed record AnalyticsSnapshotDetailDto(AnalyticsSnapshotDto Header, System.Text.Json.JsonElement Result);
