using Asp.Versioning;
using Microsoft.AspNetCore.Mvc;
using Nexora.Api.Security;
using Nexora.Application.Administration;
using Nexora.Application.Configuration;
using Nexora.Application.Security;
using Nexora.Contracts.Administration;
using Nexora.Contracts.Configuration;

namespace Nexora.Api.Controllers;

/// <summary>
/// Configuration d'entreprise (Phase 32 §44-48, §52, §54) : garde-fous, export / import / clonage,
/// versions & rollback. Les paramètres, rôles, workflows, champs restent gérés via
/// <c>/admin/*</c> et <c>/sectors/*</c> (Phases 8 / 14).
/// </summary>
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/configurations")]
public sealed class ConfigurationController(
    IConfigTransferService transfer,
    IConfigGuardrails guardrails,
    IConfigVersionService versions) : ApiControllerBase
{
    // --- §52 garde-fous -------------------------------------------

    [HttpGet("validate")]
    [RequirePermission(Permissions.Config.Validate)]
    public async Task<IActionResult> Validate(CancellationToken ct)
        => Ok(await guardrails.ValidateAsync(ct));

    // --- §46-48 export / import / clonage ------------------------

    [HttpGet("export")]
    [RequirePermission(Permissions.Config.Transfer)]
    public async Task<IActionResult> Export(CancellationToken ct)
        => Ok(await transfer.ExportAsync(ct));

    [HttpPost("import")]
    [RequirePermission(Permissions.Config.Transfer)]
    public async Task<IActionResult> Import([FromBody] ImportConfigRequest r, CancellationToken ct)
        => FromResult(await transfer.ImportAsync(r, ct));

    [HttpPost("clone")]
    [RequirePermission(Permissions.Config.Transfer)]
    public async Task<IActionResult> Clone([FromBody] CloneConfigRequest r, CancellationToken ct)
        => FromResult(await transfer.CloneAsync(r, ct));

    [HttpGet("history")]
    [RequirePermission(Permissions.Config.Transfer)]
    public async Task<IActionResult> History(CancellationToken ct)
        => Ok(await transfer.HistoryAsync(ct));

    // --- §44-45 versions -------------------------------------
    // Audit 2026-10 éd. 2, A-037 : la route « rollback » ne changeait que le statut des versions — rien n'était restauré.
    // Elle est retirée ; publier une version la marque comme référence datée (historique), sans appliquer l'instantané.

    [HttpGet("{domain}/versions")]
    [RequirePermission(Permissions.Admin.ConfigVersions)]
    public async Task<IActionResult> Versions(string domain, CancellationToken ct)
        => Ok(await versions.ListAsync(domain, ct));

    [HttpPost("{versionId:guid}/publish")]
    [RequirePermission(Permissions.Admin.ConfigVersions)]
    public async Task<IActionResult> Publish(Guid versionId, [FromBody] ActivateVersionRequest? r, CancellationToken ct)
        => FromResult(await versions.ActivateAsync(versionId, r ?? new ActivateVersionRequest(null, null), ct));

}
