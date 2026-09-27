using Nexora.Application.Abstractions;
using Nexora.Application.Finance.Engine;
using Nexora.Application.Finance.Events;
using Nexora.Domain.Common;
using Nexora.Domain.Inventory;

namespace Nexora.Application.Inventory;

public sealed record TransferLineInput(Guid ProductId, decimal Quantity, Guid? LotId);
public sealed record CreateTransferCommand(Guid SourceLocationId, Guid DestLocationId, IReadOnlyList<TransferLineInput> Lines, string? Notes);
public sealed record ReceiveTransferLineInput(Guid TransferLineId, decimal Quantity);

public sealed record CountLineInput(Guid ProductId, Guid? LotId, decimal CountedQuantity);
public sealed record LossLineInput(Guid ProductId, decimal Quantity, Guid? LotId);
public sealed record RecordLossCommand(
    Guid OperationUuid, Guid LocationId, string LossType, string Reason,
    IReadOnlyList<LossLineInput> Lines, DateTimeOffset? OccurredAt);

public interface IInventoryOpsStore
{
    Task<string> NextNumberAsync(string documentType, CancellationToken ct = default);
    Task AddTransferAsync(StockTransfer transfer, CancellationToken ct = default);
    Task<StockTransfer?> GetTransferAsync(Guid id, CancellationToken ct = default);
    Task AddCountAsync(InventoryCount count, CancellationToken ct = default);
    Task<InventoryCount?> GetCountAsync(Guid id, CancellationToken ct = default);
    Task<decimal> SystemQuantityAsync(Guid productId, Guid locationId, CancellationToken ct = default);
    Task<decimal> AverageCostAsync(Guid productId, Guid locationId, CancellationToken ct = default);
    Task AddAdjustmentAsync(StockAdjustment adjustment, CancellationToken ct = default);
    Task SetCountValidatedAsync(Guid countId, Guid? userId, CancellationToken ct = default);
    Task SetTransferStatusAsync(Guid transferId, string status, DateTimeOffset? shipped, DateTimeOffset? received, CancellationToken ct = default);
}

/// <summary>Transferts, inventaires, pertes (Phase 5 §14, §15, §16).</summary>
public sealed class InventoryOperationsService(
    IInventoryOpsStore store,
    IStockLedger stock,
    IFinancialEngine engine,
    IUnitOfWork uow,
    ITenantContext tenant,
    IDateTimeProvider clock)
{
    private Guid Company => tenant.ActiveCompanyId
        ?? throw new InvalidOperationException("Aucune entreprise active.");

    // --- Transfert (§14) : A ↓ → en transit → B ↑ ---------------------
    public async Task<Result<Guid>> CreateAndShipTransferAsync(CreateTransferCommand c, CancellationToken ct = default)
    {
        if (c.SourceLocationId == c.DestLocationId)
            return Error.Validation("inventory.transfer_same_location", "Source et destination identiques.");
        if (c.Lines.Count == 0) return Error.Validation("inventory.no_lines", "Aucune ligne à transférer.");

        return await uow.ExecuteInTransactionAsync<Result<Guid>>(async token =>
        {
            var now = clock.UtcNow;
            var transfer = new StockTransfer
            {
                Id = Guid.NewGuid(),
                CompanyId = Company,
                DocumentNumber = await store.NextNumberAsync("stock_transfer", token),
                SourceLocationId = c.SourceLocationId,
                DestLocationId = c.DestLocationId,
                Status = "in_transit",
                ShippedAt = now,
                Notes = c.Notes,
                CreatedBy = tenant.UserId,
            };
            var movements = new List<StockMovementRequest>();
            foreach (var l in c.Lines)
            {
                if (l.Quantity <= 0) throw new InvalidOperationException("Quantité de transfert invalide.");
                var available = await stock.AvailableAsync(l.ProductId, c.SourceLocationId, token);
                if (l.Quantity > available)
                    throw new InvalidOperationException($"Stock disponible insuffisant à la source ({available}).");

                var cost = await store.AverageCostAsync(l.ProductId, c.SourceLocationId, token);
                transfer.Lines.Add(new StockTransferLine
                {
                    Id = Guid.NewGuid(), StockTransferId = transfer.Id, CompanyId = Company,
                    ProductId = l.ProductId, LotId = l.LotId, Quantity = l.Quantity,
                    QuantityShipped = l.Quantity, UnitCost = cost,
                });
                movements.Add(new StockMovementRequest(l.ProductId, c.SourceLocationId, MovementType.TransferOut,
                    -l.Quantity, cost, "stock_transfer", transfer.Id, l.LotId, now));
            }
            await store.AddTransferAsync(transfer, token);
            await stock.PostManyAsync(movements, token);
            return Result.Success(transfer.Id);
        }, ct);
    }

    public async Task<Result> ReceiveTransferAsync(Guid transferId, IReadOnlyList<ReceiveTransferLineInput> lines, CancellationToken ct = default)
    {
        var transfer = await store.GetTransferAsync(transferId, ct);
        if (transfer is null) return Error.NotFound("inventory.transfer_not_found", "Transfert introuvable.");
        if (transfer.Status is "completed" or "cancelled")
            return Error.Conflict("inventory.transfer_closed", "Transfert déjà clôturé.");

        return await uow.ExecuteInTransactionAsync<Result>(async token =>
        {
            var now = clock.UtcNow;
            var movements = new List<StockMovementRequest>();
            var fully = true;
            foreach (var line in transfer.Lines)
            {
                var input = lines.FirstOrDefault(x => x.TransferLineId == line.Id);
                var qty = input?.Quantity ?? (line.QuantityShipped - line.QuantityReceived);
                if (qty <= 0) { if (line.QuantityReceived < line.QuantityShipped) fully = false; continue; }
                line.QuantityReceived += qty;
                if (line.QuantityReceived < line.QuantityShipped) fully = false;
                movements.Add(new StockMovementRequest(line.ProductId, transfer.DestLocationId, MovementType.TransferIn,
                    qty, line.UnitCost, "stock_transfer", transfer.Id, line.LotId, now));
            }
            await stock.PostManyAsync(movements, token);
            await store.SetTransferStatusAsync(transfer.Id, fully ? "completed" : "partially_received", null, fully ? now : null, token);
            return Result.Success();
        }, ct);
    }

    // --- Inventaire (§15) : comptage → écart → ajustement -------------
    public async Task<Result<Guid>> StartCountAsync(Guid locationId, string countType, IReadOnlyList<Guid> productIds, CancellationToken ct = default)
    {
        var count = new InventoryCount
        {
            Id = Guid.NewGuid(),
            CompanyId = Company,
            InventoryLocationId = locationId,
            DocumentNumber = await store.NextNumberAsync("inventory_count", ct),
            CountType = countType,
            Status = "counting",
            StartedAt = clock.UtcNow,
        };
        foreach (var pid in productIds.Distinct())
        {
            count.Lines.Add(new InventoryCountLine
            {
                Id = Guid.NewGuid(), InventoryCountId = count.Id, CompanyId = Company,
                ProductId = pid,
                SystemQuantity = await store.SystemQuantityAsync(pid, locationId, ct),
                UnitCost = await store.AverageCostAsync(pid, locationId, ct),
            });
        }
        await store.AddCountAsync(count, ct);
        await uow.SaveChangesAsync(ct);
        return Result.Success(count.Id);
    }

    public async Task<Result<Guid?>> ValidateCountAsync(Guid countId, IReadOnlyList<CountLineInput> counted, Guid operationUuid, CancellationToken ct = default)
    {
        var count = await store.GetCountAsync(countId, ct);
        if (count is null) return Error.NotFound("inventory.count_not_found", "Inventaire introuvable.");
        if (count.Status == "validated") return Error.Conflict("inventory.count_validated", "Inventaire déjà validé.");

        return await uow.ExecuteInTransactionAsync<Result<Guid?>>(async token =>
        {
            var now = clock.UtcNow;
            var adjustment = new StockAdjustment
            {
                Id = Guid.NewGuid(),
                CompanyId = Company,
                InventoryLocationId = count.InventoryLocationId,
                DocumentNumber = await store.NextNumberAsync("stock_adjustment", token),
                AdjustmentTypeValue = AdjustmentType.Count,
                Status = "posted",
                Reason = $"Inventaire {count.DocumentNumber}",
                OccurredAt = now,
                SourceType = "inventory_count",
                SourceId = count.Id,
                CreatedBy = tenant.UserId,
            };

            decimal valuationDelta = 0;
            var movements = new List<StockMovementRequest>();
            foreach (var line in count.Lines)
            {
                var input = counted.FirstOrDefault(x => x.ProductId == line.ProductId && x.LotId == line.LotId);
                if (input is null) continue;
                var variance = input.CountedQuantity - line.SystemQuantity;
                if (variance == 0) continue;

                adjustment.Lines.Add(new StockAdjustmentLine
                {
                    Id = Guid.NewGuid(), StockAdjustmentId = adjustment.Id, CompanyId = Company,
                    ProductId = line.ProductId, LotId = line.LotId,
                    QuantityDelta = variance, UnitCost = line.UnitCost,
                });
                valuationDelta += variance * line.UnitCost;
                movements.Add(new StockMovementRequest(line.ProductId, count.InventoryLocationId,
                    MovementType.Count, variance, line.UnitCost, "stock_adjustment", adjustment.Id, line.LotId, now));
            }

            if (adjustment.Lines.Count == 0)
            {
                await store.SetCountValidatedAsync(count.Id, tenant.UserId, token);
                return Result.Success<Guid?>(null);
            }

            await store.AddAdjustmentAsync(adjustment, token);
            await stock.PostManyAsync(movements, token);
            await store.SetCountValidatedAsync(count.Id, tenant.UserId, token);

            var post = await engine.HandleAsync(new InventoryRevaluationRecorded(
                Company, operationUuid, now, adjustment.Id, new Money(valuationDelta, "HTG")) { UserId = tenant.UserId }, token);
            if (!post.IsSuccess) throw new InvalidOperationException(post.Error!.Message);

            return Result.Success<Guid?>(adjustment.Id);
        }, ct);
    }

    // --- Perte / casse / expiration (§16) ---------------------------
    public async Task<Result<Guid>> RecordLossAsync(RecordLossCommand c, CancellationToken ct = default)
    {
        if (c.Lines.Count == 0) return Error.Validation("inventory.no_lines", "Aucune ligne.");

        return await uow.ExecuteInTransactionAsync<Result<Guid>>(async token =>
        {
            var now = c.OccurredAt ?? clock.UtcNow;
            var adjustment = new StockAdjustment
            {
                Id = Guid.NewGuid(),
                CompanyId = Company,
                InventoryLocationId = c.LocationId,
                DocumentNumber = await store.NextNumberAsync("stock_adjustment", token),
                AdjustmentTypeValue = c.LossType,
                Status = "posted",
                Reason = c.Reason,
                OccurredAt = now,
                CreatedBy = tenant.UserId,
            };

            decimal value = 0;
            var movements = new List<StockMovementRequest>();
            foreach (var l in c.Lines)
            {
                if (l.Quantity <= 0) throw new InvalidOperationException("Quantité de perte invalide.");
                var cost = await store.AverageCostAsync(l.ProductId, c.LocationId, token);
                value += l.Quantity * cost;
                adjustment.Lines.Add(new StockAdjustmentLine
                {
                    Id = Guid.NewGuid(), StockAdjustmentId = adjustment.Id, CompanyId = Company,
                    ProductId = l.ProductId, LotId = l.LotId, QuantityDelta = -l.Quantity, UnitCost = cost,
                });
                var mvType = c.LossType is "loss" or "breakage" or "expiry" or "found"
                    ? c.LossType : MovementType.Loss;
                movements.Add(new StockMovementRequest(l.ProductId, c.LocationId,
                    mvType, -l.Quantity, cost, "stock_adjustment", adjustment.Id, l.LotId, now));
            }

            await store.AddAdjustmentAsync(adjustment, token);
            await stock.PostManyAsync(movements, token);

            var post = await engine.HandleAsync(new InventoryWriteOffRecorded(
                Company, c.OperationUuid, now, adjustment.Id, new Money(value, "HTG"), c.LossType) { UserId = tenant.UserId }, token);
            if (!post.IsSuccess) throw new InvalidOperationException(post.Error!.Message);

            return Result.Success(adjustment.Id);
        }, ct);
    }
}
