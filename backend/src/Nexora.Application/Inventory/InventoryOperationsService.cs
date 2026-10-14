using Nexora.Application.Abstractions;
using Nexora.Application.Finance.Engine;
using Nexora.Application.Finance.Events;
using Nexora.Domain.Common;
using Nexora.Domain.Inventory;

namespace Nexora.Application.Inventory;

public sealed record TransferLineInput(Guid ProductId, decimal Quantity, Guid? LotId);
public sealed record CreateTransferCommand(Guid SourceLocationId, Guid DestLocationId, IReadOnlyList<TransferLineInput> Lines, string? Notes);
public sealed record ReceiveTransferLineInput(Guid TransferLineId, decimal Quantity);

public sealed record CountLineInput(Guid ProductId, Guid? LotId, decimal CountedQuantity, string? Note = null);
public sealed record LossLineInput(Guid ProductId, decimal Quantity, Guid? LotId, string? Note = null);
public sealed record RecordLossCommand(
    Guid OperationUuid, Guid LocationId, string LossType, string Reason,
    IReadOnlyList<LossLineInput> Lines, DateTimeOffset? OccurredAt);

/// <summary>Ligne d'ajustement manuel : quantité signée (+ trouvé / correction à la hausse, − correction à la baisse).
/// <paramref name="UnitCost"/> : coût d'une entrée quand l'article n'a pas encore de coût moyen à l'emplacement.</summary>
public sealed record AdjustmentLineInput(Guid ProductId, decimal QuantityDelta, Guid? LotId, decimal? UnitCost, string? Note);

/// <summary>Ajustement manuel (§12 finalisation) : <c>correction</c> (erreur de comptage / saisie, ±) ou <c>found</c> (stock retrouvé, +).</summary>
public sealed record RecordAdjustmentCommand(
    Guid OperationUuid, Guid LocationId, string AdjustmentType, string Reason,
    IReadOnlyList<AdjustmentLineInput> Lines, DateTimeOffset? OccurredAt);

public interface IInventoryOpsStore
{
    Task<string> NextNumberAsync(string documentType, CancellationToken ct = default);
    Task AddTransferAsync(StockTransfer transfer, CancellationToken ct = default);
    Task<StockTransfer?> GetTransferAsync(Guid id, CancellationToken ct = default);
    Task AddCountAsync(InventoryCount count, CancellationToken ct = default);
    Task<InventoryCount?> GetCountAsync(Guid id, CancellationToken ct = default);
    Task<decimal> SystemQuantityAsync(Guid productId, Guid locationId, CancellationToken ct = default);
    /// <summary>Somme des mouvements (hors réservations) enregistrés entre deux instants sur un article à un emplacement.</summary>
    Task<decimal> MovementsBetweenAsync(Guid productId, Guid locationId, Guid? lotId, DateTimeOffset after, DateTimeOffset upTo,
        CancellationToken ct = default);
    /// <summary>Stock par lot à l'emplacement, pour un article suivi par lot (vide sinon).</summary>
    Task<IReadOnlyList<(Guid LotId, decimal Quantity)>> LotQuantitiesAsync(Guid productId, Guid locationId, CancellationToken ct = default);
    Task<decimal> AverageCostAsync(Guid productId, Guid locationId, CancellationToken ct = default);
    Task AddAdjustmentAsync(StockAdjustment adjustment, CancellationToken ct = default);
    Task SetCountValidatedAsync(Guid countId, Guid? userId, CancellationToken ct = default);
    Task SetTransferStatusAsync(Guid transferId, string status, DateTimeOffset? shipped, DateTimeOffset? received, CancellationToken ct = default);

    // --- Finalisation produit, phase 2 -------------------------------------------------
    /// <summary>Devise fonctionnelle de l'entreprise (valorisation des écarts et pertes).</summary>
    Task<string> FunctionalCurrencyAsync(CancellationToken ct = default);
    Task<bool> LocationExistsAsync(Guid locationId, CancellationToken ct = default);
    Task<bool> ProductExistsAsync(Guid productId, CancellationToken ct = default);
    /// <summary>Produits à compter pour un inventaire complet : stock présent à l'emplacement,
    /// plus les produits stockés actifs qui y ont une règle de réapprovisionnement.</summary>
    Task<IReadOnlyList<Guid>> ProductsForFullCountAsync(Guid locationId, CancellationToken ct = default);
    Task SetCountStatusAsync(Guid countId, string status, string? notes, CancellationToken ct = default);
    Task SetAdjustmentJournalAsync(Guid adjustmentId, Guid journalEntryId, CancellationToken ct = default);
}

/// <summary>Transferts, inventaires, pertes et ajustements (Phase 5 §14-16 ; finalisation produit, phase 2).
/// Le stock ne bouge jamais sans mouvement tracé ; toute variation de valeur est comptabilisée.</summary>
public sealed class InventoryOperationsService(
    IInventoryOpsStore store,
    IStockLedger stock,
    IFinancialEngine engine,
    IUnitOfWork uow,
    ITenantContext tenant,
    IDateTimeProvider clock)
{
    private Guid Company => tenant.ActiveCompanyId
        ?? throw TenantErrors.ActiveCompanyRequired();

    /// <summary>Types de sortie de stock (valeur passée en charge).</summary>
    public static readonly string[] LossTypes = [AdjustmentType.Loss, AdjustmentType.Breakage, AdjustmentType.Expiry, AdjustmentType.Theft];
    /// <summary>Types d'ajustement manuel (écart valorisé en écart d'inventaire).</summary>
    public static readonly string[] AdjustmentTypes = [AdjustmentType.Correction, AdjustmentType.Found];

    /// <summary>
    /// Un refus levé dans la transaction (stock insuffisant, contrainte PostgreSQL traduite par l'unité de
    /// travail, écriture comptable refusée) devient une erreur métier explicite — jamais une erreur 500.
    /// Même convention que <c>SalesService</c> (code <c>…insufficient_stock</c> exploitable par la synchronisation).
    /// </summary>
    private static async Task<Result<T>> Guarded<T>(Func<Task<Result<T>>> operation)
    {
        try { return await operation(); }
        catch (InvalidOperationException ex)
        {
            return ex.Message.Contains("insuffisant", StringComparison.OrdinalIgnoreCase)
                ? Error.Conflict("inventory.insufficient_stock", ex.Message)
                : Error.Validation("inventory.operation_failed", ex.Message);
        }
    }

    private static async Task<Result> Guarded(Func<Task<Result>> operation)
    {
        try { return await operation(); }
        catch (InvalidOperationException ex)
        {
            return ex.Message.Contains("insuffisant", StringComparison.OrdinalIgnoreCase)
                ? Error.Conflict("inventory.insufficient_stock", ex.Message)
                : Error.Validation("inventory.operation_failed", ex.Message);
        }
    }

    // ======================================================================== Transferts

    /// <summary>Transfert (§14) : sortie immédiate de la source (A ↓), marchandise « en transit »,
    /// puis réception (B ↑) complète ou partielle.</summary>
    public async Task<Result<Guid>> CreateAndShipTransferAsync(CreateTransferCommand c, CancellationToken ct = default)
    {
        if (c.SourceLocationId == c.DestLocationId)
            return Error.Validation("inventory.transfer_same_location", "Source et destination identiques.");
        if (c.Lines.Count == 0) return Error.Validation("inventory.no_lines", "Aucune ligne à transférer.");
        if (!await store.LocationExistsAsync(c.SourceLocationId, ct) || !await store.LocationExistsAsync(c.DestLocationId, ct))
            return Error.Validation("inventory.location_unknown", "Emplacement inconnu pour cette entreprise.");
        if (c.Lines.Any(l => l.Quantity <= 0))
            return Error.Validation("inventory.quantity", "Chaque quantité transférée doit être positive.");
        if (c.Lines.GroupBy(l => (l.ProductId, l.LotId)).Any(g => g.Count() > 1))
            return Error.Validation("inventory.duplicate_line", "Un même article (et lot) ne peut figurer qu'une fois.");

        return await Guarded(() => uow.ExecuteInTransactionAsync<Result<Guid>>(async token =>
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
        }, ct));
    }

    /// <summary>Réception (complète si <paramref name="lines"/> est vide). Jamais plus que le reste en transit :
    /// recevoir davantage créerait du stock qui n'a jamais quitté la source.</summary>
    public async Task<Result> ReceiveTransferAsync(Guid transferId, IReadOnlyList<ReceiveTransferLineInput> lines, CancellationToken ct = default)
    {
        var transfer = await store.GetTransferAsync(transferId, ct);
        if (transfer is null) return Error.NotFound("inventory.transfer_not_found", "Transfert introuvable.");
        if (transfer.Status is "completed" or "cancelled")
            return Error.Conflict("inventory.transfer_closed", "Transfert déjà clôturé.");
        if (lines.Any(x => transfer.Lines.All(l => l.Id != x.TransferLineId)))
            return Error.Validation("inventory.transfer_line_unknown", "Ligne de transfert inconnue.");
        foreach (var x in lines)
        {
            var line = transfer.Lines.First(l => l.Id == x.TransferLineId);
            var remaining = line.QuantityShipped - line.QuantityReceived;
            if (x.Quantity < 0)
                return Error.Validation("inventory.quantity", "Une quantité reçue ne peut être négative.");
            if (x.Quantity > remaining)
                return Error.Validation("inventory.transfer_over_receipt",
                    $"Quantité reçue ({x.Quantity}) supérieure au reste en transit ({remaining}).");
        }

        return await Guarded(() => uow.ExecuteInTransactionAsync<Result>(async token =>
        {
            var now = clock.UtcNow;
            var movements = new List<StockMovementRequest>();
            var fully = true;
            foreach (var line in transfer.Lines)
            {
                var input = lines.FirstOrDefault(x => x.TransferLineId == line.Id);
                var qty = lines.Count == 0 ? line.QuantityShipped - line.QuantityReceived : input?.Quantity ?? 0m;
                if (qty > 0)
                {
                    line.QuantityReceived += qty;
                    movements.Add(new StockMovementRequest(line.ProductId, transfer.DestLocationId, MovementType.TransferIn,
                        qty, line.UnitCost, "stock_transfer", transfer.Id, line.LotId, now));
                }
                if (line.QuantityReceived < line.QuantityShipped) fully = false;
            }
            if (movements.Count == 0) return Error.Validation("inventory.no_lines", "Aucune quantité reçue.");
            await stock.PostManyAsync(movements, token);
            await store.SetTransferStatusAsync(transfer.Id, fully ? "completed" : "partially_received", null, fully ? now : null, token);
            return Result.Success();
        }, ct));
    }

    /// <summary>
    /// Clôture d'un transfert partiellement reçu : le reste non reçu est constaté comme perte en transit
    /// (document d'ajustement « loss » rattaché au transfert, valeur passée en charge). Aucun mouvement
    /// de stock : la marchandise a déjà quitté la source et n'est entrée nulle part.
    /// </summary>
    public async Task<Result<Guid?>> CloseTransferAsync(Guid transferId, string reason, Guid operationUuid, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(reason))
            return Error.Validation("inventory.reason_required", "Un motif est obligatoire pour clôturer avec un écart.");
        var transfer = await store.GetTransferAsync(transferId, ct);
        if (transfer is null) return Error.NotFound("inventory.transfer_not_found", "Transfert introuvable.");
        if (transfer.Status is "completed" or "cancelled")
            return Error.Conflict("inventory.transfer_closed", "Transfert déjà clôturé.");

        var missing = transfer.Lines.Where(l => l.QuantityShipped > l.QuantityReceived).ToList();
        var currency = await store.FunctionalCurrencyAsync(ct);
        return await Guarded(() => uow.ExecuteInTransactionAsync<Result<Guid?>>(async token =>
        {
            var now = clock.UtcNow;
            Guid? adjustmentId = null;
            if (missing.Count > 0)
            {
                var adjustment = new StockAdjustment
                {
                    Id = Guid.NewGuid(), CompanyId = Company, InventoryLocationId = transfer.DestLocationId,
                    DocumentNumber = await store.NextNumberAsync("stock_adjustment", token),
                    AdjustmentTypeValue = AdjustmentType.Loss, Status = "posted",
                    Reason = $"Écart de transfert {transfer.DocumentNumber} : {reason.Trim()}",
                    OccurredAt = now, SourceType = "stock_transfer", SourceId = transfer.Id, CreatedBy = tenant.UserId,
                };
                decimal value = 0;
                foreach (var l in missing)
                {
                    var qty = l.QuantityShipped - l.QuantityReceived;
                    value += qty * l.UnitCost;
                    adjustment.Lines.Add(new StockAdjustmentLine
                    {
                        Id = Guid.NewGuid(), StockAdjustmentId = adjustment.Id, CompanyId = Company,
                        ProductId = l.ProductId, LotId = l.LotId, QuantityDelta = -qty, UnitCost = l.UnitCost,
                        Note = "Non reçu à destination",
                    });
                }
                await store.AddAdjustmentAsync(adjustment, token);
                if (value != 0)
                {
                    var post = await engine.HandleAsync(new InventoryWriteOffRecorded(
                        Company, operationUuid, now, adjustment.Id, new Money(value, currency), "transfer_shortage") { UserId = tenant.UserId }, token);
                    if (!post.IsSuccess) throw new InvalidOperationException(post.Error!.Message);
                    await store.SetAdjustmentJournalAsync(adjustment.Id, post.Value!.JournalEntryId, token);
                }
                adjustmentId = adjustment.Id;
            }
            await store.SetTransferStatusAsync(transfer.Id, "completed", null, now, token);
            return Result.Success(adjustmentId);
        }, ct));
    }

    /// <summary>Annulation d'un transfert dont rien n'a encore été reçu : la marchandise revient à la source.</summary>
    public async Task<Result> CancelTransferAsync(Guid transferId, string reason, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(reason))
            return Error.Validation("inventory.reason_required", "Un motif d'annulation est obligatoire.");
        var transfer = await store.GetTransferAsync(transferId, ct);
        if (transfer is null) return Error.NotFound("inventory.transfer_not_found", "Transfert introuvable.");
        if (transfer.Status is "completed" or "cancelled")
            return Error.Conflict("inventory.transfer_closed", "Transfert déjà clôturé.");
        if (transfer.Lines.Any(l => l.QuantityReceived > 0))
            return Error.Conflict("inventory.transfer_partially_received",
                "Une partie a déjà été reçue : clôturez le transfert avec écart au lieu de l'annuler.");

        return await Guarded(() => uow.ExecuteInTransactionAsync<Result>(async token =>
        {
            var now = clock.UtcNow;
            var movements = transfer.Lines.Where(l => l.QuantityShipped > 0).Select(l => new StockMovementRequest(
                l.ProductId, transfer.SourceLocationId, MovementType.TransferIn, l.QuantityShipped, l.UnitCost,
                "stock_transfer", transfer.Id, l.LotId, now)).ToList();
            if (movements.Count > 0) await stock.PostManyAsync(movements, token);
            transfer.Notes = string.IsNullOrWhiteSpace(transfer.Notes) ? $"Annulé : {reason.Trim()}" : $"{transfer.Notes} — Annulé : {reason.Trim()}";
            await store.SetTransferStatusAsync(transfer.Id, "cancelled", null, null, token);
            return Result.Success();
        }, ct));
    }

    // ======================================================================== Inventaires

    /// <summary>Ouverture d'un inventaire (§15) : fige la quantité système de chaque article. Inventaire
    /// complet sans liste : tous les articles présents ou suivis à l'emplacement.</summary>
    public async Task<Result<Guid>> StartCountAsync(Guid locationId, string countType, IReadOnlyList<Guid> productIds, CancellationToken ct = default)
    {
        if (countType is not ("full" or "partial" or "cycle"))
            return Error.Validation("inventory.count_type", "Type d'inventaire attendu : full, partial ou cycle.");
        if (!await store.LocationExistsAsync(locationId, ct))
            return Error.Validation("inventory.location_unknown", "Emplacement inconnu pour cette entreprise.");
        var products = productIds.Distinct().ToList();
        if (products.Count == 0 && countType == "full")
            products = [.. await store.ProductsForFullCountAsync(locationId, ct)];
        if (products.Count == 0)
            return Error.Validation("inventory.no_lines", "Aucun article à compter.");
        foreach (var pid in products)
            if (!await store.ProductExistsAsync(pid, ct))
                return Error.Validation("inventory.product_unknown", "Article inconnu pour cette entreprise.");

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
        foreach (var pid in products)
        {
            var unitCost = await store.AverageCostAsync(pid, locationId, ct);
            // Article suivi par lot : une ligne par lot en stock (audit 2026-10, A-007) — sans lot, l'ajustement
            // modifiait le stock de l'article sans toucher ses lots, qui divergeaient.
            var lots = await store.LotQuantitiesAsync(pid, locationId, ct);
            if (lots.Count > 0)
            {
                foreach (var (lotId, quantity) in lots)
                    count.Lines.Add(new InventoryCountLine
                    {
                        Id = Guid.NewGuid(), InventoryCountId = count.Id, CompanyId = Company,
                        ProductId = pid, LotId = lotId, SystemQuantity = quantity, UnitCost = unitCost,
                    });
                continue;
            }
            count.Lines.Add(new InventoryCountLine
            {
                Id = Guid.NewGuid(), InventoryCountId = count.Id, CompanyId = Company,
                ProductId = pid,
                SystemQuantity = await store.SystemQuantityAsync(pid, locationId, ct),
                UnitCost = unitCost,
            });
        }
        await store.AddCountAsync(count, ct);
        await uow.SaveChangesAsync(ct);
        return Result.Success(count.Id);
    }

    /// <summary>Enregistre les quantités comptées (et leurs justifications) sans toucher au stock. Quand toutes
    /// les lignes sont comptées, l'inventaire passe « en revue ».</summary>
    public async Task<Result> SaveCountAsync(Guid countId, IReadOnlyList<CountLineInput> counted, CancellationToken ct = default)
    {
        var count = await store.GetCountAsync(countId, ct);
        if (count is null) return Error.NotFound("inventory.count_not_found", "Inventaire introuvable.");
        if (count.Status is not ("counting" or "review"))
            return Error.Conflict("inventory.count_closed", "Inventaire déjà validé ou annulé.");
        var error = ApplyCounts(count, counted);
        if (error is not null) return error;
        var status = count.Lines.All(l => l.CountedQuantity is not null) ? "review" : "counting";
        await uow.SaveChangesAsync(ct);
        if (status != count.Status) await store.SetCountStatusAsync(count.Id, status, null, ct);
        return Result.Success();
    }

    /// <summary>
    /// Validation (§15) : chaque écart compté devient une ligne d'ajustement et un mouvement « count » ;
    /// la variation de valeur est comptabilisée. Les lignes non comptées sont laissées telles quelles.
    /// <paramref name="requireJustification"/> : exige un commentaire de validation dès qu'un écart existe
    /// (appliqué par l'API ; les appels internes historiques ne le fournissent pas).
    /// </summary>
    public async Task<Result<Guid?>> ValidateCountAsync(Guid countId, IReadOnlyList<CountLineInput> counted, Guid operationUuid,
        CancellationToken ct = default, string? note = null, bool requireJustification = false)
    {
        var count = await store.GetCountAsync(countId, ct);
        if (count is null) return Error.NotFound("inventory.count_not_found", "Inventaire introuvable.");
        if (count.Status == "validated") return Error.Conflict("inventory.count_validated", "Inventaire déjà validé.");
        if (count.Status == "cancelled") return Error.Conflict("inventory.count_closed", "Inventaire annulé.");
        var countedBefore = count.Lines.ToDictionary(l => l.Id, l => l.CountedQuantity);
        var inputError = ApplyCounts(count, counted);
        if (inputError is not null) return inputError;

        // Quantité attendue au moment où chaque ligne a été comptée (audit 2026-10, A-007) : la quantité figée à
        // l'ouverture plus les mouvements enregistrés depuis (ventes, réceptions, transferts pendant le comptage).
        // Avant, l'écart se calculait sur la seule quantité figée : une vente faite pendant le comptage était
        // retranchée deux fois (vendue, puis « manquante » à l'inventaire). Une ligne saisie à l'instant compare au
        // stock courant ; une ligne saisie plus tôt, au stock de l'instant de sa saisie (updated_at de la ligne).
        var now = clock.UtcNow;
        var opened = count.StartedAt ?? count.CreatedAt;
        var expected = new Dictionary<Guid, decimal>();
        foreach (var line in count.Lines.Where(l => l.CountedQuantity is not null))
        {
            var countedAt = countedBefore[line.Id] != line.CountedQuantity ? now : line.UpdatedAt;
            expected[line.Id] = line.SystemQuantity + (countedAt > opened
                ? await store.MovementsBetweenAsync(line.ProductId, count.InventoryLocationId, line.LotId, opened, countedAt, ct)
                : 0m);
        }

        var withVariance = count.Lines.Where(l => l.CountedQuantity is { } q && q != expected[l.Id]).ToList();
        if (requireJustification && withVariance.Count > 0 && string.IsNullOrWhiteSpace(note))
            return Error.Validation("inventory.justification_required",
                "Des écarts existent : un commentaire de validation est obligatoire (il est conservé dans l'audit).");
        var currency = await store.FunctionalCurrencyAsync(ct);

        return await Guarded(() => uow.ExecuteInTransactionAsync<Result<Guid?>>(async token =>
        {
            // La quantité système de la ligne devient la quantité attendue : l'écart affiché (colonne générée
            // compté − système) est exactement l'ajustement passé.
            foreach (var (lineId, quantity) in expected)
                count.Lines.First(l => l.Id == lineId).SystemQuantity = quantity;
            var adjustment = new StockAdjustment
            {
                Id = Guid.NewGuid(),
                CompanyId = Company,
                InventoryLocationId = count.InventoryLocationId,
                DocumentNumber = await store.NextNumberAsync("stock_adjustment", token),
                AdjustmentTypeValue = AdjustmentType.Count,
                Status = "posted",
                Reason = string.IsNullOrWhiteSpace(note) ? $"Inventaire {count.DocumentNumber}" : $"Inventaire {count.DocumentNumber} : {note.Trim()}",
                OccurredAt = now,
                SourceType = "inventory_count",
                SourceId = count.Id,
                CreatedBy = tenant.UserId,
            };

            decimal valuationDelta = 0;
            var movements = new List<StockMovementRequest>();
            foreach (var line in withVariance)
            {
                var variance = line.CountedQuantity!.Value - line.SystemQuantity;
                adjustment.Lines.Add(new StockAdjustmentLine
                {
                    Id = Guid.NewGuid(), StockAdjustmentId = adjustment.Id, CompanyId = Company,
                    ProductId = line.ProductId, LotId = line.LotId,
                    QuantityDelta = variance, UnitCost = line.UnitCost, Note = line.Note,
                });
                valuationDelta += variance * line.UnitCost;
                movements.Add(new StockMovementRequest(line.ProductId, count.InventoryLocationId,
                    MovementType.Count, variance, line.UnitCost, "stock_adjustment", adjustment.Id, line.LotId, now));
            }

            if (adjustment.Lines.Count == 0)
            {
                await store.SetCountValidatedAsync(count.Id, tenant.UserId, token);
                if (!string.IsNullOrWhiteSpace(note)) await store.SetCountStatusAsync(count.Id, "validated", note.Trim(), token);
                return Result.Success<Guid?>(null);
            }

            await store.AddAdjustmentAsync(adjustment, token);
            await stock.PostManyAsync(movements, token);
            await store.SetCountValidatedAsync(count.Id, tenant.UserId, token);
            if (!string.IsNullOrWhiteSpace(note)) await store.SetCountStatusAsync(count.Id, "validated", note.Trim(), token);

            if (valuationDelta != 0)
            {
                var post = await engine.HandleAsync(new InventoryRevaluationRecorded(
                    Company, operationUuid, now, adjustment.Id, new Money(valuationDelta, currency)) { UserId = tenant.UserId }, token);
                if (!post.IsSuccess) throw new InvalidOperationException(post.Error!.Message);
                await store.SetAdjustmentJournalAsync(adjustment.Id, post.Value!.JournalEntryId, token);
            }
            return Result.Success<Guid?>(adjustment.Id);
        }, ct));
    }

    public async Task<Result> CancelCountAsync(Guid countId, string reason, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(reason))
            return Error.Validation("inventory.reason_required", "Un motif d'annulation est obligatoire.");
        var count = await store.GetCountAsync(countId, ct);
        if (count is null) return Error.NotFound("inventory.count_not_found", "Inventaire introuvable.");
        if (count.Status is "validated" or "cancelled")
            return Error.Conflict("inventory.count_closed", "Inventaire déjà validé ou annulé.");
        await store.SetCountStatusAsync(count.Id, "cancelled", $"Annulé : {reason.Trim()}", ct);
        return Result.Success();
    }

    /// <summary>Reporte les quantités comptées et notes saisies sur les lignes (suivies) de l'inventaire.</summary>
    private static Error? ApplyCounts(InventoryCount count, IReadOnlyList<CountLineInput> counted)
    {
        foreach (var input in counted)
        {
            if (input.CountedQuantity < 0)
                return Error.Validation("inventory.quantity", "Une quantité comptée ne peut être négative.");
            var line = count.Lines.FirstOrDefault(l => l.ProductId == input.ProductId && l.LotId == input.LotId);
            if (line is null)
                return Error.Validation("inventory.count_line_unknown", "Article absent de cet inventaire.");
            line.CountedQuantity = input.CountedQuantity;
            if (input.Note is not null) line.Note = string.IsNullOrWhiteSpace(input.Note) ? null : input.Note.Trim();
        }
        return null;
    }

    // ======================================================================== Pertes & ajustements

    /// <summary>Perte / casse / expiration / vol (§16) : sortie de stock valorisée au coût moyen, passée en charge.</summary>
    public async Task<Result<Guid>> RecordLossAsync(RecordLossCommand c, CancellationToken ct = default)
    {
        if (c.Lines.Count == 0) return Error.Validation("inventory.no_lines", "Aucune ligne.");
        if (!LossTypes.Contains(c.LossType))
            return Error.Validation("inventory.loss_type", "Type attendu : perte, casse, expiration ou vol (le stock retrouvé est un ajustement).");
        if (string.IsNullOrWhiteSpace(c.Reason))
            return Error.Validation("inventory.reason_required", "Le motif est obligatoire.");
        if (c.Lines.Any(l => l.Quantity <= 0))
            return Error.Validation("inventory.quantity", "Quantité de perte invalide.");
        var currency = await store.FunctionalCurrencyAsync(ct);

        return await Guarded(() => uow.ExecuteInTransactionAsync<Result<Guid>>(async token =>
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
                Reason = c.Reason.Trim(),
                OccurredAt = now,
                CreatedBy = tenant.UserId,
            };

            decimal value = 0;
            var movements = new List<StockMovementRequest>();
            // Le vol n'a pas de type de mouvement propre : il sort du stock comme une perte.
            var mvType = c.LossType == AdjustmentType.Theft ? MovementType.Loss : c.LossType;
            foreach (var l in c.Lines)
            {
                var cost = await store.AverageCostAsync(l.ProductId, c.LocationId, token);
                value += l.Quantity * cost;
                adjustment.Lines.Add(new StockAdjustmentLine
                {
                    Id = Guid.NewGuid(), StockAdjustmentId = adjustment.Id, CompanyId = Company,
                    ProductId = l.ProductId, LotId = l.LotId, QuantityDelta = -l.Quantity, UnitCost = cost,
                    Note = string.IsNullOrWhiteSpace(l.Note) ? null : l.Note.Trim(),
                });
                movements.Add(new StockMovementRequest(l.ProductId, c.LocationId,
                    mvType, -l.Quantity, cost, "stock_adjustment", adjustment.Id, l.LotId, now));
            }

            await store.AddAdjustmentAsync(adjustment, token);
            await stock.PostManyAsync(movements, token);

            var post = await engine.HandleAsync(new InventoryWriteOffRecorded(
                Company, c.OperationUuid, now, adjustment.Id, new Money(value, currency), c.LossType) { UserId = tenant.UserId }, token);
            if (!post.IsSuccess) throw new InvalidOperationException(post.Error!.Message);
            await store.SetAdjustmentJournalAsync(adjustment.Id, post.Value!.JournalEntryId, token);

            return Result.Success(adjustment.Id);
        }, ct));
    }

    /// <summary>
    /// Ajustement manuel (+ / −) avec motif obligatoire : chaque ligne devient un mouvement tracé, la
    /// variation de valeur est comptabilisée en écart d'inventaire (même règle qu'un inventaire).
    /// Une entrée sans coût moyen existant doit fournir son coût unitaire.
    /// </summary>
    public async Task<Result<Guid>> RecordAdjustmentAsync(RecordAdjustmentCommand c, CancellationToken ct = default)
    {
        if (c.Lines.Count == 0) return Error.Validation("inventory.no_lines", "Aucune ligne.");
        if (!AdjustmentTypes.Contains(c.AdjustmentType))
            return Error.Validation("inventory.adjustment_type", "Type attendu : correction ou found (stock retrouvé).");
        if (string.IsNullOrWhiteSpace(c.Reason))
            return Error.Validation("inventory.reason_required", "Le motif est obligatoire.");
        if (!await store.LocationExistsAsync(c.LocationId, ct))
            return Error.Validation("inventory.location_unknown", "Emplacement inconnu pour cette entreprise.");
        if (c.Lines.Any(l => l.QuantityDelta == 0))
            return Error.Validation("inventory.quantity", "Une ligne d'ajustement ne peut être nulle.");
        if (c.AdjustmentType == AdjustmentType.Found && c.Lines.Any(l => l.QuantityDelta < 0))
            return Error.Validation("inventory.quantity", "Du stock retrouvé ne peut être qu'une entrée (quantité positive).");
        if (c.Lines.Any(l => l.UnitCost < 0))
            return Error.Validation("inventory.unit_cost", "Le coût unitaire ne peut être négatif.");
        foreach (var l in c.Lines)
        {
            if (!await store.ProductExistsAsync(l.ProductId, ct))
                return Error.Validation("inventory.product_unknown", "Article inconnu pour cette entreprise.");
            if (l.QuantityDelta > 0 && l.UnitCost is null && await store.AverageCostAsync(l.ProductId, c.LocationId, ct) == 0)
                return Error.Validation("inventory.unit_cost_required",
                    "Article sans coût moyen à cet emplacement : indiquez son coût unitaire pour valoriser l'entrée.");
        }
        var currency = await store.FunctionalCurrencyAsync(ct);

        return await Guarded(() => uow.ExecuteInTransactionAsync<Result<Guid>>(async token =>
        {
            var now = c.OccurredAt ?? clock.UtcNow;
            var adjustment = new StockAdjustment
            {
                Id = Guid.NewGuid(), CompanyId = Company, InventoryLocationId = c.LocationId,
                DocumentNumber = await store.NextNumberAsync("stock_adjustment", token),
                AdjustmentTypeValue = c.AdjustmentType, Status = "posted", Reason = c.Reason.Trim(),
                OccurredAt = now, CreatedBy = tenant.UserId,
            };
            decimal valuationDelta = 0;
            var movements = new List<StockMovementRequest>();
            var mvType = c.AdjustmentType == AdjustmentType.Found ? MovementType.Found : MovementType.Adjustment;
            foreach (var l in c.Lines)
            {
                var average = await store.AverageCostAsync(l.ProductId, c.LocationId, token);
                // Une entrée se valorise au coût moyen existant ; à défaut, au coût saisi. Une sortie : toujours au coût moyen.
                var cost = l.QuantityDelta > 0 && average == 0 ? l.UnitCost ?? 0m : average;
                valuationDelta += l.QuantityDelta * cost;
                adjustment.Lines.Add(new StockAdjustmentLine
                {
                    Id = Guid.NewGuid(), StockAdjustmentId = adjustment.Id, CompanyId = Company,
                    ProductId = l.ProductId, LotId = l.LotId, QuantityDelta = l.QuantityDelta, UnitCost = cost,
                    Note = string.IsNullOrWhiteSpace(l.Note) ? null : l.Note.Trim(),
                });
                movements.Add(new StockMovementRequest(l.ProductId, c.LocationId, mvType, l.QuantityDelta, cost,
                    "stock_adjustment", adjustment.Id, l.LotId, now));
            }
            await store.AddAdjustmentAsync(adjustment, token);
            await stock.PostManyAsync(movements, token);
            if (valuationDelta != 0)
            {
                var post = await engine.HandleAsync(new InventoryRevaluationRecorded(
                    Company, c.OperationUuid, now, adjustment.Id, new Money(valuationDelta, currency)) { UserId = tenant.UserId }, token);
                if (!post.IsSuccess) throw new InvalidOperationException(post.Error!.Message);
                await store.SetAdjustmentJournalAsync(adjustment.Id, post.Value!.JournalEntryId, token);
            }
            return Result.Success(adjustment.Id);
        }, ct));
    }
}
