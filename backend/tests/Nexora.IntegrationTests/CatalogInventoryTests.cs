using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Nexora.Application.Catalog;
using Nexora.Application.Inventory;
using Nexora.Application.Purchasing;
using Nexora.Infrastructure.Persistence;

namespace Nexora.IntegrationTests;

/// <summary>Scénarios de validation Catalogue / Stocks / Achats (Phase 5 §39).</summary>
[Collection("finance")]
public sealed class CatalogInventoryTests(FinanceEngineFixture fx)
{
    private static readonly Guid Company = Guid.Parse("dce00000-0000-0000-0000-0000000000c1");
    private static readonly Guid Officine = Guid.Parse("dce00000-0000-0000-0000-00000000a003");
    private static readonly Guid Reserve = Guid.Parse("dce00000-0000-0000-0000-00000000a004");
    private static readonly Guid Supplier1 = Guid.Parse("dce00000-0000-0000-0000-00000000b101");
    private static readonly Guid Supplier2 = Guid.Parse("dce00000-0000-0000-0000-00000000b102");
    private static readonly Guid BoxUnit = Guid.Parse("dce00000-0000-0000-0000-00000000a011");
    private static readonly Guid CashHtg = Guid.Parse("dce00000-0000-0000-0000-00000000a051");

    private AsyncServiceScope Scope() => fx.Provider.CreateAsyncScope();

    private async Task<Guid> CreateProduct(string suffix, bool trackLots = false)
    {
        await using var s = Scope();
        var catalog = s.ServiceProvider.GetRequiredService<CatalogService>();
        var r = await catalog.CreateProductAsync(new CreateProductCommand(
            $"IT-{suffix}", $"Produit {suffix}", null, BoxUnit, "stockable",
            $"BC{suffix}", null, null, null, trackLots, false));
        r.IsSuccess.Should().BeTrue(r.Error?.Message);
        return r.Value;
    }

    // ---- §39 : créer un produit (SKU + code-barres) -------------------
    [Fact]
    public async Task Create_product_with_sku_and_barcode()
    {
        var id = await CreateProduct("P1");
        await using var s = Scope();
        var db = s.ServiceProvider.GetRequiredService<NexoraDbContext>();
        var p = await db.Products.AsNoTracking().FirstAsync(x => x.Id == id);
        p.Sku.Should().Be("IT-P1");
        p.Barcode.Should().Be("BCP1");

        var dup = await s.ServiceProvider.GetRequiredService<CatalogService>()
            .CreateProductAsync(new CreateProductCommand("IT-P1", "x", null, BoxUnit, "stockable", null, null, null, null, false, false));
        dup.IsSuccess.Should().BeFalse();
        dup.Error!.Code.Should().Be("catalog.sku_exists");
    }

    // ---- §39 : associer deux fournisseurs ---------------------------
    [Fact]
    public async Task Attach_two_suppliers_one_preferred()
    {
        var id = await CreateProduct("P2");
        await using var s = Scope();
        var catalog = s.ServiceProvider.GetRequiredService<CatalogService>();

        (await catalog.AddSupplierAsync(new AddProductSupplierCommand(id, Supplier1, "S1-REF", 40m, "HTG", 5, false))).IsSuccess.Should().BeTrue();
        (await catalog.AddSupplierAsync(new AddProductSupplierCommand(id, Supplier2, "S2-REF", 38m, "HTG", 7, true))).IsSuccess.Should().BeTrue();

        var db = s.ServiceProvider.GetRequiredService<NexoraDbContext>();
        var links = await db.ProductSuppliers.AsNoTracking().Where(x => x.ProductId == id).ToListAsync();
        links.Should().HaveCount(2);
        links.Count(x => x.IsPreferred).Should().Be(1);
        links.Single(x => x.IsPreferred).SupplierId.Should().Be(Supplier2);
    }

    // ---- §39 : réception → sortie/entrée de stock + coût moyen -------
    [Fact]
    public async Task Receive_goods_updates_stock_and_average_cost()
    {
        var id = await CreateProduct("P3");
        await using var s = Scope();
        var purchasing = s.ServiceProvider.GetRequiredService<PurchasingService>();
        var ledger = s.ServiceProvider.GetRequiredService<IStockLedger>();

        (await purchasing.ReceiveAsync(new ReceiveGoodsCommand(
            Guid.NewGuid(), Supplier1, Officine, null,
            [new ReceiveLine(id, null, 100m, 40m, null, null)], null, null))).IsSuccess.Should().BeTrue();
        (await purchasing.ReceiveAsync(new ReceiveGoodsCommand(
            Guid.NewGuid(), Supplier1, Officine, null,
            [new ReceiveLine(id, null, 100m, 50m, null, null)], null, null))).IsSuccess.Should().BeTrue();

        (await ledger.OnHandAsync(id, Officine)).Should().Be(200m);
        (await ledger.AverageCostAsync(id, Officine)).Should().Be(45m);   // (100*40 + 100*50) / 200
    }

    // ---- §39 : réception partielle sur commande --------------------
    [Fact]
    public async Task Partial_receipt_against_purchase_order()
    {
        var id = await CreateProduct("P4");
        await using var s = Scope();
        var purchasing = s.ServiceProvider.GetRequiredService<PurchasingService>();
        var db = s.ServiceProvider.GetRequiredService<NexoraDbContext>();

        var po = await purchasing.CreateOrderAsync(new CreatePurchaseOrderCommand(
            Supplier1, null, "HTG", null, [new CreatePurchaseOrderLine(id, 100m, 42m, null, 0m)], null));
        po.IsSuccess.Should().BeTrue(po.Error?.Message);
        // Résumé fonctionnel §9 : COMMANDE → APPROBATION → RÉCEPTION.
        (await purchasing.ApproveOrderAsync(po.Value)).IsSuccess.Should().BeTrue();
        var poLine = await db.Set<Nexora.Domain.Purchasing.PurchaseOrderLine>().AsNoTracking()
            .FirstAsync(l => l.PurchaseOrderId == po.Value);

        (await purchasing.ReceiveAsync(new ReceiveGoodsCommand(
            Guid.NewGuid(), Supplier1, Officine, po.Value,
            [new ReceiveLine(id, poLine.Id, 60m, 42m, null, null)], null, null))).IsSuccess.Should().BeTrue();

        var status = await db.PurchaseOrders.AsNoTracking().Where(o => o.Id == po.Value).Select(o => o.Status).FirstAsync();
        status.Should().Be("partially_received");
        var received = await db.Set<Nexora.Domain.Purchasing.PurchaseOrderLine>().AsNoTracking()
            .Where(l => l.Id == poLine.Id).Select(l => l.QuantityReceived).FirstAsync();
        received.Should().Be(60m);
    }

    // ---- §39 : réception avec lot + expiration, puis FEFO ---------
    [Fact]
    public async Task Receipt_with_lots_then_fefo_picks_earliest_expiry()
    {
        var id = await CreateProduct("P5", trackLots: true);
        await using var s = Scope();
        var purchasing = s.ServiceProvider.GetRequiredService<PurchasingService>();
        var ledger = s.ServiceProvider.GetRequiredService<IStockLedger>();

        (await purchasing.ReceiveAsync(new ReceiveGoodsCommand(Guid.NewGuid(), Supplier1, Officine, null,
            [new ReceiveLine(id, null, 30m, 10m, "LOT-DEC", new DateOnly(2026, 12, 1))], null, null))).IsSuccess.Should().BeTrue();
        (await purchasing.ReceiveAsync(new ReceiveGoodsCommand(Guid.NewGuid(), Supplier1, Officine, null,
            [new ReceiveLine(id, null, 30m, 10m, "LOT-JUN", new DateOnly(2026, 6, 1))], null, null))).IsSuccess.Should().BeTrue();

        var picks = await ledger.PickFefoAsync(id, Officine, 40m);
        picks[0].LotNumber.Should().Be("LOT-JUN");   // expire en premier
        picks[0].Quantity.Should().Be(30m);
        picks[1].LotNumber.Should().Be("LOT-DEC");
        picks[1].Quantity.Should().Be(10m);
    }

    // ---- §39 : transfert entre deux entrepôts -------------------
    [Fact]
    public async Task Transfer_moves_stock_between_locations()
    {
        var id = await CreateProduct("P6");
        await using var s = Scope();
        var purchasing = s.ServiceProvider.GetRequiredService<PurchasingService>();
        var inv = s.ServiceProvider.GetRequiredService<InventoryOperationsService>();
        var ledger = s.ServiceProvider.GetRequiredService<IStockLedger>();

        await purchasing.ReceiveAsync(new ReceiveGoodsCommand(Guid.NewGuid(), Supplier1, Officine, null,
            [new ReceiveLine(id, null, 100m, 20m, null, null)], null, null));

        var transfer = await inv.CreateAndShipTransferAsync(new CreateTransferCommand(
            Officine, Reserve, [new TransferLineInput(id, 40m, null)], null));
        transfer.IsSuccess.Should().BeTrue(transfer.Error?.Message);
        (await ledger.OnHandAsync(id, Officine)).Should().Be(60m);

        (await inv.ReceiveTransferAsync(transfer.Value, [])).IsSuccess.Should().BeTrue();
        (await ledger.OnHandAsync(id, Reserve)).Should().Be(40m);
    }

    // ---- §39 : inventaire → écart validé → réévaluation ----------
    [Fact]
    public async Task Inventory_count_variance_posts_adjustment_and_balanced_revaluation()
    {
        var id = await CreateProduct("P7");
        Guid countId;
        await using (var s = Scope())
        {
            await s.ServiceProvider.GetRequiredService<PurchasingService>().ReceiveAsync(new ReceiveGoodsCommand(
                Guid.NewGuid(), Supplier1, Officine, null, [new ReceiveLine(id, null, 100m, 12m, null, null)], null, null));
            var start = await s.ServiceProvider.GetRequiredService<InventoryOperationsService>()
                .StartCountAsync(Officine, "partial", [id]);
            start.IsSuccess.Should().BeTrue(start.Error?.Message);
            countId = start.Value;
        }

        Guid? adjId;
        await using (var s = Scope())
        {
            var v = await s.ServiceProvider.GetRequiredService<InventoryOperationsService>()
                .ValidateCountAsync(countId, [new CountLineInput(id, null, 95m)], Guid.NewGuid());
            v.IsSuccess.Should().BeTrue(v.Error?.Message);
            adjId = v.Value;
        }

        await using (var s = Scope())
        {
            var db = s.ServiceProvider.GetRequiredService<NexoraDbContext>();
            (await s.ServiceProvider.GetRequiredService<IStockLedger>().OnHandAsync(id, Officine)).Should().Be(95m);

            var adj = await db.StockAdjustments.AsNoTracking().Include(a => a.Lines).FirstAsync(a => a.Id == adjId);
            adj.Lines.Single().QuantityDelta.Should().Be(-5m);

            var jeId = await db.JournalEntries.AsNoTracking()
                .Where(e => e.SourceType == "stock_adjustment" && e.SourceId == adjId)
                .Select(e => e.Id).FirstAsync();
            var lines = await db.JournalEntryLines.AsNoTracking().Where(l => l.JournalEntryId == jeId).ToListAsync();
            lines.Sum(l => l.BaseDebit).Should().Be(lines.Sum(l => l.BaseCredit));
            lines.Sum(l => l.BaseDebit).Should().Be(60m);   // 5 * 12
        }
    }

    // ---- §39 : perte / casse -----------------------------------
    [Fact]
    public async Task Loss_writes_off_stock_and_posts_balanced_entry()
    {
        var id = await CreateProduct("P8");
        await using var s = Scope();
        var purchasing = s.ServiceProvider.GetRequiredService<PurchasingService>();
        var inv = s.ServiceProvider.GetRequiredService<InventoryOperationsService>();
        var db = s.ServiceProvider.GetRequiredService<NexoraDbContext>();

        await purchasing.ReceiveAsync(new ReceiveGoodsCommand(Guid.NewGuid(), Supplier1, Officine, null,
            [new ReceiveLine(id, null, 100m, 15m, null, null)], null, null));

        var loss = await inv.RecordLossAsync(new RecordLossCommand(
            Guid.NewGuid(), Officine, "breakage", "Cartons endommagés",
            [new LossLineInput(id, 10m, null)], null));
        loss.IsSuccess.Should().BeTrue(loss.Error?.Message);

        (await s.ServiceProvider.GetRequiredService<IStockLedger>().OnHandAsync(id, Officine)).Should().Be(90m);
        var jeId = await db.JournalEntries.AsNoTracking()
            .Where(e => e.SourceType == "stock_adjustment" && e.SourceId == loss.Value).Select(e => e.Id).FirstAsync();
        var lines = await db.JournalEntryLines.AsNoTracking().Where(l => l.JournalEntryId == jeId).ToListAsync();
        lines.Sum(l => l.BaseDebit).Should().Be(150m);   // 10 * 15
        lines.Sum(l => l.BaseDebit).Should().Be(lines.Sum(l => l.BaseCredit));
    }

    // ---- §39 : achat comptant & à crédit -----------------------
    [Fact]
    public async Task Credit_purchase_invoice_creates_payable_and_cash_purchase_moves_cash()
    {
        var id = await CreateProduct("P9");
        await using var s = Scope();
        var purchasing = s.ServiceProvider.GetRequiredService<PurchasingService>();
        var ledger = s.ServiceProvider.GetRequiredService<IStockLedger>();
        var db = s.ServiceProvider.GetRequiredService<NexoraDbContext>();

        var credit = await purchasing.RecordInvoiceAsync(new RecordPurchaseInvoiceCommand(
            Guid.NewGuid(), Supplier1, null, "HTG", new DateOnly(2026, 3, 1), new DateOnly(2026, 4, 1),
            OnCredit: true, TenderFinancialAccountId: null,
            [new InvoiceLine(id, "Ligne", 10m, 30m, null, 0m, 0m)], CapitaliseToInventory: true));
        credit.IsSuccess.Should().BeTrue(credit.Error?.Message);
        credit.Value!.PayableId.Should().NotBeNull();

        var before = await CashBalance(db);
        var cash = await purchasing.RecordInvoiceAsync(new RecordPurchaseInvoiceCommand(
            Guid.NewGuid(), Supplier1, null, "HTG", new DateOnly(2026, 3, 2), null,
            OnCredit: false, TenderFinancialAccountId: CashHtg,
            [new InvoiceLine(id, "Ligne", 5m, 30m, null, 0m, 0m)], CapitaliseToInventory: true));
        cash.IsSuccess.Should().BeTrue(cash.Error?.Message);
        (await CashBalance(db) - before).Should().Be(-150m);
        _ = ledger;
    }

    // ---- §39 : retour fournisseur -----------------------------
    [Fact]
    public async Task Supplier_return_reduces_stock_and_posts_entry()
    {
        var id = await CreateProduct("P10");
        await using var s = Scope();
        var purchasing = s.ServiceProvider.GetRequiredService<PurchasingService>();
        var ledger = s.ServiceProvider.GetRequiredService<IStockLedger>();
        var db = s.ServiceProvider.GetRequiredService<NexoraDbContext>();

        await purchasing.ReceiveAsync(new ReceiveGoodsCommand(Guid.NewGuid(), Supplier1, Officine, null,
            [new ReceiveLine(id, null, 50m, 25m, null, null)], null, null));

        var ret = await purchasing.RecordReturnAsync(new RecordSupplierReturnCommand(
            Guid.NewGuid(), Supplier1, Officine, null, null, "HTG", "Non conforme",
            [new ReturnLine(id, 12m, 25m, 0m, null)]));
        ret.IsSuccess.Should().BeTrue(ret.Error?.Message);

        (await ledger.OnHandAsync(id, Officine)).Should().Be(38m);
        var jeId = await db.JournalEntries.AsNoTracking()
            .Where(e => e.SourceType == "supplier_return" && e.SourceId == ret.Value).Select(e => e.Id).FirstAsync();
        var lines = await db.JournalEntryLines.AsNoTracking().Where(l => l.JournalEntryId == jeId).ToListAsync();
        lines.Sum(l => l.BaseDebit).Should().Be(lines.Sum(l => l.BaseCredit));
    }

    // ---- §39 : alerte de réapprovisionnement -----------------
    [Fact]
    public async Task Reorder_suggestion_appears_when_stock_below_point()
    {
        var id = await CreateProduct("P11");
        await using var s = Scope();
        var catalog = s.ServiceProvider.GetRequiredService<CatalogService>();
        var purchasing = s.ServiceProvider.GetRequiredService<PurchasingService>();
        var reorder = s.ServiceProvider.GetRequiredService<IReorderService>();

        await catalog.AddSupplierAsync(new AddProductSupplierCommand(id, Supplier1, null, 10m, "HTG", 3, true));
        await catalog.SetReorderRuleAsync(new SetReorderRuleCommand(id, Officine, 5m, 100m, 20m, 80m, 3, false));
        await purchasing.ReceiveAsync(new ReceiveGoodsCommand(Guid.NewGuid(), Supplier1, Officine, null,
            [new ReceiveLine(id, null, 10m, 10m, null, null)], null, null));   // 10 <= reorder point 20

        var suggestions = await reorder.SuggestionsAsync();
        suggestions.Should().Contain(x => x.ProductId == id);
        suggestions.Single(x => x.ProductId == id).PreferredSupplierId.Should().Be(Supplier1);
    }

    private static Task<decimal> CashBalance(NexoraDbContext db)
        => db.FinancialAccounts.AsNoTracking().Where(a => a.Id == CashHtg).Select(a => a.CurrentBalance).FirstAsync();
}
