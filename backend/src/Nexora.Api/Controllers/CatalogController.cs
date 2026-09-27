using Asp.Versioning;
using Microsoft.AspNetCore.Mvc;
using Nexora.Api.Security;
using Nexora.Application.Catalog;
using Nexora.Application.Security;
using Nexora.Contracts.Catalog;

namespace Nexora.Api.Controllers;

[ApiVersion("1.0")]
public sealed class CatalogController(
    CatalogService catalog, ICatalogQueries queries, ICatalogPricingEngine pricing) : ApiControllerBase
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

    [HttpGet("promotions")]
    [RequirePermission(Permissions.Catalog.View)]
    public async Task<IActionResult> ListPromotions(CancellationToken ct) => Ok(await queries.ListPromotionsAsync(ct));

    // --- Moteur de prix explicable (§22) --------------------------
    [HttpPost("pricing/quote")]
    [RequirePermission(Permissions.Catalog.View)]
    public async Task<IActionResult> Quote([FromBody] PriceQuoteRequest req, CancellationToken ct)
        => FromResult(await pricing.QuoteAsync(req, ct));

    public sealed record AddBarcodeBody(string Barcode, decimal PackSize, string? BarcodeType, bool IsPrimary);
    public sealed record SetProductStatusBody(string Status);
}
