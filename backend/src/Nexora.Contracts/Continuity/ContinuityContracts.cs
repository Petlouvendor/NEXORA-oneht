namespace Nexora.Contracts.Continuity;

// =============================================================================
// Continuité, sauvegarde & reprise (Phase 36 §4-7, §18-21, §36-38, §43-46).
// =============================================================================

// --- §4-7 Politique ---------------------------------------------------

public sealed record BackupPolicyDto(
    string Name,
    IReadOnlyList<string> Kinds,
    string Frequency,
    int RetentionDays,
    string Destination,
    bool Encrypted,
    bool Immutable,
    int RpoTargetMinutes,
    int RtoTargetMinutes,
    string Status);

// --- §6, §14, §21 Sauvegardes ---------------------------------------

public sealed record BackupDto(
    Guid Id,
    string Kind,
    string Status,
    DateTimeOffset StartedAt,
    DateTimeOffset? FinishedAt,
    long? SizeBytes,
    string? Checksum,
    long? ObjectCount,
    DateTimeOffset CoversUpTo,
    string? LocationRef,
    bool Encrypted,
    DateTimeOffset? ImmutableUntil,
    DateTimeOffset? VerifiedAt,
    DateTimeOffset? ExpiresAt,
    string? Error);

/// <summary>Un agent externe enregistre une sauvegarde qu'il vient de réaliser (§6). Jamais de secret.</summary>
public sealed record RegisterBackupRequest(
    string Kind,
    DateTimeOffset CoversUpTo,
    long? SizeBytes = null,
    string? Checksum = null,
    long? ObjectCount = null,
    string? LocationRef = null,
    bool Encrypted = true,
    bool Failed = false,
    string? Error = null);

public sealed record BackupVerificationDto(bool Ok, string Detail, long? LiveObjectCount, long? BackupObjectCount);

// --- §18-21 Restaurations & tests ---------------------------------

public sealed record RestoreRunDto(
    Guid Id,
    Guid BackupId,
    string Mode,
    string? Target,
    string Status,
    bool? ChecksumOk,
    long? ObjectCountExpected,
    long? ObjectCountActual,
    DateTimeOffset StartedAt,
    DateTimeOffset? FinishedAt,
    Guid? RequestedBy,
    Guid? ApprovedBy);

public sealed record RestoreTestRequest(Guid BackupId, string? Target = null);
public sealed record RestoreRequest(Guid BackupId, string Target);

// --- §36, §46 État de reprise après sinistre --------------------

public sealed record DisasterRecoveryStatusDto(
    DateTimeOffset? LastGoodBackupAt,
    int? LastGoodBackupAgeMinutes,
    DateTimeOffset? LastVerifiedBackupAt,
    int RpoTargetMinutes,
    int RtoTargetMinutes,
    bool RpoBreached,
    bool BackupOverdue,
    int UnresolvedIncidents,
    int OpenP1Incidents,
    int BackupsAwaitingVerification,
    string Grade);   // A..E

// --- §43-45 Incidents ---------------------------------------------

public sealed record ContinuityIncidentDto(
    Guid Id,
    string Severity,
    string Category,
    string Title,
    string DetectedVia,
    string Status,
    int RunbookStep,
    string RunbookLabel,
    int? RpoActualMinutes,
    int? RtoActualMinutes,
    IReadOnlyDictionary<string, string>? PostMortem,
    DateTimeOffset OpenedAt,
    DateTimeOffset? ResolvedAt,
    /// <summary>availability (défaut, Phase 36) | security (Phase 47 §29-30) — même registre, jamais dupliqué.</summary>
    string Domain = "availability",
    /// <summary>Phase 48 §32, §55 — confinements réellement exécutés (ex. verrouillage d'urgence).</summary>
    IReadOnlyList<ContainmentActionDto>? ContainmentActions = null);

public sealed record OpenIncidentRequest(
    string Severity, string Category, string Title, string DetectedVia = "user_report", string Domain = "availability");
public sealed record AdvanceIncidentRequest(int RunbookStep, string? Status = null);
public sealed record ResolveIncidentRequest(
    int? RpoActualMinutes,
    int? RtoActualMinutes,
    IReadOnlyDictionary<string, string> PostMortem);

// =============================================================================
// Résilience, Haute Disponibilité & Disaster Recovery (Phase 48 §32, §37, §39, §44-45, §55).
// =============================================================================

/// <summary>§32, §55 — confinement réel exécuté sur un incident (ex. verrouillage d'urgence),
/// jamais seulement une intention documentée après coup dans le post-mortem.</summary>
public sealed record ContainmentActionDto(string Action, DateTimeOffset At, Guid By, string Detail);

/// <summary>§32, §55 — verrouillage d'urgence : révoque toutes les sessions et clés d'API de
/// l'entreprise touchée par l'incident. Action destructive-réversible (les utilisateurs légitimes
/// se reconnectent) — jamais automatique, toujours une décision explicite (`continuity.restore`).</summary>
public sealed record LockdownResultDto(int SessionsRevoked, int ApiKeysRevoked, int UsersAffected, DateTimeOffset At);

// --- §37 Runbooks nommés par scénario (catalogue statique, comme IntelligenceModelCatalog) -----

public sealed record RunbookStepDto(int Step, string Title, string Guidance);
public sealed record RunbookDto(string Key, string Title, string Description, IReadOnlyList<RunbookStepDto> Steps);

// --- §44-45 Exercices DR (au-delà du simple test de restauration) ------------------------------

/// <summary><c>TestType</c> ∈ tabletop|restore|failover_db|failover_app|network_outage|
/// server_outage|storage_outage|ransomware_simulation|site_unavailable|return_to_normal.
/// <c>Status</c> ∈ scheduled|passed|failed|cancelled.</summary>
public sealed record DrTestDto(
    Guid Id, string TestType, string Title, DateOnly? ScheduledFor, DateTimeOffset? ExecutedAt, string Status,
    string? Scope, IReadOnlyList<string> Participants, string? Findings, string? CorrectiveActions,
    DateOnly? NextTestDue, DateTimeOffset CreatedAt);

public sealed record ScheduleDrTestRequest(string TestType, string Title, DateOnly? ScheduledFor = null,
    string? Scope = null, IReadOnlyList<string>? Participants = null);

public sealed record RecordDrTestResultRequest(bool Passed, string? Findings = null, string? CorrectiveActions = null,
    DateOnly? NextTestDue = null);

// --- §7, §39 Page de statut — composition des signaux existants (Phase 41 diagnostics + Phase 36
// état de reprise + fenêtres de maintenance), jamais un second calcul de santé --------------------

/// <summary><c>Criticality</c> ∈ critical|high|normal|low (§7). <c>Status</c> reprend celui du
/// diagnostic Phase 41 sous-jacent (ok|warn|fail|skipped).</summary>
public sealed record StatusComponentDto(string Key, string Label, string Criticality, string Status, string? Detail);

/// <summary><c>OverallStatus</c> ∈ operational|degraded|incident|maintenance (§39).</summary>
public sealed record StatusPageDto(
    string OverallStatus, IReadOnlyList<StatusComponentDto> Components,
    IReadOnlyList<ContinuityIncidentDto> RecentIncidents, DateTimeOffset GeneratedAt);
