using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace Nexora.ApiTests;

/// <summary>
/// Intelligence métier & recommandations (Phase 43 §48) — câblage & protection des routes.
/// Comme les autres suites API (base de développement partagée), lecture seule : aucune
/// mutation n'est laissée en place.
/// </summary>
public sealed class IntelligenceEndpointsTests(WebApplicationFactory<Program> factory)
    : IClassFixture<WebApplicationFactory<Program>>
{
    private static readonly string DevConnString = ApiTestDatabase.App;

    private HttpClient Client() => factory.WithWebHostBuilder(b =>
        b.ConfigureAppConfiguration((_, cfg) => cfg.AddInMemoryCollection(
            new Dictionary<string, string?> { ["Database:ConnectionString"] = DevConnString }))).CreateClient();

    [Fact]
    public async Task Intelligence_routes_require_authentication()
    {
        (await Client().GetAsync("/api/v1/intelligence/recommendations")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await Client().GetAsync("/api/v1/intelligence/scores")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await Client().GetAsync("/api/v1/intelligence/rules")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await Client().GetAsync("/api/v1/intelligence/dashboard")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await Client().PostAsync("/api/v1/assistant/query", null)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Read_only_intelligence_endpoints_are_wired_over_http()
    {
        var client = await AuthedClient();

        (await client.GetAsync("/api/v1/intelligence/recommendations")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await client.GetAsync("/api/v1/intelligence/insights")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await client.GetAsync("/api/v1/intelligence/anomalies")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await client.GetAsync("/api/v1/intelligence/forecasts")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await client.GetAsync("/api/v1/intelligence/scores")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await client.GetAsync("/api/v1/intelligence/rules")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await client.GetAsync("/api/v1/intelligence/tasks")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await client.GetAsync("/api/v1/intelligence/audit")).StatusCode.Should().Be(HttpStatusCode.OK);

        var models = await client.GetAsync("/api/v1/intelligence/models");
        models.StatusCode.Should().Be(HttpStatusCode.OK);
        (await models.Content.ReadAsStringAsync()).Should().Contain("stock_reorder").And.Contain("sales_spike").And.Contain("customer_inactive").And.Contain("supplier_price_increase");
    }

    // ---- Phase 54 §5, §16, §19, §20 : vue générale et action « modifier » ------------------------
    [Fact]
    public async Task Dashboard_is_wired_and_modify_validates_the_target_recommendation()
    {
        var client = await AuthedClient();

        var dashboard = await client.GetAsync("/api/v1/intelligence/dashboard");
        dashboard.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await dashboard.Content.ReadAsStringAsync();
        body.Should().Contain("\"criticalCount\"").And.Contain("\"topActions\"").And.Contain("\"topOpportunities\"");

        // Câblage de bout en bout sans mutation persistée : un id inexistant est bien refusé (404),
        // preuve que l'action atteint le service plutôt qu'une route non branchée (500/404 générique).
        var modify = await client.PostAsJsonAsync($"/api/v1/intelligence/recommendations/{Guid.NewGuid()}/modify",
            new { overrideValue = new { suggestedQuantity = 999 }, note = "test" });
        modify.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Assistant_briefing_and_query_are_wired_over_http()
    {
        var client = await AuthedClient();

        var briefing = await client.GetAsync("/api/v1/assistant/briefing?role=direction");
        briefing.StatusCode.Should().Be(HttpStatusCode.OK);
        (await briefing.Content.ReadAsStringAsync()).Should().Contain("\"role\":\"direction\"");

        var query = await client.PostAsJsonAsync("/api/v1/assistant/query", new { question = "Combien avons-nous vendu ce mois-ci ?" });
        query.StatusCode.Should().Be(HttpStatusCode.OK);
        (await query.Content.ReadAsStringAsync()).Should().Contain("\"intent\"");
    }

    private async Task<HttpClient> AuthedClient()
    {
        var client = Client();
        const string company = "dce00000-0000-0000-0000-0000000000c1";
        var login = await client.PostAsJsonAsync("/api/v1/auth/login", new
        {
            email = "direction@abc-group.demo", password = "Nexora!2026", companyId = company,
        });
        var token = (await login.Content.ReadFromJsonAsync<TokenShape>())!.AccessToken;
        client.DefaultRequestHeaders.Add("Authorization", $"Bearer {token}");
        client.DefaultRequestHeaders.Add("X-Company-Id", company);
        return client;
    }
    private sealed record TokenShape(string AccessToken);
}
