using Microsoft.Extensions.Logging;
using Nexora.Application.Abstractions;
using Nexora.Application.Identity;
using Nexora.Application.Security;
using Nexora.Contracts.Auth;
using Nexora.Domain.Common;

namespace Nexora.Application.UseCases.Auth;

public sealed class LoginHandler(
    IUserRepository users,
    IPermissionService permissions,
    IPasswordHasher hasher,
    IMfaService mfa,
    ISecurityEventLog securityEvents,
    AccessTokenIssuer issuer,
    IDateTimeProvider clock,
    ILogger<LoginHandler> logger)
{
    public async Task<Result<AuthTokenResponse>> HandleAsync(
        LoginRequest request, string? ipAddress = null, CancellationToken ct = default)
    {
        var user = await users.FindByEmailAsync(request.Email.Trim(), ct);

        // Message générique : ne pas révéler l'existence du compte (Phase 3 §9).
        var invalid = Error.Unauthorized("auth.invalid_credentials", "Identifiants invalides.");

        if (user is null || !user.IsActive || string.IsNullOrEmpty(user.PasswordHash))
        {
            logger.LogInformation("Échec de connexion pour {Email} (compte inconnu/inactif)", request.Email);
            return invalid;
        }

        // Verrouillage temporaire après essais répétés (Phase 33 §4, §7, §48).
        if (user.LockedUntil is { } until && until > clock.UtcNow)
        {
            await securityEvents.RecordAsync("login.blocked", user.Id, request.CompanyId,
                detail: "compte verrouillé", ipAddress: ipAddress, ct: ct);
            return Error.Unauthorized("auth.account_locked",
                "Compte temporairement verrouillé après plusieurs tentatives. Réessayez plus tard.");
        }

        if (!hasher.Verify(request.Password, user.PasswordHash))
        {
            var lockedUntil = await users.RegisterFailedLoginAsync(user.Id, clock.UtcNow, ct);
            await securityEvents.RecordAsync(lockedUntil is null ? "login.failed" : "login.locked",
                user.Id, request.CompanyId, detail: "mot de passe incorrect", ipAddress: ipAddress, ct: ct);
            logger.LogWarning("Échec de connexion pour l'utilisateur {UserId} (mot de passe)", user.Id);
            return invalid;
        }

        var profile = await permissions.LoadProfileAsync(user.Id, ct);

        if (request.CompanyId is { } requested && !profile.CompanyIds.Contains(requested))
        {
            await securityEvents.RecordAsync("login.company_denied", user.Id, requested, ipAddress: ipAddress, ct: ct);
            return Error.Forbidden("auth.company_not_authorized", "Entreprise hors du périmètre de l'utilisateur.");
        }

        // Deuxième facteur (Phase 33 §8) : si la MFA est active ET dûment enrôlée (un secret existe),
        // on n'émet pas encore le jeton. Un `mfa_enabled` sans secret (donnée incohérente) ne doit
        // jamais bloquer la connexion — il n'y aurait aucun moyen de répondre au défi.
        if (user.MfaEnabled && !string.IsNullOrEmpty(user.MfaSecret))
        {
            var (token, expires) = await mfa.IssueChallengeAsync(user.Id, request.CompanyId, ct);
            await securityEvents.RecordAsync("login.mfa_challenged", user.Id, request.CompanyId, ipAddress: ipAddress, ct: ct);
            return Result.Success(new AuthTokenResponse(string.Empty, "Bearer", 0,
                new CurrentUserResponse(user.Id, user.Email, user.FullName, true, [], []))
            {
                MfaRequired = true,
                MfaToken = token,
                MfaExpiresAt = expires,
            });
        }

        var policyRequiresMfa = await mfa.PolicyRequiresMfaAsync(user.Id, ct);
        var response = await issuer.IssueAsync(user, request.CompanyId, policyRequiresMfa, ct);
        await securityEvents.RecordAsync("login.succeeded", user.Id, request.CompanyId, ipAddress: ipAddress, ct: ct);
        return Result.Success(response);
    }
}
