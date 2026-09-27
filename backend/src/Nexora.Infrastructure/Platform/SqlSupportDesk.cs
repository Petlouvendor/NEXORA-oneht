using Microsoft.EntityFrameworkCore;
using Nexora.Application.Abstractions;
using Nexora.Application.Platform;
using Nexora.Contracts.Platform;
using Nexora.Domain.Common;
using Nexora.Infrastructure.Persistence;

namespace Nexora.Infrastructure.Platform;

// =============================================================================
// Support plateforme (Phase 38 §37-38) : tickets et accès assisté avec consentement.
// =============================================================================
public sealed class SqlSupportDeskService(NexoraDbContext db, ITenantContext tenant, IUnitOfWork uow, IDateTimeProvider clock)
    : ISupportDeskService
{
    private static readonly string[] Priorities = ["p1", "p2", "p3", "p4"];
    private static readonly string[] Statuses = ["open", "pending", "resolved", "closed"];

    private sealed record TicketRow(Guid Id, string TicketRef, Guid? CustomerId, Guid? CompanyId, string Subject, string Priority,
        string Status, string? RequesterEmail, Guid? AssignedTo, bool Escalated, DateTimeOffset? ResolvedAt, DateTimeOffset CreatedAt);
    private sealed record MessageRow(Guid Id, Guid? AuthorUserId, string Body, bool IsInternal, DateTimeOffset CreatedAt);
    private sealed record GrantRow(Guid Id, Guid CustomerId, Guid? CompanyId, Guid StaffUserId, string Reason, string Scope,
        DateTimeOffset? ApprovedAt, DateTimeOffset ExpiresAt, DateTimeOffset? RevokedAt, DateTimeOffset CreatedAt);

    private const string TicketSelect = """
        SELECT id AS "Id", ticket_ref AS "TicketRef", customer_id AS "CustomerId", company_id AS "CompanyId", subject AS "Subject",
               priority AS "Priority", status AS "Status", requester_email AS "RequesterEmail", assigned_to AS "AssignedTo",
               escalated AS "Escalated", resolved_at AS "ResolvedAt", created_at AS "CreatedAt"
        FROM nexora.support_ticket
        """;

    // ------------------------------------------------------------- Tickets

    public async Task<IReadOnlyList<SupportTicketDto>> ListTicketsAsync(Guid? customerId, string? status, CancellationToken ct = default)
    {
        var rows = await db.Database.SqlQuery<TicketRow>($"""
            SELECT id AS "Id", ticket_ref AS "TicketRef", customer_id AS "CustomerId", company_id AS "CompanyId", subject AS "Subject",
                   priority AS "Priority", status AS "Status", requester_email AS "RequesterEmail", assigned_to AS "AssignedTo",
                   escalated AS "Escalated", resolved_at AS "ResolvedAt", created_at AS "CreatedAt"
            FROM nexora.support_ticket
            WHERE ({customerId}::uuid IS NULL OR customer_id = {customerId}::uuid)
              AND ({status}::text IS NULL OR status = {status}::text)
            ORDER BY (status IN ('open','pending')) DESC, created_at DESC LIMIT 300
            """).ToListAsync(ct);
        var result = new List<SupportTicketDto>(rows.Count);
        foreach (var r in rows) result.Add(await ToDtoAsync(r, includeInternal: customerId is null, ct));
        return result;
    }

    public async Task<Result<SupportTicketDto>> GetTicketAsync(Guid id, Guid? restrictToCustomer, CancellationToken ct = default)
    {
        var row = await db.Database.SqlQueryRaw<TicketRow>(TicketSelect + " WHERE id = {0}", id).FirstOrDefaultAsync(ct);
        if (row is null) return Error.NotFound("platform.ticket", "Ticket introuvable.");
        if (restrictToCustomer is { } rc && rc != row.CustomerId) return Error.Forbidden("platform.not_your_ticket", "Ticket hors de votre périmètre.");
        return Result.Success(await ToDtoAsync(row, includeInternal: restrictToCustomer is null, ct));
    }

    public async Task<Result<SupportTicketDto>> CreateTicketAsync(CreateTicketRequest r, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(r.Subject)) return Error.Validation("platform.ticket_subject", "Objet requis.");
        if (!Priorities.Contains(r.Priority)) return Error.Validation("platform.ticket_priority", "Priorité invalide (p1..p4).");

        var seq = await db.Database.SqlQuery<long>($"""
            SELECT nextval('nexora.support_ticket_seq')::bigint AS "Value"
            """).FirstAsync(ct);
        var reference = $"NXS-{seq:D6}";
        var id = Guid.NewGuid();

        await uow.ExecuteInTransactionAsync(async token =>
        {
            await db.Database.ExecuteSqlAsync($"""
                INSERT INTO nexora.support_ticket (id, ticket_ref, customer_id, company_id, subject, priority, requester_email, opened_by)
                VALUES ({id}, {reference}, {r.CustomerId}, {r.CompanyId}, {r.Subject.Trim()}, {r.Priority}, {r.RequesterEmail}, {tenant.UserId})
                """, token);
            if (!string.IsNullOrWhiteSpace(r.Message))
                await db.Database.ExecuteSqlAsync($"""
                    INSERT INTO nexora.support_ticket_message (ticket_id, author_user_id, body, is_internal)
                    VALUES ({id}, {tenant.UserId}, {r.Message.Trim()}, false)
                    """, token);
            return 0;
        }, ct);
        await uow.SaveChangesAsync(ct);
        return await GetTicketAsync(id, null, ct);
    }

    public async Task<Result<SupportTicketDto>> ReplyAsync(Guid id, TicketReplyRequest r, Guid? restrictToCustomer, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(r.Body)) return Error.Validation("platform.ticket_body", "Message vide.");
        var row = await db.Database.SqlQueryRaw<TicketRow>(TicketSelect + " WHERE id = {0}", id).FirstOrDefaultAsync(ct);
        if (row is null) return Error.NotFound("platform.ticket", "Ticket introuvable.");
        if (restrictToCustomer is { } rc && rc != row.CustomerId) return Error.Forbidden("platform.not_your_ticket", "Ticket hors de votre périmètre.");
        var internalNote = r.Internal && restrictToCustomer is null;   // un client ne pose jamais de note interne (§37)

        await db.Database.ExecuteSqlAsync($"""
            INSERT INTO nexora.support_ticket_message (ticket_id, author_user_id, body, is_internal)
            VALUES ({id}, {tenant.UserId}, {r.Body.Trim()}, {internalNote})
            """, ct);
        await db.Database.ExecuteSqlAsync($"""
            UPDATE nexora.support_ticket SET status = CASE WHEN status IN ('resolved','closed') THEN 'open' ELSE status END, updated_at = now()
            WHERE id = {id}
            """, ct);
        await uow.SaveChangesAsync(ct);
        return await GetTicketAsync(id, restrictToCustomer, ct);
    }

    public async Task<Result<SupportTicketDto>> UpdateTicketAsync(Guid id, UpdateTicketRequest r, CancellationToken ct = default)
    {
        var row = await db.Database.SqlQueryRaw<TicketRow>(TicketSelect + " WHERE id = {0}", id).FirstOrDefaultAsync(ct);
        if (row is null) return Error.NotFound("platform.ticket", "Ticket introuvable.");
        if (r.Status is { } st && !Statuses.Contains(st)) return Error.Validation("platform.ticket_status", "Statut invalide.");
        if (r.Priority is { } pr && !Priorities.Contains(pr)) return Error.Validation("platform.ticket_priority", "Priorité invalide.");

        var resolved = r.Status is "resolved" or "closed";
        await db.Database.ExecuteSqlAsync($"""
            UPDATE nexora.support_ticket SET
                status = COALESCE({r.Status}, status),
                priority = COALESCE({r.Priority}, priority),
                assigned_to = COALESCE({r.AssignedTo}, assigned_to),
                escalated = COALESCE({r.Escalated}, escalated),
                resolved_at = CASE WHEN {resolved} THEN COALESCE(resolved_at, now()) ELSE resolved_at END,
                updated_at = now()
            WHERE id = {id}
            """, ct);
        await uow.SaveChangesAsync(ct);
        return await GetTicketAsync(id, null, ct);
    }

    // ------------------------------------------------------------- Accès assisté (§38)

    public async Task<IReadOnlyList<SupportAccessGrantDto>> ListAccessGrantsAsync(Guid customerId, CancellationToken ct = default)
    {
        var rows = await db.Database.SqlQuery<GrantRow>($"""
            SELECT id AS "Id", customer_id AS "CustomerId", company_id AS "CompanyId", staff_user_id AS "StaffUserId",
                   reason AS "Reason", scope AS "Scope", approved_at AS "ApprovedAt", expires_at AS "ExpiresAt",
                   revoked_at AS "RevokedAt", created_at AS "CreatedAt"
            FROM nexora.support_access_grant WHERE customer_id = {customerId} ORDER BY created_at DESC
            """).ToListAsync(ct);
        return rows.Select(ToDto).ToList();
    }

    public async Task<Result<SupportAccessGrantDto>> RequestAccessAsync(RequestSupportAccessRequest r, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(r.Reason)) return Error.Validation("platform.access_reason", "Motif requis (§38).");
        if (r.Hours is < 1 or > 168) return Error.Validation("platform.access_hours", "Durée hors bornes (1–168 h).");
        if (r.Scope is not ("read" or "support_write")) return Error.Validation("platform.access_scope", "Périmètre invalide.");

        var customerExists = await db.Database.SqlQuery<bool>($"""
            SELECT EXISTS(SELECT 1 FROM nexora.platform_customer WHERE id = {r.CustomerId}) AS "Value"
            """).FirstAsync(ct);
        if (!customerExists) return Error.NotFound("platform.customer", "Client introuvable.");

        var id = Guid.NewGuid();
        await db.Database.ExecuteSqlAsync($"""
            INSERT INTO nexora.support_access_grant
                (id, customer_id, company_id, staff_user_id, reason, scope, requested_by, expires_at)
            VALUES ({id}, {r.CustomerId}, {r.CompanyId}, {r.StaffUserId}, {r.Reason.Trim()}, {r.Scope}, {tenant.UserId},
                    {clock.UtcNow.AddHours(r.Hours)})
            """, ct);
        await uow.SaveChangesAsync(ct);
        return Result.Success(ToDto((await LoadGrantAsync(id, ct))!));
    }

    public async Task<Result<SupportAccessGrantDto>> ApproveAccessAsync(Guid id, CancellationToken ct = default)
    {
        var g = await LoadGrantAsync(id, ct);
        if (g is null) return Error.NotFound("platform.access_grant", "Demande d'accès introuvable.");
        if (g.RevokedAt is not null) return Error.Conflict("platform.access_revoked", "Demande révoquée.");
        if (g.ApprovedAt is not null) return Result.Success(ToDto(g));

        await db.Database.ExecuteSqlAsync($"""
            UPDATE nexora.support_access_grant SET approved_at = {clock.UtcNow}, updated_at = now() WHERE id = {id}
            """, ct);
        await uow.SaveChangesAsync(ct);
        return Result.Success(ToDto((await LoadGrantAsync(id, ct))!));
    }

    public async Task<Result> RevokeAccessAsync(Guid id, CancellationToken ct = default)
    {
        var n = await db.Database.ExecuteSqlAsync($"""
            UPDATE nexora.support_access_grant SET revoked_at = {clock.UtcNow}, revoked_by = {tenant.UserId}, updated_at = now()
            WHERE id = {id} AND revoked_at IS NULL
            """, ct);
        await uow.SaveChangesAsync(ct);
        return n == 0 ? Error.NotFound("platform.access_grant", "Demande introuvable ou déjà révoquée.") : Result.Success();
    }

    // ------------------------------------------------------------- Helpers

    private async Task<SupportTicketDto> ToDtoAsync(TicketRow t, bool includeInternal, CancellationToken ct)
    {
        var messages = await db.Database.SqlQuery<MessageRow>($"""
            SELECT id AS "Id", author_user_id AS "AuthorUserId", body AS "Body", is_internal AS "IsInternal", created_at AS "CreatedAt"
            FROM nexora.support_ticket_message
            WHERE ticket_id = {t.Id} AND ({includeInternal} OR is_internal = false)
            ORDER BY created_at
            """).ToListAsync(ct);
        return new SupportTicketDto(t.Id, t.TicketRef, t.CustomerId, t.CompanyId, t.Subject, t.Priority, t.Status,
            t.RequesterEmail, t.AssignedTo, t.Escalated, t.ResolvedAt,
            messages.Select(m => new SupportMessageDto(m.Id, m.AuthorUserId, m.Body, m.IsInternal, m.CreatedAt)).ToList(),
            t.CreatedAt);
    }

    private async Task<GrantRow?> LoadGrantAsync(Guid id, CancellationToken ct)
        => await db.Database.SqlQuery<GrantRow>($"""
            SELECT id AS "Id", customer_id AS "CustomerId", company_id AS "CompanyId", staff_user_id AS "StaffUserId",
                   reason AS "Reason", scope AS "Scope", approved_at AS "ApprovedAt", expires_at AS "ExpiresAt",
                   revoked_at AS "RevokedAt", created_at AS "CreatedAt"
            FROM nexora.support_access_grant WHERE id = {id}
            """).FirstOrDefaultAsync(ct);

    private static SupportAccessGrantDto ToDto(GrantRow g) => new(g.Id, g.CustomerId, g.CompanyId, g.StaffUserId, g.Reason,
        g.Scope, g.ApprovedAt, g.ExpiresAt, g.RevokedAt, g.CreatedAt);
}
