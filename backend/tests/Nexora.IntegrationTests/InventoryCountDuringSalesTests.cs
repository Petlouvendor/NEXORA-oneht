using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Nexora.Application.Catalog;
using Nexora.Application.Inventory;
using Nexora.Application.Purchasing;
using Nexora.Application.Sales;

namespace Nexora.IntegrationTests;

/// <summary>
/// Audit 2026-10, A-007 : l'écart d'inventaire se calculait sur la quantité figée à l'ouverture. Une vente faite pendant
/// le comptage était retranchée deux fois — vendue, puis « manquante » — et le stock final passait sous le compté.
/// </summary>
[Collection("finance")]
public sealed class InventoryCountDuringSalesTests(FinanceEngineFixture fx)
{
    private static readonly Guid Officine = Guid.Parse("dce00000-0000-0000-0000-00000000a003");
    private static readonly Guid Supplier1 = Guid.Parse("dce00000-0000-0000-0000-00000000b101");
    private static readonly Guid BoxUnit = Guid.Parse("dce00000-0000-0000-0000-00000000a011");
    private static readonly Guid CashAccount = Guid.Parse("dce00000-0000-0000-0000-00000000a051");
    private static readonly Guid CashMethod = Guid.Parse("dce00000-0000-0000-0000-00000000a081");

    private AsyncServiceScope Scope() => fx.Provider.CreateAsyncScope();

    private async Task<Guid> Stocked(decimal qty)
    {
        await using var s = Scope();
        var catalog = s.ServiceProvider.GetRequiredService<CatalogService>();
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var id = (await catalog.CreateProductAsync(new CreateProductCommand(
            $"INV-{suffix}", $"Article inventaire {suffix}", null, BoxUnit, "stockable", null, null, null, null, false, false))).Value;
        await catalog.SetPriceAsync(new SetProductPriceCommand(id, "sale", 10m, "HTG", 0m, null, null));
        await s.ServiceProvider.GetRequiredService<PurchasingService>().ReceiveAsync(new ReceiveGoodsCommand(
            Guid.NewGuid(), Supplier1, Officine, null, [new ReceiveLine(id, null, qty, 5m, null, null)], null, null));
        return id;
    }

    private async Task Sell(Guid product, decimal qty)
    {
        await using var s = Scope();
        var r = await s.ServiceProvider.GetRequiredService<SalesService>().CheckoutAsync(new CheckoutCommand(
            Guid.NewGuid(), Officine, null, null, null, null, "HTG", [new CheckoutLine(product, qty, null, 0m, null)],
            [new TenderInput(CashAccount, CashMethod, qty * 10m, null)], qty * 10m, null, null, null, null));
        r.IsSuccess.Should().BeTrue(r.Error?.Message);
    }

    private async Task<decimal> OnHand(Guid product)
    {
        await using var s = Scope();
        return await s.ServiceProvider.GetRequiredService<IStockLedger>().OnHandAsync(product, Officine);
    }

    [Fact]
    public async Task Counted_after_sales_matches_the_shelf_without_a_false_shortage()
    {
        var p = await Stocked(20m);
        Guid count;
        await using (var s = Scope())
            count = (await s.ServiceProvider.GetRequiredService<InventoryOperationsService>().StartCountAsync(Officine, "partial", [p])).Value;

        await Sell(p, 2m);   // pendant le comptage

        // Le compteur trouve 18 en rayon et le saisit à la validation : aucun écart.
        await using (var s = Scope())
        {
            var r = await s.ServiceProvider.GetRequiredService<InventoryOperationsService>()
                .ValidateCountAsync(count, [new CountLineInput(p, null, 18m)], Guid.NewGuid());
            r.IsSuccess.Should().BeTrue(r.Error?.Message);
            r.Value.Should().BeNull("aucun ajustement : 18 comptés = 18 attendus");
        }
        (await OnHand(p)).Should().Be(18m, "l'ancien calcul postait −2 : stock 16 pour 18 en rayon");
    }

    [Fact]
    public async Task Counted_before_sales_keeps_the_sales_and_posts_only_the_real_variance()
    {
        var p = await Stocked(20m);
        Guid count;
        await using (var s = Scope())
        {
            var ops = s.ServiceProvider.GetRequiredService<InventoryOperationsService>();
            count = (await ops.StartCountAsync(Officine, "partial", [p])).Value;
            // Compté tout de suite : 19 (1 article cassé).
            (await ops.SaveCountAsync(count, [new CountLineInput(p, null, 19m, "1 cassé")])).IsSuccess.Should().BeTrue();
        }

        await Sell(p, 3m);   // après le comptage de la ligne, avant la validation

        await using (var s = Scope())
            (await s.ServiceProvider.GetRequiredService<InventoryOperationsService>()
                .ValidateCountAsync(count, [], Guid.NewGuid())).IsSuccess.Should().BeTrue();
        (await OnHand(p)).Should().Be(16m, "20 − 1 cassé − 3 vendus");
    }

    [Fact]
    public async Task A_lot_tracked_article_is_counted_and_adjusted_lot_by_lot()
    {
        Guid p;
        var suffix = Guid.NewGuid().ToString("N")[..8];
        await using (var s = Scope())
        {
            var catalog = s.ServiceProvider.GetRequiredService<CatalogService>();
            p = (await catalog.CreateProductAsync(new CreateProductCommand(
                $"LOT-{suffix}", $"Article par lot {suffix}", null, BoxUnit, "stockable", null, null, null, null, true, false))).Value;
            (await s.ServiceProvider.GetRequiredService<PurchasingService>().ReceiveAsync(new ReceiveGoodsCommand(
                Guid.NewGuid(), Supplier1, Officine, null,
                [new ReceiveLine(p, null, 10m, 5m, $"A-{suffix}", TestDates.Today.AddMonths(6)),
                 new ReceiveLine(p, null, 5m, 5m, $"B-{suffix}", TestDates.Today.AddMonths(9))], null, null))).IsSuccess.Should().BeTrue();
        }
        var lotA = await Scalar<Guid>($"SELECT id FROM nexora.lot WHERE lot_number = 'A-{suffix}'");
        var lotB = await Scalar<Guid>($"SELECT id FROM nexora.lot WHERE lot_number = 'B-{suffix}'");

        await using (var s = Scope())
        {
            var ops = s.ServiceProvider.GetRequiredService<InventoryOperationsService>();
            var count = (await ops.StartCountAsync(Officine, "partial", [p])).Value;
            (await Scalar<long>($"SELECT COUNT(*) FROM nexora.inventory_count_line WHERE inventory_count_id = '{count}' AND lot_id IS NOT NULL"))
                .Should().Be(2, "une ligne par lot en stock");

            // Lot A : 8 trouvés sur 10 ; lot B : conforme.
            var r = await ops.ValidateCountAsync(count, [new CountLineInput(p, lotA, 8m, "2 périmés"), new CountLineInput(p, lotB, 5m)], Guid.NewGuid());
            r.IsSuccess.Should().BeTrue(r.Error?.Message);
        }

        (await Scalar<decimal>($"SELECT quantity_on_hand FROM nexora.lot_stock WHERE lot_id = '{lotA}' AND inventory_location_id = '{Officine}'"))
            .Should().Be(8m, "l'écart touche le lot compté");
        (await Scalar<decimal>($"SELECT quantity_on_hand FROM nexora.lot_stock WHERE lot_id = '{lotB}' AND inventory_location_id = '{Officine}'"))
            .Should().Be(5m);
        (await OnHand(p)).Should().Be(13m, "stock de l'article = somme de ses lots");
    }

    private async Task<T> Scalar<T>(string sql)
    {
        await using var c = new Npgsql.NpgsqlConnection(fx.TestConnectionString);
        await c.OpenAsync();
        await using var cmd = new Npgsql.NpgsqlCommand(sql, c);
        return (T)(await cmd.ExecuteScalarAsync())!;
    }
}
