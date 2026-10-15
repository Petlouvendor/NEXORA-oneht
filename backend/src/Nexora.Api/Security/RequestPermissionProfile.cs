using Nexora.Application.Abstractions;

namespace Nexora.Api.Security;

/// <summary>
/// Profil de permissions chargé une fois par requête HTTP pour les contrôles du pipeline (attributs
/// <c>[RequirePermission]</c> de la classe et de l'action, mode maintenance) — audit 2026-10, P-001 : chaque contrôle
/// rejouait les 3 à 4 requêtes du profil. Les services gardent leur propre chargement, à jour même si un rôle change
/// pendant la requête.
/// </summary>
public static class RequestPermissionProfile
{
    private const string Key = "nexora.permission_profile";

    public static async Task<UserSecurityProfile> GetAsync(HttpContext? http, IPermissionService permissions, Guid userId,
        CancellationToken ct = default)
    {
        if (http is null) return await permissions.LoadProfileAsync(userId, ct);
        if (http.Items[Key] is UserSecurityProfile cached && cached.UserId == userId) return cached;
        var profile = await permissions.LoadProfileAsync(userId, ct);
        http.Items[Key] = profile;
        return profile;
    }
}
