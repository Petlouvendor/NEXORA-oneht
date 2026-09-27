namespace Nexora.Contracts.Configuration;

// =============================================================================
// Configuration & adaptabilité (Phase 32 §6, §44-48, §52). Profils sectoriels,
// transfert de configuration (export / import / clonage), garde-fous.
// =============================================================================

// --- §6, §49 Profils sectoriels -----------------------------------------

public sealed record ProfileModuleSpec(string Key, bool Enabled = true);
public sealed record ProfileFieldSpec(string EntityType, string FieldKey, string Label, string DataType, IReadOnlyList<string>? Options = null);
public sealed record ProfileTermSpec(string TermKey, string Locale, string Label);
public sealed record ProfileSettingSpec(string Key, System.Text.Json.JsonElement Value);
public sealed record ProfileNumberingSpec(string DocumentType, string Prefix);

public sealed record SectorProfileDefinitionDto(
    IReadOnlyList<ProfileModuleSpec> Modules,
    IReadOnlyList<ProfileFieldSpec> CustomFields,
    IReadOnlyList<ProfileTermSpec> Terminology,
    IReadOnlyList<ProfileSettingSpec> Settings,
    IReadOnlyList<ProfileNumberingSpec> Numbering);

public sealed record SectorProfileDto(
    Guid Id, string Key, string Name, string SectorType, int Version, string Status,
    bool IsSystem, string? Description, bool IsOwn);

public sealed record SectorProfileDetailDto(SectorProfileDto Header, SectorProfileDefinitionDto Definition);

/// <summary>Ce qu'appliquerait un profil : chaque élément est neuf ou déjà présent (§50-51).</summary>
public sealed record ProfilePreviewItemDto(string Kind, string Key, string Label, bool AlreadyPresent);

public sealed record ProfilePreviewDto(
    string ProfileKey, string ProfileName,
    IReadOnlyList<ProfilePreviewItemDto> Items,
    IReadOnlyList<string> Guardrails);

public sealed record ApplyProfileRequest(bool DryRun = false);

public sealed record ApplyProfileResultDto(
    string ProfileKey, bool DryRun, Guid? ConfigVersionId,
    int ModulesApplied, int FieldsCreated, int TerminologySet, int SettingsSet,
    IReadOnlyList<string> Warnings);

// --- §46-48 Transfert de configuration -------------------------------

/// <summary>Bundle de configuration exporté (§47) — jamais de secrets, soldes ni historique (§48).</summary>
public sealed record ConfigBundleDto(
    string SchemaVersion, Guid SourceCompanyId, DateTimeOffset ExportedAt,
    IReadOnlyList<ProfileModuleSpec> Modules,
    IReadOnlyList<ProfileFieldSpec> CustomFields,
    IReadOnlyList<ProfileTermSpec> Terminology,
    IReadOnlyList<ExportedSettingDto> Settings,
    IReadOnlyList<ProfileNumberingSpec> Numbering,
    IReadOnlyList<ExportedRoleDto> CustomRoles);

public sealed record ExportedSettingDto(string Key, string Scope, System.Text.Json.JsonElement Value);
public sealed record ExportedRoleDto(string Name, string? Description, IReadOnlyList<string> Permissions);

public sealed record CloneConfigRequest(Guid TargetCompanyId);
public sealed record ImportConfigRequest(ConfigBundleDto Bundle, bool DryRun = false);

public sealed record ConfigTransferDto(
    Guid Id, string Kind, Guid? SourceCompanyId, string? SourceProfileKey, bool DryRun,
    Guid? ConfigVersionId, System.Text.Json.JsonElement Summary, DateTimeOffset CreatedAt);

// --- §52 Garde-fous --------------------------------------------------

/// <summary><c>status</c> ∈ pass | warn | fail.</summary>
public sealed record GuardrailDto(string Key, string Label, string Status, string? Detail);

public sealed record GuardrailReportDto(
    bool CanPublish, int Passed, int Failed, int Warnings,
    IReadOnlyList<GuardrailDto> Checks, DateTimeOffset GeneratedAt);
