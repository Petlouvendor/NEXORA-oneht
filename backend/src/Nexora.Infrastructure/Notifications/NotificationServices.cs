using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Nexora.Application.Abstractions;
using Nexora.Application.Notifications;
using Nexora.Contracts.Notifications;
using Nexora.Domain.Common;
using Nexora.Infrastructure.Persistence;

namespace Nexora.Infrastructure.Notifications;

// =============================================================================
// Moteur de notifications (Phase 34 §2-5, §22-23). S'appuie sur `nexora.notification`
// (centre in-app, RLS utilisateur — Phase 11) + les tables `notification_preference`,
// `message_template`, `notification_outbox` (V047). Aucune donnée métier dupliquée.
// =============================================================================

public sealed class SqlMessageTemplateService(NexoraDbContext db, ITenantContext tenant, IUnitOfWork uow)
    : IMessageTemplateService
{
    private Guid? Company => tenant.ActiveCompanyId;
    private static readonly string[] Channels = ["in_app", "email", "sms", "whatsapp", "push", "webhook"];

    internal sealed record Row(Guid Id, Guid? CompanyId, string Key, string Channel, string Locale,
        string Subject, string Body, string? Description, string Variables, int Version, string Status);

    public async Task<IReadOnlyList<MessageTemplateDto>> ListAsync(CancellationToken ct = default)
    {
        var rows = await db.Database.SqlQuery<Row>($"""
            SELECT id AS "Id", company_id AS "CompanyId", key AS "Key", channel AS "Channel", locale AS "Locale",
                   subject AS "Subject", body AS "Body", description AS "Description", variables::text AS "Variables",
                   version AS "Version", status AS "Status"
            FROM nexora.message_template
            WHERE company_id IS NULL OR company_id = {Company}::uuid
            ORDER BY key, channel, locale, (company_id IS NOT NULL)
            """).ToListAsync(ct);
        return rows.Select(ToDto).ToList();
    }

    public async Task<Result<Guid>> UpsertAsync(UpsertMessageTemplateRequest r, CancellationToken ct = default)
    {
        if (Company is not { } company) return Error.Validation("template.company", "Entreprise active requise.");
        var key = r.Key?.Trim().ToLowerInvariant() ?? "";
        if (key.Length is < 3 or > 80) return Error.Validation("template.key", "Clé invalide (3–80 caractères).");
        var channel = r.Channel?.Trim().ToLowerInvariant() ?? "";
        if (!Channels.Contains(channel)) return Error.Validation("template.channel", $"Canal inconnu. Attendu : {string.Join(", ", Channels)}.");
        if (string.IsNullOrWhiteSpace(r.Body)) return Error.Validation("template.body", "Corps requis.");
        var status = r.Status?.Trim().ToLowerInvariant() ?? "published";
        if (status is not ("draft" or "published" or "archived")) return Error.Validation("template.status", "Statut invalide.");
        var locale = string.IsNullOrWhiteSpace(r.Locale) ? "fr" : r.Locale.Trim().ToLowerInvariant();

        // Liste blanche : union des variables déclarées et des jetons réellement présents.
        var declared = r.Variables ?? [];
        var used = TemplateRenderer.Tokens(r.Subject ?? "").Concat(TemplateRenderer.Tokens(r.Body)).Distinct();
        var variables = JsonSerializer.Serialize(declared.Concat(used).Distinct().ToArray());

        await db.Database.ExecuteSqlAsync($"""
            INSERT INTO nexora.message_template (id, company_id, key, channel, locale, subject, body, description, variables, status, created_by)
            VALUES ({Guid.NewGuid()}, {company}, {key}, {channel}, {locale}, {r.Subject ?? ""}, {r.Body}, {r.Description}, {variables}::jsonb, {status}, {tenant.UserId})
            ON CONFLICT ON CONSTRAINT message_template_key_uk DO UPDATE SET
                subject = EXCLUDED.subject, body = EXCLUDED.body, description = EXCLUDED.description,
                variables = EXCLUDED.variables, status = EXCLUDED.status,
                version = nexora.message_template.version + 1, updated_at = now()
            """, ct);
        var id = await db.Database.SqlQuery<Guid>($"""
            SELECT id AS "Value" FROM nexora.message_template
            WHERE company_id = {company} AND key = {key} AND channel = {channel} AND locale = {locale}
            """).FirstAsync(ct);
        await uow.SaveChangesAsync(ct);
        return Result.Success(id);
    }

    public async Task<Result<RenderedMessageDto>> RenderAsync(RenderTemplateRequest r, CancellationToken ct = default)
    {
        var tpl = await LoadAsync(r.Key, r.Channel, r.Locale, ct);
        if (tpl is null) return Error.NotFound("template.not_found", $"Gabarit « {r.Key} » ({r.Channel}/{r.Locale}) introuvable.");
        var (subject, missS) = TemplateRenderer.Render(tpl.Subject, r.Variables);
        var (body, missB) = TemplateRenderer.Render(tpl.Body, r.Variables);
        return Result.Success(new RenderedMessageDto(subject, body, missS.Concat(missB).Distinct().ToList()));
    }

    internal async Task<Row?> LoadAsync(string key, string channel, string locale, CancellationToken ct)
    {
        locale = string.IsNullOrWhiteSpace(locale) ? "fr" : locale.Trim().ToLowerInvariant();
        return await db.Database.SqlQuery<Row>($"""
            SELECT id AS "Id", company_id AS "CompanyId", key AS "Key", channel AS "Channel", locale AS "Locale",
                   subject AS "Subject", body AS "Body", description AS "Description", variables::text AS "Variables",
                   version AS "Version", status AS "Status"
            FROM nexora.message_template
            WHERE key = {key} AND channel = {channel} AND status <> 'archived'
              AND (company_id = {Company}::uuid OR company_id IS NULL)
            ORDER BY (company_id IS NOT NULL) DESC, (locale = {locale}) DESC, locale
            LIMIT 1
            """).FirstOrDefaultAsync(ct);
    }

    private static MessageTemplateDto ToDto(Row r) => new(
        r.Id, r.CompanyId, r.Key, r.Channel, r.Locale, r.Subject, r.Body, r.Description,
        JsonSerializer.Deserialize<List<string>>(r.Variables) ?? [], r.Version, r.Status, r.CompanyId is null);
}

// -----------------------------------------------------------------------------

public sealed class SqlNotificationPreferenceService(NexoraDbContext db, ITenantContext tenant, IUnitOfWork uow)
    : INotificationPreferenceService
{
    private Guid UserId => tenant.UserId ?? throw new InvalidOperationException("Utilisateur requis.");

    internal sealed record Row(Guid Id, Guid? CompanyId, string? Category, bool InApp, bool Email, bool Sms,
        bool Webhook, short MinPriority, string Digest, string? QuietFrom, string? QuietTo);

    public async Task<IReadOnlyList<NotificationPreferenceDto>> ListAsync(CancellationToken ct = default)
    {
        var rows = await db.Database.SqlQuery<Row>($"""
            SELECT id AS "Id", company_id AS "CompanyId", category AS "Category", in_app AS "InApp", email AS "Email",
                   sms AS "Sms", webhook AS "Webhook", min_priority AS "MinPriority", digest AS "Digest",
                   quiet_from::text AS "QuietFrom", quiet_to::text AS "QuietTo"
            FROM nexora.notification_preference WHERE user_id = {UserId}
            ORDER BY (company_id IS NULL), (category IS NULL), category
            """).ToListAsync(ct);
        return rows.Select(r => new NotificationPreferenceDto(r.Id, r.CompanyId, r.Category, r.InApp, r.Email,
            r.Sms, r.Webhook, r.MinPriority, r.Digest, r.QuietFrom, r.QuietTo)).ToList();
    }

    public async Task<Result<NotificationPreferenceDto>> UpsertAsync(UpsertNotificationPreferenceRequest r, CancellationToken ct = default)
    {
        if (r.MinPriority is < 1 or > 5) return Error.Validation("preference.priority", "Priorité minimale entre 1 et 5.");
        if (r.Digest is not ("instant" or "daily" or "weekly" or "off")) return Error.Validation("preference.digest", "Résumé invalide.");
        var category = string.IsNullOrWhiteSpace(r.Category) ? null : r.Category.Trim().ToLowerInvariant();

        await db.Database.ExecuteSqlAsync($"""
            INSERT INTO nexora.notification_preference
                (id, user_id, company_id, category, in_app, email, sms, webhook, min_priority, digest, quiet_from, quiet_to)
            VALUES ({Guid.NewGuid()}, {UserId}, {r.CompanyId}, {category}, {r.InApp}, {r.Email}, {r.Sms}, {r.Webhook},
                    {r.MinPriority}, {r.Digest}, {r.QuietFrom}::time, {r.QuietTo}::time)
            ON CONFLICT ON CONSTRAINT notification_preference_uk DO UPDATE SET
                in_app = EXCLUDED.in_app, email = EXCLUDED.email, sms = EXCLUDED.sms, webhook = EXCLUDED.webhook,
                min_priority = EXCLUDED.min_priority, digest = EXCLUDED.digest,
                quiet_from = EXCLUDED.quiet_from, quiet_to = EXCLUDED.quiet_to, updated_at = now()
            """, ct);
        await uow.SaveChangesAsync(ct);

        var id = await db.Database.SqlQuery<Guid>($"""
            SELECT id AS "Value" FROM nexora.notification_preference
            WHERE user_id = {UserId} AND company_id IS NOT DISTINCT FROM {r.CompanyId}::uuid
              AND category IS NOT DISTINCT FROM {category}::text
            """).FirstAsync(ct);
        var dto = (await ListAsync(ct)).First(p => p.Id == id);
        return Result.Success(dto);
    }

    public async Task<Result> DeleteAsync(Guid id, CancellationToken ct = default)
    {
        var n = await db.Database.ExecuteSqlAsync($"""
            DELETE FROM nexora.notification_preference WHERE id = {id} AND user_id = {UserId}
            """, ct);
        return n == 0 ? Error.NotFound("preference.not_found", "Préférence introuvable.") : Result.Success();
    }
}
