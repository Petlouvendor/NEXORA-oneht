using Nexora.Contracts.Integrations;
using Nexora.Domain.Common;

namespace Nexora.Application.Integrations;

/// <summary>
/// Gestion des clés d'API d'une entreprise (Phase 35 §13-14). Le secret est haché (SHA-256),
/// affiché une seule fois, et le périmètre d'une clé ne peut jamais dépasser les permissions de
/// l'utilisateur propriétaire. Réservé à <c>integrations.manage_api_keys</c>.
/// </summary>
public interface IApiKeyService
{
    Task<IReadOnlyList<ApiKeyDto>> ListAsync(CancellationToken ct = default);
    Task<Result<ApiKeyCreatedDto>> CreateAsync(CreateApiKeyRequest request, CancellationToken ct = default);
    Task<Result<ApiKeyCreatedDto>> RotateAsync(Guid id, CancellationToken ct = default);
    Task<Result> RevokeAsync(Guid id, CancellationToken ct = default);
}

/// <summary>
/// Résout une clé d'API présentée dans un en-tête (Phase 35 §13, §43, §45). Exécuté en portée
/// système, avant l'établissement du contexte tenant. Vérifie l'empreinte, l'expiration, la
/// révocation, et met à jour <c>last_used_at</c> / <c>last_used_ip</c>. Le quota par minute
/// (§13, §33) est appliqué par le middleware API à partir de <see cref="ApiKeyResolution.RateLimitPerMinute"/>.
/// </summary>
public interface IApiKeyAuthenticator
{
    Task<ApiKeyResolution> ResolveAsync(string presentedKey, string? ipAddress, CancellationToken ct = default);
}

public enum ApiKeyOutcome { Ok, Invalid, Expired, Revoked }

/// <summary>
/// Résultat d'une résolution. <see cref="ApiKeyOutcome.Ok"/> ⇒ <paramref name="CompanyId"/>,
/// <paramref name="OwnerUserId"/> et <paramref name="Scopes"/> sont renseignés.
/// </summary>
public sealed record ApiKeyResolution(
    ApiKeyOutcome Outcome,
    Guid? KeyId = null,
    Guid? CompanyId = null,
    Guid? OwnerUserId = null,
    IReadOnlyList<string>? Scopes = null,
    int RateLimitPerMinute = 120);

/// <summary>
/// Compteur d'usage quotidien par clé (Phase 46 §9, §47-48) — alimente le portail développeur.
/// Best-effort : un échec d'enregistrement ne bloque jamais la requête authentifiée.
/// </summary>
public interface IApiKeyUsageRecorder
{
    Task RecordAsync(Guid apiKeyId, Guid companyId, bool rateLimited, CancellationToken ct = default);
}
