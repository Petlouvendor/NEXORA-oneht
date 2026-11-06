using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Nexora.Application.Abstractions;
using Nexora.Application.Finance.Engine;
using Nexora.Application.Finance.Events;
using Nexora.Application.Finance.Ledger;
using Nexora.Application.Finance.Reports;
using Nexora.Application.Finance.Reversals;
using Nexora.Domain.Common;
using Nexora.Infrastructure.Persistence;
using Npgsql;

namespace Nexora.IntegrationTests;

/// <summary>
/// Audit 2026-10 éd. 2, A-043 (CRITIQUE, constaté à l'exécution) : l'extourne passe l'écriture d'origine en
/// « reversed » et crée un miroir « posted ». Balance, grand livre, états, aperçu de clôture ne comptaient que
/// « posted » : l'origine disparaissait, le miroir restait — une vente de 1 000 extournée laissait la caisse à −1 000.
/// Entreprise neuve : les montants attendus ne dépendent d'aucune autre écriture.
/// </summary>
[Collection("finance")]
public sealed class ReversedEntriesInStatementsTests(FinanceEngineFixture fx)
{
    private static readonly DateOnly YearStart = new(2026, 1, 1), YearEnd = new(2026, 12, 31);

    private sealed record NewCompany(Guid Id, Guid Cash, Guid Period);

    private async Task<NewCompany> NewCompanyAsync()
    {
        var id = Guid.NewGuid();
        await using var c = new NpgsqlConnection(fx.TestConnectionString);
        await c.OpenAsync();
        await using (var cmd = new NpgsqlCommand($"""
            INSERT INTO nexora.company (id, name, default_currency_id)
            VALUES ('{id}', 'Extournes {id:N}', (SELECT id FROM nexora.currency WHERE code = 'HTG'));
            SELECT nexora.bootstrap_company_defaults('{id}', (SELECT id FROM nexora.currency WHERE code = 'HTG'), 2026);
            """, c))
            await cmd.ExecuteNonQueryAsync();
        async Task<Guid> One(string sql)
        {
            await using var q = new NpgsqlCommand(sql, c);
            return (Guid)(await q.ExecuteScalarAsync())!;
        }
        return new NewCompany(id,
            await One($"SELECT id FROM nexora.financial_account WHERE company_id = '{id}' AND account_type = 'cash' LIMIT 1"),
            await One($"SELECT id FROM nexora.accounting_period WHERE company_id = '{id}'"));
    }

    private async Task ArchiveAsync(Guid company)
    {
        await using var c = new NpgsqlConnection(fx.TestConnectionString);
        await c.OpenAsync();
        await using var cmd = new NpgsqlCommand($"UPDATE nexora.company SET status = 'archived' WHERE id = '{company}'", c);
        await cmd.ExecuteNonQueryAsync();
    }

    private AsyncServiceScope Scope(Guid company)
    {
        var s = fx.Provider.CreateAsyncScope();
        ((TestTenantContext)s.ServiceProvider.GetRequiredService<ITenantContext>()).SetScope([company], [], company);
        return s;
    }

    /// <summary>Vente au comptant de 1 000 HTG le 2 mars, puis son extourne ; renvoie l'écriture d'origine.</summary>
    private async Task<Guid> SaleThenReversalAsync(NewCompany co)
    {
        Guid entry;
        await using (var s = Scope(co.Id))
        {
            var r = await s.ServiceProvider.GetRequiredService<IFinancialEngine>().HandleAsync(new CashSaleRecorded(
                co.Id, Guid.NewGuid(), new DateTimeOffset(2026, 3, 2, 15, 0, 0, TimeSpan.Zero), Guid.NewGuid(),
                new Money(1_000m, "HTG"), Money.Zero("HTG"), co.Cash));
            r.IsSuccess.Should().BeTrue(r.Error?.Message);
            entry = r.Value!.JournalEntryId;
        }
        await using (var s = Scope(co.Id))
        {
            var rev = await s.ServiceProvider.GetRequiredService<IReversalService>()
                .ReverseAsync(entry, "Vente saisie par erreur", new DateOnly(2026, 3, 3));
            rev.IsSuccess.Should().BeTrue(rev.Error?.Message);
        }
        return entry;
    }

    [Fact]
    public async Task A_reversed_entry_and_its_reversal_cancel_out_in_every_statement()
    {
        var co = await NewCompanyAsync();
        try
        {
            await SaleThenReversalAsync(co);
            await using var s = Scope(co.Id);
            var reports = s.ServiceProvider.GetRequiredService<IFinancialReports>();

            var tb = await reports.TrialBalanceAsync(YearEnd);
            tb.Rows.Where(r => r.Debit != r.Credit).Should().BeEmpty(
                "une vente extournée ne laisse aucun solde (avant : caisse créditrice de 1 000, produits débiteurs de 1 000)");
            tb.TotalDebit.Should().Be(tb.TotalCredit);

            var cash = await reports.GeneralLedgerAsync("5711", YearStart, YearEnd);
            cash.Lines.Should().HaveCount(2, "l'écriture d'origine et son extourne");
            cash.ClosingBalance.Should().Be(0m);

            (await reports.IncomeStatementAsync(YearStart, YearEnd)).NetResult.Should().Be(0m);
            (await reports.BalanceSheetAsync(YearEnd)).Result.Should().Be(0m);

            var close = await s.ServiceProvider.GetRequiredService<IFiscalYearService>().PreviewCloseAsync(co.Period);
            close.IsSuccess.Should().BeTrue(close.Error?.Message);
            (close.Value!.Revenue, close.Value.NetResult).Should().Be((0m, 0m));
        }
        finally { await ArchiveAsync(co.Id); }
    }

    [Fact]
    public async Task A_reversed_entry_can_no_longer_be_altered_in_the_database()
    {
        var co = await NewCompanyAsync();
        try
        {
            var entry = await SaleThenReversalAsync(co);
            await using var c = new NpgsqlConnection(fx.TestConnectionString);
            await c.OpenAsync();

            foreach (var sql in new[]
            {
                $"UPDATE nexora.journal_entry SET entry_date = entry_date + 1 WHERE id = '{entry}'",
                $"UPDATE nexora.journal_entry SET status = 'posted' WHERE id = '{entry}'",
                $"UPDATE nexora.journal_entry SET status = 'cancelled' WHERE id = '{entry}'",
                $"UPDATE nexora.journal_entry_line SET debit = debit + 1, base_debit = base_debit + 1 WHERE journal_entry_id = '{entry}' AND debit > 0",
                $"DELETE FROM nexora.journal_entry_line WHERE journal_entry_id = '{entry}'",
            })
            {
                await using var cmd = new NpgsqlCommand(sql, c);
                var act = () => cmd.ExecuteNonQueryAsync();
                (await act.Should().ThrowAsync<PostgresException>(sql)).Which.SqlState.Should().Be("23514");
            }
        }
        finally { await ArchiveAsync(co.Id); }
    }

    [Fact]
    public async Task A_posted_entry_cannot_leave_the_ledger_without_a_reversal()
    {
        var co = await NewCompanyAsync();
        try
        {
            Guid entry;
            await using (var s = Scope(co.Id))
            {
                var r = await s.ServiceProvider.GetRequiredService<IFinancialEngine>().HandleAsync(new CashSaleRecorded(
                    co.Id, Guid.NewGuid(), new DateTimeOffset(2026, 3, 2, 15, 0, 0, TimeSpan.Zero), Guid.NewGuid(),
                    new Money(500m, "HTG"), Money.Zero("HTG"), co.Cash));
                r.IsSuccess.Should().BeTrue(r.Error?.Message);
                entry = r.Value!.JournalEntryId;
            }
            await using var c = new NpgsqlConnection(fx.TestConnectionString);
            await c.OpenAsync();
            await using var cmd = new NpgsqlCommand($"UPDATE nexora.journal_entry SET status = 'cancelled' WHERE id = '{entry}'", c);
            var act = () => cmd.ExecuteNonQueryAsync();
            (await act.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be("23514");
        }
        finally { await ArchiveAsync(co.Id); }
    }
}
