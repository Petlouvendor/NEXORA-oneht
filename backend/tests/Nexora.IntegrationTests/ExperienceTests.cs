using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Nexora.Application.Abstractions;
using Nexora.Application.Catalog;
using Nexora.Application.Experience;
using Nexora.Application.Identity;
using Nexora.Application.Intelligence;
using Nexora.Application.Purchasing;
using Nexora.Application.Reporting;
using Nexora.Application.Sales;
using Nexora.Application.Security;
using Nexora.Contracts.Identity;
using Nexora.Contracts.Reporting;
using Nexora.Infrastructure.Persistence;

namespace Nexora.IntegrationTests;

/// <summary>
/// NEXORA ONE — expérience intelligente (docs/nexora-one-experience-intelligente.md) : recherche globale
/// Ctrl+K (§5), fil d'activité du dashboard (§6), nouvelles questions de NEXORA AI et recommandations
/// jointes aux réponses (§8). Chaque test vérifie aussi le filtrage par permission, avec de vrais
/// utilisateurs affectés à des rôles système.
/// </summary>
[Collection("finance")]
public sealed class ExperienceTests(FinanceEngineFixture fx)
{
    private static readonly Guid Company = Guid.Parse("dce00000-0000-0000-0000-0000000000c1");
    private static readonly Guid Direction = Guid.Parse("dce00000-0000-0000-0000-0000000000d1");
    private static readonly Guid Officine = Guid.Parse("dce00000-0000-0000-0000-00000000a003");
    private static readonly Guid Supplier1 = Guid.Parse("dce00000-0000-0000-0000-00000000b101");
    private static readonly Guid BoxUnit = Guid.Parse("dce00000-0000-0000-0000-00000000a011");
    private static readonly Guid CashAccount = Guid.Parse("dce00000-0000-0000-0000-00000000a051");
    private static readonly Guid CashMethod = Guid.Parse("dce00000-0000-0000-0000-00000000a081");

    /// <summary>Type de résultat / source d'activité → permission(s) qui l'autorisent (miroir du service).</summary>
    private static readonly Dictionary<string, Func<IReadOnlySet<string>, bool>> SearchRules = new()
    {
        ["product"] = g => g.Contains(Permissions.Catalog.View),
        ["customer"] = g => g.Contains(Permissions.Customers.View),
        ["supplier"] = g => g.Contains(Permissions.Suppliers.View) && g.Contains(Permissions.Purchasing.View),
        ["employee"] = g => g.Contains(Permissions.Hr.ViewEmployees),
        ["document"] = g => g.Contains(Permissions.Documents.View),
        ["sale"] = g => g.Contains(Permissions.Pos.View) && (g.Contains(Permissions.Sales.View) || g.Contains(Permissions.Reporting.ViewSales)),
    };

    private static readonly Dictionary<string, Func<IReadOnlySet<string>, bool>> ActivityRules = new()
    {
        ["sale"] = g => g.Contains(Permissions.Reporting.ViewSales) || g.Contains(Permissions.Sales.View),
        ["payment"] = g => g.Contains(Permissions.Finance.View),
        ["goods_receipt"] = g => g.Contains(Permissions.Purchasing.View),
        ["cash_session"] = g => g.Contains(Permissions.Pos.ManageSessions),
        ["journal_entry"] = g => g.Contains(Permissions.Accounting.View),
        ["recommendation"] = g => g.Contains(Permissions.Intelligence.View),
    };

    private AsyncServiceScope Scope() => fx.Provider.CreateAsyncScope();

    private static void As(IServiceProvider sp, Guid userId)
    {
        var tc = (TestTenantContext)sp.GetRequiredService<ITenantContext>();
        tc.SetUser(userId);
        tc.SetScope([Company], [], Company);
    }

    private async Task<Guid> UserWithRole(string role)
    {
        var id = Guid.NewGuid();
        await using (var c = new Npgsql.NpgsqlConnection(fx.TestConnectionString))
        {
            await c.OpenAsync();
            await using var cmd = new Npgsql.NpgsqlCommand($"""
                INSERT INTO nexora.app_user (id, email, password_hash, full_name, status)
                VALUES ('{id}', 'XP-{id:N}@test.demo', crypt('x', gen_salt('bf', 4)), '{role} test', 'active')
                """, c);
            await cmd.ExecuteNonQueryAsync();
        }
        await using var s = Scope();
        As(s.ServiceProvider, Direction);
        var granted = await s.ServiceProvider.GetRequiredService<IUserAssignmentService>()
            .GrantAsync(id, new GrantAssignmentRequest(role, "COMPANY", Company, null, null, null, null));
        granted.IsSuccess.Should().BeTrue(granted.Error?.Message);
        return id;
    }

    private async Task<Guid> StockedProduct(string name, decimal stock = 50m)
    {
        await using var s = Scope();
        As(s.ServiceProvider, Direction);
        var catalog = s.ServiceProvider.GetRequiredService<CatalogService>();
        var product = (await catalog.CreateProductAsync(new CreateProductCommand(
            $"XP-{Guid.NewGuid():N}"[..20], name, null, BoxUnit, "stockable", null, null, null, null, false, false))).Value;
        await catalog.SetPriceAsync(new SetProductPriceCommand(product, "sale", 100m, "HTG", 0m, null, null));
        await s.ServiceProvider.GetRequiredService<PurchasingService>().ReceiveAsync(new ReceiveGoodsCommand(
            Guid.NewGuid(), Supplier1, Officine, null, [new ReceiveLine(product, null, stock, 40m, null, null)], null, null));
        return product;
    }

    private async Task<(Guid Id, string Number)> CompletedSale(Guid product, decimal qty)
    {
        await using var s = Scope();
        As(s.ServiceProvider, Direction);
        var cmd = new CheckoutCommand(Guid.NewGuid(), Officine, null, null, null, null, "HTG",
            [new CheckoutLine(product, qty, null, 0m, null)],
            [new TenderInput(CashAccount, CashMethod, 100m * qty, null)], 0m, null, null, null, null);
        var sale = await s.ServiceProvider.GetRequiredService<SalesService>().CheckoutAsync(cmd);
        sale.IsSuccess.Should().BeTrue(sale.Error?.Message);
        return (sale.Value!.SaleId, sale.Value.DocumentNumber);
    }

    private async Task<IReadOnlySet<string>> Granted(IServiceProvider sp)
        => (await sp.GetRequiredService<IPermissionService>().CurrentPermissionsAsync(sp.GetRequiredService<ITenantContext>())).ToHashSet();

    // ---- §5 : Ctrl+K « Riz 25kg » → produit ; insensible aux accents ; ventes par numéro ----
    [Fact]
    public async Task Global_search_finds_products_customers_and_sales_ignoring_case_and_accents()
    {
        var tag = Guid.NewGuid().ToString("N")[..6];
        var product = await StockedProduct($"Riz Prémium 25kg {tag}", stock: 30m);
        var (saleId, number) = await CompletedSale(product, 2m);
        var customer = Guid.NewGuid();
        await using var s = Scope();
        As(s.ServiceProvider, Direction);
        await s.ServiceProvider.GetRequiredService<NexoraDbContext>().Database.ExecuteSqlAsync($"""
            INSERT INTO nexora.customer (id, company_id, customer_number, name, phone, default_currency_id)
            VALUES ({customer}, {Company}, {"XP-" + tag}, {"Boutique Élodie " + tag}, '+509 3700 0000',
                    (SELECT id FROM nexora.currency WHERE code = 'HTG'))
            """);
        var search = s.ServiceProvider.GetRequiredService<IGlobalSearchService>();

        var rice = await search.SearchAsync($"RIZ PREMIUM 25KG {tag}", 5);
        var hit = rice.Groups.Single(g => g.Kind == "product").Hits.Single();
        hit.Id.Should().Be(product);
        hit.Subtitle.Should().Contain("stock 28", "la direction voit le stock disponible (30 reçus − 2 vendus)");
        hit.Route.Should().StartWith("/catalog?q=");

        var shop = await search.SearchAsync($"elodie {tag}", 5);
        shop.Groups.Single(g => g.Kind == "customer").Hits.Should().ContainSingle(h => h.Id == customer)
            .Which.Route.Should().Be($"/customers/{customer}");

        var bySale = await search.SearchAsync(number, 5);
        bySale.Groups.Single(g => g.Kind == "sale").Hits.Should().Contain(h => h.Id == saleId && h.Route == $"/sales/{saleId}");

        (await search.SearchAsync("r", 5)).Groups.Should().BeEmpty("moins de 2 caractères : pas de recherche");
    }

    // ---- §5 : navigation adaptative — la recherche ne révèle jamais un objet que l'écran interdit ----
    [Fact]
    public async Task Global_search_only_returns_groups_the_user_may_open()
    {
        var tag = Guid.NewGuid().ToString("N")[..6];
        var product = await StockedProduct($"Savon XP {tag}");
        await CompletedSale(product, 1m);
        foreach (var role in new[] { "cashier", "stock_manager" })
        {
            var user = await UserWithRole(role);
            await using var s = Scope();
            As(s.ServiceProvider, user);
            var granted = await Granted(s.ServiceProvider);
            var result = await s.ServiceProvider.GetRequiredService<IGlobalSearchService>().SearchAsync($"XP {tag}", 5);
            foreach (var group in result.Groups)
                SearchRules[group.Kind](granted).Should().BeTrue($"{role} ne doit pas voir le groupe « {group.Kind} »");
            if (!granted.Contains(Permissions.Inventory.View))
                result.Groups.SelectMany(g => g.Hits).Should().NotContain(h => h.Subtitle != null && h.Subtitle.Contains("stock "),
                    "le stock disponible n'est montré qu'à qui voit le stock");
        }

        var stock = await UserWithRole("stock_manager");
        await using var sm = Scope();
        As(sm.ServiceProvider, stock);
        var hits = await sm.ServiceProvider.GetRequiredService<IGlobalSearchService>().SearchAsync($"XP {tag}", 5);
        hits.Groups.Should().NotContain(g => g.Kind == "sale", "le responsable stock ne voit pas les ventes (guide §18)");
    }

    // ---- §6 ACTIVITY : dernières opérations, plus récentes d'abord, filtrées par permission ----
    [Fact]
    public async Task Activity_feed_lists_recent_operations_newest_first_and_respects_permissions()
    {
        var product = await StockedProduct($"Activité XP {Guid.NewGuid():N}");
        var (saleId, number) = await CompletedSale(product, 1m);

        await using (var s = Scope())
        {
            As(s.ServiceProvider, Direction);
            var feed = await s.ServiceProvider.GetRequiredService<IActivityFeedService>().RecentAsync(50);
            feed.Should().Contain(e => e.Kind == "sale" && e.Title == $"Vente {number}" && e.Route == $"/sales/{saleId}" && e.Tone == "success");
            feed.Select(e => e.OccurredAt).Should().BeInDescendingOrder();
            feed.Should().HaveCountLessThanOrEqualTo(50);
        }

        var cashier = await UserWithRole("cashier");
        await using (var s = Scope())
        {
            As(s.ServiceProvider, cashier);
            var granted = await Granted(s.ServiceProvider);
            var feed = await s.ServiceProvider.GetRequiredService<IActivityFeedService>().RecentAsync(50);
            foreach (var e in feed)
                ActivityRules[e.Kind](granted).Should().BeTrue($"le caissier ne doit pas voir l'activité « {e.Kind} »");
            feed.Should().NotContain(e => e.Kind == "payment", "un caissier ne voit pas les encaissements de l'entreprise");
        }
    }

    // ---- §8 NEXORA AI : meilleures ventes, stock qui diminue, paiements en retard ----
    [Fact]
    public async Task Nexora_ai_answers_best_sellers_stock_decrease_and_late_payments()
    {
        var product = await StockedProduct($"Best-seller XP {Guid.NewGuid():N}", stock: 40m);
        for (var i = 0; i < 3; i++) await CompletedSale(product, 5m);
        var customer = Guid.NewGuid();

        await using var s = Scope();
        As(s.ServiceProvider, Direction);
        var db = s.ServiceProvider.GetRequiredService<NexoraDbContext>();
        await db.Database.ExecuteSqlAsync($"""
            INSERT INTO nexora.customer (id, company_id, customer_number, name, default_currency_id)
            VALUES ({customer}, {Company}, {"XP-" + customer.ToString("N")}, 'Client en retard XP',
                    (SELECT id FROM nexora.currency WHERE code = 'HTG'))
            """);
        await db.Database.ExecuteSqlAsync($"""
            INSERT INTO nexora.customer_receivable (id, company_id, customer_id, currency_id, original_amount, issue_date, due_date, status)
            VALUES ({Guid.NewGuid()}, {Company}, {customer}, (SELECT id FROM nexora.currency WHERE code = 'HTG'),
                    1500, current_date - 70, current_date - 40, 'OVERDUE')
            """);
        var ask = s.ServiceProvider.GetRequiredService<IAskNexora>();

        var best = await ask.AskAsync(new AskRequest("Quels produits se vendent le mieux ?", new ReportFilter { Preset = "year" }));
        best.Intent.Should().Be("top_products");
        best.Facts.Should().NotBeEmpty().And.BeInDescendingOrder(f => f.Value);
        best.Sources.Should().Contain("reports/sales/top-products");

        var stock = await ask.AskAsync(new AskRequest("Pourquoi mon stock diminue ?", null));
        stock.Intent.Should().Be("stock_decrease");
        stock.Facts.Should().Contain(f => f.Key == "stock_sale" && f.Value < 0, "les ventes sortent du stock");
        stock.Answer.Should().Contain("sorties par ventes");

        var late = await ask.AskAsync(new AskRequest("Quels clients ont des paiements en retard ?", null));
        late.Intent.Should().Be("overdue_receivables", "« en retard » n'était pas reconnu avant NEXORA ONE");
        late.Answer.Should().Contain("Client en retard XP");
        late.Facts.Should().Contain(f => f.Label == "Client en retard XP" && f.Value == 1500m);

        var cashier = await UserWithRole("cashier");
        await using var c = Scope();
        As(c.ServiceProvider, cashier);
        var cashierAsk = c.ServiceProvider.GetRequiredService<IAskNexora>();
        foreach (var q in new[] { "Quels produits se vendent le mieux ?", "Pourquoi mon stock diminue ?", "Quels clients ont des paiements en retard ?" })
            (await cashierAsk.AskAsync(new AskRequest(q, null))).Intent.Should().Be("forbidden", q);
    }

    // ---- §8 : « l'IA doit répondre avec […] des recommandations » ----
    [Fact]
    public async Task Assistant_answer_carries_the_related_open_recommendations()
    {
        var customer = Guid.NewGuid();
        await using var s = Scope();
        As(s.ServiceProvider, Direction);
        var db = s.ServiceProvider.GetRequiredService<NexoraDbContext>();
        await db.Database.ExecuteSqlAsync($"""
            INSERT INTO nexora.customer (id, company_id, customer_number, name, default_currency_id)
            VALUES ({customer}, {Company}, {"XP-" + customer.ToString("N")}, 'Client reco XP',
                    (SELECT id FROM nexora.currency WHERE code = 'HTG'))
            """);
        await db.Database.ExecuteSqlAsync($"""
            INSERT INTO nexora.customer_receivable (id, company_id, customer_id, currency_id, original_amount, issue_date, due_date, status)
            VALUES ({Guid.NewGuid()}, {Company}, {customer}, (SELECT id FROM nexora.currency WHERE code = 'HTG'),
                    900000, current_date - 90, current_date - 60, 'OVERDUE')
            """);
        await s.ServiceProvider.GetRequiredService<IRecommendationService>().GenerateAsync();

        var answer = await s.ServiceProvider.GetRequiredService<IAssistantService>()
            .QueryAsync(new AskRequest("Quels clients ont des paiements en retard ?", null));
        answer.Intent.Should().Be("overdue_receivables");
        answer.Related.Should().NotBeNull();
        answer.Related!.Should().HaveCountLessThanOrEqualTo(3);
        answer.Related.Should().OnlyContain(r => !string.IsNullOrWhiteSpace(r.ProposedAction));
        var open = await s.ServiceProvider.GetRequiredService<IRecommendationService>().ListAsync("open", "receivable_priority");
        answer.Related!.Select(r => r.Id).Should().BeSubsetOf(open.Select(r => r.Id).Concat(
            (await s.ServiceProvider.GetRequiredService<IRecommendationService>().ListAsync("open", "customer_inactive")).Select(r => r.Id)));
    }
}
