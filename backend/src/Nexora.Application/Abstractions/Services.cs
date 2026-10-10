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

/// <summary>
/// Date métier (analyse 2026-10, §5.5) : le jour civil dans le fuseau de l'entreprise active
/// (<c>company.timezone</c>, Haïti par défaut) — jamais le jour UTC, qui bascule au lendemain dès 20 h en Haïti.
/// Toute date d'opération (écriture, échéance, « aujourd'hui » d'un rapport) passe par ce service ; la base suit le
/// même fuseau (session PostgreSQL réglée à l'ouverture de connexion).
/// </summary>
public interface IBusinessCalendar
{
    /// <summary>Fuseau de l'entreprise active (lu en base au plus une fois par requête, puis gardé en cache).</summary>
    TimeZoneInfo Zone { get; }

    /// <summary>Jour civil courant de l'entreprise (horloge <see cref="IDateTimeProvider"/>).</summary>
    DateOnly Today { get; }

    /// <summary>Jour civil d'un instant, dans le fuseau de l'entreprise.</summary>
    DateOnly DateOf(DateTimeOffset instant);

    /// <summary>Fuseau de l'entreprise active, sans appel bloquant.</summary>
    Task<TimeZoneInfo> ZoneAsync(CancellationToken ct = default);

    /// <summary>Jour civil courant de l'entreprise (horloge <see cref="IDateTimeProvider"/>).</summary>
    Task<DateOnly> TodayAsync(CancellationToken ct = default);

    /// <summary>Jour civil d'un instant, dans le fuseau de l'entreprise.</summary>
    Task<DateOnly> DateOfAsync(DateTimeOffset instant, CancellationToken ct = default);
}

/// <summary>Conversion instant → jour civil dans un fuseau donné.</summary>
public static class BusinessDate
{
    /// <summary>Fuseau appliqué quand aucune entreprise n'est active.</summary>
    public const string DefaultZoneId = "America/Port-au-Prince";

    public static DateOnly Of(DateTimeOffset instant, TimeZoneInfo zone)
        => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(instant, zone).DateTime);

    /// <summary>
    /// Instant représentant un jour civil saisi (date de facture, de comptage…) : midi UTC, qui reste ce même jour
    /// dans tout fuseau de UTC−11 à UTC+11. Minuit UTC tomberait la veille au soir en Haïti.
    /// </summary>
    public static DateTimeOffset Noon(DateOnly day) => new(day.ToDateTime(new TimeOnly(12, 0)), TimeSpan.Zero);
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
