using System.Text.Json;

namespace Nexora.Contracts.Preferences;

// =============================================================================
// UX, design system & accessibilité — personnalisation persistée (Phase 40 §13,
// §21-22, §37, §50, §57).
// =============================================================================

// --- §13, §37 Dispositions de tableau de bord --------------------------

public sealed record DashboardLayoutDto(
    Guid Id, Guid CompanyId, Guid? UserId, string? Role, string Name,
    JsonElement Widgets, bool IsDefault, DateTimeOffset UpdatedAt);

public sealed record SaveDashboardLayoutRequest(
    Guid? Id, string Name, JsonElement Widgets, bool IsDefault = true,
    string? Role = null, bool AsCompanyDefault = false);

// --- §21-22, §44 Filtres sauvegardés ---------------------------------

public sealed record SavedFilterDto(
    Guid Id, Guid CompanyId, Guid UserId, string ViewKey, string Name,
    JsonElement Filter, bool IsDefault, bool Shared, bool Mine, DateTimeOffset UpdatedAt);

public sealed record SaveFilterRequest(
    Guid? Id, string ViewKey, string Name, JsonElement Filter, bool IsDefault = false, bool Shared = false);

// --- §11, §20, §37-38 Raccourcis ------------------------------------

public sealed record ShortcutDto(Guid Id, string Chord, string Action, string Label, int SortOrder);

public sealed record SaveShortcutRequest(Guid? Id, string Chord, string Action, string Label, int SortOrder = 0);

// --- §50, §57 Version du design system ---------------------------

public sealed record DesignTokenGroupDto(string Group, int Count, IReadOnlyList<string> Sample);

public sealed record DesignSystemVersionDto(
    string Version,
    string WcagTarget,
    int ComponentCount,
    IReadOnlyList<string> Themes,
    IReadOnlyList<string> Densities,
    IReadOnlyList<DesignTokenGroupDto> TokenGroups,
    IReadOnlyList<string> Components,
    DateOnly ReleasedOn);
