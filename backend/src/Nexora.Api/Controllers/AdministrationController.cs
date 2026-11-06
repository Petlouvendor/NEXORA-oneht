using Asp.Versioning;
using Microsoft.AspNetCore.Mvc;
using Nexora.Api.Security;
using Nexora.Application.Administration;
using Nexora.Application.Security;
using Nexora.Contracts.Administration;

namespace Nexora.Api.Controllers;

/// <summary>
/// Centre d'administration (Phase 14). Configuration sans code, avec périmètre et audit :
/// paramètres résolus par hiérarchie, rôles personnalisés, workflows d'approbation testables,
/// automatisations planifiées, versionnement des configs sensibles, centre de tâches.
/// </summary>
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/admin")]
public sealed class AdministrationController(
    ISettingsService settings,
    ICustomRoleService roles,
    IWorkflowService workflows,
    IAutomationService automations,
    IConfigVersionService versions,
    IAdminTaskService tasks) : ApiControllerBase
{
    // --- Paramètres (§2-4) ---------------------------------------

    [HttpGet("settings/definitions")]
    [RequirePermission(Permissions.Admin.Settings)]
    public async Task<IActionResult> Definitions([FromQuery] string? category, CancellationToken ct)
        => Ok(await settings.DefinitionsAsync(category, ct));

    [HttpGet("settings")]
    [RequirePermission(Permissions.Admin.Settings)]
    public async Task<IActionResult> EffectiveSettings([FromQuery] Guid? establishmentId, CancellationToken ct)
        => Ok(await settings.EffectiveAsync(establishmentId, ct));

    [HttpPut("settings")]
    [RequirePermission(Permissions.Admin.Settings)]
    public async Task<IActionResult> SetSetting([FromBody] SetSettingRequest r, CancellationToken ct)
        => FromResult(await settings.SetAsync(r, ct));

    // --- Rôles personnalisés (§10-11) ---------------------------

    [HttpGet("roles")]
    [RequirePermission(Permissions.Admin.Roles)]
    public async Task<IActionResult> Roles(CancellationToken ct) => Ok(await roles.ListAsync(ct));

    [HttpPost("roles")]
    [RequireStepUp(StepUpPurposes.PermissionChange)]
    [RequirePermission(Permissions.Admin.Roles)]
    public async Task<IActionResult> CreateRole([FromBody] CreateRoleRequest r, CancellationToken ct)
        => FromResult(await roles.CreateAsync(r, ct));

    [HttpGet("permissions")]
    [RequirePermission(Permissions.Admin.Roles)]
    public async Task<IActionResult> PermissionCatalog(CancellationToken ct) => Ok(await roles.PermissionCatalogAsync(ct));

    [HttpPut("roles/{id:guid}")]
    [RequireStepUp(StepUpPurposes.PermissionChange)]
    [RequirePermission(Permissions.Admin.Roles)]
    public async Task<IActionResult> UpdateRole(Guid id, [FromBody] UpdateRoleRequest r, CancellationToken ct)
        => FromResult(await roles.UpdateAsync(id, r, ct));

    [HttpPost("roles/{id:guid}/enable")]
    [RequireStepUp(StepUpPurposes.PermissionChange)]
    [RequirePermission(Permissions.Admin.Roles)]
    public async Task<IActionResult> EnableRole(Guid id, CancellationToken ct) => FromResult(await roles.EnableAsync(id, ct));

    [HttpPut("roles/{id:guid}/permissions")]
    [RequireStepUp(StepUpPurposes.PermissionChange)]
    [RequirePermission(Permissions.Admin.Roles)]
    public async Task<IActionResult> SetRolePermissions(Guid id, [FromBody] SetRolePermissionsRequest r, CancellationToken ct)
        => FromResult(await roles.SetPermissionsAsync(id, r, ct));

    [HttpGet("roles/{id:guid}/preview")]
    [RequirePermission(Permissions.Admin.Roles)]
    public async Task<IActionResult> PreviewRole(Guid id, CancellationToken ct) => FromResult(await roles.PreviewAsync(id, ct));

    [HttpPost("roles/{id:guid}/disable")]
    [RequireStepUp(StepUpPurposes.PermissionChange)]
    [RequirePermission(Permissions.Admin.Roles)]
    public async Task<IActionResult> DisableRole(Guid id, [FromQuery] bool force, CancellationToken ct)
        => FromResult(await roles.DisableAsync(id, force, ct));

    // --- Workflows (§14-16) -----------------------------------

    [HttpGet("workflows")]
    [RequirePermission(Permissions.Admin.Workflows)]
    public async Task<IActionResult> Workflows(CancellationToken ct) => Ok(await workflows.ListAsync(ct));

    [HttpGet("workflows/catalog")]
    [RequirePermission(Permissions.Admin.Workflows)]
    public async Task<IActionResult> WorkflowCatalog(CancellationToken ct) => Ok(await workflows.CatalogAsync(ct));

    [HttpPost("workflows")]
    [RequireStepUp(StepUpPurposes.PermissionChange)]
    [RequirePermission(Permissions.Admin.Workflows)]
    public async Task<IActionResult> UpsertWorkflow([FromBody] UpsertWorkflowRequest r, CancellationToken ct)
        => FromResult(await workflows.UpsertAsync(r, ct));

    [HttpPost("workflows/{id:guid}/test")]
    [RequirePermission(Permissions.Admin.Workflows)]
    public async Task<IActionResult> TestWorkflow(Guid id, [FromBody] WorkflowTestRequest r, CancellationToken ct)
        => FromResult(await workflows.TestAsync(id, r, ct));

    [HttpPost("workflows/{id:guid}/activate")]
    [RequireStepUp(StepUpPurposes.PermissionChange)]
    [RequirePermission(Permissions.Admin.Workflows)]
    public async Task<IActionResult> ActivateWorkflow(Guid id, CancellationToken ct)
        => FromResult(await workflows.ActivateAsync(id, ct));

    [HttpPost("workflows/{id:guid}/status")]
    [RequireStepUp(StepUpPurposes.PermissionChange)]
    [RequirePermission(Permissions.Admin.Workflows)]
    public async Task<IActionResult> WorkflowStatus(Guid id, [FromBody] Nexora.Contracts.Integrations.SetStatusRequest r, CancellationToken ct)
        => FromResult(await workflows.SetStatusAsync(id, r.Status, ct));

    [HttpPost("workflows/evaluate")]
    [RequirePermission(Permissions.Admin.Workflows)]
    public async Task<IActionResult> EvaluateWorkflow([FromBody] WorkflowTriggerRequest r, CancellationToken ct)
        => FromResult(await workflows.EvaluateAsync(r, ct));

    // --- Automatisations (§17-19) ----------------------------

    [HttpGet("automations")]
    [RequirePermission(Permissions.Admin.Automations)]
    public async Task<IActionResult> Automations(CancellationToken ct) => Ok(await automations.ListAsync(ct));

    [HttpPost("automations")]
    [RequireStepUp(StepUpPurposes.PermissionChange)]
    [RequirePermission(Permissions.Admin.Automations)]
    public async Task<IActionResult> UpsertAutomation([FromBody] UpsertAutomationRequest r, CancellationToken ct)
        => FromResult(await automations.UpsertAsync(r, ct));

    [HttpPost("automations/{id:guid}/status")]
    [RequireStepUp(StepUpPurposes.PermissionChange)]
    [RequirePermission(Permissions.Admin.Automations)]
    public async Task<IActionResult> AutomationStatus(Guid id, [FromBody] Nexora.Contracts.Integrations.SetStatusRequest r, CancellationToken ct)
        => FromResult(await automations.SetStatusAsync(id, r.Status, ct));

    [HttpPost("automations/{id:guid}/run")]
    [RequirePermission(Permissions.Admin.Automations)]
    public async Task<IActionResult> RunAutomation(Guid id, [FromQuery] bool preview, CancellationToken ct)
        => FromResult(await automations.RunNowAsync(id, preview, ct));

    [HttpPost("automations/run-due")]
    [RequirePermission(Permissions.Admin.Automations)]
    public async Task<IActionResult> RunDue(CancellationToken ct) => Ok(new { ran = await automations.RunDueAsync(ct) });

    [HttpGet("automations/{id:guid}/runs")]
    [RequirePermission(Permissions.Admin.Automations)]
    public async Task<IActionResult> AutomationRuns(Guid id, CancellationToken ct) => Ok(await automations.RunsAsync(id, ct));

    [HttpGet("dunning-policy")]
    [RequirePermission(Permissions.Admin.Automations)]
    public async Task<IActionResult> GetDunning(CancellationToken ct) => Ok(await automations.GetDunningPolicyAsync(ct));

    [HttpPut("dunning-policy")]
    [RequirePermission(Permissions.Admin.Automations)]
    public async Task<IActionResult> SetDunning([FromBody] DunningPolicyDto r, CancellationToken ct)
        => FromResult(await automations.SetDunningPolicyAsync(r, ct));

    // --- Versionnement de configuration (§39) ---------------

    [HttpGet("config-versions")]
    [RequirePermission(Permissions.Admin.ConfigVersions)]
    public async Task<IActionResult> ConfigVersions([FromQuery] string domain, CancellationToken ct)
        => Ok(await versions.ListAsync(domain, ct));

    [HttpPost("config-versions/snapshot")]
    [RequirePermission(Permissions.Admin.ConfigVersions)]
    public async Task<IActionResult> Snapshot([FromQuery] string domain, [FromQuery] string? note, CancellationToken ct)
        => FromResult(await versions.SnapshotAsync(domain, note, ct));

    [HttpPost("config-versions/{id:guid}/activate")]
    [RequirePermission(Permissions.Admin.ConfigVersions)]
    public async Task<IActionResult> ActivateVersion(Guid id, [FromBody] ActivateVersionRequest r, CancellationToken ct)
        => FromResult(await versions.ActivateAsync(id, r, ct));

    [HttpGet("config-versions/active-at")]
    [RequirePermission(Permissions.Admin.ConfigVersions)]
    public async Task<IActionResult> ActiveAt([FromQuery] string domain, [FromQuery] DateTimeOffset at, CancellationToken ct)
    {
        var v = await versions.ActiveAtAsync(domain, at, ct);
        return v is null ? NotFound(new { message = "Aucune version active à cette date." }) : Ok(v);
    }

    // --- Centre de tâches administratives (§44) --------------

    [HttpGet("tasks")]
    [RequirePermission(Permissions.Admin.Settings)]
    public async Task<IActionResult> Tasks(CancellationToken ct) => Ok(await tasks.ListAsync(ct));
}
