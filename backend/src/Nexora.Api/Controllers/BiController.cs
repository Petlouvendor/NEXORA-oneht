using Asp.Versioning;
using Microsoft.AspNetCore.Mvc;
using Nexora.Api.Security;
using Nexora.Application.Preferences;
using Nexora.Application.Reporting;
using Nexora.Application.Abstractions;
using Nexora.Application.Security;
using Nexora.Contracts.Preferences;
using Nexora.Contracts.Reporting;

namespace Nexora.Api.Controllers;

/// <summary>
/// Business Intelligence, Reporting & Pilotage (Phase 42 §33-41, §48-50, §55) : catalogue KPI
/// gouverné, objectifs & score de performance, catalogue de rapports & rapports personnalisés,
/// rapports planifiés, prévisions, insights, budgets. Complète <see cref="DashboardController"/>
/// (Phase 7), <see cref="ReportsController"/> (Phase 7) et <see cref="AnalyticsController"/>
/// (Phase 31) sous le même module de permission <c>reporting.*</c>.
/// </summary>
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}")]
public sealed class BiController(
    IKpiGovernanceService kpiGovernance,
    IObjectiveService objectives,
    IReportCatalogService reportCatalog,
    IReportScheduleService reportSchedules,
    IForecastService forecasts,
    IInsightService insights,
    IBudgetService budgets,
    IDashboardLayoutService dashboardLayouts,
    IPermissionService permissions,
    ITenantContext tenant) : ApiControllerBase
{
    // --- §48, §50 Catalogue KPI gouverné ---------------------------------

    [HttpGet("kpi-definitions")]
    [RequirePermission(Permissions.Reporting.View)]
    public async Task<IActionResult> KpiDefinitions(CancellationToken ct) => Ok(await kpiGovernance.ListAsync(ct));

    [HttpGet("kpi-definitions/{key}")]
    [HttpGet("~/api/v{version:apiVersion}/kpis/{key}/definition")]
    [RequirePermission(Permissions.Reporting.View)]
    public async Task<IActionResult> KpiDefinition(string key, CancellationToken ct) => FromResult(await kpiGovernance.GetAsync(key, ct));

    // --- §39-40 Objectifs & score -----------------------------------------

    [HttpGet("objectives")]
    [RequirePermission(Permissions.Reporting.View)]
    public async Task<IActionResult> Objectives(CancellationToken ct) => Ok(await objectives.ListAsync(ct));

    [HttpPost("objectives")]
    [RequirePermission(Permissions.Reporting.ManageTargets)]
    public async Task<IActionResult> SetObjective([FromBody] SetObjectiveRequest request, CancellationToken ct)
        => FromResult(await objectives.SetAsync(request, ct));

    [HttpDelete("objectives/{id:guid}")]
    [RequirePermission(Permissions.Reporting.ManageTargets)]
    public async Task<IActionResult> DeleteObjective(Guid id, CancellationToken ct) => FromResult(await objectives.DeleteAsync(id, ct));

    [HttpGet("objectives/score")]
    [RequirePermission(Permissions.Reporting.View)]
    public async Task<IActionResult> ObjectiveScore(CancellationToken ct) => Ok(await objectives.ScoreAsync(ct));

    // --- §48-49 Catalogue de rapports & rapports personnalisés -------------

    [HttpGet("reports")]
    [RequirePermission(Permissions.Reporting.View)]
    public async Task<IActionResult> Reports(CancellationToken ct)
        => Ok(new { standard = reportCatalog.StandardReports(), saved = await reportCatalog.ListSavedAsync(ct) });

    [HttpPost("reports")]
    [RequirePermission(Permissions.Reporting.View)]
    public async Task<IActionResult> SaveReport([FromBody] SaveSavedReportRequest request, CancellationToken ct)
        => FromResult(await reportCatalog.SaveAsync(request, ct));

    [HttpGet("reports/{id:guid}")]
    [RequirePermission(Permissions.Reporting.View)]
    public async Task<IActionResult> Report(Guid id, CancellationToken ct)
    {
        var saved = await reportCatalog.ListSavedAsync(ct);
        var found = saved.FirstOrDefault(r => r.Id == id);
        return found is null ? NotFound() : Ok(found);
    }

    [HttpDelete("reports/{id:guid}")]
    [RequirePermission(Permissions.Reporting.View)]
    public async Task<IActionResult> DeleteReport(Guid id, CancellationToken ct) => FromResult(await reportCatalog.DeleteSavedAsync(id, ct));

    [HttpPost("reports/{id:guid}/run")]
    [RequirePermission(Permissions.Reporting.View)]
    public async Task<IActionResult> RunReport(Guid id, CancellationToken ct) => FromResult(await reportCatalog.RunAsync(id, ct));

    // --- §41 Rapports planifiés ---------------------------------------------

    [HttpGet("reports/schedules")]
    [RequirePermission(Permissions.Reporting.Schedule)]
    public async Task<IActionResult> Schedules(CancellationToken ct) => Ok(await reportSchedules.ListAsync(ct));

    [HttpPost("reports/schedules")]
    [RequirePermission(Permissions.Reporting.Schedule)]
    public async Task<IActionResult> SaveSchedule([FromBody] SaveReportScheduleRequest request, CancellationToken ct)
        => FromResult(await reportSchedules.SaveAsync(request, ct));

    [HttpDelete("reports/schedules/{id:guid}")]
    [RequirePermission(Permissions.Reporting.Schedule)]
    public async Task<IActionResult> DeleteSchedule(Guid id, CancellationToken ct) => FromResult(await reportSchedules.DeleteAsync(id, ct));

    [HttpGet("reports/executions")]
    [RequirePermission(Permissions.Reporting.Schedule)]
    public async Task<IActionResult> Executions(CancellationToken ct) => Ok(await reportSchedules.HistoryAsync(ct));

    // --- §35-36 Prévisions ---------------------------------------------------

    [HttpGet("forecasts")]
    [RequirePermission(Permissions.Reporting.ViewFinancial)]
    public async Task<IActionResult> Forecasts([FromQuery] string? metric, CancellationToken ct) => Ok(await forecasts.ListAsync(metric, ct));

    [HttpPost("forecasts")]
    [RequirePermission(Permissions.Reporting.Forecast)]
    public async Task<IActionResult> GenerateForecast([FromBody] GenerateForecastRequest request, CancellationToken ct)
        => FromResult(await forecasts.GenerateAsync(request, ct));

    // --- §37-38, §58 Insights ------------------------------------------------

    /// <summary>Insights filtrés par visibilité (guide §18, §19) : baisse de CA → reporting.view_sales ;
    /// marge, dépenses, trésorerie → reporting.view_financial.</summary>
    [HttpGet("insights")]
    [RequirePermission(Permissions.Reporting.View)]
    public async Task<IActionResult> Insights([FromQuery] string? status, CancellationToken ct)
    {
        var granted = await permissions.CurrentPermissionsAsync(tenant, ct);
        return Ok((await insights.ListAsync(status, ct)).Where(i => i.Category switch
        {
            "revenue_drop" => granted.Contains(Permissions.Reporting.ViewSales),
            "margin_drop" or "expense_spike" or "cash_gap" => granted.Contains(Permissions.Reporting.ViewFinancial),
            _ => true,
        }).ToList());
    }

    [HttpPost("insights/{id:guid}/dismiss")]
    [RequirePermission(Permissions.Reporting.ManageInsights)]
    public async Task<IActionResult> DismissInsight(Guid id, [FromBody] DismissInsightRequest request, CancellationToken ct)
        => FromResult(await insights.DismissAsync(id, request, ct));

    [HttpPost("insights/{id:guid}/assign")]
    [RequirePermission(Permissions.Reporting.ManageInsights)]
    public async Task<IActionResult> AssignInsight(Guid id, [FromBody] AssignInsightRequest request, CancellationToken ct)
        => FromResult(await insights.AssignAsync(id, request, ct));

    // --- §33-34 Budgets --------------------------------------------------------

    [HttpGet("budgets")]
    [RequirePermission(Permissions.Reporting.ViewFinancial)]
    public async Task<IActionResult> Budgets(CancellationToken ct) => Ok(await budgets.ListAsync(ct));

    [HttpPost("budgets")]
    [RequirePermission(Permissions.Reporting.ManageBudgets)]
    public async Task<IActionResult> CreateBudget([FromBody] CreateBudgetRequest request, CancellationToken ct)
        => FromResult(await budgets.CreateAsync(request, ct));

    [HttpGet("budgets/{id:guid}")]
    [RequirePermission(Permissions.Reporting.ViewFinancial)]
    public async Task<IActionResult> Budget(Guid id, CancellationToken ct) => FromResult(await budgets.GetAsync(id, ct));

    [HttpGet("budgets/{id:guid}/lines")]
    [RequirePermission(Permissions.Reporting.ViewFinancial)]
    public async Task<IActionResult> BudgetLines(Guid id, CancellationToken ct) => FromResult(await budgets.GetLinesAsync(id, ct));

    [HttpPut("budgets/{id:guid}/lines")]
    [RequirePermission(Permissions.Reporting.ManageBudgets)]
    public async Task<IActionResult> SetBudgetLines(Guid id, [FromBody] SetBudgetLinesRequest request, CancellationToken ct)
        => FromResult(await budgets.SetLinesAsync(id, request, ct));

    [HttpPost("budgets/{id:guid}/submit")]
    [RequirePermission(Permissions.Reporting.ManageBudgets)]
    public async Task<IActionResult> SubmitBudget(Guid id, CancellationToken ct) => FromResult(await budgets.SubmitAsync(id, ct));

    [HttpPost("budgets/{id:guid}/approve")]
    [RequirePermission(Permissions.Reporting.ManageBudgets)]
    public async Task<IActionResult> ApproveBudget(Guid id, CancellationToken ct) => FromResult(await budgets.ApproveAsync(id, ct));

    [HttpPost("budgets/{id:guid}/close")]
    [RequirePermission(Permissions.Reporting.ManageBudgets)]
    public async Task<IActionResult> CloseBudget(Guid id, CancellationToken ct) => FromResult(await budgets.CloseAsync(id, ct));

    [HttpPost("budgets/{id:guid}/revise")]
    [RequirePermission(Permissions.Reporting.ManageBudgets)]
    public async Task<IActionResult> ReviseBudget(Guid id, CancellationToken ct) => FromResult(await budgets.ReviseAsync(id, ct));

    // --- §55 Catalogue des tableaux de bord (les dispositions elles-mêmes restent Phase 40) ----

    [HttpGet("dashboards")]
    [RequirePermission(Permissions.Reporting.View)]
    public IActionResult Dashboards() => Ok(new object[]
    {
        new { Key = "cashier", Label = "Caissier", Endpoint = "/dashboard/cashier/{sessionId}", Permission = Permissions.Reporting.View },
        new { Key = "manager", Label = "Manager", Endpoint = "/dashboard/manager", Permission = Permissions.Reporting.ViewFinancial },
        new { Key = "command_center", Label = "Direction — Command Center", Endpoint = "/dashboard/command-center", Permission = Permissions.Reporting.View },
        new { Key = "group", Label = "Groupe", Endpoint = "/dashboard/groups/{groupId}", Permission = Permissions.Reporting.ViewGroup },
    });

    [HttpPost("dashboards")]
    [RequirePermission(Permissions.Reporting.View)]
    public async Task<IActionResult> SaveDashboard([FromBody] SaveDashboardLayoutRequest request, CancellationToken ct)
        => FromResult(await dashboardLayouts.SaveAsync(request, ct));
}
