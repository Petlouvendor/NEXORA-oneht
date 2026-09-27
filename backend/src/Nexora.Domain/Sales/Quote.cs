using Nexora.Domain.Common;

namespace Nexora.Domain.Sales;

public static class QuoteStatus
{
    public const string Draft = "draft";
    public const string Sent = "sent";
    public const string Accepted = "accepted";
    public const string Rejected = "rejected";
    public const string Expired = "expired";
    public const string Converted = "converted";
    public const string Cancelled = "cancelled";

    /// <summary>Cycle du devis (Phase 30 §5) : brouillon → envoyé → accepté/refusé/expiré → commande.</summary>
    public static bool CanTransition(string from, string to) => (from, to) switch
    {
        (Draft, Sent) or (Draft, Cancelled) => true,
        (Sent, Accepted) or (Sent, Rejected) or (Sent, Expired) or (Sent, Cancelled) => true,
        (Accepted, Converted) or (Accepted, Cancelled) => true,
        _ => false,
    };
}

/// <summary>Devis — table nexora.quote (Phase 30 §4-5).</summary>
public sealed class Quote : AuditableEntity
{
    public Guid CompanyId { get; set; }
    public Guid? EstablishmentId { get; set; }
    public Guid? CustomerId { get; set; }
    public string DocumentNumber { get; set; } = string.Empty;
    public Guid CurrencyId { get; set; }
    public string Status { get; set; } = QuoteStatus.Draft;
    public int Version { get; set; } = 1;
    public DateOnly QuoteDate { get; set; }
    public DateOnly? ValidUntil { get; set; }
    public decimal Subtotal { get; set; }
    public decimal DiscountTotal { get; set; }
    public decimal TaxTotal { get; set; }
    public decimal Total { get; set; }
    public string? Terms { get; set; }
    public string? Notes { get; set; }
    public DateTimeOffset? SentAt { get; set; }
    public DateTimeOffset? DecidedAt { get; set; }
    public Guid? ConvertedOrderId { get; set; }
    public Guid? CreatedBy { get; set; }

    public List<QuoteLine> Lines { get; set; } = [];
}

public sealed class QuoteLine : AuditableEntity
{
    public Guid QuoteId { get; set; }
    public Guid CompanyId { get; set; }
    public Guid ProductId { get; set; }
    public string Description { get; set; } = string.Empty;
    public decimal Quantity { get; set; }
    public decimal UnitPrice { get; set; }
    public decimal Discount { get; set; }
    public Guid? TaxId { get; set; }
    public decimal TaxAmount { get; set; }
    public decimal LineTotal { get; set; }
    public int LineNumber { get; set; } = 1;
}
