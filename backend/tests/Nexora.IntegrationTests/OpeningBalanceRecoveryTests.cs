using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Nexora.Application.Finance;
using Nexora.Infrastructure.Persistence;
using Npgsql;

namespace Nexora.IntegrationTests;

/// <summary>
/// Reprise des soldes d'ouverture saisis sans écriture (audit 2026-10, A-023) : la trésorerie comptait la colonne
/// <c>opening_balance</c>, le grand livre l'ignorait.
/// </summary>
[Collection("finance")]
public sealed class OpeningBalanceRecoveryTests(FinanceEngineFixture fx)
{
    private static readonly Guid Company = Guid.Parse("dce00000-0000-0000-0000-0000000000c1");
    private static readonly Guid CashAccount = Guid.Parse("dce00000-0000-0000-0000-00000000a051");

    private async Task Sql(string sql)
    {
        await using var c = new NpgsqlConnection(fx.TestConnectionString);
        await c.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, c);
        await cmd.ExecuteNonQueryAsync();
    }

    [Fact]
    public async Task A_legacy_opening_balance_gets_its_entry_and_the_displayed_balance_does_not_move()
    {
        // Caisse ouverte à 500 sans écriture, puis un dépôt de 100 : solde affiché 600.
        var account = Guid.NewGuid();
        await Sql($"""
            INSERT INTO nexora.financial_account (id, company_id, account_type, name, currency_id, opening_balance, current_balance,
                                                  ledger_account_id, status, created_at)
            SELECT '{account}', company_id, 'cash', 'Caisse reprise {account:N}', currency_id, 500, 600, ledger_account_id, 'active',
                   now() - interval '2 hours'
            FROM nexora.financial_account WHERE id = '{CashAccount}';
            INSERT INTO nexora.financial_transaction (company_id, financial_account_id, transaction_type, amount, currency_id,
                                                      occurred_at, status)
            SELECT '{Company}', '{account}', 'deposit', 100, currency_id, now() - interval '1 hour', 'cleared'
            FROM nexora.financial_account WHERE id = '{account}';
            """);

        await using var s = fx.Provider.CreateAsyncScope();
        var recovery = s.ServiceProvider.GetRequiredService<IOpeningBalanceRecovery>();
        var db = s.ServiceProvider.GetRequiredService<NexoraDbContext>();

        var simulated = await recovery.RunAsync(apply: false, account);
        simulated.Should().ContainSingle().Which.Status.Should().Be("a_reprendre");
        (await db.FinancialAccounts.AsNoTracking().SingleAsync(a => a.Id == account)).OpeningBalance.Should().Be(500m, "la simulation n'écrit rien");

        var applied = await recovery.RunAsync(apply: true, account);
        applied.Should().ContainSingle().Which.Status.Should().Be("repris", applied[0].Detail);

        var after = await db.FinancialAccounts.AsNoTracking().SingleAsync(a => a.Id == account);
        after.CurrentBalance.Should().Be(600m, "le solde affiché ne change pas");
        after.OpeningBalance.Should().Be(0m);
        var opening = await db.FinancialTransactions.AsNoTracking()
            .SingleAsync(t => t.FinancialAccountId == account && t.TransactionType == "opening");
        opening.Amount.Should().Be(500m);
        var deposit = await db.FinancialTransactions.AsNoTracking()
            .SingleAsync(t => t.FinancialAccountId == account && t.TransactionType == "deposit");
        opening.OccurredAt.Should().BeBefore(deposit.OccurredAt, "l'ouverture précède le premier mouvement");

        var entry = await db.JournalEntries.AsNoTracking().Include(e => e.Lines)
            .SingleAsync(e => e.SourceType == "treasury_opening" && e.SourceId == account);
        entry.Lines.Sum(l => l.Debit).Should().Be(500m);
        entry.Lines.Sum(l => l.Credit).Should().Be(500m);

        (await recovery.RunAsync(apply: true, account)).Should().BeEmpty("une reprise relancée ne refait rien");
    }
}
