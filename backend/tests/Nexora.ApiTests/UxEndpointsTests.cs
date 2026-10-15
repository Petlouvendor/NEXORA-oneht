using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace Nexora.ApiTests;

/// <summary>UX, design system &amp; accessibilité (Phase 40 §57) — câblage &amp; protection des routes.</summary>
public sealed class UxEndpointsTests(WebApplicationFactory<Program> factory)
    : IClassFixture<WebApplicationFactory<Program>>
{
    private static readonly string DevConnString = ApiTestDatabase.App;

    private HttpClient Client() => factory.WithWebHostBuilder(b =>
        b.ConfigureAppConfiguration((_, cfg) => cfg.AddInMemoryCollection(
            new Dictionary<string, string?> { ["Database:ConnectionString"] = DevConnString }))).CreateClient();

    [Fact]
    public async Task Ux_routes_require_authentication()
    {
        (await Client().GetAsync("/api/v1/design-system/version")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await Client().GetAsync("/api/v1/user-preferences")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await Client().GetAsync("/api/v1/saved-filters")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Preferences_alias_and_design_system_version_over_http()
    {
        var (client, company) = await AuthedClient();

        var prefs = await client.GetAsync("/api/v1/user-preferences");
        prefs.StatusCode.Should().Be(HttpStatusCode.OK);
        (await prefs.Content.ReadAsStringAsync()).Should().Contain("\"theme\"");

        var put = await client.PutAsJsonAsync("/api/v1/ui-preferences", new { highContrast = true, textScale = 1.15 });
        put.StatusCode.Should().Be(HttpStatusCode.OK);
        (await put.Content.ReadAsStringAsync()).Should().Contain("\"highContrast\":true");

        var ds = await client.GetAsync("/api/v1/design-system/version");
        ds.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await ds.Content.ReadAsStringAsync();
        body.Should().Contain("WCAG 2.2 AA");
        body.Should().Contain("CommandPalette");

        var layouts = await client.GetAsync("/api/v1/dashboard-layouts");
        layouts.StatusCode.Should().Be(HttpStatusCode.OK);

        // remise à zéro douce
        await client.PutAsJsonAsync("/api/v1/user-preferences", new { highContrast = false, textScale = 1.0 });
    }

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
