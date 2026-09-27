using Nexora.Application.Abstractions;
using Nexora.Contracts.Auth;

namespace Nexora.Application.UseCases.Auth;

/// <summary>
/// Construit la réponse d'authentification finale (jeton d'accès + profil) une fois tous les
/// facteurs validés. Partagé par la connexion directe (<see cref="LoginHandler"/>) et par la
/// vérification du deuxième facteur (<see cref="MfaVerifyHandler"/>).
/// </summary>
public sealed class AccessTokenIssuer(
    IPermissionService permissions,
    ITokenService tokens,
    IUserRepository users,
    IDateTimeProvider clock)
{
    public async Task<AuthTokenResponse> IssueAsync(
        Nexora.Domain.Identity.AppUser user, Guid? companyId, bool mfaEnrollmentRequired, CancellationToken ct = default)
    {
        var profile = await permissions.LoadProfileAsync(user.Id, ct);
        var token = tokens.IssueAccessToken(user, profile.CompanyIds, profile.GroupIds);
        await users.ResetFailedLoginsAsync(user.Id, ct);
        await users.TouchLastLoginAsync(user.Id, clock.UtcNow, ct);

        return new AuthTokenResponse(
            token.AccessToken,
            "Bearer",
            (int)(token.ExpiresAt - clock.UtcNow).TotalSeconds,
            new CurrentUserResponse(
                user.Id,
                user.Email,
                user.FullName,
                user.MfaEnabled,
                profile.Memberships
                    .Select(m => new CompanyMembershipResponse(m.CompanyId, m.CompanyName, m.GroupId, m.Roles))
                    .ToArray(),
                profile.PermissionsFor(companyId).ToArray()))
        {
            MfaEnrollmentRequired = mfaEnrollmentRequired,
        };
    }
}
