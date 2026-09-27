using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace Nexora.ApiTests;

/// <summary>
/// Module « Gestion clients & comptes clients » — §18-19 (permissions du caissier), §23 (IDOR/BOLA :
/// l'identifiant d'une autre entreprise répond 404), §26 (export). Lecture seule sur la base de
/// développement (règle A3) : seuls des refus et des lectures sont exercés ici, les écritures sont
/// prouvées par <c>CustomerAccountTests</c> sur une base clonée.
/// </summary>
public sealed class CustomerAccountEndpointsTests(WebApplicationFactory<Program> factory)
    : IClassFixture<WebApplicationFactory<Program>>
{
    private const string DevConnString =
        "Host=localhost;Port=5432;Database=nexora;Username=nexora_app;Password=nexora_app_dev;Search Path=nexora,public;Include Error Detail=true";
    private const string Pharmacy = "dce00000-0000-0000-0000-0000000000c1";
    private const string Restaurant = "dce00000-0000-0000-0000-0000000000c2";
    private const string DemoCustomer = "dce00000-0000-0000-0000-00000000b001";
    private const string Unknown = "00000000-0000-0000-0000-000000000001";

    private HttpClient Client() => factory.WithWebHostBuilder(b =>
        b.ConfigureAppConfiguration((_, cfg) => cfg.AddInMemoryCollection(
            new Dictionary<string, string?> { ["Database:ConnectionString"] = DevConnString }))).CreateClient();

    // §18 « Voir détail financier global : caissier non » ; « Annuler versement : non » ; « Valider dépense : non » ;
    // « Rapports créances : non » ; « Enregistrer versement : si autorisé » (pas accordé par défaut).
    [Theory]
    [InlineData("GET", $"/api/v1/customers/{DemoCustomer}/account")]
    [InlineData("GET", $"/api/v1/customers/{DemoCustomer}/ledger")]
    [InlineData("GET", $"/api/v1/customers/{DemoCustomer}/credits")]
    [InlineData("GET", $"/api/v1/customers/{DemoCustomer}/payments")]
    [InlineData("GET", $"/api/v1/customers/{DemoCustomer}/timeline")]
    [InlineData("GET", $"/api/v1/customers/{DemoCustomer}/purchases")]
    [InlineData("GET", "/api/v1/credits")]
    [InlineData("GET", "/api/v1/collections")]
    [InlineData("GET", "/api/v1/customer-reports/balances")]
    [InlineData("GET", "/api/v1/customer-reports/balances/export")]
    [InlineData("POST", $"/api/v1/customers/{DemoCustomer}/payments")]
    [InlineData("POST", $"/api/v1/credits/{Unknown}/payments")]
    [InlineData("POST", $"/api/v1/payments/{Unknown}/void")]
    [InlineData("POST", $"/api/v1/customer-expenses/{Unknown}/approve")]
    [InlineData("POST", $"/api/v1/customer-refunds/{Unknown}/approve")]
    [InlineData("POST", $"/api/v1/credit-memos/{Unknown}/apply")]
    [InlineData("PUT", $"/api/v1/customers/{DemoCustomer}")]
    [InlineData("POST", $"/api/v1/customers/{DemoCustomer}/credits")]
    [InlineData("POST", $"/api/v1/credits/{Unknown}/installments")]
    [InlineData("GET", "/api/v1/pos/credit-override-requests")]
    [InlineData("POST", $"/api/v1/pos/credit-override-requests/{Unknown}/approve")]
    public async Task Cashier_is_refused_financial_detail_and_sensitive_customer_operations(string method, string url)
        => (await Send(await Login("caisse@abc-group.demo", Pharmacy), method, url)).StatusCode.Should().Be(HttpStatusCode.Forbidden, url);

    // §17 : la recherche POS reste ouverte au caissier, bornée et sans champ superflu.
    [Fact]
    public async Task Cashier_can_search_customers_at_the_pos_with_minimal_fields()
    {
        var cashier = await Login("caisse@abc-group.demo", Pharmacy);
        var res = await cashier.GetAsync("/api/v1/customers/lookup?q=cl");
        res.StatusCode.Should().Be(HttpStatusCode.OK);
        var json = await res.Content.ReadAsStringAsync();
        json.Should().NotContain("\"email\"").And.NotContain("\"address\"").And.NotContain("creditLimit");
        (await cashier.GetFromJsonAsync<List<object>>("/api/v1/customers/lookup?q=x")).Should().BeEmpty("2 caractères minimum");
    }

    // §23 : un identifiant valide dans l'entreprise A n'existe pas vu de l'entreprise B (404, jamais la donnée).
    [Fact]
    public async Task Customer_account_of_one_company_is_not_found_from_another_companys_scope()
    {
        var pharmacy = await Login("direction@abc-group.demo", Pharmacy);
        var own = await pharmacy.GetAsync($"/api/v1/customers/{DemoCustomer}/account");
        own.StatusCode.Should().Be(HttpStatusCode.OK);
        (await own.Content.ReadFromJsonAsync<SummaryShape>())!.LedgerConsistent.Should().BeTrue();

        var restaurant = await Login("direction@abc-group.demo", Restaurant);
        foreach (var url in new[]
                 {
                     $"/api/v1/customers/{DemoCustomer}", $"/api/v1/customers/{DemoCustomer}/account",
                     $"/api/v1/customers/{DemoCustomer}/ledger",
                 })
            (await restaurant.GetAsync(url)).StatusCode.Should().Be(HttpStatusCode.NotFound, url);
        (await restaurant.GetFromJsonAsync<List<object>>($"/api/v1/customers/{DemoCustomer}/credits")).Should().BeEmpty();
    }

    // §22-23 : clé d'idempotence obligatoire ; versement inconnu → 404 (aucune écriture) ; motif absent → 422.
    [Fact]
    public async Task Voiding_an_unknown_payment_is_not_found_and_a_missing_reason_is_rejected()
    {
        var direction = await Login("direction@abc-group.demo", Pharmacy);
        (await direction.PostAsJsonAsync($"/api/v1/payments/{Unknown}/void", new { reason = "Contrôle d'accès" }))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest, "§23 : opération sensible sans clé d'idempotence refusée");
        direction.DefaultRequestHeaders.Add("Idempotency-Key", Guid.NewGuid().ToString());
        (await direction.PostAsJsonAsync($"/api/v1/payments/{Unknown}/void", new { reason = "Contrôle d'accès" }))
            .StatusCode.Should().Be(HttpStatusCode.NotFound);
        direction.DefaultRequestHeaders.Remove("Idempotency-Key");
        direction.DefaultRequestHeaders.Add("Idempotency-Key", Guid.NewGuid().ToString());
        (await direction.PostAsJsonAsync($"/api/v1/payments/{Unknown}/void", new { reason = "" }))
            .StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
    }

    // §26 : export CSV (UTF-8 BOM, « ; ») réservé à customers.export.
    [Fact]
    public async Task Customer_reports_run_and_export_as_csv()
    {
        var direction = await Login("direction@abc-group.demo", Pharmacy);
        var catalog = await direction.GetFromJsonAsync<List<ReportShape>>("/api/v1/customer-reports");
        catalog!.Select(r => r.Key).Should().Contain(["balances", "aging", "payments", "expenses", "over_limit", "ledger"]);

        var export = await direction.GetAsync("/api/v1/customer-reports/aging/export");
        export.StatusCode.Should().Be(HttpStatusCode.OK);
        export.Content.Headers.ContentType!.MediaType.Should().Be("text/csv");
        var bytes = await export.Content.ReadAsByteArrayAsync();
        bytes.Take(3).Should().Equal(0xEF, 0xBB, 0xBF);
        System.Text.Encoding.UTF8.GetString(bytes[3..]).Should().StartWith("Code client;Client;Devise;À échoir");
    }

    private static Task<HttpResponseMessage> Send(HttpClient client, string method, string url) => method switch
    {
        "GET" => client.GetAsync(url),
        "PUT" => client.PutAsJsonAsync(url, new { name = "x" }),
        _ => client.PostAsJsonAsync(url, new { }),
    };

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
    private sealed record SummaryShape(bool LedgerConsistent);
    private sealed record ReportShape(string Key);
}
