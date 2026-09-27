using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace Nexora.ApiTests;

/// <summary>
/// NEXORA ONE — recherche globale Ctrl+K (§5) et fil d'activité (§6) : câblage HTTP et protection.
/// Lecture seule sur la base de développement partagée.
/// </summary>
public sealed class ExperienceEndpointsTests(WebApplicationFactory<Program> factory)
    : IClassFixture<WebApplicationFactory<Program>>
{
    private const string DevConnString =
        "Host=localhost;Port=5432;Database=nexora;Username=nexora_app;Password=nexora_app_dev;Search Path=nexora,public;Include Error Detail=true";

    private HttpClient Client() => factory.WithWebHostBuilder(b =>
        b.ConfigureAppConfiguration((_, cfg) => cfg.AddInMemoryCollection(
            new Dictionary<string, string?> { ["Database:ConnectionString"] = DevConnString }))).CreateClient();

    [Fact]
    public async Task Search_and_activity_require_authentication()
    {
        (await Client().GetAsync("/api/v1/search?q=riz")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await Client().GetAsync("/api/v1/dashboard/activity")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Search_and_activity_are_wired_over_http()
    {
        var client = await AuthedClient();

        var search = await client.GetAsync("/api/v1/search?q=a&limit=3");
        search.StatusCode.Should().Be(HttpStatusCode.OK);
        (await search.Content.ReadAsStringAsync()).Should().Contain("\"groups\":[]", "une lettre seule ne déclenche pas de recherche");

        var real = await client.GetAsync("/api/v1/search?q=an&limit=3");
        real.StatusCode.Should().Be(HttpStatusCode.OK);
        (await real.Content.ReadAsStringAsync()).Should().Contain("\"query\":\"an\"");

        var activity = await client.GetAsync("/api/v1/dashboard/activity?limit=5");
        activity.StatusCode.Should().Be(HttpStatusCode.OK);
        var events = await activity.Content.ReadFromJsonAsync<List<ActivityShape>>();
        events.Should().NotBeNull();
        events!.Count.Should().BeLessThanOrEqualTo(5);
        events.Select(e => e.OccurredAt).Should().BeInDescendingOrder();
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
    private sealed record ActivityShape(string Kind, string Title, DateTimeOffset OccurredAt);
}
