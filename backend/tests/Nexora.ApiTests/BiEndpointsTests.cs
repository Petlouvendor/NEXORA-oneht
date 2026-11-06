using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace Nexora.ApiTests;

/// <summary>
/// Business Intelligence, Reporting & Pilotage (Phase 42 §55, §58) — câblage & protection des
/// routes. Comme les autres suites API (base de développement partagée), aucune mutation
/// n'est laissée en place au-delà du strict cycle créer→vérifier→supprimer d'un rapport
/// enregistré éphémère.
/// </summary>
public sealed class BiEndpointsTests(WebApplicationFactory<Program> factory)
    : IClassFixture<WebApplicationFactory<Program>>
{
    private static readonly string DevConnString = ApiTestDatabase.App;

    private HttpClient Client() => factory.WithWebHostBuilder(b =>
        b.ConfigureAppConfiguration((_, cfg) => cfg.AddInMemoryCollection(
            new Dictionary<string, string?> { ["Database:ConnectionString"] = DevConnString }))).CreateClient();

    [Fact]
    public async Task Bi_routes_require_authentication()
    {
        (await Client().GetAsync("/api/v1/kpi-definitions")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await Client().GetAsync("/api/v1/objectives")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await Client().GetAsync("/api/v1/reports")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await Client().GetAsync("/api/v1/forecasts")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await Client().GetAsync("/api/v1/insights")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await Client().GetAsync("/api/v1/budgets")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Read_only_bi_endpoints_are_wired_over_http()
    {
        var client = await AuthedClient();

        var kpis = await client.GetAsync("/api/v1/kpi-definitions");
        kpis.StatusCode.Should().Be(HttpStatusCode.OK);
        (await kpis.Content.ReadAsStringAsync()).Should().Contain("\"revenue\"");

        (await client.GetAsync("/api/v1/kpis/revenue/definition")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await client.GetAsync("/api/v1/objectives")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await client.GetAsync("/api/v1/objectives/score")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await client.GetAsync("/api/v1/reports")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await client.GetAsync("/api/v1/reports/schedules")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await client.GetAsync("/api/v1/reports/executions")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await client.GetAsync("/api/v1/forecasts")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await client.GetAsync("/api/v1/insights")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await client.GetAsync("/api/v1/budgets")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await client.GetAsync("/api/v1/dashboards")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await client.GetAsync("/api/v1/reporting/data-quality")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await client.GetAsync("/api/v1/reporting/exports")).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Saved_report_round_trips_over_http_and_cleans_up_after_itself()
    {
        var client = await AuthedClient();

        var created = await client.PostAsJsonAsync("/api/v1/reports",
            new { name = $"Ventes test {Guid.NewGuid():N}", reportKey = "sales_journal", filter = JsonDocument.Parse("{}").RootElement });
        created.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await created.Content.ReadAsStringAsync();
        var id = JsonDocument.Parse(body).RootElement.GetProperty("id").GetGuid();

        var run = await client.PostAsync($"/api/v1/reports/{id}/run", null);
        run.StatusCode.Should().Be(HttpStatusCode.OK);

        (await client.DeleteAsync($"/api/v1/reports/{id}")).StatusCode.Should().Be(HttpStatusCode.NoContent);
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
