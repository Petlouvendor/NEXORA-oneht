using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Nexora.Application.Finance;
using Nexora.Application.Finance.Ledger;
using Nexora.Application.Reporting;
using Nexora.Contracts.Finance;
using Nexora.Contracts.Reporting;
using Npgsql;

namespace Nexora.IntegrationTests;

/// <summary>Reporting & analytics — couche de confiance, scénarios de validation Phase 31 §53.</summary>
[Collection("finance")]
public sealed class ReportingTrustTests(FinanceEngineFixture fx)
{
    private static readonly Guid Company = Guid.Parse("dce00000-0000-0000-0000-0000000000c1");
    private static readonly Guid Customer = Guid.Parse("dce00000-0000-0000-0000-00000000b001");
    private static readonly Guid Product = Guid.Parse("dce00000-0000-0000-0000-00000000a041");

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

    // ---- §46 : réconciliation CA — ventes opérationnelles == produits comptables ----
    [Fact]
    public async Task Reconciliation_matches_a_credit_sale_on_a_private_period()
    {
        await Sql($"""
            INSERT INTO nexora.accounting_period (company_id, name, start_date, end_date, status)
            VALUES ('{Company}', 'Exercice 2023', '2023-01-01', '2023-12-31', 'open')
            """);
        var saleId = Guid.NewGuid();
        await Sql($"""
            WITH s AS (
              INSERT INTO nexora.sale (id, company_id, customer_id, document_number, status, sale_type, currency_id,
                                       sold_at, subtotal, discount_total, tax_total, total, amount_paid)
              VALUES ('{saleId}', '{Company}', '{Customer}', 'RCN-' || substr(gen_random_uuid()::text,1,8), 'completed', 'credit',
                      (SELECT id FROM nexora.currency WHERE code='HTG'), '2023-06-15', 50000, 0, 0, 50000, 0)
              RETURNING id, company_id)
            INSERT INTO nexora.sale_line (sale_id, company_id, product_id, description, quantity, unit_price, line_total, unit_cost)
            SELECT s.id, s.company_id, '{Product}', 'Test', 1, 50000, 50000, 30000 FROM s
            """);

        await using var sc = Scope();
        var finance = sc.ServiceProvider.GetRequiredService<IFinanceOperations>();
        var recon = sc.ServiceProvider.GetRequiredService<IKpiReconciliation>();

        var posted = await finance.RecordCreditSaleAsync(new RecordCreditSaleRequest(
            Guid.NewGuid(), saleId, Customer, new DateTimeOffset(2023, 6, 15, 10, 0, 0, TimeSpan.Zero),
            50000m, 0m, "HTG", new DateOnly(2023, 12, 31), null));
        posted.IsSuccess.Should().BeTrue(posted.Error?.Message);

        var report = await recon.ReconcileAsync(new DateOnly(2023, 1, 1), new DateOnly(2023, 12, 31));
        var ca = report.Lines.Should().ContainSingle(l => l.Metric == "sales_revenue").Which;
        ca.OperationalValue.Should().Be(50000m);
        ca.AccountingValue.Should().Be(50000m);
        ca.Reconciled.Should().BeTrue();
        report.Lines.Should().OnlyContain(l => l.Definition.Length > 0);   // §2 : définition claire
        report.Lines.Should().HaveCount(5);
    }

    // ---- §45 : le contrôle qualité signale un brouillon comptable et note les données ----
    [Fact]
    public async Task Data_quality_flags_a_draft_entry_and_produces_a_score()
    {
        await using var sc = Scope();
        var journal = sc.ServiceProvider.GetRequiredService<IManualJournalService>();
        var dq = sc.ServiceProvider.GetRequiredService<IDataQualityService>();

        var draft = await journal.CreateDraftAsync(new CreateJournalEntryRequest(
            "OD", DateOnly.FromDateTime(DateTime.UtcNow), "HTG", "Brouillon qualité test",
            [new JournalEntryLineInput("5711", 100m, 0m), new JournalEntryLineInput("7011", 0m, 100m)]));
        draft.IsSuccess.Should().BeTrue(draft.Error?.Message);

        var report = await dq.EvaluateAsync();
        report.Checks.Should().Contain(c => c.Key == "unposted_entries" && c.Count >= 1 && c.Severity == "warn");
        report.Score.Should().BeInRange(0m, 100m);
        report.Grade.Should().BeOneOf("A", "B", "C", "D", "E");
        report.Checks.Should().Contain(c => c.Key == "missing_account_mappings");

        await journal.DeleteDraftAsync(draft.Value);
    }

    // ---- §45 : mapping comptable essentiel manquant détecté ----
    [Fact]
    public async Task Data_quality_detects_a_missing_account_mapping()
    {
        await Sql($"DELETE FROM nexora.account_mapping WHERE company_id = '{Company}' AND mapping_key = 'cogs'");
        try
        {
            await using var sc = Scope();
            var dq = sc.ServiceProvider.GetRequiredService<IDataQualityService>();
            var report = await dq.EvaluateAsync();
            var check = report.Checks.Should().ContainSingle(c => c.Key == "missing_account_mappings").Which;
            check.Count.Should().BeGreaterThanOrEqualTo(1);
            check.Detail!.Should().Contain("cogs");
        }
        finally
        {
            await Sql($"""
                INSERT INTO nexora.account_mapping (company_id, mapping_key, account_id)
                SELECT '{Company}', 'cogs', a.id FROM nexora.account a
                WHERE a.company_id = '{Company}' AND a.code = '6031'
                ON CONFLICT DO NOTHING
                """);
        }
    }

    // ---- §45 : stock négatif signalé ----
    [Fact]
    public async Task Negative_stock_is_flagged()
    {
        var loc = await Scalar<Guid>($"SELECT id FROM nexora.inventory_location WHERE company_id = '{Company}' LIMIT 1");
        await Sql($"""
            INSERT INTO nexora.stock_balance (company_id, product_id, inventory_location_id, quantity_on_hand, average_cost)
            VALUES ('{Company}', '{Product}', '{loc}', -5, 100)
            ON CONFLICT (product_id, inventory_location_id) DO UPDATE SET quantity_on_hand = -5
            """);
        try
        {
            await using var sc = Scope();
            var dq = sc.ServiceProvider.GetRequiredService<IDataQualityService>();
            var report = await dq.EvaluateAsync();
            report.Checks.Should().Contain(c => c.Key == "negative_stock" && c.Count >= 1 && c.Severity == "error");
        }
        finally
        {
            await Sql($"DELETE FROM nexora.stock_balance WHERE company_id = '{Company}' AND product_id = '{Product}' AND quantity_on_hand < 0");
        }
    }

    // ---- §44 : un snapshot de réconciliation se rejoue à l'identique ----
    [Fact]
    public async Task Reconciliation_snapshot_is_reproducible()
    {
        await using var sc = Scope();
        var snap = sc.ServiceProvider.GetRequiredService<IAnalyticsSnapshotService>();

        var created = await snap.CreateAsync(new CreateSnapshotRequest("reconciliation", "Contrôle mensuel", null, null));
        created.IsSuccess.Should().BeTrue(created.Error?.Message);

        var detail = await snap.GetAsync(created.Value!.Id);
        detail.IsSuccess.Should().BeTrue(detail.Error?.Message);
        detail.Value!.Header.Kind.Should().Be("reconciliation");
        detail.Value.Result.GetProperty("lines").GetArrayLength().Should().Be(5);

        (await snap.ListAsync("reconciliation")).Should().Contain(s => s.Id == created.Value!.Id);

        (await snap.CreateAsync(new CreateSnapshotRequest("nonsense", null, null, null)))
            .Error!.Code.Should().Be("reporting.snapshot_kind");
    }

    // ---- §44 : un snapshot qualité porte un score ----
    [Fact]
    public async Task Data_quality_snapshot_carries_a_score()
    {
        await using var sc = Scope();
        var snap = sc.ServiceProvider.GetRequiredService<IAnalyticsSnapshotService>();

        var created = await snap.CreateAsync(new CreateSnapshotRequest("data_quality", null, null, null));
        created.IsSuccess.Should().BeTrue(created.Error?.Message);
        created.Value!.Score.Should().NotBeNull();
        created.Value!.Score.Should().BeInRange(0m, 100m);

        var detail = await snap.GetAsync(created.Value!.Id);
        detail.Value!.Result.GetProperty("grade").GetString().Should().NotBeNullOrEmpty();
    }
}
