using System.Text.RegularExpressions;
using Asp.Versioning;
using Microsoft.AspNetCore.Mvc;
using Nexora.Api.Security;
using Nexora.Application.Printing;
using Nexora.Application.Security;
using Nexora.Contracts.Printing;
using Nexora.Domain.Common;

namespace Nexora.Api.Controllers;

/// <summary>
/// Impression des reçus et documents (clients, ventes, fournisseurs) en vrai PDF A4, ticket 80 mm ou
/// Excel. Les droits dépendent du type de document (<see cref="PrintAccess"/>, vérifiés par le service) ;
/// une pièce d'une autre entreprise ou hors périmètre répond 404.
/// </summary>
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/print")]
public sealed class PrintController(IReceiptService receipts) : ApiControllerBase
{
    /// <summary>Types : customer-payment, customer-credit, credit-memo, customer-refund, customer-expense,
    /// sale, supplier-payment, supplier-invoice, purchase-order, goods-receipt.
    /// Formats : pdf (défaut), ticket, xlsx, csv.</summary>
    [HttpGet("{kind}/{id:guid}")]
    public async Task<IActionResult> Print(string kind, Guid id, [FromQuery] string? format, CancellationToken ct)
        => Send(await receipts.PrintAsync(kind, id, format ?? PrintFormats.Pdf, ct));

    /// <summary>Relevé / situation de compte client (total versé, reste à verser, mouvements).</summary>
    [HttpGet("customers/{customerId:guid}/statement")]
    public async Task<IActionResult> CustomerStatement(
        Guid customerId, [FromQuery] DateOnly? from, [FromQuery] DateOnly? to, [FromQuery] string? currency,
        [FromQuery] string? format, CancellationToken ct)
        => Send(await receipts.CustomerStatementAsync(customerId, from, to, currency, format ?? PrintFormats.Pdf, ct));

    /// <summary>Relevé fournisseur (facturé, payé, reste à payer).</summary>
    [HttpGet("suppliers/{supplierId:guid}/statement")]
    public async Task<IActionResult> SupplierStatement(
        Guid supplierId, [FromQuery] DateOnly? from, [FromQuery] DateOnly? to, [FromQuery] string? format, CancellationToken ct)
        => Send(await receipts.SupplierStatementAsync(supplierId, from, to, format ?? PrintFormats.Pdf, ct));

    /// <summary>Pièces imprimables d'un fournisseur (paiements, réceptions, factures, commandes).</summary>
    [HttpGet("suppliers/{supplierId:guid}/documents")]
    public async Task<IActionResult> SupplierDocuments(Guid supplierId, CancellationToken ct)
        => Ok(await receipts.SupplierDocumentsAsync(supplierId, ct));

    private IActionResult Send(Result<RenderedFile> r) =>
        r.IsSuccess ? Download(r.Value!.Content, r.Value.ContentType, r.Value.FileName) : throw new DomainException(r.Error!);
}

/// <summary>
/// Export de n'importe quel tableau affiché (rapport, liste) en vrai Excel (.xlsx), PDF ou CSV. Le
/// document est marqué « non contractuel » : il reproduit l'écran, il ne remplace pas un reçu émis par
/// le serveur. Chaque export est journalisé (export_log).
/// </summary>
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/exports")]
public sealed partial class ExportsController(IPrintService print) : ApiControllerBase
{
    private const int MaxTables = 20;
    private const int MaxColumns = 60;
    private const int MaxRows = 50_000;
    private const int MaxCellLength = 4_000;

    [GeneratedRegex("^[a-z0-9][a-z0-9_.:-]{0,79}$")]
    private static partial Regex ScreenKey();

    [HttpPost("screen")]
    [RequirePermission(Permissions.Reporting.Export)]
    [RequestSizeLimit(40_000_000)]
    public async Task<IActionResult> Screen([FromQuery] string? format, [FromBody] ScreenExportRequest request, CancellationToken ct)
    {
        var f = PrintFormats.Normalize(format ?? PrintFormats.Xlsx);
        if (!PrintFormats.IsValid(f))
            throw new DomainException(Error.Validation("export.format", "Format inconnu : xlsx, pdf, ticket ou csv."));
        var key = (request.ScreenKey ?? "").Trim().ToLowerInvariant();
        if (!ScreenKey().IsMatch(key))
            throw new DomainException(Error.Validation("export.screen_key", "Identifiant d'écran invalide."));
        var d = request.Document ?? throw new DomainException(Error.Validation("export.document", "Document manquant."));
        if (string.IsNullOrWhiteSpace(d.Title) || d.Title.Length > 200)
            throw new DomainException(Error.Validation("export.title", "Titre requis (200 caractères au plus)."));
        var tables = d.Tables ?? [];
        if (tables.Count > MaxTables || tables.Any(t => t.Columns.Count > MaxColumns) || tables.Sum(t => t.Rows.Count) > MaxRows)
            throw new DomainException(Error.Validation("export.too_large",
                $"Export trop volumineux ({MaxTables} tableaux, {MaxColumns} colonnes, {MaxRows} lignes au plus)."));
        if (tables.SelectMany(t => t.Rows).SelectMany(r => r).Any(v => v is System.Text.Json.JsonElement { ValueKind: System.Text.Json.JsonValueKind.String } s
                && s.GetString()!.Length > MaxCellLength))
            throw new DomainException(Error.Validation("export.cell_too_long", $"Une cellule dépasse {MaxCellLength} caractères."));

        // Jamais « officiel » : ni tampon, ni signature, mention « non contractuel » imposée.
        var doc = d with { Official = false, Stamp = null, Signatures = null };
        var file = await print.RenderAsync(doc, f, $"screen:{key}", d.Title, ct: ct);
        return Download(file.Content, file.ContentType, file.FileName);
    }
}
