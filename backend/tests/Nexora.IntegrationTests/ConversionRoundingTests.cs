using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Nexora.Application.Finance.Engine;
using Nexora.Application.Finance.Events;
using Nexora.Application.Finance.Ledger;
using Nexora.Contracts.Finance;
using Nexora.Domain.Common;
using Nexora.Infrastructure.Persistence;

namespace Nexora.IntegrationTests;

/// <summary>
/// Audit 2026-10 éd. 2, A-041 (constaté à l'exécution) : les lignes d'une écriture en devise étaient converties et
/// arrondies une à une ; à un taux non entier, la somme des bases différait d'un centime et l'écriture, juste en devise,
/// était refusée (« 2652.6500 &lt;&gt; 2652.6400 », message PostgreSQL brut). Les taux de démonstration étant entiers, le
/// défaut n'apparaissait pas : ces tests utilisent la journée à 132,5 de la fixture.
/// </summary>
[Collection("finance")]
public sealed class ConversionRoundingTests(FinanceEngineFixture fx)
{
    private static readonly Guid Company = Guid.Parse("dce00000-0000-0000-0000-0000000000c1");
    private static readonly Guid BankUsd = Guid.Parse("dce00000-0000-0000-0000-00000000a052");

    private AsyncServiceScope Scope() => fx.Provider.CreateAsyncScope();

    private sealed record Totals(decimal Debit, decimal Credit, decimal BaseDebit, decimal BaseCredit);

    private async Task<Totals> TotalsAsync(Guid entryId)
    {
        await using var s = Scope();
        return await s.ServiceProvider.GetRequiredService<NexoraDbContext>().Database.SqlQuery<Totals>($"""
            SELECT SUM(debit) AS "Debit", SUM(credit) AS "Credit", SUM(base_debit) AS "BaseDebit", SUM(base_credit) AS "BaseCredit"
            FROM nexora.journal_entry_line WHERE journal_entry_id = {entryId}
            """).SingleAsync();
    }

    [Fact]
    public async Task A_dollar_sale_with_tax_at_a_fractional_rate_is_posted()
    {
        Guid entry;
        await using (var s = Scope())
        {
            var r = await s.ServiceProvider.GetRequiredService<IFinancialEngine>().HandleAsync(new CashSaleRecorded(
                Company, Guid.NewGuid(), FinanceEngineFixture.FractionalUsdRateDay, Guid.NewGuid(),
                new Money(10.01m, "USD"), new Money(1.01m, "USD"), BankUsd));
            r.IsSuccess.Should().BeTrue(r.Error?.Message);
            entry = r.Value!.JournalEntryId;
        }

        var t = await TotalsAsync(entry);
        (t.Debit, t.Credit).Should().Be((11.02m, 11.02m));
        (t.BaseDebit, t.BaseCredit).Should().Be((1460.15m, 1460.15m), "11,02 USD × 132,5 ; avant : crédits à 1 460,14");
    }

    [Fact]
    public async Task A_manual_dollar_entry_split_in_two_lines_is_posted()
    {
        await using var s = Scope();
        var journal = s.ServiceProvider.GetRequiredService<IManualJournalService>();
        var draft = await journal.CreateDraftAsync(new CreateJournalEntryRequest("OD",
            DateOnly.FromDateTime(FinanceEngineFixture.FractionalUsdRateDay.UtcDateTime), "USD", "Répartition en dollars",
            [new JournalEntryLineInput("5711", 20.02m, 0m), new JournalEntryLineInput("7011", 0m, 10.01m),
             new JournalEntryLineInput("7011", 0m, 10.01m)]));
        draft.IsSuccess.Should().BeTrue(draft.Error?.Message);

        var posted = await journal.PostAsync(draft.Value);
        posted.IsSuccess.Should().BeTrue(posted.Error?.Message);

        var t = await TotalsAsync(draft.Value);
        t.BaseDebit.Should().Be(t.BaseCredit).And.Be(2652.65m, "20,02 USD × 132,5 ; avant : 409 « 2652.6500 <> 2652.6400 »");
    }
}
