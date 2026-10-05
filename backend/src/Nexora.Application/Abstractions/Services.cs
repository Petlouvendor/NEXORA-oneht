using Nexora.Domain.Identity;

namespace Nexora.Application.Abstractions;

public interface IPasswordHasher
{
    string Hash(string password);
    bool Verify(string password, string hash);
}

public sealed record IssuedToken(string AccessToken, DateTimeOffset ExpiresAt);

public interface ITokenService
{
    /// <summary>Jeton d'accès court. <paramref name="sessionId"/> (claim <c>sid</c>) lie le jeton à sa session :
    /// révoquer ou laisser expirer la session coupe tous ses jetons, y compris ceux émis par rafraîchissement.</summary>
    IssuedToken IssueAccessToken(AppUser user, IReadOnlyCollection<Guid> companyIds, IReadOnlyCollection<Guid> groupIds, Guid? sessionId = null);
}

public interface IDateTimeProvider
{
    DateTimeOffset UtcNow { get; }
}

/// <summary>Unité de travail transactionnelle (Phase 3 §15 : atomicité, rollback complet).</summary>
public interface IUnitOfWork
{
    Task<int> SaveChangesAsync(CancellationToken ct = default);
    Task<T> ExecuteInTransactionAsync<T>(Func<CancellationToken, Task<T>> action, CancellationToken ct = default);
}

/// <summary>Résolution des permissions effectives d'un utilisateur (Phase 3 §10, deny-by-default).</summary>
public interface IPermissionService
{
    Task<UserSecurityProfile> LoadProfileAsync(Guid userId, CancellationToken ct = default);
}

public sealed record UserSecurityProfile(
    Guid UserId,
    IReadOnlyList<CompanyMembership> Memberships,
    IReadOnlyCollection<string>? PlatformPermissions = null,
    IReadOnlyDictionary<Guid, IReadOnlyCollection<string>>? GroupPermissions = null)
{
    /// <summary>
    /// Permissions de gouvernance de groupe (<c>group.*</c>, <c>consolidation.*</c>) pour ce groupe :
    /// seules comptent celles d'un rôle attribué au niveau du groupe ou de la plateforme. Un rôle
    /// attribué à une seule société ne donne jamais accès aux sociétés sœurs (finalisation phase 14).
    /// </summary>
    public IReadOnlyCollection<string> GroupPermissionsFor(Guid groupId) =>
        (GroupPermissions is { } gp && gp.TryGetValue(groupId, out var perms) ? perms : [])
            .Concat(PlatformPermissions ?? [])
            .Distinct()
            .ToArray();

    public IReadOnlyCollection<Guid> CompanyIds => Memberships.Select(m => m.CompanyId).Distinct().ToArray();
    public IReadOnlyCollection<Guid> GroupIds => Memberships.Where(m => m.GroupId is not null).Select(m => m.GroupId!.Value).Distinct().ToArray();

    public IReadOnlyCollection<string> PermissionsFor(Guid? companyId) =>
        Memberships
            .Where(m => companyId is null || m.CompanyId == companyId)
            .SelectMany(m => m.Permissions)
            .Distinct()
            .ToArray();

    /// <summary>
    /// Vraie si la permission est accordée dans le périmètre entreprise, ou si c'est une permission
    /// plateforme (<c>platform.*</c>, Phase 38 §35) portée par un rôle à scope PLATFORM — ces rôles
    /// n'ont pas d'entreprise de rattachement.
    /// </summary>
    public bool HasPermission(string code, Guid? companyId) =>
        PermissionsFor(companyId).Contains(code)
        || (PlatformPermissions is { } pp && pp.Contains(code));
}

/// <summary>
/// Résumé fonctionnel §4 : succursales auxquelles un utilisateur est restreint dans les entreprises
/// données. <c>null</c> = aucune restriction (affectation entreprise / groupe / plateforme partout).
/// </summary>
public interface IBranchScopeResolver
{
    Task<IReadOnlyCollection<Guid>?> ResolveAsync(Guid userId, IReadOnlyCollection<Guid> companyIds, CancellationToken ct = default);
}

public static class PermissionServiceExtensions
{
    /// <summary>Permissions de l'utilisateur courant dans l'entreprise active — vide s'il n'y a pas
    /// d'utilisateur. Sert aux contrôles au niveau des données (champ masqué, variante de réponse)
    /// que l'attribut d'endpoint ne peut pas exprimer.</summary>
    public static async Task<IReadOnlyCollection<string>> CurrentPermissionsAsync(
        this IPermissionService permissions, ITenantContext tenant, CancellationToken ct = default)
        => tenant.UserId is { } uid
            ? (await permissions.LoadProfileAsync(uid, ct)).PermissionsFor(tenant.ActiveCompanyId)
            : [];
}

public sealed record CompanyMembership(
    Guid CompanyId,
    string CompanyName,
    Guid? GroupId,
    IReadOnlyList<string> Roles,
    IReadOnlyList<string> Permissions);
