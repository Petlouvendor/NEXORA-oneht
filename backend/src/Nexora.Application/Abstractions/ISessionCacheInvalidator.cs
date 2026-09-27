namespace Nexora.Application.Abstractions;

/// <summary>
/// Invalidation ciblée du cache de vérification de session (Production Readiness — REQ-02).
///
/// <para>
/// <see cref="TenantResolutionMiddleware"/> met en cache le résultat de la vérification
/// <c>(jti → valide/révoqué)</c> pendant <c>Security:SessionCacheTtlSeconds</c> secondes
/// (défaut 30 s) pour éviter une query PostgreSQL sur chaque requête authentifiée.
/// </para>
/// <para>
/// Lorsqu'une session est révoquée de façon explicite (déconnexion, changement de mot de passe,
/// révocation admin), ce service doit être appelé pour vider immédiatement l'entrée de cache —
/// sans attendre l'expiration naturelle des 30 s.
/// </para>
/// </summary>
public interface ISessionCacheInvalidator
{
    /// <summary>
    /// Invalide le cache pour un token JWT précis (révocation d'une session unique).
    /// </summary>
    /// <param name="tokenId">Valeur du claim <c>jti</c> du token à invalider.</param>
    void Invalidate(Guid tokenId);

    /// <summary>
    /// Invalide toutes les entrées de cache associées à un utilisateur.
    /// Utilisé lors d'un changement de mot de passe, d'une déconnexion globale
    /// (<c>security_epoch</c>) ou d'une désactivation de compte.
    /// </summary>
    /// <param name="userId">Identifiant de l'utilisateur dont toutes les sessions sont invalidées.</param>
    void InvalidateUser(Guid userId);
}
