using FluentAssertions;
using Nexora.Domain.Catalog;

namespace Nexora.UnitTests.Catalog;

/// <summary>Étiquettes de balance à poids intégré (audit 2026-10, A-015).</summary>
public sealed class ScaleLabelTests
{
    [Fact]
    public void A_restricted_ean13_carries_the_article_prefix_and_the_weight()
    {
        ScaleLabel.TryDecode("2012345012509", out var prefix, out var kg).Should().BeTrue();
        prefix.Should().Be("2012345");
        kg.Should().Be(1.25m);
    }

    [Theory]
    [InlineData("2012345012508")]   // clé de contrôle fausse
    [InlineData("2012345000001")]   // poids nul
    [InlineData("3017620422003")]   // EAN-13 ordinaire
    [InlineData("201234501250")]    // 12 chiffres
    [InlineData("")]
    public void Anything_else_is_not_a_scale_label(string code)
        => ScaleLabel.TryDecode(code, out _, out _).Should().BeFalse();
}
