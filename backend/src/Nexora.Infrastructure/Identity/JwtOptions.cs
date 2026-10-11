namespace Nexora.Infrastructure.Identity;

public sealed class JwtOptions
{
    public const string SectionName = "Jwt";

    public string Issuer { get; set; } = "nexora";
    public string Audience { get; set; } = "nexora-api";
    public string SigningKey { get; set; } = string.Empty;   // fourni via env / secret manager (§18)
    /// <summary>Jeton d'accès court (15 min) : il est renouvelé par le jeton de rafraîchissement tant que la session vit.</summary>
    public int AccessTokenMinutes { get; set; } = 15;
}
