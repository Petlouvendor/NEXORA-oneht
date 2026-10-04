using Asp.Versioning;
using Microsoft.AspNetCore.Mvc;
using Nexora.Api.Security;
using Nexora.Application.Intelligence;
using Nexora.Application.Mobile;
using Nexora.Application.Security;
using Nexora.Application.Sync;
using Nexora.Contracts.Mobile;
using Nexora.Contracts.Notifications;
using Nexora.Contracts.Sync;

namespace Nexora.Api.Controllers;

/// <summary>
/// Mobile, terrain & multi-appareils (Phase 44 §25, §46, §48). <c>/mobile/{sync,conflicts}</c>
/// réexposent tels quels <see cref="ISyncService"/> (Phase 10) — aucune duplication du moteur
/// offline. <c>/mobile/{tasks,notifications}</c> réexposent Phase 43/34. Pièces jointes,
/// tournées de livraison et interventions sont propres à cette phase.
/// </summary>
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}")]
public sealed class MobileController(
    ISyncService sync,
    IMobileBootstrapService bootstrap,
    ICrashReportService crashReports,
    IAttachmentService attachments,
    IDeliveryService delivery,
    IFieldInterventionService interventions,
    ITaskService tasks,
    Nexora.Application.Notifications.INotificationService notifications,
    Nexora.Application.Sales.IPosOfflineService devices) : ApiControllerBase
{
    /// <summary>Phase 12 : options de saisie terrain (livreurs/techniciens, commandes livrables, caisses, géolocalisation).</summary>
    [HttpGet("field-ops/options")]
    [RequirePermission(Permissions.FieldOps.View)]
    public async Task<IActionResult> FieldOptions(CancellationToken ct) => Ok(await delivery.OptionsAsync(ct));

    /// <summary>Phase 12 : le navigateur du livreur / technicien s'enregistre comme appareil de synchronisation
    /// (même registre que la caisse, idempotent sur l'identifiant, révocable par un responsable).</summary>
    [HttpPost("field-ops/device")]
    [RequirePermission(Permissions.FieldOps.Manage)]
    public async Task<IActionResult> EnrollFieldDevice([FromBody] Nexora.Contracts.Sales.EnrollPosDeviceRequest r, CancellationToken ct)
        => FromResult(await devices.EnrollAsync(r with { Name = string.IsNullOrWhiteSpace(r.Name) ? "Terrain (navigateur)" : r.Name }, ct));

    // --- §48 Synchronisation & conflits — alias de Phase 10, sans duplication ------

    [HttpGet("mobile/sync")]
    [RequirePermission(Permissions.Sync.View)]
    public async Task<IActionResult> SyncStatus(CancellationToken ct) => Ok(await sync.SyncCenterAsync(ct));

    [HttpPost("mobile/sync/push")]
    [RequirePermission(Permissions.Sync.View)]   // chaque opération du lot est contrôlée selon son type (phase 12)
    public async Task<IActionResult> SyncPush([FromBody] PushBatchRequest request, CancellationToken ct)
        => FromResult(await sync.PushBatchAsync(request, ct));

    /// <summary>§25 « données autorisées en cache » — périmètre strict de l'appelant.</summary>
    [HttpPost("mobile/sync/pull")]
    [RequirePermission(Permissions.Sync.View)]
    public async Task<IActionResult> SyncPull(CancellationToken ct) => Ok(await bootstrap.PullAsync(ct));

    [HttpGet("mobile/conflicts")]
    [RequirePermission(Permissions.Sync.View)]
    public async Task<IActionResult> Conflicts(CancellationToken ct) => Ok((await sync.SyncCenterAsync(ct)).Conflicts);

    [HttpPost("mobile/conflicts/{conflictId:guid}/resolve")]
    [RequirePermission(Permissions.Sync.Manage)]
    public async Task<IActionResult> ResolveConflict(Guid conflictId, [FromBody] ResolveConflictRequest r, CancellationToken ct)
        => FromResult(await sync.ResolveConflictAsync(conflictId, r, ct));

    // --- §48 Config, diagnostics, tâches, notifications ----------------------------

    [HttpGet("mobile/config")]
    [RequirePermission(Permissions.Sync.View)]
    public async Task<IActionResult> Config(CancellationToken ct) => Ok(await bootstrap.ConfigAsync(ct));

    [HttpPost("mobile/crash-reports")]
    [RequirePermission(Permissions.Sync.View)]
    public async Task<IActionResult> ReportCrash([FromBody] ReportCrashRequest request, CancellationToken ct)
        => FromResult(await crashReports.ReportAsync(request, ct));

    [HttpGet("mobile/crash-reports")]
    [RequirePermission(Permissions.Sync.Manage)]
    public async Task<IActionResult> CrashReports([FromQuery] int limit, CancellationToken ct)
        => Ok(await crashReports.ListAsync(limit == 0 ? 100 : limit, ct));

    [HttpGet("mobile/notifications")]
    [RequirePermission(Permissions.Sync.View)]
    public async Task<IActionResult> Notifications(CancellationToken ct)
        => Ok(await notifications.ListAsync(new NotificationQuery(), ct));

    [HttpGet("mobile/tasks")]
    [RequirePermission(Permissions.Sync.View)]
    public async Task<IActionResult> Tasks([FromQuery] string? status, CancellationToken ct)
        => Ok(await tasks.ListAsync(status, ct));

    // --- §22-23 Pièces jointes minimales (photo/signature) -------------------------

    [HttpPost("attachments")]
    [RequirePermission(Permissions.FieldOps.Manage)]
    public async Task<IActionResult> UploadAttachment([FromBody] UploadAttachmentRequest request, CancellationToken ct)
        => FromResult(await attachments.UploadAsync(request, ct));

    [HttpGet("attachments/{id:guid}")]
    [RequirePermission(Permissions.FieldOps.View)]
    public async Task<IActionResult> GetAttachment(Guid id, CancellationToken ct)
    {
        var result = await attachments.GetContentAsync(id, ct);
        return result.IsSuccess ? File(result.Value!.Content, result.Value.ContentType) : FromResult(result);
    }

    // --- §12, §52 Tournées de livraison ----------------------------------------------

    [HttpGet("field-ops/routes")]
    [RequirePermission(Permissions.FieldOps.View)]
    public async Task<IActionResult> Routes([FromQuery] DateOnly? date, CancellationToken ct) => Ok(await delivery.ListRoutesAsync(date, ct));

    [HttpPost("field-ops/routes")]
    [RequirePermission(Permissions.FieldOps.Manage)]
    public async Task<IActionResult> CreateRoute([FromBody] CreateDeliveryRouteRequest request, CancellationToken ct)
        => FromResult(await delivery.CreateRouteAsync(request, ct));

    [HttpGet("field-ops/routes/{id:guid}")]
    [RequirePermission(Permissions.FieldOps.View)]
    public async Task<IActionResult> Route(Guid id, CancellationToken ct) => FromResult(await delivery.GetRouteAsync(id, ct));

    [HttpGet("field-ops/my-stops")]
    [RequirePermission(Permissions.FieldOps.View)]
    public async Task<IActionResult> MyStops(CancellationToken ct) => Ok(await delivery.MyStopsAsync(ct));

    [HttpPost("field-ops/stops/{id:guid}/advance")]
    [RequirePermission(Permissions.FieldOps.Manage)]
    public async Task<IActionResult> AdvanceStop(Guid id, [FromBody] string status, CancellationToken ct)
        => FromResult(await delivery.AdvanceStopAsync(id, status, ct));

    /// <summary>Complétion en ligne (hors file offline) — pour le rejeu hors ligne, voir <c>POST /mobile/sync/push</c>
    /// avec <c>entityType = "delivery_stop"</c> (§25-29).</summary>
    [HttpPost("field-ops/stops/{id:guid}/complete")]
    [RequirePermission(Permissions.FieldOps.Manage)]
    public async Task<IActionResult> CompleteStop(Guid id, [FromBody] CompleteDeliveryStopRequest request, CancellationToken ct)
        => FromResult(await delivery.CompleteStopAsync(id, request, ct));

    [HttpPost("field-ops/stops/{id:guid}/fail")]
    [RequirePermission(Permissions.FieldOps.Manage)]
    public async Task<IActionResult> FailStop(Guid id, [FromBody] FailDeliveryStopRequest request, CancellationToken ct)
        => FromResult(await delivery.FailStopAsync(id, request, ct));

    // --- §13 Interventions techniciens -------------------------------------------------

    [HttpGet("field-ops/interventions")]
    [RequirePermission(Permissions.FieldOps.View)]
    public async Task<IActionResult> Interventions([FromQuery] string? status, CancellationToken ct) => Ok(await interventions.ListAsync(status, ct));

    [HttpPost("field-ops/interventions")]
    [RequirePermission(Permissions.FieldOps.Manage)]
    public async Task<IActionResult> CreateIntervention([FromBody] CreateInterventionRequest request, CancellationToken ct)
        => FromResult(await interventions.CreateAsync(request, ct));

    [HttpGet("field-ops/interventions/mine")]
    [RequirePermission(Permissions.FieldOps.View)]
    public async Task<IActionResult> MyInterventions(CancellationToken ct) => Ok(await interventions.MineAsync(ct));

    [HttpPost("field-ops/interventions/{id:guid}/start")]
    [RequirePermission(Permissions.FieldOps.Manage)]
    public async Task<IActionResult> StartIntervention(Guid id, CancellationToken ct) => FromResult(await interventions.StartAsync(id, ct));

    /// <summary>Complétion en ligne — pour le rejeu hors ligne, <c>entityType = "field_intervention"</c> (§25-29).</summary>
    [HttpPost("field-ops/interventions/{id:guid}/complete")]
    [RequirePermission(Permissions.FieldOps.Manage)]
    public async Task<IActionResult> CompleteIntervention(Guid id, [FromBody] CompleteInterventionRequest request, CancellationToken ct)
        => FromResult(await interventions.CompleteAsync(id, request, ct));
}
