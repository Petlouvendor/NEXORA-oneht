using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Nexora.Application.Abstractions;
using Nexora.Application.Finance.Engine;
using Nexora.Application.Finance.Events;
using Nexora.Application.Finance.Ledger;
using Nexora.Application.Finance.Periods;
using Nexora.Application.Finance.Reports;
using Nexora.Application.Reporting;
using Nexora.Contracts.Finance;
using Nexora.Contracts.Reporting;
using Nexora.Domain.Common;
using Npgsql;

namespace Nexora.IntegrationTests;

/// <summary>
/// Audit 2026-10 éd. 2, lot 6 — écarts de fonctionnement : réouverture d'un exercice clôturé (A-044), budget contre
/// réalisé (A-047), balance âgée du pilotage (A-022 résidu). Chaque test travaille sur une entreprise neuve.
/// </summary>
[Collection("finance")]
public sealed class FunctionalGapsTests(FinanceEngineFixture fx) : IAsyncLifetime
{
    private sealed record NewCompany(Guid Id, Guid Cash, Guid Period, Guid Director);

    // Entreprises créées par le test : archivées ensuite, la base de la collection est partagée.
    private readonly List<Guid> _created = [];

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        foreach (var id in _created)
            await ExecAsync($"UPDATE nexora.company SET status = 'archived' WHERE id = '{id}'");
    }

    private async Task<NpgsqlConnection> OpenAsync()
    {
        var c = new NpgsqlConnection(fx.TestConnectionString);
        await c.OpenAsync();
        return c;
    }

    private async Task<T> ScalarAsync<T>(string sql)
    {
        await using var c = await OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, c);
        return (T)(await cmd.ExecuteScalarAsync())!;
    }

    private async Task ExecAsync(string sql)
    {
        await using var c = await OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, c);
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task<NewCompany> NewCompanyAsync()
    {
        var id = Guid.NewGuid();
        _created.Add(id);
        await ExecAsync($"""
            INSERT INTO nexora.company (id, name, default_currency_id)
            VALUES ('{id}', 'Lot 6 {id:N}', (SELECT id FROM nexora.currency WHERE code = 'HTG'));
            SELECT nexora.bootstrap_company_defaults('{id}', (SELECT id FROM nexora.currency WHERE code = 'HTG'), 2026);
            INSERT INTO nexora.access_scope (scope_type, company_id)
            SELECT 'COMPANY', '{id}' WHERE NOT EXISTS (SELECT 1 FROM nexora.access_scope WHERE scope_type = 'COMPANY' AND company_id = '{id}');
            """);
        return new NewCompany(id,
            await ScalarAsync<Guid>($"SELECT id FROM nexora.financial_account WHERE company_id = '{id}' AND account_type = 'cash' LIMIT 1"),
            await ScalarAsync<Guid>($"SELECT id FROM nexora.accounting_period WHERE company_id = '{id}'"),
            await fx.NewCompanyDirectorAsync(id));
    }

    private AsyncServiceScope Scope(NewCompany co, Guid? user = null)
    {
        var s = fx.Provider.CreateAsyncScope();
        var tc = (TestTenantContext)s.ServiceProvider.GetRequiredService<ITenantContext>();
        tc.SetUser(user ?? co.Director);
        tc.SetScope([co.Id], [], co.Id);
        return s;
    }

    private async Task SellAsync(NewCompany co, decimal amount, int day)
    {
        await using var s = Scope(co);
        var r = await s.ServiceProvider.GetRequiredService<IFinancialEngine>().HandleAsync(new CashSaleRecorded(
            co.Id, Guid.NewGuid(), new DateTimeOffset(2026, 3, day, 15, 0, 0, TimeSpan.Zero), Guid.NewGuid(),
            new Money(amount, "HTG"), Money.Zero("HTG"), co.Cash));
        r.IsSuccess.Should().BeTrue(r.Error?.Message);
    }

    [Fact]
    public async Task A_closed_year_can_be_reopened_completed_and_closed_again()
    {
        var co = await NewCompanyAsync();
        await SellAsync(co, 1_000m, 2);

        await using (var s = Scope(co))
        {
            var close = await s.ServiceProvider.GetRequiredService<IFiscalYearService>()
                .CloseYearAsync(co.Period, new CloseYearRequest(Force: true, Note: "Première clôture"));
            close.IsSuccess.Should().BeTrue(close.Error?.Message);
            close.Value!.NetResult.Should().Be(1_000m);
        }

        await using (var s = Scope(co))
            (await s.ServiceProvider.GetRequiredService<IAccountingPeriodService>()
                .ReopenAsync(co.Period, "Vente du 3 mars oubliée")).IsSuccess.Should().BeTrue();
        (await ScalarAsync<string>($"SELECT status FROM nexora.accounting_year_close WHERE accounting_period_id = '{co.Period}'"))
            .Should().Be("reopened");
        (await ScalarAsync<string>($"SELECT note FROM nexora.accounting_year_close WHERE accounting_period_id = '{co.Period}'"))
            .Should().Contain("Vente du 3 mars oubliée", "le motif est conservé en base");

        await SellAsync(co, 500m, 3);

        await using (var s = Scope(co))
        {
            // Avant : « L'exercice est déjà clôturé » — les écritures passées après la réouverture n'étaient jamais soldées.
            var again = await s.ServiceProvider.GetRequiredService<IFiscalYearService>()
                .CloseYearAsync(co.Period, new CloseYearRequest(Force: true, Note: "Seconde clôture"));
            again.IsSuccess.Should().BeTrue(again.Error?.Message);
            again.Value!.NetResult.Should().Be(1_500m, "le résultat de l'exercice entier, une seule fois");

            var tb = await s.ServiceProvider.GetRequiredService<IFinancialReports>().TrialBalanceAsync(new DateOnly(2026, 12, 31));
            tb.Rows.Where(r => r.AccountType == "revenue" && r.Debit != r.Credit).Should().BeEmpty(
                "les produits sont soldés une fois, sans double clôture");
            var earnings = tb.Rows.Single(r => r.AccountCode == "1300");
            (earnings.Credit - earnings.Debit).Should().Be(1_500m);
        }
    }

    [Fact]
    public async Task Budget_versus_actual_uses_the_budget_in_force_not_every_version()
    {
        var co = await NewCompanyAsync();
        var approver = await fx.NewCompanyDirectorAsync(co.Id);
        var account = await ScalarAsync<Guid>($"SELECT id FROM nexora.account WHERE company_id = '{co.Id}' AND code = '6800'");
        await SellAsync(co, 1_000m, 2);

        Guid original;
        await using (var s = Scope(co))
        {
            var budgets = s.ServiceProvider.GetRequiredService<IBudgetService>();
            original = (await budgets.CreateAsync(new CreateBudgetRequest("Budget 2026", 2026, "HTG"))).Value!.Id;
            (await budgets.SetLinesAsync(original, new SetBudgetLinesRequest([new BudgetLineInput(account, null, null, null, 100m)])))
                .IsSuccess.Should().BeTrue();
            (await budgets.SubmitAsync(original)).IsSuccess.Should().BeTrue();
            // un brouillon abandonné ne doit jamais compter
            var draft = (await budgets.CreateAsync(new CreateBudgetRequest("Brouillon abandonné", 2026, "HTG"))).Value!.Id;
            await budgets.SetLinesAsync(draft, new SetBudgetLinesRequest([new BudgetLineInput(account, null, null, null, 999m)]));
        }
        await using (var s = Scope(co, approver))
            (await s.ServiceProvider.GetRequiredService<IBudgetService>().ApproveAsync(original)).IsSuccess.Should().BeTrue();

        Guid revision;
        await using (var s = Scope(co))
        {
            var budgets = s.ServiceProvider.GetRequiredService<IBudgetService>();
            revision = (await budgets.ReviseAsync(original)).Value!.Id;
            await budgets.SetLinesAsync(revision, new SetBudgetLinesRequest([new BudgetLineInput(account, null, null, null, 120m)]));
            (await budgets.SubmitAsync(revision)).IsSuccess.Should().BeTrue();
        }
        await using (var s = Scope(co, approver))
            (await s.ServiceProvider.GetRequiredService<IBudgetService>().ApproveAsync(revision)).IsSuccess.Should().BeTrue();
        (await ScalarAsync<string>($"SELECT status FROM nexora.budget WHERE id = '{original}'")).Should().Be("closed",
            "la révision approuvée remplace la version d'origine");

        await using (var s = Scope(co))
        {
            var report = await s.ServiceProvider.GetRequiredService<IBudgetReports>().BudgetVsActualAsync(2026, new ReportFilter());
            report.Rows.Single(r => r.AccountCode == "6800").Budget.Should().Be(120m,
                "avant : 100 + 120 + 999 = 1 219 (toutes les versions additionnées)");
            report.Rows.Single(r => r.AccountCode == "7011").Actual.Should().Be(1_000m, "un produit se lit au crédit");
        }
    }

    [Fact]
    public async Task The_pilotage_payables_aging_deducts_supplier_credits()
    {
        var co = await NewCompanyAsync();
        var supplier = Guid.NewGuid();
        await ExecAsync($"""
            INSERT INTO nexora.supplier (id, company_id, supplier_number, name, default_currency_id)
            VALUES ('{supplier}', '{co.Id}', 'F-{supplier:N}', 'Fournisseur avoir', (SELECT id FROM nexora.currency WHERE code = 'HTG'));
            INSERT INTO nexora.supplier_payable (company_id, supplier_id, currency_id, original_amount, paid_amount, credited_amount, status, issue_date, due_date)
            VALUES ('{co.Id}', '{supplier}', (SELECT id FROM nexora.currency WHERE code = 'HTG'), 100, 10, 40, 'PARTIALLY_PAID', DATE '2026-03-01', DATE '2026-03-31');
            """);

        await using var s = Scope(co);
        var aging = await s.ServiceProvider.GetRequiredService<IAgingReports>().PayablesAsync(new DateOnly(2026, 4, 15));
        aging.Rows.Single(r => r.Party == "Fournisseur avoir").Total.Should().Be(50m, "100 − 10 payés − 40 d'avoir (avant : 90)");
    }

    /// <summary>
    /// A-050 : l'import du stock d'ouverture créait le stock sans écriture — la valorisation dépassait le compte de stock
    /// de toute la valeur importée. Une reprise relancée ne double ni le stock ni l'écriture.
    /// </summary>
    [Fact]
    public async Task Imported_opening_stock_is_posted_to_the_stock_account_once()
    {
        var co = await NewCompanyAsync();
        var sku = $"OUV-{Guid.NewGuid():N}"[..20];
        await ExecAsync($"""
            INSERT INTO nexora.product (company_id, sku, name, unit_id)
            VALUES ('{co.Id}', '{sku}', 'Article repris', (SELECT id FROM nexora.unit WHERE company_id = '{co.Id}' LIMIT 1));
            """);
        var location = await ScalarAsync<string>($"SELECT code FROM nexora.inventory_location WHERE company_id = '{co.Id}' LIMIT 1");
        var row = new Dictionary<string, string?> { ["sku"] = sku, ["location"] = location, ["quantity"] = "10", ["cost"] = "5" };

        async Task<string> ImportAsync()
        {
            await using var s = Scope(co);
            var importer = s.ServiceProvider.GetServices<Nexora.Application.Migration.IEntityImporter>().Single(i => i.Kind == "opening_stock");
            var ctx = new Nexora.Application.Migration.ImportContext(co.Id, Guid.NewGuid(), co.Director, "create", false);
            return await s.ServiceProvider.GetRequiredService<IUnitOfWork>().ExecuteInTransactionAsync(async t =>
                (await importer.ProcessAsync(row, ctx, t)).Status);
        }

        (await ImportAsync()).Should().Be("created");
        (await ImportAsync()).Should().Be("skipped", "un stock d'ouverture déjà repris n'est pas réimporté");

        (await ScalarAsync<decimal>($"""
            SELECT COALESCE(SUM(l.base_debit - l.base_credit), 0) FROM nexora.journal_entry_line l
            JOIN nexora.account_mapping m ON m.company_id = l.company_id AND m.account_id = l.account_id AND m.mapping_key = 'inventory'
            WHERE l.company_id = '{co.Id}'
            """)).Should().Be(50m, "10 unités à 5 : compte de stock = valorisation (avant : 0)");
        (await ScalarAsync<decimal>($"SELECT COALESCE(SUM(quantity_on_hand * average_cost), 0) FROM nexora.stock_balance WHERE company_id = '{co.Id}'"))
            .Should().Be(50m);
    }
}
