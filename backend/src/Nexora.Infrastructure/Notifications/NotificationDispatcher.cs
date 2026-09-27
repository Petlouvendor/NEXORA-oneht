using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Nexora.Application.Abstractions;
using Nexora.Application.Notifications;
using Nexora.Contracts.Notifications;
using Nexora.Infrastructure.Persistence;

namespace Nexora.Infrastructure.Notifications;

/// <summary>
/// Distributeur de l'outbox (Phase 34 §28-31). Exécuté en portée système par le planificateur :
/// reprend les remises dues, tente le canal, applique un backoff exponentiel, place en
/// dead-letter après <c>max_attempts</c> et notifie alors un administrateur de l'entreprise.
/// </summary>
public sealed class NotificationDispatcher(
    NexoraDbContext db,
    ITenantContext tenant,
    IDateTimeProvider clock,
    INotificationService notifications,
    IEnumerable<INotificationChannel> channels,
    ILogger<NotificationDispatcher> logger) : INotificationDispatcher
{
    private const int BatchSize = 50;

    private sealed record Row(Guid Id, Guid? CompanyId, Guid? UserId, string Channel, string Category, short Priority,
        string Subject, string Body, string? Address, short Attempts, short MaxAttempts, Guid CorrelationId,
        DateTimeOffset CreatedAt, DateTimeOffset? SentAt);

    public async Task<int> DispatchDueAsync(CancellationToken ct = default)
    {
        using var _ = tenant.BeginSystemScope();

        var due = await db.Database.SqlQuery<Row>($"""
            SELECT id AS "Id", company_id AS "CompanyId", user_id AS "UserId", channel AS "Channel", category AS "Category",
                   priority AS "Priority", subject AS "Subject", body AS "Body", address AS "Address",
                   attempts AS "Attempts", max_attempts AS "MaxAttempts", correlation_id AS "CorrelationId",
                   created_at AS "CreatedAt", sent_at AS "SentAt"
            FROM nexora.notification_outbox
            WHERE status IN ('pending','failed') AND next_attempt_at <= now()
            ORDER BY priority, next_attempt_at
            LIMIT {BatchSize}
            """).ToListAsync(ct);

        var handled = 0;
        foreach (var row in due)
        {
            // Verrou optimiste : ne traite que si la ligne est toujours à faire.
            var claimed = await db.Database.ExecuteSqlAsync($"""
                UPDATE nexora.notification_outbox SET status = 'sending', updated_at = now()
                WHERE id = {row.Id} AND status IN ('pending','failed')
                """, ct);
            if (claimed == 0) continue;

            var channel = channels.FirstOrDefault(c => c.Channel == row.Channel);
            ChannelSendResult result;
            try
            {
                result = channel is null
                    ? ChannelSendResult.Fail($"Canal « {row.Channel} » non configuré.")
                    : await channel.SendAsync(ToDto(row), ct);
            }
            catch (Exception ex)
            {
                result = ChannelSendResult.Retry(ex.Message);
            }

            var attempts = (short)(row.Attempts + 1);
            if (result.Sent)
            {
                await db.Database.ExecuteSqlAsync($"""
                    UPDATE nexora.notification_outbox
                    SET status = 'sent', attempts = {attempts}, sent_at = now(), last_error = NULL, updated_at = now()
                    WHERE id = {row.Id}
                    """, ct);
            }
            else if (!result.Transient || attempts >= row.MaxAttempts)
            {
                await db.Database.ExecuteSqlAsync($"""
                    UPDATE nexora.notification_outbox
                    SET status = 'dead', attempts = {attempts}, last_error = {result.Error}, updated_at = now()
                    WHERE id = {row.Id}
                    """, ct);
                await NotifyAdminOfFailureAsync(row, attempts, ct);
                logger.LogWarning("Notification {Id} vers {Channel} en dead-letter après {Attempts} tentatives : {Error}",
                    row.Id, row.Channel, attempts, result.Error);
            }
            else
            {
                var delayMinutes = Math.Min(60, (int)Math.Pow(2, attempts));   // 2, 4, 8, 16, 32, plafond 60
                var next = clock.UtcNow.AddMinutes(delayMinutes);
                await db.Database.ExecuteSqlAsync($"""
                    UPDATE nexora.notification_outbox
                    SET status = 'failed', attempts = {attempts}, last_error = {result.Error},
                        next_attempt_at = {next}, updated_at = now()
                    WHERE id = {row.Id}
                    """, ct);
            }
            handled++;
        }
        return handled;
    }

    private async Task NotifyAdminOfFailureAsync(Row row, int attempts, CancellationToken ct)
    {
        // Ne jamais escalader un échec d'escalade (§31 — pas de cascade).
        if (row.Category == "delivery_failed" || row.CompanyId is not { } company) return;

        var admins = await db.Database.SqlQuery<Guid>($"""
            SELECT DISTINCT ur.user_id AS "Value"
            FROM nexora.user_role ur
            JOIN nexora.role_permission rp ON rp.role_id = ur.role_id
            JOIN nexora.permission p ON p.id = rp.permission_id
            JOIN nexora.access_scope sc ON sc.id = ur.scope_id
            WHERE ur.revoked_at IS NULL AND p.code = 'notifications.manage_templates'
              AND (sc.company_id = {company} OR sc.scope_type IN ('GROUP','PLATFORM'))
            LIMIT 20
            """).ToListAsync(ct);
        if (admins.Count == 0) return;

        tenant.SetScope([company], [], company);
        await notifications.NotifyAsync(new NotifyRequest(
            Category: "delivery_failed",
            Recipients: admins,
            TemplateKey: "notification.delivery_failed",
            Variables: new Dictionary<string, string?>
            {
                ["original.category"] = row.Category,
                ["channel"] = row.Channel,
                ["attempts"] = attempts.ToString(),
            },
            Priority: 2,
            Severity: "warning",
            Source: "system",
            DedupKey: $"delivery_failed:{row.Id}"), ct);
    }

    private static OutboxMessageDto ToDto(Row r) => new(
        r.Id, r.Channel, r.Category, r.Priority, r.Subject, r.Body, r.Address, "sending",
        r.Attempts, r.MaxAttempts, DateTimeOffset.UtcNow, null, r.CorrelationId, r.CreatedAt, r.SentAt);
}

// -----------------------------------------------------------------------------
// Canaux par défaut (Phase 34 §3) — journalisent la remise. Les fournisseurs réels
// (SMTP, Twilio, Meta WhatsApp, WebPush…) remplacent ces implémentations sans toucher au moteur.
// -----------------------------------------------------------------------------

internal abstract class LoggingChannel(ILogger logger) : INotificationChannel
{
    public abstract string Channel { get; }
    protected bool RequiresAddress => Channel is "email" or "webhook" or "push";

    public Task<ChannelSendResult> SendAsync(OutboxMessageDto m, CancellationToken ct = default)
    {
        if (RequiresAddress && string.IsNullOrWhiteSpace(m.Address))
            return Task.FromResult(ChannelSendResult.Retry($"Adresse {Channel} absente pour la notification {m.Id}."));

        logger.LogInformation("[{Channel}] → {Address} · {Subject}", Channel, m.Address ?? "(interne)", m.Subject);
        return Task.FromResult(ChannelSendResult.Ok());
    }
}

internal sealed class LoggingEmailChannel(ILogger<LoggingEmailChannel> logger) : LoggingChannel(logger)
{ public override string Channel => "email"; }

internal sealed class LoggingSmsChannel(ILogger<LoggingSmsChannel> logger) : LoggingChannel(logger)
{ public override string Channel => "sms"; }

internal sealed class LoggingWebhookChannel(ILogger<LoggingWebhookChannel> logger) : LoggingChannel(logger)
{ public override string Channel => "webhook"; }

/// <summary>Canal push (Phase 44 §20) — la colonne `notification_outbox.channel`/`message_template.channel`
/// autorisait déjà « push » depuis la Phase 34 (V047), jamais implémenté avant cette phase. Journalise
/// comme les autres canaux : un vrai fournisseur (FCM/APNs/WebPush) remplacera cette implémentation
/// sans toucher au moteur (§3).</summary>
internal sealed class LoggingPushChannel(ILogger<LoggingPushChannel> logger) : LoggingChannel(logger)
{ public override string Channel => "push"; }
