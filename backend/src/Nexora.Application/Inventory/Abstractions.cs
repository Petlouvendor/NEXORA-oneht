using Nexora.Domain.Inventory;

namespace Nexora.Application.Inventory;

/// <summary>Demande de mouvement de stock (Phase 5 §13). Un seul écrivain : <see cref="IStockLedger"/>.</summary>
public sealed record StockMovementRequest(
    Guid ProductId,
    Guid LocationId,
    string MovementType,
    decimal SignedQuantity,
    decimal UnitCost,
    string? ReferenceType = null,
    Guid? ReferenceId = null,
    Guid? LotId = null,
    DateTimeOffset? OccurredAt = null,
    Guid? SourceOperationUuid = null);

public sealed record FefoPick(Guid LotId, string LotNumber, DateOnly? ExpirationDate, decimal Quantity);

/// <summary>
/// Point d'écriture unique des stocks : insère les mouvements ; le solde, le coût moyen
/// pondéré et le stock par lot sont maintenus par les triggers PostgreSQL (Phase 2 §36).
/// </summary>
public interface IStockLedger
{
    Task PostAsync(StockMovementRequest movement, CancellationToken ct = default);
    Task PostManyAsync(IEnumerable<StockMovementRequest> movements, CancellationToken ct = default);

    Task<decimal> OnHandAsync(Guid productId, Guid locationId, CancellationToken ct = default);
    Task<decimal> AvailableAsync(Guid productId, Guid locationId, CancellationToken ct = default);
    Task<decimal> AverageCostAsync(Guid productId, Guid locationId, CancellationToken ct = default);

    /// <summary>Verrouille la ligne de solde (SELECT … FOR UPDATE) et renvoie la quantité disponible.
    /// Sérialise les caisses concurrentes sur un même produit/emplacement (§18).</summary>
    Task<decimal> LockAvailableAsync(Guid productId, Guid locationId, CancellationToken ct = default);

    /// <summary>Sélection FEFO des lots à sortir (First Expired, First Out — §18).</summary>
    Task<IReadOnlyList<FefoPick>> PickFefoAsync(Guid productId, Guid locationId, decimal quantity, CancellationToken ct = default);

    Task ReserveAsync(Guid productId, Guid locationId, decimal quantity, CancellationToken ct = default);
    Task ReleaseAsync(Guid productId, Guid locationId, decimal quantity, CancellationToken ct = default);
}

public sealed record ReorderSuggestion(
    Guid ProductId, string Sku, string ProductName, Guid LocationId,
    decimal OnHand, decimal Available, decimal ReorderPoint, decimal SuggestedQuantity,
    Guid? PreferredSupplierId);
