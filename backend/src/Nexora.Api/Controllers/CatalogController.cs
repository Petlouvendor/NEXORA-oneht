using Asp.Versioning;
using Microsoft.AspNetCore.Mvc;
using Nexora.Api.Security;
using Nexora.Application.Catalog;
using Nexora.Application.Security;
using Nexora.Contracts.Catalog;

namespace Nexora.Api.Controllers;

[ApiVersion("1.0")]
public sealed class CatalogController(
    CatalogService catalog, ICatalogQueries queries, ICatalogPricingEngine pricing,
    ICatalogReferenceService references, IUnitConverter converter, IPromotionService promotions) : ApiControllerBase
{
    // --- Recherche & scan (§37-38, §47) ----------------------------------
    [HttpGet("search")]
    [RequirePermission(Permissions.Catalog.View)]
    public async Task<IActionResult> Search([FromQuery] string q, [FromQuery] int limit = 20, CancellationToken ct = default)
        => Ok(await queries.SearchAsync(q, limit, ct));

    [HttpGet("scan")]
    [RequirePermission(Permissions.Catalog.View)]
    public async Task<IActionResult> Scan([FromQuery] string barcode, CancellationToken ct)
    {
        var hit = await queries.ScanAsync(barcode, ct);
        return hit is null ? NotFound() : Ok(hit);
    }

    // --- Produits (§4, §39, §47) ----------------------------------------
    [HttpGet("products")]
    [RequirePermission(Permissions.Catalog.View)]
    public async Task<IActionResult> ListProducts(
        [FromQuery] string? q, [FromQuery] Guid? categoryId, [FromQuery] string? status,
        [FromQuery] int limit = 100, CancellationToken ct = default)
        => Ok(await queries.ListProductsAsync(q, categoryId, status, limit, ct));

    [HttpGet("products/{id:guid}")]
    [RequirePermission(Permissions.Catalog.View)]
    public async Task<IActionResult> GetProduct(Guid id, CancellationToken ct)
    {
        var p = await queries.GetProductAsync(id, ct);
        return p is null ? NotFound() : Ok(p);
    }

    [HttpPost("products")]
    [RequirePermission(Permissions.Catalog.Create)]
    public async Task<IActionResult> CreateProduct([FromBody] CreateProductCommand c, CancellationToken ct)
        => FromResult(await catalog.CreateProductAsync(c, ct));

    [HttpPut("products/{id:guid}")]
    [RequirePermission(Permissions.Catalog.Update)]
    public async Task<IActionResult> UpdateProduct(Guid id, [FromBody] UpdateProductCommand c, CancellationToken ct)
        => FromResult(await catalog.UpdateProductAsync(id, c, ct));

    [HttpPost("products/{id:guid}/status")]
    [RequirePermission(Permissions.Catalog.Update)]
    public async Task<IActionResult> SetStatus(Guid id, [FromBody] SetProductStatusBody body, CancellationToken ct)
        => FromResult(await catalog.SetProductStatusAsync(id, body.Status, ct));

    [HttpPost("products/{id:guid}/suppliers")]
    [RequirePermission(Permissions.Catalog.Update)]
    public async Task<IActionResult> AddSupplier(Guid id, [FromBody] AddProductSupplierCommand c, CancellationToken ct)
        => FromResult(await catalog.AddSupplierAsync(c with { ProductId = id }, ct));

    [HttpPost("products/{id:guid}/prices")]
    [RequirePermission(Permissions.Catalog.ManagePrice)]
    public async Task<IActionResult> SetPrice(Guid id, [FromBody] SetProductPriceCommand c, CancellationToken ct)
        => FromResult(await catalog.SetPriceAsync(c with { ProductId = id }, ct));

    [HttpPost("products/{id:guid}/barcodes")]
    [RequirePermission(Permissions.Catalog.Update)]
    public async Task<IActionResult> AddBarcode(Guid id, [FromBody] AddBarcodeBody body, CancellationToken ct)
        => FromResult(await catalog.AddBarcodeAsync(id, body.Barcode, body.PackSize, body.BarcodeType, body.IsPrimary, ct));

    [HttpPut("products/{id:guid}/reorder-rule")]
    [RequirePermission(Permissions.Inventory.Adjust)]
    public async Task<IActionResult> SetReorderRule(Guid id, [FromBody] SetReorderRuleCommand c, CancellationToken ct)
        => FromResult(await catalog.SetReorderRuleAsync(c with { ProductId = id }, ct));

    // --- Catégories (§9) ----------------------------------------------
    [HttpGet("categories")]
    [RequirePermission(Permissions.Catalog.View)]
    public async Task<IActionResult> ListCategories(CancellationToken ct) => Ok(await queries.ListCategoriesAsync(ct));

    [HttpPost("categories")]
    [RequirePermission(Permissions.Catalog.Create)]
    public async Task<IActionResult> CreateCategory([FromBody] CreateCategoryRequest r, CancellationToken ct)
        => FromResult(await catalog.CreateCategoryAsync(r, ct));

    [HttpPut("categories/{id:guid}")]
    [RequirePermission(Permissions.Catalog.Update)]
    public async Task<IActionResult> UpdateCategory(Guid id, [FromBody] UpdateCategoryRequest r, CancellationToken ct)
        => FromResult(await references.UpdateCategoryAsync(id, r, ct));

    // --- Unités & conversions (§13) ---------------------------------
    [HttpGet("units")]
    [RequirePermission(Permissions.Catalog.View)]
    public async Task<IActionResult> ListUnits(CancellationToken ct) => Ok(await references.ListUnitsAsync(ct));

    [HttpPost("units")]
    [RequirePermission(Permissions.Catalog.Create)]
    public async Task<IActionResult> CreateUnit([FromBody] CreateUnitRequest r, CancellationToken ct)
        => FromResult(await references.CreateUnitAsync(r, ct));

    [HttpGet("unit-conversions")]
    [RequirePermission(Permissions.Catalog.View)]
    public async Task<IActionResult> ListConversions([FromQuery] Guid? productId, CancellationToken ct)
        => Ok(await references.ListConversionsAsync(productId, ct));

    [HttpPost("unit-conversions")]
    [RequirePermission(Permissions.Catalog.Update)]
    public async Task<IActionResult> CreateConversion([FromBody] CreateUnitConversionRequest r, CancellationToken ct)
        => FromResult(await references.CreateConversionAsync(r, ct));

    [HttpDelete("unit-conversions/{id:guid}")]
    [RequirePermission(Permissions.Catalog.Update)]
    public async Task<IActionResult> DeleteConversion(Guid id, CancellationToken ct)
        => FromResult(await references.DeleteConversionAsync(id, ct));

    /// <summary>Aperçu de conversion calculé par le serveur — l'écran ne convertit jamais lui-même.</summary>
    [HttpGet("units/convert")]
    [RequirePermission(Permissions.Catalog.View)]
    public async Task<IActionResult> Convert(
        [FromQuery] Guid? productId, [FromQuery] Guid fromUnitId, [FromQuery] Guid toUnitId,
        [FromQuery] decimal quantity, CancellationToken ct)
        => FromResult(await converter.ConvertAsync(productId, fromUnitId, toUnitId, quantity, ct));

    // --- Image produit (stockage nexora.attachment, Phase 44) -------------
    [HttpPost("products/{id:guid}/image")]
    [RequirePermission(Permissions.Catalog.Update)]
    [RequestSizeLimit(4 * 1024 * 1024)]
    public async Task<IActionResult> SetImage(Guid id, [FromBody] SetProductImageRequest r, CancellationToken ct)
        => FromResult(await references.SetProductImageAsync(id, r, ct));

    [HttpGet("products/{id:guid}/image")]
    [RequirePermission(Permissions.Catalog.View)]
    public async Task<IActionResult> GetImage(Guid id, CancellationToken ct)
    {
        var result = await references.GetProductImageAsync(id, ct);
        if (!result.IsSuccess) return FromResult(result);
        var ext = result.Value.ContentType switch { "image/jpeg" => "jpg", "image/webp" => "webp", _ => "png" };
        return Download(result.Value.Content, result.Value.ContentType, $"produit-{id:N}.{ext}");
    }

    // --- Listes tarifaires (§18) -------------------------------------
    [HttpGet("price-lists")]
    [RequirePermission(Permissions.Catalog.View)]
    public async Task<IActionResult> ListPriceLists(CancellationToken ct) => Ok(await queries.ListPriceListsAsync(ct));

    [HttpPost("price-lists")]
    [RequirePermission(Permissions.Catalog.ManagePrice)]
    public async Task<IActionResult> CreatePriceList([FromBody] CreatePriceListRequest r, CancellationToken ct)
        => FromResult(await catalog.CreatePriceListAsync(r, ct));

    // --- Taxes & promotions (§21, §23) -----------------------------
    [HttpGet("taxes")]
    [RequirePermission(Permissions.Catalog.View)]
    public async Task<IActionResult> ListTaxes(CancellationToken ct) => Ok(await queries.ListTaxesAsync(ct));

    /// <summary>Promotions avec conditions et cibles (le calcul est celui de la caisse : PromotionCalculator).</summary>
    [HttpGet("promotions")]
    [RequirePermission(Permissions.Catalog.View)]
    public async Task<IActionResult> ListPromotions(CancellationToken ct) => Ok(await promotions.ListAsync(ct));

    [HttpPost("promotions")]
    [RequirePermission(Permissions.Catalog.ManagePromotion)]
    public async Task<IActionResult> CreatePromotion([FromBody] SavePromotionRequest r, CancellationToken ct)
        => FromResult(await promotions.CreateAsync(r, ct));

    [HttpPut("promotions/{id:guid}")]
    [RequirePermission(Permissions.Catalog.ManagePromotion)]
    public async Task<IActionResult> UpdatePromotion(Guid id, [FromBody] SavePromotionRequest r, CancellationToken ct)
        => FromResult(await promotions.UpdateAsync(id, r, ct));

    [HttpPost("promotions/{id:guid}/status")]
    [RequirePermission(Permissions.Catalog.ManagePromotion)]
    public async Task<IActionResult> SetPromotionStatus(Guid id, [FromBody] SetStatusRequest r, CancellationToken ct)
        => FromResult(await promotions.SetStatusAsync(id, r.Status, ct));

    [HttpGet("coupons")]
    [RequirePermission(Permissions.Catalog.ManagePromotion)]
    public async Task<IActionResult> ListCoupons(CancellationToken ct) => Ok(await promotions.ListCouponsAsync(ct));

    [HttpPost("coupons")]
    [RequirePermission(Permissions.Catalog.ManagePromotion)]
    public async Task<IActionResult> CreateCoupon([FromBody] CreateCouponRequest r, CancellationToken ct)
        => FromResult(await promotions.CreateCouponAsync(r, ct));

    [HttpPost("coupons/{id:guid}/status")]
    [RequirePermission(Permissions.Catalog.ManagePromotion)]
    public async Task<IActionResult> SetCouponStatus(Guid id, [FromBody] SetStatusRequest r, CancellationToken ct)
        => FromResult(await promotions.SetCouponStatusAsync(id, r.Status, ct));

    // --- Devises (formulaires de prix) ------------------------------------
    [HttpGet("currencies")]
    [RequirePermission(Permissions.Catalog.View)]
    public async Task<IActionResult> ListCurrencies(CancellationToken ct) => Ok(await references.ListCurrenciesAsync(ct));

    // --- Marques & groupes de clients -----------------------------------
    [HttpGet("brands")]
    [RequirePermission(Permissions.Catalog.View)]
    public async Task<IActionResult> ListBrands(CancellationToken ct) => Ok(await references.ListBrandsAsync(ct));

    [HttpPost("brands")]
    [RequirePermission(Permissions.Catalog.Create)]
    public async Task<IActionResult> CreateBrand([FromBody] CreateNamedItemRequest r, CancellationToken ct)
        => FromResult(await references.CreateBrandAsync(r, ct));

    [HttpGet("customer-groups")]
    [RequirePermission(Permissions.Catalog.View)]
    public async Task<IActionResult> ListCustomerGroups(CancellationToken ct) => Ok(await references.ListCustomerGroupsAsync(ct));

    [HttpPost("customer-groups")]
    [RequirePermission(Permissions.Catalog.ManagePrice)]
    public async Task<IActionResult> CreateCustomerGroup([FromBody] CreateNamedItemRequest r, CancellationToken ct)
        => FromResult(await references.CreateCustomerGroupAsync(r, ct));

    // --- Moteur de prix explicable (§22) --------------------------
    [HttpPost("pricing/quote")]
    [RequirePermission(Permissions.Catalog.View)]
    public async Task<IActionResult> Quote([FromBody] PriceQuoteRequest req, CancellationToken ct)
        => FromResult(await pricing.QuoteAsync(req, ct));

    public sealed record AddBarcodeBody(string Barcode, decimal PackSize, string? BarcodeType, bool IsPrimary);
    public sealed record SetProductStatusBody(string Status);
}
