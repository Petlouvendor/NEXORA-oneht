using Nexora.Contracts.Sectors;
using Nexora.Domain.Common;

namespace Nexora.Application.Sectors;

/// <summary>
/// Configuration sectorielle (Phase 8 §3-4, §27-29). Active/désactive des modules et
/// des activités sans jamais supprimer de données existantes : la désactivation borne
/// seulement les nouveaux processus (§29).
/// </summary>
public interface ISectorConfigService
{
    Task<SectorConfig> GetAsync(CancellationToken ct = default);
    Task<Result> SetActivitiesAsync(SetActivitiesRequest request, CancellationToken ct = default);
    Task<Result> SetModuleAsync(SetModuleRequest request, CancellationToken ct = default);
    Task<bool> IsModuleEnabledAsync(string moduleKey, CancellationToken ct = default);
    Task<Result> SetTerminologyAsync(SetTerminologyRequest request, CancellationToken ct = default);
}

/// <summary>
/// Moteur de champs personnalisés (Phase 8 §16) : « ENTITY + CUSTOM_FIELDS → adaptation
/// sans modifier le Core ». Les valeurs sont typées et validées (obligatoire, type).
/// </summary>
public interface ICustomFieldService
{
    Task<Result<Guid>> DefineAsync(DefineCustomFieldRequest request, CancellationToken ct = default);
    Task<IReadOnlyList<CustomFieldDto>> ListAsync(string entityType, CancellationToken ct = default);
    Task<Result> SetValuesAsync(SetCustomFieldValuesRequest request, CancellationToken ct = default);
    Task<IReadOnlyList<CustomFieldValueDto>> GetValuesAsync(string entityType, Guid entityId, CancellationToken ct = default);
}
