using Asp.Versioning;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Nexora.Api.Security;
using Nexora.Application.Ged;
using Nexora.Application.Security;
using Nexora.Contracts.Ged;

namespace Nexora.Api.Controllers;

/// <summary>
/// GED, documents, archivage & signature (Phase 45 §3-6, §13-31, §39, §45, §49). Distinct de
/// <see cref="DocumentsController"/> (Phase 30 — documents ÉMIS par NEXORA) et de
/// <see cref="MobileController"/> (Phase 44 — pièces jointes terrain) : cette bibliothèque couvre
/// les pièces REÇUES/IMPORTÉES/SCANNÉES, versionnées et gouvernées.
/// </summary>
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}")]
public sealed class GedController(
    IGedService ged, ISignatureService signatures, IDocumentOcrService ocr,
    IDocumentRetentionService retention, IGedAuditQuery audit) : ApiControllerBase
{
    // --- §7, §16, §49 Document & versions -------------------------------------

    [HttpPost("ged-documents")]
    [RequirePermission(Permissions.Ged.Upload)]
    public async Task<IActionResult> Create([FromBody] CreateDocumentRequest request, CancellationToken ct)
        => FromResult(await ged.CreateAsync(request, ct));

    [HttpGet("ged-documents/search")]
    [RequirePermission(Permissions.Ged.View)]
    public async Task<IActionResult> Search([FromQuery] DocumentSearchQuery query, CancellationToken ct)
        => Ok(await ged.SearchAsync(query, ct));

    [HttpGet("ged-documents/{id:guid}")]
    [RequirePermission(Permissions.Ged.View)]
    public async Task<IActionResult> Get(Guid id, CancellationToken ct) => FromResult(await ged.GetAsync(id, ct));

    [HttpPut("ged-documents/{id:guid}")]
    [RequirePermission(Permissions.Ged.Manage)]
    public async Task<IActionResult> UpdateMetadata(Guid id, [FromBody] UpdateDocumentMetadataRequest request, CancellationToken ct)
        => FromResult(await ged.UpdateMetadataAsync(id, request, ct));

    [HttpPost("ged-documents/{id:guid}/versions")]
    [RequirePermission(Permissions.Ged.Upload)]
    public async Task<IActionResult> AddVersion(Guid id, [FromBody] AddDocumentVersionRequest request, CancellationToken ct)
        => FromResult(await ged.AddVersionAsync(id, request, ct));

    [HttpGet("ged-documents/{id:guid}/versions")]
    [RequirePermission(Permissions.Ged.View)]
    public async Task<IActionResult> Versions(Guid id, CancellationToken ct) => Ok(await ged.ListVersionsAsync(id, ct));

    [HttpGet("ged-documents/{id:guid}/content")]
    [RequirePermission(Permissions.Ged.View)]
    public async Task<IActionResult> Content(Guid id, [FromQuery] int? versionNo, CancellationToken ct)
    {
        var result = await ged.GetVersionContentAsync(id, versionNo, ct);
        return result.IsSuccess ? File(result.Value!.Content, result.Value.ContentType) : FromResult(result);
    }

    // --- §23-24 Workflow ---------------------------------------------------------

    /// <summary>Documents en attente d'une action de revue (§24).</summary>
    [HttpGet("document-workflows")]
    [RequirePermission(Permissions.Ged.View)]
    public async Task<IActionResult> Workflows(CancellationToken ct)
        => Ok(await ged.SearchAsync(new DocumentSearchQuery(Status: "pending_review", PageSize: 200), ct));

    [HttpPost("document-workflows")]
    [RequirePermission(Permissions.Ged.Manage)]
    public async Task<IActionResult> SubmitForReview([FromBody] Guid documentId, CancellationToken ct)
        => FromResult(await ged.SubmitForReviewAsync(documentId, ct));

    [HttpPost("ged-documents/{id:guid}/approve")]
    [RequirePermission(Permissions.Ged.Manage)]
    public async Task<IActionResult> Approve(Guid id, CancellationToken ct) => FromResult(await ged.ApproveAsync(id, ct));

    [HttpPost("ged-documents/{id:guid}/reject")]
    [RequirePermission(Permissions.Ged.Manage)]
    public async Task<IActionResult> Reject(Guid id, [FromBody] string reason, CancellationToken ct)
        => FromResult(await ged.RejectAsync(id, reason, ct));

    // --- §18 Verrouillage ------------------------------------------------------------

    [HttpPost("ged-documents/{id:guid}/lock")]
    [RequirePermission(Permissions.Ged.Manage)]
    public async Task<IActionResult> Lock(Guid id, [FromBody] string reason, CancellationToken ct) => FromResult(await ged.LockAsync(id, reason, ct));

    [HttpPost("ged-documents/{id:guid}/unlock")]
    [RequirePermission(Permissions.Ged.Manage)]
    public async Task<IActionResult> Unlock(Guid id, CancellationToken ct) => FromResult(await ged.UnlockAsync(id, ct));

    // --- §27 Archivage -------------------------------------------------------------------

    [HttpPost("ged-documents/{id:guid}/archive")]
    [RequirePermission(Permissions.Ged.Manage)]
    public async Task<IActionResult> Archive(Guid id, CancellationToken ct) => FromResult(await ged.ArchiveAsync(id, ct));

    [HttpPost("ged-documents/{id:guid}/restore")]
    [RequirePermission(Permissions.Ged.Manage)]
    public async Task<IActionResult> Restore(Guid id, CancellationToken ct) => FromResult(await ged.RestoreAsync(id, ct));

    // --- §30 Suppression contrôlée --------------------------------------------------------

    [HttpPost("ged-documents/{id:guid}/request-deletion")]
    [RequirePermission(Permissions.Ged.ManageRetention)]
    public async Task<IActionResult> RequestDeletion(Guid id, [FromBody] string reason, CancellationToken ct)
        => FromResult(await ged.RequestDeletionAsync(id, reason, ct));

    [HttpPost("ged-documents/{id:guid}/confirm-deletion")]
    [RequirePermission(Permissions.Ged.ManageRetention)]
    public async Task<IActionResult> ConfirmDeletion(Guid id, CancellationToken ct) => FromResult(await ged.ConfirmDeletionAsync(id, ct));

    [HttpPost("ged-documents/{id:guid}/cancel-deletion")]
    [RequirePermission(Permissions.Ged.ManageRetention)]
    public async Task<IActionResult> CancelDeletion(Guid id, CancellationToken ct) => FromResult(await ged.CancelDeletionRequestAsync(id, ct));

    // --- §6 Liens ------------------------------------------------------------------------

    [HttpPost("ged-documents/{id:guid}/links")]
    [RequirePermission(Permissions.Ged.Manage)]
    public async Task<IActionResult> AddLink(Guid id, [FromBody] AddDocumentLinkRequest request, CancellationToken ct)
        => FromResult(await ged.AddLinkAsync(id, request, ct));

    [HttpGet("ged-documents/{id:guid}/links")]
    [RequirePermission(Permissions.Ged.View)]
    public async Task<IActionResult> Links(Guid id, CancellationToken ct) => Ok(await ged.ListLinksAsync(id, ct));

    // --- §31 Partage -------------------------------------------------------------------

    [HttpPost("ged-documents/{id:guid}/share")]
    [RequirePermission(Permissions.Ged.Manage)]
    public async Task<IActionResult> Share(Guid id, [FromBody] CreateShareRequest request, CancellationToken ct)
        => FromResult(await ged.ShareAsync(id, request, ct));

    [HttpPost("document-shares/{shareId:guid}/revoke")]
    [RequirePermission(Permissions.Ged.Manage)]
    public async Task<IActionResult> RevokeShare(Guid shareId, CancellationToken ct) => FromResult(await ged.RevokeShareAsync(shareId, ct));

    /// <summary>Accès public par jeton (§31) — aucune permission NEXORA, seulement le jeton (+ mot de passe si défini).</summary>
    [AllowAnonymous]
    [HttpGet("shared/{token}")]
    public async Task<IActionResult> GetShared(string token, [FromQuery] string? password, CancellationToken ct)
    {
        var result = await ged.GetSharedContentAsync(token, password, ct);
        return result.IsSuccess ? File(result.Value!.Content, result.Value.ContentType, result.Value.DocumentName) : FromResult(result);
    }

    // --- §11-12, §38 OCR ---------------------------------------------------------------

    [HttpPost("ged-documents/ocr")]
    [RequirePermission(Permissions.Ged.Upload)]
    public async Task<IActionResult> Ocr([FromQuery] Guid documentId, [FromQuery] int? versionNo, CancellationToken ct)
        => FromResult(await ocr.ExtractAsync(documentId, versionNo, ct));

    // --- §19-22 Signature ----------------------------------------------------------------

    [HttpPost("ged-documents/{id:guid}/signature")]
    [RequirePermission(Permissions.Ged.Sign)]
    public async Task<IActionResult> RequestSignature(Guid id, [FromBody] CreateSignatureRequestRequest request, CancellationToken ct)
        => FromResult(await signatures.RequestAsync(id, request, ct));

    [HttpGet("signature-requests")]
    [RequirePermission(Permissions.Ged.View)]
    public async Task<IActionResult> SignatureRequests([FromQuery] string? status, [FromQuery] bool mine = false,
        [FromQuery] Guid? documentId = null, CancellationToken ct = default)
        => Ok(await signatures.ListAsync(status, mine, documentId, ct));

    /// <summary>Phase 10 : utilisateurs pouvant être désignés signataires.</summary>
    [HttpGet("signature-candidates")]
    [RequirePermission(Permissions.Ged.Sign)]
    public async Task<IActionResult> SignatureCandidates(CancellationToken ct) => Ok(await signatures.CandidatesAsync(ct));

    [HttpGet("signature-requests/{id:guid}")]
    [RequirePermission(Permissions.Ged.View)]
    public async Task<IActionResult> SignatureRequest(Guid id, CancellationToken ct) => FromResult(await signatures.GetAsync(id, ct));

    [HttpPost("signature-requests/{id:guid}/signers/{signerId:guid}/sign")]
    [RequirePermission(Permissions.Ged.Sign)]
    public async Task<IActionResult> Sign(Guid id, Guid signerId, [FromBody] SignDocumentRequest request, CancellationToken ct)
        => FromResult(await signatures.SignAsync(id, signerId, request, ct));

    [HttpPost("signature-requests/{id:guid}/signers/{signerId:guid}/decline")]
    [RequirePermission(Permissions.Ged.Sign)]
    public async Task<IActionResult> Decline(Guid id, Guid signerId, [FromBody] string reason, CancellationToken ct)
        => FromResult(await signatures.DeclineAsync(id, signerId, reason, ct));

    [HttpPost("signature-requests/{id:guid}/cancel")]
    [RequirePermission(Permissions.Ged.Sign)]
    public async Task<IActionResult> CancelSignature(Guid id, [FromBody] string reason, CancellationToken ct)
        => FromResult(await signatures.CancelAsync(id, reason, ct));

    // --- §27-29 Rétention & legal hold -----------------------------------------------------

    [HttpGet("document-retention-policies")]
    [RequirePermission(Permissions.Ged.View)]
    public async Task<IActionResult> RetentionPolicies(CancellationToken ct) => Ok(await retention.ListPoliciesAsync(ct));

    [HttpPost("document-retention-policies")]
    [RequirePermission(Permissions.Ged.ManageRetention)]
    public async Task<IActionResult> SaveRetentionPolicy([FromBody] SaveDocumentRetentionPolicyRequest request, CancellationToken ct)
        => FromResult(await retention.SavePolicyAsync(request, ct));

    [HttpPost("ged-documents/{id:guid}/legal-hold")]
    [RequirePermission(Permissions.Ged.ManageRetention)]
    public async Task<IActionResult> LegalHold(Guid id, [FromQuery] bool hold, [FromBody] string? reason, CancellationToken ct)
        => FromResult(await retention.SetLegalHoldAsync(id, hold, reason, ct));

    [HttpGet("ged-documents/eligible-for-deletion")]
    [RequirePermission(Permissions.Ged.ManageRetention)]
    public async Task<IActionResult> EligibleForDeletion(CancellationToken ct) => Ok(await retention.ListEligibleForDeletionAsync(ct));

    // --- §45 Audit ----------------------------------------------------------------------

    [HttpGet("document-audit")]
    [RequirePermission(Permissions.Ged.View)]
    public async Task<IActionResult> Audit([FromQuery] Guid? documentId, [FromQuery] int limit, CancellationToken ct)
        => Ok(await audit.RecentAsync(documentId, limit == 0 ? 100 : limit, ct));
}
