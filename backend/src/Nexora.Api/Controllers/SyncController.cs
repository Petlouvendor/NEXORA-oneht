using Asp.Versioning;
using Microsoft.AspNetCore.Mvc;
using Nexora.Api.Security;
using Nexora.Application.Security;
using Nexora.Application.Sync;
using Nexora.Contracts.Sync;

namespace Nexora.Api.Controllers;

/// <summary>
/// Synchronisation offline (Phase 10 §11-16, §30-31). Le mode de déploiement ne change pas
/// les règles métier : une opération offline est rejouée via le même parcours de vente, avec
/// la même idempotence (operation_uuid). Un désaccord devient un conflit, jamais un écrasement.
/// </summary>
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/sync")]
public sealed class SyncController(
    ISyncService sync,
    IDeviceRegistryService devices,
    IOfflinePolicyService offlinePolicy) : ApiControllerBase
{
    // --- Push d'un lot d'opérations offline (§14) -------------------

    [HttpPost("batch")]
    [RequirePermission(Permissions.Sync.View)]   // chaque opération du lot est contrôlée selon son type (phase 12)
    public async Task<IActionResult> PushBatch([FromBody] PushBatchRequest request, CancellationToken ct)
        => FromResult(await sync.PushBatchAsync(request, ct));

    // --- Centre de synchronisation (§30) --------------------------

    [HttpGet("center")]
    [RequirePermission(Permissions.Sync.View)]
    public async Task<IActionResult> Center(CancellationToken ct)
        => Ok(await sync.SyncCenterAsync(ct));

    [HttpPost("conflicts/{conflictId:guid}/resolve")]
    [RequirePermission(Permissions.Sync.Manage)]
    public async Task<IActionResult> ResolveConflict(Guid conflictId, [FromBody] ResolveConflictRequest r, CancellationToken ct)
        => FromResult(await sync.ResolveConflictAsync(conflictId, r, ct));

    // --- Appareils (§12, §31 ; alias canoniques Phase 44 §48 : ~/api/v1/devices*) --

    [HttpGet("devices")]
    [HttpGet("~/api/v{version:apiVersion}/devices")]
    [RequirePermission(Permissions.Sync.View)]
    public async Task<IActionResult> Devices(CancellationToken ct)
        => Ok(await devices.ListAsync(ct));

    [HttpPost("devices")]
    [HttpPost("~/api/v{version:apiVersion}/devices")]
    [RequirePermission(Permissions.Sync.Devices)]
    public async Task<IActionResult> Register([FromBody] RegisterDeviceRequest r, CancellationToken ct)
        => FromResult(await devices.RegisterAsync(r, ct));

    [HttpPost("devices/{deviceId:guid}/revoke")]
    [HttpPost("~/api/v{version:apiVersion}/devices/{deviceId:guid}/revoke")]
    [RequirePermission(Permissions.Sync.Devices)]
    public async Task<IActionResult> Revoke(Guid deviceId, [FromBody] RevokeDeviceRequest r, CancellationToken ct)
        => FromResult(await devices.RevokeAsync(deviceId, r, ct));

    [HttpPost("devices/{deviceId:guid}/heartbeat")]
    [RequirePermission(Permissions.Sync.View)]
    public async Task<IActionResult> Heartbeat(Guid deviceId, CancellationToken ct)
        => FromResult(await devices.HeartbeatAsync(deviceId, ct));

    /// <summary>Jeton push (Phase 44 §20, §32) — appareil personnel uniquement.</summary>
    [HttpPost("devices/{deviceId:guid}/push-token")]
    [RequirePermission(Permissions.Sync.Devices)]
    public async Task<IActionResult> RegisterPushToken(Guid deviceId, [FromBody] Nexora.Contracts.Mobile.RegisterPushTokenRequest r, CancellationToken ct)
        => FromResult(await devices.RegisterPushTokenAsync(deviceId, r, ct));

    // --- Politique offline (§10, §17) ---------------------------

    [HttpGet("offline-policy")]
    [RequirePermission(Permissions.Sync.View)]
    public async Task<IActionResult> GetPolicy(CancellationToken ct)
        => Ok(await offlinePolicy.GetAsync(ct));

    [HttpPut("offline-policy")]
    [RequirePermission(Permissions.Offline.Configure)]
    public async Task<IActionResult> SetPolicy([FromBody] SetOfflinePolicyRequest r, CancellationToken ct)
        => FromResult(await offlinePolicy.SetAsync(r, ct));
}
