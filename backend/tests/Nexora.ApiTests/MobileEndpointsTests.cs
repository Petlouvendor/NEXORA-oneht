using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace Nexora.ApiTests;

/// <summary>
/// Mobile, terrain & multi-appareils (Phase 44 §48) — câblage & protection des routes.
/// Comme les autres suites API (base de développement partagée), lecture seule.
/// </summary>
public sealed class MobileEndpointsTests(WebApplicationFactory<Program> factory)
    : IClassFixture<WebApplicationFactory<Program>>
{
    private static readonly string DevConnString = ApiTestDatabase.App;

    private HttpClient Client() => factory.WithWebHostBuilder(b =>
        b.ConfigureAppConfiguration((_, cfg) => cfg.AddInMemoryCollection(
            new Dictionary<string, string?> { ["Database:ConnectionString"] = DevConnString }))).CreateClient();

    [Fact]
    public async Task Mobile_routes_require_authentication()
    {
        (await Client().GetAsync("/api/v1/mobile/config")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await Client().GetAsync("/api/v1/devices")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await Client().GetAsync("/api/v1/field-ops/routes")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await Client().PostAsync("/api/v1/mobile/sync/pull", null)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Read_only_mobile_and_field_ops_endpoints_are_wired_over_http()
    {
        var client = await AuthedClient();

        (await client.GetAsync("/api/v1/mobile/config")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await client.GetAsync("/api/v1/mobile/sync")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await client.PostAsync("/api/v1/mobile/sync/pull", null)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await client.GetAsync("/api/v1/mobile/conflicts")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await client.GetAsync("/api/v1/mobile/notifications")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await client.GetAsync("/api/v1/mobile/tasks")).StatusCode.Should().Be(HttpStatusCode.OK);

        // Alias canoniques §48 — même registre que /api/v1/sync/devices (Phase 10).
        (await client.GetAsync("/api/v1/devices")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await client.GetAsync("/api/v1/sync/devices")).StatusCode.Should().Be(HttpStatusCode.OK);

        (await client.GetAsync("/api/v1/field-ops/routes")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await client.GetAsync("/api/v1/field-ops/my-stops")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await client.GetAsync("/api/v1/field-ops/interventions")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await client.GetAsync("/api/v1/field-ops/interventions/mine")).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Attachment_upload_and_read_round_trip_over_http()
    {
        var client = await AuthedClient();

        var upload = await client.PostAsJsonAsync("/api/v1/attachments", new
        {
            kind = "photo", referenceType = (string?)null, referenceId = (Guid?)null,
            // En-tête PNG réel : le format est lu dans le contenu (audit 2026-10 éd. 2).
            contentBase64 = Convert.ToBase64String([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, .. "api-test"u8.ToArray()]),
            contentType = "image/png",
        });
        upload.StatusCode.Should().Be(HttpStatusCode.OK);
        var id = (await upload.Content.ReadFromJsonAsync<JsonIdShape>())!.Id;

        var read = await client.GetAsync($"/api/v1/attachments/{id}");
        read.StatusCode.Should().Be(HttpStatusCode.OK);
        read.Content.Headers.ContentType!.MediaType.Should().Be("image/png");
    }

    private sealed record JsonIdShape(Guid Id);

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
