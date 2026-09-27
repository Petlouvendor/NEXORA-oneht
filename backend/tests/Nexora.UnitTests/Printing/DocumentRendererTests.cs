using System.Text;
using System.Text.Json;
using ClosedXML.Excel;
using FluentAssertions;
using Nexora.Application.Printing;
using Nexora.Contracts.Printing;
using Nexora.Infrastructure.Printing;
using PdfSharp.Pdf.IO;
using Xunit;

namespace Nexora.UnitTests.Printing;

/// <summary>Impression & export : les fichiers produits sont de vrais .xlsx et PDF, relus ici.</summary>
public sealed class DocumentRendererTests
{
    private static readonly PrintIssuer Issuer = new("Pharmacie ABC", "Pharmacie ABC S.A.", "000-123-456", "12, rue Capois, Port-au-Prince",
        "+509 2222 3333", "Merci de votre confiance.", null, "America/Port-au-Prince");
    private static readonly PrintMeta Meta = new(new DateTimeOffset(2026, 9, 24, 15, 30, 0, TimeSpan.Zero), "Caissier Test");
    private readonly DocumentRenderer _renderer = new();

    private static PrintDocument Receipt(int rows = 2) => new(
        Title: "Reçu de versement",
        Reference: "PAY-2026-000123",
        Parties: [new PrintParty("Client", ["Restaurant Le Bon Goût", "Code : CLI-00002"])],
        Details: [new PrintFigure("Date", new DateTimeOffset(2026, 9, 24, 14, 5, 0, TimeSpan.Zero), PrintKinds.DateTime)],
        Tables:
        [
            new PrintTable("Crédits réglés",
                [new("Crédit"), new("Montant du crédit", PrintKinds.Money, "HTG"), new("Échéance", PrintKinds.Date), new("Qté", PrintKinds.Integer)],
                Enumerable.Range(1, rows).Select(i => (IReadOnlyList<object?>)[$"CRD-2026-{i:000000}", 4246.5m + i, new DateOnly(2026, 10, 15), i]).ToList(),
                [null, 9000m, null, null]),
        ],
        Summary:
        [
            new PrintFigure("Total versé", 58480m, PrintKinds.Money, "HTG"),
            new PrintFigure("Reste à verser", 8996m, PrintKinds.Money, "HTG", Emphasis: true),
        ],
        Signatures: ["Signature du client", "Le caissier"]);

    [Fact]
    public void Xlsx_is_a_real_workbook_with_typed_cells_frozen_header_and_filter()
    {
        var file = _renderer.Render(Receipt(), Issuer, Meta, "xlsx", "Reçu de versement PAY-2026-000123");

        file.ContentType.Should().Be("application/vnd.openxmlformats-officedocument.spreadsheetml.sheet");
        file.FileName.Should().Be("recu-de-versement-pay-2026-000123.xlsx");
        Encoding.ASCII.GetString(file.Content, 0, 2).Should().Be("PK", "un .xlsx est une archive Open XML");

        using var wb = new XLWorkbook(new MemoryStream(file.Content));
        var ws = wb.Worksheets.Single();
        var header = ws.CellsUsed().Single(c => c.GetString() == "Montant du crédit (HTG)");
        var amount = ws.Cell(header.Address.RowNumber + 1, header.Address.ColumnNumber);
        amount.DataType.Should().Be(XLDataType.Number, "un montant reste un nombre dans Excel");
        amount.GetDouble().Should().Be(4247.5);
        amount.Style.NumberFormat.Format.Should().Be("#,##0.00");
        ws.Cell(header.Address.RowNumber + 1, header.Address.ColumnNumber + 1).DataType.Should().Be(XLDataType.DateTime);
        ws.SheetView.SplitRow.Should().Be(header.Address.RowNumber, "la ligne d'en-tête est figée");
        ws.AutoFilter.IsEnabled.Should().BeTrue();

        var rest = ws.CellsUsed().Single(c => c.GetString() == "Reste à verser");
        ws.Cell(rest.Address.RowNumber, 2).GetDouble().Should().Be(8996);
        ws.CellsUsed().Should().Contain(c => c.GetString() == "Pharmacie ABC");
    }

    [Fact]
    public void Xlsx_puts_each_table_on_its_own_sheet_with_a_summary_sheet()
    {
        var doc = Receipt() with
        {
            Tables = [Receipt().Tables![0], new PrintTable("Échéancier", [new("N°", PrintKinds.Integer)], [[1], [2]])],
        };
        using var wb = new XLWorkbook(new MemoryStream(_renderer.Render(doc, Issuer, Meta, "xlsx", "x").Content));
        wb.Worksheets.Select(w => w.Name).Should().Equal("Synthèse", "Crédits réglés", "Échéancier");
    }

    [Fact]
    public void Pdf_is_a_real_pdf_and_long_tables_flow_on_several_pages()
    {
        var small = _renderer.Render(Receipt(), Issuer, Meta, "pdf", "recu");
        small.ContentType.Should().Be("application/pdf");
        Encoding.ASCII.GetString(small.Content, 0, 5).Should().Be("%PDF-");
        using (var pdf = PdfReader.Open(new MemoryStream(small.Content), PdfDocumentOpenMode.Import))
            pdf.PageCount.Should().Be(1);

        var big = _renderer.Render(Receipt(rows: 180), Issuer, Meta, "pdf", "releve");
        using var many = PdfReader.Open(new MemoryStream(big.Content), PdfDocumentOpenMode.Import);
        many.PageCount.Should().BeGreaterThan(1);
    }

    [Fact]
    public void Ticket_is_an_80_mm_wide_pdf()
    {
        var file = _renderer.Render(Receipt(), Issuer, Meta, "ticket", "recu");
        file.FileName.Should().EndWith("-ticket.pdf");
        using var pdf = PdfReader.Open(new MemoryStream(file.Content), PdfDocumentOpenMode.Import);
        pdf.Pages[0].Width.Point.Should().BeApproximately(80 / 25.4 * 72, 0.5);
        pdf.Pages[0].Height.Point.Should().BeLessThan(842, "le ticket ne débite pas une page A4");
    }

    [Fact]
    public void Wide_tables_switch_the_pdf_to_landscape()
    {
        var cols = Enumerable.Range(1, 9).Select(i => new PrintColumn($"C{i}", PrintKinds.Number)).ToList();
        var doc = new PrintDocument("Balance", Tables: [new PrintTable(null, cols, [cols.Select(_ => (object?)1m).ToList()])]);
        using var pdf = PdfReader.Open(new MemoryStream(_renderer.Render(doc, Issuer, Meta, "pdf", "b").Content), PdfDocumentOpenMode.Import);
        pdf.Pages[0].Width.Point.Should().BeGreaterThan(pdf.Pages[0].Height.Point);
    }

    [Fact]
    public void Csv_has_a_bom_semicolons_invariant_numbers_and_neutralizes_formulas()
    {
        var doc = new PrintDocument("t", Tables:
        [
            new PrintTable(null, [new("Libellé"), new("Montant", PrintKinds.Money), new("Date", PrintKinds.Date)],
                [["=HYPERLINK(\"x\")", 1234.5m, new DateOnly(2026, 9, 1)], ["a;b", -3m, null]]),
        ]);
        var bytes = _renderer.Render(doc, Issuer, Meta, "csv", "t").Content;
        bytes.Take(3).Should().Equal(Encoding.UTF8.GetPreamble());
        var lines = Encoding.UTF8.GetString(bytes[3..]).Split(Environment.NewLine);
        lines[0].Should().Be("Libellé;Montant;Date");
        lines[1].Should().Be("\"'=HYPERLINK(\"\"x\"\")\";1234.50;2026-09-01");
        lines[2].Should().Be("\"a;b\";-3.00;");
    }

    [Fact]
    public void A_screen_export_sent_as_json_keeps_its_numbers_typed()
    {
        const string json = """
            {"screenKey":"credits","document":{"title":"Crédits","tables":[{"title":null,
             "columns":[{"label":"Crédit","kind":"text"},{"label":"Reste dû","kind":"money","currency":"HTG"},{"label":"Émis le","kind":"date"}],
             "rows":[["CRD-1", 4246.25, "2026-09-15"],["CRD-2", null, null]]}]}}
            """;
        var request = JsonSerializer.Deserialize<ScreenExportRequest>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        using var wb = new XLWorkbook(new MemoryStream(_renderer.Render(request.Document, Issuer, Meta, "xlsx", "c").Content));
        var ws = wb.Worksheets.Single();
        var header = ws.CellsUsed().Single(c => c.GetString() == "Reste dû (HTG)");
        ws.Cell(header.Address.RowNumber + 1, header.Address.ColumnNumber).GetDouble().Should().Be(4246.25);
        ws.Cell(header.Address.RowNumber + 1, header.Address.ColumnNumber + 1).GetDateTime().Should().Be(new DateTime(2026, 9, 15));
    }

    [Fact]
    public void Unknown_formats_are_refused()
    {
        var act = () => _renderer.Render(Receipt(), Issuer, Meta, "docx", "x");
        act.Should().Throw<ArgumentOutOfRangeException>();
        PrintFormats.Normalize("Excel").Should().Be("xlsx");
        PrintFormats.Normalize(null).Should().Be("pdf");
    }

    [Fact]
    public void String_reports_are_typed_by_inference_but_identifiers_stay_text()
    {
        var t = PrintTableInference.Build(null, ["Code client", "Téléphone", "Montant", "Retard (j)", "Émis le", "Statut"],
            [["00123", "50934567890", "1250.50", "12", "2026-09-01", "open"], ["00124", null, "10.00", "0", "2026-09-02", "paid"]]);
        t.Columns.Select(c => c.Kind).Should().Equal("text", "text", "money", "integer", "date", "text");
        t.Rows[0][2].Should().Be(1250.50m);
        t.Rows[0][0].Should().Be("00123");
    }

    [Fact]
    public void Print_access_distinguishes_full_own_and_none()
    {
        PrintAccess.Level(PrintableKinds.CustomerPayment, p => p == "credit.view_portfolio").Should().Be("full");
        PrintAccess.Level(PrintableKinds.CustomerPayment, p => p == "credit.receive_payment").Should().Be("own");
        PrintAccess.Level(PrintableKinds.CustomerPayment, p => p == "pos.sell").Should().Be("none");
        PrintAccess.Level(PrintableKinds.SupplierPayment, p => p == "purchasing.pay").Should().Be("full");
        PrintAccess.Level("inconnu", _ => true).Should().Be("none");
    }
}
