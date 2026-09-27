using Nexora.Contracts.Common;
using Nexora.Contracts.Marketplace;
using Nexora.Domain.Common;

namespace Nexora.Application.Marketplace;

/// <summary>
/// Catalogue marketplace (Phase 46 §41-42, §45) — curé par NEXORA (<c>platform.manage_marketplace</c>).
/// Un tiers ne publie jamais directement : soumission → revue humaine → publication, jamais
/// d'auto-approbation. La lecture publique (§17) ne montre que les apps <c>approved</c> à un
/// appelant qui n'a pas ce privilège.
/// </summary>
public interface IMarketplaceCatalogService
{
    Task<CursorPage<MarketplaceAppDto>> ListAsync(string? category, string? status, string? cursor, int limit, bool includeAllStatuses, CancellationToken ct = default);
    Task<Result<MarketplaceAppDto>> GetAsync(Guid id, bool includeAllStatuses, CancellationToken ct = default);
    Task<Result<MarketplaceAppDto>> SubmitAsync(SubmitAppRequest request, CancellationToken ct = default);
    Task<Result<MarketplaceAppDto>> ReviewAsync(Guid id, ReviewAppRequest request, CancellationToken ct = default);
}

/// <summary>
/// Installation d'une app marketplace par une entreprise (Phase 46 §43-44, §56). Le périmètre
/// accordé (<see cref="InstallAppRequest.GrantedScopes"/>) doit être ⊆ ce que l'app a demandé —
/// jamais plus, l'entreprise peut toujours accorder moins.
/// </summary>
public interface IAppInstallationService
{
    Task<IReadOnlyList<AppInstallationDto>> ListInstalledAsync(CancellationToken ct = default);
    Task<Result<AppInstallationDto>> InstallAsync(Guid marketplaceAppId, InstallAppRequest request, CancellationToken ct = default);
    /// <summary>§43 : une seule installation par (entreprise, app) — identifiée par l'id de l'app, pas de l'installation.</summary>
    Task<Result> DisableAsync(Guid marketplaceAppId, CancellationToken ct = default);
    Task<Result> UninstallAsync(Guid marketplaceAppId, CancellationToken ct = default);
}

/// <summary>Usage quotidien des clés d'API de l'entreprise (portail développeur §47-48).</summary>
public interface IDeveloperUsageQuery
{
    Task<IReadOnlyList<DeveloperKeyUsageDto>> UsageAsync(int days, CancellationToken ct = default);
}
