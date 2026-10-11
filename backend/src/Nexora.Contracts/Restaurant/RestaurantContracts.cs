namespace Nexora.Contracts.Restaurant;

public sealed record RestaurantAreaDto(Guid Id, string Name, string Code, string Status, Guid? EstablishmentId = null, int TableCount = 0);

/// <summary>Table avec, si elle est occupée, l'état de sa commande (finalisation phase 13).</summary>
public sealed record RestaurantTableDto(
    Guid Id, string Name, string Code, int Seats, Guid? AreaId, string Status, Guid? CurrentSaleId,
    string? AreaName = null, Guid? EstablishmentId = null, int? Covers = null, string? KitchenStatus = null,
    DateTimeOffset? OpenedAt = null, decimal? RunningTotal = null, string? DocumentNumber = null);

public sealed record CreateAreaRequest(Guid EstablishmentId, string Name, string Code);
public sealed record CreateTableRequest(Guid EstablishmentId, Guid? AreaId, string Name, string Code, int Seats);

/// <summary><c>Modifiers</c> : précisions pour la cuisine (« sans oignon », « bien cuit ») — sans incidence sur le prix.</summary>
public sealed record OrderItemInput(Guid ProductId, decimal Quantity, decimal LineDiscount, IReadOnlyList<string>? Modifiers);

public sealed record PlaceOrderRequest(
    Guid TableId, int Covers, Guid? WaiterUserId, string? Notes,
    IReadOnlyList<OrderItemInput> Items);

/// <summary>Phase 13 : articles ajoutés à une commande ouverte (nouvelle « vague » pour la cuisine).</summary>
public sealed record AddOrderItemsRequest(Guid SaleId, IReadOnlyList<OrderItemInput> Items);

/// <summary>Phase 13 : annulation d'une commande non réglée — motif obligatoire, la table est libérée.</summary>
public sealed record CancelOrderRequest(Guid SaleId, string Reason);

public sealed record KitchenAdvanceRequest(Guid SaleId, string TargetStatus);   // preparing | ready | served

public sealed record CloseOrderRequest(
    Guid SaleId,
    IReadOnlyList<OrderTenderInput> Tenders,
    decimal AmountTendered,
    decimal TipAmount,
    Guid? CashSessionId);

public sealed record OrderTenderInput(Guid FinancialAccountId, Guid? PaymentMethodId, decimal Amount, string? Reference);

/// <summary><c>AddedAt</c> : heure d'ajout ; une ligne ajoutée après l'envoi en cuisine est une nouvelle vague.</summary>
public sealed record OrderLineDto(Guid ProductId, string Description, decimal Quantity, decimal UnitPrice, decimal LineTotal,
    Guid? LineId = null, decimal Discount = 0m, IReadOnlyList<string>? Modifiers = null, DateTimeOffset? AddedAt = null);

public sealed record RestaurantOrderDto(
    Guid SaleId, string DocumentNumber, Guid TableId, string TableName,
    int Covers, string KitchenStatus, decimal RunningTotal,
    IReadOnlyList<OrderLineDto> Lines,
    string? Notes = null, DateTimeOffset? OpenedAt = null, DateTimeOffset? FiredAt = null, DateTimeOffset? ReadyAt = null,
    DateTimeOffset? ServedAt = null, string? AreaName = null, Guid? InventoryLocationId = null, string? Currency = null,
    string? WaiterName = null, string? Status = null);

// --- Recettes / nomenclatures (§6, §11) --------------------------------

public sealed record RecipeComponentInput(
    Guid ComponentProductId, decimal Quantity, Guid UnitId, decimal WastagePercent, string? KitchenStation);

public sealed record DefineRecipeRequest(
    Guid ProductId, string Name, Guid YieldUnitId, decimal YieldQuantity,
    IReadOnlyList<RecipeComponentInput> Components);

public sealed record RecipeDto(
    Guid Id, Guid ProductId, string Name, decimal YieldQuantity, decimal EstimatedCost,
    IReadOnlyList<RecipeComponentDto> Components,
    string? ProductName = null, Guid? YieldUnitId = null, decimal? SalePrice = null);

public sealed record RecipeComponentDto(
    Guid ComponentProductId, string ComponentName, decimal Quantity, decimal WastagePercent, string? KitchenStation,
    Guid? UnitId = null, string? UnitCode = null, decimal UnitCost = 0m);

/// <summary>Plats vendables (produits non stockés) et ingrédients (produits stockés), avec unités — saisie des recettes.</summary>
public sealed record RecipeOptionsDto(IReadOnlyList<RecipeProductOption> Dishes, IReadOnlyList<RecipeProductOption> Ingredients,
    IReadOnlyList<RecipeUnitOption> Units);
public sealed record RecipeProductOption(Guid Id, string Name, string? Sku, string ProductType, Guid? UnitId, bool HasRecipe);
public sealed record RecipeUnitOption(Guid Id, string Code, string Name);

/// <summary>Établissements et emplacements de vente de l'entreprise — création de salles et tables.</summary>
public sealed record RestaurantSetupDto(IReadOnlyList<RestaurantEstablishmentOption> Establishments, bool ModuleEnabled);
public sealed record RestaurantEstablishmentOption(Guid Id, string Name, bool HasStockLocation);
