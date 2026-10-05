using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Nexora.Application.Abstractions;
using Nexora.Infrastructure.Identity;
using Nexora.Infrastructure.Persistence;
using Nexora.Infrastructure.Tenancy;

namespace Nexora.Api.Middleware;

/// <summary>
/// Établit le périmètre effectif de la requête (Phase 3 §11-12) à partir du jeton validé :
///
///  • X-Company-Id  → restreint le périmètre à cette entreprise (doit être autorisée).
///  • X-Group-Scope: true → vue groupe (consolidation) si l'utilisateur a un périmètre groupe.
///  • sinon         → toutes les entreprises autorisées de l'utilisateur.
///
/// Le company_id du client n'accorde jamais d'accès : il ne fait que *sélectionner*
/// parmi les entreprises déjà présentes dans le jeton.
///
/// <para>
/// <b>Cache de révocation de session (Production Readiness — REQ-02)</b> : la vérification
/// <c>(jti → valide/révoqué/epoch)</c> est mise en cache <see cref="IMemoryCache"/> pendant
/// <c>Security:SessionCacheTtlSeconds</c> secondes (défaut 30 s) pour éviter une query
/// PostgreSQL sur chaque requête authentifiée. L'entrée est invalidée immédiatement par
/// <see cref="ISessionCacheInvalidator"/> lors d'une révocation explicite.
/// </para>
/// </summary>
public sealed class TenantResolutionMiddleware(RequestDelegate next)
{
    public const string CompanyHeader = "X-Company-Id";
    public const string GroupScopeHeader = "X-Group-Scope";

    /// <summary>TTL du cache de validité de session. Configurable via <c>Security:SessionCacheTtlSeconds</c>.</summary>
    private const int DefaultSessionCacheTtlSeconds = 30;

    public async Task InvokeAsync(HttpContext context, TenantContext tenant, NexoraDbContext db,
        IBranchScopeResolver branches, IMemoryCache cache, IConfiguration configuration)
    {
        var user = context.User;
        if (user.Identity?.IsAuthenticated != true)
        {
            await next(context);
            return;
        }

        if (Guid.TryParse(user.FindFirstValue(NexoraClaims.UserId), out var userId))
        {
            tenant.SetUser(userId);

            // Déconnexion globale / désactivation de compte (Phase 11 §6, §30, §40) :
            // un jeton émis avant l'epoch de sécurité de l'utilisateur est refusé.
            // Recette Phase 55 AUTH-004 : un jeton dont la session a été révoquée est refusé aussi.
            if (long.TryParse(user.FindFirstValue("iat"), out var iatSeconds))
            {
                var issuedAt = DateTimeOffset.FromUnixTimeSeconds(iatSeconds);
                Guid? tokenId = Guid.TryParse(user.FindFirstValue("jti"), out var jti) ? jti : null;
                Guid? sessionId = Guid.TryParse(user.FindFirstValue("sid"), out var sid) ? sid : null;

                var validity = await ResolveSessionValidityAsync(
                    userId, tokenId, sessionId, issuedAt, db, cache, configuration, context.RequestAborted);

                if (!validity.IsValid)
                {
                    context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                    await context.Response.WriteAsJsonAsync(new
                    {
                        title = "session_revoked",
                        detail = "Session invalidée — reconnectez-vous.",
                        correlationId = context.TraceIdentifier,
                    });
                    return;
                }
            }
        }

        var authorizedCompanies = ParseGuidList(user.FindFirstValue(NexoraClaims.CompanyIds));
        var authorizedGroups = ParseGuidList(user.FindFirstValue(NexoraClaims.GroupIds));

        Guid? active = null;
        if (context.Request.Headers.TryGetValue(CompanyHeader, out var raw) && Guid.TryParse(raw, out var requested))
        {
            if (!authorizedCompanies.Contains(requested))
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                await context.Response.WriteAsJsonAsync(new
                {
                    title = "company_not_authorized",
                    detail = "Entreprise hors du périmètre autorisé.",
                    correlationId = context.TraceIdentifier,
                });
                return;
            }
            active = requested;
        }

        var groupScope = context.Request.Headers.TryGetValue(GroupScopeHeader, out var gs)
            && bool.TryParse(gs, out var b) && b
            && authorizedGroups.Count > 0;

        // Clé d'API (Phase 35 §13) : identité machine bornée à une seule entreprise → celle-ci
        // devient l'entreprise active par défaut (aucun en-tête X-Company-Id attendu).
        var apiScopesClaim = user.FindFirstValue(ApiKeyAuthenticationMiddleware.ScopeClaim);
        if (apiScopesClaim is not null && active is null && authorizedCompanies.Count == 1)
            active = authorizedCompanies[0];

        // Périmètre effectif transmis à RLS.
        IEnumerable<Guid> effectiveCompanies = active is { } a ? [a] : authorizedCompanies;
        IEnumerable<Guid> effectiveGroups = groupScope ? authorizedGroups : [];

        tenant.SetScope(effectiveCompanies, effectiveGroups, active);

        // Résumé fonctionnel §4 : un utilisateur affecté à une succursale ne voit que ses succursales.
        if (tenant.UserId is { } uid && tenant.CompanyIds.Count > 0)
            tenant.SetEstablishments(await branches.ResolveAsync(uid, tenant.CompanyIds, context.RequestAborted));

        // La requête est bornée aux scopes de la clé, en plus des permissions du propriétaire.
        tenant.SetApiScopes(apiScopesClaim is null ? null
            : apiScopesClaim.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

        await next(context);
    }

    // -------------------------------------------------------------------------
    // Cache de vérification de session (REQ-02)
    // -------------------------------------------------------------------------

    private sealed record SessionValidity(bool IsValid, DateTimeOffset Epoch);

    /// <summary>
    /// Résout la validité de session depuis le cache ou PostgreSQL.
    /// Cache TTL = <c>Security:SessionCacheTtlSeconds</c> (défaut 30 s).
    /// La taille de chaque entrée est comptée à 1 (limite globale = 10 000 dans AddMemoryCache).
    /// </summary>
    private static async Task<SessionValidity> ResolveSessionValidityAsync(
        Guid userId,
        Guid? tokenId,
        Guid? sessionId,
        DateTimeOffset issuedAt,
        NexoraDbContext db,
        IMemoryCache cache,
        IConfiguration configuration,
        CancellationToken ct)
    {
        // --- Marqueur de révocation globale (posé par SessionCacheInvalidator.InvalidateUser) ---
        // Tant qu'il vit, le cache de cet utilisateur est ignoré et PostgreSQL tranche : les jetons
        // antérieurs à l'epoch sont refusés, mais celui obtenu en se reconnectant reste accepté.
        var userRevoked = cache.TryGetValue(SessionCacheKeys.UserRevoked(userId), out _);

        // --- Cache individuel par jti ---
        if (!userRevoked && tokenId is { } tid)
        {
            var cacheKey = SessionCacheKeys.Session(tid);
            if (cache.TryGetValue<SessionValidity>(cacheKey, out var cached) && cached is not null)
            {
                // Revalider l'epoch même sur un hit cache : l'epoch peut avoir été avancée
                // (déconnexion globale) après la mise en cache de la session.
                return new SessionValidity(cached.IsValid && issuedAt >= cached.Epoch.AddSeconds(-5), cached.Epoch);
            }
        }

        // --- Miss cache : query PostgreSQL ---
        // Exécution sans portée système explicite : tenant n'est pas encore configuré à ce stade.
        // On lit app_user directement (RLS inactive sur app_user pour cette query anonyme).
        UserSecurityRow? check = await db.Database.SqlQuery<UserSecurityRow>($"""
                SELECT u.status AS "Status", u.security_epoch AS "Epoch",
                       (({tokenId}::uuid IS NOT NULL AND EXISTS(
                           SELECT 1 FROM nexora.user_session s
                           WHERE s.token_id = {tokenId} AND s.user_id = u.id AND s.revoked_at IS NOT NULL))
                        -- Finalisation phase 17 : la session porteuse (claim sid) révoquée ou arrivée à sa durée
                        -- maximale coupe tous ses jetons, y compris ceux émis par rafraîchissement.
                        OR ({sessionId}::uuid IS NOT NULL AND EXISTS(
                           SELECT 1 FROM nexora.user_session s
                           WHERE s.id = {sessionId} AND s.user_id = u.id AND (s.revoked_at IS NOT NULL OR s.expires_at <= now())))) AS "SessionRevoked"
                FROM nexora.app_user u WHERE u.id = {userId}
                """).FirstOrDefaultAsync(ct);

        // Dernière activité de la session (sert au délai d'inactivité du rafraîchissement) — au plus une écriture
        // par durée de cache, jamais à chaque requête.
        if (sessionId is { } activeSession && check is { Status: "active", SessionRevoked: false })
            await db.Database.ExecuteSqlAsync($"""
                UPDATE nexora.user_session SET last_seen_at = now()
                WHERE id = {activeSession} AND revoked_at IS NULL AND last_seen_at < now() - interval '30 seconds'
                """, ct);

        var isValid = check is not null
            && check.Status == "active"
            && !check.SessionRevoked
            && issuedAt >= check.Epoch.AddSeconds(-5);

        var validity = new SessionValidity(isValid, check?.Epoch ?? DateTimeOffset.MinValue);

        // --- Mise en cache du résultat ---
        if (tokenId is { } cacheTokenId)
        {
            var ttl = configuration.GetValue("Security:SessionCacheTtlSeconds", DefaultSessionCacheTtlSeconds);
            var cacheKey = SessionCacheKeys.Session(cacheTokenId);
            cache.Set(cacheKey, validity, new MemoryCacheEntryOptions
            {
                AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(ttl),
                Size = 1,
            });
        }

        return validity;
    }

    private sealed record UserSecurityRow(string Status, DateTimeOffset Epoch, bool SessionRevoked);

    private static List<Guid> ParseGuidList(string? csv) =>
        string.IsNullOrWhiteSpace(csv)
            ? []
            : csv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                 .Select(s => Guid.TryParse(s, out var g) ? g : (Guid?)null)
                 .Where(g => g is not null)
                 .Select(g => g!.Value)
                 .ToList();
}
