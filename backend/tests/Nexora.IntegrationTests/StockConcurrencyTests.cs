using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Nexora.Application.Catalog;
using Nexora.Application.Inventory;
using Nexora.Application.Purchasing;

namespace Nexora.IntegrationTests;

/// <summary>
/// Audit 2026-10 éd. 2, A-038 (constaté à l'exécution) : réceptions, annulations de transfert et validations d'inventaire
/// contrôlaient l'état du document hors transaction et sans verrou. Huit réceptions simultanées d'un transfert d'une
/// unité en recevaient sept ou huit : du stock créé sans avoir quitté la source.
/// </summary>
[Collection("finance")]
public sealed class StockConcurrencyTests(FinanceEngineFixture fx)
{
    private static readonly Guid Officine = Guid.Parse("dce00000-0000-0000-0000-00000000a003");
    private static readonly Guid Reserve = Guid.Parse("dce00000-0000-0000-0000-00000000a004");
    private static readonly Guid Supplier1 = Guid.Parse("dce00000-0000-0000-0000-00000000b101");
    private static readonly Guid BoxUnit = Guid.Parse("dce00000-0000-0000-0000-00000000a011");

    private AsyncServiceScope Scope() => fx.Provider.CreateAsyncScope();

    private async Task<Guid> StockedAsync(decimal qty)
    {
        await using var s = Scope();
        var catalog = s.ServiceProvider.GetRequiredService<CatalogService>();
        var id = (await catalog.CreateProductAsync(new CreateProductCommand(
            $"CC-{Guid.NewGuid():N}"[..20], "Article concurrence", null, BoxUnit, "stockable", null, null, null, null, false, false))).Value;
        await catalog.SetPriceAsync(new SetProductPriceCommand(id, "sale", 100m, "HTG", 0m, null, null));
        (await s.ServiceProvider.GetRequiredService<PurchasingService>().ReceiveAsync(new ReceiveGoodsCommand(
            Guid.NewGuid(), Supplier1, Officine, null, [new ReceiveLine(id, null, qty, 20m, null, null)], null, null)))
            .IsSuccess.Should().BeTrue();
        return id;
    }

    private async Task<Guid> ShippedTransferAsync(Guid product, decimal qty)
    {
        await using var s = Scope();
        var t = await s.ServiceProvider.GetRequiredService<InventoryOperationsService>().CreateAndShipTransferAsync(
            new CreateTransferCommand(Officine, Reserve, [new TransferLineInput(product, qty, null)], null));
        t.IsSuccess.Should().BeTrue(t.Error?.Message);
        return t.Value;
    }

    private async Task<decimal> OnHandAsync(Guid product, Guid location)
    {
        await using var s = Scope();
        return await s.ServiceProvider.GetRequiredService<IStockLedger>().OnHandAsync(product, location);
    }

    [Fact]
    public async Task Simultaneous_receipts_of_a_one_unit_transfer_receive_one_unit()
    {
        var p = await StockedAsync(5m);
        var transfer = await ShippedTransferAsync(p, 1m);

        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(async _ =>
        {
            await using var s = Scope();
            return await s.ServiceProvider.GetRequiredService<InventoryOperationsService>().ReceiveTransferAsync(transfer, []);
        }));

        results.Count(r => r.IsSuccess).Should().Be(1, "un transfert d'une unité se reçoit une fois");
        results.Where(r => !r.IsSuccess).Should().OnlyContain(r => r.Error!.Code == "inventory.transfer_closed");
        (await OnHandAsync(p, Reserve)).Should().Be(1m, "l'ancien code en recevait 7 ou 8");
        (await OnHandAsync(p, Officine)).Should().Be(4m);
    }

    [Fact]
    public async Task A_cancellation_racing_a_receipt_never_duplicates_the_goods()
    {
        var p = await StockedAsync(5m);
        var transfer = await ShippedTransferAsync(p, 2m);

        var receive = Task.Run(async () =>
        {
            await using var s = Scope();
            return await s.ServiceProvider.GetRequiredService<InventoryOperationsService>().ReceiveTransferAsync(transfer, []);
        });
        var cancel = Task.Run(async () =>
        {
            await using var s = Scope();
            return await s.ServiceProvider.GetRequiredService<InventoryOperationsService>().CancelTransferAsync(transfer, "Erreur de saisie");
        });
        var outcomes = await Task.WhenAll(receive, cancel);

        outcomes.Count(r => r.IsSuccess).Should().Be(1, "réception et annulation s'excluent");
        ((await OnHandAsync(p, Officine)) + (await OnHandAsync(p, Reserve)))
            .Should().Be(5m, "la marchandise est soit revenue à la source, soit entrée à destination — jamais les deux");
    }

    [Fact]
    public async Task Simultaneous_validations_of_a_count_post_the_variance_once()
    {
        var p = await StockedAsync(10m);
        Guid count;
        await using (var s = Scope())
            count = (await s.ServiceProvider.GetRequiredService<InventoryOperationsService>().StartCountAsync(Officine, "partial", [p])).Value;

        var results = await Task.WhenAll(Enumerable.Range(0, 4).Select(async _ =>
        {
            await using var s = Scope();
            return await s.ServiceProvider.GetRequiredService<InventoryOperationsService>()
                .ValidateCountAsync(count, [new CountLineInput(p, null, 7m)], Guid.NewGuid());
        }));

        results.Count(r => r.IsSuccess).Should().Be(1);
        (await OnHandAsync(p, Officine)).Should().Be(7m, "un manquant de 3 constaté une seule fois (et non 10 − 4 × 3)");
    }
}
