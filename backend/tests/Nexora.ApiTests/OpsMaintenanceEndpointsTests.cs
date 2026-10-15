using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace Nexora.ApiTests;

/// <summary>
/// Administration opérationnelle & maintenance (Phase 41 §51, §58) — câblage & protection des
/// routes <c>/api/v1/ops/*</c>. Volontairement limité à des appels sans effet durable sur la base
/// de développement partagée : le passage à l'état <c>in_progress</c> d'une fenêtre de maintenance
/// dégraderait l'API pour tout le monde (§24) — ce scénario reste couvert par
/// <c>OperationsMaintenanceTests</c> (Nexora.IntegrationTests) sur une base clonée jetable.
/// </summary>
public sealed class OpsMaintenanceEndpointsTests(WebApplicationFactory<Program> factory)
    : IClassFixture<WebApplicationFactory<Program>>
{
    private static readonly string DevConnString = ApiTestDatabase.App;

    private HttpClient Client() => factory.WithWebHostBuilder(b =>
        b.ConfigureAppConfiguration((_, cfg) => cfg.AddInMemoryCollection(
            new Dictionary<string, string?> { ["Database:ConnectionString"] = DevConnString }))).CreateClient();

    [Fact]
    public async Task Ops_routes_require_authentication()
    {
        (await Client().GetAsync("/api/v1/ops/status")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await Client().GetAsync("/api/v1/ops/jobs")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await Client().GetAsync("/api/v1/ops/queues")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await Client().GetAsync("/api/v1/ops/diagnostics")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await Client().GetAsync("/api/v1/ops/audit")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await Client().PostAsJsonAsync("/api/v1/ops/jobs/imports.resume_stalled/run", new { })).StatusCode
            .Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Read_only_ops_endpoints_are_wired_over_http()
    {
        var client = await AuthedClient();

        (await client.GetAsync("/api/v1/ops/status")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await client.GetAsync("/api/v1/system/status")).StatusCode.Should().Be(HttpStatusCode.OK); // alias historique (Phase 37)

        var jobs = await client.GetAsync("/api/v1/ops/jobs");
        jobs.StatusCode.Should().Be(HttpStatusCode.OK);
        (await jobs.Content.ReadAsStringAsync()).Should().Contain("imports.resume_stalled");

        (await client.GetAsync("/api/v1/ops/queues")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await client.GetAsync("/api/v1/ops/dead-letters")).StatusCode.Should().Be(HttpStatusCode.OK);

        var diagnostics = await client.GetAsync("/api/v1/ops/diagnostics");
        diagnostics.StatusCode.Should().Be(HttpStatusCode.OK);
        (await diagnostics.Content.ReadAsStringAsync()).Should().Contain("\"database\"");

        (await client.GetAsync("/api/v1/ops/database")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await client.GetAsync("/api/v1/ops/storage")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await client.GetAsync("/api/v1/ops/feature-flags")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await client.GetAsync("/api/v1/ops/maintenance")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await client.GetAsync("/api/v1/ops/retention-policies")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await client.GetAsync("/api/v1/ops/purge/history")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await client.GetAsync("/api/v1/ops/data-corrections")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await client.GetAsync("/api/v1/ops/audit")).StatusCode.Should().Be(HttpStatusCode.OK); // alias de /security/audit (Phase 11)
    }

    [Fact]
    public async Task Feature_flag_round_trips_over_http_and_cleans_up_after_itself()
    {
        var client = await AuthedClient();
        var key = $"ops.http.test.{Guid.NewGuid():N}"[..40];

        var created = await client.PostAsJsonAsync("/api/v1/ops/feature-flags",
            new { key, scope = "global", enabled = true, description = "test HTTP éphémère" });
        created.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await created.Content.ReadAsStringAsync();
        body.Should().Contain(key);

        var id = System.Text.Json.JsonDocument.Parse(body).RootElement.GetProperty("id").GetGuid();
        (await client.DeleteAsync($"/api/v1/ops/feature-flags/{id}")).StatusCode.Should().Be(HttpStatusCode.NoContent);
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
