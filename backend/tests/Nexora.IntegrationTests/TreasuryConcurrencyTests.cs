using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Nexora.Application.Abstractions;
using Nexora.Application.Finance;
using Nexora.Application.Finance.Abstractions;
using Nexora.Contracts.Finance;
using Nexora.Infrastructure.Persistence;

namespace Nexora.IntegrationTests;

/// <summary>
/// Audit 2026-10, A-018 : le solde d'un compte financier était lu, augmenté en mémoire puis réécrit. Des opérations
/// simultanées sur un même compte perdaient des mises à jour sans erreur (525 HTG sur 20 opérations). Le solde
/// stocké doit toujours égaler le solde d'ouverture plus la somme des transactions.
/// </summary>
[Collection("finance")]
public sealed class TreasuryConcurrencyTests(FinanceEngineFixture fx)
{
    private static readonly Guid Company = Guid.Parse("dce00000-0000-0000-0000-0000000000c1");
    private static readonly Guid Direction = Guid.Parse("dce00000-0000-0000-0000-0000000000d1");
    private static readonly Guid CashAccount = Guid.Parse("dce00000-0000-0000-0000-00000000a051");

    private AsyncServiceScope Scope()
    {
        var s = fx.Provider.CreateAsyncScope();
        var tc = (TestTenantContext)s.ServiceProvider.GetRequiredService<ITenantContext>();
        tc.SetUser(Direction);
        tc.SetScope([Company], [], Company);
        return s;
    }

    private async Task<decimal> Drift(Guid account)
    {
        await using var s = Scope();
        return await s.ServiceProvider.GetRequiredService<NexoraDbContext>().Database.SqlQuery<decimal>($"""
            SELECT fa.current_balance - fa.opening_balance
                   - COALESCE((SELECT SUM(t.amount) FROM nexora.financial_transaction t WHERE t.financial_account_id = fa.id), 0)
                   AS "Value"
            FROM nexora.financial_account fa WHERE fa.id = {account}
            """).FirstAsync();
    }

    /// <summary>
    /// Cœur du défaut : N transactions ajoutent chacune 1 au même compte en même temps (comme une vente et un
    /// transfert, que des verrous de numérotation différents ne sérialisent pas). Chaque transaction attend un peu
    /// les autres avant de valider ; avec l'ancien code elles lisaient toutes le même solde et n'en gardaient qu'une.
    /// </summary>
    [Fact]
    public async Task Simultaneous_balance_adjustments_are_all_kept()
    {
        Guid account;
        await using (var s = Scope())
            account = (await s.ServiceProvider.GetRequiredService<ITreasuryManagementService>()
                .CreateAccountAsync(new SaveFinancialAccountRequest($"Concurrence S {Guid.NewGuid():N}"[..30], "cash", "HTG", null, null))).Value;

        const int n = 12;
        var start = new TaskCompletionSource();
        var arrived = 0;
        var allArrived = new TaskCompletionSource();
        async Task Adjust()
        {
            await start.Task;
            await using var s = Scope();
            var store = s.ServiceProvider.GetRequiredService<IFinancialAccountStore>();
            await s.ServiceProvider.GetRequiredService<IUnitOfWork>().ExecuteInTransactionAsync(async t =>
            {
                await store.AdjustBalanceAsync(account, 1m, t);
                if (Interlocked.Increment(ref arrived) == n) allArrived.TrySetResult();
                // Le nouveau code verrouille la ligne : les autres attendent leur tour, d'où le délai borné.
                await Task.WhenAny(allArrived.Task, Task.Delay(300, t));
                return 0;
            });
        }
        var tasks = Enumerable.Range(0, n).Select(_ => Adjust()).ToList();
        start.SetResult();
        await Task.WhenAll(tasks);

        await using var check = Scope();
        (await check.ServiceProvider.GetRequiredService<NexoraDbContext>().Database
            .SqlQuery<decimal>($"SELECT current_balance AS \"Value\" FROM nexora.financial_account WHERE id = {account}").FirstAsync())
            .Should().Be(n, "aucun ajout ne doit être perdu");

        // Ajouts sans transaction (test du magasin seul) : remettre le compte à zéro et le clôturer, pour ne pas
        // fausser les agrégats de trésorerie des autres tests de la collection.
        await using var c = new Npgsql.NpgsqlConnection(fx.TestConnectionString);
        await c.OpenAsync();
        await using var reset = new Npgsql.NpgsqlCommand(
            $"UPDATE nexora.financial_account SET current_balance = 0, status = 'closed' WHERE id = '{account}'", c);
        await reset.ExecuteNonQueryAsync();
    }

    [Fact]
    public async Task Simultaneous_transfers_on_a_shared_account_lose_no_update()
    {
        Guid a, b;
        await using (var s = Scope())
        {
            var t = s.ServiceProvider.GetRequiredService<ITreasuryManagementService>();
            a = (await t.CreateAccountAsync(new SaveFinancialAccountRequest($"Concurrence A {Guid.NewGuid():N}"[..30], "cash", "HTG", null, null))).Value;
            b = (await t.CreateAccountAsync(new SaveFinancialAccountRequest($"Concurrence B {Guid.NewGuid():N}"[..30], "cash", "HTG", null, null))).Value;
            (await t.TransferAsync(new TreasuryTransferRequest(Guid.NewGuid(), CashAccount, a, 500m, "Fonds"))).IsSuccess.Should().BeTrue();
        }

        // Caisse → A et A → B en même temps : A est mouvementé par les deux séries (ordre de verrouillage sans cycle).
        var start = new TaskCompletionSource();
        async Task<bool> Transfer(Guid from, Guid to, decimal amount)
        {
            await start.Task;
            await using var s = Scope();
            var t = s.ServiceProvider.GetRequiredService<ITreasuryManagementService>();
            return (await t.TransferAsync(new TreasuryTransferRequest(Guid.NewGuid(), from, to, amount, "Concurrence"))).IsSuccess;
        }
        var tasks = Enumerable.Range(0, 10).Select(_ => Transfer(CashAccount, a, 10m))
            .Concat(Enumerable.Range(0, 10).Select(_ => Transfer(a, b, 5m))).ToList();
        start.SetResult();
        var results = await Task.WhenAll(tasks);

        results.Should().AllBeEquivalentTo(true, "aucun transfert n'a de raison d'être refusé");
        (await Drift(a)).Should().Be(0m, "solde stocké = ouverture + Σ transactions");
        (await Drift(b)).Should().Be(0m);
        (await Drift(CashAccount)).Should().Be(0m);

        await using var check = Scope();
        var balance = await check.ServiceProvider.GetRequiredService<NexoraDbContext>().Database
            .SqlQuery<decimal>($"SELECT current_balance AS \"Value\" FROM nexora.financial_account WHERE id = {a}").FirstAsync();
        balance.Should().Be(500m + 10 * 10m - 10 * 5m);

        // Rendre l'argent à la caisse et clôturer les deux comptes de test (agrégats des autres tests intacts).
        var tm = check.ServiceProvider.GetRequiredService<ITreasuryManagementService>();
        (await tm.TransferAsync(new TreasuryTransferRequest(Guid.NewGuid(), a, CashAccount, balance, "Fin de test"))).IsSuccess.Should().BeTrue();
        (await tm.TransferAsync(new TreasuryTransferRequest(Guid.NewGuid(), b, CashAccount, 50m, "Fin de test"))).IsSuccess.Should().BeTrue();
        (await tm.SetAccountStatusAsync(a, new FinancialAccountStatusRequest("closed", "Fin de test"))).IsSuccess.Should().BeTrue();
        (await tm.SetAccountStatusAsync(b, new FinancialAccountStatusRequest("closed", "Fin de test"))).IsSuccess.Should().BeTrue();
    }

    /// <summary>
    /// Audit 2026-10 éd. 2 : le contrôle de découvert d'une caisse était lu sans verrou. Six transferts simultanés de 40
    /// depuis une caisse de 100 passaient tous le contrôle ; seuls deux doivent passer et la caisse ne devient jamais
    /// négative.
    /// </summary>
    [Fact]
    public async Task Simultaneous_transfers_never_overdraw_a_cash_account()
    {
        Guid a, b;
        await using (var s = Scope())
        {
            var tm = s.ServiceProvider.GetRequiredService<ITreasuryManagementService>();
            a = (await tm.CreateAccountAsync(new SaveFinancialAccountRequest($"Découvert A {Guid.NewGuid():N}"[..30], "cash", "HTG", null, null))).Value;
            b = (await tm.CreateAccountAsync(new SaveFinancialAccountRequest($"Découvert B {Guid.NewGuid():N}"[..30], "cash", "HTG", null, null))).Value;
            (await tm.RecordOpeningBalanceAsync(a, new TreasuryOpeningBalanceRequest(Guid.NewGuid(), 100m))).IsSuccess.Should().BeTrue();
        }

        var start = new TaskCompletionSource();
        async Task<bool> Transfer()
        {
            await start.Task;
            await using var s = Scope();
            var r = await s.ServiceProvider.GetRequiredService<ITreasuryManagementService>()
                .TransferAsync(new TreasuryTransferRequest(Guid.NewGuid(), a, b, 40m, null));
            if (!r.IsSuccess) r.Error!.Code.Should().Be("treasury.insufficient_balance");
            return r.IsSuccess;
        }
        var tasks = Enumerable.Range(0, 6).Select(_ => Transfer()).ToList();
        start.SetResult();
        var results = await Task.WhenAll(tasks);

        results.Count(ok => ok).Should().Be(2, "100 permet deux transferts de 40, pas davantage");
        await using var check = Scope();
        (await check.ServiceProvider.GetRequiredService<NexoraDbContext>().Database.SqlQuery<decimal>($"""
            SELECT current_balance AS "Value" FROM nexora.financial_account WHERE id = {a}
            """).FirstAsync()).Should().Be(20m);
        (await Drift(a)).Should().Be(0m);

        // Fin de test : les deux caisses sont vidées dans la caisse partagée et clôturées (la position de trésorerie
        // d'autres tests compte les comptes actifs).
        var cleanup = check.ServiceProvider.GetRequiredService<ITreasuryManagementService>();
        (await cleanup.TransferAsync(new TreasuryTransferRequest(Guid.NewGuid(), a, CashAccount, 20m, "Fin de test"))).IsSuccess.Should().BeTrue();
        (await cleanup.TransferAsync(new TreasuryTransferRequest(Guid.NewGuid(), b, CashAccount, 80m, "Fin de test"))).IsSuccess.Should().BeTrue();
        (await cleanup.SetAccountStatusAsync(a, new FinancialAccountStatusRequest("closed", "Fin de test"))).IsSuccess.Should().BeTrue();
        (await cleanup.SetAccountStatusAsync(b, new FinancialAccountStatusRequest("closed", "Fin de test"))).IsSuccess.Should().BeTrue();
    }
}
