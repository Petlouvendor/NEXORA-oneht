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

    /// <summary>
    /// Audit 2026-10 éd. 2, A-041 : 10,01 USD + 1,01 USD de taxe à 132,5 → 1 326,32 + 133,82 = 1 460,14 HTG en base,
    /// contre 1 460,15 pour les 11,02 USD encaissés. L'écriture, juste en devise, était refusée pour un centime.
    /// </summary>
    [Fact]
    public async Task A_dollar_sale_at_a_fractional_rate_is_balanced_to_the_cent()
    {
        _fx.ConvertAsync(Arg.Any<Money>(), "HTG", Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
            .Returns(ci => Task.FromResult((new Money(ci.ArgAt<Money>(0).Amount * 132.5m, "HTG"), (Guid?)Guid.NewGuid())));
        var evt = new CashSaleRecorded(Company, Guid.NewGuid(), DateTimeOffset.UnixEpoch, Guid.NewGuid(),
            new Money(10.01m, "USD"), new Money(1.01m, "USD"), Tender);

        var draft = await _sut.BuildAsync(evt, Ctx());

        draft.IsBalanced.Should().BeTrue();
        draft.TotalBaseDebit.Should().Be(1460.15m, "11,02 USD × 132,5");
        draft.Lines.Sum(l => l.BaseCredit).Should().Be(1460.15m);
        draft.Lines.Where(l => l.Credit > 0).Sum(l => l.Credit).Should().Be(11.02m, "les montants en devise ne changent pas");
    }

    [Fact]
    public void Conversion_rounding_never_hides_a_real_imbalance()
    {
        var d = new JournalEntryDraft("OD", new DateOnly(2026, 3, 10), "HTG");
        d.Debit(Guid.NewGuid(), new Money(20.02m, "USD"), new Money(2652.65m, "HTG"));
        d.Credit(Guid.NewGuid(), new Money(10.01m, "USD"), new Money(1326.32m, "HTG"));
        d.Credit(Guid.NewGuid(), new Money(10.01m, "USD"), new Money(1326.32m, "HTG"));
        d.AbsorbConversionRounding();
        d.IsBalanced.Should().BeTrue("un centime d'arrondi sur deux lignes est absorbé");

        var wrong = new JournalEntryDraft("OD", new DateOnly(2026, 3, 10), "HTG");
        wrong.Debit(Guid.NewGuid(), new Money(300m, "USD"), new Money(40_500m, "HTG"));
        wrong.Credit(Guid.NewGuid(), new Money(300m, "USD"), new Money(300m, "HTG"));
        wrong.AbsorbConversionRounding();
        wrong.IsBalanced.Should().BeFalse("un écart de 40 200 n'est pas un arrondi");

        var unequal = new JournalEntryDraft("OD", new DateOnly(2026, 3, 10), "HTG");
        unequal.Debit(Guid.NewGuid(), new Money(10.01m, "USD"), new Money(1326.33m, "HTG"));
        unequal.Credit(Guid.NewGuid(), new Money(10.00m, "USD"), new Money(1326.32m, "HTG"));
        unequal.AbsorbConversionRounding();
        unequal.IsBalanced.Should().BeFalse("déséquilibrée en devise : rien n'est ajusté");
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
