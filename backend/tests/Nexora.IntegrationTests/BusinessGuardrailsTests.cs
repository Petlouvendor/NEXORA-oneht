using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Nexora.Application.Catalog;
using Nexora.Application.Finance.Reversals;
using Nexora.Application.Purchasing;
using Nexora.Application.Sales;
using Nexora.Application.Sync;
using Nexora.Contracts.Sync;
using Nexora.Infrastructure.Persistence;
using Npgsql;

namespace Nexora.IntegrationTests;

/// <summary>
/// Audit 2026-10 — A-016 : le crédit hors ligne était estimé sur le seul prix forcé (0 dans le cas courant) ; A-006 :
/// l'extourne d'une écriture de vente ne corrigeait que le grand livre ; A-015 : un code de carton ajoutait 1 unité.
/// </summary>
[Collection("finance")]
public sealed class BusinessGuardrailsTests(FinanceEngineFixture fx)
{
    private static readonly Guid Company = Guid.Parse("dce00000-0000-0000-0000-0000000000c1");
    private static readonly Guid Officine = Guid.Parse("dce00000-0000-0000-0000-00000000a003");
    private static readonly Guid Supplier1 = Guid.Parse("dce00000-0000-0000-0000-00000000b101");
    private static readonly Guid BoxUnit = Guid.Parse("dce00000-0000-0000-0000-00000000a011");
    private static readonly Guid CashAccount = Guid.Parse("dce00000-0000-0000-0000-00000000a051");
    private static readonly Guid CashMethod = Guid.Parse("dce00000-0000-0000-0000-00000000a081");

    private AsyncServiceScope Scope() => fx.Provider.CreateAsyncScope();

    private async Task<Guid> Stocked(IServiceProvider sp, decimal price, decimal qty)
    {
        var catalog = sp.GetRequiredService<CatalogService>();
        var id = (await catalog.CreateProductAsync(new CreateProductCommand(
            $"GRD-{Guid.NewGuid():N}"[..20], "Article garde-fou", null, BoxUnit, "stockable", null, null, null, null, false, false))).Value;
        await catalog.SetPriceAsync(new SetProductPriceCommand(id, "sale", price, "HTG", 0m, null, null));
        await sp.GetRequiredService<PurchasingService>().ReceiveAsync(new ReceiveGoodsCommand(
            Guid.NewGuid(), Supplier1, Officine, null, [new ReceiveLine(id, null, qty, price / 2m, null, null)], null, null));
        return id;
    }

    [Fact]
    public async Task Offline_credit_is_estimated_with_server_prices_when_no_price_is_forced()
    {
        await using var s = Scope();
        var sync = s.ServiceProvider.GetRequiredService<ISyncService>();
        var policy = s.ServiceProvider.GetRequiredService<IOfflinePolicyService>();
        var device = (await s.ServiceProvider.GetRequiredService<IDeviceRegistryService>()
            .RegisterAsync(new RegisterDeviceRequest($"GRD-{Guid.NewGuid():N}"[..12], "Caisse", "pos", null, null))).Value;
        var product = await Stocked(s.ServiceProvider, 100m, 20m);
        var customer = Guid.NewGuid();
        await s.ServiceProvider.GetRequiredService<NexoraDbContext>().Database.ExecuteSqlAsync($"""
            INSERT INTO nexora.customer (id, company_id, customer_number, name, credit_limit, default_currency_id)
            VALUES ({customer}, {Company}, {"CLI-" + customer.ToString("N")[..8]}, 'Client hors ligne', 500000,
                    (SELECT id FROM nexora.currency WHERE code = 'HTG'))
            """);

        (await policy.SetAsync(new SetOfflinePolicyRequest(null, false, null, null, null, null, null, null, null))).IsSuccess.Should().BeTrue();
        try
        {
            // Aucun prix forcé : la caisse a calculé 5 × 100, rien n'est payé → 500 de crédit hors ligne, interdit.
            var op = Guid.NewGuid();
            var cmd = new CheckoutCommand(op, Officine, null, null, null, customer, "HTG",
                [new CheckoutLine(product, 5m, null, 0m, null)], [], 0m, null, null, null, null);
            var push = await sync.PushBatchAsync(new PushBatchRequest(device,
                [new SyncOperationInput(op, "sale", "create", JsonSerializer.SerializeToElement(cmd))]));
            push.Value!.Operations[0].Status.Should().Be("CONFLICT", "l'ancien calcul estimait 0 et laissait passer le crédit");
        }
        finally
        {
            await policy.SetAsync(new SetOfflinePolicyRequest(null, false, 0m, null, null, null, null, null, null));
        }
    }

    [Fact]
    public async Task A_sale_in_gourdes_cannot_be_tendered_to_a_dollar_account()
    {
        // Compte Unibank de la Pharmacie : tenu en USD. Avant, 1 000 HTG y devenaient 1 000 USD (écriture en HTG).
        var usdBank = Guid.Parse("dce00000-0000-0000-0000-00000000a052");
        await using var s = Scope();
        var db = s.ServiceProvider.GetRequiredService<NexoraDbContext>();
        var product = await Stocked(s.ServiceProvider, 40m, 5m);
        var before = await db.FinancialAccounts.AsNoTracking().Where(a => a.Id == usdBank).Select(a => a.CurrentBalance).SingleAsync();

        var sale = await s.ServiceProvider.GetRequiredService<SalesService>().CheckoutAsync(new CheckoutCommand(
            Guid.NewGuid(), Officine, null, null, null, null, "HTG", [new CheckoutLine(product, 1m, null, 0m, null)],
            [new TenderInput(usdBank, null, 40m, "CB")], 40m, null, null, null, null));

        sale.IsSuccess.Should().BeFalse("la caisse le refusait déjà");

        // Le moteur lui-même, par un chemin sans contrôle en amont (frais bancaires) : avant, 40 HTG devenaient 40 USD.
        var charge = await s.ServiceProvider.GetRequiredService<Nexora.Application.Finance.IFinanceOperations>().RecordBankChargeAsync(
            new Nexora.Contracts.Finance.RecordBankChargeRequest(Guid.NewGuid(), DateTimeOffset.UtcNow, usdBank, 40m, "HTG", "Frais"));
        charge.IsSuccess.Should().BeFalse("l'ancien moteur retirait 40 du solde en dollars");
        charge.Error!.Message.Should().Contain("n'est pas tenu en HTG");
        (await db.FinancialAccounts.AsNoTracking().Where(a => a.Id == usdBank).Select(a => a.CurrentBalance).SingleAsync())
            .Should().Be(before);
    }

    [Fact]
    public async Task The_accounting_screen_cannot_reverse_an_entry_born_from_a_sale()
    {
        await using var s = Scope();
        var product = await Stocked(s.ServiceProvider, 50m, 5m);
        var sale = await s.ServiceProvider.GetRequiredService<SalesService>().CheckoutAsync(new CheckoutCommand(
            Guid.NewGuid(), Officine, null, null, null, null, "HTG", [new CheckoutLine(product, 1m, null, 0m, null)],
            [new TenderInput(CashAccount, CashMethod, 50m, null)], 50m, null, null, null, null));
        sale.IsSuccess.Should().BeTrue(sale.Error?.Message);
        var entry = await s.ServiceProvider.GetRequiredService<NexoraDbContext>().JournalEntries.AsNoTracking()
            .Where(e => e.SourceId == sale.Value!.SaleId).Select(e => e.Id).FirstAsync();

        (await s.ServiceProvider.GetRequiredService<IReversalService>().ReverseUserEntryAsync(entry, "Erreur de caisse"))
            .Error!.Code.Should().Be("accounting.reverse_business_entry");
    }

    [Fact]
    public async Task Scanning_a_case_barcode_adds_its_whole_content()
    {
        await using var s = Scope();
        var product = await Stocked(s.ServiceProvider, 20m, 50m);
        var code = $"99{Random.Shared.NextInt64(10_000_000_000, 99_999_999_999)}";
        await using (var c = new NpgsqlConnection(fx.TestConnectionString))
        {
            await c.OpenAsync();
            await using var cmd = new NpgsqlCommand($"""
                INSERT INTO nexora.product_barcode (product_id, company_id, barcode, pack_size, barcode_type)
                VALUES ('{product}', '{Company}', '{code}', 12, 'ean13')
                """, c);
            await cmd.ExecuteNonQueryAsync();
        }

        var hit = await s.ServiceProvider.GetRequiredService<IPosCatalogQueries>().ScanAsync(code, Officine);
        hit.Should().NotBeNull();
        hit!.PackSize.Should().Be(12m, "un code de carton de 12 ajoute 12 unités");
    }

    [Fact]
    public async Task Scanning_a_scale_label_adds_the_weight_it_carries()
    {
        await using var s = Scope();
        var product = await Stocked(s.ServiceProvider, 300m, 40m);
        // Préfixe d'article unique : « 2 » + 6 chiffres ; l'étiquette porte 1 250 g et sa clé de contrôle.
        var prefix = $"2{Random.Shared.Next(100_000, 999_999)}";
        var body = prefix + "01250";
        var sum = body.Select((ch, i) => (ch - '0') * (i % 2 == 0 ? 1 : 3)).Sum();
        var label = body + (10 - sum % 10) % 10;
        await using (var c = new NpgsqlConnection(fx.TestConnectionString))
        {
            await c.OpenAsync();
            await using var cmd = new NpgsqlCommand($"""
                INSERT INTO nexora.product_barcode (product_id, company_id, barcode, is_weight_embedded, barcode_type)
                VALUES ('{product}', '{Company}', '{prefix}', true, 'ean13')
                """, c);
            await cmd.ExecuteNonQueryAsync();
        }

        var hit = await s.ServiceProvider.GetRequiredService<IPosCatalogQueries>().ScanAsync(label, Officine);
        hit.Should().NotBeNull("l'ancienne lecture ne décodait pas les étiquettes de balance");
        hit!.ProductId.Should().Be(product);
        hit.PackSize.Should().Be(1.25m);
        hit.Barcode.Should().Be(label);
    }
}
