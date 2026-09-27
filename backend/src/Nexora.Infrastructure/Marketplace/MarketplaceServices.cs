using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Nexora.Application.Abstractions;
using Nexora.Application.Integrations;
using Nexora.Application.Marketplace;
using Nexora.Contracts.Common;
using Nexora.Contracts.Marketplace;
using Nexora.Domain.Common;
using Nexora.Infrastructure.Persistence;

namespace Nexora.Infrastructure.Marketplace;

internal static class MarketplaceJson
{
    public static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);
}

/// <summary>
/// Catalogue marketplace curé par NEXORA (Phase 46 §41-42, §45). Table plateforme
/// (<c>marketplace_app</c>, sans RLS entreprise) — la lecture publique ne montre que les apps
/// <c>approved</c> à un appelant sans <c>platform.manage_marketplace</c>.
/// </summary>
public sealed class SqlMarketplaceCatalogService(NexoraDbContext db, ITenantContext tenant, IUnitOfWork uow, IDateTimeProvider clock)
    : IMarketplaceCatalogService
{
    private static readonly string[] Categories =
    [
        "payment","bank","mobile_money","accounting_export","ecommerce","crm","erp","delivery",
        "sms_email","messaging","storage","signature","ocr","pos_terminal","other",
    ];
    private static readonly string[] PricingModels = ["free", "paid", "subscription", "usage"];
    private static readonly string[] Decisions = ["approve", "reject", "suspend"];

    private Guid CurrentUser => tenant.UserId ?? throw new InvalidOperationException("Utilisateur requis.");

    // Concaténation de texte SQL brut, jamais interpolée dans un FormattableString ($"...") —
    // sinon EF traiterait ce fragment comme une VALEUR de paramètre plutôt que du texte SQL.
    // Toujours combinée via SqlQueryRaw + tableau de paramètres positionnels {0}, {1}, ….
    private const string Select = """
        SELECT id AS "Id", slug AS "Slug", name AS "Name", publisher_name AS "PublisherName",
               publisher_contact AS "PublisherContact", category AS "Category", certification AS "Certification",
               status AS "Status", version AS "Version", description AS "Description",
               requested_scopes::text AS "RequestedScopes", pricing_model AS "PricingModel",
               price_amount AS "PriceAmount", price_currency_code AS "PriceCurrencyCode",
               homepage_url AS "HomepageUrl", privacy_policy_url AS "PrivacyPolicyUrl", support_contact AS "SupportContact",
               submitted_by AS "SubmittedBy", reviewed_by AS "ReviewedBy", reviewed_at AS "ReviewedAt",
               review_notes AS "ReviewNotes", published_at AS "PublishedAt", created_at AS "CreatedAt"
        FROM nexora.marketplace_app
        """;

    private sealed record Row(Guid Id, string Slug, string Name, string PublisherName, string? PublisherContact,
        string Category, string Certification, string Status, string Version, string? Description,
        string RequestedScopes, string PricingModel, decimal? PriceAmount, string PriceCurrencyCode,
        string? HomepageUrl, string? PrivacyPolicyUrl, string? SupportContact, Guid? SubmittedBy,
        Guid? ReviewedBy, DateTimeOffset? ReviewedAt, string? ReviewNotes, DateTimeOffset? PublishedAt, DateTimeOffset CreatedAt);

    private static MarketplaceAppDto ToDto(Row r) => new(
        r.Id, r.Slug, r.Name, r.PublisherName, r.PublisherContact, r.Category, r.Certification, r.Status, r.Version,
        r.Description, JsonSerializer.Deserialize<List<string>>(r.RequestedScopes) ?? [], r.PricingModel, r.PriceAmount,
        r.PriceCurrencyCode, r.HomepageUrl, r.PrivacyPolicyUrl, r.SupportContact, r.ReviewedBy, r.ReviewedAt,
        r.ReviewNotes, r.PublishedAt, r.CreatedAt);

    public async Task<CursorPage<MarketplaceAppDto>> ListAsync(
        string? category, string? status, string? cursor, int limit, bool includeAllStatuses, CancellationToken ct = default)
    {
        var take = Math.Clamp(limit <= 0 ? 20 : limit, 1, 100);
        var sql = new StringBuilder(Select + " WHERE 1=1");
        var args = new List<object>();

        void Add(string fragment, object value)
        {
            args.Add(value);
            sql.Append(' ').Append(fragment.Replace("{}", "{" + (args.Count - 1) + "}"));
        }

        if (!includeAllStatuses) sql.Append(" AND status = 'approved'");
        else if (!string.IsNullOrWhiteSpace(status)) Add("AND status = {}", status.Trim().ToLowerInvariant());

        if (!string.IsNullOrWhiteSpace(category)) Add("AND category = {}", category.Trim().ToLowerInvariant());

        if (DecodeCursor(cursor) is { } after)
        {
            var idxCreatedAt = args.Count; args.Add(after.CreatedAt);
            var idxId = args.Count; args.Add(after.Id);
            sql.Append($" AND (created_at, id) < ({{{idxCreatedAt}}}, {{{idxId}}})");
        }

        sql.Append(" ORDER BY created_at DESC, id DESC LIMIT ").Append(take + 1);

        var rows = await db.Database.SqlQueryRaw<Row>(sql.ToString(), args.ToArray()).ToListAsync(ct);
        var hasMore = rows.Count > take;
        var page = rows.Take(take).ToList();
        var next = hasMore ? EncodeCursor(page[^1].CreatedAt, page[^1].Id) : null;
        return new CursorPage<MarketplaceAppDto>(page.Select(ToDto).ToList(), next);
    }

    public async Task<Result<MarketplaceAppDto>> GetAsync(Guid id, bool includeAllStatuses, CancellationToken ct = default)
    {
        var row = await db.Database.SqlQueryRaw<Row>(Select + " WHERE id = {0}", id).FirstOrDefaultAsync(ct);
        if (row is null || (!includeAllStatuses && row.Status != "approved"))
            return Error.NotFound("marketplace.app_not_found", "Application introuvable.");
        return Result.Success(ToDto(row));
    }

    public async Task<Result<MarketplaceAppDto>> SubmitAsync(SubmitAppRequest request, CancellationToken ct = default)
    {
        var slug = request.Slug?.Trim().ToLowerInvariant() ?? "";
        if (slug.Length is < 2 or > 60) return Error.Validation("marketplace.slug", "Identifiant invalide (2–60 caractères).");
        if (string.IsNullOrWhiteSpace(request.Name)) return Error.Validation("marketplace.name", "Nom requis.");
        if (string.IsNullOrWhiteSpace(request.PublisherName)) return Error.Validation("marketplace.publisher", "Éditeur requis.");
        var category = request.Category?.Trim().ToLowerInvariant() ?? "";
        if (!Categories.Contains(category)) return Error.Validation("marketplace.category", $"Catégorie invalide ({string.Join('/', Categories)}).");
        var pricing = string.IsNullOrWhiteSpace(request.PricingModel) ? "free" : request.PricingModel.Trim().ToLowerInvariant();
        if (!PricingModels.Contains(pricing)) return Error.Validation("marketplace.pricing_model", "Modèle tarifaire invalide.");
        if (pricing == "free" && request.PriceAmount is not null)
            return Error.Validation("marketplace.price", "Un modèle gratuit ne porte pas de prix.");
        if (pricing != "free" && (request.PriceAmount is null || request.PriceAmount < 0))
            return Error.Validation("marketplace.price", "Prix requis (≥ 0) pour un modèle payant.");

        var scopes = (request.RequestedScopes ?? []).Select(s => s.Trim().ToLowerInvariant())
            .Where(s => s.Length > 0).Distinct().ToArray();

        var id = Guid.NewGuid();
        try
        {
            await db.Database.ExecuteSqlAsync($"""
                INSERT INTO nexora.marketplace_app
                    (id, slug, name, publisher_name, publisher_contact, category, status, version, description,
                     requested_scopes, pricing_model, price_amount, price_currency_code, homepage_url,
                     privacy_policy_url, support_contact, submitted_by)
                VALUES ({id}, {slug}, {request.Name.Trim()}, {request.PublisherName.Trim()}, {request.PublisherContact},
                        {category}, 'pending_review', {request.Version.Trim()}, {request.Description},
                        {JsonSerializer.Serialize(scopes)}::jsonb, {pricing}, {request.PriceAmount},
                        {request.PriceCurrencyCode.ToUpperInvariant()}, {request.HomepageUrl}, {request.PrivacyPolicyUrl},
                        {request.SupportContact}, {CurrentUser})
                """, ct);
        }
        catch (Npgsql.PostgresException pg) when (pg.SqlState == "23505")
        {
            return Error.Conflict("marketplace.slug_taken", "Cet identifiant d'application est déjà utilisé.");
        }
        await uow.SaveChangesAsync(ct);
        return await GetAsync(id, includeAllStatuses: true, ct);
    }

    public async Task<Result<MarketplaceAppDto>> ReviewAsync(Guid id, ReviewAppRequest request, CancellationToken ct = default)
    {
        var existing = await db.Database.SqlQueryRaw<Row>(Select + " WHERE id = {0}", id).FirstOrDefaultAsync(ct);
        if (existing is null) return Error.NotFound("marketplace.app_not_found", "Application introuvable.");
        if (existing.SubmittedBy is { } submitter && submitter == CurrentUser)
            return Error.Forbidden("marketplace.self_review", "Vous ne pouvez pas réviser votre propre soumission.");

        var decision = request.Decision?.Trim().ToLowerInvariant() ?? "";
        if (!Decisions.Contains(decision)) return Error.Validation("marketplace.decision", "Décision invalide (approve/reject/suspend).");
        if (decision == "suspend" && existing.Status != "approved")
            return Error.Conflict("marketplace.not_approved", "Seule une application publiée peut être suspendue.");
        if (decision != "suspend" && existing.Status != "pending_review")
            return Error.Conflict("marketplace.not_pending", "Application déjà tranchée.");

        var newStatus = decision switch { "approve" => "approved", "reject" => "rejected", _ => "suspended" };
        var now = clock.UtcNow;
        await db.Database.ExecuteSqlAsync($"""
            UPDATE nexora.marketplace_app
            SET status = {newStatus}, reviewed_by = {CurrentUser}, reviewed_at = {now}, review_notes = {request.Notes},
                published_at = CASE WHEN {newStatus} = 'approved' AND published_at IS NULL THEN {now} ELSE published_at END,
                updated_at = now()
            WHERE id = {id}
            """, ct);
        await uow.SaveChangesAsync(ct);
        return await GetAsync(id, includeAllStatuses: true, ct);
    }

    private static string EncodeCursor(DateTimeOffset createdAt, Guid id)
        => Convert.ToBase64String(Encoding.UTF8.GetBytes($"{createdAt.UtcTicks}|{id}"));

    private static (DateTimeOffset CreatedAt, Guid Id)? DecodeCursor(string? cursor)
    {
        if (string.IsNullOrWhiteSpace(cursor)) return null;
        try
        {
            var raw = Encoding.UTF8.GetString(Convert.FromBase64String(cursor));
            var parts = raw.Split('|');
            if (parts.Length != 2 || !long.TryParse(parts[0], out var ticks) || !Guid.TryParse(parts[1], out var id)) return null;
            return (new DateTimeOffset(ticks, TimeSpan.Zero), id);
        }
        catch { return null; }
    }
}

// -----------------------------------------------------------------------------

/// <summary>Installation d'une app marketplace par l'entreprise active (Phase 46 §43-44, §56).</summary>
public sealed class SqlAppInstallationService(NexoraDbContext db, ITenantContext tenant, IUnitOfWork uow)
    : IAppInstallationService
{
    private Guid Company => tenant.ActiveCompanyId ?? throw new InvalidOperationException("Entreprise active requise.");
    private Guid CurrentUser => tenant.UserId ?? throw new InvalidOperationException("Utilisateur requis.");

    private const string Select = """
        SELECT i.id AS "Id", i.marketplace_app_id AS "MarketplaceAppId", a.slug AS "AppSlug", a.name AS "AppName",
               a.category AS "AppCategory", a.certification AS "AppCertification", i.status AS "Status",
               i.granted_scopes::text AS "GrantedScopes", i.config::text AS "Config",
               i.installed_at AS "InstalledAt", i.disabled_at AS "DisabledAt", i.uninstalled_at AS "UninstalledAt"
        FROM nexora.app_installation i JOIN nexora.marketplace_app a ON a.id = i.marketplace_app_id
        """;

    private sealed record Row(Guid Id, Guid MarketplaceAppId, string AppSlug, string AppName, string AppCategory,
        string AppCertification, string Status, string GrantedScopes, string Config,
        DateTimeOffset InstalledAt, DateTimeOffset? DisabledAt, DateTimeOffset? UninstalledAt);

    private static AppInstallationDto ToDto(Row r) => new(
        r.Id, r.MarketplaceAppId, r.AppSlug, r.AppName, r.AppCategory, r.AppCertification, r.Status,
        JsonSerializer.Deserialize<List<string>>(r.GrantedScopes) ?? [],
        JsonSerializer.Deserialize<Dictionary<string, string>>(r.Config) ?? [], r.InstalledAt, r.DisabledAt, r.UninstalledAt);

    public async Task<IReadOnlyList<AppInstallationDto>> ListInstalledAsync(CancellationToken ct = default)
    {
        var rows = await db.Database.SqlQueryRaw<Row>(Select + " WHERE i.company_id = {0} ORDER BY i.installed_at DESC", Company)
            .ToListAsync(ct);
        return rows.Select(ToDto).ToList();
    }

    public async Task<Result<AppInstallationDto>> InstallAsync(Guid marketplaceAppId, InstallAppRequest request, CancellationToken ct = default)
    {
        var app = await db.Database.SqlQuery<AppLookup>($"""
            SELECT status AS "Status", requested_scopes::text AS "RequestedScopes"
            FROM nexora.marketplace_app WHERE id = {marketplaceAppId}
            """).FirstOrDefaultAsync(ct);
        if (app is null || app.Status != "approved")
            return Error.NotFound("marketplace.app_not_found", "Application introuvable ou non publiée.");

        var requested = JsonSerializer.Deserialize<HashSet<string>>(app.RequestedScopes) ?? [];
        var granted = (request.GrantedScopes ?? []).Select(s => s.Trim().ToLowerInvariant())
            .Where(s => s.Length > 0).Distinct().ToArray();
        var excess = granted.Where(s => !requested.Contains(s)).ToArray();
        if (excess.Length > 0)
            return Error.Forbidden("marketplace.scope_excess", $"Scopes hors du périmètre demandé par l'application : {string.Join(", ", excess)}.");

        var config = JsonSerializer.Serialize(request.Config ?? new Dictionary<string, string>(), MarketplaceJson.Web);
        var id = Guid.NewGuid();
        await db.Database.ExecuteSqlAsync($"""
            INSERT INTO nexora.app_installation (id, company_id, marketplace_app_id, status, granted_scopes, config, installed_by)
            VALUES ({id}, {Company}, {marketplaceAppId}, 'installed', {JsonSerializer.Serialize(granted)}::jsonb, {config}::jsonb, {CurrentUser})
            ON CONFLICT ON CONSTRAINT app_installation_company_app_uk DO UPDATE SET
                status = 'installed', granted_scopes = EXCLUDED.granted_scopes, config = EXCLUDED.config,
                installed_by = EXCLUDED.installed_by, installed_at = now(), disabled_at = NULL, uninstalled_at = NULL, updated_at = now()
            """, ct);
        await uow.SaveChangesAsync(ct);

        var row = await db.Database.SqlQueryRaw<Row>(Select + " WHERE i.company_id = {0} AND i.marketplace_app_id = {1}", Company, marketplaceAppId)
            .FirstAsync(ct);
        return Result.Success(ToDto(row));
    }

    private sealed record AppLookup(string Status, string RequestedScopes);

    public async Task<Result> DisableAsync(Guid marketplaceAppId, CancellationToken ct = default)
    {
        var n = await db.Database.ExecuteSqlAsync($"""
            UPDATE nexora.app_installation SET status = 'disabled', disabled_at = now(), updated_at = now()
            WHERE company_id = {Company} AND marketplace_app_id = {marketplaceAppId} AND status = 'installed'
            """, ct);
        return n == 0 ? Error.NotFound("marketplace.installation_not_found", "Installation introuvable ou déjà inactive.") : Result.Success();
    }

    public async Task<Result> UninstallAsync(Guid marketplaceAppId, CancellationToken ct = default)
    {
        var installationId = await db.Database.SqlQuery<Guid>($"""
            SELECT id AS "Value" FROM nexora.app_installation
            WHERE company_id = {Company} AND marketplace_app_id = {marketplaceAppId} AND status <> 'uninstalled'
            """).FirstOrDefaultAsync(ct);
        if (installationId == Guid.Empty) return Error.NotFound("marketplace.installation_not_found", "Installation introuvable.");

        await db.Database.ExecuteSqlAsync($"""
            UPDATE nexora.app_installation SET status = 'uninstalled', uninstalled_at = now(), updated_at = now()
            WHERE id = {installationId}
            """, ct);

        // §46 : désinstaller révoque toute clé d'API émise pour cette installation — aucun accès résiduel.
        await db.Database.ExecuteSqlAsync($"""
            UPDATE nexora.api_key SET revoked_at = now(), updated_at = now()
            WHERE app_installation_id = {installationId} AND revoked_at IS NULL
            """, ct);
        await uow.SaveChangesAsync(ct);
        return Result.Success();
    }
}

// -----------------------------------------------------------------------------

/// <summary>Usage quotidien des clés (portail développeur §47-48).</summary>
public sealed class SqlDeveloperUsageQuery(NexoraDbContext db, ITenantContext tenant) : IDeveloperUsageQuery
{
    private Guid Company => tenant.ActiveCompanyId ?? throw new InvalidOperationException("Entreprise active requise.");

    private sealed record Row(Guid ApiKeyId, string KeyName, DateOnly UsageDate, int RequestCount, int RateLimitedCount);

    public async Task<IReadOnlyList<DeveloperKeyUsageDto>> UsageAsync(int days, CancellationToken ct = default)
    {
        var horizon = Math.Clamp(days <= 0 ? 30 : days, 1, 90);
        var rows = await db.Database.SqlQuery<Row>($"""
            SELECT k.id AS "ApiKeyId", k.name AS "KeyName", u.usage_date AS "UsageDate",
                   u.request_count AS "RequestCount", u.rate_limited_count AS "RateLimitedCount"
            FROM nexora.api_key_daily_usage u
            JOIN nexora.api_key k ON k.id = u.api_key_id
            WHERE u.company_id = {Company} AND u.usage_date >= (current_date - {horizon})
            ORDER BY k.name, u.usage_date
            """).ToListAsync(ct);

        return rows.GroupBy(r => (r.ApiKeyId, r.KeyName))
            .Select(g => new DeveloperKeyUsageDto(g.Key.ApiKeyId, g.Key.KeyName,
                g.Select(r => new ApiKeyDailyUsageDto(r.UsageDate, r.RequestCount, r.RateLimitedCount)).ToList()))
            .ToList();
    }
}

// -----------------------------------------------------------------------------

/// <summary>
/// Enregistre l'usage d'une clé (Phase 46 §9, §47-48). Exécuté par le middleware d'authentification,
/// avant l'établissement du contexte tenant — portée système, best-effort (§18 : la télémétrie ne
/// doit jamais faire échouer une requête authentifiée).
/// </summary>
public sealed class SqlApiKeyUsageRecorder(NexoraDbContext db, ITenantContext tenant) : IApiKeyUsageRecorder
{
    public async Task RecordAsync(Guid apiKeyId, Guid companyId, bool rateLimited, CancellationToken ct = default)
    {
        try
        {
            using var _ = tenant.BeginSystemScope();
            var limited = rateLimited ? 1 : 0;
            await db.Database.ExecuteSqlAsync($"""
                INSERT INTO nexora.api_key_daily_usage (company_id, api_key_id, usage_date, request_count, rate_limited_count)
                VALUES ({companyId}, {apiKeyId}, current_date, 1, {limited})
                ON CONFLICT ON CONSTRAINT api_key_daily_usage_uk DO UPDATE SET
                    request_count = nexora.api_key_daily_usage.request_count + 1,
                    rate_limited_count = nexora.api_key_daily_usage.rate_limited_count + {limited},
                    updated_at = now()
                """, ct);
            await db.SaveChangesAsync(ct);
        }
        catch { /* télémétrie best-effort — n'affecte jamais la requête authentifiée */ }
    }
}
