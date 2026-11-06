using Microsoft.EntityFrameworkCore;
using Nexora.Application.Abstractions;
using Nexora.Application.Inventory;
using Nexora.Contracts.Inventory;
using Nexora.Infrastructure.Persistence;
using Nexora.Domain.Common;

namespace Nexora.Infrastructure.Inventory;

/// <summary>
/// Lectures des opérations de stock (finalisation produit, phase 2). Tout est calculé côté serveur :
/// état de stock (rupture / faible / normal / surstock), écarts, valeurs — l'écran n'affiche que ces résultats.
/// </summary>
public sealed class SqlInventoryDocumentQueries(NexoraDbContext db, ITenantContext tenant, IPermissionService permissions)
    : IInventoryDocumentQueries
{
    private Guid Company => tenant.ActiveCompanyId ?? throw TenantErrors.ActiveCompanyRequired();

    /// <summary>Coûts et valeurs de stock : réservés à <c>inventory.view_valuation</c> (masqués à 0 sinon).</summary>
    private async Task<bool> CanSeeValuationAsync(CancellationToken ct)
        => (await permissions.CurrentPermissionsAsync(tenant, ct)).Contains(Nexora.Application.Security.Permissions.Inventory.ViewValuation);

    private static readonly string[] Statuses = ["out", "low", "normal", "over", "expiring"];

    private sealed record OverviewRow(
        Guid ProductId, string Sku, string ProductName, string? CategoryName, string? UnitCode,
        Guid LocationId, string LocationName, decimal OnHand, decimal Reserved, decimal Available,
        decimal? MinQuantity, decimal? ReorderPoint, decimal? MaxQuantity, decimal AverageCost, decimal StockValue,
        DateTimeOffset? LastMovementAt, DateOnly? NearestExpiry, string StockStatus,
        int TotalCount, decimal TotalValue, int OutCount, int LowCount, int OverCount);

    public async Task<StockOverviewDto> OverviewAsync(StockOverviewQuery q, CancellationToken ct = default)
    {
        var limit = Math.Clamp(q.Limit, 1, 1000);
        var offset = Math.Max(0, q.Offset);
        var status = q.Status is { } s && Statuses.Contains(s) ? s : null;
        var like = string.IsNullOrWhiteSpace(q.Search) ? null : "%" + q.Search.Trim().Replace("%", "") + "%";

        // Couples article × emplacement : tout solde existant, plus les articles suivis (règle de
        // réapprovisionnement active) jamais entrés à un emplacement actif — ce sont des ruptures réelles.
        var rows = await db.Database.SqlQuery<OverviewRow>($"""
            WITH pairs AS (
                SELECT sb.product_id, sb.inventory_location_id AS location_id
                FROM nexora.stock_balance sb WHERE sb.company_id = {Company}
                UNION
                SELECT r.product_id, il.id
                FROM nexora.product_reorder_rule r
                JOIN nexora.product p ON p.id = r.product_id AND p.status = 'active'
                JOIN nexora.inventory_location il ON il.company_id = r.company_id AND il.status = 'active'
                     AND il.location_type IN ('store','warehouse')
                     AND (r.inventory_location_id IS NULL OR r.inventory_location_id = il.id)
                WHERE r.company_id = {Company} AND r.status = 'active'
            ),
            enriched AS (
                SELECT p.id AS product_id, p.sku, p.name AS product_name, c.name AS category_name, u.code AS unit_code,
                       il.id AS location_id, il.name AS location_name,
                       COALESCE(sb.quantity_on_hand, 0) AS on_hand, COALESCE(sb.quantity_reserved, 0) AS reserved,
                       COALESCE(sb.quantity_available, 0) AS available,
                       rr.min_quantity, rr.reorder_point, NULLIF(rr.max_quantity, 0) AS max_quantity,
                       COALESCE(sb.average_cost, 0) AS average_cost,
                       ROUND(COALESCE(sb.quantity_on_hand, 0) * COALESCE(sb.average_cost, 0), 2) AS stock_value,
                       (SELECT max(m.occurred_at) FROM nexora.stock_movement m
                         WHERE m.product_id = p.id AND m.inventory_location_id = il.id) AS last_movement_at,
                       (SELECT min(l.expiration_date) FROM nexora.lot_stock ls JOIN nexora.lot l ON l.id = ls.lot_id
                         WHERE l.product_id = p.id AND ls.inventory_location_id = il.id
                           AND ls.quantity_on_hand > 0 AND l.status = 'active') AS nearest_expiry,
                       p.category_id
                FROM pairs pr
                JOIN nexora.product p ON p.id = pr.product_id
                JOIN nexora.inventory_location il ON il.id = pr.location_id
                LEFT JOIN nexora.stock_balance sb ON sb.product_id = pr.product_id AND sb.inventory_location_id = pr.location_id
                LEFT JOIN nexora.product_category c ON c.id = p.category_id
                LEFT JOIN nexora.unit u ON u.id = p.unit_id
                LEFT JOIN LATERAL (
                    SELECT r.min_quantity, r.reorder_point, r.max_quantity
                    FROM nexora.product_reorder_rule r
                    WHERE r.product_id = p.id AND r.status = 'active'
                      AND (r.inventory_location_id = il.id OR r.inventory_location_id IS NULL)
                    ORDER BY (r.inventory_location_id IS NULL)
                    LIMIT 1) rr ON true
                WHERE p.product_type NOT IN ('service','non_stock')
                  AND ({q.LocationId}::uuid IS NULL OR il.id = {q.LocationId})
                  AND ({q.CategoryId}::uuid IS NULL OR p.category_id = {q.CategoryId})
                  AND ({like}::text IS NULL OR p.sku ILIKE {like} OR p.name ILIKE {like})
            ),
            classified AS (
                SELECT e.*,
                       CASE WHEN e.available <= 0 THEN 'out'
                            WHEN (e.reorder_point IS NOT NULL AND e.reorder_point > 0 AND e.available <= e.reorder_point)
                              OR (e.min_quantity IS NOT NULL AND e.min_quantity > 0 AND e.on_hand < e.min_quantity) THEN 'low'
                            WHEN e.max_quantity IS NOT NULL AND e.on_hand > e.max_quantity THEN 'over'
                            ELSE 'normal' END AS stock_status
                FROM enriched e
            ),
            kpis AS (
                SELECT c.*,
                       SUM(c.stock_value) OVER () AS total_value,
                       (COUNT(*) FILTER (WHERE c.stock_status = 'out') OVER ())::int AS out_count,
                       (COUNT(*) FILTER (WHERE c.stock_status = 'low') OVER ())::int AS low_count,
                       (COUNT(*) FILTER (WHERE c.stock_status = 'over') OVER ())::int AS over_count
                FROM classified c
            ),
            filtered AS (
                SELECT k.*, (COUNT(*) OVER ())::int AS total_count
                FROM kpis k
                WHERE {status}::text IS NULL
                   OR ({status} = 'expiring' AND k.nearest_expiry IS NOT NULL AND k.nearest_expiry <= current_date + 60)
                   OR k.stock_status = {status}
            )
            SELECT product_id AS "ProductId", sku AS "Sku", product_name AS "ProductName", category_name AS "CategoryName",
                   unit_code AS "UnitCode", location_id AS "LocationId", location_name AS "LocationName",
                   on_hand AS "OnHand", reserved AS "Reserved", available AS "Available",
                   min_quantity AS "MinQuantity", reorder_point AS "ReorderPoint", max_quantity AS "MaxQuantity",
                   average_cost AS "AverageCost", stock_value AS "StockValue",
                   last_movement_at AS "LastMovementAt", nearest_expiry AS "NearestExpiry", stock_status AS "StockStatus",
                   total_count AS "TotalCount", COALESCE(total_value, 0) AS "TotalValue",
                   out_count AS "OutCount", low_count AS "LowCount", over_count AS "OverCount"
            FROM filtered
            ORDER BY CASE stock_status WHEN 'out' THEN 0 WHEN 'low' THEN 1 WHEN 'over' THEN 2 ELSE 3 END,
                     product_name, location_name
            LIMIT {limit} OFFSET {offset}
            """).ToListAsync(ct);

        var first = rows.FirstOrDefault();
        var valued = await CanSeeValuationAsync(ct);
        return new StockOverviewDto(
            rows.Select(r => new StockOverviewRowDto(
                r.ProductId, r.Sku, r.ProductName, r.CategoryName, r.UnitCode, r.LocationId, r.LocationName,
                r.OnHand, r.Reserved, r.Available, r.MinQuantity, r.ReorderPoint, r.MaxQuantity,
                valued ? r.AverageCost : 0m, valued ? r.StockValue : 0m, r.LastMovementAt, r.NearestExpiry, r.StockStatus)).ToList(),
            first?.TotalCount ?? 0, valued ? first?.TotalValue ?? 0m : 0m,
            first?.OutCount ?? 0, first?.LowCount ?? 0, first?.OverCount ?? 0, valued);
    }

    public async Task<IReadOnlyList<StockSiteDto>> SitesAsync(CancellationToken ct = default)
        => await db.Database.SqlQuery<StockSiteDto>($"""
            SELECT id AS "Id", 'establishment' AS "Kind", name AS "Name", code AS "Code",
                   NULL::uuid AS "EstablishmentId", status AS "Status"
            FROM nexora.establishment WHERE company_id = {Company}
            UNION ALL
            SELECT id, 'store', name, code, establishment_id, status
            FROM nexora.store WHERE company_id = {Company}
            ORDER BY 2, 3
            """).ToListAsync(ct);

    // ------------------------------------------------------------------ Transferts

    public async Task<IReadOnlyList<TransferSummaryDto>> TransfersAsync(string? status, int limit, CancellationToken ct = default)
        => await TransferSummaries(null, status, Math.Clamp(limit, 1, 500), ct);

    private async Task<List<TransferSummaryDto>> TransferSummaries(Guid? id, string? status, int limit, CancellationToken ct)
    {
        var rows = await TransferRows(id, status, limit, ct);
        return await CanSeeValuationAsync(ct) ? rows : rows.Select(r => r with { Value = 0m }).ToList();
    }

    private async Task<List<TransferSummaryDto>> TransferRows(Guid? id, string? status, int limit, CancellationToken ct)
        => await db.Database.SqlQuery<TransferSummaryDto>($"""
            SELECT t.id AS "Id", t.document_number AS "DocumentNumber", t.status AS "Status",
                   t.source_location_id AS "SourceLocationId", src.name AS "SourceLocationName",
                   t.dest_location_id AS "DestLocationId", dst.name AS "DestLocationName",
                   (SELECT COUNT(*)::int FROM nexora.stock_transfer_line l WHERE l.stock_transfer_id = t.id) AS "LineCount",
                   COALESCE((SELECT SUM(l.quantity_shipped) FROM nexora.stock_transfer_line l WHERE l.stock_transfer_id = t.id), 0) AS "QuantityShipped",
                   COALESCE((SELECT SUM(l.quantity_received) FROM nexora.stock_transfer_line l WHERE l.stock_transfer_id = t.id), 0) AS "QuantityReceived",
                   COALESCE((SELECT ROUND(SUM(l.quantity_shipped * l.unit_cost), 2) FROM nexora.stock_transfer_line l WHERE l.stock_transfer_id = t.id), 0) AS "Value",
                   t.shipped_at AS "ShippedAt", t.received_at AS "ReceivedAt", t.notes AS "Notes",
                   COALESCE(u.full_name, u.email::text) AS "CreatedByName"
            FROM nexora.stock_transfer t
            JOIN nexora.inventory_location src ON src.id = t.source_location_id
            JOIN nexora.inventory_location dst ON dst.id = t.dest_location_id
            LEFT JOIN nexora.app_user u ON u.id = t.created_by
            WHERE t.company_id = {Company}
              AND ({id}::uuid IS NULL OR t.id = {id})
              AND ({status}::text IS NULL OR t.status = {status})
            ORDER BY t.created_at DESC
            LIMIT {limit}
            """).ToListAsync(ct);

    public async Task<TransferDetailDto?> TransferAsync(Guid id, CancellationToken ct = default)
    {
        var header = (await TransferSummaries(id, null, 1, ct)).FirstOrDefault();
        if (header is null) return null;
        var lines = await db.Database.SqlQuery<TransferLineDto>($"""
            SELECT l.id AS "Id", l.product_id AS "ProductId", p.sku AS "Sku", p.name AS "ProductName",
                   lt.lot_number AS "LotNumber", l.quantity_shipped AS "QuantityShipped",
                   l.quantity_received AS "QuantityReceived", (l.quantity_shipped - l.quantity_received) AS "Remaining",
                   l.unit_cost AS "UnitCost"
            FROM nexora.stock_transfer_line l
            JOIN nexora.product p ON p.id = l.product_id
            LEFT JOIN nexora.lot lt ON lt.id = l.lot_id
            WHERE l.stock_transfer_id = {id}
            ORDER BY p.name
            """).ToListAsync(ct);
        if (!await CanSeeValuationAsync(ct)) lines = lines.Select(l => l with { UnitCost = 0m }).ToList();
        return new TransferDetailDto(header, lines);
    }

    // ------------------------------------------------------------------ Inventaires

    public async Task<IReadOnlyList<CountSummaryDto>> CountsAsync(string? status, int limit, CancellationToken ct = default)
        => await CountSummaries(null, status, Math.Clamp(limit, 1, 500), ct);

    private async Task<List<CountSummaryDto>> CountSummaries(Guid? id, string? status, int limit, CancellationToken ct)
    {
        var rows = await CountRows(id, status, limit, ct);
        return await CanSeeValuationAsync(ct) ? rows : rows.Select(r => r with { VarianceValue = 0m }).ToList();
    }

    private async Task<List<CountSummaryDto>> CountRows(Guid? id, string? status, int limit, CancellationToken ct)
        => await db.Database.SqlQuery<CountSummaryDto>($"""
            SELECT c.id AS "Id", c.document_number AS "DocumentNumber", c.count_type AS "CountType", c.status AS "Status",
                   c.inventory_location_id AS "LocationId", il.name AS "LocationName",
                   (SELECT COUNT(*)::int FROM nexora.inventory_count_line l WHERE l.inventory_count_id = c.id) AS "LineCount",
                   (SELECT COUNT(*)::int FROM nexora.inventory_count_line l
                     WHERE l.inventory_count_id = c.id AND l.counted_quantity IS NOT NULL) AS "CountedLines",
                   (SELECT COUNT(*)::int FROM nexora.inventory_count_line l
                     WHERE l.inventory_count_id = c.id AND l.counted_quantity IS NOT NULL AND l.variance <> 0) AS "VarianceLines",
                   COALESCE((SELECT ROUND(SUM(l.variance * l.unit_cost), 2) FROM nexora.inventory_count_line l
                     WHERE l.inventory_count_id = c.id AND l.counted_quantity IS NOT NULL), 0) AS "VarianceValue",
                   c.started_at AS "StartedAt", c.validated_at AS "ValidatedAt",
                   COALESCE(u.full_name, u.email::text) AS "ValidatedByName", c.notes AS "Notes"
            FROM nexora.inventory_count c
            JOIN nexora.inventory_location il ON il.id = c.inventory_location_id
            LEFT JOIN nexora.app_user u ON u.id = c.validated_by
            WHERE c.company_id = {Company}
              AND ({id}::uuid IS NULL OR c.id = {id})
              AND ({status}::text IS NULL OR c.status = {status})
            ORDER BY c.created_at DESC
            LIMIT {limit}
            """).ToListAsync(ct);

    public async Task<CountDetailDto?> CountAsync(Guid id, CancellationToken ct = default)
    {
        var header = (await CountSummaries(id, null, 1, ct)).FirstOrDefault();
        if (header is null) return null;
        var lines = await db.Database.SqlQuery<CountLineDto>($"""
            SELECT l.id AS "Id", l.product_id AS "ProductId", p.sku AS "Sku", p.name AS "ProductName", u.code AS "UnitCode",
                   lt.lot_number AS "LotNumber", l.system_quantity AS "SystemQuantity", l.counted_quantity AS "CountedQuantity",
                   CASE WHEN l.counted_quantity IS NULL THEN NULL ELSE l.variance END AS "Variance",
                   l.unit_cost AS "UnitCost",
                   CASE WHEN l.counted_quantity IS NULL THEN NULL ELSE ROUND(l.variance * l.unit_cost, 2) END AS "VarianceValue",
                   l.note AS "Note", l.lot_id AS "LotId"
            FROM nexora.inventory_count_line l
            JOIN nexora.product p ON p.id = l.product_id
            LEFT JOIN nexora.unit u ON u.id = p.unit_id
            LEFT JOIN nexora.lot lt ON lt.id = l.lot_id
            WHERE l.inventory_count_id = {id}
            ORDER BY p.name, lt.lot_number
            """).ToListAsync(ct);
        if (!await CanSeeValuationAsync(ct)) lines = lines.Select(l => l with { UnitCost = 0m, VarianceValue = null }).ToList();
        return new CountDetailDto(header, lines);
    }

    // ------------------------------------------------------------------ Ajustements

    public async Task<IReadOnlyList<AdjustmentSummaryDto>> AdjustmentsAsync(
        string? type, DateTimeOffset? from, DateTimeOffset? to, int limit, CancellationToken ct = default)
        => await AdjustmentSummaries(null, type, from, to, Math.Clamp(limit, 1, 500), ct);

    private async Task<List<AdjustmentSummaryDto>> AdjustmentSummaries(
        Guid? id, string? type, DateTimeOffset? from, DateTimeOffset? to, int limit, CancellationToken ct)
    {
        var rows = await AdjustmentRows(id, type, from, to, limit, ct);
        return await CanSeeValuationAsync(ct) ? rows : rows.Select(r => r with { Value = 0m }).ToList();
    }

    private async Task<List<AdjustmentSummaryDto>> AdjustmentRows(
        Guid? id, string? type, DateTimeOffset? from, DateTimeOffset? to, int limit, CancellationToken ct)
        => await db.Database.SqlQuery<AdjustmentSummaryDto>($"""
            SELECT a.id AS "Id", a.document_number AS "DocumentNumber", a.adjustment_type AS "AdjustmentType",
                   a.status AS "Status", a.inventory_location_id AS "LocationId", il.name AS "LocationName",
                   a.reason AS "Reason",
                   (SELECT COUNT(*)::int FROM nexora.stock_adjustment_line l WHERE l.stock_adjustment_id = a.id) AS "LineCount",
                   COALESCE((SELECT SUM(l.quantity_delta) FROM nexora.stock_adjustment_line l WHERE l.stock_adjustment_id = a.id), 0) AS "QuantityDelta",
                   COALESCE((SELECT ROUND(SUM(l.quantity_delta * l.unit_cost), 2) FROM nexora.stock_adjustment_line l WHERE l.stock_adjustment_id = a.id), 0) AS "Value",
                   a.occurred_at AS "OccurredAt", a.source_type AS "SourceType", a.source_id AS "SourceId",
                   a.journal_entry_id AS "JournalEntryId", COALESCE(u.full_name, u.email::text) AS "CreatedByName"
            FROM nexora.stock_adjustment a
            JOIN nexora.inventory_location il ON il.id = a.inventory_location_id
            LEFT JOIN nexora.app_user u ON u.id = a.created_by
            WHERE a.company_id = {Company}
              AND ({id}::uuid IS NULL OR a.id = {id})
              AND ({type}::text IS NULL OR a.adjustment_type = {type})
              AND ({from}::timestamptz IS NULL OR a.occurred_at >= {from})
              AND ({to}::timestamptz IS NULL OR a.occurred_at < {to})
            ORDER BY a.occurred_at DESC, a.document_number DESC
            LIMIT {limit}
            """).ToListAsync(ct);

    public async Task<AdjustmentDetailDto?> AdjustmentAsync(Guid id, CancellationToken ct = default)
    {
        var header = (await AdjustmentSummaries(id, null, null, null, 1, ct)).FirstOrDefault();
        if (header is null) return null;
        var lines = await db.Database.SqlQuery<AdjustmentLineDto>($"""
            SELECT l.id AS "Id", l.product_id AS "ProductId", p.sku AS "Sku", p.name AS "ProductName",
                   lt.lot_number AS "LotNumber", l.quantity_delta AS "QuantityDelta", l.unit_cost AS "UnitCost",
                   ROUND(l.quantity_delta * l.unit_cost, 2) AS "Value", l.note AS "Note"
            FROM nexora.stock_adjustment_line l
            JOIN nexora.product p ON p.id = l.product_id
            LEFT JOIN nexora.lot lt ON lt.id = l.lot_id
            WHERE l.stock_adjustment_id = {id}
            ORDER BY p.name
            """).ToListAsync(ct);
        if (!await CanSeeValuationAsync(ct)) lines = lines.Select(l => l with { UnitCost = 0m, Value = 0m }).ToList();
        return new AdjustmentDetailDto(header, lines);
    }
}
