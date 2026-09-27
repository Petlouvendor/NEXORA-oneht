using Nexora.Contracts.Operations;
using Nexora.Domain.Common;

namespace Nexora.Application.Operations;

/// <summary>
/// Exploitation & préparation à la mise en production (Phase 37 §29, §34, §38-40, §54, §56).
/// Agrège la santé des sous-systèmes en un seul tableau, calcule la **checklist Go-Live** à partir
/// des signaux déjà en place (sauvegardes vérifiées &amp; tests de restauration — Phase 36, qualité
/// des données — Phase 31, secrets de production, planificateur, RPO), enregistre les
/// **déploiements** et les **validations Go-Live**.
/// </summary>
public interface IOperationsService
{
    Task<SystemStatusDto> SystemStatusAsync(CancellationToken ct = default);

    Task<GoLiveReadinessDto> GoLiveReadinessAsync(CancellationToken ct = default);
    Task<Result<GoLiveSignOffDto>> SignOffAsync(SignOffRequest request, CancellationToken ct = default);
    Task<IReadOnlyList<GoLiveSignOffDto>> ListSignOffsAsync(CancellationToken ct = default);

    Task<IReadOnlyList<DeploymentDto>> ListDeploymentsAsync(CancellationToken ct = default);
    Task<Result<DeploymentDto>> RecordDeploymentAsync(RecordDeploymentRequest request, CancellationToken ct = default);
    Task<Result<DeploymentDto>> RollBackAsync(Guid deploymentId, RollbackRequest request, CancellationToken ct = default);
}

/// <summary>
/// Compteurs plateforme non sensibles pour un endpoint <c>/metrics</c> (format texte Prometheus,
/// Phase 37 §27). Aucune donnée d'entreprise n'est exposée — uniquement des agrégats.
/// </summary>
public interface IPlatformMetrics
{
    Task<IReadOnlyList<(string Name, string Help, double Value)>> GaugesAsync(CancellationToken ct = default);
}
