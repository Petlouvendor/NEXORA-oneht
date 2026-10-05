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
            return Ok(await StartSessionAsync(result.Value!, request.CompanyId, ct));
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
            return Ok(await StartSessionAsync(result.Value!, null, ct));
        return FromResult(result);
    }

    /// <summary>
    /// Rafraîchissement (finalisation phase 17) : le jeton de rafraîchissement voyage dans un cookie
    /// HttpOnly limité à <c>/api/v1/auth</c> — jamais lisible par le JavaScript de la page. Chaque appel le
    /// consomme et en remet un nouveau (rotation) ; un jeton déjà consommé ferme toutes les sessions.
    /// </summary>
    [HttpPost("refresh")]
    [EnableRateLimiting("refresh")]
    [ProducesResponseType<AuthTokenResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> Refresh(CancellationToken ct)
    {
        var raw = Request.Cookies[RefreshCookie];
        var outcome = await sessions.RefreshAsync(raw ?? "", ClientIp(), ct);
        if (!outcome.IsSuccess)
        {
            // Un autre onglet vient de renouveler : on garde son cookie (déjà posé) ; sinon on l'efface.
            if (outcome.Error!.Code != "auth.refresh_race") ClearRefreshCookie(Response);
            return FromResult(outcome);
        }
        SetRefreshCookie(outcome.Value!.RefreshToken, outcome.Value.ExpiresAt);
        return Ok(outcome.Value.Token);
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

    /// <summary>Déconnexion par le cookie de rafraîchissement — fonctionne même si le jeton d'accès a expiré.</summary>
    [HttpPost("logout")]
    [EnableRateLimiting("refresh")]
    public async Task<IActionResult> Logout(CancellationToken ct)
    {
        if (Request.Cookies[RefreshCookie] is { Length: > 0 } raw) await sessions.RevokeByRefreshTokenAsync(raw, ct);
        ClearRefreshCookie(Response);
        return NoContent();
    }

    public const string RefreshCookie = "nx_rt";
    public const string RefreshCookiePath = "/api/v1/auth";

    /// <summary>Ouvre la session portée par le jeton (claim <c>sid</c>) et pose le cookie de rafraîchissement.</summary>
    private async Task<AuthTokenResponse> StartSessionAsync(AuthTokenResponse token, Guid? companyId, CancellationToken ct)
    {
        Guid? tokenId = Guid.TryParse(new JsonWebToken(token.AccessToken).Id, out var jti) ? jti : null;
        var ua = Request.Headers.UserAgent.ToString();
        var start = await sessions.StartAsync(token.SessionId, token.User.Id, companyId, ClientIp(),
            string.IsNullOrWhiteSpace(ua) ? null : ua, tokenId, ct);
        SetRefreshCookie(start.RefreshToken, start.ExpiresAt);
        return token with { SessionExpiresAt = start.ExpiresAt };
    }

    private void SetRefreshCookie(string value, DateTimeOffset expires)
        => Response.Cookies.Append(RefreshCookie, value, new CookieOptions
        {
            HttpOnly = true,
            Secure = Request.IsHttps,
            SameSite = SameSiteMode.Strict,
            Path = RefreshCookiePath,
            Expires = expires,
            IsEssential = true,
        });

    public static void ClearRefreshCookie(HttpResponse response)
        => response.Cookies.Delete(RefreshCookie, new CookieOptions { Path = RefreshCookiePath, SameSite = SameSiteMode.Strict, HttpOnly = true });
}
