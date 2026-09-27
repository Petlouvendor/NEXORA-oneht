using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Nexora.Application.Abstractions;
using Nexora.Application.Ged;
using Nexora.Contracts.Ged;
using Nexora.Domain.Common;
using Nexora.Infrastructure.Persistence;

namespace Nexora.Infrastructure.Ged;

/// <summary>
/// Signature multi-signataires (Phase 45 §19-22). Signature simple horodatée : identité déclarée,
/// authentification NEXORA, hash du document courant, journal (§22) — jamais présentée comme une
/// qualification juridique particulière (§19). L'ordre des signataires (§21) est respecté :
/// un signataire ne peut signer tant qu'un rang antérieur reste en attente. Verrouille le document
/// pendant la signature et une fois signé (§18).
/// </summary>
public sealed class SqlSignatureService(NexoraDbContext db, ITenantContext tenant, IUnitOfWork uow, IDateTimeProvider clock) : ISignatureService
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);
    private Guid Company => tenant.ActiveCompanyId ?? throw new InvalidOperationException("Entreprise active requise.");

    private sealed record RequestRow(Guid Id, Guid DocumentId, string Status, DateTimeOffset? CompletedAt, DateTimeOffset CreatedAt);
    private sealed record SignerRow(Guid Id, int SequenceNo, string SignerType, Guid? UserId, string? ExternalName,
        string? ExternalEmail, string? Role, string Status, DateTimeOffset? DueAt, DateTimeOffset? SignedAt, string? DeclinedReason);
    private sealed record SignerLookupRow(int SequenceNo, string Status, Guid? UserId);

    public async Task<Result<SignatureRequestDto>> RequestAsync(Guid documentId, CreateSignatureRequestRequest request, CancellationToken ct = default)
    {
        if (request.Signers.Count == 0) return Error.Validation("signature.signers", "Au moins un signataire requis (§21).");
        var docExists = await db.Database.SqlQuery<bool>($"""
            SELECT EXISTS(SELECT 1 FROM nexora.document WHERE id = {documentId} AND company_id = {Company}) AS "Value"
            """).FirstAsync(ct);
        if (!docExists) return Error.NotFound("document.not_found", "Document introuvable.");

        foreach (var s in request.Signers)
        {
            var type = s.SignerType.Trim().ToLowerInvariant();
            if (type is not ("user" or "external")) return Error.Validation("signature.signer_type", "Type de signataire invalide.");
            if (type == "user" && s.UserId is null) return Error.Validation("signature.signer_user", "Utilisateur requis pour un signataire interne.");
            if (type == "external" && string.IsNullOrWhiteSpace(s.ExternalName)) return Error.Validation("signature.signer_name", "Nom requis pour un signataire externe.");
        }

        var requestId = Guid.NewGuid();
        await uow.ExecuteInTransactionAsync(async token =>
        {
            await db.Database.ExecuteSqlAsync($"""
                INSERT INTO nexora.document_signature_request (id, company_id, document_id, requested_by)
                VALUES ({requestId}, {Company}, {documentId}, {tenant.UserId})
                """, token);
            var seq = 1;
            foreach (var s in request.Signers)
            {
                var type = s.SignerType.Trim().ToLowerInvariant();
                await db.Database.ExecuteSqlAsync($"""
                    INSERT INTO nexora.document_signer
                        (id, company_id, signature_request_id, sequence_no, signer_type, user_id, external_name, external_email, role, due_at)
                    VALUES ({Guid.NewGuid()}, {Company}, {requestId}, {seq}, {type}, {s.UserId}, {s.ExternalName}, {s.ExternalEmail}, {s.Role}, {s.DueAt})
                    """, token);
                seq++;
            }
            return 0;
        }, ct);

        await db.Database.ExecuteSqlAsync($"""
            UPDATE nexora.document SET locked = true, locked_reason = 'En cours de signature (§18)', updated_at = now()
            WHERE id = {documentId} AND NOT locked
            """, ct);
        await uow.SaveChangesAsync(ct);
        return await GetAsync(requestId, ct);
    }

    public async Task<Result<SignatureRequestDto>> GetAsync(Guid id, CancellationToken ct = default)
    {
        var req = await db.Database.SqlQuery<RequestRow>($"""
            SELECT id AS "Id", document_id AS "DocumentId", status AS "Status", completed_at AS "CompletedAt", created_at AS "CreatedAt"
            FROM nexora.document_signature_request WHERE id = {id} AND company_id = {Company}
            """).FirstOrDefaultAsync(ct);
        if (req is null) return Error.NotFound("signature.not_found", "Demande introuvable.");
        return Result.Success(new SignatureRequestDto(req.Id, req.DocumentId, req.Status, req.CompletedAt, await LoadSignersAsync(id, ct), req.CreatedAt));
    }

    public async Task<IReadOnlyList<SignatureRequestDto>> ListAsync(string? status, CancellationToken ct = default)
    {
        var rows = status is null
            ? await db.Database.SqlQueryRaw<RequestRow>("""
                SELECT id AS "Id", document_id AS "DocumentId", status AS "Status", completed_at AS "CompletedAt", created_at AS "CreatedAt"
                FROM nexora.document_signature_request WHERE company_id = {0} ORDER BY created_at DESC LIMIT 200
                """, Company).ToListAsync(ct)
            : await db.Database.SqlQueryRaw<RequestRow>("""
                SELECT id AS "Id", document_id AS "DocumentId", status AS "Status", completed_at AS "CompletedAt", created_at AS "CreatedAt"
                FROM nexora.document_signature_request WHERE company_id = {0} AND status = {1} ORDER BY created_at DESC LIMIT 200
                """, Company, status.Trim().ToLowerInvariant()).ToListAsync(ct);

        var result = new List<SignatureRequestDto>();
        foreach (var r in rows) result.Add(new SignatureRequestDto(r.Id, r.DocumentId, r.Status, r.CompletedAt, await LoadSignersAsync(r.Id, ct), r.CreatedAt));
        return result;
    }

    public async Task<Result<SignatureRequestDto>> SignAsync(Guid requestId, Guid signerId, SignDocumentRequest request, CancellationToken ct = default)
    {
        var signer = await db.Database.SqlQuery<SignerLookupRow>($"""
            SELECT sequence_no AS "SequenceNo", status AS "Status", user_id AS "UserId"
            FROM nexora.document_signer WHERE id = {signerId} AND signature_request_id = {requestId} AND company_id = {Company}
            """).FirstOrDefaultAsync(ct);
        if (signer is null) return Error.NotFound("signature.signer_not_found", "Signataire introuvable.");
        if (signer.Status != "pending") return Error.Conflict("signature.already_decided", "Ce signataire a déjà statué.");
        if (signer.UserId is { } uid && uid != tenant.UserId) return Error.Forbidden("signature.not_you", "Vous n'êtes pas ce signataire.");

        var earlierPending = await db.Database.SqlQuery<bool>($"""
            SELECT EXISTS(SELECT 1 FROM nexora.document_signer
                WHERE signature_request_id = {requestId} AND sequence_no < {signer.SequenceNo} AND status = 'pending') AS "Value"
            """).FirstAsync(ct);
        if (earlierPending) return Error.Conflict("signature.order", "Un signataire précédent doit signer avant vous (§21).");

        var docHash = await db.Database.SqlQuery<string>($"""
            SELECT v.sha256_hash AS "Value" FROM nexora.document_signature_request r
            JOIN nexora.document d ON d.id = r.document_id JOIN nexora.document_version v ON v.id = d.current_version_id
            WHERE r.id = {requestId}
            """).FirstAsync(ct);

        var proof = JsonSerializer.Serialize(new
        {
            declaredIdentity = tenant.UserId, authentication = "nexora_session", signedAt = clock.UtcNow,
            documentHash = docHash, note = request.Note,
        }, Web);

        await db.Database.ExecuteSqlAsync($"""
            UPDATE nexora.document_signer SET status = 'signed', signed_at = now(), proof = {proof}::jsonb WHERE id = {signerId}
            """, ct);

        var remaining = await db.Database.SqlQuery<bool>($"""
            SELECT EXISTS(SELECT 1 FROM nexora.document_signer WHERE signature_request_id = {requestId} AND status = 'pending') AS "Value"
            """).FirstAsync(ct);
        if (!remaining)
        {
            await db.Database.ExecuteSqlAsync($"""
                UPDATE nexora.document_signature_request SET status = 'completed', completed_at = now(), updated_at = now() WHERE id = {requestId}
                """, ct);
            await db.Database.ExecuteSqlAsync($"""
                UPDATE nexora.document SET status = 'signed', locked = true, locked_reason = 'Document signé (§18)', updated_at = now()
                WHERE id = (SELECT document_id FROM nexora.document_signature_request WHERE id = {requestId})
                """, ct);
        }
        await uow.SaveChangesAsync(ct);
        return await GetAsync(requestId, ct);
    }

    public async Task<Result<SignatureRequestDto>> DeclineAsync(Guid requestId, Guid signerId, string reason, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(reason)) return Error.Validation("signature.decline_reason", "Motif requis.");
        var rows = await db.Database.ExecuteSqlAsync($"""
            UPDATE nexora.document_signer SET status = 'declined', declined_reason = {reason}
            WHERE id = {signerId} AND signature_request_id = {requestId} AND company_id = {Company} AND status = 'pending'
            """, ct);
        if (rows == 0) return Error.NotFound("signature.signer_not_found", "Signataire introuvable ou déjà statué.");

        var cancelReason = $"Refus de signature : {reason}";
        await db.Database.ExecuteSqlAsync($"""
            UPDATE nexora.document_signature_request SET status = 'cancelled', cancelled_reason = {cancelReason}, updated_at = now()
            WHERE id = {requestId}
            """, ct);
        await uow.SaveChangesAsync(ct);
        return await GetAsync(requestId, ct);
    }

    public async Task<Result> CancelAsync(Guid requestId, string reason, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(reason)) return Error.Validation("signature.cancel_reason", "Motif requis.");
        var rows = await db.Database.ExecuteSqlAsync($"""
            UPDATE nexora.document_signature_request SET status = 'cancelled', cancelled_reason = {reason}, updated_at = now()
            WHERE id = {requestId} AND company_id = {Company} AND status = 'pending'
            """, ct);
        if (rows == 0) return Error.NotFound("signature.not_found", "Demande introuvable ou déjà close.");
        await uow.SaveChangesAsync(ct);
        return Result.Success();
    }

    public async Task<int> ExpireDueAsync(CancellationToken ct = default)
    {
        var expired = await db.Database.ExecuteSqlAsync($"""
            UPDATE nexora.document_signature_request SET status = 'expired', updated_at = now()
            WHERE status = 'pending' AND id IN (
                SELECT DISTINCT signature_request_id FROM nexora.document_signer WHERE status = 'pending' AND due_at IS NOT NULL AND due_at < now()
            )
            """, ct);
        if (expired > 0) await uow.SaveChangesAsync(ct);
        return expired;
    }

    private async Task<IReadOnlyList<SignerDto>> LoadSignersAsync(Guid requestId, CancellationToken ct)
    {
        var rows = await db.Database.SqlQuery<SignerRow>($"""
            SELECT id AS "Id", sequence_no AS "SequenceNo", signer_type AS "SignerType", user_id AS "UserId",
                   external_name AS "ExternalName", external_email::text AS "ExternalEmail", role AS "Role",
                   status AS "Status", due_at AS "DueAt", signed_at AS "SignedAt", declined_reason AS "DeclinedReason"
            FROM nexora.document_signer WHERE signature_request_id = {requestId} ORDER BY sequence_no
            """).ToListAsync(ct);
        return rows.Select(r => new SignerDto(r.Id, r.SequenceNo, r.SignerType, r.UserId, r.ExternalName, r.ExternalEmail,
            r.Role, r.Status, r.DueAt, r.SignedAt, r.DeclinedReason)).ToList();
    }
}
