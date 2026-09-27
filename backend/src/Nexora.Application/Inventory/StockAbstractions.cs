using Nexora.Contracts.Inventory;
using Nexora.Domain.Common;

namespace Nexora.Application.Inventory;

/// <summary>Cycle de vie des lots — blocage, rappel, balayage des expirations (Phase 24 §13-15).</summary>
public interface ILotService
{
    Task<Result> BlockAsync(Guid lotId, string reason, CancellationToken ct = default);
    Task<Result> UnblockAsync(Guid lotId, string reason, CancellationToken ct = default);
    Task<Result> RecallAsync(Guid lotId, string reference, string reason, CancellationToken ct = default);

    /// <summary>Lots en approche de péremption ou expirés, avec le stock encore détenu (§13, §40).</summary>
    Task<IReadOnlyList<ExpiringLotDto>> ExpiringAsync(int? withinDays, CancellationToken ct = default);

    /// <summary>Passe à l'état « expiré » tout lot actif dont la date est dépassée et qui détient
    /// encore du stock, sort ce stock (mouvement <c>expiry</c>) et comptabilise la perte (§13).
    /// Idempotent — sans effet si aucun lot n'est concerné. Exécuté par le planificateur (Phase 16).</summary>
    Task<Result<LotSweepResultDto>> SweepExpiredAsync(CancellationToken ct = default);
}

/// <summary>Lectures stock : soldes, mouvements, lots, alertes, entrepôts (Phase 24 §38-41).</summary>
public interface IStockQueries
{
    Task<IReadOnlyList<StockBalanceDto>> BalancesAsync(Guid? locationId, Guid? productId, bool onlyPositive, CancellationToken ct = default);
    Task<IReadOnlyList<StockMovementDto>> MovementsAsync(Guid? productId, Guid? locationId, DateTimeOffset? from, DateTimeOffset? to, int limit, int offset, CancellationToken ct = default);
    Task<IReadOnlyList<LotDto>> LotsAsync(Guid? productId, Guid? locationId, string? status, int? expiringWithinDays, CancellationToken ct = default);
    Task<IReadOnlyList<StockAlertDto>> AlertsAsync(CancellationToken ct = default);
    Task<IReadOnlyList<WarehouseDto>> WarehousesAsync(CancellationToken ct = default);
    Task<IReadOnlyList<LocationDto>> LocationsAsync(Guid? warehouseId, CancellationToken ct = default);
}

/// <summary>Création d'entrepôts et d'emplacements (Phase 24 §4-6, §42 « modifier paramètres »).</summary>
public interface IWarehouseService
{
    Task<Result<Guid>> CreateWarehouseAsync(CreateWarehouseRequest request, CancellationToken ct = default);
    Task<Result<Guid>> CreateLocationAsync(CreateLocationRequest request, CancellationToken ct = default);
}

/// <summary>Politique de disponibilité du stock (Phase 24 §32). Le stock négatif est interdit
/// par défaut ; l'autorisation vient du paramètre <c>stock.negative_stock_allowed</c>.</summary>
public interface IStockAvailabilityPolicy
{
    Task<bool> AllowNegativeAsync(CancellationToken ct = default);
}
