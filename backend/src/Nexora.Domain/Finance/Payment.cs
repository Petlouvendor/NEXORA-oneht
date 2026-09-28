using Nexora.Domain.Common;

namespace Nexora.Domain.Finance;

public enum PaymentDirection { Inbound, Outbound }

/// <summary>Paiement — objet financier générique (Phase 4 §15) — table nexora.payment.</summary>
public sealed class Payment : AuditableEntity
{
    public Guid CompanyId { get; set; }
    public string PaymentNumber { get; set; } = string.Empty;
    public PaymentDirection Direction { get; set; }
    public string? PartyType { get; set; }
    public Guid? CustomerId { get; set; }
    public Guid? SupplierId { get; set; }
    public Guid? PaymentMethodId { get; set; }
    public Guid? FinancialAccountId { get; set; }
    public Guid? CashSessionId { get; set; }
    public Guid CurrencyId { get; set; }
    public Guid? ExchangeRateId { get; set; }
    public decimal Amount { get; set; }
    public decimal AllocatedAmount { get; set; }
    public string? Reference { get; set; }
    public string Status { get; set; } = "completed";
    public DateTimeOffset ReceivedAt { get; set; }
    public Guid? SourceOperationUuid { get; set; }
    public Guid? CreatedBy { get; set; }
    /// <summary>Motif / commentaire du versement (module clients §9).</summary>
    public string? Note { get; set; }

    public decimal UnallocatedAmount => Amount - AllocatedAmount;

    public List<PaymentAllocation> Allocations { get; set; } = [];
}

/// <summary>Affectation d'un paiement à une créance OU une dette — table nexora.payment_allocation.</summary>
public sealed class PaymentAllocation : Entity
{
    public Guid CompanyId { get; set; }
    public Guid PaymentId { get; set; }
    public Guid? ReceivableId { get; set; }
    public Guid? PayableId { get; set; }
    public decimal AllocatedAmount { get; set; }
    public decimal FxDifference { get; set; }
    public Guid CurrencyId { get; set; }
    public DateTimeOffset AllocatedAt { get; set; }
    public Guid? CreatedBy { get; set; }
}

public static class ReceivableStatus
{
    public const string Open = "OPEN";
    public const string PartiallyPaid = "PARTIALLY_PAID";
    public const string Paid = "PAID";
    public const string Overdue = "OVERDUE";
    public const string Cancelled = "CANCELLED";
    public const string InDispute = "IN_DISPUTE";
    public const string WrittenOff = "WRITTEN_OFF";
}

/// <summary>Origine d'un crédit client (<c>customer_receivable.source_type</c>, module clients §8).</summary>
public static class ReceivableSource
{
    public const string Sale = "sale";
    public const string CustomerExpense = "customer_expense";
    public const string Opening = "opening";
}

/// <summary>Créance client — table nexora.customer_receivable (Phase 4 §13).</summary>
public sealed class CustomerReceivable : AuditableEntity
{
    public Guid CompanyId { get; set; }
    public Guid? SaleId { get; set; }
    public Guid CustomerId { get; set; }
    public Guid CurrencyId { get; set; }
    public decimal OriginalAmount { get; set; }
    public decimal PaidAmount { get; set; }
    public DateOnly IssueDate { get; set; }
    public DateOnly? DueDate { get; set; }
    public string Status { get; set; } = ReceivableStatus.Open;
    /// <summary>NULL = déduit par la base (vente si <see cref="SaleId"/>, sinon manuel) ; le numéro de crédit
    /// (<c>CRD-…</c>) est toujours attribué par la base.</summary>
    public string? SourceType { get; set; }
    public Guid? SourceId { get; set; }

    public decimal RemainingAmount => OriginalAmount - PaidAmount;
}

/// <summary>Dette fournisseur — table nexora.supplier_payable (Phase 4 §14).</summary>
public sealed class SupplierPayable : AuditableEntity
{
    public Guid CompanyId { get; set; }
    public Guid? SupplierInvoiceId { get; set; }
    public Guid SupplierId { get; set; }
    public Guid CurrencyId { get; set; }
    public decimal OriginalAmount { get; set; }
    public decimal PaidAmount { get; set; }
    /// <summary>Avoirs : retours fournisseur comptabilisés imputés sur la dette (V082, tenu par la base).</summary>
    public decimal CreditedAmount { get; set; }
    public DateOnly IssueDate { get; set; }
    public DateOnly? DueDate { get; set; }
    public string Status { get; set; } = ReceivableStatus.Open;

    public decimal RemainingAmount => OriginalAmount - PaidAmount - CreditedAmount;
}
