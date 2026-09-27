using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Nexora.Application.Catalog;
using Nexora.Contracts.Catalog;
using Nexora.Infrastructure.Persistence;

namespace Nexora.IntegrationTests;

/// <summary>Moteur de prix & catalogue — scénarios de validation Phase 23 §48.</summary>
[Collection("finance")]
public sealed class CatalogPricingTests(FinanceEngineFixture fx)
{
    private static readonly Guid Company = Guid.Parse("dce00000-0000-0000-0000-0000000000c1");
    private static readonly Guid Para500 = Guid.Parse("dce00000-0000-0000-0000-00000000a041");
    private static readonly Guid WholesaleList = Guid.Parse("dce00000-0000-0000-0000-00000000a025");
    private static readonly Guid BoxUnit = Guid.Parse("dce00000-0000-0000-0000-00000000a011");
    private static readonly Guid Vat10 = Guid.Parse("dce00000-0000-0000-0000-00000000a022");
    private static readonly Guid Category = Guid.Parse("dce00000-0000-0000-0000-00000000a031");

    private AsyncServiceScope Scope() => fx.Provider.CreateAsyncScope();

    private static PriceQuoteRequest Q(Guid product, decimal qty, string currency = "HTG") =>
        new(product, qty, currency, null, null, null, null, 0m, null);

    private async Task<Guid> NewProduct(IServiceProvider sp, string suffix, Guid? tax = null, string type = "service")
    {
        var r = await sp.GetRequiredService<CatalogService>().CreateProductAsync(new CreateProductCommand(
            $"PR-{suffix}", $"Produit {suffix}", null, BoxUnit, type, null, null, null, tax, false, false));
        r.IsSuccess.Should().BeTrue(r.Error?.Message);
        return r.Value;
    }

    // ---- §18, §20, §22 : prix de base + palier quantité, calcul explicable ----
    [Fact]
    public async Task Base_price_and_quantity_tier_are_explained()
    {
        await using var s = Scope();
        var engine = s.ServiceProvider.GetRequiredService<ICatalogPricingEngine>();

        var one = (await engine.QuoteAsync(Q(Para500, 1))).Value!;
        one.BaseUnitPrice.Should().Be(75m);
        one.LineGross.Should().Be(75m);
        one.Steps[0].Rule.Should().Be("base_price");
        one.Steps.Should().Contain(st => st.Rule == "final");

        // Sur la liste « Grossiste », le palier ≥ 10 fait tomber le prix unitaire à 65.
        var bulk = (await engine.QuoteAsync(Q(Para500, 12) with { PriceListId = WholesaleList })).Value!;
        bulk.NetUnitPrice.Should().Be(65m);
        bulk.LineGross.Should().Be(780m);   // 12 * 65
        bulk.Steps.Should().Contain(st => st.Rule == "price_list" || st.Rule == "quantity_tier");
        bulk.Steps.Last().Rule.Should().Be("final");
    }

    // ---- §27 : le prix dépend de la devise de transaction ----
    [Fact]
    public async Task Quote_uses_the_currency_specific_price()
    {
        await using var s = Scope();
        var engine = s.ServiceProvider.GetRequiredService<ICatalogPricingEngine>();

        (await engine.QuoteAsync(Q(Para500, 1, "HTG"))).Value!.BaseUnitPrice.Should().Be(75m);
        (await engine.QuoteAsync(Q(Para500, 1, "USD"))).Value!.BaseUnitPrice.Should().Be(0.55m);
    }

    // ---- §25 : un prix saisi TTC est décomposé de façon déterministe ----
    [Fact]
    public async Task Tax_inclusive_price_is_split_deterministically()
    {
        await using var s = Scope();
        var catalog = s.ServiceProvider.GetRequiredService<CatalogService>();
        var engine = s.ServiceProvider.GetRequiredService<ICatalogPricingEngine>();
        var id = await NewProduct(s.ServiceProvider, "TTC", tax: Vat10);

        (await catalog.SetPriceAsync(new SetProductPriceCommand(
            id, "sale", 110m, "HTG", 0m, null, null, TaxIncluded: true))).IsSuccess.Should().BeTrue();

        var q = (await engine.QuoteAsync(Q(id, 2))).Value!;
        q.NetUnitPrice.Should().Be(100m);          // 110 / 1.10
        q.LineNet.Should().Be(200m);
        q.LineTax.Should().Be(20m);
        q.LineGross.Should().Be(220m);
        q.Steps.Should().Contain(st => st.Rule == "tax_extraction");
    }

    // ---- §21-22 : une promotion active apparaît comme une étape de prix ----
    [Fact]
    public async Task Promotion_appears_as_a_pricing_step()
    {
        await using var s = Scope();
        var catalog = s.ServiceProvider.GetRequiredService<CatalogService>();
        var engine = s.ServiceProvider.GetRequiredService<ICatalogPricingEngine>();
        var db = s.ServiceProvider.GetRequiredService<NexoraDbContext>();
        var id = await NewProduct(s.ServiceProvider, "PROMO");
        (await catalog.SetPriceAsync(new SetProductPriceCommand(id, "sale", 200m, "HTG", 0m, null, null))).IsSuccess.Should().BeTrue();

        var promoId = Guid.NewGuid();
        await db.Database.ExecuteSqlAsync($"""
            INSERT INTO nexora.promotion (id, company_id, name, promo_type, value, priority, status)
            VALUES ({promoId}, {Company}, 'Soldes -15%', 'percentage', 15, 5, 'active')
            """);
        await db.Database.ExecuteSqlAsync($"""
            INSERT INTO nexora.promotion_target (id, promotion_id, company_id, target_type, product_id)
            VALUES ({Guid.NewGuid()}, {promoId}, {Company}, 'product', {id})
            """);

        var q = (await engine.QuoteAsync(Q(id, 1))).Value!;
        q.LineDiscount.Should().Be(30m);           // 15 % de 200
        q.LineNet.Should().Be(170m);
        q.Steps.Should().Contain(st => st.Rule == "promotion" && st.Reference == "percentage");
    }

    // ---- §46 : la remise manuelle est plafonnée au prix plancher ----
    [Fact]
    public async Task Manual_discount_is_capped_at_the_floor_price()
    {
        await using var s = Scope();
        var catalog = s.ServiceProvider.GetRequiredService<CatalogService>();
        var engine = s.ServiceProvider.GetRequiredService<ICatalogPricingEngine>();
        var id = await NewProduct(s.ServiceProvider, "FLOOR");
        (await catalog.SetPriceAsync(new SetProductPriceCommand(
            id, "sale", 100m, "HTG", 0m, null, null, MinPrice: 80m))).IsSuccess.Should().BeTrue();

        var q = (await engine.QuoteAsync(Q(id, 1) with { ManualDiscount = 50m })).Value!;
        q.LineNet.Should().Be(80m);                 // remise ramenée à 20, pas 50
        q.LineDiscount.Should().Be(20m);
        q.Steps.Should().Contain(st => st.Rule == "manual_discount" && st.Reference == "plafonnée au plancher");
    }

    // ---- §19 : un prix historique reste reconstituable après révision ----
    [Fact]
    public async Task Historical_price_is_preserved_after_a_revision()
    {
        await using var s = Scope();
        var catalog = s.ServiceProvider.GetRequiredService<CatalogService>();
        var engine = s.ServiceProvider.GetRequiredService<ICatalogPricingEngine>();
        var id = await NewProduct(s.ServiceProvider, "HIST");

        var t0 = DateTimeOffset.UtcNow.AddDays(-30);
        (await catalog.SetPriceAsync(new SetProductPriceCommand(id, "sale", 40m, "HTG", 0m, null, t0))).IsSuccess.Should().BeTrue();
        (await catalog.SetPriceAsync(new SetProductPriceCommand(id, "sale", 55m, "HTG", 0m, null, DateTimeOffset.UtcNow))).IsSuccess.Should().BeTrue();

        (await engine.QuoteAsync(Q(id, 1) with { At = t0.AddDays(1) })).Value!.BaseUnitPrice.Should().Be(40m);
        (await engine.QuoteAsync(Q(id, 1))).Value!.BaseUnitPrice.Should().Be(55m);
    }

    // ---- §37-38 : recherche tolérante + scan code-barres exact ----
    [Fact]
    public async Task Search_is_tolerant_and_scan_returns_one_exact_match()
    {
        await using var s = Scope();
        var q = s.ServiceProvider.GetRequiredService<ICatalogQueries>();

        (await q.SearchAsync("paracet", 10)).Should().Contain(h => h.ProductId == Para500);
        (await q.SearchAsync("PARA500", 10)).Should().Contain(h => h.ProductId == Para500);
        (await q.SearchAsync("2041", 10)).Should().Contain(h => h.ProductId == Para500 && h.PluCode == "2041");

        var scan = await q.ScanAsync("00003401011999");
        scan.Should().NotBeNull();
        scan!.ProductId.Should().Be(Para500);
        scan.ExactBarcode.Should().BeTrue();

        (await q.ScanAsync("does-not-exist")).Should().BeNull();
    }

    // ---- §45 : workflow produit — transitions contrôlées ----
    [Fact]
    public async Task Product_workflow_enforces_allowed_transitions()
    {
        await using var s = Scope();
        var catalog = s.ServiceProvider.GetRequiredService<CatalogService>();
        var db = s.ServiceProvider.GetRequiredService<NexoraDbContext>();
        var r = await catalog.CreateProductAsync(new CreateProductCommand(
            "PR-WF", "Brouillon", null, BoxUnit, "service", null, Category, null, null, false, false,
            Status: "draft"));
        r.IsSuccess.Should().BeTrue(r.Error?.Message);

        (await catalog.SetProductStatusAsync(r.Value, "active")).IsSuccess.Should().BeTrue();
        (await catalog.SetProductStatusAsync(r.Value, "draft")).IsSuccess.Should().BeFalse();   // active -> draft interdit
        (await catalog.SetProductStatusAsync(r.Value, "suspended")).IsSuccess.Should().BeTrue();

        var status = await db.Products.AsNoTracking().Where(p => p.Id == r.Value).Select(p => p.Status).FirstAsync();
        status.Should().Be("suspended");
    }

    // ---- §9, §18 : création d'une catégorie et d'une liste tarifaire ----
    [Fact]
    public async Task Category_and_price_list_creation_are_scoped_and_unique()
    {
        await using var s = Scope();
        var catalog = s.ServiceProvider.GetRequiredService<CatalogService>();
        var queries = s.ServiceProvider.GetRequiredService<ICatalogQueries>();

        var cat = await catalog.CreateCategoryAsync(new CreateCategoryRequest("Sirops", "SIR", Category));
        cat.IsSuccess.Should().BeTrue(cat.Error?.Message);
        (await catalog.CreateCategoryAsync(new CreateCategoryRequest("Autre", "SIR", null)))
            .Error!.Code.Should().Be("catalog.category_code_exists");

        var list = await catalog.CreatePriceListAsync(new CreatePriceListRequest("VIP", "Clients VIP", "member", "HTG", null, 20));
        list.IsSuccess.Should().BeTrue(list.Error?.Message);
        (await queries.ListPriceListsAsync()).Should().Contain(l => l.Code == "VIP" && l.ListType == "member");
    }
}
