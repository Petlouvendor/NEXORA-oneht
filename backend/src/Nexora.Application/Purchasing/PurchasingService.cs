using System.Text.Json;
using Nexora.Application.Abstractions;
using Nexora.Application.Finance;
using Nexora.Application.Inventory;
using Nexora.Contracts.Finance;
using Nexora.Contracts.Purchasing;
using Nexora.Domain.Catalog;
using Nexora.Domain.Common;
using Nexora.Domain.Inventory;
using Nexora.Domain.Purchasing;

namespace Nexora.Application.Purchasing;

public sealed record CreatePurchaseOrderLine(Guid ProductId, decimal Quantity, decimal UnitPrice, Guid? TaxId, decimal Discount);
public sealed record CreatePurchaseOrderCommand(
    Guid SupplierId, Guid? WarehouseId, string Currency, DateOnly? ExpectedDate,
    IReadOnlyList<CreatePurchaseOrderLine> Lines, string? Notes);

public sealed record ReceiveLine(Guid ProductId, Guid? PurchaseOrderLineId, decimal Quantity, decimal UnitCost,
    string? LotNumber, DateOnly? ExpirationDate);
public sealed record ReceiveGoodsCommand(
    Guid OperationUuid, Guid SupplierId, Guid InventoryLocationId, Guid? PurchaseOrderId,
    IReadOnlyList<ReceiveLine> Lines, DateTimeOffset? ReceivedAt, string? Notes);

public sealed record InvoiceLine(Guid? ProductId, string Description, decimal Quantity, decimal UnitPrice, Guid? TaxId, decimal TaxAmount, decimal Discount);
public sealed record RecordPurchaseInvoiceCommand(
    Guid OperationUuid, Guid SupplierId, Guid? PurchaseOrderId, string Currency,
    DateOnly InvoiceDate, DateOnly? DueDate, bool OnCredit, Guid? TenderFinancialAccountId,
    IReadOnlyList<InvoiceLine> Lines, bool CapitaliseToInventory,
    string? SupplierReference = null, Guid? GoodsReceiptId = null);

public sealed record RecordPurchaseInvoiceResult(
    Guid InvoiceId, Guid? PayableId, Guid JournalEntryId, string EntryNumber, bool WasReplay);

public sealed record ReturnLine(Guid ProductId, decimal Quantity, decimal UnitPrice, decimal TaxAmount, Guid? LotId);
public sealed record RecordSupplierReturnCommand(
    Guid OperationUuid, Guid SupplierId, Guid InventoryLocationId, Guid? GoodsReceiptId, Guid? PayableId,
    string Currency, string Reason, IReadOnlyList<ReturnLine> Lines);

public interface IPurchasingStore
{
    Task<Guid> ResolveCurrencyIdAsync(string code, CancellationToken ct = default);
    /// <summary>Devise fonctionnelle de l'entreprise active (historique de coût d'une réception sans commande).</summary>
    Task<Guid> CompanyCurrencyIdAsync(CancellationToken ct = default);
    Task<string> NextNumberAsync(string documentType, CancellationToken ct = default);
    Task AddSupplierReturnAsync(Domain.Purchasing.SupplierReturn ret, CancellationToken ct = default);
    Task AddOrderAsync(PurchaseOrder order, CancellationToken ct = default);
    Task<PurchaseOrder?> GetOrderAsync(Guid id, CancellationToken ct = default);
    Task<string?> OrderStatusAsync(Guid orderId, CancellationToken ct = default);
    Task SetOrderStatusAsync(Guid orderId, string status, CancellationToken ct = default);
    Task AddReceiptAsync(GoodsReceipt receipt, CancellationToken ct = default);
    Task<Guid?> FindReceiptByOperationAsync(Guid operationUuid, CancellationToken ct = default);
    Task BumpOrderLineReceivedAsync(Guid orderLineId, decimal quantity, CancellationToken ct = default);
    Task RefreshOrderStatusAsync(Guid orderId, CancellationToken ct = default);
    Task<Guid> EnsureLotAsync(Guid productId, string lotNumber, DateOnly? expiration, Guid? supplierId, DateOnly? receivedAt, CancellationToken ct = default);
    Task<Guid> CreateSupplierInvoiceAsync(SupplierInvoiceRow row, IReadOnlyList<SupplierInvoiceLineRow> lines, CancellationToken ct = default);
    Task<bool> SupplierRefExistsAsync(Guid supplierId, string reference, CancellationToken ct = default);
    Task<InvoiceMatchData?> GetInvoiceMatchDataAsync(Guid invoiceId, CancellationToken ct = default);
    Task SetInvoiceMatchAsync(Guid invoiceId, string matchStatus, string detailJson, bool blocked, string? blockReason, Guid? matchedBy, CancellationToken ct = default);
    Task<PayablePaymentInfo?> GetPayablePaymentInfoAsync(Guid payableId, CancellationToken ct = default);
    Task AddCostHistoryAsync(ProductCostHistory history, CancellationToken ct = default);
    Task<Product?> GetProductAsync(Guid id, CancellationToken ct = default);
}

public sealed record SupplierInvoiceRow(
    Guid Id, Guid CompanyId, Guid SupplierId, Guid? PurchaseOrderId, string DocumentNumber,
    DateOnly InvoiceDate, DateOnly? DueDate, Guid CurrencyId,
    decimal Subtotal, decimal TaxTotal, decimal Total, string Status,
    string? SupplierReference, Guid? GoodsReceiptId);

public sealed record SupplierInvoiceLineRow(
    Guid? ProductId, string Description, decimal Quantity, decimal UnitPrice, Guid? TaxId, decimal Discount, decimal LineTotal);

public sealed record InvoiceMatchLine(Guid? ProductId, string Description, decimal Quantity, decimal UnitPrice);
public sealed record OrderMatchLine(Guid ProductId, decimal QuantityOrdered, decimal UnitPrice);
public sealed record InvoiceMatchData(
    Guid InvoiceId, string InvoiceNumber, Guid? PurchaseOrderId, string CurrentMatchStatus,
    IReadOnlyList<InvoiceMatchLine> InvoiceLines, IReadOnlyList<OrderMatchLine> OrderLines,
    IReadOnlyDictionary<Guid, decimal> ReceivedByProduct);

public sealed record PayablePaymentInfo(
    Guid PayableId, Guid SupplierId, string Currency, decimal RemainingAmount, string Status,
    bool InvoicePaymentBlocked, string? BlockReason);

/// <summary>Cycle d'achat (Phase 5 §21-27). La réception alimente le stock ; la facture alimente le moteur financier.</summary>
public sealed class PurchasingService(
    IPurchasingStore store,
    IStockLedger stock,
    IFinanceOperations finance,
    IUnitOfWork uow,
    ITenantContext tenant,
    IDateTimeProvider clock)
{
    private Guid Company => tenant.ActiveCompanyId
        ?? throw new InvalidOperationException("Aucune entreprise active.");

    // --- Commande fournisseur (§23) ------------------------------------
    public async Task<Result<Guid>> CreateOrderAsync(CreatePurchaseOrderCommand c, CancellationToken ct = default)
    {
        if (c.Lines.Count == 0) return Error.Validation("purchasing.no_lines", "La commande doit contenir au moins une ligne.");

        var currencyId = await store.ResolveCurrencyIdAsync(c.Currency, ct);
        var order = new PurchaseOrder
        {
            Id = Guid.NewGuid(),
            CompanyId = Company,
            SupplierId = c.SupplierId,
            WarehouseId = c.WarehouseId,
            DocumentNumber = await store.NextNumberAsync("purchase_order", ct),
            Status = "submitted",
            CurrencyId = currencyId,
            OrderDate = DateOnly.FromDateTime(clock.UtcNow.UtcDateTime),
            ExpectedDate = c.ExpectedDate,
            Notes = c.Notes,
            CreatedBy = tenant.UserId,
        };

        decimal sub = 0, tax = 0;
        foreach (var l in c.Lines)
        {
            var lineTotal = (l.Quantity * l.UnitPrice) - l.Discount;
            sub += lineTotal;
            order.Lines.Add(new PurchaseOrderLine
            {
                Id = Guid.NewGuid(),
                PurchaseOrderId = order.Id,
                CompanyId = Company,
                ProductId = l.ProductId,
                QuantityOrdered = l.Quantity,
                UnitPrice = l.UnitPrice,
                TaxId = l.TaxId,
                Discount = l.Discount,
                LineTotal = lineTotal,
            });
        }
        order.Subtotal = sub;
        order.TaxTotal = tax;
        order.Total = sub + tax;

        await store.AddOrderAsync(order, ct);
        await uow.SaveChangesAsync(ct);
        return Result.Success(order.Id);
    }

    // --- Cycle de vie de la commande (§10) ----------------------------
    public Task<Result> SubmitOrderAsync(Guid id, CancellationToken ct = default)
        => TransitionOrderAsync(id, PurchaseOrderStatus.Submitted, ct);

    public Task<Result> ApproveOrderAsync(Guid id, CancellationToken ct = default)
        => TransitionOrderAsync(id, PurchaseOrderStatus.Approved, ct);

    public Task<Result> SendOrderAsync(Guid id, CancellationToken ct = default)
        => TransitionOrderAsync(id, PurchaseOrderStatus.Sent, ct);

    public Task<Result> CancelOrderAsync(Guid id, CancellationToken ct = default)
        => TransitionOrderAsync(id, PurchaseOrderStatus.Cancelled, ct);

    private async Task<Result> TransitionOrderAsync(Guid id, string target, CancellationToken ct)
    {
        var current = await store.OrderStatusAsync(id, ct);
        if (current is null) return Error.NotFound("purchasing.order_not_found", "Commande introuvable.");
        if (current == target) return Result.Success();
        if (!PurchaseOrderStatus.CanTransition(current, target))
            return Error.Conflict("purchasing.order_transition", $"Transition interdite : {current} → {target} (§10).");
        await store.SetOrderStatusAsync(id, target, ct);
        await uow.SaveChangesAsync(ct);
        return Result.Success();
    }

    // --- Réception (§24) : partielle possible, lots + expiration ------
    public async Task<Result<Guid>> ReceiveAsync(ReceiveGoodsCommand c, CancellationToken ct = default)
    {
        var existing = await store.FindReceiptByOperationAsync(c.OperationUuid, ct);
        if (existing is { } id) return Result.Success(id);
        if (c.Lines.Count == 0) return Error.Validation("purchasing.no_lines", "Rien à réceptionner.");
        if (c.Lines.Any(l => l.Quantity <= 0))
            return Error.Validation("purchasing.receipt_quantity", "Quantité reçue invalide.");

        // Résumé fonctionnel §9 : COMMANDE → APPROBATION → RÉCEPTION. Une réception rattachée à une
        // commande exige une commande approuvée, du même fournisseur, et ne dépasse jamais le reste à recevoir.
        Guid currencyId;
        if (c.PurchaseOrderId is { } poId)
        {
            var po = await store.GetOrderAsync(poId, ct);
            if (po is null || po.CompanyId != Company)
                return Error.NotFound("purchasing.po_not_found", "Commande fournisseur introuvable.");
            if (!PurchaseOrderStatus.Receivable.Contains(po.Status))
                return Error.Conflict("purchasing.po_not_receivable",
                    $"La commande {po.DocumentNumber} ne peut pas être réceptionnée (statut « {po.Status} ») : elle doit être approuvée.");
            if (po.SupplierId != c.SupplierId)
                return Error.Validation("purchasing.po_supplier_mismatch", "Le fournisseur ne correspond pas à celui de la commande.");
            foreach (var group in c.Lines.Where(l => l.PurchaseOrderLineId is not null).GroupBy(l => l.PurchaseOrderLineId!.Value))
            {
                var poLine = po.Lines.FirstOrDefault(pl => pl.Id == group.Key);
                if (poLine is null)
                    return Error.Validation("purchasing.po_line_mismatch", "Une ligne reçue n'appartient pas à la commande.");
                if (group.Any(l => l.ProductId != poLine.ProductId))
                    return Error.Validation("purchasing.po_line_product", "Le produit reçu ne correspond pas à la ligne de commande.");
                var remaining = poLine.QuantityOrdered - poLine.QuantityReceived;
                var received = group.Sum(l => l.Quantity);
                if (received > remaining)
                    return Error.Validation("purchasing.over_receipt",
                        $"Réception de {received} pour {remaining} restant(s) à recevoir sur la commande {po.DocumentNumber}.");
            }
            currencyId = po.CurrencyId;
        }
        else
        {
            if (c.Lines.Any(l => l.PurchaseOrderLineId is not null))
                return Error.Validation("purchasing.po_required", "Des lignes de commande sont citées sans la commande.");
            currencyId = await store.CompanyCurrencyIdAsync(ct);
        }

        var receivedAt = c.ReceivedAt ?? clock.UtcNow;

        return await uow.ExecuteInTransactionAsync<Result<Guid>>(async token =>
        {
            var receipt = new GoodsReceipt
            {
                Id = Guid.NewGuid(),
                CompanyId = Company,
                SupplierId = c.SupplierId,
                InventoryLocationId = c.InventoryLocationId,
                PurchaseOrderId = c.PurchaseOrderId,
                DocumentNumber = await store.NextNumberAsync("goods_receipt", token),
                Status = "posted",
                ReceivedAt = receivedAt,
                Notes = c.Notes,
                CreatedBy = tenant.UserId,
            };

            var movements = new List<StockMovementRequest>();
            foreach (var l in c.Lines)
            {
                Guid? lotId = null;
                if (!string.IsNullOrWhiteSpace(l.LotNumber))
                    lotId = await store.EnsureLotAsync(l.ProductId, l.LotNumber!, l.ExpirationDate,
                        c.SupplierId, DateOnly.FromDateTime(receivedAt.UtcDateTime), token);

                receipt.Lines.Add(new GoodsReceiptLine
                {
                    Id = Guid.NewGuid(),
                    GoodsReceiptId = receipt.Id,
                    CompanyId = Company,
                    PurchaseOrderLineId = l.PurchaseOrderLineId,
                    ProductId = l.ProductId,
                    QuantityReceived = l.Quantity,
                    UnitCost = l.UnitCost,
                    LotId = lotId,
                });

                movements.Add(new StockMovementRequest(
                    l.ProductId, c.InventoryLocationId, MovementType.Receipt, l.Quantity, l.UnitCost,
                    // L'UUID d'opération est unique par mouvement : seul le premier le porte (il sert
                    // d'ancre d'idempotence), sinon toute réception de plus d'une ligne échoue.
                    "goods_receipt", receipt.Id, lotId, receivedAt, movements.Count == 0 ? c.OperationUuid : null));

                await store.AddCostHistoryAsync(new ProductCostHistory
                {
                    Id = Guid.NewGuid(),
                    CompanyId = Company,
                    ProductId = l.ProductId,
                    SourceType = "goods_receipt",
                    SourceId = receipt.Id,
                    UnitCost = l.UnitCost,
                    CurrencyId = currencyId,   // devise de la commande, sinon devise de l'entreprise (était « HTG » en dur)
                    Method = "average",
                    EffectiveAt = receivedAt,
                    CreatedAt = clock.UtcNow,
                }, token);

                if (l.PurchaseOrderLineId is { } pol)
                    await store.BumpOrderLineReceivedAsync(pol, l.Quantity, token);
            }

            await store.AddReceiptAsync(receipt, token);
            await stock.PostManyAsync(movements, token);

            if (c.PurchaseOrderId is { } po)
                await store.RefreshOrderStatusAsync(po, token);

            return Result.Success(receipt.Id);
        }, ct);
    }

    // --- Facture fournisseur → moteur financier (§19, §25-27, §54) ---
    public async Task<Result<RecordPurchaseInvoiceResult>> RecordInvoiceAsync(RecordPurchaseInvoiceCommand c, CancellationToken ct = default)
    {
        if (c.Lines.Count == 0) return Error.Validation("purchasing.no_lines", "La facture doit contenir au moins une ligne.");

        // §53-54 : une référence de facture ne peut être réutilisée pour le même fournisseur.
        if (!string.IsNullOrWhiteSpace(c.SupplierReference)
            && await store.SupplierRefExistsAsync(c.SupplierId, c.SupplierReference!.Trim(), ct))
            return Error.Conflict("purchasing.duplicate_invoice",
                $"Une facture avec la référence « {c.SupplierReference} » existe déjà pour ce fournisseur (§54).");

        decimal net = c.Lines.Sum(l => (l.Quantity * l.UnitPrice) - l.Discount);
        decimal tax = c.Lines.Sum(l => l.TaxAmount);

        var invoiceLines = c.Lines.Select(l => new SupplierInvoiceLineRow(
            l.ProductId, l.Description, l.Quantity, l.UnitPrice, l.TaxId, l.Discount,
            (l.Quantity * l.UnitPrice) - l.Discount)).ToList();

        var invoiceId = await store.CreateSupplierInvoiceAsync(new SupplierInvoiceRow(
            Guid.NewGuid(), Company, c.SupplierId, c.PurchaseOrderId,
            await store.NextNumberAsync("supplier_invoice", ct),
            c.InvoiceDate, c.DueDate, await store.ResolveCurrencyIdAsync(c.Currency, ct),
            net, tax, net + tax, c.OnCredit ? "validated" : "paid",
            string.IsNullOrWhiteSpace(c.SupplierReference) ? null : c.SupplierReference!.Trim(), c.GoodsReceiptId), invoiceLines, ct);

        var occurredAt = c.InvoiceDate.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);

        Result<PostingResultResponse> posted;
        if (c.OnCredit)
        {
            posted = await finance.RecordSupplierInvoiceAsync(new RecordSupplierInvoiceRequest(
                c.OperationUuid, invoiceId, c.SupplierId, occurredAt, net, tax, c.Currency, c.DueDate,
                c.CapitaliseToInventory), ct);
        }
        else
        {
            if (c.TenderFinancialAccountId is not { } tender)
                return Error.Validation("purchasing.tender_required", "Un compte financier est requis pour un achat comptant.");
            posted = await finance.RecordCashPurchaseAsync(new RecordCashPurchaseRequest(
                c.OperationUuid, invoiceId, occurredAt, net, tax, c.Currency, tender, c.CapitaliseToInventory), ct);
        }

        if (!posted.IsSuccess) return posted.Error!;
        var p = posted.Value!;
        return Result.Success(new RecordPurchaseInvoiceResult(
            invoiceId, p.PayableId, p.JournalEntryId, p.EntryNumber, p.WasReplay));
    }

    // --- Retour fournisseur (§28) : sortie stock + impact dette / avoir ----
    public async Task<Result<Guid>> RecordReturnAsync(RecordSupplierReturnCommand c, CancellationToken ct = default)
    {
        if (c.Lines.Count == 0) return Error.Validation("purchasing.no_lines", "Aucune ligne à retourner.");

        return await uow.ExecuteInTransactionAsync<Result<Guid>>(async token =>
        {
            var now = clock.UtcNow;
            decimal net = c.Lines.Sum(l => l.Quantity * l.UnitPrice);
            decimal tax = c.Lines.Sum(l => l.TaxAmount);

            var ret = new Domain.Purchasing.SupplierReturn
            {
                Id = Guid.NewGuid(),
                CompanyId = Company,
                SupplierId = c.SupplierId,
                GoodsReceiptId = c.GoodsReceiptId,
                WarehouseLocationId = c.InventoryLocationId,
                DocumentNumber = await store.NextNumberAsync("supplier_return", token),
                Status = "posted",
                Reason = c.Reason,
                CurrencyId = await store.ResolveCurrencyIdAsync(c.Currency, token),
                Subtotal = net,
                TaxTotal = tax,
                Total = net + tax,
                ReturnedAt = now,
                CreatedBy = tenant.UserId,
            };
            var movements = new List<StockMovementRequest>();
            foreach (var l in c.Lines)
            {
                if (l.Quantity <= 0) throw new InvalidOperationException("Quantité de retour invalide.");
                var cost = await stock.AverageCostAsync(l.ProductId, c.InventoryLocationId, token);
                ret.Lines.Add(new Domain.Purchasing.SupplierReturnLine
                {
                    Id = Guid.NewGuid(), SupplierReturnId = ret.Id, CompanyId = Company,
                    ProductId = l.ProductId, LotId = l.LotId, Quantity = l.Quantity,
                    UnitPrice = l.UnitPrice, LineTotal = l.Quantity * l.UnitPrice,
                });
                movements.Add(new StockMovementRequest(l.ProductId, c.InventoryLocationId,
                    MovementType.PurchaseReturn, -l.Quantity, cost, "supplier_return", ret.Id, l.LotId, now));
            }

            await store.AddSupplierReturnAsync(ret, token);
            await stock.PostManyAsync(movements, token);

            var post = await finance.RecordSupplierReturnAsync(new RecordSupplierReturnRequest(
                c.OperationUuid, ret.Id, c.SupplierId, now, net, tax, c.Currency, c.PayableId is not null), token);
            if (!post.IsSuccess) return post.Error!;

            return Result.Success(ret.Id);
        }, ct);
    }

    // --- Rapprochement 3-way : COMMANDE ↕ RÉCEPTION ↕ FACTURE (§20, §40) ----
    public async Task<Result<ThreeWayMatchReport>> MatchInvoiceAsync(
        Guid invoiceId, MatchInvoiceRequest req, CancellationToken ct = default)
    {
        var qtyTol = Math.Max(0m, req.QuantityTolerancePercent);
        var priceTol = Math.Max(0m, req.PriceTolerancePercent);

        var data = await store.GetInvoiceMatchDataAsync(invoiceId, ct);
        if (data is null) return Error.NotFound("purchasing.invoice_not_found", "Facture introuvable.");
        if (data.PurchaseOrderId is null)
            return Error.Validation("purchasing.no_order", "Le rapprochement 3-way exige une facture rattachée à une commande (§21 pour une facture libre).");

        var order = data.OrderLines.ToDictionary(o => o.ProductId);
        var lines = new List<MatchLineDto>();
        var anyVariance = false;

        foreach (var inv in data.InvoiceLines)
        {
            if (inv.ProductId is not { } pid)
            {
                anyVariance = true;
                lines.Add(new MatchLineDto(Guid.Empty, inv.Description, 0m, 0m, inv.Quantity, 0m, inv.UnitPrice,
                    100m, 100m, "variance", "Ligne de facture sans produit — non rapprochable."));
                continue;
            }
            if (!order.TryGetValue(pid, out var ol))
            {
                anyVariance = true;
                lines.Add(new MatchLineDto(pid, inv.Description, 0m, 0m, inv.Quantity, 0m, inv.UnitPrice,
                    100m, 100m, "variance", "Article non commandé."));
                continue;
            }
            var received = data.ReceivedByProduct.TryGetValue(pid, out var r) ? r : 0m;
            var qtyVar = Pct(inv.Quantity, received);
            var priceVar = Pct(inv.UnitPrice, ol.UnitPrice);
            var ok = qtyVar <= qtyTol && priceVar <= priceTol;
            if (!ok) anyVariance = true;
            var detail = ok ? null
                : priceVar > priceTol && qtyVar > qtyTol ? "Écart prix et quantité."
                : priceVar > priceTol ? $"Écart prix : facturé {inv.UnitPrice}, commandé {ol.UnitPrice}."
                : $"Écart quantité : facturé {inv.Quantity}, reçu {received}.";
            lines.Add(new MatchLineDto(pid, inv.Description, ol.QuantityOrdered, received, inv.Quantity,
                ol.UnitPrice, inv.UnitPrice, Math.Round(qtyVar, 2), Math.Round(priceVar, 2),
                ok ? "matched" : "variance", detail));
        }

        var status = anyVariance ? "variance" : "matched";
        var blocked = anyVariance;
        var blockReason = blocked ? "Écart de rapprochement 3-way — paiement bloqué (§40)." : null;
        var detailJson = JsonSerializer.Serialize(lines);
        await store.SetInvoiceMatchAsync(invoiceId, status, detailJson, blocked, blockReason, tenant.UserId, ct);
        await uow.SaveChangesAsync(ct);

        return Result.Success(new ThreeWayMatchReport(
            invoiceId, data.InvoiceNumber, data.PurchaseOrderId, status, blocked, blockReason, lines));
    }

    // --- Paiement d'une dette fournisseur (§26-27, §40) --------------
    public async Task<Result<PostingResultResponse>> PayPayableAsync(
        Guid payableId, PayPayableRequest req, CancellationToken ct = default)
    {
        if (req.Amount <= 0m) return Error.Validation("purchasing.payment_amount", "Le montant du paiement doit être positif.");
        var info = await store.GetPayablePaymentInfoAsync(payableId, ct);
        if (info is null) return Error.NotFound("purchasing.payable_not_found", "Dette introuvable.");
        if (info.Status is "PAID" or "CANCELLED")
            return Error.Conflict("purchasing.payable_closed", "Cette dette est déjà soldée ou annulée.");
        if (info.InvoicePaymentBlocked)
            return Error.Conflict("purchasing.payment_blocked", info.BlockReason ?? "Paiement bloqué par le rapprochement (§40).");
        if (req.Amount > info.RemainingAmount)
            return Error.Validation("purchasing.payment_exceeds", $"Le paiement ({req.Amount}) dépasse le solde ({info.RemainingAmount}).");

        return await finance.RecordSupplierPaymentAsync(new RecordPaymentRequest(
            req.OperationUuid, clock.UtcNow, info.SupplierId, req.Amount, info.Currency, req.TenderFinancialAccountId,
            [new AllocationInput(payableId, req.Amount, IsReceivable: false)], req.Reference), ct);
    }

    private static decimal Pct(decimal actual, decimal reference)
        => reference == 0m ? (actual == 0m ? 0m : 100m) : Math.Abs(actual - reference) / Math.Abs(reference) * 100m;
}
