using Nexora.Contracts.Administration;
using Nexora.Domain.Common;

namespace Nexora.Application.Administration;

/// <summary>
/// Paramètres résolus par périmètre (Phase 14 §2-3). Configuration sans code : chaque clé a une
/// définition (type, périmètre maximal, sensibilité). La valeur effective suit la hiérarchie
/// establishment > company > group > system > défaut ; un niveau ne pose que ce qui lui appartient.
/// </summary>
public interface ISettingsService
{
    Task<IReadOnlyList<SettingDefinitionDto>> DefinitionsAsync(string? category, CancellationToken ct = default);
    Task<IReadOnlyList<EffectiveSettingDto>> EffectiveAsync(Guid? establishmentId, CancellationToken ct = default);
    Task<EffectiveSettingDto> ResolveAsync(string key, Guid? establishmentId = null, CancellationToken ct = default);
    Task<Result<SetSettingResult>> SetAsync(SetSettingRequest request, CancellationToken ct = default);
}

/// <summary>Rôles personnalisés d'entreprise (§10-11). Réutilise role / role_permission.</summary>
public interface ICustomRoleService
{
    Task<IReadOnlyList<RoleDto>> ListAsync(CancellationToken ct = default);
    Task<Result<Guid>> CreateAsync(CreateRoleRequest request, CancellationToken ct = default);
    Task<Result> SetPermissionsAsync(Guid roleId, SetRolePermissionsRequest request, CancellationToken ct = default);
    Task<Result<RoleDto>> PreviewAsync(Guid roleId, CancellationToken ct = default);
    Task<Result> DisableAsync(Guid roleId, bool force, CancellationToken ct = default);
}

/// <summary>
/// Workflows d'approbation configurables + moteur de règles (§14-16). Une règle est
/// <b>testable avant activation</b> (dry-run sans effet, montrant les conditions évaluées et les
/// effets prévus) ; l'activation exige un test réussi. En production, une correspondance crée une
/// demande d'approbation via le mécanisme de la Phase 11.
/// </summary>
public interface IWorkflowService
{
    Task<IReadOnlyList<WorkflowDto>> ListAsync(CancellationToken ct = default);
    Task<Result<Guid>> UpsertAsync(UpsertWorkflowRequest request, CancellationToken ct = default);
    Task<Result<WorkflowTestResult>> TestAsync(Guid workflowId, WorkflowTestRequest request, CancellationToken ct = default);
    Task<Result> ActivateAsync(Guid workflowId, CancellationToken ct = default);
    Task<Result> SetStatusAsync(Guid workflowId, string status, CancellationToken ct = default);
    Task<Result<WorkflowEvaluationResult>> EvaluateAsync(WorkflowTriggerRequest request, CancellationToken ct = default);
}

/// <summary>
/// Automatisations planifiées (§17-18) : relances, alertes stock, rapports. Les exécuteurs sont
/// explicables et les opérations financières critiques ne sont jamais exécutées sans règle (§46).
/// </summary>
public interface IAutomationService
{
    Task<IReadOnlyList<AutomationDto>> ListAsync(CancellationToken ct = default);
    Task<Result<Guid>> UpsertAsync(UpsertAutomationRequest request, CancellationToken ct = default);
    Task<Result> SetStatusAsync(Guid automationId, string status, CancellationToken ct = default);
    Task<Result<AutomationRunDto>> RunNowAsync(Guid automationId, bool preview, CancellationToken ct = default);
    Task<int> RunDueAsync(CancellationToken ct = default);
    Task<IReadOnlyList<AutomationRunDto>> RunsAsync(Guid automationId, CancellationToken ct = default);

    Task<DunningPolicyDto> GetDunningPolicyAsync(CancellationToken ct = default);
    Task<Result<DunningPolicyDto>> SetDunningPolicyAsync(DunningPolicyDto policy, CancellationToken ct = default);
}

/// <summary>
/// Versionnement des configurations sensibles (§39). Un instantané daté permet de savoir quelle
/// règle était active au moment d'une transaction — une nouvelle version ne réécrit jamais
/// l'historique.
/// </summary>
public interface IConfigVersionService
{
    Task<IReadOnlyList<ConfigVersionDto>> ListAsync(string domain, CancellationToken ct = default);
    Task<Result<ConfigVersionDto>> SnapshotAsync(string domain, string? note, CancellationToken ct = default);
    Task<Result<ConfigVersionDto>> ActivateAsync(Guid versionId, ActivateVersionRequest request, CancellationToken ct = default);
    Task<ConfigVersionDto?> ActiveAtAsync(string domain, DateTimeOffset at, CancellationToken ct = default);
}

/// <summary>Centre de tâches administratives : lacunes de configuration calculées en direct (§44).</summary>
public interface IAdminTaskService
{
    Task<IReadOnlyList<AdminTaskDto>> ListAsync(CancellationToken ct = default);
}
