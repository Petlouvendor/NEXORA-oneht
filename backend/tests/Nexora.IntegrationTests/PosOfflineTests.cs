using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Nexora.Application.Abstractions;
using Nexora.Application.Catalog;
using Nexora.Application.Purchasing;
using Nexora.Application.Sales;
using Nexora.Application.Sync;
using Nexora.Contracts.Catalog;
using Nexora.Contracts.Sales;
using Nexora.Contracts.Sync;
using Nexora.Infrastructure.Persistence;

namespace Nexora.IntegrationTests;

/// <summary>
/// Finalisation produit, phase 6 — caisse hors ligne : l'appareil s'enregistre seul (révocable), le profil hors ligne
/// contient le catalogue vendable sans coût, la politique hors ligne est appliquée à la synchronisation (moyens
/// permis, remises, durée hors ligne) et « le poste a raison » enregistre réellement la vente.
/// </summary>
[Collection("finance")]
public sealed class PosOfflineTests(FinanceEngineFixture fx)
{
    private static readonly Guid Company = Guid.Parse("dce00000-0000-0000-0000-0000000000c1");
    private static readonly Guid Direction = Guid.Parse("dce00000-0000-0000-0000-0000000000d1");
    private static readonly Guid Officine = Guid.Parse("dce00000-0000-0000-0000-00000000a003");
    private static readonly Guid Supplier1 = Guid.Parse("dce00000-0000-0000-0000-00000000b101");
    private static readonly Guid BoxUnit = Guid.Parse("dce00000-0000-0000-0000-00000000a011");
    private static readonly Guid CashAccount = Guid.Parse("dce00000-0000-0000-0000-00000000a051");
    private static readonly Guid UsdBank = Guid.Parse("dce00000-0000-0000-0000-00000000a052");
    private static readonly Guid CashMethod = Guid.Parse("dce00000-0000-0000-0000-00000000a081");
    private static readonly Guid CardMethod = Guid.Parse("dce00000-0000-0000-0000-00000000a082");

    private AsyncServiceScope Scope()
    {
        var s = fx.Provider.CreateAsyncScope();
        var tc = (TestTenantContext)s.ServiceProvider.GetRequiredService<ITenantContext>();
        tc.SetUser(Direction);
        tc.SetScope([Company], [], Company);
        return s;
    }

    private static string U() => Guid.NewGuid().ToString("N")[..10].ToUpperInvariant();

    private static async Task<Guid> Stocked(IServiceProvider sp, decimal price, string? barcode = null)
    {
        var catalog = sp.GetRequiredService<CatalogService>();
        var id = (await catalog.CreateProductAsync(new CreateProductCommand(
            $"OFF-{U()}", $"Article hors ligne {U()}", null, BoxUnit, "stockable", barcode, null, null, null, false, false))).Value;
        await catalog.SetPriceAsync(new SetProductPriceCommand(id, "sale", price, "HTG", 0m, null, null));
        (await sp.GetRequiredService<PurchasingService>().ReceiveAsync(new ReceiveGoodsCommand(
            Guid.NewGuid(), Supplier1, Officine, null, [new ReceiveLine(id, null, 30m, price / 2m, null, null)], null, null)))
            .IsSuccess.Should().BeTrue();
        return id;
    }

    private static SyncOperationInput SaleOp(Guid product, decimal qty, IReadOnlyList<TenderInput> tenders, decimal discount = 0m,
        DateTimeOffset? createdAt = null)
    {
        var op = Guid.NewGuid();
        var cmd = new CheckoutCommand(op, Officine, null, null, null, null, "HTG",
            [new CheckoutLine(product, qty, null, discount, null)], tenders, tenders.Sum(t => t.Amount), null, null, null, null);
        return new SyncOperationInput(op, "sale", "create", JsonSerializer.SerializeToElement(cmd), ClientCreatedAt: createdAt);
    }

    [Fact]
    public async Task Pos_device_enrolls_itself_idempotently_and_a_revoked_device_is_refused()
    {
        await using var s = Scope();
        var offline = s.ServiceProvider.GetRequiredService<IPosOfflineService>();
        var identifier = $"browser-{Guid.NewGuid():N}";

        (await offline.EnrollAsync(new EnrollPosDeviceRequest("x y", null))).Error!.Code.Should().Be("pos.device_identifier");
        var first = await offline.EnrollAsync(new EnrollPosDeviceRequest(identifier, "Caisse 1 — test"));
        first.IsSuccess.Should().BeTrue(first.Error?.Message);
        var again = await offline.EnrollAsync(new EnrollPosDeviceRequest(identifier, "autre nom"));
        again.Value!.Id.Should().Be(first.Value!.Id, "même navigateur, même appareil");

        (await s.ServiceProvider.GetRequiredService<IDeviceRegistryService>().RevokeAsync(first.Value.Id, new RevokeDeviceRequest("Poste volé")))
            .IsSuccess.Should().BeTrue();
        (await offline.EnrollAsync(new EnrollPosDeviceRequest(identifier, null))).Error!.Code.Should().Be("sync.device_revoked");
    }

    [Fact]
    public async Task Offline_profile_contains_sellable_products_with_price_and_codes_but_never_costs()
    {
        await using var s = Scope();
        var barcode = $"20{Random.Shared.NextInt64(10_000_000_000, 99_999_999_999)}";
        var p = await Stocked(s.ServiceProvider, 125m, barcode);
        var profile = await s.ServiceProvider.GetRequiredService<IPosOfflineService>().ProfileAsync(Officine);

        var item = profile.Catalog.Single(i => i.ProductId == p);
        item.SalePrice.Should().Be(125m);
        item.Codes.Should().Contain(barcode);
        item.Availability.Should().Be("in_stock");
        typeof(PosOfflineCatalogItemDto).GetProperties().Select(x => x.Name).Should().NotContain(n => n.Contains("Cost"));
        profile.Policy.MaxOfflineMinutes.Should().BeGreaterThanOrEqualTo(0);
    }

    [Fact]
    public async Task Sync_applies_the_offline_policy_and_accepting_a_conflict_records_the_sale()
    {
        await using var s = Scope();
        var sp = s.ServiceProvider;
        var sync = sp.GetRequiredService<ISyncService>();
        var policies = sp.GetRequiredService<IOfflinePolicyService>();
        var db = sp.GetRequiredService<NexoraDbContext>();
        var before = await policies.GetAsync();
        try
        {
            (await policies.SetAsync(new SetOfflinePolicyRequest(true, null, null, false, null, 60, null, ["cash"], null))).IsSuccess.Should().BeTrue();
            var device = (await sp.GetRequiredService<IPosOfflineService>().EnrollAsync(new EnrollPosDeviceRequest($"pol-{Guid.NewGuid():N}", null))).Value!.Id;
            var p = await Stocked(sp, 100m);

            // Compte bancaire HTG pour la carte (la banque de démo est en USD).
            var bank = Guid.NewGuid();
            await db.Database.ExecuteSqlAsync($"""
                INSERT INTO nexora.financial_account (id, company_id, account_type, name, currency_id, ledger_account_id)
                SELECT {bank}, company_id, 'bank', {"Banque HTG " + U()}, (SELECT id FROM nexora.currency WHERE code = 'HTG'), ledger_account_id
                FROM nexora.financial_account WHERE id = {UsdBank}
                """);

            var card = SaleOp(p, 2m, [new TenderInput(bank, CardMethod, 200m, "AUTH-OFF")]);
            var discount = SaleOp(p, 1m, [new TenderInput(CashAccount, CashMethod, 90m, null)], discount: 10m);
            var ok = SaleOp(p, 1m, [new TenderInput(CashAccount, CashMethod, 100m, null)]);
            var r = (await sync.PushBatchAsync(new PushBatchRequest(device, [card, discount, ok]))).Value!;
            r.Operations.Single(o => o.OperationUuid == card.OperationUuid).Status.Should().Be("CONFLICT", "la carte n'est pas un moyen autorisé hors ligne");
            r.Operations.Single(o => o.OperationUuid == discount.OperationUuid).Status.Should().Be("CONFLICT", "remise interdite hors ligne");
            r.Operations.Single(o => o.OperationUuid == ok.OperationUuid).Status.Should().Be("SYNCED");

            // Vente faite 3 h après le dernier contact de l'appareil, pour 60 min autorisées.
            await db.Database.ExecuteSqlAsync($"UPDATE nexora.sync_device SET last_seen_at = now() - interval '4 hours' WHERE id = {device}");
            var late = SaleOp(p, 1m, [new TenderInput(CashAccount, CashMethod, 100m, null)], createdAt: DateTimeOffset.UtcNow.AddHours(-1));
            var lateResult = (await sync.PushBatchAsync(new PushBatchRequest(device, [late]))).Value!;
            lateResult.Operations.Single().Status.Should().Be("CONFLICT");
            lateResult.Operations.Single().Message.Should().Contain("dernier contact");

            // Le responsable accepte la vente carte : elle est réellement enregistrée.
            var conflictId = r.Operations.Single(o => o.OperationUuid == card.OperationUuid).ConflictId!.Value;
            var resolved = await sync.ResolveConflictAsync(conflictId, new ResolveConflictRequest("client_wins", "Vente vérifiée avec le ticket TPE"));
            resolved.IsSuccess.Should().BeTrue(resolved.Error?.Message);
            (await db.Sales.AsNoTracking().AnyAsync(x => x.SourceOperationUuid == card.OperationUuid)).Should().BeTrue();
            (await db.Database.SqlQuery<string>($"SELECT status AS \"Value\" FROM nexora.sync_operation WHERE operation_uuid = {card.OperationUuid}").FirstAsync())
                .Should().Be("SYNCED");
        }
        finally
        {
            await policies.SetAsync(new SetOfflinePolicyRequest(before.AllowOfflineSales, before.AllowOfflineCredit, before.OfflineCreditLimit,
                before.AllowOfflineDiscount, before.AllowOfflineRefund, before.MaxOfflineMinutes, before.PendingAlertThreshold,
                before.AllowedPaymentMethods, before.AutoSync, before.AllowOfflineCustomerPayment));
        }
    }
    [Fact]
    public async Task Cart_preview_matches_the_recorded_sale_with_tax_and_promotion()
    {
        // Défaut phase 6 : la caisse affichait le prix hors taxe et sans promotion ; le serveur encaissait autre chose.
        await using var s = Scope();
        var sp = s.ServiceProvider;
        var p = await Stocked(sp, 200m);
        var tax = Guid.NewGuid();
        var db = sp.GetRequiredService<NexoraDbContext>();
        await db.Database.ExecuteSqlAsync($"""
            INSERT INTO nexora.tax (id, company_id, name, code, rate, type) VALUES ({tax}, {Company}, 'TVA test', {"TVT-" + U()}, 10, 'vat')
            """);
        await db.Database.ExecuteSqlAsync($"UPDATE nexora.product SET default_tax_id = {tax} WHERE id = {p}");
        (await sp.GetRequiredService<IPromotionService>().CreateAsync(new SavePromotionRequest($"Promo {U()}", "percentage", 10m, 5, null, null, null,
            [new PromotionTargetDto("product", p, null, null)]))).IsSuccess.Should().BeTrue();

        var preview = (await sp.GetRequiredService<SalesService>().PreviewAsync(new CartPreviewRequest(Officine, null, "HTG",
            [new CartPreviewLineInput(p, 2m, 0m)]))).Value!;
        preview.Total.Should().Be(396m, "400 HT − 10 % = 360, + 10 % de taxe = 396");
        preview.Lines.Single().PromotionDiscount.Should().Be(40m);
        typeof(CartPreviewLineDto).GetProperties().Select(x => x.Name).Should().NotContain(n => n.Contains("Cost"));

        var sale = await sp.GetRequiredService<SalesService>().CheckoutAsync(new CheckoutCommand(Guid.NewGuid(), Officine, null, null, null, null, "HTG",
            [new CheckoutLine(p, 2m, null, 0m, null)], [new TenderInput(CashAccount, CashMethod, preview.Total, null)], preview.Total, null, null, null, null));
        sale.IsSuccess.Should().BeTrue(sale.Error?.Message);
        sale.Value!.Total.Should().Be(preview.Total);
        sale.Value.ChangeGiven.Should().Be(0m);
    }
}
