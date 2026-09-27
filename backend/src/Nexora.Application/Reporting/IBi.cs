using Nexora.Contracts.Reporting;
using Nexora.Domain.Common;

namespace Nexora.Application.Reporting;

/// <summary>
/// Catalogue KPI gouverné (Phase 42 §48, §50) — définition unique, source officielle, formule
/// documentée, propriétaire, version. Table plateforme (comme <c>job_definition</c>) : amorcée
/// depuis <see cref="KpiCatalog"/> ; lecture seule côté API dans cette phase (toute évolution
/// future passe par une migration + <c>audit_log</c>).
/// </summary>
public interface IKpiGovernanceService
{
    Task<IReadOnlyList<KpiDefinitionDto>> ListAsync(CancellationToken ct = default);
    Task<Result<KpiDefinitionDto>> GetAsync(string key, CancellationToken ct = default);
}

/// <summary>
/// Objectifs & score de performance (Phase 42 §39-40) — active <c>kpi_target</c> (V026, jusque-là
/// sans service). Le score pondère l'écart de chaque KPI cible par son poids (§40).
/// </summary>
public interface IObjectiveService
{
    Task<IReadOnlyList<ObjectiveDto>> ListAsync(CancellationToken ct = default);
    Task<Result<ObjectiveDto>> SetAsync(SetObjectiveRequest request, CancellationToken ct = default);
    Task<Result> DeleteAsync(Guid id, CancellationToken ct = default);
    Task<ObjectiveScoreDto> ScoreAsync(CancellationToken ct = default);
}

/// <summary>
/// Rapports planifiés (Phase 42 §41) — active <c>report_schedule</c>/<c>report_execution</c>
/// (V026, jusque-là sans service). La livraison passe par <see cref="Notifications.INotificationService"/>
/// (canal e-mail/SMS = stub Phase 34, notification interne toujours créée).
/// </summary>
public interface IReportScheduleService
{
    Task<IReadOnlyList<ReportScheduleDto>> ListAsync(CancellationToken ct = default);
    Task<Result<ReportScheduleDto>> SaveAsync(SaveReportScheduleRequest request, CancellationToken ct = default);
    Task<Result> DeleteAsync(Guid id, CancellationToken ct = default);
    Task<IReadOnlyList<ReportExecutionDto>> HistoryAsync(CancellationToken ct = default);

    /// <summary>Exécute les rapports planifiés dus — appelé par le planificateur (job <c>reports.deliver_scheduled</c>).</summary>
    Task<int> RunDueAsync(CancellationToken ct = default);
}

/// <summary>
/// Catalogue de rapports & rapports personnalisés (Phase 42 §48-49) — documente les rapports
/// standards déjà livrés (Phases 7/9/28/29) et active <c>saved_report</c> (V026) pour les
/// rapports enregistrés (privés ou partagés, comme <c>saved_filter</c> Phase 40). <see cref="RunAsync"/>
/// ne réexécute que les rapports fondés sur <see cref="Contracts.Reporting.ReportFilter"/> — les
/// autres restent accessibles via leur point d'entrée dédié (documenté dans le catalogue).
/// </summary>
public interface IReportCatalogService
{
    IReadOnlyList<ReportCatalogEntryDto> StandardReports();
    Task<IReadOnlyList<SavedReportDto>> ListSavedAsync(CancellationToken ct = default);
    Task<Result<SavedReportDto>> SaveAsync(SaveSavedReportRequest request, CancellationToken ct = default);
    Task<Result> DeleteSavedAsync(Guid id, CancellationToken ct = default);
    Task<Result<ReportRunResultDto>> RunAsync(Guid savedReportId, CancellationToken ct = default);

    /// <summary>Dispatecher partagé — exécute un rapport « générique » (fondé sur <see cref="Contracts
    /// .Reporting.ReportFilter"/>) directement par clé + filtre brut, réutilisé par <see cref="RunAsync"/>
    /// et par <see cref="IReportScheduleService.RunDueAsync"/> pour éviter deux aiguillages divergents.</summary>
    Task<Result<System.Text.Json.JsonElement>> RunByKeyAsync(string reportKey, string filterJson, CancellationToken ct = default);
}

/// <summary>
/// Prévisions (Phase 42 §35-36) — projection simple et transparente (moyenne mobile / tendance
/// linéaire), toujours présentée comme une <b>estimation</b> avec hypothèses et niveau de
/// confiance — jamais une certitude (§35). Le scénario applique un ajustement en pourcentage
/// (optimiste/prudent/personnalisé) au cas de base.
/// </summary>
public interface IForecastService
{
    Task<IReadOnlyList<ForecastDto>> ListAsync(string? metric, CancellationToken ct = default);
    Task<Result<ForecastDto>> GenerateAsync(GenerateForecastRequest request, CancellationToken ct = default);
}

/// <summary>
/// Insights — anomalies détectées, expliquées et actionnables (Phase 42 §37-38, §58). Chaque
/// insight montre les données qui le justifient (<c>evidence</c>), peut être ignoré ou transformé
/// en tâche assignée — jamais d'action financière critique automatique (§38).
/// </summary>
public interface IInsightService
{
    Task<IReadOnlyList<InsightDto>> ListAsync(string? status, CancellationToken ct = default);
    Task<Result> DismissAsync(Guid id, DismissInsightRequest request, CancellationToken ct = default);
    Task<Result<InsightDto>> AssignAsync(Guid id, AssignInsightRequest request, CancellationToken ct = default);

    /// <summary>Détecte les anomalies pour l'entreprise donnée — appelé par le planificateur (job <c>insights.scan</c>).</summary>
    Task<int> ScanAsync(CancellationToken ct = default);
}

/// <summary>
/// Budgets (Phase 42 §33-34) — active le CRUD sur <c>budget</c>/<c>budget_line</c> (V011, jusque-là
/// lecture vs-réel seule via <see cref="IBudgetReports"/>). Cycle
/// <c>draft → pending_approval → active → closed</c> ; une révision crée un nouveau budget lié au
/// précédent par <c>revised_from</c> (historique de version, §33).
/// </summary>
public interface IBudgetService
{
    Task<IReadOnlyList<BudgetDto>> ListAsync(CancellationToken ct = default);
    Task<Result<BudgetDto>> GetAsync(Guid id, CancellationToken ct = default);
    Task<Result<BudgetDto>> CreateAsync(CreateBudgetRequest request, CancellationToken ct = default);
    Task<Result<IReadOnlyList<BudgetLineDto>>> GetLinesAsync(Guid id, CancellationToken ct = default);
    Task<Result<IReadOnlyList<BudgetLineDto>>> SetLinesAsync(Guid id, SetBudgetLinesRequest request, CancellationToken ct = default);
    Task<Result<BudgetDto>> SubmitAsync(Guid id, CancellationToken ct = default);
    Task<Result<BudgetDto>> ApproveAsync(Guid id, CancellationToken ct = default);
    Task<Result<BudgetDto>> CloseAsync(Guid id, CancellationToken ct = default);
    Task<Result<BudgetDto>> ReviseAsync(Guid id, CancellationToken ct = default);
}
