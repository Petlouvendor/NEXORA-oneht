using Nexora.Application.Abstractions;

namespace Nexora.Infrastructure.Identity;

/// <summary>Hash de mot de passe moderne (Phase 3 §9). Coût ajustable via configuration.</summary>
public sealed class BcryptPasswordHasher(int workFactor = 12) : IPasswordHasher
{
    public string Hash(string password) => BCrypt.Net.BCrypt.HashPassword(password, workFactor);

    public bool Verify(string password, string hash)
    {
        try
        {
            return BCrypt.Net.BCrypt.Verify(password, hash);
        }
        catch (BCrypt.Net.SaltParseException)
        {
            return false;
        }
    }
}
