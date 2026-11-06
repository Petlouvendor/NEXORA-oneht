using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace Nexora.ApiTests;

/// <summary>
/// Phase 49 §32, §57 — isolation multi-tenant : preuve fonctionnelle bout-en-bout, avec le même
/// utilisateur et le même jeton, en ne changeant que l'entreprise active (<c>X-Company-Id</c>).
/// C'est le test le plus strict possible : si une fuite existe, elle vient du filtrage RLS/SQL lui
/// même, jamais d'une différence d'autorisation entre deux comptes distincts. Entreprises démo
/// ABC GROUP : c1 = Pharmacie (produit PARA500 amorcé), c2 = Restaurant — sans recouvrement de SKU.
/// </summary>
public sealed class MultiTenantIsolationTests(WebApplicationFactory<Program> factory)
    : IClassFixture<WebApplicationFactory<Program>>
{
    private static readonly string DevConnString = ApiTestDatabase.App;
    private const string Pharmacy = "dce00000-0000-0000-0000-0000000000c1";
    private const string Restaurant = "dce00000-0000-0000-0000-0000000000c2";

    private HttpClient Client() => factory.WithWebHostBuilder(b =>
        b.ConfigureAppConfiguration((_, cfg) => cfg.AddInMemoryCollection(
            new Dictionary<string, string?> { ["Database:ConnectionString"] = DevConnString }))).CreateClient();

    // ---- §32 : « API A ne peut pas lire B » — un id valide dans l'entreprise A n'existe pas en B ----
    [Fact]
    public async Task A_product_readable_in_its_own_company_returns_not_found_under_another_companys_scope()
    {
        var pharmacy = await AuthedClient(Pharmacy);
        var listed = await pharmacy.GetFromJsonAsync<List<ProductShape>>("/api/v1/catalog/products?q=PARA500");
        var product = listed!.Should().ContainSingle(p => p.Sku == "PARA500").Which;

        (await pharmacy.GetAsync($"/api/v1/catalog/products/{product.Id}")).StatusCode.Should().Be(HttpStatusCode.OK);

        var restaurant = await AuthedClient(Restaurant);
        (await restaurant.GetAsync($"/api/v1/catalog/products/{product.Id}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ---- §32 : « Utilisateur A ne voit jamais B » — la liste ne fuit jamais un SKU d'une autre entreprise ----
    [Fact]
    public async Task Product_list_scoped_to_one_company_never_contains_another_companys_sku()
    {
        var restaurant = await AuthedClient(Restaurant);
        var listed = await restaurant.GetFromJsonAsync<List<ProductShape>>("/api/v1/catalog/products?limit=200");

        listed!.Should().NotContain(p => p.Sku == "PARA500");
    }

    // ---- §32 : « Recherche A ne retourne pas B » — la recherche tolérante ne traverse jamais les entreprises ----
    [Fact]
    public async Task Catalog_search_scoped_to_one_company_never_surfaces_another_companys_product()
    {
        var restaurant = await AuthedClient(Restaurant);
        var hits = await restaurant.GetFromJsonAsync<List<SearchHitShape>>("/api/v1/catalog/search?q=Paracetamol");

        hits.Should().NotContain(h => h.Sku == "PARA500");
    }

    // ---- §32 : un incident ouvert dans une entreprise n'apparaît jamais dans le registre d'une autre ----
    [Fact]
    public async Task Incident_opened_in_one_company_is_invisible_when_listing_from_another_companys_scope()
    {
        var uniqueTitle = $"Isolation probe {Guid.NewGuid():N}";
        var pharmacy = await AuthedClient(Pharmacy);
        var opened = await pharmacy.PostAsJsonAsync("/api/v1/continuity/incidents",
            new { severity = "p4", category = "other", title = uniqueTitle, detectedVia = "user_report" });
        opened.StatusCode.Should().Be(HttpStatusCode.OK);

        var ownList = await pharmacy.GetFromJsonAsync<List<IncidentShape>>("/api/v1/continuity/incidents");
        ownList!.Should().Contain(i => i.Title == uniqueTitle);

        var restaurant = await AuthedClient(Restaurant);
        var otherList = await restaurant.GetFromJsonAsync<List<IncidentShape>>("/api/v1/continuity/incidents");
        otherList!.Should().NotContain(i => i.Title == uniqueTitle);
    }

    private sealed record ProductShape(Guid Id, string Sku, string Name);
    private sealed record SearchHitShape(string Sku);
    private sealed record IncidentShape(Guid Id, string Title);

    private async Task<HttpClient> AuthedClient(string companyId)
    {
        var client = Client();
        var login = await client.PostAsJsonAsync("/api/v1/auth/login", new
        {
            email = "direction@abc-group.demo", password = "Nexora!2026", companyId,
        });
        var token = (await login.Content.ReadFromJsonAsync<TokenShape>())!.AccessToken;
        client.DefaultRequestHeaders.Add("Authorization", $"Bearer {token}");
        client.DefaultRequestHeaders.Add("X-Company-Id", companyId);
        return client;
    }
    private sealed record TokenShape(string AccessToken);
}
