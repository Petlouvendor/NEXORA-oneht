using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Nexora.Application.Documents;
using Nexora.Application.Sales;
using Nexora.Contracts.Documents;
using Npgsql;

namespace Nexora.IntegrationTests;

/// <summary>Facturation & documents commerciaux — scénarios de validation Phase 30 §50-51.</summary>
[Collection("finance")]
public sealed class CommercialDocumentsTests(FinanceEngineFixture fx)
{
    private static readonly Guid Company = Guid.Parse("dce00000-0000-0000-0000-0000000000c1");
    private static readonly Guid Customer = Guid.Parse("dce00000-0000-0000-0000-00000000b001");
    private static readonly Guid Product = Guid.Parse("dce00000-0000-0000-0000-00000000a041");
    private static readonly Guid Officine = Guid.Parse("dce00000-0000-0000-0000-00000000a003");

    private AsyncServiceScope Scope() => fx.Provider.CreateAsyncScope();

    private static string Num(decimal d) => d.ToString(System.Globalization.CultureInfo.InvariantCulture);

    private async Task<T> Scalar<T>(string sql)
    {
        await using var c = new NpgsqlConnection(fx.TestConnectionString);
        await c.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, c);
        return (T)(await cmd.ExecuteScalarAsync())!;
    }

    private Task<Guid> NewSale(decimal total, string type = "credit") =>
        Scalar<Guid>($"""
            WITH s AS (
              INSERT INTO nexora.sale (company_id, customer_id, document_number, status, sale_type, currency_id,
                                       sold_at, subtotal, discount_total, tax_total, total, amount_paid)
              VALUES ('{Company}', '{Customer}', 'DOC-TST-' || substr(gen_random_uuid()::text,1,10), 'completed', '{type}',
                      (SELECT id FROM nexora.currency WHERE code='HTG'), now(),
                      {Num(total)}, 0, 0, {Num(total)}, 0)
              RETURNING id, company_id
            )
            , l AS (
              INSERT INTO nexora.sale_line (sale_id, company_id, product_id, description, quantity, unit_price, line_total, tax_amount)
              SELECT s.id, s.company_id, '{Product}', 'Paracétamol 500 mg', 1, {Num(total)}, {Num(total)}, 0 FROM s
            )
            SELECT id FROM s
            """);

    private Task<Guid> NewReturn(Guid saleId, decimal total) =>
        Scalar<Guid>($"""
            WITH r AS (
              INSERT INTO nexora.sale_return (company_id, sale_id, document_number, status, currency_id, reason, refund_total, returned_at)
              VALUES ('{Company}', '{saleId}', 'RET-TST-' || substr(gen_random_uuid()::text,1,10), 'completed',
                      (SELECT id FROM nexora.currency WHERE code='HTG'), 'Produit non conforme', {Num(total)}, now())
              RETURNING id, company_id
            )
            , l AS (
              INSERT INTO nexora.sale_return_line (sale_return_id, company_id, product_id, quantity, unit_price, line_total, tax_amount)
              SELECT r.id, r.company_id, '{Product}', 1, {Num(total)}, {Num(total)}, 0 FROM r
            )
            SELECT id FROM r
            """);

    private Task<Guid> NewPayment(decimal amount) =>
        Scalar<Guid>($"""
            INSERT INTO nexora.payment (company_id, payment_number, direction, party_type, customer_id, currency_id, amount, status, received_at)
            VALUES ('{Company}', 'PAY-TST-' || substr(gen_random_uuid()::text,1,10), 'inbound', 'customer', '{Customer}',
                    (SELECT id FROM nexora.currency WHERE code='HTG'), {Num(amount)}, 'completed', now())
            RETURNING id
            """);

    // ---- §4-5, §50 : devis créé → chiffré → envoyé → converti en commande ----
    [Fact]
    public async Task Quote_is_created_priced_sent_and_converted_to_order()
    {
        await using var s = Scope();
        var quotes = s.ServiceProvider.GetRequiredService<QuoteService>();

        var created = await quotes.CreateAsync(new CreateQuoteRequest(
            Customer, "HTG", Officine, TestDates.Today.AddDays(30), "Paiement à 30 j", null,
            [new QuoteLineInput(Product, 4m, 25_000m, 0m, null)]));
        created.IsSuccess.Should().BeTrue(created.Error?.Message);

        var q = (await quotes.GetAsync(created.Value)).Value!;
        q.Status.Should().Be("draft");
        q.Total.Should().Be(100_000m);
        q.DocumentNumber.Should().Contain("DEV");

        (await quotes.SendAsync(created.Value)).IsSuccess.Should().BeTrue();
        (await quotes.AcceptAsync(created.Value)).IsSuccess.Should().BeTrue();

        var order = await quotes.ConvertToOrderAsync(created.Value, new ConvertQuoteRequest(OnCredit: false, DueDate: null, OrderType: "delivery"));
        order.IsSuccess.Should().BeTrue(order.Error?.Message);

        var after = (await quotes.GetAsync(created.Value)).Value!;
        after.Status.Should().Be("converted");
        after.ConvertedOrderId.Should().Be(order.Value);

        (await quotes.ConvertToOrderAsync(created.Value, new ConvertQuoteRequest()))
            .Error!.Code.Should().Be("quote.already_converted");
    }

    // ---- §8, §19, §33 : facture émise depuis une vente — snapshot figé + hash + code ----
    [Fact]
    public async Task Invoice_is_issued_from_a_sale_with_a_frozen_snapshot()
    {
        var sale = await NewSale(60_000m);
        await using var s = Scope();
        var docs = s.ServiceProvider.GetRequiredService<IDocumentService>();

        var issued = await docs.IssueAsync(new IssueDocumentRequest("invoice", "sale", sale));
        issued.IsSuccess.Should().BeTrue(issued.Error?.Message);
        issued.Value!.DocumentNumber.Should().Contain("INV");
        issued.Value.Status.Should().Be("issued");
        issued.Value.ContentHash.Should().NotBeNullOrEmpty();
        issued.Value.VerificationCode.Should().NotBeNullOrEmpty();
        issued.Value.Total.Should().Be(60_000m);

        (await docs.IssueAsync(new IssueDocumentRequest("invoice", "sale", sale)))
            .Error!.Code.Should().Be("document.already_issued");
    }

    // ---- §27-28 : vérification publique — données minimales ; code inconnu ----
    [Fact]
    public async Task Public_verification_returns_minimal_data()
    {
        var sale = await NewSale(15_000m);
        await using var s = Scope();
        var docs = s.ServiceProvider.GetRequiredService<IDocumentService>();
        var issued = (await docs.IssueAsync(new IssueDocumentRequest("invoice", "sale", sale))).Value!;

        var ok = await docs.VerifyAsync(issued.VerificationCode!);
        ok.Found.Should().BeTrue();
        ok.DocumentNumber.Should().Be(issued.DocumentNumber);
        ok.Status.Should().Be("issued");
        ok.Total.Should().Be(15_000m);

        (await docs.VerifyAsync("DEADBEEF00")).Found.Should().BeFalse();
    }

    // ---- §31-32 : annulation — le document reste visible, la vérification le reflète ----
    [Fact]
    public async Task Cancelled_document_stays_visible()
    {
        var sale = await NewSale(9_000m);
        await using var s = Scope();
        var docs = s.ServiceProvider.GetRequiredService<IDocumentService>();
        var q = s.ServiceProvider.GetRequiredService<IDocumentQueries>();
        var issued = (await docs.IssueAsync(new IssueDocumentRequest("invoice", "sale", sale))).Value!;

        (await docs.CancelAsync(issued.Id, new CancelDocumentRequest(""))).Error!.Code.Should().Be("document.cancel_reason");
        (await docs.CancelAsync(issued.Id, new CancelDocumentRequest("Erreur de montant"))).IsSuccess.Should().BeTrue();

        (await q.GetAsync(issued.Id)).Value!.Header.Status.Should().Be("cancelled");
        (await docs.VerifyAsync(issued.VerificationCode!)).Message.Should().Contain("annulé");
    }

    // ---- §12-13, §19 : avoir depuis un retour, reçu depuis un paiement — numéros distincts ----
    [Fact]
    public async Task Credit_note_and_receipt_are_issued_with_distinct_series()
    {
        var sale = await NewSale(40_000m);
        var ret = await NewReturn(sale, 12_000m);
        var pay = await NewPayment(28_000m);

        await using var s = Scope();
        var docs = s.ServiceProvider.GetRequiredService<IDocumentService>();

        var inv = (await docs.IssueAsync(new IssueDocumentRequest("invoice", "sale", sale))).Value!;
        var crn = await docs.IssueAsync(new IssueDocumentRequest("credit_note", "sale_return", ret));
        var rcu = await docs.IssueAsync(new IssueDocumentRequest("receipt", "payment", pay));

        crn.IsSuccess.Should().BeTrue(crn.Error?.Message);
        rcu.IsSuccess.Should().BeTrue(rcu.Error?.Message);
        crn.Value!.DocumentNumber.Should().Contain("AVR");
        rcu.Value!.DocumentNumber.Should().Contain("RCU");
        new[] { inv.DocumentNumber, crn.Value.DocumentNumber, rcu.Value.DocumentNumber }.Distinct().Should().HaveCount(3);
    }

    // ---- §25 : rendu — HTML contenant le numéro du document ----
    [Fact]
    public async Task Rendered_document_is_html_with_the_number()
    {
        var sale = await NewSale(21_000m);
        await using var s = Scope();
        var docs = s.ServiceProvider.GetRequiredService<IDocumentService>();
        var issued = (await docs.IssueAsync(new IssueDocumentRequest("invoice", "sale", sale))).Value!;

        var render = await docs.RenderAsync(issued.Id, "a4");
        render.IsSuccess.Should().BeTrue(render.Error?.Message);
        render.Value!.ContentType.Should().Contain("text/html");
        render.Value.Content.Should().Contain(issued.DocumentNumber);
        render.Value.Content.Should().Contain("Facture");
    }

    // ---- §34, §36 : recherche par type + journal d'envoi ----
    [Fact]
    public async Task Search_and_send_log()
    {
        var sale = await NewSale(33_000m);
        await using var s = Scope();
        var docs = s.ServiceProvider.GetRequiredService<IDocumentService>();
        var q = s.ServiceProvider.GetRequiredService<IDocumentQueries>();
        var issued = (await docs.IssueAsync(new IssueDocumentRequest("invoice", "sale", sale))).Value!;

        var send = await docs.SendAsync(issued.Id, new SendDocumentRequest("link", "client@exemple.test", "Votre facture", null));
        send.IsSuccess.Should().BeTrue(send.Error?.Message);
        (await q.SendsAsync(issued.Id)).Should().Contain(x => x.Channel == "link" && x.Status == "sent");

        var found = await q.SearchAsync(new DocumentSearchRequest(Type: "invoice"));
        found.Should().Contain(d => d.Id == issued.Id);
        found.Should().OnlyContain(d => d.DocumentType == "invoice");
    }
}
