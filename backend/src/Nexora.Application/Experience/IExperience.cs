using Nexora.Contracts.Experience;

namespace Nexora.Application.Experience;

/// <summary>
/// Recherche globale (NEXORA ONE §5, Ctrl+K) : produits, clients, fournisseurs, employés, documents
/// émis et ventes, en une requête. Chaque type d'objet est filtré par la permission qui ouvre son
/// écran ; la RLS reste la seconde barrière (entreprise active).
/// </summary>
public interface IGlobalSearchService
{
    Task<GlobalSearchResponse> SearchAsync(string query, int limitPerGroup, CancellationToken ct = default);
}

/// <summary>
/// Fil d'activité (NEXORA ONE §6 « ACTIVITY ») : les opérations importantes récentes, lues dans les
/// tables métier (jamais une seconde source de vérité) et filtrées par permission, source par source.
/// </summary>
public interface IActivityFeedService
{
    Task<IReadOnlyList<ActivityEventDto>> RecentAsync(int limit, CancellationToken ct = default);
}
