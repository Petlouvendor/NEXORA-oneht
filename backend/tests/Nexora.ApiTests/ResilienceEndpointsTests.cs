using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace Nexora.ApiTests;

/// <summary>
/// Résilience, Haute Disponibilité &amp; Disaster Recovery (Phase 48 §32, §37, §39, §44-45, §55) —
/// câblage &amp; protection des nouvelles routes <c>/resilience/*</c>. Même registre que la continuité
/// (Phase 36) : jamais une seconde logique, seulement des alias canoniques et de nouvelles actions
/// (verrouillage, exercices DR, page de statut).
/// </summary>
public sealed class ResilienceEndpointsTests(WebApplicationFactory<Program> factory)
    : IClassFixture<WebApplicationFactory<Program>>
{
    private static readonly string DevConnString = ApiTestDatabase.App;

    private HttpClient Client() => factory.WithWebHostBuilder(b =>
        b.ConfigureAppConfiguration((_, cfg) => cfg.AddInMemoryCollection(
            new Dictionary<string, string?> { ["Database:ConnectionString"] = DevConnString }))).CreateClient();

    [Fact]
    public async Task Resilience_routes_require_authentication()
    {
        var c = Client();
        (await c.GetAsync("/api/v1/resilience/status-page")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await c.GetAsync("/api/v1/resilience/status")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await c.GetAsync("/api/v1/resilience/runbooks")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await c.GetAsync("/api/v1/resilience/dr-tests")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await c.GetAsync("/api/v1/resilience/backups")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await c.GetAsync("/api/v1/resilience/incidents")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Read_only_resilience_endpoints_are_wired_over_http()
    {
        var client = await AuthedClient();

        (await client.GetAsync("/api/v1/resilience/status-page")).StatusCode.Should().Be(HttpStatusCode.OK);
        // alias §56 sur le même moteur que /continuity/disaster-recovery/status — jamais un second calcul.
        (await client.GetAsync("/api/v1/resilience/status")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await client.GetAsync("/api/v1/resilience/backups")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await client.GetAsync("/api/v1/resilience/incidents")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await client.GetAsync("/api/v1/resilience/dr-tests")).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Runbook_catalog_is_a_static_reference_covering_the_key_scenarios()
    {
        var client = await AuthedClient();

        var res = await client.GetAsync("/api/v1/resilience/runbooks");
        res.StatusCode.Should().Be(HttpStatusCode.OK);
        var runbooks = await res.Content.ReadFromJsonAsync<List<RunbookShape>>();

        runbooks.Should().NotBeNullOrEmpty();
        var list = runbooks!;
        list.Select(r => r.Key).Should().Contain(["db_outage", "ransomware", "restore_procedure", "return_to_normal"]);
        // le runbook ransomware relie explicitement le confinement réel (§32) au reste du registre.
        list.Single(r => r.Key == "ransomware").Steps.Should().Contain(s => s.Guidance.Contains("/lockdown"));
    }

    [Fact]
    public async Task Dr_test_lifecycle_is_wired_over_http()
    {
        var client = await AuthedClient();

        var badType = await client.PostAsJsonAsync("/api/v1/resilience/dr-tests", new { testType = "invalid", title = "Exercice invalide" });
        badType.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);

        var scheduled = await client.PostAsJsonAsync("/api/v1/resilience/dr-tests",
            new { testType = "tabletop", title = "Exercice HTTP tabletop" });
        scheduled.StatusCode.Should().Be(HttpStatusCode.OK);
        var test = await scheduled.Content.ReadFromJsonAsync<DrTestShape>();
        test!.Status.Should().Be("scheduled");

        var recorded = await client.PostAsJsonAsync($"/api/v1/resilience/dr-tests/{test.Id}/record",
            new { passed = true, findings = "OK", correctiveActions = (string?)null, nextTestDue = (DateOnly?)null });
        recorded.StatusCode.Should().Be(HttpStatusCode.OK);
        (await recorded.Content.ReadFromJsonAsync<DrTestShape>())!.Status.Should().Be("passed");
    }

    [Fact]
    public async Task Lockdown_on_a_nonexistent_incident_returns_not_found()
    {
        var client = await AuthedClient();
        var res = await client.PostAsync($"/api/v1/resilience/incidents/{Guid.NewGuid()}/lockdown", null);
        res.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    private sealed record RunbookStepShape(int Step, string Title, string Guidance);
    private sealed record RunbookShape(string Key, string Title, string Description, List<RunbookStepShape> Steps);
    private sealed record DrTestShape(Guid Id, string TestType, string Title, string Status);

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
