using FluentAssertions;
using Nexora.Api.Middleware;

namespace Nexora.ApiTests;

/// <summary>
/// Audit 2026-10 éd. 2 : pendant une maintenance, seules les routes d'exploitation, d'authentification, de santé et
/// d'installation restent ouvertes en écriture. La recherche par sous-chaîne exemptait aussi des routes métier dont le
/// chemin contenait « /ready » ou « /health » plus loin.
/// </summary>
public sealed class MaintenanceExemptionTests
{
    [Theory]
    [InlineData("/api/v1/ops/maintenance")]
    [InlineData("/api/v1.0/ops")]
    [InlineData("/api/v1/auth/login")]
    [InlineData("/api/v1/setup/install")]
    [InlineData("/health/ready")]
    [InlineData("/api/v1/ready")]
    [InlineData("/swagger/index.html")]
    public void Operating_routes_stay_open(string path) => MaintenanceModeMiddleware.IsExempt(path).Should().BeTrue();

    [Theory]
    [InlineData("/api/v1/sales/ready-orders")]
    [InlineData("/api/v1/customers/health-check")]
    [InlineData("/api/v1/ged-documents/x/auth/sign")]
    [InlineData("/api/v1/pos/checkout")]
    [InlineData("/api/v1/integrations/ops/run")]
    public void Business_routes_are_suspended(string path) => MaintenanceModeMiddleware.IsExempt(path).Should().BeFalse();
}
