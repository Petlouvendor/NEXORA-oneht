using Microsoft.EntityFrameworkCore;
using Nexora.Application.Abstractions;
using Nexora.Application.Operations;
using Nexora.Contracts.Operations;
using Nexora.Domain.Common;
using Nexora.Infrastructure.Persistence;

namespace Nexora.Infrastructure.Operations;

/// <summary>
/// Administration des deux files d'attente externes de l'entreprise active (Phase 41 §10-12, §51) :
/// <c>notification_outbox</c> (Phase 34 — email/SMS/webhook de notification) et
/// <c>outbound_webhook_delivery</c> (Phase 12 — webhooks sortants métier). Un message « mort »
/// (<c>dead</c> / <c>exhausted</c>) peut être rejoué ou supprimé — jamais silencieusement.
/// </summary>
public sealed class SqlQueueAdminService(NexoraDbContext db, ITenantContext tenant, IUnitOfWork uow) : IQueueAdminService
{
    private Guid Company => tenant.ActiveCompanyId ?? throw new InvalidOperationException("Entreprise active requise.");

    private sealed record NotifStatsRow(int Pending, int Failed, int Dead, int DeliveredLastHour, double? OldestPendingAgeMinutes);
    private sealed record WebhookStatsRow(int Pending, int Failed, int Dead, int DeliveredLastHour, double? OldestPendingAgeMinutes);

    public async Task<IReadOnlyList<QueueStatsDto>> StatsAsync(CancellationToken ct = default)
    {
        var company = Company;
        var n = await db.Database.SqlQuery<NotifStatsRow>($"""
            SELECT
              COUNT(*) FILTER (WHERE status IN ('pending','sending'))::int AS "Pending",
              COUNT(*) FILTER (WHERE status = 'failed')::int AS "Failed",
              COUNT(*) FILTER (WHERE status = 'dead')::int AS "Dead",
              COUNT(*) FILTER (WHERE status = 'sent' AND sent_at > now() - interval '1 hour')::int AS "DeliveredLastHour",
              EXTRACT(EPOCH FROM (now() - MIN(created_at) FILTER (WHERE status IN ('pending','sending','failed')))) / 60.0 AS "OldestPendingAgeMinutes"
            FROM nexora.notification_outbox WHERE company_id = {company}
            """).FirstAsync(ct);

        var w = await db.Database.SqlQuery<WebhookStatsRow>($"""
            SELECT
              COUNT(*) FILTER (WHERE status = 'pending')::int AS "Pending",
              COUNT(*) FILTER (WHERE status = 'failed')::int AS "Failed",
              COUNT(*) FILTER (WHERE status = 'exhausted')::int AS "Dead",
              COUNT(*) FILTER (WHERE status = 'delivered' AND delivered_at > now() - interval '1 hour')::int AS "DeliveredLastHour",
              EXTRACT(EPOCH FROM (now() - MIN(created_at) FILTER (WHERE status IN ('pending','failed')))) / 60.0 AS "OldestPendingAgeMinutes"
            FROM nexora.outbound_webhook_delivery WHERE company_id = {company}
            """).FirstAsync(ct);

        return
        [
            new("notifications", n.Pending, n.Failed, n.Dead, n.DeliveredLastHour, n.OldestPendingAgeMinutes),
            new("webhooks", w.Pending, w.Failed, w.Dead, w.DeliveredLastHour, w.OldestPendingAgeMinutes),
        ];
    }

    private sealed record OutboxDeadRow(Guid Id, string Subject, string? LastError, int Attempts, DateTimeOffset CreatedAt, Guid? CompanyId);
    private sealed record WebhookDeadRow(Guid Id, string EventType, string? LastError, int Attempts, DateTimeOffset CreatedAt, Guid? CompanyId);

    public async Task<IReadOnlyList<DeadLetterItemDto>> DeadLettersAsync(CancellationToken ct = default)
    {
        var company = Company;
        var n = await db.Database.SqlQuery<OutboxDeadRow>($"""
            SELECT id AS "Id", subject AS "Subject", last_error AS "LastError", attempts AS "Attempts",
                   created_at AS "CreatedAt", company_id AS "CompanyId"
            FROM nexora.notification_outbox WHERE company_id = {company} AND status = 'dead'
            ORDER BY created_at DESC LIMIT 100
            """).ToListAsync(ct);
        var w = await db.Database.SqlQuery<WebhookDeadRow>($"""
            SELECT id AS "Id", event_type AS "EventType", last_error AS "LastError", attempts AS "Attempts",
                   created_at AS "CreatedAt", company_id AS "CompanyId"
            FROM nexora.outbound_webhook_delivery WHERE company_id = {company} AND status = 'exhausted'
            ORDER BY created_at DESC LIMIT 100
            """).ToListAsync(ct);

        var items = new List<DeadLetterItemDto>(n.Count + w.Count);
        items.AddRange(n.Select(r => new DeadLetterItemDto(r.Id, "notifications", r.Subject, r.LastError, r.Attempts, r.CreatedAt, null, r.CompanyId)));
        items.AddRange(w.Select(r => new DeadLetterItemDto(r.Id, "webhooks", r.EventType, r.LastError, r.Attempts, r.CreatedAt, null, r.CompanyId)));
        return items.OrderByDescending(i => i.CreatedAt).ToList();
    }

    public async Task<Result> RetryAsync(Guid messageId, CancellationToken ct = default)
    {
        var company = Company;
        var n = await db.Database.ExecuteSqlAsync($"""
            UPDATE nexora.notification_outbox SET next_attempt_at = now(), updated_at = now()
            WHERE id = {messageId} AND company_id = {company} AND status IN ('pending','failed')
            """, ct);
        if (n == 0)
            n = await db.Database.ExecuteSqlAsync($"""
                UPDATE nexora.outbound_webhook_delivery SET next_attempt_at = now(), updated_at = now()
                WHERE id = {messageId} AND company_id = {company} AND status IN ('pending','failed')
                """, ct);
        if (n == 0) return Error.NotFound("queue.message_not_found", "Message introuvable ou déjà remis/mort.");
        await uow.SaveChangesAsync(ct);
        return Result.Success();
    }

    public async Task<Result> ReplayDeadLetterAsync(Guid messageId, CancellationToken ct = default)
    {
        var company = Company;
        var n = await db.Database.ExecuteSqlAsync($"""
            UPDATE nexora.notification_outbox
            SET status = 'pending', attempts = 0, next_attempt_at = now(), last_error = NULL, updated_at = now()
            WHERE id = {messageId} AND company_id = {company} AND status = 'dead'
            """, ct);
        if (n == 0)
            n = await db.Database.ExecuteSqlAsync($"""
                UPDATE nexora.outbound_webhook_delivery
                SET status = 'pending', attempts = 0, next_attempt_at = now(), last_error = NULL, updated_at = now()
                WHERE id = {messageId} AND company_id = {company} AND status = 'exhausted'
                """, ct);
        if (n == 0) return Error.NotFound("queue.dead_letter_not_found", "Message mort introuvable.");
        await uow.SaveChangesAsync(ct);
        return Result.Success();
    }

    public async Task<Result> DeleteDeadLetterAsync(Guid messageId, CancellationToken ct = default)
    {
        var company = Company;
        var n = await db.Database.ExecuteSqlAsync($"""
            DELETE FROM nexora.notification_outbox WHERE id = {messageId} AND company_id = {company} AND status = 'dead'
            """, ct);
        if (n == 0)
            n = await db.Database.ExecuteSqlAsync($"""
                DELETE FROM nexora.outbound_webhook_delivery WHERE id = {messageId} AND company_id = {company} AND status = 'exhausted'
                """, ct);
        if (n == 0) return Error.NotFound("queue.dead_letter_not_found", "Message mort introuvable.");
        await uow.SaveChangesAsync(ct);
        return Result.Success();
    }
}
