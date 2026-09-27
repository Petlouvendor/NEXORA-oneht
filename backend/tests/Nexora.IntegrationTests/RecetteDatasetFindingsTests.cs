using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Nexora.Application.Abstractions;
using Nexora.Application.Catalog;
using Nexora.Application.Purchasing;
using Nexora.Application.Sales;
using Nexora.Contracts.Catalog;
using Nexora.Contracts.Purchasing;
using Nexora.Domain.Purchasing;
using Nexora.Infrastructure.Persistence;
using Npgsql;

namespace Nexora.IntegrationTests;

/// <summary>
/// Recette Phase 55 — anomalies révélées par la génération du jeu de données de recette
/// (backend/tools/Nexora.RecetteSeed) : prix multi-devises, réception et retour multi-lignes,
/// vente d'un article sans prix, statut de commande rétrogradé.
/// </summary>
[Collection("finance")]
public sealed class RecetteDatasetFindingsTests(FinanceEngineFixture fx)
{
    private static readonly Guid Pharmacy = Guid.Parse("dce00000-0000-0000-0000-0000000000c1");
    private static readonly Guid Direction = Guid.Parse("dce00000-0000-0000-0000-0000000000d1");
    private static readonly Guid BoxUnit = Guid.Parse("dce00000-0000-0000-0000-00000000a011");
    private static readonly Guid Officine = Guid.Parse("dce00000-0000-0000-0000-00000000a003");
    private static readonly Guid Distrimed = Guid.Parse("dce00000-0000-0000-0000-00000000b101");
    private static readonly Guid Cash = Guid.Parse("dce00000-0000-0000-0000-00000000a051");
    private static readonly Guid CashMethod = Guid.Parse("dce00000-0000-0000-0000-00000000a081");

    private AsyncServiceScope Scope() => fx.Provider.CreateAsyncScope();

    private static T As<T>(IServiceProvider sp) where T : notnull
    {
        var tc = (TestTenantContext)sp.GetRequiredService<ITenantContext>();
        tc.SetUser(Direction);
        tc.SetScope([Pharmacy], [], Pharmacy);
        return sp.GetRequiredService<T>();
    }

    private async Task<T?> Scalar<T>(string sql)
    {
        await using var c = new NpgsqlConnection(fx.TestConnectionString);
        await c.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, c);
        var v = await cmd.ExecuteScalarAsync();
        return v is null or DBNull ? default : (T)v;
    }

    private async Task<Guid> NewProductAsync(decimal? price)
    {
        await using var s = Scope();
        var catalog = As<CatalogService>(s.ServiceProvider);
        var r = await catalog.CreateProductAsync(new CreateProductCommand(
            $"RDS-{Guid.NewGuid():N}"[..14], "Article recette données", null, BoxUnit, "stockable", null, null, null, null, false, false));
        r.IsSuccess.Should().BeTrue(r.Error?.Message);
        if (price is { } p)
            (await catalog.SetPriceAsync(new SetProductPriceCommand(r.Value, "sale", p, "HTG", 0m, null, null))).IsSuccess.Should().BeTrue();
        return r.Value;
    }

    private async Task<(Guid OrderId, Dictionary<Guid, Guid> Lines)> SentOrderAsync(params Guid[] products)
    {
        await using var s = Scope();
        var po = As<PurchasingService>(s.ServiceProvider);
        var id = await po.CreateOrderAsync(new CreatePurchaseOrderCommand(Distrimed, null, "HTG", null,
            products.Select(p => new CreatePurchaseOrderLine(p, 10m, 40m, null, 0m)).ToList(), null));
        id.IsSuccess.Should().BeTrue(id.Error?.Message);
        (await po.ApproveOrderAsync(id.Value)).IsSuccess.Should().BeTrue();
        (await po.SendOrderAsync(id.Value)).IsSuccess.Should().BeTrue();
        var lines = await s.ServiceProvider.GetRequiredService<NexoraDbContext>().Set<PurchaseOrderLine>().AsNoTracking()
            .Where(l => l.PurchaseOrderId == id.Value).ToDictionaryAsync(l => l.ProductId, l => l.Id);
        return (id.Value, lines);
    }

    [Fact]
    public async Task A_price_in_another_currency_does_not_close_the_existing_price()
    {
        var product = await NewProductAsync(260m);
        await using var s = Scope();
        var catalog = As<CatalogService>(s.ServiceProvider);

        (await catalog.SetPriceAsync(new SetProductPriceCommand(product, "sale", 2.10m, "USD", 0m, null, null))).IsSuccess.Should().BeTrue();

        (await Scalar<long>($"SELECT COUNT(*) FROM nexora.product_price WHERE product_id = '{product}' AND valid_to IS NULL"))
            .Should().Be(2, "le prix HTG et le prix USD coexistent");

        (await catalog.SetPriceAsync(new SetProductPriceCommand(product, "sale", 280m, "HTG", 0m, null, null))).IsSuccess.Should().BeTrue();
        (await Scalar<decimal>($"""
            SELECT pp.amount FROM nexora.product_price pp JOIN nexora.currency c ON c.id = pp.currency_id
            WHERE pp.product_id = '{product}' AND pp.valid_to IS NULL AND c.code = 'USD'
            """)).Should().Be(2.10m, "un nouveau prix HTG ne ferme que l'ancien prix HTG");
    }

    [Fact]
    public async Task A_multi_line_receipt_is_posted_and_completes_the_order()
    {
        var a = await NewProductAsync(null);
        var b = await NewProductAsync(null);
        var (order, lines) = await SentOrderAsync(a, b);

        await using var s = Scope();
        var po = As<PurchasingService>(s.ServiceProvider);
        var op = Guid.NewGuid();
        var receipt = await po.ReceiveAsync(new ReceiveGoodsCommand(op, Distrimed, Officine, order,
            [new ReceiveLine(a, lines[a], 10m, 40m, null, null), new ReceiveLine(b, lines[b], 10m, 40m, null, null)], null, null));
        receipt.IsSuccess.Should().BeTrue(receipt.Error?.Message);

        (await Scalar<long>($"SELECT COUNT(*) FROM nexora.stock_movement WHERE reference_id = '{receipt.Value}'")).Should().Be(2);
        (await Scalar<string>($"SELECT status FROM nexora.purchase_order WHERE id = '{order}'")).Should().Be("received");

        var replay = await po.ReceiveAsync(new ReceiveGoodsCommand(op, Distrimed, Officine, order,
            [new ReceiveLine(a, lines[a], 10m, 40m, null, null), new ReceiveLine(b, lines[b], 10m, 40m, null, null)], null, null));
        replay.Value.Should().Be(receipt.Value, "le rejeu de la même opération reste idempotent");
        (await Scalar<long>($"SELECT COUNT(*) FROM nexora.stock_movement WHERE reference_id = '{receipt.Value}'")).Should().Be(2);
    }

    [Fact]
    public async Task A_receipt_not_imputed_to_order_lines_never_downgrades_the_order()
    {
        var a = await NewProductAsync(null);
        var (order, _) = await SentOrderAsync(a);

        await using var s = Scope();
        var r = await As<PurchasingService>(s.ServiceProvider).ReceiveAsync(new ReceiveGoodsCommand(Guid.NewGuid(), Distrimed, Officine, order,
            [new ReceiveLine(a, null, 4m, 40m, null, null)], null, null));
        r.IsSuccess.Should().BeTrue(r.Error?.Message);

        (await Scalar<string>($"SELECT status FROM nexora.purchase_order WHERE id = '{order}'")).Should().Be("sent");
    }

    [Fact]
    public async Task An_article_without_a_price_is_refused_and_a_multi_line_sale_can_be_fully_returned()
    {
        var priced = await NewProductAsync(150m);
        var other = await NewProductAsync(90m);
        var unpriced = await NewProductAsync(null);
        var (order, lines) = await SentOrderAsync(priced, other, unpriced);
        await using (var r = Scope())
            (await As<PurchasingService>(r.ServiceProvider).ReceiveAsync(new ReceiveGoodsCommand(Guid.NewGuid(), Distrimed, Officine, order,
                [new ReceiveLine(priced, lines[priced], 10m, 40m, null, null), new ReceiveLine(other, lines[other], 10m, 40m, null, null),
                 new ReceiveLine(unpriced, lines[unpriced], 10m, 40m, null, null)], null, null))).IsSuccess.Should().BeTrue();

        await using var s = Scope();
        var sales = As<SalesService>(s.ServiceProvider);
        var free = await sales.CheckoutAsync(new CheckoutCommand(Guid.NewGuid(), Officine, null, null, null, null, "HTG",
            [new CheckoutLine(unpriced, 1m, null, 0m, null)], [new TenderInput(Cash, CashMethod, 100m, null)], 100m, null, null, null, null));
        free.Error!.Code.Should().Be("sale.no_price", "sans prix en vigueur l'article partirait gratuitement");

        var sale = await sales.CheckoutAsync(new CheckoutCommand(Guid.NewGuid(), Officine, null, null, null, null, "HTG",
            [new CheckoutLine(priced, 2m, null, 0m, null), new CheckoutLine(other, 1m, null, 0m, null)],
            [new TenderInput(Cash, CashMethod, 400m, null)], 400m, null, null, null, null));
        sale.IsSuccess.Should().BeTrue(sale.Error?.Message);

        var saleLines = await s.ServiceProvider.GetRequiredService<NexoraDbContext>().SaleLines.AsNoTracking()
            .Where(l => l.SaleId == sale.Value!.SaleId).ToListAsync();
        var ret = await As<SalesReturnService>(s.ServiceProvider).ReturnAsync(new ReturnCommand(Guid.NewGuid(), sale.Value!.SaleId, Officine, null,
            "Retour total recette", "cash", Cash, saleLines.Select(l => new ReturnLineInput(l.Id, l.Quantity, true)).ToList()));
        ret.IsSuccess.Should().BeTrue(ret.Error?.Message);
        (await Scalar<long>($"SELECT COUNT(*) FROM nexora.stock_movement WHERE reference_type = 'sale_return' AND reference_id = '{ret.Value!.SaleReturnId}'"))
            .Should().Be(2, "chaque ligne retournée remet son article en stock");
    }
}
