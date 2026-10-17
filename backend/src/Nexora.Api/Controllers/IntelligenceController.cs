using Asp.Versioning;
using Microsoft.AspNetCore.Mvc;
using Nexora.Api.Security;
using Nexora.Application.Abstractions;
using Nexora.Application.Intelligence;
using Nexora.Application.Reporting;
using Nexora.Application.Security;
using Nexora.Contracts.Intelligence;
using Nexora.Contracts.Reporting;

namespace Nexora.Api.Controllers;

/// <summary>
/// Intelligence métier & recommandations (Phase 43 §2, §4-5, §12-13, §18, §20-21, §31, §33-39,
/// §45-48). Complète <see cref="BiController"/> (Phase 42) : les insights (anomalies détectées)
/// et prévisions restent celles de Phase 42, réexposées ici sous <c>/intelligence/*</c> pour un
/// centre d'intelligence unique (§45) ; les recommandations/scores/règles sont nouveaux.
/// L'assistant conversationnel (§28-30) délègue à <see cref="AskController"/> (Phase 7) via
/// <see cref="IAssistantService"/>, qui journalise chaque question (§38).
/// </summary>
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}")]
public sealed class IntelligenceController(
    IRecommendationService recommendations,
    IIntelligenceRuleService rules,
    IScoringService scoring,
    IAssistantService assistant,
    ITaskService tasks,
    IIntelligenceAuditQuery audit,
    IInsightService insights,
    IForecastService forecasts,
    IPermissionService permissions,
    ITenantContext tenant) : ApiControllerBase
{
    // A-048 : mêmes règles de confidentialité que les tableaux de bord (IntelligenceVisibility).
    private Task<IReadOnlyCollection<string>> GrantedAsync(CancellationToken ct) => permissions.CurrentPermissionsAsync(tenant, ct);

    // --- §2, §4-5, §12-13, §18, §20-21, §31, §33, §46 Recommandations --------

    [HttpGet("intelligence/recommendations")]
    [RequirePermission(Permissions.Intelligence.View)]
    public async Task<IActionResult> Recommendations([FromQuery] string? status, [FromQuery] string? category, CancellationToken ct)
        => Ok(await recommendations.ListAsync(status, category, ct));

    [HttpGet("intelligence/recommendations/{id:guid}")]
    [RequirePermission(Permissions.Intelligence.View)]
    public async Task<IActionResult> Recommendation(Guid id, CancellationToken ct) => FromResult(await recommendations.GetAsync(id, ct));

    /// <summary>Résumé fonctionnel §17 : la recommandation a été consultée (« vue »).</summary>
    [HttpPost("intelligence/recommendations/{id:guid}/view")]
    [RequirePermission(Permissions.Intelligence.View)]
    public async Task<IActionResult> ViewRecommendation(Guid id, CancellationToken ct) => FromResult(await recommendations.MarkViewedAsync(id, ct));

    [HttpPost("intelligence/recommendations/{id:guid}/accept")]
    [RequirePermission(Permissions.Intelligence.ActOnRecommendation)]
    public async Task<IActionResult> AcceptRecommendation(Guid id, [FromBody] AcceptRecommendationRequest request, CancellationToken ct)
        => FromResult(await recommendations.AcceptAsync(id, request, ct));

    [HttpPost("intelligence/recommendations/{id:guid}/dismiss")]
    [RequirePermission(Permissions.Intelligence.ActOnRecommendation)]
    public async Task<IActionResult> DismissRecommendation(Guid id, [FromBody] DismissRecommendationRequest request, CancellationToken ct)
        => FromResult(await recommendations.DismissAsync(id, request, ct));

    [HttpPost("intelligence/recommendations/{id:guid}/snooze")]
    [RequirePermission(Permissions.Intelligence.ActOnRecommendation)]
    public async Task<IActionResult> SnoozeRecommendation(Guid id, [FromBody] SnoozeRecommendationRequest request, CancellationToken ct)
        => FromResult(await recommendations.SnoozeAsync(id, request, ct));

    /// <summary>Boucle d'apprentissage (§33) — résultat positif/négatif après action.</summary>
    [HttpPost("intelligence/recommendations/{id:guid}/resolve")]
    [RequirePermission(Permissions.Intelligence.ActOnRecommendation)]
    public async Task<IActionResult> ResolveRecommendation(Guid id, [FromBody] ResolveRecommendationRequest request, CancellationToken ct)
        => FromResult(await recommendations.ResolveAsync(id, request, ct));

    /// <summary>Phase 54 §16, §19 « modifier » — ajuste une valeur proposée avant validation.</summary>
    [HttpPost("intelligence/recommendations/{id:guid}/modify")]
    [RequirePermission(Permissions.Intelligence.ActOnRecommendation)]
    public async Task<IActionResult> ModifyRecommendation(Guid id, [FromBody] ModifyRecommendationRequest request, CancellationToken ct)
        => FromResult(await recommendations.ModifyAsync(id, request, ct));

    /// <summary>Phase 54 §5, §20 — vue générale : comptes par priorité, actions prioritaires, opportunités.</summary>
    [HttpGet("intelligence/dashboard")]
    [RequirePermission(Permissions.Intelligence.View)]
    public async Task<IActionResult> Dashboard(CancellationToken ct) => Ok(await recommendations.DashboardAsync(ct));

    // --- §45, §58 Centre d'intelligence — réexpose Phase 42 sans dupliquer ---

    [HttpGet("intelligence/insights")]
    [RequirePermission(Permissions.Intelligence.View)]
    public async Task<IActionResult> Insights([FromQuery] string? status, CancellationToken ct)
    {
        var granted = await GrantedAsync(ct);
        return Ok((await insights.ListAsync(status, ct)).Where(i => IntelligenceVisibility.IsVisible(i.Category, granted)).ToList());
    }

    /// <summary>§22 — chaque catégorie d'insight (Phase 42) EST une anomalie détectée ; alias sans redérivation.</summary>
    [HttpGet("intelligence/anomalies")]
    [RequirePermission(Permissions.Intelligence.View)]
    public async Task<IActionResult> Anomalies([FromQuery] string? status, CancellationToken ct) => await Insights(status, ct);

    /// <summary>Prévisions de chiffre d'affaires et de trésorerie : même permission que <c>GET /forecasts</c> (A-048).</summary>
    [HttpGet("intelligence/forecasts")]
    [RequirePermission(Permissions.Intelligence.View)]
    [RequirePermission(Permissions.Reporting.ViewFinancial)]
    public async Task<IActionResult> Forecasts([FromQuery] string? metric, CancellationToken ct) => Ok(await forecasts.ListAsync(metric, ct));

    // --- §35 Scoring ----------------------------------------------------------

    [HttpGet("intelligence/scores")]
    [RequirePermission(Permissions.Intelligence.View)]
    public async Task<IActionResult> Scores([FromQuery] string? entityType, CancellationToken ct)
    {
        var granted = await GrantedAsync(ct);
        return Ok((await scoring.ListAsync(entityType, ct)).Where(x => IntelligenceVisibility.IsScoreVisible(x.EntityType, granted)).ToList());
    }

    // --- §39 Gouvernance — catalogue descriptif, lecture seule ----------------

    [HttpGet("intelligence/models")]
    [RequirePermission(Permissions.Intelligence.View)]
    public IActionResult Models() => Ok(IntelligenceModelCatalog.All);

    // --- §34 Règles configurables ----------------------------------------------

    [HttpGet("intelligence/rules")]
    [RequirePermission(Permissions.Intelligence.View)]
    public async Task<IActionResult> Rules(CancellationToken ct) => Ok(await rules.ListAsync(ct));

    [HttpPost("intelligence/rules")]
    [RequirePermission(Permissions.Intelligence.ManageRules)]
    public async Task<IActionResult> SaveRule([FromBody] SaveIntelligenceRuleRequest request, CancellationToken ct)
        => FromResult(await rules.SaveAsync(request, ct));

    [HttpDelete("intelligence/rules/{id:guid}")]
    [RequirePermission(Permissions.Intelligence.ManageRules)]
    public async Task<IActionResult> DeleteRule(Guid id, CancellationToken ct) => FromResult(await rules.DeleteAsync(id, ct));

    // --- §38 Observabilité -------------------------------------------------------

    [HttpGet("intelligence/audit")]
    [RequirePermission(Permissions.Intelligence.View)]
    public async Task<IActionResult> Audit([FromQuery] int limit, CancellationToken ct) => Ok(await audit.RecentAsync(limit == 0 ? 100 : limit, ct));

    // --- §31 Tâches (comble le manque — voir SqlTaskService) --------------------

    [HttpGet("intelligence/tasks")]
    [RequirePermission(Permissions.Intelligence.View)]
    public async Task<IActionResult> Tasks([FromQuery] string? status, CancellationToken ct) => Ok(await tasks.ListAsync(status, ct));

    [HttpPost("intelligence/tasks")]
    [RequirePermission(Permissions.Intelligence.ActOnRecommendation)]
    public async Task<IActionResult> CreateTask([FromBody] CreateTaskRequest request, CancellationToken ct)
        => FromResult(await tasks.CreateAsync(request, ct));

    [HttpPost("intelligence/tasks/{id:guid}/complete")]
    [RequirePermission(Permissions.Intelligence.ActOnRecommendation)]
    public async Task<IActionResult> CompleteTask(Guid id, CancellationToken ct) => FromResult(await tasks.CompleteAsync(id, ct));

    // --- §6-10, §28-30 Assistant ------------------------------------------------

    /// <summary>Briefing par rôle (§6-10) — réutilise les tableaux de bord Phase 7, enrichis des
    /// insights/recommandations pertinents. <c>sessionId</c> requis pour <c>caissier</c> (sinon la
    /// session ouverte de l'utilisateur est recherchée automatiquement).</summary>
    [HttpGet("assistant/briefing")]
    [RequirePermission(Permissions.Intelligence.UseAssistant)]
    public async Task<IActionResult> Briefing([FromQuery] string role, [FromQuery] Guid? sessionId, CancellationToken ct)
        => Ok(await assistant.BriefingAsync(role, sessionId, ct));

    [HttpPost("assistant/query")]
    [RequirePermission(Permissions.Intelligence.UseAssistant)]
    public async Task<IActionResult> Query([FromBody] AskRequest request, CancellationToken ct) => Ok(await assistant.QueryAsync(request, ct));
}
