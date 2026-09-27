using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace Nexora.ApiTests;

/// <summary>
/// Migration &amp; onboarding (Phase 39 §45). Le pipeline et la sécurité de scope sont couverts
/// finement côté intégration ; ici on vérifie le câblage et la protection des routes.
/// </summary>
public sealed class MigrationEndpointsTests(WebApplicationFactory<Program> factory)
    : IClassFixture<WebApplicationFactory<Program>>
{
    private const string DevConnString =
        "Host=localhost;Port=5432;Database=nexora;Username=nexora_app;Password=nexora_app_dev;Search Path=nexora,public;Include Error Detail=true";

    private HttpClient Client() => factory.WithWebHostBuilder(b =>
        b.ConfigureAppConfiguration((_, cfg) => cfg.AddInMemoryCollection(
            new Dictionary<string, string?> { ["Database:ConnectionString"] = DevConnString }))).CreateClient();

    [Fact]
    public async Task Import_routes_require_authentication()
    {
        (await Client().GetAsync("/api/v1/imports")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await Client().PostAsJsonAsync("/api/v1/imports", new { kind = "customers", sourceType = "csv", content = "a,b" }))
            .StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await Client().GetAsync("/api/v1/onboarding/checklist")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Full_pipeline_over_http()
    {
        var (client, companyId) = await AuthedClient();
        var csv = "code,name\nMIG-API-1,Client API Un\nMIG-API-2,Client API Deux";

        var created = await client.PostAsJsonAsync("/api/v1/imports", new
        {
            kind = "customers", sourceType = "csv", fileName = "api.csv", mode = "upsert", content = csv,
        });
        created.StatusCode.Should().Be(HttpStatusCode.OK);
        var id = (await created.Content.ReadFromJsonAsync<JobShape>())!.Id;

        (await client.PostAsync($"/api/v1/imports/{id}/profile", null)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await client.PostAsJsonAsync($"/api/v1/imports/{id}/mapping", new
        {
            mapping = new[]
            {
                new { targetField = "code", sourceColumn = "code", transform = "trim" },
                new { targetField = "name", sourceColumn = "name", transform = "trim" },
            },
        })).StatusCode.Should().Be(HttpStatusCode.OK);
        (await client.PostAsync($"/api/v1/imports/{id}/validate", null)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await client.PostAsync($"/api/v1/imports/{id}/simulate", null)).StatusCode.Should().Be(HttpStatusCode.OK);
        var executed = await client.PostAsync($"/api/v1/imports/{id}/execute", null);
        executed.StatusCode.Should().Be(HttpStatusCode.OK);
        (await executed.Content.ReadFromJsonAsync<JobShape>())!.Status.Should().Be("completed");

        var recon = await client.GetAsync($"/api/v1/imports/{id}/reconciliation");
        recon.StatusCode.Should().Be(HttpStatusCode.OK);

        // nettoyage
        (await client.PostAsync($"/api/v1/imports/{id}/cancel", null)).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    private sealed record JobShape(Guid Id, string Status);

    private async Task<(HttpClient, string)> AuthedClient()
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
        return (client, company);
    }
    private sealed record TokenShape(string AccessToken);
}
