using Nexora.Application.Abstractions;
using Nexora.Application.Identity;
using Nexora.Application.Security;
using Nexora.Contracts.Auth;
using Nexora.Domain.Common;

namespace Nexora.Application.UseCases.Auth;

/// <summary>
/// Deuxième étape de la connexion MFA (Phase 33 §8) : vérifie le code TOTP / de récupération
/// associé au défi émis par <see cref="LoginHandler"/> et émet le jeton d'accès final.
/// </summary>
public sealed class MfaVerifyHandler(
    IMfaService mfa,
    IUserRepository users,
    IPermissionService permissions,
    AccessTokenIssuer issuer,
    ISecurityEventLog securityEvents)
{
    public async Task<Result<AuthTokenResponse>> HandleAsync(
        MfaVerifyLoginRequest request, string? ipAddress = null, CancellationToken ct = default)
    {
        var ticket = await mfa.VerifyChallengeAsync(request.MfaToken, request.Code, ct);
        if (!ticket.IsSuccess) return ticket.Error!;

        var user = await users.FindByIdAsync(ticket.Value!.UserId, ct);
        if (user is null || !user.IsActive)
            return Error.Unauthorized("auth.invalid_credentials", "Compte indisponible.");

        var companyId = ticket.Value!.CompanyId;
        if (companyId is { } requested)
        {
            var profile = await permissions.LoadProfileAsync(user.Id, ct);
            if (!profile.CompanyIds.Contains(requested))
                return Error.Forbidden("auth.company_not_authorized", "Entreprise hors du périmètre de l'utilisateur.");
        }

        var response = await issuer.IssueAsync(user, companyId, await mfa.PolicyRequiresMfaAsync(user.Id, ct), ct);
        await securityEvents.RecordAsync("login.succeeded", user.Id, companyId, detail: "mfa", ipAddress: ipAddress, ct: ct);
        return Result.Success(response);
    }
}
