using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace Nexora.ApiTests;

/// <summary>
/// Recette Phase 55 — câblage HTTP des corrections (AUTH-004/008, SEC-011, CLI-001, ACH-001,
/// PROD-004, RBAC-001). <b>Aucun test ici ne persiste de donnée métier</b> dans la base de
/// développement : elle sert de modèle aux tests d'intégration (anomalie A3 de la Phase 49).
/// Les écritures réelles sont prouvées par <c>RecetteCorrectionsTests</c>, sur base clonée.
/// </summary>
public sealed class RecetteEndpointsTests(WebApplicationFactory<Program> factory)
    : IClassFixture<WebApplicationFactory<Program>>
{
    private static readonly string DevConnString = ApiTestDatabase.App;
    private const string Pharmacy = "dce00000-0000-0000-0000-0000000000c1";
    private const string Restaurant = "dce00000-0000-0000-0000-0000000000c2";
    private const string DemoCustomer = "dce00000-0000-0000-0000-00000000b001";   // Clinique Bon Samaritain (pharmacie)

    private HttpClient Client() => factory.WithWebHostBuilder(b =>
        b.ConfigureAppConfiguration((_, cfg) => cfg.AddInMemoryCollection(
            new Dictionary<string, string?> { ["Database:ConnectionString"] = DevConnString }))).CreateClient();

    private async Task<HttpClient> AuthedClient(string company = Pharmacy)
    {
        var client = Client();
        var login = await client.PostAsJsonAsync("/api/v1/auth/login",
            new { email = "direction@abc-group.demo", password = "Nexora!2026", companyId = company });
        login.StatusCode.Should().Be(HttpStatusCode.OK);
        var token = (await login.Content.ReadFromJsonAsync<TokenShape>())!.AccessToken;
        client.DefaultRequestHeaders.Add("Authorization", $"Bearer {token}");
        client.DefaultRequestHeaders.Add("X-Company-Id", company);
        return client;
    }
    private sealed record TokenShape(string AccessToken);
    private sealed record ErrorShape(string? Title);

    // ---- AUTH-004 : la déconnexion coupe CE jeton immédiatement, sans toucher aux autres ----
    [Fact]
    public async Task Logout_revokes_this_token_immediately_but_not_other_sessions()
    {
        var first = await AuthedClient();
        var second = await AuthedClient();
        (await first.GetAsync("/api/v1/me")).StatusCode.Should().Be(HttpStatusCode.OK);

        (await first.PostAsync("/api/v1/me/logout", null)).StatusCode.Should().Be(HttpStatusCode.NoContent);

        var after = await first.GetAsync("/api/v1/me");
        after.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await after.Content.ReadFromJsonAsync<ErrorShape>())!.Title.Should().Be("session_revoked");
        (await second.GetAsync("/api/v1/me")).StatusCode.Should().Be(HttpStatusCode.OK, "une autre session du même compte reste ouverte");
    }

    // ---- AUTH-005 / API-004 : les nouvelles routes sont protégées ----
    [Fact]
    public async Task New_routes_require_authentication()
    {
        var c = Client();
        (await c.GetAsync("/api/v1/customers")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await c.PostAsJsonAsync("/api/v1/customers", new { name = "x" })).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await c.PostAsJsonAsync("/api/v1/suppliers", new { name = "x" })).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await c.PutAsJsonAsync($"/api/v1/catalog/products/{Guid.NewGuid()}", new { name = "x" })).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await c.PostAsJsonAsync("/api/v1/users", new { email = "x@y.z" })).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await c.PostAsync("/api/v1/me/logout", null)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await c.PostAsJsonAsync("/api/v1/me/password", new { currentPassword = "a", newPassword = "b" })).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    // ---- CLI-002 / TENANT-005 : un client est lisible dans son entreprise, introuvable ailleurs ----
    [Fact]
    public async Task Customer_card_is_readable_in_its_company_and_not_found_from_another()
    {
        var pharmacy = await AuthedClient(Pharmacy);
        (await pharmacy.GetAsync($"/api/v1/customers/{DemoCustomer}")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await pharmacy.GetFromJsonAsync<List<object>>("/api/v1/customers?q=Samaritain")).Should().NotBeEmpty();

        var restaurant = await AuthedClient(Restaurant);
        (await restaurant.GetAsync($"/api/v1/customers/{DemoCustomer}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await restaurant.GetFromJsonAsync<List<object>>("/api/v1/customers?q=Samaritain")).Should().BeEmpty();
    }

    // ---- SEC-001 : une chaîne d'injection SQL dans un filtre reste une simple valeur ----
    [Fact]
    public async Task Sql_injection_strings_in_filters_are_treated_as_plain_text()
    {
        var c = await AuthedClient();
        foreach (var probe in new[] { "' OR '1'='1", "x'; DROP TABLE nexora.customer; --", "%' UNION SELECT email FROM nexora.app_user --" })
        {
            var q = Uri.EscapeDataString(probe);
            var customers = await c.GetAsync($"/api/v1/customers?q={q}");
            customers.StatusCode.Should().Be(HttpStatusCode.OK, probe);
            (await customers.Content.ReadFromJsonAsync<List<object>>()).Should().BeEmpty(probe);
            (await c.GetAsync($"/api/v1/catalog/search?q={q}")).StatusCode.Should().Be(HttpStatusCode.OK, probe);
        }
        (await c.GetAsync($"/api/v1/customers/{DemoCustomer}")).StatusCode.Should().Be(HttpStatusCode.OK, "la table est intacte");
    }

    // ---- API-002 : les charges invalides sont refusées avant toute écriture ----
    [Fact]
    public async Task Invalid_payloads_are_refused_without_writing_anything()
    {
        var c = await AuthedClient();
        (await c.PostAsJsonAsync("/api/v1/customers", new { name = "" })).StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        (await c.PostAsJsonAsync("/api/v1/suppliers", new { name = " " })).StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        (await c.PutAsJsonAsync($"/api/v1/catalog/products/{Guid.NewGuid()}", new { name = "Inconnu" }))
            .StatusCode.Should().Be(HttpStatusCode.NotFound);
        await StepUpTestHelper.AddStepUpAsync(c, "Nexora!2026", "permission_change");
        (await c.PostAsJsonAsync("/api/v1/users", new
        {
            email = $"faible-{Guid.NewGuid():N}@test.demo", fullName = "Faible", password = "court",
            initialAssignment = new { roleName = "salesperson", scopeType = "COMPANY", companyId = Pharmacy },
        })).StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        // Un mauvais mot de passe actuel n'est pas un 401 : le client web ne doit pas déconnecter l'utilisateur.
        (await c.PostAsJsonAsync("/api/v1/me/password", new { currentPassword = "pas-le-bon-1", newPassword = "Autre2026-ok" }))
            .StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
    }

    // ---- AUTH-008 : le parcours « oublié » répond pareil que le compte existe ou non ----
    [Fact]
    public async Task Forgot_password_answers_the_same_for_an_unknown_account()
    {
        var c = Client();
        (await c.PostAsJsonAsync("/api/v1/auth/password/forgot", new { email = $"inconnu-{Guid.NewGuid():N}@nulle-part.demo" }))
            .StatusCode.Should().Be(HttpStatusCode.Accepted);
        (await c.PostAsJsonAsync("/api/v1/auth/password/reset", new { token = "0000", newPassword = "Nouveau2026-ok" }))
            .StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
    }

    // ---- SEC-011 / AUTH-010 : la politique de débit « auth » est enfin appliquée ----
    [Fact]
    public async Task Anonymous_auth_endpoints_are_rate_limited_per_client()
    {
        var c = Client();   // hôte neuf : compteur de débit propre à ce test
        var codes = new List<HttpStatusCode>();
        for (var i = 0; i < 12; i++)
            codes.Add((await c.PostAsJsonAsync("/api/v1/auth/password/forgot",
                new { email = $"inconnu-{Guid.NewGuid():N}@nulle-part.demo" })).StatusCode);

        codes.Take(10).Should().OnlyContain(s => s == HttpStatusCode.Accepted);
        codes.Skip(10).Should().OnlyContain(s => s == HttpStatusCode.TooManyRequests);
    }
}
