using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Nexora.Domain.Common;
using Nexora.Infrastructure.Operations;

namespace Nexora.UnitTests.Operations;

/// <summary>
/// Finalisation phase 18 — configuration de production : une seule lecture partagée par le garde de démarrage, la
/// checklist de mise en production et la vue « Production » ; jamais la valeur d'un secret dans un verdict.
/// </summary>
public sealed class ProductionConfigurationTests
{
    private static readonly string StrongJwt = new('k', 48);
    private static readonly string Key32 = Convert.ToBase64String(new byte[32]);
    private static readonly string DataKey32 = Convert.ToBase64String(Enumerable.Repeat((byte)7, 32).ToArray());
    private const string Db = "Host=postgres;Database=nexora;Username=nexora_app;Password=s3cret-tres-long";

    private static IConfiguration Config(params (string Key, string? Value)[] values)
        => new ConfigurationBuilder().AddInMemoryCollection(   // la dernière valeur d'une clé l'emporte (surcharge)
            values.GroupBy(v => v.Key).ToDictionary(g => g.Key, g => g.Last().Value)).Build();

    private static IConfiguration Production(params (string Key, string? Value)[] extra)
        => Config([("Jwt:SigningKey", StrongJwt), ("Security:MfaEncryptionKey", Key32), ("Security:DataEncryptionKey", DataKey32),
            ("Database:ConnectionString", Db),
            ("PublicUrl", "https://app.exemple.ht"), ("ReverseProxy:Enabled", "true"), ("Cors:Origins:0", "https://app.exemple.ht"),
            ("AllowedHosts", "app.exemple.ht"), ("Observability:OtlpEndpoint", "http://otel-collector:4317"),
            ("Scheduler:Enabled", "true"), ("Notifications:Smtp:Enabled", "true"), .. extra]);

    [Fact]
    public void A_complete_production_configuration_passes_every_check()
        => ProductionConfiguration.Evaluate(Production(), "Production").Should().OnlyContain(c => c.Status == "pass");

    /// <summary>Audit 2026-10 éd. 2 : une seule clé pour MFA et données personnelles est signalée (sans bloquer).</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("mfa")]
    public void A_data_key_missing_or_equal_to_the_mfa_key_is_a_warning(string? dataKey)
    {
        var check = ProductionConfiguration.Evaluate(Production(("Security:DataEncryptionKey", dataKey is null ? null : Key32)), "Production")
            .Single(c => c.Key == "data_key");
        check.Status.Should().Be("warn");
        check.Detail.Should().NotContain(Key32, "jamais la valeur d'un secret dans un verdict");
    }

    [Fact]
    public void The_default_application_role_password_is_refused()
    {
        ProductionConfiguration.SecretProblems(Production(("Database:ConnectionString",
                "Host=db;Username=nexora_app;Password=change_me_in_deployment")))
            .Should().ContainSingle().Which.Should().Contain("mot de passe par défaut");
    }

    [Fact]
    public void An_invalid_data_encryption_key_is_refused_but_its_absence_is_allowed()
    {
        ProductionConfiguration.SecretProblems(Production()).Should().BeEmpty();
        ProductionConfiguration.SecretProblems(Production(("Security:DataEncryptionKey", "pas-du-base64!")))
            .Should().ContainSingle().Which.Should().Contain("DataEncryptionKey");
    }

    [Fact]
    public void A_weak_platform_webhook_token_is_refused_but_its_absence_is_allowed()
    {
        // Audit 2026-10 éd. 2, A-049 : sans jeton, le webhook de facturation est fermé ; le jeton publié dans le code
        // (dev-platform-webhook-token) ou un jeton court bloquent le démarrage.
        ProductionConfiguration.SecretProblems(Production()).Should().BeEmpty();
        ProductionConfiguration.SecretProblems(Production(("PlatformBilling:WebhookToken", "dev-platform-webhook-token")))
            .Should().ContainSingle().Which.Should().Contain("WebhookToken");
        ProductionConfiguration.SecretProblems(Production(("PlatformBilling:WebhookToken", "court")))
            .Should().ContainSingle().Which.Should().Contain("WebhookToken");
        ProductionConfiguration.SecretProblems(Production(("PlatformBilling:WebhookToken", new string('k', 48))))
            .Should().BeEmpty();
    }

    [Fact]
    public void Secret_values_never_appear_in_the_verdicts()
    {
        var checks = ProductionConfiguration.Evaluate(Production(("Jwt:SigningKey", "dev-only-insecure-key-de-test-0123456789")), "Production");
        checks.Single(c => c.Key == "secrets").Status.Should().Be("fail");
        checks.Should().NotContain(c => c.Detail.Contains("dev-only-insecure-key-de-test") || c.Detail.Contains("s3cret"));
    }

    [Fact]
    public void Without_https_production_fails_but_staging_only_warns()
    {
        var plain = Production(("PublicUrl", "http://app.exemple.ht"));
        ProductionConfiguration.Evaluate(plain, "Production").Single(c => c.Key == "https").Status.Should().Be("fail");
        ProductionConfiguration.Evaluate(plain, "Staging").Single(c => c.Key == "https").Status.Should().Be("warn");
    }

    [Fact]
    public void Missing_proxy_collector_and_json_logs_are_reported()
    {
        var checks = ProductionConfiguration.Evaluate(Production(("ReverseProxy:Enabled", "false"),
            ("Observability:OtlpEndpoint", ""), ("Observability:LogFormat", "text"), ("Cors:Origins:0", "http://localhost:5173")),
            "Production").ToDictionary(c => c.Key);
        checks["reverse_proxy"].Status.Should().Be("warn");
        checks["observability"].Status.Should().Be("warn");
        checks["structured_logs"].Status.Should().Be("warn");
        checks["cors"].Detail.Should().Contain("http://localhost:5173");
    }

    [Fact]
    public void Logs_default_to_json_outside_development_and_the_standard_otlp_variable_is_honoured()
    {
        ProductionConfiguration.LogFormat(Config(), isDevelopment: false).Should().Be("json");
        ProductionConfiguration.LogFormat(Config(), isDevelopment: true).Should().Be("text");
        ProductionConfiguration.OtlpEndpoint(Config(("OTEL_EXPORTER_OTLP_ENDPOINT", "http://collector:4317")))
            .Should().Be("http://collector:4317");
    }

    [Fact]
    public void A_login_rate_limit_widened_for_tests_fails_in_production()
    {
        ProductionConfiguration.Evaluate(Production(("Security:LoginRateLimitPerMinute", "120")), "Production")
            .Single(c => c.Key == "login_rate_limit").Status.Should().Be("fail");
        ProductionConfiguration.Evaluate(Production(), "Production")
            .Single(c => c.Key == "login_rate_limit").Status.Should().Be("pass");
    }

    [Fact]
    public void A_missing_company_header_is_a_bad_request_not_an_internal_error()
    {
        var error = TenantErrors.ActiveCompanyRequired().Error;
        error.Kind.Should().Be(ErrorKind.BadRequest);
        error.Code.Should().Be("tenant.company_required");
    }
}
