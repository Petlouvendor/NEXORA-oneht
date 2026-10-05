using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Nexora.Application.Abstractions;
using Nexora.Application.Integrations;
using Nexora.Contracts.Integrations;
using Nexora.Domain.Common;
using Nexora.Infrastructure.Persistence;

namespace Nexora.Infrastructure.Integrations;

/// <summary>
/// Réception des webhooks fournisseurs (§12-13). Endpoint public : on résout le connecteur en
/// portée système, on vérifie la signature HMAC, on déduplique par event id fournisseur, on
/// journalise, puis on dispatche. Une signature invalide ou un doublon n'altère jamais l'état interne.
/// </summary>
public sealed class SqlWebhookIngestService(
    NexoraDbContext db, ITenantContext tenant, IPaymentGateway gateway)
    : IWebhookIngestService
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    private sealed record ConnectorRow(Guid Id, Guid CompanyId, string? WebhookSecret, string Status);
    private sealed record EventRow(
        Guid Id, Guid ConnectorId, string ProviderEventId, string EventType, bool SignatureValid,
        string Status, string? Error, DateTimeOffset ReceivedAt, DateTimeOffset? ProcessedAt);

    public async Task<WebhookIngestResult> IngestAsync(string connectorCode, string? signature, string rawBody, CancellationToken ct = default)
    {
        ConnectorRow? connector;
        using (tenant.BeginSystemScope())
        {
            connector = await db.Database.SqlQuery<ConnectorRow>($"""
                SELECT id AS "Id", company_id AS "CompanyId", webhook_secret AS "WebhookSecret", status AS "Status"
                FROM nexora.integration_connector WHERE code = {connectorCode?.Trim().ToLowerInvariant()}
                LIMIT 1
                """).FirstOrDefaultAsync(ct);
        }
        if (connector is null)
            return new WebhookIngestResult("rejected", false, null, "Connecteur inconnu.");

        // À partir d'ici, tout se fait dans la portée de l'entreprise propriétaire du connecteur.
        tenant.SetScope([connector.CompanyId], [], connector.CompanyId);

        var signatureValid = !string.IsNullOrEmpty(connector.WebhookSecret)
            && IntegrationCrypto.SignatureMatches(connector.WebhookSecret!, rawBody, signature);

        var (providerEventId, eventType, reference, providerAmount) = Parse(rawBody);
        var eventId = Guid.NewGuid();

        // Un événement non vérifié ne doit jamais empoisonner la déduplication : on le journalise
        // sous une clé synthétique pour garder la trace (§12) sans bloquer le vrai événement.
        if (!signatureValid)
        {
            await db.Database.ExecuteSqlAsync($"""
                INSERT INTO nexora.webhook_event
                    (id, company_id, connector_id, provider_event_id, event_type, signature_valid, payload, status, error, processed_at)
                VALUES ({eventId}, {connector.CompanyId}, {connector.Id}, {"unverified:" + providerEventId + ":" + Guid.NewGuid().ToString("N")[..8]},
                        {eventType}, false, {SafeJson(rawBody)}::jsonb, 'failed', 'Signature invalide — événement rejeté (§12).', now())
                """, ct);
            return new WebhookIngestResult("rejected", false, eventId, "Signature invalide.");
        }
        if (connector.Status != "active")
        {
            await db.Database.ExecuteSqlAsync($"""
                INSERT INTO nexora.webhook_event
                    (id, company_id, connector_id, provider_event_id, event_type, signature_valid, payload, status, error, processed_at)
                VALUES ({eventId}, {connector.CompanyId}, {connector.Id}, {"inactive:" + providerEventId + ":" + Guid.NewGuid().ToString("N")[..8]},
                        {eventType}, true, {SafeJson(rawBody)}::jsonb, 'failed', 'Connecteur désactivé — événement non traité.', now())
                """, ct);
            return new WebhookIngestResult("rejected", true, eventId, "Connecteur désactivé.");
        }

        // Idempotence (§13) : (connector_id, provider_event_id) unique pour les événements vérifiés.
        try
        {
            await db.Database.ExecuteSqlAsync($"""
                INSERT INTO nexora.webhook_event
                    (id, company_id, connector_id, provider_event_id, event_type, signature_valid, payload, status)
                VALUES ({eventId}, {connector.CompanyId}, {connector.Id}, {providerEventId}, {eventType},
                        true, {rawBody}::jsonb, 'received')
                """, ct);
        }
        catch (Npgsql.PostgresException pg) when (pg.SqlState == "23505")
        {
            return new WebhookIngestResult("duplicate", true, null, "Événement déjà reçu — ignoré (§13).");
        }
        catch (Npgsql.PostgresException pg) when (pg.SqlState is "22P02" or "22032" or "22P05")
        {
            await db.Database.ExecuteSqlAsync($"""
                INSERT INTO nexora.webhook_event
                    (id, company_id, connector_id, provider_event_id, event_type, signature_valid, payload, status, error, processed_at)
                VALUES ({eventId}, {connector.CompanyId}, {connector.Id}, {providerEventId}, {eventType}, true,
                        {"{}"}::jsonb, 'failed', 'Charge utile JSON invalide.', now())
                ON CONFLICT (connector_id, provider_event_id) DO NOTHING
                """, ct);
            return new WebhookIngestResult("failed", true, eventId, "Charge utile invalide.");
        }

        // Dispatch par type d'événement.
        if (eventType.Contains("confirm", StringComparison.OrdinalIgnoreCase)
            || eventType.Contains("success", StringComparison.OrdinalIgnoreCase)
            || eventType.Contains("paid", StringComparison.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(reference))
            {
                await MarkAsync(eventId, "failed", "Référence de paiement absente de la charge utile.", ct);
                return new WebhookIngestResult("failed", true, eventId, "Référence absente.");
            }

            var intentId = await db.Database.SqlQuery<Guid>($"""
                SELECT id AS "Value" FROM nexora.payment_intent
                WHERE company_id = {connector.CompanyId} AND provider_reference = {reference}
                LIMIT 1
                """).FirstOrDefaultAsync(ct);
            if (intentId == Guid.Empty)
            {
                await MarkAsync(eventId, "failed", $"Aucune intention pour la référence « {reference} » (transaction inconnue §14).", ct);
                return new WebhookIngestResult("failed", true, eventId, "Transaction inconnue.");
            }

            // Paiement réel : l'événement déclenche une vérification auprès du fournisseur, jamais une confirmation aveugle.
            var confirm = await gateway.ConfirmFromProviderEventAsync(intentId, providerAmount, reference, ct);
            if (!confirm.IsSuccess)
            {
                await MarkAsync(eventId, "failed", confirm.Error!.Message, ct);
                return new WebhookIngestResult("failed", true, eventId, confirm.Error!.Message);
            }
            if (confirm.Value!.Status != "confirmed")
            {
                await MarkAsync(eventId, "failed", $"Confirmation refusée : intention « {confirm.Value.Status} » ({confirm.Value.FailureReason}).", ct);
                return new WebhookIngestResult("failed", true, eventId, $"Intention {confirm.Value.Status}.");
            }

            await MarkAsync(eventId, "processed", null, ct);
            return new WebhookIngestResult("processed", true, eventId, "Paiement confirmé.");
        }

        if (eventType.Contains("fail", StringComparison.OrdinalIgnoreCase)
            || eventType.Contains("cancel", StringComparison.OrdinalIgnoreCase))
        {
            if (!string.IsNullOrWhiteSpace(reference))
            {
                var intentId = await db.Database.SqlQuery<Guid>($"""
                    SELECT id AS "Value" FROM nexora.payment_intent
                    WHERE company_id = {connector.CompanyId} AND provider_reference = {reference} LIMIT 1
                    """).FirstOrDefaultAsync(ct);
                if (intentId != Guid.Empty)
                    await gateway.ProviderCallbackAsync(intentId,
                        new ProviderCallbackRequest(eventType.Contains("cancel", StringComparison.OrdinalIgnoreCase) ? "cancelled" : "failed", null, reference), ct);
            }
            await MarkAsync(eventId, "processed", "Échec/annulation fournisseur enregistré.", ct);
            return new WebhookIngestResult("processed", true, eventId, "Échec fournisseur enregistré.");
        }

        await MarkAsync(eventId, "processed", "Événement acquitté, aucun traitement métier associé.", ct);
        return new WebhookIngestResult("processed", true, eventId, "Acquitté.");
    }

    public async Task<IReadOnlyList<WebhookEventDto>> ListAsync(string? status, CancellationToken ct = default)
    {
        var company = tenant.ActiveCompanyId ?? throw new InvalidOperationException("Entreprise active requise.");
        var s = status?.Trim().ToLowerInvariant();
        var rows = await db.Database.SqlQuery<EventRow>($"""
            SELECT id AS "Id", connector_id AS "ConnectorId", provider_event_id AS "ProviderEventId",
                   event_type AS "EventType", signature_valid AS "SignatureValid", status AS "Status",
                   error AS "Error", received_at AS "ReceivedAt", processed_at AS "ProcessedAt"
            FROM nexora.webhook_event
            WHERE company_id = {company} AND ({s}::text IS NULL OR status = {s})
            ORDER BY received_at DESC LIMIT 200
            """).ToListAsync(ct);
        return rows.Select(r => new WebhookEventDto(
            r.Id, r.ConnectorId, r.ProviderEventId, r.EventType, r.SignatureValid, r.Status, r.Error,
            r.ReceivedAt, r.ProcessedAt)).ToList();
    }

    private Task MarkAsync(Guid eventId, string status, string? error, CancellationToken ct)
        => db.Database.ExecuteSqlAsync($"""
            UPDATE nexora.webhook_event SET status = {status}, error = {error},
                processed_at = CASE WHEN {status} = 'received' THEN NULL ELSE now() END
            WHERE id = {eventId}
            """, ct);

    private static string SafeJson(string body)
    {
        try { using var _ = JsonDocument.Parse(body); return body; }
        catch (JsonException) { return "{}"; }
    }

    private static (string EventId, string EventType, string? Reference, decimal? Amount) Parse(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            string Str(JsonElement el, string name) =>
                el.ValueKind == JsonValueKind.Object && el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
                    ? v.GetString() ?? "" : "";

            var eventId = Str(root, "id");
            if (string.IsNullOrEmpty(eventId)) eventId = Str(root, "event_id");
            if (string.IsNullOrEmpty(eventId))
                eventId = "sha-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(body)))[..16].ToLowerInvariant();

            var eventType = Str(root, "type");
            if (string.IsNullOrEmpty(eventType)) eventType = Str(root, "event");
            if (string.IsNullOrEmpty(eventType)) eventType = "unknown";

            var data = root.ValueKind == JsonValueKind.Object && root.TryGetProperty("data", out var d) ? d : root;
            var reference = Str(data, "reference");
            if (string.IsNullOrEmpty(reference)) reference = Str(data, "provider_reference");
            if (string.IsNullOrEmpty(reference)) reference = Str(root, "reference");

            decimal? amount = null;
            if (data.ValueKind == JsonValueKind.Object && data.TryGetProperty("amount", out var a)
                && a.ValueKind == JsonValueKind.Number && a.TryGetDecimal(out var av))
                amount = av;

            return (eventId, eventType, string.IsNullOrEmpty(reference) ? null : reference, amount);
        }
        catch (JsonException)
        {
            return ("sha-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(body)))[..16].ToLowerInvariant(),
                "unparseable", null, null);
        }
    }
}

/// <summary>
/// Webhooks sortants de l'API publique (§31-32). Émission → livraisons signées → nouvelle
/// tentative avec back-off (1, 5, 15, 60, 180 min) jusqu'à épuisement.
/// </summary>
public sealed class SqlOutboundWebhookService(
    NexoraDbContext db, ITenantContext tenant, IDateTimeProvider clock, IOutboundWebhookDispatcher dispatcher)
    : IOutboundWebhookService
{
    private Guid Company => tenant.ActiveCompanyId ?? throw new InvalidOperationException("Entreprise active requise.");
    private static readonly int[] BackoffMinutes = [1, 5, 15, 60, 180];

    private sealed record HookRow(Guid Id, string Name, string TargetUrl, string Events, string Status, int ConsecutiveFailures, DateTimeOffset? LastDeliveryAt);
    private sealed record DueRow(Guid Id, Guid WebhookId, string TargetUrl, string Secret, string Payload, int Attempts, int MaxAttempts);
    private sealed record DeliveryRow(
        Guid Id, Guid OutboundWebhookId, string EventType, Guid EventId, int Attempts, int MaxAttempts,
        string Status, int? ResponseCode, DateTimeOffset NextAttemptAt, string? LastError, DateTimeOffset? DeliveredAt);

    public async Task<Result<Guid>> RegisterAsync(RegisterOutboundWebhookRequest request, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(request.Name)) return Error.Validation("integrations.webhook_name", "Nom requis.");
        if (!Uri.TryCreate(request.TargetUrl, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
            return Error.Validation("integrations.webhook_url", "URL cible absolue http(s) requise.");
        var events = (request.SubscribedEvents ?? []).Select(e => e.Trim()).Where(e => e.Length > 0).Distinct().ToArray();
        if (events.Length == 0) return Error.Validation("integrations.webhook_events", "Au moins un événement à souscrire.");

        var id = Guid.NewGuid();
        await db.Database.ExecuteSqlAsync($"""
            INSERT INTO nexora.outbound_webhook (id, company_id, name, target_url, secret, subscribed_events, created_by)
            VALUES ({id}, {Company}, {request.Name.Trim()}, {request.TargetUrl.Trim()}, {IntegrationCrypto.NewSecret()},
                    {JsonSerializer.Serialize(events)}::jsonb, {tenant.UserId})
            """, ct);
        return Result.Success(id);
    }

    public async Task<IReadOnlyList<OutboundWebhookDto>> ListAsync(CancellationToken ct = default)
    {
        var rows = await db.Database.SqlQuery<HookRow>($"""
            SELECT id AS "Id", name AS "Name", target_url AS "TargetUrl", subscribed_events::text AS "Events",
                   status AS "Status", consecutive_failures AS "ConsecutiveFailures", last_delivery_at AS "LastDeliveryAt"
            FROM nexora.outbound_webhook WHERE company_id = {Company} ORDER BY name
            """).ToListAsync(ct);
        return rows.Select(r => new OutboundWebhookDto(
            r.Id, r.Name, r.TargetUrl, Deserialize(r.Events), r.Status, r.ConsecutiveFailures, r.LastDeliveryAt)).ToList();
    }

    public async Task<Result> SetStatusAsync(Guid id, string status, CancellationToken ct = default)
    {
        var s = status?.Trim().ToLowerInvariant() ?? "";
        if (s is not ("active" or "paused")) return Error.Validation("integrations.webhook_status", "Statut invalide (active/paused).");
        var n = await db.Database.ExecuteSqlAsync($"""
            UPDATE nexora.outbound_webhook SET status = {s}, updated_at = now() WHERE id = {id} AND company_id = {Company}
            """, ct);
        return n == 0 ? Error.NotFound("integrations.webhook", "Webhook sortant introuvable.") : Result.Success();
    }

    public async Task<int> EmitAsync(string eventType, Guid eventId, object payload, CancellationToken ct = default)
    {
        var body = JsonSerializer.Serialize(new
        {
            id = eventId,
            type = eventType,
            occurred_at = clock.UtcNow,
            company_id = Company,
            data = payload,
        });

        var hooks = await db.Database.SqlQuery<HookRow>($"""
            SELECT id AS "Id", name AS "Name", target_url AS "TargetUrl", subscribed_events::text AS "Events",
                   status AS "Status", consecutive_failures AS "ConsecutiveFailures", last_delivery_at AS "LastDeliveryAt"
            FROM nexora.outbound_webhook
            WHERE company_id = {Company} AND status = 'active'
              AND (subscribed_events @> to_jsonb({eventType}::text) OR subscribed_events @> '"*"'::jsonb)
            """).ToListAsync(ct);

        foreach (var h in hooks)
        {
            await db.Database.ExecuteSqlAsync($"""
                INSERT INTO nexora.outbound_webhook_delivery
                    (id, company_id, outbound_webhook_id, event_type, event_id, payload, status, next_attempt_at)
                VALUES ({Guid.NewGuid()}, {Company}, {h.Id}, {eventType}, {eventId}, {body}::jsonb, 'pending', now())
                ON CONFLICT (outbound_webhook_id, event_id) DO NOTHING
                """, ct);
        }

        await DeliverDueAsync(ct);
        return hooks.Count;
    }

    public async Task<int> DeliverDueAsync(CancellationToken ct = default)
    {
        var due = await db.Database.SqlQuery<DueRow>($"""
            SELECT d.id AS "Id", d.outbound_webhook_id AS "WebhookId", w.target_url AS "TargetUrl", w.secret AS "Secret",
                   d.payload::text AS "Payload", d.attempts AS "Attempts", d.max_attempts AS "MaxAttempts"
            FROM nexora.outbound_webhook_delivery d
            JOIN nexora.outbound_webhook w ON w.id = d.outbound_webhook_id
            WHERE d.company_id = {Company} AND d.status IN ('pending','failed') AND d.next_attempt_at <= now()
            ORDER BY d.next_attempt_at LIMIT 50
            """).ToListAsync(ct);

        foreach (var d in due)
            await AttemptAsync(d, ct);
        return due.Count;
    }

    public async Task<Result> RetryAsync(Guid deliveryId, CancellationToken ct = default)
    {
        var row = await db.Database.SqlQuery<string>($"""
            SELECT status AS "Value" FROM nexora.outbound_webhook_delivery
            WHERE id = {deliveryId} AND company_id = {Company}
            """).FirstOrDefaultAsync(ct);
        if (row is null) return Error.NotFound("integrations.delivery", "Livraison introuvable.");
        if (row == "delivered") return Error.Validation("integrations.delivery_done", "Livraison déjà effectuée.");

        await db.Database.ExecuteSqlAsync($"""
            UPDATE nexora.outbound_webhook_delivery
            SET status = CASE WHEN status = 'exhausted' THEN 'failed' ELSE status END, next_attempt_at = now(), updated_at = now()
            WHERE id = {deliveryId}
            """, ct);

        var due = await db.Database.SqlQuery<DueRow>($"""
            SELECT d.id AS "Id", d.outbound_webhook_id AS "WebhookId", w.target_url AS "TargetUrl", w.secret AS "Secret",
                   d.payload::text AS "Payload", d.attempts AS "Attempts", d.max_attempts AS "MaxAttempts"
            FROM nexora.outbound_webhook_delivery d
            JOIN nexora.outbound_webhook w ON w.id = d.outbound_webhook_id
            WHERE d.id = {deliveryId}
            """).FirstAsync(ct);
        await AttemptAsync(due, ct);
        return Result.Success();
    }

    public async Task<IReadOnlyList<OutboundDeliveryDto>> DeliveriesAsync(Guid? outboundWebhookId, CancellationToken ct = default)
    {
        var rows = await db.Database.SqlQuery<DeliveryRow>($"""
            SELECT id AS "Id", outbound_webhook_id AS "OutboundWebhookId", event_type AS "EventType", event_id AS "EventId",
                   attempts AS "Attempts", max_attempts AS "MaxAttempts", status AS "Status", response_code AS "ResponseCode",
                   next_attempt_at AS "NextAttemptAt", last_error AS "LastError", delivered_at AS "DeliveredAt"
            FROM nexora.outbound_webhook_delivery
            WHERE company_id = {Company} AND ({outboundWebhookId}::uuid IS NULL OR outbound_webhook_id = {outboundWebhookId})
            ORDER BY created_at DESC LIMIT 200
            """).ToListAsync(ct);
        return rows.Select(r => new OutboundDeliveryDto(
            r.Id, r.OutboundWebhookId, r.EventType, r.EventId, r.Attempts, r.MaxAttempts, r.Status, r.ResponseCode,
            r.NextAttemptAt, r.LastError, r.DeliveredAt)).ToList();
    }

    private async Task AttemptAsync(DueRow d, CancellationToken ct)
    {
        var signature = IntegrationCrypto.Sign(d.Secret, d.Payload);
        var (code, error) = await dispatcher.SendAsync(d.TargetUrl, signature, d.Payload, ct);
        var attempts = d.Attempts + 1;
        var ok = error is null && code is >= 200 and < 300;

        if (ok)
        {
            await db.Database.ExecuteSqlAsync($"""
                UPDATE nexora.outbound_webhook_delivery
                SET status = 'delivered', attempts = {attempts}, response_code = {code}, last_error = NULL,
                    delivered_at = now(), updated_at = now()
                WHERE id = {d.Id}
                """, ct);
            await db.Database.ExecuteSqlAsync($"""
                UPDATE nexora.outbound_webhook SET consecutive_failures = 0, last_delivery_at = now(), updated_at = now()
                WHERE id = {d.WebhookId}
                """, ct);
            return;
        }

        var status = attempts >= d.MaxAttempts ? "exhausted" : "failed";
        var next = clock.UtcNow.AddMinutes(BackoffMinutes[Math.Min(attempts - 1, BackoffMinutes.Length - 1)]);
        await db.Database.ExecuteSqlAsync($"""
            UPDATE nexora.outbound_webhook_delivery
            SET status = {status}, attempts = {attempts}, response_code = {code},
                last_error = {error ?? $"HTTP {code}"}, next_attempt_at = {next}, updated_at = now()
            WHERE id = {d.Id}
            """, ct);
        await db.Database.ExecuteSqlAsync($"""
            UPDATE nexora.outbound_webhook SET consecutive_failures = consecutive_failures + 1, updated_at = now()
            WHERE id = {d.WebhookId}
            """, ct);
    }

    private static IReadOnlyList<string> Deserialize(string json)
    {
        try { return JsonSerializer.Deserialize<List<string>>(string.IsNullOrWhiteSpace(json) ? "[]" : json) ?? []; }
        catch (JsonException) { return []; }
    }
}
