using System.Text.Json;
using Nexora.Application.Abstractions;
using Nexora.Application.Catalog;
using Nexora.Application.Finance;
using Nexora.Application.Inventory;
using Nexora.Contracts.Finance;
using Nexora.Contracts.Purchasing;
using Nexora.Domain.Catalog;
using Nexora.Domain.Common;
using Nexora.Domain.Inventory;
using Nexora.Domain.Purchasing;

namespace Nexora.Application.Purchasing;

/// <summary>Ligne de commande. Avec <paramref name="UnitId"/> (unité d'achat, ex. carton), quantité et prix
/// sont exprimés dans cette unité ; le serveur les convertit en unité de base (phase 3).</summary>
public sealed record CreatePurchaseOrderLine(Guid ProductId, decimal Quantity, decimal UnitPrice, Guid? TaxId, decimal Discount,
    Guid? UnitId = null);
/// <summary><paramref name="Submit"/> : true = soumise à approbation (comportement historique), false = brouillon modifiable.</summary>
public sealed record CreatePurchaseOrderCommand(
    Guid SupplierId, Guid? WarehouseId, string Currency, DateOnly? ExpectedDate,
    IReadOnlyList<CreatePurchaseOrderLine> Lines, string? Notes, bool Submit = true);

/// <summary>Ligne reçue : <paramref name="Quantity"/> = quantité ACCEPTÉE (entre en stock) ; <paramref name="QuantityRejected"/>
/// = refusée au contrôle (jamais en stock, motif obligatoire). Avec <paramref name="UnitId"/>, quantités et coût sont
/// exprimés dans cette unité et convertis par le serveur.</summary>
public sealed record ReceiveLine(Guid ProductId, Guid? PurchaseOrderLineId, decimal Quantity, decimal UnitCost,
    string? LotNumber, DateOnly? ExpirationDate, Guid? UnitId = null, decimal QuantityRejected = 0m, string? RejectionReason = null);
public sealed record ReceiveGoodsCommand(
    Guid OperationUuid, Guid SupplierId, Guid InventoryLocationId, Guid? PurchaseOrderId,
    IReadOnlyList<ReceiveLine> Lines, DateTimeOffset? ReceivedAt, string? Notes);

/// <summary>Ligne de facture. Avec <paramref name="TaxId"/>, la taxe est calculée par le serveur (le montant fourni est ignoré).</summary>
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
    /// <summary>Réception : fournisseur et quantité encore retournable par article (reçu − déjà retourné).</summary>
    Task<(Guid SupplierId, IReadOnlyDictionary<Guid, decimal> Returnable)?> ReceiptReturnableAsync(Guid receiptId, CancellationToken ct = default);
    Task AddCostHistoryAsync(ProductCostHistory history, CancellationToken ct = default);
    Task<Product?> GetProductAsync(Guid id, CancellationToken ct = default);

    // --- Finalisation produit, phase 3 -------------------------------------------------
    /// <summary>Devise par code, ou null si inconnue (au lieu d'une exception).</summary>
    Task<Guid?> CurrencyIdOrNullAsync(string code, CancellationToken ct = default);
    Task<SupplierPurchasingInfo?> SupplierInfoAsync(Guid supplierId, CancellationToken ct = default);
    Task<decimal?> TaxRateAsync(Guid taxId, CancellationToken ct = default);
    Task<bool> LocationExistsAsync(Guid locationId, CancellationToken ct = default);
    /// <summary>Facture déjà enregistrée pour cet identifiant d'opération (rejeu) — via l'écriture comptable.</summary>
    Task<Guid?> FindInvoiceByOperationAsync(Guid operationUuid, CancellationToken ct = default);
    /// <summary>Commande suivie (lignes comprises) pour modification.</summary>
    Task<PurchaseOrder?> GetOrderForUpdateAsync(Guid id, CancellationToken ct = default);
    Task ReplaceOrderLinesAsync(PurchaseOrder order, IReadOnlyList<PurchaseOrderLine> lines, CancellationToken ct = default);
    /// <summary>Change le statut seulement s'il est encore <paramref name="fromStatus"/>.</summary>
    Task SetOrderStatusIfAsync(Guid orderId, string fromStatus, string toStatus, CancellationToken ct = default);
}

/// <summary>Fournisseur vu par les achats : statut, délai de paiement (jours) et limite de crédit.</summary>
public sealed record SupplierPurchasingInfo(Guid SupplierId, string Status, int? PaymentTermsDays, decimal CreditLimit);

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

/// <summary>Cycle d'achat (Phase 5 §21-27 ; finalisation produit, phase 3). La réception alimente le stock ;
/// la facture alimente le moteur financier. Montants, taxes et conversions sont calculés ici, jamais par l'écran.</summary>
public sealed class PurchasingService(
    IPurchasingStore store,
    IStockLedger stock,
    IFinanceOperations finance,
    IUnitOfWork uow,
    ITenantContext tenant,
    IDateTimeProvider clock,
    IUnitConverter converter)
{
    private Guid Company => tenant.ActiveCompanyId
        ?? throw new InvalidOperationException("Aucune entreprise active.");

    private static decimal R2(decimal v) => Math.Round(v, 2, MidpointRounding.ToEven);
    private static decimal R4(decimal v) => Math.Round(v, 4, MidpointRounding.ToEven);

    /// <summary>Refus levé dans une transaction (stock insuffisant, contrainte) → erreur métier, jamais 500.</summary>
    private static async Task<Result<T>> Guarded<T>(Func<Task<Result<T>>> operation)
    {
        try { return await operation(); }
        catch (InvalidOperationException ex)
        {
            return ex.Message.Contains("insuffisant", StringComparison.OrdinalIgnoreCase)
                ? Error.Conflict("purchasing.insufficient_stock", ex.Message)
                : Error.Validation("purchasing.operation_failed", ex.Message);
        }
    }

    /// <summary>Quantité et prix saisis dans une unité d'achat → unité de base du produit (conversion serveur).</summary>
    private async Task<Result<(decimal Quantity, decimal UnitPrice, decimal? PurchaseQuantity)>> ToBaseAsync(
        Product product, Guid? unitId, decimal quantity, decimal unitPrice, CancellationToken ct)
    {
        if (unitId is null || unitId == product.UnitId) return Result.Success((quantity, unitPrice, (decimal?)null));
        var conv = await converter.ConvertAsync(product.Id, unitId.Value, product.UnitId, quantity, ct);
        if (!conv.IsSuccess)
            return Error.Validation("purchasing.unit_conversion",
                $"Aucune conversion définie entre cette unité et l'unité de base de « {product.Name} » (catalogue › Unités).");
        var baseQty = conv.Value!.Converted;
        if (baseQty <= 0) return Error.Validation("purchasing.quantity", "Quantité convertie nulle.");
        // Même montant total : prix unitaire de base = total saisi / quantité de base.
        return Result.Success((baseQty, R4(quantity * unitPrice / baseQty), (decimal?)quantity));
    }

    // ======================================================================== Commandes

    public async Task<Result<Guid>> CreateOrderAsync(CreatePurchaseOrderCommand c, CancellationToken ct = default)
    {
        var header = await ValidateOrderHeaderAsync(c, ct);
        if (!header.IsSuccess) return header.Error!;
        var built = await BuildOrderLinesAsync(Guid.NewGuid(), c.Lines, ct);
        if (!built.IsSuccess) return built.Error!;
        var (orderId, lines) = built.Value;

        var order = new PurchaseOrder
        {
            Id = orderId,
            CompanyId = Company,
            SupplierId = c.SupplierId,
            WarehouseId = c.WarehouseId,
            DocumentNumber = await store.NextNumberAsync("purchase_order", ct),
            Status = c.Submit ? PurchaseOrderStatus.Submitted : PurchaseOrderStatus.Draft,
            CurrencyId = header.Value,
            OrderDate = DateOnly.FromDateTime(clock.UtcNow.UtcDateTime),
            ExpectedDate = c.ExpectedDate,
            Notes = c.Notes,
            CreatedBy = tenant.UserId,
        };
        foreach (var l in lines) order.Lines.Add(l);
        ApplyTotals(order);

        await store.AddOrderAsync(order, ct);
        await uow.SaveChangesAsync(ct);
        return Result.Success(order.Id);
    }

    /// <summary>Modification d'une commande encore en brouillon ou soumise (avant approbation) : en-tête et
    /// lignes remplacés, totaux recalculés. Une commande approuvée n'est plus modifiable.</summary>
    public async Task<Result> UpdateOrderAsync(Guid id, CreatePurchaseOrderCommand c, CancellationToken ct = default)
    {
        var order = await store.GetOrderForUpdateAsync(id, ct);
        if (order is null) return Error.NotFound("purchasing.order_not_found", "Commande introuvable.");
        if (order.Status is not (PurchaseOrderStatus.Draft or PurchaseOrderStatus.Submitted))
            return Error.Conflict("purchasing.order_locked",
                $"La commande {order.DocumentNumber} est « {order.Status} » : elle n'est plus modifiable (seulement annulable).");
        var header = await ValidateOrderHeaderAsync(c, ct);
        if (!header.IsSuccess) return header.Error!;
        var built = await BuildOrderLinesAsync(order.Id, c.Lines, ct);
        if (!built.IsSuccess) return built.Error!;

        order.SupplierId = c.SupplierId;
        order.WarehouseId = c.WarehouseId;
        order.CurrencyId = header.Value;
        order.ExpectedDate = c.ExpectedDate;
        order.Notes = c.Notes;
        if (c.Submit && order.Status == PurchaseOrderStatus.Draft) order.Status = PurchaseOrderStatus.Submitted;
        await store.ReplaceOrderLinesAsync(order, built.Value.Lines, ct);
        ApplyTotals(order);
        await uow.SaveChangesAsync(ct);
        return Result.Success();
    }

    private async Task<Result<Guid>> ValidateOrderHeaderAsync(CreatePurchaseOrderCommand c, CancellationToken ct)
    {
        if (c.Lines.Count == 0) return Error.Validation("purchasing.no_lines", "La commande doit contenir au moins une ligne.");
        var supplier = await store.SupplierInfoAsync(c.SupplierId, ct);
        if (supplier is null) return Error.Validation("purchasing.supplier_unknown", "Fournisseur inconnu pour cette entreprise.");
        if (supplier.Status is "blocked" or "archived")
            return Error.Conflict("purchasing.supplier_inactive", $"Ce fournisseur est « {supplier.Status} » : aucune commande possible.");
        var currency = await store.CurrencyIdOrNullAsync(c.Currency, ct);
        if (currency is null) return Error.Validation("purchasing.currency_unknown", $"Devise « {c.Currency} » inconnue.");
        if (c.ExpectedDate is { } d && d < DateOnly.FromDateTime(clock.UtcNow.UtcDateTime).AddDays(-1))
            return Error.Validation("purchasing.expected_date", "La date de livraison prévue est déjà passée.");
        return Result.Success(currency.Value);
    }

    private async Task<Result<(Guid OrderId, List<PurchaseOrderLine> Lines)>> BuildOrderLinesAsync(
        Guid orderId, IReadOnlyList<CreatePurchaseOrderLine> input, CancellationToken ct)
    {
        var lines = new List<PurchaseOrderLine>();
        foreach (var l in input)
        {
            if (l.Quantity <= 0) return Error.Validation("purchasing.quantity", "Chaque quantité commandée doit être positive.");
            if (l.UnitPrice < 0) return Error.Validation("purchasing.unit_price", "Un prix d'achat ne peut être négatif.");
            if (l.Discount < 0 || l.Discount > l.Quantity * l.UnitPrice)
                return Error.Validation("purchasing.discount", "La remise doit être comprise entre 0 et le montant de la ligne.");
            var product = await store.GetProductAsync(l.ProductId, ct);
            if (product is null || product.CompanyId != Company)
                return Error.Validation("purchasing.product_unknown", "Article inconnu pour cette entreprise.");
            var rate = 0m;
            if (l.TaxId is { } taxId)
            {
                var r = await store.TaxRateAsync(taxId, ct);
                if (r is null) return Error.Validation("purchasing.tax_unknown", "Taxe inconnue pour cette entreprise.");
                rate = r.Value;
            }
            var conv = await ToBaseAsync(product, l.UnitId, l.Quantity, l.UnitPrice, ct);
            if (!conv.IsSuccess) return conv.Error!;
            var net = R2(l.Quantity * l.UnitPrice - l.Discount);
            lines.Add(new PurchaseOrderLine
            {
                Id = Guid.NewGuid(), PurchaseOrderId = orderId, CompanyId = Company, ProductId = l.ProductId,
                QuantityOrdered = conv.Value.Quantity, UnitPrice = conv.Value.UnitPrice,
                PurchaseUnitId = conv.Value.PurchaseQuantity is null ? null : l.UnitId,
                PurchaseQuantity = conv.Value.PurchaseQuantity,
                TaxId = l.TaxId, Discount = l.Discount, LineTotal = net, TaxAmount = R2(net * rate / 100m),
            });
        }
        return Result.Success((orderId, lines));
    }

    private static void ApplyTotals(PurchaseOrder order)
    {
        order.Subtotal = order.Lines.Sum(l => l.LineTotal);
        order.TaxTotal = order.Lines.Sum(l => l.TaxAmount);
        order.Total = order.Subtotal + order.TaxTotal;
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

    /// <summary>Solde une commande partiellement reçue : le reliquat ne sera pas livré.</summary>
    public Task<Result> CloseOrderAsync(Guid id, CancellationToken ct = default)
        => TransitionOrderAsync(id, PurchaseOrderStatus.Closed, ct);

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

    // ======================================================================== Réception

    /// <summary>Réception (§24) : partielle possible, contrôle (quantité refusée + motif, jamais en stock),
    /// lots + expiration, saisie en unité d'achat convertie par le serveur. Idempotente par opération.</summary>
    public async Task<Result<Guid>> ReceiveAsync(ReceiveGoodsCommand c, CancellationToken ct = default)
    {
        var existing = await store.FindReceiptByOperationAsync(c.OperationUuid, ct);
        if (existing is { } id) return Result.Success(id);
        if (c.Lines.Count == 0) return Error.Validation("purchasing.no_lines", "Rien à réceptionner.");
        if (c.Lines.Any(l => l.Quantity < 0 || l.QuantityRejected < 0 || l.Quantity + l.QuantityRejected <= 0))
            return Error.Validation("purchasing.receipt_quantity", "Quantité reçue invalide.");
        if (c.Lines.Any(l => l.QuantityRejected > 0 && string.IsNullOrWhiteSpace(l.RejectionReason)))
            return Error.Validation("purchasing.rejection_reason", "Toute quantité refusée doit être justifiée.");
        if (c.Lines.Any(l => l.UnitCost < 0))
            return Error.Validation("purchasing.unit_cost", "Un coût unitaire ne peut être négatif.");
        if (!await store.LocationExistsAsync(c.InventoryLocationId, ct))
            return Error.Validation("purchasing.location_unknown", "Emplacement de réception inconnu.");
        if (await store.SupplierInfoAsync(c.SupplierId, ct) is null)
            return Error.Validation("purchasing.supplier_unknown", "Fournisseur inconnu pour cette entreprise.");

        // Conversion unité d'achat → unité de base, ligne par ligne (acceptée ET refusée).
        var normalized = new List<(ReceiveLine Line, decimal Accepted, decimal Rejected, decimal UnitCost, decimal? PurchaseQty, string Sku)>();
        foreach (var l in c.Lines)
        {
            var product = await store.GetProductAsync(l.ProductId, ct);
            if (product is null || product.CompanyId != Company)
                return Error.Validation("purchasing.product_unknown", "Article inconnu pour cette entreprise.");
            var accepted = await ToBaseAsync(product, l.UnitId, Math.Max(l.Quantity, 0m) + l.QuantityRejected, l.UnitCost, ct);
            if (!accepted.IsSuccess) return accepted.Error!;
            var factor = accepted.Value.Quantity / (l.Quantity + l.QuantityRejected);
            normalized.Add((l, Math.Round(l.Quantity * factor, 6), Math.Round(l.QuantityRejected * factor, 6),
                accepted.Value.UnitPrice, l.UnitId is null ? null : l.Quantity, product.Sku));
        }

        // Résumé fonctionnel §9 : COMMANDE → APPROBATION → RÉCEPTION. Une réception rattachée à une commande exige une
        // commande approuvée, du même fournisseur, et ne dépasse jamais le reste à recevoir (quantité acceptée seulement).
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
            foreach (var group in normalized.Where(n => n.Line.PurchaseOrderLineId is not null).GroupBy(n => n.Line.PurchaseOrderLineId!.Value))
            {
                var poLine = po.Lines.FirstOrDefault(pl => pl.Id == group.Key);
                if (poLine is null)
                    return Error.Validation("purchasing.po_line_mismatch", "Une ligne reçue n'appartient pas à la commande.");
                if (group.Any(n => n.Line.ProductId != poLine.ProductId))
                    return Error.Validation("purchasing.po_line_product", "Le produit reçu ne correspond pas à la ligne de commande.");
                var remaining = poLine.QuantityOrdered - poLine.QuantityReceived;
                var received = group.Sum(n => n.Accepted);
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

        return await Guarded(() => uow.ExecuteInTransactionAsync<Result<Guid>>(async token =>
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
            foreach (var (l, accepted, rejected, unitCost, purchaseQty, sku) in normalized)
            {
                Guid? lotId = null;
                if (accepted > 0 && !string.IsNullOrWhiteSpace(l.LotNumber))
                    lotId = await store.EnsureLotAsync(l.ProductId, l.LotNumber!, l.ExpirationDate,
                        c.SupplierId, DateOnly.FromDateTime(receivedAt.UtcDateTime), token);

                if (accepted > 0)
                {
                    receipt.Lines.Add(new GoodsReceiptLine
                    {
                        Id = Guid.NewGuid(), GoodsReceiptId = receipt.Id, CompanyId = Company,
                        PurchaseOrderLineId = l.PurchaseOrderLineId, ProductId = l.ProductId,
                        QuantityReceived = accepted, UnitCost = unitCost, LotId = lotId,
                        PurchaseUnitId = purchaseQty is null ? null : l.UnitId, PurchaseQuantity = purchaseQty,
                        QuantityRejected = rejected, RejectionReason = rejected > 0 ? l.RejectionReason!.Trim() : null,
                    });
                    movements.Add(new StockMovementRequest(
                        l.ProductId, c.InventoryLocationId, MovementType.Receipt, accepted, unitCost,
                        // L'UUID d'opération est unique par mouvement : seul le premier le porte (ancre d'idempotence).
                        "goods_receipt", receipt.Id, lotId, receivedAt, movements.Count == 0 ? c.OperationUuid : null));
                    await store.AddCostHistoryAsync(new ProductCostHistory
                    {
                        Id = Guid.NewGuid(), CompanyId = Company, ProductId = l.ProductId,
                        SourceType = "goods_receipt", SourceId = receipt.Id, UnitCost = unitCost,
                        CurrencyId = currencyId, Method = "average", EffectiveAt = receivedAt, CreatedAt = clock.UtcNow,
                    }, token);
                    if (l.PurchaseOrderLineId is { } pol)
                        await store.BumpOrderLineReceivedAsync(pol, accepted, token);
                }
                else
                {
                    // Livraison entièrement refusée : tracée sur le bon (quantity_received doit rester > 0 en base,
                    // on la trace donc par une note), aucune entrée en stock.
                    receipt.Notes = $"{receipt.Notes}{(string.IsNullOrEmpty(receipt.Notes) ? "" : " — ")}Refusé : {rejected} × {sku} ({l.RejectionReason!.Trim()})";
                }
            }

            if (receipt.Lines.Count == 0)
                return Error.Validation("purchasing.nothing_accepted",
                    "Aucune quantité acceptée : refusez la livraison auprès du fournisseur sans créer de bon de réception.");

            await store.AddReceiptAsync(receipt, token);
            await stock.PostManyAsync(movements, token);

            if (c.PurchaseOrderId is { } po)
                await store.RefreshOrderStatusAsync(po, token);

            return Result.Success(receipt.Id);
        }, ct));
    }

    // ======================================================================== Facture

    /// <summary>
    /// Facture fournisseur → moteur financier (§19, §25-27, §54). Pièce ET écriture dans une seule transaction
    /// (plus de facture orpheline si l'écriture échoue) ; un rejeu du même identifiant d'opération renvoie la facture
    /// existante au lieu d'en créer une seconde ; taxe calculée par le serveur ; échéance déduite des conditions du
    /// fournisseur si elle n'est pas fournie.
    /// </summary>
    public async Task<Result<RecordPurchaseInvoiceResult>> RecordInvoiceAsync(RecordPurchaseInvoiceCommand c, CancellationToken ct = default)
    {
        if (await store.FindInvoiceByOperationAsync(c.OperationUuid, ct) is { } replayed)
            return Result.Success(new RecordPurchaseInvoiceResult(replayed, null, Guid.Empty, "", WasReplay: true));
        if (c.Lines.Count == 0) return Error.Validation("purchasing.no_lines", "La facture doit contenir au moins une ligne.");
        if (c.Lines.Any(l => l.Quantity <= 0 || l.UnitPrice < 0 || l.Discount < 0 || l.TaxAmount < 0 || l.Discount > l.Quantity * l.UnitPrice))
            return Error.Validation("purchasing.invoice_line", "Ligne de facture invalide (quantité positive, prix, remise et taxe non négatifs).");
        if (c.Lines.Any(l => string.IsNullOrWhiteSpace(l.Description) && l.ProductId is null))
            return Error.Validation("purchasing.invoice_line", "Une ligne sans article doit avoir une description.");
        var supplier = await store.SupplierInfoAsync(c.SupplierId, ct);
        if (supplier is null) return Error.Validation("purchasing.supplier_unknown", "Fournisseur inconnu pour cette entreprise.");
        var currencyId = await store.CurrencyIdOrNullAsync(c.Currency, ct);
        if (currencyId is null) return Error.Validation("purchasing.currency_unknown", $"Devise « {c.Currency} » inconnue.");
        if (c.PurchaseOrderId is { } poId)
        {
            var po = await store.GetOrderAsync(poId, ct);
            if (po is null || po.CompanyId != Company) return Error.NotFound("purchasing.po_not_found", "Commande fournisseur introuvable.");
            if (po.SupplierId != c.SupplierId)
                return Error.Validation("purchasing.po_supplier_mismatch", "La commande appartient à un autre fournisseur.");
        }
        if (!c.OnCredit && c.TenderFinancialAccountId is null)
            return Error.Validation("purchasing.tender_required", "Un compte financier est requis pour un achat comptant.");
        var dueDate = c.DueDate ?? (c.OnCredit && supplier.PaymentTermsDays is { } days ? c.InvoiceDate.AddDays(days) : null);
        if (dueDate is { } due && due < c.InvoiceDate)
            return Error.Validation("purchasing.due_date", "L'échéance ne peut précéder la date de facture.");

        // §53-54 : une référence de facture ne peut être réutilisée pour le même fournisseur.
        if (!string.IsNullOrWhiteSpace(c.SupplierReference)
            && await store.SupplierRefExistsAsync(c.SupplierId, c.SupplierReference!.Trim(), ct))
            return Error.Conflict("purchasing.duplicate_invoice",
                $"Une facture avec la référence « {c.SupplierReference} » existe déjà pour ce fournisseur (§54).");

        var invoiceLines = new List<SupplierInvoiceLineRow>();
        decimal net = 0, tax = 0;
        foreach (var l in c.Lines)
        {
            var lineNet = R2(l.Quantity * l.UnitPrice - l.Discount);
            var lineTax = l.TaxAmount;
            if (l.TaxId is { } taxId)
            {
                var rate = await store.TaxRateAsync(taxId, ct);
                if (rate is null) return Error.Validation("purchasing.tax_unknown", "Taxe inconnue pour cette entreprise.");
                lineTax = R2(lineNet * rate.Value / 100m);
            }
            net += lineNet;
            tax += lineTax;
            invoiceLines.Add(new SupplierInvoiceLineRow(l.ProductId, string.IsNullOrWhiteSpace(l.Description) ? "" : l.Description.Trim(),
                l.Quantity, l.UnitPrice, l.TaxId, l.Discount, lineNet));
        }
        var occurredAt = c.InvoiceDate.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);

        return await Guarded(() => uow.ExecuteInTransactionAsync<Result<RecordPurchaseInvoiceResult>>(async token =>
        {
            var invoiceId = await store.CreateSupplierInvoiceAsync(new SupplierInvoiceRow(
                Guid.NewGuid(), Company, c.SupplierId, c.PurchaseOrderId,
                await store.NextNumberAsync("supplier_invoice", token),
                c.InvoiceDate, dueDate, currencyId.Value,
                net, tax, net + tax, c.OnCredit ? "validated" : "paid",
                string.IsNullOrWhiteSpace(c.SupplierReference) ? null : c.SupplierReference!.Trim(), c.GoodsReceiptId), invoiceLines, token);

            var posted = c.OnCredit
                ? await finance.RecordSupplierInvoiceAsync(new RecordSupplierInvoiceRequest(
                    c.OperationUuid, invoiceId, c.SupplierId, occurredAt, net, tax, c.Currency, dueDate,
                    c.CapitaliseToInventory), token)
                : await finance.RecordCashPurchaseAsync(new RecordCashPurchaseRequest(
                    c.OperationUuid, invoiceId, occurredAt, net, tax, c.Currency, c.TenderFinancialAccountId!.Value,
                    c.CapitaliseToInventory), token);

            // L'écriture a échoué : on annule toute la transaction (pas de facture sans écriture).
            if (!posted.IsSuccess) throw new InvalidOperationException(posted.Error!.Message);
            var p = posted.Value!;

            // Commande entièrement reçue et désormais facturée.
            if (c.PurchaseOrderId is { } po)
                await store.SetOrderStatusIfAsync(po, PurchaseOrderStatus.Received, PurchaseOrderStatus.Invoiced, token);

            return Result.Success(new RecordPurchaseInvoiceResult(invoiceId, p.PayableId, p.JournalEntryId, p.EntryNumber, p.WasReplay));
        }, ct));
    }

    // ======================================================================== Retour

    /// <summary>Retour fournisseur (§28) : sortie de stock + impact dette / avoir, motif obligatoire.</summary>
    public async Task<Result<Guid>> RecordReturnAsync(RecordSupplierReturnCommand c, CancellationToken ct = default)
    {
        if (c.Lines.Count == 0) return Error.Validation("purchasing.no_lines", "Aucune ligne à retourner.");
        if (string.IsNullOrWhiteSpace(c.Reason))
            return Error.Validation("purchasing.reason_required", "Le motif du retour est obligatoire.");
        if (c.Lines.Any(l => l.Quantity <= 0 || l.UnitPrice < 0 || l.TaxAmount < 0))
            return Error.Validation("purchasing.return_line", "Ligne de retour invalide (quantité positive, prix et taxe non négatifs).");
        if (!await store.LocationExistsAsync(c.InventoryLocationId, ct))
            return Error.Validation("purchasing.location_unknown", "Emplacement inconnu.");
        if (await store.SupplierInfoAsync(c.SupplierId, ct) is null)
            return Error.Validation("purchasing.supplier_unknown", "Fournisseur inconnu pour cette entreprise.");
        var currencyId = await store.CurrencyIdOrNullAsync(c.Currency, ct);
        if (currencyId is null) return Error.Validation("purchasing.currency_unknown", $"Devise « {c.Currency} » inconnue.");

        // Retour d'une réception : jamais plus que ce qui a été reçu (moins les retours déjà faits).
        if (c.GoodsReceiptId is { } receiptId)
        {
            var receipt = await store.ReceiptReturnableAsync(receiptId, ct);
            if (receipt is null) return Error.Validation("purchasing.receipt_unknown", "Réception inconnue.");
            if (receipt.Value.SupplierId != c.SupplierId)
                return Error.Validation("purchasing.receipt_supplier", "Cette réception appartient à un autre fournisseur.");
            foreach (var g in c.Lines.GroupBy(l => l.ProductId))
            {
                var left = receipt.Value.Returnable.GetValueOrDefault(g.Key);
                if (g.Sum(l => l.Quantity) > left)
                    return Error.Validation("purchasing.return_exceeds_receipt",
                        $"Quantité retournée supérieure au reçu restant sur cette réception ({left}).");
            }
        }

        decimal net = R2(c.Lines.Sum(l => l.Quantity * l.UnitPrice));
        decimal tax = R2(c.Lines.Sum(l => l.TaxAmount));

        // Retour imputé sur une dette : même fournisseur, même devise, et l'avoir ne dépasse pas le reste à payer.
        if (c.PayableId is { } payableId)
        {
            var payable = await store.GetPayablePaymentInfoAsync(payableId, ct);
            if (payable is null) return Error.Validation("purchasing.payable_unknown", "Dette inconnue.");
            if (payable.SupplierId != c.SupplierId)
                return Error.Validation("purchasing.payable_supplier", "Cette dette appartient à un autre fournisseur.");
            if (!string.Equals(payable.Currency, c.Currency, StringComparison.OrdinalIgnoreCase))
                return Error.Validation("purchasing.payable_currency", "La devise du retour diffère de celle de la dette.");
            if (payable.Status is "PAID" or "CANCELLED")
                return Error.Conflict("purchasing.payable_closed", "Cette dette est soldée : l'avoir ne peut plus y être imputé.");
            if (net + tax > payable.RemainingAmount)
                return Error.Validation("purchasing.return_exceeds_payable",
                    $"Le retour ({net + tax}) dépasse le reste à payer de la dette ({payable.RemainingAmount}).");
        }

        return await Guarded(() => uow.ExecuteInTransactionAsync<Result<Guid>>(async token =>
        {
            var now = clock.UtcNow;

            var ret = new Domain.Purchasing.SupplierReturn
            {
                Id = Guid.NewGuid(),
                CompanyId = Company,
                SupplierId = c.SupplierId,
                GoodsReceiptId = c.GoodsReceiptId,
                PayableId = c.PayableId,
                WarehouseLocationId = c.InventoryLocationId,
                DocumentNumber = await store.NextNumberAsync("supplier_return", token),
                Status = "posted",
                Reason = c.Reason.Trim(),
                CurrencyId = currencyId.Value,
                Subtotal = net,
                TaxTotal = tax,
                Total = net + tax,
                ReturnedAt = now,
                CreatedBy = tenant.UserId,
            };
            var movements = new List<StockMovementRequest>();
            foreach (var l in c.Lines)
            {
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
            if (!post.IsSuccess) throw new InvalidOperationException(post.Error!.Message);

            return Result.Success(ret.Id);
        }, ct));
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
