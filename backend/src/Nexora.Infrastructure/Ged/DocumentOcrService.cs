using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Nexora.Application.Abstractions;
using Nexora.Application.Ged;
using Nexora.Contracts.Ged;
using Nexora.Domain.Common;
using Nexora.Infrastructure.Persistence;

namespace Nexora.Infrastructure.Ged;

/// <summary>
/// OCR (Phase 45 §11-12, §38). NEXORA ONE n'embarque **aucun moteur OCR tiers** cette phase —
/// backlog explicite. Cette implémentation ne fait qu'un heuristique minimal : le texte est
/// extrait tel quel pour les contenus déjà textuels (CSV), tout le reste (PDF/image binaire) est
/// marqué <c>unsupported</c> plutôt que de prétendre à une extraction qui n'existe pas. Le champ
/// <c>ocr_extracted</c> reste vide dans ce cas — jamais une donnée inventée pour alimenter une
/// facture (§11 : « les données extraites doivent être validées avant d'alimenter automatiquement
/// des opérations financières sensibles »).
/// </summary>
public sealed class SqlDocumentOcrService(NexoraDbContext db, ITenantContext tenant, IUnitOfWork uow) : IDocumentOcrService
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);
    private Guid Company => tenant.ActiveCompanyId ?? throw new InvalidOperationException("Entreprise active requise.");

    private sealed record VersionRow(Guid Id, int VersionNo, byte[] Content, string ContentType);

    public async Task<Result<OcrResultDto>> ExtractAsync(Guid documentId, int? versionNo, CancellationToken ct = default)
    {
        var row = versionNo is null
            ? await db.Database.SqlQuery<VersionRow>($"""
                SELECT v.id AS "Id", v.version_no AS "VersionNo", v.content AS "Content", v.content_type AS "ContentType"
                FROM nexora.document d JOIN nexora.document_version v ON v.id = d.current_version_id
                WHERE d.id = {documentId} AND d.company_id = {Company}
                """).FirstOrDefaultAsync(ct)
            : await db.Database.SqlQuery<VersionRow>($"""
                SELECT id AS "Id", version_no AS "VersionNo", content AS "Content", content_type AS "ContentType"
                FROM nexora.document_version WHERE document_id = {documentId} AND company_id = {Company} AND version_no = {versionNo}
                """).FirstOrDefaultAsync(ct);
        if (row is null) return Error.NotFound("document.version_not_found", "Version introuvable.");

        string status;
        string? text = null;
        decimal? confidence = null;
        string? extractedJson = null;

        if (row.ContentType == "text/csv")
        {
            status = "completed";
            text = Encoding.UTF8.GetString(row.Content);
            confidence = 100m;   // texte déjà lisible tel quel — pas une reconnaissance d'image
            extractedJson = JsonSerializer.Serialize(new { }, Web);
        }
        else
        {
            status = "unsupported";
        }

        await db.Database.ExecuteSqlAsync($"""
            UPDATE nexora.document_version SET ocr_status = {status}, ocr_text = {text}, ocr_confidence = {confidence}, ocr_extracted = {extractedJson}::jsonb
            WHERE id = {row.Id}
            """, ct);
        await uow.SaveChangesAsync(ct);

        return Result.Success(new OcrResultDto(documentId, row.VersionNo, status, text, confidence,
            extractedJson is null ? null : JsonDocument.Parse(extractedJson).RootElement));
    }
}
