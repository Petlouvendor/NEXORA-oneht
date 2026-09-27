using Nexora.Contracts.Migration;
using Nexora.Domain.Common;

namespace Nexora.Application.Migration;

// =============================================================================
// Migration, importation & onboarding avancé (Phase 39 §5, §45-46).
// =============================================================================

/// <summary>Événements du pipeline d'import (§46) — journalisés dans <c>import_event</c>.</summary>
public static class MigrationEvents
{
    public const string ImportCreated = "ImportCreated";
    public const string ImportProfiled = "ImportProfiled";
    public const string ImportMapped = "ImportMapped";
    public const string ImportValidated = "ImportValidated";
    public const string ImportSimulationCompleted = "ImportSimulationCompleted";
    public const string ImportStarted = "ImportStarted";
    public const string ImportProgressed = "ImportProgressed";
    public const string ImportPaused = "ImportPaused";
    public const string ImportCompleted = "ImportCompleted";
    public const string ImportFailed = "ImportFailed";
    public const string ImportCancelled = "ImportCancelled";
    public const string MigrationReconciled = "MigrationReconciled";
    public const string OnboardingStepCompleted = "OnboardingStepCompleted";
}

/// <summary>
/// Pipeline d'import (§2, §5). Un job passe par
/// <c>created → profiled → mapped → validated → simulated → executing → completed</c>
/// (ou <c>paused / failed / canceled</c>). Rien n'est écrit en production avant <c>execute</c> ;
/// <c>simulate</c> n'a **aucun** impact (§23). Le rollback est possible tant que le job n'est pas finalisé (§37).
/// </summary>
public interface IImportService
{
    Task<IReadOnlyList<ImportJobDto>> ListAsync(CancellationToken ct = default);
    Task<Result<ImportJobDto>> GetAsync(Guid id, CancellationToken ct = default);

    Task<Result<ImportJobDto>> CreateAsync(CreateImportRequest request, CancellationToken ct = default);
    Task<Result<ImportJobDto>> ProfileAsync(Guid id, CancellationToken ct = default);
    Task<Result<ImportJobDto>> SetMappingAsync(Guid id, SetMappingRequest request, CancellationToken ct = default);
    Task<Result<ImportJobDto>> ValidateAsync(Guid id, CancellationToken ct = default);
    Task<Result<ImportJobDto>> SimulateAsync(Guid id, CancellationToken ct = default);
    Task<Result<ImportJobDto>> ExecuteAsync(Guid id, CancellationToken ct = default);
    Task<Result<ImportJobDto>> PauseAsync(Guid id, CancellationToken ct = default);
    Task<Result<ImportJobDto>> ResumeAsync(Guid id, CancellationToken ct = default);
    Task<Result<ImportJobDto>> CancelAsync(Guid id, CancellationToken ct = default);

    Task<IReadOnlyList<ImportRowDto>> ListRowsAsync(Guid id, string? status, CancellationToken ct = default);
    Task<Result<ImportRowDto>> CorrectRowAsync(Guid id, CorrectRowRequest request, CancellationToken ct = default);
    Task<Result<ReconciliationDto>> ReconcileAsync(Guid id, CancellationToken ct = default);
    Task<IReadOnlyList<ImportEventDto>> ListEventsAsync(Guid id, CancellationToken ct = default);

    /// <summary>Champs NEXORA cibles pour un type d'import + mapping automatique suggéré depuis des colonnes source (§7, §33).</summary>
    Task<IReadOnlyList<TargetFieldDto>> TargetFieldsAsync(string kind, CancellationToken ct = default);
    Task<IReadOnlyList<ColumnMappingDto>> SuggestMappingAsync(string kind, IReadOnlyList<string> columns, CancellationToken ct = default);
    Task<IReadOnlyList<MappingTemplateDto>> ListTemplatesAsync(string? kind, CancellationToken ct = default);

    /// <summary>Reprend les jobs restés en <c>executing</c> (planificateur §27, §40 asynchrone).</summary>
    Task<int> ResumeStalledAsync(CancellationToken ct = default);
}

/// <summary>Checklist d'onboarding + score de migration (§31-34). La plupart des items sont dérivés en direct.</summary>
public interface IOnboardingService
{
    Task<OnboardingChecklistDto> GetChecklistAsync(CancellationToken ct = default);
    Task<Result<OnboardingChecklistDto>> CompleteTaskAsync(CompleteOnboardingTaskRequest request, CancellationToken ct = default);
}

// --- Contrat interne des importeurs par type d'entité (§9, §11-14, §25, §28-29) -----

/// <summary>Contexte partagé d'une exécution d'import.</summary>
public sealed record ImportContext(Guid CompanyId, Guid ImportJobId, Guid? UserId, string Mode, bool DryRun);

/// <summary>Résultat du traitement d'une ligne : statut, correspondance, messages, entité cible.</summary>
public sealed record RowProcessResult(
    string Status,          // valid | warning | error | created | updated | skipped | rejected
    string MatchType,       // exact | probable | none
    string? DedupKey,
    Guid? TargetId,
    string? RecordEntityType,
    string? RecordAction,   // created | updated
    IReadOnlyList<RowMessageDto> Messages);

/// <summary>
/// Importeur d'un type d'entité. <c>ValidateAsync</c> ne touche pas la base ;
/// <c>ProcessAsync</c> applique (ou simule si <c>ctx.DryRun</c>).
/// </summary>
public interface IEntityImporter
{
    string Kind { get; }
    IReadOnlyList<TargetFieldDto> TargetFields { get; }

    Task<RowProcessResult> ValidateAsync(IReadOnlyDictionary<string, string?> mapped, ImportContext ctx, CancellationToken ct);
    Task<RowProcessResult> ProcessAsync(IReadOnlyDictionary<string, string?> mapped, ImportContext ctx, CancellationToken ct);
}
