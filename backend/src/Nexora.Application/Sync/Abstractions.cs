using Nexora.Contracts.Mobile;
using Nexora.Contracts.Sync;
using Nexora.Domain.Common;

namespace Nexora.Application.Sync;

/// <summary>
/// Moteur de synchronisation offline (Phase 10 §11-16, §30). Le mode de déploiement ne
/// change pas les règles métier : une opération offline est rejouée via le même parcours
/// de vente que l'API centrale, avec la même idempotence (operation_uuid).
/// </summary>
public interface ISyncService
{
    /// <summary>Reçoit un lot d'opérations offline, les traite de façon idempotente, renvoie un résultat par opération (§13-14).</summary>
    Task<Result<PushBatchResult>> PushBatchAsync(PushBatchRequest request, CancellationToken ct = default);

    /// <summary>Centre de synchronisation : file, échecs, conflits, dernière synchro par appareil (§30).</summary>
    Task<SyncCenterDto> SyncCenterAsync(CancellationToken ct = default);

    /// <summary>Résout un conflit — la décision et son auteur sont journalisés (§15).</summary>
    Task<Result> ResolveConflictAsync(Guid conflictId, ResolveConflictRequest request, CancellationToken ct = default);
}

/// <summary>Registre des appareils autorisés à synchroniser (Phase 10 §12, §31).</summary>
public interface IDeviceRegistryService
{
    Task<Result<Guid>> RegisterAsync(RegisterDeviceRequest request, CancellationToken ct = default);
    Task<IReadOnlyList<SyncDeviceDto>> ListAsync(CancellationToken ct = default);
    Task<Result> RevokeAsync(Guid deviceId, RevokeDeviceRequest request, CancellationToken ct = default);
    Task<Result> HeartbeatAsync(Guid deviceId, CancellationToken ct = default);

    /// <summary>Jeton push (Phase 44 §20, §32) — appareil personnel (<c>device_type = 'mobile'</c>) uniquement.</summary>
    Task<Result> RegisterPushTokenAsync(Guid deviceId, RegisterPushTokenRequest request, CancellationToken ct = default);
}

/// <summary>Politique de fonctionnement hors connexion, configurable par entreprise (Phase 10 §10, §17).</summary>
public interface IOfflinePolicyService
{
    Task<OfflinePolicyDto> GetAsync(CancellationToken ct = default);
    Task<Result> SetAsync(SetOfflinePolicyRequest request, CancellationToken ct = default);
}
