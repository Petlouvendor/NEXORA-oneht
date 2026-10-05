using Asp.Versioning;
using Microsoft.AspNetCore.Mvc;
using Nexora.Api.Security;
using Nexora.Application.Continuity;
using Nexora.Application.Security;
using Nexora.Contracts.Continuity;

namespace Nexora.Api.Controllers;

/// <summary>
/// Continuité, sauvegarde & reprise (Phase 36 §46). Registre applicatif : politiques, catalogue
/// des sauvegardes + vérification, tests & restaurations, état de reprise après sinistre, runbook
/// d'incident. Une restauration en production exige une validation par un tiers (§39).
/// </summary>
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/continuity")]
public sealed class ContinuityController(IContinuityService continuity) : ApiControllerBase
{
    // --- §4-7 Politiques (route canonique + alias §46) ---------------------

    [HttpGet("~/api/v{version:apiVersion}/backup-policies")]
    [RequirePermission(Permissions.Continuity.View)]
    public async Task<IActionResult> GetPolicy(CancellationToken ct) => Ok(await continuity.GetPolicyAsync(ct));

    [HttpPost("~/api/v{version:apiVersion}/backup-policies")]
    [RequirePermission(Permissions.Continuity.Manage)]
    public async Task<IActionResult> SetPolicy([FromBody] BackupPolicyDto policy, CancellationToken ct)
        => FromResult(await continuity.SetPolicyAsync(policy, ct));

    // --- §6, §21 Sauvegardes ---------------------------------------------

    [HttpGet("~/api/v{version:apiVersion}/backups")]
    [RequirePermission(Permissions.Continuity.View)]
    public async Task<IActionResult> Backups(CancellationToken ct) => Ok(await continuity.ListBackupsAsync(ct));

    [HttpPost("~/api/v{version:apiVersion}/backups")]
    [RequirePermission(Permissions.Continuity.Manage)]
    public async Task<IActionResult> RegisterBackup([FromBody] RegisterBackupRequest r, CancellationToken ct)
        => FromResult(await continuity.RegisterBackupAsync(r, ct));

    [HttpPost("~/api/v{version:apiVersion}/backups/{id:guid}/verify")]
    [RequirePermission(Permissions.Continuity.Manage)]
    public async Task<IActionResult> Verify(Guid id, CancellationToken ct)
        => FromResult(await continuity.VerifyBackupAsync(id, ct));

    [HttpPost("~/api/v{version:apiVersion}/backups/{id:guid}/restore-test")]
    [RequirePermission(Permissions.Continuity.Manage)]
    public async Task<IActionResult> RestoreTest(Guid id, [FromBody] RestoreTestRequest? r, CancellationToken ct)
        => FromResult(await continuity.RunRestoreTestAsync(r is null ? new RestoreTestRequest(id) : r with { BackupId = id }, ct));

    // --- §18-19 Restaurations ------------------------------------------

    [HttpGet("~/api/v{version:apiVersion}/restores")]
    [RequirePermission(Permissions.Continuity.View)]
    public async Task<IActionResult> Restores(CancellationToken ct) => Ok(await continuity.ListRestoreRunsAsync(ct));

    [HttpPost("~/api/v{version:apiVersion}/restores")]
    [RequirePermission(Permissions.Continuity.Restore)]
    public async Task<IActionResult> RequestRestore([FromBody] RestoreRequest r, CancellationToken ct)
        => FromResult(await continuity.RequestProductionRestoreAsync(r, ct));

    [HttpPost("~/api/v{version:apiVersion}/restores/{id:guid}/approve")]
    [RequireStepUp(StepUpPurposes.Deletion)]
    [RequirePermission(Permissions.Continuity.Restore)]
    public async Task<IActionResult> ApproveRestore(Guid id, CancellationToken ct)
        => FromResult(await continuity.ApproveProductionRestoreAsync(id, ct));

    // --- §36 État de reprise -----------------------------------------

    [HttpGet("~/api/v{version:apiVersion}/disaster-recovery/status")]
    [RequirePermission(Permissions.Continuity.View)]
    public async Task<IActionResult> DrStatus(CancellationToken ct) => Ok(await continuity.DisasterRecoveryStatusAsync(ct));

    // --- §43-45 Incidents ------------------------------------------

    [HttpGet("incidents")]
    [HttpGet("~/api/v{version:apiVersion}/incidents")]   // alias §56 (Phase 37)
    [HttpGet("~/api/v{version:apiVersion}/security/incidents")]   // alias Phase 47 §58 — même registre, domaine security/availability
    [RequirePermission(Permissions.Continuity.View)]
    public async Task<IActionResult> Incidents([FromQuery] string? status, [FromQuery] string? domain, CancellationToken ct)
        => Ok(await continuity.ListIncidentsAsync(status, domain, ct));

    [HttpPost("incidents")]
    [HttpPost("~/api/v{version:apiVersion}/security/incidents")]   // alias Phase 47 §58
    [RequirePermission(Permissions.Continuity.Manage)]
    public async Task<IActionResult> OpenIncident([FromBody] OpenIncidentRequest r, CancellationToken ct)
        => FromResult(await continuity.OpenIncidentAsync(r, ct));

    [HttpPost("incidents/{id:guid}/advance")]
    [RequirePermission(Permissions.Continuity.Manage)]
    public async Task<IActionResult> AdvanceIncident(Guid id, [FromBody] AdvanceIncidentRequest r, CancellationToken ct)
        => FromResult(await continuity.AdvanceIncidentAsync(id, r, ct));

    [HttpPost("incidents/{id:guid}/resolve")]
    [RequirePermission(Permissions.Continuity.Manage)]
    public async Task<IActionResult> ResolveIncident(Guid id, [FromBody] ResolveIncidentRequest r, CancellationToken ct)
        => FromResult(await continuity.ResolveIncidentAsync(id, r, ct));

    [HttpGet("tests")]
    [RequirePermission(Permissions.Continuity.View)]
    public async Task<IActionResult> Tests(CancellationToken ct) => Ok(await continuity.ListRestoreRunsAsync(ct));

    // --- §32, §55 Verrouillage d'urgence — même niveau de confiance qu'une restauration production ---

    [HttpPost("incidents/{id:guid}/lockdown")]
    [HttpPost("~/api/v{version:apiVersion}/resilience/incidents/{id:guid}/lockdown")]
    [RequirePermission(Permissions.Continuity.Restore)]
    public async Task<IActionResult> LockDown(Guid id, CancellationToken ct) => FromResult(await continuity.LockDownAsync(id, ct));

    // --- §44-45 Exercices DR ---------------------------------------------------------------------

    [HttpGet("~/api/v{version:apiVersion}/resilience/dr-tests")]
    [RequirePermission(Permissions.Continuity.View)]
    public async Task<IActionResult> DrTests([FromQuery] string? status, CancellationToken ct)
        => Ok(await continuity.ListDrTestsAsync(status, ct));

    [HttpPost("~/api/v{version:apiVersion}/resilience/dr-tests")]
    [RequirePermission(Permissions.Continuity.Manage)]
    public async Task<IActionResult> ScheduleDrTest([FromBody] ScheduleDrTestRequest r, CancellationToken ct)
        => FromResult(await continuity.ScheduleDrTestAsync(r, ct));

    [HttpPost("~/api/v{version:apiVersion}/resilience/dr-tests/{id:guid}/record")]
    [RequirePermission(Permissions.Continuity.Manage)]
    public async Task<IActionResult> RecordDrTestResult(Guid id, [FromBody] RecordDrTestResultRequest r, CancellationToken ct)
        => FromResult(await continuity.RecordDrTestResultAsync(id, r, ct));

    // --- §37 Runbooks — catalogue statique, lecture seule -----------------------------------------

    [HttpGet("~/api/v{version:apiVersion}/resilience/runbooks")]
    [RequirePermission(Permissions.Continuity.View)]
    public IActionResult Runbooks() => Ok(ContinuityRunbookCatalog.All);

    // --- §7, §39 Page de statut — composition des signaux existants -------------------------------

    [HttpGet("~/api/v{version:apiVersion}/resilience/status-page")]
    [RequirePermission(Permissions.Continuity.View)]
    public async Task<IActionResult> StatusPage(CancellationToken ct) => Ok(await continuity.StatusPageAsync(ct));

    // --- §56 Alias canoniques /resilience/* sur les mêmes services, jamais une seconde logique -----

    [HttpGet("~/api/v{version:apiVersion}/resilience/status")]
    [RequirePermission(Permissions.Continuity.View)]
    public async Task<IActionResult> ResilienceStatus(CancellationToken ct) => Ok(await continuity.DisasterRecoveryStatusAsync(ct));

    [HttpGet("~/api/v{version:apiVersion}/resilience/backups")]
    [RequirePermission(Permissions.Continuity.View)]
    public async Task<IActionResult> ResilienceBackups(CancellationToken ct) => Ok(await continuity.ListBackupsAsync(ct));

    [HttpPost("~/api/v{version:apiVersion}/resilience/backups/{id:guid}/verify")]
    [RequirePermission(Permissions.Continuity.Manage)]
    public async Task<IActionResult> ResilienceVerify(Guid id, CancellationToken ct) => FromResult(await continuity.VerifyBackupAsync(id, ct));

    [HttpPost("~/api/v{version:apiVersion}/resilience/backups/{id:guid}/restore-test")]
    [RequirePermission(Permissions.Continuity.Manage)]
    public async Task<IActionResult> ResilienceRestoreTest(Guid id, [FromBody] RestoreTestRequest? r, CancellationToken ct)
        => FromResult(await continuity.RunRestoreTestAsync(r is null ? new RestoreTestRequest(id) : r with { BackupId = id }, ct));

    [HttpGet("~/api/v{version:apiVersion}/resilience/incidents")]
    [RequirePermission(Permissions.Continuity.View)]
    public async Task<IActionResult> ResilienceIncidents([FromQuery] string? status, [FromQuery] string? domain, CancellationToken ct)
        => Ok(await continuity.ListIncidentsAsync(status, domain, ct));

    [HttpPost("~/api/v{version:apiVersion}/resilience/incidents")]
    [RequirePermission(Permissions.Continuity.Manage)]
    public async Task<IActionResult> OpenResilienceIncident([FromBody] OpenIncidentRequest r, CancellationToken ct)
        => FromResult(await continuity.OpenIncidentAsync(r, ct));
}
