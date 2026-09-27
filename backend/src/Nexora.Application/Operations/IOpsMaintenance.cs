using Nexora.Contracts.Operations;
using Nexora.Domain.Common;

namespace Nexora.Application.Operations;

/// <summary>
/// Registre des jobs planifiés (Phase 41 §7-9, §51). Le <see cref="Nexora.Infrastructure
/// .Scheduling.SchedulerRunner"/> consulte <see cref="IsEnabledAsync"/> avant d'exécuter chaque
/// job et journalise chaque passage via <see cref="StartRunAsync"/>/<see cref="FinishRunAsync"/> —
/// portée système (aucun <c>company_id</c> : un job couvre toutes les entreprises actives).
/// </summary>
public interface IJobRegistry
{
    Task<IReadOnlyList<JobStatusDto>> ListAsync(CancellationToken ct = default);
    Task<bool> IsEnabledAsync(string key, CancellationToken ct = default);
    Task<Guid> StartRunAsync(string key, string triggeredBy, Guid? triggeredByUser, CancellationToken ct = default);
    Task FinishRunAsync(Guid runId, bool success, int affectedCount, string? error, CancellationToken ct = default);

    /// <summary>Déclenche un job immédiatement, hors planificateur (§7, §51) — audité.</summary>
    Task<Result<JobRunDto>> RunNowAsync(string key, CancellationToken ct = default);
    Task<Result> PauseAsync(string key, CancellationToken ct = default);
    Task<Result> ResumeAsync(string key, CancellationToken ct = default);
    Task<IReadOnlyList<JobRunDto>> HistoryAsync(string key, CancellationToken ct = default);
}

/// <summary>
/// Administration des files d'attente externes — notifications (Phase 34) et webhooks sortants
/// (Phase 12) — et de leurs messages en échec définitif (Phase 41 §10-12, §51).
/// </summary>
public interface IQueueAdminService
{
    Task<IReadOnlyList<QueueStatsDto>> StatsAsync(CancellationToken ct = default);
    Task<IReadOnlyList<DeadLetterItemDto>> DeadLettersAsync(CancellationToken ct = default);

    /// <summary>Force une nouvelle tentative immédiate d'un message encore actif (pas déjà mort).</summary>
    Task<Result> RetryAsync(Guid messageId, CancellationToken ct = default);

    /// <summary>Rejeu contrôlé d'un message en échec définitif (§11) — remis en file, tentatives réinitialisées.</summary>
    Task<Result> ReplayDeadLetterAsync(Guid messageId, CancellationToken ct = default);

    /// <summary>Suppression contrôlée d'un message mort (§11) — le payload reste dans l'audit.</summary>
    Task<Result> DeleteDeadLetterAsync(Guid messageId, CancellationToken ct = default);
}

/// <summary>
/// Diagnostics d'exploitation (Phase 41 §5-6, §13-15, §28) : batterie de contrôles bornée à
/// l'entreprise active, maintenance base de données (portée système — aucune donnée d'entreprise)
/// et stockage. Aucun accès arbitraire à la base (§45) : chaque contrôle est un test prédéfini.
/// </summary>
public interface IOpsDiagnosticsService
{
    Task<IReadOnlyList<DiagnosticCheckDto>> RunAllAsync(CancellationToken ct = default);
    Task<StorageStatsDto> StorageAsync(CancellationToken ct = default);
    Task<DatabaseMaintenanceDto> DatabaseAsync(CancellationToken ct = default);
}

/// <summary>
/// Bascules de fonctionnalité (Phase 41 §27) — résolution par spécificité croissante
/// (global → entreprise → utilisateur) avec un **kill switch** : une bascule globale désactivée
/// l'emporte toujours, quel que soit un réglage plus spécifique.
/// </summary>
public interface IFeatureFlagService
{
    Task<IReadOnlyList<FeatureFlagDto>> ListAsync(CancellationToken ct = default);
    Task<Result<FeatureFlagDto>> SaveAsync(SaveFeatureFlagRequest request, CancellationToken ct = default);
    Task<Result> DeleteAsync(Guid id, CancellationToken ct = default);
    Task<bool> IsEnabledAsync(string key, Guid? companyId, Guid? userId, CancellationToken ct = default);
}

/// <summary>
/// Fenêtres de maintenance planifiée (Phase 41 §23-24) : état machine
/// <c>planned → notified → backed_up → in_progress → verifying → closed</c> (ou <c>canceled</c>).
/// Une seule fenêtre <c>in_progress</c> à la fois — c'est elle qui active le mode maintenance.
/// </summary>
public interface IMaintenanceService
{
    Task<IReadOnlyList<MaintenanceWindowDto>> ListAsync(CancellationToken ct = default);
    Task<Result<MaintenanceWindowDto>> ScheduleAsync(ScheduleMaintenanceRequest request, CancellationToken ct = default);
    Task<Result<MaintenanceWindowDto>> TransitionAsync(Guid id, TransitionMaintenanceRequest request, CancellationToken ct = default);

    /// <summary>Vrai si une fenêtre est actuellement <c>in_progress</c> (utilisé par le middleware §24).</summary>
    Task<bool> IsActiveAsync(CancellationToken ct = default);
}

/// <summary>
/// Rétention & purge de données strictement techniques (Phase 41 §16-19, §22, §46). La liste des
/// types purgeables est fermée (sessions expirées, défis MFA expirés, historique de jobs terminés,
/// notifications résolues) : **aucune donnée métier ou comptable n'est éligible** (§17). Toute
/// exécution est précédée d'un aperçu à blanc (<see cref="PreviewAsync"/>, aucune écriture).
/// </summary>
public interface IRetentionPurgeService
{
    Task<IReadOnlyList<RetentionPolicyDto>> ListPoliciesAsync(CancellationToken ct = default);
    Task<Result<RetentionPolicyDto>> SavePolicyAsync(SaveRetentionPolicyRequest request, CancellationToken ct = default);
    Task<PurgeRunDto> PreviewAsync(PurgePreviewRequest request, CancellationToken ct = default);
    Task<Result<PurgeRunDto>> ExecuteAsync(PurgePreviewRequest request, CancellationToken ct = default);
    Task<IReadOnlyList<PurgeRunDto>> HistoryAsync(CancellationToken ct = default);

    /// <summary>Purge automatique selon les politiques système dues — appelé par le planificateur (§46).</summary>
    Task<int> RunDueAsync(CancellationToken ct = default);
}

/// <summary>
/// Workflow de correction de donnée contrôlée (Phase 41 §44) : jamais d'<c>UPDATE</c> direct non
/// audité. Liste fermée de champs corrigeables ; demandeur ≠ approbateur (séparation des
/// responsabilités, comme le workflow d'approbation Phase 11) ; avant/après conservés.
/// </summary>
public interface IDataCorrectionService
{
    Task<Result<DataCorrectionDto>> RequestAsync(RequestDataCorrectionRequest request, CancellationToken ct = default);
    Task<Result<DataCorrectionDto>> DecideAsync(Guid id, DecideDataCorrectionRequest decision, CancellationToken ct = default);
    Task<IReadOnlyList<DataCorrectionDto>> ListAsync(string? status, CancellationToken ct = default);
}
