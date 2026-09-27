namespace Nexora.Contracts.Inventory;

// --- Entrepôts & emplacements (Phase 24 §4-6) ---------------------------

public sealed record WarehouseDto(
    Guid Id, string Name, string Code, string Type, string Status,
    Guid EstablishmentId, string? Address, decimal? Capacity, Guid? ManagerUserId);

public sealed record CreateWarehouseRequest(
    string Name, string Code, string Type, Guid EstablishmentId, string? Address, decimal? Capacity);

public sealed record LocationDto(
    Guid Id, string Name, string? Code, string LocationType, string Status,
    Guid? WarehouseId, Guid? StoreId, string? Zone, string? Aisle, string? Shelf, string? Bin,
    decimal? Capacity);

public sealed record CreateLocationRequest(
    string Name, string? Code, string LocationType, Guid? WarehouseId, Guid? StoreId,
    string? Zone, string? Aisle, string? Shelf, string? Bin, decimal? Capacity);

// --- Soldes & mouvements (Phase 24 §8, §10-11, §38) -------------------

public sealed record StockBalanceDto(
    Guid ProductId, string Sku, string ProductName, Guid LocationId, string LocationName,
    decimal OnHand, decimal Reserved, decimal Available, decimal AverageCost, decimal StockValue);

public sealed record StockMovementDto(
    Guid Id, DateTimeOffset OccurredAt, string MovementType, Guid ProductId, string Sku,
    Guid LocationId, string LocationName, string? LotNumber, decimal Quantity, decimal UnitCost,
    string? ReferenceType, Guid? ReferenceId);

// --- Lots (Phase 24 §12-15) ------------------------------------------

public sealed record LotDto(
    Guid Id, string LotNumber, Guid ProductId, string Sku, string ProductName,
    DateOnly? ExpirationDate, DateOnly? ManufactureDate, DateOnly? ReceivedAt,
    string Status, string ExpiryStatus, decimal OnHand, decimal Reserved,
    Guid? SupplierId, string? SupplierName, string? BlockedReason, string? RecallReference);

public sealed record BlockLotRequest(string Reason);
public sealed record RecallLotRequest(string Reference, string Reason);

public sealed record ExpiringLotDto(
    Guid LotId, string LotNumber, Guid ProductId, string Sku, string ProductName,
    DateOnly ExpirationDate, int DaysToExpiry, string ExpiryStatus, decimal OnHand, decimal StockValue);

public sealed record LotSweepResultDto(int LotsExpired, decimal StockValueWrittenOff, int MovementsPosted);

// --- Alertes stock (Phase 24 §30, §39) -----------------------------

public sealed record StockAlertDto(
    string AlertType, Guid ProductId, string Sku, string ProductName, Guid LocationId,
    string LocationName, decimal OnHand, decimal Available, decimal? Threshold, string Detail);
