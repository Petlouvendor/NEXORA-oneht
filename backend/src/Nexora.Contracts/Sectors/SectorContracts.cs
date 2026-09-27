namespace Nexora.Contracts.Sectors;

/// <summary>Configuration sectorielle effective d'une entreprise (Phase 8 §3-4).</summary>
public sealed record SectorConfig(
    Guid CompanyId,
    string PrimaryActivity,
    IReadOnlyList<string> Activities,
    IReadOnlyList<ModuleState> Modules,
    IReadOnlyList<TerminologyEntry> Terminology);

public sealed record ModuleState(string Key, bool Enabled);

public sealed record TerminologyEntry(string Key, string Locale, string Label);

/// <summary>Choix des activités : la première est la principale (§27).</summary>
public sealed record SetActivitiesRequest(IReadOnlyList<string> Activities);

public sealed record SetModuleRequest(string ModuleKey, bool Enabled);

public sealed record SetTerminologyRequest(string TermKey, string Label, string Locale = "fr");

// --- Champs personnalisés (§16) ------------------------------------------

public sealed record CustomFieldDto(
    Guid Id, string EntityType, string FieldKey, string Label, string DataType,
    bool IsRequired, IReadOnlyList<string> Options, string? ReferenceEntity,
    int Position, string? ViewPermission, string Status);

public sealed record DefineCustomFieldRequest(
    string EntityType, string FieldKey, string Label, string DataType,
    bool IsRequired = false, IReadOnlyList<string>? Options = null,
    string? ReferenceEntity = null, string? ViewPermission = null, int Position = 0);

public sealed record CustomFieldValueDto(
    string FieldKey, string Label, string DataType,
    string? Text, decimal? Number, DateOnly? Date, bool? Boolean, Guid? Reference);

public sealed record SetCustomFieldValuesRequest(
    string EntityType, Guid EntityId, IReadOnlyList<CustomFieldValueInput> Values);

/// <summary>Valeur brute en texte ; convertie selon le type déclaré du champ.</summary>
public sealed record CustomFieldValueInput(string FieldKey, string? Value);
