using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Nexora.Application.Finance.Abstractions;
using Nexora.Application.Reporting;
using Nexora.Contracts.Reporting;
using Nexora.Infrastructure.Persistence;

namespace Nexora.IntegrationTests;

/// <summary>
/// Audit 2026-10 éd. 2, A-046 : les indicateurs additionnaient des montants de devises différentes. La trésorerie de
/// la Pharmacie (caisse et mobile money en gourdes, banque Unibank en dollars) affichait le solde en dollars comme s'il
/// était en gourdes : 3 200 USD comptaient pour 3 200 HTG au lieu de 3 200 × taux.
/// </summary>
[Collection("finance")]
public sealed class KpiCurrencyTests(FinanceEngineFixture fx)
{
    private AsyncServiceScope Scope() => fx.Provider.CreateAsyncScope();

    private sealed record BalanceRow(string Currency, decimal Balance);

    [Fact]
    public async Task The_cash_indicator_converts_dollar_accounts_into_gourdes()
    {
        await using var s = Scope();
        var db = s.ServiceProvider.GetRequiredService<NexoraDbContext>();
        var balances = await db.Database.SqlQuery<BalanceRow>($"""
            SELECT c.code AS "Currency", SUM(fa.current_balance) AS "Balance"
            FROM nexora.financial_account fa JOIN nexora.currency c ON c.id = fa.currency_id
            WHERE fa.company_id = {fx.CompanyId} AND fa.status = 'active' AND fa.account_type IN ('cash','bank','mobile_money')
            GROUP BY c.code
            """).ToListAsync();
        balances.Should().Contain(b => b.Currency == "USD" && b.Balance != 0m, "le jeu de démonstration a un compte en dollars");

        var (rate, _) = await s.ServiceProvider.GetRequiredService<IExchangeRateProvider>()
            .GetRateAsync("USD", "HTG", DateTimeOffset.UtcNow);
        var expected = Math.Round(balances.Sum(b => b.Currency == "USD" ? b.Balance * rate : b.Balance), 2);
        var naive = Math.Round(balances.Sum(b => b.Balance), 2);

        var cc = await s.ServiceProvider.GetRequiredService<IKpiService>().CommandCenterAsync(new ReportFilter { Preset = "today" });
        var cash = cc.Kpis.Single(k => k.Key == KpiCatalog.Cash);

        cash.Value.Should().Be(expected, $"dollars convertis au taux du jour ({rate}) ; l'ancien calcul donnait {naive}");
        cash.Unit.Should().Be("HTG");
        cash.Kind.Should().Be(KpiValue.Actual, "tous les taux existent");
    }

    /// <summary>
    /// Audit 2026-10 éd. 2 : l'alerte « Trésorerie négative » additionnait les devises. Une caisse à −1 000 HTG et une
    /// banque à +100 USD (≈ 13 000 HTG) déclenchaient l'alerte (−900 « tout confondu ») alors que la trésorerie est positive.
    /// </summary>
    [Fact]
    public async Task The_negative_cash_alert_converts_currencies()
    {
        var company = Guid.NewGuid();
        var scope = Guid.NewGuid();
        var director = await fx.NewCompanyDirectorAsync(Guid.Parse("dce00000-0000-0000-0000-0000000000c1"));
        await using var s = Scope();
        var db = s.ServiceProvider.GetRequiredService<NexoraDbContext>();
        await db.Database.ExecuteSqlAsync($"""
            INSERT INTO nexora.company (id, name, default_currency_id)
            VALUES ({company}, {"Alerte devises " + company.ToString("N")}, (SELECT id FROM nexora.currency WHERE code = 'HTG'));
            INSERT INTO nexora.financial_account (company_id, account_type, name, currency_id, current_balance)
            VALUES ({company}, 'cash', 'Caisse gourdes', (SELECT id FROM nexora.currency WHERE code = 'HTG'), -1000),
                   ({company}, 'bank', 'Banque dollars', (SELECT id FROM nexora.currency WHERE code = 'USD'), 100);
            INSERT INTO nexora.access_scope (id, scope_type, company_id) VALUES ({scope}, 'COMPANY', {company});
            INSERT INTO nexora.user_role (user_id, role_id, scope_id)
            SELECT {director}, r.id, {scope} FROM nexora.role r WHERE r.name = 'owner_direction' AND r.system_role;
            """);
        var tc = (TestTenantContext)s.ServiceProvider.GetRequiredService<Nexora.Application.Abstractions.ITenantContext>();
        tc.SetUser(director);
        tc.SetScope([company], [], company);

        var alerts = await s.ServiceProvider.GetRequiredService<IAlertCenter>().EvaluateAsync();
        alerts.Alerts.Should().NotContain(a => a.Key == "low_cash", "100 USD convertis dépassent les 1 000 HTG de découvert");
    }
}
