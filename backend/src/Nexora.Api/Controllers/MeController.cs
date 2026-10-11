using Asp.Versioning;
using Microsoft.AspNetCore.Mvc;
using Nexora.Application.Abstractions;
using Nexora.Application.Identity;
using Nexora.Application.Preferences;
using Nexora.Application.Security;
using Nexora.Contracts.Identity;

namespace Nexora.Api.Controllers;

[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/me")]
public sealed class MeController(
    ITenantContext tenant,
    IPermissionService permissions,
    IScopeQueryService scopes,
    IUserPreferenceService preferences,
    ICompanyBrandingService branding,
    ISessionSecurityService sessions,
    IAccountService accounts,
    ISecurityEventLog securityEvents) : ApiControllerBase
{
    /// <summary>Contexte de l'utilisateur courant (§12) : sécurité + préférences UX + personnalisation entreprise.</summary>
    [HttpGet]
    public async Task<IActionResult> Get(CancellationToken ct)
    {
        if (tenant.UserId is not { } userId)
            return Unauthorized();

        var profile = await permissions.LoadProfileAsync(userId, ct);
        var prefs = await preferences.GetAsync(ct);
        var brand = tenant.ActiveCompanyId is not null ? await branding.GetAsync(ct) : null;

        return Ok(new
        {
            userId,
            activeCompanyId = tenant.ActiveCompanyId,
            companyIds = tenant.CompanyIds,
            groupIds = tenant.GroupIds,
            permissions = profile.PermissionsFor(tenant.ActiveCompanyId),
            memberships = profile.Memberships,
            preferences = prefs,
            branding = brand,
        });
    }

    /// <summary>Permissions effectives de l'utilisateur pour l'entreprise active (§39).</summary>
    [HttpGet("permissions")]
    public async Task<IActionResult> Permissions(CancellationToken ct)
    {
        if (tenant.UserId is null) return Unauthorized();
        return Ok(await scopes.MyPermissionsAsync(ct));
    }

    /// <summary>
    /// Déconnexion (recette Phase 55 AUTH-004) : révoque la session portée par CE jeton — le même
    /// jeton est refusé dès l'appel suivant. Les autres appareils restent connectés (pour tout
    /// couper : <c>POST /security/sessions/revoke-all</c>). Idempotent : 204 même si la session
    /// était déjà close ou antérieure au lien jeton ↔ session.
    /// </summary>
    [HttpPost("logout")]
    public async Task<IActionResult> Logout(CancellationToken ct)
    {
        if (tenant.UserId is not { } userId) return Unauthorized();
        // La session entière tombe : jeton d'accès courant et jetons de rafraîchissement (finalisation phase 17).
        if (Guid.TryParse(User.FindFirst("sid")?.Value, out var sessionId))
            await sessions.RevokeSessionAsync(sessionId, "déconnexion", ct);
        else if (Guid.TryParse(User.FindFirst("jti")?.Value, out var tokenId))
            await sessions.RevokeTokenAsync(tokenId, "déconnexion", ct);
        AuthController.ClearRefreshCookie(Response);
        await securityEvents.RecordAsync("logout", userId, tenant.ActiveCompanyId,
            ipAddress: HttpContext.Connection.RemoteIpAddress?.ToString(), ct: ct);
        return NoContent();
    }

    /// <summary>Changement de mot de passe (AUTH-007). Exige le mot de passe actuel ; toutes les
    /// sessions tombent ensuite, y compris celle-ci — le client doit se reconnecter.</summary>
    [HttpPost("password")]
    public async Task<IActionResult> ChangePassword([FromBody] ChangePasswordRequest request, CancellationToken ct)
    {
        if (tenant.UserId is null) return Unauthorized();
        return FromResult(await accounts.ChangePasswordAsync(request, HttpContext.Connection.RemoteIpAddress?.ToString(), ct));
    }

    /// <summary>Périmètre accessible — alimente le sélecteur d'entreprise (§14-16, §39).</summary>
    [HttpGet("scopes")]
    public async Task<IActionResult> Scopes(CancellationToken ct)
    {
        if (tenant.UserId is null) return Unauthorized();
        return Ok(await scopes.MyScopesAsync(ct));
    }
}
