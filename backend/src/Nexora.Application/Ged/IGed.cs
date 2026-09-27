using Nexora.Contracts.Ged;
using Nexora.Domain.Common;

namespace Nexora.Application.Ged;

/// <summary>
/// Bibliothèque documentaire (Phase 45 §3-6, §13-18, §27, §31). Distincte de
/// <see cref="Documents.IDocumentService"/> (Phase 30 — documents ÉMIS par NEXORA, numérotés,
/// figés) et de <see cref="Mobile.IAttachmentService"/> (Phase 44 — blob minimal terrain) :
/// <c>document</c>/<c>document_version</c> couvrent les pièces REÇUES/IMPORTÉES/SCANNÉES, avec
/// classification, versions, liens multiples (§6), verrouillage (§18) et cycle
/// brouillon→revue→approbation→[signature]→archive (§24).
/// </summary>
public interface IGedService
{
    /// <summary>Crée le document avec sa première version — §39 : <c>UploadResultDto.PotentialDuplicateOf</c>
    /// signale un contenu déjà connu dans l'entreprise (même hash), jamais bloquant.</summary>
    Task<Result<UploadResultDto>> CreateAsync(CreateDocumentRequest request, CancellationToken ct = default);
    Task<Result<GedDocumentDto>> GetAsync(Guid id, CancellationToken ct = default);
    Task<IReadOnlyList<GedDocumentDto>> SearchAsync(DocumentSearchQuery query, CancellationToken ct = default);
    Task<Result<GedDocumentDto>> UpdateMetadataAsync(Guid id, UpdateDocumentMetadataRequest request, CancellationToken ct = default);

    /// <summary>§16 : nouvelle version, jamais un écrasement — §39 : même détection de doublon qu'à la création.</summary>
    Task<Result<UploadResultDto>> AddVersionAsync(Guid id, AddDocumentVersionRequest request, CancellationToken ct = default);
    Task<IReadOnlyList<DocumentVersionDto>> ListVersionsAsync(Guid id, CancellationToken ct = default);

    /// <summary>§15 aperçu/téléchargement — <paramref name="versionNo"/> nul = version active.</summary>
    Task<Result<(byte[] Content, string ContentType)>> GetVersionContentAsync(Guid id, int? versionNo, CancellationToken ct = default);

    /// <summary>§24 : brouillon → revue → approbation → archive. Approbateur ≠ demandeur (§23, comme Phase 11).</summary>
    Task<Result<GedDocumentDto>> SubmitForReviewAsync(Guid id, CancellationToken ct = default);
    Task<Result<GedDocumentDto>> ApproveAsync(Guid id, CancellationToken ct = default);
    Task<Result<GedDocumentDto>> RejectAsync(Guid id, string reason, CancellationToken ct = default);

    /// <summary>§18 : un document en verrouillage métier n'accepte plus de nouvelle version ni de modification.</summary>
    Task<Result<GedDocumentDto>> LockAsync(Guid id, string reason, CancellationToken ct = default);
    Task<Result<GedDocumentDto>> UnlockAsync(Guid id, CancellationToken ct = default);

    Task<Result<GedDocumentDto>> ArchiveAsync(Guid id, CancellationToken ct = default);
    Task<Result<GedDocumentDto>> RestoreAsync(Guid id, CancellationToken ct = default);

    /// <summary>§30 : suppression contrôlée — bloquée par <c>legal_hold</c> ou <c>retention_until</c> non échue ;
    /// confirmation à 4 yeux (confirmeur ≠ demandeur).</summary>
    Task<Result> RequestDeletionAsync(Guid id, string reason, CancellationToken ct = default);
    Task<Result> ConfirmDeletionAsync(Guid id, CancellationToken ct = default);
    Task<Result> CancelDeletionRequestAsync(Guid id, CancellationToken ct = default);

    /// <summary>§6 : un document peut être lié à plusieurs objets métier, en plus de son lien principal.</summary>
    Task<Result<DocumentLinkDto>> AddLinkAsync(Guid id, AddDocumentLinkRequest request, CancellationToken ct = default);
    Task<IReadOnlyList<DocumentLinkDto>> ListLinksAsync(Guid id, CancellationToken ct = default);

    /// <summary>§31 : lien temporaire, mot de passe optionnel, révocable — jamais un accès permanent.</summary>
    Task<Result<DocumentShareDto>> ShareAsync(Guid id, CreateShareRequest request, CancellationToken ct = default);
    Task<Result> RevokeShareAsync(Guid shareId, CancellationToken ct = default);

    /// <summary>Accès public par jeton (§31) — pas d'authentification NEXORA, seulement le jeton (+ mot de passe si défini).</summary>
    Task<Result<(byte[] Content, string ContentType, string DocumentName)>> GetSharedContentAsync(string token, string? password, CancellationToken ct = default);
}

/// <summary>
/// Signature multi-signataires (Phase 45 §19-22). Signature simple horodatée avec preuve
/// (identité déclarée, authentification NEXORA, hash du document, journal) — jamais une
/// qualification juridique automatique (§19, avertissement explicite du cahier des charges).
/// </summary>
public interface ISignatureService
{
    Task<Result<SignatureRequestDto>> RequestAsync(Guid documentId, CreateSignatureRequestRequest request, CancellationToken ct = default);
    Task<Result<SignatureRequestDto>> GetAsync(Guid id, CancellationToken ct = default);
    Task<IReadOnlyList<SignatureRequestDto>> ListAsync(string? status, CancellationToken ct = default);

    /// <summary>Signe pour le signataire courant — l'ordre (§21) est respecté : un signataire ne peut
    /// signer que si tous ceux d'un rang antérieur ont déjà signé.</summary>
    Task<Result<SignatureRequestDto>> SignAsync(Guid requestId, Guid signerId, SignDocumentRequest request, CancellationToken ct = default);
    Task<Result<SignatureRequestDto>> DeclineAsync(Guid requestId, Guid signerId, string reason, CancellationToken ct = default);
    Task<Result> CancelAsync(Guid requestId, string reason, CancellationToken ct = default);

    /// <summary>Expire les demandes dont l'échéance est dépassée — appelé par le planificateur.</summary>
    Task<int> ExpireDueAsync(CancellationToken ct = default);
}

/// <summary>
/// OCR (Phase 45 §11-12, §38). NEXORA ONE n'embarque aucun moteur OCR tiers cette phase :
/// l'extraction reste un heuristique minimal (texte déjà lisible dans le fichier), jamais une
/// vraie reconnaissance d'image — backlog explicite. Les champs proposés doivent toujours être
/// validés par un humain avant d'alimenter une facture/un achat (§11).
/// </summary>
public interface IDocumentOcrService
{
    Task<Result<OcrResultDto>> ExtractAsync(Guid documentId, int? versionNo, CancellationToken ct = default);
}

/// <summary>
/// Rétention DÉDIÉE aux documents métier (Phase 45 §27-29) — distincte de
/// <c>Operations.IRetentionPurgeService</c> (Phase 41), qui exclut explicitement toute donnée
/// métier/comptable. Un document sous <c>legal_hold</c> ou dont la rétention n'est pas échue
/// n'est jamais éligible à la suppression, quelle que soit la politique générique.
/// </summary>
public interface IDocumentRetentionService
{
    Task<IReadOnlyList<DocumentRetentionPolicyDto>> ListPoliciesAsync(CancellationToken ct = default);
    Task<Result<DocumentRetentionPolicyDto>> SavePolicyAsync(SaveDocumentRetentionPolicyRequest request, CancellationToken ct = default);
    Task<Result> SetLegalHoldAsync(Guid documentId, bool hold, string? reason, CancellationToken ct = default);
    Task<IReadOnlyList<GedDocumentDto>> ListEligibleForDeletionAsync(CancellationToken ct = default);

    /// <summary>Calcule/rafraîchit <c>document.retention_until</c> selon la politique effective — appelé par le planificateur.</summary>
    Task<int> ApplyRetentionAsync(CancellationToken ct = default);
}

/// <summary>Observabilité GED (§45) — ne fait que lire <c>audit_log</c> (immuable, Phase 11), filtré aux tables de ce module.</summary>
public interface IGedAuditQuery
{
    Task<IReadOnlyList<GedAuditEntryDto>> RecentAsync(Guid? documentId, int limit, CancellationToken ct = default);
}
