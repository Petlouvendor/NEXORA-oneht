using System.Net;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace Nexora.ApiTests;

/// <summary>
/// Endpoints d'exploitation &amp; d'observabilité (Phase 37 §46, §56). <c>/health</c>, <c>/ready</c> et
/// <c>/metrics</c> sont ouverts (scraping protégé au niveau réseau) ; <c>/system/status</c> exige une session.
/// </summary>
public sealed class OperationsEndpointsTests(WebApplicationFactory<Program> factory)
    : IClassFixture<WebApplicationFactory<Program>>
{
    private const string DevConnString =
        "Host=localhost;Port=5432;Database=nexora;Username=nexora_app;Password=nexora_app_dev;Search Path=nexora,public;Include Error Detail=true";

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

    [Fact]
    public async Task System_status_requires_authentication()
    {
        (await Client().GetAsync("/api/v1/system/status")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }
}
