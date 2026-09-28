using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Nexora.Application.Catalog;
using Nexora.Application.Inventory;
using Nexora.Application.Parties;
using Nexora.Application.Purchasing;
using Nexora.Contracts.Catalog;
using Nexora.Contracts.Parties;
using Nexora.Contracts.Purchasing;
using Nexora.Infrastructure.Persistence;

namespace Nexora.IntegrationTests;

/// <summary>
/// Finalisation produit, phase 3 — cycle d'achat : brouillon modifiable, taxe calculée par le serveur, commande et
/// réception en unité d'achat (carton → unité de base), contrôle à réception (quantité refusée + motif), facture
/// rejouée sans doublon, échéance issue des conditions fournisseur, retour avec motif, lectures de détail.
/// </summary>
[Collection("finance")]
public sealed class PurchasingFlowTests(FinanceEngineFixture fx)
{
    private static readonly Guid Company = Guid.Parse("dce00000-0000-0000-0000-0000000000c1");
    private static readonly Guid Officine = Guid.Parse("dce00000-0000-0000-0000-00000000a003");
    private static readonly Guid Supplier1 = Guid.Parse("dce00000-0000-0000-0000-00000000b101");
    private static readonly Guid BoxUnit = Guid.Parse("dce00000-0000-0000-0000-00000000a011");

    private AsyncServiceScope Scope() => fx.Provider.CreateAsyncScope();
    private static string U() => Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();

    private static async Task<Guid> NewProduct(IServiceProvider sp, Guid? unit = null)
    {
        var r = await sp.GetRequiredService<CatalogService>().CreateProductAsync(new CreateProductCommand(
            $"PF-{U()}", "Article achat", null, unit ?? BoxUnit, "stockable", null, null, null, null, false, false));
        r.IsSuccess.Should().BeTrue(r.Error?.Message);
        return r.Value;
    }

    private static async Task<Guid> NewTax(IServiceProvider sp, decimal rate)
    {
        var id = Guid.NewGuid();
        await sp.GetRequiredService<NexoraDbContext>().Database.ExecuteSqlAsync($"""
            INSERT INTO nexora.tax (id, company_id, name, code, rate, type)
            VALUES ({id}, {Company}, 'TCA achat', {"TA-" + id.ToString("N")}, {rate}, 'vat')
            """);
        return id;
    }

    [Fact]
    public async Task Draft_order_is_editable_then_locked_once_approved_and_tax_is_computed_by_the_server()
    {
        await using var s = Scope();
        var po = s.ServiceProvider.GetRequiredService<PurchasingService>();
        var docs = s.ServiceProvider.GetRequiredService<IPurchasingDocumentQueries>();
        var p = await NewProduct(s.ServiceProvider);
        var tax = await NewTax(s.ServiceProvider, 10m);

        var draft = await po.CreateOrderAsync(new CreatePurchaseOrderCommand(
            Supplier1, null, "HTG", null, [new CreatePurchaseOrderLine(p, 10m, 100m, tax, 0m)], "brouillon", Submit: false));
        draft.IsSuccess.Should().BeTrue(draft.Error?.Message);
        var d = (await docs.OrderAsync(draft.Value))!;
        d.Status.Should().Be("draft");
        d.Subtotal.Should().Be(1000m);
        d.TaxTotal.Should().Be(100m, "la taxe choisie n'est plus ignorée");
        d.Total.Should().Be(1100m);

        // Modification du brouillon : quantité et remise changent, les totaux sont recalculés.
        (await po.UpdateOrderAsync(draft.Value, new CreatePurchaseOrderCommand(
            Supplier1, null, "HTG", null, [new CreatePurchaseOrderLine(p, 20m, 100m, tax, 200m)], null)))
            .IsSuccess.Should().BeTrue();
        d = (await docs.OrderAsync(draft.Value))!;
        d.Status.Should().Be("submitted");
        d.Lines.Single().QuantityOrdered.Should().Be(20m);
        d.Subtotal.Should().Be(1800m);
        d.TaxTotal.Should().Be(180m);

        (await po.ApproveOrderAsync(draft.Value)).IsSuccess.Should().BeTrue();
        (await po.UpdateOrderAsync(draft.Value, new CreatePurchaseOrderCommand(
            Supplier1, null, "HTG", null, [new CreatePurchaseOrderLine(p, 1m, 1m, null, 0m)], null)))
            .Error!.Code.Should().Be("purchasing.order_locked");
    }

    [Fact]
    public async Task Order_validation_refuses_bad_lines_and_unknown_supplier()
    {
        await using var s = Scope();
        var po = s.ServiceProvider.GetRequiredService<PurchasingService>();
        var p = await NewProduct(s.ServiceProvider);

        (await po.CreateOrderAsync(new CreatePurchaseOrderCommand(Guid.NewGuid(), null, "HTG", null,
            [new CreatePurchaseOrderLine(p, 1m, 1m, null, 0m)], null))).Error!.Code.Should().Be("purchasing.supplier_unknown");
        (await po.CreateOrderAsync(new CreatePurchaseOrderCommand(Supplier1, null, "HTG", null,
            [new CreatePurchaseOrderLine(p, 0m, 1m, null, 0m)], null))).Error!.Code.Should().Be("purchasing.quantity");
        (await po.CreateOrderAsync(new CreatePurchaseOrderCommand(Supplier1, null, "HTG", null,
            [new CreatePurchaseOrderLine(p, 2m, -1m, null, 0m)], null))).Error!.Code.Should().Be("purchasing.unit_price");
        (await po.CreateOrderAsync(new CreatePurchaseOrderCommand(Supplier1, null, "HTG", null,
            [new CreatePurchaseOrderLine(p, 2m, 10m, null, 50m)], null))).Error!.Code.Should().Be("purchasing.discount");
        (await po.CreateOrderAsync(new CreatePurchaseOrderCommand(Supplier1, null, "HTG", null,
            [new CreatePurchaseOrderLine(Guid.NewGuid(), 2m, 10m, null, 0m)], null))).Error!.Code.Should().Be("purchasing.product_unknown");
    }

    [Fact]
    public async Task Carton_order_and_receipt_are_converted_to_the_base_unit_and_rejected_goods_never_enter_stock()
    {
        await using var s = Scope();
        var sp = s.ServiceProvider;
        var po = sp.GetRequiredService<PurchasingService>();
        var docs = sp.GetRequiredService<IPurchasingDocumentQueries>();
        var refs = sp.GetRequiredService<ICatalogReferenceService>();
        var bottle = (await refs.CreateUnitAsync(new CreateUnitRequest($"BT{U()}", "Bouteille", 0, false))).Value;
        var carton = (await refs.CreateUnitAsync(new CreateUnitRequest($"CT{U()}", "Carton", 0, false))).Value;
        var p = await NewProduct(sp, bottle);
        (await refs.CreateConversionAsync(new CreateUnitConversionRequest(p, carton, bottle, 24m))).IsSuccess.Should().BeTrue();

        // 10 cartons à 2 400 = 240 bouteilles à 100.
        var order = await po.CreateOrderAsync(new CreatePurchaseOrderCommand(
            Supplier1, null, "HTG", null, [new CreatePurchaseOrderLine(p, 10m, 2400m, null, 0m, carton)], null));
        order.IsSuccess.Should().BeTrue(order.Error?.Message);
        var line = (await docs.OrderAsync(order.Value))!.Lines.Single();
        line.QuantityOrdered.Should().Be(240m);
        line.UnitPrice.Should().Be(100m);
        line.PurchaseQuantity.Should().Be(10m);
        line.LineTotal.Should().Be(24000m);
        (await po.ApproveOrderAsync(order.Value)).IsSuccess.Should().BeTrue();

        // Un refus sans motif est rejeté.
        (await po.ReceiveAsync(new ReceiveGoodsCommand(Guid.NewGuid(), Supplier1, Officine, order.Value,
            [new ReceiveLine(p, line.Id, 8m, 2400m, null, null, carton, 1m)], null, null)))
            .Error!.Code.Should().Be("purchasing.rejection_reason");

        // 8 cartons acceptés, 1 carton refusé (casse) : 192 bouteilles en stock.
        var before = await sp.GetRequiredService<IStockLedger>().OnHandAsync(p, Officine);
        var rec = await po.ReceiveAsync(new ReceiveGoodsCommand(Guid.NewGuid(), Supplier1, Officine, order.Value,
            [new ReceiveLine(p, line.Id, 8m, 2400m, null, null, carton, 1m, "Carton écrasé")], null, null));
        rec.IsSuccess.Should().BeTrue(rec.Error?.Message);
        (await sp.GetRequiredService<IStockLedger>().OnHandAsync(p, Officine)).Should().Be(before + 192m);

        var receipt = (await docs.ReceiptAsync(rec.Value))!;
        receipt.Receipt.QuantityAccepted.Should().Be(192m);
        receipt.Receipt.QuantityRejected.Should().Be(24m);
        receipt.Lines.Single().RejectionReason.Should().Be("Carton écrasé");
        receipt.Lines.Single().UnitCost.Should().Be(100m);

        // Reste à recevoir 48 bouteilles : en recevoir 3 cartons (72) dépasse la commande.
        (await po.ReceiveAsync(new ReceiveGoodsCommand(Guid.NewGuid(), Supplier1, Officine, order.Value,
            [new ReceiveLine(p, line.Id, 3m, 2400m, null, null, carton)], null, null)))
            .Error!.Code.Should().Be("purchasing.over_receipt");

        // Solde de la commande partiellement reçue.
        (await po.CloseOrderAsync(order.Value)).IsSuccess.Should().BeTrue();
        (await docs.OrderAsync(order.Value))!.Receipts.Should().ContainSingle();
    }

    [Fact]
    public async Task Replayed_invoice_returns_the_first_one_and_due_date_follows_supplier_terms()
    {
        await using var s = Scope();
        var sp = s.ServiceProvider;
        var po = sp.GetRequiredService<PurchasingService>();
        var docs = sp.GetRequiredService<IPurchasingDocumentQueries>();
        var terms = sp.GetRequiredService<ISupplierTermsService>();
        var p = await NewProduct(sp);

        var supplier = (await sp.GetRequiredService<ISupplierService>().CreateAsync(new CreateSupplierRequest($"Fournisseur {U()}"))).Value!;
        var net30 = await terms.CreatePaymentTermsAsync(new SavePaymentTermsRequest($"30 jours {U()}", 30, 2m, 10, 0m, null));
        net30.IsSuccess.Should().BeTrue(net30.Error?.Message);
        (await terms.SetSupplierTermsAsync(supplier.Id, new SupplierTermsRequest(-1m, null, null, null)))
            .Error!.Code.Should().Be("purchasing.credit_limit");
        (await terms.SetSupplierTermsAsync(supplier.Id, new SupplierTermsRequest(500000m, net30.Value, "Mme Joseph", "Livre le mardi")))
            .IsSuccess.Should().BeTrue();

        var op = Guid.NewGuid();
        var cmd = new RecordPurchaseInvoiceCommand(op, supplier.Id, null, "HTG", new DateOnly(2026, 5, 1), null,
            OnCredit: true, null, [new InvoiceLine(p, "Achat", 4m, 250m, null, 0m, 0m)], true, SupplierReference: $"F-{U()}");
        var first = await po.RecordInvoiceAsync(cmd);
        first.IsSuccess.Should().BeTrue(first.Error?.Message);
        var again = await po.RecordInvoiceAsync(cmd);
        again.IsSuccess.Should().BeTrue();
        again.Value!.WasReplay.Should().BeTrue();
        again.Value.InvoiceId.Should().Be(first.Value!.InvoiceId);

        var invoices = await docs.InvoicesAsync(supplier.Id, null, 50);
        invoices.Should().ContainSingle("un rejeu ne crée pas de seconde facture");
        invoices.Single().DueDate.Should().Be(new DateOnly(2026, 5, 31));
        invoices.Single().Total.Should().Be(1000m);

        var overview = (await docs.SupplierOverviewAsync(supplier.Id))!;
        overview.PaymentTermsDays.Should().Be(30);
        overview.ContactName.Should().Be("Mme Joseph");
        overview.OpenPayables.Should().Be(1000m);
    }

    [Fact]
    public async Task Invoice_with_tax_is_computed_by_the_server_and_a_return_requires_a_reason()
    {
        await using var s = Scope();
        var sp = s.ServiceProvider;
        var po = sp.GetRequiredService<PurchasingService>();
        var docs = sp.GetRequiredService<IPurchasingDocumentQueries>();
        var p = await NewProduct(sp);
        var tax = await NewTax(sp, 10m);

        var inv = await po.RecordInvoiceAsync(new RecordPurchaseInvoiceCommand(Guid.NewGuid(), Supplier1, null, "HTG",
            new DateOnly(2026, 5, 2), new DateOnly(2026, 6, 2), OnCredit: true, null,
            [new InvoiceLine(p, "Taxé", 10m, 100m, tax, 999m, 0m)], true, SupplierReference: $"T-{U()}"));
        inv.IsSuccess.Should().BeTrue(inv.Error?.Message);
        var detail = (await docs.InvoiceAsync(inv.Value!.InvoiceId))!;
        detail.Invoice.TaxTotal.Should().Be(100m, "le montant de taxe fourni par le client est ignoré");
        detail.Invoice.Total.Should().Be(1100m);

        var rec = await po.ReceiveAsync(new ReceiveGoodsCommand(Guid.NewGuid(), Supplier1, Officine, null,
            [new ReceiveLine(p, null, 10m, 100m, null, null)], null, null));
        rec.IsSuccess.Should().BeTrue(rec.Error?.Message);

        (await po.RecordReturnAsync(new RecordSupplierReturnCommand(Guid.NewGuid(), Supplier1, Officine, rec.Value, null, "HTG", "  ",
            [new ReturnLine(p, 2m, 100m, 0m, null)]))).Error!.Code.Should().Be("purchasing.reason_required");
        var ret = await po.RecordReturnAsync(new RecordSupplierReturnCommand(Guid.NewGuid(), Supplier1, Officine, rec.Value, null, "HTG",
            "Flacons non conformes", [new ReturnLine(p, 2m, 100m, 0m, null)]));
        ret.IsSuccess.Should().BeTrue(ret.Error?.Message);
        (await docs.ReturnsAsync(Supplier1, 200)).Should().Contain(r => r.Id == ret.Value && r.Reason == "Flacons non conformes");

        // 10 reçus, 2 déjà retournés : en retourner 9 dépasse la réception.
        (await po.RecordReturnAsync(new RecordSupplierReturnCommand(Guid.NewGuid(), Supplier1, Officine, rec.Value, null, "HTG",
            "Encore", [new ReturnLine(p, 9m, 100m, 0m, null)]))).Error!.Code.Should().Be("purchasing.return_exceeds_receipt");
    }

    [Fact]
    public async Task Return_against_a_payable_reduces_what_is_left_to_pay()
    {
        await using var s = Scope();
        var sp = s.ServiceProvider;
        var po = sp.GetRequiredService<PurchasingService>();
        var db = sp.GetRequiredService<NexoraDbContext>();
        var p = await NewProduct(sp);

        var rec = await po.ReceiveAsync(new ReceiveGoodsCommand(Guid.NewGuid(), Supplier1, Officine, null,
            [new ReceiveLine(p, null, 10m, 100m, null, null)], null, null));
        var inv = (await po.RecordInvoiceAsync(new RecordPurchaseInvoiceCommand(Guid.NewGuid(), Supplier1, null, "HTG",
            new DateOnly(2026, 5, 2), new DateOnly(2026, 6, 2), OnCredit: true, null,
            [new InvoiceLine(p, "Avoir", 10m, 100m, null, 0m, 0m)], true, SupplierReference: $"AV-{U()}", GoodsReceiptId: rec.Value))).Value!;
        var payableId = inv.PayableId!.Value;

        // Un avoir supérieur à la dette est refusé ; un avoir de 300 laisse 700 à payer.
        (await po.RecordReturnAsync(new RecordSupplierReturnCommand(Guid.NewGuid(), Supplier1, Officine, null, payableId, "HTG",
            "Trop", [new ReturnLine(p, 11m, 100m, 0m, null)]))).Error!.Code.Should().Be("purchasing.return_exceeds_payable");
        var ret = await po.RecordReturnAsync(new RecordSupplierReturnCommand(Guid.NewGuid(), Supplier1, Officine, rec.Value, payableId, "HTG",
            "3 flacons périmés à la livraison", [new ReturnLine(p, 3m, 100m, 0m, null)]));
        ret.IsSuccess.Should().BeTrue(ret.Error?.Message);

        var payable = await db.Payables.AsNoTracking().Where(x => x.Id == payableId)
            .Select(x => new { x.CreditedAmount, x.PaidAmount, x.Status, Remaining = x.OriginalAmount - x.PaidAmount - x.CreditedAmount }).FirstAsync();
        payable.CreditedAmount.Should().Be(300m);
        payable.Remaining.Should().Be(700m);
        payable.Status.Should().Be("PARTIALLY_PAID");

        // Payer la totalité initiale serait un surpaiement de la marchandise retournée.
        (await po.PayPayableAsync(payableId, new PayPayableRequest(Guid.NewGuid(), 1000m,
            Guid.Parse("dce00000-0000-0000-0000-00000000a051"), null))).Error!.Code.Should().Be("purchasing.payment_exceeds");
        (await po.PayPayableAsync(payableId, new PayPayableRequest(Guid.NewGuid(), 700m,
            Guid.Parse("dce00000-0000-0000-0000-00000000a051"), null))).IsSuccess.Should().BeTrue();
        (await db.Payables.AsNoTracking().Where(x => x.Id == payableId).Select(x => x.Status).FirstAsync()).Should().Be("PAID");
    }
}
