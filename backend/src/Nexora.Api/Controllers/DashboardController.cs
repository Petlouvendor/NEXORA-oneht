using Asp.Versioning;
using Microsoft.AspNetCore.Mvc;
using Nexora.Api.Security;
using Nexora.Application.Reporting;
using Nexora.Application.Security;
using Nexora.Contracts.Reporting;

namespace Nexora.Api.Controllers;

/// <summary>
/// Dashboard & pilotage (Phase 7 §4-12, §28). Les valeurs sont dérivées des données
/// validées et réconciliables avec les rapports comptables (§2, §34). Le périmètre
/// effectif reste borné par la RLS serveur, quel que soit le filtre demandé (§6, §36).
/// </summary>
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/dashboard")]
public sealed class DashboardController(
    IKpiService kpi,
    IAlertCenter alerts,
    IDrillDown drill) : ApiControllerBase
{
    /// <summary>Command Center — KPI universels, tendance et comparaison de période (§4-5).</summary>
    [HttpGet("command-center")]
    [RequirePermission(Permissions.Reporting.View)]
    public async Task<IActionResult> CommandCenter([FromQuery] ReportFilter filter, CancellationToken ct)
        => Ok(await kpi.CommandCenterAsync(filter, ct));

    /// <summary>Tableau de bord caissier — sa session en cours (§7). Guide §13, §18 : accessible au
    /// caissier (sans le pilotage) pour SA session ; toute autre session exige pos.manage_sessions.</summary>
    [HttpGet("cashier/{sessionId:guid}")]
    [RequirePermission(Permissions.Pos.View)]
    public async Task<IActionResult> Cashier(Guid sessionId, CancellationToken ct)
        => Ok(await kpi.CashierDashboardAsync(sessionId, ct));

    /// <summary>Tableau de bord responsable — établissement / point de vente (§8).</summary>
    [HttpGet("manager")]
    [RequirePermission(Permissions.Reporting.ViewFinancial)]
    public async Task<IActionResult> Manager([FromQuery] ReportFilter filter, CancellationToken ct)
        => Ok(await kpi.ManagerDashboardAsync(filter, ct));

    /// <summary>Tableau de bord groupe / consolidé — entreprises non autorisées exclues (§12, §37).</summary>
    [HttpGet("groups/{groupId:guid}")]
    [RequirePermission(Permissions.Reporting.ViewGroup)]
    public async Task<IActionResult> Group(Guid groupId, [FromQuery] ReportFilter filter, CancellationToken ct)
        => Ok(await kpi.GroupDashboardAsync(groupId, filter, ct));

    /// <summary>Centre d'alertes — évalué à la volée sur les données validées (§28).</summary>
    [HttpGet("alerts")]
    [RequirePermission(Permissions.Reporting.View)]
    public async Task<IActionResult> Alerts(CancellationToken ct)
        => Ok(await alerts.EvaluateAsync(ct));

    /// <summary>Explorer un KPI vers ses transactions sources (§29). Ex. : <c>revenue/2026-08-01</c>.</summary>
    [HttpGet("drill")]
    [RequirePermission(Permissions.Reporting.ViewFinancial)]
    public async Task<IActionResult> Drill([FromQuery] string path, CancellationToken ct)
        => Ok(await drill.ExpandAsync(path, ct));

    /// <summary>Catalogue documenté des indicateurs — source et formule de chaque KPI (§34).</summary>
    [HttpGet("kpi-catalog")]
    [RequirePermission(Permissions.Reporting.View)]
    public IActionResult KpiCatalogListing()
        => Ok(KpiCatalog.All);
}
