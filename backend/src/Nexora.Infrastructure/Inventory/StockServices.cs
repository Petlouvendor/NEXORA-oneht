using Microsoft.EntityFrameworkCore;
using Nexora.Application.Abstractions;
using Nexora.Application.Administration;
using Nexora.Application.Inventory;
using Nexora.Contracts.Inventory;
using Nexora.Domain.Common;
using Nexora.Domain.Inventory;
using Nexora.Infrastructure.Persistence;

namespace Nexora.Infrastructure.Inventory;

/// <summary>Politique de stock négatif (Phase 24 §32) : pilotée par <c>stock.negative_stock_allowed</c>.</summary>
public sealed class SqlStockAvailabilityPolicy(ISettingsService settings) : IStockAvailabilityPolicy
{
    public async Task<bool> AllowNegativeAsync(CancellationToken ct = default)
    {
        try
        {
            var s = await settings.ResolveAsync("stock.negative_stock_allowed", null, ct);
            return s.Value.ValueKind == System.Text.Json.JsonValueKind.True;
        }
        catch { return false; }
    }
}

/// <summary>Cycle de vie des lots — blocage, rappel, balayage des expirations (Phase 24 §13-15).</summary>
public sealed class SqlLotService(
    NexoraDbContext db, ITenantContext tenant, InventoryOperationsService ops,
    IDateTimeProvider clock, ISettingsService settings) : ILotService
{
    private Guid Company => tenant.ActiveCompanyId
        ?? throw TenantErrors.ActiveCompanyRequired();

    public async Task<Result> BlockAsync(Guid lotId, string reason, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(reason))
            return Error.Validation("inventory.lot_reason_required", "Un motif de blocage est obligatoire (§43).");
        var lot = await db.Lots.FirstOrDefaultAsync(l => l.Id == lotId && l.CompanyId == Company, ct);
        if (lot is null) return Error.NotFound("inventory.lot_not_found", "Lot introuvable.");
        if (lot.Status is "blocked" or "recalled") return Error.Conflict("inventory.lot_already_blocked", "Lot déjà bloqué.");
        lot.Status = "blocked";
        lot.BlockedAt = clock.UtcNow;
        lot.BlockedReason = reason.Trim();
        lot.BlockedBy = tenant.UserId;
        await db.SaveChangesAsync(ct);
        return Result.Success();
    }

    public async Task<Result> UnblockAsync(Guid lotId, string reason, CancellationToken ct = default)
    {
        var lot = await db.Lots.FirstOrDefaultAsync(l => l.Id == lotId && l.CompanyId == Company, ct);
        if (lot is null) return Error.NotFound("inventory.lot_not_found", "Lot introuvable.");
        if (lot.Status != "blocked") return Error.Conflict("inventory.lot_not_blocked", "Le lot n'est pas bloqué.");
        lot.Status = "active";
        lot.BlockedAt = null;
        lot.BlockedReason = string.IsNullOrWhiteSpace(reason) ? null : $"débloqué : {reason.Trim()}";
        lot.BlockedBy = tenant.UserId;
        await db.SaveChangesAsync(ct);
        return Result.Success();
    }

    public async Task<Result> RecallAsync(Guid lotId, string reference, string reason, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(reference))
            return Error.Validation("inventory.recall_reference_required", "La référence de rappel est obligatoire (§15).");
        var lot = await db.Lots.FirstOrDefaultAsync(l => l.Id == lotId && l.CompanyId == Company, ct);
        if (lot is null) return Error.NotFound("inventory.lot_not_found", "Lot introuvable.");
        if (lot.Status == "recalled") return Error.Conflict("inventory.lot_recalled", "Lot déjà rappelé.");
        lot.Status = "recalled";
        lot.RecallReference = reference.Trim();
        lot.BlockedAt = clock.UtcNow;
        lot.BlockedReason = string.IsNullOrWhiteSpace(reason) ? "rappel produit" : reason.Trim();
        lot.BlockedBy = tenant.UserId;
        await db.SaveChangesAsync(ct);
        return Result.Success();
    }

    private sealed record ExpRow(
        Guid LotId, string LotNumber, Guid ProductId, string Sku, string ProductName,
        DateOnly ExpirationDate, decimal OnHand, decimal AverageCost);

    public async Task<IReadOnlyList<ExpiringLotDto>> ExpiringAsync(int? withinDays, CancellationToken ct = default)
    {
        var alertDays = withinDays ?? await AlertDaysAsync(ct);
        var today = DateOnly.FromDateTime(clock.UtcNow.UtcDateTime);
        var horizon = today.AddDays(alertDays);

        var rows = await db.Database.SqlQuery<ExpRow>($"""
            SELECT l.id AS "LotId", l.lot_number AS "LotNumber", l.product_id AS "ProductId",
                   p.sku AS "Sku", p.name AS "ProductName", l.expiration_date AS "ExpirationDate",
                   COALESCE(SUM(ls.quantity_on_hand), 0) AS "OnHand",
                   COALESCE(MAX(sb.average_cost), 0) AS "AverageCost"
            FROM nexora.lot l
            JOIN nexora.product p ON p.id = l.product_id
            LEFT JOIN nexora.lot_stock ls ON ls.lot_id = l.id
            LEFT JOIN nexora.stock_balance sb ON sb.product_id = l.product_id
                 AND sb.inventory_location_id = ls.inventory_location_id
            WHERE l.company_id = {Company} AND l.expiration_date IS NOT NULL
              AND l.expiration_date <= {horizon} AND l.status IN ('active','blocked','quarantine')
            GROUP BY l.id, l.lot_number, l.product_id, p.sku, p.name, l.expiration_date
            HAVING COALESCE(SUM(ls.quantity_on_hand), 0) > 0
            ORDER BY l.expiration_date
            """).ToListAsync(ct);

        return rows.Select(r =>
        {
            var days = r.ExpirationDate.DayNumber - today.DayNumber;
            var status = days < 0 ? "expired" : "near";
            return new ExpiringLotDto(r.LotId, r.LotNumber, r.ProductId, r.Sku, r.ProductName,
                r.ExpirationDate, days, status, r.OnHand, Math.Round(r.OnHand * r.AverageCost, 2));
        }).ToList();
    }

    public async Task<Result<LotSweepResultDto>> SweepExpiredAsync(CancellationToken ct = default)
    {
        var today = DateOnly.FromDateTime(clock.UtcNow.UtcDateTime);

        var rows = await db.Database.SqlQuery<ExpRow>($"""
            SELECT l.id AS "LotId", l.lot_number AS "LotNumber", l.product_id AS "ProductId",
                   p.sku AS "Sku", p.name AS "ProductName", l.expiration_date AS "ExpirationDate",
                   ls.quantity_on_hand AS "OnHand",
                   COALESCE(sb.average_cost, 0) AS "AverageCost"
            FROM nexora.lot l
            JOIN nexora.product p ON p.id = l.product_id
            JOIN nexora.lot_stock ls ON ls.lot_id = l.id AND ls.quantity_on_hand > 0
            LEFT JOIN nexora.stock_balance sb ON sb.product_id = l.product_id
                 AND sb.inventory_location_id = ls.inventory_location_id
            WHERE l.company_id = {Company} AND l.status = 'active'
              AND l.expiration_date IS NOT NULL AND l.expiration_date < {today}
            """).ToListAsync(ct);

        if (rows.Count == 0)
            return Result.Success(new LotSweepResultDto(0, 0m, 0));

        // Regroupe la sortie de stock par emplacement — une perte comptabilisée par lot_stock.
        var byLocation = await db.Database.SqlQuery<ExpLocRow>($"""
            SELECT ls.inventory_location_id AS "LocationId", l.product_id AS "ProductId",
                   l.id AS "LotId", ls.quantity_on_hand AS "OnHand"
            FROM nexora.lot l
            JOIN nexora.lot_stock ls ON ls.lot_id = l.id AND ls.quantity_on_hand > 0
            WHERE l.company_id = {Company} AND l.status = 'active'
              AND l.expiration_date IS NOT NULL AND l.expiration_date < {today}
            """).ToListAsync(ct);

        var movements = 0;
        foreach (var grp in byLocation.GroupBy(x => x.LocationId))
        {
            var loss = await ops.RecordLossAsync(new RecordLossCommand(
                Guid.NewGuid(), grp.Key, AdjustmentType.Expiry, $"Balayage des lots expirés au {today:yyyy-MM-dd}",
                grp.Select(x => new LossLineInput(x.ProductId, x.OnHand, x.LotId)).ToList(), clock.UtcNow), ct);
            if (!loss.IsSuccess) return loss.Error!;
            movements += grp.Count();
        }

        var value = rows.Sum(r => r.OnHand * r.AverageCost);
        var lotIds = rows.Select(r => r.LotId).Distinct().ToArray();
        await db.Lots.Where(l => lotIds.Contains(l.Id))
            .ExecuteUpdateAsync(u => u.SetProperty(l => l.Status, "expired"), ct);

        return Result.Success(new LotSweepResultDto(lotIds.Length, Math.Round(value, 2), movements));
    }

    private sealed record ExpLocRow(Guid LocationId, Guid ProductId, Guid LotId, decimal OnHand);

    private async Task<int> AlertDaysAsync(CancellationToken ct)
    {
        try
        {
            var s = await settings.ResolveAsync("stock.expiry_alert_days", null, ct);
            return s.Value.TryGetInt32(out var d) ? d : 30;
        }
        catch { return 30; }
    }
}

/// <summary>Création d'entrepôts & d'emplacements (Phase 24 §4-6).</summary>
public sealed class SqlWarehouseService(NexoraDbContext db, ITenantContext tenant) : IWarehouseService
{
    private Guid Company => tenant.ActiveCompanyId
        ?? throw TenantErrors.ActiveCompanyRequired();

    public async Task<Result<Guid>> CreateWarehouseAsync(CreateWarehouseRequest r, CancellationToken ct = default)
    {
        var types = new[] { "general", "cold", "bonded", "transit", "quarantine" };
        if (string.IsNullOrWhiteSpace(r.Name) || string.IsNullOrWhiteSpace(r.Code))
            return Error.Validation("inventory.warehouse_fields", "Nom et code sont obligatoires.");
        if (!types.Contains(r.Type))
            return Error.Validation("inventory.warehouse_type", $"Type d'entrepôt « {r.Type} » inconnu.");
        var estOk = await db.Database.SqlQuery<bool>($"""
            SELECT EXISTS(SELECT 1 FROM nexora.establishment WHERE id = {r.EstablishmentId} AND company_id = {Company}) AS "Value"
            """).FirstAsync(ct);
        if (!estOk) return Error.Validation("inventory.establishment_unknown", "Établissement inconnu pour cette entreprise.");
        var dup = await db.Database.SqlQuery<bool>($"""
            SELECT EXISTS(SELECT 1 FROM nexora.warehouse WHERE company_id = {Company} AND code = {r.Code}) AS "Value"
            """).FirstAsync(ct);
        if (dup) return Error.Conflict("inventory.warehouse_exists", $"L'entrepôt « {r.Code} » existe déjà.");

        var id = Guid.NewGuid();
        await db.Database.ExecuteSqlAsync($"""
            INSERT INTO nexora.warehouse (id, establishment_id, company_id, name, code, type, address, capacity)
            VALUES ({id}, {r.EstablishmentId}, {Company}, {r.Name.Trim()}, {r.Code.Trim()}, {r.Type},
                    {r.Address}, {r.Capacity})
            """, ct);
        return Result.Success(id);
    }

    public async Task<Result<Guid>> CreateLocationAsync(CreateLocationRequest r, CancellationToken ct = default)
    {
        if (!InventoryLocation.Types.Contains(r.LocationType))
            return Error.Validation("inventory.location_type", $"Type d'emplacement « {r.LocationType} » inconnu.");
        var physical = !InventoryLocation.NonPhysicalTypes.Contains(r.LocationType);
        if (physical && r.WarehouseId is null && r.StoreId is null)
            return Error.Validation("inventory.location_target", "Un emplacement physique doit être rattaché à un entrepôt ou un magasin (§6).");

        Guid? establishmentId = null;
        if (r.WarehouseId is { } wid)
        {
            establishmentId = await db.Database.SqlQuery<Guid?>($"""
                SELECT establishment_id AS "Value" FROM nexora.warehouse WHERE id = {wid} AND company_id = {Company}
                """).FirstOrDefaultAsync(ct);
            if (establishmentId is null) return Error.Validation("inventory.warehouse_unknown", "Entrepôt inconnu.");
        }

        var id = Guid.NewGuid();
        await db.Database.ExecuteSqlAsync($"""
            INSERT INTO nexora.inventory_location
              (id, company_id, establishment_id, warehouse_id, store_id, name, location_type, code,
               zone, aisle, shelf, bin, capacity)
            VALUES ({id}, {Company}, {establishmentId}, {r.WarehouseId}, {r.StoreId}, {r.Name.Trim()},
                    {r.LocationType}, {r.Code}, {r.Zone}, {r.Aisle}, {r.Shelf}, {r.Bin}, {r.Capacity})
            """, ct);
        return Result.Success(id);
    }
}

/// <summary>Lectures stock (Phase 24 §38-41).</summary>
public sealed class SqlStockQueries(
    NexoraDbContext db, ITenantContext tenant, IDateTimeProvider clock, ISettingsService settings) : IStockQueries
{
    private Guid Company => tenant.ActiveCompanyId
        ?? throw TenantErrors.ActiveCompanyRequired();

    public async Task<IReadOnlyList<StockBalanceDto>> BalancesAsync(Guid? locationId, Guid? productId, bool onlyPositive, CancellationToken ct = default)
        => await db.Database.SqlQuery<StockBalanceDto>($"""
            SELECT sb.product_id AS "ProductId", p.sku AS "Sku", p.name AS "ProductName",
                   sb.inventory_location_id AS "LocationId", il.name AS "LocationName",
                   sb.quantity_on_hand AS "OnHand", sb.quantity_reserved AS "Reserved",
                   sb.quantity_available AS "Available", sb.average_cost AS "AverageCost",
                   ROUND(sb.quantity_on_hand * sb.average_cost, 2) AS "StockValue"
            FROM nexora.stock_balance sb
            JOIN nexora.product p ON p.id = sb.product_id
            JOIN nexora.inventory_location il ON il.id = sb.inventory_location_id
            WHERE sb.company_id = {Company}
              AND ({locationId}::uuid IS NULL OR sb.inventory_location_id = {locationId})
              AND ({productId}::uuid IS NULL OR sb.product_id = {productId})
              AND (NOT {onlyPositive} OR sb.quantity_on_hand <> 0)
            ORDER BY p.name, il.name
            """).ToListAsync(ct);

    public async Task<IReadOnlyList<StockMovementDto>> MovementsAsync(
        Guid? productId, Guid? locationId, DateTimeOffset? from, DateTimeOffset? to, int limit, int offset, CancellationToken ct = default)
    {
        limit = Math.Clamp(limit, 1, 500);
        offset = Math.Max(0, offset);
        return await db.Database.SqlQuery<StockMovementDto>($"""
            SELECT m.id AS "Id", m.occurred_at AS "OccurredAt", m.movement_type AS "MovementType",
                   m.product_id AS "ProductId", p.sku AS "Sku", m.inventory_location_id AS "LocationId",
                   il.name AS "LocationName", l.lot_number AS "LotNumber", m.quantity AS "Quantity",
                   m.unit_cost AS "UnitCost", m.reference_type AS "ReferenceType", m.reference_id AS "ReferenceId"
            FROM nexora.stock_movement m
            JOIN nexora.product p ON p.id = m.product_id
            JOIN nexora.inventory_location il ON il.id = m.inventory_location_id
            LEFT JOIN nexora.lot l ON l.id = m.lot_id
            WHERE m.company_id = {Company}
              AND ({productId}::uuid IS NULL OR m.product_id = {productId})
              AND ({locationId}::uuid IS NULL OR m.inventory_location_id = {locationId})
              AND ({from}::timestamptz IS NULL OR m.occurred_at >= {from})
              AND ({to}::timestamptz IS NULL OR m.occurred_at <= {to})
            ORDER BY m.occurred_at DESC, m.created_at DESC
            LIMIT {limit} OFFSET {offset}
            """).ToListAsync(ct);
    }

    public async Task<IReadOnlyList<LotDto>> LotsAsync(Guid? productId, Guid? locationId, string? status, int? expiringWithinDays, CancellationToken ct = default)
    {
        var today = DateOnly.FromDateTime(clock.UtcNow.UtcDateTime);
        var alertDays = expiringWithinDays ?? 3650;
        var horizon = today.AddDays(alertDays);
        var rows = await db.Database.SqlQuery<LotRow>($"""
            SELECT l.id AS "Id", l.lot_number AS "LotNumber", l.product_id AS "ProductId",
                   p.sku AS "Sku", p.name AS "ProductName", l.expiration_date AS "ExpirationDate",
                   l.manufacture_date AS "ManufactureDate", l.received_at AS "ReceivedAt",
                   l.status AS "Status", l.supplier_id AS "SupplierId", s.name AS "SupplierName",
                   l.blocked_reason AS "BlockedReason", l.recall_reference AS "RecallReference",
                   COALESCE(SUM(ls.quantity_on_hand), 0) AS "OnHand",
                   COALESCE(SUM(ls.quantity_reserved), 0) AS "Reserved"
            FROM nexora.lot l
            JOIN nexora.product p ON p.id = l.product_id
            LEFT JOIN nexora.supplier s ON s.id = l.supplier_id
            LEFT JOIN nexora.lot_stock ls ON ls.lot_id = l.id
                 AND ({locationId}::uuid IS NULL OR ls.inventory_location_id = {locationId})
            WHERE l.company_id = {Company}
              AND ({productId}::uuid IS NULL OR l.product_id = {productId})
              AND ({status}::text IS NULL OR l.status = {status})
              AND (l.expiration_date IS NULL OR l.expiration_date <= {horizon} OR {expiringWithinDays}::int IS NULL)
            GROUP BY l.id, l.lot_number, l.product_id, p.sku, p.name, l.expiration_date,
                     l.manufacture_date, l.received_at, l.status, l.supplier_id, s.name,
                     l.blocked_reason, l.recall_reference
            ORDER BY l.expiration_date NULLS LAST, l.lot_number
            """).ToListAsync(ct);

        var days = await ExpiryAlertDaysAsync(ct);
        return rows.Select(r => new LotDto(
            r.Id, r.LotNumber, r.ProductId, r.Sku, r.ProductName, r.ExpirationDate, r.ManufactureDate,
            r.ReceivedAt, r.Status,
            r.ExpirationDate is null ? "none"
                : r.ExpirationDate < today ? "expired"
                : r.ExpirationDate <= today.AddDays(days) ? "near" : "normal",
            r.OnHand, r.Reserved, r.SupplierId, r.SupplierName, r.BlockedReason, r.RecallReference)).ToList();
    }

    public async Task<IReadOnlyList<StockAlertDto>> AlertsAsync(CancellationToken ct = default)
    {
        var today = DateOnly.FromDateTime(clock.UtcNow.UtcDateTime);
        var days = await ExpiryAlertDaysAsync(ct);
        var horizon = today.AddDays(days);

        var shortage = await db.Database.SqlQuery<StockAlertDto>($"""
            SELECT CASE WHEN (sb.quantity_on_hand - sb.quantity_reserved) <= 0 THEN 'out_of_stock'
                        WHEN (sb.quantity_on_hand - sb.quantity_reserved) <= r.reorder_point THEN 'low_stock'
                        ELSE 'overstock' END AS "AlertType",
                   sb.product_id AS "ProductId", p.sku AS "Sku", p.name AS "ProductName",
                   sb.inventory_location_id AS "LocationId", il.name AS "LocationName",
                   sb.quantity_on_hand AS "OnHand", sb.quantity_available AS "Available",
                   CASE WHEN sb.quantity_on_hand > r.max_quantity AND r.max_quantity > 0 THEN r.max_quantity
                        ELSE r.reorder_point END AS "Threshold",
                   '' AS "Detail"
            FROM nexora.stock_balance sb
            JOIN nexora.product p ON p.id = sb.product_id
            JOIN nexora.inventory_location il ON il.id = sb.inventory_location_id
            JOIN nexora.product_reorder_rule r ON r.product_id = sb.product_id
                 AND (r.inventory_location_id IS NULL OR r.inventory_location_id = sb.inventory_location_id)
            WHERE sb.company_id = {Company} AND r.status = 'active'
              AND ((sb.quantity_on_hand - sb.quantity_reserved) <= r.reorder_point
                   OR (r.max_quantity > 0 AND sb.quantity_on_hand > r.max_quantity))
            """).ToListAsync(ct);

        var expiry = await db.Database.SqlQuery<StockAlertDto>($"""
            SELECT CASE WHEN l.expiration_date < {today} THEN 'expired' ELSE 'near_expiry' END AS "AlertType",
                   l.product_id AS "ProductId", p.sku AS "Sku", p.name AS "ProductName",
                   ls.inventory_location_id AS "LocationId", il.name AS "LocationName",
                   ls.quantity_on_hand AS "OnHand", ls.quantity_on_hand - ls.quantity_reserved AS "Available",
                   NULL::numeric AS "Threshold",
                   ('lot ' || l.lot_number || ' exp. ' || l.expiration_date::text) AS "Detail"
            FROM nexora.lot l
            JOIN nexora.lot_stock ls ON ls.lot_id = l.id AND ls.quantity_on_hand > 0
            JOIN nexora.product p ON p.id = l.product_id
            JOIN nexora.inventory_location il ON il.id = ls.inventory_location_id
            WHERE l.company_id = {Company} AND l.status IN ('active','blocked')
              AND l.expiration_date IS NOT NULL AND l.expiration_date <= {horizon}
            ORDER BY l.expiration_date
            """).ToListAsync(ct);

        return [.. shortage, .. expiry];
    }

    public async Task<IReadOnlyList<WarehouseDto>> WarehousesAsync(CancellationToken ct = default)
        => await db.Database.SqlQuery<WarehouseDto>($"""
            SELECT id AS "Id", name AS "Name", code AS "Code", type AS "Type", status AS "Status",
                   establishment_id AS "EstablishmentId", address AS "Address", capacity AS "Capacity",
                   manager_user_id AS "ManagerUserId"
            FROM nexora.warehouse WHERE company_id = {Company} ORDER BY name
            """).ToListAsync(ct);

    public async Task<IReadOnlyList<LocationDto>> LocationsAsync(Guid? warehouseId, CancellationToken ct = default)
        => await db.Database.SqlQuery<LocationDto>($"""
            SELECT id AS "Id", name AS "Name", code AS "Code", location_type AS "LocationType",
                   status AS "Status", warehouse_id AS "WarehouseId", store_id AS "StoreId",
                   zone AS "Zone", aisle AS "Aisle", shelf AS "Shelf", bin AS "Bin", capacity AS "Capacity"
            FROM nexora.inventory_location
            WHERE company_id = {Company} AND ({warehouseId}::uuid IS NULL OR warehouse_id = {warehouseId})
            ORDER BY name
            """).ToListAsync(ct);

    private async Task<int> ExpiryAlertDaysAsync(CancellationToken ct)
    {
        try
        {
            var s = await settings.ResolveAsync("stock.expiry_alert_days", null, ct);
            return s.Value.TryGetInt32(out var d) ? d : 30;
        }
        catch { return 30; }
    }

    private sealed record LotRow(
        Guid Id, string LotNumber, Guid ProductId, string Sku, string ProductName,
        DateOnly? ExpirationDate, DateOnly? ManufactureDate, DateOnly? ReceivedAt, string Status,
        Guid? SupplierId, string? SupplierName, string? BlockedReason, string? RecallReference,
        decimal OnHand, decimal Reserved);
}
