using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Nexora.Application.Abstractions;
using Nexora.Application.Catalog;
using Nexora.Application.Purchasing;
using Nexora.Application.Restaurant;
using Nexora.Application.Sales;
using Nexora.Application.Sectors;
using Nexora.Contracts.Restaurant;
using Nexora.Contracts.Sectors;
using Nexora.Infrastructure.Persistence;

namespace Nexora.IntegrationTests;

/// <summary>Scénarios de validation Modules sectoriels (Phase 8 §31).</summary>
[Collection("finance")]
public sealed class SectorModulesTests(FinanceEngineFixture fx)
{
    private static readonly Guid Company = Guid.Parse("dce00000-0000-0000-0000-0000000000c1");
    private static readonly Guid CompanyRestaurant = Guid.Parse("dce00000-0000-0000-0000-0000000000c2");
    private static readonly Guid Establishment = Guid.Parse("dce00000-0000-0000-0000-0000000000f1");
    private static readonly Guid Officine = Guid.Parse("dce00000-0000-0000-0000-00000000a003");
    private static readonly Guid Supplier1 = Guid.Parse("dce00000-0000-0000-0000-00000000b101");
    private static readonly Guid BoxUnit = Guid.Parse("dce00000-0000-0000-0000-00000000a011");
    private static readonly Guid CashAccount = Guid.Parse("dce00000-0000-0000-0000-00000000a051");
    private static readonly Guid CashMethod = Guid.Parse("dce00000-0000-0000-0000-00000000a081");

    private AsyncServiceScope Scope() => fx.Provider.CreateAsyncScope();

    private async Task<Guid> Ingredient(IServiceProvider sp, string suffix, decimal cost, decimal qty)
    {
        var catalog = sp.GetRequiredService<CatalogService>();
        var id = (await catalog.CreateProductAsync(new CreateProductCommand(
            $"ING-{suffix}-{Guid.NewGuid():N}", $"Ingrédient {suffix}", null, BoxUnit, "stockable", null, null, null, null, false, false))).Value;
        await sp.GetRequiredService<PurchasingService>().ReceiveAsync(new ReceiveGoodsCommand(
            Guid.NewGuid(), Supplier1, Officine, null, [new ReceiveLine(id, null, qty, cost, null, null)], null, null));
        return id;
    }

    private async Task<Guid> SellableProduct(IServiceProvider sp, string type, string suffix, decimal price)
    {
        var catalog = sp.GetRequiredService<CatalogService>();
        var id = (await catalog.CreateProductAsync(new CreateProductCommand(
            $"{type[..3].ToUpperInvariant()}-{suffix}-{Guid.NewGuid():N}", $"Article {suffix}", null, BoxUnit, type, null, null, null, null, false, false))).Value;
        await catalog.SetPriceAsync(new SetProductPriceCommand(id, "sale", price, "HTG", 0m, null, null));
        return id;
    }

    // ---- §31 : activer deux secteurs dans une même entreprise --------------
    [Fact]
    public async Task Multiple_activities_can_be_enabled_on_one_company()
    {
        await using var s = Scope();
        var config = s.ServiceProvider.GetRequiredService<ISectorConfigService>();

        (await config.SetActivitiesAsync(new SetActivitiesRequest(["pharmacy", "restaurant"]))).IsSuccess.Should().BeTrue();

        var cfg = await config.GetAsync();
        cfg.PrimaryActivity.Should().Be("pharmacy");
        cfg.Activities.Should().BeEquivalentTo(["pharmacy", "restaurant"]);
        cfg.Modules.Where(m => m.Enabled).Select(m => m.Key).Should().Contain(["pharmacy", "restaurant"]);

        var db = s.ServiceProvider.GetRequiredService<NexoraDbContext>();
        (await db.Companies.AsNoTracking().Where(c => c.Id == Company).Select(c => c.MainActivityType).FirstAsync())
            .Should().Be("pharmacy");
        (await config.IsModuleEnabledAsync("restaurant")).Should().BeTrue();
    }

    // ---- §31 : restaurant avec tables + KDS, réglé par le noyau ----------
    [Fact]
    public async Task Restaurant_order_uses_core_checkout_and_consumes_recipe_ingredients()
    {
        await using var s = Scope();
        var sp = s.ServiceProvider;
        var restaurant = sp.GetRequiredService<IRestaurantService>();
        var recipes = sp.GetRequiredService<IRecipeService>();
        var ledger = sp.GetRequiredService<Nexora.Application.Inventory.IStockLedger>();
        var db = sp.GetRequiredService<NexoraDbContext>();

        // Le module doit être activé pour l'entreprise (§3, §28).
        (await sp.GetRequiredService<ISectorConfigService>()
            .SetModuleAsync(new SetModuleRequest("restaurant", true))).IsSuccess.Should().BeTrue();

        var ing1 = await Ingredient(sp, "A", cost: 10m, qty: 100m);
        var ing2 = await Ingredient(sp, "B", cost: 20m, qty: 100m);
        var dish = await SellableProduct(sp, "composite", "PLAT", price: 500m);
        (await recipes.DefineAsync(new DefineRecipeRequest(dish, "Plat du jour", BoxUnit, 1m,
        [
            new RecipeComponentInput(ing1, 3m, BoxUnit, 0m, "chaud"),
            new RecipeComponentInput(ing2, 2m, BoxUnit, 0m, "chaud"),
        ]))).IsSuccess.Should().BeTrue();

        var area = (await restaurant.CreateAreaAsync(new CreateAreaRequest(Establishment, "Salle", $"S-{Guid.NewGuid():N}"[..8]))).Value;
        var table = (await restaurant.CreateTableAsync(new CreateTableRequest(Establishment, area, "Table 1", $"T-{Guid.NewGuid():N}"[..8], 4))).Value;

        var order = await restaurant.PlaceOrderAsync(new PlaceOrderRequest(table, 2, fx.UserId, "Sans piment",
            [new OrderItemInput(dish, 2m, 0m, null)]));
        order.IsSuccess.Should().BeTrue(order.Error?.Message);
        order.Value!.KitchenStatus.Should().Be("pending");
        order.Value.RunningTotal.Should().Be(1000m);

        (await restaurant.AdvanceKitchenAsync(new KitchenAdvanceRequest(order.Value.SaleId, "preparing"))).IsSuccess.Should().BeTrue();
        (await restaurant.AdvanceKitchenAsync(new KitchenAdvanceRequest(order.Value.SaleId, "ready"))).IsSuccess.Should().BeTrue();
        // Un ticket ne revient pas en arrière.
        (await restaurant.AdvanceKitchenAsync(new KitchenAdvanceRequest(order.Value.SaleId, "pending"))).IsSuccess.Should().BeFalse();

        var close = await restaurant.CloseOrderAsync(new CloseOrderRequest(
            order.Value.SaleId, [new OrderTenderInput(CashAccount, CashMethod, 1000m, null)], 1000m, 50m, null));
        close.IsSuccess.Should().BeTrue(close.Error?.Message);
        close.Value!.Total.Should().Be(1000m);

        // Ingrédients consommés (§6, §24).
        (await ledger.OnHandAsync(ing1, Officine)).Should().Be(94m);   // 100 - 3*2
        (await ledger.OnHandAsync(ing2, Officine)).Should().Be(96m);   // 100 - 2*2

        // Une seule vente réglée, écriture équilibrée (§22).
        var jeLines = await db.JournalEntryLines.AsNoTracking()
            .Where(l => l.JournalEntryId == close.Value.JournalEntryId).ToListAsync();
        jeLines.Sum(l => l.BaseDebit).Should().Be(jeLines.Sum(l => l.BaseCredit));
        var soldLine = await db.SaleLines.AsNoTracking().FirstAsync(l => l.SaleId == close.Value.SaleId);
        soldLine.UnitCost.Should().Be(70m);   // 3*10 + 2*20

        // Table libérée, commande d'origine clôturée de façon traçable (§24).
        var tbl = await db.Database.SqlQuery<TableState>($"""
            SELECT status AS "Status", (current_sale_id IS NULL) AS "Freed"
            FROM nexora.restaurant_table WHERE id = {table}
            """).FirstAsync();
        tbl.Status.Should().Be("available");
        tbl.Freed.Should().BeTrue();
        (await db.Sales.AsNoTracking().Where(x => x.Id == order.Value.SaleId).Select(x => x.Status).FirstAsync())
            .Should().Be("void");
    }

    private sealed record TableState(string Status, bool Freed);

    // ---- §31 : entreprise de services sans stock ------------------------
    [Fact]
    public async Task Service_product_sells_without_stock_movement_but_posts_accounting()
    {
        await using var s = Scope();
        var sp = s.ServiceProvider;
        var db = sp.GetRequiredService<NexoraDbContext>();
        var svc = await SellableProduct(sp, "service", "CONSULT", price: 300m);

        var r = await sp.GetRequiredService<SalesService>().CheckoutAsync(new CheckoutCommand(
            Guid.NewGuid(), Officine, null, null, null, null, "HTG",
            [new CheckoutLine(svc, 1m, null, 0m, null)],
            [new TenderInput(CashAccount, CashMethod, 300m, null)], 300m, null, null, null, null));
        r.IsSuccess.Should().BeTrue(r.Error?.Message);
        r.Value!.Total.Should().Be(300m);

        (await db.StockMovements.AsNoTracking().CountAsync(m => m.ReferenceId == r.Value.SaleId)).Should().Be(0);
        var lines = await db.JournalEntryLines.AsNoTracking().Where(l => l.JournalEntryId == r.Value.JournalEntryId).ToListAsync();
        lines.Sum(l => l.BaseDebit).Should().Be(lines.Sum(l => l.BaseCredit));
        lines.Sum(l => l.BaseCredit).Should().BeGreaterThan(0m);
    }

    // ---- §31 : ajouter un champ personnalisé (typé, obligatoire) --------
    [Fact]
    public async Task Custom_field_is_typed_and_required_validation_enforced()
    {
        await using var s = Scope();
        var cf = s.ServiceProvider.GetRequiredService<ICustomFieldService>();
        var db = s.ServiceProvider.GetRequiredService<NexoraDbContext>();

        var key = $"loyalty_tier_{Guid.NewGuid():N}"[..20];
        (await cf.DefineAsync(new DefineCustomFieldRequest("customer", key, "Palier fidélité", "number", IsRequired: true)))
            .IsSuccess.Should().BeTrue();

        var customerId = Guid.NewGuid();
        await db.Database.ExecuteSqlAsync($"""
            INSERT INTO nexora.customer (id, company_id, customer_number, name, default_currency_id)
            VALUES ({customerId}, {Company}, {"CF-" + customerId.ToString("N")[..8]}, 'Client CF',
                    (SELECT id FROM nexora.currency WHERE code = 'HTG'))
            """);

        // Obligatoire manquant → refus.
        (await cf.SetValuesAsync(new SetCustomFieldValuesRequest("customer", customerId, [])))
            .IsSuccess.Should().BeFalse();
        // Type incorrect → refus.
        (await cf.SetValuesAsync(new SetCustomFieldValuesRequest("customer", customerId,
            [new CustomFieldValueInput(key, "or")]))).IsSuccess.Should().BeFalse();
        // Valeur valide → stockée et typée.
        (await cf.SetValuesAsync(new SetCustomFieldValuesRequest("customer", customerId,
            [new CustomFieldValueInput(key, "3")]))).IsSuccess.Should().BeTrue();

        var values = await cf.GetValuesAsync("customer", customerId);
        values.Should().ContainSingle(v => v.FieldKey == key && v.Number == 3m && v.DataType == "number");
    }

    // ---- §31 : modifier la configuration sans supprimer l'historique ----
    [Fact]
    public async Task Disabling_a_module_keeps_historical_data()
    {
        await using var s = Scope();
        var sp = s.ServiceProvider;
        var config = sp.GetRequiredService<ISectorConfigService>();
        var db = sp.GetRequiredService<NexoraDbContext>();

        var svc = await SellableProduct(sp, "service", "HIST", price: 120m);
        var sale = await sp.GetRequiredService<SalesService>().CheckoutAsync(new CheckoutCommand(
            Guid.NewGuid(), Officine, null, null, null, null, "HTG",
            [new CheckoutLine(svc, 1m, null, 0m, null)],
            [new TenderInput(CashAccount, CashMethod, 120m, null)], 120m, null, null, null, null));
        sale.IsSuccess.Should().BeTrue(sale.Error?.Message);

        (await config.SetModuleAsync(new SetModuleRequest("services", false))).IsSuccess.Should().BeTrue();
        (await config.IsModuleEnabledAsync("services")).Should().BeFalse();

        // La vente historique reste intacte et consultable (§29).
        (await db.Sales.AsNoTracking().AnyAsync(x => x.Id == sale.Value!.SaleId)).Should().BeTrue();
        (await db.Database.SqlQuery<bool>($"""
            SELECT EXISTS(SELECT 1 FROM nexora.company_module
                          WHERE company_id = {Company} AND module_key = 'services' AND disabled_at IS NOT NULL) AS "Value"
            """).FirstAsync()).Should().BeTrue();
    }

    // ---- §31 : un utilisateur ne voit pas les données sectorielles hors périmètre --
    [Fact]
    public async Task Sector_configuration_is_scoped_to_the_active_company()
    {
        var key = $"scoped_{Guid.NewGuid():N}"[..16];
        await using (var s1 = Scope())
        {
            var tc = (TestTenantContext)s1.ServiceProvider.GetRequiredService<ITenantContext>();
            tc.SetScope([Company], [], Company);
            (await s1.ServiceProvider.GetRequiredService<ICustomFieldService>()
                .DefineAsync(new DefineCustomFieldRequest("product", key, "Champ c1", "text"))).IsSuccess.Should().BeTrue();
        }

        await using var s2 = Scope();
        var tc2 = (TestTenantContext)s2.ServiceProvider.GetRequiredService<ITenantContext>();
        tc2.SetScope([CompanyRestaurant], [], CompanyRestaurant);

        var fields = await s2.ServiceProvider.GetRequiredService<ICustomFieldService>().ListAsync("product");
        fields.Should().NotContain(f => f.FieldKey == key);
    }
}
