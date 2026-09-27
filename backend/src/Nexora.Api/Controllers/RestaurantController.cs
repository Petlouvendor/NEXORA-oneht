using Asp.Versioning;
using Microsoft.AspNetCore.Mvc;
using Nexora.Api.Security;
using Nexora.Application.Restaurant;
using Nexora.Application.Security;
using Nexora.Contracts.Restaurant;

namespace Nexora.Api.Controllers;

/// <summary>
/// Module Restaurant (Phase 8 §6, §24). TABLE → COMMANDE → KDS → SERVICE → PAIEMENT.
/// La clôture d'une commande passe par le parcours de vente du noyau : même stock,
/// même paiement, même comptabilité (§22).
/// </summary>
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/restaurant")]
public sealed class RestaurantController(
    IRestaurantService restaurant,
    IRecipeService recipes) : ApiControllerBase
{
    [HttpPost("areas")]
    [RequirePermission(Permissions.Restaurant.Manage)]
    public async Task<IActionResult> CreateArea([FromBody] CreateAreaRequest r, CancellationToken ct)
        => FromResult(await restaurant.CreateAreaAsync(r, ct));

    [HttpPost("tables")]
    [RequirePermission(Permissions.Restaurant.Manage)]
    public async Task<IActionResult> CreateTable([FromBody] CreateTableRequest r, CancellationToken ct)
        => FromResult(await restaurant.CreateTableAsync(r, ct));

    [HttpGet("tables")]
    [RequirePermission(Permissions.Restaurant.Serve)]
    public async Task<IActionResult> ListTables(CancellationToken ct)
        => Ok(await restaurant.ListTablesAsync(ct));

    [HttpPost("orders")]
    [RequirePermission(Permissions.Restaurant.Serve)]
    public async Task<IActionResult> PlaceOrder([FromBody] PlaceOrderRequest r, CancellationToken ct)
        => FromResult(await restaurant.PlaceOrderAsync(r, ct));

    [HttpGet("orders/{saleId:guid}")]
    [RequirePermission(Permissions.Restaurant.Serve)]
    public async Task<IActionResult> GetOrder(Guid saleId, CancellationToken ct)
        => FromResult(await restaurant.GetOrderAsync(saleId, ct));

    [HttpGet("kitchen")]
    [RequirePermission(Permissions.Restaurant.Kitchen)]
    public async Task<IActionResult> KitchenBoard(CancellationToken ct)
        => Ok(await restaurant.KitchenBoardAsync(ct));

    [HttpPost("kitchen/advance")]
    [RequirePermission(Permissions.Restaurant.Kitchen)]
    public async Task<IActionResult> Advance([FromBody] KitchenAdvanceRequest r, CancellationToken ct)
        => FromResult(await restaurant.AdvanceKitchenAsync(r, ct));

    [HttpPost("orders/close")]
    [RequirePermission(Permissions.Restaurant.Serve)]
    [Idempotent]
    public async Task<IActionResult> Close([FromBody] CloseOrderRequest r, CancellationToken ct)
        => FromResult(await restaurant.CloseOrderAsync(r, ct));

    // --- Recettes (§6, §11) -----------------------------------------

    [HttpPost("recipes")]
    [RequirePermission(Permissions.Recipes.Manage)]
    public async Task<IActionResult> DefineRecipe([FromBody] DefineRecipeRequest r, CancellationToken ct)
        => FromResult(await recipes.DefineAsync(r, ct));

    [HttpGet("recipes/{productId:guid}")]
    [RequirePermission(Permissions.Recipes.Manage)]
    public async Task<IActionResult> GetRecipe(Guid productId, CancellationToken ct)
        => FromResult(await recipes.GetAsync(productId, ct));
}
