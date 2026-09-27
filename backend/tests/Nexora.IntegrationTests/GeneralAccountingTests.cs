using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Nexora.Application.Finance.Ledger;
using Nexora.Application.Finance.Reports;
using Nexora.Contracts.Finance;
using Nexora.Infrastructure.Persistence;
using Npgsql;

namespace Nexora.IntegrationTests;

/// <summary>Comptabilité générale — scénarios de validation Phase 29 §53-56.</summary>
[Collection("finance")]
public sealed class GeneralAccountingTests(FinanceEngineFixture fx)
{
    private static readonly Guid Company = Guid.Parse("dce00000-0000-0000-0000-0000000000c1");

    private AsyncServiceScope Scope() => fx.Provider.CreateAsyncScope();

    private async Task Sql(string sql)
    {
        await using var c = new NpgsqlConnection(fx.TestConnectionString);
        await c.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, c);
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task<T> Scalar<T>(string sql)
    {
        await using var c = new NpgsqlConnection(fx.TestConnectionString);
        await c.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, c);
        return (T)(await cmd.ExecuteScalarAsync())!;
    }

    private Task<Guid> NewPeriod(string name, string start, string end, string status) =>
        Scalar<Guid>($"""
            INSERT INTO nexora.accounting_period (company_id, name, start_date, end_date, status)
            VALUES ('{Company}', '{name}', '{start}', '{end}', '{status}')
            RETURNING id
            """);

    private static CreateJournalEntryRequest Entry(string journal, DateOnly date, params (string code, decimal debit, decimal credit)[] lines)
        => new(journal, date, "HTG", "Test comptable",
            lines.Select(l => new JournalEntryLineInput(l.code, l.debit, l.credit)).ToList());

    private static DateOnly D(string s) => DateOnly.Parse(s);

    // ---- §11-12, §53 : brouillon équilibré → validation ; les contrôles §47 passent ----
    [Fact]
    public async Task Balanced_manual_entry_is_drafted_then_posted()
    {
        await using var s = Scope();
        var svc = s.ServiceProvider.GetRequiredService<IManualJournalService>();

        var draft = await svc.CreateDraftAsync(Entry("OD", D("2026-03-04"), ("5711", 100_000m, 0m), ("7011", 0m, 100_000m)));
        draft.IsSuccess.Should().BeTrue(draft.Error?.Message);

        var got = await svc.GetAsync(draft.Value);
        got.Value!.Status.Should().Be("draft");
        got.Value.IsBalanced.Should().BeTrue();
        got.Value.EntryNumber.Should().StartWith("BROUILLON-");

        var posted = await svc.PostAsync(draft.Value);
        posted.IsSuccess.Should().BeTrue(posted.Error?.Message);
        posted.Value!.Controls.Should().OnlyContain(x => x.Status != "fail");
        posted.Value.EntryNumber.Should().NotStartWith("BROUILLON-");

        (await svc.GetAsync(draft.Value)).Value!.Status.Should().Be("posted");
    }

    // ---- §10, §53 : une écriture déséquilibrée ne peut pas être validée ----
    [Fact]
    public async Task Unbalanced_draft_cannot_be_posted()
    {
        await using var s = Scope();
        var svc = s.ServiceProvider.GetRequiredService<IManualJournalService>();

        var draft = await svc.CreateDraftAsync(Entry("OD", D("2026-03-05"), ("5711", 100m, 0m), ("7011", 0m, 90m)));
        draft.IsSuccess.Should().BeTrue(draft.Error?.Message);

        var posted = await svc.PostAsync(draft.Value);
        posted.IsSuccess.Should().BeFalse();
        posted.Error!.Code.Should().Be("accounting.controls_failed");
        posted.Error.Message.Should().Contain("Équilibre");
    }

    // ---- §15, §53 : période fermée refusée ----
    [Fact]
    public async Task Entry_in_a_closed_period_is_rejected()
    {
        await NewPeriod("Exercice 2019", "2019-01-01", "2019-12-31", "closed");
        await using var s = Scope();
        var svc = s.ServiceProvider.GetRequiredService<IManualJournalService>();

        var draft = await svc.CreateDraftAsync(Entry("OD", D("2019-06-01"), ("5711", 100m, 0m), ("7011", 0m, 100m)));
        draft.IsSuccess.Should().BeFalse();
    }

    // ---- §13, §53 : contrepassation d'une écriture validée ----
    [Fact]
    public async Task Posted_entry_can_be_reversed()
    {
        await using var s = Scope();
        var svc = s.ServiceProvider.GetRequiredService<IManualJournalService>();
        var db = s.ServiceProvider.GetRequiredService<NexoraDbContext>();

        var draft = await svc.CreateDraftAsync(Entry("OD", D("2026-03-06"), ("6011", 7_000m, 0m), ("4011", 0m, 7_000m)));
        var posted = await svc.PostAsync(draft.Value);
        posted.IsSuccess.Should().BeTrue(posted.Error?.Message);

        var rev = await svc.ReverseAsync(draft.Value, new ReverseJournalEntryRequest("Erreur de saisie"));
        rev.IsSuccess.Should().BeTrue(rev.Error?.Message);

        (await db.JournalEntries.AsNoTracking().Where(e => e.Id == draft.Value).Select(e => e.Status).FirstAsync())
            .Should().Be(Nexora.Domain.Accounting.JournalEntryStatus.Reversed);
        var mirror = await db.JournalEntries.AsNoTracking().FirstAsync(e => e.Id == rev.Value);
        mirror.ReversalOfEntryId.Should().Be(draft.Value);
        mirror.Origin.Should().Be("reversal");
    }

    // ---- §11 : un brouillon se modifie et se supprime ----
    [Fact]
    public async Task Draft_can_be_edited_and_deleted()
    {
        await using var s = Scope();
        var svc = s.ServiceProvider.GetRequiredService<IManualJournalService>();

        var draft = await svc.CreateDraftAsync(Entry("OD", D("2026-03-07"), ("5711", 500m, 0m), ("7011", 0m, 500m)));
        var upd = await svc.UpdateDraftAsync(draft.Value, Entry("OD", D("2026-03-07"), ("5711", 900m, 0m), ("7011", 0m, 900m)));
        upd.IsSuccess.Should().BeTrue($"{upd.Error?.Code}: {upd.Error?.Message}");
        (await svc.GetAsync(draft.Value)).Value!.TotalDebit.Should().Be(900m);

        (await svc.DeleteDraftAsync(draft.Value)).IsSuccess.Should().BeTrue();
        (await svc.GetAsync(draft.Value)).IsSuccess.Should().BeFalse();
    }

    // ---- §20, §47 : compte non postable refusé ----
    [Fact]
    public async Task Non_postable_account_is_refused()
    {
        await Sql($"UPDATE nexora.account SET is_postable = false WHERE company_id='{Company}' AND code='1011'");
        try
        {
            await using var s = Scope();
            var svc = s.ServiceProvider.GetRequiredService<IManualJournalService>();
            var draft = await svc.CreateDraftAsync(Entry("OD", D("2026-03-08"), ("1011", 100m, 0m), ("7011", 0m, 100m)));
            draft.IsSuccess.Should().BeFalse();
            draft.Error!.Code.Should().Be("accounting.account_unknown");
        }
        finally
        {
            await Sql($"UPDATE nexora.account SET is_postable = true WHERE company_id='{Company}' AND code='1011'");
        }
    }

    // ---- §19 : à-nouveaux générés depuis les soldes de bilan de l'exercice précédent ----
    [Fact]
    public async Task Opening_balances_are_generated_and_balanced()
    {
        var source = await NewPeriod("Exercice 2020", "2020-01-01", "2020-12-31", "open");
        var target = await NewPeriod("Exercice 2022", "2022-01-01", "2022-12-31", "open");

        await using var s = Scope();
        var svc = s.ServiceProvider.GetRequiredService<IManualJournalService>();
        var fiscal = s.ServiceProvider.GetRequiredService<IFiscalYearService>();
        var db = s.ServiceProvider.GetRequiredService<NexoraDbContext>();

        var e = await svc.CreateDraftAsync(Entry("OD", D("2020-05-01"), ("5711", 40_000m, 0m), ("1011", 0m, 40_000m)));
        (await svc.PostAsync(e.Value)).IsSuccess.Should().BeTrue();

        var an = await fiscal.GenerateOpeningBalancesAsync(target, new GenerateOpeningBalancesRequest(source));
        an.IsSuccess.Should().BeTrue(an.Error?.Message);
        an.Value!.LineCount.Should().BeGreaterThanOrEqualTo(2);
        an.Value.EntryDate.Should().Be(D("2022-01-01"));

        var entry = await db.JournalEntries.AsNoTracking().Include(x => x.Lines).FirstAsync(x => x.Id == an.Value.EntryId);
        entry.Origin.Should().Be("opening");
        entry.Lines.Sum(l => l.BaseDebit).Should().Be(entry.Lines.Sum(l => l.BaseCredit));

        (await fiscal.GenerateOpeningBalancesAsync(target, new GenerateOpeningBalancesRequest(source)))
            .Error!.Code.Should().Be("accounting.opening_exists");
    }

    // ---- §18 : clôture d'exercice — le résultat va en capitaux propres, le bilan reste équilibré ----
    [Fact]
    public async Task Year_end_close_moves_the_result_to_equity()
    {
        var year = await NewPeriod("Exercice 2021", "2021-01-01", "2021-12-31", "open");

        await using var s = Scope();
        var svc = s.ServiceProvider.GetRequiredService<IManualJournalService>();
        var fiscal = s.ServiceProvider.GetRequiredService<IFiscalYearService>();
        var reports = s.ServiceProvider.GetRequiredService<IFinancialReports>();

        var rev = await svc.CreateDraftAsync(Entry("OD", D("2021-06-01"), ("5711", 80_000m, 0m), ("7011", 0m, 80_000m)));
        (await svc.PostAsync(rev.Value)).IsSuccess.Should().BeTrue();
        var exp = await svc.CreateDraftAsync(Entry("OD", D("2021-06-02"), ("6011", 30_000m, 0m), ("5711", 0m, 30_000m)));
        (await svc.PostAsync(exp.Value)).IsSuccess.Should().BeTrue();

        var preview = await fiscal.PreviewCloseAsync(year);
        preview.IsSuccess.Should().BeTrue(preview.Error?.Message);
        preview.Value!.NetResult.Should().Be(50_000m);
        preview.Value.CanClose.Should().BeTrue();

        var close = await fiscal.CloseYearAsync(year, new CloseYearRequest());
        close.IsSuccess.Should().BeTrue(close.Error?.Message);
        close.Value!.NetResult.Should().Be(50_000m);

        // le compte de résultat 2021 exclut l'écriture de clôture → montre toujours 80 000 / 30 000
        var pnl = await reports.IncomeStatementAsync(D("2021-01-01"), D("2021-12-31"));
        pnl.NetResult.Should().Be(50_000m);

        // le bilan au 31/12/2021 reste équilibré (résultat porté en 1300)
        var bs = await reports.BalanceSheetAsync(D("2021-12-31"));
        bs.IsBalanced.Should().BeTrue();

        (await fiscal.CloseYearAsync(year, new CloseYearRequest())).Error!.Code.Should().Be("accounting.year_already_closed");
    }

    // ---- §4-8, §51 : plan comptable & journaux lisibles ----
    [Fact]
    public async Task Chart_accounts_and_journals_are_listed()
    {
        await using var s = Scope();
        var q = s.ServiceProvider.GetRequiredService<IChartQueries>();

        (await q.ChartsAsync()).Should().NotBeEmpty();
        (await q.JournalsAsync()).Should().Contain(j => j.Code == "AN").And.Contain(j => j.Code == "CL");
        var expenses = await q.AccountsAsync("expense", onlyPostable: true);
        expenses.Should().OnlyContain(a => a.AccountType == "expense" && a.IsPostable);
    }
}
