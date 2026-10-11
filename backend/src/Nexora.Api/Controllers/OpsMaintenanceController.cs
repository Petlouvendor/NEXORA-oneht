using Asp.Versioning;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;
using Nexora.Api.Security;
using Nexora.Application.Operations;
using Nexora.Application.Security;
using Nexora.Contracts.Operations;

namespace Nexora.Api.Controllers;

/// <summary>
/// Administration opérationnelle & maintenance (Phase 41 §3-51) : registre des jobs planifiés,
/// files d'attente & dead-letter, diagnostics, base de données, stockage, cache, bascules de
/// fonctionnalité, maintenance planifiée, rétention & purge technique, correction de donnée
/// contrôlée. Complète <see cref="OperationsController"/> (Phase 37 : état système, Go-Live,
/// déploiements) sous le même module de permission <c>operations.*</c>.
/// </summary>
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/ops")]
public sealed class OpsMaintenanceController(
    IJobRegistry jobs,
    IQueueAdminService queues,
    IOpsDiagnosticsService diagnostics,
    IFeatureFlagService flags,
    IMaintenanceService maintenance,
    IRetentionPurgeService retention,
    IDataCorrectionService corrections,
    IMemoryCache cache) : ApiControllerBase
{
    // --- §7-9 Jobs planifiés ------------------------------------------------

    [HttpGet("jobs")]
    [RequirePermission(Permissions.Operations.View)]
    public async Task<IActionResult> Jobs(CancellationToken ct) => Ok(await jobs.ListAsync(ct));

    [HttpGet("jobs/{key}/history")]
    [RequirePermission(Permissions.Operations.View)]
    public async Task<IActionResult> JobHistory(string key, CancellationToken ct) => Ok(await jobs.HistoryAsync(key, ct));

    [HttpPost("jobs/{key}/run")]
    [RequirePermission(Permissions.Operations.Manage)]
    public async Task<IActionResult> RunJob(string key, CancellationToken ct) => FromResult(await jobs.RunNowAsync(key, ct));

    [HttpPost("jobs/{key}/pause")]
    [RequirePermission(Permissions.Operations.Manage)]
    public async Task<IActionResult> PauseJob(string key, CancellationToken ct) => FromResult(await jobs.PauseAsync(key, ct));

    [HttpPost("jobs/{key}/resume")]
    [RequirePermission(Permissions.Operations.Manage)]
    public async Task<IActionResult> ResumeJob(string key, CancellationToken ct) => FromResult(await jobs.ResumeAsync(key, ct));

    // --- §10-12 Files d'attente & dead-letter -------------------------------

    [HttpGet("queues")]
    [RequirePermission(Permissions.Operations.View)]
    public async Task<IActionResult> Queues(CancellationToken ct) => Ok(await queues.StatsAsync(ct));

    [HttpPost("queues/{id:guid}/retry")]
    [RequirePermission(Permissions.Operations.Manage)]
    public async Task<IActionResult> RetryMessage(Guid id, CancellationToken ct) => FromResult(await queues.RetryAsync(id, ct));

    [HttpGet("dead-letters")]
    [RequirePermission(Permissions.Operations.View)]
    public async Task<IActionResult> DeadLetters(CancellationToken ct) => Ok(await queues.DeadLettersAsync(ct));

    [HttpPost("dead-letters/{id:guid}/replay")]
    [RequirePermission(Permissions.Operations.Manage)]
    public async Task<IActionResult> ReplayDeadLetter(Guid id, CancellationToken ct) => FromResult(await queues.ReplayDeadLetterAsync(id, ct));

    [HttpDelete("dead-letters/{id:guid}")]
    [RequirePermission(Permissions.Operations.Manage)]
    public async Task<IActionResult> DeleteDeadLetter(Guid id, CancellationToken ct) => FromResult(await queues.DeleteDeadLetterAsync(id, ct));

    // --- §5-6, §13-15 Diagnostics, base de données & stockage ---------------

    [HttpGet("diagnostics")]
    [HttpGet("health")]
    [RequirePermission(Permissions.Operations.View)]
    public async Task<IActionResult> Diagnostics(CancellationToken ct) => Ok(await diagnostics.RunAllAsync(ct));

    [HttpGet("database")]
    [RequirePermission(Permissions.Operations.View)]
    public async Task<IActionResult> Database(CancellationToken ct) => Ok(await diagnostics.DatabaseAsync(ct));

    [HttpGet("storage")]
    [RequirePermission(Permissions.Operations.View)]
    public async Task<IActionResult> Storage(CancellationToken ct) => Ok(await diagnostics.StorageAsync(ct));

    // --- §20 Cache ------------------------------------------------------------

    [HttpPost("cache/invalidate")]
    [RequirePermission(Permissions.Operations.Manage)]
    public IActionResult InvalidateCache()
    {
        if (cache is MemoryCache mc) mc.Compact(1.0);   // purge du seul cache technique en mémoire (clés d'API, idempotence)
        return NoContent();
    }

    // --- §27 Bascules de fonctionnalité ---------------------------------------

    [HttpGet("feature-flags")]
    [RequirePermission(Permissions.Operations.View)]
    public async Task<IActionResult> FeatureFlags(CancellationToken ct) => Ok(await flags.ListAsync(ct));

    [HttpPost("feature-flags")]
    [RequirePermission(Permissions.Operations.Manage)]
    public async Task<IActionResult> SaveFeatureFlag([FromBody] SaveFeatureFlagRequest request, CancellationToken ct)
        => FromResult(await flags.SaveAsync(request, ct));

    [HttpDelete("feature-flags/{id:guid}")]
    [RequirePermission(Permissions.Operations.Manage)]
    public async Task<IActionResult> DeleteFeatureFlag(Guid id, CancellationToken ct) => FromResult(await flags.DeleteAsync(id, ct));

    // --- §23-24 Maintenance planifiée -----------------------------------------

    [HttpGet("maintenance")]
    [RequirePermission(Permissions.Operations.View)]
    public async Task<IActionResult> MaintenanceWindows(CancellationToken ct) => Ok(await maintenance.ListAsync(ct));

    [HttpPost("maintenance")]
    [RequirePermission(Permissions.Operations.Manage)]
    public async Task<IActionResult> ScheduleMaintenance([FromBody] ScheduleMaintenanceRequest request, CancellationToken ct)
        => FromResult(await maintenance.ScheduleAsync(request, ct));

    [HttpPost("maintenance/{id:guid}/transition")]
    [RequirePermission(Permissions.Operations.Manage)]
    public async Task<IActionResult> TransitionMaintenance(Guid id, [FromBody] TransitionMaintenanceRequest request, CancellationToken ct)
        => FromResult(await maintenance.TransitionAsync(id, request, ct));

    // --- §16-19, §22 Rétention & purge technique -------------------------------

    [HttpGet("retention-policies")]
    [RequirePermission(Permissions.Operations.View)]
    public async Task<IActionResult> RetentionPolicies(CancellationToken ct) => Ok(await retention.ListPoliciesAsync(ct));

    [HttpPost("retention-policies")]
    [RequirePermission(Permissions.Operations.Manage)]
    public async Task<IActionResult> SaveRetentionPolicy([FromBody] SaveRetentionPolicyRequest request, CancellationToken ct)
        => FromResult(await retention.SavePolicyAsync(request, ct));

    [HttpPost("purge/preview")]
    [RequirePermission(Permissions.Operations.Manage)]
    public async Task<IActionResult> PreviewPurge([FromBody] PurgePreviewRequest request, CancellationToken ct)
        => Ok(await retention.PreviewAsync(request, ct));

    [HttpPost("purge/execute")]
    [RequireStepUp(StepUpPurposes.Deletion)]
    [RequirePermission(Permissions.Operations.Manage)]
    public async Task<IActionResult> ExecutePurge([FromBody] PurgePreviewRequest request, CancellationToken ct)
        => FromResult(await retention.ExecuteAsync(request, ct));

    [HttpGet("purge/history")]
    [RequirePermission(Permissions.Operations.View)]
    public async Task<IActionResult> PurgeHistory(CancellationToken ct) => Ok(await retention.HistoryAsync(ct));

    // --- §44 Correction de donnée contrôlée ------------------------------------

    [HttpGet("data-corrections")]
    [RequirePermission(Permissions.Operations.View)]
    public async Task<IActionResult> DataCorrections([FromQuery] string? status, CancellationToken ct) => Ok(await corrections.ListAsync(status, ct));

    [HttpPost("data-corrections")]
    [RequirePermission(Permissions.Operations.Manage)]
    public async Task<IActionResult> RequestDataCorrection([FromBody] RequestDataCorrectionRequest request, CancellationToken ct)
        => FromResult(await corrections.RequestAsync(request, ct));

    [HttpPost("data-corrections/{id:guid}/decide")]
    [RequirePermission(Permissions.Operations.CorrectData)]
    public async Task<IActionResult> DecideDataCorrection(Guid id, [FromBody] DecideDataCorrectionRequest request, CancellationToken ct)
        => FromResult(await corrections.DecideAsync(id, request, ct));
}
