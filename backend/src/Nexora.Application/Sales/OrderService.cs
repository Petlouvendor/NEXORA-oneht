using Microsoft.Extensions.Logging;
using Nexora.Application.Abstractions;
using Nexora.Application.Customers;
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
    /// <summary>Phase 4 : devise connue (sinon null — jamais d'exception « séquence vide »).</summary>
    Task<Guid?> CurrencyIdOrNullAsync(string code, CancellationToken ct = default);
    /// <summary>Emplacement actif de l'entreprise.</summary>
    Task<bool> LocationExistsAsync(Guid locationId, CancellationToken ct = default);
    /// <summary>Statut du client de l'entreprise (null si inconnu).</summary>
    Task<string?> CustomerStatusAsync(Guid customerId, CancellationToken ct = default);
    /// <summary>Recalcule l'établissement de la commande depuis son emplacement (branche, V071).</summary>
    Task SyncEstablishmentAsync(Guid orderId, CancellationToken ct = default);
    /// <summary>Supprime les lignes d'un brouillon avant leur remplacement.</summary>
    Task RemoveLinesAsync(SalesOrder order, CancellationToken ct = default);
    /// <summary>Nouvelles lignes d'un brouillon modifié (clé fixée côté application : ajout explicite).</summary>
    Task AddLinesAsync(IEnumerable<SalesOrderLine> lines, CancellationToken ct = default);
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
    ICustomerCreditMemoWriter creditMemos,
    IUnitOfWork uow,
    ITenantContext tenant,
    IDateTimeProvider clock,
    ILogger<OrderService> logger, IBusinessCalendar calendar)
{
    private Guid Company => tenant.ActiveCompanyId
        ?? throw TenantErrors.ActiveCompanyRequired();

    /// <summary>Commande saisie par un client : prix forcés et remises contrôlés (recette Phase 55).</summary>
    public Task<Result<Guid>> CreateOrderAsync(CreateOrderRequest r, CancellationToken ct = default)
        => CreateOrderCoreAsync(r, guardClientPricing: true, ct);

    /// <summary>Conversion d'un devis : ses prix ont déjà été contrôlés à la création du devis.</summary>
    internal Task<Result<Guid>> CreateOrderFromQuoteAsync(CreateOrderRequest r, CancellationToken ct = default)
        => CreateOrderCoreAsync(r, guardClientPricing: false, ct);

    private async Task<Result<Guid>> CreateOrderCoreAsync(CreateOrderRequest r, bool guardClientPricing, CancellationToken ct)
    {
        var valid = await ValidateHeaderAsync(r, requireLocation: guardClientPricing, ct);
        if (!valid.IsSuccess) return valid.Error!;

        var order = new SalesOrder
        {
            Id = Guid.NewGuid(),
            CompanyId = Company,
            CustomerId = r.CustomerId,
            DocumentNumber = await store.NextNumberAsync("sales_order", ct),
            OrderType = r.OrderType,
            Status = SalesOrderStatus.Draft,
            CurrencyId = valid.Value,
            InventoryLocationId = r.InventoryLocationId,
            OnCredit = r.OnCredit,
            ExpectedAt = r.ExpectedAt,
            DueDate = r.DueDate,
            DeliveryAddress = Clean(r.DeliveryAddress),
            Notes = Clean(r.Notes),
            CreatedBy = tenant.UserId,
        };
        var priced = await PriceLinesAsync(order, r, guardClientPricing, ct);
        if (!priced.IsSuccess) return priced.Error!;

        await store.AddOrderAsync(order, ct);
        await uow.SaveChangesAsync(ct);
        return Result.Success(order.Id);
    }

    /// <summary>
    /// Modification d'une commande encore en brouillon (phase 4) : en-tête et lignes remplacés, prix
    /// recalculés et contrôlés comme à la création. Une commande confirmée ne se modifie plus.
    /// </summary>
    public async Task<Result> UpdateOrderAsync(Guid id, CreateOrderRequest r, CancellationToken ct = default)
    {
        var order = await store.GetOrderAsync(id, ct);
        if (order is null || order.CompanyId != Company) return Error.NotFound("order.not_found", "Commande introuvable.");
        if (order.Status != SalesOrderStatus.Draft)
            return Error.Conflict("order.locked", $"La commande {order.DocumentNumber} est « {order.Status} » : elle n'est plus modifiable.");
        if (order.DepositAmount > 0m && r.CustomerId != order.CustomerId)
            return Error.Conflict("order.deposit_customer", "Un acompte a été versé : le client de la commande ne peut plus changer.");
        var valid = await ValidateHeaderAsync(r, requireLocation: true, ct);
        if (!valid.IsSuccess) return valid.Error!;

        var locationChanged = order.InventoryLocationId != r.InventoryLocationId;
        order.CustomerId = r.CustomerId;
        order.OrderType = r.OrderType;
        order.CurrencyId = valid.Value;
        order.InventoryLocationId = r.InventoryLocationId;
        order.OnCredit = r.OnCredit;
        order.ExpectedAt = r.ExpectedAt;
        order.DueDate = r.DueDate;
        order.DeliveryAddress = Clean(r.DeliveryAddress);
        order.Notes = Clean(r.Notes);
        order.Subtotal = order.DiscountTotal = order.TaxTotal = order.Total = 0m;
        await store.RemoveLinesAsync(order, ct);
        var priced = await PriceLinesAsync(order, r, guardClientPricing: true, ct);
        if (!priced.IsSuccess) return priced.Error!;
        await store.AddLinesAsync(order.Lines, ct);
        if (order.DepositAmount > order.Total)
            return Error.Validation("order.deposit_exceeds", "Le total de la commande deviendrait inférieur à l'acompte déjà versé.");

        await uow.SaveChangesAsync(ct);
        if (locationChanged) await store.SyncEstablishmentAsync(order.Id, ct);
        return Result.Success();
    }

    private async Task<Result<Guid>> ValidateHeaderAsync(CreateOrderRequest r, bool requireLocation, CancellationToken ct)
    {
        if (r.Lines is null || r.Lines.Count == 0) return Error.Validation("order.no_lines", "Une commande doit comporter au moins une ligne.");
        if (!SalesOrderType.All.Contains(r.OrderType))
            return Error.Validation("order.type", $"Type de commande « {r.OrderType} » inconnu.");
        if (r.OnCredit && r.CustomerId is null)
            return Error.Validation("order.customer_required", "Un client est obligatoire pour une commande à crédit (§19).");
        if (r.CustomerId is { } c)
        {
            var status = await store.CustomerStatusAsync(c, ct);
            if (status is null) return Error.Validation("order.customer_unknown", "Client inconnu.");
            if (status is "archived" or "inactive")
                return Error.Conflict("order.customer_inactive", $"Ce client est « {status} » : aucune commande possible.");
        }
        if (r.Lines.Any(l => l.Quantity <= 0m))
            return Error.Validation("order.quantity", "Chaque quantité commandée doit être positive.");
        if (r.Lines.Any(l => l.LineDiscount < 0m || l.UnitPriceOverride < 0m))
            return Error.Validation("order.line_amount", "Prix et remises ne peuvent être négatifs.");
        if (r.Lines.GroupBy(l => l.ProductId).Any(g => g.Count() > 1))
            return Error.Validation("order.duplicate_line", "Un même article apparaît deux fois : regroupez les quantités.");
        if (r.DueDate is { } due && due < calendar.Today.AddDays(-1))
            return Error.Validation("order.due_date", "L'échéance est déjà passée.");
        if (r.InventoryLocationId is { } loc)
        {
            if (!await store.LocationExistsAsync(loc, ct)) return Error.Validation("order.location_unknown", "Emplacement de stock inconnu.");
        }
        else if (requireLocation)
            return Error.Validation("order.no_location", "Choisissez l'emplacement de stock qui servira la commande.");
        var currency = await store.CurrencyIdOrNullAsync(r.Currency, ct);
        return currency is { } id ? Result.Success(id) : Error.Validation("order.currency_unknown", $"Devise « {r.Currency} » inconnue.");
    }

    private async Task<Result> PriceLinesAsync(SalesOrder order, CreateOrderRequest r, bool guardClientPricing, CancellationToken ct)
    {
        var at = clock.UtcNow;
        var requested = r.Lines.Select(l => new CheckoutLine(l.ProductId, l.Quantity, l.UnitPriceOverride, l.LineDiscount, l.TaxId)).ToList();
        if (guardClientPricing && await pricingGuard.ValidateProductsAsync(requested, ct) is { IsSuccess: false } unsellable)
            return unsellable.Error!;
        var priced = await pricing.PriceCartAsync(r.InventoryLocationId ?? Guid.Empty, r.CustomerId, r.Currency, at, requested, null, ct);
        if (guardClientPricing && await pricingGuard.ValidateAsync(requested, priced, r.Currency, at, null, ct) is { IsSuccess: false } refused)
            return refused.Error!;

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
        return Result.Success();
    }

    private static string? Clean(string? v) => string.IsNullOrWhiteSpace(v) ? null : v.Trim();

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
        var amount = Round(r.Amount);
        try
        {
            // Encaissement + avoir « acompte » dans la même transaction : l'acompte apparaît au compte du
            // client et sera imputé sur la vente à l'exécution (V083).
            return await uow.ExecuteInTransactionAsync<Result>(async token =>
            {
                var posted = await finance.RecordCustomerPaymentAsync(new RecordPaymentRequest(
                    r.OperationUuid, clock.UtcNow, customer, amount, currency, r.TenderFinancialAccountId,
                    Array.Empty<AllocationInput>(), $"Acompte commande {order.DocumentNumber}"), token);
                if (!posted.IsSuccess) throw new InvalidOperationException(posted.Error!.Message);
                if (posted.Value!.WasReplay) return Result.Success();   // même opération rejouée : déjà enregistrée

                await creditMemos.IssueForOrderDepositAsync(customer, posted.Value.PaymentId!.Value, order.Id, order.CurrencyId,
                    amount, order.DocumentNumber, token);
                order.DepositAmount = Round(order.DepositAmount + amount);
                order.DepositPaymentId = posted.Value.PaymentId;
                await uow.SaveChangesAsync(token);
                logger.LogInformation("Acompte {Amount} enregistré sur la commande {Order}", amount, order.DocumentNumber);
                return Result.Success();
            }, ct);
        }
        catch (InvalidOperationException ex)
        {
            return Error.Validation("order.deposit_failed", ex.Message);
        }
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

        if (r.OnCredit && order.CustomerId is null)
            return Error.Validation("order.customer_required", "Un client est obligatoire pour livrer à crédit.");
        if (r.Tenders.Any(t => t.Amount <= 0m))
            return Error.Validation("order.tender_amount", "Chaque montant encaissé doit être positif.");

        var currency = await CurrencyCode(order.CurrencyId, ct);
        var reserved = order.ReservedAt is not null;
        // Acompte encore disponible : imputé sur la vente (plafonné à son total).
        var deposit = order.CustomerId is null ? 0m : await creditMemos.OrderDepositAvailableAsync(order.Id, ct);
        // Livraison au comptant : paiement vérifié AVANT la vente (sinon le reste non payé passerait par le contrôle
        // de crédit et l'utilisateur lirait « client bloqué » au lieu de « il reste X à encaisser »). Le contrôle
        // exact, sur le total recalculé, est refait après la vente.
        if (!r.OnCredit)
        {
            var expected = toFulfil.Sum(x => RoundShare(x.Line.LineTotal, x.Qty, x.Line.Quantity));
            var missing = Round(expected - Math.Min(deposit, expected) - r.Tenders.Sum(t => t.Amount));
            if (missing > 0.05m)
                return Error.Validation("order.payment_incomplete",
                    $"Paiement incomplet : il reste {missing} {await CurrencyCode(order.CurrencyId, ct)} à encaisser (ou livrez à crédit).");
        }

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
                    r.Tenders.Sum(t => t.Amount), r.CreditDueDate ?? order.DueDate, null, null, clock.UtcNow,
                    StoreCreditAmount: deposit, StoreCreditSalesOrderId: order.Id, CapStoreCreditToTotal: true);

                var sale = await sales.CheckoutFrozenPricesAsync(checkout, token);
                if (!sale.IsSuccess) throw new InvalidOperationException(sale.Error!.Message);
                // Livraison au comptant : le reste (après acompte) doit être entièrement encaissé.
                if (!r.OnCredit && sale.Value!.CreditAmount > 0m)
                    throw new PaymentIncompleteException(sale.Value.CreditAmount, currency);

                await store.LinkSaleToOrderAsync(sale.Value!.SaleId, order.Id, token);
                foreach (var (line, qty) in toFulfil) line.QuantityFulfilled = Round(line.QuantityFulfilled + qty);

                var complete = order.Lines.All(l => l.QuantityOutstanding <= 0m);
                order.Status = complete ? SalesOrderStatus.Fulfilled : SalesOrderStatus.PartiallyFulfilled;
                if (complete) order.FulfilledAt = clock.UtcNow;
                await uow.SaveChangesAsync(token);
                return sale;
            }, ct);
        }
        catch (PaymentIncompleteException ex)
        {
            return Error.Validation("order.payment_incomplete",
                $"Paiement incomplet : il reste {ex.Missing} {ex.Currency} à encaisser (ou livrez à crédit).");
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
            // Une commande réservée puis partiellement livrée garde son reliquat réservé : il est libéré aussi.
            if (order.ReservedAt is not null && order.InventoryLocationId is { } loc)
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

    private sealed class PaymentIncompleteException(decimal missing, string currency)
        : InvalidOperationException("Paiement incomplet.")
    {
        public decimal Missing { get; } = missing;
        public string Currency { get; } = currency;
    }

    private static decimal Round(decimal v) => Math.Round(v, 2, MidpointRounding.ToEven);
    private static decimal RoundShare(decimal total, decimal part, decimal whole)
        => whole <= 0m ? 0m : Math.Round(total * part / whole, 2, MidpointRounding.ToEven);
}
