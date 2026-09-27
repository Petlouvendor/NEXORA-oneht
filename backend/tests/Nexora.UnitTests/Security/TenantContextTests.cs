using FluentAssertions;
using Nexora.Infrastructure.Tenancy;

namespace Nexora.UnitTests.Security;

public sealed class TenantContextTests
{
    [Fact]
    public void System_scope_toggles_bypass_and_restores()
    {
        var ctx = new TenantContext();
        ctx.BypassRls.Should().BeFalse();

        using (ctx.BeginSystemScope())
        {
            ctx.BypassRls.Should().BeTrue();
            using (ctx.BeginSystemScope())
                ctx.BypassRls.Should().BeTrue();
            ctx.BypassRls.Should().BeTrue();   // portée imbriquée encore ouverte
        }

        ctx.BypassRls.Should().BeFalse();
    }

    [Fact]
    public void Set_scope_replaces_previous_values()
    {
        var ctx = new TenantContext();
        var c1 = Guid.NewGuid();
        var c2 = Guid.NewGuid();

        ctx.SetScope([c1, c2], [], null);
        ctx.CompanyIds.Should().BeEquivalentTo([c1, c2]);

        ctx.SetScope([c1], [], c1);
        ctx.CompanyIds.Should().BeEquivalentTo([c1]);
        ctx.ActiveCompanyId.Should().Be(c1);
    }
}
