using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Nexora.Application.Catalog;
using Nexora.Application.Inventory;
using Nexora.Application.Purchasing;
using Nexora.Application.Sales;
using Nexora.Application.Sync;
using Nexora.Contracts.Sync;
using Nexora.Infrastructure.Persistence;

namespace Nexora.IntegrationTests;

/// <summary>Scénarios de validation Local, Offline, Hybride & Cloud (Phase 10 §35).</summary>
[Collection("finance")]
public sealed class OfflineSyncTests(FinanceEngineFixture fx)
{
    private static readonly Guid Company = Guid.Parse("dce00000-0000-0000-0000-0000000000c1");
    private static readonly Guid Officine = Guid.Parse("dce00000-0000-0000-0000-00000000a003");
    private static readonly Guid Supplier1 = Guid.Parse("dce00000-0000-0000-0000-00000000b101");
    private static readonly Guid BoxUnit = Guid.Parse("dce00000-0000-0000-0000-00000000a011");
    private static readonly Guid CashAccount = Guid.Parse("dce00000-0000-0000-0000-00000000a051");
    private static readonly Guid CashMethod = Guid.Parse("dce00000-0000-0000-0000-00000000a081");

    private AsyncServiceScope Scope() => fx.Provider.CreateAsyncScope();

    private async Task<Guid> RegisterDevice(IServiceProvider sp, string id)
        => (await sp.GetRequiredService<IDeviceRegistryService>()
            .RegisterAsync(new RegisterDeviceRequest(id, id, "pos", null, null))).Value;

    private async Task<Guid> Stocked(IServiceProvider sp, string suffix, decimal price, decimal cost, decimal qty)
    {
        var catalog = sp.GetRequiredService<CatalogService>();
        var pid = (await catalog.CreateProductAsync(new CreateProductCommand(
            $"SYNC-{suffix}-{Guid.NewGuid():N}", $"Article {suffix}", null, BoxUnit, "stockable", null, null, null, null, false, false))).Value;
        await catalog.SetPriceAsync(new SetProductPriceCommand(pid, "sale", price, "HTG", 0m, null, null));
        await sp.GetRequiredService<PurchasingService>().ReceiveAsync(new ReceiveGoodsCommand(
            Guid.NewGuid(), Supplier1, Officine, null, [new ReceiveLine(pid, null, qty, cost, null, null)], null, null));
        return pid;
    }

    private static SyncOperationInput SaleOp(Guid opUuid, Guid product, decimal qty, decimal? price,
        IReadOnlyList<TenderInput> tenders, Guid? customer = null)
    {
        var cmd = new CheckoutCommand(opUuid, Officine, null, null, null, customer, "HTG",
            [new CheckoutLine(product, qty, price, 0m, null)], tenders, 0m, null, null, null, null);
        return new SyncOperationInput(opUuid, "sale", "create", JsonSerializer.SerializeToElement(cmd));
    }

    private static TenderInput Cash(decimal amount) => new(CashAccount, CashMethod, amount, null);

    // ---- §35 : enregistrer, lister, révoquer un appareil ; un appareil révoqué ne synchronise plus --
    [Fact]
    public async Task Device_register_list_and_revoke()
    {
        await using var s = Scope();
        var registry = s.ServiceProvider.GetRequiredService<IDeviceRegistryService>();
        var sync = s.ServiceProvider.GetRequiredService<ISyncService>();

        var deviceId = await RegisterDevice(s.ServiceProvider, $"DEV-{Guid.NewGuid():N}"[..12]);
        (await registry.ListAsync()).Should().Contain(d => d.Id == deviceId && d.Status == "active");

        (await registry.RevokeAsync(deviceId, new RevokeDeviceRequest("Appareil perdu"))).IsSuccess.Should().BeTrue();
        (await registry.RevokeAsync(deviceId, new RevokeDeviceRequest(""))).IsSuccess.Should().BeFalse();

        var p = await Stocked(s.ServiceProvider, "REV", 100m, 60m, 10m);
        var push = await sync.PushBatchAsync(new PushBatchRequest(deviceId, [SaleOp(Guid.NewGuid(), p, 1m, null, [Cash(100m)])]));
        push.IsSuccess.Should().BeFalse();
        push.Error!.Code.Should().Be("sync.device_revoked");
    }

    // ---- §35 : lot de ventes offline synchronisé → ventes réelles créées ----
    [Fact]
    public async Task Offline_batch_syncs_and_creates_real_sales()
    {
        await using var s = Scope();
        var sync = s.ServiceProvider.GetRequiredService<ISyncService>();
        var ledger = s.ServiceProvider.GetRequiredService<IStockLedger>();
        var device = await RegisterDevice(s.ServiceProvider, $"BATCH-{Guid.NewGuid():N}"[..12]);
        var p = await Stocked(s.ServiceProvider, "BATCH", 100m, 60m, 20m);

        var op1 = Guid.NewGuid();
        var op2 = Guid.NewGuid();
        var result = await sync.PushBatchAsync(new PushBatchRequest(device, [
            SaleOp(op1, p, 3m, null, [Cash(300m)]),
            SaleOp(op2, p, 2m, null, [Cash(200m)]),
        ]));

        result.IsSuccess.Should().BeTrue(result.Error?.Message);
        result.Value!.Confirmed.Should().Be(2);
        result.Value.Operations.Should().OnlyContain(o => o.Status == "SYNCED" && o.ResultEntityId != null);

        (await ledger.OnHandAsync(p, Officine)).Should().Be(15m);   // 20 - 3 - 2

        var db = s.ServiceProvider.GetRequiredService<NexoraDbContext>();
        (await db.Sales.AsNoTracking().CountAsync(x => x.SourceOperationUuid == op1 || x.SourceOperationUuid == op2)).Should().Be(2);
    }

    // ---- §35 : envoyer deux fois le même lot → idempotence ----
    [Fact]
    public async Task Duplicate_push_is_idempotent()
    {
        await using var s = Scope();
        var sync = s.ServiceProvider.GetRequiredService<ISyncService>();
        var device = await RegisterDevice(s.ServiceProvider, $"IDEM-{Guid.NewGuid():N}"[..12]);
        var p = await Stocked(s.ServiceProvider, "IDEM", 100m, 50m, 20m);

        var op = Guid.NewGuid();
        var batch = new PushBatchRequest(device, [SaleOp(op, p, 4m, null, [Cash(400m)])]);

        var first = await sync.PushBatchAsync(batch);
        var firstSale = first.Value!.Operations[0].ResultEntityId;
        var second = await sync.PushBatchAsync(batch);

        second.Value!.Operations[0].Status.Should().Be("SYNCED");
        second.Value.Operations[0].ResultEntityId.Should().Be(firstSale);

        var db = s.ServiceProvider.GetRequiredService<NexoraDbContext>();
        (await db.Sales.AsNoTracking().CountAsync(x => x.SourceOperationUuid == op)).Should().Be(1);
        (await db.StockMovements.AsNoTracking().CountAsync(x => x.SourceOperationUuid == op)).Should().Be(1);
    }

    // ---- §35 : conflit de stock — jamais écrasé silencieusement (§16) ----
    [Fact]
    public async Task Stock_conflict_is_recorded_not_overwritten()
    {
        await using var s = Scope();
        var sync = s.ServiceProvider.GetRequiredService<ISyncService>();
        var device = await RegisterDevice(s.ServiceProvider, $"STK-{Guid.NewGuid():N}"[..12]);
        var p = await Stocked(s.ServiceProvider, "STK", 100m, 40m, 1m);   // 1 seul en stock

        var result = await sync.PushBatchAsync(new PushBatchRequest(device, [
            SaleOp(Guid.NewGuid(), p, 1m, null, [Cash(100m)]),
            SaleOp(Guid.NewGuid(), p, 1m, null, [Cash(100m)]),
        ]));
        result.IsSuccess.Should().BeTrue();
        result.Value!.Confirmed.Should().Be(1);
        result.Value.Conflicts.Should().Be(1);
        result.Value.Operations.Should().Contain(o => o.Status == "CONFLICT" && o.ConflictId != null);

        var db = s.ServiceProvider.GetRequiredService<NexoraDbContext>();
        var conflict = await db.Database.SqlQuery<string>($"""
            SELECT conflict_type AS "Value" FROM nexora.sync_conflict
            WHERE company_id = {Company} AND sync_operation_id IN (
                SELECT id FROM nexora.sync_operation WHERE operation_uuid = {result.Value.Operations.First(o => o.Status == "CONFLICT").OperationUuid})
            """).FirstAsync();
        conflict.Should().Be("stock");
        // Une seule vente créée : le dernier article n'est pas vendu deux fois.
        (await db.StockMovements.AsNoTracking()
            .Where(m => m.ProductId == p && m.MovementTypeValue == "sale").SumAsync(m => m.Quantity)).Should().Be(-1m);
    }

    // ---- §35 : crédit offline selon politique (§17) ----
    [Fact]
    public async Task Offline_credit_follows_policy()
    {
        await using var s = Scope();
        var sync = s.ServiceProvider.GetRequiredService<ISyncService>();
        var policy = s.ServiceProvider.GetRequiredService<IOfflinePolicyService>();
        var device = await RegisterDevice(s.ServiceProvider, $"CRD-{Guid.NewGuid():N}"[..12]);
        var p = await Stocked(s.ServiceProvider, "CRD", 100m, 40m, 20m);

        var customer = Guid.NewGuid();
        var db = s.ServiceProvider.GetRequiredService<NexoraDbContext>();
        await db.Database.ExecuteSqlAsync($"""
            INSERT INTO nexora.customer (id, company_id, customer_number, name, credit_limit, default_currency_id)
            VALUES ({customer}, {Company}, {"CLI-" + customer.ToString("N")[..8]}, 'Client offline', 500000,
                    (SELECT id FROM nexora.currency WHERE code='HTG'))
            """);

        // Politique : crédit offline interdit → conflit.
        (await policy.SetAsync(new SetOfflinePolicyRequest(null, false, null, null, null, null, null, null, null))).IsSuccess.Should().BeTrue();
        var blocked = await sync.PushBatchAsync(new PushBatchRequest(device,
            [SaleOp(Guid.NewGuid(), p, 5m, 100m, [], customer)]));
        blocked.Value!.Operations[0].Status.Should().Be("CONFLICT");

        // Politique : crédit offline autorisé → la vente à crédit passe.
        (await policy.SetAsync(new SetOfflinePolicyRequest(null, true, 1_000_000m, null, null, null, null, null, null))).IsSuccess.Should().BeTrue();
        var allowed = await sync.PushBatchAsync(new PushBatchRequest(device,
            [SaleOp(Guid.NewGuid(), p, 5m, 100m, [], customer)]));
        allowed.Value!.Operations[0].Status.Should().Be("SYNCED");

        // Remise en état pour les autres tests.
        await policy.SetAsync(new SetOfflinePolicyRequest(null, false, 0m, null, null, null, null, null, null));
    }

    // ---- §35 : centre de synchronisation ----
    [Fact]
    public async Task Sync_center_summarizes_queue()
    {
        await using var s = Scope();
        var sync = s.ServiceProvider.GetRequiredService<ISyncService>();
        var device = await RegisterDevice(s.ServiceProvider, $"CTR-{Guid.NewGuid():N}"[..12]);
        var p = await Stocked(s.ServiceProvider, "CTR", 100m, 40m, 10m);
        await sync.PushBatchAsync(new PushBatchRequest(device, [SaleOp(Guid.NewGuid(), p, 2m, null, [Cash(200m)])]));

        var center = await sync.SyncCenterAsync();
        center.SyncedOperations.Should().BeGreaterThan(0);
        center.Devices.Should().Contain(d => d.Id == device);
        center.LastSyncAt.Should().NotBeNull();
        center.GeneratedAt.Should().BeCloseTo(DateTimeOffset.UtcNow, TimeSpan.FromMinutes(30));
    }

    // ---- §35 : résoudre un conflit → décision et auteur journalisés (§15) ----
    [Fact]
    public async Task Resolve_conflict_records_decision()
    {
        await using var s = Scope();
        var sync = s.ServiceProvider.GetRequiredService<ISyncService>();
        var device = await RegisterDevice(s.ServiceProvider, $"RES-{Guid.NewGuid():N}"[..12]);
        var p = await Stocked(s.ServiceProvider, "RES", 100m, 40m, 1m);

        var result = await sync.PushBatchAsync(new PushBatchRequest(device, [
            SaleOp(Guid.NewGuid(), p, 1m, null, [Cash(100m)]),
            SaleOp(Guid.NewGuid(), p, 1m, null, [Cash(100m)]),
        ]));
        var conflictId = result.Value!.Operations.First(o => o.Status == "CONFLICT").ConflictId!.Value;

        (await sync.ResolveConflictAsync(conflictId, new ResolveConflictRequest("server_wins", "Vente locale annulée"))).IsSuccess.Should().BeTrue();
        (await sync.ResolveConflictAsync(conflictId, new ResolveConflictRequest("server_wins", null))).IsSuccess.Should().BeFalse();

        var db = s.ServiceProvider.GetRequiredService<NexoraDbContext>();
        var row = await db.Database.SqlQuery<ResolvedRow>($"""
            SELECT resolution AS "Resolution", (resolved_by IS NOT NULL) AS "HasResolver", resolution_note AS "Note"
            FROM nexora.sync_conflict WHERE id = {conflictId}
            """).FirstAsync();
        row.Resolution.Should().Be("server_wins");
        row.HasResolver.Should().BeTrue();
        row.Note.Should().Be("Vente locale annulée");
    }

    private sealed record ResolvedRow(string Resolution, bool HasResolver, string? Note);

    // ---- §35 : une opération non persistée n'est jamais confirmée comme vente ----
    [Fact]
    public async Task Failed_operation_is_never_confirmed_as_a_sale()
    {
        await using var s = Scope();
        var sync = s.ServiceProvider.GetRequiredService<ISyncService>();
        var device = await RegisterDevice(s.ServiceProvider, $"FAIL-{Guid.NewGuid():N}"[..12]);

        var op = Guid.NewGuid();
        var bogus = Guid.NewGuid();   // produit inexistant
        var result = await sync.PushBatchAsync(new PushBatchRequest(device, [SaleOp(op, bogus, 1m, 100m, [Cash(100m)])]));

        result.Value!.Operations[0].Status.Should().Be("FAILED");
        result.Value.Confirmed.Should().Be(0);

        var db = s.ServiceProvider.GetRequiredService<NexoraDbContext>();
        (await db.Sales.AsNoTracking().AnyAsync(x => x.SourceOperationUuid == op)).Should().BeFalse();

        // Rejeu → toujours FAILED, jamais de vente.
        var replay = await sync.PushBatchAsync(new PushBatchRequest(device, [SaleOp(op, bogus, 1m, 100m, [Cash(100m)])]));
        replay.Value!.Operations[0].Status.Should().Be("FAILED");
        (await db.Sales.AsNoTracking().AnyAsync(x => x.SourceOperationUuid == op)).Should().BeFalse();
    }

    // ---- Audit 2026-10 éd. 2, A-003 résidu : vente rejouée dans une caisse déjà comptée ; appareil révoqué réactivé ----
    private static readonly Guid CashRegister = Guid.Parse("dce00000-0000-0000-0000-00000000a061");

    [Fact]
    public async Task An_offline_sale_into_a_closed_session_waits_for_a_manager_decision()
    {
        await using var s = Scope();
        var sync = s.ServiceProvider.GetRequiredService<ISyncService>();
        var sessions = s.ServiceProvider.GetRequiredService<CashSessionService>();
        var device = await RegisterDevice(s.ServiceProvider, $"CLS-{Guid.NewGuid():N}"[..12]);
        var p = await Stocked(s.ServiceProvider, "CLS", 100m, 50m, 10m);

        var session = (await sessions.OpenAsync(new OpenSessionCommand(CashRegister, null, 1_000m))).Value;
        var soldAt = DateTimeOffset.UtcNow;
        var closed = await sessions.CloseAsync(new CloseSessionCommand(session, 1_000m, null));
        closed.IsSuccess.Should().BeTrue(closed.Error?.Message);
        closed.Value!.Status.Should().Be("closed");

        var op = Guid.NewGuid();
        var cmd = new CheckoutCommand(op, Officine, null, null, session, null, "HTG",
            [new CheckoutLine(p, 1m, null, 0m, null)], [Cash(100m)], 100m, null, null, null, soldAt);
        var push = await sync.PushBatchAsync(new PushBatchRequest(device,
            [new SyncOperationInput(op, "sale", "create", JsonSerializer.SerializeToElement(cmd))]));

        push.IsSuccess.Should().BeTrue(push.Error?.Message);
        var result = push.Value!.Operations.Single();
        result.Status.Should().Be("CONFLICT", "l'ancien code ajoutait la vente en silence à la caisse déjà comptée");
        var db = s.ServiceProvider.GetRequiredService<NexoraDbContext>();
        (await db.Sales.AsNoTracking().CountAsync(x => x.SourceOperationUuid == op)).Should().Be(0);

        var accepted = await sync.ResolveConflictAsync(result.ConflictId!.Value,
            new ResolveConflictRequest("client_wins", "Vente vérifiée avec le ticket ; écart de clôture expliqué"));
        accepted.IsSuccess.Should().BeTrue(accepted.Error?.Message);
        (await db.Sales.AsNoTracking().CountAsync(x => x.SourceOperationUuid == op && x.CashSessionId == session))
            .Should().Be(1, "acceptée par un responsable, la vente rejoint sa session");
    }

    [Fact]
    public async Task Registering_a_revoked_device_again_does_not_reactivate_it()
    {
        await using var s = Scope();
        var registry = s.ServiceProvider.GetRequiredService<IDeviceRegistryService>();
        var identifier = $"RVK-{Guid.NewGuid():N}"[..12];
        var device = await RegisterDevice(s.ServiceProvider, identifier);
        (await registry.RevokeAsync(device, new RevokeDeviceRequest("Appareil perdu"))).IsSuccess.Should().BeTrue();

        var again = await registry.RegisterAsync(new RegisterDeviceRequest(identifier, identifier, "pos", null, null));
        again.Error!.Code.Should().Be("sync.device_revoked", "l'ancien code le remettait « actif »");
        (await registry.ListAsync()).Should().Contain(d => d.Id == device && d.Status == "revoked");
    }
}
