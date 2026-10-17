using Microsoft.EntityFrameworkCore;
using Nexora.Application.Abstractions;
using Nexora.Application.Catalog;
using Nexora.Contracts.Catalog;
using Nexora.Domain.Common;
using Nexora.Infrastructure.Persistence;

namespace Nexora.Infrastructure.Catalog;

/// <summary>
/// Moteur de prix explicable (Phase 23 §22). Résout le prix d'une ligne dans un ordre figé
/// — prix de base → liste tarifaire / tarif client → palier quantité → (extraction TTC)
/// → promotion → coupon → remise manuelle → taxe — et restitue chaque étape.
/// </summary>
public sealed class CatalogPricingEngine(NexoraDbContext db, ITenantContext tenant, IDateTimeProvider clock)
    : ICatalogPricingEngine
{
    private Guid Company => tenant.ActiveCompanyId
        ?? throw TenantErrors.ActiveCompanyRequired();

    private static decimal R2(decimal v) => Math.Round(v, 2, MidpointRounding.ToEven);
    private static decimal R4(decimal v) => Math.Round(v, 4, MidpointRounding.ToEven);

    private sealed record PriceRow(
        decimal Amount, bool TaxIncluded, decimal? MinPrice, decimal QuantityMin,
        Guid? PriceListId, Guid? CustomerGroupId);
    private sealed record CouponRow(Guid Id, string DiscountType, decimal DiscountValue, int? UsageLimit, int UsedCount);

    public async Task<Result<PriceQuoteDto>> QuoteAsync(PriceQuoteRequest req, CancellationToken ct = default)
    {
        if (req.Quantity <= 0m)
            return Error.Validation("catalog.quote_quantity", "La quantité doit être strictement positive.");

        var product = await db.Products.AsNoTracking().FirstOrDefaultAsync(p => p.Id == req.ProductId, ct);
        if (product is null) return Error.NotFound("catalog.product_not_found", "Produit introuvable.");

        var at = req.At ?? clock.UtcNow;
        var currencyId = await db.Currencies.AsNoTracking()
            .Where(c => c.Code == req.Currency).Select(c => (Guid?)c.Id).FirstOrDefaultAsync(ct);
        if (currencyId is null) return Error.Validation("catalog.currency_unknown", $"Devise « {req.Currency} » inconnue.");

        Guid? customerGroupId = req.CustomerId is null ? null : await db.Database
            .SqlQuery<Guid?>($"""SELECT customer_group_id AS "Value" FROM nexora.customer WHERE id = {req.CustomerId}""")
            .FirstOrDefaultAsync(ct);

        var qty = req.Quantity;
        var steps = new List<PriceStepDto>();

        // --- 1. Prix de base (portée par défaut, palier 0) ---------------------
        var baseRow = await ResolveAsync(req.ProductId, currencyId.Value, at, 0m, null, null, ct);
        var basePrice = baseRow?.Amount ?? 0m;

        // --- 2. Liste tarifaire / tarif client + palier quantité --------------
        // Même règle que l'encaissement (A-013) : liste choisie, sinon celle du groupe du client.
        var priceListId = req.PriceListId
            ?? await PriceListResolver.ForCustomerGroupAsync(db, Company, customerGroupId, currencyId.Value, ct);
        var scoped = await ResolveAsync(req.ProductId, currencyId.Value, at, qty, priceListId, customerGroupId, ct)
                     ?? baseRow;
        var winning = scoped ?? new PriceRow(0m, false, null, 0m, null, null);
        var unit = winning.Amount;

        steps.Add(new PriceStepDto("base_price", "Prix de base catalogue", basePrice, R4(basePrice),
            baseRow is null ? "aucun prix par défaut" : null));

        if (scoped is not null && scoped.Amount != basePrice)
        {
            var (rule, label, reference) = ScopeLabel(scoped);
            steps.Add(new PriceStepDto(rule, label, R4(scoped.Amount - basePrice), R4(scoped.Amount), reference));
        }
        else if (scoped is not null && scoped.QuantityMin > 0m)
        {
            steps.Add(new PriceStepDto("quantity_tier", $"Palier quantité ≥ {Trim(scoped.QuantityMin)}",
                0m, R4(scoped.Amount), $"≥ {Trim(scoped.QuantityMin)}"));
        }

        // --- 3. Taxe applicable ---------------------------------------------
        var taxId = product.DefaultTaxId;
        decimal taxRate = 0m; string? taxCode = null;
        if (taxId is { } tid)
        {
            var tax = await db.Database.SqlQuery<TaxLite>($"""
                SELECT rate AS "Rate", code AS "Code" FROM nexora.tax WHERE id = {tid} AND status = 'active'
                """).FirstOrDefaultAsync(ct);
            if (tax is not null) { taxRate = tax.Rate; taxCode = tax.Code; }
        }

        // --- 4. Extraction de la taxe si prix saisi TTC (§25) ---------------
        var netUnit = unit;
        if (winning.TaxIncluded && taxRate > 0m)
        {
            netUnit = R4(unit / (1m + taxRate / 100m));
            steps.Add(new PriceStepDto("tax_extraction", $"Prix saisi TTC — extraction {taxCode} ({Trim(taxRate)} %)",
                R4(netUnit - unit), netUnit, "HT"));
        }

        decimal lineNet = R2(netUnit * qty);
        decimal discountTotal = 0m;

        // --- 5. Promotion --------------------------------------------------
        var promo = await PromotionResolver.FindAsync(db, Company, req.ProductId, product.CategoryId, product.BrandId, at, ct);
        if (promo is not null)
        {
            var d = PromotionCalculator.Discount(promo, netUnit, qty, lineNet);
            if (d > 0m)
            {
                discountTotal += d;
                steps.Add(new PriceStepDto("promotion", $"Promotion « {promo.Name} »", -d,
                    R4((lineNet - discountTotal) / qty), promo.PromoType));
            }
        }

        // --- 6. Coupon ---------------------------------------------------
        if (!string.IsNullOrWhiteSpace(req.CouponCode))
        {
            var coupon = await db.Database.SqlQuery<CouponRow>($"""
                SELECT id AS "Id", discount_type AS "DiscountType", discount_value AS "DiscountValue",
                       usage_limit AS "UsageLimit", used_count AS "UsedCount"
                FROM nexora.coupon
                WHERE company_id = {Company} AND upper(code) = upper({req.CouponCode.Trim()}) AND status = 'active'
                  AND (valid_from IS NULL OR valid_from <= {at})
                  AND (valid_to IS NULL OR valid_to >= {at})
                """).FirstOrDefaultAsync(ct);
            if (coupon is null)
                steps.Add(new PriceStepDto("coupon", $"Coupon « {req.CouponCode} » non applicable", 0m,
                    R4((lineNet - discountTotal) / qty), "ignoré"));
            else if (coupon.UsageLimit is { } lim && coupon.UsedCount >= lim)
                steps.Add(new PriceStepDto("coupon", $"Coupon « {req.CouponCode} » épuisé", 0m,
                    R4((lineNet - discountTotal) / qty), "ignoré"));
            else if (coupon.DiscountType != "percentage" && discountTotal > 0m)
                // Même règle que la caisse (SqlSalesPricing) : un coupon à montant fixe ne se cumule pas avec une promotion.
                steps.Add(new PriceStepDto("coupon", $"Coupon « {req.CouponCode} » non cumulable avec la promotion", 0m,
                    R4((lineNet - discountTotal) / qty), "ignoré"));
            else
            {
                // Audit 2026-10 éd. 2, A-039 : le simulateur appliquait le pourcentage après la promotion, la caisse sur le
                // montant de la ligne avant promotion (102,96 annoncés, 100,10 encaissés). Le simulateur suit la caisse,
                // qui fait foi : pourcentage sur le montant de la ligne, cumulé à la promotion, remise plafonnée à la ligne.
                var d = coupon.DiscountType == "percentage"
                    ? R2(lineNet * coupon.DiscountValue / 100m)
                    : R2(coupon.DiscountValue);
                d = Math.Min(d, lineNet - discountTotal);
                if (d > 0m)
                {
                    discountTotal += d;
                    steps.Add(new PriceStepDto("coupon", $"Coupon « {req.CouponCode} »", -d,
                        R4((lineNet - discountTotal) / qty), coupon.DiscountType));
                }
            }
        }

        // --- 7. Remise manuelle (bornée par le plancher §46) ---------------
        if (req.ManualDiscount > 0m)
        {
            var remaining = lineNet - discountTotal;
            var floor = winning.MinPrice is { } mp ? mp * qty : 0m;
            var maxAllowed = Math.Max(0m, remaining - floor);
            var d = Math.Min(R2(req.ManualDiscount), maxAllowed);
            discountTotal += d;
            var note = d < R2(req.ManualDiscount) ? "plafonnée au plancher" : null;
            steps.Add(new PriceStepDto("manual_discount", "Remise manuelle", -d,
                R4((lineNet - discountTotal) / qty), note));
        }

        var netAfterDiscount = R2(lineNet - discountTotal);

        // --- 8. Taxe -----------------------------------------------------
        var lineTax = R2(netAfterDiscount * taxRate / 100m);
        steps.Add(new PriceStepDto("tax", taxCode is null ? "Hors taxe" : $"Taxe {taxCode} ({Trim(taxRate)} %)",
            R4(lineTax / qty), R4((netAfterDiscount + lineTax) / qty), taxCode));

        var lineGross = R2(netAfterDiscount + lineTax);
        steps.Add(new PriceStepDto("final", "Prix final ligne (TTC)", 0m, R4(lineGross / qty), null));

        return Result.Success(new PriceQuoteDto(
            req.ProductId, product.ShortName ?? product.Name, qty, req.Currency,
            R4(basePrice), R4(netAfterDiscount / qty), taxRate, taxCode,
            R4(lineTax / qty), R4(lineGross / qty),
            netAfterDiscount, R2(discountTotal), lineTax, lineGross, steps));
    }

    /// <summary>Résout la meilleure ligne de prix pour une portée et un palier donnés.</summary>
    private async Task<PriceRow?> ResolveAsync(
        Guid productId, Guid currencyId, DateTimeOffset at, decimal qty,
        Guid? priceListId, Guid? customerGroupId, CancellationToken ct)
        => await db.Database.SqlQuery<PriceRow>($"""
            SELECT amount AS "Amount", tax_included AS "TaxIncluded", min_price AS "MinPrice",
                   quantity_min AS "QuantityMin", price_list_id AS "PriceListId",
                   customer_group_id AS "CustomerGroupId"
            FROM nexora.product_price
            WHERE product_id = {productId} AND price_type = 'sale' AND currency_id = {currencyId}
              AND quantity_min <= {qty} AND valid_from <= {at}
              AND (valid_to IS NULL OR valid_to > {at})
              AND (price_list_id IS NULL OR price_list_id = {priceListId}::uuid)
              AND (customer_group_id = {customerGroupId}::uuid OR customer_group_id IS NULL)
            ORDER BY (price_list_id IS NOT NULL) DESC,
                     (customer_group_id IS NOT NULL) DESC,
                     quantity_min DESC, valid_from DESC
            LIMIT 1
            """).FirstOrDefaultAsync(ct);

    private static (string Rule, string Label, string? Reference) ScopeLabel(PriceRow row)
    {
        var tier = row.QuantityMin > 0m ? $" · palier ≥ {Trim(row.QuantityMin)}" : "";
        if (row.PriceListId is not null)
            return ("price_list", "Liste tarifaire" + tier, row.PriceListId.ToString());
        if (row.CustomerGroupId is not null)
            return ("customer_price", "Tarif client" + tier, row.CustomerGroupId.ToString());
        return ("quantity_tier", $"Palier quantité ≥ {Trim(row.QuantityMin)}", $"≥ {Trim(row.QuantityMin)}");
    }

    private static string Trim(decimal v) => v.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture);

    private sealed record TaxLite(decimal Rate, string Code);
}
