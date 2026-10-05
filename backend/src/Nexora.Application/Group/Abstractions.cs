using Nexora.Contracts.Group;
using Nexora.Domain.Common;

namespace Nexora.Application.Group;

/// <summary>
/// Plan comptable groupe et mappings compte local → compte groupe (Phase 9 §12).
/// Le mapping est versionné : une nouvelle version supersède l'ancienne, l'historique
/// reste consultable.
/// </summary>
public interface IGroupLedgerService
{
    Task<IReadOnlyList<GroupAccountDto>> ListAccountsAsync(Guid groupId, CancellationToken ct = default);
    Task<Result<Guid>> UpsertAccountAsync(Guid groupId, UpsertGroupAccountRequest request, CancellationToken ct = default);
    Task<Result> SetMappingAsync(Guid groupId, SetMappingRequest request, CancellationToken ct = default);
    Task<IReadOnlyList<MappingCoverageRow>> MappingCoverageAsync(Guid groupId, CancellationToken ct = default);
    Task<Result<IReadOnlyList<LocalAccountMappingDto>>> ListMappingsAsync(Guid groupId, Guid companyId, CancellationToken ct = default);
    Task<GroupOptionsDto> OptionsAsync(Guid groupId, CancellationToken ct = default);
    Task<GroupPolicyDto> GetPolicyAsync(Guid groupId, CancellationToken ct = default);
    Task<Result> SetPolicyAsync(Guid groupId, SetGroupPolicyRequest request, CancellationToken ct = default);
}

/// <summary>
/// Transactions intercompany et rapprochement (Phase 9 §14-15). Le rapprochement ne
/// modifie jamais les livres individuels : il alimente les éliminations de consolidation.
/// </summary>
public interface IIntercompanyService
{
    Task<Result<Guid>> RecordAsync(Guid groupId, RecordIntercompanyRequest request, CancellationToken ct = default);
    Task<Result> ConfirmAsync(Guid groupId, Guid transactionId, CancellationToken ct = default);
    Task<IReadOnlyList<IntercompanyTransactionDto>> ListAsync(Guid groupId, CancellationToken ct = default);
    Task<Result<IntercompanyMatchDto>> ReconcileAsync(Guid groupId, ReconcileIntercompanyRequest request, CancellationToken ct = default);
    Task<IReadOnlyList<IntercompanyTransactionDto>> UnreconciledAsync(Guid groupId, CancellationToken ct = default);
}

/// <summary>
/// Processus de consolidation (Phase 9 §17-20). Agrège des copies converties et mappées
/// des balances individuelles, applique les éliminations, contrôle la cohérence — sans
/// jamais écrire dans les comptabilités sources (§2, §16).
/// </summary>
public interface IConsolidationService
{
    Task<Result<ConsolidationRunDto>> CreateRunAsync(Guid groupId, CreateConsolidationRunRequest request, CancellationToken ct = default);
    Task<Result<ConsolidationRunDto>> ExecuteAsync(Guid groupId, Guid runId, CancellationToken ct = default);
    Task<Result<ConsolidationRunDto>> ValidateAsync(Guid groupId, Guid runId, CancellationToken ct = default);
    Task<Result<ConsolidationRunDto>> PublishAsync(Guid groupId, Guid runId, CancellationToken ct = default);
    Task<Result<ConsolidationRunDto>> ReopenAsync(Guid groupId, Guid runId, ReopenRunRequest request, CancellationToken ct = default);
    Task<Result<ConsolidationRunDto>> GetRunAsync(Guid groupId, Guid runId, CancellationToken ct = default);
    Task<IReadOnlyList<ConsolidationRunDto>> ListRunsAsync(Guid groupId, CancellationToken ct = default);
    Task<Result<ConsolidatedStatementsDto>> GetStatementsAsync(Guid groupId, Guid runId, CancellationToken ct = default);
}

/// <summary>Centre de contrôle du comptable en chef / cabinet (Phase 9 §23-24).</summary>
public interface IGroupControlCenter
{
    Task<GroupControlCenterDto> SummaryAsync(Guid groupId, CancellationToken ct = default);
}

/// <summary>Tâches de contrôle groupe (Phase 9 §25).</summary>
public interface IGroupTaskService
{
    Task<Result<Guid>> CreateAsync(Guid groupId, CreateGroupTaskRequest request, CancellationToken ct = default);
    Task<IReadOnlyList<GroupTaskDto>> ListAsync(Guid groupId, CancellationToken ct = default);
    Task<Result> UpdateAsync(Guid groupId, Guid taskId, UpdateGroupTaskRequest request, CancellationToken ct = default);
}

/// <summary>
/// Ajout / archivage d'une entreprise sans nouvelle installation (Phase 9 §28-29).
/// L'archivage désactive les nouvelles opérations et conserve tout l'historique.
/// </summary>
public interface ICompanyProvisioningService
{
    Task<Result<Guid>> ProvisionAsync(ProvisionCompanyRequest request, CancellationToken ct = default);
    Task<Result> ArchiveAsync(Guid groupId, Guid companyId, ArchiveCompanyRequest request, CancellationToken ct = default);
}
