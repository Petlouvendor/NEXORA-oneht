using System.Text.Json;

namespace Nexora.Contracts.Ged;

// =============================================================================
// GED, documents, archivage & signature (Phase 45 §3-6, §13-31, §39, §45, §49).
// Distinct de ReportingContracts/`issued_document` (Phase 30, documents ÉMIS par NEXORA) et de
// MobileContracts/`attachment` (Phase 44, blob minimal terrain) — `document` est la bibliothèque
// versionnée des pièces REÇUES/IMPORTÉES/SCANNÉES.
// =============================================================================

// --- §3-6, §17-18, §27 Document ----------------------------------------------

/// <summary><c>documentType</c> ∈ invoice_sale|invoice_purchase|receipt|credit_note|quote|
/// purchase_order|delivery_note|contract|accounting_voucher|expense_proof|statement|
/// tax_document|customer_document|supplier_document|internal|photo|scan|other.
/// <c>status</c> ∈ draft|pending_review|approved|signed|archived|frozen.</summary>
public sealed record GedDocumentDto(
    Guid Id, Guid? EstablishmentId, string Name, string DocumentType, string? Category,
    IReadOnlyList<string> Tags, string? ReferenceType, Guid? ReferenceId, Guid? CustomerId, Guid? SupplierId,
    int? FiscalYear, DateOnly? DocumentDate, string Status, int? CurrentVersionNo,
    bool Locked, string? LockedReason, bool LegalHold, string? LegalHoldReason, DateOnly? RetentionUntil,
    DateTimeOffset? DeletionRequestedAt, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);

/// <summary>Crée le document avec sa première version (§7, §16) — le contenu source ne s'écrit jamais sans version.</summary>
public sealed record CreateDocumentRequest(
    string Name, string DocumentType, string? Category, IReadOnlyList<string>? Tags,
    string? ReferenceType, Guid? ReferenceId, Guid? CustomerId, Guid? SupplierId,
    int? FiscalYear, DateOnly? DocumentDate, string ContentBase64, string ContentType);

public sealed record UpdateDocumentMetadataRequest(
    string? Name, string? Category, IReadOnlyList<string>? Tags,
    Guid? CustomerId, Guid? SupplierId, int? FiscalYear, DateOnly? DocumentDate);

/// <summary>§39 : la réponse porte <c>PotentialDuplicateOf</c> quand un autre document de l'entreprise
/// a déjà exactement ce contenu (même hash) — jamais bloquant, une décision humaine (§39 « décision utilisateur »).</summary>
public sealed record UploadResultDto(GedDocumentDto Document, Guid? PotentialDuplicateOf);

// --- §16, §46 Versions --------------------------------------------------------

public sealed record AddDocumentVersionRequest(string ContentBase64, string ContentType, string? ChangeReason);

/// <summary><c>ocrStatus</c> ∈ not_requested|pending|completed|unsupported.</summary>
public sealed record DocumentVersionDto(
    Guid Id, int VersionNo, string ContentType, int SizeBytes, string Sha256Hash,
    string ChangeReason, string OcrStatus, DateTimeOffset CreatedAt);

// --- §6 Liens vers plusieurs objets métier -------------------------------------

public sealed record AddDocumentLinkRequest(string LinkedType, Guid LinkedId);
public sealed record DocumentLinkDto(Guid Id, string LinkedType, Guid LinkedId, DateTimeOffset CreatedAt);

// --- §31 Partage à lien temporaire ----------------------------------------------

public sealed record CreateShareRequest(DateTimeOffset ExpiresAt, bool AllowDownload, string? Password);
public sealed record DocumentShareDto(
    Guid Id, string Token, bool AllowDownload, DateTimeOffset ExpiresAt, DateTimeOffset? RevokedAt, DateTimeOffset CreatedAt);

// --- §13-14 Recherche -------------------------------------------------------------

/// <summary><c>Text</c> cherche dans le nom, la catégorie et le texte OCR de la version courante (§13).</summary>
public sealed record DocumentSearchQuery(
    string? Text = null, string? DocumentType = null, string? Status = null, Guid? CustomerId = null,
    Guid? SupplierId = null, DateOnly? DateFrom = null, DateOnly? DateTo = null,
    IReadOnlyList<string>? Tags = null, bool? SignedOnly = null, int Page = 1, int PageSize = 50);

// --- §11-12, §38 OCR ------------------------------------------------------------

/// <summary><c>status</c> ∈ completed|unsupported — NEXORA ONE n'embarque aucun moteur OCR tiers ;
/// cette extraction reste un heuristique minimal pour le texte déjà lisible (ex. PDF texte), jamais
/// une vraie reconnaissance d'image (§11 backlog explicite). Les champs extraits DOIVENT être
/// validés par un humain avant d'alimenter une opération financière (§11).</summary>
public sealed record OcrResultDto(
    Guid DocumentId, int VersionNo, string Status, string? Text, decimal? Confidence, JsonElement? Extracted);

// --- §19-22 Signature multi-signataires ------------------------------------------

/// <summary><c>signerType</c> ∈ user|external.</summary>
public sealed record CreateSignerInput(
    string SignerType, Guid? UserId, string? ExternalName, string? ExternalEmail, string? Role, DateTimeOffset? DueAt);

public sealed record CreateSignatureRequestRequest(IReadOnlyList<CreateSignerInput> Signers);

/// <summary><c>status</c> ∈ pending|signed|declined.</summary>
public sealed record SignerDto(
    Guid Id, int SequenceNo, string SignerType, Guid? UserId, string? ExternalName, string? ExternalEmail,
    string? Role, string Status, DateTimeOffset? DueAt, DateTimeOffset? SignedAt, string? DeclinedReason);

/// <summary><c>status</c> ∈ pending|completed|cancelled|expired.</summary>
public sealed record SignatureRequestDto(
    Guid Id, Guid DocumentId, string Status, DateTimeOffset? CompletedAt,
    IReadOnlyList<SignerDto> Signers, DateTimeOffset CreatedAt);

/// <summary>§22 « preuve » — l'identité/authentification/date/hash sont capturées côté serveur ;
/// <c>Note</c> est la seule donnée libre que le signataire ajoute.</summary>
public sealed record SignDocumentRequest(string? Note);

// --- §27-29 Rétention dédiée aux documents métier ---------------------------------

/// <summary><c>IsSystemDefault</c> = gabarit plateforme (company_id NULL, comme sector_profile) — jamais
/// modifiable via l'API, seulement surchargé par une politique propre à l'entreprise.</summary>
public sealed record DocumentRetentionPolicyDto(
    Guid Id, bool IsSystemDefault, string? DocumentType, int RetentionYears, string LegalBasis);

public sealed record SaveDocumentRetentionPolicyRequest(string? DocumentType, int RetentionYears, string LegalBasis);

// --- §45 Audit GED ------------------------------------------------------------------

public sealed record GedAuditEntryDto(
    Guid Id, string TableName, string Action, JsonElement? Changes, Guid? UserId, DateTimeOffset OccurredAt);
