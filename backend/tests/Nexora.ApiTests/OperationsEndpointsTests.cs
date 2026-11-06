using System.Net;
using FluentAssertions;
using System.Net.Http.Headers;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Nexora.ApiTests;

/// <summary>
/// Endpoints d'exploitation &amp; d'observabilité (Phase 37 §46, §56). <c>/health</c>, <c>/ready</c> et
/// <c>/metrics</c> sont anonymes ; <c>/metrics</c> n'est servi qu'à une adresse interne ou sur jeton (audit 2026-10,
/// A-024) ; <c>/system/status</c> exige une session.
/// </summary>
public sealed class OperationsEndpointsTests(WebApplicationFactory<Program> factory)
    : IClassFixture<WebApplicationFactory<Program>>
{
    private static readonly string DevConnString = ApiTestDatabase.App;

    private HttpClient Client() => factory.WithWebHostBuilder(b =>
        b.ConfigureAppConfiguration((_, cfg) => cfg.AddInMemoryCollection(
            new Dictionary<string, string?> { ["Database:ConnectionString"] = DevConnString }))).CreateClient();

    [Fact]
    public async Task Ready_is_anonymous_and_reports_healthy()
    {
        var res = await Client().GetAsync("/api/v1/ready");

        res.StatusCode.Should().Be(HttpStatusCode.OK);
        (await res.Content.ReadAsStringAsync()).Should().Contain("Healthy");
    }

    [Fact]
    public async Task Health_alias_under_api_v1_is_anonymous()
    {
        (await Client().GetAsync("/api/v1/health")).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Metrics_exposes_prometheus_text_without_a_session()
    {
        var res = await Client().GetAsync("/api/v1/metrics");

        res.StatusCode.Should().Be(HttpStatusCode.OK);
        res.Content.Headers.ContentType!.MediaType.Should().Be("text/plain");
        var body = await res.Content.ReadAsStringAsync();
        body.Should().Contain("# TYPE nexora_up gauge");
        body.Should().Contain("\nnexora_up 1");
        body.Should().Contain("nexora_active_companies");
    }

    /// <summary>Client vu depuis une adresse publique (ce que donne le proxy après résolution de X-Forwarded-For).</summary>
    private HttpClient PublicClient(string? token = null) => factory.WithWebHostBuilder(b =>
    {
        b.ConfigureAppConfiguration((_, cfg) => cfg.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Database:ConnectionString"] = DevConnString,
            ["Metrics:Token"] = token,
        }));
        b.ConfigureServices(sv => sv.AddSingleton<IStartupFilter, PublicAddressFilter>());
    }).CreateClient();

    private sealed class PublicAddressFilter : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use((ctx, nxt) => { ctx.Connection.RemoteIpAddress = IPAddress.Parse("8.8.8.8"); return nxt(); });
            next(app);
        };
    }

    [Theory]
    [InlineData("/api/v1/metrics")]
    [InlineData("/api/v1.0/metrics")]
    [InlineData("/API/V1/METRICS/")]
    public async Task Metrics_are_hidden_from_a_public_address_whatever_the_path_spelling(string path)
    {
        (await PublicClient().GetAsync(path)).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Metrics_are_served_to_a_public_address_holding_the_collection_token()
    {
        const string token = "collecteur-0123456789abcdef";
        var client = PublicClient(token);
        (await client.GetAsync("/api/v1/metrics")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        (await client.GetAsync("/api/v1/metrics")).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task System_status_requires_authentication()
    {
        (await Client().GetAsync("/api/v1/system/status")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }
}
