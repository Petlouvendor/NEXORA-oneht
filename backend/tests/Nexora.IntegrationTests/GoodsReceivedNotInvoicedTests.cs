using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Nexora.Application.Catalog;
using Nexora.Application.Inventory;
using Nexora.Application.Purchasing;
using Npgsql;

namespace Nexora.IntegrationTests;

/// <summary>
/// Compte 4081 « Fournisseurs — factures non parvenues » (décision du 2026-10-09). Une réception augmentait le stock
/// sans écriture : le 3110 restait en retard sur la valeur du stock tant que la facture n'était pas saisie.
/// Réception D 3110 / C 4081 ; facture D 4081 + écart de prix / C 4011 ; retour avant facture D 4081 / C 3110.
/// </summary>
[Collection("finance")]
public sealed class GoodsReceivedNotInvoicedTests(FinanceEngineFixture fx)
{
    private static readonly Guid Officine = Guid.Parse("dce00000-0000-0000-0000-00000000a003");
    private static readonly Guid Supplier1 = Guid.Parse("dce00000-0000-0000-0000-00000000b101");
    private static readonly Guid BoxUnit = Guid.Parse("dce00000-0000-0000-0000-00000000a011");

    private AsyncServiceScope Scope() => fx.Provider.CreateAsyncScope();

    private async Task<Guid> Product(string suffix)
    {
        await using var s = Scope();
        var r = await s.ServiceProvider.GetRequiredService<CatalogService>().CreateProductAsync(new CreateProductCommand(
            $"GRNI-{suffix}-{Guid.NewGuid():N}"[..20], $"Produit 4081 {suffix}", null, BoxUnit, "stockable",
            null, null, null, null, false, false));
        r.IsSuccess.Should().BeTrue(r.Error?.Message);
        return r.Value;
    }

    /// <summary>Solde (débit − crédit, devise de l'entreprise) d'un compte sur les écritures d'une pièce.</summary>
    private async Task<decimal> Balance(string accountCode, string sourceType, Guid sourceId)
    {
        await using var c = new NpgsqlConnection(fx.TestConnectionString);
        await c.OpenAsync();
        await using var cmd = new NpgsqlCommand($"""
            SELECT COALESCE(SUM(l.base_debit - l.base_credit), 0)
            FROM nexora.journal_entry_line l
            JOIN nexora.journal_entry e ON e.id = l.journal_entry_id
            JOIN nexora.account a ON a.id = l.account_id
            WHERE a.code = '{accountCode}' AND e.source_type = '{sourceType}' AND e.source_id = '{sourceId}'
            """, c);
        return (decimal)(await cmd.ExecuteScalarAsync())!;
    }

    private static async Task<Guid> Receive(PurchasingService p, Guid product, decimal qty, decimal cost)
    {
        var r = await p.ReceiveAsync(new ReceiveGoodsCommand(
            Guid.NewGuid(), Supplier1, Officine, null, [new ReceiveLine(product, null, qty, cost, null, null)], null, null));
        r.IsSuccess.Should().BeTrue(r.Error?.Message);
        return r.Value;
    }

    private static async Task<RecordPurchaseInvoiceResult> Invoice(PurchasingService p, Guid receipt, Guid product, decimal qty, decimal price)
    {
        var r = await p.RecordInvoiceAsync(new RecordPurchaseInvoiceCommand(
            Guid.NewGuid(), Supplier1, null, "HTG", TestDates.Today, null, OnCredit: true, TenderFinancialAccountId: null,
            [new InvoiceLine(product, "Marchandise", qty, price, null, 0m, 0m)], CapitaliseToInventory: true,
            GoodsReceiptId: receipt));
        r.IsSuccess.Should().BeTrue(r.Error?.Message);
        return r.Value!;
    }

    [Fact]
    public async Task A_receipt_brings_the_stock_into_the_ledger_against_4081()
    {
        var product = await Product("R");
        await using var s = Scope();
        var receipt = await Receive(s.ServiceProvider.GetRequiredService<PurchasingService>(), product, 30m, 500m);

        (await Balance("3110", "goods_receipt", receipt)).Should().Be(15_000m, "le stock reçu entre au 3110 au coût de réception");
        (await Balance("4081", "goods_receipt", receipt)).Should().Be(-15_000m, "en contrepartie des factures non parvenues");
        var stockValue = 30m * await s.ServiceProvider.GetRequiredService<IStockLedger>().AverageCostAsync(product, Officine);
        stockValue.Should().Be(15_000m);
    }

    [Fact]
    public async Task The_invoice_at_the_receipt_cost_settles_4081_and_creates_the_payable()
    {
        var product = await Product("I");
        await using var s = Scope();
        var p = s.ServiceProvider.GetRequiredService<PurchasingService>();
        var receipt = await Receive(p, product, 30m, 500m);
        var invoice = await Invoice(p, receipt, product, 30m, 500m);

        (await Balance("4081", "supplier_invoice", invoice.InvoiceId)).Should().Be(15_000m);
        (await Balance("4011", "supplier_invoice", invoice.InvoiceId)).Should().Be(-15_000m);
        (await Balance("3110", "supplier_invoice", invoice.InvoiceId)).Should().Be(0m, "le stock est déjà entré à la réception");
        ((await Balance("4081", "goods_receipt", receipt)) + (await Balance("4081", "supplier_invoice", invoice.InvoiceId)))
            .Should().Be(0m, "réception facturée : plus rien en factures non parvenues");
    }

    [Fact]
    public async Task A_price_difference_goes_to_the_purchase_price_variance()
    {
        var product = await Product("V");
        await using var s = Scope();
        var p = s.ServiceProvider.GetRequiredService<PurchasingService>();
        var receipt = await Receive(p, product, 10m, 100m);
        var invoice = await Invoice(p, receipt, product, 10m, 104m);

        (await Balance("4081", "supplier_invoice", invoice.InvoiceId)).Should().Be(1_000m, "le 4081 est soldé au coût de réception");
        (await Balance("6800", "supplier_invoice", invoice.InvoiceId)).Should().Be(40m, "l'écart de prix va en charge");
        (await Balance("4011", "supplier_invoice", invoice.InvoiceId)).Should().Be(-1_040m, "la dette suit la facture");
    }

    [Fact]
    public async Task A_return_before_the_invoice_cancels_the_receipt_in_4081()
    {
        var product = await Product("T");
        await using var s = Scope();
        var p = s.ServiceProvider.GetRequiredService<PurchasingService>();
        var receipt = await Receive(p, product, 20m, 50m);

        var ret = await p.RecordReturnAsync(new RecordSupplierReturnCommand(
            Guid.NewGuid(), Supplier1, Officine, receipt, null, "HTG", "Abîmé à la livraison",
            [new ReturnLine(product, 4m, 50m, 0m, null)]));
        ret.IsSuccess.Should().BeTrue(ret.Error?.Message);

        (await Balance("4081", "supplier_return", ret.Value)).Should().Be(200m);
        (await Balance("3110", "supplier_return", ret.Value)).Should().Be(-200m);
        (await Balance("4081", "goods_receipt", receipt) + await Balance("4081", "supplier_return", ret.Value))
            .Should().Be(-800m, "restent 16 unités reçues à facturer");
    }
}
