using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Nexora.Application.Abstractions;

namespace Nexora.Infrastructure.Identity;

/// <summary>
/// Implémentation de <see cref="ISessionCacheInvalidator"/> sur <see cref="IMemoryCache"/>
/// (Production Readiness — REQ-02).
///
/// <para>
/// <b>Clés de cache gérées</b>
/// <list type="bullet">
///   <item><c>session:{tokenId}</c> — résultat de vérification pour un JWT précis ;
///   posée par <c>TenantResolutionMiddleware</c>, invalidée ici lors d'une révocation.</item>
///   <item><c>user-revoked:{userId}</c> — marqueur de révocation globale pour un utilisateur ;
///   son existence suffit à court-circuiter le cache de session (TTL 60 s).
///   Utilisé lors d'un changement de mot de passe ou d'une désactivation de compte.</item>
/// </list>
/// </para>
///
/// <para>Ce service est enregistré en <b>Singleton</b> — le <see cref="IMemoryCache"/> est
/// lui-même Singleton et les opérations <c>Remove</c> / <c>Set</c> sont thread-safe.</para>
/// </summary>
public sealed class SessionCacheInvalidator(
    IMemoryCache cache,
    ILogger<SessionCacheInvalidator> logger) : ISessionCacheInvalidator
{
    /// <summary>TTL du marqueur de révocation globale. Doit être ≥ TTL des entrées de session
    /// (<c>Security:SessionCacheTtlSeconds</c>, défaut 30 s) pour garantir que toutes
    /// les entrées existantes expirent avant que le marqueur disparaisse.</summary>
    private static readonly TimeSpan UserRevokedTtl = TimeSpan.FromSeconds(60);

    /// <inheritdoc />
    public void Invalidate(Guid tokenId)
    {
        var key = SessionCacheKeys.Session(tokenId);
        cache.Remove(key);
        logger.LogDebug("[session-cache] invalidé → {Key}", key);
    }

    /// <inheritdoc />
    public void InvalidateUser(Guid userId)
    {
        // Pose un marqueur de révocation globale. TenantResolutionMiddleware le vérifie
        // avant même de consulter l'entrée de session individuelle.
        var key = SessionCacheKeys.UserRevoked(userId);
        cache.Set(key, true, new MemoryCacheEntryOptions
        {
            AbsoluteExpirationRelativeToNow = UserRevokedTtl,
            Size = 1,
        });
        logger.LogDebug("[session-cache] révocation globale posée → {Key} (TTL {Ttl}s)", key, UserRevokedTtl.TotalSeconds);
    }
}

/// <summary>Clés de cache partagées entre <see cref="SessionCacheInvalidator"/> et
/// <c>TenantResolutionMiddleware</c> — source unique de vérité pour les noms de clés.</summary>
public static class SessionCacheKeys
{
    /// <summary>Entrée de vérification pour un JWT précis.</summary>
    public static string Session(Guid tokenId) => $"session:{tokenId:D}";

    /// <summary>Marqueur de révocation globale pour un utilisateur entier.</summary>
    public static string UserRevoked(Guid userId) => $"user-revoked:{userId:D}";
}
