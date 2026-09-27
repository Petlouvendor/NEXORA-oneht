using System.Text.Json;

namespace Nexora.Contracts.Reporting;

// =============================================================================
// Business Intelligence, Reporting & Pilotage (Phase 42 §33-41, §48-50, §55).
// Complète ReportingContracts.cs (Phase 7) et TrustContracts.cs (Phase 31).
// =============================================================================

// --- §48, §50 Catalogue KPI gouverné --------------------------------------

/// <summary><c>kind</c> ∈ actual | forecast | estimated.</summary>
public sealed record KpiDefinitionDto(
    Guid Id, string Key, string Label, string Category, string Unit, string Source,
    string Formula, string Kind, string Owner, int Version, DateTimeOffset UpdatedAt);

// --- §39-40 Objectifs & score de performance ------------------------------

/// <summary><c>direction</c> ∈ min (à atteindre) | max (à ne pas dépasser). <c>periodType</c> ∈ day|week|month|quarter|year.</summary>
public sealed record ObjectiveDto(
    Guid Id, Guid CompanyId, Guid? EstablishmentId, string KpiKey, string PeriodType,
    decimal TargetValue, string Direction, decimal Weight, DateOnly EffectiveFrom, DateOnly? EffectiveTo, string Status);

public sealed record SetObjectiveRequest(
    Guid? Id, Guid? EstablishmentId, string KpiKey, string PeriodType, decimal TargetValue,
    string Direction = "min", decimal Weight = 1m, DateOnly? EffectiveFrom = null, DateOnly? EffectiveTo = null);

public sealed record ObjectiveScoreItemDto(string KpiKey, string Label, decimal Target, decimal Actual, decimal RatioPct, decimal Weight);

/// <summary><c>Grade</c> ∈ A..E, dérivée du score global (§40).</summary>
public sealed record ObjectiveScoreDto(decimal Score, string Grade, IReadOnlyList<ObjectiveScoreItemDto> Items, DateTimeOffset EvaluatedAt);

// --- §41 Rapports planifiés -------------------------------------------------

/// <summary><c>frequency</c> ∈ daily|weekly|monthly|on_close. <c>format</c> ∈ csv|xlsx|pdf. <c>status</c> ∈ active|paused.</summary>
public sealed record ReportScheduleDto(
    Guid Id, string Name, string ReportKey, string Frequency, JsonElement Filter, string Format,
    IReadOnlyList<string> Recipients, string Status, DateTimeOffset? LastRunAt, DateTimeOffset? NextRunAt);

public sealed record SaveReportScheduleRequest(
    Guid? Id, string Name, string ReportKey, string Frequency, JsonElement? Filter,
    string Format, IReadOnlyList<string> Recipients, string? Timezone = null);

/// <summary><c>status</c> ∈ running|succeeded|failed.</summary>
public sealed record ReportExecutionDto(
    Guid Id, Guid? ReportScheduleId, string ReportKey, string Format, string Status,
    int? RowCount, string? Error, DateTimeOffset StartedAt, DateTimeOffset? FinishedAt);

// --- §48-49 Catalogue de rapports & rapports personnalisés ------------------

/// <summary>Un rapport standard déjà livré (Phases 7/9/28/29) — documentation, pas une nouvelle donnée.</summary>
public sealed record ReportCatalogEntryDto(
    string Key, string Category, string Name, string Description, string Source,
    IReadOnlyList<string> RolesAllowed, bool RunnableGeneric, string Endpoint);

public sealed record SavedReportDto(
    Guid Id, string Name, string ReportKey, JsonElement Filter, bool IsShared, bool Mine, DateTimeOffset UpdatedAt);

public sealed record SaveSavedReportRequest(Guid? Id, string Name, string ReportKey, JsonElement Filter, bool IsShared = false);

public sealed record ReportRunResultDto(string ReportKey, DateTimeOffset RanAt, JsonElement Result);

// --- §35-36 Prévisions & scénarios ------------------------------------------

public sealed record ForecastPointDto(DateOnly Date, decimal Value);

/// <summary><c>metric</c> ∈ revenue|cash|stock|receivables|expenses. <c>confidenceLevel</c> ∈ low|medium|high —
/// jamais présentée comme une certitude (§35).</summary>
public sealed record ForecastDto(
    Guid Id, string Metric, string Method, string Scenario, decimal AdjustmentPct, int HorizonDays,
    string ConfidenceLevel, string Assumptions, IReadOnlyList<ForecastPointDto> Points, DateTimeOffset GeneratedAt);

public sealed record GenerateForecastRequest(string Metric, int HorizonDays = 30, string Scenario = "base", decimal? AdjustmentPct = null);

// --- §37-38, §58 Insights ----------------------------------------------------

/// <summary><c>category</c> ∈ revenue_drop|margin_drop|stock_anomaly|expense_spike|cash_gap.
/// <c>status</c> ∈ open|dismissed|actioned.</summary>
public sealed record InsightDto(
    Guid Id, string Category, string Severity, string Title, string Detail, JsonElement Evidence,
    string? DrillPath, string? SuggestedAction, DateOnly PeriodStart, string Status,
    Guid? AssignedTo, DateTimeOffset? DueAt, DateTimeOffset CreatedAt);

public sealed record DismissInsightRequest(string? Reason = null);
public sealed record AssignInsightRequest(Guid UserId, DateTimeOffset? DueAt = null);

// --- §33-34 Budgets ----------------------------------------------------------

/// <summary><c>status</c> ∈ draft|pending_approval|active|closed.</summary>
public sealed record BudgetDto(
    Guid Id, string Name, int FiscalYear, string CurrencyCode, string Status,
    Guid? RevisedFrom, Guid? ApprovedBy, DateTimeOffset? ApprovedAt, DateTimeOffset UpdatedAt);

public sealed record CreateBudgetRequest(string Name, int FiscalYear, string CurrencyCode);

public sealed record BudgetLineDto(Guid Id, Guid? AccountId, Guid? CostCenterId, Guid? EstablishmentId, int? PeriodMonth, decimal Amount);

public sealed record SetBudgetLinesRequest(IReadOnlyList<BudgetLineInput> Lines);
public sealed record BudgetLineInput(Guid? AccountId, Guid? CostCenterId, Guid? EstablishmentId, int? PeriodMonth, decimal Amount);

// --- §30, §36 Historique des exports (alias /reporting/exports) ------------

public sealed record ExportHistoryEntryDto(
    Guid Id, string ReportKey, string Format, int? RowCount, string Sensitivity, DateTimeOffset CreatedAt);
