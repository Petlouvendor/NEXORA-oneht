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

    public Task<int> DispatchDueAsync(CancellationToken ct = default) => DispatchAsync(null, null, ct);

    public Task<int> DispatchDueForCompanyAsync(Guid companyId, CancellationToken ct = default) => DispatchAsync(companyId, null, ct);

    public Task DispatchOneAsync(Guid outboxId, CancellationToken ct = default) => DispatchAsync(null, outboxId, ct);

    private async Task<int> DispatchAsync(Guid? companyId, Guid? outboxId, CancellationToken ct)
    {
        using var _ = tenant.BeginSystemScope();

        var due = await db.Database.SqlQuery<Row>($"""
            SELECT id AS "Id", company_id AS "CompanyId", user_id AS "UserId", channel AS "Channel", category AS "Category",
                   priority AS "Priority", subject AS "Subject", body AS "Body", address AS "Address",
                   attempts AS "Attempts", max_attempts AS "MaxAttempts", correlation_id AS "CorrelationId",
                   created_at AS "CreatedAt", sent_at AS "SentAt"
            FROM nexora.notification_outbox
            WHERE status IN ('pending','failed')
              AND ({outboxId}::uuid IS NOT NULL OR next_attempt_at <= now())
              AND ({outboxId}::uuid IS NULL OR id = {outboxId})
              AND ({companyId}::uuid IS NULL OR company_id = {companyId})
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

            var channel = channels.LastOrDefault(c => c.Channel == row.Channel);   // le dernier enregistré l'emporte (surcharge)
            var provider = channel?.ProviderName ?? "aucun";
            var started = System.Diagnostics.Stopwatch.StartNew();
            ChannelSendResult result;
            try
            {
                result = channel is null
                    ? ChannelSendResult.Unconfigured($"Aucun fournisseur pour le canal « {row.Channel} » : le message n'a pas été envoyé.")
                    : await channel.SendAsync(ToDto(row), ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                result = ChannelSendResult.Retry(ex.Message);
            }
            var duration = (int)started.ElapsedMilliseconds;

            var attempts = (short)(row.Attempts + 1);
            string status;
            if (result.Sent)
            {
                status = "sent";
                await db.Database.ExecuteSqlAsync($"""
                    UPDATE nexora.notification_outbox
                    SET status = 'sent', attempts = {attempts}, sent_at = now(), last_error = NULL,
                        provider = {provider}, last_attempt_at = now(), updated_at = now()
                    WHERE id = {row.Id}
                    """, ct);
            }
            else if (result.NotConfigured)
            {
                // Rien n'est parti : ni « envoyé », ni panne à escalader. Le message attend un fournisseur ;
                // l'administrateur le relance une fois le fournisseur configuré.
                status = "not_configured";
                await db.Database.ExecuteSqlAsync($"""
                    UPDATE nexora.notification_outbox
                    SET status = 'not_configured', attempts = {attempts}, last_error = {result.Error},
                        provider = {provider}, last_attempt_at = now(), updated_at = now()
                    WHERE id = {row.Id}
                    """, ct);
            }
            else if (!result.Transient || attempts >= row.MaxAttempts)
            {
                status = "dead";
                await db.Database.ExecuteSqlAsync($"""
                    UPDATE nexora.notification_outbox
                    SET status = 'dead', attempts = {attempts}, last_error = {result.Error},
                        provider = {provider}, last_attempt_at = now(), updated_at = now()
                    WHERE id = {row.Id}
                    """, ct);
                await NotifyAdminOfFailureAsync(row, attempts, ct);
                logger.LogWarning("Notification {Id} vers {Channel} en dead-letter après {Attempts} tentatives : {Error}",
                    row.Id, row.Channel, attempts, result.Error);
            }
            else
            {
                status = "failed";
                var delayMinutes = Math.Min(60, (int)Math.Pow(2, attempts));   // 2, 4, 8, 16, 32, plafond 60
                var next = clock.UtcNow.AddMinutes(delayMinutes);
                await db.Database.ExecuteSqlAsync($"""
                    UPDATE nexora.notification_outbox
                    SET status = 'failed', attempts = {attempts}, last_error = {result.Error},
                        next_attempt_at = {next}, provider = {provider}, last_attempt_at = now(), updated_at = now()
                    WHERE id = {row.Id}
                    """, ct);
            }

            // Journal des tentatives : chaque essai est conservé (fournisseur, résultat, erreur, durée).
            await db.Database.ExecuteSqlAsync($"""
                INSERT INTO nexora.notification_delivery_log (outbox_id, company_id, attempt, status, provider, error, duration_ms)
                VALUES ({row.Id}, {row.CompanyId}, {attempts}, {status}, {provider}, {result.Error}, {duration})
                """, ct);
            handled++;
        }
        return handled;
    }

    private async Task NotifyAdminOfFailureAsync(Row row, int attempts, CancellationToken ct)
    {
        // Ne jamais escalader un échec d'escalade (§31 — pas de cascade).
        // Un message de test en échec est déjà affiché à l'administrateur qui l'a lancé.
        if (row.Category is "delivery_failed" or "delivery_test" || row.CompanyId is not { } company) return;

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
// Canaux sans fournisseur intégré (finalisation phase 16). Les anciens canaux « journalisation »
// renvoyaient « envoyé » sans rien envoyer : un message vers un webhook de notification ou WhatsApp
// apparaissait comme remis. Ils disent maintenant la vérité : fournisseur non configuré.
// -----------------------------------------------------------------------------

public sealed class UnavailableChannel(string channel, string label) : INotificationChannel
{
    public string Channel => channel;
    public string ProviderName => "aucun";
    public bool IsConfigured => false;
    public string ConfigurationSummary => $"Aucun fournisseur n'est intégré pour le canal {label}.";

    public Task<ChannelSendResult> SendAsync(OutboxMessageDto m, CancellationToken ct = default)
        => Task.FromResult(ChannelSendResult.Unconfigured($"Aucun fournisseur {label} : le message n'a pas été envoyé."));

    public Task<ChannelTestResultDto> TestAsync(CancellationToken ct = default)
        => Task.FromResult(new ChannelTestResultDto(Channel, Nexora.Application.Integrations.ConnectionStatus.NotConfigured, ConfigurationSummary, 0));
}

/// <summary>Masquage des données affichées : secrets, adresses e-mail, numéros.</summary>
public static class NotificationMasks
{
    public static string Secret(string value)
        => string.IsNullOrEmpty(value) ? "" : value.Length <= 6 ? new string('•', value.Length) : $"{value[..4]}…{value[^2..]}";

    public static string? Address(string channel, string? address)
    {
        if (string.IsNullOrWhiteSpace(address)) return null;
        var a = address.Trim();
        if (channel == "email" && a.IndexOf('@') is var at and > 0)
            return $"{a[..Math.Min(2, at)]}…{a[at..]}";
        if (channel == "push") return a.Length > 12 ? $"{a[..6]}…{a[^4..]}" : "appareil";
        return a.Length > 6 ? $"{a[..4]}…{a[^2..]}" : new string('•', a.Length);
    }
}
