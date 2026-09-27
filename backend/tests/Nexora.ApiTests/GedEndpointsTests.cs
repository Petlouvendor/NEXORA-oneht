using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace Nexora.ApiTests;

/// <summary>
/// GED, documents, archivage & signature (Phase 45 §49) — câblage & protection des routes.
/// Le chemin canonique <c>/documents</c> du §49 appartient déjà à <c>DocumentsController</c>
/// (Phase 30, documents ÉMIS) : la bibliothèque GED est exposée sous <c>/ged-documents</c> pour
/// éviter toute ambiguïté de routage (confirmé par <c>AmbiguousMatchException</c> avant correction).
/// Comme les autres suites API (base de développement partagée), création éphémère uniquement.
/// </summary>
public sealed class GedEndpointsTests(WebApplicationFactory<Program> factory)
    : IClassFixture<WebApplicationFactory<Program>>
{
    private const string DevConnString =
        "Host=localhost;Port=5432;Database=nexora;Username=nexora_app;Password=nexora_app_dev;Search Path=nexora,public;Include Error Detail=true";

    private HttpClient Client() => factory.WithWebHostBuilder(b =>
        b.ConfigureAppConfiguration((_, cfg) => cfg.AddInMemoryCollection(
            new Dictionary<string, string?> { ["Database:ConnectionString"] = DevConnString }))).CreateClient();

    [Fact]
    public async Task Ged_routes_require_authentication()
    {
        (await Client().GetAsync("/api/v1/ged-documents/search")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await Client().GetAsync("/api/v1/document-workflows")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await Client().GetAsync("/api/v1/signature-requests")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await Client().GetAsync("/api/v1/document-retention-policies")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Read_only_ged_endpoints_are_wired_over_http()
    {
        var client = await AuthedClient();

        (await client.GetAsync("/api/v1/ged-documents/search")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await client.GetAsync("/api/v1/document-workflows")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await client.GetAsync("/api/v1/signature-requests")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await client.GetAsync("/api/v1/document-retention-policies")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await client.GetAsync("/api/v1/document-audit")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await client.GetAsync("/api/v1/ged-documents/eligible-for-deletion")).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Document_create_content_and_share_round_trip_over_http()
    {
        var client = await AuthedClient();

        var created = await client.PostAsJsonAsync("/api/v1/ged-documents", new
        {
            name = $"Document API test {Guid.NewGuid():N}", documentType = "internal",
            contentBase64 = Convert.ToBase64String("api-test-content"u8.ToArray()), contentType = "application/pdf",
        });
        created.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await created.Content.ReadFromJsonAsync<UploadShape>();
        var id = body!.Document.Id;

        (await client.GetAsync($"/api/v1/ged-documents/{id}")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await client.GetAsync($"/api/v1/ged-documents/{id}/content")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await client.GetAsync($"/api/v1/ged-documents/{id}/versions")).StatusCode.Should().Be(HttpStatusCode.OK);

        var shared = await client.PostAsJsonAsync($"/api/v1/ged-documents/{id}/share",
            new { expiresAt = DateTimeOffset.UtcNow.AddHours(1), allowDownload = true, password = (string?)null });
        shared.StatusCode.Should().Be(HttpStatusCode.OK);
        var token = (await shared.Content.ReadFromJsonAsync<ShareShape>())!.Token;

        var publicClient = Client();
        var download = await publicClient.GetAsync($"/api/v1/shared/{token}");
        download.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    private sealed record DocShape(Guid Id);
    private sealed record UploadShape(DocShape Document);
    private sealed record ShareShape(string Token);

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
