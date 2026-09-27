using Asp.Versioning;
using Microsoft.AspNetCore.Mvc;
using Nexora.Api.Security;
using Nexora.Application.Abstractions;
using Nexora.Application.Integrations;
using Nexora.Application.Marketplace;
using Nexora.Application.Security;
using Nexora.Contracts.Integrations;
using Nexora.Contracts.Marketplace;

namespace Nexora.Api.Controllers;

/// <summary>
/// Marketplace & écosystème partenaires (Phase 46 §41-48, §58). Le catalogue est curé par NEXORA
/// (<c>platform.manage_marketplace</c>) — jamais un accès direct d'un tiers à sa propre fiche.
/// Une entreprise installe une app avec un périmètre de scopes qu'elle accorde explicitement
/// (§44, §56), puis peut lui émettre une clé d'API ordinaire via <c>/developer/keys</c> — même
/// moteur que la Phase 35, jamais un second système d'authentification (§8, §46).
/// </summary>
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}")]
public sealed class MarketplaceController(
    IMarketplaceCatalogService catalog, IAppInstallationService installations,
    IApiKeyService keys, IDeveloperUsageQuery usage, IOutboundWebhookService outbound,
    IPermissionService permissions, ITenantContext tenant) : ApiControllerBase
{
    private async Task<bool> IsMarketplaceStaffAsync(CancellationToken ct)
    {
        if (tenant.UserId is not { } uid) return false;
        var profile = await permissions.LoadProfileAsync(uid, ct);
        return profile.HasPermission(Permissions.Platform.ManageMarketplace, tenant.ActiveCompanyId);
    }

    // --- §41-42, §45 Catalogue (curation NEXORA) ------------------------------

    [HttpGet("apps")]
    [RequirePermission(Permissions.Integrations.View)]
    public async Task<IActionResult> ListApps(
        [FromQuery] string? category, [FromQuery] string? status, [FromQuery] string? cursor, [FromQuery] int limit, CancellationToken ct)
    {
        var staff = await IsMarketplaceStaffAsync(ct);
        return Ok(await catalog.ListAsync(category, status, cursor, limit, includeAllStatuses: staff, ct));
    }

    [HttpGet("apps/{id:guid}")]
    [RequirePermission(Permissions.Integrations.View)]
    public async Task<IActionResult> GetApp(Guid id, CancellationToken ct)
    {
        var staff = await IsMarketplaceStaffAsync(ct);
        return FromResult(await catalog.GetAsync(id, includeAllStatuses: staff, ct));
    }

    [HttpPost("apps")]
    [RequirePermission(Permissions.Platform.ManageMarketplace)]
    public async Task<IActionResult> SubmitApp([FromBody] SubmitAppRequest request, CancellationToken ct)
        => FromResult(await catalog.SubmitAsync(request, ct));

    [HttpPost("apps/{id:guid}/review")]
    [RequirePermission(Permissions.Platform.ManageMarketplace)]
    public async Task<IActionResult> ReviewApp(Guid id, [FromBody] ReviewAppRequest request, CancellationToken ct)
        => FromResult(await catalog.ReviewAsync(id, request, ct));

    // --- §43-44, §56 Installation par entreprise ------------------------------

    [HttpGet("apps/installed")]
    [RequirePermission(Permissions.Integrations.View)]
    public async Task<IActionResult> Installed(CancellationToken ct) => Ok(await installations.ListInstalledAsync(ct));

    [HttpPost("apps/{id:guid}/install")]
    [RequirePermission(Permissions.Integrations.ManageApps)]
    public async Task<IActionResult> Install(Guid id, [FromBody] InstallAppRequest request, CancellationToken ct)
        => FromResult(await installations.InstallAsync(id, request, ct));

    [HttpPost("apps/{id:guid}/uninstall")]
    [RequirePermission(Permissions.Integrations.ManageApps)]
    public async Task<IActionResult> Uninstall(Guid id, CancellationToken ct)
        => FromResult(await installations.UninstallAsync(id, ct));

    [HttpPost("apps/{id:guid}/disable")]
    [RequirePermission(Permissions.Integrations.ManageApps)]
    public async Task<IActionResult> Disable(Guid id, CancellationToken ct)
        => FromResult(await installations.DisableAsync(id, ct));

    // --- §47-48, §8 Portail développeur (alias sur les clés d'API Phase 35) ---

    [HttpGet("developer/keys")]
    [RequirePermission(Permissions.Integrations.ManageApiKeys)]
    public async Task<IActionResult> DeveloperKeys(CancellationToken ct) => Ok(await keys.ListAsync(ct));

    [HttpPost("developer/keys")]
    [RequirePermission(Permissions.Integrations.ManageApiKeys)]
    public async Task<IActionResult> CreateDeveloperKey([FromBody] CreateApiKeyRequest request, CancellationToken ct)
        => FromResult(await keys.CreateAsync(request, ct));

    [HttpPost("developer/keys/{id:guid}/revoke")]
    [RequirePermission(Permissions.Integrations.ManageApiKeys)]
    public async Task<IActionResult> RevokeDeveloperKey(Guid id, CancellationToken ct) => FromResult(await keys.RevokeAsync(id, ct));

    [HttpGet("developer/usage")]
    [RequirePermission(Permissions.Integrations.ManageApiKeys)]
    public async Task<IActionResult> DeveloperUsage([FromQuery] int days, CancellationToken ct) => Ok(await usage.UsageAsync(days, ct));

    // --- §13, §58 Webhooks sortants (alias sur le moteur Phase 12) ------------

    [HttpGet("webhooks")]
    [RequirePermission(Permissions.Integrations.Webhooks)]
    public async Task<IActionResult> Webhooks(CancellationToken ct) => Ok(await outbound.ListAsync(ct));

    [HttpPost("webhooks")]
    [RequirePermission(Permissions.Integrations.Webhooks)]
    public async Task<IActionResult> RegisterWebhook([FromBody] RegisterOutboundWebhookRequest request, CancellationToken ct)
        => FromResult(await outbound.RegisterAsync(request, ct));
}
