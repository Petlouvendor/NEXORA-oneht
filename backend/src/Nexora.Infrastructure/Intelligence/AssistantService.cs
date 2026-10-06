using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Nexora.Application.Abstractions;
using Nexora.Application.Intelligence;
using Nexora.Application.Reporting;
using Nexora.Application.Security;
using Nexora.Domain.Common;
using Nexora.Contracts.Intelligence;
using Nexora.Contracts.Reporting;
using Nexora.Infrastructure.Persistence;

namespace Nexora.Infrastructure.Intelligence;

/// <summary>
/// Assistants par rôle (Phase 43 §6-10) & assistant conversationnel (§28-30, §38). Les briefings
/// réutilisent tels quels les tableaux de bord existants (Phase 7 <see cref="IKpiService"/>) — jamais
/// de redérivation — et les enrichissent des insights/recommandations pertinents pour le rôle.
/// <see cref="QueryAsync"/> délègue à <see cref="IAskNexora"/> (déterministe, sans LLM, Phase 7) et
/// journalise la question dans <c>assistant_query</c> pour l'observabilité (§38).
/// </summary>
public sealed class SqlAssistantService(
    NexoraDbContext db, ITenantContext tenant, IUnitOfWork uow, IDateTimeProvider clock,
    IKpiService kpi, IInsightService insights, IRecommendationService recommendations, IAskNexora ask,
    IPermissionService permissions) : IAssistantService
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);
    private Guid Company => tenant.ActiveCompanyId ?? throw TenantErrors.ActiveCompanyRequired();

    /// <summary>Catégories pertinentes par rôle (§7-9) — tableau vide = pas de filtre (direction voit tout, §6).</summary>
    private static readonly Dictionary<string, (string[] InsightCategories, string[] RecommendationCategories)> RoleFilters = new()
    {
        ["direction"] = ([], []),
        ["manager"]   = (["stock_anomaly", "revenue_drop", "margin_drop"], ["stock_reorder", "stock_dormant", "receivable_priority"]),
        ["comptable"] = (["expense_spike"], ["receivable_priority", "budget_overrun"]),
        ["finance"]   = (["cash_gap", "expense_spike"], ["cashflow_risk", "budget_overrun"]),
        ["caissier"]  = ([], []),
    };

    public async Task<AssistantBriefingDto> BriefingAsync(string role, Guid? sessionId, CancellationToken ct = default)
    {
        // Guide §27 : le rôle vient des permissions serveur, jamais du client. Le paramètre ne fait
        // que choisir parmi les briefings auxquels l'utilisateur a droit ; sinon 403.
        var granted = await permissions.CurrentPermissionsAsync(tenant, ct);
        var financial = granted.Contains(Permissions.Reporting.ViewFinancial);
        bool Allowed(string briefing) => briefing == "caissier" ? granted.Contains(Permissions.Pos.Sell) : financial;

        var r = (role ?? "").Trim().ToLowerInvariant();
        if (!RoleFilters.ContainsKey(r))
            r = financial ? "direction" : "caissier";
        if (!Allowed(r))
            throw new DomainException(Error.Forbidden("assistant.briefing_forbidden",
                $"Le briefing « {r} » n'est pas accessible avec vos permissions."));
        var today = DateOnly.FromDateTime(clock.UtcNow.Date);

        if (r == "caissier")
        {
            // Un caissier ne voit que sa propre session (guide §13) ; la supervision exige pos.manage_sessions.
            if (sessionId is { } requested && !granted.Contains(Permissions.Pos.ManageSessions)
                && !await OwnsSessionAsync(requested, ct))
                throw new DomainException(Error.Forbidden("pos.session_not_owned",
                    "Cette session de caisse n'est pas la vôtre."));
            var effectiveSessionId = sessionId ?? await FindOpenSessionAsync(ct);
            if (effectiveSessionId is null)
            {
                return new AssistantBriefingDto("caissier", today, "Aucune session de caisse ouverte pour le moment.",
                    JsonSerializer.SerializeToElement<object?>(null, Web), [], []);
            }
            var dash = await kpi.CashierDashboardAsync(effectiveSessionId.Value, ct);
            var headline = dash.Variance is { } v && v != 0
                ? $"Écart de caisse détecté : {v:0.##}."
                : $"{dash.SaleCount} vente(s) enregistrée(s), aucun écart détecté pour l'instant.";
            return new AssistantBriefingDto("caissier", today, headline, JsonSerializer.SerializeToElement(dash, Web), [], []);
        }

        var (insightCats, recCats) = RoleFilters[r];
        // Insights et recommandations : visibles selon permission (guide §19-20).
        var intelligence = granted.Contains(Permissions.Intelligence.View);
        var openInsights = intelligence ? await insights.ListAsync("open", ct) : [];
        var filteredInsights = insightCats.Length == 0 ? openInsights : openInsights.Where(i => insightCats.Contains(i.Category)).ToList();

        var newRecs = intelligence ? await recommendations.ListAsync("open", null, ct) : [];
        var filteredRecs = recCats.Length == 0 ? newRecs : newRecs.Where(x => recCats.Contains(x.Category)).ToList();

        JsonElement dashboard;
        string headline2;
        if (r == "manager")
        {
            var md = await kpi.ManagerDashboardAsync(new ReportFilter { Preset = "month" }, ct);
            dashboard = JsonSerializer.SerializeToElement(md, Web);
            headline2 = $"{md.CriticalStock.Count} alerte(s) de stock critique, {md.SlowMovers.Count} produit(s) en baisse ce mois-ci.";
        }
        else
        {
            var cc = await kpi.CommandCenterAsync(new ReportFilter { Preset = "month" }, ct);
            dashboard = JsonSerializer.SerializeToElement(cc, Web);
            var rev = cc.Kpis.FirstOrDefault(k => k.Key == "revenue");
            headline2 = rev is null ? "Résumé indisponible pour le moment." : $"Chiffre d'affaires du mois : {rev.Value:0.##} {rev.Unit}.";
        }

        return new AssistantBriefingDto(r, today, headline2, dashboard, filteredInsights, filteredRecs);
    }

    private async Task<bool> OwnsSessionAsync(Guid sessionId, CancellationToken ct)
        => await db.Database.SqlQuery<bool>($"""
            SELECT EXISTS (SELECT 1 FROM nexora.cash_session
                           WHERE id = {sessionId} AND company_id = {Company} AND user_id = {tenant.UserId}) AS "Value"
            """).FirstAsync(ct);

    private async Task<Guid?> FindOpenSessionAsync(CancellationToken ct)
    {
        var id = await db.Database.SqlQuery<Guid>($"""
            SELECT id AS "Value" FROM nexora.cash_session
            WHERE company_id = {Company} AND user_id = {tenant.UserId} AND status = 'open'
            ORDER BY opened_at DESC LIMIT 1
            """).FirstOrDefaultAsync(ct);
        return id == Guid.Empty ? null : id;
    }

    /// <summary>Intention de l'assistant → catégories de recommandations qui y répondent (NEXORA ONE §8 :
    /// « l'IA doit répondre avec des recommandations ») — lues, jamais générées ici.</summary>
    private static readonly Dictionary<string, string[]> RelatedCategories = new()
    {
        ["profit_change"] = ["sales_decline", "supplier_price_increase", "budget_overrun"],
        ["margin"] = ["supplier_price_increase", "sales_decline"],
        ["expense_increase"] = ["budget_overrun"],
        ["cash"] = ["cashflow_risk", "receivable_priority"],
        ["reorder"] = ["stock_reorder"],
        ["stockout_risk"] = ["stock_reorder"],
        ["stock_decrease"] = ["stock_reorder", "stock_dormant"],
        ["top_products"] = ["sales_spike", "stock_reorder"],
        ["sales_total"] = ["sales_spike", "sales_decline"],
        ["top_debtors"] = ["receivable_priority"],
        ["overdue_receivables"] = ["receivable_priority", "customer_inactive"],
        ["customer_balance"] = ["receivable_priority"],
    };

    public async Task<AskResponse> QueryAsync(AskRequest request, CancellationToken ct = default)
    {
        var response = await ask.AskAsync(request, ct);
        if (RelatedCategories.TryGetValue(response.Intent, out var categories)
            && (await permissions.CurrentPermissionsAsync(tenant, ct)).Contains(Permissions.Intelligence.View))
        {
            var open = await recommendations.ListAsync("open", null, ct);
            var related = open.Where(r => categories.Contains(r.Category))
                .OrderByDescending(r => r.PriorityScore).Take(3)
                .Select(r => new RelatedRecommendation(r.Id, r.Title, r.PriorityLevel, r.ProposedAction))
                .ToList();
            if (related.Count > 0) response = response with { Related = related };
        }
        await db.Database.ExecuteSqlAsync($"""
            INSERT INTO nexora.assistant_query (id, company_id, user_id, question, intent, answer, sources, contains_forecast)
            VALUES ({Guid.NewGuid()}, {Company}, {tenant.UserId}, {request.Question}, {response.Intent}, {response.Answer},
                    {JsonSerializer.Serialize(response.Sources, Web)}::jsonb, {response.ContainsForecast})
            """, ct);
        await uow.SaveChangesAsync(ct);
        return response;
    }
}
