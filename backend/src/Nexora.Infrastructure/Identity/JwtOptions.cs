namespace Nexora.Infrastructure.Identity;

public sealed class JwtOptions
{
    public const string SectionName = "Jwt";

    public string Issuer { get; set; } = "nexora";
    public string Audience { get; set; } = "nexora-api";
    public string SigningKey { get; set; } = string.Empty;   // fourni via env / secret manager (§18)
    public int AccessTokenMinutes { get; set; } = 60;
}
