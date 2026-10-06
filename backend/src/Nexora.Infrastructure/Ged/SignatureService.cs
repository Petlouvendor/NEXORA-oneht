using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Nexora.Application.Abstractions;
using Nexora.Application.Ged;
using Nexora.Application.Notifications;
using Nexora.Contracts.Ged;
using Nexora.Contracts.Notifications;
using Nexora.Domain.Common;
using Nexora.Infrastructure.Customers;
using Nexora.Infrastructure.Persistence;

namespace Nexora.Infrastructure.Ged;

/// <summary>
/// Signature multi-signataires (Phase 45 §19-22 ; finalisation phase 10) — document → demande → destinataire →
/// signature / refus → archivage. Signature simple horodatée : identité authentifiée NEXORA, hash de la VERSION
/// demandée, journal (§22) — jamais présentée comme une qualification juridique particulière (§19).
/// <list type="bullet">
/// <item>L'ordre des signataires est respecté (§21) ; seul le signataire désigné signe ou refuse pour lui-même.</item>
/// <item>Signataire externe : NEXORA n'envoie pas de lien de signature ; sa signature (ou son refus) est
/// <b>recueillie</b> par le demandeur ou un gestionnaire GED, et la preuve le dit (« recorded_on_behalf »).</item>
/// <item>Le document est verrouillé pendant la signature ; refus, annulation et expiration le libèrent ;
/// une fois signé, il reste verrouillé définitivement.</item>
/// <item>Le destinataire est notifié quand c'est son tour ; le demandeur à l'issue.</item>
/// </list>
/// </summary>
public sealed class SqlSignatureService(
    NexoraDbContext db, ITenantContext tenant, IUnitOfWork uow, IDateTimeProvider clock,
    IPermissionService permissions, INotificationService notifications, ILogger<SqlSignatureService> logger) : ISignatureService
{
    public const string SigningLock = "En cours de signature (§18)";
    public const string SignedLock = "Document signé (§18)";
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);
    private Guid Company => tenant.ActiveCompanyId ?? throw TenantErrors.ActiveCompanyRequired();

    private sealed record DocRow(string Name, string Status, Guid? CurrentVersionId, int? VersionNo, DateTimeOffset? DeletionRequestedAt);
    private sealed record RequestRow(Guid Id, Guid DocumentId, string Status, DateTimeOffset? CompletedAt, DateTimeOffset CreatedAt,
        string DocumentName, Guid? VersionId, int? VersionNo, Guid? RequestedById, string? RequestedByName, string? Message,
        string? CancelledReason, string? CancelledByName);
    private sealed record SignerRow(Guid Id, int SequenceNo, string SignerType, Guid? UserId, string? ExternalName,
        string? ExternalEmail, string? Role, string Status, DateTimeOffset? DueAt, DateTimeOffset? SignedAt, string? DeclinedReason,
        string? UserName, string? RecordedByName, DateTimeOffset? DeclinedAt);

    // ------------------------------------------------------------------ candidats

    public async Task<IReadOnlyList<SignerCandidateDto>> CandidatesAsync(CancellationToken ct = default)
    {
        var ids = await CustomerAccountRules.HoldersAsync(db, tenant, Application.Security.Permissions.Ged.Sign, null, ct);
        if (ids.Count == 0) return [];
        return await db.Database.SqlQuery<SignerCandidateDto>($"""
            SELECT id AS "Id", full_name AS "FullName", email::text AS "Email" FROM nexora.app_user
            WHERE id = ANY({ids.ToArray()}) ORDER BY full_name
            """).ToListAsync(ct);
    }

    // ------------------------------------------------------------------ demande

    public async Task<Result<SignatureRequestDto>> RequestAsync(Guid documentId, CreateSignatureRequestRequest request, CancellationToken ct = default)
    {
        if (request.Signers is null || request.Signers.Count == 0)
            return Error.Validation("signature.signers", "Au moins un signataire requis (§21).");
        if (request.Signers.Count > 10) return Error.Validation("signature.signers", "Dix signataires au plus par demande.");
        if (request.Message is { Length: > 500 }) return Error.Validation("signature.message", "Message limité à 500 caractères.");

        var doc = await db.Database.SqlQuery<DocRow>($"""
            SELECT d.name AS "Name", d.status AS "Status", d.current_version_id AS "CurrentVersionId", v.version_no AS "VersionNo",
                   d.deletion_requested_at AS "DeletionRequestedAt"
            FROM nexora.document d LEFT JOIN nexora.document_version v ON v.id = d.current_version_id
            WHERE d.id = {documentId} AND d.company_id = {Company}
            """).FirstOrDefaultAsync(ct);
        if (doc is null) return Error.NotFound("document.not_found", "Document introuvable.");
        if (doc.CurrentVersionId is null) return Error.Conflict("signature.no_version", "Le document n'a aucune version à signer.");
        if (doc.Status is not ("draft" or "approved"))
            return Error.Conflict("signature.document_status", doc.Status switch
            {
                "signed" => "Ce document est déjà signé.",
                "pending_review" => "Le document est en revue : approuvez-le avant de le faire signer.",
                "archived" => "Le document est archivé : restaurez-le avant de le faire signer.",
                _ => "Ce document ne peut pas être soumis à signature.",
            });
        if (doc.DeletionRequestedAt is not null)
            return Error.Conflict("signature.document_status", "Une suppression est demandée pour ce document.");
        if (await db.Database.SqlQuery<bool>($"""
                SELECT EXISTS(SELECT 1 FROM nexora.document_signature_request WHERE document_id = {documentId} AND status = 'pending') AS "Value"
                """).FirstAsync(ct))
            return Error.Conflict("signature.pending_exists", "Une demande de signature est déjà en cours pour ce document.");

        var allowed = (await CustomerAccountRules.HoldersAsync(db, tenant, Application.Security.Permissions.Ged.Sign, null, ct)).ToHashSet();
        var seen = new HashSet<Guid>();
        foreach (var (s, i) in request.Signers.Select((s, i) => (s, i + 1)))
        {
            var type = (s.SignerType ?? "").Trim().ToLowerInvariant();
            if (type is not ("user" or "external")) return Error.Validation("signature.signer_type", $"Signataire {i} : type invalide.");
            if (type == "user")
            {
                if (s.UserId is not { } uid) return Error.Validation("signature.signer_user", $"Signataire {i} : choisissez l'utilisateur.");
                if (!allowed.Contains(uid))
                    return Error.Validation("signature.signer_not_allowed", $"Signataire {i} : cet utilisateur n'a pas le droit de signer dans cette entreprise.");
                if (!seen.Add(uid)) return Error.Validation("signature.signer_duplicate", $"Signataire {i} : déjà présent dans la liste.");
            }
            else
            {
                if (string.IsNullOrWhiteSpace(s.ExternalName)) return Error.Validation("signature.signer_name", $"Signataire {i} : nom requis.");
                if (!string.IsNullOrWhiteSpace(s.ExternalEmail) && !s.ExternalEmail.Contains('@'))
                    return Error.Validation("signature.signer_email", $"Signataire {i} : e-mail invalide.");
            }
            if (s.DueAt is { } due && due <= clock.UtcNow)
                return Error.Validation("signature.signer_due", $"Signataire {i} : l'échéance doit être dans le futur.");
        }

        var requestId = Guid.NewGuid();
        var message = string.IsNullOrWhiteSpace(request.Message) ? null : request.Message.Trim();
        await uow.ExecuteInTransactionAsync(async token =>
        {
            await db.Database.ExecuteSqlAsync($"""
                INSERT INTO nexora.document_signature_request (id, company_id, document_id, requested_by, version_id, message)
                VALUES ({requestId}, {Company}, {documentId}, {tenant.UserId}, {doc.CurrentVersionId}, {message})
                """, token);
            var seq = 1;
            foreach (var s in request.Signers)
            {
                var type = s.SignerType.Trim().ToLowerInvariant();
                await db.Database.ExecuteSqlAsync($"""
                    INSERT INTO nexora.document_signer
                        (id, company_id, signature_request_id, sequence_no, signer_type, user_id, external_name, external_email, role, due_at)
                    VALUES ({Guid.NewGuid()}, {Company}, {requestId}, {seq}, {type}, {(type == "user" ? s.UserId : null)},
                            {(type == "external" ? s.ExternalName!.Trim() : null)}, {(type == "external" ? s.ExternalEmail?.Trim() : null)},
                            {s.Role?.Trim()}, {s.DueAt})
                    """, token);
                seq++;
            }
            await db.Database.ExecuteSqlAsync($"""
                UPDATE nexora.document SET locked = true, locked_reason = {SigningLock}, updated_at = now()
                WHERE id = {documentId} AND NOT locked
                """, token);
            return 0;
        }, ct);
        await uow.SaveChangesAsync(ct);

        var created = (await GetAsync(requestId, ct)).Value!;
        await NotifyCurrentSignerAsync(created, ct);
        return Result.Success(created);
    }

    // ------------------------------------------------------------------ lecture

    private IQueryable<RequestRow> Requests(Guid? id, string? status, Guid? documentId) => db.Database.SqlQuery<RequestRow>($"""
        SELECT r.id AS "Id", r.document_id AS "DocumentId", r.status AS "Status", r.completed_at AS "CompletedAt", r.created_at AS "CreatedAt",
               d.name AS "DocumentName", r.version_id AS "VersionId", v.version_no AS "VersionNo", r.requested_by AS "RequestedById",
               ru.full_name AS "RequestedByName", r.message AS "Message", r.cancelled_reason AS "CancelledReason",
               cu.full_name AS "CancelledByName"
        FROM nexora.document_signature_request r
        JOIN nexora.document d ON d.id = r.document_id
        LEFT JOIN nexora.document_version v ON v.id = r.version_id
        LEFT JOIN nexora.app_user ru ON ru.id = r.requested_by
        LEFT JOIN nexora.app_user cu ON cu.id = r.cancelled_by
        WHERE r.company_id = {Company}
          AND ({id}::uuid IS NULL OR r.id = {id}::uuid)
          AND ({status}::text IS NULL OR r.status = {status}::text)
          AND ({documentId}::uuid IS NULL OR r.document_id = {documentId}::uuid)
        ORDER BY r.created_at DESC
        LIMIT 200
        """);

    public async Task<Result<SignatureRequestDto>> GetAsync(Guid id, CancellationToken ct = default)
    {
        var req = await Requests(id, null, null).FirstOrDefaultAsync(ct);
        if (req is null) return Error.NotFound("signature.not_found", "Demande introuvable.");
        return Result.Success(await MapAsync(req, ct));
    }

    public async Task<IReadOnlyList<SignatureRequestDto>> ListAsync(string? status, bool mine = false, Guid? documentId = null, CancellationToken ct = default)
    {
        var st = string.IsNullOrWhiteSpace(status) ? null : status.Trim().ToLowerInvariant();
        if (mine) st = "pending";
        var result = new List<SignatureRequestDto>();
        foreach (var r in await Requests(null, st, documentId).ToListAsync(ct))
        {
            var dto = await MapAsync(r, ct);
            if (!mine || dto.IsMyTurn) result.Add(dto);
        }
        return result;
    }

    private async Task<SignatureRequestDto> MapAsync(RequestRow r, CancellationToken ct)
    {
        var signers = await LoadSignersAsync(r.Id, ct);
        var current = r.Status == "pending" ? signers.FirstOrDefault(s => s.Status == "pending") : null;
        var myTurn = current is not null && (current.SignerType == "user"
            ? current.UserId == tenant.UserId
            : r.RequestedById == tenant.UserId);
        return new SignatureRequestDto(r.Id, r.DocumentId, r.Status, r.CompletedAt, signers, r.CreatedAt,
            r.DocumentName, r.VersionNo, r.RequestedById, r.RequestedByName, r.Message, r.CancelledReason, r.CancelledByName,
            current?.Id, myTurn);
    }

    // ------------------------------------------------------------------ signature / refus

    private sealed record Turn(RequestRow Request, SignerRow Signer);

    /// <summary>Contrôles communs à la signature et au refus : demande en cours, signataire en attente, c'est son
    /// tour, et l'utilisateur courant est bien ce signataire (ou recueille la réponse d'un externe).</summary>
    private async Task<Result<Turn>> TurnAsync(Guid requestId, Guid signerId, CancellationToken ct)
    {
        var req = await Requests(requestId, null, null).FirstOrDefaultAsync(ct);
        if (req is null) return Error.NotFound("signature.not_found", "Demande introuvable.");
        if (req.Status != "pending")
            return Error.Conflict("signature.closed", req.Status switch
            {
                "completed" => "Cette demande est déjà entièrement signée.",
                "expired" => "Cette demande a expiré.",
                _ => "Cette demande a été annulée.",
            });
        var signers = await LoadSignersAsync(requestId, ct);
        var signer = signers.FirstOrDefault(s => s.Id == signerId);
        if (signer is null) return Error.NotFound("signature.signer_not_found", "Signataire introuvable.");
        if (signer.Status != "pending") return Error.Conflict("signature.already_decided", "Ce signataire a déjà répondu.");
        if (signers.Any(s => s.SequenceNo < signer.SequenceNo && s.Status == "pending"))
            return Error.Conflict("signature.order", "Un signataire précédent doit répondre avant (§21).");
        if (signer.SignerType == "user")
        {
            if (signer.UserId != tenant.UserId) return Error.Forbidden("signature.not_you", "Seul le signataire désigné peut répondre pour lui-même.");
        }
        else if (req.RequestedById != tenant.UserId
                 && !(await permissions.CurrentPermissionsAsync(tenant, ct)).Contains(Application.Security.Permissions.Ged.Manage))
            return Error.Forbidden("signature.not_recorder", "La réponse d'un signataire externe est recueillie par le demandeur ou un gestionnaire GED.");
        var row = new SignerRow(signer.Id, signer.SequenceNo, signer.SignerType, signer.UserId, signer.ExternalName, signer.ExternalEmail,
            signer.Role, signer.Status, signer.DueAt, signer.SignedAt, signer.DeclinedReason, signer.UserName, signer.RecordedByName, signer.DeclinedAt);
        return Result.Success(new Turn(req, row));
    }

    public async Task<Result<SignatureRequestDto>> SignAsync(Guid requestId, Guid signerId, SignDocumentRequest request, CancellationToken ct = default)
    {
        var turn = await TurnAsync(requestId, signerId, ct);
        if (!turn.IsSuccess) return turn.Error!;
        var (req, signer) = (turn.Value!.Request, turn.Value.Signer);
        if (request.Note is { Length: > 500 }) return Error.Validation("signature.note", "Note limitée à 500 caractères.");

        var version = await db.Database.SqlQuery<VersionRow>($"""
            SELECT v.id AS "Id", v.version_no AS "VersionNo", v.sha256_hash AS "Hash", d.current_version_id AS "CurrentId"
            FROM nexora.document_version v JOIN nexora.document d ON d.id = v.document_id
            WHERE v.id = {req.VersionId}
            """).FirstOrDefaultAsync(ct);
        if (version is null) return Error.Conflict("signature.no_version", "Version à signer introuvable.");
        if (version.CurrentId != version.Id)
            return Error.Conflict("signature.version_changed", "Le document a changé depuis la demande : annulez-la et relancez une demande.");

        var external = signer.SignerType == "external";
        var proof = JsonSerializer.Serialize(new
        {
            mode = external ? "recorded_on_behalf" : "authenticated",
            signer = external ? signer.ExternalName : null,
            signerUserId = signer.UserId,
            recordedBy = external ? tenant.UserId : null,
            authentication = "nexora_session",
            signedAt = clock.UtcNow,
            documentVersion = version.VersionNo,
            documentHash = version.Hash,
            note = request.Note,
        }, Web);

        bool completed = false;
        await uow.ExecuteInTransactionAsync(async token =>
        {
            await db.Database.ExecuteSqlAsync($"""
                UPDATE nexora.document_signer SET status = 'signed', signed_at = now(), proof = {proof}::jsonb,
                       recorded_by = {(external ? tenant.UserId : null)}
                WHERE id = {signerId} AND status = 'pending'
                """, token);
            completed = !await db.Database.SqlQuery<bool>($"""
                SELECT EXISTS(SELECT 1 FROM nexora.document_signer WHERE signature_request_id = {requestId} AND status = 'pending') AS "Value"
                """).FirstAsync(token);
            if (completed)
            {
                await db.Database.ExecuteSqlAsync($"""
                    UPDATE nexora.document_signature_request SET status = 'completed', completed_at = now(), updated_at = now() WHERE id = {requestId}
                    """, token);
                await db.Database.ExecuteSqlAsync($"""
                    UPDATE nexora.document SET status = 'signed', locked = true, locked_reason = {SignedLock}, updated_at = now()
                    WHERE id = {req.DocumentId}
                    """, token);
            }
            return 0;
        }, ct);
        await uow.SaveChangesAsync(ct);

        var dto = (await GetAsync(requestId, ct)).Value!;
        if (completed)
            await NotifyRequesterAsync(dto, "Document signé", $"« {dto.DocumentName} » est signé par tous les signataires.", "success", ct);
        else
            await NotifyCurrentSignerAsync(dto, ct);
        return Result.Success(dto);
    }

    private sealed record VersionRow(Guid Id, int VersionNo, string Hash, Guid? CurrentId);

    public async Task<Result<SignatureRequestDto>> DeclineAsync(Guid requestId, Guid signerId, string reason, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(reason) || reason.Trim().Length < 3) return Error.Validation("signature.decline_reason", "Motif du refus requis.");
        if (reason.Length > 500) return Error.Validation("signature.decline_reason", "Motif limité à 500 caractères.");
        var turn = await TurnAsync(requestId, signerId, ct);
        if (!turn.IsSuccess) return turn.Error!;
        var (req, signer) = (turn.Value!.Request, turn.Value.Signer);
        var who = signer.SignerType == "external" ? signer.ExternalName : signer.UserName;
        var cancelReason = $"Refus de {who} : {reason.Trim()}";

        await uow.ExecuteInTransactionAsync(async token =>
        {
            await db.Database.ExecuteSqlAsync($"""
                UPDATE nexora.document_signer SET status = 'declined', declined_reason = {reason.Trim()}, declined_at = now(),
                       recorded_by = {(signer.SignerType == "external" ? tenant.UserId : null)}
                WHERE id = {signerId} AND status = 'pending'
                """, token);
            await CloseAsync(requestId, req.DocumentId, "cancelled", cancelReason, token);
            return 0;
        }, ct);
        await uow.SaveChangesAsync(ct);

        var dto = (await GetAsync(requestId, ct)).Value!;
        await NotifyRequesterAsync(dto, "Signature refusée", cancelReason, "warning", ct);
        return Result.Success(dto);
    }

    public async Task<Result> CancelAsync(Guid requestId, string reason, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(reason) || reason.Trim().Length < 3) return Error.Validation("signature.cancel_reason", "Motif d'annulation requis.");
        var req = await Requests(requestId, null, null).FirstOrDefaultAsync(ct);
        if (req is null) return Error.NotFound("signature.not_found", "Demande introuvable.");
        if (req.Status != "pending") return Error.Conflict("signature.closed", "Cette demande est déjà close.");
        if (req.RequestedById != tenant.UserId
            && !(await permissions.CurrentPermissionsAsync(tenant, ct)).Contains(Application.Security.Permissions.Ged.Manage))
            return Error.Forbidden("signature.not_requester", "Seul le demandeur ou un gestionnaire GED peut annuler la demande.");

        await uow.ExecuteInTransactionAsync(async token =>
        {
            await CloseAsync(requestId, req.DocumentId, "cancelled", reason.Trim(), token);
            return 0;
        }, ct);
        await uow.SaveChangesAsync(ct);
        return Result.Success();
    }

    /// <summary>Clôt la demande sans signature complète et libère le document du verrou de signature
    /// (un verrou posé pour une autre raison reste en place).</summary>
    private async Task CloseAsync(Guid requestId, Guid documentId, string status, string? reason, CancellationToken ct)
    {
        await db.Database.ExecuteSqlAsync($"""
            UPDATE nexora.document_signature_request
               SET status = {status}, cancelled_reason = {reason}, cancelled_by = {tenant.UserId}, cancelled_at = now(), updated_at = now()
             WHERE id = {requestId} AND status = 'pending'
            """, ct);
        await db.Database.ExecuteSqlAsync($"""
            UPDATE nexora.document SET locked = false, locked_reason = NULL, updated_at = now()
            WHERE id = {documentId} AND locked_reason = {SigningLock}
            """, ct);
    }

    public async Task<int> ExpireDueAsync(CancellationToken ct = default)
    {
        var due = await db.Database.SqlQuery<ExpireRow>($"""
            SELECT DISTINCT r.id AS "Id", r.document_id AS "DocumentId"
            FROM nexora.document_signature_request r JOIN nexora.document_signer s ON s.signature_request_id = r.id
            WHERE r.company_id = {Company} AND r.status = 'pending'
              AND s.status = 'pending' AND s.due_at IS NOT NULL AND s.due_at < now()
            """).ToListAsync(ct);
        foreach (var r in due)
        {
            await db.Database.ExecuteSqlAsync($"""
                UPDATE nexora.document_signature_request SET status = 'expired', cancelled_reason = 'Échéance de signature dépassée',
                       updated_at = now()
                WHERE id = {r.Id} AND status = 'pending'
                """, ct);
            await db.Database.ExecuteSqlAsync($"""
                UPDATE nexora.document SET locked = false, locked_reason = NULL, updated_at = now()
                WHERE id = {r.DocumentId} AND locked_reason = {SigningLock}
                """, ct);
        }
        if (due.Count > 0) await uow.SaveChangesAsync(ct);
        foreach (var r in due)
            if ((await GetAsync(r.Id, ct)).Value is { } dto)
                await NotifyRequesterAsync(dto, "Signature expirée", $"La demande de signature de « {dto.DocumentName} » a expiré.", "warning", ct);
        return due.Count;
    }

    private sealed record ExpireRow(Guid Id, Guid DocumentId);

    // ------------------------------------------------------------------ notifications

    private async Task NotifyCurrentSignerAsync(SignatureRequestDto r, CancellationToken ct)
    {
        var current = r.Signers.FirstOrDefault(s => s.Id == r.CurrentSignerId);
        if (current?.UserId is not { } uid || uid == tenant.UserId) return;
        await SafeNotifyAsync(new NotifyRequest(
            Category: "ged.signature", Recipients: [uid], TemplateKey: "ged.signature_requested",
            Variables: new Dictionary<string, string?>
            {
                ["document.name"] = r.DocumentName, ["document.version"] = r.VersionNo?.ToString(),
                ["requester.name"] = r.RequestedByName ?? "Un utilisateur",
                ["request.message"] = r.Message is null ? "" : $" Message : « {r.Message} »",
            },
            Priority: 2, Severity: "info", Source: "workflow", ActionUrl: "/ged?tab=signatures",
            ReferenceType: "signature_request", ReferenceId: r.Id, DedupKey: $"signature:{r.Id}:{current.Id}"), ct);
    }

    private async Task NotifyRequesterAsync(SignatureRequestDto r, string title, string detail, string severity, CancellationToken ct)
    {
        if (r.RequestedById is not { } uid || uid == tenant.UserId) return;
        await SafeNotifyAsync(new NotifyRequest(
            Category: "ged.signature", Recipients: [uid], TemplateKey: "ged.signature_outcome",
            Variables: new Dictionary<string, string?> { ["outcome.title"] = title, ["document.name"] = r.DocumentName, ["outcome.detail"] = detail },
            Priority: 3, Severity: severity, Source: "workflow", ActionUrl: "/ged?tab=signatures",
            ReferenceType: "signature_request", ReferenceId: r.Id, DedupKey: $"signature:{r.Id}:{r.Status}"), ct);
    }

    /// <summary>Une notification manquée ne doit jamais annuler une signature déjà enregistrée.</summary>
    private async Task SafeNotifyAsync(NotifyRequest request, CancellationToken ct)
    {
        try { await notifications.NotifyAsync(request, ct); }
        catch (Exception ex) { logger.LogWarning(ex, "Notification de signature non émise ({Ref})", request.ReferenceId); }
    }

    private async Task<IReadOnlyList<SignerDto>> LoadSignersAsync(Guid requestId, CancellationToken ct)
    {
        var rows = await db.Database.SqlQuery<SignerRow>($"""
            SELECT s.id AS "Id", s.sequence_no AS "SequenceNo", s.signer_type AS "SignerType", s.user_id AS "UserId",
                   s.external_name AS "ExternalName", s.external_email::text AS "ExternalEmail", s.role AS "Role",
                   s.status AS "Status", s.due_at AS "DueAt", s.signed_at AS "SignedAt", s.declined_reason AS "DeclinedReason",
                   u.full_name AS "UserName", rb.full_name AS "RecordedByName", s.declined_at AS "DeclinedAt"
            FROM nexora.document_signer s
            LEFT JOIN nexora.app_user u ON u.id = s.user_id
            LEFT JOIN nexora.app_user rb ON rb.id = s.recorded_by
            WHERE s.signature_request_id = {requestId} ORDER BY s.sequence_no
            """).ToListAsync(ct);
        return rows.Select(r => new SignerDto(r.Id, r.SequenceNo, r.SignerType, r.UserId, r.ExternalName, r.ExternalEmail,
            r.Role, r.Status, r.DueAt, r.SignedAt, r.DeclinedReason, r.UserName, r.RecordedByName, r.DeclinedAt)).ToList();
    }
}
