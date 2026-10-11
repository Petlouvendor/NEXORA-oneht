using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Nexora.Application.Abstractions;
using Nexora.Application.Notifications;
using Nexora.Contracts.Notifications;
using Nexora.Domain.Common;
using Nexora.Infrastructure.Persistence;
using Nexora.Infrastructure.Scheduling;

namespace Nexora.Infrastructure.Notifications;

/// <summary>
/// Administration des envois (finalisation phase 16) : fournisseurs, test réel des canaux, journal des
/// envois et des tentatives, relances. Les fournisseurs sont configurés au niveau du serveur ; le journal
/// et les relances sont limités à l'entreprise active.
/// </summary>
public sealed partial class SqlNotificationDeliveryAdmin(
    NexoraDbContext db, ITenantContext tenant, IEnumerable<INotificationChannel> channels,
    INotificationDispatcher dispatcher, IOptions<SchedulerOptions> scheduler) : INotificationDeliveryAdmin
{
    private static readonly string[] ChannelOrder = ["email", "sms", "push", "whatsapp", "webhook"];
    private static readonly string[] Statuses = ["pending", "sending", "sent", "failed", "dead", "not_configured"];
    private Guid Company => tenant.ActiveCompanyId ?? throw TenantErrors.ActiveCompanyRequired();

    private sealed record TestRow(string Channel, string Status, string Message, DateTimeOffset TestedAt);
    private sealed record WaitingRow(string Channel, int Count);
    private sealed record EntryRow(
        Guid Id, string Channel, string Category, string Subject, string? Address, string? RecipientName, string Status,
        int Attempts, int MaxAttempts, DateTimeOffset NextAttemptAt, string? LastError, string? Provider,
        DateTimeOffset? LastAttemptAt, DateTimeOffset CreatedAt, DateTimeOffset? SentAt);
    private sealed record CountRow(string Status, int Count);
    private sealed record LogRow(int Attempt, string Status, string? Provider, string? Error, int? DurationMs, DateTimeOffset CreatedAt);

    private IEnumerable<INotificationChannel> Ordered =>
        channels.GroupBy(c => c.Channel).Select(g => g.Last())
            .OrderBy(c => Array.IndexOf(ChannelOrder, c.Channel) is var i and >= 0 ? i : 99);

    public async Task<IReadOnlyList<ChannelProviderDto>> ProvidersAsync(CancellationToken ct = default)
    {
        var tests = await db.Database.SqlQuery<TestRow>($"""
            SELECT channel AS "Channel", status AS "Status", message AS "Message", tested_at AS "TestedAt"
            FROM nexora.notification_channel_test
            """).ToListAsync(ct);
        var waiting = await db.Database.SqlQuery<WaitingRow>($"""
            SELECT channel AS "Channel", COUNT(*)::int AS "Count" FROM nexora.notification_outbox
            WHERE company_id = {Company} AND status = 'not_configured' GROUP BY channel
            """).ToListAsync(ct);
        return Ordered.Select(c =>
        {
            var t = tests.FirstOrDefault(x => x.Channel == c.Channel);
            return new ChannelProviderDto(c.Channel, c.ProviderName, c.IsConfigured, c.ConfigurationSummary,
                t?.Status, t?.Message, t?.TestedAt, waiting.FirstOrDefault(w => w.Channel == c.Channel)?.Count ?? 0);
        }).ToList();
    }

    public async Task<Result<ChannelTestResultDto>> TestChannelAsync(string channel, CancellationToken ct = default)
    {
        var c = Find(channel);
        if (c is null) return Error.NotFound("notifications.channel", "Canal inconnu.");
        var r = await c.TestAsync(ct);
        await db.Database.ExecuteSqlAsync($"""
            INSERT INTO nexora.notification_channel_test (channel, provider, status, message, latency_ms, tested_at, tested_by)
            VALUES ({c.Channel}, {c.ProviderName}, {r.Status}, {r.Message}, {r.LatencyMs}, now(), {tenant.UserId})
            ON CONFLICT (channel) DO UPDATE SET provider = EXCLUDED.provider, status = EXCLUDED.status, message = EXCLUDED.message,
                latency_ms = EXCLUDED.latency_ms, tested_at = now(), tested_by = EXCLUDED.tested_by
            """, ct);
        return Result.Success(r);
    }

    public async Task<Result<OutboxEntryDto>> SendTestAsync(SendTestMessageRequest request, CancellationToken ct = default)
    {
        var c = Find(request.Channel);
        if (c is null) return Error.NotFound("notifications.channel", "Canal inconnu.");
        var address = request.Address?.Trim() ?? "";
        var valid = c.Channel switch
        {
            "email" => EmailRegex().IsMatch(address),
            "sms" or "whatsapp" => PhoneRegex().IsMatch(address),
            "push" => address.Length is >= 10 and <= 4096,
            _ => address.Length > 0,
        };
        if (!valid)
            return Error.Validation("notifications.test_address", c.Channel switch
            {
                "email" => "Adresse e-mail invalide.",
                "sms" or "whatsapp" => "Numéro au format international attendu (ex. +50937001234).",
                "push" => "Jeton d'appareil invalide.",
                _ => "Adresse invalide.",
            });

        // Le test passe par la file et le répartiteur, comme un vrai message : son résultat est journalisé.
        var id = Guid.NewGuid();
        await db.Database.ExecuteSqlAsync($"""
            INSERT INTO nexora.notification_outbox
                (id, company_id, user_id, channel, category, priority, subject, body, address, dedup_key, max_attempts, next_attempt_at)
            VALUES ({id}, {Company}, {tenant.UserId}, {c.Channel}, 'delivery_test', 3, 'Test d''envoi NEXORA ONE',
                    'Message de test envoyé depuis NEXORA ONE pour vérifier le fournisseur.', {address},
                    {"delivery_test:" + id}, 1, now())
            """, ct);
        await dispatcher.DispatchOneAsync(id, ct);
        return Result.Success((await LoadAsync(id, ct))!);
    }

    public async Task<DeliverySummaryDto> SummaryAsync(CancellationToken ct = default)
    {
        var counts = await db.Database.SqlQuery<CountRow>($"""
            SELECT status AS "Status", COUNT(*)::int AS "Count" FROM nexora.notification_outbox
            WHERE company_id = {Company} GROUP BY status
            """).ToListAsync(ct);
        var oldest = await db.Database.SqlQuery<DateTimeOffset?>($"""
            SELECT MIN(created_at) AS "Value" FROM nexora.notification_outbox
            WHERE company_id = {Company} AND status IN ('pending','failed')
            """).FirstOrDefaultAsync(ct);
        int N(string s) => counts.FirstOrDefault(c => c.Status == s)?.Count ?? 0;
        return new DeliverySummaryDto(scheduler.Value.Enabled, N("pending"), N("sending"), N("sent"), N("failed"), N("dead"),
            N("not_configured"), oldest);
    }

    public async Task<IReadOnlyList<OutboxEntryDto>> ListAsync(string? status, string? channel, CancellationToken ct = default)
    {
        var s = Statuses.Contains(status) ? status : null;
        var ch = ChannelOrder.Contains(channel) ? channel : null;
        var rows = await Query(null, s, ch).ToListAsync(ct);
        return rows.Select(Map).ToList();
    }

    public async Task<Result<IReadOnlyList<DeliveryLogDto>>> LogAsync(Guid outboxId, CancellationToken ct = default)
    {
        if (await LoadAsync(outboxId, ct) is null) return Error.NotFound("notifications.outbox", "Envoi introuvable.");
        var rows = await db.Database.SqlQuery<LogRow>($"""
            SELECT attempt::int AS "Attempt", status AS "Status", provider AS "Provider", error AS "Error",
                   duration_ms AS "DurationMs", created_at AS "CreatedAt"
            FROM nexora.notification_delivery_log WHERE outbox_id = {outboxId} AND company_id = {Company}
            ORDER BY attempt, created_at
            """).ToListAsync(ct);
        return Result.Success<IReadOnlyList<DeliveryLogDto>>(rows.Select(r =>
            new DeliveryLogDto(r.Attempt, r.Status, r.Provider, r.Error, r.DurationMs, r.CreatedAt)).ToList());
    }

    public async Task<Result> RetryAsync(Guid outboxId, CancellationToken ct = default)
    {
        var entry = await LoadAsync(outboxId, ct);
        if (entry is null) return Error.NotFound("notifications.outbox", "Envoi introuvable.");
        if (entry.Status is not ("failed" or "dead" or "not_configured"))
            return Error.Conflict("notifications.not_retryable", "Seul un envoi en échec, abandonné ou sans fournisseur se relance.");
        if (entry.Status == "not_configured" && Find(entry.Channel) is not { IsConfigured: true })
            return Error.Conflict("notifications.provider_not_configured", "Fournisseur non configuré : configurez-le avant de relancer.");
        await db.Database.ExecuteSqlAsync($"""
            UPDATE nexora.notification_outbox
            SET status = 'pending', attempts = 0, next_attempt_at = now(), last_error = NULL, updated_at = now()
            WHERE id = {outboxId} AND company_id = {Company}
            """, ct);
        return Result.Success();
    }

    public async Task<Result<int>> RequeueNotConfiguredAsync(string channel, CancellationToken ct = default)
    {
        var c = Find(channel);
        if (c is null) return Error.NotFound("notifications.channel", "Canal inconnu.");
        if (!c.IsConfigured)
            return Error.Conflict("notifications.provider_not_configured", "Fournisseur non configuré : configurez-le avant de relancer.");
        var n = await db.Database.ExecuteSqlAsync($"""
            UPDATE nexora.notification_outbox
            SET status = 'pending', attempts = 0, next_attempt_at = now(), last_error = NULL, updated_at = now()
            WHERE company_id = {Company} AND channel = {c.Channel} AND status = 'not_configured'
            """, ct);
        return Result.Success(n);
    }

    public Task<int> DispatchNowAsync(CancellationToken ct = default) => dispatcher.DispatchDueForCompanyAsync(Company, ct);

    // --- helpers ---------------------------------------------------------

    private INotificationChannel? Find(string? channel) => channels.LastOrDefault(c => c.Channel == channel?.Trim().ToLowerInvariant());

    private IQueryable<EntryRow> Query(Guid? id, string? status, string? channel) => db.Database.SqlQuery<EntryRow>($"""
        SELECT o.id AS "Id", o.channel AS "Channel", o.category AS "Category", o.subject AS "Subject", o.address AS "Address",
               COALESCE(NULLIF(u.full_name, ''), u.email::text) AS "RecipientName", o.status AS "Status",
               o.attempts::int AS "Attempts", o.max_attempts::int AS "MaxAttempts", o.next_attempt_at AS "NextAttemptAt",
               o.last_error AS "LastError", o.provider AS "Provider", o.last_attempt_at AS "LastAttemptAt",
               o.created_at AS "CreatedAt", o.sent_at AS "SentAt"
        FROM nexora.notification_outbox o LEFT JOIN nexora.app_user u ON u.id = o.user_id
        WHERE o.company_id = {Company}
          AND ({id}::uuid IS NULL OR o.id = {id})
          AND ({status}::text IS NULL OR o.status = {status})
          AND ({channel}::text IS NULL OR o.channel = {channel})
        ORDER BY o.created_at DESC
        LIMIT 200
        """);

    private async Task<OutboxEntryDto?> LoadAsync(Guid id, CancellationToken ct)
        => await Query(id, null, null).FirstOrDefaultAsync(ct) is { } r ? Map(r) : null;

    private static OutboxEntryDto Map(EntryRow r) => new(
        r.Id, r.Channel, r.Category, r.Subject, NotificationMasks.Address(r.Channel, r.Address), r.RecipientName, r.Status,
        r.Attempts, r.MaxAttempts, r.NextAttemptAt, r.LastError, r.Provider, r.LastAttemptAt, r.CreatedAt, r.SentAt);

    [GeneratedRegex(@"^[^@\s]+@[^@\s]+\.[^@\s]+$")]
    private static partial Regex EmailRegex();

    [GeneratedRegex(@"^\+[1-9]\d{7,14}$")]
    private static partial Regex PhoneRegex();
}
