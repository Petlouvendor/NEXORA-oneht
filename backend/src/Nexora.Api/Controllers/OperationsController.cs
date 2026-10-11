using Asp.Versioning;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Nexora.Api.Security;
using Nexora.Application.Operations;
using Nexora.Application.Security;
using Nexora.Contracts.Operations;
using Nexora.Infrastructure.Operations;
using Nexora.Infrastructure.Setup;

namespace Nexora.Api.Controllers;

/// <summary>
/// Qualité, exploitation &amp; préparation production (Phase 37 §29, §34, §38-40, §54, §56).
/// Tableau d'état système consolidé, checklist Go-Live dérivée en direct des signaux existants,
/// journal des déploiements réversibles et validations Go-Live avec instantané figé.
/// </summary>
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/operations")]
public sealed class OperationsController(IOperationsService operations) : ApiControllerBase
{
    // --- Finalisation phase 18 : état de production --------------------------

    /// <summary>
    /// Version et commit déployés, sondes readiness, écart de migrations, contrôles de configuration de production
    /// (secrets, HTTPS, proxy, observabilité, journaux, planificateur, e-mail). Lecture seule, sans secret.
    /// </summary>
    [HttpGet("~/api/v{version:apiVersion}/ops/production")]
    [RequirePermission(Permissions.Operations.View)]
    public async Task<IActionResult> Production(
        [FromServices] HealthCheckService health, [FromServices] MigrationStatusProbe migrationProbe,
        [FromServices] IConfiguration configuration, [FromServices] RuntimeEnvironment runtime, CancellationToken ct)
    {
        var report = await health.CheckHealthAsync(c => c.Tags.Contains("ready"), ct);
        var probes = report.Entries.Select(e => new HealthProbeDto(e.Key, e.Value.Status.ToString(), e.Value.Description,
            Math.Round(e.Value.Duration.TotalMilliseconds, 1))).ToList();

        MigrationStatusDto? migrations = null;
        try
        {
            var m = await migrationProbe.GetAsync(ct);
            migrations = new(m.Embedded, m.Applied, m.LatestApplied, m.LatestEmbedded, m.Pending, m.Drifted, m.Unknown);
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            // Base injoignable : la sonde « database » le dit déjà.
        }

        var checks = ProductionConfiguration.Evaluate(configuration, runtime.Name)
            .Select(c => new ProductionCheckDto(c.Key, c.Label, c.Category, c.Status, c.Detail)).ToList();
        var overall = report.Status == HealthStatus.Unhealthy || checks.Any(c => c.Status == "fail") ? "fail"
            : report.Status == HealthStatus.Degraded || checks.Any(c => c.Status == "warn") ? "warn" : "pass";
        var now = DateTimeOffset.UtcNow;
        return Ok(new ProductionReadinessDto(runtime.Name, runtime.Version, runtime.Commit, runtime.StartedAt,
            (long)(now - runtime.StartedAt).TotalSeconds, overall, probes, migrations, checks, now));
    }

    // --- §29, §56 État système ---------------------------------------------

    [HttpGet("~/api/v{version:apiVersion}/system/status")]
    [HttpGet("~/api/v{version:apiVersion}/ops/status")]
    [RequirePermission(Permissions.Operations.View)]
    public async Task<IActionResult> SystemStatus(CancellationToken ct) => Ok(await operations.SystemStatusAsync(ct));

    // --- §40, §54 Checklist Go-Live --------------------------------------

    [HttpGet("~/api/v{version:apiVersion}/go-live")]
    [RequirePermission(Permissions.Operations.View)]
    public async Task<IActionResult> GoLive(CancellationToken ct) => Ok(await operations.GoLiveReadinessAsync(ct));

    [HttpGet("~/api/v{version:apiVersion}/go-live/sign-offs")]
    [RequirePermission(Permissions.Operations.View)]
    public async Task<IActionResult> SignOffs(CancellationToken ct) => Ok(await operations.ListSignOffsAsync(ct));

    [HttpPost("~/api/v{version:apiVersion}/go-live/sign-off")]
    [RequirePermission(Permissions.Operations.Manage)]
    public async Task<IActionResult> SignOff([FromBody] SignOffRequest request, CancellationToken ct)
        => FromResult(await operations.SignOffAsync(request, ct));

    // --- §34, §38-39 Déploiements / releases (route canonique + alias) ----

    [HttpGet("~/api/v{version:apiVersion}/deployments")]
    [HttpGet("~/api/v{version:apiVersion}/releases")]
    [RequirePermission(Permissions.Operations.View)]
    public async Task<IActionResult> Deployments(CancellationToken ct) => Ok(await operations.ListDeploymentsAsync(ct));

    [HttpPost("~/api/v{version:apiVersion}/deployments")]
    [HttpPost("~/api/v{version:apiVersion}/releases")]
    [RequirePermission(Permissions.Operations.Manage)]
    public async Task<IActionResult> RecordDeployment([FromBody] RecordDeploymentRequest request, CancellationToken ct)
        => FromResult(await operations.RecordDeploymentAsync(request, ct));

    [HttpPost("~/api/v{version:apiVersion}/deployments/{id:guid}/rollback")]
    [RequirePermission(Permissions.Operations.Manage)]
    public async Task<IActionResult> RollBack(Guid id, [FromBody] RollbackRequest request, CancellationToken ct)
        => FromResult(await operations.RollBackAsync(id, request, ct));
}
