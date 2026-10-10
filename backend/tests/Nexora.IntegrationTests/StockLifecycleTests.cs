using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Nexora.Application.Catalog;
using Nexora.Application.Inventory;
using Nexora.Application.Purchasing;
using Nexora.Application.Sales;
using Nexora.Contracts.Inventory;
using Nexora.Domain.Inventory;
using Nexora.Infrastructure.Persistence;

namespace Nexora.IntegrationTests;

/// <summary>Stock, entrepôts, lots & inventaires — scénarios de validation Phase 24 §47-48.</summary>
[Collection("finance")]
public sealed class StockLifecycleTests(FinanceEngineFixture fx)
{
    private static readonly Guid Company = Guid.Parse("dce00000-0000-0000-0000-0000000000c1");
    private static readonly Guid Officine = Guid.Parse("dce00000-0000-0000-0000-00000000a003");
    private static readonly Guid Reserve = Guid.Parse("dce00000-0000-0000-0000-00000000a004");
    private static readonly Guid Establishment = Guid.Parse("dce00000-0000-0000-0000-0000000000f1");
    private static readonly Guid Supplier1 = Guid.Parse("dce00000-0000-0000-0000-00000000b101");
    private static readonly Guid BoxUnit = Guid.Parse("dce00000-0000-0000-0000-00000000a011");
    private static readonly Guid CashAccount = Guid.Parse("dce00000-0000-0000-0000-00000000a051");
    private static readonly Guid CashMethod = Guid.Parse("dce00000-0000-0000-0000-00000000a081");

    private AsyncServiceScope Scope() => fx.Provider.CreateAsyncScope();

    private async Task<Guid> NewProduct(IServiceProvider sp, string suffix, bool trackLots = false, decimal price = 100m)
    {
        var catalog = sp.GetRequiredService<CatalogService>();
        var r = await catalog.CreateProductAsync(new CreateProductCommand(
            $"ST-{suffix}", $"Article {suffix}", null, BoxUnit, "stockable", null, null, null, null, trackLots, false));
        r.IsSuccess.Should().BeTrue(r.Error?.Message);
        await catalog.SetPriceAsync(new SetProductPriceCommand(r.Value, "sale", price, "HTG", 0m, null, null));
        return r.Value;
    }

    private static async Task Receive(IServiceProvider sp, Guid product, Guid location, decimal qty, decimal cost,
        string? lot = null, DateOnly? expiry = null)
    {
        var r = await sp.GetRequiredService<PurchasingService>().ReceiveAsync(new ReceiveGoodsCommand(
            Guid.NewGuid(), Supplier1, location, null, [new ReceiveLine(product, null, qty, cost, lot, expiry)], null, null));
        r.IsSuccess.Should().BeTrue(r.Error?.Message);
    }

    private async Task Exec(string sql)
    {
        await using var c = new Npgsql.NpgsqlConnection(fx.TestConnectionString);
        await c.OpenAsync();
        await using var cmd = new Npgsql.NpgsqlCommand(sql, c);
        await cmd.ExecuteNonQueryAsync();
    }

    // ---- §48 : scénario de validation complet — achat → vente → retour → transfert ----
    [Fact]
    public async Task Full_supply_chain_scenario_reconciles()
    {
        await using var s = Scope();
        var ledger = s.ServiceProvider.GetRequiredService<IStockLedger>();
        var ops = s.ServiceProvider.GetRequiredService<InventoryOperationsService>();
        var id = await NewProduct(s.ServiceProvider, "E2E");

        await Receive(s.ServiceProvider, id, Officine, 100m, 20m);
        (await ledger.OnHandAsync(id, Officine)).Should().Be(100m);

        var sale = await s.ServiceProvider.GetRequiredService<SalesService>().CheckoutAsync(new CheckoutCommand(
            Guid.NewGuid(), Officine, null, null, null, null, "HTG",
            [new CheckoutLine(id, 10m, null, 0m, null)],
            [new TenderInput(CashAccount, CashMethod, 1000m, null)], 1000m, null, null, null, null));
        sale.IsSuccess.Should().BeTrue(sale.Error?.Message);
        (await ledger.OnHandAsync(id, Officine)).Should().Be(90m);

        await ledger.PostAsync(new StockMovementRequest(id, Officine, MovementType.SaleReturn, 2m, 20m, "sale_return", Guid.NewGuid()));
        await s.ServiceProvider.GetRequiredService<Nexora.Application.Abstractions.IUnitOfWork>().SaveChangesAsync();
        (await ledger.OnHandAsync(id, Officine)).Should().Be(92m);

        var transfer = await ops.CreateAndShipTransferAsync(new CreateTransferCommand(
            Officine, Reserve, [new TransferLineInput(id, 20m, null)], null));
        transfer.IsSuccess.Should().BeTrue(transfer.Error?.Message);
        (await ledger.OnHandAsync(id, Officine)).Should().Be(72m);

        (await ops.ReceiveTransferAsync(transfer.Value, [])).IsSuccess.Should().BeTrue();
        (await ledger.OnHandAsync(id, Officine)).Should().Be(72m);
        (await ledger.OnHandAsync(id, Reserve)).Should().Be(20m);
    }

    // ---- §13-14 : le balayage des lots expirés sort le stock et bloque la vente ----
    [Fact]
    public async Task Expired_lot_sweep_writes_off_and_removes_it_from_sale()
    {
        await using var s = Scope();
        var ledger = s.ServiceProvider.GetRequiredService<IStockLedger>();
        var lots = s.ServiceProvider.GetRequiredService<ILotService>();
        var db = s.ServiceProvider.GetRequiredService<NexoraDbContext>();
        var id = await NewProduct(s.ServiceProvider, "EXP", trackLots: true);

        var past = TestDates.Today.AddDays(-3);
        await Receive(s.ServiceProvider, id, Officine, 40m, 10m, "LOT-OLD", past);
        (await ledger.OnHandAsync(id, Officine)).Should().Be(40m);

        var sweep = await lots.SweepExpiredAsync();
        sweep.IsSuccess.Should().BeTrue(sweep.Error?.Message);
        sweep.Value!.LotsExpired.Should().BeGreaterThanOrEqualTo(1);
        sweep.Value.StockValueWrittenOff.Should().BeGreaterThanOrEqualTo(400m);   // ≥ 40 * 10

        (await ledger.OnHandAsync(id, Officine)).Should().Be(0m);
        var status = await db.Lots.AsNoTracking().Where(l => l.ProductId == id).Select(l => l.Status).FirstAsync();
        status.Should().Be("expired");

        var act = async () => await ledger.PickFefoAsync(id, Officine, 1m);
        await act.Should().ThrowAsync<InvalidOperationException>();

        // Idempotent : un second passage ne fait rien.
        (await lots.SweepExpiredAsync()).Value!.LotsExpired.Should().Be(0);
    }

    // ---- §14-15 : un lot bloqué est exclu de la sélection FEFO ----
    [Fact]
    public async Task Blocked_lot_is_excluded_from_fefo()
    {
        await using var s = Scope();
        var ledger = s.ServiceProvider.GetRequiredService<IStockLedger>();
        var lots = s.ServiceProvider.GetRequiredService<ILotService>();
        var db = s.ServiceProvider.GetRequiredService<NexoraDbContext>();
        var id = await NewProduct(s.ServiceProvider, "BLK", trackLots: true);

        await Receive(s.ServiceProvider, id, Officine, 30m, 10m, "LOT-JUN", new DateOnly(2027, 6, 1));
        await Receive(s.ServiceProvider, id, Officine, 30m, 10m, "LOT-DEC", new DateOnly(2027, 12, 1));

        var june = await db.Lots.AsNoTracking().Where(l => l.ProductId == id && l.LotNumber == "LOT-JUN").Select(l => l.Id).FirstAsync();
        (await lots.BlockAsync(june, "Suspicion de non-conformité")).IsSuccess.Should().BeTrue();

        var picks = await ledger.PickFefoAsync(id, Officine, 10m);
        picks.Should().ContainSingle().Which.LotNumber.Should().Be("LOT-DEC");

        (await lots.UnblockAsync(june, "Contrôle qualité OK")).IsSuccess.Should().BeTrue();
        (await ledger.PickFefoAsync(id, Officine, 10m))[0].LotNumber.Should().Be("LOT-JUN");
    }

    // ---- §15 : rappel produit — le lot est gelé et tracé ----
    [Fact]
    public async Task Recall_freezes_the_lot_and_records_the_reference()
    {
        await using var s = Scope();
        var lots = s.ServiceProvider.GetRequiredService<ILotService>();
        var db = s.ServiceProvider.GetRequiredService<NexoraDbContext>();
        var ledger = s.ServiceProvider.GetRequiredService<IStockLedger>();
        var id = await NewProduct(s.ServiceProvider, "RCL", trackLots: true);
        await Receive(s.ServiceProvider, id, Officine, 25m, 8m, "LOT-RC", new DateOnly(2027, 9, 1));
        var lotId = await db.Lots.AsNoTracking().Where(l => l.ProductId == id).Select(l => l.Id).FirstAsync();

        (await lots.RecallAsync(lotId, "", "x")).IsSuccess.Should().BeFalse();   // référence obligatoire
        (await lots.RecallAsync(lotId, "RAPPEL-2026-07", "Défaut d'étiquetage")).IsSuccess.Should().BeTrue();

        var lot = await db.Lots.AsNoTracking().FirstAsync(l => l.Id == lotId);
        lot.Status.Should().Be("recalled");
        lot.RecallReference.Should().Be("RAPPEL-2026-07");

        var act = async () => await ledger.PickFefoAsync(id, Officine, 1m);
        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    // ---- §32 : stock négatif interdit par défaut, autorisé par paramètre d'entreprise ----
    [Fact]
    public async Task Negative_stock_is_refused_by_default_and_allowed_by_setting()
    {
        Guid id;
        await using (var s = Scope())
        {
            id = await NewProduct(s.ServiceProvider, "NEG");
            await Receive(s.ServiceProvider, id, Officine, 5m, 30m);
        }

        CheckoutCommand Cmd() => new(
            Guid.NewGuid(), Officine, null, null, null, null, "HTG",
            [new CheckoutLine(id, 8m, null, 0m, null)],
            [new TenderInput(CashAccount, CashMethod, 800m, null)], 800m, null, null, null, null);

        await using (var s = Scope())
        {
            var r = await s.ServiceProvider.GetRequiredService<SalesService>().CheckoutAsync(Cmd());
            r.IsSuccess.Should().BeFalse();
            r.Error!.Code.Should().Be("pos.insufficient_stock");
        }

        await Exec($"""
            INSERT INTO nexora.setting (key, scope, scope_id, company_id, value)
            VALUES ('stock.negative_stock_allowed', 'company', '{Company}', '{Company}', 'true'::jsonb)
            ON CONFLICT (key, scope, scope_id) DO UPDATE SET value = 'true'::jsonb
            """);
        try
        {
            await using var s = Scope();
            var r = await s.ServiceProvider.GetRequiredService<SalesService>().CheckoutAsync(Cmd());
            r.IsSuccess.Should().BeTrue(r.Error?.Message);
            (await s.ServiceProvider.GetRequiredService<IStockLedger>().OnHandAsync(id, Officine)).Should().Be(-3m);
        }
        finally
        {
            await Exec($"DELETE FROM nexora.setting WHERE key = 'stock.negative_stock_allowed' AND company_id = '{Company}'");
        }
    }

    // ---- §13, §40 : rapport des lots proches ou dépassés ----
    [Fact]
    public async Task Expiring_lots_report_lists_near_and_past_expiry()
    {
        await using var s = Scope();
        var lots = s.ServiceProvider.GetRequiredService<ILotService>();
        var id = await NewProduct(s.ServiceProvider, "EXPR", trackLots: true);
        var today = TestDates.Today;
        await Receive(s.ServiceProvider, id, Officine, 10m, 5m, "LOT-NEAR", today.AddDays(10));
        await Receive(s.ServiceProvider, id, Officine, 10m, 5m, "LOT-GONE", today.AddDays(-2));

        var report = await lots.ExpiringAsync(30);
        report.Where(r => r.ProductId == id).Should().HaveCount(2);
        report.Should().Contain(r => r.LotNumber == "LOT-NEAR" && r.ExpiryStatus == "near");
        report.Should().Contain(r => r.LotNumber == "LOT-GONE" && r.ExpiryStatus == "expired" && r.DaysToExpiry < 0);
    }

    // ---- §30, §39 : alertes stock — rupture / seuil / péremption ----
    [Fact]
    public async Task Stock_alerts_flag_low_stock_and_near_expiry()
    {
        await using var s = Scope();
        var catalog = s.ServiceProvider.GetRequiredService<CatalogService>();
        var queries = s.ServiceProvider.GetRequiredService<IStockQueries>();
        var lowId = await NewProduct(s.ServiceProvider, "LOW");
        var lotId = await NewProduct(s.ServiceProvider, "NEAR", trackLots: true);

        await catalog.SetReorderRuleAsync(new SetReorderRuleCommand(lowId, Officine, 5m, 100m, 20m, 80m, 3, false));
        await Receive(s.ServiceProvider, lowId, Officine, 10m, 10m);            // 10 <= reorder point 20
        await Receive(s.ServiceProvider, lotId, Officine, 8m, 5m, "LOT-SOON",
            TestDates.Today.AddDays(7));

        var alerts = await queries.AlertsAsync();
        alerts.Should().Contain(a => a.ProductId == lowId && a.AlertType == "low_stock");
        alerts.Should().Contain(a => a.ProductId == lotId && a.AlertType == "near_expiry");
    }

    // ---- §4-6 : création d'entrepôt et d'emplacement, périmètre & validation ----
    [Fact]
    public async Task Warehouse_and_location_creation_is_scoped_and_validated()
    {
        await using var s = Scope();
        var wh = s.ServiceProvider.GetRequiredService<IWarehouseService>();

        var w = await wh.CreateWarehouseAsync(new CreateWarehouseRequest(
            "Dépôt Sud", "WH-SUD", "general", Establishment, "Route de Léogâne", 5000m));
        w.IsSuccess.Should().BeTrue(w.Error?.Message);

        (await wh.CreateWarehouseAsync(new CreateWarehouseRequest("x", "WH-SUD", "general", Establishment, null, null)))
            .Error!.Code.Should().Be("inventory.warehouse_exists");

        (await wh.CreateLocationAsync(new CreateLocationRequest(
            "Rayon A", "LOC-A", "cold_room", null, null, null, null, null, null, null)))
            .Error!.Code.Should().Be("inventory.location_target");

        var loc = await wh.CreateLocationAsync(new CreateLocationRequest(
            "Zone quarantaine", "LOC-Q", "quarantine", null, null, null, null, null, null, null));
        loc.IsSuccess.Should().BeTrue(loc.Error?.Message);

        var phys = await wh.CreateLocationAsync(new CreateLocationRequest(
            "Allée 1", "LOC-A1", "warehouse", w.Value, null, "A", "1", "3", "B12", 200m));
        phys.IsSuccess.Should().BeTrue(phys.Error?.Message);

        (await s.ServiceProvider.GetRequiredService<IStockQueries>().WarehousesAsync())
            .Should().Contain(x => x.Code == "WH-SUD");
    }

    // ---- §38, §46 : requête des mouvements — filtrage et pagination ----
    [Fact]
    public async Task Stock_movements_query_filters_and_paginates()
    {
        await using var s = Scope();
        var queries = s.ServiceProvider.GetRequiredService<IStockQueries>();
        var id = await NewProduct(s.ServiceProvider, "MV");
        await Receive(s.ServiceProvider, id, Officine, 10m, 4m);
        await Receive(s.ServiceProvider, id, Officine, 5m, 4m);

        var all = await queries.MovementsAsync(id, null, null, null, 100, 0);
        all.Should().HaveCountGreaterThanOrEqualTo(2);
        all.Should().OnlyContain(m => m.ProductId == id);

        var firstPage = await queries.MovementsAsync(id, null, null, null, 1, 0);
        var secondPage = await queries.MovementsAsync(id, null, null, null, 1, 1);
        firstPage.Should().ContainSingle();
        secondPage.Should().ContainSingle();
        firstPage[0].Id.Should().NotBe(secondPage[0].Id);
    }
}
