using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Nexora.Application.Abstractions;
using Nexora.Application.Catalog;
using Nexora.Application.Purchasing;
using Nexora.Application.Reporting;
using Nexora.Application.Sales;
using Nexora.Contracts.Reporting;
using Nexora.Infrastructure.Persistence;

namespace Nexora.IntegrationTests;

/// <summary>Scénarios de validation Dashboard, Rapports & Pilotage (Phase 7 §37).</summary>
[Collection("finance")]
public sealed class DashboardReportingTests(FinanceEngineFixture fx)
{
    private static readonly Guid Company = Guid.Parse("dce00000-0000-0000-0000-0000000000c1");
    private static readonly Guid CompanyRestaurant = Guid.Parse("dce00000-0000-0000-0000-0000000000c2");
    private static readonly Guid CompanySupermarket = Guid.Parse("dce00000-0000-0000-0000-0000000000c3");
    private static readonly Guid Group = Guid.Parse("dce00000-0000-0000-0000-000000000001");
    private static readonly Guid Officine = Guid.Parse("dce00000-0000-0000-0000-00000000a003");
    private static readonly Guid Supplier1 = Guid.Parse("dce00000-0000-0000-0000-00000000b101");
    private static readonly Guid BoxUnit = Guid.Parse("dce00000-0000-0000-0000-00000000a011");
    private static readonly Guid CashAccount = Guid.Parse("dce00000-0000-0000-0000-00000000a051");
    private static readonly Guid CashMethod = Guid.Parse("dce00000-0000-0000-0000-00000000a081");

    private AsyncServiceScope Scope() => fx.Provider.CreateAsyncScope();

    private async Task<Guid> NewCustomer(decimal creditLimit)
    {
        var id = Guid.NewGuid();
        await using var c = new Npgsql.NpgsqlConnection(fx.TestConnectionString);
        await c.OpenAsync();
        await using var cmd = new Npgsql.NpgsqlCommand($"""
            INSERT INTO nexora.customer (id, company_id, customer_number, name, credit_limit, default_currency_id)
            VALUES ('{id}', '{Company}', 'CLI-{id:N}', 'Client pilotage',
                    {creditLimit.ToString(System.Globalization.CultureInfo.InvariantCulture)},
                    (SELECT id FROM nexora.currency WHERE code='HTG'))
            """, c);
        await cmd.ExecuteNonQueryAsync();
        return id;
    }

    private async Task<Guid> Stocked(string suffix, decimal salePrice, decimal cost, decimal qty)
    {
        await using var s = Scope();
        var catalog = s.ServiceProvider.GetRequiredService<CatalogService>();
        var id = (await catalog.CreateProductAsync(new CreateProductCommand(
            $"RPT-{suffix}-{Guid.NewGuid():N}", $"Article {suffix}", null, BoxUnit, "stockable", null, null, null, null, false, false))).Value;
        await catalog.SetPriceAsync(new SetProductPriceCommand(id, "sale", salePrice, "HTG", 0m, null, null));
        await s.ServiceProvider.GetRequiredService<PurchasingService>().ReceiveAsync(new ReceiveGoodsCommand(
            Guid.NewGuid(), Supplier1, Officine, null, [new ReceiveLine(id, null, qty, cost, null, null)], null, null));
        return id;
    }

    private static CheckoutCommand Checkout(Guid product, decimal qty, IReadOnlyList<TenderInput> tenders,
        Guid? customer = null, DateOnly? due = null)
        => new(Guid.NewGuid(), Officine, null, null, null, customer, "HTG",
            [new CheckoutLine(product, qty, null, 0m, null)], tenders, 0m, due, null, null, null);

    private static decimal Kpi(CommandCenterResponse r, string key) => r.Kpis.First(k => k.Key == key).Value;

    // ---- §37 : le CA du Command Center se réconcilie avec le journal des ventes ----
    [Fact]
    public async Task Command_center_revenue_reconciles_with_sales_journal()
    {
        var p = await Stocked("CA", salePrice: 100m, cost: 60m, qty: 100m);
        var filter = new ReportFilter { Preset = "today" };

        await using var s = Scope();
        var kpi = s.ServiceProvider.GetRequiredService<IKpiService>();
        var reports = s.ServiceProvider.GetRequiredService<ISalesReports>();
        var sales = s.ServiceProvider.GetRequiredService<SalesService>();

        var ccBefore = Kpi(await kpi.CommandCenterAsync(filter), "revenue");
        var jBefore = (await reports.JournalAsync(filter)).TotalRevenue;
        var txBefore = Kpi(await kpi.CommandCenterAsync(filter), "transactions");

        for (var i = 0; i < 3; i++)
            (await sales.CheckoutAsync(Checkout(p, 3m, [new TenderInput(CashAccount, CashMethod, 300m, null)])))
                .IsSuccess.Should().BeTrue();

        var cc = await kpi.CommandCenterAsync(filter);
        var journal = await reports.JournalAsync(filter);

        (Kpi(cc, "revenue") - ccBefore).Should().Be(900m);
        (journal.TotalRevenue - jBefore).Should().Be(900m);
        (Kpi(cc, "revenue") - ccBefore).Should().Be(journal.TotalRevenue - jBefore);   // réconciliation §2/§34
        (Kpi(cc, "transactions") - txBefore).Should().Be(3m);
        cc.Kpis.Should().OnlyContain(k => k.Kind == KpiValue.Actual);   // §34 : aucune valeur estimée présentée comme réelle
    }

    // ---- §14 : la ventilation des ventes fonctionne sur toutes les dimensions ----
    [Fact]
    public async Task Sales_breakdown_covers_every_dimension()
    {
        var p = await Stocked("BRK", 100m, 60m, 30m);
        await using var s = Scope();
        (await s.ServiceProvider.GetRequiredService<SalesService>().CheckoutAsync(
            Checkout(p, 5m, [new TenderInput(CashAccount, CashMethod, 500m, null)]))).IsSuccess.Should().BeTrue();

        var reports = s.ServiceProvider.GetRequiredService<ISalesReports>();
        var filter = new ReportFilter { Preset = "today" };
        foreach (var dim in new[] { "product", "category", "customer", "salesperson", "payment_method", "day" })
        {
            var res = await reports.BreakdownAsync(dim, filter);
            res.Dimension.Should().Be(dim);
            res.Rows.Should().OnlyContain(r => r.MarginRate <= 100m);
        }

        var product = await reports.BreakdownAsync("product", filter);
        product.Rows.Should().Contain(r => r.Revenue >= 500m);
    }

    // ---- §37 : la balance âgée se réconcilie avec le sous-livre des créances ----
    [Fact]
    public async Task Aging_receivables_reconciles_with_open_receivable_subledger()
    {
        var p = await Stocked("AGE", 100m, 40m, 50m);
        var customer = await NewCustomer(500_000m);
        var today = DateOnly.FromDateTime(DateTime.UtcNow);

        await using var s = Scope();
        var sale = await s.ServiceProvider.GetRequiredService<SalesService>().CheckoutAsync(
            Checkout(p, 10m, [], customer, due: today.AddDays(-15)));   // échu depuis 15 j → tranche « 1-30 j »
        sale.IsSuccess.Should().BeTrue(sale.Error?.Message);

        var db = s.ServiceProvider.GetRequiredService<NexoraDbContext>();
        var subledger = await db.Receivables.AsNoTracking()
            .Where(r => r.CompanyId == Company && (r.Status == "OPEN" || r.Status == "PARTIALLY_PAID" || r.Status == "OVERDUE"))
            .SumAsync(r => r.OriginalAmount - r.PaidAmount);

        var aging = await s.ServiceProvider.GetRequiredService<IAgingReports>().ReceivablesAsync(today);

        aging.GrandTotal.Should().Be(Math.Round(subledger, 2));
        aging.BucketLabels.Should().HaveCount(5);
        // Intégrité interne : chaque ligne = somme de ses tranches
        foreach (var row in aging.Rows)
            row.Buckets.Sum().Should().BeApproximately(row.Total, 0.01m);
        aging.Rows.Should().Contain(r => r.Party == "Client pilotage" && r.Buckets[1] >= 1000m);
    }

    // ---- §37 : le tableau de bord groupe exclut une entreprise non autorisée ----
    [Fact]
    public async Task Group_dashboard_excludes_unauthorized_company()
    {
        await using var s = Scope();
        var tc = (TestTenantContext)s.ServiceProvider.GetRequiredService<ITenantContext>();
        tc.SetScope([Company, CompanyRestaurant], [Group], Company);   // Supermarché non autorisé

        var resp = await s.ServiceProvider.GetRequiredService<IKpiService>()
            .GroupDashboardAsync(Group, new ReportFilter { Preset = "month" });

        resp.Companies.Select(c => c.CompanyId).Should().BeEquivalentTo([Company, CompanyRestaurant]);
        resp.Companies.Should().OnlyContain(c => c.Authorized);
        resp.ExcludedCompanies.Should().Contain("Supermarché ABC");
        resp.ExcludedCompanies.Should().NotContain(["Pharmacie ABC", "Restaurant ABC"]);
        // Le CA consolidé ne comptabilise que les entreprises autorisées.
        resp.ConsolidatedRevenue.Should().Be(Math.Round(resp.Companies.Sum(c => c.Revenue), 2));
    }

    // ---- §37 : le groupe hors périmètre est refusé ----
    [Fact]
    public async Task Group_dashboard_rejects_group_outside_scope()
    {
        await using var s = Scope();
        var tc = (TestTenantContext)s.ServiceProvider.GetRequiredService<ITenantContext>();
        tc.SetScope([Company], [], Company);   // aucun groupe autorisé

        var act = async () => await s.ServiceProvider.GetRequiredService<IKpiService>()
            .GroupDashboardAsync(Group, new ReportFilter { Preset = "month" });

        (await act.Should().ThrowAsync<Nexora.Domain.Common.DomainException>())
            .Which.Error.Kind.Should().Be(Nexora.Domain.Common.ErrorKind.Forbidden);
    }

    // ---- §37 : le drill-down d'un KPI retrouve ses transactions sources ----
    [Fact]
    public async Task Drill_down_from_revenue_reaches_source_sale_lines()
    {
        var p = await Stocked("DRILL", 100m, 55m, 20m);
        await using var s = Scope();
        var checkout = await s.ServiceProvider.GetRequiredService<SalesService>().CheckoutAsync(
            Checkout(p, 4m, [new TenderInput(CashAccount, CashMethod, 400m, null)]));
        checkout.IsSuccess.Should().BeTrue(checkout.Error?.Message);
        var saleId = checkout.Value!.SaleId;

        var db = s.ServiceProvider.GetRequiredService<NexoraDbContext>();
        var day = (await db.Database.SqlQueryRaw<string>(
            "SELECT sold_at::date::text AS \"Value\" FROM nexora.sale WHERE id = {0}", saleId).ToListAsync()).Single();

        var drill = s.ServiceProvider.GetRequiredService<IDrillDown>();

        var days = await drill.ExpandAsync("revenue");
        days.Children.Should().Contain(n => n.Id == day);

        var salesOfDay = await drill.ExpandAsync($"revenue/{day}");
        salesOfDay.Children.Should().Contain(n => n.Id == saleId.ToString());

        var lines = await drill.ExpandAsync($"revenue/{day}/{saleId}");
        lines.Children.Should().NotBeEmpty();
        lines.Children.Sum(l => l.Value).Should().BeApproximately(400m, 0.01m);
    }

    // ---- §37 : « Ask Nexora » cite ses sources et ne présente jamais une prévision comme un fait ----
    [Fact]
    public async Task Ask_nexora_answers_with_sources_and_no_fabricated_forecast()
    {
        await using var s = Scope();
        var ask = s.ServiceProvider.GetRequiredService<IAskNexora>();

        var sales = await ask.AskAsync(new AskRequest("Combien avons-nous vendu ce mois-ci ?", null));
        sales.Intent.Should().Be("sales_total");
        sales.Sources.Should().NotBeEmpty();
        sales.ContainsForecast.Should().BeFalse();
        sales.Facts.Should().OnlyContain(f => f.Kind == KpiValue.Actual);

        var unknown = await ask.AskAsync(new AskRequest("Quelle est la météo demain ?", null));
        unknown.Intent.Should().Be("unknown");
    }

    // ---- §37 : l'export respecte le périmètre et est journalisé ----
    [Fact]
    public async Task Export_sales_journal_is_csv_financial_and_logged()
    {
        var p = await Stocked("EXP", 100m, 50m, 30m);
        await using var s = Scope();
        (await s.ServiceProvider.GetRequiredService<SalesService>().CheckoutAsync(
            Checkout(p, 2m, [new TenderInput(CashAccount, CashMethod, 200m, null)]))).IsSuccess.Should().BeTrue();

        var export = s.ServiceProvider.GetRequiredService<IExportService>();
        var result = await export.ExportAsync(new ExportRequest("sales_journal", "csv", new ReportFilter { Preset = "today" }));

        result.ContentType.Should().StartWith("text/csv");
        result.Sensitivity.Should().Be("financial");
        System.Text.Encoding.UTF8.GetString(result.Content).TrimStart('﻿')
            .Should().StartWith("Date;Document;Client;Vendeur;Type;Sous-total;Remise;Taxe;Total;Payé");
        result.RowCount.Should().BeGreaterThan(0);

        var db = s.ServiceProvider.GetRequiredService<NexoraDbContext>();
        var logged = (await db.Database.SqlQueryRaw<long>(
            "SELECT COUNT(*)::bigint AS \"Value\" FROM nexora.export_log WHERE report_key = {0} AND company_id = {1}",
            "sales_journal", Company).ToListAsync()).Single();
        logged.Should().BeGreaterThan(0);
    }

    // ---- §37 : le centre d'alertes ne se déclenche que sur des données validées ----
    [Fact]
    public async Task Alert_center_reports_only_validated_conditions()
    {
        await using var s = Scope();
        var response = await s.ServiceProvider.GetRequiredService<IAlertCenter>().EvaluateAsync();
        foreach (var a in response.Alerts)
        {
            a.Severity.Should().BeOneOf("critical", "warning", "info");
            a.Key.Should().NotBeNullOrWhiteSpace();
        }
        response.GeneratedAt.Should().BeCloseTo(DateTimeOffset.UtcNow, TimeSpan.FromMinutes(30));
    }
}
