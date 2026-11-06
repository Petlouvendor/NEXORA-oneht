using System.Globalization;
using System.Text;
using ClosedXML.Excel;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Nexora.Application.Abstractions;
using Nexora.Application.Catalog;
using Nexora.Application.Customers;
using Nexora.Application.Identity;
using Nexora.Application.Parties;
using Nexora.Application.Printing;
using Nexora.Application.Purchasing;
using Nexora.Application.Reporting;
using Nexora.Application.Sales;
using Nexora.Contracts.Customers;
using Nexora.Contracts.Identity;
using Nexora.Contracts.Parties;
using Nexora.Contracts.Printing;
using Nexora.Contracts.Purchasing;
using Nexora.Contracts.Reporting;
using PdfSharp.Pdf.IO;

namespace Nexora.IntegrationTests;

/// <summary>
/// Impression des reçus clients et fournisseurs et export des rapports : chiffres « total versé / reste à
/// verser » figés au moment de l'opération, réimpression marquée DUPLICATA, droits par type de pièce,
/// vrais fichiers PDF (A4, ticket 80 mm) et Excel relus.
/// </summary>
[Collection("finance")]
public sealed class PrintingTests(FinanceEngineFixture fx)
{
    private static readonly Guid Company = Guid.Parse("dce00000-0000-0000-0000-0000000000c1");
    private static readonly Guid Direction = Guid.Parse("dce00000-0000-0000-0000-0000000000d1");
    private static readonly Guid Officine = Guid.Parse("dce00000-0000-0000-0000-00000000a003");
    private static readonly Guid Supplier1 = Guid.Parse("dce00000-0000-0000-0000-00000000b101");
    private static readonly Guid BoxUnit = Guid.Parse("dce00000-0000-0000-0000-00000000a011");
    private static readonly Guid CashAccount = Guid.Parse("dce00000-0000-0000-0000-00000000a051");
    private static readonly Guid CashMethod = Guid.Parse("dce00000-0000-0000-0000-00000000a081");

    private AsyncServiceScope Scope() => fx.Provider.CreateAsyncScope();

    private static void As(IServiceProvider sp, Guid userId)
    {
        var tc = (TestTenantContext)sp.GetRequiredService<ITenantContext>();
        tc.SetUser(userId);
        tc.SetScope([Company], [], Company);
    }

    private async Task<Guid> UserWithRole(string role)
    {
        var id = Guid.NewGuid();
        await using (var c = new Npgsql.NpgsqlConnection(fx.TestConnectionString))
        {
            await c.OpenAsync();
            await using var cmd = new Npgsql.NpgsqlCommand($"""
                INSERT INTO nexora.app_user (id, email, password_hash, full_name, status)
                VALUES ('{id}', 'PRT-{id:N}@test.demo', crypt('x', gen_salt('bf', 4)), 'Impression {role}', 'active')
                """, c);
            await cmd.ExecuteNonQueryAsync();
        }
        await using var s = Scope();
        As(s.ServiceProvider, Direction);
        (await s.ServiceProvider.GetRequiredService<IUserAssignmentService>()
            .GrantAsync(id, new GrantAssignmentRequest(role, "COMPANY", Company, null, null, null, null))).IsSuccess.Should().BeTrue();
        return id;
    }

    private async Task<long> Scalar(string sql)
    {
        await using var c = new Npgsql.NpgsqlConnection(fx.TestConnectionString);
        await c.OpenAsync();
        await using var cmd = new Npgsql.NpgsqlCommand(sql, c);
        return Convert.ToInt64(await cmd.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
    }

    private async Task<CustomerDto> NewCustomer()
    {
        await using var s = Scope();
        As(s.ServiceProvider, Direction);
        return (await s.ServiceProvider.GetRequiredService<ICustomerService>().CreateAsync(new CreateCustomerRequest(
            $"Client reçu {Guid.NewGuid():N}"[..24], CreditLimit: 100_000m))).Value!;
    }

    private async Task<Guid> Product(decimal price)
    {
        await using var s = Scope();
        As(s.ServiceProvider, Direction);
        var catalog = s.ServiceProvider.GetRequiredService<CatalogService>();
        var id = (await catalog.CreateProductAsync(new CreateProductCommand(
            $"PRT-{Guid.NewGuid():N}"[..20], "Article reçu", null, BoxUnit, "stockable", null, null, null, null, false, false))).Value;
        await catalog.SetPriceAsync(new SetProductPriceCommand(id, "sale", price, "HTG", 0m, null, null));
        await s.ServiceProvider.GetRequiredService<PurchasingService>().ReceiveAsync(new ReceiveGoodsCommand(
            Guid.NewGuid(), Supplier1, Officine, null, [new ReceiveLine(id, null, 500m, price / 2m, null, null)], null, null));
        return id;
    }

    private async Task<CheckoutResult> CreditSale(Guid customer, Guid product, decimal qty, IReadOnlyList<TenderInput>? tenders = null)
    {
        await using var s = Scope();
        As(s.ServiceProvider, Direction);
        var r = await s.ServiceProvider.GetRequiredService<SalesService>().CheckoutAsync(new CheckoutCommand(
            Guid.NewGuid(), Officine, null, null, null, customer, "HTG", [new CheckoutLine(product, qty, null, 0m, null)],
            tenders ?? [], 0m, null, null, null, null));
        r.IsSuccess.Should().BeTrue(r.Error?.Message);
        return r.Value!;
    }

    private async Task<CustomerPaymentDto> Pay(Guid credit, decimal amount, Guid? user = null)
    {
        await using var s = Scope();
        As(s.ServiceProvider, user ?? Direction);
        var r = await s.ServiceProvider.GetRequiredService<ICustomerAccountService>().RecordCreditPaymentAsync(credit,
            new RecordCreditPaymentRequest(Guid.NewGuid(), amount, CashAccount, CashMethod));
        r.IsSuccess.Should().BeTrue(r.Error?.Message);
        return r.Value!;
    }

    private async Task<RenderedFile> Print(string kind, Guid id, string format, Guid? user = null)
    {
        await using var s = Scope();
        As(s.ServiceProvider, user ?? Direction);
        var r = await s.ServiceProvider.GetRequiredService<IReceiptService>().PrintAsync(kind, id, format);
        r.IsSuccess.Should().BeTrue(r.Error?.Message);
        Keep(r.Value!);
        return r.Value!;
    }

    /// <summary>Dépose les fichiers produits dans NEXORA_PRINT_SAMPLES (relecture visuelle), sinon rien.</summary>
    private static void Keep(RenderedFile file)
    {
        var dir = Environment.GetEnvironmentVariable("NEXORA_PRINT_SAMPLES");
        if (string.IsNullOrWhiteSpace(dir)) return;
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, file.FileName), file.Content);
    }

    /// <summary>Valeur (colonne B) du chiffre clé « label » d'un classeur de reçu.</summary>
    private static double Figure(RenderedFile xlsx, string label)
    {
        using var wb = new XLWorkbook(new MemoryStream(xlsx.Content));
        var ws = wb.Worksheets.First();
        var cell = ws.CellsUsed().First(c => c.Address.ColumnNumber == 1 && c.GetString() == label);
        return ws.Cell(cell.Address.RowNumber, 2).GetDouble();
    }

    private static bool Contains(RenderedFile xlsx, string text)
    {
        using var wb = new XLWorkbook(new MemoryStream(xlsx.Content));
        return wb.Worksheets.SelectMany(w => w.CellsUsed()).Any(c => c.GetString().Contains(text, StringComparison.Ordinal));
    }

    // ---- Audit 2026-10 éd. 2 : impressions simultanées — un seul original ----
    [Fact]
    public async Task Simultaneous_prints_of_one_receipt_produce_a_single_original()
    {
        var customer = await NewCustomer();
        var sale = await CreditSale(customer.Id, await Product(100m), 3m);
        var payment = await Pay(sale.ReceivableId!.Value, 100m);

        var prints = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Print(PrintableKinds.CustomerPayment, payment.Id, "xlsx")));
        prints.Count(p => !Contains(p, "DUPLICATA")).Should().Be(1, "une seule sortie est l'original, les autres sont des copies");
        prints.Where(p => Contains(p, "DUPLICATA")).Select(p => Enumerable.Range(1, 3).Single(n => Contains(p, $"copie n° {n}")))
            .Should().BeEquivalentTo([1, 2, 3], "chaque copie a son propre numéro");
    }

    // ---- Reçu de versement : total versé / reste à verser du moment, réimpression = DUPLICATA ----
    [Fact]
    public async Task Payment_receipt_freezes_paid_and_remaining_at_payment_time_and_reprints_are_duplicates()
    {
        var customer = await NewCustomer();
        var sale = await CreditSale(customer.Id, await Product(100m), 9m);           // 900 à crédit
        var credit = sale.ReceivableId!.Value;
        var first = await Pay(credit, 300m);

        var receipt = await Print(PrintableKinds.CustomerPayment, first.Id, "xlsx");
        receipt.FileName.Should().EndWith(".xlsx");
        Figure(receipt, "Montant de ce versement").Should().Be(300);
        Figure(receipt, "Total des crédits").Should().Be(900);
        Figure(receipt, "Total versé").Should().Be(300);
        Figure(receipt, "Reste à verser").Should().Be(600);
        Contains(receipt, "DUPLICATA").Should().BeFalse("première impression = original");

        var second = await Pay(credit, 200m);
        var pdf = await Print(PrintableKinds.CustomerPayment, first.Id, "pdf");
        Encoding.ASCII.GetString(pdf.Content, 0, 5).Should().Be("%PDF-");
        var reprint = await Print(PrintableKinds.CustomerPayment, first.Id, "xlsx");
        Figure(reprint, "Reste à verser").Should().Be(600, "un reçu redonne la situation du jour du versement");
        Contains(reprint, "DUPLICATA — copie n° 2").Should().BeTrue();

        var receipt2 = await Print(PrintableKinds.CustomerPayment, second.Id, "xlsx");
        Figure(receipt2, "Total versé").Should().Be(500);
        Figure(receipt2, "Reste à verser").Should().Be(400);

        var ticket = await Print(PrintableKinds.CustomerPayment, second.Id, "ticket");
        using (var t = PdfReader.Open(new MemoryStream(ticket.Content), PdfDocumentOpenMode.Import))
            t.Pages[0].Width.Point.Should().BeApproximately(226.8, 0.5, "ticket thermique 80 mm");

        (await Scalar($"""
            SELECT count(*) FROM nexora.export_log
            WHERE report_key = 'receipt:customer-payment' AND filter ->> 'sourceId' = '{first.Id}'
            """)).Should().Be(3, "chaque impression est journalisée");
    }

    // ---- Relevé, crédit (reconnaissance de dette), avoir et ticket de vente ----
    [Fact]
    public async Task Statement_credit_and_sale_ticket_carry_the_account_position()
    {
        var customer = await NewCustomer();
        var product = await Product(250m);
        var sale = await CreditSale(customer.Id, product, 4m);                        // 1 000 à crédit
        await Pay(sale.ReceivableId!.Value, 400m);

        await using (var s = Scope())
        {
            As(s.ServiceProvider, Direction);
            var statement = await s.ServiceProvider.GetRequiredService<IReceiptService>()
                .CustomerStatementAsync(customer.Id, null, null, null, "xlsx");
            statement.IsSuccess.Should().BeTrue(statement.Error?.Message);
            Keep(statement.Value!);
            Figure(statement.Value!, "Reste à verser").Should().Be(600);
            Figure(statement.Value!, "Total versé").Should().Be(400);
            using var wb = new XLWorkbook(new MemoryStream(statement.Value!.Content));
            var ws = wb.Worksheets.First();
            var debit = ws.CellsUsed().Single(c => c.GetString() == "Débit (HTG)");
            ws.Cell(debit.Address.RowNumber + 1, debit.Address.ColumnNumber).DataType.Should().Be(XLDataType.Number);

            var pdf = await s.ServiceProvider.GetRequiredService<IReceiptService>()
                .CustomerStatementAsync(customer.Id, null, null, null, "pdf");
            Keep(pdf.Value!);
            Encoding.ASCII.GetString(pdf.Value!.Content, 0, 5).Should().Be("%PDF-");

            // Relevé sur l'imprimante de caisse : ticket 80 mm, une seule bande à la hauteur du contenu.
            var statementTicket = await s.ServiceProvider.GetRequiredService<IReceiptService>()
                .CustomerStatementAsync(customer.Id, null, null, null, "ticket");
            statementTicket.IsSuccess.Should().BeTrue(statementTicket.Error?.Message);
            Keep(statementTicket.Value!);
            statementTicket.Value!.FileName.Should().EndWith("-ticket.pdf");
            using var t80 = PdfReader.Open(new MemoryStream(statementTicket.Value.Content), PdfDocumentOpenMode.Import);
            t80.PageCount.Should().Be(1);
            t80.Pages[0].Width.Point.Should().BeApproximately(226.8, 0.5);
        }

        var creditDoc = await Print(PrintableKinds.CustomerCredit, sale.ReceivableId!.Value, "xlsx");
        Figure(creditDoc, "Reste à payer").Should().Be(600);
        await Print(PrintableKinds.CustomerCredit, sale.ReceivableId!.Value, "pdf");

        var ticket = await Print(PrintableKinds.Sale, sale.SaleId, "xlsx");
        Figure(ticket, "Total").Should().Be(1000);
        Figure(ticket, "Reste à verser (compte client)").Should().Be(1000, "situation au moment de la vente");
        var pdfTicket = await Print(PrintableKinds.Sale, sale.SaleId, "ticket");
        using var t = PdfReader.Open(new MemoryStream(pdfTicket.Content), PdfDocumentOpenMode.Import);
        t.PageCount.Should().Be(1);
    }

    // ---- Analyse 2026-10 §6.2 : la réimpression d'un ticket de vente exige pos.reprint ----
    [Fact]
    public async Task Reprinting_a_sale_ticket_requires_the_reprint_permission()
    {
        var sale = await CreditSale((await NewCustomer()).Id, await Product(100m), 1m);
        var cashier = await UserWithRole("cashier");     // pos.view + pos.reprint
        var auditor = await UserWithRole("auditor");     // pos.view, sans pos.reprint

        (await Print(PrintableKinds.Sale, sale.SaleId, "ticket", auditor)).Content.Should().NotBeEmpty("première impression : original");

        await using var s = Scope();
        As(s.ServiceProvider, auditor);
        (await s.ServiceProvider.GetRequiredService<IReceiptService>().PrintAsync(PrintableKinds.Sale, sale.SaleId, "ticket"))
            .Error!.Code.Should().Be("print.reprint_forbidden");

        var copy = await Print(PrintableKinds.Sale, sale.SaleId, "xlsx", cashier);
        Contains(copy, "DUPLICATA").Should().BeTrue("le caissier réimprime, et la copie le dit");
    }

    // ---- Droits : l'encaisseur n'imprime que SES versements ; le portefeuille reste protégé ----
    [Fact]
    public async Task A_cashier_prints_only_the_receipts_of_payments_they_took()
    {
        var customer = await NewCustomer();
        var sale = await CreditSale(customer.Id, await Product(100m), 5m);
        var cashier = await UserWithRole("senior_cashier");       // credit.receive_payment, pas credit.view_portfolio
        var mine = await Pay(sale.ReceivableId!.Value, 100m, cashier);
        var other = await Pay(sale.ReceivableId!.Value, 100m);

        (await Print(PrintableKinds.CustomerPayment, mine.Id, "ticket", cashier)).Content.Should().NotBeEmpty();

        await using var s = Scope();
        As(s.ServiceProvider, cashier);
        var receipts = s.ServiceProvider.GetRequiredService<IReceiptService>();
        (await receipts.PrintAsync(PrintableKinds.CustomerPayment, other.Id, "pdf")).Error!.Code.Should().Be("print.not_found");
        (await receipts.PrintAsync(PrintableKinds.CustomerCredit, sale.ReceivableId!.Value, "pdf")).Error!.Code.Should().Be("print.forbidden");
        (await receipts.CustomerStatementAsync(customer.Id, null, null, null, "pdf")).Error!.Code.Should().Be("print.forbidden");
        (await receipts.PrintAsync(PrintableKinds.SupplierPayment, Guid.NewGuid(), "pdf")).Error!.Code.Should().Be("print.forbidden");
        (await receipts.SupplierDocumentsAsync(Supplier1)).Should().BeEmpty("aucune pièce fournisseur sans droit achats");
        (await receipts.PrintAsync(PrintableKinds.CustomerPayment, mine.Id, "docx")).Error!.Code.Should().Be("print.format");
        (await receipts.PrintAsync("bon-inconnu", mine.Id, "pdf")).Error!.Code.Should().Be("print.unknown_kind");
    }

    // ---- Fournisseur : bon de commande, réception, facture, paiement (reste à payer), relevé ----
    [Fact]
    public async Task Supplier_documents_print_with_the_remaining_balance()
    {
        await using var s = Scope();
        As(s.ServiceProvider, Direction);
        var sp = s.ServiceProvider;
        var catalog = sp.GetRequiredService<CatalogService>();
        var product = (await catalog.CreateProductAsync(new CreateProductCommand(
            $"PRF-{Guid.NewGuid():N}"[..20], "Achat reçu", null, BoxUnit, "stockable", null, null, null, null, false, false))).Value;
        var po = sp.GetRequiredService<PurchasingService>();
        var order = (await po.CreateOrderAsync(new CreatePurchaseOrderCommand(
            Supplier1, null, "HTG", null, [new CreatePurchaseOrderLine(product, 10m, 500m, null, 0m)], null))).Value;
        await po.ApproveOrderAsync(order);
        var receipt = (await po.ReceiveAsync(new ReceiveGoodsCommand(Guid.NewGuid(), Supplier1, Officine, order,
            [new ReceiveLine(product, null, 10m, 500m, null, null)], null, null))).Value;
        var invoice = (await po.RecordInvoiceAsync(new RecordPurchaseInvoiceCommand(
            Guid.NewGuid(), Supplier1, order, "HTG", TestDates.Today, DateOnly.FromDateTime(DateTime.UtcNow.AddDays(30)),
            OnCredit: true, null, [new InvoiceLine(product, "Achat reçu", 10m, 500m, null, 0m, 0m)], true,
            SupplierReference: $"FA-{Guid.NewGuid():N}"[..14]))).Value!;
        var paid = await po.PayPayableAsync(invoice.PayableId!.Value, new PayPayableRequest(Guid.NewGuid(), 2_000m, CashAccount, "CHQ-77"));
        paid.IsSuccess.Should().BeTrue(paid.Error?.Message);

        var payment = await Print(PrintableKinds.SupplierPayment, paid.Value!.PaymentId!.Value, "xlsx");
        Figure(payment, "Montant de ce paiement").Should().Be(2_000);
        Contains(payment, "Factures réglées").Should().BeTrue();
        using (var wb = new XLWorkbook(new MemoryStream(payment.Content)))
        {
            var ws = wb.Worksheets.First();
            var rest = ws.CellsUsed().Single(c => c.GetString() == "Reste dû (HTG)");
            ws.Cell(rest.Address.RowNumber + 1, rest.Address.ColumnNumber).GetDouble().Should().Be(3_000, "5 000 − 2 000 sur cette facture");
        }
        Figure(payment, "Reste à payer au fournisseur").Should().BeGreaterThanOrEqualTo(3_000);
        await Print(PrintableKinds.SupplierPayment, paid.Value!.PaymentId!.Value, "pdf");

        Figure(await Print(PrintableKinds.SupplierInvoice, invoice.InvoiceId, "xlsx"), "Reste à payer").Should().Be(3_000);
        await Print(PrintableKinds.SupplierInvoice, invoice.InvoiceId, "pdf");
        Figure(await Print(PrintableKinds.PurchaseOrder, order, "xlsx"), "Total de la commande").Should().Be(5_000);
        await Print(PrintableKinds.PurchaseOrder, order, "pdf");
        Figure(await Print(PrintableKinds.GoodsReceipt, receipt, "xlsx"), "Valeur de la réception").Should().Be(5_000);
        await Print(PrintableKinds.GoodsReceipt, receipt, "pdf");

        var docs = await sp.GetRequiredService<IReceiptService>().SupplierDocumentsAsync(Supplier1);
        docs.Select(d => d.Kind).Should().Contain(["supplier-payment", "goods-receipt", "supplier-invoice", "purchase-order"]);
        docs.Should().Contain(d => d.Kind == "supplier-payment" && d.Id == paid.Value!.PaymentId!.Value);

        var statement = await sp.GetRequiredService<IReceiptService>().SupplierStatementAsync(Supplier1, null, null, "xlsx");
        statement.IsSuccess.Should().BeTrue(statement.Error?.Message);
        Keep(statement.Value!);
        Contains(statement.Value!, "Reste à payer").Should().BeTrue();
    }

    // ---- Rapports de pilotage : vrai Excel et vrai PDF, journalisés ----
    [Fact]
    public async Task Reports_export_to_real_excel_and_pdf()
    {
        await CreditSale((await NewCustomer()).Id, await Product(80m), 2m);
        await using var s = Scope();
        As(s.ServiceProvider, Direction);
        var export = s.ServiceProvider.GetRequiredService<IExportService>();

        var xlsx = await export.ExportAsync(new ExportRequest("sales_journal", "xlsx", new ReportFilter { Preset = "today" }));
        xlsx.ContentType.Should().Be("application/vnd.openxmlformats-officedocument.spreadsheetml.sheet");
        xlsx.FileName.Should().EndWith(".xlsx");
        using (var wb = new XLWorkbook(new MemoryStream(xlsx.Content)))
        {
            var ws = wb.Worksheets.First();
            var total = ws.CellsUsed().Single(c => c.GetString() == "Total");
            ws.Cell(total.Address.RowNumber + 1, total.Address.ColumnNumber).DataType.Should().Be(XLDataType.Number);
        }

        var pdf = await export.ExportAsync(new ExportRequest("aging_receivables", "pdf", new ReportFilter()));
        Encoding.ASCII.GetString(pdf.Content, 0, 5).Should().Be("%PDF-");
        (await Scalar("SELECT count(*) FROM nexora.export_log WHERE report_key = 'aging_receivables' AND format = 'pdf'"))
            .Should().BeGreaterThan(0);
    }
}
