using Nexora.Contracts.Reporting;

namespace Nexora.Application.Reporting;

/// <summary>
/// Sensibilité des rapports exportables (§30, §36). Un rapport « financial » exige
/// <c>reporting.export_financial</c> en plus de <c>reporting.export</c>.
/// </summary>
public static class ReportCatalog
{
    private static readonly Dictionary<string, string> Sensitivity = new(StringComparer.OrdinalIgnoreCase)
    {
        ["sales_journal"] = "financial",
        ["stock_valuation"] = "financial",
        ["aging_receivables"] = "financial",
        ["aging_payables"] = "financial",
    };

    public static string SensitivityOf(string reportKey) =>
        Sensitivity.TryGetValue(reportKey, out var s) ? s : "normal";

    public static bool IsFinancial(string reportKey) => SensitivityOf(reportKey) is "financial" or "restricted";
}

/// <summary>Résolution des dates du filtre (§6) — presets + période personnalisée.</summary>
public static class FilterPeriod
{
    public static (DateOnly From, DateOnly To) Resolve(ReportFilter f, DateOnly today)
    {
        if (f.From is { } from && f.To is { } to) return (from, to);
        return (f.Preset ?? "month") switch
        {
            "today" => (today, today),
            "week" => (today.AddDays(-(int)today.DayOfWeek + 1), today),
            "year" => (new DateOnly(today.Year, 1, 1), today),
            "month" or _ => (new DateOnly(today.Year, today.Month, 1), today),
        };
    }
}

/// <summary>KPI et Command Center (Phase 7 §4-5). Dérivés des données validées, réconciliables (§2, §34).</summary>
public interface IKpiService
{
    Task<CommandCenterResponse> CommandCenterAsync(ReportFilter filter, CancellationToken ct = default);
    Task<CashierDashboardResponse> CashierDashboardAsync(Guid sessionId, CancellationToken ct = default);
    Task<ManagerDashboardResponse> ManagerDashboardAsync(ReportFilter filter, CancellationToken ct = default);
    Task<GroupDashboardResponse> GroupDashboardAsync(Guid groupId, ReportFilter filter, CancellationToken ct = default);
}

/// <summary>Rapports de ventes / produits (§13, §14).</summary>
public interface ISalesReports
{
    Task<SalesJournalResponse> JournalAsync(ReportFilter filter, CancellationToken ct = default);
    Task<SalesBreakdownResponse> BreakdownAsync(string dimension, ReportFilter filter, CancellationToken ct = default);
    Task<IReadOnlyList<ProductRankRow>> TopProductsAsync(ReportFilter filter, int take, CancellationToken ct = default);
}

/// <summary>Rapports stock (§15).</summary>
public interface IStockReports
{
    Task<StockValuationResponse> ValuationAsync(ReportFilter filter, CancellationToken ct = default);
    Task<IReadOnlyList<StockAlertRow>> RupturesAsync(ReportFilter filter, CancellationToken ct = default);
    Task<IReadOnlyList<ProductRankRow>> DormantAsync(ReportFilter filter, int days, CancellationToken ct = default);
}

/// <summary>Aging clients / fournisseurs (§17-19), tranches configurables (§18).</summary>
public interface IAgingReports
{
    /// <param name="asOf">Date d'arrêté ; <c>null</c> = date métier courante (§6).</param>
    Task<AgingResponse> ReceivablesAsync(DateOnly? asOf = null, CancellationToken ct = default);
    Task<AgingResponse> PayablesAsync(DateOnly? asOf = null, CancellationToken ct = default);
}

/// <summary>Budget vs réalisé (§24).</summary>
public interface IBudgetReports
{
    Task<BudgetVsActualResponse> BudgetVsActualAsync(int fiscalYear, ReportFilter filter, CancellationToken ct = default);
}

/// <summary>Centre d'alertes (§28).</summary>
public interface IAlertCenter
{
    Task<AlertCenterResponse> EvaluateAsync(CancellationToken ct = default);
}

/// <summary>Drill-down d'un KPI vers ses transactions sources (§29).</summary>
public interface IDrillDown
{
    Task<DrillResponse> ExpandAsync(string path, CancellationToken ct = default);
}

/// <summary>Exports CSV conformes aux permissions et filtres (§30, §36).</summary>
public interface IExportService
{
    Task<ExportResult> ExportAsync(ExportRequest request, CancellationToken ct = default);

    /// <summary>Historique des exports de l'entreprise active (Phase 42 §55 <c>/reporting/exports</c>).</summary>
    Task<IReadOnlyList<ExportHistoryEntryDto>> HistoryAsync(CancellationToken ct = default);
}

/// <summary>Ask Nexora (§33) : langage naturel → indicateurs autorisés, avec sources.</summary>
public interface IAskNexora
{
    Task<AskResponse> AskAsync(AskRequest request, CancellationToken ct = default);
}
