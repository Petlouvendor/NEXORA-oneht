using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace Nexora.ApiTests;

/// <summary>
/// Administration SaaS &amp; monétisation (Phase 38 §50, §53-54). Le webhook de paiement est ouvert
/// mais signé ; la vérification de licence offline est publique ; la console est protégée.
/// </summary>
public sealed class PlatformEndpointsTests(WebApplicationFactory<Program> factory)
    : IClassFixture<WebApplicationFactory<Program>>
{
    private static readonly string DevConnString = ApiTestDatabase.App;

    /// <summary>Jeton de test fort : sans jeton configuré, le webhook n'existe pas (audit 2026-10 éd. 2, A-049).</summary>
    private const string WebhookToken = "test-webhook-token-0123456789abcdefghij";

    private HttpClient Client(bool withToken = true) => factory.WithWebHostBuilder(b =>
        b.ConfigureAppConfiguration((_, cfg) => cfg.AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                ["Database:ConnectionString"] = DevConnString,
                ["PlatformBilling:WebhookToken"] = withToken ? WebhookToken : null,
            }))).CreateClient();

    [Fact]
    public async Task Platform_console_requires_authentication()
    {
        (await Client().GetAsync("/api/v1/platform/metrics")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await Client().GetAsync("/api/v1/platform/customers")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Payment_webhook_does_not_exist_without_a_configured_token()
    {
        // A-049 : l'ancien jeton par défaut, publié dans le code, ne vaut plus rien ; sans jeton, la route est fermée.
        foreach (var token in new[] { "dev-platform-webhook-token", "" })
        {
            var req = new HttpRequestMessage(HttpMethod.Post, "/api/v1/billing/webhooks/payment")
            {
                Content = JsonContent.Create(new { eventId = $"evt-{Guid.NewGuid():N}", eventType = "charge.succeeded", amount = 1m, currencyCode = "USD", outcome = "succeeded" }),
            };
            req.Headers.Add("X-Platform-Webhook-Token", token);
            (await Client(withToken: false).SendAsync(req)).StatusCode.Should().Be(HttpStatusCode.NotFound);
        }
    }

    [Fact]
    public async Task Payment_webhook_refuses_the_former_default_token_when_configured()
    {
        var req = new HttpRequestMessage(HttpMethod.Post, "/api/v1/billing/webhooks/payment")
        {
            Content = JsonContent.Create(new { eventId = $"evt-{Guid.NewGuid():N}", eventType = "charge.succeeded", amount = 1m, currencyCode = "USD", outcome = "succeeded" }),
        };
        req.Headers.Add("X-Platform-Webhook-Token", "dev-platform-webhook-token");
        (await Client().SendAsync(req)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Payment_webhook_rejects_a_wrong_token()
    {
        var req = new HttpRequestMessage(HttpMethod.Post, "/api/v1/billing/webhooks/payment")
        {
            Content = JsonContent.Create(new
            {
                eventId = "evt-wrong", eventType = "charge.succeeded", amount = 1m, currencyCode = "USD", outcome = "ambiguous",
            }),
        };
        req.Headers.Add("X-Platform-Webhook-Token", "nope");

        // Le webhook est ouvert (AllowAnonymous) mais le jeton partagé est vérifié (§53).
        (await Client().SendAsync(req)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task License_verify_is_public_and_rejects_a_tampered_token()
    {
        var res = await Client().PostAsJsonAsync("/api/v1/licenses/verify", new { token = "not-a-real-token" });

        res.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await res.Content.ReadAsStringAsync();
        body.Should().Contain("\"valid\":false");
    }

    [Fact]
    public async Task Payment_webhook_with_a_valid_token_is_accepted_and_idempotent()
    {
        var client = Client();
        var payload = new
        {
            eventId = $"evt-api-{Guid.NewGuid():N}", eventType = "charge.disputed",
            provider = "sim", customerRef = "NO-SUCH-REF", amount = 5m, currencyCode = "USD", outcome = "ambiguous",
        };

        var first = await SendWebhook(client, payload);
        var second = await SendWebhook(client, payload);

        first.StatusCode.Should().Be(HttpStatusCode.OK);
        second.StatusCode.Should().Be(HttpStatusCode.OK);
        var b1 = await first.Content.ReadAsStringAsync();
        var b2 = await second.Content.ReadAsStringAsync();
        b1.Should().Contain("\"reviewRequired\":true");
        b2.Should().Be(b1, "un même événement webhook est idempotent (§54)");
    }

    private static Task<HttpResponseMessage> SendWebhook(HttpClient client, object payload)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, "/api/v1/billing/webhooks/payment")
        {
            Content = JsonContent.Create(payload),
        };
        req.Headers.Add("X-Platform-Webhook-Token", WebhookToken);
        return client.SendAsync(req);
    }
}
