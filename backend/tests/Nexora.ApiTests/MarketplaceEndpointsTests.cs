using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace Nexora.ApiTests;

/// <summary>
/// API, intégrations & écosystème — marketplace partenaires (Phase 46 §41-48, §58) : câblage &
/// protection des routes. Comme les autres suites API (base de développement partagée), toute
/// action y est idempotente (installer une app déjà installée met simplement à jour la ligne).
/// </summary>
public sealed class MarketplaceEndpointsTests(WebApplicationFactory<Program> factory)
    : IClassFixture<WebApplicationFactory<Program>>
{
    private const string DevConnString =
        "Host=localhost;Port=5432;Database=nexora;Username=nexora_app;Password=nexora_app_dev;Search Path=nexora,public;Include Error Detail=true";

    private HttpClient Client() => factory.WithWebHostBuilder(b =>
        b.ConfigureAppConfiguration((_, cfg) => cfg.AddInMemoryCollection(
            new Dictionary<string, string?> { ["Database:ConnectionString"] = DevConnString }))).CreateClient();

    [Fact]
    public async Task Marketplace_routes_require_authentication()
    {
        (await Client().GetAsync("/api/v1/apps")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await Client().GetAsync("/api/v1/apps/installed")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await Client().GetAsync("/api/v1/webhooks")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await Client().GetAsync("/api/v1/developer/keys")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await Client().GetAsync("/api/v1/developer/usage")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await Client().GetAsync("/api/v1/integrations")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Read_only_marketplace_endpoints_are_wired_over_http()
    {
        var client = await AuthedClient();

        (await client.GetAsync("/api/v1/apps")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await client.GetAsync("/api/v1/apps/installed")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await client.GetAsync("/api/v1/webhooks")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await client.GetAsync("/api/v1/developer/keys")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await client.GetAsync("/api/v1/developer/usage")).StatusCode.Should().Be(HttpStatusCode.OK);

        // alias canoniques §58 sur le moteur d'intégrations existant (Phase 12) — même contrôleur.
        (await client.GetAsync("/api/v1/integrations")).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Install_app_and_issue_a_scoped_developer_key_over_http()
    {
        var client = await AuthedClient();

        var apps = await (await client.GetAsync("/api/v1/apps")).Content.ReadFromJsonAsync<AppsPage>();
        var app = apps!.Items.Should().ContainSingle(a => a.Slug == "nexora-export-comptable").Which;

        var installed = await client.PostAsJsonAsync($"/api/v1/apps/{app.Id}/install",
            new { grantedScopes = new[] { "accounting.view" }, config = (Dictionary<string, string>?)null });
        installed.StatusCode.Should().Be(HttpStatusCode.OK);
        var installation = await installed.Content.ReadFromJsonAsync<InstallationShape>();
        installation!.Status.Should().Be("installed");

        var key = await client.PostAsJsonAsync("/api/v1/developer/keys", new
        {
            name = "Clé test API export comptable", scopes = new[] { "accounting.view" },
            appInstallationId = installation.Id,
        });
        key.StatusCode.Should().Be(HttpStatusCode.OK);
        var created = await key.Content.ReadFromJsonAsync<CreatedKeyShape>();
        created!.Secret.Should().StartWith("nxk_live_");
        created.Key.AppInstallationId.Should().Be(installation.Id);

        (await client.PostAsync($"/api/v1/developer/keys/{created.Key.Id}/revoke", null)).StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await client.PostAsync($"/api/v1/apps/{app.Id}/uninstall", null)).StatusCode.Should().Be(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task Catalogue_curation_is_reserved_to_platform_staff()
    {
        var client = await AuthedClient(); // direction@abc-group.demo — pas platform.manage_marketplace

        var submit = await client.PostAsJsonAsync("/api/v1/apps", new
        {
            slug = $"forbidden-{Guid.NewGuid():N}"[..24], name = "Interdit", publisherName = "Test",
            category = "crm", version = "1.0", description = (string?)null, requestedScopes = new[] { "catalog.view" },
        });
        submit.StatusCode.Should().Be(HttpStatusCode.Forbidden);

        var apps = await (await client.GetAsync("/api/v1/apps")).Content.ReadFromJsonAsync<AppsPage>();
        var review = await client.PostAsJsonAsync($"/api/v1/apps/{apps!.Items[0].Id}/review", new { decision = "approve", notes = (string?)null });
        review.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    private sealed record AppShape(Guid Id, string Slug);
    private sealed record AppsPage(List<AppShape> Items, string? NextCursor);
    private sealed record InstallationShape(Guid Id, string Status);
    private sealed record KeyShape(Guid Id, Guid? AppInstallationId);
    private sealed record CreatedKeyShape(KeyShape Key, string Secret);

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
