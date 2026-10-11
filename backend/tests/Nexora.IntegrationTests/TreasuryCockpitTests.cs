using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Nexora.Application.Finance.Treasury;
using Nexora.Contracts.Finance;
using Npgsql;

namespace Nexora.IntegrationTests;

/// <summary>Finance & trésorerie — scénarios de validation Phase 28 §54.</summary>
[Collection("finance")]
public sealed class TreasuryCockpitTests(FinanceEngineFixture fx)
{
    private static readonly Guid Company = Guid.Parse("dce00000-0000-0000-0000-0000000000c1");
    private static readonly Guid CashHtg = Guid.Parse("dce00000-0000-0000-0000-00000000a051");
    private static readonly Guid BankUsd = Guid.Parse("dce00000-0000-0000-0000-00000000a052");
    private static readonly Guid MobileHtg = Guid.Parse("dce00000-0000-0000-0000-00000000a053");
    private static readonly Guid Register = Guid.Parse("dce00000-0000-0000-0000-00000000a061");
    private static readonly Guid User = Guid.Parse("dce00000-0000-0000-0000-0000000000d1");

    private AsyncServiceScope Scope() => fx.Provider.CreateAsyncScope();

    private static string Num(decimal d) => d.ToString(System.Globalization.CultureInfo.InvariantCulture);

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

    private Task SetBalance(Guid account, decimal balance) =>
        Sql($"UPDATE nexora.financial_account SET current_balance = {Num(balance)} WHERE id = '{account}'");

    private Task NewReceivable(decimal amount, int dueOffsetDays) =>
        Sql($"""
            INSERT INTO nexora.customer_receivable (id, company_id, customer_id, currency_id, original_amount, issue_date, due_date, status)
            VALUES (gen_random_uuid(), '{Company}',
                    (SELECT id FROM nexora.customer WHERE company_id='{Company}' LIMIT 1),
                    (SELECT id FROM nexora.currency WHERE code='HTG'),
                    {Num(amount)}, current_date, current_date + ({dueOffsetDays}), 'OPEN')
            """);

    private Task NewPayable(decimal amount, int dueOffsetDays) =>
        Sql($"""
            INSERT INTO nexora.supplier_payable (id, company_id, supplier_id, currency_id, original_amount, issue_date, due_date, status)
            VALUES (gen_random_uuid(), '{Company}',
                    (SELECT id FROM nexora.supplier WHERE company_id='{Company}' LIMIT 1),
                    (SELECT id FROM nexora.currency WHERE code='HTG'),
                    {Num(amount)}, current_date, current_date + ({dueOffsetDays}), 'OPEN')
            """);

    private Task<Guid> PeriodId() =>
        Scalar<Guid>($"SELECT id FROM nexora.accounting_period WHERE company_id='{Company}' AND name='Exercice 2026'");

    // ---- §27 : la position agrège la trésorerie par nature de compte, en devise fonctionnelle ----
    [Fact]
    public async Task Position_aggregates_cash_across_account_types()
    {
        await SetBalance(CashHtg, 100_000m);
        await SetBalance(BankUsd, 1_000m);      // USD → × 135 (dernier taux démo)
        await SetBalance(MobileHtg, 50_000m);

        await using var s = Scope();
        var pos = await s.ServiceProvider.GetRequiredService<ITreasuryQueries>().PositionAsync();

        pos.Currency.Should().Be("HTG");
        pos.Cash.Should().Be(100_000m);
        pos.Bank.Should().Be(135_000m);
        pos.MobileMoney.Should().Be(50_000m);
        pos.Available.Should().Be(285_000m);
        pos.Accounts.Should().Contain(a => a.AccountId == BankUsd && a.Currency == "USD" && a.SystemBalance == 1_000m);
    }

    // ---- §28 : gap de liquidité négatif → alerte ----
    [Fact]
    public async Task Liquidity_flags_a_gap_when_commitments_exceed_available_cash()
    {
        await SetBalance(CashHtg, 0m);
        await SetBalance(BankUsd, 0m);
        await SetBalance(MobileHtg, 0m);
        await NewPayable(9_000_000m, dueOffsetDays: 10);

        await using var s = Scope();
        var liq = await s.ServiceProvider.GetRequiredService<ITreasuryQueries>().LiquidityAsync(30);

        liq.ShortTermCommitments.Should().BeGreaterThanOrEqualTo(9_000_000m);
        liq.ProjectedGap.Should().BeLessThan(0m);
        liq.Alerts.Should().Contain(a => a.Contains("négative"));
    }

    // ---- §22-23 : créances et dettes tombent dans la bonne tranche de maturité ----
    [Fact]
    public async Task Maturity_forecast_places_flows_in_the_right_buckets()
    {
        await NewReceivable(111_000m, dueOffsetDays: 5);    // tranche « 7 jours »
        await NewPayable(222_000m, dueOffsetDays: 45);      // tranche « 60 jours »

        await using var s = Scope();
        var f = await s.ServiceProvider.GetRequiredService<ITreasuryQueries>().MaturityForecastAsync();

        f.Buckets.Should().Contain(b => b.Label == "7 jours" && b.Inflow >= 111_000m);
        f.Buckets.Should().Contain(b => b.Label == "60 jours" && b.Outflow >= 222_000m);
        f.Buckets.Should().HaveCount(6);
    }

    // ---- §32 : un suspens s'ouvre, se liste, se résout — et c'est audité ----
    [Fact]
    public async Task Suspense_item_opens_lists_and_resolves()
    {
        await using var s = Scope();
        var svc = s.ServiceProvider.GetRequiredService<ISuspenseService>();

        var open = await svc.OpenAsync(new OpenSuspenseRequest(
            CashHtg, "bank_discrepancy", 4_250m, "HTG", "Virement recu sans reference", "REF-X", null, null));
        open.IsSuccess.Should().BeTrue(open.Error?.Message);

        (await svc.ListAsync("open", false)).Should().Contain(i => i.Id == open.Value && i.Kind == "bank_discrepancy");

        (await svc.ResolveAsync(open.Value, new ResolveSuspenseRequest("resolved", "Rapproche au versement du 12"))).IsSuccess.Should().BeTrue();
        (await svc.ListAsync("open", false)).Should().NotContain(i => i.Id == open.Value);
        (await svc.ListAsync("resolved", false)).Should().Contain(i => i.Id == open.Value);

        (await svc.ResolveAsync(open.Value, new ResolveSuspenseRequest("resolved", "x"))).Error!.Code.Should().Be("finance.suspense_closed");

        var audited = await Scalar<long>(
            $"SELECT COUNT(*) FROM nexora.audit_log WHERE entity_type = 'treasury_suspense_item' AND entity_id = '{open.Value}'");
        audited.Should().BeGreaterThanOrEqualTo(2);   // INSERT + UPDATE
    }

    // ---- §32, §48 : les suspens en retard de résolution sont signalés ----
    [Fact]
    public async Task Overdue_suspense_items_are_flagged()
    {
        await using var s = Scope();
        var svc = s.ServiceProvider.GetRequiredService<ISuspenseService>();

        var late = await svc.OpenAsync(new OpenSuspenseRequest(
            null, "unidentified", -1_500m, "HTG", "Sortie non identifiee", null,
            TestDates.Today.AddDays(-5), null));
        late.IsSuccess.Should().BeTrue(late.Error?.Message);

        var overdue = await svc.ListAsync(null, onlyOverdue: true);
        overdue.Should().Contain(i => i.Id == late.Value && i.Overdue);

        await svc.ResolveAsync(late.Value, new ResolveSuspenseRequest("cancelled", "Nettoyage test"));
    }

    // ---- §38 : la clôture financière est bloquée par une session de caisse ouverte ----
    [Fact]
    public async Task Financial_close_control_detects_an_open_cash_session()
    {
        var period = await PeriodId();
        await Sql($"UPDATE nexora.cash_session SET status='closed', closed_at=now() WHERE cash_register_id='{Register}' AND status IN ('open','closing')");
        await Sql($"""
            INSERT INTO nexora.cash_session (id, company_id, cash_register_id, user_id, opened_at, opening_balance, status)
            VALUES (gen_random_uuid(), '{Company}', '{Register}', '{User}', now(), 0, 'open')
            """);

        await using var s = Scope();
        var close = s.ServiceProvider.GetRequiredService<IFinancialCloseService>();

        var before = await close.PreviewAsync(period);
        before.IsSuccess.Should().BeTrue(before.Error?.Message);
        before.Value!.Controls.Should().Contain(c => c.Key == "cash_sessions_open" && c.Status == "fail" && c.Blocking);
        before.Value.CanClose.Should().BeFalse();

        (await close.CompleteAsync(period, new CompleteFinancialCloseRequest(Force: false)))
            .Error!.Code.Should().Be("finance.close_blocked");

        await Sql($"UPDATE nexora.cash_session SET status='closed', closed_at=now() WHERE cash_register_id='{Register}' AND status='open'");

        var after = await close.PreviewAsync(period);
        after.Value!.Controls.Should().Contain(c => c.Key == "cash_sessions_open" && c.Status == "pass");
    }

    // ---- §38 : une clôture forcée exige un motif, est enregistrée, et n'est pas rejouable ----
    [Fact]
    public async Task Financial_close_can_be_forced_with_a_reason_and_is_recorded()
    {
        var period = await PeriodId();
        // Blocage garanti : un suspens en retard.
        await using var s = Scope();
        var suspense = s.ServiceProvider.GetRequiredService<ISuspenseService>();
        var close = s.ServiceProvider.GetRequiredService<IFinancialCloseService>();
        var blocker = await suspense.OpenAsync(new OpenSuspenseRequest(
            null, "other", 10m, "HTG", "Blocage test cloture", null,
            TestDates.Today.AddDays(-2), null));
        blocker.IsSuccess.Should().BeTrue(blocker.Error?.Message);

        (await close.CompleteAsync(period, new CompleteFinancialCloseRequest(Force: false)))
            .Error!.Code.Should().Be("finance.close_blocked");
        (await close.CompleteAsync(period, new CompleteFinancialCloseRequest(Force: true, Note: "")))
            .Error!.Code.Should().Be("finance.force_reason");

        var done = await close.CompleteAsync(period, new CompleteFinancialCloseRequest(Force: true, Note: "Direction : cloture mensuelle sous reserve"));
        done.IsSuccess.Should().BeTrue(done.Error?.Message);
        done.Value!.Forced.Should().BeTrue();

        var recorded = await Scalar<long>(
            $"SELECT COUNT(*) FROM nexora.financial_close WHERE id = '{done.Value.CloseId}' AND forced = true AND status = 'completed'");
        recorded.Should().Be(1);

        (await close.CompleteAsync(period, new CompleteFinancialCloseRequest(Force: true, Note: "encore")))
            .Error!.Code.Should().Be("finance.already_closed");

        await suspense.ResolveAsync(blocker.Value, new ResolveSuspenseRequest("cancelled", "Nettoyage test"));
        await Sql($"DELETE FROM nexora.financial_close WHERE id = '{done.Value.CloseId}'");
    }

    // ---- §12, §38 : la clôture voit les lignes de relevé bancaire non rapprochées ----
    [Fact]
    public async Task Financial_close_control_detects_unreconciled_bank_lines()
    {
        var period = await PeriodId();
        var stmt = Guid.NewGuid();
        await Sql($"""
            INSERT INTO nexora.bank_statement (id, company_id, financial_account_id, statement_date, opening_balance, closing_balance, status)
            VALUES ('{stmt}', '{Company}', '{BankUsd}', current_date, 0, 0, 'imported');
            INSERT INTO nexora.bank_statement_line (id, bank_statement_id, company_id, value_date, amount, description, status)
            VALUES (gen_random_uuid(), '{stmt}', '{Company}', current_date, 123.45, 'Ligne non rapprochee', 'unmatched');
            """);

        await using var s = Scope();
        var report = await s.ServiceProvider.GetRequiredService<IFinancialCloseService>().PreviewAsync(period);

        report.Value!.Controls.Should().Contain(c => c.Key == "bank_unreconciled" && c.Status == "fail" && c.Blocking && c.Count >= 1);

        await Sql($"DELETE FROM nexora.bank_statement WHERE id = '{stmt}'");
    }
}
