using Asp.Versioning;
using Microsoft.AspNetCore.Mvc;
using Nexora.Api.Security;
using Nexora.Application.Configuration;
using Nexora.Application.Security;
using Nexora.Contracts.Configuration;

namespace Nexora.Api.Controllers;

/// <summary>
/// Profils sectoriels (Phase 32 §6, §49, §54). Bundles réutilisables de configuration
/// appliqués à une entreprise sans changer de version logicielle. Chaque application prend
/// un snapshot versionné (§44) et passe les garde-fous (§52).
/// </summary>
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/sector-profiles")]
public sealed class SectorProfilesController(ISectorProfileService profiles) : ApiControllerBase
{
    [HttpGet]
    [RequirePermission(Permissions.Sectors.Configure)]
    public async Task<IActionResult> List(CancellationToken ct)
        => Ok(await profiles.ListAsync(ct));

    [HttpGet("{key}")]
    [RequirePermission(Permissions.Sectors.Configure)]
    public async Task<IActionResult> Get(string key, CancellationToken ct)
        => FromResult(await profiles.GetAsync(key, ct));

    [HttpPost("{key}/preview")]
    [RequirePermission(Permissions.Sectors.Configure)]
    public async Task<IActionResult> Preview(string key, CancellationToken ct)
        => FromResult(await profiles.PreviewAsync(key, ct));

    [HttpPost("{key}/apply")]
    [RequirePermission(Permissions.Config.ApplyProfile)]
    public async Task<IActionResult> Apply(string key, [FromBody] ApplyProfileRequest? r, CancellationToken ct)
        => FromResult(await profiles.ApplyAsync(key, r ?? new ApplyProfileRequest(), ct));
}
