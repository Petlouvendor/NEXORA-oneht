using Asp.Versioning;
using Microsoft.AspNetCore.Mvc;
using Nexora.Api.Security;
using Nexora.Application.Reporting;
using Nexora.Application.Security;
using Nexora.Contracts.Reporting;

namespace Nexora.Api.Controllers;

/// <summary>
/// Analytics — couche de confiance du reporting (Phase 31 §2, §44-46, §50). Prouve que les
/// chiffres du reporting correspondent aux sources métier / comptables, contrôle la qualité
/// des données et fige des photos reproductibles. Le périmètre reste borné par la RLS (§49).
/// </summary>
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/analytics")]
public sealed class AnalyticsController(
    IKpiReconciliation reconciliation,
    IDataQualityService dataQuality,
    IAnalyticsSnapshotService snapshots,
    IKpiService kpi,
    IAlertCenter alerts) : ApiControllerBase
{
    // --- §46 Réconciliation KPI --------------------------------------

    [HttpGet("reconciliation")]
    [RequirePermission(Permissions.Reporting.DataQuality)]
    public async Task<IActionResult> Reconciliation([FromQuery] DateOnly? from, [FromQuery] DateOnly? to, CancellationToken ct)
        => Ok(await reconciliation.ReconcileAsync(from, to, ct));

    // --- §45 Data quality -----------------------------------------

    [HttpGet("data-quality")]
    [HttpGet("~/api/v{version:apiVersion}/reporting/data-quality")]
    [RequirePermission(Permissions.Reporting.DataQuality)]
    public async Task<IActionResult> DataQuality(CancellationToken ct)
        => Ok(await dataQuality.EvaluateAsync(ct));

    // --- §44 Snapshots -------------------------------------------

    [HttpGet("snapshots")]
    [RequirePermission(Permissions.Reporting.DataQuality)]
    public async Task<IActionResult> Snapshots([FromQuery] string? kind, CancellationToken ct)
        => Ok(await snapshots.ListAsync(kind, ct));

    [HttpGet("snapshots/{id:guid}")]
    [RequirePermission(Permissions.Reporting.DataQuality)]
    public async Task<IActionResult> Snapshot(Guid id, CancellationToken ct)
        => FromResult(await snapshots.GetAsync(id, ct));

    [HttpPost("snapshots")]
    [RequirePermission(Permissions.Reporting.DataQuality)]
    public async Task<IActionResult> CreateSnapshot([FromBody] CreateSnapshotRequest r, CancellationToken ct)
        => FromResult(await snapshots.CreateAsync(r, ct));

    // --- §50 alias canoniques (servis par Phase 7) ---------------

    [HttpGet("~/api/v{version:apiVersion}/kpis")]
    [RequirePermission(Permissions.Reporting.View)]
    public async Task<IActionResult> Kpis([FromQuery] ReportFilter filter, CancellationToken ct)
        => Ok(await kpi.CommandCenterAsync(filter, ct));

    [HttpGet("~/api/v{version:apiVersion}/alerts")]
    [RequirePermission(Permissions.Reporting.View)]
    public async Task<IActionResult> Alerts(CancellationToken ct)
        => Ok(await alerts.EvaluateAsync(ct));
}
