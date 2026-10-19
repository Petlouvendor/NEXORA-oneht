using Asp.Versioning;
using Microsoft.AspNetCore.Mvc;
using Nexora.Api.Security;
using Nexora.Application.Documents;
using Nexora.Application.Security;
using Nexora.Contracts.Documents;

namespace Nexora.Api.Controllers;

/// <summary>
/// Factures (Phase 30 §8-11, §48). Une facture est un document émis à partir d'une vente
/// (Phase 6) : le total, les créances et les écritures restent portés par la vente / le moteur.
/// </summary>
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/invoices")]
public sealed class InvoicesController(IDocumentService documents, IDocumentQueries queries) : ApiControllerBase
{
    [HttpGet]
    [RequirePermission(Permissions.Documents.View)]
    public async Task<IActionResult> List([FromQuery] string? status, [FromQuery] Guid? partyId,
        [FromQuery] DateOnly? from, [FromQuery] DateOnly? to, CancellationToken ct = default)
    {
        // Types filtrés en base, avant la limite (audit 2026-10 éd. 2 : 300 documents de tous types étaient lus puis
        // filtrés en mémoire — au-delà, des factures devenaient invisibles derrière les devis et bons de livraison).
        return Ok(await queries.SearchAsync(new DocumentSearchRequest(
            Status: status, PartyId: partyId, From: from, To: to, Limit: 500,
            Types: ["invoice", "deposit_invoice", "final_invoice", "proforma"]), ct));
    }

    [HttpPost("{saleId:guid}/issue")]
    [RequirePermission(Permissions.Documents.Issue)]
    [Idempotent]
    public async Task<IActionResult> Issue(Guid saleId, [FromQuery] string type = "invoice", CancellationToken ct = default)
        => FromResult(await documents.IssueAsync(new IssueDocumentRequest(type, "sale", saleId), ct));

    [HttpPost("{id:guid}/cancel")]
    [RequireStepUp(StepUpPurposes.Cancellation)]
    [RequirePermission(Permissions.Documents.Cancel)]
    public async Task<IActionResult> Cancel(Guid id, [FromBody] CancelDocumentRequest r, CancellationToken ct)
        => FromResult(await documents.CancelAsync(id, r, ct));
}
