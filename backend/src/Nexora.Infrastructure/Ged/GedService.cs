using System.Security.Cryptography;
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
/// Bibliothèque documentaire (Phase 45 §3-6, §13-18, §24, §27, §31, §39). Le fichier lui-même vit
/// dans <c>document_version</c> (jamais écrasé, §16) ; <c>document</c> porte la classification, le
/// cycle de vie et les verrous. La détection de doublon (§39) compare des empreintes SHA-256,
/// jamais bloquante — une décision humaine.
/// </summary>
public sealed class SqlGedService(NexoraDbContext db, ITenantContext tenant, IUnitOfWork uow) : IGedService
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);
    private Guid Company => tenant.ActiveCompanyId ?? throw TenantErrors.ActiveCompanyRequired();

    private static readonly string[] DocumentTypes =
    [
        "invoice_sale", "invoice_purchase", "receipt", "credit_note", "quote", "purchase_order",
        "delivery_note", "contract", "accounting_voucher", "expense_proof", "statement", "tax_document",
        "customer_document", "supplier_document", "internal", "photo", "scan", "other",
    ];

    private static readonly string[] AllowedContentTypes =
    [
        "application/pdf", "image/jpeg", "image/jpg", "image/png", "image/webp", "image/tiff",
        "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
        "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", "text/csv",
    ];

    private sealed record Row(
        Guid Id, Guid? EstablishmentId, string Name, string DocumentType, string? Category, string Tags,
        string? ReferenceType, Guid? ReferenceId, Guid? CustomerId, Guid? SupplierId, int? FiscalYear,
        DateOnly? DocumentDate, string Status, int? CurrentVersionNo, bool Locked, string? LockedReason,
        bool LegalHold, string? LegalHoldReason, DateOnly? RetentionUntil, DateTimeOffset? DeletionRequestedAt,
        Guid? DeletionRequestedBy, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);

    private const string Select = """
        SELECT d.id AS "Id", d.establishment_id AS "EstablishmentId", d.name AS "Name", d.document_type AS "DocumentType",
               d.category AS "Category", d.tags::text AS "Tags", d.reference_type AS "ReferenceType", d.reference_id AS "ReferenceId",
               d.customer_id AS "CustomerId", d.supplier_id AS "SupplierId", d.fiscal_year AS "FiscalYear",
               d.document_date AS "DocumentDate", d.status AS "Status", v.version_no AS "CurrentVersionNo",
               d.locked AS "Locked", d.locked_reason AS "LockedReason", d.legal_hold AS "LegalHold",
               d.legal_hold_reason AS "LegalHoldReason", d.retention_until AS "RetentionUntil",
               d.deletion_requested_at AS "DeletionRequestedAt", d.deletion_requested_by AS "DeletionRequestedBy",
               d.created_at AS "CreatedAt", d.updated_at AS "UpdatedAt"
        FROM nexora.document d
        LEFT JOIN nexora.document_version v ON v.id = d.current_version_id
        """;

    private sealed record VersionRow(Guid Id, int VersionNo, string ContentType, int SizeBytes, string Sha256Hash,
        string ChangeReason, string OcrStatus, DateTimeOffset CreatedAt);

    private const string VersionSelect = """
        SELECT id AS "Id", version_no AS "VersionNo", content_type AS "ContentType", size_bytes AS "SizeBytes",
               sha256_hash AS "Sha256Hash", change_reason AS "ChangeReason", ocr_status AS "OcrStatus", created_at AS "CreatedAt"
        FROM nexora.document_version
        """;

    // --- Création & versions (§7-9, §16, §39) --------------------------------

    public async Task<Result<UploadResultDto>> CreateAsync(CreateDocumentRequest request, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(request.Name)) return Error.Validation("document.name", "Nom requis.");
        var type = request.DocumentType.Trim().ToLowerInvariant();
        if (!DocumentTypes.Contains(type)) return Error.Validation("document.type", $"Type « {type} » invalide.");

        var contentResult = DecodeContent(request.ContentBase64, request.ContentType);
        if (!contentResult.IsSuccess) return contentResult.Error!;
        var (bytes, hash) = contentResult.Value;

        var documentId = Guid.NewGuid();
        var versionId = Guid.NewGuid();
        var tagsJson = JsonSerializer.Serialize(request.Tags ?? [], Web);

        await uow.ExecuteInTransactionAsync(async token =>
        {
            await db.Database.ExecuteSqlAsync($"""
                INSERT INTO nexora.document
                    (id, company_id, name, document_type, category, tags, reference_type, reference_id,
                     customer_id, supplier_id, fiscal_year, document_date, created_by)
                VALUES ({documentId}, {Company}, {request.Name.Trim()}, {type}, {request.Category}, {tagsJson}::jsonb,
                        {request.ReferenceType}, {request.ReferenceId}, {request.CustomerId}, {request.SupplierId},
                        {request.FiscalYear}, {request.DocumentDate}, {tenant.UserId})
                """, token);
            await db.Database.ExecuteSqlAsync($"""
                INSERT INTO nexora.document_version (id, company_id, document_id, version_no, content, content_type, size_bytes, sha256_hash, created_by)
                VALUES ({versionId}, {Company}, {documentId}, 1, {bytes}, {request.ContentType}, {bytes.Length}, {hash}, {tenant.UserId})
                """, token);
            await db.Database.ExecuteSqlAsync($"""
                UPDATE nexora.document SET current_version_id = {versionId}, updated_at = now() WHERE id = {documentId}
                """, token);
            return 0;
        }, ct);

        var duplicateOf = await FindDuplicateAsync(hash, documentId, ct);
        var saved = await GetAsync(documentId, ct);
        return saved.IsSuccess ? Result.Success(new UploadResultDto(saved.Value!, duplicateOf)) : saved.Error!;
    }

    public async Task<Result<UploadResultDto>> AddVersionAsync(Guid id, AddDocumentVersionRequest request, CancellationToken ct = default)
    {
        var row = await GetRowAsync(id, ct);
        if (row is null) return Error.NotFound("document.not_found", "Document introuvable.");
        if (row.Locked) return Error.Conflict("document.locked", $"Document verrouillé ({row.LockedReason}).");

        var contentResult = DecodeContent(request.ContentBase64, request.ContentType);
        if (!contentResult.IsSuccess) return contentResult.Error!;
        var (bytes, hash) = contentResult.Value;

        var nextVersion = (row.CurrentVersionNo ?? 0) + 1;
        var versionId = Guid.NewGuid();
        await uow.ExecuteInTransactionAsync(async token =>
        {
            await db.Database.ExecuteSqlAsync($"""
                INSERT INTO nexora.document_version (id, company_id, document_id, version_no, content, content_type, size_bytes, sha256_hash, change_reason, created_by)
                VALUES ({versionId}, {Company}, {id}, {nextVersion}, {bytes}, {request.ContentType}, {bytes.Length}, {hash}, {request.ChangeReason ?? ""}, {tenant.UserId})
                """, token);
            await db.Database.ExecuteSqlAsync($"""
                UPDATE nexora.document SET current_version_id = {versionId}, updated_at = now() WHERE id = {id}
                """, token);
            return 0;
        }, ct);

        var duplicateOf = await FindDuplicateAsync(hash, id, ct);
        var saved = await GetAsync(id, ct);
        return saved.IsSuccess ? Result.Success(new UploadResultDto(saved.Value!, duplicateOf)) : saved.Error!;
    }

    private Result<(byte[] Bytes, string Hash)> DecodeContent(string contentBase64, string contentType)
    {
        if (string.IsNullOrWhiteSpace(contentType) || !AllowedContentTypes.Contains(contentType.Trim().ToLowerInvariant()))
            return Error.Validation("document.content_type", $"Type de contenu « {contentType} » non autorisé (§8-9).");
        byte[] bytes;
        try { bytes = Convert.FromBase64String(contentBase64); }
        catch (FormatException) { return Error.Validation("document.content", "Contenu base64 invalide."); }
        if (bytes.Length == 0 || bytes.Length > 25 * 1024 * 1024)
            return Error.Validation("document.size", "Taille invalide (1 octet à 25 Mo, §9).");
        var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        return Result.Success((bytes, hash));
    }

    private async Task<Guid?> FindDuplicateAsync(string hash, Guid excludeDocumentId, CancellationToken ct)
    {
        var row = await db.Database.SqlQuery<Guid>($"""
            SELECT document_id AS "Value" FROM nexora.document_version
            WHERE company_id = {Company} AND sha256_hash = {hash} AND document_id <> {excludeDocumentId}
            ORDER BY created_at LIMIT 1
            """).FirstOrDefaultAsync(ct);
        return row == Guid.Empty ? null : row;
    }

    public async Task<IReadOnlyList<DocumentVersionDto>> ListVersionsAsync(Guid id, CancellationToken ct = default)
    {
        var rows = await db.Database.SqlQueryRaw<VersionRow>(
            VersionSelect + " WHERE document_id = {0} AND company_id = {1} ORDER BY version_no DESC", id, Company).ToListAsync(ct);
        return rows.Select(r => new DocumentVersionDto(r.Id, r.VersionNo, r.ContentType, r.SizeBytes, r.Sha256Hash, r.ChangeReason, r.OcrStatus, r.CreatedAt)).ToList();
    }

    public async Task<Result<(byte[] Content, string ContentType)>> GetVersionContentAsync(Guid id, int? versionNo, CancellationToken ct = default)
    {
        var row = versionNo is null
            ? await db.Database.SqlQuery<ContentRow>($"""
                SELECT v.content AS "Content", v.content_type AS "ContentType" FROM nexora.document d
                JOIN nexora.document_version v ON v.id = d.current_version_id WHERE d.id = {id} AND d.company_id = {Company}
                """).FirstOrDefaultAsync(ct)
            : await db.Database.SqlQuery<ContentRow>($"""
                SELECT content AS "Content", content_type AS "ContentType" FROM nexora.document_version
                WHERE document_id = {id} AND company_id = {Company} AND version_no = {versionNo}
                """).FirstOrDefaultAsync(ct);
        return row is null ? Error.NotFound("document.version_not_found", "Version introuvable.") : Result.Success((row.Content, row.ContentType));
    }
    private sealed record ContentRow(byte[] Content, string ContentType);

    // --- Lecture, métadonnées, recherche (§5, §13-14) -------------------------

    public async Task<Result<GedDocumentDto>> GetAsync(Guid id, CancellationToken ct = default)
    {
        var row = await db.Database.SqlQueryRaw<Row>(Select + " WHERE d.id = {0} AND d.company_id = {1}", id, Company).FirstOrDefaultAsync(ct);
        return row is null ? Error.NotFound("document.not_found", "Document introuvable.") : Result.Success(ToDto(row));
    }

    private async Task<Row?> GetRowAsync(Guid id, CancellationToken ct) =>
        await db.Database.SqlQueryRaw<Row>(Select + " WHERE d.id = {0} AND d.company_id = {1}", id, Company).FirstOrDefaultAsync(ct);

    public async Task<IReadOnlyList<GedDocumentDto>> SearchAsync(DocumentSearchQuery query, CancellationToken ct = default)
    {
        var sql = new StringBuilder(Select + " WHERE d.company_id = {0}");
        var args = new List<object> { Company };

        void AddClause(string fragment, object value)
        {
            args.Add(value);
            sql.Append(' ').Append(fragment.Replace("{}", "{" + (args.Count - 1) + "}"));
        }

        if (!string.IsNullOrWhiteSpace(query.DocumentType)) AddClause("AND d.document_type = {}", query.DocumentType.Trim().ToLowerInvariant());
        if (!string.IsNullOrWhiteSpace(query.Status)) AddClause("AND d.status = {}", query.Status.Trim().ToLowerInvariant());
        if (query.CustomerId is { } cust) AddClause("AND d.customer_id = {}", cust);
        if (query.SupplierId is { } sup) AddClause("AND d.supplier_id = {}", sup);
        if (query.DateFrom is { } from) AddClause("AND d.document_date >= {}", from);
        if (query.DateTo is { } to) AddClause("AND d.document_date <= {}", to);
        if (query.SignedOnly == true) sql.Append(" AND d.status = 'signed'");
        if (query.SignedOnly == false) sql.Append(" AND d.status <> 'signed'");
        if (!string.IsNullOrWhiteSpace(query.Text))
        {
            var idx = args.Count;
            args.Add($"%{query.Text.Trim()}%");
            sql.Append($" AND (d.name ILIKE {{{idx}}} OR COALESCE(d.category,'') ILIKE {{{idx}}} OR COALESCE(v.ocr_text,'') ILIKE {{{idx}}})");
        }
        if (query.Tags is { Count: > 0 })
        {
            var idx = args.Count;
            args.Add(JsonSerializer.Serialize(query.Tags, Web));
            sql.Append($" AND d.tags @> {{{idx}}}::jsonb");
        }

        var page = Math.Max(1, query.Page);
        var pageSize = Math.Clamp(query.PageSize, 1, 200);
        sql.Append(" ORDER BY d.created_at DESC LIMIT ").Append(pageSize).Append(" OFFSET ").Append((page - 1) * pageSize);

        var rows = await db.Database.SqlQueryRaw<Row>(sql.ToString(), args.ToArray()).ToListAsync(ct);
        return rows.Select(ToDto).ToList();
    }

    public async Task<Result<GedDocumentDto>> UpdateMetadataAsync(Guid id, UpdateDocumentMetadataRequest request, CancellationToken ct = default)
    {
        var row = await GetRowAsync(id, ct);
        if (row is null) return Error.NotFound("document.not_found", "Document introuvable.");
        if (row.Locked) return Error.Conflict("document.locked", $"Document verrouillé ({row.LockedReason}).");

        var tagsJson = request.Tags is null ? (string?)null : JsonSerializer.Serialize(request.Tags, Web);
        await db.Database.ExecuteSqlAsync($"""
            UPDATE nexora.document SET
                name = COALESCE({request.Name}, name), category = COALESCE({request.Category}, category),
                tags = COALESCE({tagsJson}::jsonb, tags), customer_id = COALESCE({request.CustomerId}, customer_id),
                supplier_id = COALESCE({request.SupplierId}, supplier_id), fiscal_year = COALESCE({request.FiscalYear}, fiscal_year),
                document_date = COALESCE({request.DocumentDate}, document_date), updated_at = now()
            WHERE id = {id} AND company_id = {Company}
            """, ct);
        await uow.SaveChangesAsync(ct);
        return await GetAsync(id, ct);
    }

    // --- Workflow §23-24 : brouillon → revue → approbation → archive ----------

    public async Task<Result<GedDocumentDto>> SubmitForReviewAsync(Guid id, CancellationToken ct = default)
    {
        var rows = await db.Database.ExecuteSqlAsync($"""
            UPDATE nexora.document SET status = 'pending_review', updated_at = now()
            WHERE id = {id} AND company_id = {Company} AND status = 'draft'
            """, ct);
        if (rows == 0) return Error.NotFound("document.not_found", "Document introuvable ou déjà en revue.");
        await uow.SaveChangesAsync(ct);
        return await GetAsync(id, ct);
    }

    public async Task<Result<GedDocumentDto>> ApproveAsync(Guid id, CancellationToken ct = default)
    {
        var row = await db.Database.SqlQuery<StatusOwnerRow>($"""
            SELECT status AS "Status", created_by AS "CreatedBy" FROM nexora.document WHERE id = {id} AND company_id = {Company}
            """).FirstOrDefaultAsync(ct);
        if (row is null) return Error.NotFound("document.not_found", "Document introuvable.");
        if (row.Status != "pending_review") return Error.Conflict("document.not_pending", "Le document n'est pas en revue.");
        if (row.CreatedBy is { } creator && creator == tenant.UserId)
            return Error.Forbidden("document.four_eyes", "Le déposant ne peut pas approuver son propre document (§23).");

        await db.Database.ExecuteSqlAsync($"UPDATE nexora.document SET status = 'approved', updated_at = now() WHERE id = {id}", ct);
        await uow.SaveChangesAsync(ct);
        return await GetAsync(id, ct);
    }
    private sealed record StatusOwnerRow(string Status, Guid? CreatedBy);

    public async Task<Result<GedDocumentDto>> RejectAsync(Guid id, string reason, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(reason)) return Error.Validation("document.reject_reason", "Motif requis.");
        var rows = await db.Database.ExecuteSqlAsync($"""
            UPDATE nexora.document SET status = 'draft', updated_at = now()
            WHERE id = {id} AND company_id = {Company} AND status = 'pending_review'
            """, ct);
        if (rows == 0) return Error.NotFound("document.not_found", "Document introuvable ou pas en revue.");
        await uow.SaveChangesAsync(ct);
        return await GetAsync(id, ct);
    }

    // --- Verrouillage §18 ------------------------------------------------------

    public async Task<Result<GedDocumentDto>> LockAsync(Guid id, string reason, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(reason)) return Error.Validation("document.lock_reason", "Motif de verrouillage requis.");
        var rows = await db.Database.ExecuteSqlAsync($"""
            UPDATE nexora.document SET locked = true, locked_reason = {reason}, updated_at = now()
            WHERE id = {id} AND company_id = {Company}
            """, ct);
        if (rows == 0) return Error.NotFound("document.not_found", "Document introuvable.");
        await uow.SaveChangesAsync(ct);
        return await GetAsync(id, ct);
    }

    public async Task<Result<GedDocumentDto>> UnlockAsync(Guid id, CancellationToken ct = default)
    {
        // Un document signé, ou en cours de signature, ne se déverrouille pas : sinon une nouvelle version
        // remplacerait le contenu signé (le verrou de signature tombe seul au refus, à l'annulation, à l'expiration).
        var state = await db.Database.SqlQuery<string?>($"""
            SELECT CASE WHEN d.status = 'signed' THEN 'signed'
                        WHEN EXISTS(SELECT 1 FROM nexora.document_signature_request r WHERE r.document_id = d.id AND r.status = 'pending')
                             THEN 'signing' END AS "Value"
            FROM nexora.document d WHERE d.id = {id} AND d.company_id = {Company}
            """).FirstOrDefaultAsync(ct);
        if (state == "signed") return Error.Conflict("document.signed", "Un document signé ne peut pas être déverrouillé.");
        if (state == "signing") return Error.Conflict("document.signing", "Document en cours de signature : annulez la demande pour le libérer.");
        var rows = await db.Database.ExecuteSqlAsync($"""
            UPDATE nexora.document SET locked = false, locked_reason = NULL, updated_at = now()
            WHERE id = {id} AND company_id = {Company}
            """, ct);
        if (rows == 0) return Error.NotFound("document.not_found", "Document introuvable.");
        await uow.SaveChangesAsync(ct);
        return await GetAsync(id, ct);
    }

    // --- Archivage §27 -----------------------------------------------------------

    public async Task<Result<GedDocumentDto>> ArchiveAsync(Guid id, CancellationToken ct = default)
    {
        if (await db.Database.SqlQuery<bool>($"""
                SELECT EXISTS(SELECT 1 FROM nexora.document_signature_request
                              WHERE document_id = {id} AND company_id = {Company} AND status = 'pending') AS "Value"
                """).FirstAsync(ct))
            return Error.Conflict("document.signing", "Document en cours de signature : attendez l'issue ou annulez la demande.");
        var rows = await db.Database.ExecuteSqlAsync($"""
            UPDATE nexora.document SET status = 'archived', updated_at = now()
            WHERE id = {id} AND company_id = {Company} AND status <> 'archived'
            """, ct);
        if (rows == 0) return Error.NotFound("document.not_found", "Document introuvable ou déjà archivé.");
        await uow.SaveChangesAsync(ct);
        return await GetAsync(id, ct);
    }

    public async Task<Result<GedDocumentDto>> RestoreAsync(Guid id, CancellationToken ct = default)
    {
        var rows = await db.Database.ExecuteSqlAsync($"""
            UPDATE nexora.document d
               SET status = CASE WHEN EXISTS(SELECT 1 FROM nexora.document_signature_request r
                                             WHERE r.document_id = d.id AND r.status = 'completed') THEN 'signed' ELSE 'approved' END,
                   updated_at = now()
            WHERE id = {id} AND company_id = {Company} AND status = 'archived'
            """, ct);
        if (rows == 0) return Error.NotFound("document.not_found", "Document introuvable ou non archivé.");
        await uow.SaveChangesAsync(ct);
        return await GetAsync(id, ct);
    }

    // --- Suppression contrôlée §30 ------------------------------------------------

    public async Task<Result> RequestDeletionAsync(Guid id, string reason, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(reason)) return Error.Validation("document.deletion_reason", "Motif requis.");
        var row = await GetRowAsync(id, ct);
        if (row is null) return Error.NotFound("document.not_found", "Document introuvable.");
        if (row.LegalHold) return Error.Forbidden("document.legal_hold", "Document sous legal hold — suppression impossible (§29).");
        if (row.RetentionUntil is { } until && until > DateOnly.FromDateTime(DateTime.UtcNow))
            return Error.Forbidden("document.retention", $"Document sous rétention jusqu'au {until:yyyy-MM-dd} (§28).");

        await db.Database.ExecuteSqlAsync($"""
            UPDATE nexora.document SET deletion_requested_at = now(), deletion_requested_by = {tenant.UserId},
                   deletion_reason = {reason}, updated_at = now()
            WHERE id = {id} AND company_id = {Company}
            """, ct);
        await uow.SaveChangesAsync(ct);
        return Result.Success();
    }

    public async Task<Result> ConfirmDeletionAsync(Guid id, CancellationToken ct = default)
    {
        var row = await db.Database.SqlQuery<DeletionRow>($"""
            SELECT deletion_requested_by AS "RequestedBy", legal_hold AS "LegalHold", retention_until AS "RetentionUntil"
            FROM nexora.document WHERE id = {id} AND company_id = {Company} AND deletion_requested_at IS NOT NULL
            """).FirstOrDefaultAsync(ct);
        if (row is null) return Error.NotFound("document.not_found", "Aucune demande de suppression en attente.");
        if (row.LegalHold) return Error.Forbidden("document.legal_hold", "Document sous legal hold (§29).");
        if (row.RetentionUntil is { } until && until > DateOnly.FromDateTime(DateTime.UtcNow))
            return Error.Forbidden("document.retention", "Document encore sous rétention (§28).");
        if (row.RequestedBy is { } requester && requester == tenant.UserId)
            return Error.Forbidden("document.four_eyes", "Le demandeur ne peut pas confirmer sa propre suppression (§30).");

        await db.Database.ExecuteSqlAsync($"DELETE FROM nexora.document WHERE id = {id} AND company_id = {Company}", ct);
        await uow.SaveChangesAsync(ct);
        return Result.Success();
    }
    private sealed record DeletionRow(Guid? RequestedBy, bool LegalHold, DateOnly? RetentionUntil);

    public async Task<Result> CancelDeletionRequestAsync(Guid id, CancellationToken ct = default)
    {
        var rows = await db.Database.ExecuteSqlAsync($"""
            UPDATE nexora.document SET deletion_requested_at = NULL, deletion_requested_by = NULL, deletion_reason = NULL, updated_at = now()
            WHERE id = {id} AND company_id = {Company} AND deletion_requested_at IS NOT NULL
            """, ct);
        if (rows == 0) return Error.NotFound("document.not_found", "Aucune demande de suppression en attente.");
        await uow.SaveChangesAsync(ct);
        return Result.Success();
    }

    // --- Liens §6 -----------------------------------------------------------------

    public async Task<Result<DocumentLinkDto>> AddLinkAsync(Guid id, AddDocumentLinkRequest request, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(request.LinkedType)) return Error.Validation("document_link.type", "Type d'objet lié requis.");
        var linkId = Guid.NewGuid();
        await db.Database.ExecuteSqlAsync($"""
            INSERT INTO nexora.document_link (id, company_id, document_id, linked_type, linked_id, created_by)
            VALUES ({linkId}, {Company}, {id}, {request.LinkedType.Trim().ToLowerInvariant()}, {request.LinkedId}, {tenant.UserId})
            ON CONFLICT ON CONSTRAINT document_link_uk DO NOTHING
            """, ct);
        await uow.SaveChangesAsync(ct);
        var link = await db.Database.SqlQuery<LinkRow>($"""
            SELECT id AS "Id", linked_type AS "LinkedType", linked_id AS "LinkedId", created_at AS "CreatedAt"
            FROM nexora.document_link WHERE document_id = {id} AND linked_type = {request.LinkedType.Trim().ToLowerInvariant()} AND linked_id = {request.LinkedId}
            """).FirstAsync(ct);
        return Result.Success(new DocumentLinkDto(link.Id, link.LinkedType, link.LinkedId, link.CreatedAt));
    }
    private sealed record LinkRow(Guid Id, string LinkedType, Guid LinkedId, DateTimeOffset CreatedAt);

    public async Task<IReadOnlyList<DocumentLinkDto>> ListLinksAsync(Guid id, CancellationToken ct = default)
    {
        var rows = await db.Database.SqlQuery<LinkRow>($"""
            SELECT id AS "Id", linked_type AS "LinkedType", linked_id AS "LinkedId", created_at AS "CreatedAt"
            FROM nexora.document_link WHERE document_id = {id} AND company_id = {Company} ORDER BY created_at
            """).ToListAsync(ct);
        return rows.Select(r => new DocumentLinkDto(r.Id, r.LinkedType, r.LinkedId, r.CreatedAt)).ToList();
    }

    // --- Partage §31 ----------------------------------------------------------------

    public async Task<Result<DocumentShareDto>> ShareAsync(Guid id, CreateShareRequest request, CancellationToken ct = default)
    {
        if (request.ExpiresAt <= DateTimeOffset.UtcNow) return Error.Validation("document_share.expires_at", "L'expiration doit être future.");
        var doc = await GetRowAsync(id, ct);
        if (doc is null) return Error.NotFound("document.not_found", "Document introuvable.");

        var shareId = Guid.NewGuid();
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();
        var passwordHash = string.IsNullOrWhiteSpace(request.Password) ? null : HashPassword(request.Password);
        await db.Database.ExecuteSqlAsync($"""
            INSERT INTO nexora.document_share (id, company_id, document_id, token, allow_download, password_hash, expires_at, created_by)
            VALUES ({shareId}, {Company}, {id}, {token}, {request.AllowDownload}, {passwordHash}, {request.ExpiresAt}, {tenant.UserId})
            """, ct);
        await uow.SaveChangesAsync(ct);
        return Result.Success(new DocumentShareDto(shareId, token, request.AllowDownload, request.ExpiresAt, null, DateTimeOffset.UtcNow));
    }

    public async Task<Result> RevokeShareAsync(Guid shareId, CancellationToken ct = default)
    {
        var rows = await db.Database.ExecuteSqlAsync($"""
            UPDATE nexora.document_share SET revoked_at = now() WHERE id = {shareId} AND company_id = {Company} AND revoked_at IS NULL
            """, ct);
        if (rows == 0) return Error.NotFound("document_share.not_found", "Partage introuvable ou déjà révoqué.");
        await uow.SaveChangesAsync(ct);
        return Result.Success();
    }

    public async Task<Result<(byte[] Content, string ContentType, string DocumentName)>> GetSharedContentAsync(string token, string? password, CancellationToken ct = default)
    {
        using var _ = tenant.BeginSystemScope();   // accès public par jeton — hors périmètre entreprise, comme DocumentsController.Verify (§31)
        var share = await db.Database.SqlQuery<ShareRow>($"""
            SELECT s.document_id AS "DocumentId", s.allow_download AS "AllowDownload", s.password_hash AS "PasswordHash",
                   s.expires_at AS "ExpiresAt", s.revoked_at AS "RevokedAt"
            FROM nexora.document_share s WHERE s.token = {token}
            """).FirstOrDefaultAsync(ct);
        if (share is null) return Error.NotFound("document_share.not_found", "Lien de partage invalide.");
        if (share.RevokedAt is not null) return Error.Forbidden("document_share.revoked", "Lien révoqué.");
        if (share.ExpiresAt <= DateTimeOffset.UtcNow) return Error.Forbidden("document_share.expired", "Lien expiré (§31).");
        if (!share.AllowDownload) return Error.Forbidden("document_share.no_download", "Ce lien est en lecture seule.");
        if (share.PasswordHash is { } hash && hash != HashPassword(password ?? ""))
            return Error.Forbidden("document_share.password", "Mot de passe incorrect.");

        var content = await db.Database.SqlQuery<SharedContentRow>($"""
            SELECT d.name AS "Name", v.content AS "Content", v.content_type AS "ContentType"
            FROM nexora.document d JOIN nexora.document_version v ON v.id = d.current_version_id
            WHERE d.id = {share.DocumentId}
            """).FirstOrDefaultAsync(ct);
        return content is null
            ? Error.NotFound("document.not_found", "Document introuvable.")
            : Result.Success((content.Content, content.ContentType, content.Name));
    }
    private sealed record ShareRow(Guid DocumentId, bool AllowDownload, string? PasswordHash, DateTimeOffset ExpiresAt, DateTimeOffset? RevokedAt);
    private sealed record SharedContentRow(string Name, byte[] Content, string ContentType);

    private static string HashPassword(string password) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(password))).ToLowerInvariant();

    private static GedDocumentDto ToDto(Row r) => new(
        r.Id, r.EstablishmentId, r.Name, r.DocumentType, r.Category, JsonSerializer.Deserialize<List<string>>(r.Tags) ?? [],
        r.ReferenceType, r.ReferenceId, r.CustomerId, r.SupplierId, r.FiscalYear, r.DocumentDate, r.Status, r.CurrentVersionNo,
        r.Locked, r.LockedReason, r.LegalHold, r.LegalHoldReason, r.RetentionUntil, r.DeletionRequestedAt, r.CreatedAt, r.UpdatedAt);
}
