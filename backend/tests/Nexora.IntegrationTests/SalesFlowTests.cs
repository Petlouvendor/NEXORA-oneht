using System.Text;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Nexora.Application.Abstractions;
using Nexora.Application.Catalog;
using Nexora.Application.Documents;
using Nexora.Application.Inventory;
using Nexora.Application.Printing;
using Nexora.Application.Purchasing;
using Nexora.Application.Sales;
using Nexora.Contracts.Documents;
using Nexora.Contracts.Printing;
using Nexora.Contracts.Sales;
using Nexora.Infrastructure.Persistence;

namespace Nexora.IntegrationTests;

/// <summary>
/// Finalisation produit, phase 4 — ventes et facturation : acompte = avoir imputé à l'exécution, paiement complet
/// exigé hors crédit, réservation libérée à l'annulation d'une commande partiellement livrée, brouillon modifiable,
/// devis (validité, refus / annulation motivés, conversion avec emplacement), factures (vente annulée refusée,
/// vrai PDF depuis le contenu figé, recherche par source).
/// </summary>
[Collection("finance")]
public sealed class SalesFlowTests(FinanceEngineFixture fx)
{
    private static readonly Guid Company = Guid.Parse("dce00000-0000-0000-0000-0000000000c1");
    private static readonly Guid Direction = Guid.Parse("dce00000-0000-0000-0000-0000000000d1");
    private static readonly Guid Officine = Guid.Parse("dce00000-0000-0000-0000-00000000a003");
    private static readonly Guid Supplier1 = Guid.Parse("dce00000-0000-0000-0000-00000000b101");
    private static readonly Guid BoxUnit = Guid.Parse("dce00000-0000-0000-0000-00000000a011");
    private static readonly Guid CashAccount = Guid.Parse("dce00000-0000-0000-0000-00000000a051");
    private static readonly Guid CashMethod = Guid.Parse("dce00000-0000-0000-0000-00000000a081");

    private AsyncServiceScope Scope()
    {
        var s = fx.Provider.CreateAsyncScope();
        var tc = (TestTenantContext)s.ServiceProvider.GetRequiredService<ITenantContext>();
        tc.SetUser(Direction);
        tc.SetScope([Company], [], Company);
        return s;
    }

    private static string U() => Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();

    private async Task<Guid> NewCustomer(decimal creditLimit = 0m)
    {
        var id = Guid.NewGuid();
        await using var c = new Npgsql.NpgsqlConnection(fx.TestConnectionString);
        await c.OpenAsync();
        await using var cmd = new Npgsql.NpgsqlCommand($"""
            INSERT INTO nexora.customer (id, company_id, customer_number, name, credit_limit, default_currency_id)
            VALUES ('{id}', '{Company}', 'SF-{id:N}', 'Client ventes {U()}',
                    {creditLimit.ToString(System.Globalization.CultureInfo.InvariantCulture)},
                    (SELECT id FROM nexora.currency WHERE code='HTG'))
            """, c);
        await cmd.ExecuteNonQueryAsync();
        return id;
    }

    private static async Task<Guid> NewProduct(IServiceProvider sp, decimal price, decimal stock)
    {
        var catalog = sp.GetRequiredService<CatalogService>();
        var id = (await catalog.CreateProductAsync(new CreateProductCommand(
            $"SF-{U()}", "Article vente", null, BoxUnit, "stockable", null, null, null, null, false, false))).Value;
        await catalog.SetPriceAsync(new SetProductPriceCommand(id, "sale", price, "HTG", 0m, null, null));
        if (stock > 0m)
            (await sp.GetRequiredService<PurchasingService>().ReceiveAsync(new ReceiveGoodsCommand(
                Guid.NewGuid(), Supplier1, Officine, null, [new ReceiveLine(id, null, stock, price / 2m, null, null)], null, null)))
                .IsSuccess.Should().BeTrue();
        return id;
    }

    private static CreateOrderRequest Order(Guid? customer, params (Guid Product, decimal Qty)[] lines)
        => new(customer, "HTG", "pickup", Officine, false, null, null, null, null,
            lines.Select(l => new CreateOrderLineInput(l.Product, l.Qty, null, 0m, null)).ToList());

    private static CompleteOrderRequest Pay(decimal cash, bool onCredit = false, IReadOnlyDictionary<Guid, decimal>? qty = null)
        => new(Guid.NewGuid(), null, onCredit, onCredit ? new DateOnly(2026, 12, 31) : null,
            cash > 0m ? [new OrderTenderInput(CashAccount, CashMethod, cash, null)] : [], qty);

    [Fact]
    public async Task Deposit_becomes_a_customer_credit_and_is_deducted_when_the_order_is_delivered()
    {
        await using var s = Scope();
        var orders = s.ServiceProvider.GetRequiredService<OrderService>();
        var q = s.ServiceProvider.GetRequiredService<ISalesQueries>();
        var db = s.ServiceProvider.GetRequiredService<NexoraDbContext>();
        var cust = await NewCustomer();
        var p = await NewProduct(s.ServiceProvider, 100m, 20m);

        var oid = (await orders.CreateOrderAsync(Order(cust, (p, 5m)))).Value;
        (await orders.ConfirmOrderAsync(oid)).IsSuccess.Should().BeTrue();
        var op = Guid.NewGuid();
        (await orders.RecordDepositAsync(oid, new OrderDepositRequest(op, 200m, CashAccount))).IsSuccess.Should().BeTrue();
        // Rejeu de la même opération : pas de second acompte.
        (await orders.RecordDepositAsync(oid, new OrderDepositRequest(op, 200m, CashAccount))).IsSuccess.Should().BeTrue();

        var dto = (await q.GetOrderAsync(oid))!;
        dto.DepositAmount.Should().Be(200m);
        dto.DepositAvailable.Should().Be(200m);
        // L'acompte apparaît au compte du client (journal client).
        (await db.Database.SqlQuery<decimal>($"""
            SELECT COALESCE(SUM(credit), 0) AS "Value" FROM nexora.customer_ledger_entry
            WHERE customer_id = {cust} AND entry_type = 'order_deposit'
            """).FirstAsync()).Should().Be(200m);

        // Payer 500 au comptant ferait payer l'acompte deux fois : seul le reste (300) est à encaisser.
        var sale = await orders.CompleteOrderAsync(oid, Pay(300m));
        sale.IsSuccess.Should().BeTrue(sale.Error?.Message);
        sale.Value!.Total.Should().Be(500m);
        sale.Value.CreditAmount.Should().Be(0m);

        dto = (await q.GetOrderAsync(oid))!;
        dto.Status.Should().Be("fulfilled");
        dto.DepositAvailable.Should().Be(0m);
        dto.Sales!.Single().AmountDue.Should().Be(0m, "l'acompte a soldé la part non encaissée");
    }

    [Fact]
    public async Task Cash_delivery_with_an_incomplete_payment_is_refused_and_changes_nothing()
    {
        await using var s = Scope();
        var orders = s.ServiceProvider.GetRequiredService<OrderService>();
        var ledger = s.ServiceProvider.GetRequiredService<IStockLedger>();
        var cust = await NewCustomer(100000m);
        var p = await NewProduct(s.ServiceProvider, 100m, 10m);

        var oid = (await orders.CreateOrderAsync(Order(cust, (p, 4m)))).Value;
        await orders.ConfirmOrderAsync(oid);
        var r = await orders.CompleteOrderAsync(oid, Pay(250m));
        r.Error!.Code.Should().Be("order.payment_incomplete");
        (await ledger.OnHandAsync(p, Officine)).Should().Be(10m, "la vente refusée n'a rien sorti du stock");
        (await orders.CompleteOrderAsync(oid, Pay(250m, onCredit: true))).IsSuccess.Should().BeTrue("à crédit, le reste devient une créance");

        // Client sans crédit autorisé : le refus parle du paiement manquant, pas d'un blocage de crédit.
        var cashOnly = await NewCustomer(0m);
        var o2 = (await orders.CreateOrderAsync(Order(cashOnly, (p, 2m)))).Value;
        await orders.ConfirmOrderAsync(o2);
        var refused = await orders.CompleteOrderAsync(o2, Pay(50m));
        refused.Error!.Code.Should().Be("order.payment_incomplete");
        refused.Error.Message.Should().Contain("150");
    }

    [Fact]
    public async Task Cancelling_a_partially_delivered_reserved_order_releases_the_rest_of_the_reservation()
    {
        await using var s = Scope();
        var orders = s.ServiceProvider.GetRequiredService<OrderService>();
        var q = s.ServiceProvider.GetRequiredService<ISalesQueries>();
        var ledger = s.ServiceProvider.GetRequiredService<IStockLedger>();
        var p = await NewProduct(s.ServiceProvider, 50m, 10m);

        var oid = (await orders.CreateOrderAsync(Order(null, (p, 6m)))).Value;
        await orders.ConfirmOrderAsync(oid);
        (await orders.ReserveOrderAsync(oid)).IsSuccess.Should().BeTrue();
        (await ledger.AvailableAsync(p, Officine)).Should().Be(4m);

        var line = (await q.GetOrderAsync(oid))!.Lines.Single();
        (await orders.CompleteOrderAsync(oid, Pay(100m, qty: new Dictionary<Guid, decimal> { [line.Id] = 2m }))).IsSuccess.Should().BeTrue();
        (await ledger.OnHandAsync(p, Officine)).Should().Be(8m);
        (await ledger.AvailableAsync(p, Officine)).Should().Be(4m, "4 unités restent réservées pour la commande");

        (await orders.CancelOrderAsync(oid, "Le client ne prend pas le reste")).IsSuccess.Should().BeTrue();
        (await ledger.AvailableAsync(p, Officine)).Should().Be(8m, "le reliquat réservé est libéré");
    }

    [Fact]
    public async Task Draft_order_is_editable_and_validation_never_crashes()
    {
        await using var s = Scope();
        var orders = s.ServiceProvider.GetRequiredService<OrderService>();
        var q = s.ServiceProvider.GetRequiredService<ISalesQueries>();
        var cust = await NewCustomer();
        var p = await NewProduct(s.ServiceProvider, 100m, 0m);

        (await orders.CreateOrderAsync(Order(cust, (p, 1m)) with { Currency = "ZZZ" })).Error!.Code.Should().Be("order.currency_unknown");
        (await orders.CreateOrderAsync(Order(cust, (p, 1m)) with { InventoryLocationId = null })).Error!.Code.Should().Be("order.no_location");
        (await orders.CreateOrderAsync(Order(cust, (p, 1m), (p, 2m)))).Error!.Code.Should().Be("order.duplicate_line");
        (await orders.CreateOrderAsync(Order(cust, (p, 0m)))).Error!.Code.Should().Be("order.quantity");

        var oid = (await orders.CreateOrderAsync(Order(cust, (p, 2m)))).Value;
        (await orders.UpdateOrderAsync(oid, Order(cust, (p, 5m)) with { Notes = "Livrer avant midi" })).IsSuccess.Should().BeTrue();
        var dto = (await q.GetOrderAsync(oid))!;
        dto.Total.Should().Be(500m);
        dto.Lines.Single().Quantity.Should().Be(5m);
        dto.Notes.Should().Be("Livrer avant midi");

        await orders.ConfirmOrderAsync(oid);
        (await orders.UpdateOrderAsync(oid, Order(cust, (p, 1m)))).Error!.Code.Should().Be("order.locked");
    }

    [Fact]
    public async Task Quote_lifecycle_validity_reasons_and_conversion_with_a_location()
    {
        await using var s = Scope();
        var quotes = s.ServiceProvider.GetRequiredService<QuoteService>();
        var orders = s.ServiceProvider.GetRequiredService<OrderService>();
        var q = s.ServiceProvider.GetRequiredService<ISalesQueries>();
        var cust = await NewCustomer();
        var p = await NewProduct(s.ServiceProvider, 80m, 10m);
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        CreateQuoteRequest Req(decimal qty, DateOnly? until) => new(cust, "HTG", null, until, "Paiement à 30 jours", null,
            [new QuoteLineInput(p, qty, null, 0m, null)]);

        (await quotes.CreateAsync(Req(1m, today.AddDays(-2)))).Error!.Code.Should().Be("quote.valid_until");
        (await quotes.CreateAsync(Req(1m, today) with { Currency = "ZZZ" })).Error!.Code.Should().Be("quote.currency_unknown");

        var qid = (await quotes.CreateAsync(Req(2m, today.AddDays(15)))).Value;
        (await quotes.UpdateAsync(qid, Req(3m, today.AddDays(15)))).IsSuccess.Should().BeTrue();
        (await quotes.GetAsync(qid)).Value!.Total.Should().Be(240m);
        (await quotes.SendAsync(qid)).IsSuccess.Should().BeTrue();
        (await quotes.UpdateAsync(qid, Req(1m, null))).Error!.Code.Should().Be("quote.locked");
        (await quotes.RejectAsync(qid, " ")).Error!.Code.Should().Be("quote.reason_required");
        (await quotes.AcceptAsync(qid)).IsSuccess.Should().BeTrue();

        var orderId = await quotes.ConvertToOrderAsync(qid, new ConvertQuoteRequest(InventoryLocationId: Officine, OrderType: "pickup"));
        orderId.IsSuccess.Should().BeTrue(orderId.Error?.Message);
        var order = (await q.GetOrderAsync(orderId.Value))!;
        order.InventoryLocationId.Should().Be(Officine);
        order.Total.Should().Be(240m);
        (await orders.ConfirmOrderAsync(orderId.Value)).IsSuccess.Should().BeTrue();
        (await orders.ReserveOrderAsync(orderId.Value)).IsSuccess.Should().BeTrue("la commande issue du devis a un emplacement");

        // Un devis envoyé dont la validité est dépassée expire à l'acceptation.
        var old = (await quotes.CreateAsync(Req(1m, today.AddDays(3)))).Value;
        await quotes.SendAsync(old);
        await using (var c = new Npgsql.NpgsqlConnection(fx.TestConnectionString))
        {
            await c.OpenAsync();
            await using var cmd = new Npgsql.NpgsqlCommand($"UPDATE nexora.quote SET valid_until = current_date - 1 WHERE id = '{old}'", c);
            await cmd.ExecuteNonQueryAsync();
        }
        await using var s2 = Scope();
        var quotes2 = s2.ServiceProvider.GetRequiredService<QuoteService>();
        (await quotes2.AcceptAsync(old)).Error!.Code.Should().Be("quote.expired");
        (await quotes2.GetAsync(old)).Value!.Status.Should().Be("expired");

        var cancel = (await quotes2.CreateAsync(Req(1m, null))).Value;
        (await quotes2.CancelAsync(cancel, "")).Error!.Code.Should().Be("quote.reason_required");
        (await quotes2.CancelAsync(cancel, "Doublon")).IsSuccess.Should().BeTrue();
        (await quotes2.GetAsync(cancel)).Value!.Status.Should().Be("cancelled");
    }

    [Fact]
    public async Task Invoice_is_issued_from_a_sale_printed_as_a_real_pdf_and_refused_for_a_void_sale()
    {
        await using var s = Scope();
        var orders = s.ServiceProvider.GetRequiredService<OrderService>();
        var docs = s.ServiceProvider.GetRequiredService<IDocumentService>();
        var search = s.ServiceProvider.GetRequiredService<IDocumentQueries>();
        var db = s.ServiceProvider.GetRequiredService<NexoraDbContext>();
        var cust = await NewCustomer();
        var p = await NewProduct(s.ServiceProvider, 120m, 10m);

        var oid = (await orders.CreateOrderAsync(Order(cust, (p, 2m)))).Value;
        await orders.ConfirmOrderAsync(oid);
        var sale = (await orders.CompleteOrderAsync(oid, Pay(240m))).Value!;

        var issued = await docs.IssueAsync(new IssueDocumentRequest("invoice", "sale", sale.SaleId));
        issued.IsSuccess.Should().BeTrue(issued.Error?.Message);
        var content = await db.Database.SqlQuery<string>($"""
            SELECT (content->'lines'->0->>'description') AS "Value" FROM nexora.issued_document WHERE id = {issued.Value!.Id}
            """).FirstAsync();
        content.Should().Be("Article vente", "les lignes figées sont lisibles (camelCase)");

        var pdf = await s.ServiceProvider.GetRequiredService<IReceiptService>().PrintAsync(PrintableKinds.IssuedDocument, issued.Value.Id, "pdf");
        pdf.IsSuccess.Should().BeTrue(pdf.Error?.Message);
        pdf.Value!.ContentType.Should().Be("application/pdf");
        Encoding.ASCII.GetString(pdf.Value.Content, 0, 5).Should().Be("%PDF-");
        (await s.ServiceProvider.GetRequiredService<IReceiptService>().PrintAsync(PrintableKinds.SalesOrder, oid, "pdf")).IsSuccess.Should().BeTrue();

        (await search.SearchAsync(new DocumentSearchRequest(SourceType: "sale", SourceId: sale.SaleId)))
            .Should().ContainSingle(d => d.Id == issued.Value.Id);

        // Une vente annulée ne se facture pas.
        await db.Database.ExecuteSqlAsync($"UPDATE nexora.sale SET status = 'void', void_reason = 'test' WHERE id = {sale.SaleId}");
        await docs.CancelAsync(issued.Value.Id, new CancelDocumentRequest("Vente annulée", null));
        (await docs.IssueAsync(new IssueDocumentRequest("invoice", "sale", sale.SaleId))).Error!.Code.Should().Be("document.source_state");
    }

    [Fact]
    public async Task Cancelled_order_keeps_the_deposit_available_to_the_customer()
    {
        await using var s = Scope();
        var orders = s.ServiceProvider.GetRequiredService<OrderService>();
        var memos = s.ServiceProvider.GetRequiredService<Nexora.Application.Customers.ICustomerCreditMemoWriter>();
        var cust = await NewCustomer();
        var p = await NewProduct(s.ServiceProvider, 100m, 0m);

        var oid = (await orders.CreateOrderAsync(Order(cust, (p, 3m)))).Value;
        await orders.RecordDepositAsync(oid, new OrderDepositRequest(Guid.NewGuid(), 150m, CashAccount));
        (await orders.CancelOrderAsync(oid, "Rupture fournisseur")).IsSuccess.Should().BeTrue();
        (await memos.AvailableAsync(cust, "HTG")).Should().Be(150m, "l'acompte reste un avoir utilisable ou remboursable");
    }
}
