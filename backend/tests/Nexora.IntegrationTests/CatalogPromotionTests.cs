using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Nexora.Application.Catalog;
using Nexora.Application.Purchasing;
using Nexora.Application.Sales;
using Nexora.Contracts.Catalog;
using Nexora.Infrastructure.Persistence;

namespace Nexora.IntegrationTests;

/// <summary>
/// Finalisation produit, phase 1 — promotions et coupons : une seule règle de calcul pour la caisse et
/// le simulateur, prix de liste tarifaire jamais appliqués hors liste, coupon borné par sa validité et
/// sa limite d'utilisation. Aucune promotion « tout le catalogue » n'est créée active (base partagée).
/// </summary>
[Collection("finance")]
public sealed class CatalogPromotionTests(FinanceEngineFixture fx)
{
    private static readonly Guid Officine = Guid.Parse("dce00000-0000-0000-0000-00000000a003");
    private static readonly Guid Supplier1 = Guid.Parse("dce00000-0000-0000-0000-00000000b101");
    private static readonly Guid BoxUnit = Guid.Parse("dce00000-0000-0000-0000-00000000a011");
    private static readonly Guid WholesaleList = Guid.Parse("dce00000-0000-0000-0000-00000000a025");
    private static readonly Guid CashAccount = Guid.Parse("dce00000-0000-0000-0000-00000000a051");
    private static readonly Guid CashMethod = Guid.Parse("dce00000-0000-0000-0000-00000000a081");

    private AsyncServiceScope Scope() => fx.Provider.CreateAsyncScope();
    private static string U() => Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();

    private async Task<Guid> Product(decimal salePrice, decimal stock = 0m)
    {
        await using var s = Scope();
        var catalog = s.ServiceProvider.GetRequiredService<CatalogService>();
        var id = (await catalog.CreateProductAsync(new CreateProductCommand(
            $"PRM-{U()}", "Article promo", null, BoxUnit, "stockable", null, null, null, null, false, false))).Value;
        (await catalog.SetPriceAsync(new SetProductPriceCommand(id, "sale", salePrice, "HTG", 0m, null, null))).IsSuccess.Should().BeTrue();
        if (stock > 0m)
            await s.ServiceProvider.GetRequiredService<PurchasingService>().ReceiveAsync(new ReceiveGoodsCommand(
                Guid.NewGuid(), Supplier1, Officine, null, [new ReceiveLine(id, null, stock, salePrice / 2m, null, null)], null, null));
        return id;
    }

    private static SavePromotionRequest Promo(Guid product, string type, decimal value, PromotionConditionsDto? c = null)
        => new($"Promo {U()}", type, value, 5, null, null, c, [new PromotionTargetDto("product", product, null, null)]);

    private static async Task<PricedLine> PriceAtPos(IServiceProvider sp, Guid product, decimal qty, string? coupon = null)
        => (await sp.GetRequiredService<ISalesPricing>().PriceCartAsync(
            Officine, null, "HTG", DateTimeOffset.UtcNow, [new CheckoutLine(product, qty, null, 0m, null)], coupon))[0];

    [Fact]
    public async Task Price_list_prices_are_never_applied_at_the_pos_without_the_list()
    {
        var product = await Product(100m);
        await using var s = Scope();
        (await s.ServiceProvider.GetRequiredService<CatalogService>().SetPriceAsync(
            new SetProductPriceCommand(product, "sale", 60m, "HTG", 10m, null, null, WholesaleList))).IsSuccess.Should().BeTrue();

        (await PriceAtPos(s.ServiceProvider, product, 12m)).UnitPrice.Should().Be(100m,
            "le prix « Grossiste » n'appartient qu'à sa liste tarifaire");

        var quote = (await s.ServiceProvider.GetRequiredService<ICatalogPricingEngine>().QuoteAsync(
            new PriceQuoteRequest(product, 12m, "HTG", null, WholesaleList, null, null, 0m, null))).Value!;
        quote.NetUnitPrice.Should().Be(60m);
    }

    [Fact]
    public async Task Two_for_one_gives_the_same_discount_at_the_pos_and_in_the_simulator()
    {
        var product = await Product(100m);
        await using var s = Scope();
        (await s.ServiceProvider.GetRequiredService<IPromotionService>().CreateAsync(
            Promo(product, "buy_x_get_y", 0m, new PromotionConditionsDto(null, 1m, 1m, null)))).IsSuccess.Should().BeTrue();

        var pos = await PriceAtPos(s.ServiceProvider, product, 5m);
        pos.PromotionDiscount.Should().Be(200m);   // 2 paires complètes → 2 unités offertes

        var quote = (await s.ServiceProvider.GetRequiredService<ICatalogPricingEngine>().QuoteAsync(
            new PriceQuoteRequest(product, 5m, "HTG", null, null, null, null, 0m, null))).Value!;
        quote.LineDiscount.Should().Be(pos.PromotionDiscount);
        quote.Steps.Should().Contain(st => st.Rule == "promotion" && st.Reference == "buy_x_get_y");
    }

    [Fact]
    public async Task Bundle_and_disabled_promotions_are_applied_consistently()
    {
        var product = await Product(100m);
        await using var s = Scope();
        var promos = s.ServiceProvider.GetRequiredService<IPromotionService>();
        var id = (await promos.CreateAsync(Promo(product, "bundle", 250m, new PromotionConditionsDto(null, null, null, 3m)))).Value;

        (await PriceAtPos(s.ServiceProvider, product, 3m)).PromotionDiscount.Should().Be(50m);

        (await promos.SetStatusAsync(id, "inactive")).IsSuccess.Should().BeTrue();
        (await PriceAtPos(s.ServiceProvider, product, 3m)).PromotionDiscount.Should().Be(0m);
    }

    [Fact]
    public async Task Promotion_rules_are_refused_when_they_cannot_be_computed()
    {
        var product = await Product(100m);
        await using var s = Scope();
        var promos = s.ServiceProvider.GetRequiredService<IPromotionService>();

        (await promos.CreateAsync(Promo(product, "buy_x_get_y", 0m))).Error!.Code.Should().Be("catalog.promotion_conditions");
        (await promos.CreateAsync(Promo(product, "buy_x_get_y", 0m, new PromotionConditionsDto(null, 1.5m, 1m, null))))
            .Error!.Code.Should().Be("catalog.promotion_conditions");
        (await promos.CreateAsync(Promo(product, "bundle", 0m, new PromotionConditionsDto(null, null, null, 3m))))
            .Error!.Code.Should().Be("catalog.promotion_conditions");
        (await promos.CreateAsync(Promo(product, "percentage", 150m))).Error!.Code.Should().Be("catalog.promotion_value");
        (await promos.CreateAsync(Promo(product, "magique", 10m))).Error!.Code.Should().Be("catalog.promotion_type");
        (await promos.CreateAsync(Promo(Guid.NewGuid(), "percentage", 10m))).Error!.Code.Should().Be("catalog.promotion_targets");
        (await promos.CreateAsync(Promo(product, "percentage", 10m) with
        {
            Targets = [new PromotionTargetDto("all", null, null, null), new PromotionTargetDto("product", product, null, null)],
        })).Error!.Code.Should().Be("catalog.promotion_targets");
        (await promos.CreateAsync(Promo(product, "percentage", 10m) with
        {
            StartsAt = DateTimeOffset.UtcNow, EndsAt = DateTimeOffset.UtcNow.AddDays(-1),
        })).Error!.Code.Should().Be("catalog.promotion_period");
    }

    [Fact]
    public async Task Updating_a_promotion_replaces_its_targets_and_conditions()
    {
        var first = await Product(100m);
        var second = await Product(100m);
        await using var s = Scope();
        var promos = s.ServiceProvider.GetRequiredService<IPromotionService>();
        var id = (await promos.CreateAsync(Promo(first, "percentage", 10m))).Value;

        (await promos.UpdateAsync(id, Promo(second, "percentage", 20m, new PromotionConditionsDto(2m, null, null, null))))
            .IsSuccess.Should().BeTrue();

        (await PriceAtPos(s.ServiceProvider, first, 2m)).PromotionDiscount.Should().Be(0m);
        (await PriceAtPos(s.ServiceProvider, second, 1m)).PromotionDiscount.Should().Be(0m, "quantité minimale 2");
        (await PriceAtPos(s.ServiceProvider, second, 2m)).PromotionDiscount.Should().Be(40m);

        var listed = (await promos.ListAsync()).Single(p => p.Id == id);
        listed.Targets.Should().ContainSingle(t => t.ProductId == second);
        listed.Conditions!.MinQuantity.Should().Be(2m);
    }

    [Fact]
    public async Task Coupon_usage_limit_is_consumed_by_the_sale_and_then_enforced()
    {
        var product = await Product(100m, stock: 20m);
        await using var s = Scope();
        var promos = s.ServiceProvider.GetRequiredService<IPromotionService>();
        var code = $"once{U()}";
        var couponId = (await promos.CreateCouponAsync(new CreateCouponRequest(code, "percentage", 10m, 1, null, null))).Value;
        (await promos.CreateCouponAsync(new CreateCouponRequest(code.ToUpperInvariant(), "amount", 5m, null, null, null)))
            .Error!.Code.Should().Be("catalog.coupon_exists");

        var sales = s.ServiceProvider.GetRequiredService<SalesService>();
        CheckoutCommand Sale(decimal paid) => new(Guid.NewGuid(), Officine, null, null, null, null, "HTG",
            [new CheckoutLine(product, 1m, null, 0m, null)], [new TenderInput(CashAccount, CashMethod, paid, null)],
            paid, null, code.ToUpperInvariant(), null, null);

        var first = await sales.CheckoutAsync(Sale(90m));
        first.IsSuccess.Should().BeTrue(first.Error?.Message);
        first.Value!.Total.Should().Be(90m);

        var used = await s.ServiceProvider.GetRequiredService<NexoraDbContext>().Database.SqlQuery<int>($"""
            SELECT used_count AS "Value" FROM nexora.coupon WHERE id = {couponId}
            """).FirstAsync();
        used.Should().Be(1);

        // Limite atteinte : le coupon n'est plus appliqué, la vente se fait au prix normal.
        (await PriceAtPos(s.ServiceProvider, product, 1m, code.ToUpperInvariant())).CouponId.Should().BeNull();
        var second = await sales.CheckoutAsync(Sale(100m));
        second.IsSuccess.Should().BeTrue(second.Error?.Message);
        second.Value!.Total.Should().Be(100m);
    }

    /// <summary>
    /// Audit 2026-10, A-013 : une liste tarifaire rattachée au groupe d'un client n'était jamais appliquée à une vente
    /// réelle — le simulateur pouvait annoncer un prix que la caisse ne facturait pas.
    /// </summary>
    [Fact]
    public async Task Price_list_of_the_customer_group_applies_at_the_pos_and_in_the_simulator()
    {
        var product = await Product(100m);
        var group = Guid.NewGuid();
        var customer = Guid.NewGuid();
        await using (var c = new Npgsql.NpgsqlConnection(fx.TestConnectionString))
        {
            await c.OpenAsync();
            await using var cmd = new Npgsql.NpgsqlCommand($"""
                INSERT INTO nexora.customer_group (id, company_id, name, status)
                VALUES ('{group}', 'dce00000-0000-0000-0000-0000000000c1', 'Membres {group:N}', 'active');
                INSERT INTO nexora.customer (id, company_id, customer_number, name, customer_group_id, default_currency_id)
                VALUES ('{customer}', 'dce00000-0000-0000-0000-0000000000c1', 'CLI-{customer:N}', 'Membre', '{group}',
                        (SELECT id FROM nexora.currency WHERE code = 'HTG'));
                """, c);
            await cmd.ExecuteNonQueryAsync();
        }

        await using var s = Scope();
        var catalog = s.ServiceProvider.GetRequiredService<CatalogService>();
        var list = await catalog.CreatePriceListAsync(new CreatePriceListRequest($"MBR{U()}", "Membres", "member", "HTG", group, 20));
        list.IsSuccess.Should().BeTrue(list.Error?.Message);
        (await catalog.SetPriceAsync(new SetProductPriceCommand(product, "sale", 80m, "HTG", 0m, null, null, list.Value)))
            .IsSuccess.Should().BeTrue();

        var pricing = s.ServiceProvider.GetRequiredService<ISalesPricing>();
        (await pricing.PriceCartAsync(Officine, customer, "HTG", DateTimeOffset.UtcNow, [new CheckoutLine(product, 1m, null, 0m, null)], null))
            .Single().UnitPrice.Should().Be(80m, "le client du groupe paie le prix de sa liste");
        (await pricing.PriceCartAsync(Officine, null, "HTG", DateTimeOffset.UtcNow, [new CheckoutLine(product, 1m, null, 0m, null)], null))
            .Single().UnitPrice.Should().Be(100m, "sans client, prix de base");

        var quote = await s.ServiceProvider.GetRequiredService<ICatalogPricingEngine>().QuoteAsync(
            new PriceQuoteRequest(product, 1m, "HTG", customer, null, Officine, null, 0m, null));
        quote.Value!.NetUnitPrice.Should().Be(80m, "le simulateur annonce le prix facturé");
    }

    /// <summary>Audit 2026-10, A-012 : un coupon fixe de 50 remisait 50 PAR LIGNE (100 sur deux lignes, 150 sur trois).</summary>
    [Fact]
    public async Task Fixed_amount_coupon_applies_once_per_cart_spread_over_the_lines()
    {
        var a = await Product(100m);
        var b = await Product(100m);
        var c = await Product(50m);
        await using var s = Scope();
        var code = $"fix{U()}";
        (await s.ServiceProvider.GetRequiredService<IPromotionService>().CreateCouponAsync(
            new CreateCouponRequest(code, "amount", 50m, null, null, null))).IsSuccess.Should().BeTrue();

        var lines = await s.ServiceProvider.GetRequiredService<ISalesPricing>().PriceCartAsync(
            Officine, null, "HTG", DateTimeOffset.UtcNow,
            [new CheckoutLine(a, 1m, null, 0m, null), new CheckoutLine(b, 1m, null, 0m, null), new CheckoutLine(c, 1m, null, 0m, null)],
            code.ToUpperInvariant());

        lines.Sum(l => l.PromotionDiscount).Should().Be(50m, "un coupon de 50 remise 50 sur le panier");
        lines.Select(l => l.PromotionDiscount).Should().Equal(20m, 20m, 10m);   // au prorata de 100 / 100 / 50

        // Panier plus petit que le coupon : remise plafonnée au panier.
        var small = await s.ServiceProvider.GetRequiredService<ISalesPricing>().PriceCartAsync(
            Officine, null, "HTG", DateTimeOffset.UtcNow, [new CheckoutLine(c, 0.5m, null, 0m, null)], code.ToUpperInvariant());
        small.Single().PromotionDiscount.Should().Be(25m);
    }

    [Fact]
    public async Task Coupon_outside_its_validity_period_is_ignored_at_the_pos()
    {
        var product = await Product(100m);
        await using var s = Scope();
        var code = $"old{U()}";
        (await s.ServiceProvider.GetRequiredService<IPromotionService>().CreateCouponAsync(new CreateCouponRequest(
            code, "percentage", 10m, null, DateTimeOffset.UtcNow.AddDays(-10), DateTimeOffset.UtcNow.AddDays(-1))))
            .IsSuccess.Should().BeTrue();

        var line = await PriceAtPos(s.ServiceProvider, product, 1m, code.ToUpperInvariant());
        line.CouponId.Should().BeNull();
        line.PromotionDiscount.Should().Be(0m);
    }

    // Audit 2026-10 éd. 2, A-039 (constaté à l'exécution) : promotion −20 % et coupon −10 % sur un article à 130 —
    // simulateur 102,96 (coupon après promotion), caisse 100,10 (coupon sur le montant de la ligne). Le simulateur suit
    // désormais la caisse, qui fait foi ; un coupon à montant fixe ne se cumule pas avec une promotion, des deux côtés.
    [Fact]
    public async Task Promotion_and_percentage_coupon_give_the_same_price_at_the_pos_and_in_the_simulator()
    {
        var product = await Product(130m);
        var code = $"A039{U()}";
        await using var s = Scope();
        (await s.ServiceProvider.GetRequiredService<IPromotionService>().CreateAsync(Promo(product, "percentage", 20m))).IsSuccess.Should().BeTrue();
        (await s.ServiceProvider.GetRequiredService<IPromotionService>().CreateCouponAsync(
            new CreateCouponRequest(code, "percentage", 10m, null, null, null))).IsSuccess.Should().BeTrue();

        var pos = await PriceAtPos(s.ServiceProvider, product, 1m, code);
        var quote = (await s.ServiceProvider.GetRequiredService<ICatalogPricingEngine>().QuoteAsync(
            new PriceQuoteRequest(product, 1m, "HTG", null, null, null, null, 0m, code))).Value!;

        pos.PromotionDiscount.Should().Be(39m, "20 % + 10 % du montant de la ligne");
        quote.LineDiscount.Should().Be(pos.PromotionDiscount, "avant : 36,40 au simulateur contre 39 à la caisse");
    }

    [Fact]
    public async Task A_fixed_coupon_is_not_combined_with_a_promotion_in_the_simulator_either()
    {
        var product = await Product(130m);
        var code = $"A039F{U()}";
        await using var s = Scope();
        (await s.ServiceProvider.GetRequiredService<IPromotionService>().CreateAsync(Promo(product, "percentage", 20m))).IsSuccess.Should().BeTrue();
        (await s.ServiceProvider.GetRequiredService<IPromotionService>().CreateCouponAsync(
            new CreateCouponRequest(code, "amount", 10m, null, null, null))).IsSuccess.Should().BeTrue();

        var quote = (await s.ServiceProvider.GetRequiredService<ICatalogPricingEngine>().QuoteAsync(
            new PriceQuoteRequest(product, 1m, "HTG", null, null, null, null, 0m, code))).Value!;
        quote.LineDiscount.Should().Be(26m, "la promotion seule, comme à la caisse");
        quote.Steps.Should().Contain(st => st.Rule == "coupon" && st.Reference == "ignoré");
    }
}
