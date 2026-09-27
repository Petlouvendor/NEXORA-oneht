using Nexora.Contracts.Restaurant;
using Nexora.Domain.Common;

namespace Nexora.Application.Restaurant;

/// <summary>
/// Module Restaurant (Phase 8 §6, §24). TABLE → COMMANDE → KDS → SERVICE → PAIEMENT.
/// Le paiement, le stock, la finance et la comptabilité restent ceux du noyau (§22) :
/// la clôture d'une commande passe par <c>SalesService.CheckoutAsync</c>.
/// </summary>
public interface IRestaurantService
{
    Task<Result<Guid>> CreateAreaAsync(CreateAreaRequest request, CancellationToken ct = default);
    Task<Result<Guid>> CreateTableAsync(CreateTableRequest request, CancellationToken ct = default);
    Task<IReadOnlyList<RestaurantTableDto>> ListTablesAsync(CancellationToken ct = default);

    Task<Result<RestaurantOrderDto>> PlaceOrderAsync(PlaceOrderRequest request, CancellationToken ct = default);
    Task<Result> AdvanceKitchenAsync(KitchenAdvanceRequest request, CancellationToken ct = default);
    Task<Result<RestaurantOrderDto>> GetOrderAsync(Guid saleId, CancellationToken ct = default);
    Task<IReadOnlyList<RestaurantOrderDto>> KitchenBoardAsync(CancellationToken ct = default);
    Task<Result<Nexora.Application.Sales.CheckoutResult>> CloseOrderAsync(CloseOrderRequest request, CancellationToken ct = default);
}

/// <summary>Recettes / nomenclatures partagées restaurant &amp; production (§6, §11).</summary>
public interface IRecipeService
{
    Task<Result<Guid>> DefineAsync(DefineRecipeRequest request, CancellationToken ct = default);
    Task<Result<RecipeDto>> GetAsync(Guid productId, CancellationToken ct = default);
}
