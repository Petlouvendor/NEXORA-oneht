using System.Net;
using System.Net.Http.Json;
using System.Text;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace Nexora.ApiTests;

/// <summary>
/// Impression des reçus et export des écrans / rapports : vrais fichiers (signature %PDF / archive PK),
/// droits par type de document, 404 hors périmètre. Base de développement : seules des lectures sont
/// exercées — une impression réussie n'écrit qu'une ligne du journal des exports (export_log).
/// </summary>
public sealed class PrintEndpointsTests(WebApplicationFactory<Program> factory)
    : IClassFixture<WebApplicationFactory<Program>>
{
    private const string DevConnString =
        "Host=localhost;Port=5432;Database=nexora;Username=nexora_app;Password=nexora_app_dev;Search Path=nexora,public;Include Error Detail=true";
    private const string Pharmacy = "dce00000-0000-0000-0000-0000000000c1";
    private const string Restaurant = "dce00000-0000-0000-0000-0000000000c2";
    private const string DemoCustomer = "dce00000-0000-0000-0000-00000000b001";
    private const string DemoSupplier = "dce00000-0000-0000-0000-00000000b101";
    private const string Unknown = "00000000-0000-0000-0000-000000000001";
    private const string Xlsx = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";

    private HttpClient Client() => factory.WithWebHostBuilder(b =>
        b.ConfigureAppConfiguration((_, cfg) => cfg.AddInMemoryCollection(
            new Dictionary<string, string?> { ["Database:ConnectionString"] = DevConnString }))).CreateClient();

    [Fact]
    public async Task Customer_statement_downloads_as_a_real_pdf_and_a_real_workbook()
    {
        var direction = await Login("direction@abc-group.demo", Pharmacy);

        var pdf = await direction.GetAsync($"/api/v1/print/customers/{DemoCustomer}/statement");
        pdf.StatusCode.Should().Be(HttpStatusCode.OK);
        pdf.Content.Headers.ContentType!.MediaType.Should().Be("application/pdf");
        Encoding.ASCII.GetString((await pdf.Content.ReadAsByteArrayAsync())[..5]).Should().Be("%PDF-");

        var xlsx = await direction.GetAsync($"/api/v1/print/customers/{DemoCustomer}/statement?format=xlsx");
        xlsx.Content.Headers.ContentType!.MediaType.Should().Be(Xlsx);
        xlsx.Content.Headers.ContentDisposition!.FileName.Should().EndWith(".xlsx");
        Encoding.ASCII.GetString((await xlsx.Content.ReadAsByteArrayAsync())[..2]).Should().Be("PK");

        (await direction.GetAsync($"/api/v1/print/suppliers/{DemoSupplier}/statement?format=pdf"))
            .Content.Headers.ContentType!.MediaType.Should().Be("application/pdf");

        // L'application web reçoit le fichier dans une enveloppe JSON : un navigateur ou un gestionnaire de
        // téléchargements qui intercepte les réponses PDF laisserait sinon un fichier de 0 octet.
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/api/v1/print/customers/{DemoCustomer}/statement?format=pdf");
        request.Headers.Accept.ParseAdd("application/vnd.nexora.file+json");
        var wrapped = await direction.SendAsync(request);
        wrapped.StatusCode.Should().Be(HttpStatusCode.OK);
        var envelope = (await wrapped.Content.ReadFromJsonAsync<EnvelopeShape>())!;
        envelope.FileName.Should().EndWith(".pdf");
        envelope.ContentType.Should().Be("application/pdf");
        var bytes = Convert.FromBase64String(envelope.Base64);
        bytes.Length.Should().Be(envelope.Size).And.BeGreaterThan(1000);
        Encoding.ASCII.GetString(bytes[..5]).Should().Be("%PDF-");
    }

    [Theory]
    [InlineData($"/api/v1/print/customers/{DemoCustomer}/statement")]
    [InlineData($"/api/v1/print/suppliers/{DemoSupplier}/statement")]
    [InlineData($"/api/v1/print/customer-credit/{Unknown}")]
    [InlineData($"/api/v1/print/supplier-payment/{Unknown}")]
    [InlineData($"/api/v1/print/purchase-order/{Unknown}")]
    [InlineData($"/api/v1/print/customer-payment/{Unknown}")]
    public async Task Cashier_cannot_print_financial_documents(string url)
        => (await (await Login("caisse@abc-group.demo", Pharmacy)).GetAsync(url)).StatusCode.Should().Be(HttpStatusCode.Forbidden, url);

    [Fact]
    public async Task Unknown_or_foreign_documents_are_not_found_and_bad_formats_rejected()
    {
        var direction = await Login("direction@abc-group.demo", Pharmacy);
        (await direction.GetAsync($"/api/v1/print/customer-payment/{Unknown}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await direction.GetAsync($"/api/v1/print/bon-inconnu/{Unknown}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await direction.GetAsync($"/api/v1/print/customers/{DemoCustomer}/statement?format=docx"))
            .StatusCode.Should().BeOneOf(HttpStatusCode.BadRequest, HttpStatusCode.UnprocessableEntity);

        var restaurant = await Login("direction@abc-group.demo", Restaurant);
        (await restaurant.GetAsync($"/api/v1/print/customers/{DemoCustomer}/statement"))
            .StatusCode.Should().Be(HttpStatusCode.NotFound, "§23 : le client d'une autre entreprise n'existe pas");
    }

    [Fact]
    public async Task Any_displayed_table_exports_to_excel_or_pdf_but_not_for_a_cashier()
    {
        var body = new
        {
            screenKey = "credits",
            document = new
            {
                title = "Crédits clients",
                subtitle = "Filtre : ouverts",
                tables = new[]
                {
                    new
                    {
                        title = (string?)null,
                        columns = new object[] { new { label = "Crédit", kind = "text" }, new { label = "Reste dû", kind = "money", currency = "HTG" } },
                        rows = new[] { new object?[] { "CRD-2026-000001", 4246.5m } },
                    },
                },
            },
        };
        var direction = await Login("direction@abc-group.demo", Pharmacy);
        var xlsx = await direction.PostAsJsonAsync("/api/v1/exports/screen?format=xlsx", body);
        xlsx.StatusCode.Should().Be(HttpStatusCode.OK);
        xlsx.Content.Headers.ContentType!.MediaType.Should().Be(Xlsx);
        Encoding.ASCII.GetString((await xlsx.Content.ReadAsByteArrayAsync())[..2]).Should().Be("PK");

        var pdf = await direction.PostAsJsonAsync("/api/v1/exports/screen?format=pdf", body);
        Encoding.ASCII.GetString((await pdf.Content.ReadAsByteArrayAsync())[..5]).Should().Be("%PDF-");

        (await direction.PostAsJsonAsync("/api/v1/exports/screen?format=docx", body))
            .StatusCode.Should().BeOneOf(HttpStatusCode.BadRequest, HttpStatusCode.UnprocessableEntity);
        (await direction.PostAsJsonAsync("/api/v1/exports/screen?format=xlsx", body with { screenKey = "../../etc" }))
            .StatusCode.Should().BeOneOf(HttpStatusCode.BadRequest, HttpStatusCode.UnprocessableEntity);

        (await (await Login("caisse@abc-group.demo", Pharmacy)).PostAsJsonAsync("/api/v1/exports/screen?format=xlsx", body))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden, "l'export exige reporting.export");
    }

    [Fact]
    public async Task Pilotage_and_customer_reports_export_as_real_excel()
    {
        var direction = await Login("direction@abc-group.demo", Pharmacy);
        var report = await direction.PostAsJsonAsync("/api/v1/reports/export",
            new { reportKey = "aging_receivables", format = "xlsx", filter = new { } });
        report.StatusCode.Should().Be(HttpStatusCode.OK);
        report.Content.Headers.ContentType!.MediaType.Should().Be(Xlsx);

        var customers = await direction.GetAsync("/api/v1/customer-reports/aging/export?format=xlsx");
        customers.Content.Headers.ContentType!.MediaType.Should().Be(Xlsx);
        Encoding.ASCII.GetString((await customers.Content.ReadAsByteArrayAsync())[..2]).Should().Be("PK");
        (await direction.GetAsync("/api/v1/customer-reports/aging/export?format=pdf"))
            .Content.Headers.ContentType!.MediaType.Should().Be("application/pdf");
    }

    // Paiement d'une dette : les comptes proposés à l'écran Achats, sans aucun solde de trésorerie.
    [Fact]
    public async Task Supplier_payment_accounts_are_listed_without_balance_and_refused_to_the_cashier()
    {
        var direction = await Login("direction@abc-group.demo", Pharmacy);
        var res = await direction.GetAsync("/api/v1/payables/payment-accounts");
        res.StatusCode.Should().Be(HttpStatusCode.OK);
        var json = await res.Content.ReadAsStringAsync();
        json.Should().Contain("\"currency\"").And.NotContain("balance", "aucun solde de trésorerie n'est exposé");
        (await (await Login("caisse@abc-group.demo", Pharmacy)).GetAsync("/api/v1/payables/payment-accounts"))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    private async Task<HttpClient> Login(string email, string company)
    {
        var client = Client();
        var login = await client.PostAsJsonAsync("/api/v1/auth/login", new { email, password = "Nexora!2026", companyId = company });
        login.StatusCode.Should().Be(HttpStatusCode.OK, $"connexion {email}");
        var token = (await login.Content.ReadFromJsonAsync<TokenShape>())!.AccessToken;
        client.DefaultRequestHeaders.Add("Authorization", $"Bearer {token}");
        client.DefaultRequestHeaders.Add("X-Company-Id", company);
        return client;
    }

    private sealed record TokenShape(string AccessToken);
    private sealed record EnvelopeShape(string FileName, string ContentType, int Size, string Base64);
}
