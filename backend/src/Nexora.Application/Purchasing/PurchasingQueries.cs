using Nexora.Contracts.Purchasing;
using Nexora.Domain.Common;

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

/// <summary>
/// Lectures du cycle d'achat (finalisation produit, phase 3) et conditions commerciales fournisseur :
/// commande détaillée (unités d'achat, taxes, réceptions, factures), réceptions, factures, retours,
/// synthèse fournisseur avec historique des prix, conditions de paiement.
/// </summary>
public interface IPurchasingDocumentQueries
{
    Task<Nexora.Contracts.Purchasing.PurchaseOrderDetailDto?> OrderAsync(Guid id, CancellationToken ct = default);
    Task<IReadOnlyList<Nexora.Contracts.Purchasing.GoodsReceiptSummaryDto>> ReceiptsAsync(Guid? supplierId, Guid? purchaseOrderId, int limit, CancellationToken ct = default);
    Task<Nexora.Contracts.Purchasing.GoodsReceiptDetailDto?> ReceiptAsync(Guid id, CancellationToken ct = default);
    Task<IReadOnlyList<Nexora.Contracts.Purchasing.SupplierInvoiceSummaryDto>> InvoicesAsync(Guid? supplierId, string? status, int limit, CancellationToken ct = default);
    Task<Nexora.Contracts.Purchasing.SupplierInvoiceDetailDto?> InvoiceAsync(Guid id, CancellationToken ct = default);
    Task<IReadOnlyList<Nexora.Contracts.Purchasing.SupplierReturnSummaryDto>> ReturnsAsync(Guid? supplierId, int limit, CancellationToken ct = default);
    Task<Nexora.Contracts.Purchasing.SupplierOverviewDto?> SupplierOverviewAsync(Guid supplierId, CancellationToken ct = default);
    Task<IReadOnlyList<Nexora.Contracts.Purchasing.SupplierPaymentTermsDto>> PaymentTermsAsync(CancellationToken ct = default);
}

/// <summary>Conditions de paiement et conditions commerciales des fournisseurs (écritures auditées).</summary>
public interface ISupplierTermsService
{
    Task<Result<Guid>> CreatePaymentTermsAsync(Nexora.Contracts.Purchasing.SavePaymentTermsRequest request, CancellationToken ct = default);
    Task<Result> SetSupplierTermsAsync(Guid supplierId, Nexora.Contracts.Purchasing.SupplierTermsRequest request, CancellationToken ct = default);
}
