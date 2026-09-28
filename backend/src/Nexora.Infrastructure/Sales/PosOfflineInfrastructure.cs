using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Nexora.Application.Abstractions;
using Nexora.Application.Sales;
using Nexora.Application.Sync;
using Nexora.Contracts.Sales;
using Nexora.Domain.Common;
using Nexora.Infrastructure.Persistence;

namespace Nexora.Infrastructure.Sales;

/// <summary>Caisse hors ligne (phase 6) — voir <see cref="IPosOfflineService"/>.</summary>
public sealed partial class SqlPosOfflineService(
    NexoraDbContext db, ITenantContext tenant, IOfflinePolicyService policies, IUnitOfWork uow) : IPosOfflineService
{
    /// <summary>Catalogue hors ligne plafonné : au-delà, la caisse le signale (recherche en ligne nécessaire).</summary>
    public const int MaxCatalogItems = 5000;

    private Guid Company => SalesHelpers.Company(tenant);

    [GeneratedRegex("^[A-Za-z0-9-]{8,100}$")]
    private static partial Regex IdentifierPattern();

    private sealed record DeviceRow(Guid Id, string? Name, string Status);

    public async Task<Result<PosDeviceDto>> EnrollAsync(EnrollPosDeviceRequest request, CancellationToken ct = default)
    {
        var identifier = (request.DeviceIdentifier ?? "").Trim();
        if (!IdentifierPattern().IsMatch(identifier))
            return Error.Validation("pos.device_identifier", "Identifiant d'appareil invalide.");

        var existing = (await db.Database.SqlQuery<DeviceRow>($"""
            SELECT id AS "Id", name AS "Name", status AS "Status" FROM nexora.sync_device
            WHERE company_id = {Company} AND device_identifier = {identifier}
            """).ToListAsync(ct)).FirstOrDefault();
        if (existing is not null)
        {
            if (existing.Status == "revoked")
                return Error.Forbidden("sync.device_revoked", "Cet appareil a été révoqué : la caisse hors ligne y est désactivée.");
            if (existing.Status == "suspended")
                return Error.Forbidden("sync.device_suspended", "Cet appareil est suspendu : contactez un responsable.");
            await db.Database.ExecuteSqlAsync($"""
                UPDATE nexora.sync_device SET last_seen_at = now(), user_id = COALESCE({tenant.UserId}, user_id), updated_at = now()
                WHERE id = {existing.Id}
                """, ct);
            await uow.SaveChangesAsync(ct);
            return Result.Success(new PosDeviceDto(existing.Id, existing.Name ?? identifier, existing.Status));
        }

        var name = string.IsNullOrWhiteSpace(request.Name) ? "Caisse (navigateur)" : request.Name.Trim()[..Math.Min(request.Name.Trim().Length, 120)];
        var id = Guid.NewGuid();
        await db.Database.ExecuteSqlAsync($"""
            INSERT INTO nexora.sync_device (id, company_id, device_identifier, device_type, name, status, user_id, last_seen_at)
            VALUES ({id}, {Company}, {identifier}, 'pos', {name}, 'active', {tenant.UserId}, now())
            ON CONFLICT (company_id, device_identifier) DO NOTHING
            """, ct);
        await uow.SaveChangesAsync(ct);
        var row = await db.Database.SqlQuery<DeviceRow>($"""
            SELECT id AS "Id", name AS "Name", status AS "Status" FROM nexora.sync_device
            WHERE company_id = {Company} AND device_identifier = {identifier}
            """).FirstAsync(ct);
        return Result.Success(new PosDeviceDto(row.Id, row.Name ?? name, row.Status));
    }

    public async Task<PosOfflineProfileDto> ProfileAsync(Guid? inventoryLocationId, CancellationToken ct = default)
    {
        var p = await policies.GetAsync(ct);
        var policy = new PosOfflinePolicyDto(p.AllowOfflineSales, p.AllowOfflineCredit, p.OfflineCreditLimit, p.AllowOfflineDiscount,
            p.AllowOfflineRefund, p.AllowOfflineCustomerPayment, p.MaxOfflineMinutes, p.AllowedPaymentMethods);
        var take = MaxCatalogItems + 1;

        // Même règle de prix et de disponibilité que la recherche de la caisse (SqlPosCatalogQueries).
        var rows = await db.Database.SqlQuery<PosOfflineCatalogItemDto>($"""
            SELECT p.id AS "ProductId", p.name AS "Name", p.sku AS "Sku",
                   COALESCE(p.barcode, (SELECT b.barcode FROM nexora.product_barcode b
                                        WHERE b.product_id = p.id AND b.is_primary LIMIT 1)) AS "Barcode",
                   ARRAY(SELECT c FROM (
                         SELECT p.barcode AS c UNION SELECT p.plu_code
                         UNION SELECT b.barcode FROM nexora.product_barcode b WHERE b.product_id = p.id AND b.status = 'active') x
                         WHERE c IS NOT NULL AND c <> '') AS "Codes",
                   nexora.search_norm(concat_ws(' ', p.name, p.sku, p.short_name, p.search_aliases)) AS "SearchText",
                   dp.amount AS "SalePrice", dp.code AS "Currency",
                   CASE WHEN p.product_type NOT IN ('stockable', 'weight') THEN 'untracked'
                        WHEN COALESCE(sb.available, 0) <= 0 THEN 'out'
                        WHEN rr.reorder_point IS NOT NULL AND sb.available <= rr.reorder_point THEN 'low'
                        ELSE 'in_stock' END AS "Availability",
                   COALESCE((SELECT tx.rate FROM nexora.tax tx WHERE tx.id = p.default_tax_id), 0) AS "TaxRate",
                   COALESCE(dp.tax_included, false) AS "PriceIncludesTax"
            FROM nexora.product p
            JOIN nexora.company co ON co.id = p.company_id
            LEFT JOIN LATERAL (
                SELECT pp.amount, cur.code, pp.tax_included
                FROM nexora.product_price pp JOIN nexora.currency cur ON cur.id = pp.currency_id
                WHERE pp.product_id = p.id AND pp.price_type = 'sale' AND pp.price_list_id IS NULL
                  AND pp.customer_group_id IS NULL AND pp.quantity_min = 0
                  AND pp.valid_from <= now() AND (pp.valid_to IS NULL OR pp.valid_to > now())
                ORDER BY (pp.currency_id = co.default_currency_id) DESC, pp.valid_from DESC
                LIMIT 1) dp ON true
            LEFT JOIN LATERAL (
                SELECT SUM(sb.quantity_on_hand - sb.quantity_reserved) AS available
                FROM nexora.stock_balance sb
                WHERE sb.product_id = p.id AND ({inventoryLocationId}::uuid IS NULL OR sb.inventory_location_id = {inventoryLocationId})) sb ON true
            LEFT JOIN LATERAL (
                SELECT r.reorder_point FROM nexora.product_reorder_rule r
                WHERE r.product_id = p.id AND r.status = 'active'
                ORDER BY (r.inventory_location_id = {inventoryLocationId}) DESC NULLS LAST LIMIT 1) rr ON true
            WHERE p.company_id = {Company} AND p.status = 'active' AND p.is_sellable AND dp.amount IS NOT NULL
            ORDER BY p.name
            LIMIT {take}
            """).ToListAsync(ct);

        var truncated = rows.Count > MaxCatalogItems;
        return new PosOfflineProfileDto(DateTimeOffset.UtcNow, policy, truncated ? rows.Take(MaxCatalogItems).ToList() : rows, truncated, MaxCatalogItems);
    }
}
