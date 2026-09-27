using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Nexora.Application.Abstractions;
using Nexora.Application.Preferences;
using Nexora.Contracts.Preferences;
using Nexora.Domain.Common;
using Nexora.Infrastructure.Persistence;

namespace Nexora.Infrastructure.Preferences;

/// <summary>Préférences UX de l'utilisateur courant (Phase 13 §36) — UPSERT partiel, RLS `user_id`.</summary>
public sealed class SqlUserPreferenceService(NexoraDbContext db, ITenantContext tenant) : IUserPreferenceService
{
    private Guid User => tenant.UserId ?? throw new InvalidOperationException("Utilisateur requis.");
    private static readonly string[] Themes = ["system", "light", "dark"];
    private static readonly string[] Densities = ["comfortable", "compact"];
    private static readonly string[] DashModes = ["summary", "detailed"];
    private static readonly JsonElement EmptyObject = JsonDocument.Parse("{}").RootElement.Clone();

    private sealed record Row(
        string Theme, string Density, string Locale, bool ReducedMotion, string DashboardMode,
        string Favorites, string TableColumns, string NotificationPrefs,
        bool HighContrast, decimal TextScale, string Timezone);

    public async Task<UserPreferenceDto> GetAsync(CancellationToken ct = default)
    {
        var row = await LoadAsync(ct);
        return row is null
            ? new UserPreferenceDto("system", "comfortable", "fr-FR", false, "summary", [], EmptyObject, EmptyObject,
                false, 1.00m, "America/Port-au-Prince")
            : Map(row);
    }

    public async Task<Result<UserPreferenceDto>> UpdateAsync(UpdateUserPreferenceRequest request, CancellationToken ct = default)
    {
        var current = await LoadAsync(ct);

        var theme = Pick(request.Theme, current?.Theme ?? "system", Themes);
        if (theme is null) return Error.Validation("preferences.theme", "Thème invalide (system/light/dark).");
        var density = Pick(request.Density, current?.Density ?? "comfortable", Densities);
        if (density is null) return Error.Validation("preferences.density", "Densité invalide (comfortable/compact).");
        var dash = Pick(request.DashboardMode, current?.DashboardMode ?? "summary", DashModes);
        if (dash is null) return Error.Validation("preferences.dashboard_mode", "Mode invalide (summary/detailed).");

        var locale = Clean(request.Locale) ?? current?.Locale ?? "fr-FR";
        if (locale.Length is < 2 or > 12) return Error.Validation("preferences.locale", "Code de langue invalide.");
        var reduced = request.ReducedMotion ?? current?.ReducedMotion ?? false;

        var favorites = request.Favorites is not null
            ? JsonSerializer.Serialize(request.Favorites.Where(f => !string.IsNullOrWhiteSpace(f)).Distinct().Take(30))
            : current?.Favorites ?? "[]";
        var tableColumns = request.TableColumns is { } tc ? tc.GetRawText() : current?.TableColumns ?? "{}";
        var notif = request.NotificationPrefs is { } np ? np.GetRawText() : current?.NotificationPrefs ?? "{}";

        var highContrast = request.HighContrast ?? current?.HighContrast ?? false;
        var textScale = request.TextScale ?? current?.TextScale ?? 1.00m;
        if (textScale is < 0.85m or > 1.50m) return Error.Validation("preferences.text_scale", "Échelle de texte hors bornes (0,85–1,50).");
        textScale = Math.Round(textScale, 2);
        var timezone = Clean(request.Timezone) ?? current?.Timezone ?? "America/Port-au-Prince";
        if (timezone.Length is < 3 or > 64) return Error.Validation("preferences.timezone", "Fuseau horaire invalide.");

        await db.Database.ExecuteSqlAsync($"""
            INSERT INTO nexora.user_preference
                (user_id, theme, density, locale, reduced_motion, dashboard_mode, favorites, table_columns, notification_prefs,
                 high_contrast, text_scale, timezone)
            VALUES ({User}, {theme}, {density}, {locale}, {reduced}, {dash}, {favorites}::jsonb, {tableColumns}::jsonb, {notif}::jsonb,
                    {highContrast}, {textScale}, {timezone})
            ON CONFLICT (user_id) DO UPDATE SET
                theme = EXCLUDED.theme, density = EXCLUDED.density, locale = EXCLUDED.locale,
                reduced_motion = EXCLUDED.reduced_motion, dashboard_mode = EXCLUDED.dashboard_mode,
                favorites = EXCLUDED.favorites, table_columns = EXCLUDED.table_columns,
                notification_prefs = EXCLUDED.notification_prefs, high_contrast = EXCLUDED.high_contrast,
                text_scale = EXCLUDED.text_scale, timezone = EXCLUDED.timezone, updated_at = now()
            """, ct);

        return Result.Success(Map((await LoadAsync(ct))!));
    }

    private Task<Row?> LoadAsync(CancellationToken ct) => db.Database.SqlQuery<Row>($"""
        SELECT theme AS "Theme", density AS "Density", locale AS "Locale", reduced_motion AS "ReducedMotion",
               dashboard_mode AS "DashboardMode", favorites::text AS "Favorites",
               table_columns::text AS "TableColumns", notification_prefs::text AS "NotificationPrefs",
               high_contrast AS "HighContrast", text_scale AS "TextScale", timezone AS "Timezone"
        FROM nexora.user_preference WHERE user_id = {User}
        """).FirstOrDefaultAsync(ct);

    private static string? Pick(string? incoming, string fallback, string[] allowed)
    {
        if (incoming is null) return fallback;
        var v = incoming.Trim().ToLowerInvariant();
        return allowed.Contains(v) ? v : null;
    }

    private static string? Clean(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    private static UserPreferenceDto Map(Row r) => new(
        r.Theme, r.Density, r.Locale, r.ReducedMotion, r.DashboardMode,
        JsonSerializer.Deserialize<List<string>>(string.IsNullOrWhiteSpace(r.Favorites) ? "[]" : r.Favorites) ?? [],
        Parse(r.TableColumns), Parse(r.NotificationPrefs), r.HighContrast, r.TextScale, r.Timezone);

    private static JsonElement Parse(string json)
    {
        try { return JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json).RootElement.Clone(); }
        catch (JsonException) { return EmptyObject; }
    }
}

/// <summary>Personnalisation de l'entreprise active (Phase 13 §35).</summary>
public sealed class SqlCompanyBrandingService(NexoraDbContext db, ITenantContext tenant) : ICompanyBrandingService
{
    private Guid Company => tenant.ActiveCompanyId ?? throw new InvalidOperationException("Entreprise active requise.");
    private static readonly string[] ReceiptFormats = ["standard", "compact", "detailed"];

    private sealed record Row(
        string? DisplayName, string? AccentColor, string? LogoUrl, string ReceiptFormat,
        string? DocumentFooter, string DateFormat, string NumberLocale, string DefaultLocale);

    public async Task<CompanyBrandingDto> GetAsync(CancellationToken ct = default)
    {
        var row = await LoadAsync(ct);
        return row is null
            ? new CompanyBrandingDto(Company, null, null, null, "standard", null, "dd/MM/yyyy", "fr-FR", "fr-FR")
            : new CompanyBrandingDto(Company, row.DisplayName, row.AccentColor, row.LogoUrl, row.ReceiptFormat,
                row.DocumentFooter, row.DateFormat, row.NumberLocale, row.DefaultLocale);
    }

    public async Task<Result<CompanyBrandingDto>> UpdateAsync(UpdateCompanyBrandingRequest request, CancellationToken ct = default)
    {
        var current = await LoadAsync(ct);

        string? accent = current?.AccentColor;
        if (request.AccentColor is not null)
        {
            var a = request.AccentColor.Trim();
            if (a.Length == 0) accent = null;
            else if (!IsValidAccent(a, out var reason)) return Error.Validation("branding.accent_color", reason);
            else accent = a.ToUpperInvariant();
        }

        var receipt = request.ReceiptFormat is null
            ? current?.ReceiptFormat ?? "standard"
            : request.ReceiptFormat.Trim().ToLowerInvariant();
        if (!ReceiptFormats.Contains(receipt))
            return Error.Validation("branding.receipt_format", "Format de reçu invalide (standard/compact/detailed).");

        var displayName = Trim(request.DisplayName, current?.DisplayName, 120);
        var logo = Trim(request.LogoUrl, current?.LogoUrl, 500);
        if (logo is not null && !(logo.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
              || logo.StartsWith("data:image/", StringComparison.OrdinalIgnoreCase)))
            return Error.Validation("branding.logo_url", "Le logo doit être une URL https ou une donnée image encodée.");
        var footer = Trim(request.DocumentFooter, current?.DocumentFooter, 400);
        var dateFmt = Trim(request.DateFormat, current?.DateFormat, 20) ?? "dd/MM/yyyy";
        var numberLocale = Trim(request.NumberLocale, current?.NumberLocale, 12) ?? "fr-FR";
        var defaultLocale = Trim(request.DefaultLocale, current?.DefaultLocale, 12) ?? "fr-FR";

        await db.Database.ExecuteSqlAsync($"""
            INSERT INTO nexora.company_branding
                (company_id, display_name, accent_color, logo_url, receipt_format, document_footer,
                 date_format, number_locale, default_locale, configured_by)
            VALUES ({Company}, {displayName}, {accent}, {logo}, {receipt}, {footer},
                    {dateFmt}, {numberLocale}, {defaultLocale}, {tenant.UserId})
            ON CONFLICT (company_id) DO UPDATE SET
                display_name = EXCLUDED.display_name, accent_color = EXCLUDED.accent_color, logo_url = EXCLUDED.logo_url,
                receipt_format = EXCLUDED.receipt_format, document_footer = EXCLUDED.document_footer,
                date_format = EXCLUDED.date_format, number_locale = EXCLUDED.number_locale,
                default_locale = EXCLUDED.default_locale, configured_by = EXCLUDED.configured_by, updated_at = now()
            """, ct);

        return Result.Success(await GetAsync(ct));
    }

    private Task<Row?> LoadAsync(CancellationToken ct) => db.Database.SqlQuery<Row>($"""
        SELECT display_name AS "DisplayName", accent_color AS "AccentColor", logo_url AS "LogoUrl",
               receipt_format AS "ReceiptFormat", document_footer AS "DocumentFooter",
               date_format AS "DateFormat", number_locale AS "NumberLocale", default_locale AS "DefaultLocale"
        FROM nexora.company_branding WHERE company_id = {Company}
        """).FirstOrDefaultAsync(ct);

    private static string? Trim(string? incoming, string? fallback, int max)
    {
        if (incoming is null) return fallback;
        var v = incoming.Trim();
        return v.Length == 0 ? null : v[..Math.Min(v.Length, max)];
    }

    /// <summary>Hex valide + luminance relative bornée : l'accent doit rester lisible sur les surfaces
    /// sombres comme claires et porter du texte foncé (§35 : ne pas dégrader l'accessibilité).</summary>
    private static bool IsValidAccent(string hex, out string reason)
    {
        reason = "";
        if (!System.Text.RegularExpressions.Regex.IsMatch(hex, "^#[0-9A-Fa-f]{6}$"))
        {
            reason = "Couleur d'accent invalide : format #RRGGBB attendu.";
            return false;
        }
        double Ch(int start)
        {
            var c = int.Parse(hex.Substring(start, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture) / 255.0;
            return c <= 0.03928 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
        }
        var lum = 0.2126 * Ch(1) + 0.7152 * Ch(3) + 0.0722 * Ch(5);
        if (lum is < 0.16 or > 0.92)
        {
            reason = "Couleur d'accent trop sombre ou trop claire : contraste insuffisant avec le texte et les surfaces.";
            return false;
        }
        return true;
    }
}
