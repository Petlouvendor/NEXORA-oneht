using Microsoft.Extensions.Logging;
using Nexora.Application.Abstractions;
using Nexora.Application.Finance.Engine;
using Nexora.Application.Finance.Events;
using Nexora.Application.Inventory;
using Nexora.Domain.Common;
using Nexora.Domain.Finance;
using Nexora.Domain.Inventory;
using Nexora.Domain.Sales;

namespace Nexora.Application.Sales;

/// <summary>
/// Moteur de vente POS (Phase 6). Une vente est saisie une fois puis propagée de façon
/// atomique vers stock, caisse, créances, finance et comptabilité (§2, §19, §39).
/// </summary>
public sealed class SalesService(
    ISalesStore store,
    ISalesPricing pricing,
    ICreditControl credit,
    ICashSessionStore sessions,
    IStockLedger stock,
    IRecipeExpansion recipes,
    IFinancialEngine engine,
    Customers.ICustomerCreditMemoWriter creditMemos,
    Nexora.Application.Inventory.IStockAvailabilityPolicy stockPolicy,
    ISalePricingGuard pricingGuard,
    IPermissionService permissions,
    Administration.ISettingsService settings,
    IUnitOfWork uow,
    ITenantContext tenant,
    IDateTimeProvider clock,
    ILogger<SalesService> logger)
{
    private Guid Company => tenant.ActiveCompanyId
        ?? throw new InvalidOperationException("Aucune entreprise active.");

    /// <summary>Vente saisie par un client (POS, API, synchronisation hors ligne, restaurant) : les
    /// montants saisis passent par <see cref="ISalePricingGuard"/> (recette Phase 55).</summary>
    public Task<Result<CheckoutResult>> CheckoutAsync(CheckoutCommand cmd, CancellationToken ct = default)
        => CheckoutCoreAsync(cmd, guardClientPricing: true, ct);

    /// <summary>Exécution d'une commande : ses prix ont été figés — et contrôlés — à la saisie de la
    /// commande ou du devis d'origine ; les rejouer ici exigerait à tort <c>pos.change_price</c>
    /// de la personne qui livre. Non exposé hors de la couche Application.</summary>
    internal Task<Result<CheckoutResult>> CheckoutFrozenPricesAsync(CheckoutCommand cmd, CancellationToken ct = default)
        => CheckoutCoreAsync(cmd, guardClientPricing: false, ct);

    private async Task<Result<CheckoutResult>> CheckoutCoreAsync(CheckoutCommand cmd, bool guardClientPricing, CancellationToken ct)
    {
        if (cmd.Lines.Count == 0) return Error.Validation("pos.empty_cart", "Le panier est vide.");

        var existing = await store.FindSaleByOperationAsync(cmd.OperationUuid, ct);
        if (existing is { } dup)
        {
            var s = await store.GetSaleWithLinesAsync(dup, ct);
            return Result.Success(Describe(s!));
        }

        var soldAt = cmd.SoldAt ?? clock.UtcNow;
        var granted = guardClientPricing ? await permissions.CurrentPermissionsAsync(tenant, ct) : [];
        if (guardClientPricing)
        {
            // Guide §25 : produit inexistant, inactif ou non vendable → refus avant tout calcul.
            if (await pricingGuard.ValidateProductsAsync(cmd.Lines, ct) is { IsSuccess: false } unsellable)
                return unsellable.Error!;
            // Guide §13, §25 : la vente est rattachée à SA session de caisse, ouverte.
            if (cmd.CashSessionId is { } sessionId && await SessionRefusalAsync(sessionId, soldAt, granted, ct) is { } sessionRefused)
                return sessionRefused;
            if (cmd.CashSessionId is null && await RequiresCashSessionAsync(ct))
                return Error.Validation("pos.session_required", "Ouvrez une session de caisse avant de vendre.");
        }
        var priced = await pricing.PriceCartAsync(cmd.InventoryLocationId, cmd.CustomerId, cmd.Currency, soldAt, cmd.Lines, cmd.CouponCode, ct);
        if (guardClientPricing && await pricingGuard.ValidateAsync(cmd.Lines, priced, cmd.Currency, soldAt, cmd.OperationUuid, ct) is { IsSuccess: false } refused)
            return refused.Error!;

        decimal subtotal = 0, discountTotal = 0, taxTotal = 0;
        foreach (var p in priced)
        {
            var gross = Rounding.Money(p.UnitPrice * p.Quantity);
            var disc = Rounding.Money(p.ManualDiscount + p.PromotionDiscount);
            var net = gross - disc;
            var tax = Rounding.Money(net * p.TaxRate / 100m);
            subtotal += gross;
            discountTotal += disc;
            taxTotal += tax;
        }
        var total = Rounding.Money(subtotal - discountTotal + taxTotal);

        var paid = Rounding.Money(cmd.Tenders.Sum(t => t.Amount));
        var tenders = cmd.Tenders.ToList();
        decimal change = 0m;
        // Module clients §7, §15 : l'avoir disponible du client paie une partie de la vente ; il est
        // imputé par le serveur sur le crédit né de la vente, dans la même transaction.
        var storeCredit = Rounding.Money(cmd.StoreCreditAmount);
        if (storeCredit < 0m) return Error.Validation("pos.store_credit_amount", "Montant d'avoir invalide.");
        if (cmd.CapStoreCreditToTotal && storeCredit > total) storeCredit = total;
        if (storeCredit > 0m)
        {
            if (cmd.CustomerId is not { } memoCustomer)
                return Error.Validation("pos.customer_required", "Un client est obligatoire pour payer avec un avoir.");
            if (storeCredit > total)
                return Error.Validation("pos.store_credit_exceeds_total", "L'avoir utilisé dépasse le total de la vente.");
            var available = await creditMemos.AvailableAsync(memoCustomer, cmd.Currency, ct);
            if (storeCredit > available)
                return Error.Conflict("pos.store_credit_insufficient", $"Avoir disponible insuffisant : {available} {cmd.Currency}.");
        }
        decimal creditAmount = Rounding.Money(total - storeCredit - paid);

        if (creditAmount < 0m)
        {
            change = -creditAmount;
            creditAmount = 0m;
            // Le rendu monnaie sort de l'espèce : on ramène le dernier moyen au net.
            var last = tenders[^1];
            tenders[^1] = last with { Amount = Rounding.Money(last.Amount - change) };
        }

        var overrideApplied = false;
        ApprovedCreditOverride? approvedOverride = null;
        if (creditAmount > 0m)
        {
            if (cmd.CustomerId is not { } customerId)
                return Error.Validation("pos.customer_required", "Un client est obligatoire pour une vente à crédit (§16).");
            // Guide §16 : vendre à crédit est une permission à part entière (pos.sell ne suffit pas).
            if (guardClientPricing && !granted.Contains(Security.Permissions.Pos.SellCredit))
                return Error.Forbidden("pos.credit_forbidden", "Vendre à crédit exige la permission pos.sell_credit.");
            var decision = await credit.EvaluateAsync(customerId, cmd.Currency, creditAmount, ct);
            if (!decision.Allowed)
            {
                if (!decision.RequiresOverride)
                    return Error.Forbidden("pos.credit_limit", decision.Message ?? "Limite de crédit dépassée (§17).");
                // Module clients §12 : dépassement approuvé par le workflow configuré pour CE panier.
                approvedOverride = await credit.FindApprovedOverrideAsync(cmd.OperationUuid, customerId, creditAmount, ct);
                if (approvedOverride is null)
                {
                    if (string.IsNullOrWhiteSpace(cmd.CreditOverrideReason))
                        return Error.Forbidden("pos.credit_limit", decision.Message ?? "Limite de crédit dépassée (§17).");
                    // Guide §16 : un motif ne suffit pas — dépasser la limite exige une autorisation.
                    if (guardClientPricing && !granted.Contains(Security.Permissions.Pos.CreditOverride))
                        return Error.Forbidden("pos.credit_override_forbidden",
                            "Dépassement de la limite de crédit : validation d'un responsable requise (permission pos.credit_override).");
                }
                overrideApplied = true;
                logger.LogWarning("Dépassement de crédit autorisé pour {Customer} : {Reason}", customerId,
                    approvedOverride?.Reason ?? cmd.CreditOverrideReason);
            }
        }

        var saleType = creditAmount >= total ? SaleType.Credit : creditAmount > 0m ? SaleType.Mixed : SaleType.Cash;
        var currencyId = await store.ResolveCurrencyIdAsync(cmd.Currency, ct);
        // Module clients §6, §13 : sans échéance explicite, un crédit sans date ne serait jamais « échu »
        // (ni relancé, ni compté dans l'aging) — la durée de la fiche client ou le réglage s'applique.
        var creditDueDate = cmd.CreditDueDate;
        if (creditAmount > 0m && creditDueDate is null && cmd.CustomerId is { } dueCustomer)
            creditDueDate = await credit.DefaultDueDateAsync(dueCustomer, DateOnly.FromDateTime(soldAt.UtcDateTime), ct);

        try
        {
            return await uow.ExecuteInTransactionAsync<Result<CheckoutResult>>(async token =>
            {
                var sale = new Sale
                {
                    Id = Guid.NewGuid(),
                    CompanyId = Company,
                    StoreId = cmd.StoreId,
                    PosTerminalId = cmd.PosTerminalId,
                    CashSessionId = cmd.CashSessionId,
                    CustomerId = cmd.CustomerId,
                    EstablishmentId = await store.LocationEstablishmentAsync(cmd.InventoryLocationId, token),
                    DocumentNumber = await store.NextNumberAsync("sale", token),
                    Status = SaleStatus.Completed,
                    SaleTypeValue = saleType,
                    CurrencyId = currencyId,
                    SoldAt = soldAt,
                    Subtotal = subtotal,
                    DiscountTotal = discountTotal,
                    TaxTotal = taxTotal,
                    Total = total,
                    AmountPaid = Rounding.Money(paid - change),
                    AmountTendered = cmd.AmountTendered == 0m ? paid : cmd.AmountTendered,
                    ChangeGiven = change,
                    SalespersonUserId = tenant.UserId,
                    SourceOperationUuid = cmd.OperationUuid,
                    CreditOverrideBy = overrideApplied ? approvedOverride?.ApprovedBy ?? tenant.UserId : null,
                    CreditOverrideReason = overrideApplied ? approvedOverride?.Reason ?? cmd.CreditOverrideReason : null,
                    CreatedBy = tenant.UserId,
                };

                decimal cogsTotal = 0m;
                var movements = new List<StockMovementRequest>();
                var lineResults = new List<CheckoutLineResult>();

                foreach (var p in priced)
                {
                    var gross = Rounding.Money(p.UnitPrice * p.Quantity);
                    var disc = Rounding.Money(p.ManualDiscount + p.PromotionDiscount);
                    var net = gross - disc;
                    var tax = Rounding.Money(net * p.TaxRate / 100m);
                    var unitCost = p.UnitCost;

                    var requiresStock = await store.ProductRequiresStockAsync(p.ProductId, token);
                    var tracksLots = requiresStock && await store.ProductTracksLotsAsync(p.ProductId, token);

                    // Produit composé (plat / kit) : consomme ses ingrédients, coût = recette (§6, §22).
                    var recipe = requiresStock
                        ? null
                        : await recipes.ExpandAsync(p.ProductId, p.Quantity, cmd.InventoryLocationId, token);
                    if (recipe is not null) unitCost = recipe.UnitCost;

                    var picks = tracksLots
                        ? await stock.PickFefoAsync(p.ProductId, cmd.InventoryLocationId, p.Quantity, token)
                        : [new FefoPick(Guid.Empty, "", null, p.Quantity)];

                    if (requiresStock && !tracksLots)
                    {
                        // Verrou de ligne : sérialise les caisses concurrentes (§18, §42).
                        var available = await stock.LockAvailableAsync(p.ProductId, cmd.InventoryLocationId, token);
                        if (p.Quantity > available)
                        {
                            // §32 : stock négatif interdit par défaut, autorisé par paramètre d'entreprise.
                            if (await stockPolicy.AllowNegativeAsync(token))
                                logger.LogWarning(
                                    "Vente en stock négatif autorisée pour {Product} : demandé {Qty}, disponible {Available} (§32).",
                                    p.Description, p.Quantity, available);
                            else
                                throw new InvalidOperationException(
                                    $"Stock insuffisant pour {p.Description} : disponible {available} (§18).");
                        }
                    }

                    var first = true;
                    foreach (var pick in picks)
                    {
                        var shareRatio = pick.Quantity / p.Quantity;
                        var lineNet = Rounding.Money(net * shareRatio);
                        var lineTax = Rounding.Money(tax * shareRatio);
                        sale.Lines.Add(new SaleLine
                        {
                            Id = Guid.NewGuid(),
                            SaleId = sale.Id,
                            CompanyId = Company,
                            ProductId = p.ProductId,
                            Description = p.Description,
                            Quantity = pick.Quantity,
                            UnitPrice = p.UnitPrice,
                            OriginalUnitPrice = first ? p.UnitPrice : null,
                            Discount = Rounding.Money(disc * shareRatio),
                            TaxId = p.TaxId,
                            TaxAmount = lineTax,
                            LineTotal = Rounding.Money(lineNet + lineTax),
                            CostSnapshot = unitCost,
                            UnitCost = unitCost,
                            LotId = pick.LotId == Guid.Empty ? null : pick.LotId,
                            InventoryLocationId = cmd.InventoryLocationId,
                            PromotionId = p.PromotionId,
                        });
                        cogsTotal += Rounding.Money(unitCost * pick.Quantity);
                        if (requiresStock)
                            movements.Add(new StockMovementRequest(
                                p.ProductId, cmd.InventoryLocationId, MovementType.Sale, -pick.Quantity, unitCost,
                                "sale", sale.Id, pick.LotId == Guid.Empty ? null : pick.LotId, soldAt, null));
                        first = false;
                    }

                    // Sortie des ingrédients d'un plat / kit vendu (§6, §24).
                    if (recipe is not null)
                        foreach (var m in recipe.IngredientMovements)
                            movements.Add(m with { ReferenceType = "sale", ReferenceId = sale.Id, OccurredAt = soldAt });

                    lineResults.Add(new CheckoutLineResult(p.ProductId, p.Quantity, p.UnitPrice, disc, tax, Rounding.Money(net + tax)));

                    if (p.PromotionDiscount > 0m || p.CouponId is not null)
                        await store.AddSalePromotionAsync(new SalePromotion
                        {
                            Id = Guid.NewGuid(), CompanyId = Company, SaleId = sale.Id,
                            PromotionId = p.PromotionId, CouponId = p.CouponId,
                            PromoType = p.PromotionLabel ?? "promo", Label = p.PromotionLabel ?? "",
                            DiscountAmount = p.PromotionDiscount, CreatedAt = clock.UtcNow,
                        }, token);
                }

                // Un coupon est consommé une fois par vente, dans la transaction de vente : s'il a atteint
                // sa limite entre le calcul du prix et l'encaissement, la vente entière est annulée.
                if (priced.Select(x => x.CouponId).FirstOrDefault(c => c is not null) is { } couponUsed
                    && !await store.ConsumeCouponAsync(couponUsed, token))
                    throw new InvalidOperationException("Ce coupon a atteint sa limite d'utilisation.");

                await store.AddSaleAsync(sale, token);
                if (movements.Count > 0)
                {
                    // Un seul mouvement porte l'UUID d'opération (marqueur d'idempotence offline, §28).
                    movements[0] = movements[0] with { SourceOperationUuid = cmd.OperationUuid };
                    await stock.PostManyAsync(movements, token);
                }

                var sessionCashAccount = cmd.CashSessionId is { } sc
                    ? await store.SessionCashAccountAsync(sc, token) : null;

                foreach (var t in tenders.Where(t => t.Amount > 0m))
                {
                    var payment = new Payment
                    {
                        Id = Guid.NewGuid(),
                        CompanyId = Company,
                        PaymentNumber = await store.NextNumberAsync("payment", token),
                        Direction = PaymentDirection.Inbound,
                        PartyType = cmd.CustomerId is not null ? "customer" : null,
                        CustomerId = cmd.CustomerId,
                        PaymentMethodId = t.PaymentMethodId,
                        FinancialAccountId = t.FinancialAccountId,
                        CashSessionId = cmd.CashSessionId,
                        CurrencyId = currencyId,
                        Amount = t.Amount,
                        Reference = t.Reference,
                        Status = "completed",
                        ReceivedAt = soldAt,
                        SourceOperationUuid = null,
                        CreatedBy = tenant.UserId,
                    };
                    await store.AddPaymentAsync(payment, token);
                    await store.AddSalePaymentAsync(new SalePayment
                    {
                        Id = Guid.NewGuid(), SaleId = sale.Id, CompanyId = Company,
                        PaymentId = payment.Id, Amount = t.Amount, CurrencyId = currencyId, CreatedAt = clock.UtcNow,
                    }, token);

                    if (cmd.CashSessionId is { } sessionId && (t.FinancialAccountId == sessionCashAccount || IsCashLike(t)))
                        await sessions.AddMovementAsync(new CashMovement
                        {
                            Id = Guid.NewGuid(), CompanyId = Company, CashSessionId = sessionId,
                            MovementType = "sale_payment", Amount = t.Amount, CurrencyId = currencyId,
                            ReferenceType = "sale", ReferenceId = sale.Id, OccurredAt = soldAt,
                            UserId = tenant.UserId, CreatedAt = clock.UtcNow,
                        }, token);
                }

                // Flush : la vente et ses lignes doivent exister avant que le moteur ne crée
                // l'écriture et la créance qui la référencent.
                await uow.SaveChangesAsync(token);

                // Remises exceptionnelles approuvées pour ce panier : consommées par cette vente (§12).
                if (guardClientPricing)
                    await pricingGuard.ConsumeDiscountApprovalsAsync(cmd.OperationUuid, sale.Id, token);

                var netMoney = new Money(Rounding.Money(subtotal - discountTotal), cmd.Currency);
                var taxMoney = new Money(taxTotal, cmd.Currency);
                var tenderSplits = tenders.Where(t => t.Amount > 0m)
                    .Select(t => new TenderSplit(t.FinancialAccountId, new Money(t.Amount, cmd.Currency))).ToArray();

                // La part payée par avoir passe par le crédit né de la vente, soldé aussitôt par l'avoir.
                var posted = await engine.HandleAsync(new PosSaleRecorded(
                    Company, cmd.OperationUuid, soldAt, sale.Id, cmd.CustomerId,
                    netMoney, taxMoney, tenderSplits, new Money(creditAmount + storeCredit, cmd.Currency),
                    creditDueDate ?? (storeCredit > 0m ? DateOnly.FromDateTime(soldAt.UtcDateTime) : null),
                    cogsTotal > 0m ? new Money(cogsTotal, cmd.Currency) : null), token);
                if (!posted.IsSuccess) throw new InvalidOperationException(posted.Error!.Message);

                if (storeCredit > 0m)
                {
                    var used = await creditMemos.ApplyToReceivableAsync(cmd.CustomerId!.Value, posted.Value!.ReceivableId!.Value,
                        cmd.Currency, storeCredit, soldAt, token, cmd.StoreCreditSalesOrderId);
                    if (used != storeCredit)
                        throw new InvalidOperationException($"Avoir disponible insuffisant au moment de l'encaissement ({used} / {storeCredit}).");
                }
                if (approvedOverride is { } approval)
                    await credit.ConsumeOverrideAsync(approval.RequestId, sale.Id, token);

                return Result.Success(new CheckoutResult(
                    sale.Id, sale.DocumentNumber, saleType, subtotal, discountTotal, taxTotal, total,
                    sale.AmountPaid, creditAmount, change, posted.Value!.JournalEntryId, posted.Value.ReceivableId,
                    overrideApplied, lineResults, storeCredit));
            }, ct);
        }
        catch (InvalidOperationException ex)
        {
            // Code dédié pour la sortie de stock insuffisante — exploité par le moteur de
            // synchronisation pour classer un conflit de stock (Phase 10 §16).
            if (ex.Message.Contains("Stock insuffisant", StringComparison.OrdinalIgnoreCase))
                return Error.Conflict("pos.insufficient_stock", ex.Message);
            return Error.Validation("pos.checkout_failed", ex.Message);
        }
    }

    // --- Ventes en attente (§34-35) -----------------------------------
    public async Task<Result<Guid>> ParkAsync(ParkCartCommand cmd, CancellationToken ct = default)
    {
        if (cmd.Lines.Count == 0) return Error.Validation("pos.empty_cart", "Le panier est vide.");
        if (await pricingGuard.ValidateProductsAsync(cmd.Lines, ct) is { IsSuccess: false } unsellable)
            return unsellable.Error!;
        var priced = await pricing.PriceCartAsync(cmd.InventoryLocationId, cmd.CustomerId, cmd.Currency, clock.UtcNow, cmd.Lines, null, ct);
        var currencyId = await store.ResolveCurrencyIdAsync(cmd.Currency, ct);
        var sale = new Sale
        {
            Id = Guid.NewGuid(),
            CompanyId = Company,
            CustomerId = cmd.CustomerId,
            EstablishmentId = await store.LocationEstablishmentAsync(cmd.InventoryLocationId, ct),
            DocumentNumber = await store.NextNumberAsync("sale", ct),
            Status = SaleStatus.Parked,
            SaleTypeValue = SaleType.Cash,
            CurrencyId = currencyId,
            SoldAt = clock.UtcNow,
            ParkedReference = cmd.Reference,
            ParkedAt = clock.UtcNow,
            SalespersonUserId = tenant.UserId,
            CreatedBy = tenant.UserId,
        };
        foreach (var p in priced)
            sale.Lines.Add(new SaleLine
            {
                Id = Guid.NewGuid(), SaleId = sale.Id, CompanyId = Company, ProductId = p.ProductId,
                Description = p.Description, Quantity = p.Quantity, UnitPrice = p.UnitPrice,
                Discount = p.ManualDiscount, TaxId = p.TaxId, LineTotal = Rounding.Money(p.UnitPrice * p.Quantity),
                CostSnapshot = p.UnitCost, UnitCost = p.UnitCost, InventoryLocationId = cmd.InventoryLocationId,
            });
        await store.AddSaleAsync(sale, ct);
        await uow.SaveChangesAsync(ct);
        return Result.Success(sale.Id);
    }

    // --- Annulation contrôlée après validation (§24) ------------------
    public async Task<Result> VoidAsync(Guid saleId, string reason, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(reason))
            return Error.Validation("pos.reason_required", "Un motif d'annulation est obligatoire (§24).");
        var sale = await store.GetSaleWithLinesAsync(saleId, ct);
        if (sale is null) return Error.NotFound("pos.sale_not_found", "Vente introuvable.");
        if (sale.Status == SaleStatus.Parked)
        {
            // Guide §11 : un panier non payé s'annule par son auteur ; celui d'un autre exige pos.void_sale.
            if (sale.CreatedBy != tenant.UserId
                && !(await permissions.CurrentPermissionsAsync(tenant, ct)).Contains(Security.Permissions.Pos.VoidSale))
                return Error.Forbidden("pos.void_forbidden", "Seul l'auteur du panier (ou un responsable) peut l'annuler.");
            await store.MarkSaleStatusAsync(saleId, SaleStatus.Void, ct);
            await uow.SaveChangesAsync(ct);
            return Result.Success();
        }
        if (sale.Status != SaleStatus.Completed)
            return Error.Conflict("pos.sale_not_voidable", "Seule une vente validée ou en attente peut être annulée.");

        // §24 : pas de suppression — l'annulation d'une vente validée passe par un retour total.
        return Error.Conflict("pos.use_full_return",
            "Annuler une vente validée via un retour total (correction traçable) — voir SalesReturnService.");
    }

    private static bool IsCashLike(TenderInput t) => t.PaymentMethodId is null;

    /// <summary>Réglage <c>pos.require_cash_session</c> (défaut : non, pour les ventes API / commandes).</summary>
    private async Task<bool> RequiresCashSessionAsync(CancellationToken ct)
    {
        try
        {
            var effective = await settings.ResolveAsync("pos.require_cash_session", null, ct);
            return effective.Value.ValueKind == System.Text.Json.JsonValueKind.True;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    /// <summary>
    /// Session citée par la vente : de l'entreprise, au vendeur (sauf superviseur <c>pos.manage_sessions</c>)
    /// et ouverte — ou fermée après l'heure de la vente (vente hors ligne rejouée à la reconnexion, §21).
    /// </summary>
    private async Task<Error?> SessionRefusalAsync(Guid sessionId, DateTimeOffset soldAt, IReadOnlyCollection<string> granted, CancellationToken ct)
    {
        var session = await sessions.GetSessionAsync(sessionId, ct);
        if (session is null || session.CompanyId != Company)
            return Error.NotFound("pos.session_not_found", "Session de caisse introuvable.");
        if (session.UserId != tenant.UserId && !granted.Contains(Security.Permissions.Pos.ManageSessions))
            return Error.Forbidden("pos.session_not_owned", "Cette session de caisse n'est pas la vôtre.");
        // « closing » = caisse comptée, en attente de validation d'écart : plus aucune vente.
        var replayedOffline = session.ClosedAt is { } closedAt && soldAt <= closedAt;
        if (session.Status != "open" && !replayedOffline)
            return Error.Conflict("pos.session_closed", "La session de caisse est fermée : ouvrez une session pour vendre.");
        return null;
    }

    private CheckoutResult Describe(Sale s) => new(
        s.Id, s.DocumentNumber, s.SaleTypeValue, s.Subtotal, s.DiscountTotal, s.TaxTotal, s.Total,
        s.AmountPaid, Math.Max(0m, s.Total - s.AmountPaid), s.ChangeGiven, Guid.Empty, null, false,
        s.Lines.Select(l => new CheckoutLineResult(l.ProductId, l.Quantity, l.UnitPrice, l.Discount, l.TaxAmount, l.LineTotal)).ToList());
}
