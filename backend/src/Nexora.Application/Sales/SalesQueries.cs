using Nexora.Contracts.Sales;

namespace Nexora.Application.Sales;

/// <summary>Lectures ventes & commandes clients (Phase 25 §9, §43 : <c>GET /sales/{id}</c>, <c>GET /orders/{id}</c>).</summary>
public interface ISalesQueries
{
    Task<SaleDto?> GetSaleAsync(Guid saleId, CancellationToken ct = default);
    Task<OrderDto?> GetOrderAsync(Guid orderId, CancellationToken ct = default);
    Task<IReadOnlyList<OrderSummaryDto>> ListOrdersAsync(string? status, int limit, CancellationToken ct = default);

    /// <summary>Client + créance ouverte d'une vente à crédit — pour un règlement ultérieur (§43).</summary>
    Task<(Guid? CustomerId, Guid? ReceivableId, string Currency)> SaleSettlementTargetAsync(Guid saleId, CancellationToken ct = default);
}
