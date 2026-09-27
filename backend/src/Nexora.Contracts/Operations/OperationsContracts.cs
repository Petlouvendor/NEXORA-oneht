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
