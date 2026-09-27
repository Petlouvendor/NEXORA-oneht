using System.Text.Json;
using Nexora.Contracts.Reporting;

namespace Nexora.Contracts.Intelligence;

// =============================================================================
// Intelligence métier & recommandations (Phase 43 §2, §4-5, §31, §33-39, §45-46, §48).
// Complète BiContracts.cs (Phase 42) : une recommandation part d'un insight/forecast/budget
// existant et propose une action, jamais un fait (§2) — toujours avec pourquoi/données/
// impact estimé/confiance expliquée (§4-5).
// =============================================================================

// --- §2, §4-5, §12-13, §18, §20-21, §31, §33, §46 Recommandations -----------

/// <summary><c>category</c> ∈ stock_reorder|stock_dormant|receivable_priority|cashflow_risk|budget_overrun|
/// sales_spike|sales_decline|customer_inactive|supplier_price_increase (Phase 54 §9). <c>confidenceLevel</c>
/// ∈ high|medium|low|insufficient_data (§4). <c>status</c> ∈ new|accepted|dismissed|snoozed|resolved|
/// modified|expired. <c>priorityScore</c> = impact × urgence × confiance (§46). <c>priorityLevel</c>
/// (Phase 54 §7) ∈ critical|important|watch|opportunity — colonne générée, dérivée de <c>nature</c>
/// (risk|opportunity) et de <c>priorityScore</c>, jamais recalculée côté client. <c>outcome</c> ∈
/// positive|negative — boucle d'apprentissage (§33), renseigné par <see cref="ResolveRecommendationRequest"/>.</summary>
public sealed record RecommendationDto(
    Guid Id, Guid? EstablishmentId, string Category, string RuleKey, string Title,
    string Why, JsonElement Evidence, JsonElement ImpactEstimate, string ProposedAction, string ExpectedResult,
    int ImpactScore, int UrgencyScore, int ConfidenceScore, string ConfidenceLevel, string ConfidenceReason,
    int PriorityScore, string PriorityLevel, string Nature, string? DrillPath, DateOnly PeriodStart, string Status,
    DateTimeOffset? SnoozeUntil, string? Outcome, string? OutcomeNote, DateTimeOffset? ExpiresAt,
    JsonElement? OverrideValue, DateTimeOffset CreatedAt, DateTimeOffset? ViewedAt = null);

/// <summary>§31 « créer tâche » regroupée dans l'acceptation plutôt qu'un point d'entrée séparé —
/// la quantité/action proposée reste une proposition (§12), jamais exécutée seule.</summary>
public sealed record AcceptRecommendationRequest(bool CreateTask = false, Guid? AssignTo = null, string? Note = null);
public sealed record DismissRecommendationRequest(string? Reason = null);
public sealed record SnoozeRecommendationRequest(DateTimeOffset Until, string? Reason = null);

/// <summary><c>Outcome</c> ∈ positive|negative (§33).</summary>
public sealed record ResolveRecommendationRequest(string Outcome, string? Note = null);

/// <summary>
/// §16, §19 « modifier » — ajuste une valeur proposée (ex. quantité suggérée) avant validation.
/// <c>OverrideValue</c> est journalé à côté de <c>impact_estimate</c> d'origine, jamais à sa place
/// (Phase 54 §18 feedback : valeur initiale + valeur modifiée + motif).
/// </summary>
public sealed record ModifyRecommendationRequest(JsonElement OverrideValue, string? Note = null);

/// <summary>
/// Vue générale (Phase 54 §5, §20) — compte les recommandations ouvertes par niveau de priorité et
/// met en avant les actions prioritaires et les opportunités. Agrège <see cref="RecommendationDto"/>
/// existants, jamais un second calcul (anomalies ouvertes déléguées à <c>IInsightService</c>, déjà
/// exposé sous <c>/intelligence/insights</c>).
/// </summary>
public sealed record IntelligenceDashboardDto(
    int CriticalCount, int ImportantCount, int WatchCount, int OpportunityCount, int OpenAnomalies,
    IReadOnlyList<RecommendationDto> TopActions, IReadOnlyList<RecommendationDto> TopOpportunities,
    DateTimeOffset GeneratedAt);

// --- §34, §39 Règles configurables -------------------------------------------

/// <summary><c>IsSystemDefault</c> = valeur plateforme (company_id NULL, comme sector_profile) —
/// jamais modifiable via l'API, seulement surchargée par une règle propre à l'entreprise.</summary>
public sealed record IntelligenceRuleDto(
    Guid Id, bool IsSystemDefault, string Key, string Category, string Label, JsonElement Threshold,
    int? PeriodDays, string DefaultPriority, string? RecipientRole, string ActionLabel,
    int? EscalateAfterDays, bool Enabled, DateOnly ValidFrom, DateOnly? ExpiresAt,
    int Version, DateTimeOffset? ApprovedAt);

/// <summary>Upsert par clé (§34) — l'identité d'une règle est <c>(entreprise, key)</c>, pas un id
/// arbitraire ; une entreprise ne fait que surcharger sa propre clé, jamais une valeur système.</summary>
public sealed record SaveIntelligenceRuleRequest(
    string Key, string Category, string Label, JsonElement Threshold, int? PeriodDays,
    string DefaultPriority, string? RecipientRole, string ActionLabel, int? EscalateAfterDays,
    bool Enabled, DateOnly? ExpiresAt);

// --- §35 Scoring --------------------------------------------------------------

/// <summary><c>entityType</c> ∈ product|customer|supplier. <c>level</c> ∈ low|medium|high.
/// <c>explanation</c> porte les variables utilisées — jamais une boîte noire (§35).</summary>
public sealed record EntityScoreDto(
    Guid EntityId, string EntityLabel, string EntityType, string ScoreKey, decimal Value,
    string Level, JsonElement Explanation, DateTimeOffset ComputedAt);

// --- §39 Gouvernance — catalogue descriptif (lecture seule) -------------------

/// <summary>Documente un générateur (règle ou score) déjà branché — pas une nouvelle donnée,
/// juste la traçabilité propriétaire/version exigée par la gouvernance (§39).</summary>
public sealed record IntelligenceModelDto(
    string Key, string Kind, string Category, string Name, string Description,
    string Owner, int Version, IReadOnlyList<string> Inputs);

// --- §38 Observabilité --------------------------------------------------------

public sealed record IntelligenceAuditEntryDto(
    Guid Id, string TableName, string Action, JsonElement? Changes, Guid? UserId, DateTimeOffset OccurredAt);

// --- §6-10, §28-30 Assistant conversationnel & briefings par rôle -----------

/// <summary><c>role</c> ∈ direction|manager|comptable|finance|caissier (§6-10). <c>Dashboard</c> réutilise
/// le tableau de bord existant du rôle (Phase 7 <c>IKpiService</c>) — sérialisé tel quel, jamais
/// redérivé. Caissier exige une session (<c>sessionId</c>) ; sans session ouverte, <c>Dashboard</c>
/// est vide et <c>Headline</c> l'indique.</summary>
public sealed record AssistantBriefingDto(
    string Role, DateOnly AsOf, string Headline, JsonElement Dashboard,
    IReadOnlyList<InsightDto> Insights, IReadOnlyList<RecommendationDto> Recommendations);

/// <summary>Journal d'observabilité (§38) d'une question posée à l'assistant (§28-30) — la réponse
/// elle-même reste celle d'<see cref="Reporting.IAskNexora"/> (Phase 7), jamais redupliquée.</summary>
public sealed record AssistantQueryLogDto(
    Guid Id, string Question, string Intent, string Answer, IReadOnlyList<string> Sources,
    bool ContainsForecast, DateTimeOffset CreatedAt);

// --- §31 Tâche générique (comble un manque réel — aucune table libre n'existait) --------

/// <summary><c>priority</c> ∈ low|normal|high|urgent. <c>status</c> ∈ open|in_progress|done|cancelled.</summary>
public sealed record TaskDto(
    Guid Id, string Title, string Description, string? ReferenceType, Guid? ReferenceId,
    Guid? AssignedTo, string Priority, string Status, DateTimeOffset? DueAt, DateTimeOffset CreatedAt);

public sealed record CreateTaskRequest(
    string Title, string? Description, string? ReferenceType, Guid? ReferenceId,
    Guid? AssignedTo, string Priority = "normal", DateTimeOffset? DueAt = null);
