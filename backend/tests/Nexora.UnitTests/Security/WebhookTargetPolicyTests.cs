using System.Net;
using FluentAssertions;
using Nexora.Infrastructure.Integrations;
using Xunit;

namespace Nexora.UnitTests.Security;

/// <summary>Protection SSRF des webhooks sortants (analyse exhaustive 2026-10, §6.5).</summary>
public sealed class WebhookTargetPolicyTests
{
    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("10.1.2.3")]
    [InlineData("172.16.0.1")]
    [InlineData("172.31.255.255")]
    [InlineData("192.168.1.10")]
    [InlineData("169.254.169.254")]   // métadonnées du nuage
    [InlineData("100.64.0.1")]
    [InlineData("0.0.0.0")]
    [InlineData("224.0.0.1")]
    [InlineData("::1")]
    [InlineData("fe80::1")]
    [InlineData("fd00::1")]
    [InlineData("::ffff:127.0.0.1")]
    public void Internal_addresses_are_not_public(string address)
        => WebhookTargetPolicy.IsPublic(IPAddress.Parse(address)).Should().BeFalse();

    [Theory]
    [InlineData("8.8.8.8")]
    [InlineData("172.32.0.1")]
    [InlineData("193.168.1.1")]
    [InlineData("2001:4860:4860::8888")]
    public void Internet_addresses_are_public(string address)
        => WebhookTargetPolicy.IsPublic(IPAddress.Parse(address)).Should().BeTrue();

    [Theory]
    [InlineData("http://127.0.0.1/hook")]
    [InlineData("http://localhost:8080/hook")]
    [InlineData("http://169.254.169.254/latest/meta-data/")]
    [InlineData("https://[::1]/hook")]
    [InlineData("https://erp.internal/hook")]
    [InlineData("https://user:pass@example.com/hook")]
    [InlineData("ftp://example.com/hook")]
    [InlineData("pas une url")]
    public void Internal_or_malformed_targets_are_refused(string url)
        => WebhookTargetPolicy.Validate(url, allowPrivate: false).Should().NotBeNull();

    [Theory]
    [InlineData("https://hooks.example.com/nexora")]
    [InlineData("https://sim.webhook.local/ok")]
    public void Public_and_simulation_targets_are_accepted(string url)
        => WebhookTargetPolicy.Validate(url, allowPrivate: false).Should().BeNull();

    [Fact]
    public void An_on_premise_installation_may_allow_its_local_network()
        => WebhookTargetPolicy.Validate("http://192.168.1.20/erp", allowPrivate: true).Should().BeNull();

    [Fact]
    public async Task A_name_resolving_to_an_internal_address_is_blocked_at_connection_time()
    {
        // « localhost » se résout localement vers la boucle : c'est le cas d'un nom public qui serait redirigé par le
        // DNS vers une adresse interne (rebinding) — le contrôle à la connexion le bloque, même sans contrôle d'URL.
        using var http = new HttpClient(WebhookTargetPolicy.CreateHandler(allowPrivate: false));
        var act = () => http.GetAsync("http://localhost:9/");
        (await act.Should().ThrowAsync<HttpRequestException>()).Which.Message.Should().Contain("Adresse interne refusée");
    }
}
