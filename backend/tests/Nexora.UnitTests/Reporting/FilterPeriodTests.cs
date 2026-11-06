using FluentAssertions;
using Nexora.Application.Reporting;
using Nexora.Contracts.Reporting;

namespace Nexora.UnitTests.Reporting;

/// <summary>Audit 2026-10, A-008 : « cette semaine » était vide chaque dimanche (début calculé au lundi suivant).</summary>
public sealed class FilterPeriodTests
{
    // Semaine du lundi 12 au dimanche 18 octobre 2026.
    [Theory]
    [InlineData(12)] [InlineData(13)] [InlineData(14)] [InlineData(15)] [InlineData(16)] [InlineData(17)] [InlineData(18)]
    public void Week_starts_on_the_monday_of_the_current_week(int day)
    {
        var today = new DateOnly(2026, 10, day);
        var (from, to) = FilterPeriod.Resolve(new ReportFilter { Preset = "week" }, today);
        from.Should().Be(new DateOnly(2026, 10, 12));
        to.Should().Be(today);
        from.Should().BeOnOrBefore(to);
    }

    [Fact]
    public void A_custom_period_entered_backwards_is_put_back_in_order()
    {
        var (from, to) = FilterPeriod.Resolve(
            new ReportFilter { From = new DateOnly(2026, 10, 31), To = new DateOnly(2026, 10, 1) }, new DateOnly(2026, 10, 15));
        (from, to).Should().Be((new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 31)));
    }
}
