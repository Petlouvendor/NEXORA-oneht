using Nexora.Contracts.Purchasing;

namespace Nexora.Application.Purchasing;

/// <summary>Lectures achats : dettes fournisseurs, relevé, commande (Phase 26 §24, §36, §46).</summary>
public interface IPurchasingQueries
{
    Task<IReadOnlyList<PayableDto>> ListPayablesAsync(Guid? supplierId, bool onlyOpen, int limit, CancellationToken ct = default);
    Task<SupplierStatementDto?> SupplierStatementAsync(Guid supplierId, DateOnly? from, DateOnly? to, CancellationToken ct = default);
    Task<PurchaseOrderDto?> GetPurchaseOrderAsync(Guid orderId, CancellationToken ct = default);
    Task<IReadOnlyList<SupplierSummaryDto>> ListSuppliersAsync(CancellationToken ct = default);
    Task<IReadOnlyList<PurchaseOrderSummaryDto>> ListPurchaseOrdersAsync(string? status, int limit, CancellationToken ct = default);
}

public sealed record SupplierSummaryDto(Guid Id, string SupplierNumber, string Name, string Status, string Currency, decimal OpenPayables);
public sealed record PurchaseOrderSummaryDto(
    Guid Id, string DocumentNumber, string Status, Guid SupplierId, string SupplierName,
    string Currency, System.DateOnly OrderDate, decimal Total, decimal ReceivedRatio);
