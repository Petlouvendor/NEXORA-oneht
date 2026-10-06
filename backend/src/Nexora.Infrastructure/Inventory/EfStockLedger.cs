using Microsoft.EntityFrameworkCore;
using Nexora.Application.Abstractions;
using Nexora.Application.Inventory;
using Nexora.Domain.Inventory;
using Nexora.Infrastructure.Persistence;
using Nexora.Domain.Common;

namespace Nexora.Infrastructure.Inventory;

/// <summary>
/// Écrivain unique des mouvements de stock. Insère dans <c>stock_movement</c> ;
/// le solde, le coût moyen pondéré et le stock par lot sont maintenus par les triggers
/// PostgreSQL (Phase 2 §36). Les réservations agissent directement sur <c>stock_balance</c>.
/// </summary>
public sealed class EfStockLedger(NexoraDbContext db, ITenantContext tenant) : IStockLedger
{
    private Guid Company => tenant.ActiveCompanyId
        ?? throw TenantErrors.ActiveCompanyRequired();

    public async Task PostAsync(StockMovementRequest m, CancellationToken ct = default)
        => await PostManyAsync([m], ct);

    public async Task PostManyAsync(IEnumerable<StockMovementRequest> movements, CancellationToken ct = default)
    {
        foreach (var m in movements)
        {
            if (m.SignedQuantity == 0m)
                throw new InvalidOperationException("Un mouvement de stock ne peut être de quantité nulle.");
            await db.StockMovements.AddAsync(new StockMovement
            {
                Id = Guid.NewGuid(),
                CompanyId = Company,
                ProductId = m.ProductId,
                InventoryLocationId = m.LocationId,
                LotId = m.LotId,
                MovementTypeValue = m.MovementType,
                Quantity = m.SignedQuantity,
                UnitCost = m.UnitCost,
                ReferenceType = m.ReferenceType,
                ReferenceId = m.ReferenceId,
                OccurredAt = m.OccurredAt ?? DateTimeOffset.UtcNow,
                UserId = tenant.UserId,
                SourceOperationUuid = m.SourceOperationUuid,
            }, ct);
        }
    }

    public async Task<decimal> OnHandAsync(Guid productId, Guid locationId, CancellationToken ct = default)
        => await db.StockBalances.AsNoTracking()
            .Where(s => s.ProductId == productId && s.InventoryLocationId == locationId)
            .Select(s => (decimal?)s.QuantityOnHand).FirstOrDefaultAsync(ct) ?? 0m;

    public async Task<decimal> AvailableAsync(Guid productId, Guid locationId, CancellationToken ct = default)
        => await db.StockBalances.AsNoTracking()
            .Where(s => s.ProductId == productId && s.InventoryLocationId == locationId)
            .Select(s => (decimal?)(s.QuantityOnHand - s.QuantityReserved)).FirstOrDefaultAsync(ct) ?? 0m;

    public async Task<decimal> AverageCostAsync(Guid productId, Guid locationId, CancellationToken ct = default)
        => await db.StockBalances.AsNoTracking()
            .Where(s => s.ProductId == productId && s.InventoryLocationId == locationId)
            .Select(s => (decimal?)s.AverageCost).FirstOrDefaultAsync(ct) ?? 0m;

    public async Task<decimal> LockAvailableAsync(Guid productId, Guid locationId, CancellationToken ct = default)
    {
        var rows = await db.Database.SqlQuery<decimal>($"""
            SELECT COALESCE(
                (SELECT quantity_on_hand - quantity_reserved FROM nexora.stock_balance
                 WHERE product_id = {productId} AND inventory_location_id = {locationId}
                 FOR UPDATE), 0) AS "Value"
            """).ToListAsync(ct);
        return rows[0];
    }

    public async Task<IReadOnlyList<FefoPick>> PickFefoAsync(
        Guid productId, Guid locationId, decimal quantity, CancellationToken ct = default)
    {
        var candidates = await (
            from ls in db.LotStocks.AsNoTracking()
            join lot in db.Lots.AsNoTracking() on ls.LotId equals lot.Id
            where lot.ProductId == productId
                  && ls.InventoryLocationId == locationId
                  && ls.QuantityOnHand - ls.QuantityReserved > 0
                  && lot.Status == "active"
            orderby lot.ExpirationDate ?? DateOnly.MaxValue, lot.LotNumber
            select new { ls.LotId, lot.LotNumber, lot.ExpirationDate, Available = ls.QuantityOnHand - ls.QuantityReserved })
            .ToListAsync(ct);

        var picks = new List<FefoPick>();
        var remaining = quantity;
        foreach (var c in candidates)
        {
            if (remaining <= 0) break;
            var take = Math.Min(remaining, c.Available);
            picks.Add(new FefoPick(c.LotId, c.LotNumber, c.ExpirationDate, take));
            remaining -= take;
        }
        if (remaining > 0)
            throw new InvalidOperationException(
                $"Stock lot insuffisant pour {productId} : manque {remaining} (FEFO, §18).");
        return picks;
    }

    public async Task ReserveAsync(Guid productId, Guid locationId, decimal quantity, CancellationToken ct = default)
        => await AdjustReservationAsync(productId, locationId, quantity, ct);

    public async Task ReleaseAsync(Guid productId, Guid locationId, decimal quantity, CancellationToken ct = default)
        => await AdjustReservationAsync(productId, locationId, -quantity, ct);

    private async Task AdjustReservationAsync(Guid productId, Guid locationId, decimal delta, CancellationToken ct)
    {
        var affected = await db.StockBalances
            .Where(s => s.ProductId == productId && s.InventoryLocationId == locationId)
            .ExecuteUpdateAsync(u => u.SetProperty(s => s.QuantityReserved, s => s.QuantityReserved + delta), ct);
        if (affected == 0 && delta > 0)
        {
            await db.StockBalances.AddAsync(new StockBalance
            {
                Id = Guid.NewGuid(), ProductId = productId, InventoryLocationId = locationId,
                CompanyId = Company, QuantityOnHand = 0m, QuantityReserved = delta, AverageCost = 0m,
            }, ct);
        }
    }
}
