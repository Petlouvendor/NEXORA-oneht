using Asp.Versioning;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Nexora.Api.Security;
using Nexora.Application.Documents;
using Nexora.Application.Printing;
using Nexora.Contracts.Printing;
using Nexora.Application.Security;
using Nexora.Contracts.Documents;

namespace Nexora.Api.Controllers;

/// <summary>
/// Documents commerciaux émis (Phase 30 §3, §19, §25, §27-28, §31, §34, §36, §48) : émission
/// depuis une source métier (snapshot figé), rendu, envoi, annulation, vérification publique.
/// </summary>
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/documents")]
public sealed class DocumentsController(IDocumentService documents, IDocumentQueries queries, IReceiptService receipts) : ApiControllerBase
{
    // --- Recherche & détail (§34) -------------------------------------

    [HttpGet]
    [RequirePermission(Permissions.Documents.View)]
    public async Task<IActionResult> Search([FromQuery] DocumentSearchRequest r, CancellationToken ct)
        => Ok(await queries.SearchAsync(r, ct));

    [HttpGet("{id:guid}")]
    [RequirePermission(Permissions.Documents.View)]
    public async Task<IActionResult> Get(Guid id, CancellationToken ct)
        => FromResult(await queries.GetAsync(id, ct));

    [HttpGet("{id:guid}/sends")]
    [RequirePermission(Permissions.Documents.View)]
    public async Task<IActionResult> Sends(Guid id, CancellationToken ct)
        => Ok(await queries.SendsAsync(id, ct));

    // --- Émission (§8, §12-13, §48) -----------------------------------

    [HttpPost]
    [RequirePermission(Permissions.Documents.Issue)]
    [Idempotent]
    public async Task<IActionResult> Issue([FromBody] IssueDocumentRequest r, CancellationToken ct)
        => FromResult(await documents.IssueAsync(r, ct));

    [HttpPost("~/api/v{version:apiVersion}/credit-notes")]
    [RequirePermission(Permissions.Documents.Issue)]
    [Idempotent]
    public async Task<IActionResult> CreditNote([FromBody] IssueDocumentRequest r, CancellationToken ct)
        => FromResult(await documents.IssueAsync(r with { Type = "credit_note" }, ct));

    [HttpPost("~/api/v{version:apiVersion}/receipts")]
    [RequirePermission(Permissions.Documents.Issue)]
    [Idempotent]
    public async Task<IActionResult> Receipt([FromBody] IssueDocumentRequest r, CancellationToken ct)
        => FromResult(await documents.IssueAsync(r with { Type = "receipt" }, ct));

    [HttpPost("~/api/v{version:apiVersion}/delivery-notes")]
    [RequirePermission(Permissions.Documents.Issue)]
    [Idempotent]
    public async Task<IActionResult> DeliveryNote([FromBody] IssueDocumentRequest r, CancellationToken ct)
        => FromResult(await documents.IssueAsync(r with { Type = "delivery_note" }, ct));

    // --- Rendu / envoi / annulation (§25, §31, §36) ------------------

    /// <summary>Vrai PDF (A4 ou ticket 80 mm) reconstruit depuis le contenu figé du document (phase 4 :
    /// remplaçait une page HTML présentée comme « PDF »).</summary>
    [HttpGet("{id:guid}/pdf")]
    [RequirePermission(Permissions.Documents.View)]
    public async Task<IActionResult> Pdf(Guid id, [FromQuery] string? format, CancellationToken ct)
    {
        var r = await receipts.PrintAsync(PrintableKinds.IssuedDocument, id, format ?? PrintFormats.Pdf, ct);
        return r.IsSuccess ? Download(r.Value!.Content, r.Value.ContentType, r.Value.FileName) : FromResult(r);
    }

    [HttpPost("{id:guid}/send")]
    [RequirePermission(Permissions.Documents.Send)]
    public async Task<IActionResult> Send(Guid id, [FromBody] SendDocumentRequest r, CancellationToken ct)
        => FromResult(await documents.SendAsync(id, r, ct));

    [HttpPost("{id:guid}/cancel")]
    [RequirePermission(Permissions.Documents.Cancel)]
    public async Task<IActionResult> Cancel(Guid id, [FromBody] CancelDocumentRequest r, CancellationToken ct)
        => FromResult(await documents.CancelAsync(id, r, ct));

    // --- Vérification publique (§27-28) -----------------------------

    [HttpGet("verify/{code}")]
    [AllowAnonymous]
    public async Task<IActionResult> Verify(string code, CancellationToken ct)
        => Ok(await documents.VerifyAsync(code, ct));

    // --- Numérotation (§19-20) ------------------------------------

    [HttpGet("~/api/v{version:apiVersion}/document-sequences")]
    [RequirePermission(Permissions.Documents.ManageSequence)]
    public async Task<IActionResult> Sequences(CancellationToken ct)
        => Ok(await queries.SequencesAsync(ct));
}
