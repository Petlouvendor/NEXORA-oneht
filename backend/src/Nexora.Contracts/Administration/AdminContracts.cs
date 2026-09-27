using System.Text.Json;

namespace Nexora.Contracts.Administration;

// --- Paramètres (§2-4) ----------------------------------------------

public sealed record SettingDefinitionDto(
    string Key, string Category, string ValueType, JsonElement DefaultValue, JsonElement AllowedValues,
    string MaxScope, bool IsSensitive, bool AffectsFuture, string Description);

/// <summary>Valeur effective résolue par périmètre (§3) : establishment > company > group > system > défaut.</summary>
public sealed record EffectiveSettingDto(string Key, string Category, JsonElement Value, string Source);

public sealed record SetSettingRequest(string Key, string Scope, Guid? ScopeId, JsonElement Value, string? Note);

public sealed record SetSettingResult(EffectiveSettingDto Effective, IReadOnlyList<string> Warnings);

// --- Rôles personnalisés (§10-11) ---------------------------------

public sealed record RoleDto(
    Guid Id, string Name, string? Description, bool SystemRole, string Status,
    int AssignmentCount, IReadOnlyList<string> Permissions);

public sealed record CreateRoleRequest(
    string Name, string? Description, Guid? BaseRoleId, IReadOnlyList<string> Permissions);

public sealed record SetRolePermissionsRequest(IReadOnlyList<string> Permissions);

// --- Workflows configurables (§14-16) ----------------------------

public sealed record WorkflowConditionDto(string Field, string Op, JsonElement Value);

public sealed record WorkflowApprovalDto(int Levels, string? ApproverRoleCode);

public sealed record WorkflowDefinitionDto(
    string Logic, IReadOnlyList<WorkflowConditionDto> Conditions, WorkflowApprovalDto? Approval,
    IReadOnlyList<string> Actions, IReadOnlyList<string> Notifications);

public sealed record WorkflowDto(
    Guid Id, string Code, string Name, string TriggerEvent, WorkflowDefinitionDto Definition,
    string Status, bool IsTested, DateTimeOffset? LastTestedAt);

public sealed record UpsertWorkflowRequest(string Code, string Name, string TriggerEvent, WorkflowDefinitionDto Definition);

public sealed record WorkflowTestRequest(JsonElement Context);

public sealed record ConditionEvalDto(string Field, string Op, JsonElement Expected, JsonElement? Actual, bool Passed);

public sealed record WorkflowTestResult(
    bool Matched, string Logic, IReadOnlyList<ConditionEvalDto> Conditions,
    IReadOnlyList<string> PlannedApprovals, IReadOnlyList<string> PlannedActions, IReadOnlyList<string> PlannedNotifications);

public sealed record WorkflowTriggerRequest(string TriggerEvent, JsonElement Context, string? ReferenceType, Guid? ReferenceId);

public sealed record WorkflowEvaluationResult(bool Matched, Guid? WorkflowId, Guid? ApprovalRequestId, string Message);

// --- Automatisations & planificateur (§17-18) -------------------

public sealed record AutomationDto(
    Guid Id, string Code, string Name, string Kind, JsonElement Schedule, JsonElement Parameters,
    string Status, DateTimeOffset? LastRunAt, DateTimeOffset? NextRunAt);

public sealed record UpsertAutomationRequest(string Code, string Name, string Kind, JsonElement Schedule, JsonElement Parameters);

public sealed record AutomationRunDto(
    Guid Id, Guid AutomationId, string Mode, string Status, int ItemsProcessed, JsonElement Result,
    string? Error, DateTimeOffset StartedAt, DateTimeOffset? FinishedAt);

// --- Relances clients (§19) -----------------------------------

public sealed record DunningLevelDto(int Level, int OffsetDays, string Title, string Message);

public sealed record DunningPolicyDto(Guid? Id, string Name, string Channel, IReadOnlyList<DunningLevelDto> Levels, string Status);

// --- Versionnement de configuration (§39) --------------------

public sealed record ConfigVersionDto(
    Guid Id, string Domain, int VersionNumber, string Status, DateTimeOffset? EffectiveFrom, DateTimeOffset? EffectiveTo,
    JsonElement Snapshot, JsonElement Diff, string? Note, DateTimeOffset CreatedAt);

public sealed record ActivateVersionRequest(DateTimeOffset? EffectiveFrom, string? Note);

// --- Centre de tâches administratives (§44) ------------------

public sealed record AdminTaskDto(string Key, string Category, string Severity, string Title, string Detail, int Count);
