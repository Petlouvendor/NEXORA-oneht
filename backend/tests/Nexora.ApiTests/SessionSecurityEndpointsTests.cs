using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.IdentityModel.JsonWebTokens;
using Npgsql;

namespace Nexora.ApiTests;

/// <summary>
/// Finalisation phase 17 — connexion → jeton d'accès court → expiration → jeton de rafraîchissement (cookie
/// HttpOnly, rotation) → nouveau jeton d'accès ; révocation, expiration, inactivité, déconnexion, session
/// compromise ; réauthentification forte des actions critiques. Comptes dédiés : ces tests ferment des sessions
/// et ne doivent jamais toucher les comptes partagés par les autres tests.
/// </summary>
public sealed class SessionSecurityEndpointsTests(WebApplicationFactory<Program> factory)
    : IClassFixture<WebApplicationFactory<Program>>
{
    private static readonly string DevConnString = ApiTestDatabase.App;
    private static readonly string AdminConnString = ApiTestDatabase.Admin;
    private const string Pharmacy = "dce00000-0000-0000-0000-0000000000c1";
    private const string Password = "Session!Test2026";

    private HttpClient Client() => factory.WithWebHostBuilder(b =>
        b.ConfigureAppConfiguration((_, cfg) => cfg.AddInMemoryCollection(
            new Dictionary<string, string?> { ["Database:ConnectionString"] = DevConnString })))
        .CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });

    private sealed record TokenShape(string AccessToken, DateTimeOffset? SessionExpiresAt);

    private static async Task Sql(string sql)
    {
        await using var c = new NpgsqlConnection(Environment.GetEnvironmentVariable("NEXORA_IT_ADMIN") is { Length: > 0 } pwd
            ? AdminConnString.Replace("Admin@2023", pwd) : AdminConnString);
        await c.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, c);
        await cmd.ExecuteNonQueryAsync();
    }

    /// <summary>Compte dédié (créé une fois) : rôle « cashier » à la Pharmacie.</summary>
    private async Task EnsureUserAsync(string email)
    {
        var admin = Client();
        var login = await admin.PostAsJsonAsync("/api/v1/auth/login", new { email = "direction@abc-group.demo", password = "Nexora!2026", companyId = Pharmacy });
        var token = (await login.Content.ReadFromJsonAsync<TokenShape>())!.AccessToken;
        admin.DefaultRequestHeaders.Add("Authorization", $"Bearer {token}");
        admin.DefaultRequestHeaders.Add("X-Company-Id", Pharmacy);
        await StepUpTestHelper.AddStepUpAsync(admin, "Nexora!2026", "permission_change");
        var created = await admin.PostAsJsonAsync("/api/v1/users", new
        {
            email, fullName = "Compte de test des sessions", password = Password,
            initialAssignment = new { roleName = "cashier", scopeType = "COMPANY", companyId = Pharmacy, reason = "Tests de session" },
        });
        created.StatusCode.Should().BeOneOf(HttpStatusCode.OK, HttpStatusCode.Conflict);
        // Remise à zéro d'éventuels restes d'un passage précédent.
        await Sql($"UPDATE nexora.app_user SET status = 'active', failed_login_count = 0, locked_until = NULL WHERE email = '{email}'");
        await Sql($"DELETE FROM nexora.step_up_failure WHERE user_id = (SELECT id FROM nexora.app_user WHERE email = '{email}')");
    }

    private async Task<(TokenShape Token, string Cookie, HttpResponseMessage Response)> LoginAsync(HttpClient client, string email)
    {
        var r = await client.PostAsJsonAsync("/api/v1/auth/login", new { email, password = Password, companyId = Pharmacy });
        r.StatusCode.Should().Be(HttpStatusCode.OK);
        return ((await r.Content.ReadFromJsonAsync<TokenShape>())!, RefreshCookie(r)!, r);
    }

    private static string? RefreshCookie(HttpResponseMessage r)
        => r.Headers.TryGetValues("Set-Cookie", out var values)
            ? values.Where(v => v.StartsWith("nx_rt=")).Select(v => v.Split(';')[0]["nx_rt=".Length..]).FirstOrDefault(v => v.Length > 0)
            : null;

    private static async Task<HttpResponseMessage> RefreshAsync(HttpClient client, string cookie)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, "/api/v1/auth/refresh");
        req.Headers.Add("Cookie", $"nx_rt={cookie}");
        return await client.SendAsync(req);
    }

    private static async Task<HttpStatusCode> MeAsync(HttpClient client, string accessToken)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, "/api/v1/me");
        req.Headers.Add("Authorization", $"Bearer {accessToken}");
        return (await client.SendAsync(req)).StatusCode;
    }

    private static async Task<string> TitleAsync(HttpResponseMessage r) => await r.Content.ReadAsStringAsync();

    [Fact]
    public async Task Refresh_rotates_and_a_reused_token_closes_every_session()
    {
        const string email = "session.rotation@abc-group.demo";
        await EnsureUserAsync(email);
        var client = Client();
        var (token, cookie, login) = await LoginAsync(client, email);

        // Le jeton de rafraîchissement n'est jamais lisible par la page.
        var setCookie = login.Headers.GetValues("Set-Cookie").Single(v => v.StartsWith("nx_rt="));
        setCookie.Should().Contain("httponly").And.Contain("path=/api/v1/auth").And.Contain("samesite=strict");
        (await login.Content.ReadAsStringAsync()).Should().NotContain(cookie, "le jeton de rafraîchissement ne figure pas dans la réponse");
        new JsonWebToken(token.AccessToken).GetClaim("sid").Value.Should().NotBeNullOrEmpty();
        token.SessionExpiresAt.Should().NotBeNull();

        var refreshed = await RefreshAsync(client, cookie);
        refreshed.StatusCode.Should().Be(HttpStatusCode.OK);
        var next = (await refreshed.Content.ReadFromJsonAsync<TokenShape>())!;
        var nextCookie = RefreshCookie(refreshed)!;
        next.AccessToken.Should().NotBe(token.AccessToken);
        nextCookie.Should().NotBe(cookie, "rotation : chaque rafraîchissement remet un nouveau jeton");
        (await MeAsync(client, next.AccessToken)).Should().Be(HttpStatusCode.OK);
        new JsonWebToken(next.AccessToken).GetClaim("sid").Value.Should().Be(new JsonWebToken(token.AccessToken).GetClaim("sid").Value);

        // Deux onglets au même instant : refus sans révocation.
        var race = await RefreshAsync(client, cookie);
        race.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await TitleAsync(race)).Should().Contain("auth.refresh_race");
        (await MeAsync(client, next.AccessToken)).Should().Be(HttpStatusCode.OK);

        // Plus tard, l'ancien jeton revient : vol présumé, toutes les sessions tombent.
        await Sql($"UPDATE nexora.auth_refresh_token SET used_at = now() - interval '5 minutes' WHERE used_at IS NOT NULL AND user_id = (SELECT id FROM nexora.app_user WHERE email = '{email}')");
        var reuse = await RefreshAsync(client, cookie);
        reuse.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await TitleAsync(reuse)).Should().Contain("auth.session_compromised");
        (await MeAsync(client, next.AccessToken)).Should().Be(HttpStatusCode.Unauthorized, "le jeton d'accès en cours tombe aussi");
        (await RefreshAsync(client, nextCookie)).StatusCode.Should().Be(HttpStatusCode.Unauthorized, "le jeton légitime suivant aussi");
    }

    [Fact]
    public async Task Logout_idle_and_maximum_lifetime_end_the_session()
    {
        const string email = "session.fin@abc-group.demo";
        await EnsureUserAsync(email);
        var client = Client();

        // Déconnexion : le jeton d'accès et le jeton de rafraîchissement tombent, le cookie est effacé.
        var (token, cookie, _) = await LoginAsync(client, email);
        using (var logout = new HttpRequestMessage(HttpMethod.Post, "/api/v1/me/logout"))
        {
            logout.Headers.Add("Authorization", $"Bearer {token.AccessToken}");
            var r = await client.SendAsync(logout);
            r.StatusCode.Should().Be(HttpStatusCode.NoContent);
            r.Headers.GetValues("Set-Cookie").Should().Contain(v => v.StartsWith("nx_rt=;") || v.Contains("expires=Thu, 01 Jan 1970"));
        }
        (await MeAsync(client, token.AccessToken)).Should().Be(HttpStatusCode.Unauthorized);
        (await RefreshAsync(client, cookie)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        // Inactivité au-delà de la politique (60 min) : plus de rafraîchissement.
        (token, cookie, _) = await LoginAsync(client, email);
        var sid = new JsonWebToken(token.AccessToken).GetClaim("sid").Value;
        await Sql($"UPDATE nexora.user_session SET last_seen_at = now() - interval '3 hours' WHERE id = '{sid}'");
        var idle = await RefreshAsync(client, cookie);
        idle.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await TitleAsync(idle)).Should().Contain("auth.session_idle");

        // Durée maximale atteinte : le jeton d'accès encore valide est refusé aussi.
        (token, cookie, _) = await LoginAsync(client, email);
        sid = new JsonWebToken(token.AccessToken).GetClaim("sid").Value;
        await Sql($"UPDATE nexora.user_session SET expires_at = now() - interval '1 minute' WHERE id = '{sid}'");
        (await MeAsync(client, token.AccessToken)).Should().Be(HttpStatusCode.Unauthorized);
        var expired = await RefreshAsync(client, cookie);
        expired.StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        // Jeton inconnu.
        (await RefreshAsync(client, "pas-un-jeton")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Critical_actions_need_a_recent_strong_confirmation_for_their_category()
    {
        const string email = "session.stepup@abc-group.demo";
        await EnsureUserAsync(email);
        var client = Client();
        var (token, _, _) = await LoginAsync(client, email);
        client.DefaultRequestHeaders.Add("Authorization", $"Bearer {token.AccessToken}");
        client.DefaultRequestHeaders.Add("X-Company-Id", Pharmacy);

        // Mauvais mot de passe : refus ; cinq échecs verrouillent la confirmation, même avec le bon mot de passe.
        var bad = await client.PostAsJsonAsync("/api/v1/security/step-up", new { password = "mauvais", purpose = "payment" });
        bad.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity, "un 401 ferait croire au client que la session a expiré");
        (await TitleAsync(bad)).Should().Contain("security.step_up_failed");

        var ok = await client.PostAsJsonAsync("/api/v1/security/step-up", new { password = Password, purpose = "payment" });
        ok.StatusCode.Should().Be(HttpStatusCode.OK);
        var stepUp = (await ok.Content.ReadFromJsonAsync<StepUpShape>())!.Token;

        // Le jeton d'une catégorie n'ouvre pas une autre catégorie (ni une autre session).
        (await StepUpValidAsync(stepUp, "cancellation")).Should().BeFalse();
        for (var i = 0; i < 5; i++)
            await client.PostAsJsonAsync("/api/v1/security/step-up", new { password = "mauvais", purpose = "payment" });
        var locked = await client.PostAsJsonAsync("/api/v1/security/step-up", new { password = Password, purpose = "payment" });
        locked.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await TitleAsync(locked)).Should().Contain("security.step_up_locked");
    }

    private static async Task<bool> StepUpValidAsync(string token, string purpose)
    {
        await using var c = new NpgsqlConnection(AdminConnString);
        await c.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            "SELECT EXISTS(SELECT 1 FROM nexora.step_up_challenge WHERE token_hash = @h AND purpose = @p AND expires_at > now())", c);
        cmd.Parameters.AddWithValue("h", Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(token))));
        cmd.Parameters.AddWithValue("p", purpose);
        return (bool)(await cmd.ExecuteScalarAsync())!;
    }

    private sealed record StepUpShape(string Token);
}
