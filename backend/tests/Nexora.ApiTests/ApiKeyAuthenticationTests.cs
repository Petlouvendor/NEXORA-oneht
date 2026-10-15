using System.Net;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Nexora.Application.Integrations;

namespace Nexora.ApiTests;

/// <summary>
/// Pipeline d'authentification par clé d'API (Phase 35 §13-14). L'authentificateur est simulé ;
/// on vérifie le middleware, le filtrage par scope et les codes de refus. Le contrôle des
/// permissions du propriétaire touche la base <c>nexora</c> locale (utilisateur de démo).
/// </summary>
public sealed class ApiKeyAuthenticationTests(WebApplicationFactory<Program> factory)
    : IClassFixture<WebApplicationFactory<Program>>
{
    private static readonly Guid DemoCompany = Guid.Parse("dce00000-0000-0000-0000-0000000000c1");
    private static readonly Guid DemoDirection = Guid.Parse("dce00000-0000-0000-0000-0000000000d1");

    private static readonly string DevConnString = ApiTestDatabase.App;

    private HttpClient ClientWith(ApiKeyResolution canned)
        => factory.WithWebHostBuilder(b =>
        {
            b.ConfigureAppConfiguration((_, cfg) => cfg.AddInMemoryCollection(
                new Dictionary<string, string?> { ["Database:ConnectionString"] = DevConnString }));
            b.ConfigureTestServices(s => s.AddScoped<IApiKeyAuthenticator>(_ => new FakeAuthenticator(canned)));
        }).CreateClient();

    [Fact]
    public async Task Unknown_api_key_is_rejected_with_401()
    {
        var client = ClientWith(new ApiKeyResolution(ApiKeyOutcome.Invalid));
        client.DefaultRequestHeaders.Add("X-Api-Key", "nxk_live_deadbeef_bogus");

        var res = await client.GetAsync("/api/v1/integrations/connectors");

        res.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await res.Content.ReadAsStringAsync()).Should().Contain("api_key_invalid");
    }

    [Fact]
    public async Task Revoked_api_key_is_rejected_with_401()
    {
        var client = ClientWith(new ApiKeyResolution(ApiKeyOutcome.Revoked));
        client.DefaultRequestHeaders.Add("X-Api-Key", "nxk_live_deadbeef_x");

        var res = await client.GetAsync("/api/v1/integrations/connectors");

        res.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await res.Content.ReadAsStringAsync()).Should().Contain("api_key_revoked");
    }

    [Fact]
    public async Task A_key_whose_scope_covers_the_endpoint_is_authorized()
    {
        var client = ClientWith(new ApiKeyResolution(
            ApiKeyOutcome.Ok, Guid.NewGuid(), DemoCompany, DemoDirection, ["integrations.view"], 120));
        client.DefaultRequestHeaders.Add("X-Api-Key", "nxk_live_deadbeef_ok");

        var res = await client.GetAsync("/api/v1/integrations/connectors");

        res.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task A_key_whose_scope_does_not_cover_the_endpoint_is_forbidden()
    {
        // Le propriétaire a `integrations.view`, mais la clé n'a que `catalog.view`.
        var client = ClientWith(new ApiKeyResolution(
            ApiKeyOutcome.Ok, Guid.NewGuid(), DemoCompany, DemoDirection, ["catalog.view"], 120));
        client.DefaultRequestHeaders.Add("X-Api-Key", "nxk_live_deadbeef_narrow");

        var res = await client.GetAsync("/api/v1/integrations/connectors");

        res.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    private sealed class FakeAuthenticator(ApiKeyResolution canned) : IApiKeyAuthenticator
    {
        public Task<ApiKeyResolution> ResolveAsync(string presentedKey, string? ipAddress, CancellationToken ct = default)
            => Task.FromResult(canned);
    }
}
