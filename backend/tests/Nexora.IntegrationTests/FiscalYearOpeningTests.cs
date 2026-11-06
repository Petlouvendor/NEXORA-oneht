using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Nexora.Application.Abstractions;
using Nexora.Application.Finance.Abstractions;
using Nexora.Application.Finance.Engine;
using Nexora.Application.Finance.Events;
using Nexora.Application.Finance.Periods;
using Nexora.Domain.Accounting;
using Nexora.Domain.Common;
using Nexora.Infrastructure.Persistence;
using Npgsql;

namespace Nexora.IntegrationTests;

/// <summary>
/// Audit 2026-10 éd. 2, A-042 : seul l'amorçage créait un exercice. Au 1er janvier suivant, toute écriture — donc toute
/// vente — était refusée (« Aucune période comptable ne couvre le … »), sans écran ni route pour y remédier. Chaque test
/// travaille sur une entreprise neuve (exercice 2026 seulement) pour ne pas ouvrir 2027 dans la base partagée.
/// </summary>
[Collection("finance")]
public sealed class FiscalYearOpeningTests(FinanceEngineFixture fx)
{
    private sealed class FixedClock(DateTimeOffset now) : IDateTimeProvider
    {
        public DateTimeOffset UtcNow => now;
    }

    /// <summary>Instant UTC de midi, heure d'Haïti, le jour donné.</summary>
    private static DateTimeOffset NoonInHaiti(int year, int month, int day) => new(year, month, day, 17, 0, 0, TimeSpan.Zero);

    private sealed record NewCompany(Guid Id, Guid Cash);

    private async Task<NewCompany> NewCompanyWithExercise2026Async()
    {
        var id = Guid.NewGuid();
        await using var c = new NpgsqlConnection(fx.TestConnectionString);
        await c.OpenAsync();
        await using (var cmd = new NpgsqlCommand($"""
            INSERT INTO nexora.company (id, name, default_currency_id)
            VALUES ('{id}', 'Exercices {id:N}', (SELECT id FROM nexora.currency WHERE code = 'HTG'));
            SELECT nexora.bootstrap_company_defaults('{id}', (SELECT id FROM nexora.currency WHERE code = 'HTG'), 2026);
            """, c))
            await cmd.ExecuteNonQueryAsync();
        await using var q = new NpgsqlCommand(
            $"SELECT id FROM nexora.financial_account WHERE company_id = '{id}' AND account_type = 'cash' LIMIT 1", c);
        return new NewCompany(id, (Guid)(await q.ExecuteScalarAsync())!);
    }

    private async Task ArchiveAsync(Guid company)
    {
        await using var c = new NpgsqlConnection(fx.TestConnectionString);
        await c.OpenAsync();
        await using var cmd = new NpgsqlCommand($"UPDATE nexora.company SET status = 'archived' WHERE id = '{company}'", c);
        await cmd.ExecuteNonQueryAsync();
    }

    private ServiceProvider ProviderAt(DateTimeOffset now)
        => fx.CreateProviderWith(s => s.AddSingleton<IDateTimeProvider>(new FixedClock(now)));

    private static AsyncServiceScope Scope(ServiceProvider sp, Guid company)
    {
        var s = sp.CreateAsyncScope();
        var tc = (TestTenantContext)s.ServiceProvider.GetRequiredService<ITenantContext>();
        tc.SetScope([company], [], company);
        return s;
    }

    private static Task<List<AccountingPeriod>> PeriodsAsync(AsyncServiceScope s, Guid company)
        => s.ServiceProvider.GetRequiredService<NexoraDbContext>().AccountingPeriods.AsNoTracking()
            .Where(p => p.CompanyId == company).OrderBy(p => p.StartDate).ToListAsync();

    private static Task<Result<PostingResult>> SellAsync(AsyncServiceScope s, NewCompany co, DateTimeOffset at)
        => s.ServiceProvider.GetRequiredService<IFinancialEngine>().HandleAsync(new CashSaleRecorded(
            co.Id, Guid.NewGuid(), at, Guid.NewGuid(), new Money(100m, "HTG"), Money.Zero("HTG"), co.Cash));

    [Fact]
    public async Task A_sale_on_new_years_day_opens_the_next_exercise_instead_of_being_refused()
    {
        var co = await NewCompanyWithExercise2026Async();
        try
        {
            var newYear = NoonInHaiti(2027, 1, 1);
            await using var sp = ProviderAt(newYear);
            await using var s = Scope(sp, co.Id);

            var r = await SellAsync(s, co, newYear);
            r.IsSuccess.Should().BeTrue(r.Error?.Message);

            var periods = await PeriodsAsync(s, co.Id);
            periods.Select(p => p.Name).Should().Equal("Exercice 2026", "Exercice 2027");
            var y2027 = periods[1];
            (y2027.StartDate, y2027.EndDate, y2027.Status)
                .Should().Be((new DateOnly(2027, 1, 1), new DateOnly(2027, 12, 31), PeriodStatus.Open));
            (await s.ServiceProvider.GetRequiredService<NexoraDbContext>().JournalEntries.AsNoTracking()
                    .SingleAsync(e => e.Id == r.Value!.JournalEntryId))
                .AccountingPeriodId.Should().Be(y2027.Id);
        }
        finally { await ArchiveAsync(co.Id); }
    }

    [Fact]
    public async Task Simultaneous_new_years_day_sales_open_a_single_exercise()
    {
        var co = await NewCompanyWithExercise2026Async();
        try
        {
            var newYear = NoonInHaiti(2027, 1, 1);
            await using var sp = ProviderAt(newYear);
            var results = await Task.WhenAll(Enumerable.Range(0, 6).Select(async _ =>
            {
                await using var s = Scope(sp, co.Id);
                return await SellAsync(s, co, newYear);
            }));

            results.Should().OnlyContain(r => r.IsSuccess, string.Join(" | ", results.Select(r => r.Error?.Message)));
            await using var check = Scope(sp, co.Id);
            (await PeriodsAsync(check, co.Id)).Should().HaveCount(2);
        }
        finally { await ArchiveAsync(co.Id); }
    }

    [Fact]
    public async Task A_future_dated_operation_never_opens_an_exercise()
    {
        var co = await NewCompanyWithExercise2026Async();
        try
        {
            await using var sp = ProviderAt(NoonInHaiti(2026, 10, 17));
            await using var s = Scope(sp, co.Id);

            var r = await SellAsync(s, co, NoonInHaiti(2027, 3, 1));
            r.IsSuccess.Should().BeFalse("une saisie datée dans le futur ne doit pas ouvrir d'exercice");
            (await PeriodsAsync(s, co.Id)).Should().ContainSingle();
        }
        finally { await ArchiveAsync(co.Id); }
    }

    [Fact]
    public async Task The_next_exercise_can_be_opened_in_advance_but_only_once()
    {
        var co = await NewCompanyWithExercise2026Async();
        try
        {
            await using var sp = ProviderAt(NoonInHaiti(2026, 12, 15));
            await using (var s = Scope(sp, co.Id))
            {
                var r = await s.ServiceProvider.GetRequiredService<IAccountingPeriodService>().OpenNextYearAsync();
                r.IsSuccess.Should().BeTrue(r.Error?.Message);
                (r.Value!.Name, r.Value.StartDate, r.Value.EndDate)
                    .Should().Be(("Exercice 2027", new DateOnly(2027, 1, 1), new DateOnly(2027, 12, 31)));
            }
            await using (var s = Scope(sp, co.Id))
            {
                var again = await s.ServiceProvider.GetRequiredService<IAccountingPeriodService>().OpenNextYearAsync();
                again.Error!.Code.Should().Be("accounting.next_year_exists", "2028 ne s'ouvre pas avant que 2027 ait commencé");
                (await PeriodsAsync(s, co.Id)).Should().HaveCount(2);
            }
        }
        finally { await ArchiveAsync(co.Id); }
    }

    [Fact]
    public async Task The_scheduler_opens_the_next_exercise_during_the_last_month_only()
    {
        var co = await NewCompanyWithExercise2026Async();
        try
        {
            await using (var november = ProviderAt(NoonInHaiti(2026, 11, 15)))
            await using (var s = Scope(november, co.Id))
                (await s.ServiceProvider.GetRequiredService<IAccountingPeriodService>().OpenNextYearIfDueAsync()).Should().Be(0);

            await using var december = ProviderAt(NoonInHaiti(2026, 12, 5));
            await using (var s = Scope(december, co.Id))
                (await s.ServiceProvider.GetRequiredService<IAccountingPeriodService>().OpenNextYearIfDueAsync()).Should().Be(1);
            await using (var s = Scope(december, co.Id))
            {
                (await s.ServiceProvider.GetRequiredService<IAccountingPeriodService>().OpenNextYearIfDueAsync())
                    .Should().Be(0, "l'exercice 2027 existe déjà");
                (await PeriodsAsync(s, co.Id)).Select(p => p.Name).Should().Equal("Exercice 2026", "Exercice 2027");
            }
        }
        finally { await ArchiveAsync(co.Id); }
    }
}
