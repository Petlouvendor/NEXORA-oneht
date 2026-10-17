using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Nexora.Application.Abstractions;
using Nexora.Application.Finance;
using Nexora.Application.Finance.Abstractions;
using Nexora.Contracts.Finance;

namespace Nexora.IntegrationTests;

/// <summary>
/// Audit 2026-10, A-017 : la table des taux était lue partout mais aucune fonction ne permettait d'y écrire ; le
/// multidevise ne marchait qu'avec les taux de démonstration. Un taux vaut pour toute l'installation.
/// </summary>
[Collection("finance")]
public sealed class ExchangeRateManagementTests(FinanceEngineFixture fx)
{
    private static readonly Guid Company = Guid.Parse("dce00000-0000-0000-0000-0000000000c1");
    private static readonly Guid Direction = Guid.Parse("dce00000-0000-0000-0000-0000000000d1");

    private AsyncServiceScope Scope(Guid? user = null)
    {
        var s = fx.Provider.CreateAsyncScope();
        var tc = (TestTenantContext)s.ServiceProvider.GetRequiredService<ITenantContext>();
        tc.SetUser(user ?? Direction);
        tc.SetScope([Company], [], Company);
        return s;
    }

    [Fact]
    public async Task A_recorded_rate_is_used_for_conversion_and_never_overwritten()
    {
        // Date passée unique : le test ne dépend pas des taux de démonstration.
        var at = new DateTimeOffset(2015, 3, 1, 12, 0, 0, TimeSpan.Zero).AddMinutes(Random.Shared.Next(0, 100_000));
        await using var s = Scope();
        var rates = s.ServiceProvider.GetRequiredService<IExchangeRateService>();

        var r = await rates.RecordAsync(new RecordExchangeRateRequest("usd", "HTG", 131.25m, at));
        r.IsSuccess.Should().BeTrue(r.Error?.Message);
        r.Value!.BaseCurrency.Should().Be("USD");
        (await rates.ListAsync("USD", "HTG")).Should().Contain(x => x.Id == r.Value.Id);

        var (rate, rateId) = await s.ServiceProvider.GetRequiredService<IExchangeRateProvider>().GetRateAsync("USD", "HTG", at.AddMinutes(1));
        rate.Should().Be(131.25m);
        rateId.Should().Be(r.Value.Id);
        (await s.ServiceProvider.GetRequiredService<IExchangeRateProvider>().GetRateAsync("HTG", "USD", at.AddMinutes(1))).Rate
            .Should().Be(1m / 131.25m, "le taux inverse est dérivé");

        (await rates.RecordAsync(new RecordExchangeRateRequest("USD", "HTG", 140m, at))).Error!.Code.Should().Be("finance.rate_exists");
        (await rates.RecordAsync(new RecordExchangeRateRequest("USD", "USD", 1m))).Error!.Code.Should().Be("finance.rate_currency");
        (await rates.RecordAsync(new RecordExchangeRateRequest("USD", "HTG", 0m))).Error!.Code.Should().Be("finance.rate_value");
    }

    [Fact]
    public async Task The_fractional_rate_day_does_not_change_the_surrounding_rates()
    {
        await using var s = Scope();
        var provider = s.ServiceProvider.GetRequiredService<IExchangeRateProvider>();
        var day = FinanceEngineFixture.FractionalUsdRateDay;

        (await provider.GetRateAsync("USD", "HTG", day)).Rate.Should().Be(FinanceEngineFixture.FractionalUsdRate);
        var before = (await provider.GetRateAsync("USD", "HTG", day.AddDays(-1))).Rate;
        (await provider.GetRateAsync("USD", "HTG", day.AddDays(1))).Rate
            .Should().Be(before, "le taux d'origine est rétabli dès le lendemain");
        before.Should().NotBe(FinanceEngineFixture.FractionalUsdRate);
    }

    [Fact]
    public async Task With_another_client_hosted_only_the_platform_operator_records_rates()
    {
        var other = Guid.NewGuid();
        await using (var c = new Npgsql.NpgsqlConnection(fx.TestConnectionString))
        {
            await c.OpenAsync();
            await using var cmd = new Npgsql.NpgsqlCommand($"""
                INSERT INTO nexora.company (id, name, default_currency_id)
                VALUES ('{other}', 'Autre client {other:N}', (SELECT id FROM nexora.currency WHERE code = 'HTG'))
                """, c);
            await cmd.ExecuteNonQueryAsync();
        }
        try
        {
            // Directeur d'entreprise ordinaire ; l'utilisateur de test « direction » est exploitant de la plateforme.
            await using var s = Scope(await fx.NewCompanyDirectorAsync(Company));
            (await s.ServiceProvider.GetRequiredService<IExchangeRateService>()
                    .RecordAsync(new RecordExchangeRateRequest("USD", "HTG", 133m)))
                .Error!.Code.Should().Be("operations.platform_only", "un taux vaut pour tous les clients de la plateforme");
        }
        finally
        {
            await using var c = new Npgsql.NpgsqlConnection(fx.TestConnectionString);
            await c.OpenAsync();
            await using var cmd = new Npgsql.NpgsqlCommand($"UPDATE nexora.company SET status = 'archived' WHERE id = '{other}'", c);
            await cmd.ExecuteNonQueryAsync();
        }
    }
}
