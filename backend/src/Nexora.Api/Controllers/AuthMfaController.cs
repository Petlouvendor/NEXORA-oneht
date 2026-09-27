using Asp.Versioning;
using Microsoft.AspNetCore.Mvc;
using Nexora.Application.Identity;
using Nexora.Contracts.Auth;

namespace Nexora.Api.Controllers;

/// <summary>
/// Gestion self-service de l'authentification multi-facteur de l'utilisateur courant
/// (Phase 33 §8-9). Authentifié — l'enrôlement et la désactivation exigent une session valide.
/// </summary>
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/auth/mfa")]
public sealed class AuthMfaController(IMfaService mfa) : ApiControllerBase
{
    /// <summary>Statut MFA : activée, date d'enrôlement, codes de récupération restants, imposée par la politique.</summary>
    [HttpGet]
    [HttpGet("~/api/v{version:apiVersion}/security/mfa")]   // alias Phase 47 §58 — même service
    public async Task<IActionResult> Status(CancellationToken ct) => Ok(await mfa.StatusAsync(ct));

    /// <summary>Démarre l'enrôlement — renvoie le secret + l'URI <c>otpauth://</c> (à afficher une seule fois).</summary>
    [HttpPost("enroll")]
    [HttpPost("~/api/v{version:apiVersion}/security/mfa/enroll")]   // alias Phase 47 §58
    public async Task<IActionResult> Enroll(CancellationToken ct) => FromResult(await mfa.BeginEnrollmentAsync(ct));

    /// <summary>Confirme l'enrôlement avec un premier code valide → active la MFA et renvoie les codes de récupération.</summary>
    [HttpPost("enroll/confirm")]
    public async Task<IActionResult> Confirm([FromBody] MfaConfirmRequest r, CancellationToken ct)
        => FromResult(await mfa.ConfirmEnrollmentAsync(r, ct));

    /// <summary>Désactive la MFA (exige un code TOTP ou de récupération valide).</summary>
    [HttpPost("disable")]
    [HttpPost("~/api/v{version:apiVersion}/security/mfa/disable")]   // alias Phase 47 §58
    public async Task<IActionResult> Disable([FromBody] MfaDisableRequest r, CancellationToken ct)
        => FromResult(await mfa.DisableAsync(r, ct));
}
