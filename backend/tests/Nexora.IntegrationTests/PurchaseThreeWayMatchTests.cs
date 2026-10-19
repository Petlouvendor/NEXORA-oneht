using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Nexora.Application.Catalog;
using Nexora.Application.Purchasing;
using Nexora.Contracts.Purchasing;
using Nexora.Infrastructure.Persistence;

namespace Nexora.IntegrationTests;

/// <summary>Achats, fournisseurs, 3-way match & dettes — scénarios de validation Phase 26 §55-56.</summary>
[Collection("finance")]
public sealed class PurchaseThreeWayMatchTests(FinanceEngineFixture fx)
{
    private static readonly Guid Company = Guid.Parse("dce00000-0000-0000-0000-0000000000c1");
    private static readonly Guid Officine = Guid.Parse("dce00000-0000-0000-0000-00000000a003");
    private static readonly Guid Supplier1 = Guid.Parse("dce00000-0000-0000-0000-00000000b101");
    private static readonly Guid BoxUnit = Guid.Parse("dce00000-0000-0000-0000-00000000a011");
    private static readonly Guid CashHtg = Guid.Parse("dce00000-0000-0000-0000-00000000a051");

    private AsyncServiceScope Scope() => fx.Provider.CreateAsyncScope();

    private async Task<Guid> NewProduct(IServiceProvider sp, string suffix)
    {
        var r = await sp.GetRequiredService<CatalogService>().CreateProductAsync(new CreateProductCommand(
            $"PU-{suffix}", $"Achat {suffix}", null, BoxUnit, "stockable", null, null, null, null, false, false));
        r.IsSuccess.Should().BeTrue(r.Error?.Message);
        return r.Value;
    }

    private static readonly MatchInvoiceRequest Tol5 = new(5m, 2m);

    // ---- §10 : cycle de vie de la commande — transitions contrôlées ----
    [Fact]
    public async Task Purchase_order_lifecycle_enforces_transitions()
    {
        await using var s = Scope();
        var po = s.ServiceProvider.GetRequiredService<PurchasingService>();
        var id = await NewProduct(s.ServiceProvider, "L1");
        var order = (await po.CreateOrderAsync(new CreatePurchaseOrderCommand(
            Supplier1, null, "HTG", null, [new CreatePurchaseOrderLine(id, 10m, 500m, null, 0m)], null))).Value;

        (await po.ApproveOrderAsync(order)).IsSuccess.Should().BeTrue();     // submitted -> approved
        (await po.SendOrderAsync(order)).IsSuccess.Should().BeTrue();        // approved -> sent
        (await po.ApproveOrderAsync(order)).IsSuccess.Should().BeFalse();    // sent -> approved interdit
        (await po.CancelOrderAsync(order)).IsSuccess.Should().BeTrue();      // sent -> cancelled

        var status = await s.ServiceProvider.GetRequiredService<NexoraDbContext>()
            .PurchaseOrders.AsNoTracking().Where(o => o.Id == order).Select(o => o.Status).FirstAsync();
        status.Should().Be("cancelled");
    }

    // ---- §54 : une référence de facture ne peut être réutilisée pour le même fournisseur ----
    [Fact]
    public async Task Duplicate_supplier_invoice_reference_is_rejected()
    {
        await using var s = Scope();
        var po = s.ServiceProvider.GetRequiredService<PurchasingService>();
        var id = await NewProduct(s.ServiceProvider, "DUP");

        var cmd = new RecordPurchaseInvoiceCommand(
            Guid.NewGuid(), Supplier1, null, "HTG", new DateOnly(2026, 5, 1), new DateOnly(2026, 6, 1),
            OnCredit: true, TenderFinancialAccountId: null,
            [new InvoiceLine(id, "L", 2m, 100m, null, 0m, 0m)], CapitaliseToInventory: true,
            SupplierReference: "FA-2026-777");

        (await po.RecordInvoiceAsync(cmd)).IsSuccess.Should().BeTrue();
        var dup = await po.RecordInvoiceAsync(cmd with { OperationUuid = Guid.NewGuid() });
        dup.IsSuccess.Should().BeFalse();
        dup.Error!.Code.Should().Be("purchasing.duplicate_invoice");
    }

    // ---- §20 : 3-way match dans la tolérance → facture rapprochée, paiement autorisé ----
    [Fact]
    public async Task Three_way_match_within_tolerance_passes()
    {
        await using var s = Scope();
        var po = s.ServiceProvider.GetRequiredService<PurchasingService>();
        var db = s.ServiceProvider.GetRequiredService<NexoraDbContext>();
        var id = await NewProduct(s.ServiceProvider, "M1");

        var order = (await po.CreateOrderAsync(new CreatePurchaseOrderCommand(
            Supplier1, null, "HTG", null, [new CreatePurchaseOrderLine(id, 100m, 500m, null, 0m)], null))).Value;
        await po.ApproveOrderAsync(order);
        (await po.ReceiveAsync(new ReceiveGoodsCommand(Guid.NewGuid(), Supplier1, Officine, order,
            [new ReceiveLine(id, null, 100m, 500m, null, null)], null, null))).IsSuccess.Should().BeTrue();

        var inv = (await po.RecordInvoiceAsync(new RecordPurchaseInvoiceCommand(
            Guid.NewGuid(), Supplier1, order, "HTG", new DateOnly(2026, 5, 2), new DateOnly(2026, 6, 1),
            OnCredit: true, null, [new InvoiceLine(id, "M1", 100m, 500m, null, 0m, 0m)], true))).Value!;

        var report = (await po.MatchInvoiceAsync(inv.InvoiceId, Tol5)).Value!;
        report.MatchStatus.Should().Be("matched");
        report.PaymentBlocked.Should().BeFalse();
        report.Lines.Should().ContainSingle().Which.Status.Should().Be("matched");

        var blocked = await db.Database.SqlQuery<bool>($"""
            SELECT payment_blocked AS "Value" FROM nexora.supplier_invoice WHERE id = {inv.InvoiceId}
            """).FirstAsync();
        blocked.Should().BeFalse();
    }

    // ---- §20, §40 : écart de prix hors tolérance → variance + blocage paiement ----
    [Fact]
    public async Task Price_variance_blocks_the_payment()
    {
        await using var s = Scope();
        var po = s.ServiceProvider.GetRequiredService<PurchasingService>();
        var id = await NewProduct(s.ServiceProvider, "M2");

        var order = (await po.CreateOrderAsync(new CreatePurchaseOrderCommand(
            Supplier1, null, "HTG", null, [new CreatePurchaseOrderLine(id, 100m, 500m, null, 0m)], null))).Value;
        await po.ApproveOrderAsync(order);
        await po.ReceiveAsync(new ReceiveGoodsCommand(Guid.NewGuid(), Supplier1, Officine, order,
            [new ReceiveLine(id, null, 100m, 500m, null, null)], null, null));

        // Facture à 600 (20 % au-dessus du prix commandé).
        var inv = (await po.RecordInvoiceAsync(new RecordPurchaseInvoiceCommand(
            Guid.NewGuid(), Supplier1, order, "HTG", new DateOnly(2026, 5, 2), new DateOnly(2026, 6, 1),
            OnCredit: true, null, [new InvoiceLine(id, "M2", 100m, 600m, null, 0m, 0m)], true))).Value!;

        var report = (await po.MatchInvoiceAsync(inv.InvoiceId, Tol5)).Value!;
        report.MatchStatus.Should().Be("variance");
        report.PaymentBlocked.Should().BeTrue();
        report.Lines.Single().PriceVariancePercent.Should().Be(20m);

        var pay = await po.PayPayableAsync(inv.PayableId!.Value,
            new PayPayableRequest(Guid.NewGuid(), 10000m, CashHtg, null));
        pay.IsSuccess.Should().BeFalse();
        pay.Error!.Code.Should().Be("purchasing.payment_blocked");
    }

    // ---- §20 : écart de quantité (facturé > reçu) hors tolérance → variance ----
    [Fact]
    public async Task Quantity_variance_is_flagged()
    {
        await using var s = Scope();
        var po = s.ServiceProvider.GetRequiredService<PurchasingService>();
        var id = await NewProduct(s.ServiceProvider, "M3");

        var order = (await po.CreateOrderAsync(new CreatePurchaseOrderCommand(
            Supplier1, null, "HTG", null, [new CreatePurchaseOrderLine(id, 100m, 500m, null, 0m)], null))).Value;
        await po.ApproveOrderAsync(order);
        await po.ReceiveAsync(new ReceiveGoodsCommand(Guid.NewGuid(), Supplier1, Officine, order,
            [new ReceiveLine(id, null, 100m, 500m, null, null)], null, null));

        var inv = (await po.RecordInvoiceAsync(new RecordPurchaseInvoiceCommand(
            Guid.NewGuid(), Supplier1, order, "HTG", new DateOnly(2026, 5, 2), null,
            OnCredit: true, null, [new InvoiceLine(id, "M3", 120m, 500m, null, 0m, 0m)], true))).Value!;

        var report = (await po.MatchInvoiceAsync(inv.InvoiceId, Tol5)).Value!;
        report.MatchStatus.Should().Be("variance");
        report.Lines.Single().QuantityVariancePercent.Should().Be(20m);   // 120 vs 100
    }

    // ---- §56 : scénario complet — commande → réception → facture crédit → dette → paiements → solde 0 ----
    [Fact]
    public async Task Full_purchase_to_debt_settlement()
    {
        await using var s = Scope();
        var po = s.ServiceProvider.GetRequiredService<PurchasingService>();
        var db = s.ServiceProvider.GetRequiredService<NexoraDbContext>();
        var id = await NewProduct(s.ServiceProvider, "E2E");

        var order = (await po.CreateOrderAsync(new CreatePurchaseOrderCommand(
            Supplier1, null, "HTG", null, [new CreatePurchaseOrderLine(id, 100m, 500m, null, 0m)], null))).Value;
        await po.ApproveOrderAsync(order);
        await po.SendOrderAsync(order);
        await po.ReceiveAsync(new ReceiveGoodsCommand(Guid.NewGuid(), Supplier1, Officine, order,
            [new ReceiveLine(id, null, 100m, 500m, null, null)], null, null));

        var inv = (await po.RecordInvoiceAsync(new RecordPurchaseInvoiceCommand(
            Guid.NewGuid(), Supplier1, order, "HTG", new DateOnly(2026, 5, 2), new DateOnly(2026, 6, 1),
            OnCredit: true, null, [new InvoiceLine(id, "E2E", 100m, 500m, null, 0m, 0m)], true,
            SupplierReference: "FA-E2E-001"))).Value!;
        inv.PayableId.Should().NotBeNull();

        (await po.MatchInvoiceAsync(inv.InvoiceId, Tol5)).Value!.PaymentBlocked.Should().BeFalse();

        var payableId = inv.PayableId!.Value;
        (await po.PayPayableAsync(payableId, new PayPayableRequest(Guid.NewGuid(), 20000m, CashHtg, "vir-1"))).IsSuccess.Should().BeTrue();
        var mid = await db.Payables.AsNoTracking().Where(p => p.Id == payableId).Select(p => p.PaidAmount).FirstAsync();
        mid.Should().Be(20000m);

        (await po.PayPayableAsync(payableId, new PayPayableRequest(Guid.NewGuid(), 30000m, CashHtg, "vir-2"))).IsSuccess.Should().BeTrue();
        var final = await db.Payables.AsNoTracking().Where(p => p.Id == payableId)
            .Select(p => new { p.PaidAmount, p.Status }).FirstAsync();
        final.PaidAmount.Should().Be(50000m);
        final.Status.Should().Be("PAID");

        // Un paiement de trop est refusé.
        (await po.PayPayableAsync(payableId, new PayPayableRequest(Guid.NewGuid(), 1m, CashHtg, null)))
            .Error!.Code.Should().Be("purchasing.payable_closed");
    }

    // ---- §36 : relevé fournisseur — factures & paiements avec solde progressif ----
    [Fact]
    public async Task Supplier_statement_lists_invoices_and_payments()
    {
        await using var s = Scope();
        var po = s.ServiceProvider.GetRequiredService<PurchasingService>();
        var q = s.ServiceProvider.GetRequiredService<IPurchasingQueries>();
        var id = await NewProduct(s.ServiceProvider, "ST");

        var inv = (await po.RecordInvoiceAsync(new RecordPurchaseInvoiceCommand(
            Guid.NewGuid(), Supplier1, null, "HTG", new DateOnly(2026, 7, 1), new DateOnly(2026, 8, 1),
            OnCredit: true, null, [new InvoiceLine(id, "ST", 4m, 250m, null, 0m, 0m)], true,
            SupplierReference: "FA-ST-1"))).Value!;
        await po.PayPayableAsync(inv.PayableId!.Value, new PayPayableRequest(Guid.NewGuid(), 400m, CashHtg, "acompte"));

        var stmt = await q.SupplierStatementAsync(Supplier1, null, null);
        stmt.Should().NotBeNull();
        stmt!.Lines.Should().Contain(l => l.DocumentType == "invoice" && l.Charge == 1000m);
        stmt.Lines.Should().Contain(l => l.DocumentType == "payment" && l.Payment == 400m);

        var payables = await q.ListPayablesAsync(Supplier1, onlyOpen: true, 50);
        payables.Should().Contain(p => p.SupplierReference == "FA-ST-1" && p.RemainingAmount == 600m);

        // Audit 2026-10 éd. 2 : un paiement annulé ne réduit plus le solde du relevé.
        var db = s.ServiceProvider.GetRequiredService<NexoraDbContext>();
        var paymentNumber = await db.Database.SqlQuery<string>($"""
            SELECT payment_number AS "Value" FROM nexora.payment
            WHERE supplier_id = {Supplier1} AND direction = 'outbound' AND status = 'completed' ORDER BY created_at DESC LIMIT 1
            """).FirstAsync();
        await db.Database.ExecuteSqlAsync($"""
            UPDATE nexora.payment SET status = 'void', voided_at = now(), void_reason = 'Chèque rejeté (test)'
            WHERE payment_number = {paymentNumber} AND supplier_id = {Supplier1}
            """);
        var after = await q.SupplierStatementAsync(Supplier1, null, null);
        after!.Lines.Should().NotContain(l => l.DocumentNumber == paymentNumber, "un paiement annulé n'a rien réglé");
        after.ClosingBalance.Should().Be(stmt.ClosingBalance + 400m);
    }
}
