using System.Security.Claims;
using System.Text;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Nexora.Application.Abstractions;
using Nexora.Domain.Identity;

namespace Nexora.Infrastructure.Identity;

public static class NexoraClaims
{
    public const string UserId = "nx_uid";
    public const string CompanyIds = "nx_companies";
    public const string GroupIds = "nx_groups";
}

public sealed class JwtTokenService(IOptions<JwtOptions> options, IDateTimeProvider clock) : ITokenService
{
    private readonly JwtOptions _o = options.Value;

    public IssuedToken IssueAccessToken(
        AppUser user, IReadOnlyCollection<Guid> companyIds, IReadOnlyCollection<Guid> groupIds)
    {
        var now = clock.UtcNow;
        var expires = now.AddMinutes(_o.AccessTokenMinutes);

        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, user.Id.ToString()),
            // Identifiant du jeton, enregistré sur la session : révoquer la session coupe ce jeton.
            new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString()),
            new(JwtRegisteredClaimNames.Email, user.Email),
            new(NexoraClaims.UserId, user.Id.ToString()),
            new(NexoraClaims.CompanyIds, string.Join(",", companyIds)),
            new(NexoraClaims.GroupIds, string.Join(",", groupIds)),
        };

        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_o.SigningKey));
        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = _o.Issuer,
            Audience = _o.Audience,
            Subject = new ClaimsIdentity(claims),
            IssuedAt = now.UtcDateTime,
            NotBefore = now.UtcDateTime,
            Expires = expires.UtcDateTime,
            SigningCredentials = new SigningCredentials(key, SecurityAlgorithms.HmacSha256),
        };

        var token = new JsonWebTokenHandler().CreateToken(descriptor);
        return new IssuedToken(token, expires);
    }
}
