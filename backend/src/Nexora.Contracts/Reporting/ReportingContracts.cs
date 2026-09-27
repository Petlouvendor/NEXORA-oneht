namespace Nexora.Contracts.Reporting;

/// <summary>Filtre universel (Phase 7 §6). Le périmètre effectif reste borné par la RLS serveur.</summary>
public sealed record ReportFilter
{
    public DateOnly? From { get; init; }
    public DateOnly? To { get; init; }
    public string? Preset { get; init; }          // today | week | month | year
    public Guid? EstablishmentId { get; init; }
    public Guid? StoreId { get; init; }
    public Guid? CashRegisterId { get; init; }
    public Guid? WarehouseId { get; init; }
    public Guid? ProductId { get; init; }
    public Guid? CategoryId { get; init; }
    public Guid? CustomerId { get; init; }
    public Guid? SupplierId { get; init; }
    public Guid? SalespersonUserId { get; init; }
    public Guid? PaymentMethodId { get; init; }
    public string? Status { get; init; }
    public string? Currency { get; init; }
    public int Page { get; init; } = 1;
    public int PageSize { get; init; } = 50;
}

/// <summary>Valeur d'indicateur avec sa nature (§34).</summary>
public sealed record KpiValue(string Key, string Label, decimal Value, string Unit, string Kind)
{
    public const string Actual = "actual";
    public const string Forecast = "forecast";
    public const string Estimated = "estimated";
}

public sealed record TrendPoint(DateOnly Date, decimal Value);

public sealed record CommandCenterResponse(
    DateOnly From, DateOnly To, string Currency,
    IReadOnlyList<KpiValue> Kpis,
    IReadOnlyList<TrendPoint> RevenueTrend,
    ComparisonBlock Comparison,
    IReadOnlyList<AlertSummary> CriticalAlerts,
    DateTimeOffset GeneratedAt);

public sealed record ComparisonBlock(string PeriodLabel, decimal Current, decimal Previous, decimal DeltaAmount, decimal DeltaPercent);

public sealed record CashierDashboardResponse(
    Guid SessionId, string Status, int SaleCount, decimal CollectedTotal, decimal AverageBasket,
    IReadOnlyList<KpiValue> PaymentBreakdown, decimal OpeningBalance, decimal ExpectedBalance, decimal? Variance);

public sealed record ManagerRow(string Label, decimal Revenue, decimal Margin, int Transactions);
public sealed record ManagerDashboardResponse(
    DateOnly From, DateOnly To, string Currency,
    IReadOnlyList<KpiValue> Kpis,
    IReadOnlyList<ManagerRow> ByRegister,
    IReadOnlyList<ManagerRow> BySalesperson,
    IReadOnlyList<ProductRankRow> TopProducts,
    IReadOnlyList<ProductRankRow> SlowMovers,
    IReadOnlyList<StockAlertRow> CriticalStock,
    decimal OpenReceivables, decimal Expenses);

public sealed record CompanyKpiRow(Guid CompanyId, string CompanyName, string? Sector,
    decimal Revenue, decimal Result, decimal Cash, decimal Receivables, decimal Payables, bool Authorized);
public sealed record GroupDashboardResponse(
    Guid GroupId, DateOnly From, DateOnly To, string Currency,
    IReadOnlyList<CompanyKpiRow> Companies,
    decimal ConsolidatedRevenue, decimal ConsolidatedResult,
    decimal GroupCash, decimal GroupReceivables, decimal GroupPayables,
    decimal IntercompanyExposure, IReadOnlyList<string> ExcludedCompanies);

// --- Rapports ventes / produits / stock ---------------------------------

public sealed record SalesJournalRow(
    DateOnly Date, string DocumentNumber, string? Customer, string? Salesperson, string SaleType,
    decimal Subtotal, decimal Discount, decimal Tax, decimal Total, decimal Paid);
public sealed record SalesJournalResponse(DateOnly From, DateOnly To, string Currency,
    IReadOnlyList<SalesJournalRow> Rows, decimal TotalRevenue, decimal TotalTax, long Count);

public sealed record SalesBreakdownRow(string Dimension, string Label, decimal Revenue, decimal Margin, decimal MarginRate, decimal Quantity, int Transactions);
public sealed record SalesBreakdownResponse(string Dimension, DateOnly From, DateOnly To, string Currency, IReadOnlyList<SalesBreakdownRow> Rows);

public sealed record ProductRankRow(Guid ProductId, string Sku, string Name, decimal Quantity, decimal Revenue, decimal Margin);

public sealed record StockAlertRow(Guid ProductId, string Sku, string Name, Guid LocationId, decimal OnHand, decimal ReorderPoint);
public sealed record StockValuationRow(Guid ProductId, string Sku, string Name, decimal OnHand, decimal AverageCost, decimal Value);
public sealed record StockValuationResponse(DateOnly AsOf, string Currency, IReadOnlyList<StockValuationRow> Rows, decimal TotalValue);

// --- Aging (§18) -------------------------------------------------------

public sealed record AgingLine(string Party, decimal Total, IReadOnlyList<decimal> Buckets);
public sealed record AgingResponse(string Kind, DateOnly AsOf, string Currency,
    IReadOnlyList<string> BucketLabels, IReadOnlyList<AgingLine> Rows, decimal GrandTotal);

// --- Budget vs réalisé (§24) -----------------------------------------

public sealed record BudgetVsActualRow(string AccountCode, string AccountName, decimal Budget, decimal Actual, decimal Variance, decimal VariancePercent);
public sealed record BudgetVsActualResponse(string BudgetName, int FiscalYear, string Currency, IReadOnlyList<BudgetVsActualRow> Rows,
    decimal TotalBudget, decimal TotalActual);

// --- Alertes (§28) --------------------------------------------------

public sealed record AlertSummary(string Key, string Severity, string Title, string Detail, string? DrillPath, int Count);
public sealed record AlertCenterResponse(DateTimeOffset GeneratedAt, IReadOnlyList<AlertSummary> Alerts);

// --- Drill-down (§29) ---------------------------------------------

public sealed record DrillNode(string Level, string Id, string Label, decimal Value);
public sealed record DrillResponse(string Path, string Level, IReadOnlyList<DrillNode> Children, string? NextLevel);

// --- Ask Nexora (§33) -------------------------------------------

/// <summary><c>CustomerId</c> : client en contexte (question posée depuis sa fiche) — module clients §27.</summary>
public sealed record AskRequest(string Question, ReportFilter? Filter, Guid? CustomerId = null);
/// <summary><c>Related</c> : recommandations ouvertes liées à la question (NEXORA AI §8 « l'IA doit répondre avec
/// des recommandations ») — renseigné par l'assistant seulement si l'utilisateur voit l'intelligence.</summary>
public sealed record AskResponse(string Intent, string Answer, IReadOnlyList<KpiValue> Facts,
    IReadOnlyList<string> Sources, bool ContainsForecast, IReadOnlyList<RelatedRecommendation>? Related = null);

public sealed record RelatedRecommendation(Guid Id, string Title, string PriorityLevel, string ProposedAction);

// --- Export (§30) ----------------------------------------------

public sealed record ExportRequest(string ReportKey, string Format, ReportFilter Filter);
/// <summary>Fichier exporté : vrai .xlsx, PDF ou CSV (octets).</summary>
public sealed record ExportResult(string FileName, string ContentType, byte[] Content, int RowCount, string Sensitivity);

// --- Rapports planifiés (§31, activés Phase 42 §41) — voir BiContracts.cs pour
// ReportScheduleDto / SaveReportScheduleRequest (ces deux stubs n'avaient aucun
// service ni contrôleur ; remplacés par la version réellement branchée).
