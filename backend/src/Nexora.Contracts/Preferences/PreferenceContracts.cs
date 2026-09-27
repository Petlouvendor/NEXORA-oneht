using System.Text.Json;

namespace Nexora.Contracts.Preferences;

// --- Préférences utilisateur (Phase 13 §36) ---------------------------

public sealed record UserPreferenceDto(
    string Theme,            // system | light | dark
    string Density,          // comfortable | compact
    string Locale,
    bool ReducedMotion,
    string DashboardMode,    // summary | detailed
    IReadOnlyList<string> Favorites,
    JsonElement TableColumns,
    JsonElement NotificationPrefs,
    bool HighContrast = false,        // §31-33 WCAG
    decimal TextScale = 1.00m,        // 0.85 – 1.50 (§31 taille texte)
    string Timezone = "America/Port-au-Prince");   // §34

public sealed record UpdateUserPreferenceRequest(
    string? Theme,
    string? Density,
    string? Locale,
    bool? ReducedMotion,
    string? DashboardMode,
    IReadOnlyList<string>? Favorites,
    JsonElement? TableColumns,
    JsonElement? NotificationPrefs,
    bool? HighContrast = null,
    decimal? TextScale = null,
    string? Timezone = null);

// --- Personnalisation entreprise (Phase 13 §35) ----------------------

public sealed record CompanyBrandingDto(
    Guid CompanyId,
    string? DisplayName,
    string? AccentColor,     // #RRGGBB ou null (accent NEXORA par défaut)
    string? LogoUrl,
    string ReceiptFormat,    // standard | compact | detailed
    string? DocumentFooter,
    string DateFormat,
    string NumberLocale,
    string DefaultLocale);

public sealed record UpdateCompanyBrandingRequest(
    string? DisplayName,
    string? AccentColor,
    string? LogoUrl,
    string? ReceiptFormat,
    string? DocumentFooter,
    string? DateFormat,
    string? NumberLocale,
    string? DefaultLocale);
