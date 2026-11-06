using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Nexora.Application.Abstractions;
using Nexora.Application.Mobile;
using Nexora.Contracts.Mobile;
using Nexora.Domain.Common;
using Nexora.Infrastructure.Persistence;

namespace Nexora.Infrastructure.Mobile;

/// <summary>
/// Pièces jointes minimales (Phase 44 §22-23) — un blob (`bytea`) + son empreinte SHA-256,
/// rien de plus : pas de versioning, pas de recherche, pas d'aperçu. Phase 45 (GED) généralisera
/// si un vrai stockage de documents devient nécessaire ; ceci couvre juste « capturer une photo
/// ou une signature et pouvoir la relire ».
/// </summary>
public sealed class SqlAttachmentService(NexoraDbContext db, ITenantContext tenant, IUnitOfWork uow) : IAttachmentService
{
    private Guid Company => tenant.ActiveCompanyId ?? throw TenantErrors.ActiveCompanyRequired();

    public async Task<Result<AttachmentDto>> UploadAsync(UploadAttachmentRequest request, CancellationToken ct = default)
    {
        var kind = request.Kind.Trim().ToLowerInvariant();
        if (kind is not ("photo" or "signature")) return Error.Validation("attachment.kind", "Type attendu : photo ou signature.");
        if (string.IsNullOrWhiteSpace(request.ContentType)) return Error.Validation("attachment.content_type", "Type de contenu requis.");

        byte[] bytes;
        try { bytes = Convert.FromBase64String(request.ContentBase64); }
        catch (FormatException) { return Error.Validation("attachment.content", "Contenu base64 invalide."); }
        if (bytes.Length == 0 || bytes.Length > 5 * 1024 * 1024)
            return Error.Validation("attachment.size", "Taille invalide (1 octet à 5 Mo — capture mobile seulement, §22-23).");
        // Le type enregistré est celui du contenu réel (audit 2026-10 éd. 2) : avant, le type déclaré était stocké puis
        // renvoyé tel quel — un contenu HTML déclaré « text/html » était servi et interprété par le navigateur.
        var contentType = Nexora.Infrastructure.Common.ImageSignature.Detect(bytes);
        if (contentType is null)
            return Error.Validation("attachment.content_type", "Photo ou signature attendue au format PNG, JPEG ou WebP.");

        var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        var id = Guid.NewGuid();
        await db.Database.ExecuteSqlAsync($"""
            INSERT INTO nexora.attachment
                (id, company_id, kind, reference_type, reference_id, content, content_type, size_bytes, sha256_hash, captured_by)
            VALUES ({id}, {Company}, {kind}, {request.ReferenceType}, {request.ReferenceId}, {bytes}, {contentType}, {bytes.Length}, {hash}, {tenant.UserId})
            """, ct);
        await uow.SaveChangesAsync(ct);

        var capturedAt = await db.Database.SqlQuery<DateTimeOffset>($"""
            SELECT captured_at AS "Value" FROM nexora.attachment WHERE id = {id}
            """).FirstAsync(ct);
        return Result.Success(new AttachmentDto(id, kind, request.ReferenceType, request.ReferenceId, contentType, bytes.Length, hash, capturedAt));
    }

    public async Task<Result<(byte[] Content, string ContentType)>> GetContentAsync(Guid id, CancellationToken ct = default)
    {
        var row = await db.Database.SqlQuery<ContentRow>($"""
            SELECT content AS "Content", content_type AS "ContentType" FROM nexora.attachment WHERE id = {id} AND company_id = {Company}
            """).FirstOrDefaultAsync(ct);
        return row is null
            ? Error.NotFound("attachment.not_found", "Pièce jointe introuvable.")
            : Result.Success((row.Content, row.ContentType));
    }

    private sealed record ContentRow(byte[] Content, string ContentType);
}
