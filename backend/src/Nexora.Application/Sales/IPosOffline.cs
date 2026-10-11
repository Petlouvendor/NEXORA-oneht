using Nexora.Contracts.Sales;
using Nexora.Domain.Common;

namespace Nexora.Application.Sales;

/// <summary>
/// Caisse hors ligne (finalisation produit, phase 6). L'appareil de caisse s'enregistre lui-même (plus de saisie
/// manuelle d'un identifiant technique) ; un responsable peut toujours le suspendre ou le révoquer depuis le centre
/// de synchronisation. Le profil hors ligne est téléchargé tant que le réseau est là.
/// </summary>
public interface IPosOfflineService
{
    Task<Result<PosDeviceDto>> EnrollAsync(EnrollPosDeviceRequest request, CancellationToken ct = default);
    Task<PosOfflineProfileDto> ProfileAsync(Guid? inventoryLocationId, CancellationToken ct = default);
}
