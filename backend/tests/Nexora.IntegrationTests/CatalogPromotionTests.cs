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
}
