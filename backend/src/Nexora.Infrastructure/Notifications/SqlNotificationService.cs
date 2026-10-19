using Microsoft.EntityFrameworkCore;
using Nexora.Application.Abstractions;
using Nexora.Application.Notifications;
using Nexora.Contracts.Notifications;
using Nexora.Domain.Common;
using Nexora.Infrastructure.Persistence;

namespace Nexora.Infrastructure.Notifications;

/// <summary>
/// Cœur du moteur (Phase 34 §2). <see cref="NotifyAsync"/> : déduplication → préférences par
/// destinataire → rendu de gabarit → écriture de la notification interne + mise en file des
/// canaux externes. Les lectures du centre sont bornées par la RLS utilisateur de
/// <c>nexora.notification</c>.
/// </summary>
public sealed class SqlNotificationService(
    NexoraDbContext db,
    ITenantContext tenant,
    IUnitOfWork uow,
    IDateTimeProvider clock,
    SqlMessageTemplateService templates,
    IBusinessCalendar calendar) : INotificationService
{
    private Guid UserId => tenant.UserId ?? throw new InvalidOperationException("Utilisateur requis.");
    private static readonly string[] ExternalChannels = ["email", "sms", "webhook"];

    private sealed record Effective(bool InApp, bool Email, bool Sms, bool Webhook, short MinPriority, string Digest, TimeOnly? QuietFrom, TimeOnly? QuietTo);

    // ---------------------------------------------------------------- Émission

    public async Task<NotifyResultDto> NotifyAsync(NotifyRequest request, CancellationToken ct = default)
    {
        var category = request.Category.Trim().ToLowerInvariant();
        var priority = Math.Clamp(request.Priority, 1, 5);
        var recipients = request.Recipients.Distinct().ToArray();
        int created = 0, queued = 0, skipped = 0;

        // Destinataires de l'entreprise (ou de son groupe), ou exploitants de la plateforme (audit 2026-10 éd. 2 :
        // l'appartenance était laissée aux appelants — un identifiant quelconque recevait la notification).
        if (tenant.ActiveCompanyId is { } company && recipients.Length > 0)
        {
            var allowed = await db.Database.SqlQuery<Guid>($"""
                SELECT DISTINCT ur.user_id AS "Value"
                FROM nexora.user_role ur JOIN nexora.access_scope sc ON sc.id = ur.scope_id
                JOIN nexora.company co ON co.id = {company}
                WHERE ur.user_id = ANY({recipients}) AND ur.revoked_at IS NULL
                  AND (sc.company_id = co.id OR sc.scope_type = 'PLATFORM'
                       OR (sc.group_id IS NOT NULL AND sc.group_id = co.group_id))
                """).ToListAsync(ct);
            skipped += recipients.Length - allowed.Count;
            recipients = recipients.Where(allowed.Contains).ToArray();
        }
        var zone = await calendar.ZoneAsync(ct);

        var inAppTpl = await templates.LoadAsync(request.TemplateKey, "in_app", request.Locale, ct);
        var externalTpl = new Dictionary<string, SqlMessageTemplateService.Row?>();
        foreach (var ch in ExternalChannels)
            externalTpl[ch] = await templates.LoadAsync(request.TemplateKey, ch, request.Locale, ct);

        var (title, _) = inAppTpl is not null
            ? TemplateRenderer.Render(inAppTpl.Subject, request.Variables)
            : (request.Variables.GetValueOrDefault("title") ?? category, (IReadOnlyList<string>)[]);
        var (message, _) = inAppTpl is not null
            ? TemplateRenderer.Render(inAppTpl.Body, request.Variables)
            : (request.Variables.GetValueOrDefault("message") ?? "", (IReadOnlyList<string>)[]);

        await uow.ExecuteInTransactionAsync(async token =>
        {
            foreach (var userId in recipients)
            {
                if (request.DedupKey is { } dk)
                {
                    var exists = await db.Database.SqlQuery<bool>($"""
                        SELECT EXISTS(SELECT 1 FROM nexora.notification WHERE user_id = {userId} AND dedup_key = {dk}) AS "Value"
                        """).FirstAsync(token);
                    if (exists) { skipped++; continue; }
                }

                var pref = await ResolveAsync(userId, request.ReferenceCompany(tenant), category, token);
                if (priority > pref.MinPriority) { skipped++; continue; }

                Guid? notificationId = null;
                if (pref.InApp)
                {
                    // La déduplication est déjà assurée par le pré-contrôle ci-dessus ;
                    // l'index unique partiel `notification_dedup_uk` protège contre les courses.
                    var newId = Guid.NewGuid();
                    await db.Database.ExecuteSqlAsync($"""
                        INSERT INTO nexora.notification
                            (id, user_id, company_id, type, category, source, priority, severity, title, message,
                             action_url, reference_type, reference_id, dedup_key, expires_at)
                        VALUES ({newId}, {userId}, {tenant.ActiveCompanyId}, {category}, {category}, {request.Source}, {priority},
                                {request.Severity}, {title}, {message}, {request.ActionUrl}, {request.ReferenceType},
                                {request.ReferenceId}, {request.DedupKey}, {request.ExpiresAt})
                        """, token);
                    notificationId = newId;
                    created++;
                }

                var quiet = InQuietHours(clock.UtcNow, zone, pref.QuietFrom, pref.QuietTo, out var quietEnd);
                var digestHoldsExternal = pref.Digest != "instant";   // daily/weekly/off — le batch de résumé est un backlog

                if (!digestHoldsExternal)
                {
                    foreach (var ch in ExternalChannels)
                    {
                        var enabled = ch switch { "email" => pref.Email, "sms" => pref.Sms, "webhook" => pref.Webhook, _ => false };
                        if (!enabled) continue;

                        var tpl = externalTpl.GetValueOrDefault(ch) ?? inAppTpl;
                        var subj = tpl is not null ? TemplateRenderer.Render(tpl.Subject, request.Variables).Text : title;
                        var bod = tpl is not null ? TemplateRenderer.Render(tpl.Body, request.Variables).Text : message;
                        var address = ch == "email"
                            ? await db.Database.SqlQuery<string>($"""SELECT email::text AS "Value" FROM nexora.app_user WHERE id = {userId}""").FirstOrDefaultAsync(token)
                            : null;
                        var channelDedup = request.DedupKey is { } d ? $"{d}:{userId}" : null;
                        var nextAt = quiet ? quietEnd : clock.UtcNow;

                        var n = await db.Database.ExecuteSqlAsync($"""
                            INSERT INTO nexora.notification_outbox
                                (company_id, user_id, notification_id, channel, category, priority, subject, body, address, dedup_key, next_attempt_at)
                            VALUES ({tenant.ActiveCompanyId}, {userId}, {notificationId}, {ch}, {category}, {priority},
                                    {subj}, {bod}, {address}, {channelDedup}, {nextAt})
                            ON CONFLICT ON CONSTRAINT notification_outbox_dedup_uk DO NOTHING
                            """, token);
                        queued += n;
                    }
                }
            }
            return 0;
        }, ct);

        return new NotifyResultDto(created, queued, skipped);
    }

    public async Task<int> SendPushAsync(IReadOnlyList<Guid> userIds, string title, string body, string? actionUrl = null, CancellationToken ct = default)
    {
        var queued = 0;
        foreach (var userId in userIds.Distinct())
        {
            var notificationId = Guid.NewGuid();
            await db.Database.ExecuteSqlAsync($"""
                INSERT INTO nexora.notification
                    (id, user_id, company_id, type, category, source, priority, severity, title, message, action_url)
                VALUES ({notificationId}, {userId}, {tenant.ActiveCompanyId}, 'push', 'push', 'automation', 3, 'info', {title}, {body}, {actionUrl})
                """, ct);

            var tokens = await db.Database.SqlQuery<string>($"""
                SELECT push_token AS "Value" FROM nexora.sync_device
                WHERE user_id = {userId} AND status = 'active' AND push_token IS NOT NULL
                """).ToListAsync(ct);
            foreach (var token in tokens)
            {
                // `notification_outbox_dedup_uk` est UNIQUE NULLS NOT DISTINCT (dedup_key, channel) : sans
                // clé propre, un deuxième envoi push (n'importe lequel, n'importe quel destinataire) entrerait
                // en collision avec le premier. Une clé fraîche par ligne = pas de déduplication voulue ici.
                queued += await db.Database.ExecuteSqlAsync($"""
                    INSERT INTO nexora.notification_outbox (company_id, user_id, notification_id, channel, category, priority, subject, body, address, dedup_key)
                    VALUES ({tenant.ActiveCompanyId}, {userId}, {notificationId}, 'push', 'push', 3, {title}, {body}, {token}, {Guid.NewGuid().ToString()})
                    """, ct);
            }
        }
        await uow.SaveChangesAsync(ct);
        return queued;
    }

    private async Task<Effective> ResolveAsync(Guid userId, Guid? companyId, string category, CancellationToken ct)
    {
        var row = await db.Database.SqlQuery<SqlNotificationPreferenceService.Row>($"""
            SELECT id AS "Id", company_id AS "CompanyId", category AS "Category", in_app AS "InApp", email AS "Email",
                   sms AS "Sms", webhook AS "Webhook", min_priority AS "MinPriority", digest AS "Digest",
                   quiet_from::text AS "QuietFrom", quiet_to::text AS "QuietTo"
            FROM nexora.notification_preference
            WHERE user_id = {userId}
              AND (company_id = {companyId}::uuid OR company_id IS NULL)
              AND (category = {category}::text OR category IS NULL)
            ORDER BY (company_id IS NOT NULL) DESC, (category IS NOT NULL) DESC
            LIMIT 1
            """).FirstOrDefaultAsync(ct);

        if (row is null) return new Effective(true, false, false, false, 5, "instant", null, null);
        return new Effective(row.InApp, row.Email, row.Sms, row.Webhook, row.MinPriority, row.Digest,
            Parse(row.QuietFrom), Parse(row.QuietTo));

        static TimeOnly? Parse(string? s) => TimeOnly.TryParse(s, out var t) ? t : null;
    }

    /// <summary>
    /// Heures de silence lues dans le fuseau de l'entreprise (audit 2026-10 éd. 2 : elles l'étaient en UTC — « 22 h-7 h »
    /// réglé en Haïti tombait de 18 h à 3 h, heure locale).
    /// </summary>
    internal static bool InQuietHours(DateTimeOffset now, TimeZoneInfo zone, TimeOnly? from, TimeOnly? to, out DateTimeOffset quietEnd)
    {
        quietEnd = now;
        if (from is not { } f || to is not { } t || f == t) return false;
        var local = TimeZoneInfo.ConvertTime(now, zone);
        var current = TimeOnly.FromDateTime(local.DateTime);
        var quiet = f < t ? current >= f && current < t : current >= f || current < t;
        if (!quiet) return false;
        var end = local.Date.Add(t.ToTimeSpan());
        if (end <= local.DateTime) end = end.AddDays(1);
        quietEnd = new DateTimeOffset(end, zone.GetUtcOffset(end)).ToUniversalTime();
        return true;
    }

    // ---------------------------------------------------------------- Centre

    private sealed record NotifRow(Guid Id, string? Category, string Source, short Priority, string Severity,
        string Title, string Message, string? ActionUrl, string? ReferenceType, Guid? ReferenceId, Guid? CompanyId,
        DateTimeOffset CreatedAt, DateTimeOffset? ReadAt, DateTimeOffset? PinnedAt, DateTimeOffset? ArchivedAt, DateTimeOffset? ExpiresAt);

    public async Task<NotificationPageDto> ListAsync(NotificationQuery query, CancellationToken ct = default)
    {
        var (page, size) = (Math.Max(1, query.Page), Math.Clamp(query.PageSize, 1, 100));
        var state = query.State?.Trim().ToLowerInvariant();
        var category = string.IsNullOrWhiteSpace(query.Category) ? null : query.Category.Trim().ToLowerInvariant();

        var rows = await db.Database.SqlQuery<NotifRow>($"""
            SELECT id AS "Id", category AS "Category", source AS "Source", priority AS "Priority", severity AS "Severity",
                   title AS "Title", message AS "Message", action_url AS "ActionUrl", reference_type AS "ReferenceType",
                   reference_id AS "ReferenceId", company_id AS "CompanyId", created_at AS "CreatedAt", read_at AS "ReadAt",
                   pinned_at AS "PinnedAt", archived_at AS "ArchivedAt", expires_at AS "ExpiresAt"
            FROM nexora.notification
            WHERE user_id = {UserId}
              AND (expires_at IS NULL OR expires_at > now())
              AND (({state}::text = 'archived' AND archived_at IS NOT NULL)
                OR ({state}::text = 'unread'   AND read_at IS NULL     AND archived_at IS NULL)
                OR ({state}::text = 'pinned'   AND pinned_at IS NOT NULL AND archived_at IS NULL)
                OR ({state}::text NOT IN ('archived','unread','pinned') AND archived_at IS NULL)
                OR {state}::text IS NULL AND archived_at IS NULL)
              AND ({category}::text IS NULL OR category = {category}::text)
            ORDER BY (pinned_at IS NOT NULL) DESC, created_at DESC
            OFFSET {(page - 1) * size} LIMIT {size}
            """).ToListAsync(ct);

        var total = await db.Database.SqlQuery<long>($"""
            SELECT COUNT(*)::bigint AS "Value" FROM nexora.notification
            WHERE user_id = {UserId} AND archived_at IS NULL AND (expires_at IS NULL OR expires_at > now())
            """).FirstAsync(ct);
        var unread = await UnreadCountAsync(ct);

        return new NotificationPageDto(rows.Select(ToDto).ToList(), (int)total, unread);
    }

    public async Task<int> UnreadCountAsync(CancellationToken ct = default)
        => await db.Database.SqlQuery<int>($"""
            SELECT COUNT(*)::int AS "Value" FROM nexora.notification
            WHERE user_id = {UserId} AND read_at IS NULL AND archived_at IS NULL
              AND (expires_at IS NULL OR expires_at > now())
            """).FirstAsync(ct);

    public async Task<Result> MarkReadAsync(Guid id, bool read, CancellationToken ct = default)
    {
        var n = await db.Database.ExecuteSqlAsync($"""
            UPDATE nexora.notification SET read_at = {(read ? clock.UtcNow : (DateTimeOffset?)null)}
            WHERE id = {id} AND user_id = {UserId}
            """, ct);
        return n == 0 ? Error.NotFound("notification.not_found", "Notification introuvable.") : Result.Success();
    }

    public async Task<int> MarkAllReadAsync(CancellationToken ct = default)
        => await db.Database.ExecuteSqlAsync($"""
            UPDATE nexora.notification SET read_at = {clock.UtcNow}
            WHERE user_id = {UserId} AND read_at IS NULL AND archived_at IS NULL
            """, ct);

    public async Task<Result> SetPinnedAsync(Guid id, bool pinned, CancellationToken ct = default)
    {
        var n = await db.Database.ExecuteSqlAsync($"""
            UPDATE nexora.notification SET pinned_at = {(pinned ? clock.UtcNow : (DateTimeOffset?)null)}
            WHERE id = {id} AND user_id = {UserId}
            """, ct);
        return n == 0 ? Error.NotFound("notification.not_found", "Notification introuvable.") : Result.Success();
    }

    public async Task<Result> SetArchivedAsync(Guid id, bool archived, CancellationToken ct = default)
    {
        var n = await db.Database.ExecuteSqlAsync($"""
            UPDATE nexora.notification SET archived_at = {(archived ? clock.UtcNow : (DateTimeOffset?)null)}
            WHERE id = {id} AND user_id = {UserId}
            """, ct);
        return n == 0 ? Error.NotFound("notification.not_found", "Notification introuvable.") : Result.Success();
    }

    private static NotificationDto ToDto(NotifRow r) => new(
        r.Id, r.Category ?? "system", r.Source, r.Priority, r.Severity, r.Title, r.Message, r.ActionUrl,
        r.ReferenceType, r.ReferenceId, r.CompanyId, r.CreatedAt, r.ReadAt, r.PinnedAt, r.ArchivedAt, r.ExpiresAt);
}

internal static class NotifyRequestExtensions
{
    /// <summary>L'entreprise à laquelle rattacher la préférence — l'entreprise active de la requête.</summary>
    public static Guid? ReferenceCompany(this NotifyRequest _, ITenantContext tenant) => tenant.ActiveCompanyId;
}
