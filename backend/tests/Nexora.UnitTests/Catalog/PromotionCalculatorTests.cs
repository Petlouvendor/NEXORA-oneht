using FluentAssertions;
using Nexora.Application.Catalog;

namespace Nexora.UnitTests.Catalog;

/// <summary>Calcul unique des promotions (encaissement + simulateur) — déterministe et borné.</summary>
public sealed class PromotionCalculatorTests
{
    private static PromotionRule P(string type, decimal value, PromotionConditions? c = null)
        => new(Guid.NewGuid(), "Promo", type, value, c ?? new PromotionConditions());

    [Fact]
    public void Percentage_and_fixed_amount_follow_the_line()
    {
        PromotionCalculator.Discount(P("percentage", 10m), 50m, 3m, 150m).Should().Be(15m);
        PromotionCalculator.Discount(P("amount", 5m), 50m, 3m, 150m).Should().Be(15m);
        PromotionCalculator.Discount(P("price_override", 40m), 50m, 3m, 150m).Should().Be(30m);
    }

    [Fact]
    public void Two_for_one_gives_one_unit_free_per_complete_pair()
    {
        var twoForOne = P("buy_x_get_y", 0m, new PromotionConditions(BuyQuantity: 1m, GetQuantity: 1m));
        PromotionCalculator.Discount(twoForOne, 100m, 1m, 100m).Should().Be(0m);    // paire incomplète
        PromotionCalculator.Discount(twoForOne, 100m, 2m, 200m).Should().Be(100m);
        PromotionCalculator.Discount(twoForOne, 100m, 5m, 500m).Should().Be(200m);  // 2 paires complètes

        var threeForTwo = P("buy_x_get_y", 0m, new PromotionConditions(BuyQuantity: 2m, GetQuantity: 1m));
        PromotionCalculator.Discount(threeForTwo, 30m, 7m, 210m).Should().Be(60m);  // 2 lots de 3 → 2 offerts
    }

    [Fact]
    public void Bundle_sells_n_units_for_the_bundle_price()
    {
        var bundle = P("bundle", 250m, new PromotionConditions(BundleQuantity: 3m));
        PromotionCalculator.Discount(bundle, 100m, 3m, 300m).Should().Be(50m);
        PromotionCalculator.Discount(bundle, 100m, 7m, 700m).Should().Be(100m);     // 2 lots + 1 au prix normal
        PromotionCalculator.Discount(bundle, 100m, 2m, 200m).Should().Be(0m);
    }

    [Fact]
    public void Minimum_quantity_gates_every_promotion()
    {
        var p = P("percentage", 20m, new PromotionConditions(MinQuantity: 5m));
        PromotionCalculator.Discount(p, 10m, 4m, 40m).Should().Be(0m);
        PromotionCalculator.Discount(p, 10m, 5m, 50m).Should().Be(10m);
    }

    [Fact]
    public void Discount_never_exceeds_the_line_nor_goes_negative()
    {
        PromotionCalculator.Discount(P("amount", 80m), 50m, 1m, 50m).Should().Be(50m);
        PromotionCalculator.Discount(P("price_override", 70m), 50m, 1m, 50m).Should().Be(0m);   // prix imposé > prix normal
        PromotionCalculator.Discount(P("bundle", 500m, new PromotionConditions(BundleQuantity: 2m)), 100m, 2m, 200m).Should().Be(0m);
        PromotionCalculator.Discount(P("inconnu", 50m), 50m, 1m, 50m).Should().Be(0m);
        PromotionCalculator.Discount(P("buy_x_get_y", 0m), 50m, 4m, 200m).Should().Be(0m);      // conditions absentes
    }
}
