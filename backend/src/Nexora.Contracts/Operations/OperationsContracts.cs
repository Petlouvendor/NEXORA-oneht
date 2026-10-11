namespace Nexora.Contracts.Operations;

// =============================================================================
// Qualité & préparation production (Phase 37 §29, §34, §38-40, §54, §56).
// =============================================================================

// --- §29, §56 État système ------------------------------------------

/// <summary><c>status</c> ∈ ok | degraded | down.</summary>
public sealed record ComponentStatusDto(string Component, string Status, string Detail);

public sealed record SystemStatusDto(
    string Overall,
    DateTimeOffset CheckedAt,
    IReadOnlyList<ComponentStatusDto> Components);

// --- §40, §54 Checklist Go-Live -----------------------------------

/// <summary>
/// Un point de contrôle Go-Live. <c>status</c> ∈ pass | warn | fail | manual.
/// <c>source</c> ∈ auto (calculé) | manual (à attester par un humain).
/// </summary>
public sealed record GoLiveCheckDto(
    string Key, string Label, string Category, string Status, string Source, string Detail);

public sealed record GoLiveReadinessDto(
    bool CanGoLive,
    int Passed, int Warnings, int Failed, int Manual,
    IReadOnlyList<GoLiveCheckDto> Checks,
    DateTimeOffset EvaluatedAt);

public sealed record SignOffRequest(
    string Environment,
    string Verdict,          // go | go_with_reservations | no_go
    string? Notes = null,
    bool Force = false,
    string? ForceReason = null);

public sealed record GoLiveSignOffDto(
    Guid Id,
    string Environment,
    string Verdict,
    bool CanGoLive,
    bool Forced,
    string? ForceReason,
    string? Notes,
    Guid? SignedBy,
    DateTimeOffset SignedAt,
    GoLiveReadinessDto ChecklistSnapshot);

// --- §34, §38-39, §56 Déploiements -------------------------------

public sealed record DeploymentDto(
    Guid Id,
    string Version,
    string Environment,
    string Status,
    string? SchemaVersion,
    string? Notes,
    Guid? RolledBackFrom,
    string? RollbackReason,
    DateTimeOffset StartedAt,
    DateTimeOffset? FinishedAt,
    Guid? CreatedBy);

public sealed record RecordDeploymentRequest(string Version, string Environment = "production", string? Notes = null);
public sealed record RollbackRequest(string Reason, string? ToVersion = null);

// --- Finalisation phase 18 : état de production -----------------------------

/// <summary>Contrôle de configuration de production (<c>pass</c> / <c>warn</c> / <c>fail</c>) ; jamais la valeur d'un secret.</summary>
public sealed record ProductionCheckDto(string Key, string Label, string Category, string Status, string Detail);

/// <summary>Résultat d'une sonde de santé (readiness) : <c>Healthy</c>, <c>Degraded</c>, <c>Unhealthy</c>.</summary>
public sealed record HealthProbeDto(string Name, string Status, string? Description, double DurationMs);

public sealed record MigrationStatusDto(
    int Embedded, int Applied, string? LatestApplied, string? LatestEmbedded,
    IReadOnlyList<string> Pending, IReadOnlyList<string> Drifted, IReadOnlyList<string> Unknown);

/// <summary>Vue « Production » de l'exploitation : version déployée, sondes, migrations, configuration.</summary>
public sealed record ProductionReadinessDto(
    string Environment,
    string Version,
    string? Commit,
    DateTimeOffset StartedAt,
    long UptimeSeconds,
    string Overall,
    IReadOnlyList<HealthProbeDto> Probes,
    MigrationStatusDto? Migrations,
    IReadOnlyList<ProductionCheckDto> Checks,
    DateTimeOffset GeneratedAt);
