namespace Nexora.Contracts.Experience;

// NEXORA ONE — expérience intelligente (docs/nexora-one-experience-intelligente.md §5-6).

/// <summary>Un résultat de la recherche globale (Ctrl+K, §5). <c>Route</c> est l'écran frontend qui
/// ouvre l'objet ; <c>Kind</c> ∈ product|customer|supplier|employee|document|sale.</summary>
public sealed record SearchHitDto(string Kind, Guid Id, string Title, string? Subtitle, string Route);

/// <summary>Résultats d'un type d'objet. Un groupe n'apparaît que si l'utilisateur a la permission
/// de consulter ce type d'objet — la recherche ne révèle jamais l'existence d'une donnée interdite.</summary>
public sealed record SearchGroupDto(string Kind, string Label, IReadOnlyList<SearchHitDto> Hits);

public sealed record GlobalSearchResponse(string Query, IReadOnlyList<SearchGroupDto> Groups);

/// <summary>Une opération importante du fil d'activité (§6 « ACTIVITY »). <c>Tone</c> ∈
/// success|info|warning|error (toujours doublé d'un libellé, jamais la couleur seule).</summary>
public sealed record ActivityEventDto(
    string Kind, string Title, string Detail, decimal? Amount, string? Currency,
    DateTimeOffset OccurredAt, string? Route, string Tone);
