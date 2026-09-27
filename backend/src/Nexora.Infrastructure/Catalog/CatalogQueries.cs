using Microsoft.EntityFrameworkCore;
using Nexora.Application.Abstractions;
using Nexora.Application.Catalog;
using Nexora.Contracts.Catalog;
using Nexora.Infrastructure.Persistence;

namespace Nexora.Infrastructure.Catalog;

/// <summary>Lectures catalogue : recherche tolérante, scan POS, fiche produit, référentiels (Phase 23 §37-39, §47).</summary>
public sealed class CatalogQueries(NexoraDbContext db, ITenantContext tenant, IPermissionService permissions) : ICatalogQueries
{
    private Guid Company => tenant.ActiveCompanyId
        ?? throw new InvalidOperationException("Aucune entreprise active.");

    private sealed record HitRow(
        Guid ProductId, string Sku, string Name, string? ShortName, string? Barcode, string? PluCode,
        string ProductType, string Status, string? CategoryName, string? BrandName,
        decimal? SalePrice, string? Currency, bool ExactBarcode);

    public async Task<IReadOnlyList<CatalogHitDto>> SearchAsync(string term, int limit, CancellationToken ct = default)
    {
        term = (term ?? "").Trim();
        if (term.Length == 0) return [];
        limit = Math.Clamp(limit, 1, 100);

        var rows = await db.Database.SqlQuery<HitRow>($"""
            SELECT p.id AS "ProductId", p.sku AS "Sku", p.name AS "Name", p.short_name AS "ShortName",
                   COALESCE(p.barcode, (SELECT b.barcode FROM nexora.product_barcode b
                                        WHERE b.product_id = p.id AND b.is_primary LIMIT 1)) AS "Barcode",
                   p.plu_code AS "PluCode", p.product_type AS "ProductType", p.status AS "Status",
                   c.name AS "CategoryName", br.name AS "BrandName",
                   dp.amount AS "SalePrice", dp.code AS "Currency",
                   COALESCE(p.barcode = {term}, false)
                     OR EXISTS (SELECT 1 FROM nexora.product_barcode b
                        WHERE b.product_id = p.id AND b.barcode = {term} AND b.status = 'active') AS "ExactBarcode"
            FROM nexora.product p
            JOIN nexora.company co ON co.id = p.company_id
            LEFT JOIN nexora.product_category c ON c.id = p.category_id
            LEFT JOIN nexora.brand br ON br.id = p.brand_id
            CROSS JOIN LATERAL (SELECT nexora.search_norm({term}) AS n) q
            LEFT JOIN LATERAL (
                SELECT pp.amount, cur.code
                FROM nexora.product_price pp JOIN nexora.currency cur ON cur.id = pp.currency_id
                WHERE pp.product_id = p.id AND pp.price_type = 'sale' AND pp.price_list_id IS NULL
                  AND pp.customer_group_id IS NULL AND pp.quantity_min = 0
                  AND (pp.valid_to IS NULL OR pp.valid_to > now())
                ORDER BY (pp.currency_id = co.default_currency_id) DESC, pp.valid_from DESC
                LIMIT 1) dp ON true
            WHERE p.company_id = {Company}
              AND (nexora.search_norm(p.sku) LIKE '%' || q.n || '%'
                   OR nexora.search_norm(p.name) LIKE '%' || q.n || '%'
                   OR nexora.search_norm(coalesce(p.short_name,'')) LIKE '%' || q.n || '%'
                   OR nexora.search_norm(coalesce(p.search_aliases,'')) LIKE '%' || q.n || '%'
                   OR coalesce(p.plu_code,'') = {term}
                   OR p.barcode = {term}
                   OR EXISTS (SELECT 1 FROM nexora.product_barcode b
                              WHERE b.product_id = p.id AND b.barcode = {term})
                   OR EXISTS (SELECT 1 FROM nexora.product_supplier ps
                              WHERE ps.supplier_sku IS NOT NULL
                                AND nexora.search_norm(ps.supplier_sku) LIKE '%' || q.n || '%'
                                AND ps.product_id = p.id))
            ORDER BY (p.barcode = {term}) DESC,
                     similarity(nexora.search_norm(p.name || ' ' || p.sku), q.n) DESC,
                     p.name
            LIMIT {limit}
            """).ToListAsync(ct);

        return rows.Select(Map).ToList();
    }

    public async Task<CatalogHitDto?> ScanAsync(string barcode, CancellationToken ct = default)
    {
        barcode = (barcode ?? "").Trim();
        if (barcode.Length == 0) return null;

        var row = await db.Database.SqlQuery<HitRow>($"""
            SELECT p.id AS "ProductId", p.sku AS "Sku", p.name AS "Name", p.short_name AS "ShortName",
                   {barcode} AS "Barcode", p.plu_code AS "PluCode", p.product_type AS "ProductType",
                   p.status AS "Status", c.name AS "CategoryName", br.name AS "BrandName",
                   dp.amount AS "SalePrice", dp.code AS "Currency", true AS "ExactBarcode"
            FROM nexora.product p
            JOIN nexora.company co ON co.id = p.company_id
            LEFT JOIN nexora.product_category c ON c.id = p.category_id
            LEFT JOIN nexora.brand br ON br.id = p.brand_id
            LEFT JOIN LATERAL (
                SELECT pp.amount, cur.code
                FROM nexora.product_price pp JOIN nexora.currency cur ON cur.id = pp.currency_id
                WHERE pp.product_id = p.id AND pp.price_type = 'sale' AND pp.price_list_id IS NULL
                  AND pp.customer_group_id IS NULL AND pp.quantity_min = 0
                  AND (pp.valid_to IS NULL OR pp.valid_to > now())
                ORDER BY (pp.currency_id = co.default_currency_id) DESC, pp.valid_from DESC
                LIMIT 1) dp ON true
            WHERE p.company_id = {Company}
              AND (p.barcode = {barcode}
                   OR EXISTS (SELECT 1 FROM nexora.product_barcode b
                              WHERE b.product_id = p.id AND b.barcode = {barcode} AND b.status = 'active'))
            LIMIT 1
            """).FirstOrDefaultAsync(ct);

        return row is null ? null : Map(row);
    }

    private sealed record ListRow(
        Guid Id, string Sku, string Name, string? ShortName, string ProductType, string Status,
        string? CategoryName, string? BrandName, string? PrimaryBarcode, decimal? SalePrice, string? Currency);

    public async Task<IReadOnlyList<ProductListItemDto>> ListProductsAsync(
        string? term, Guid? categoryId, string? status, int limit, CancellationToken ct = default)
    {
        limit = Math.Clamp(limit, 1, 500);
        var like = string.IsNullOrWhiteSpace(term) ? null : "%" + term!.Trim().Replace("%", "") + "%";

        var rows = await db.Database.SqlQuery<ListRow>($"""
            SELECT p.id AS "Id", p.sku AS "Sku", p.name AS "Name", p.short_name AS "ShortName",
                   p.product_type AS "ProductType", p.status AS "Status",
                   c.name AS "CategoryName", br.name AS "BrandName",
                   COALESCE(p.barcode, (SELECT b.barcode FROM nexora.product_barcode b
                                        WHERE b.product_id = p.id AND b.is_primary LIMIT 1)) AS "PrimaryBarcode",
                   dp.amount AS "SalePrice", dp.code AS "Currency"
            FROM nexora.product p
            JOIN nexora.company co ON co.id = p.company_id
            LEFT JOIN nexora.product_category c ON c.id = p.category_id
            LEFT JOIN nexora.brand br ON br.id = p.brand_id
            LEFT JOIN LATERAL (
                SELECT pp.amount, cur.code
                FROM nexora.product_price pp JOIN nexora.currency cur ON cur.id = pp.currency_id
                WHERE pp.product_id = p.id AND pp.price_type = 'sale' AND pp.price_list_id IS NULL
                  AND pp.customer_group_id IS NULL AND pp.quantity_min = 0
                  AND (pp.valid_to IS NULL OR pp.valid_to > now())
                ORDER BY (pp.currency_id = co.default_currency_id) DESC, pp.valid_from DESC
                LIMIT 1) dp ON true
            WHERE p.company_id = {Company}
              AND ({like}::text IS NULL OR p.sku ILIKE {like} OR p.name ILIKE {like} OR COALESCE(p.short_name,'') ILIKE {like})
              AND ({categoryId}::uuid IS NULL OR p.category_id = {categoryId})
              AND ({status}::text IS NULL OR p.status = {status})
            ORDER BY p.name
            LIMIT {limit}
            """).ToListAsync(ct);

        return rows.Select(r => new ProductListItemDto(
            r.Id, r.Sku, r.Name, r.ShortName, r.ProductType, r.Status,
            r.CategoryName, r.BrandName, r.PrimaryBarcode, r.SalePrice, r.Currency)).ToList();
    }

    public async Task<ProductDetailDto?> GetProductAsync(Guid productId, CancellationToken ct = default)
    {
        var p = await db.Products.AsNoTracking().FirstOrDefaultAsync(x => x.Id == productId && x.CompanyId == Company, ct);
        if (p is null) return null;

        var categoryName = p.CategoryId is null ? null : await db.Database
            .SqlQuery<string>($"""SELECT name AS "Value" FROM nexora.product_category WHERE id = {p.CategoryId}""")
            .FirstOrDefaultAsync(ct);
        var brandName = p.BrandId is null ? null : await db.Database
            .SqlQuery<string>($"""SELECT name AS "Value" FROM nexora.brand WHERE id = {p.BrandId}""")
            .FirstOrDefaultAsync(ct);

        var prices = await db.Database.SqlQuery<ProductPriceDto>($"""
            SELECT pp.id AS "Id", pp.price_type AS "PriceType", pl.code AS "PriceListCode",
                   pp.amount AS "Amount", cur.code AS "Currency", pp.tax_included AS "TaxIncluded",
                   pp.quantity_min AS "QuantityMin", cg.name AS "CustomerGroup",
                   pp.valid_from AS "ValidFrom", pp.valid_to AS "ValidTo"
            FROM nexora.product_price pp
            JOIN nexora.currency cur ON cur.id = pp.currency_id
            LEFT JOIN nexora.price_list pl ON pl.id = pp.price_list_id
            LEFT JOIN nexora.customer_group cg ON cg.id = pp.customer_group_id
            WHERE pp.product_id = {productId}
            ORDER BY pp.valid_to IS NOT NULL, pp.price_type, pp.quantity_min, pp.valid_from DESC
            """).ToListAsync(ct);

        var barcodes = await db.Database.SqlQuery<ProductBarcodeDto>($"""
            SELECT id AS "Id", barcode AS "Barcode", barcode_type AS "BarcodeType",
                   is_primary AS "IsPrimary", status AS "Status", pack_size AS "PackSize"
            FROM nexora.product_barcode WHERE product_id = {productId}
            ORDER BY is_primary DESC, barcode
            """).ToListAsync(ct);

        var suppliers = await db.Database.SqlQuery<ProductSupplierDto>($"""
            SELECT ps.id AS "Id", s.name AS "SupplierName", ps.supplier_sku AS "SupplierSku",
                   ps.purchase_price AS "PurchasePrice", cur.code AS "Currency",
                   ps.lead_time_days AS "LeadTimeDays", ps.is_preferred AS "IsPreferred"
            FROM nexora.product_supplier ps
            JOIN nexora.supplier s ON s.id = ps.supplier_id
            JOIN nexora.currency cur ON cur.id = ps.currency_id
            WHERE ps.product_id = {productId}
            ORDER BY ps.is_preferred DESC, s.name
            """).ToListAsync(ct);

        // Guide §7.2, §27 : prix d'achat et fournisseurs ne quittent pas le serveur sans catalog.view_cost.
        if (!(await permissions.CurrentPermissionsAsync(tenant, ct)).Contains(Nexora.Application.Security.Permissions.Catalog.ViewCost))
        {
            prices = prices.Where(x => x.PriceType != "purchase").ToList();
            suppliers = [];
        }

        var unitCode = await db.Database
            .SqlQuery<string>($"""SELECT code AS "Value" FROM nexora.unit WHERE id = {p.UnitId}""")
            .FirstOrDefaultAsync(ct);
        // Règle générale (tous emplacements) : c'est celle que la fiche produit édite.
        var reorder = await db.Database.SqlQuery<ReorderRuleDto>($"""
            SELECT min_quantity AS "MinQuantity", max_quantity AS "MaxQuantity", reorder_point AS "ReorderPoint",
                   reorder_quantity AS "ReorderQuantity", lead_time_days AS "LeadTimeDays", auto_order AS "AutoOrder"
            FROM nexora.product_reorder_rule
            WHERE product_id = {productId} AND inventory_location_id IS NULL AND status = 'active'
            """).FirstOrDefaultAsync(ct);
        var conversions = await db.Database.SqlQuery<UnitConversionDto>($"""
            SELECT uc.id AS "Id", uc.product_id AS "ProductId",
                   uc.from_unit_id AS "FromUnitId", fu.code AS "FromUnitCode",
                   uc.to_unit_id AS "ToUnitId", tu.code AS "ToUnitCode", uc.factor AS "Factor"
            FROM nexora.unit_conversion uc
            JOIN nexora.unit fu ON fu.id = uc.from_unit_id
            JOIN nexora.unit tu ON tu.id = uc.to_unit_id
            WHERE uc.product_id = {productId}
            ORDER BY fu.code, tu.code
            """).ToListAsync(ct);
        var hasImage = p.ImageUrl?.StartsWith(SqlCatalogReferenceService.ImagePrefix, StringComparison.Ordinal) == true;

        return new ProductDetailDto(
            p.Id, p.Sku, p.Name, p.ShortName, p.Description, p.ProductType, p.Status,
            p.PluCode, p.ManufacturerRef, p.InternalRef, hasImage ? null : p.ImageUrl, categoryName, brandName,
            p.TrackLots, p.IsWeighed, prices, barcodes, suppliers,
            p.CategoryId, p.BrandId, p.UnitId, unitCode, p.DefaultTaxId, reorder, hasImage, conversions);
    }

    public async Task<IReadOnlyList<CategoryDto>> ListCategoriesAsync(CancellationToken ct = default)
        => await db.Database.SqlQuery<CategoryDto>($"""
            SELECT id AS "Id", parent_id AS "ParentId", name AS "Name", code AS "Code", status AS "Status"
            FROM nexora.product_category WHERE company_id = {Company} ORDER BY name
            """).ToListAsync(ct);

    public async Task<IReadOnlyList<PriceListDto>> ListPriceListsAsync(CancellationToken ct = default)
        => await db.Database.SqlQuery<PriceListDto>($"""
            SELECT pl.id AS "Id", pl.code AS "Code", pl.name AS "Name", pl.list_type AS "ListType",
                   cur.code AS "Currency", cg.name AS "CustomerGroup", pl.priority AS "Priority", pl.status AS "Status"
            FROM nexora.price_list pl
            LEFT JOIN nexora.currency cur ON cur.id = pl.currency_id
            LEFT JOIN nexora.customer_group cg ON cg.id = pl.customer_group_id
            WHERE pl.company_id = {Company} ORDER BY pl.priority DESC, pl.code
            """).ToListAsync(ct);

    public async Task<IReadOnlyList<TaxDto>> ListTaxesAsync(CancellationToken ct = default)
        => await db.Database.SqlQuery<TaxDto>($"""
            SELECT id AS "Id", code AS "Code", name AS "Name", rate AS "Rate", type AS "Type",
                   is_inclusive AS "IsInclusive", status AS "Status"
            FROM nexora.tax WHERE company_id = {Company} ORDER BY rate, code
            """).ToListAsync(ct);

    public async Task<IReadOnlyList<PromotionDto>> ListPromotionsAsync(CancellationToken ct = default)
        => await db.Database.SqlQuery<PromotionDto>($"""
            SELECT id AS "Id", name AS "Name", promo_type AS "PromoType", value AS "Value",
                   priority AS "Priority", starts_at AS "StartsAt", ends_at AS "EndsAt", status AS "Status"
            FROM nexora.promotion WHERE company_id = {Company} ORDER BY status, priority DESC, name
            """).ToListAsync(ct);

    private static CatalogHitDto Map(HitRow r) => new(
        r.ProductId, r.Sku, r.Name, r.ShortName, r.Barcode, r.PluCode, r.ProductType, r.Status,
        r.CategoryName, r.BrandName, r.SalePrice, r.Currency, r.ExactBarcode);
}
