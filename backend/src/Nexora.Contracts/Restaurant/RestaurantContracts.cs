namespace Nexora.Contracts.Restaurant;

public sealed record RestaurantAreaDto(Guid Id, string Name, string Code, string Status);

public sealed record RestaurantTableDto(
    Guid Id, string Name, string Code, int Seats, Guid? AreaId, string Status, Guid? CurrentSaleId);

public sealed record CreateAreaRequest(Guid EstablishmentId, string Name, string Code);
public sealed record CreateTableRequest(Guid EstablishmentId, Guid? AreaId, string Name, string Code, int Seats);

public sealed record OrderItemInput(Guid ProductId, decimal Quantity, decimal LineDiscount, IReadOnlyList<string>? Modifiers);

public sealed record PlaceOrderRequest(
    Guid TableId, int Covers, Guid? WaiterUserId, string? Notes,
    IReadOnlyList<OrderItemInput> Items);

public sealed record KitchenAdvanceRequest(Guid SaleId, string TargetStatus);   // preparing | ready | served

public sealed record CloseOrderRequest(
    Guid SaleId,
    IReadOnlyList<OrderTenderInput> Tenders,
    decimal AmountTendered,
    decimal TipAmount,
    Guid? CashSessionId);

public sealed record OrderTenderInput(Guid FinancialAccountId, Guid? PaymentMethodId, decimal Amount, string? Reference);

public sealed record OrderLineDto(Guid ProductId, string Description, decimal Quantity, decimal UnitPrice, decimal LineTotal);

public sealed record RestaurantOrderDto(
    Guid SaleId, string DocumentNumber, Guid TableId, string TableName,
    int Covers, string KitchenStatus, decimal RunningTotal,
    IReadOnlyList<OrderLineDto> Lines);

// --- Recettes / nomenclatures (§6, §11) --------------------------------

public sealed record RecipeComponentInput(
    Guid ComponentProductId, decimal Quantity, Guid UnitId, decimal WastagePercent, string? KitchenStation);

public sealed record DefineRecipeRequest(
    Guid ProductId, string Name, Guid YieldUnitId, decimal YieldQuantity,
    IReadOnlyList<RecipeComponentInput> Components);

public sealed record RecipeDto(
    Guid Id, Guid ProductId, string Name, decimal YieldQuantity, decimal EstimatedCost,
    IReadOnlyList<RecipeComponentDto> Components);

public sealed record RecipeComponentDto(
    Guid ComponentProductId, string ComponentName, decimal Quantity, decimal WastagePercent, string? KitchenStation);
