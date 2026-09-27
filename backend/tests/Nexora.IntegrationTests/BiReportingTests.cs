using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Nexora.Application.Abstractions;
using Nexora.Application.Reporting;
using Nexora.Contracts.Reporting;
using Npgsql;

namespace Nexora.IntegrationTests;

/// <summary>
/// Business Intelligence, Reporting & Pilotage (Phase 42 §33-41, §48-50, §55) : catalogue KPI
/// gouverné, objectifs & score, catalogue de rapports & rapports personnalisés, rapports
/// planifiés, prévisions, insights, budgets.
/// </summary>
[Collection("finance")]
public sealed class BiReportingTests(FinanceEngineFixture fx)
{
    private static readonly Guid Company = Guid.Parse("dce00000-0000-0000-0000-0000000000c2");
    private static readonly Guid Direction = Guid.Parse("dce00000-0000-0000-0000-0000000000d1");
    private static readonly Guid Compta = Guid.Parse("dce00000-0000-0000-0000-0000000000d2");

    private AsyncServiceScope Scope() => fx.Provider.CreateAsyncScope();

    private static T For<T>(IServiceProvider sp, Guid user) where T : notnull
    {
        var tc = (TestTenantContext)sp.GetRequiredService<ITenantContext>();
        tc.SetUser(user);
        tc.SetScope([Company], [], Company);
        return sp.GetRequiredService<T>();
    }

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

    private async Task InsertSale(decimal subtotal, DateTimeOffset soldAt)
    {
        await Sql($"""
            INSERT INTO nexora.sale (id, company_id, document_number, status, currency_id, sold_at, subtotal, total, amount_paid)
            VALUES ('{Guid.NewGuid()}', '{Company}', 'BI-{Guid.NewGuid():N}', 'completed',
                    (SELECT id FROM nexora.currency WHERE code = 'HTG'), '{soldAt:O}', {subtotal}, {subtotal}, {subtotal})
            """);
    }

    // ---- §48, §50 : catalogue KPI gouverné ----
    [Fact]
    public async Task Kpi_governance_lists_seeded_definitions_and_looks_up_one()
    {
        await using var s = Scope();
        var gov = For<IKpiGovernanceService>(s.ServiceProvider, Direction);

        var all = await gov.ListAsync();
        all.Select(k => k.Key).Should().Contain(["revenue", "gross_margin", "stock_value", "cash"]);

        var one = await gov.GetAsync("revenue");
        one.IsSuccess.Should().BeTrue();
        one.Value!.Category.Should().Be("sales");

        (await gov.GetAsync("does_not_exist")).Error!.Code.Should().Be("kpi.not_found");
    }

    // ---- §39-40 : objectifs & score de performance ----
    [Fact]
    public async Task Objective_set_list_delete_and_score_reflects_actual_vs_target()
    {
        await InsertSale(6000, DateTimeOffset.UtcNow.AddDays(-1));

        await using var s = Scope();
        var objectives = For<IObjectiveService>(s.ServiceProvider, Direction);

        var set = await objectives.SetAsync(new SetObjectiveRequest(null, null, "revenue", "week", 5000m, "min", 2m));
        set.IsSuccess.Should().BeTrue(set.Error?.Message);
        set.Value!.KpiKey.Should().Be("revenue");

        (await objectives.ListAsync()).Should().Contain(o => o.KpiKey == "revenue" && o.Weight == 2m);

        var score = await objectives.ScoreAsync();
        var item = score.Items.Single(i => i.KpiKey == "revenue");
        item.Actual.Should().BeGreaterThanOrEqualTo(6000m);
        item.RatioPct.Should().BeGreaterThanOrEqualTo(100m, "l'actuel dépasse la cible");

        (await objectives.DeleteAsync(set.Value.Id)).IsSuccess.Should().BeTrue();
        (await objectives.ListAsync()).Should().NotContain(o => o.Id == set.Value.Id);
    }

    // ---- §41 : rapports planifiés ----
    [Fact]
    public async Task Report_schedule_save_list_run_due_delivers_and_logs_execution()
    {
        await using var s = Scope();
        var schedules = For<IReportScheduleService>(s.ServiceProvider, Direction);

        var saved = await schedules.SaveAsync(new SaveReportScheduleRequest(
            null, "Ventes hebdo", "sales_journal", "weekly", null, "csv", [Direction.ToString()]));
        saved.IsSuccess.Should().BeTrue(saved.Error?.Message);
        (await schedules.ListAsync()).Should().Contain(x => x.Id == saved.Value!.Id);

        await Sql($"UPDATE nexora.report_schedule SET next_run_at = now() - interval '1 hour' WHERE id = '{saved.Value!.Id}'");

        var delivered = await schedules.RunDueAsync();
        delivered.Should().BeGreaterThanOrEqualTo(1);

        var history = await schedules.HistoryAsync();
        history.Should().Contain(h => h.ReportScheduleId == saved.Value.Id && h.Status == "succeeded");

        var refreshed = (await schedules.ListAsync()).Single(x => x.Id == saved.Value.Id);
        refreshed.NextRunAt.Should().BeAfter(DateTimeOffset.UtcNow);

        (await schedules.DeleteAsync(saved.Value.Id)).IsSuccess.Should().BeTrue();
    }

    // ---- §48-49 : catalogue de rapports & rapports personnalisés ----
    [Fact]
    public async Task Report_catalog_lists_standard_reports_and_saved_report_round_trips_and_runs()
    {
        await using var s = Scope();
        var catalog = For<IReportCatalogService>(s.ServiceProvider, Direction);

        var standard = catalog.StandardReports();
        standard.Should().Contain(r => r.Key == "sales_journal" && r.RunnableGeneric);
        standard.Should().Contain(r => r.Key == "trial_balance" && !r.RunnableGeneric);

        var rejected = await catalog.SaveAsync(new SaveSavedReportRequest(null, "Bilan", "balance_sheet", JsonDocument.Parse("{}").RootElement));
        rejected.Error!.Code.Should().Be("saved_report.report_key");

        var saved = await catalog.SaveAsync(new SaveSavedReportRequest(null, "Mes ventes", "sales_journal", JsonDocument.Parse("""{"preset":"month"}""").RootElement));
        saved.IsSuccess.Should().BeTrue(saved.Error?.Message);
        (await catalog.ListSavedAsync()).Should().Contain(r => r.Id == saved.Value!.Id && r.Mine);

        var run = await catalog.RunAsync(saved.Value!.Id);
        run.IsSuccess.Should().BeTrue(run.Error?.Message);
        run.Value!.ReportKey.Should().Be("sales_journal");
        run.Value.Result.TryGetProperty("rows", out _).Should().BeTrue();

        (await catalog.DeleteSavedAsync(saved.Value.Id)).IsSuccess.Should().BeTrue();
    }

    // ---- §35-36 : prévisions ----
    [Fact]
    public async Task Forecast_generate_for_revenue_and_cash_and_rejects_invalid_input()
    {
        for (var d = 1; d <= 20; d++) await InsertSale(1000, DateTimeOffset.UtcNow.AddDays(-d));

        await using var s = Scope();
        var forecasts = For<IForecastService>(s.ServiceProvider, Direction);

        var revenue = await forecasts.GenerateAsync(new GenerateForecastRequest("revenue", 10, "optimistic"));
        revenue.IsSuccess.Should().BeTrue(revenue.Error?.Message);
        revenue.Value!.Method.Should().Be("moving_average");
        revenue.Value.Points.Should().HaveCount(10);
        revenue.Value.AdjustmentPct.Should().Be(10m);
        revenue.Value.ConfidenceLevel.Should().BeOneOf("low", "medium", "high");

        var cash = await forecasts.GenerateAsync(new GenerateForecastRequest("cash", 30, "base"));
        cash.IsSuccess.Should().BeTrue(cash.Error?.Message);
        cash.Value!.Method.Should().Be("manual");
        cash.Value.Points.Should().NotBeEmpty();

        (await forecasts.ListAsync("revenue")).Should().Contain(f => f.Id == revenue.Value.Id);

        (await forecasts.GenerateAsync(new GenerateForecastRequest("unknown", 10))).Error!.Code.Should().Be("forecast.metric");
        (await forecasts.GenerateAsync(new GenerateForecastRequest("revenue", 400))).Error!.Code.Should().Be("forecast.horizon");
        (await forecasts.GenerateAsync(new GenerateForecastRequest("revenue", 10, "custom"))).Error!.Code.Should().Be("forecast.adjustment_pct");
    }

    // ---- §37-38, §58 : insights ----
    [Fact]
    public async Task Insight_scan_detects_anomalies_then_dismiss_assign_and_dedup()
    {
        // Chiffre d'affaires en forte baisse : 14000 il y a 8-14 jours, quasi rien les 7 derniers jours.
        for (var d = 8; d <= 14; d++) await InsertSale(2000, DateTimeOffset.UtcNow.AddDays(-d));
        await InsertSale(100, DateTimeOffset.UtcNow.AddDays(-1));

        var locationId = Guid.NewGuid();
        var productId = Guid.NewGuid();
        var unitId = Guid.NewGuid();
        await Sql($"INSERT INTO nexora.inventory_location (id, company_id, name, location_type) VALUES ('{locationId}', '{Company}', 'Local BI test', 'virtual')");
        var unitCode = $"BIU{Guid.NewGuid():N}"[..8];
        await Sql($"INSERT INTO nexora.unit (id, company_id, name, code) VALUES ('{unitId}', '{Company}', 'Unité BI test', '{unitCode}')");
        await Sql($"""
            INSERT INTO nexora.product (id, company_id, sku, name, unit_id)
            VALUES ('{productId}', '{Company}', 'BI-SKU-{Guid.NewGuid():N}', 'Produit test BI', '{unitId}')
            """);
        await Sql($"""
            INSERT INTO nexora.stock_balance (id, product_id, inventory_location_id, company_id, quantity_on_hand)
            VALUES ('{Guid.NewGuid()}', '{productId}', '{locationId}', '{Company}', -7)
            """);

        await using var s = Scope();
        var insights = For<IInsightService>(s.ServiceProvider, Direction);

        var created = await insights.ScanAsync();
        created.Should().BeGreaterThanOrEqualTo(2);

        var open = await insights.ListAsync("open");
        open.Should().Contain(i => i.Category == "revenue_drop");
        var stockInsight = open.Single(i => i.Category == "stock_anomaly");

        // Un second passage ne recrée pas les mêmes insights (déduplication §37).
        (await insights.ScanAsync()).Should().Be(0);

        (await insights.DismissAsync(stockInsight.Id, new DismissInsightRequest("inventaire déjà planifié"))).IsSuccess.Should().BeTrue();
        (await insights.ListAsync("dismissed")).Should().Contain(i => i.Id == stockInsight.Id);

        var revenueInsight = open.Single(i => i.Category == "revenue_drop");
        var assigned = await For<IInsightService>(s.ServiceProvider, Compta).AssignAsync(revenueInsight.Id, new AssignInsightRequest(Direction));
        assigned.IsSuccess.Should().BeTrue(assigned.Error?.Message);
        assigned.Value!.Status.Should().Be("actioned");
        assigned.Value.AssignedTo.Should().Be(Direction);
    }

    // ---- §33-34 : budgets ----
    [Fact]
    public async Task Budget_lifecycle_requires_four_eyes_and_revision_preserves_lines()
    {
        await using var s = Scope();
        var asDirection = For<IBudgetService>(s.ServiceProvider, Direction);

        var created = await asDirection.CreateAsync(new CreateBudgetRequest($"Budget test {Guid.NewGuid():N}"[..30], 2027, "HTG"));
        created.IsSuccess.Should().BeTrue(created.Error?.Message);
        created.Value!.Status.Should().Be("draft");

        var lines = await asDirection.SetLinesAsync(created.Value.Id, new SetBudgetLinesRequest([
            new BudgetLineInput(null, null, null, null, 12000m),
            new BudgetLineInput(null, null, null, 6, 1000m),
        ]));
        lines.IsSuccess.Should().BeTrue(lines.Error?.Message);
        lines.Value!.Should().HaveCount(2);

        (await asDirection.SubmitAsync(created.Value.Id)).Value!.Status.Should().Be("pending_approval");

        (await asDirection.ApproveAsync(created.Value.Id)).Error!.Code.Should().Be("budget.four_eyes");

        var asCompta = For<IBudgetService>(s.ServiceProvider, Compta);
        var approved = await asCompta.ApproveAsync(created.Value.Id);
        approved.IsSuccess.Should().BeTrue(approved.Error?.Message);
        approved.Value!.Status.Should().Be("active");

        (await asDirection.CloseAsync(created.Value.Id)).Value!.Status.Should().Be("closed");

        var revised = await asDirection.ReviseAsync(created.Value.Id);
        revised.IsSuccess.Should().BeTrue(revised.Error?.Message);
        revised.Value!.RevisedFrom.Should().Be(created.Value.Id);
        revised.Value.Status.Should().Be("draft");

        var revisedLines = await asDirection.GetLinesAsync(revised.Value.Id);
        revisedLines.IsSuccess.Should().BeTrue(revisedLines.Error?.Message);
        revisedLines.Value!.Select(l => l.Amount).Should().BeEquivalentTo([12000m, 1000m], "la révision recopie les lignes du budget d'origine (§33)");
    }
}
