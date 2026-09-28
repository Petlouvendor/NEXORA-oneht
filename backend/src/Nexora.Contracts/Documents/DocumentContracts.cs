namespace Nexora.Contracts.Documents;

// =============================================================================
// Facturation & documents commerciaux (Phase 30 §3-5, §8-13, §19, §27-28, §34, §36, §48).
// =============================================================================

// --- Devis (§4-5) ---------------------------------------------------------

public sealed record QuoteLineInput(Guid ProductId, decimal Quantity, decimal? UnitPriceOverride, decimal LineDiscount, Guid? TaxId);

public sealed record CreateQuoteRequest(
    Guid? CustomerId, string Currency, Guid? InventoryLocationId, DateOnly? ValidUntil,
    string? Terms, string? Notes, IReadOnlyList<QuoteLineInput> Lines);

public sealed record QuoteLineDto(
    Guid Id, Guid ProductId, string Description, decimal Quantity, decimal UnitPrice,
    decimal Discount, decimal TaxAmount, decimal LineTotal);

public sealed record QuoteDto(
    Guid Id, string DocumentNumber, string Status, int Version, Guid? CustomerId, string? CustomerName,
    string Currency, DateOnly QuoteDate, DateOnly? ValidUntil,
    decimal Subtotal, decimal DiscountTotal, decimal TaxTotal, decimal Total,
    string? Terms, string? Notes, Guid? ConvertedOrderId, DateTimeOffset? SentAt, DateTimeOffset CreatedAt,
    IReadOnlyList<QuoteLineDto> Lines);

public sealed record QuoteDecisionRequest(string? Reason = null);

/// <summary>Conversion en commande. <c>InventoryLocationId</c> : emplacement qui servira la commande (sans lui,
/// la commande reste en brouillon à compléter).</summary>
public sealed record ConvertQuoteRequest(bool OnCredit = false, DateOnly? DueDate = null, string OrderType = "delivery",
    Guid? InventoryLocationId = null);

// --- Documents émis (§3, §8, §19, §31, §33) ----------------------------

/// <summary>
/// Émet un document à partir d'une source métier. <c>type</c> ∈ invoice | deposit_invoice |
/// final_invoice | credit_note | receipt | delivery_note | proforma. <c>sourceType</c> ∈
/// sale | sale_return | payment | sales_order | quote.
/// </summary>
public sealed record IssueDocumentRequest(string Type, string SourceType, Guid SourceId, string? Language = null);

public sealed record CancelDocumentRequest(string Reason, Guid? ReplacedByDocumentId = null);

public sealed record IssuedDocumentDto(
    Guid Id, string DocumentType, string DocumentNumber, string Status, string Language,
    string? PartyType, Guid? PartyId, string? PartyName,
    string? ReferenceType, Guid? ReferenceId, string Currency,
    decimal TotalNet, decimal TotalTax, decimal Total, DateOnly IssueDate, DateOnly? ValidUntil,
    string? ContentHash, string? VerificationCode, string? CancelReason, Guid? ReplacedByDocumentId,
    DateTimeOffset? IssuedAt, DateTimeOffset CreatedAt);

public sealed record IssuedDocumentDetailDto(IssuedDocumentDto Header, System.Text.Json.JsonElement Content);

/// <summary>Recherche documentaire (§34).</summary>
public sealed record DocumentSearchRequest(
    string? Type = null, string? Status = null, string? Number = null, Guid? PartyId = null,
    DateOnly? From = null, DateOnly? To = null, int Limit = 100,
    // Phase 4 : documents d'une source (vente, commande, devis, retour, paiement).
    string? SourceType = null, Guid? SourceId = null);

/// <summary>Données publiques minimales renvoyées par la vérification QR (§27-28).</summary>
public sealed record DocumentVerificationDto(
    bool Found, string? DocumentType, string? DocumentNumber, string? Status,
    DateOnly? IssueDate, string? CompanyName, decimal? Total, string? Currency, string Message);

// --- Envoi (§36) --------------------------------------------------------

public sealed record SendDocumentRequest(string Channel, string? Recipient, string? Subject, string? Message);

public sealed record DocumentSendDto(
    Guid Id, string Channel, string? Recipient, string? Subject, string Status, string? Error,
    DateTimeOffset? SentAt, DateTimeOffset CreatedAt);

// --- Numérotation (§19-20) --------------------------------------------

public sealed record DocumentSequenceDto(
    Guid Id, string DocumentType, Guid? EstablishmentId, int? Year, string Prefix,
    bool IncludeYear, int Padding, long CurrentNumber, string Status);

// --- Rendu (§25) -------------------------------------------------------

public sealed record RenderedDocument(string ContentType, string Content, string FileName);
