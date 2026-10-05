using Asp.Versioning;
using Microsoft.AspNetCore.Mvc;
using Nexora.Api.Security;
using Nexora.Application.Group;
using Nexora.Application.Security;
using Nexora.Contracts.Group;

namespace Nexora.Api.Controllers;

/// <summary>
/// Gouvernance de groupe (Phase 9 §12, §14-15, §23-25, §28-29). Toutes les routes exigent
/// l'en-tête <c>X-Group-Scope: true</c> et un périmètre groupe habilité ; le serveur
/// re-vérifie systématiquement les autorisations (§21-22).
/// </summary>
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/groups/{groupId:guid}")]
public sealed class GroupController(
    IGroupLedgerService ledger,
    IIntercompanyService intercompany,
    IGroupControlCenter controlCenter,
    IGroupTaskService tasks,
    ICompanyProvisioningService provisioning) : ApiControllerBase
{
    // --- Plan comptable groupe & mappings (§12) ----------------------

    [HttpGet("accounts")]
    [RequirePermission(Permissions.Group.Mappings)]
    public async Task<IActionResult> Accounts(Guid groupId, CancellationToken ct)
        => Ok(await ledger.ListAccountsAsync(groupId, ct));

    [HttpPost("accounts")]
    [RequirePermission(Permissions.Group.Mappings)]
    public async Task<IActionResult> UpsertAccount(Guid groupId, [FromBody] UpsertGroupAccountRequest r, CancellationToken ct)
        => FromResult(await ledger.UpsertAccountAsync(groupId, r, ct));

    [HttpPut("accounts/mappings")]
    [RequirePermission(Permissions.Group.Mappings)]
    public async Task<IActionResult> SetMapping(Guid groupId, [FromBody] SetMappingRequest r, CancellationToken ct)
        => FromResult(await ledger.SetMappingAsync(groupId, r, ct));

    [HttpGet("accounts/mappings")]
    [RequirePermission(Permissions.Group.Mappings)]
    public async Task<IActionResult> Mappings(Guid groupId, [FromQuery] Guid companyId, CancellationToken ct)
        => FromResult(await ledger.ListMappingsAsync(groupId, companyId, ct));

    [HttpGet("options")]
    [RequirePermission(Permissions.Group.View)]
    public async Task<IActionResult> Options(Guid groupId, CancellationToken ct)
        => Ok(await ledger.OptionsAsync(groupId, ct));

    [HttpGet("accounts/mapping-coverage")]
    [RequirePermission(Permissions.Group.Mappings)]
    public async Task<IActionResult> MappingCoverage(Guid groupId, CancellationToken ct)
        => Ok(await ledger.MappingCoverageAsync(groupId, ct));

    [HttpGet("policy")]
    [RequirePermission(Permissions.Group.View)]
    public async Task<IActionResult> GetPolicy(Guid groupId, CancellationToken ct)
        => Ok(await ledger.GetPolicyAsync(groupId, ct));

    [HttpPut("policy")]
    [RequirePermission(Permissions.Group.Manage)]
    public async Task<IActionResult> SetPolicy(Guid groupId, [FromBody] SetGroupPolicyRequest r, CancellationToken ct)
        => FromResult(await ledger.SetPolicyAsync(groupId, r, ct));

    // --- Intercompany (§14-15) --------------------------------------

    [HttpGet("intercompany")]
    [RequirePermission(Permissions.Group.Intercompany)]
    public async Task<IActionResult> Intercompany(Guid groupId, CancellationToken ct)
        => Ok(await intercompany.ListAsync(groupId, ct));

    [HttpPost("intercompany")]
    [RequirePermission(Permissions.Group.Intercompany)]
    public async Task<IActionResult> RecordIntercompany(Guid groupId, [FromBody] RecordIntercompanyRequest r, CancellationToken ct)
        => FromResult(await intercompany.RecordAsync(groupId, r, ct));

    [HttpPost("intercompany/{transactionId:guid}/confirm")]
    [RequirePermission(Permissions.Group.Intercompany)]
    public async Task<IActionResult> ConfirmIntercompany(Guid groupId, Guid transactionId, CancellationToken ct)
        => FromResult(await intercompany.ConfirmAsync(groupId, transactionId, ct));

    [HttpPost("intercompany/reconcile")]
    [RequirePermission(Permissions.Group.Intercompany)]
    public async Task<IActionResult> Reconcile(Guid groupId, [FromBody] ReconcileIntercompanyRequest r, CancellationToken ct)
        => FromResult(await intercompany.ReconcileAsync(groupId, r, ct));

    [HttpGet("intercompany/unreconciled")]
    [RequirePermission(Permissions.Group.Intercompany)]
    public async Task<IActionResult> Unreconciled(Guid groupId, CancellationToken ct)
        => Ok(await intercompany.UnreconciledAsync(groupId, ct));

    // --- Centre de contrôle (§24) ----------------------------------

    [HttpGet("control-center")]
    [RequirePermission(Permissions.Group.View)]
    public async Task<IActionResult> ControlCenter(Guid groupId, CancellationToken ct)
        => Ok(await controlCenter.SummaryAsync(groupId, ct));

    // --- Tâches groupe (§25) --------------------------------------

    [HttpGet("tasks")]
    [RequirePermission(Permissions.Group.Tasks)]
    public async Task<IActionResult> Tasks(Guid groupId, CancellationToken ct)
        => Ok(await tasks.ListAsync(groupId, ct));

    [HttpPost("tasks")]
    [RequirePermission(Permissions.Group.Tasks)]
    public async Task<IActionResult> CreateTask(Guid groupId, [FromBody] CreateGroupTaskRequest r, CancellationToken ct)
        => FromResult(await tasks.CreateAsync(groupId, r, ct));

    [HttpPut("tasks/{taskId:guid}")]
    [RequirePermission(Permissions.Group.Tasks)]
    public async Task<IActionResult> UpdateTask(Guid groupId, Guid taskId, [FromBody] UpdateGroupTaskRequest r, CancellationToken ct)
        => FromResult(await tasks.UpdateAsync(groupId, taskId, r, ct));

    // --- Entreprises (§28-29) ------------------------------------

    [HttpPost("companies")]
    [RequirePermission(Permissions.Group.Companies)]
    public async Task<IActionResult> ProvisionCompany(Guid groupId, [FromBody] ProvisionCompanyRequest r, CancellationToken ct)
        => FromResult(await provisioning.ProvisionAsync(r with { GroupId = groupId }, ct));

    [HttpPost("companies/{companyId:guid}/archive")]
    [RequirePermission(Permissions.Group.Companies)]
    public async Task<IActionResult> ArchiveCompany(Guid groupId, Guid companyId, [FromBody] ArchiveCompanyRequest r, CancellationToken ct)
        => FromResult(await provisioning.ArchiveAsync(groupId, companyId, r, ct));
}
