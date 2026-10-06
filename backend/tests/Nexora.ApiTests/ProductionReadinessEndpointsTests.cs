using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Nexora.Api.Setup;

namespace Nexora.ApiTests;

/// <summary>
/// Finalisation phase 18 — production readiness par HTTP : sondes de vivacité et de disponibilité (JSON), refus
/// métier en 400 au lieu de 500, vue « Production », commandes d'exploitation de l'image.
/// </summary>
public sealed class ProductionReadinessEndpointsTests(WebApplicationFactory<Program> factory)
    : IClassFixture<WebApplicationFactory<Program>>
{
    private const string DevConnString =
        "Host=localhost;Port=5432;Database=nexora;Username=nexora_app;Password=nexora_app_dev;Search Path=nexora,public;Include Error Detail=true";
    private const string Company = "dce00000-0000-0000-0000-0000000000c1";

    private HttpClient Client() => factory.WithWebHostBuilder(b =>
        b.ConfigureAppConfiguration((_, cfg) => cfg.AddInMemoryCollection(
            new Dictionary<string, string?> { ["Database:ConnectionString"] = DevConnString }))).CreateClient();

    private async Task<HttpClient> AuthedClient(bool withCompany = true)
    {
        var client = Client();
        var login = await client.PostAsJsonAsync("/api/v1/auth/login", new
        {
            email = "direction@abc-group.demo", password = "Nexora!2026", companyId = Company,
        });
        var token = (await login.Content.ReadFromJsonAsync<TokenShape>())!.AccessToken;
        client.DefaultRequestHeaders.Add("Authorization", $"Bearer {token}");
        if (withCompany) client.DefaultRequestHeaders.Add("X-Company-Id", Company);
        return client;
    }
    private sealed record TokenShape(string AccessToken);

    [Fact]
    public async Task Liveness_and_readiness_are_anonymous_and_readiness_details_each_check()
    {
        var client = Client();
        (await client.GetAsync("/health/live")).StatusCode.Should().Be(HttpStatusCode.OK);

        var ready = await client.GetAsync("/health/ready");
        ready.StatusCode.Should().Be(HttpStatusCode.OK);
        ready.Content.Headers.ContentType!.MediaType.Should().Be("application/json");
        using var json = JsonDocument.Parse(await ready.Content.ReadAsStringAsync());
        var checks = json.RootElement.GetProperty("checks").EnumerateArray()
            .ToDictionary(c => c.GetProperty("name").GetString()!, c => c.GetProperty("status").GetString());
        checks.Should().ContainKeys("database", "migrations");
        checks["database"].Should().Be("Healthy");
        // « Degraded » sur un poste dont une migration a été retouchée après application ; jamais « Unhealthy »
        // (migration en attente) sur la base de développement migrée.
        checks["migrations"].Should().BeOneOf("Healthy", "Degraded");
        json.RootElement.GetRawText().Should().NotContain("Password").And.NotContain("Host=");
    }

    [Fact]
    public async Task An_operation_without_active_company_is_a_bad_request_not_a_server_error()
    {
        var client = await AuthedClient(withCompany: false);
        var response = await client.GetAsync("/api/v1/ops/status");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        problem.GetProperty("title").GetString().Should().Be("tenant.company_required");
        problem.GetProperty("correlationId").GetString().Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task Production_view_requires_a_session_and_reports_version_migrations_and_configuration()
    {
        (await Client().GetAsync("/api/v1/ops/production")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        var client = await AuthedClient();
        var response = await client.GetAsync("/api/v1/ops/production");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync();
        using var json = JsonDocument.Parse(body);
        var root = json.RootElement;

        root.GetProperty("environment").GetString().Should().Be("Development");
        root.GetProperty("version").GetString().Should().NotBeNullOrEmpty();
        var migrations = root.GetProperty("migrations");
        migrations.GetProperty("pending").GetArrayLength().Should().Be(0);
        migrations.GetProperty("applied").GetInt32().Should().Be(migrations.GetProperty("embedded").GetInt32());
        root.GetProperty("checks").EnumerateArray().Select(c => c.GetProperty("key").GetString())
            .Should().Contain(["secrets", "https", "reverse_proxy", "cors", "observability", "structured_logs", "scheduler", "email"]);
        body.Should().NotContain("nexora_app_dev", "aucun secret n'est rendu");
    }

    [Fact]
    public async Task The_image_commands_are_recognised_and_migrate_status_sees_no_pending_migration()
    {
        CliCommands.Detect(["migrate", "--status"]).Should().Be("migrate");
        CliCommands.Detect(["bootstrap"]).Should().Be("bootstrap");
        CliCommands.Detect(["healthcheck"]).Should().Be("healthcheck");
        CliCommands.Detect(["--urls=http://+:8080"]).Should().BeNull();
        CliCommands.Detect([]).Should().BeNull();

        var config = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { ["Database:ConnectionString"] = DevConnString }).Build();
        var code = await CliCommands.RunAsync("migrate", ["migrate", "--status"], config);
        code.Should().BeOneOf(CliCommands.Ok, CliCommands.Inconsistent);   // jamais « migrations en attente »

        var noDatabase = await CliCommands.RunAsync("migrate", ["migrate", "--status"], new ConfigurationBuilder().Build());
        noDatabase.Should().Be(CliCommands.Failed);
        var bootstrapWithoutPassword = await CliCommands.RunAsync("bootstrap", ["bootstrap", "--company", "X"], config);
        bootstrapWithoutPassword.Should().Be(CliCommands.Failed);
    }
}
