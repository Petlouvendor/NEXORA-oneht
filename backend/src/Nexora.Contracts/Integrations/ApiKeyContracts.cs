namespace Nexora.Contracts.Integrations;

// =============================================================================
// Clés d'API pour les intégrations serveur (Phase 35 §13-14, §43-44).
// =============================================================================

public sealed record ApiKeyDto(
    Guid Id,
    string Name,
    string KeyPrefix,
    string Environment,
    IReadOnlyList<string> Scopes,
    int RateLimitPerMinute,
    Guid OwnerUserId,
    DateTimeOffset? ExpiresAt,
    DateTimeOffset? LastUsedAt,
    string? LastUsedIp,
    DateTimeOffset? RevokedAt,
    DateTimeOffset CreatedAt,
    string Status,   // active | expired | revoked
    Guid? AppInstallationId = null);   // clé émise pour une app marketplace installée (Phase 46 §8, §46)

public sealed record CreateApiKeyRequest(
    string Name,
    IReadOnlyList<string> Scopes,
    string Environment = "live",
    int? ExpiresInDays = null,
    int RateLimitPerMinute = 120,
    /// <summary>Si renseigné, la clé est un « service account » d'une app installée (Phase 46 §8) :
    /// les scopes doivent aussi être ⊆ <c>app_installation.granted_scopes</c>, en plus d'être
    /// ⊆ aux permissions du propriétaire (règle Phase 35 §13 inchangée).</summary>
    Guid? AppInstallationId = null);

/// <summary>Le secret n'est renvoyé qu'une seule fois, à la création / rotation (§14).</summary>
public sealed record ApiKeyCreatedDto(ApiKeyDto Key, string Secret);
