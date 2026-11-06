using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace Nexora.ApiTests;

/// <summary>
/// Finalisation phase 14 — Tenant A ≠ Tenant B au niveau du groupe : les routes de gouvernance de groupe
/// (centre de contrôle, plan comptable groupe, intersociétés, consolidation) ne s'ouvrent qu'avec un rôle
/// attribué AU GROUPE. Un gérant d'une seule société, dont le rôle porte pourtant <c>group.view</c>,
/// lisait les créances et dettes des sociétés sœurs.
/// </summary>
public sealed class GroupAccessEndpointsTests(WebApplicationFactory<Program> factory)
    : IClassFixture<WebApplicationFactory<Program>>
{
    private static readonly string DevConnString = ApiTestDatabase.App;
    private const string Group = "dce00000-0000-0000-0000-000000000001";
    private const string Pharmacy = "dce00000-0000-0000-0000-0000000000c1";
    // Compte stable (créé une seule fois) : gérant de la seule Pharmacie, rôle « manager » qui contient group.view.
    private const string CompanyManager = "gerant.pharmacie.groupe@abc-group.demo";
    private const string CompanyManagerPassword = "Gerant!Pharmacie2026";

    private HttpClient Client() => factory.WithWebHostBuilder(b =>
        b.ConfigureAppConfiguration((_, cfg) => cfg.AddInMemoryCollection(
            new Dictionary<string, string?> { ["Database:ConnectionString"] = DevConnString }))).CreateClient();

    [Fact]
    public async Task Group_routes_open_only_to_a_role_held_at_group_level()
    {
        var direction = await Login("direction@abc-group.demo", "Nexora!2026");
        await StepUpTestHelper.AddStepUpAsync(direction, "Nexora!2026", "permission_change");
        var created = await direction.PostAsJsonAsync("/api/v1/users", new
        {
            email = CompanyManager, fullName = "Gérant Pharmacie (test groupe)", password = CompanyManagerPassword,
            initialAssignment = new { roleName = "manager", scopeType = "COMPANY", companyId = Pharmacy, reason = "Test d'isolation groupe" },
        });
        created.StatusCode.Should().BeOneOf(HttpStatusCode.OK, HttpStatusCode.Conflict);

        var manager = await Login(CompanyManager, CompanyManagerPassword);
        foreach (var path in new[] { "control-center", "consolidation/runs", "policy", "options" })
            (await manager.GetAsync($"/api/v1/groups/{Group}/{path}")).StatusCode
                .Should().Be(HttpStatusCode.Forbidden, $"le gérant d'une société ne lit pas « {path} » du groupe");
        (await manager.GetAsync("/api/v1/consolidations")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        var me = await manager.GetFromJsonAsync<MeShape>("/api/v1/me");
        me!.Permissions.Should().NotContain("group.view", "le menu Groupe ne doit pas lui être proposé");

        (await direction.GetAsync($"/api/v1/groups/{Group}/control-center")).StatusCode.Should().Be(HttpStatusCode.OK);
        var compta = await Login("compta@abc-group.demo", "Nexora!2026");
        (await compta.GetAsync($"/api/v1/groups/{Group}/consolidation/runs")).StatusCode.Should().Be(HttpStatusCode.OK);

        // Un groupe qui n'est pas le sien reste fermé, même à la direction.
        (await direction.GetAsync($"/api/v1/groups/{Guid.NewGuid()}/control-center")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    private async Task<HttpClient> Login(string email, string password)
    {
        var client = Client();
        var login = await client.PostAsJsonAsync("/api/v1/auth/login", new { email, password, companyId = Pharmacy });
        login.StatusCode.Should().Be(HttpStatusCode.OK, $"connexion {email}");
        var token = (await login.Content.ReadFromJsonAsync<TokenShape>())!.AccessToken;
        client.DefaultRequestHeaders.Add("Authorization", $"Bearer {token}");
        client.DefaultRequestHeaders.Add("X-Company-Id", Pharmacy);
        client.DefaultRequestHeaders.Add("X-Group-Scope", "true");
        return client;
    }

    private sealed record TokenShape(string AccessToken);
    private sealed record MeShape(List<string> Permissions);
}
