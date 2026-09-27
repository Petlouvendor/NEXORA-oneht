using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Nexora.Api.Security;

namespace Nexora.ApiTests;

/// <summary>
/// Résumé fonctionnel §18, §21 : hors développement, l'API ne démarre jamais avec un secret absent ou
/// retombant sur la valeur de repli écrite dans le code.
/// </summary>
public sealed class StartupSecretsGuardTests(WebApplicationFactory<Program> factory)
    : IClassFixture<WebApplicationFactory<Program>>
{
    private static IConfiguration Config(params (string Key, string? Value)[] values)
        => new ConfigurationBuilder().AddInMemoryCollection(values.ToDictionary(v => v.Key, v => v.Value)).Build();

    private static readonly string StrongJwt = new('k', 48);
    private static readonly string MfaKey = Convert.ToBase64String(new byte[32]);
    private const string Db = "Host=db;Database=nexora;Username=nexora_app;Password=x";

    [Fact]
    public void Complete_production_configuration_passes()
        => StartupSecretsGuard.Problems(Config(("Jwt:SigningKey", StrongJwt), ("Security:MfaEncryptionKey", MfaKey),
            ("Database:ConnectionString", Db))).Should().BeEmpty();

    [Theory]
    [InlineData("", "Jwt:SigningKey absente")]
    [InlineData("court", "Jwt:SigningKey trop courte")]
    [InlineData("dev-only-insecure-signing-key-please-override-via-secrets-32b+", "de développement")]
    public void Weak_or_missing_jwt_key_is_reported(string key, string expected)
        => StartupSecretsGuard.Problems(Config(("Jwt:SigningKey", key), ("Security:MfaEncryptionKey", MfaKey),
            ("Database:ConnectionString", Db))).Should().ContainSingle().Which.Should().Contain(expected);

    [Fact]
    public void Missing_mfa_key_and_connection_string_are_reported()
        => StartupSecretsGuard.Problems(Config(("Jwt:SigningKey", StrongJwt)))
            .Should().HaveCount(2).And.Contain(p => p.Contains("MfaEncryptionKey")).And.Contain(p => p.Contains("ConnectionString"));

    [Fact]
    public void The_api_refuses_to_start_in_production_with_a_database_but_without_secrets()
    {
        var production = factory.WithWebHostBuilder(b =>
        {
            b.UseEnvironment("Production");
            b.UseSetting("Database:ConnectionString", Db);
            b.UseSetting("Jwt:SigningKey", "");
            b.UseSetting("Security:MfaEncryptionKey", "");
        });
        var start = () => production.CreateClient();
        start.Should().Throw<InvalidOperationException>().WithMessage("*Démarrage refusé*");
    }

    /// <summary>Résumé fonctionnel §20 : sans AUCUNE configuration, l'API démarre en mode installation —
    /// seul l'assistant répond, toutes les autres routes sont fermées (503) : aucune donnée n'est exposée.</summary>
    [Fact]
    public async Task Production_without_any_configuration_starts_in_setup_mode_only()
    {
        var production = factory.WithWebHostBuilder(b =>
        {
            b.UseEnvironment("Production");
            b.UseSetting("Database:ConnectionString", "");
            b.UseSetting("Jwt:SigningKey", "");
            b.UseSetting("Security:MfaEncryptionKey", "");
            b.UseSetting("Install:ConfigPath", Path.Combine(Path.GetTempPath(), $"nexora-absent-{Guid.NewGuid():N}", "nexora.install.json"));
        });
        var client = production.CreateClient();
        (await client.GetStringAsync("/api/v1/setup/status")).Should().Contain("\"setupRequired\":true");
        (await client.GetAsync("/api/v1/me")).StatusCode.Should().Be(System.Net.HttpStatusCode.ServiceUnavailable);
        (await client.PostAsync("/api/v1/auth/login", null)).StatusCode.Should().Be(System.Net.HttpStatusCode.ServiceUnavailable);
    }
}
