using FluentAssertions;
using NSubstitute;
using Nexora.Application.Finance.Abstractions;
using Nexora.Application.Finance.Events;
using Nexora.Application.Finance.Posting;
using Nexora.Domain.Accounting;
using Nexora.Domain.Common;

namespace Nexora.UnitTests.Finance;

public sealed class PostingRuleSetTests
{
    private readonly IAccountResolver _accounts = Substitute.For<IAccountResolver>();
    private readonly IExchangeRateProvider _fx = Substitute.For<IExchangeRateProvider>();
    private readonly PostingRuleSet _sut = new();

    private static readonly Guid Tender = Guid.NewGuid();
    private static readonly Guid Company = Guid.NewGuid();

    public PostingRuleSetTests()
    {
        _accounts.ResolveAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(ci => KnownAccount(ci.ArgAt<string>(0)));
        _accounts.ResolveTenderAccountAsync(Tender, Arg.Any<CancellationToken>()).Returns(Guid.NewGuid());
        // Devise fonctionnelle = HTG → conversion identité pour HTG (le contexte court-circuite le fx).
    }

    private static readonly Dictionary<string, Guid> _map = new();
    private static Guid KnownAccount(string key)
    {
        if (!_map.TryGetValue(key, out var id)) { id = Guid.NewGuid(); _map[key] = id; }
        return id;
    }

    private PostingContext Ctx() => new(_accounts, _fx, "HTG", 2);

    [Fact]
    public async Task Cash_sale_with_tax_produces_balanced_three_line_entry()
    {
        var evt = new CashSaleRecorded(Company, Guid.NewGuid(), DateTimeOffset.UnixEpoch, Guid.NewGuid(),
            new Money(1000m, "HTG"), new Money(100m, "HTG"), Tender);

        var draft = await _sut.BuildAsync(evt, Ctx());

        draft.Lines.Should().HaveCount(3);
        draft.IsBalanced.Should().BeTrue();
        draft.TotalBaseDebit.Should().Be(1100m);
        draft.Lines.Where(l => l.Credit > 0).Sum(l => l.Credit).Should().Be(1100m);
    }

    [Fact]
    public async Task Credit_sale_debits_receivable_with_third_party()
    {
        var customer = Guid.NewGuid();
        var evt = new CreditSaleRecorded(Company, Guid.NewGuid(), DateTimeOffset.UnixEpoch, Guid.NewGuid(), customer,
            new Money(5000m, "HTG"), Money.Zero("HTG"), null);

        var draft = await _sut.BuildAsync(evt, Ctx());

        draft.IsBalanced.Should().BeTrue();
        draft.Lines.Should().ContainSingle(l => l.Debit == 5000m && l.ThirdPartyId == customer);
    }

    [Fact]
    public async Task Cash_shortage_debits_short_over_and_credits_cash()
    {
        var evt = new CashCountRecorded(Company, Guid.NewGuid(), DateTimeOffset.UnixEpoch, Guid.NewGuid(), Tender,
            new Money(5000m, "HTG"), new Money(4900m, "HTG"));

        var draft = await _sut.BuildAsync(evt, Ctx());

        draft.IsBalanced.Should().BeTrue();
        draft.Lines.Should().HaveCount(2);
        draft.TotalBaseDebit.Should().Be(100m);
    }

    [Fact]
    public async Task Unknown_event_type_is_rejected()
    {
        var bogus = new UnknownEvent(Company, Guid.NewGuid(), DateTimeOffset.UnixEpoch);
        await _sut.Invoking(s => s.BuildAsync(bogus, Ctx()))
            .Should().ThrowAsync<NotSupportedException>();
    }

    private sealed record UnknownEvent(Guid CompanyId, Guid OperationUuid, DateTimeOffset OccurredAt)
        : FinancialEvent(CompanyId, OperationUuid, OccurredAt);
}
