using Asp.Versioning;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.IdentityModel.JsonWebTokens;
using Nexora.Application.Identity;
using Nexora.Application.Security;
using Nexora.Application.UseCases.Auth;
using Nexora.Contracts.Auth;
using Nexora.Contracts.Identity;

namespace Nexora.Api.Controllers;

/// <summary>
/// Authentification anonyme. La politique de débit « auth » (10 requêtes / minute / IP) était
/// définie depuis la Phase 3 mais n'était attachée à aucun endpoint — constat de la recette
/// Phase 55 (SEC-011) : elle couvre désormais tout ce contrôleur, en plus du verrouillage par
/// compte (Phase 33).
/// </summary>
[ApiVersion("1.0")]
[AllowAnonymous]
[EnableRateLimiting("auth")]
public sealed class AuthController(
    LoginHandler login,
    MfaVerifyHandler mfaVerify,
    ISessionSecurityService sessions,
    IAccountService accounts) : ApiControllerBase
{
    /// <summary>Authentifie un utilisateur et émet un jeton d'accès (Phase 3 §9). Si la MFA est
    /// active, renvoie <c>mfaRequired</c> + un jeton de défi à confirmer via <c>/auth/mfa/verify</c> (§8).</summary>
    [HttpPost("login")]
    [ProducesResponseType<AuthTokenResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> Login([FromBody] LoginRequest request, CancellationToken ct)
    {
        var result = await login.HandleAsync(request, ClientIp(), ct);
        if (result.IsSuccess && result.Value is { MfaRequired: false })
            await RecordSessionAsync(result.Value!.User.Id, request.CompanyId, result.Value.AccessToken, ct);
        return FromResult(result);
    }

    /// <summary>Deuxième facteur : valide le code TOTP / de récupération lié au défi et émet le jeton (§8).</summary>
    [HttpPost("mfa/verify")]
    [ProducesResponseType<AuthTokenResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> VerifyMfa([FromBody] MfaVerifyLoginRequest request, CancellationToken ct)
    {
        var result = await mfaVerify.HandleAsync(request, ClientIp(), ct);
        if (result.IsSuccess)
            await RecordSessionAsync(result.Value!.User.Id, null, result.Value.AccessToken, ct);
        return FromResult(result);
    }

    /// <summary>Mot de passe oublié (recette AUTH-008) : toujours 202, que le compte existe ou non.
    /// Le code part par email, jamais dans la réponse.</summary>
    [HttpPost("password/forgot")]
    [ProducesResponseType(StatusCodes.Status202Accepted)]
    public async Task<IActionResult> ForgotPassword([FromBody] ForgotPasswordRequest request, CancellationToken ct)
    {
        await accounts.RequestPasswordResetAsync(request, ClientIp(), ct);
        return Accepted();
    }

    /// <summary>Réinitialisation avec le code reçu (AUTH-008). Fait tomber toutes les sessions.</summary>
    [HttpPost("password/reset")]
    public async Task<IActionResult> ResetPassword([FromBody] ResetPasswordRequest request, CancellationToken ct)
        => FromResult(await accounts.ResetPasswordAsync(request, ClientIp(), ct));

    private string? ClientIp() => HttpContext.Connection.RemoteIpAddress?.ToString();

    private async Task RecordSessionAsync(Guid userId, Guid? companyId, string accessToken, CancellationToken ct)
    {
        // Registre des sessions (Phase 11 §6, §30) — meilleur effort, ne bloque pas la connexion.
        // L'identifiant du jeton (jti) lie la session au jeton : révoquer l'une coupe l'autre (AUTH-004).
        try
        {
            Guid? tokenId = Guid.TryParse(new JsonWebToken(accessToken).Id, out var jti) ? jti : null;
            var ua = Request.Headers.UserAgent.ToString();
            await sessions.RecordAsync(userId, companyId,
                string.IsNullOrWhiteSpace(ua) ? null : ua[..Math.Min(ua.Length, 200)], ClientIp(), ua, tokenId, ct);
        }
        catch { /* le registre ne doit jamais empêcher l'authentification */ }
    }
}
