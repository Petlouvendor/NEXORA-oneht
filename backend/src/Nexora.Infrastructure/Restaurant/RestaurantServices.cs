using Microsoft.EntityFrameworkCore;
using Nexora.Application.Abstractions;
using Nexora.Application.Inventory;
using Nexora.Application.Restaurant;
using Nexora.Application.Sales;
using Nexora.Application.Sectors;
using Nexora.Contracts.Restaurant;
using Nexora.Domain.Common;
using Nexora.Domain.Catalog;
using Nexora.Domain.Inventory;
using Nexora.Infrastructure.Persistence;

namespace Nexora.Infrastructure.Restaurant;

/// <summary>
/// Expansion « plat → ingrédients » au moment de la vente (Phase 8 §6, §24).
/// Aucune écriture directe : renvoie des mouvements que le noyau applique.
/// </summary>
public sealed class SqlRecipeExpansion(NexoraDbContext db, ITenantContext tenant, IStockLedger stock) : IRecipeExpansion
{
    private Guid Company => tenant.ActiveCompanyId ?? throw TenantErrors.ActiveCompanyRequired();
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

/// <summary>
/// Recettes / nomenclatures (Phase 8 §6, §11 ; finalisation phase 13). Une recette ne s'applique à la vente qu'à un plat
/// <b>non stocké</b> (composé, service, non stocké) : sur un produit stocké, elle était acceptée puis ignorée. Les quantités
/// d'ingrédients sont exprimées dans l'unité de stock de l'ingrédient — la sortie de stock n'applique aucune conversion.
/// </summary>
public sealed class SqlRecipeService(NexoraDbContext db, IUnitOfWork uow, ITenantContext tenant) : IRecipeService
{
    private Guid Company => tenant.ActiveCompanyId ?? throw TenantErrors.ActiveCompanyRequired();
    private sealed record ProdRow(Guid Id, string ProductType, Guid? UnitId, string Name);

    private Task<ProdRow?> ProductAsync(Guid id, CancellationToken ct) => db.Database.SqlQuery<ProdRow>($"""
        SELECT id AS "Id", product_type AS "ProductType", unit_id AS "UnitId", name AS "Name"
        FROM nexora.product WHERE id = {id} AND company_id = {Company}
        """).FirstOrDefaultAsync(ct);

    private static bool Stocked(string type) => type is ProductType.Stockable or ProductType.Weight;

    public async Task<Result<Guid>> DefineAsync(DefineRecipeRequest request, CancellationToken ct = default)
    {
        if (request.Components is null || request.Components.Count == 0)
            return Error.Validation("recipes.empty", "Une recette doit contenir au moins un ingrédient.");
        if (string.IsNullOrWhiteSpace(request.Name) || request.Name.Length > 120)
            return Error.Validation("recipes.name", "Nom de recette requis (120 caractères au plus).");
        if (request.YieldQuantity <= 0m) return Error.Validation("recipes.yield", "La recette produit une quantité positive.");
        var dish = await ProductAsync(request.ProductId, ct);
        if (dish is null) return Error.NotFound("recipes.product", "Produit introuvable.");
        if (Stocked(dish.ProductType))
            return Error.Validation("recipes.stocked_dish",
                $"« {dish.Name} » est un produit stocké : sa vente sort le produit lui-même, jamais sa recette. Utilisez un plat de type composé.");
        if (!await db.Database.SqlQuery<bool>($"""
                SELECT EXISTS(SELECT 1 FROM nexora.unit WHERE id = {request.YieldUnitId} AND company_id = {Company}) AS "Value"
                """).FirstAsync(ct))
            return Error.Validation("recipes.unit", "Unité de rendement introuvable.");

        var seen = new HashSet<Guid>();
        foreach (var (c, i) in request.Components.Select((c, i) => (c, i + 1)))
        {
            if (!seen.Add(c.ComponentProductId)) return Error.Validation("recipes.duplicate", $"Ingrédient {i} : déjà présent dans la recette.");
            if (c.ComponentProductId == request.ProductId) return Error.Validation("recipes.self", $"Ingrédient {i} : un plat ne peut pas être son propre ingrédient.");
            if (c.Quantity <= 0m) return Error.Validation("recipes.quantity", $"Ingrédient {i} : quantité positive requise.");
            if (c.WastagePercent is < 0m or >= 100m) return Error.Validation("recipes.wastage", $"Ingrédient {i} : perte entre 0 et 99 %.");
            if (c.KitchenStation is { Length: > 40 }) return Error.Validation("recipes.station", $"Ingrédient {i} : poste de cuisine trop long.");
            var ing = await ProductAsync(c.ComponentProductId, ct);
            if (ing is null) return Error.Validation("recipes.component", $"Ingrédient {i} : produit introuvable.");
            if (!Stocked(ing.ProductType))
                return Error.Validation("recipes.component_not_stocked", $"Ingrédient {i} : « {ing.Name} » n'est pas suivi en stock.");
            if (ing.UnitId is { } unit && unit != c.UnitId)
                return Error.Validation("recipes.component_unit",
                    $"Ingrédient {i} : saisissez la quantité de « {ing.Name} » dans son unité de stock (aucune conversion n'est appliquée à la sortie).");
        }

        var newId = Guid.NewGuid();
        var id = await uow.ExecuteInTransactionAsync(async token =>
        {
            await db.Database.ExecuteSqlAsync($"""
                INSERT INTO nexora.recipe (id, company_id, product_id, name, yield_quantity, yield_unit_id, status)
                VALUES ({newId}, {Company}, {request.ProductId}, {request.Name.Trim()}, {request.YieldQuantity}, {request.YieldUnitId}, 'active')
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
                            {c.WastagePercent}, {c.KitchenStation?.Trim()})
                    """, token);
            return recipeId;
        }, ct);
        return Result.Success(id);
    }

    public async Task<Result<RecipeDto>> GetAsync(Guid productId, CancellationToken ct = default)
    {
        var dto = await LoadAsync(productId, ct);
        return dto is null ? Error.NotFound("recipes.not_found", "Aucune recette pour ce produit.") : Result.Success(dto);
    }

    public async Task<IReadOnlyList<RecipeDto>> ListAsync(CancellationToken ct = default)
    {
        var products = await db.Database.SqlQuery<Guid>($"""
            SELECT r.product_id AS "Value" FROM nexora.recipe r JOIN nexora.product p ON p.id = r.product_id
            WHERE r.company_id = {Company} AND r.status = 'active' ORDER BY p.name
            """).ToListAsync(ct);
        var list = new List<RecipeDto>();
        foreach (var p in products) if (await LoadAsync(p, ct) is { } dto) list.Add(dto);
        return list;
    }

    public async Task<RecipeOptionsDto> OptionsAsync(CancellationToken ct = default)
    {
        var products = await db.Database.SqlQuery<RecipeProductRow>($"""
            SELECT p.id AS "Id", p.name AS "Name", p.sku AS "Sku", p.product_type AS "ProductType", p.unit_id AS "UnitId",
                   EXISTS(SELECT 1 FROM nexora.recipe r WHERE r.product_id = p.id AND r.status = 'active') AS "HasRecipe",
                   p.is_sellable AS "IsSellable"
            FROM nexora.product p
            WHERE p.company_id = {Company} AND p.status = 'active'
            ORDER BY p.name
            """).ToListAsync(ct);
        var units = await db.Database.SqlQuery<RecipeUnitOption>($"""
            SELECT id AS "Id", code AS "Code", name AS "Name" FROM nexora.unit WHERE company_id = {Company} ORDER BY name
            """).ToListAsync(ct);
        RecipeProductOption Map(RecipeProductRow r) => new(r.Id, r.Name, r.Sku, r.ProductType, r.UnitId, r.HasRecipe);
        return new RecipeOptionsDto(
            products.Where(p => p.IsSellable && !Stocked(p.ProductType)).Select(Map).ToList(),
            products.Where(p => Stocked(p.ProductType)).Select(Map).ToList(),
            units);
    }

    private sealed record RecipeProductRow(Guid Id, string Name, string? Sku, string ProductType, Guid? UnitId, bool HasRecipe, bool IsSellable);

    private async Task<RecipeDto?> LoadAsync(Guid productId, CancellationToken ct)
    {
        var head = await db.Database.SqlQuery<HeadRow>($"""
            SELECT r.id AS "Id", r.name AS "Name", r.yield_quantity AS "YieldQuantity", p.name AS "ProductName", r.yield_unit_id AS "YieldUnitId"
            FROM nexora.recipe r JOIN nexora.product p ON p.id = r.product_id
            WHERE r.company_id = {Company} AND r.product_id = {productId} AND r.status = 'active'
            """).FirstOrDefaultAsync(ct);
        if (head is null) return null;

        var comps = await db.Database.SqlQuery<CompRow>($"""
            SELECT ri.component_product_id AS "ComponentProductId", p.name AS "ComponentName",
                   ri.quantity AS "Quantity", ri.wastage_percentage AS "WastagePercent",
                   ri.kitchen_station AS "KitchenStation", ri.unit_id AS "UnitId", u.code AS "UnitCode",
                   COALESCE((SELECT AVG(sb.average_cost) FROM nexora.stock_balance sb
                             WHERE sb.product_id = ri.component_product_id AND sb.company_id = {Company}), 0) AS "AvgCost"
            FROM nexora.recipe r
            JOIN nexora.recipe_ingredient ri ON ri.recipe_id = r.id
            JOIN nexora.product p ON p.id = ri.component_product_id
            LEFT JOIN nexora.unit u ON u.id = ri.unit_id
            WHERE r.company_id = {Company} AND r.product_id = {productId}
            ORDER BY p.name
            """).ToListAsync(ct);

        var cost = Math.Round(comps.Sum(c => c.Quantity * (1m + c.WastagePercent / 100m) * c.AvgCost), 2);
        return new RecipeDto(head.Id, productId, head.Name, head.YieldQuantity, cost,
            comps.Select(c => new RecipeComponentDto(c.ComponentProductId, c.ComponentName, c.Quantity, c.WastagePercent, c.KitchenStation,
                c.UnitId, c.UnitCode, Math.Round(c.AvgCost, 2))).ToList(),
            head.ProductName, head.YieldUnitId);
    }

    private sealed record HeadRow(Guid Id, string Name, decimal YieldQuantity, string ProductName, Guid? YieldUnitId);
    private sealed record CompRow(Guid ComponentProductId, string ComponentName, decimal Quantity, decimal WastagePercent,
        string? KitchenStation, Guid? UnitId, string? UnitCode, decimal AvgCost);
}

/// <summary>
/// Module Restaurant (Phase 8 §6, §24 ; finalisation phase 13). TABLE → COMMANDE → CUISINE → SERVICE → PAIEMENT.
/// La commande est une vente en attente ; sa clôture appelle <c>SalesService.CheckoutAsync</c> (contrôles de caisse,
/// prix, remises, stock, recette, finance, comptabilité).
/// <list type="bullet">
/// <item>La table est prise de façon atomique : deux serveurs ne peuvent pas ouvrir une commande sur la même table.</item>
/// <item>Les précisions cuisine (« sans oignon ») sont enregistrées ; elles étaient ignorées.</item>
/// <item>Le règlement est idempotent : un nouvel essai ne crée jamais une seconde vente.</item>
/// </list>
/// </summary>
public sealed class EfRestaurantService(
    NexoraDbContext db, IUnitOfWork uow, ITenantContext tenant, IDateTimeProvider clock,
    ISectorConfigService sectors, SalesService sales) : IRestaurantService
{
    private Guid Company => tenant.ActiveCompanyId ?? throw TenantErrors.ActiveCompanyRequired();

    private static readonly string[] KitchenFlow = ["pending", "preparing", "ready", "served"];

    private async Task<Error?> RequireModuleAsync(CancellationToken ct)
        => await sectors.IsModuleEnabledAsync("restaurant", ct)
            ? null
            : Error.Forbidden("restaurant.module_disabled", "Le module Restaurant n'est pas activé pour cette entreprise (§3).");

    /// <summary>Identifiant d'opération du règlement, dérivé de la commande : le même à chaque essai.</summary>
    private static Guid CloseOperationId(Guid saleId)
    {
        var hash = System.Security.Cryptography.MD5.HashData(System.Text.Encoding.UTF8.GetBytes($"restaurant-close:{saleId}"));
        return new Guid(hash);
    }

    public async Task<RestaurantSetupDto> SetupAsync(CancellationToken ct = default)
    {
        var est = await db.Database.SqlQuery<RestaurantEstablishmentOption>($"""
            SELECT e.id AS "Id", e.name AS "Name",
                   EXISTS(SELECT 1 FROM nexora.inventory_location l WHERE l.establishment_id = e.id AND l.status = 'active') AS "HasStockLocation"
            FROM nexora.establishment e WHERE e.company_id = {Company} ORDER BY e.name
            """).ToListAsync(ct);
        return new RestaurantSetupDto(est, await sectors.IsModuleEnabledAsync("restaurant", ct));
    }

    private async Task<Error?> CheckCodeNameAsync(string table, string name, string code, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Trim().Length > 80) return Error.Validation("restaurant.name", "Nom requis (80 caractères au plus).");
        if (string.IsNullOrWhiteSpace(code) || code.Trim().Length > 20) return Error.Validation("restaurant.code", "Code requis (20 caractères au plus).");
        var exists = table == "area"
            ? await db.Database.SqlQuery<bool>($"""SELECT EXISTS(SELECT 1 FROM nexora.restaurant_area WHERE company_id = {Company} AND lower(code) = lower({code.Trim()})) AS "Value" """).FirstAsync(ct)
            : await db.Database.SqlQuery<bool>($"""SELECT EXISTS(SELECT 1 FROM nexora.restaurant_table WHERE company_id = {Company} AND lower(code) = lower({code.Trim()})) AS "Value" """).FirstAsync(ct);
        return exists ? Error.Conflict("restaurant.code_exists", $"Le code « {code.Trim()} » est déjà utilisé.") : null;
    }

    public async Task<Result<Guid>> CreateAreaAsync(CreateAreaRequest r, CancellationToken ct = default)
    {
        if (await RequireModuleAsync(ct) is { } err) return err;
        if (!await db.Establishments.AsNoTracking().AnyAsync(e => e.Id == r.EstablishmentId && e.CompanyId == Company, ct))
            return Error.NotFound("restaurant.establishment", "Établissement introuvable.");
        if (await CheckCodeNameAsync("area", r.Name, r.Code, ct) is { } invalid) return invalid;
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
        if (r.Seats is < 1 or > 99) return Error.Validation("restaurant.seats", "Nombre de couverts entre 1 et 99.");
        if (r.AreaId is { } areaId && !await db.Database.SqlQuery<bool>($"""
                SELECT EXISTS(SELECT 1 FROM nexora.restaurant_area WHERE id = {areaId} AND company_id = {Company} AND establishment_id = {r.EstablishmentId}) AS "Value"
                """).FirstAsync(ct))
            return Error.Validation("restaurant.area", "La salle n'appartient pas à cet établissement.");
        if (await CheckCodeNameAsync("table", r.Name, r.Code, ct) is { } invalid) return invalid;
        var id = Guid.NewGuid();
        await db.Database.ExecuteSqlAsync($"""
            INSERT INTO nexora.restaurant_table (id, company_id, establishment_id, area_id, name, code, seats, status)
            VALUES ({id}, {Company}, {r.EstablishmentId}, {r.AreaId}, {r.Name.Trim()}, {r.Code.Trim()}, {(short)r.Seats}, 'available')
            """, ct);
        await uow.SaveChangesAsync(ct);
        return Result.Success(id);
    }

    public async Task<IReadOnlyList<RestaurantAreaDto>> ListAreasAsync(CancellationToken ct = default)
        => await db.Database.SqlQuery<RestaurantAreaDto>($"""
            SELECT a.id AS "Id", a.name AS "Name", a.code AS "Code", a.status AS "Status", a.establishment_id AS "EstablishmentId",
                   (SELECT COUNT(*)::int FROM nexora.restaurant_table t WHERE t.area_id = a.id) AS "TableCount"
            FROM nexora.restaurant_area a WHERE a.company_id = {Company} ORDER BY a.name
            """).ToListAsync(ct);

    public async Task<IReadOnlyList<RestaurantTableDto>> ListTablesAsync(CancellationToken ct = default)
        => await db.Database.SqlQuery<RestaurantTableDto>($"""
            SELECT t.id AS "Id", t.name AS "Name", t.code AS "Code", t.seats::int AS "Seats",
                   t.area_id AS "AreaId", t.status AS "Status", t.current_sale_id AS "CurrentSaleId",
                   a.name AS "AreaName", t.establishment_id AS "EstablishmentId", ssc.covers::int AS "Covers",
                   ssc.kitchen_status AS "KitchenStatus", ssc.opened_at AS "OpenedAt",
                   (SELECT SUM(sl.line_total - sl.discount) FROM nexora.sale_line sl WHERE sl.sale_id = t.current_sale_id) AS "RunningTotal",
                   s.document_number AS "DocumentNumber"
            FROM nexora.restaurant_table t
            LEFT JOIN nexora.restaurant_area a ON a.id = t.area_id
            LEFT JOIN nexora.sale_service_context ssc ON ssc.sale_id = t.current_sale_id
            LEFT JOIN nexora.sale s ON s.id = t.current_sale_id
            WHERE t.company_id = {Company} ORDER BY a.name NULLS LAST, t.code
            """).ToListAsync(ct);

    private Error? CheckItems(IReadOnlyList<OrderItemInput>? items)
    {
        if (items is null || items.Count == 0) return Error.Validation("restaurant.empty_order", "La commande est vide.");
        foreach (var (i, n) in items.Select((i, n) => (i, n + 1)))
        {
            if (i.Quantity <= 0m || i.Quantity > 999m) return Error.Validation("restaurant.quantity", $"Article {n} : quantité invalide.");
            if (i.LineDiscount < 0m) return Error.Validation("restaurant.discount", $"Article {n} : remise invalide.");
            if ((i.Modifiers ?? []).Any(m => string.IsNullOrWhiteSpace(m) || m.Length > 80) || (i.Modifiers?.Count ?? 0) > 10)
                return Error.Validation("restaurant.modifier", $"Article {n} : précisions de cuisine invalides (10 au plus, 80 caractères chacune).");
        }
        return null;
    }

    /// <summary>Précisions de cuisine rattachées aux lignes créées (même ordre que les articles demandés). Sans incidence sur le prix.</summary>
    private async Task SaveModifiersAsync(Guid saleId, IReadOnlyList<Guid> lineIds, IReadOnlyList<OrderItemInput> items, CancellationToken ct)
    {
        for (var k = 0; k < Math.Min(lineIds.Count, items.Count); k++)
            foreach (var m in items[k].Modifiers ?? [])
                await db.Database.ExecuteSqlAsync($"""
                    INSERT INTO nexora.sale_line_modifier (company_id, sale_id, sale_line_id, name, price_delta)
                    VALUES ({Company}, {saleId}, {lineIds[k]}, {m.Trim()}, 0)
                    """, ct);
    }

    public async Task<Result<RestaurantOrderDto>> PlaceOrderAsync(PlaceOrderRequest r, CancellationToken ct = default)
    {
        if (await RequireModuleAsync(ct) is { } err) return err;
        if (CheckItems(r.Items) is { } invalid) return invalid;
        if (r.Covers is < 1 or > 99) return Error.Validation("restaurant.covers", "Nombre de couverts entre 1 et 99.");
        if (r.Notes is { Length: > 300 }) return Error.Validation("restaurant.notes", "Note limitée à 300 caractères.");

        var table = await db.Database.SqlQuery<TableRow>($"""
            SELECT id AS "Id", establishment_id AS "EstablishmentId", status AS "Status",
                   current_sale_id AS "CurrentSaleId", name AS "Name"
            FROM nexora.restaurant_table WHERE company_id = {Company} AND id = {r.TableId}
            """).FirstOrDefaultAsync(ct);
        if (table is null) return Error.NotFound("restaurant.table", "Table introuvable.");
        if (table.CurrentSaleId is not null) return Error.Conflict("restaurant.table_busy", "La table a déjà une commande ouverte : ajoutez-y les articles.");
        if (table.Status != "available") return Error.Conflict("restaurant.table_unavailable", "Cette table n'est pas disponible.");

        var locationId = await db.Database.SqlQuery<Guid?>($"""
            SELECT id AS "Value" FROM nexora.inventory_location
            WHERE company_id = {Company} AND establishment_id = {table.EstablishmentId} AND status = 'active'
            ORDER BY (location_type = 'store') DESC LIMIT 1
            """).FirstOrDefaultAsync(ct);
        if (locationId is null) return Error.Validation("restaurant.no_location", "Aucun emplacement de stock pour l'établissement.");

        // Prise atomique de la table : deux serveurs ne peuvent pas l'ouvrir en même temps.
        var claimed = await db.Database.ExecuteSqlAsync($"""
            UPDATE nexora.restaurant_table SET status = 'occupied', updated_at = now()
            WHERE id = {r.TableId} AND company_id = {Company} AND status = 'available' AND current_sale_id IS NULL
            """, ct);
        if (claimed == 0) return Error.Conflict("restaurant.table_busy", "La table vient d'être prise par un autre serveur.");

        var currencyCode = await CurrencyCodeAsync(ct);
        // Référence unique : l'ancienne (table + heure:min:s) entrait en collision dès que la même table rouvrait à la même
        // seconde, n'importe quel jour (contrainte d'unicité des paniers en attente → erreur 500).
        var reference = $"T{table.Id.ToString("N")[..6]}-{clock.UtcNow:yyMMddHHmmss}-{Guid.NewGuid().ToString("N")[..4]}";
        var lines = r.Items.Select(i => new CheckoutLine(i.ProductId, i.Quantity, null, i.LineDiscount, null)).ToList();
        var parkResult = await sales.ParkAsync(new ParkCartCommand(locationId.Value, null, currencyCode, reference, lines), ct);
        if (!parkResult.IsSuccess)
        {
            await db.Database.ExecuteSqlAsync($"""
                UPDATE nexora.restaurant_table SET status = 'available', updated_at = now() WHERE id = {r.TableId} AND current_sale_id IS NULL
                """, ct);
            return parkResult.Error!;
        }
        var saleId = parkResult.Value;

        await uow.ExecuteInTransactionAsync(async token =>
        {
            await db.Database.ExecuteSqlAsync($"""
                INSERT INTO nexora.sale_service_context
                    (sale_id, company_id, restaurant_table_id, covers, waiter_user_id, kitchen_status, notes, priority, opened_at)
                VALUES ({saleId}, {Company}, {r.TableId}, {(short)r.Covers}, {r.WaiterUserId ?? tenant.UserId},
                        'pending', {r.Notes?.Trim()}, 0, now())
                """, token);
            await db.Database.ExecuteSqlAsync($"""
                UPDATE nexora.restaurant_table SET current_sale_id = {saleId}, updated_at = now() WHERE id = {r.TableId}
                """, token);
            await SaveModifiersAsync(saleId, await sales.ParkedLineIdsAsync(saleId, token), r.Items, token);
            return 0;
        }, ct);

        return await GetOrderAsync(saleId, ct);
    }

    public async Task<Result<RestaurantOrderDto>> AddItemsAsync(AddOrderItemsRequest r, CancellationToken ct = default)
    {
        if (await RequireModuleAsync(ct) is { } err) return err;
        if (CheckItems(r.Items) is { } invalid) return invalid;
        if (await OrderStatusAsync(r.SaleId, ct) is not { } status) return Error.NotFound("restaurant.order", "Commande introuvable.");
        if (status != "parked") return Error.Conflict("restaurant.order_closed", "Cette commande n'est plus ouverte.");

        var added = await sales.AddToParkedAsync(r.SaleId, await CurrencyCodeAsync(ct),
            r.Items.Select(i => new CheckoutLine(i.ProductId, i.Quantity, null, i.LineDiscount, null)).ToList(), ct);
        if (!added.IsSuccess) return added.Error!;

        await uow.ExecuteInTransactionAsync(async token =>
        {
            await SaveModifiersAsync(r.SaleId, added.Value!, r.Items, token);
            // Nouvelle vague : un ticket prêt ou servi revient en attente pour la cuisine.
            await db.Database.ExecuteSqlAsync($"""
                UPDATE nexora.sale_service_context
                   SET kitchen_status = CASE WHEN kitchen_status IN ('ready', 'served') THEN 'pending' ELSE kitchen_status END,
                       updated_at = now()
                WHERE sale_id = {r.SaleId} AND company_id = {Company}
                """, token);
            return 0;
        }, ct);
        return await GetOrderAsync(r.SaleId, ct);
    }

    public async Task<Result> CancelOrderAsync(CancelOrderRequest r, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(r.Reason) || r.Reason.Trim().Length < 3)
            return Error.Validation("restaurant.cancel_reason", "Motif d'annulation requis.");
        var ctx = await ContextAsync(r.SaleId, ct);
        if (ctx is null) return Error.NotFound("restaurant.order", "Commande introuvable.");
        if (ctx.SaleStatus != "parked") return Error.Conflict("restaurant.order_closed", "Cette commande n'est plus ouverte.");

        // Même règle que la caisse : l'auteur annule son panier, sinon il faut le droit d'annuler une vente.
        var voided = await sales.VoidAsync(r.SaleId, $"Commande restaurant annulée : {r.Reason.Trim()}", ct);
        if (!voided.IsSuccess) return voided.Error!;
        await uow.ExecuteInTransactionAsync(async token =>
        {
            await db.Database.ExecuteSqlAsync($"""
                UPDATE nexora.sale_service_context SET kitchen_status = 'cancelled', closed_at = now(), notes = concat_ws(' — ', notes, {"Annulée : " + r.Reason.Trim()}), updated_at = now()
                WHERE sale_id = {r.SaleId} AND company_id = {Company}
                """, token);
            await db.Database.ExecuteSqlAsync($"""
                UPDATE nexora.sale SET void_reason = {"Commande restaurant annulée : " + r.Reason.Trim()}, voided_at = COALESCE(voided_at, now()), updated_at = now()
                WHERE id = {r.SaleId} AND company_id = {Company}
                """, token);
            await FreeTableAsync(ctx.TableId, r.SaleId, token);
            return 0;
        }, ct);
        return Result.Success();
    }

    public async Task<Result> AdvanceKitchenAsync(KitchenAdvanceRequest r, CancellationToken ct = default)
    {
        if (await RequireModuleAsync(ct) is { } err) return err;
        var target = (r.TargetStatus ?? "").Trim().ToLowerInvariant();
        if (!KitchenFlow.Contains(target))
            return Error.Validation("restaurant.kitchen_status", "Statut cuisine invalide (pending/preparing/ready/served).");

        var ctx = await ContextAsync(r.SaleId, ct);
        if (ctx is null) return Error.NotFound("restaurant.order", "Commande introuvable.");
        if (ctx.SaleStatus != "parked") return Error.Conflict("restaurant.order_closed", "Cette commande est réglée ou annulée.");
        if (Array.IndexOf(KitchenFlow, target) < Array.IndexOf(KitchenFlow, ctx.KitchenStatus))
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
            WHERE ssc.company_id = {Company} AND s.status = 'parked' AND ssc.kitchen_status NOT IN ('served', 'cancelled')
            ORDER BY ssc.priority DESC, ssc.opened_at
            """).ToListAsync(ct);
        var board = new List<RestaurantOrderDto>();
        foreach (var id in ids)
            if (await LoadOrderAsync(id, ct) is { } o) board.Add(o);
        return board;
    }

    public async Task<Result<CheckoutResult>> CloseOrderAsync(CloseOrderRequest r, CancellationToken ct = default)
    {
        if (await RequireModuleAsync(ct) is { } err) return err;
        if (r.TipAmount < 0m) return Error.Validation("restaurant.tip", "Pourboire invalide.");
        var ctx = await ContextAsync(r.SaleId, ct);
        if (ctx is null) return Error.NotFound("restaurant.order", "Commande introuvable.");
        if (ctx.SaleStatus != "parked") return Error.Conflict("restaurant.order_closed", "Cette commande n'est plus ouverte.");

        var items = await db.Database.SqlQuery<ItemRow>($"""
            SELECT sl.product_id AS "ProductId", sl.quantity AS "Quantity", sl.discount AS "Discount",
                   sl.inventory_location_id AS "LocationId"
            FROM nexora.sale_line sl WHERE sl.sale_id = {r.SaleId}
            """).ToListAsync(ct);
        if (items.Count == 0) return Error.Validation("restaurant.empty_order", "Commande sans article.");
        var locationId = items[0].LocationId ?? throw new InvalidOperationException("Emplacement manquant sur la commande.");

        // Identifiant d'opération stable : si la vente a été créée mais que la suite a échoué, un nouvel essai la retrouve
        // au lieu d'encaisser une seconde fois.
        var checkout = new CheckoutCommand(
            CloseOperationId(r.SaleId), locationId, null, null, r.CashSessionId, null, await CurrencyCodeAsync(ct),
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
                WHERE id = {r.SaleId} AND company_id = {Company} AND status = 'parked'
                """, token);
            await db.Database.ExecuteSqlAsync($"""
                INSERT INTO nexora.sale_service_context
                    (sale_id, company_id, restaurant_table_id, covers, waiter_user_id, kitchen_status,
                     tip_amount, opened_at, closed_at, served_at)
                VALUES ({result.Value!.SaleId}, {Company}, {ctx.TableId}, {(short)ctx.Covers}, {ctx.WaiterUserId},
                        'served', {r.TipAmount}, {ctx.OpenedAt}, now(), now())
                ON CONFLICT (sale_id) DO NOTHING
                """, token);
            await db.Database.ExecuteSqlAsync($"""
                UPDATE nexora.sale_service_context SET closed_at = now(), updated_at = now() WHERE sale_id = {r.SaleId}
                """, token);
            await FreeTableAsync(ctx.TableId, r.SaleId, token);
            return 0;
        }, ct);

        return result;
    }

    private Task FreeTableAsync(Guid tableId, Guid saleId, CancellationToken ct) => db.Database.ExecuteSqlAsync($"""
        UPDATE nexora.restaurant_table SET status = 'available', current_sale_id = NULL, updated_at = now()
        WHERE id = {tableId} AND company_id = {Company} AND current_sale_id = {saleId}
        """, ct);

    private async Task<string> CurrencyCodeAsync(CancellationToken ct)
        => await db.Companies.AsNoTracking().Where(c => c.Id == Company)
            .Join(db.Currencies.AsNoTracking(), c => c.DefaultCurrencyId, cur => cur.Id, (c, cur) => cur.Code).FirstAsync(ct);

    private Task<string?> OrderStatusAsync(Guid saleId, CancellationToken ct) => db.Database.SqlQuery<string?>($"""
        SELECT s.status AS "Value" FROM nexora.sale_service_context ssc JOIN nexora.sale s ON s.id = ssc.sale_id
        WHERE ssc.company_id = {Company} AND ssc.sale_id = {saleId}
        """).FirstOrDefaultAsync(ct);

    private Task<CtxRow?> ContextAsync(Guid saleId, CancellationToken ct) => db.Database.SqlQuery<CtxRow>($"""
        SELECT ssc.restaurant_table_id AS "TableId", ssc.covers::int AS "Covers",
               ssc.waiter_user_id AS "WaiterUserId", ssc.kitchen_status AS "KitchenStatus",
               s.status AS "SaleStatus", ssc.opened_at AS "OpenedAt"
        FROM nexora.sale_service_context ssc
        JOIN nexora.sale s ON s.id = ssc.sale_id
        WHERE ssc.company_id = {Company} AND ssc.sale_id = {saleId}
        """).FirstOrDefaultAsync(ct);

    private async Task<RestaurantOrderDto?> LoadOrderAsync(Guid saleId, CancellationToken ct)
    {
        var head = await db.Database.SqlQuery<OrderHeadRow>($"""
            SELECT s.document_number AS "DocumentNumber", ssc.restaurant_table_id AS "TableId",
                   rt.name AS "TableName", ssc.covers::int AS "Covers", ssc.kitchen_status AS "KitchenStatus",
                   ssc.notes AS "Notes", ssc.opened_at AS "OpenedAt", ssc.fired_at AS "FiredAt", ssc.ready_at AS "ReadyAt",
                   ssc.served_at AS "ServedAt", a.name AS "AreaName", cur.code AS "Currency", u.full_name AS "WaiterName",
                   s.status AS "Status"
            FROM nexora.sale_service_context ssc
            JOIN nexora.sale s ON s.id = ssc.sale_id
            JOIN nexora.currency cur ON cur.id = s.currency_id
            LEFT JOIN nexora.restaurant_table rt ON rt.id = ssc.restaurant_table_id
            LEFT JOIN nexora.restaurant_area a ON a.id = rt.area_id
            LEFT JOIN nexora.app_user u ON u.id = ssc.waiter_user_id
            WHERE ssc.company_id = {Company} AND ssc.sale_id = {saleId}
            """).FirstOrDefaultAsync(ct);
        if (head is null) return null;

        var lines = await db.Database.SqlQuery<LineRow>($"""
            SELECT sl.id AS "Id", sl.product_id AS "ProductId", sl.description AS "Description", sl.quantity AS "Quantity",
                   sl.unit_price AS "UnitPrice", sl.line_total AS "LineTotal", sl.discount AS "Discount", sl.created_at AS "AddedAt",
                   sl.inventory_location_id AS "LocationId",
                   ARRAY(SELECT m.name FROM nexora.sale_line_modifier m WHERE m.sale_line_id = sl.id ORDER BY m.created_at) AS "Modifiers"
            FROM nexora.sale_line sl WHERE sl.sale_id = {saleId} ORDER BY sl.created_at
            """).ToListAsync(ct);

        return new RestaurantOrderDto(saleId, head.DocumentNumber, head.TableId, head.TableName ?? "—",
            head.Covers, head.KitchenStatus, Math.Round(lines.Sum(l => l.LineTotal - l.Discount), 2),
            lines.Select(l => new OrderLineDto(l.ProductId, l.Description, l.Quantity, l.UnitPrice, l.LineTotal, l.Id, l.Discount, l.Modifiers, l.AddedAt)).ToList(),
            head.Notes, head.OpenedAt, head.FiredAt, head.ReadyAt, head.ServedAt, head.AreaName,
            lines.Select(l => l.LocationId).FirstOrDefault(x => x is not null), head.Currency, head.WaiterName, head.Status);
    }

    private sealed record TableRow(Guid Id, Guid EstablishmentId, string Status, Guid? CurrentSaleId, string Name);
    private sealed record CtxRow(Guid TableId, int Covers, Guid? WaiterUserId, string KitchenStatus, string SaleStatus, DateTimeOffset? OpenedAt);
    private sealed record ItemRow(Guid ProductId, decimal Quantity, decimal Discount, Guid? LocationId);
    private sealed record OrderHeadRow(string DocumentNumber, Guid TableId, string? TableName, int Covers, string KitchenStatus,
        string? Notes, DateTimeOffset? OpenedAt, DateTimeOffset? FiredAt, DateTimeOffset? ReadyAt, DateTimeOffset? ServedAt,
        string? AreaName, string Currency, string? WaiterName, string Status);
    private sealed record LineRow(Guid Id, Guid ProductId, string Description, decimal Quantity, decimal UnitPrice, decimal LineTotal,
        decimal Discount, DateTimeOffset AddedAt, Guid? LocationId, string[] Modifiers);
}
