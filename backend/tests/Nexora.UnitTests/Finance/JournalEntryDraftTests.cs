using FluentAssertions;
using Nexora.Domain.Accounting;
using Nexora.Domain.Common;

namespace Nexora.UnitTests.Finance;

public sealed class MoneyTests
{
    [Fact]
    public void Operations_require_same_currency()
    {
        var a = new Money(100m, "HTG");
        var b = new Money(50m, "USD");
        FluentActions.Invoking(() => a.Add(b)).Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Add_and_subtract_are_exact_decimals()
    {
        (new Money(0.1m, "HTG") + new Money(0.2m, "HTG")).Amount.Should().Be(0.3m);
        (new Money(1000m, "HTG") - new Money(999.99m, "HTG")).Amount.Should().Be(0.01m);
    }
}

public sealed class JournalEntryDraftTests
{
    private static readonly Guid A = Guid.NewGuid();
    private static readonly Guid B = Guid.NewGuid();

    [Fact]
    public void Balanced_two_line_entry_passes()
    {
        var d = new JournalEntryDraft("VT", new DateOnly(2026, 1, 1), "HTG");
        d.Debit(A, new Money(1100m, "HTG"), new Money(1100m, "HTG"));
        d.Credit(B, new Money(1100m, "HTG"), new Money(1100m, "HTG"));

        d.IsBalanced.Should().BeTrue();
        d.Invoking(x => x.EnsureBalanced()).Should().NotThrow();
    }

    [Fact]
    public void Unbalanced_entry_is_rejected()
    {
        var d = new JournalEntryDraft("VT", new DateOnly(2026, 1, 1), "HTG");
        d.Debit(A, new Money(100m, "HTG"), new Money(100m, "HTG"));
        d.Credit(B, new Money(90m, "HTG"), new Money(90m, "HTG"));

        d.Invoking(x => x.EnsureBalanced()).Should().Throw<InvalidOperationException>()
            .WithMessage("*déséquilibrée*");
    }

    [Fact]
    public void Single_line_entry_is_rejected()
    {
        var d = new JournalEntryDraft("VT", new DateOnly(2026, 1, 1), "HTG");
        d.Debit(A, new Money(100m, "HTG"), new Money(100m, "HTG"));

        d.Invoking(x => x.EnsureBalanced()).Should().Throw<InvalidOperationException>()
            .WithMessage("*2 lignes*");
    }

    [Fact]
    public void Negative_debit_is_folded_into_a_credit()
    {
        var d = new JournalEntryDraft("VT", new DateOnly(2026, 1, 1), "HTG");
        d.Debit(A, new Money(-100m, "HTG"), new Money(-100m, "HTG"));

        d.Lines.Should().ContainSingle();
        d.Lines[0].Credit.Should().Be(100m);
        d.Lines[0].Debit.Should().Be(0m);
    }
}
