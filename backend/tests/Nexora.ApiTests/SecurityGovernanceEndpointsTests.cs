using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace Nexora.ApiTests;

/// <summary>
/// Sécurité, IAM & Conformité (Phase 47 §58) — câblage &amp; protection des routes. Les registres
/// plateforme (vulnérabilités/certificats/conformité) sont réservés au personnel plateforme
/// (<c>platform.security</c>) — non détenu par un utilisateur entreprise, même Direction.
/// </summary>
public sealed class SecurityGovernanceEndpointsTests(WebApplicationFactory<Program> factory)
    : IClassFixture<WebApplicationFactory<Program>>
{
    private static readonly string DevConnString = ApiTestDatabase.App;

    private HttpClient Client() => factory.WithWebHostBuilder(b =>
        b.ConfigureAppConfiguration((_, cfg) => cfg.AddInMemoryCollection(
            new Dictionary<string, string?> { ["Database:ConnectionString"] = DevConnString }))).CreateClient();

    [Fact]
    public async Task Security_governance_routes_require_authentication()
    {
        var c = Client();
        (await c.GetAsync("/api/v1/security/devices")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await c.GetAsync("/api/v1/security/emergency-access")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await c.GetAsync("/api/v1/security/score")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await c.GetAsync("/api/v1/security/dashboard")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await c.GetAsync("/api/v1/security/vulnerabilities")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await c.GetAsync("/api/v1/security/certificates")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await c.GetAsync("/api/v1/security/compliance")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await c.GetAsync("/api/v1/security/mfa")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Read_only_security_governance_endpoints_are_wired_over_http()
    {
        var client = await AuthedClient();

        (await client.GetAsync("/api/v1/security/devices")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await client.GetAsync("/api/v1/security/emergency-access")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await client.GetAsync("/api/v1/security/score")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await client.GetAsync("/api/v1/security/dashboard")).StatusCode.Should().Be(HttpStatusCode.OK);

        // alias Phase 47 §58 sur des moteurs existants — même contrôleur, jamais une seconde logique.
        (await client.GetAsync("/api/v1/security/mfa")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await client.GetAsync("/api/v1/security/incidents?domain=security")).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Platform_only_security_registries_are_reserved_to_platform_staff()
    {
        var client = await AuthedClient(); // direction@abc-group.demo — pas platform.security

        (await client.GetAsync("/api/v1/security/vulnerabilities")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await client.GetAsync("/api/v1/security/certificates")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await client.GetAsync("/api/v1/security/compliance")).StatusCode.Should().Be(HttpStatusCode.Forbidden);

        var submit = await client.PostAsJsonAsync("/api/v1/security/vulnerabilities", new
        {
            source = "manual", severity = "low", title = "Test", description = (string?)null, cveId = (string?)null, component = (string?)null,
        });
        submit.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Emergency_access_self_decision_is_rejected_over_http()
    {
        var client = await AuthedClient();
        var request = new { roleName = "manager", reason = "Vérification du câblage HTTP de l'accès d'urgence.", requestedDurationHours = 1 };
        await StepUpTestHelper.ShouldRequireStepUpAsync(
            await client.PostAsJsonAsync("/api/v1/security/emergency-access", request), "permission_change");
        await StepUpTestHelper.AddStepUpAsync(client, "Nexora!2026", "permission_change");

        var requested = await client.PostAsJsonAsync("/api/v1/security/emergency-access", new
        {
            roleName = "manager", reason = "Vérification du câblage HTTP de l'accès d'urgence.", requestedDurationHours = 1,
        });
        requested.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await requested.Content.ReadFromJsonAsync<GrantShape>();

        // le demandeur ne peut pas décider de sa propre demande (§6) — même s'il détient par
        // ailleurs la permission d'approbation.
        var selfDecide = await client.PostAsJsonAsync($"/api/v1/security/emergency-access/{body!.Id}/decide",
            new { approve = true, note = (string?)null });
        selfDecide.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    private sealed record GrantShape(Guid Id, string Status);

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
