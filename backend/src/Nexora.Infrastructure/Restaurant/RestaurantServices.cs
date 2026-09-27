using Microsoft.EntityFrameworkCore;
using Nexora.Application.Abstractions;
using Nexora.Application.Inventory;
using Nexora.Application.Restaurant;
using Nexora.Application.Sales;
using Nexora.Application.Sectors;
using Nexora.Contracts.Restaurant;
using Nexora.Domain.Common;
using Nexora.Domain.Inventory;
using Nexora.Infrastructure.Persistence;

namespace Nexora.Infrastructure.Restaurant;

/// <summary>
/// Expansion « plat → ingrédients » au moment de la vente (Phase 8 §6, §24).
/// Aucune écriture directe : renvoie des mouvements que le noyau applique.
/// </summary>
public sealed class SqlRecipeExpansion(NexoraDbContext db, ITenantContext tenant, IStockLedger stock) : IRecipeExpansion
{
    private Guid Company => tenant.ActiveCompanyId ?? throw new InvalidOperationException("Aucune entreprise active.");
    private sealed record Ing(Guid ComponentProductId, decimal Quantity, decimal WastagePercentage);

    public async Task<RecipeExpansion?> ExpandAsync(Guid productId, decimal dishQuantity, Guid inventoryLocationId, CancellationToken ct = default)
    {
        var ingredients = await db.Database.SqlQuery<Ing>($"""
            SELECT ri.component_product_id AS "ComponentProductId", ri.quantity AS "Quantity",
                   ri.wastage_percentage AS "WastagePercentage"
            FROM nexora.recipe r
            JOIN nexora.recipe_ingredient ri ON ri.recipe_id = r.id
            WHERE r.company_id = {Company} AND r.product_id = {productId} AND r.status = 'active'
            """).ToListAsync(ct);
        if (ingredients.Count == 0) return null;

        var movements = new List<StockMovementRequest>();
        decimal totalCost = 0m;
        foreach (var ing in ingredients)
        {
            var perDish = ing.Quantity * (1m + ing.WastagePercentage / 100m);
            var qty = Math.Round(perDish * dishQuantity, 6);
            if (qty <= 0m) continue;
            var unitCost = await stock.AverageCostAsync(ing.ComponentProductId, inventoryLocationId, ct);
            totalCost += Math.Round(unitCost * qty, 2);
            movements.Add(new StockMovementRequest(
                ing.ComponentProductId, inventoryLocationId, MovementType.Sale, -qty, unitCost));
        }
        if (movements.Count == 0) return null;

        var unitDishCost = dishQuantity > 0m ? Math.Round(totalCost / dishQuantity, 2) : 0m;
        return new RecipeExpansion(unitDishCost, movements);
    }
}

/// <summary>Recettes / nomenclatures (Phase 8 §6, §11).</summary>
public sealed class SqlRecipeService(NexoraDbContext db, IUnitOfWork uow, ITenantContext tenant) : IRecipeService
{
    private Guid Company => tenant.ActiveCompanyId ?? throw new InvalidOperationException("Aucune entreprise active.");

    public async Task<Result<Guid>> DefineAsync(DefineRecipeRequest request, CancellationToken ct = default)
    {
        if (request.Components.Count == 0)
            return Error.Validation("recipes.empty", "Une recette doit contenir au moins un ingrédient.");
        var productOk = await db.Products.AsNoTracking().AnyAsync(p => p.Id == request.ProductId && p.CompanyId == Company, ct);
        if (!productOk) return Error.NotFound("recipes.product", "Produit introuvable.");

        var newId = Guid.NewGuid();
        var id = await uow.ExecuteInTransactionAsync(async token =>
        {
            await db.Database.ExecuteSqlAsync($"""
                INSERT INTO nexora.recipe (id, company_id, product_id, name, yield_quantity, yield_unit_id, status)
                VALUES ({newId}, {Company}, {request.ProductId}, {request.Name}, {request.YieldQuantity}, {request.YieldUnitId}, 'active')
                ON CONFLICT (product_id) DO UPDATE SET name = EXCLUDED.name, yield_quantity = EXCLUDED.yield_quantity,
                    yield_unit_id = EXCLUDED.yield_unit_id, status = 'active', updated_at = now()
                """, token);
            var recipeId = await db.Database.SqlQuery<Guid>($"""
                SELECT id AS "Value" FROM nexora.recipe WHERE company_id = {Company} AND product_id = {request.ProductId}
                """).FirstAsync(token);

            await db.Database.ExecuteSqlAsync($"DELETE FROM nexora.recipe_ingredient WHERE recipe_id = {recipeId}", token);
            foreach (var c in request.Components)
                await db.Database.ExecuteSqlAsync($"""
                    INSERT INTO nexora.recipe_ingredient
                        (recipe_id, company_id, component_product_id, quantity, unit_id, wastage_percentage, kitchen_station)
                    VALUES ({recipeId}, {Company}, {c.ComponentProductId}, {c.Quantity}, {c.UnitId},
                            {c.WastagePercent}, {c.KitchenStation})
                    """, token);
            return recipeId;
        }, ct);
        return Result.Success(id);
    }

    public async Task<Result<RecipeDto>> GetAsync(Guid productId, CancellationToken ct = default)
    {
        var head = await db.Database.SqlQuery<HeadRow>($"""
            SELECT id AS "Id", name AS "Name", yield_quantity AS "YieldQuantity"
            FROM nexora.recipe WHERE company_id = {Company} AND product_id = {productId} AND status = 'active'
            """).FirstOrDefaultAsync(ct);
        if (head is null) return Error.NotFound("recipes.not_found", "Aucune recette pour ce produit.");

        var comps = await db.Database.SqlQuery<CompRow>($"""
            SELECT ri.component_product_id AS "ComponentProductId", p.name AS "ComponentName",
                   ri.quantity AS "Quantity", ri.wastage_percentage AS "WastagePercent",
                   ri.kitchen_station AS "KitchenStation",
                   COALESCE((SELECT AVG(sb.average_cost) FROM nexora.stock_balance sb
                             WHERE sb.product_id = ri.component_product_id), 0) AS "AvgCost"
            FROM nexora.recipe r
            JOIN nexora.recipe_ingredient ri ON ri.recipe_id = r.id
            JOIN nexora.product p ON p.id = ri.component_product_id
            WHERE r.company_id = {Company} AND r.product_id = {productId}
            ORDER BY p.name
            """).ToListAsync(ct);

        var cost = Math.Round(comps.Sum(c => c.Quantity * (1m + c.WastagePercent / 100m) * c.AvgCost), 2);
        return Result.Success(new RecipeDto(head.Id, productId, head.Name, head.YieldQuantity, cost,
            comps.Select(c => new RecipeComponentDto(c.ComponentProductId, c.ComponentName, c.Quantity, c.WastagePercent, c.KitchenStation)).ToList()));
    }

    private sealed record HeadRow(Guid Id, string Name, decimal YieldQuantity);
    private sealed record CompRow(Guid ComponentProductId, string ComponentName, decimal Quantity, decimal WastagePercent, string? KitchenStation, decimal AvgCost);
}

/// <summary>
/// Module Restaurant (Phase 8 §6, §24). La commande est une vente en attente (§34-35 Phase 6) ;
/// sa clôture appelle <c>SalesService.CheckoutAsync</c> — même stock / paiement / comptabilité (§22).
/// </summary>
public sealed class EfRestaurantService(
    NexoraDbContext db, IUnitOfWork uow, ITenantContext tenant, IDateTimeProvider clock,
    ISectorConfigService sectors, SalesService sales) : IRestaurantService
{
    private Guid Company => tenant.ActiveCompanyId ?? throw new InvalidOperationException("Aucune entreprise active.");

    private static readonly string[] KitchenFlow = ["pending", "preparing", "ready", "served"];

    private async Task<Error?> RequireModuleAsync(CancellationToken ct)
        => await sectors.IsModuleEnabledAsync("restaurant", ct)
            ? null
            : Error.Forbidden("restaurant.module_disabled", "Le module Restaurant n'est pas activé pour cette entreprise (§3).");

    public async Task<Result<Guid>> CreateAreaAsync(CreateAreaRequest r, CancellationToken ct = default)
    {
        if (await RequireModuleAsync(ct) is { } err) return err;
        if (!await db.Establishments.AsNoTracking().AnyAsync(e => e.Id == r.EstablishmentId && e.CompanyId == Company, ct))
            return Error.NotFound("restaurant.establishment", "Établissement introuvable.");
        var id = Guid.NewGuid();
        await db.Database.ExecuteSqlAsync($"""
            INSERT INTO nexora.restaurant_area (id, company_id, establishment_id, name, code)
            VALUES ({id}, {Company}, {r.EstablishmentId}, {r.Name.Trim()}, {r.Code.Trim()})
            """, ct);
        await uow.SaveChangesAsync(ct);
        return Result.Success(id);
    }

    public async Task<Result<Guid>> CreateTableAsync(CreateTableRequest r, CancellationToken ct = default)
    {
        if (await RequireModuleAsync(ct) is { } err) return err;
        if (!await db.Establishments.AsNoTracking().AnyAsync(e => e.Id == r.EstablishmentId && e.CompanyId == Company, ct))
            return Error.NotFound("restaurant.establishment", "Établissement introuvable.");
        var id = Guid.NewGuid();
        await db.Database.ExecuteSqlAsync($"""
            INSERT INTO nexora.restaurant_table (id, company_id, establishment_id, area_id, name, code, seats, status)
            VALUES ({id}, {Company}, {r.EstablishmentId}, {r.AreaId}, {r.Name.Trim()}, {r.Code.Trim()},
                    {(short)Math.Clamp(r.Seats, 1, 999)}, 'available')
            """, ct);
        await uow.SaveChangesAsync(ct);
        return Result.Success(id);
    }

    public async Task<IReadOnlyList<RestaurantTableDto>> ListTablesAsync(CancellationToken ct = default)
        => await db.Database.SqlQuery<RestaurantTableDto>($"""
            SELECT id AS "Id", name AS "Name", code AS "Code", seats::int AS "Seats",
                   area_id AS "AreaId", status AS "Status", current_sale_id AS "CurrentSaleId"
            FROM nexora.restaurant_table WHERE company_id = {Company} ORDER BY code
            """).ToListAsync(ct);

    public async Task<Result<RestaurantOrderDto>> PlaceOrderAsync(PlaceOrderRequest r, CancellationToken ct = default)
    {
        if (await RequireModuleAsync(ct) is { } err) return err;
        if (r.Items.Count == 0) return Error.Validation("restaurant.empty_order", "La commande est vide.");

        var table = await db.Database.SqlQuery<TableRow>($"""
            SELECT id AS "Id", establishment_id AS "EstablishmentId", status AS "Status",
                   current_sale_id AS "CurrentSaleId", name AS "Name"
            FROM nexora.restaurant_table WHERE company_id = {Company} AND id = {r.TableId}
            """).FirstOrDefaultAsync(ct);
        if (table is null) return Error.NotFound("restaurant.table", "Table introuvable.");
        if (table.CurrentSaleId is not null) return Error.Conflict("restaurant.table_busy", "La table a déjà une commande ouverte.");

        var locationId = await db.Database.SqlQuery<Guid?>($"""
            SELECT id AS "Value" FROM nexora.inventory_location
            WHERE company_id = {Company} AND establishment_id = {table.EstablishmentId} AND status = 'active'
            ORDER BY (location_type = 'store') DESC LIMIT 1
            """).FirstOrDefaultAsync(ct);
        if (locationId is null) return Error.Validation("restaurant.no_location", "Aucun emplacement de stock pour l'établissement.");

        var currency = await db.Companies.AsNoTracking()
            .Where(c => c.Id == Company).Select(c => c.DefaultCurrencyId).FirstAsync(ct);
        var currencyCode = await db.Currencies.AsNoTracking().Where(c => c.Id == currency).Select(c => c.Code).FirstAsync(ct);

        var reference = $"T{table.Id.ToString("N")[..6]}-{clock.UtcNow:HHmmss}";
        var lines = r.Items.Select(i => new CheckoutLine(i.ProductId, i.Quantity, null, i.LineDiscount, null)).ToList();

        Guid saleId = Guid.Empty;
        var parkResult = await sales.ParkAsync(new ParkCartCommand(locationId.Value, null, currencyCode, reference, lines), ct);
        if (!parkResult.IsSuccess) return parkResult.Error!;
        saleId = parkResult.Value;

        await db.Database.ExecuteSqlAsync($"""
            INSERT INTO nexora.sale_service_context
                (sale_id, company_id, restaurant_table_id, covers, waiter_user_id, kitchen_status, notes, priority, opened_at)
            VALUES ({saleId}, {Company}, {r.TableId}, {(short)Math.Max(1, r.Covers)}, {r.WaiterUserId},
                    'pending', {r.Notes}, 0, now())
            """, ct);
        await db.Database.ExecuteSqlAsync($"""
            UPDATE nexora.restaurant_table SET status = 'occupied', current_sale_id = {saleId}, updated_at = now()
            WHERE id = {r.TableId}
            """, ct);
        await uow.SaveChangesAsync(ct);

        return await GetOrderAsync(saleId, ct);
    }

    public async Task<Result> AdvanceKitchenAsync(KitchenAdvanceRequest r, CancellationToken ct = default)
    {
        var target = r.TargetStatus.Trim().ToLowerInvariant();
        if (!KitchenFlow.Contains(target))
            return Error.Validation("restaurant.kitchen_status", "Statut cuisine invalide (pending/preparing/ready/served).");

        var current = await db.Database.SqlQuery<string>($"""
            SELECT kitchen_status AS "Value" FROM nexora.sale_service_context WHERE sale_id = {r.SaleId} AND company_id = {Company}
            """).FirstOrDefaultAsync(ct);
        if (current is null) return Error.NotFound("restaurant.order", "Commande introuvable.");
        if (Array.IndexOf(KitchenFlow, target) < Array.IndexOf(KitchenFlow, current))
            return Error.Conflict("restaurant.kitchen_regress", "Un ticket cuisine ne revient pas en arrière.");

        var stamp = target switch
        {
            "preparing" => "fired_at",
            "ready" => "ready_at",
            "served" => "served_at",
            _ => null,
        };
        var sql = $"UPDATE nexora.sale_service_context SET kitchen_status = {{0}}, updated_at = now()"
                  + (stamp is null ? "" : $", {stamp} = now()")
                  + " WHERE sale_id = {1} AND company_id = {2}";
        await db.Database.ExecuteSqlRawAsync(sql, target, r.SaleId, Company);
        await uow.SaveChangesAsync(ct);
        return Result.Success();
    }

    public async Task<Result<RestaurantOrderDto>> GetOrderAsync(Guid saleId, CancellationToken ct = default)
    {
        var order = await LoadOrderAsync(saleId, ct);
        return order is null ? Error.NotFound("restaurant.order", "Commande introuvable.") : Result.Success(order);
    }

    public async Task<IReadOnlyList<RestaurantOrderDto>> KitchenBoardAsync(CancellationToken ct = default)
    {
        var ids = await db.Database.SqlQuery<Guid>($"""
            SELECT ssc.sale_id AS "Value"
            FROM nexora.sale_service_context ssc
            JOIN nexora.sale s ON s.id = ssc.sale_id
            WHERE ssc.company_id = {Company} AND s.status = 'parked' AND ssc.kitchen_status <> 'served'
            ORDER BY ssc.priority DESC, ssc.opened_at
            """).ToListAsync(ct);
        var board = new List<RestaurantOrderDto>();
        foreach (var id in ids)
            if (await LoadOrderAsync(id, ct) is { } o) board.Add(o);
        return board;
    }

    public async Task<Result<CheckoutResult>> CloseOrderAsync(CloseOrderRequest r, CancellationToken ct = default)
    {
        var ctx = await db.Database.SqlQuery<CtxRow>($"""
            SELECT ssc.restaurant_table_id AS "TableId", ssc.covers::int AS "Covers",
                   ssc.waiter_user_id AS "WaiterUserId", ssc.kitchen_status AS "KitchenStatus",
                   s.status AS "SaleStatus"
            FROM nexora.sale_service_context ssc
            JOIN nexora.sale s ON s.id = ssc.sale_id
            WHERE ssc.company_id = {Company} AND ssc.sale_id = {r.SaleId}
            """).FirstOrDefaultAsync(ct);
        if (ctx is null) return Error.NotFound("restaurant.order", "Commande introuvable.");
        if (ctx.SaleStatus != "parked") return Error.Conflict("restaurant.order_closed", "Cette commande n'est plus ouverte.");

        var items = await db.Database.SqlQuery<ItemRow>($"""
            SELECT sl.product_id AS "ProductId", sl.quantity AS "Quantity", sl.discount AS "Discount",
                   sl.inventory_location_id AS "LocationId"
            FROM nexora.sale_line sl WHERE sl.sale_id = {r.SaleId}
            """).ToListAsync(ct);
        if (items.Count == 0) return Error.Validation("restaurant.empty_order", "Commande sans article.");
        var locationId = items[0].LocationId ?? throw new InvalidOperationException("Emplacement manquant sur la commande.");

        var currencyCode = await db.Companies.AsNoTracking().Where(c => c.Id == Company)
            .Join(db.Currencies.AsNoTracking(), c => c.DefaultCurrencyId, cur => cur.Id, (c, cur) => cur.Code).FirstAsync(ct);

        var checkout = new CheckoutCommand(
            Guid.NewGuid(), locationId, null, null, r.CashSessionId, null, currencyCode,
            items.Select(i => new CheckoutLine(i.ProductId, i.Quantity, null, i.Discount, null)).ToList(),
            r.Tenders.Select(t => new TenderInput(t.FinancialAccountId, t.PaymentMethodId, t.Amount, t.Reference)).ToList(),
            r.AmountTendered, null, null, null, null);

        var result = await sales.CheckoutAsync(checkout, ct);
        if (!result.IsSuccess) return result.Error!;

        // La commande en attente est clôturée (§24 : traçable, pas de suppression) et
        // le contexte service est rattaché à la vente réglée.
        await uow.ExecuteInTransactionAsync(async token =>
        {
            await db.Database.ExecuteSqlAsync($"""
                UPDATE nexora.sale SET status = 'void', voided_at = now(),
                    void_reason = {"Commande restaurant réglée → " + result.Value!.DocumentNumber}, updated_at = now()
                WHERE id = {r.SaleId} AND company_id = {Company}
                """, token);
            await db.Database.ExecuteSqlAsync($"""
                INSERT INTO nexora.sale_service_context
                    (sale_id, company_id, restaurant_table_id, covers, waiter_user_id, kitchen_status,
                     tip_amount, opened_at, closed_at, served_at)
                VALUES ({result.Value!.SaleId}, {Company}, {ctx.TableId}, {(short)ctx.Covers}, {ctx.WaiterUserId},
                        'served', {Math.Max(0m, r.TipAmount)}, now(), now(), now())
                """, token);
            await db.Database.ExecuteSqlAsync($"""
                UPDATE nexora.restaurant_table SET status = 'available', current_sale_id = NULL, updated_at = now()
                WHERE id = {ctx.TableId} AND company_id = {Company}
                """, token);
            return 0;
        }, ct);

        return result;
    }

    private async Task<RestaurantOrderDto?> LoadOrderAsync(Guid saleId, CancellationToken ct)
    {
        var head = await db.Database.SqlQuery<OrderHeadRow>($"""
            SELECT s.document_number AS "DocumentNumber", ssc.restaurant_table_id AS "TableId",
                   rt.name AS "TableName", ssc.covers::int AS "Covers", ssc.kitchen_status AS "KitchenStatus"
            FROM nexora.sale_service_context ssc
            JOIN nexora.sale s ON s.id = ssc.sale_id
            LEFT JOIN nexora.restaurant_table rt ON rt.id = ssc.restaurant_table_id
            WHERE ssc.company_id = {Company} AND ssc.sale_id = {saleId}
            """).FirstOrDefaultAsync(ct);
        if (head is null) return null;

        var lines = await db.Database.SqlQuery<OrderLineDto>($"""
            SELECT product_id AS "ProductId", description AS "Description", quantity AS "Quantity",
                   unit_price AS "UnitPrice", line_total AS "LineTotal"
            FROM nexora.sale_line WHERE sale_id = {saleId} ORDER BY created_at
            """).ToListAsync(ct);

        return new RestaurantOrderDto(saleId, head.DocumentNumber, head.TableId, head.TableName ?? "—",
            head.Covers, head.KitchenStatus, Math.Round(lines.Sum(l => l.LineTotal), 2), lines);
    }

    private sealed record TableRow(Guid Id, Guid EstablishmentId, string Status, Guid? CurrentSaleId, string Name);
    private sealed record CtxRow(Guid TableId, int Covers, Guid? WaiterUserId, string KitchenStatus, string SaleStatus);
    private sealed record ItemRow(Guid ProductId, decimal Quantity, decimal Discount, Guid? LocationId);
    private sealed record OrderHeadRow(string DocumentNumber, Guid TableId, string? TableName, int Covers, string KitchenStatus);
}
