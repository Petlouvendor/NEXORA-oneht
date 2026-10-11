using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Nexora.Application.Abstractions;
using Nexora.Application.Finance.Engine;
using Nexora.Application.Finance.Events;
using Nexora.Domain.Common;
using Nexora.Infrastructure.Persistence;
using Npgsql;

namespace Nexora.IntegrationTests;

/// <summary>
/// Date métier (analyse exhaustive 2026-10, §5.5) : une opération du soir appartient au jour civil de l'entreprise,
/// pas au jour UTC — en Haïti, l'UTC passe au lendemain dès 20 h (19 h en hiver). Constaté en E2E : un règlement
/// fournisseur de 20 h 39 daté du lendemain, alors que sa facture gardait la date du jour.
/// </summary>
[Collection("finance")]
public sealed class BusinessDateTests(FinanceEngineFixture fx)
{
    private static readonly Guid Company = Guid.Parse("dce00000-0000-0000-0000-0000000000c1");
    private static readonly Guid CashHtg = Guid.Parse("dce00000-0000-0000-0000-00000000a051");

    /// <summary>30 avril 2026, 22 h heure d'Haïti (UTC−4) = 1er mai, 2 h UTC : dernier soir du mois.</summary>
    private static readonly DateTimeOffset LastEveningOfApril = new(2026, 5, 1, 2, 0, 0, TimeSpan.Zero);

    private sealed class FixedClock(DateTimeOffset now) : IDateTimeProvider
    {
        public DateTimeOffset UtcNow => now;
    }

    private ServiceProvider EveningProvider()
        => fx.CreateProviderWith(s => s.AddSingleton<IDateTimeProvider>(new FixedClock(LastEveningOfApril)));

    [Fact]
    public async Task The_business_day_is_the_company_local_day_not_the_utc_day()
    {
        await using var sp = EveningProvider();
        await using var s = sp.CreateAsyncScope();
        var calendar = s.ServiceProvider.GetRequiredService<IBusinessCalendar>();

        (await calendar.ZoneAsync()).Id.Should().Be("America/Port-au-Prince");
        calendar.Today.Should().Be(new DateOnly(2026, 4, 30));
        (await calendar.TodayAsync()).Should().Be(new DateOnly(2026, 4, 30));
        calendar.DateOf(LastEveningOfApril.AddHours(-3)).Should().Be(new DateOnly(2026, 4, 30));
        calendar.DateOf(LastEveningOfApril.AddHours(3)).Should().Be(new DateOnly(2026, 5, 1));
    }

    [Fact]
    public async Task An_evening_operation_is_posted_on_the_local_day_and_stays_in_its_month()
    {
        await using var s = fx.Provider.CreateAsyncScope();
        var engine = s.ServiceProvider.GetRequiredService<IFinancialEngine>();

        var r = await engine.HandleAsync(new ExpenseRecorded(
            Company, Guid.NewGuid(), LastEveningOfApril, Guid.NewGuid(),
            new Money(150m, "HTG"), Money.Zero("HTG"), CashHtg));
        r.IsSuccess.Should().BeTrue(r.Error?.Message);

        var db = s.ServiceProvider.GetRequiredService<NexoraDbContext>();
        var entryDate = await db.Database
            .SqlQuery<DateOnly>($"""SELECT entry_date AS "Value" FROM nexora.journal_entry WHERE id = {r.Value!.JournalEntryId}""")
            .SingleAsync();
        entryDate.Should().Be(new DateOnly(2026, 4, 30), "une dépense de 22 h le 30 avril appartient à avril, pas à mai");
    }

    [Fact]
    public async Task The_database_session_follows_the_company_time_zone()
    {
        await using var s = fx.Provider.CreateAsyncScope();
        var db = s.ServiceProvider.GetRequiredService<NexoraDbContext>();

        var zone = await db.Database.SqlQuery<string>($"""SELECT current_setting('TimeZone') AS "Value" """).SingleAsync();
        zone.Should().Be("America/Port-au-Prince");

        // current_date et l'horloge applicative donnent le même jour, quelle que soit l'heure du serveur.
        var sqlToday = await db.Database.SqlQuery<DateOnly>($"""SELECT current_date AS "Value" """).SingleAsync();
        sqlToday.Should().Be(await s.ServiceProvider.GetRequiredService<IBusinessCalendar>().TodayAsync());
    }

    [Fact]
    public async Task An_unknown_company_time_zone_is_refused()
    {
        await using var c = new NpgsqlConnection(fx.TestConnectionString);
        await c.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            $"UPDATE nexora.company SET timezone = 'Mars/Olympus_Mons' WHERE id = '{Company}'", c);
        var act = async () => await cmd.ExecuteNonQueryAsync();
        (await act.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be("22023");
    }
}
