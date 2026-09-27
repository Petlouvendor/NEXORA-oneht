using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Nexora.Application.Catalog;
using Nexora.Contracts.Catalog;
using Nexora.Infrastructure.Persistence;

namespace Nexora.IntegrationTests;

/// <summary>
/// Finalisation produit, phase 1 — référentiels du catalogue gérés depuis l'interface :
/// validations de création, unités, conversions déterministes, catégories, seuils, image, audit.
/// </summary>
[Collection("finance")]
public sealed class CatalogReferenceTests(FinanceEngineFixture fx)
{
    private static readonly Guid BoxUnit = Guid.Parse("dce00000-0000-0000-0000-00000000a011");
    private static readonly Guid Supplier1 = Guid.Parse("dce00000-0000-0000-0000-00000000b101");

    // PNG 1×1 transparent — octets réels, pas un faux contenu.
    private const string OnePixelPng =
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNkYPhfDwAChwGA60e6kgAAAABJRU5ErkJggg==";

    private AsyncServiceScope Scope() => fx.Provider.CreateAsyncScope();
    private static string U() => Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();

    private static async Task<Guid> NewUnit(IServiceProvider sp, string code)
    {
        var r = await sp.GetRequiredService<ICatalogReferenceService>().CreateUnitAsync(new CreateUnitRequest(code, code, 0, false));
        r.IsSuccess.Should().BeTrue(r.Error?.Message);
        return r.Value;
    }

    private static async Task<Guid> NewProduct(IServiceProvider sp, Guid unit, string? sku = null)
    {
        var r = await sp.GetRequiredService<CatalogService>().CreateProductAsync(new CreateProductCommand(
            sku ?? $"REF-{U()}", "Produit référentiel", null, unit, "stockable", null, null, null, null, false, false));
        r.IsSuccess.Should().BeTrue(r.Error?.Message);
        return r.Value;
    }

    [Fact]
    public async Task Product_creation_rejects_missing_name_unknown_unit_and_unknown_type()
    {
        await using var s = Scope();
        var catalog = s.ServiceProvider.GetRequiredService<CatalogService>();

        (await catalog.CreateProductAsync(new CreateProductCommand(
            $"V-{U()}", "  ", null, BoxUnit, "stockable", null, null, null, null, false, false)))
            .Error!.Code.Should().Be("catalog.name_required");
        (await catalog.CreateProductAsync(new CreateProductCommand(
            $"V-{U()}", "Nom", null, Guid.NewGuid(), "stockable", null, null, null, null, false, false)))
            .Error!.Code.Should().Be("catalog.unit_unknown");
        (await catalog.CreateProductAsync(new CreateProductCommand(
            $"V-{U()}", "Nom", null, BoxUnit, "magique", null, null, null, null, false, false)))
            .Error!.Code.Should().Be("catalog.product_type");
        (await catalog.CreateProductAsync(new CreateProductCommand(
            $"V-{U()}", "Nom", null, BoxUnit, "stockable", null, null, null, Guid.NewGuid(), false, false)))
            .Error!.Code.Should().Be("catalog.tax_unknown");
    }

    [Fact]
    public async Task Duplicate_sku_is_refused_even_with_surrounding_spaces()
    {
        await using var s = Scope();
        var sku = $"DUP-{U()}";
        await NewProduct(s.ServiceProvider, BoxUnit, sku);
        var again = await s.ServiceProvider.GetRequiredService<CatalogService>().CreateProductAsync(new CreateProductCommand(
            $"  {sku} ", "Autre", null, BoxUnit, "stockable", null, null, null, null, false, false));
        again.Error!.Code.Should().Be("catalog.sku_exists");
    }

    [Fact]
    public async Task Units_are_unique_per_company_case_insensitively_and_precision_is_bounded()
    {
        await using var s = Scope();
        var refs = s.ServiceProvider.GetRequiredService<ICatalogReferenceService>();
        var code = $"u{U()}".ToLowerInvariant();
        await NewUnit(s.ServiceProvider, code);

        (await refs.CreateUnitAsync(new CreateUnitRequest(code.ToUpperInvariant(), "Doublon", 0, false)))
            .Error!.Code.Should().Be("catalog.unit_exists");
        (await refs.CreateUnitAsync(new CreateUnitRequest($"P{U()}", "Trop précis", 7, false)))
            .Error!.Code.Should().Be("catalog.unit_precision");
        (await refs.ListUnitsAsync()).Should().Contain(u => u.Code == code);
    }

    [Fact]
    public async Task Carton_of_24_bottles_converts_both_ways_deterministically()
    {
        await using var s = Scope();
        var refs = s.ServiceProvider.GetRequiredService<ICatalogReferenceService>();
        var converter = s.ServiceProvider.GetRequiredService<IUnitConverter>();
        var carton = await NewUnit(s.ServiceProvider, $"CRT{U()}");
        var bottle = await NewUnit(s.ServiceProvider, $"BTL{U()}");

        (await refs.CreateConversionAsync(new CreateUnitConversionRequest(null, carton, bottle, 24m))).IsSuccess.Should().BeTrue();

        var buy = (await converter.ConvertAsync(null, carton, bottle, 10m)).Value!;
        buy.Converted.Should().Be(240m);
        buy.Rule.Should().Be("generic");

        var back = (await converter.ConvertAsync(null, bottle, carton, 48m)).Value!;
        back.Converted.Should().Be(2m);
        back.Rule.Should().Be("generic_inverse");

        // Une bouteille = 1/24 carton : arrondi à 6 décimales (domaine nexora.quantity).
        (await converter.ConvertAsync(null, bottle, carton, 1m)).Value!.Converted.Should().Be(0.041667m);
        (await converter.ConvertAsync(null, carton, carton, 3m)).Value!.Rule.Should().Be("identity");

        // La règle inverse est la même règle : la déclarer une seconde fois est refusé.
        (await refs.CreateConversionAsync(new CreateUnitConversionRequest(null, bottle, carton, 0.5m)))
            .Error!.Code.Should().Be("catalog.conversion_exists");
    }

    [Fact]
    public async Task Product_specific_conversion_wins_over_the_generic_one()
    {
        await using var s = Scope();
        var refs = s.ServiceProvider.GetRequiredService<ICatalogReferenceService>();
        var converter = s.ServiceProvider.GetRequiredService<IUnitConverter>();
        var box = await NewUnit(s.ServiceProvider, $"BX{U()}");
        var piece = await NewUnit(s.ServiceProvider, $"PC{U()}");
        var product = await NewProduct(s.ServiceProvider, piece);

        (await refs.CreateConversionAsync(new CreateUnitConversionRequest(null, box, piece, 12m))).IsSuccess.Should().BeTrue();
        (await refs.CreateConversionAsync(new CreateUnitConversionRequest(product, box, piece, 6m))).IsSuccess.Should().BeTrue();

        (await converter.ConvertAsync(product, box, piece, 2m)).Value!.Converted.Should().Be(12m);
        (await converter.ConvertAsync(null, box, piece, 2m)).Value!.Converted.Should().Be(24m);

        var detail = await s.ServiceProvider.GetRequiredService<ICatalogQueries>().GetProductAsync(product);
        detail!.Conversions.Should().ContainSingle(c => c.Factor == 6m);
        detail.UnitId.Should().Be(piece);
    }

    [Fact]
    public async Task Missing_or_invalid_conversions_are_explicit_refusals()
    {
        await using var s = Scope();
        var refs = s.ServiceProvider.GetRequiredService<ICatalogReferenceService>();
        var a = await NewUnit(s.ServiceProvider, $"A{U()}");
        var b = await NewUnit(s.ServiceProvider, $"B{U()}");

        (await s.ServiceProvider.GetRequiredService<IUnitConverter>().ConvertAsync(null, a, b, 1m))
            .Error!.Code.Should().Be("catalog.conversion_missing");
        (await refs.CreateConversionAsync(new CreateUnitConversionRequest(null, a, b, 0m)))
            .Error!.Code.Should().Be("catalog.conversion_factor");
        (await refs.CreateConversionAsync(new CreateUnitConversionRequest(null, a, a, 2m)))
            .Error!.Code.Should().Be("catalog.conversion_same_unit");

        var created = await refs.CreateConversionAsync(new CreateUnitConversionRequest(null, a, b, 3m));
        (await refs.DeleteConversionAsync(created.Value)).IsSuccess.Should().BeTrue();
        (await refs.DeleteConversionAsync(created.Value)).Error!.Code.Should().Be("catalog.conversion_not_found");
    }

    [Fact]
    public async Task Category_can_be_renamed_moved_and_never_put_under_its_own_child()
    {
        await using var s = Scope();
        var catalog = s.ServiceProvider.GetRequiredService<CatalogService>();
        var refs = s.ServiceProvider.GetRequiredService<ICatalogReferenceService>();
        var parent = (await catalog.CreateCategoryAsync(new CreateCategoryRequest("Boissons", $"C{U()}", null))).Value;
        var child = (await catalog.CreateCategoryAsync(new CreateCategoryRequest("Sodas", $"C{U()}", parent))).Value;

        (await refs.UpdateCategoryAsync(parent, new UpdateCategoryRequest("Boissons", null, child, "active")))
            .Error!.Code.Should().Be("catalog.category_cycle");
        (await refs.UpdateCategoryAsync(parent, new UpdateCategoryRequest("Boissons", null, parent, "active")))
            .Error!.Code.Should().Be("catalog.category_cycle");

        (await refs.UpdateCategoryAsync(child, new UpdateCategoryRequest("Sodas & jus", null, null, "inactive")))
            .IsSuccess.Should().BeTrue();
        var categories = await s.ServiceProvider.GetRequiredService<ICatalogQueries>().ListCategoriesAsync();
        categories.Should().ContainSingle(c => c.Id == child && c.Name == "Sodas & jus" && c.ParentId == null && c.Status == "inactive");

        (await refs.UpdateCategoryAsync(child, new UpdateCategoryRequest("X", null, null, "supprimée")))
            .Error!.Code.Should().Be("catalog.category_status");
    }

    [Fact]
    public async Task Reorder_rule_is_validated_and_shown_on_the_product_sheet()
    {
        await using var s = Scope();
        var catalog = s.ServiceProvider.GetRequiredService<CatalogService>();
        var product = await NewProduct(s.ServiceProvider, BoxUnit);

        (await catalog.SetReorderRuleAsync(new SetReorderRuleCommand(product, null, 50m, 20m, 10m, 30m, 5, false)))
            .Error!.Code.Should().Be("catalog.reorder_bounds");
        (await catalog.SetReorderRuleAsync(new SetReorderRuleCommand(product, null, -1m, 0m, 0m, 0m, 0, false)))
            .Error!.Code.Should().Be("catalog.reorder_negative");
        (await catalog.SetReorderRuleAsync(new SetReorderRuleCommand(Guid.NewGuid(), null, 1m, 2m, 1m, 1m, 0, false)))
            .Error!.Code.Should().Be("catalog.product_not_found");

        (await catalog.SetReorderRuleAsync(new SetReorderRuleCommand(product, null, 10m, 100m, 20m, 60m, 7, false)))
            .IsSuccess.Should().BeTrue();
        var rule = (await s.ServiceProvider.GetRequiredService<ICatalogQueries>().GetProductAsync(product))!.ReorderRule!;
        rule.ReorderPoint.Should().Be(20m);
        rule.MaxQuantity.Should().Be(100m);
        rule.LeadTimeDays.Should().Be(7);
    }

    [Fact]
    public async Task Supplier_link_requires_a_known_supplier_and_a_non_negative_price()
    {
        await using var s = Scope();
        var catalog = s.ServiceProvider.GetRequiredService<CatalogService>();
        var product = await NewProduct(s.ServiceProvider, BoxUnit);

        (await catalog.AddSupplierAsync(new AddProductSupplierCommand(product, Guid.NewGuid(), null, 10m, "HTG", 3, true)))
            .Error!.Code.Should().Be("catalog.supplier_unknown");
        (await catalog.AddSupplierAsync(new AddProductSupplierCommand(product, Supplier1, null, -5m, "HTG", 3, true)))
            .Error!.Code.Should().Be("catalog.supplier_values");
        (await catalog.AddSupplierAsync(new AddProductSupplierCommand(product, Supplier1, "REF-1", 10m, "HTG", 3, true)))
            .IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task Product_image_is_stored_in_attachments_and_served_back()
    {
        await using var s = Scope();
        var refs = s.ServiceProvider.GetRequiredService<ICatalogReferenceService>();
        var product = await NewProduct(s.ServiceProvider, BoxUnit);

        (await refs.SetProductImageAsync(product, new SetProductImageRequest(OnePixelPng, "image/gif")))
            .Error!.Code.Should().Be("catalog.image_type");
        (await refs.SetProductImageAsync(product, new SetProductImageRequest("pas du base64 !", "image/png")))
            .Error!.Code.Should().Be("catalog.image_content");
        (await refs.GetProductImageAsync(product)).Error!.Code.Should().Be("catalog.image_not_found");

        (await refs.SetProductImageAsync(product, new SetProductImageRequest(OnePixelPng, "image/png"))).IsSuccess.Should().BeTrue();
        var image = (await refs.GetProductImageAsync(product)).Value;
        image.ContentType.Should().Be("image/png");
        image.Content.Should().Equal(Convert.FromBase64String(OnePixelPng));

        var detail = await s.ServiceProvider.GetRequiredService<ICatalogQueries>().GetProductAsync(product);
        detail!.HasImage.Should().BeTrue();
        detail.ImageUrl.Should().BeNull("le pointeur interne attachment:{id} n'est pas une URL publiable");
    }

    [Fact]
    public async Task Product_changes_are_written_to_the_immutable_audit_log()
    {
        await using var s = Scope();
        var product = await NewProduct(s.ServiceProvider, BoxUnit);
        (await s.ServiceProvider.GetRequiredService<CatalogService>().UpdateProductAsync(product,
            new UpdateProductCommand("Nom corrigé", null, null, null, null, null, null))).IsSuccess.Should().BeTrue();

        var db = s.ServiceProvider.GetRequiredService<NexoraDbContext>();
        var actions = await db.Database.SqlQuery<string>($"""
            SELECT action AS "Value" FROM nexora.audit_log
            WHERE entity_type = 'product' AND entity_id = {product} ORDER BY occurred_at, id
            """).ToListAsync();
        actions.Should().Contain("INSERT").And.Contain("UPDATE");

        var renamed = await db.Database.SqlQuery<string>($"""
            SELECT new_values ->> 'name' AS "Value" FROM nexora.audit_log
            WHERE entity_type = 'product' AND entity_id = {product} AND action = 'UPDATE'
            """).ToListAsync();
        renamed.Should().Contain("Nom corrigé");
    }
}
