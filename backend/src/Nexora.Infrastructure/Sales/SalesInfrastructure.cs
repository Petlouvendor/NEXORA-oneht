using Microsoft.EntityFrameworkCore;
using Nexora.Application.Abstractions;
using Nexora.Application.Sales;
using Nexora.Domain.Finance;
using Nexora.Domain.Sales;
using Nexora.Infrastructure.Persistence;

namespace Nexora.Infrastructure.Sales;

internal static class SalesHelpers
{
    public static Guid Company(ITenantContext t) => t.ActiveCompanyId
        ?? throw new InvalidOperationException("Aucune entreprise active.");

    public static async Task<string> NextNumber(NexoraDbContext db, ITenantContext t, string type, CancellationToken ct)
    {
        var year = DateTime.UtcNow.Year;
        var rows = await db.Database
            .SqlQuery<string>($"""SELECT nexora.next_document_number({Company(t)}, {type}, {(Guid?)null}, {year}) AS "Value" """)
            .ToListAsync(ct);
        return rows[0];
    }
}

/// <summary>Tarification déterministe : prix + promotions + taxe (Phase 6 §9-12).</summary>
public sealed class SqlSalesPricing(NexoraDbContext db, ITenantContext tenant) : ISalesPricing
{
    private Guid Company => SalesHelpers.Company(tenant);

    private sealed record PriceRow(decimal Amount);
    private sealed record ResolvedPrice(decimal Amount, bool TaxIncluded);
    private sealed record PromoRow(Guid Id, string PromoType, decimal Value, string Name);

    public async Task<IReadOnlyList<PricedLine>> PriceCartAsync(
        Guid inventoryLocationId, Guid? customerId, string currency, DateTimeOffset at,
        IReadOnlyList<CheckoutLine> lines, string? couponCode, CancellationToken ct = default)
    {
        var currencyId = await db.Currencies.AsNoTracking().Where(c => c.Code == currency).Select(c => c.Id).FirstAsync(ct);
        Guid? customerGroupId = customerId is null ? null : await db.Database
            .SqlQuery<Guid?>($"""SELECT customer_group_id AS "Value" FROM nexora.customer WHERE id = {customerId}""")
            .FirstOrDefaultAsync(ct);

        Guid? couponId = null;
        decimal couponPercent = 0m, couponAmount = 0m;
        if (!string.IsNullOrWhiteSpace(couponCode))
        {
            var coupon = await db.Database.SqlQuery<CouponRow>($"""
                SELECT id AS "Id", discount_type AS "DiscountType", discount_value AS "DiscountValue"
                FROM nexora.coupon WHERE company_id = {Company} AND code = {couponCode} AND status = 'active'
                """).FirstOrDefaultAsync(ct);
            if (coupon is not null)
            {
                couponId = coupon.Id;
                if (coupon.DiscountType == "percentage") couponPercent = coupon.DiscountValue;
                else couponAmount = coupon.DiscountValue;
            }
        }

        var result = new List<PricedLine>();
        foreach (var line in lines)
        {
            var product = await db.Products.AsNoTracking().FirstOrDefaultAsync(p => p.Id == line.ProductId, ct)
                ?? throw new InvalidOperationException($"Produit {line.ProductId} introuvable.");

            var priceRow = line.UnitPriceOverride is null
                ? await db.Database.SqlQuery<ResolvedPrice>($"""
                    SELECT amount AS "Amount", tax_included AS "TaxIncluded" FROM nexora.product_price
                    WHERE product_id = {line.ProductId} AND price_type = 'sale' AND currency_id = {currencyId}
                      AND quantity_min <= {line.Quantity} AND valid_from <= {at}
                      AND (valid_to IS NULL OR valid_to > {at})
                      AND (customer_group_id IS NULL OR customer_group_id = {customerGroupId})
                    ORDER BY (customer_group_id IS NOT NULL) DESC, quantity_min DESC, valid_from DESC
                    LIMIT 1
                    """).FirstOrDefaultAsync(ct)
                : null;
            decimal unitPrice = line.UnitPriceOverride ?? priceRow?.Amount ?? 0m;

            var taxId = line.TaxId ?? product.DefaultTaxId;
            decimal taxRate = taxId is null ? 0m : await db.Database.SqlQuery<decimal?>($"""
                SELECT rate AS "Value" FROM nexora.tax WHERE id = {taxId}
                """).FirstOrDefaultAsync(ct) ?? 0m;

            // §25 : un prix saisi TTC est ramené au HT de façon déterministe ; le reste du
            // moteur de vente ajoute la taxe sur ce net (le total TTC reste cohérent).
            if (priceRow is { TaxIncluded: true } && taxRate > 0m)
                unitPrice = Math.Round(unitPrice / (1m + taxRate / 100m), 4, MidpointRounding.ToEven);

            decimal unitCost = await db.StockBalances.AsNoTracking()
                .Where(s => s.ProductId == line.ProductId && s.InventoryLocationId == inventoryLocationId)
                .Select(s => (decimal?)s.AverageCost).FirstOrDefaultAsync(ct) ?? 0m;

            var gross = Math.Round(unitPrice * line.Quantity, 2);
            decimal promoDiscount = 0m;
            string? promoLabel = null;
            Guid? promoId = null;

            var promo = await db.Database.SqlQuery<PromoRow>($"""
                SELECT pr.id AS "Id", pr.promo_type AS "PromoType", pr.value AS "Value", pr.name AS "Name"
                FROM nexora.promotion pr
                JOIN nexora.promotion_target pt ON pt.promotion_id = pr.id
                WHERE pr.company_id = {Company} AND pr.status = 'active'
                  AND (pr.starts_at IS NULL OR pr.starts_at <= {at})
                  AND (pr.ends_at IS NULL OR pr.ends_at >= {at})
                  AND (pt.target_type = 'all'
                       OR (pt.target_type = 'product'  AND pt.product_id  = {line.ProductId})
                       OR (pt.target_type = 'category' AND pt.category_id = {product.CategoryId})
                       OR (pt.target_type = 'brand'    AND pt.brand_id    = {product.BrandId}))
                ORDER BY pr.priority DESC LIMIT 1
                """).FirstOrDefaultAsync(ct);

            if (promo is not null)
            {
                promoId = promo.Id;
                promoLabel = promo.Name;
                promoDiscount = promo.PromoType switch
                {
                    "percentage" => Math.Round(gross * promo.Value / 100m, 2),
                    "amount" => Math.Min(gross, Math.Round(promo.Value * line.Quantity, 2)),
                    _ => 0m,
                };
            }

            if (couponPercent > 0m) promoDiscount += Math.Round(gross * couponPercent / 100m, 2);
            else if (couponAmount > 0m && promoDiscount == 0m) promoDiscount += Math.Min(gross, couponAmount);

            result.Add(new PricedLine(
                line.ProductId, line.Quantity, unitPrice, Math.Round(line.LineDiscount, 2),
                promoDiscount, promoLabel, promoId, couponId, taxRate, taxId, unitCost, product.Name));
        }
        return result;
    }

    private sealed record CouponRow(Guid Id, string DiscountType, decimal DiscountValue);
}

/// <summary>Contrôle du crédit client (Phase 6 §17).</summary>
public sealed class SqlCreditControl(
    NexoraDbContext db, ITenantContext tenant, Nexora.Application.Administration.ISettingsService settings) : ICreditControl
{
    private Guid Company => SalesHelpers.Company(tenant);
    private sealed record Row(string Status, decimal CreditLimit, string Currency, decimal OpenReceivables, int Unconvertible,
        int Overdue, int Blocks, decimal? NewAmount);

    public async Task<CreditDecision> EvaluateAsync(Guid customerId, string currency, decimal newSaleAmount, CancellationToken ct = default)
    {
        // Module clients §12 : l'encours inclut les crédits en litige (toujours dus) ; le retard se lit
        // sur l'échéance — le statut OVERDUE n'est jamais posé, le contrôle ne s'en servait donc jamais.
        // §21 : chaque devise est convertie dans celle du client (limite) au dernier taux ; jamais additionnées.
        var row = await db.Database.SqlQuery<Row>($"""
            SELECT c.status AS "Status", c.credit_limit AS "CreditLimit", cur.code AS "Currency",
                   COALESCE((SELECT SUM(nexora.convert_amount(r.remaining_amount, r.currency_id, c.default_currency_id))
                             FROM nexora.customer_receivable r
                             WHERE r.customer_id = c.id AND r.status IN ('OPEN','PARTIALLY_PAID','OVERDUE','IN_DISPUTE')
                               AND r.remaining_amount > 0), 0) AS "OpenReceivables",
                   COALESCE((SELECT COUNT(*) FROM nexora.customer_receivable r
                             WHERE r.customer_id = c.id AND r.status IN ('OPEN','PARTIALLY_PAID','OVERDUE','IN_DISPUTE')
                               AND r.remaining_amount > 0
                               AND nexora.convert_amount(r.remaining_amount, r.currency_id, c.default_currency_id) IS NULL), 0)::int AS "Unconvertible",
                   COALESCE((SELECT COUNT(*) FROM nexora.customer_receivable r
                             WHERE r.customer_id = c.id AND r.status IN ('OPEN','PARTIALLY_PAID','OVERDUE')
                               AND r.due_date < current_date AND r.original_amount > r.paid_amount), 0)::int AS "Overdue",
                   COALESCE((SELECT COUNT(*) FROM nexora.customer_block b
                             WHERE b.customer_id = c.id AND b.released_at IS NULL
                               AND b.block_type IN ('credit','sales','account')), 0)::int AS "Blocks",
                   nexora.convert_amount({newSaleAmount}, (SELECT id FROM nexora.currency WHERE code = {currency}), c.default_currency_id) AS "NewAmount"
            FROM nexora.customer c JOIN nexora.currency cur ON cur.id = c.default_currency_id
            WHERE c.id = {customerId} AND c.company_id = {Company}
            """).FirstOrDefaultAsync(ct)
            ?? throw new InvalidOperationException("Client introuvable.");

        if (row.Unconvertible > 0 || row.NewAmount is null)
            return new CreditDecision(false, true, 0m, row.OpenReceivables + newSaleAmount,
                $"Aucun taux de change pour convertir l'encours dans la devise du client ({row.Currency}) : saisissez le taux du jour.");
        var projected = row.OpenReceivables + row.NewAmount.Value;
        var available = row.CreditLimit - row.OpenReceivables;

        // §12 « client actif » : une fiche inactive ou archivée n'achète pas à crédit, même avec dérogation.
        if (row.Status is "inactive" or "archived")
            return new CreditDecision(false, false, 0m, projected,
                $"Client {(row.Status == "archived" ? "archivé" : "inactif")} : réactivez la fiche avant toute vente à crédit.");
        // §26 : un client bloqué (crédit / vente / compte) ne peut acheter à crédit.
        if (row.Status == "blocked" || row.Blocks > 0)
            return new CreditDecision(false, true, Math.Max(0m, available), projected,
                "Client bloqué pour le crédit (§26) — un déblocage ou une autorisation exceptionnelle est requis.");
        if (row.CreditLimit <= 0m)
            return new CreditDecision(false, true, 0m, projected, "Aucune limite de crédit définie pour ce client.");
        if (row.Overdue > 0)
            return new CreditDecision(false, true, Math.Max(0m, available), projected,
                $"{row.Overdue} créance(s) échue(s) impayée(s) (§17).");
        if (projected > row.CreditLimit)
            return new CreditDecision(false, true, Math.Max(0m, available), projected,
                $"Encours projeté {Math.Round(projected, 2)} {row.Currency} > limite {row.CreditLimit} {row.Currency}.");

        return new CreditDecision(true, false, available, projected, null);
    }

    private sealed record OverrideRow(Guid Id, Guid? ApprovedBy, string? Reason);

    public async Task<ApprovedCreditOverride?> FindApprovedOverrideAsync(Guid cartOperationUuid, Guid customerId, decimal amount,
        CancellationToken ct = default)
    {
        var row = await db.Database.SqlQuery<OverrideRow>($"""
            SELECT a.id AS "Id",
                   (SELECT x.approver_id FROM nexora.approval_action x WHERE x.approval_request_id = a.id AND x.action = 'approve'
                    ORDER BY x.created_at DESC LIMIT 1) AS "ApprovedBy",
                   a.context ->> 'reason' AS "Reason"
            FROM nexora.approval_request a
            WHERE a.company_id = {Company} AND a.reference_type = 'pos_credit_override' AND a.reference_id = {cartOperationUuid}
              AND a.status = 'approved' AND a.consumed_at IS NULL AND a.amount >= {amount}
              AND a.context ->> 'customerId' = {customerId.ToString()}
              AND a.requested_at > now() - interval '2 hours'
            ORDER BY a.requested_at DESC
            LIMIT 1
            """).FirstOrDefaultAsync(ct);
        return row is null ? null : new ApprovedCreditOverride(row.Id, row.ApprovedBy,
            $"Dépassement approuvé (workflow) — {row.Reason ?? "sans motif"}");
    }

    public async Task ConsumeOverrideAsync(Guid approvalRequestId, Guid saleId, CancellationToken ct = default)
    {
        var n = await db.Database.ExecuteSqlAsync($"""
            UPDATE nexora.approval_request SET consumed_at = now(), consumed_by_id = {saleId}, updated_at = now()
            WHERE id = {approvalRequestId} AND company_id = {Company} AND consumed_at IS NULL AND status = 'approved'
            """, ct);
        if (n != 1) throw new InvalidOperationException("Cette approbation de dépassement a déjà été utilisée.");
    }

    public async Task<DateOnly> DefaultDueDateAsync(Guid customerId, DateOnly from, CancellationToken ct = default)
    {
        var days = await db.Database.SqlQuery<int?>($"""
            SELECT credit_days AS "Value" FROM nexora.customer WHERE id = {customerId} AND company_id = {Company}
            """).FirstOrDefaultAsync(ct);
        if (days is null)
        {
            try
            {
                var setting = await settings.ResolveAsync("credit.default_due_days", null, ct);
                if (setting.Value.ValueKind == System.Text.Json.JsonValueKind.Number) days = setting.Value.GetInt32();
            }
            catch (InvalidOperationException)
            {
                // définition absente : repli sur 30 jours
            }
        }
        return from.AddDays(Math.Clamp(days ?? 30, 0, 3650));
    }
}

// --- Stores --------------------------------------------------------------

public sealed class EfSalesStore(NexoraDbContext db, ITenantContext tenant) : ISalesStore
{
    private Guid Company => SalesHelpers.Company(tenant);

    public async Task<Guid> ResolveCurrencyIdAsync(string code, CancellationToken ct = default)
        => await db.Currencies.AsNoTracking().Where(c => c.Code == code).Select(c => (Guid?)c.Id).FirstOrDefaultAsync(ct)
           ?? throw new InvalidOperationException($"Devise {code} inconnue.");

    public Task<string> NextNumberAsync(string documentType, CancellationToken ct = default) => SalesHelpers.NextNumber(db, tenant, documentType, ct);

    public Task<Guid?> FindSaleByOperationAsync(Guid operationUuid, CancellationToken ct = default)
        => db.Sales.AsNoTracking().Where(s => s.SourceOperationUuid == operationUuid).Select(s => (Guid?)s.Id).FirstOrDefaultAsync(ct);

    public Task<Sale?> GetSaleWithLinesAsync(Guid saleId, CancellationToken ct = default)
        => db.Sales.AsNoTracking().Include(s => s.Lines).FirstOrDefaultAsync(s => s.Id == saleId, ct);

    public async Task AddSaleAsync(Sale sale, CancellationToken ct = default) => await db.Sales.AddAsync(sale, ct);
    public async Task AddPaymentAsync(Payment payment, CancellationToken ct = default) => await db.Payments.AddAsync(payment, ct);
    public async Task AddSalePaymentAsync(SalePayment link, CancellationToken ct = default) => await db.SalePayments.AddAsync(link, ct);
    public async Task AddSalePromotionAsync(SalePromotion promo, CancellationToken ct = default) => await db.SalePromotions.AddAsync(promo, ct);

    public async Task<Guid?> DefaultTaxIdAsync(Guid productId, CancellationToken ct = default)
        => await db.Products.AsNoTracking().Where(p => p.Id == productId).Select(p => p.DefaultTaxId).FirstOrDefaultAsync(ct);

    public async Task<bool> ProductRequiresStockAsync(Guid productId, CancellationToken ct = default)
        => await db.Products.AsNoTracking().Where(p => p.Id == productId)
            .Select(p => p.ProductType == "stockable" || p.ProductType == "weight").FirstOrDefaultAsync(ct);

    public async Task<bool> ProductTracksLotsAsync(Guid productId, CancellationToken ct = default)
        => await db.Products.AsNoTracking().Where(p => p.Id == productId).Select(p => p.TrackLots).FirstOrDefaultAsync(ct);

    public async Task<string> ProductNameAsync(Guid productId, CancellationToken ct = default)
        => await db.Products.AsNoTracking().Where(p => p.Id == productId).Select(p => p.Name).FirstOrDefaultAsync(ct) ?? "";

    public async Task MarkSaleStatusAsync(Guid saleId, string status, CancellationToken ct = default)
        => await db.Sales.Where(s => s.Id == saleId).ExecuteUpdateAsync(u => u.SetProperty(s => s.Status, status), ct);

    public async Task<Guid?> LocationEstablishmentAsync(Guid inventoryLocationId, CancellationToken ct = default)
        => await db.Database.SqlQuery<Guid?>($"""
            SELECT establishment_id AS "Value" FROM nexora.inventory_location WHERE id = {inventoryLocationId}
            """).FirstOrDefaultAsync(ct);

    public async Task<Guid?> SessionCashAccountAsync(Guid sessionId, CancellationToken ct = default)
        => await (from cs in db.CashSessions.AsNoTracking()
                  join cr in db.CashRegisters.AsNoTracking() on cs.CashRegisterId equals cr.Id
                  where cs.Id == sessionId
                  select cr.FinancialAccountId).FirstOrDefaultAsync(ct);
}

public sealed class EfSalesReturnStore(NexoraDbContext db, ITenantContext tenant) : ISalesReturnStore
{
    public Task<string> NextNumberAsync(string documentType, CancellationToken ct = default) => SalesHelpers.NextNumber(db, tenant, documentType, ct);

    public Task<Guid?> FindReturnByOperationAsync(Guid operationUuid, CancellationToken ct = default)
        => db.SaleReturns.AsNoTracking().Where(r => r.SourceOperationUuid == operationUuid).Select(r => (Guid?)r.Id).FirstOrDefaultAsync(ct);

    public async Task AddReturnAsync(SaleReturn ret, CancellationToken ct = default) => await db.SaleReturns.AddAsync(ret, ct);

    public Task<Sale?> GetSaleWithLinesAsync(Guid saleId, CancellationToken ct = default)
        => db.Sales.AsNoTracking().Include(s => s.Lines).FirstOrDefaultAsync(s => s.Id == saleId, ct);

    public Task<Guid?> ReceivableForSaleAsync(Guid saleId, CancellationToken ct = default)
        => db.Receivables.AsNoTracking().Where(r => r.SaleId == saleId).Select(r => (Guid?)r.Id).FirstOrDefaultAsync(ct);

    public async Task<SaleReceivableBalance?> ReceivableBalanceForSaleAsync(Guid saleId, CancellationToken ct = default)
    {
        // Verrou : deux retours simultanés sur la même vente ne peuvent pas imputer deux fois le même reste dû.
        var rows = await db.Database.SqlQuery<SaleReceivableBalance>($"""
            SELECT id AS "ReceivableId",
                   CASE WHEN status IN ('CANCELLED', 'WRITTEN_OFF') THEN 0 ELSE remaining_amount END AS "Remaining"
            FROM nexora.customer_receivable
            WHERE sale_id = {saleId} AND company_id = {SalesHelpers.Company(tenant)}
            FOR UPDATE
            """).ToListAsync(ct);
        return rows.Count == 0 ? null : rows[0];
    }

    public async Task SetSaleStatusAsync(Guid saleId, string status, CancellationToken ct = default)
        => await db.Sales.Where(s => s.Id == saleId).ExecuteUpdateAsync(u => u.SetProperty(s => s.Status, status), ct);

    public async Task<IReadOnlyDictionary<Guid, decimal>> ReturnedQuantitiesAsync(Guid saleId, CancellationToken ct = default)
        => await db.SaleReturns.AsNoTracking()
            .Where(r => r.SaleId == saleId && r.Status == "completed")
            .SelectMany(r => r.Lines)
            .Where(l => l.SaleLineId != null)
            .GroupBy(l => l.SaleLineId!.Value)
            .Select(g => new { g.Key, Quantity = g.Sum(l => l.Quantity) })
            .ToDictionaryAsync(x => x.Key, x => x.Quantity, ct);

    public async Task<string> CurrencyCodeAsync(Guid currencyId, CancellationToken ct = default)
        => await db.Currencies.AsNoTracking().Where(c => c.Id == currencyId).Select(c => c.Code).FirstAsync(ct);
}

public sealed class EfCashSessionStore(NexoraDbContext db, ITenantContext tenant) : ICashSessionStore
{
    public Task<string> NextNumberAsync(string documentType, CancellationToken ct = default) => SalesHelpers.NextNumber(db, tenant, documentType, ct);

    public Task<CashRegister?> GetRegisterAsync(Guid registerId, CancellationToken ct = default)
        => db.CashRegisters.AsNoTracking().FirstOrDefaultAsync(r => r.Id == registerId, ct);

    public Task<CashSession?> GetOpenSessionAsync(Guid registerId, CancellationToken ct = default)
        => db.CashSessions.AsNoTracking()
            .FirstOrDefaultAsync(s => s.CashRegisterId == registerId && (s.Status == "open" || s.Status == "closing"), ct);

    public Task<CashSession?> GetSessionAsync(Guid sessionId, CancellationToken ct = default)
        => db.CashSessions.AsNoTracking().FirstOrDefaultAsync(s => s.Id == sessionId, ct);

    public async Task AddSessionAsync(CashSession session, CancellationToken ct = default) => await db.CashSessions.AddAsync(session, ct);
    public async Task AddMovementAsync(CashMovement movement, CancellationToken ct = default) => await db.CashMovements.AddAsync(movement, ct);

    public async Task<(int SaleCount, decimal CashIn, decimal CashOut)> SessionTotalsAsync(Guid sessionId, CancellationToken ct = default)
    {
        var moves = await db.CashMovements.AsNoTracking().Where(m => m.CashSessionId == sessionId)
            .Where(m => m.MovementType != "opening_float" && m.MovementType != "closing")
            .Select(m => new { m.Amount, m.MovementType, m.ReferenceType }).ToListAsync(ct);
        var saleCount = moves.Count(m => m.ReferenceType == "sale");
        var cashIn = moves.Where(m => m.Amount > 0m).Sum(m => m.Amount);
        var cashOut = moves.Where(m => m.Amount < 0m).Sum(m => m.Amount);
        return (saleCount, cashIn, cashOut);
    }

    public async Task MarkCountedAsync(Guid sessionId, decimal expected, decimal counted, string? notes, CancellationToken ct = default)
        => await db.Database.ExecuteSqlAsync($"""
            UPDATE nexora.cash_session
               SET status = 'closing', counted_at = now(), expected_balance = {expected}, counted_balance = {counted}, notes = {notes}
             WHERE id = {sessionId} AND status = 'open'
            """, ct);

    public async Task FinalizeCloseAsync(Guid sessionId, decimal expected, decimal counted, string? notes,
        Guid? validatedBy, string? validationNote, CancellationToken ct = default)
        => await db.Database.ExecuteSqlAsync($"""
            UPDATE nexora.cash_session
               SET status = 'closed', closed_at = now(), counted_at = COALESCE(counted_at, now()),
                   expected_balance = {expected}, counted_balance = {counted}, notes = {notes},
                   close_validated_by = {validatedBy},
                   close_validated_at = CASE WHEN {validatedBy}::uuid IS NULL THEN NULL ELSE now() END,
                   close_validation_note = {validationNote}
             WHERE id = {sessionId} AND status IN ('open', 'closing')
            """, ct);

    public async Task<string> CurrencyCodeAsync(Guid currencyId, CancellationToken ct = default)
        => await db.Currencies.AsNoTracking().Where(c => c.Id == currencyId).Select(c => c.Code).FirstAsync(ct);
}

/// <summary>
/// Garde-fou des montants saisis par le client (recette Phase 55 POS-006 / FIN-014). Le prix forcé
/// et la remise manuelle étaient jusque-là acceptés sans permission ni borne : un simple vendeur
/// pouvait vendre à 0,01 ou remiser au-delà du montant de la ligne (total négatif). Guide
/// fonctionnel : produit actif et vendable (§25), limite de remise par rôle et remise exceptionnelle
/// soumise à validation Manager (§12).
/// </summary>
public sealed class SqlSalePricingGuard(
    NexoraDbContext db, ITenantContext tenant, IPermissionService permissions, IPosLimits limits) : ISalePricingGuard
{
    private Guid Company => SalesHelpers.Company(tenant);

    private sealed record ProductState(Guid Id, string Name, string Status, bool IsSellable);

    public async Task<Nexora.Domain.Common.Result> ValidateProductsAsync(IReadOnlyList<CheckoutLine> requested, CancellationToken ct = default)
    {
        var ids = requested.Select(l => l.ProductId).Distinct().ToArray();
        var found = await db.Database.SqlQuery<ProductState>($"""
            SELECT id AS "Id", name AS "Name", status AS "Status", is_sellable AS "IsSellable"
            FROM nexora.product WHERE company_id = {Company} AND id = ANY({ids})
            """).ToDictionaryAsync(p => p.Id, ct);
        foreach (var id in ids)
        {
            if (!found.TryGetValue(id, out var p))
                return Nexora.Domain.Common.Error.NotFound("sale.product_not_found", "Produit introuvable.");
            if (p.Status != "active")
                return Nexora.Domain.Common.Error.Validation("sale.product_inactive",
                    $"« {p.Name} » n'est pas actif (statut {p.Status}) : il ne peut pas être vendu.");
            if (!p.IsSellable)
                return Nexora.Domain.Common.Error.Validation("sale.product_not_sellable", $"« {p.Name} » n'est pas un article vendable.");
        }
        return Nexora.Domain.Common.Result.Success();
    }

    public async Task<Nexora.Domain.Common.Result> ValidateAsync(IReadOnlyList<CheckoutLine> requested, IReadOnlyList<PricedLine> priced,
        string currency, DateTimeOffset at, Guid? approvalOperation, CancellationToken ct = default)
    {
        IReadOnlyCollection<string>? granted = null;
        async Task<bool> Has(string permission)
        {
            if (tenant.UserId is null) return false;
            granted ??= await permissions.CurrentPermissionsAsync(tenant, ct);
            return granted.Contains(permission);
        }
        DiscountLimits? discountLimits = null;

        for (var i = 0; i < requested.Count; i++)
        {
            var req = requested[i];
            var line = priced[i];
            if (req.Quantity <= 0)
                return Nexora.Domain.Common.Error.Validation("sale.quantity", "La quantité doit être strictement positive.");
            if (req.LineDiscount < 0)
                return Nexora.Domain.Common.Error.Validation("sale.discount_negative", "Une remise ne peut pas être négative.");
            if (req.UnitPriceOverride is < 0)
                return Nexora.Domain.Common.Error.Validation("sale.price_negative", "Un prix ne peut pas être négatif.");

            // Sans prix en vigueur (devise, date), l'article partirait gratuitement : refus explicite.
            if (req.UnitPriceOverride is null && line.UnitPrice <= 0m)
                return Nexora.Domain.Common.Error.Validation("sale.no_price",
                    $"Aucun prix de vente en vigueur pour « {line.Description} » en {currency} à cette date.");

            var gross = Math.Round(line.UnitPrice * line.Quantity, 2);
            if (line.ManualDiscount > gross)
                return Nexora.Domain.Common.Error.Validation("sale.discount_exceeds_line",
                    $"La remise ({line.ManualDiscount}) dépasse le montant de la ligne « {line.Description} » ({gross}).");

            if (req.UnitPriceOverride is not null && !await Has(Nexora.Application.Security.Permissions.Pos.ChangePrice))
                return Nexora.Domain.Common.Error.Forbidden("sale.price_override_forbidden",
                    "Modifier le prix de vente exige la permission pos.change_price.");

            if (line.ManualDiscount > 0)
            {
                if (!await Has(Nexora.Application.Security.Permissions.Pos.Discount))
                    return Nexora.Domain.Common.Error.Forbidden("sale.discount_forbidden",
                        "Accorder une remise exige la permission pos.discount.");
                discountLimits ??= await limits.DiscountLimitsAsync(ct);
                var percent = gross > 0 ? line.ManualDiscount / gross * 100m : 0m;
                if (discountLimits.SelfLimitPercent is { } maxPct && percent > maxPct + 0.0001m
                    && !(approvalOperation is { } op && await HasApprovedDiscountAsync(op, line.ProductId, percent, ct)))
                    return Nexora.Domain.Common.Error.Forbidden("sale.discount_over_limit",
                        $"Remise de {Math.Round(percent, 2)} % sur « {line.Description} » : au-delà de votre limite de {maxPct} %. " +
                        "Demandez une validation Manager (remise exceptionnelle).");
            }

            if (req.UnitPriceOverride is not null || line.ManualDiscount > 0)
            {
                var floor = await db.Database.SqlQuery<decimal?>($"""
                    SELECT pp.min_price AS "Value" FROM nexora.product_price pp
                    JOIN nexora.currency c ON c.id = pp.currency_id
                    WHERE pp.product_id = {line.ProductId} AND pp.price_type = 'sale' AND c.code = {currency}
                      AND pp.min_price IS NOT NULL AND pp.valid_from <= {at} AND (pp.valid_to IS NULL OR pp.valid_to > {at})
                    ORDER BY pp.valid_from DESC LIMIT 1
                    """).FirstOrDefaultAsync(ct);
                var netUnit = line.Quantity == 0 ? 0 : (gross - line.ManualDiscount) / line.Quantity;
                if (floor is { } f && netUnit < f)
                    return Nexora.Domain.Common.Error.Validation("sale.below_floor_price",
                        $"Prix net unitaire {Math.Round(netUnit, 2)} sous le prix plancher {f} de « {line.Description} ».");
            }
        }
        return Nexora.Domain.Common.Result.Success();
    }

    public async Task ConsumeDiscountApprovalsAsync(Guid operationUuid, Guid saleId, CancellationToken ct = default)
        => await db.Database.ExecuteSqlAsync($"""
            UPDATE nexora.pos_discount_request
               SET status = 'consumed', consumed_sale_id = {saleId}
             WHERE company_id = {Company} AND operation_uuid = {operationUuid} AND status = 'approved'
               AND requested_by = {tenant.UserId}
            """, ct);

    /// <summary>Remise exceptionnelle approuvée, non expirée, demandée par ce vendeur pour ce panier et ce
    /// produit, d'un pourcentage au moins égal à celui appliqué.</summary>
    private async Task<bool> HasApprovedDiscountAsync(Guid operationUuid, Guid productId, decimal percent, CancellationToken ct)
        => await db.Database.SqlQuery<bool>($"""
            SELECT EXISTS (SELECT 1 FROM nexora.pos_discount_request
                           WHERE company_id = {Company} AND operation_uuid = {operationUuid} AND product_id = {productId}
                             AND requested_by = {tenant.UserId} AND status = 'approved' AND expires_at > now()
                             AND requested_percent >= {percent} - 0.0001) AS "Value"
            """).FirstAsync(ct);
}

/// <summary>
/// Limites de remise de l'utilisateur courant (guide §8, §12). Limite du rôle = MAX des
/// <c>role.max_discount_percent</c> de ses affectations actives sur l'entreprise (ou son groupe) ;
/// plafond d'entreprise = réglage <c>pos.max_discount_percent</c>. Remise appliquée soi-même :
/// min(rôle, plafond) ; validation d'une remise exceptionnelle : limite du rôle (ou plafond à défaut).
/// </summary>
public sealed class SqlPosLimits(
    NexoraDbContext db, ITenantContext tenant, Nexora.Application.Administration.ISettingsService settings) : IPosLimits
{
    private Guid Company => SalesHelpers.Company(tenant);

    public async Task<DiscountLimits> DiscountLimitsAsync(CancellationToken ct = default)
    {
        var cap = await CompanyCapAsync(ct);
        decimal? role;
        using (tenant.BeginSystemScope())
            role = await db.Database.SqlQuery<decimal?>($"""
                SELECT MAX(r.max_discount_percent) AS "Value"
                FROM nexora.user_role ur
                JOIN nexora.role r ON r.id = ur.role_id
                JOIN nexora.access_scope sc ON sc.id = ur.scope_id
                WHERE ur.user_id = {tenant.UserId} AND ur.revoked_at IS NULL
                  AND (ur.valid_from IS NULL OR ur.valid_from <= now())
                  AND (ur.valid_until IS NULL OR ur.valid_until > now())
                  AND (sc.company_id = {Company}
                       OR (sc.group_id IS NOT NULL AND sc.group_id = (SELECT group_id FROM nexora.company WHERE id = {Company})))
                """).FirstOrDefaultAsync(ct);

        var self = (role, cap) switch
        {
            ({ } r, { } c) => Math.Min(r, c),
            ({ } r, null) => r,
            (null, { } c) => c,
            _ => (decimal?)null,
        };
        return new DiscountLimits(self, role ?? cap);
    }

    /// <summary><c>pos.max_discount_percent</c> résolu par périmètre (défaut système : 15 %).</summary>
    private async Task<decimal?> CompanyCapAsync(CancellationToken ct)
    {
        try
        {
            var effective = await settings.ResolveAsync("pos.max_discount_percent", null, ct);
            return effective.Value.ValueKind == System.Text.Json.JsonValueKind.Number ? effective.Value.GetDecimal() : null;
        }
        catch (InvalidOperationException)
        {
            return null;   // définition absente : aucun plafond configuré
        }
    }
}
