namespace Nexora.Application.Abstractions;

/// <summary>
/// Contexte d'exécution de la requête courante (Phase 3 §11-12).
/// Alimenté par le middleware API à partir du jeton + en-têtes, jamais par le corps de requête.
/// L'infrastructure l'utilise pour poser les variables de session RLS PostgreSQL.
/// </summary>
public interface ITenantContext
{
    Guid? UserId { get; }

    /// <summary>Entreprises autorisées pour l'utilisateur (périmètre effectif).</summary>
    IReadOnlyCollection<Guid> CompanyIds { get; }

    /// <summary>Groupes autorisés (consolidation, vues inter-entreprises).</summary>
    IReadOnlyCollection<Guid> GroupIds { get; }

    /// <summary>Entreprise active sélectionnée pour la requête (sous-ensemble de CompanyIds).</summary>
    Guid? ActiveCompanyId { get; }

    /// <summary>Si vrai, l'isolation RLS est contournée (tâches système uniquement).</summary>
    bool BypassRls { get; }

    /// <summary>
    /// Périmètre de permissions d'une clé d'API (Phase 35 §13-14). <c>null</c> pour une session
    /// humaine (JWT) ; sinon la requête est bornée à l'intersection de ces codes et des permissions
    /// de l'utilisateur propriétaire de la clé.
    /// </summary>
    IReadOnlyCollection<string>? ApiScopes { get; }

    /// <summary>
    /// Succursales autorisées (résumé fonctionnel §4). <c>null</c> = aucune restriction : l'utilisateur
    /// est affecté au niveau de l'entreprise, du groupe ou de la plateforme. Transmis à RLS
    /// (<c>nexora.establishment_ids</c>, politiques « branch_isolation »).
    /// </summary>
    IReadOnlyCollection<Guid>? EstablishmentIds { get; }
    void SetEstablishments(IEnumerable<Guid>? establishmentIds);

    void SetUser(Guid userId);
    void SetScope(IEnumerable<Guid> companyIds, IEnumerable<Guid> groupIds, Guid? activeCompanyId);
    void SetApiScopes(IEnumerable<string>? scopes);

    /// <summary>Ouvre une portée système (bypass RLS) pour les requêtes d'infrastructure (IAM, sync).</summary>
    IDisposable BeginSystemScope();

    /// <summary>
    /// Levé quand la portée système commence ou se termine (audit 2026-10, A-009). Le contexte RLS n'était posé qu'à
    /// l'ouverture de la connexion : dans une transaction (connexion déjà ouverte), une portée système était sans
    /// effet, et une transaction ouverte dans une portée système en gardait la levée de RLS après sa fin. La connexion
    /// ouverte réapplique désormais son contexte à chaque changement.
    /// </summary>
    event Action? SystemScopeChanged;
}
