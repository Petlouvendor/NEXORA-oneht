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

/// <summary>Site de rattachement d'un entrepôt ou d'un emplacement : <paramref name="Kind"/> = establishment | store.</summary>
public sealed record StockSiteDto(Guid Id, string Kind, string Name, string Code, Guid? EstablishmentId, string Status);

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

// --- Tableau de stock (finalisation produit, phase 2) ------------------------

/// <summary>
/// Ligne du tableau de stock (article × emplacement). <paramref name="StockStatus"/> est calculé par le
/// serveur : <c>out</c> (disponible ≤ 0), <c>low</c> (≤ point de commande ou &lt; minimum), <c>over</c>
/// (&gt; maximum défini), sinon <c>normal</c>. Seuils : règle de l'emplacement, sinon règle générale.
/// </summary>
public sealed record StockOverviewRowDto(
    Guid ProductId, string Sku, string ProductName, string? CategoryName, string? UnitCode,
    Guid LocationId, string LocationName,
    decimal OnHand, decimal Reserved, decimal Available,
    decimal? MinQuantity, decimal? ReorderPoint, decimal? MaxQuantity,
    decimal AverageCost, decimal StockValue, DateTimeOffset? LastMovementAt, DateOnly? NearestExpiry,
    string StockStatus);

/// <summary><paramref name="ValuationVisible"/> = false : coûts et valeurs masqués (0) faute de
/// <c>inventory.view_valuation</c> — le masquage est fait par le serveur, pas par l'écran.</summary>
public sealed record StockOverviewDto(
    IReadOnlyList<StockOverviewRowDto> Rows, int Total, decimal TotalValue,
    int OutCount, int LowCount, int OverCount, bool ValuationVisible = true);

// --- Transferts, inventaires, ajustements : lecture (phase 2) ------------------

public sealed record TransferSummaryDto(
    Guid Id, string DocumentNumber, string Status, Guid SourceLocationId, string SourceLocationName,
    Guid DestLocationId, string DestLocationName, int LineCount, decimal QuantityShipped, decimal QuantityReceived,
    decimal Value, DateTimeOffset? ShippedAt, DateTimeOffset? ReceivedAt, string? Notes, string? CreatedByName);

public sealed record TransferLineDto(
    Guid Id, Guid ProductId, string Sku, string ProductName, string? LotNumber,
    decimal QuantityShipped, decimal QuantityReceived, decimal Remaining, decimal UnitCost);

public sealed record TransferDetailDto(TransferSummaryDto Transfer, IReadOnlyList<TransferLineDto> Lines);

public sealed record CountSummaryDto(
    Guid Id, string DocumentNumber, string CountType, string Status, Guid LocationId, string LocationName,
    int LineCount, int CountedLines, int VarianceLines, decimal VarianceValue,
    DateTimeOffset? StartedAt, DateTimeOffset? ValidatedAt, string? ValidatedByName, string? Notes);

public sealed record CountLineDto(
    Guid Id, Guid ProductId, string Sku, string ProductName, string? UnitCode, string? LotNumber,
    decimal SystemQuantity, decimal? CountedQuantity, decimal? Variance, decimal UnitCost, decimal? VarianceValue, string? Note,
    Guid? LotId = null);

public sealed record CountDetailDto(CountSummaryDto Count, IReadOnlyList<CountLineDto> Lines);

public sealed record AdjustmentSummaryDto(
    Guid Id, string DocumentNumber, string AdjustmentType, string Status, Guid LocationId, string LocationName,
    string? Reason, int LineCount, decimal QuantityDelta, decimal Value, DateTimeOffset OccurredAt,
    string? SourceType, Guid? SourceId, Guid? JournalEntryId, string? CreatedByName);

public sealed record AdjustmentLineDto(
    Guid Id, Guid ProductId, string Sku, string ProductName, string? LotNumber,
    decimal QuantityDelta, decimal UnitCost, decimal Value, string? Note);

public sealed record AdjustmentDetailDto(AdjustmentSummaryDto Adjustment, IReadOnlyList<AdjustmentLineDto> Lines);
