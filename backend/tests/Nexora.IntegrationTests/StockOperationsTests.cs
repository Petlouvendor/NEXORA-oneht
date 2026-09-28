using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Nexora.Application.Catalog;
using Nexora.Application.Inventory;
using Nexora.Application.Purchasing;
using Nexora.Infrastructure.Persistence;

namespace Nexora.IntegrationTests;

/// <summary>
/// Finalisation produit, phase 2 — opérations de stock : transferts (sur-réception refusée, clôture avec
/// écart, annulation), inventaires (saisie, justification, validation), pertes (dont vol), ajustements ±,
/// tableau de stock. Toute variation passe par un mouvement ; toute valeur est comptabilisée.
/// </summary>
[Collection("finance")]
public sealed class StockOperationsTests(FinanceEngineFixture fx)
{
    private static readonly Guid Officine = Guid.Parse("dce00000-0000-0000-0000-00000000a003");
    private static readonly Guid Reserve = Guid.Parse("dce00000-0000-0000-0000-00000000a004");
    private static readonly Guid Supplier1 = Guid.Parse("dce00000-0000-0000-0000-00000000b101");
    private static readonly Guid BoxUnit = Guid.Parse("dce00000-0000-0000-0000-00000000a011");

    private AsyncServiceScope Scope() => fx.Provider.CreateAsyncScope();
    private static string U() => Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();

    private static async Task<Guid> Stocked(IServiceProvider sp, decimal qty, decimal cost, Guid? location = null)
    {
        var id = (await sp.GetRequiredService<CatalogService>().CreateProductAsync(new CreateProductCommand(
            $"OPS-{U()}", "Article opérations", null, BoxUnit, "stockable", null, null, null, null, false, false))).Value;
        if (qty > 0)
        {
            var r = await sp.GetRequiredService<PurchasingService>().ReceiveAsync(new ReceiveGoodsCommand(
                Guid.NewGuid(), Supplier1, location ?? Officine, null, [new ReceiveLine(id, null, qty, cost, null, null)], null, null));
            r.IsSuccess.Should().BeTrue(r.Error?.Message);
        }
        return id;
    }

    private static Task<decimal> OnHand(IServiceProvider sp, Guid product, Guid location)
        => sp.GetRequiredService<IStockLedger>().OnHandAsync(product, location);

    // ------------------------------------------------------------------ Transferts

    [Fact]
    public async Task Transfer_cannot_receive_more_than_shipped_and_closing_books_the_shortage()
    {
        await using var s = Scope();
        var ops = s.ServiceProvider.GetRequiredService<InventoryOperationsService>();
        var queries = s.ServiceProvider.GetRequiredService<IInventoryDocumentQueries>();
        var p = await Stocked(s.ServiceProvider, 50m, 10m);

        var t = await ops.CreateAndShipTransferAsync(new CreateTransferCommand(Officine, Reserve, [new TransferLineInput(p, 20m, null)], "camion 3"));
        t.IsSuccess.Should().BeTrue(t.Error?.Message);
        (await OnHand(s.ServiceProvider, p, Officine)).Should().Be(30m);
        var line = (await queries.TransferAsync(t.Value))!.Lines.Single();

        // Recevoir plus que le reste en transit créerait du stock qui n'a jamais quitté la source.
        (await ops.ReceiveTransferAsync(t.Value, [new ReceiveTransferLineInput(line.Id, 25m)]))
            .Error!.Code.Should().Be("inventory.transfer_over_receipt");
        (await ops.ReceiveTransferAsync(t.Value, [new ReceiveTransferLineInput(line.Id, 17m)])).IsSuccess.Should().BeTrue();
        (await OnHand(s.ServiceProvider, p, Reserve)).Should().Be(17m);
        (await queries.TransferAsync(t.Value))!.Transfer.Status.Should().Be("partially_received");

        // Annulation impossible après une réception partielle ; la clôture constate 3 unités perdues en transit.
        (await ops.CancelTransferAsync(t.Value, "erreur")).Error!.Code.Should().Be("inventory.transfer_partially_received");
        (await ops.CloseTransferAsync(t.Value, " ", Guid.NewGuid())).Error!.Code.Should().Be("inventory.reason_required");
        var closed = await ops.CloseTransferAsync(t.Value, "3 cartons écrasés pendant le transport", Guid.NewGuid());
        closed.IsSuccess.Should().BeTrue(closed.Error?.Message);

        var adjustment = (await queries.AdjustmentAsync(closed.Value!.Value))!;
        adjustment.Adjustment.AdjustmentType.Should().Be("loss");
        adjustment.Adjustment.SourceType.Should().Be("stock_transfer");
        adjustment.Adjustment.JournalEntryId.Should().NotBeNull("la perte en transit est comptabilisée");
        adjustment.Lines.Single().QuantityDelta.Should().Be(-3m);
        (await queries.TransferAsync(t.Value))!.Transfer.Status.Should().Be("completed");
        (await OnHand(s.ServiceProvider, p, Officine)).Should().Be(30m, "la clôture ne rend rien à la source");
    }

    [Fact]
    public async Task Cancelling_an_unreceived_transfer_returns_goods_to_the_source()
    {
        await using var s = Scope();
        var ops = s.ServiceProvider.GetRequiredService<InventoryOperationsService>();
        var p = await Stocked(s.ServiceProvider, 40m, 5m);

        var t = await ops.CreateAndShipTransferAsync(new CreateTransferCommand(Officine, Reserve, [new TransferLineInput(p, 15m, null)], null));
        (await OnHand(s.ServiceProvider, p, Officine)).Should().Be(25m);
        (await ops.CancelTransferAsync(t.Value, "Mauvaise destination")).IsSuccess.Should().BeTrue();
        (await OnHand(s.ServiceProvider, p, Officine)).Should().Be(40m);
        (await OnHand(s.ServiceProvider, p, Reserve)).Should().Be(0m);
        (await ops.ReceiveTransferAsync(t.Value, [])).Error!.Code.Should().Be("inventory.transfer_closed");
    }

    [Fact]
    public async Task Transfer_beyond_available_stock_is_a_business_refusal_not_a_crash()
    {
        await using var s = Scope();
        var p = await Stocked(s.ServiceProvider, 5m, 5m);
        var r = await s.ServiceProvider.GetRequiredService<InventoryOperationsService>()
            .CreateAndShipTransferAsync(new CreateTransferCommand(Officine, Reserve, [new TransferLineInput(p, 8m, null)], null));
        r.Error!.Code.Should().Be("inventory.insufficient_stock");
        (await OnHand(s.ServiceProvider, p, Officine)).Should().Be(5m);
    }

    // ------------------------------------------------------------------ Inventaires

    [Fact]
    public async Task Count_is_saved_then_validated_with_a_mandatory_justification()
    {
        await using var s = Scope();
        var ops = s.ServiceProvider.GetRequiredService<InventoryOperationsService>();
        var queries = s.ServiceProvider.GetRequiredService<IInventoryDocumentQueries>();
        var p = await Stocked(s.ServiceProvider, 30m, 12m, Reserve);
        var other = await Stocked(s.ServiceProvider, 10m, 12m, Reserve);

        var count = await ops.StartCountAsync(Reserve, "partial", [p, other]);
        count.IsSuccess.Should().BeTrue(count.Error?.Message);
        (await ops.SaveCountAsync(count.Value, [new CountLineInput(p, null, 27m, "3 flacons cassés")])).IsSuccess.Should().BeTrue();
        var partial = (await queries.CountAsync(count.Value))!;
        partial.Count.Status.Should().Be("counting");
        partial.Lines.Single(l => l.ProductId == p).CountedQuantity.Should().Be(27m);

        (await ops.SaveCountAsync(count.Value, [new CountLineInput(other, null, 10m)])).IsSuccess.Should().BeTrue();
        (await queries.CountAsync(count.Value))!.Count.Status.Should().Be("review");

        // Écart sans commentaire de validation : refusé quand la justification est exigée (route API).
        (await ops.ValidateCountAsync(count.Value, [], Guid.NewGuid(), requireJustification: true))
            .Error!.Code.Should().Be("inventory.justification_required");
        var validated = await ops.ValidateCountAsync(count.Value, [], Guid.NewGuid(), note: "Casse constatée en réserve", requireJustification: true);
        validated.IsSuccess.Should().BeTrue(validated.Error?.Message);

        (await OnHand(s.ServiceProvider, p, Reserve)).Should().Be(27m);
        (await OnHand(s.ServiceProvider, other, Reserve)).Should().Be(10m);
        var adjustment = (await queries.AdjustmentAsync(validated.Value!.Value))!;
        adjustment.Lines.Should().ContainSingle(l => l.ProductId == p && l.QuantityDelta == -3m && l.Note == "3 flacons cassés");
        adjustment.Adjustment.JournalEntryId.Should().NotBeNull();
        (await queries.CountAsync(count.Value))!.Count.Status.Should().Be("validated");
        (await ops.CancelCountAsync(count.Value, "trop tard")).Error!.Code.Should().Be("inventory.count_closed");
    }

    [Fact]
    public async Task Full_count_without_product_list_takes_every_stocked_article_of_the_location()
    {
        await using var s = Scope();
        var ops = s.ServiceProvider.GetRequiredService<InventoryOperationsService>();
        var p = await Stocked(s.ServiceProvider, 8m, 3m, Reserve);
        var count = await ops.StartCountAsync(Reserve, "full", []);
        count.IsSuccess.Should().BeTrue(count.Error?.Message);
        var detail = (await s.ServiceProvider.GetRequiredService<IInventoryDocumentQueries>().CountAsync(count.Value))!;
        detail.Lines.Should().Contain(l => l.ProductId == p && l.SystemQuantity == 8m);
        (await ops.CancelCountAsync(count.Value, "Inventaire de test")).IsSuccess.Should().BeTrue();
        (await ops.StartCountAsync(Reserve, "partial", [])).Error!.Code.Should().Be("inventory.no_lines");
        (await ops.StartCountAsync(Reserve, "hebdo", [p])).Error!.Code.Should().Be("inventory.count_type");
    }

    // ------------------------------------------------------------------ Pertes & ajustements

    [Fact]
    public async Task Theft_is_recorded_as_a_loss_movement_with_its_own_document_type()
    {
        await using var s = Scope();
        var ops = s.ServiceProvider.GetRequiredService<InventoryOperationsService>();
        var p = await Stocked(s.ServiceProvider, 20m, 7m);

        (await ops.RecordLossAsync(new RecordLossCommand(Guid.NewGuid(), Officine, "found", "x", [new LossLineInput(p, 1m, null)], null)))
            .Error!.Code.Should().Be("inventory.loss_type");
        (await ops.RecordLossAsync(new RecordLossCommand(Guid.NewGuid(), Officine, "theft", "  ", [new LossLineInput(p, 1m, null)], null)))
            .Error!.Code.Should().Be("inventory.reason_required");

        var theft = await ops.RecordLossAsync(new RecordLossCommand(
            Guid.NewGuid(), Officine, "theft", "Vol constaté à l'ouverture", [new LossLineInput(p, 2m, null, "rayon 4")], null));
        theft.IsSuccess.Should().BeTrue(theft.Error?.Message);
        (await OnHand(s.ServiceProvider, p, Officine)).Should().Be(18m);

        var doc = (await s.ServiceProvider.GetRequiredService<IInventoryDocumentQueries>().AdjustmentAsync(theft.Value))!;
        doc.Adjustment.AdjustmentType.Should().Be("theft");
        doc.Adjustment.JournalEntryId.Should().NotBeNull();
        doc.Lines.Single().Note.Should().Be("rayon 4");
        var movementType = await s.ServiceProvider.GetRequiredService<NexoraDbContext>().Database.SqlQuery<string>($"""
            SELECT movement_type AS "Value" FROM nexora.stock_movement WHERE reference_id = {theft.Value}
            """).SingleAsync();
        movementType.Should().Be("loss");
    }

    [Fact]
    public async Task Manual_adjustments_go_both_ways_and_new_stock_needs_a_cost()
    {
        await using var s = Scope();
        var ops = s.ServiceProvider.GetRequiredService<InventoryOperationsService>();
        var stocked = await Stocked(s.ServiceProvider, 10m, 4m);
        var fresh = await Stocked(s.ServiceProvider, 0m, 0m);
        AdjustmentLineInput L(Guid p, decimal d, decimal? cost = null) => new(p, d, null, cost, null);

        var minus = await ops.RecordAdjustmentAsync(new RecordAdjustmentCommand(Guid.NewGuid(), Officine, "correction", "Erreur de saisie réception", [L(stocked, -2m)], null));
        minus.IsSuccess.Should().BeTrue(minus.Error?.Message);
        (await OnHand(s.ServiceProvider, stocked, Officine)).Should().Be(8m);
        (await ops.RecordAdjustmentAsync(new RecordAdjustmentCommand(Guid.NewGuid(), Officine, "correction", "Recompté", [L(stocked, 3m)], null)))
            .IsSuccess.Should().BeTrue();
        (await OnHand(s.ServiceProvider, stocked, Officine)).Should().Be(11m);

        (await ops.RecordAdjustmentAsync(new RecordAdjustmentCommand(Guid.NewGuid(), Officine, "found", "Carton retrouvé", [L(stocked, -1m)], null)))
            .Error!.Code.Should().Be("inventory.quantity");
        (await ops.RecordAdjustmentAsync(new RecordAdjustmentCommand(Guid.NewGuid(), Officine, "found", "Carton retrouvé", [L(fresh, 6m)], null)))
            .Error!.Code.Should().Be("inventory.unit_cost_required", "sans coût moyen, le coût de l'entrée doit être fourni");
        (await ops.RecordAdjustmentAsync(new RecordAdjustmentCommand(Guid.NewGuid(), Officine, "found", "Carton retrouvé", [L(fresh, 6m, 9m)], null)))
            .IsSuccess.Should().BeTrue();
        (await OnHand(s.ServiceProvider, fresh, Officine)).Should().Be(6m);
        (await ops.RecordAdjustmentAsync(new RecordAdjustmentCommand(Guid.NewGuid(), Officine, "loss", "x", [L(stocked, 1m)], null)))
            .Error!.Code.Should().Be("inventory.adjustment_type");
    }

    // ------------------------------------------------------------------ Tableau de stock

    [Fact]
    public async Task Overview_classifies_stock_against_reorder_thresholds()
    {
        await using var s = Scope();
        var catalog = s.ServiceProvider.GetRequiredService<CatalogService>();
        var queries = s.ServiceProvider.GetRequiredService<IInventoryDocumentQueries>();
        var low = await Stocked(s.ServiceProvider, 5m, 2m);
        var over = await Stocked(s.ServiceProvider, 90m, 2m);
        var never = await Stocked(s.ServiceProvider, 0m, 0m);
        (await catalog.SetReorderRuleAsync(new SetReorderRuleCommand(low, null, 0m, 100m, 10m, 50m, 0, false))).IsSuccess.Should().BeTrue();
        (await catalog.SetReorderRuleAsync(new SetReorderRuleCommand(over, null, 0m, 50m, 10m, 0m, 0, false))).IsSuccess.Should().BeTrue();
        (await catalog.SetReorderRuleAsync(new SetReorderRuleCommand(never, null, 1m, 0m, 2m, 0m, 0, false))).IsSuccess.Should().BeTrue();

        var rows = (await queries.OverviewAsync(new StockOverviewQuery(Officine, null, null, null, 1000))).Rows;
        rows.Single(r => r.ProductId == low).StockStatus.Should().Be("low");
        rows.Single(r => r.ProductId == over).StockStatus.Should().Be("over");
        rows.Single(r => r.ProductId == never).StockStatus.Should().Be("out", "un article suivi jamais reçu est une rupture");

        var outOnly = await queries.OverviewAsync(new StockOverviewQuery(Officine, null, "out", null, 1000));
        outOnly.Rows.Should().OnlyContain(r => r.StockStatus == "out");
        outOnly.OutCount.Should().BeGreaterThan(0);
    }
}
