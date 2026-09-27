using Microsoft.Extensions.Logging;
using Nexora.Application.Abstractions;
using Nexora.Application.Finance;
using Nexora.Application.Inventory;
using Nexora.Contracts.Finance;
using Nexora.Contracts.Sales;
using Nexora.Domain.Common;
using Nexora.Domain.Sales;

namespace Nexora.Application.Sales;

/// <summary>Persistance des commandes clients (Phase 25 §18).</summary>
public interface IOrderStore
{
    Task<Guid> ResolveCurrencyIdAsync(string code, CancellationToken ct = default);
    Task<string> CurrencyCodeAsync(Guid currencyId, CancellationToken ct = default);
    Task<string> NextNumberAsync(string documentType, CancellationToken ct = default);
    Task AddOrderAsync(SalesOrder order, CancellationToken ct = default);
    Task<SalesOrder?> GetOrderAsync(Guid id, CancellationToken ct = default);
    Task<IReadOnlyList<Guid>> SaleIdsForOrderAsync(Guid orderId, CancellationToken ct = default);
    Task LinkSaleToOrderAsync(Guid saleId, Guid orderId, CancellationToken ct = default);
    Task<bool> ProductRequiresStockAsync(Guid productId, CancellationToken ct = default);
}

/// <summary>
/// Commandes clients : lignes figées au prix du jour (§9), réservation de stock (§18, §38),
/// acompte (§18-19), puis exécution qui crée la vente réelle via <see cref="SalesService"/>
/// pour la quantité livrée — libère d'abord la réservation, puis décrémente le stock.
/// </summary>
public sealed class OrderService(
    IOrderStore store,
    ISalesPricing pricing,
    ISalePricingGuard pricingGuard,
    IStockLedger stock,
    SalesService sales,
    IFinanceOperations finance,
    IUnitOfWork uow,
    ITenantContext tenant,
    IDateTimeProvider clock,
    ILogger<OrderService> logger)
{
    private Guid Company => tenant.ActiveCompanyId
        ?? throw new InvalidOperationException("Aucune entreprise active.");

    /// <summary>Commande saisie par un client : prix forcés et remises contrôlés (recette Phase 55).</summary>
    public Task<Result<Guid>> CreateOrderAsync(CreateOrderRequest r, CancellationToken ct = default)
        => CreateOrderCoreAsync(r, guardClientPricing: true, ct);

    /// <summary>Conversion d'un devis : ses prix ont déjà été contrôlés à la création du devis.</summary>
    internal Task<Result<Guid>> CreateOrderFromQuoteAsync(CreateOrderRequest r, CancellationToken ct = default)
        => CreateOrderCoreAsync(r, guardClientPricing: false, ct);

    private async Task<Result<Guid>> CreateOrderCoreAsync(CreateOrderRequest r, bool guardClientPricing, CancellationToken ct)
    {
        if (r.Lines.Count == 0) return Error.Validation("order.no_lines", "Une commande doit comporter au moins une ligne.");
        if (!SalesOrderType.All.Contains(r.OrderType))
            return Error.Validation("order.type", $"Type de commande « {r.OrderType} » inconnu.");
        if (r.OnCredit && r.CustomerId is null)
            return Error.Validation("order.customer_required", "Un client est obligatoire pour une commande à crédit (§19).");

        var at = clock.UtcNow;
        var requested = r.Lines.Select(l => new CheckoutLine(l.ProductId, l.Quantity, l.UnitPriceOverride, l.LineDiscount, l.TaxId)).ToList();
        if (guardClientPricing && await pricingGuard.ValidateProductsAsync(requested, ct) is { IsSuccess: false } unsellable)
            return unsellable.Error!;
        var priced = await pricing.PriceCartAsync(r.InventoryLocationId ?? Guid.Empty, r.CustomerId, r.Currency, at, requested, null, ct);
        if (guardClientPricing && await pricingGuard.ValidateAsync(requested, priced, r.Currency, at, null, ct) is { IsSuccess: false } refused)
            return refused.Error!;

        var order = new SalesOrder
        {
            Id = Guid.NewGuid(),
            CompanyId = Company,
            CustomerId = r.CustomerId,
            DocumentNumber = await store.NextNumberAsync("sales_order", ct),
            OrderType = r.OrderType,
            Status = SalesOrderStatus.Draft,
            CurrencyId = await store.ResolveCurrencyIdAsync(r.Currency, ct),
            InventoryLocationId = r.InventoryLocationId,
            OnCredit = r.OnCredit,
            ExpectedAt = r.ExpectedAt,
            DueDate = r.DueDate,
            DeliveryAddress = r.DeliveryAddress,
            Notes = r.Notes,
            CreatedBy = tenant.UserId,
        };

        foreach (var p in priced)
        {
            var gross = Round(p.UnitPrice * p.Quantity);
            var disc = Round(p.ManualDiscount + p.PromotionDiscount);
            var net = gross - disc;
            var tax = Round(net * p.TaxRate / 100m);
            order.Subtotal += gross;
            order.DiscountTotal += disc;
            order.TaxTotal += tax;
            order.Lines.Add(new SalesOrderLine
            {
                Id = Guid.NewGuid(), SalesOrderId = order.Id, CompanyId = Company, ProductId = p.ProductId,
                Description = p.Description, Quantity = p.Quantity, UnitPrice = p.UnitPrice,
                Discount = disc, TaxId = p.TaxId, TaxAmount = tax, LineTotal = Round(net + tax), UnitCost = p.UnitCost,
            });
        }
        order.Total = Round(order.Subtotal - order.DiscountTotal + order.TaxTotal);

        await store.AddOrderAsync(order, ct);
        await uow.SaveChangesAsync(ct);
        return Result.Success(order.Id);
    }

    public Task<Result> ConfirmOrderAsync(Guid id, CancellationToken ct = default)
        => TransitionAsync(id, SalesOrderStatus.Confirmed, ct);

    public async Task<Result> ReserveOrderAsync(Guid id, CancellationToken ct = default)
    {
        var order = await store.GetOrderAsync(id, ct);
        if (order is null) return Error.NotFound("order.not_found", "Commande introuvable.");
        if (order.Status is SalesOrderStatus.Reserved) return Result.Success();
        if (!SalesOrderStatus.CanTransition(order.Status, SalesOrderStatus.Reserved))
            return Error.Conflict("order.transition", $"Réservation impossible depuis l'état « {order.Status} ».");
        if (order.InventoryLocationId is not { } loc)
            return Error.Validation("order.no_location", "Un emplacement de stock est requis pour réserver (§38).");

        try
        {
            return await uow.ExecuteInTransactionAsync<Result>(async token =>
            {
                foreach (var line in order.Lines.Where(l => l.QuantityOutstanding > 0m))
                {
                    if (!await store.ProductRequiresStockAsync(line.ProductId, token)) continue;
                    var available = await stock.AvailableAsync(line.ProductId, loc, token);
                    if (line.QuantityOutstanding > available)
                        throw new InvalidOperationException(
                            $"Stock disponible insuffisant pour {line.Description} : {available} < {line.QuantityOutstanding}.");
                    await stock.ReserveAsync(line.ProductId, loc, line.QuantityOutstanding, token);
                }
                order.Status = SalesOrderStatus.Reserved;
                order.ReservedAt = clock.UtcNow;
                await uow.SaveChangesAsync(token);
                return Result.Success();
            }, ct);
        }
        catch (InvalidOperationException ex)
        {
            return Error.Conflict("order.insufficient_stock", ex.Message);
        }
    }

    public async Task<Result> RecordDepositAsync(Guid id, OrderDepositRequest r, CancellationToken ct = default)
    {
        if (r.Amount <= 0m) return Error.Validation("order.deposit_amount", "Le montant de l'acompte doit être positif.");
        var order = await store.GetOrderAsync(id, ct);
        if (order is null) return Error.NotFound("order.not_found", "Commande introuvable.");
        if (order.Status is SalesOrderStatus.Cancelled or SalesOrderStatus.Fulfilled)
            return Error.Conflict("order.closed", "Commande clôturée.");
        if (order.CustomerId is not { } customer)
            return Error.Validation("order.customer_required", "Un client est requis pour enregistrer un acompte.");
        if (r.Amount > order.RemainingBalance)
            return Error.Validation("order.deposit_exceeds", "L'acompte dépasse le solde restant de la commande.");

        var currency = await CurrencyCode(order.CurrencyId, ct);
        var posted = await finance.RecordCustomerPaymentAsync(new RecordPaymentRequest(
            r.OperationUuid, clock.UtcNow, customer, r.Amount, currency, r.TenderFinancialAccountId,
            Array.Empty<AllocationInput>(), $"Acompte commande {order.DocumentNumber}"), ct);
        if (!posted.IsSuccess) return posted.Error!;

        order.DepositAmount = Round(order.DepositAmount + r.Amount);
        order.DepositPaymentId = posted.Value!.PaymentId;
        await uow.SaveChangesAsync(ct);
        logger.LogInformation("Acompte {Amount} enregistré sur la commande {Order}", r.Amount, order.DocumentNumber);
        return Result.Success();
    }

    public async Task<Result<CheckoutResult>> CompleteOrderAsync(Guid id, CompleteOrderRequest r, CancellationToken ct = default)
    {
        var order = await store.GetOrderAsync(id, ct);
        if (order is null) return Error.NotFound("order.not_found", "Commande introuvable.");
        if (order.Status is not (SalesOrderStatus.Confirmed or SalesOrderStatus.Reserved or SalesOrderStatus.PartiallyFulfilled))
            return Error.Conflict("order.not_completable", $"La commande doit être confirmée avant exécution (état « {order.Status} »).");
        if (order.InventoryLocationId is not { } loc)
            return Error.Validation("order.no_location", "Un emplacement de stock est requis pour l'exécution.");

        var toFulfil = new List<(SalesOrderLine Line, decimal Qty)>();
        foreach (var line in order.Lines)
        {
            var qty = r.LineQuantities is { } m && m.TryGetValue(line.Id, out var q) ? q : line.QuantityOutstanding;
            if (qty <= 0m) continue;
            if (qty > line.QuantityOutstanding)
                return Error.Validation("order.over_fulfil", $"Quantité livrée supérieure au reliquat pour {line.Description}.");
            toFulfil.Add((line, qty));
        }
        if (toFulfil.Count == 0) return Error.Validation("order.nothing_to_fulfil", "Aucune quantité à livrer.");

        var currency = await CurrencyCode(order.CurrencyId, ct);
        var reserved = order.ReservedAt is not null;

        try
        {
            return await uow.ExecuteInTransactionAsync<Result<CheckoutResult>>(async token =>
            {
                // La réservation doit être levée avant la vente, sinon le contrôle de
                // disponibilité de la caisse la compterait comme indisponible (§38).
                if (reserved)
                    foreach (var (line, qty) in toFulfil)
                        if (await store.ProductRequiresStockAsync(line.ProductId, token))
                            await stock.ReleaseAsync(line.ProductId, loc, qty, token);

                var checkout = new CheckoutCommand(
                    r.OperationUuid, loc, null, null, r.CashSessionId, order.CustomerId, currency,
                    toFulfil.Select(x => new CheckoutLine(
                        x.Line.ProductId, x.Qty, x.Line.UnitPrice, RoundShare(x.Line.Discount, x.Qty, x.Line.Quantity), x.Line.TaxId)).ToList(),
                    r.Tenders.Select(t => new TenderInput(t.FinancialAccountId, t.PaymentMethodId, t.Amount, t.Reference)).ToList(),
                    r.Tenders.Sum(t => t.Amount), r.CreditDueDate ?? order.DueDate, null, null, clock.UtcNow);

                var sale = await sales.CheckoutFrozenPricesAsync(checkout, token);
                if (!sale.IsSuccess) throw new InvalidOperationException(sale.Error!.Message);

                await store.LinkSaleToOrderAsync(sale.Value!.SaleId, order.Id, token);
                foreach (var (line, qty) in toFulfil) line.QuantityFulfilled = Round(line.QuantityFulfilled + qty);

                var complete = order.Lines.All(l => l.QuantityOutstanding <= 0m);
                order.Status = complete ? SalesOrderStatus.Fulfilled : SalesOrderStatus.PartiallyFulfilled;
                if (complete) order.FulfilledAt = clock.UtcNow;
                await uow.SaveChangesAsync(token);
                return sale;
            }, ct);
        }
        catch (InvalidOperationException ex)
        {
            return ex.Message.Contains("Stock", StringComparison.OrdinalIgnoreCase)
                ? Error.Conflict("order.insufficient_stock", ex.Message)
                : Error.Validation("order.complete_failed", ex.Message);
        }
    }

    public async Task<Result> CancelOrderAsync(Guid id, string reason, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(reason))
            return Error.Validation("order.reason_required", "Un motif d'annulation est obligatoire (§29).");
        var order = await store.GetOrderAsync(id, ct);
        if (order is null) return Error.NotFound("order.not_found", "Commande introuvable.");
        if (order.Status is SalesOrderStatus.Fulfilled)
            return Error.Conflict("order.fulfilled", "Une commande exécutée s'annule par un retour (§29).");
        if (order.Status is SalesOrderStatus.Cancelled) return Result.Success();

        return await uow.ExecuteInTransactionAsync<Result>(async token =>
        {
            if (order.Status is SalesOrderStatus.Reserved && order.InventoryLocationId is { } loc)
                foreach (var line in order.Lines.Where(l => l.QuantityOutstanding > 0m))
                    if (await store.ProductRequiresStockAsync(line.ProductId, token))
                        await stock.ReleaseAsync(line.ProductId, loc, line.QuantityOutstanding, token);
            order.Status = SalesOrderStatus.Cancelled;
            order.CancelledReason = reason.Trim();
            await uow.SaveChangesAsync(token);
            return Result.Success();
        }, ct);
    }

    private async Task<Result> TransitionAsync(Guid id, string target, CancellationToken ct)
    {
        var order = await store.GetOrderAsync(id, ct);
        if (order is null) return Error.NotFound("order.not_found", "Commande introuvable.");
        if (order.Status == target) return Result.Success();
        if (!SalesOrderStatus.CanTransition(order.Status, target))
            return Error.Conflict("order.transition", $"Transition interdite : {order.Status} → {target}.");
        order.Status = target;
        await uow.SaveChangesAsync(ct);
        return Result.Success();
    }

    private Task<string> CurrencyCode(Guid currencyId, CancellationToken ct) => store.CurrencyCodeAsync(currencyId, ct);

    private static decimal Round(decimal v) => Math.Round(v, 2, MidpointRounding.ToEven);
    private static decimal RoundShare(decimal total, decimal part, decimal whole)
        => whole <= 0m ? 0m : Math.Round(total * part / whole, 2, MidpointRounding.ToEven);
}
