using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Nexora.Application.Abstractions;
using Nexora.Application.Credit;
using Nexora.Application.Finance.Treasury;
using Nexora.Application.Intelligence;
using Nexora.Application.Reporting;
using Nexora.Contracts.Intelligence;
using Nexora.Contracts.Reporting;
using Nexora.Domain.Common;
using Nexora.Infrastructure.Persistence;

namespace Nexora.Infrastructure.Intelligence;

/// <summary>
/// Recommandations (Phase 43 §2, §4-5, §12-13, §18, §20-21, §31, §33, §46) — 5 règles bornées,
/// une par catégorie, chacune un contrôle SQL déterministe (même esprit que les 5 règles
/// <c>SqlInsightService</c> de Phase 42). Chaque recommandation explique pourquoi elle existe,
/// montre les données qui la soutiennent et propose une action — jamais présentée comme un fait
/// (§2) ; aucune action financière n'est jamais déclenchée automatiquement (§32).
/// </summary>
public sealed class SqlRecommendationService(
    NexoraDbContext db, ITenantContext tenant, IUnitOfWork uow, IDateTimeProvider clock,
    ITreasuryQueries treasury, ICreditPortfolioQueries credit, IBudgetReports budgetReports,
    ITaskService tasks, IInsightService insights, IBusinessCalendar calendar, IPermissionService permissions) : IRecommendationService
{
    // A-048 : catégories filtrées selon les permissions de pilotage de l'utilisateur (IntelligenceVisibility).
    private IReadOnlyCollection<string>? _granted;
    private async Task<IReadOnlyCollection<string>> GrantedAsync(CancellationToken ct)
        => _granted ??= tenant.UserId is null ? [] : await permissions.CurrentPermissionsAsync(tenant, ct);

    private async Task<List<Row>> VisibleAsync(IEnumerable<Row> rows, CancellationToken ct)
    {
        var granted = await GrantedAsync(ct);
        return rows.Where(r => Nexora.Application.Intelligence.IntelligenceVisibility.IsVisible(r.Category, granted)).ToList();
    }

    /// <summary>Recommandation hors des catégories visibles : traitée comme introuvable (ni lue, ni modifiée).</summary>
    private async Task<bool> HiddenAsync(Guid id, CancellationToken ct)
    {
        var category = (await db.Database.SqlQuery<string>($"""
            SELECT category AS "Value" FROM nexora.recommendation WHERE id = {id} AND company_id = {Company}
            """).ToListAsync(ct)).FirstOrDefault();
        return category is not null && !Nexora.Application.Intelligence.IntelligenceVisibility.IsVisible(category, await GrantedAsync(ct));
    }

    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);
    private Guid Company => tenant.ActiveCompanyId ?? throw TenantErrors.ActiveCompanyRequired();

    /// <summary>Durée de vie par défaut d'une recommandation jamais traitée (Phase 54 §6, §21).</summary>
    private const int DefaultExpiryDays = 30;

    private sealed record Row(Guid Id, Guid? EstablishmentId, string Category, string RuleKey, string Title,
        string Why, string Evidence, string ImpactEstimate, string ProposedAction, string ExpectedResult,
        int ImpactScore, int UrgencyScore, int ConfidenceScore, string ConfidenceLevel, string ConfidenceReason,
        int PriorityScore, string PriorityLevel, string Nature, string? DrillPath, DateOnly PeriodStart, string Status,
        DateTimeOffset? SnoozeUntil, string? Outcome, string? OutcomeNote, DateTimeOffset? ExpiresAt,
        string? OverrideValue, DateTimeOffset CreatedAt, DateTimeOffset? ViewedAt);

    private const string Select = """
        SELECT id AS "Id", establishment_id AS "EstablishmentId", category AS "Category", rule_key AS "RuleKey",
               title AS "Title", why AS "Why", evidence::text AS "Evidence", impact_estimate::text AS "ImpactEstimate",
               proposed_action AS "ProposedAction", expected_result AS "ExpectedResult",
               impact_score AS "ImpactScore", urgency_score AS "UrgencyScore", confidence_score AS "ConfidenceScore",
               confidence_level AS "ConfidenceLevel", confidence_reason AS "ConfidenceReason",
               priority_score AS "PriorityScore", priority_level AS "PriorityLevel", nature AS "Nature",
               drill_path AS "DrillPath", period_start AS "PeriodStart",
               status AS "Status", snooze_until AS "SnoozeUntil", outcome AS "Outcome", outcome_note AS "OutcomeNote",
               expires_at AS "ExpiresAt", override_value::text AS "OverrideValue", created_at AS "CreatedAt",
               viewed_at AS "ViewedAt"
        FROM nexora.recommendation
        """;

    // --- Lecture & cycle de vie (§31, §33) --------------------------------

    public async Task<IReadOnlyList<RecommendationDto>> ListAsync(string? status, string? category, CancellationToken ct = default)
    {
        // « open » : à décider = nouvelle ou déjà vue (résumé fonctionnel §17).
        if (string.Equals(status?.Trim(), "open", StringComparison.OrdinalIgnoreCase))
            return (category is null
                    ? await db.Database.SqlQueryRaw<Row>(
                        Select + " WHERE company_id = {0} AND status IN ('new','viewed') ORDER BY priority_score DESC, created_at DESC LIMIT 200",
                        Company).ToListAsync(ct)
                    : await db.Database.SqlQueryRaw<Row>(
                        Select + " WHERE company_id = {0} AND status IN ('new','viewed') AND category = {1} ORDER BY priority_score DESC, created_at DESC LIMIT 200",
                        Company, category.Trim()).ToListAsync(ct))
                is var open ? (await VisibleAsync(open, ct)).Select(ToDto).ToList() : [];
        var rows = (status, category) switch
        {
            (null, null) => await db.Database.SqlQueryRaw<Row>(
                Select + " WHERE company_id = {0} ORDER BY priority_score DESC, created_at DESC LIMIT 200", Company).ToListAsync(ct),
            ({ } s, null) => await db.Database.SqlQueryRaw<Row>(
                Select + " WHERE company_id = {0} AND status = {1} ORDER BY priority_score DESC, created_at DESC LIMIT 200",
                Company, s.Trim().ToLowerInvariant()).ToListAsync(ct),
            (null, { } c) => await db.Database.SqlQueryRaw<Row>(
                Select + " WHERE company_id = {0} AND category = {1} ORDER BY priority_score DESC, created_at DESC LIMIT 200",
                Company, c.Trim().ToLowerInvariant()).ToListAsync(ct),
            ({ } s, { } c) => await db.Database.SqlQueryRaw<Row>(
                Select + " WHERE company_id = {0} AND status = {1} AND category = {2} ORDER BY priority_score DESC, created_at DESC LIMIT 200",
                Company, s.Trim().ToLowerInvariant(), c.Trim().ToLowerInvariant()).ToListAsync(ct),
        };
        return (await VisibleAsync(rows, ct)).Select(ToDto).ToList();
    }

    /// <summary>État « Vue » (§17) : première consultation — qui, quand. Sans effet sur une recommandation déjà décidée.</summary>
    public async Task<Result<RecommendationDto>> MarkViewedAsync(Guid id, CancellationToken ct = default)
    {
        if (await HiddenAsync(id, ct)) return Error.NotFound("recommendation.not_found", "Recommandation introuvable.");
        var rows = await db.Database.ExecuteSqlAsync($"""
            UPDATE nexora.recommendation
               SET status = CASE WHEN status = 'new' THEN 'viewed' ELSE status END,
                   viewed_at = COALESCE(viewed_at, now()), viewed_by = COALESCE(viewed_by, {tenant.UserId}), updated_at = now()
             WHERE id = {id} AND company_id = {Company}
            """, ct);
        if (rows == 0) return Error.NotFound("recommendation.not_found", "Recommandation introuvable.");
        await uow.SaveChangesAsync(ct);
        return await GetAsync(id, ct);
    }

    public async Task<Result<RecommendationDto>> GetAsync(Guid id, CancellationToken ct = default)
    {
        var row = await db.Database.SqlQueryRaw<Row>(Select + " WHERE id = {0} AND company_id = {1}", id, Company).FirstOrDefaultAsync(ct);
        return row is null || (await VisibleAsync([row], ct)).Count == 0
            ? Error.NotFound("recommendation.not_found", "Recommandation introuvable.")
            : Result.Success(ToDto(row));
    }

    // --- §5, §20 Vue générale — agrège les recommandations, jamais un second calcul --------

    private sealed record LevelCountRow(string PriorityLevel, string Category, int Count);

    public async Task<IntelligenceDashboardDto> DashboardAsync(CancellationToken ct = default)
    {
        // A-048 : comptes et listes limités aux catégories visibles de l'utilisateur.
        var granted = await GrantedAsync(ct);
        var counts = (await db.Database.SqlQueryRaw<LevelCountRow>("""
            SELECT priority_level AS "PriorityLevel", category AS "Category", count(*)::int AS "Count"
            FROM nexora.recommendation WHERE company_id = {0} AND status IN ('new', 'viewed')
            GROUP BY priority_level, category
            """, Company).ToListAsync(ct))
            .Where(c => Nexora.Application.Intelligence.IntelligenceVisibility.IsVisible(c.Category, granted)).ToList();
        int CountOf(string level) => counts.Where(c => c.PriorityLevel == level).Sum(c => c.Count);

        var topActions = (await VisibleAsync(await db.Database.SqlQueryRaw<Row>(
            Select + " WHERE company_id = {0} AND status IN ('new', 'viewed') AND nature = 'risk' ORDER BY priority_score DESC LIMIT 50",
            Company).ToListAsync(ct), ct)).Take(5).ToList();
        var topOpportunities = (await VisibleAsync(await db.Database.SqlQueryRaw<Row>(
            Select + " WHERE company_id = {0} AND status IN ('new', 'viewed') AND nature = 'opportunity' ORDER BY priority_score DESC LIMIT 50",
            Company).ToListAsync(ct), ct)).Take(5).ToList();
        var openAnomalies = (await insights.ListAsync("open", ct))
            .Count(i => Nexora.Application.Intelligence.IntelligenceVisibility.IsVisible(i.Category, granted));

        return new IntelligenceDashboardDto(
            CountOf("critical"), CountOf("important"), CountOf("watch"), CountOf("opportunity"), openAnomalies,
            topActions.Select(ToDto).ToList(), topOpportunities.Select(ToDto).ToList(), clock.UtcNow);
    }

    public async Task<Result<RecommendationDto>> AcceptAsync(Guid id, AcceptRecommendationRequest request, CancellationToken ct = default)
    {
        if (await HiddenAsync(id, ct)) return Error.NotFound("recommendation.not_found", "Recommandation introuvable.");
        var rows = await db.Database.ExecuteSqlAsync($"""
            UPDATE nexora.recommendation SET status = 'accepted', decided_by = {tenant.UserId}, decided_at = now(),
                   decision_reason = {request.Note}, updated_at = now()
            WHERE id = {id} AND company_id = {Company} AND status IN ('new','viewed','snoozed')
            """, ct);
        if (rows == 0) return Error.NotFound("recommendation.not_found", "Recommandation introuvable ou déjà traitée.");
        await uow.SaveChangesAsync(ct);

        var saved = await GetAsync(id, ct);
        if (request.CreateTask && saved.IsSuccess)
        {
            await tasks.CreateAsync(new CreateTaskRequest(
                saved.Value!.Title, saved.Value.ProposedAction, "recommendation", id,
                request.AssignTo ?? tenant.UserId, "normal", null), ct);
        }
        return saved;
    }

    public async Task<Result> DismissAsync(Guid id, DismissRecommendationRequest request, CancellationToken ct = default)
    {
        if (await HiddenAsync(id, ct)) return Error.NotFound("recommendation.not_found", "Recommandation introuvable.");
        var rows = await db.Database.ExecuteSqlAsync($"""
            UPDATE nexora.recommendation SET status = 'dismissed', decided_by = {tenant.UserId}, decided_at = now(),
                   decision_reason = {request.Reason}, updated_at = now()
            WHERE id = {id} AND company_id = {Company} AND status IN ('new','viewed','snoozed')
            """, ct);
        if (rows == 0) return Error.NotFound("recommendation.not_found", "Recommandation introuvable ou déjà traitée.");
        await uow.SaveChangesAsync(ct);
        return Result.Success();
    }

    public async Task<Result<RecommendationDto>> SnoozeAsync(Guid id, SnoozeRecommendationRequest request, CancellationToken ct = default)
    {
        if (await HiddenAsync(id, ct)) return Error.NotFound("recommendation.not_found", "Recommandation introuvable.");
        if (request.Until <= clock.UtcNow) return Error.Validation("recommendation.snooze_until", "La date de report doit être future.");
        var rows = await db.Database.ExecuteSqlAsync($"""
            UPDATE nexora.recommendation SET status = 'snoozed', snooze_until = {request.Until},
                   decision_reason = {request.Reason}, updated_at = now()
            WHERE id = {id} AND company_id = {Company} AND status IN ('new','viewed')
            """, ct);
        if (rows == 0) return Error.NotFound("recommendation.not_found", "Recommandation introuvable ou déjà traitée.");
        await uow.SaveChangesAsync(ct);
        return await GetAsync(id, ct);
    }

    public async Task<Result<RecommendationDto>> ModifyAsync(Guid id, ModifyRecommendationRequest request, CancellationToken ct = default)
    {
        if (await HiddenAsync(id, ct)) return Error.NotFound("recommendation.not_found", "Recommandation introuvable.");
        var overrideJson = JsonSerializer.Serialize(request.OverrideValue, Web);
        var rows = await db.Database.ExecuteSqlAsync($"""
            UPDATE nexora.recommendation SET status = 'modified', override_value = {overrideJson}::jsonb,
                   decided_by = {tenant.UserId}, decided_at = now(), decision_reason = {request.Note}, updated_at = now()
            WHERE id = {id} AND company_id = {Company} AND status IN ('new', 'viewed', 'snoozed')
            """, ct);
        if (rows == 0) return Error.NotFound("recommendation.not_found", "Recommandation introuvable ou déjà traitée.");
        await uow.SaveChangesAsync(ct);
        return await GetAsync(id, ct);
    }

    public async Task<Result<RecommendationDto>> ResolveAsync(Guid id, ResolveRecommendationRequest request, CancellationToken ct = default)
    {
        if (await HiddenAsync(id, ct)) return Error.NotFound("recommendation.not_found", "Recommandation introuvable.");
        var outcome = request.Outcome.Trim().ToLowerInvariant();
        if (outcome is not ("positive" or "negative"))
            return Error.Validation("recommendation.outcome", "Résultat attendu : positive ou negative.");

        var rows = await db.Database.ExecuteSqlAsync($"""
            UPDATE nexora.recommendation SET status = 'resolved', outcome = {outcome}, outcome_note = {request.Note},
                   outcome_at = now(), updated_at = now()
            WHERE id = {id} AND company_id = {Company} AND status IN ('accepted','modified')
            """, ct);
        if (rows == 0) return Error.NotFound("recommendation.not_found", "Recommandation introuvable.");
        await uow.SaveChangesAsync(ct);
        return await GetAsync(id, ct);
    }

    // --- Génération (§34, §46) — appelée par le planificateur ---------------

    private sealed record RuleRow(Guid Id, string Key, string Category, string Label, string ThresholdJson,
        int? PeriodDays, string DefaultPriority, string? RecipientRole, string ActionLabel, int? EscalateAfterDays);

    public async Task<int> GenerateAsync(CancellationToken ct = default)
    {
        await ExpireDueAsync(ct);

        var today = calendar.Today;
        var rules = await LoadEnabledRulesAsync(today, ct);
        var created = 0;
        if (rules.TryGetValue("stock_reorder", out var r1)) created += await GenerateStockReorder(r1, today, ct);
        if (rules.TryGetValue("stock_dormant", out var r2)) created += await GenerateStockDormant(r2, today, ct);
        if (rules.TryGetValue("receivable_priority", out var r3)) created += await GenerateReceivablePriority(r3, today, ct);
        if (rules.TryGetValue("cashflow_risk", out var r4)) created += await GenerateCashflowRisk(r4, today, ct);
        if (rules.TryGetValue("budget_overrun", out var r5)) created += await GenerateBudgetOverrun(r5, today, ct);
        if (rules.TryGetValue("sales_spike", out var r6)) created += await GenerateSalesSpike(r6, today, ct);
        if (rules.TryGetValue("sales_decline", out var r7)) created += await GenerateSalesDecline(r7, today, ct);
        if (rules.TryGetValue("customer_inactive", out var r8)) created += await GenerateCustomerInactive(r8, today, ct);
        if (rules.TryGetValue("supplier_price_increase", out var r9)) created += await GenerateSupplierPriceIncrease(r9, today, ct);
        return created;
    }

    /// <summary>Phase 54 §6, §21 — une recommandation `new` jamais traitée avant son échéance expire
    /// plutôt que de s'accumuler indéfiniment ; les autres statuts (accepted/modified/…) ne sont
    /// jamais expirés, une décision humaine ne s'efface pas toute seule.</summary>
    private async Task<int> ExpireDueAsync(CancellationToken ct)
    {
        var n = await db.Database.ExecuteSqlAsync($"""
            UPDATE nexora.recommendation SET status = 'expired', updated_at = now()
            WHERE company_id = {Company} AND status IN ('new', 'viewed') AND expires_at IS NOT NULL AND expires_at < now()
            """, ct);
        if (n > 0) await uow.SaveChangesAsync(ct);
        return n;
    }

    private async Task<Dictionary<string, RuleRow>> LoadEnabledRulesAsync(DateOnly today, CancellationToken ct)
    {
        // company_id NULL = valeur système par défaut ; une règle propre à l'entreprise la surcharge (§34).
        var rows = await db.Database.SqlQueryRaw<RuleRow>("""
            SELECT DISTINCT ON (key) id AS "Id", key AS "Key", category AS "Category", label AS "Label",
                   threshold::text AS "ThresholdJson", period_days AS "PeriodDays", default_priority AS "DefaultPriority",
                   recipient_role AS "RecipientRole", action_label AS "ActionLabel", escalate_after_days AS "EscalateAfterDays"
            FROM nexora.intelligence_rule
            WHERE (company_id = {0} OR company_id IS NULL) AND enabled = true
              AND valid_from <= {1} AND (expires_at IS NULL OR expires_at >= {1})
            ORDER BY key, company_id NULLS LAST
            """, Company, today).ToListAsync(ct);
        return rows.ToDictionary(r => r.Key);
    }

    // --- §12-13 Réapprovisionnement intelligent ------------------------------

    private sealed record ReorderRow(Guid ProductId, string Sku, string Name, Guid LocationId,
        decimal OnHand, decimal ReorderPoint, int LeadTimeDays, decimal TotalQty, int HistoryDays);

    private async Task<int> GenerateStockReorder(RuleRow rule, DateOnly today, CancellationToken ct)
    {
        var threshold = JsonDocument.Parse(rule.ThresholdJson).RootElement;
        var safetyDays = threshold.TryGetProperty("safetyDays", out var sd) ? sd.GetInt32() : 7;
        var minHistoryDays = threshold.TryGetProperty("minHistoryDays", out var mh) ? mh.GetInt32() : 14;
        var periodDays = rule.PeriodDays ?? 60;
        var windowStart = today.AddDays(-periodDays);

        var rows = await db.Database.SqlQuery<ReorderRow>($"""
            SELECT p.id AS "ProductId", p.sku AS "Sku", p.name AS "Name", sb.inventory_location_id AS "LocationId",
                   sb.quantity_available AS "OnHand", rr.reorder_point AS "ReorderPoint", rr.lead_time_days AS "LeadTimeDays",
                   COALESCE(v.total_qty, 0) AS "TotalQty", COALESCE(v.history_days, 0)::int AS "HistoryDays"
            FROM nexora.stock_balance sb
            JOIN nexora.product p ON p.id = sb.product_id
            JOIN LATERAL (
                SELECT reorder_point, lead_time_days FROM nexora.product_reorder_rule
                WHERE product_id = p.id AND company_id = {Company} AND status = 'active'
                  AND (inventory_location_id = sb.inventory_location_id OR inventory_location_id IS NULL)
                ORDER BY inventory_location_id NULLS LAST LIMIT 1
            ) rr ON true
            LEFT JOIN LATERAL (
                SELECT SUM(sl.quantity) total_qty, COUNT(DISTINCT s.sold_at::date) history_days
                FROM nexora.sale_line sl JOIN nexora.sale s ON s.id = sl.sale_id
                WHERE sl.product_id = p.id AND s.company_id = {Company} AND s.status = 'completed' AND s.sold_at::date > {windowStart}
            ) v ON true
            WHERE sb.company_id = {Company} AND sb.quantity_available <= rr.reorder_point
            """).ToListAsync(ct);

        var created = 0;
        foreach (var r in rows)
        {
            if (r.HistoryDays < 3 || r.TotalQty <= 0) continue;
            var velocity = r.TotalQty / (decimal)periodDays;
            var suggested = Math.Ceiling(velocity * (r.LeadTimeDays + safetyDays) - r.OnHand);
            if (suggested <= 0) continue;

            var (confScore, confLevel, confReason) = r.HistoryDays >= 45
                ? (3, "high", $"{r.HistoryDays} jours d'historique de vente sur la fenêtre analysée.")
                : r.HistoryDays >= minHistoryDays
                    ? (2, "medium", $"{r.HistoryDays} jours d'historique — au-dessus du minimum ({minHistoryDays}).")
                    : (1, "low", $"Seulement {r.HistoryDays} jours d'historique — sous le minimum recommandé ({minHistoryDays}).");

            var evidence = JsonSerializer.Serialize(new
            {
                onHand = r.OnHand, velocityPerDay = Math.Round(velocity, 2), leadTimeDays = r.LeadTimeDays,
                safetyDays, historyDays = r.HistoryDays, reorderPoint = r.ReorderPoint,
            }, Web);
            var impact = JsonSerializer.Serialize(new
            {
                suggestedQuantity = suggested,
                note = "Proposition basée sur les paramètres et l'historique disponibles — pas une commande automatique (§12).",
            }, Web);

            created += await Insert("stock_reorder", rule.Key, $"{rule.Label} — {r.Name}",
                $"Le stock disponible ({r.OnHand:0.##}) est sous le seuil de réapprovisionnement ({r.ReorderPoint:0.##}) " +
                $"avec une vélocité de {Math.Round(velocity, 2):0.##} unité(s)/jour.",
                evidence, impact, rule.ActionLabel, $"Réapprovisionner {r.Name} d'environ {suggested:0} unité(s).",
                3, r.OnHand <= 0 ? 5 : 4, confScore, confLevel, confReason,
                $"stock/reorder/{r.ProductId}", today, ct);
        }
        return created;
    }

    // --- §12, §51 Produit dormant / surstock --------------------------------

    private sealed record DormantRow(Guid ProductId, string Name, decimal OnHand, decimal AvgCost, decimal? MarginRate, bool HasHistory);

    private async Task<int> GenerateStockDormant(RuleRow rule, DateOnly today, CancellationToken ct)
    {
        var threshold = JsonDocument.Parse(rule.ThresholdJson).RootElement;
        var dormantDays = rule.PeriodDays ?? (threshold.TryGetProperty("dormantDays", out var dd) ? dd.GetInt32() : 45);
        var marginMax = threshold.TryGetProperty("marginRateMaxPct", out var mm) ? mm.GetDecimal() : 15m;

        var rows = await db.Database.SqlQuery<DormantRow>($"""
            SELECT p.id AS "ProductId", p.name AS "Name", SUM(sb.quantity_on_hand) AS "OnHand", AVG(sb.average_cost) AS "AvgCost",
                   m.margin_rate AS "MarginRate", COALESCE(m.has_history, false) AS "HasHistory"
            FROM nexora.product p
            JOIN nexora.stock_balance sb ON sb.product_id = p.id
            LEFT JOIN LATERAL (
                SELECT CASE WHEN SUM(sl.line_total) > 0
                            THEN (SUM(sl.line_total) - SUM(sl.unit_cost * sl.quantity)) / SUM(sl.line_total) * 100 END AS margin_rate,
                       (COUNT(*) > 0) AS has_history
                FROM nexora.sale_line sl JOIN nexora.sale s ON s.id = sl.sale_id
                WHERE sl.product_id = p.id AND s.company_id = {Company} AND s.status = 'completed'
            ) m ON true
            WHERE p.company_id = {Company} AND sb.quantity_on_hand > 0
              AND NOT EXISTS (
                  SELECT 1 FROM nexora.stock_movement mo
                  WHERE mo.product_id = p.id AND mo.movement_type = 'sale'
                    AND mo.occurred_at >= now() - make_interval(days => {dormantDays}))
            GROUP BY p.id, p.name, m.margin_rate, m.has_history
            """).ToListAsync(ct);

        var created = 0;
        foreach (var r in rows)
        {
            var (confScore, confLevel, confReason) = r.HasHistory
                ? (3, "high", $"Basé sur {dormantDays} jours sans mouvement de vente et la marge historique réelle.")
                : (0, "insufficient_data", $"Produit jamais vendu — aucune donnée de marge, seule l'absence de mouvement sur {dormantDays} jours est certaine.");
            var urgency = r.MarginRate is { } m2 && m2 < marginMax ? 4 : 2;

            var evidence = JsonSerializer.Serialize(new
            {
                onHand = r.OnHand, avgCost = Math.Round(r.AvgCost, 2),
                marginRatePct = r.MarginRate.HasValue ? Math.Round(r.MarginRate.Value, 1) : (decimal?)null, dormantDays,
            }, Web);
            var impact = JsonSerializer.Serialize(new
            {
                estimatedTiedUpValue = Math.Round(r.OnHand * r.AvgCost, 2),
                note = "Estimation de la valeur immobilisée — pas une perte constatée (§2).",
            }, Web);
            var why = r.MarginRate.HasValue
                ? $"{r.Name} n'a enregistré aucune vente depuis au moins {dormantDays} jours alors que {r.OnHand:0.##} unité(s) restent en stock, avec une marge historique de {r.MarginRate:0.#} %."
                : $"{r.Name} n'a jamais été vendu et {r.OnHand:0.##} unité(s) restent en stock depuis au moins {dormantDays} jours.";

            created += await Insert("stock_dormant", rule.Key, $"{rule.Label} — {r.Name}", why, evidence, impact,
                rule.ActionLabel, "Libérer du capital immobilisé et éviter une dépréciation supplémentaire.",
                3, urgency, confScore, confLevel, confReason,
                $"stock/dormant/{r.ProductId}", today.AddDays(-dormantDays), ct);
        }
        return created;
    }

    // --- §18 Priorisation du recouvrement ------------------------------------

    private async Task<int> GenerateReceivablePriority(RuleRow rule, DateOnly today, CancellationToken ct)
    {
        var threshold = JsonDocument.Parse(rule.ThresholdJson).RootElement;
        var minAmount = threshold.TryGetProperty("minOverdueAmount", out var ma) ? ma.GetDecimal() : 100m;
        var topN = threshold.TryGetProperty("topN", out var tn) ? tn.GetInt32() : 10;

        var overdue = await credit.CollectionsAsync(onlyOverdue: true, ct);
        var candidates = overdue.Where(c => c.TotalOutstanding >= minAmount && !c.Blocked)
            .OrderByDescending(c => c.OldestOverdueDays * (double)c.TotalOutstanding)
            .Take(topN);

        var created = 0;
        foreach (var c in candidates)
        {
            var urgency = c.OldestOverdueDays > 90 ? 5 : c.OldestOverdueDays > 60 ? 4 : c.OldestOverdueDays > 30 ? 3 : 2;
            var evidence = JsonSerializer.Serialize(new
            {
                totalOutstanding = c.TotalOutstanding, oldestOverdueDays = c.OldestOverdueDays,
                bucket1To30 = c.Bucket1To30, bucket31To60 = c.Bucket31To60, bucket61To90 = c.Bucket61To90,
                bucket90Plus = c.Bucket90Plus, openPromises = c.OpenPromises, openDisputes = c.OpenDisputes,
            }, Web);
            var impact = JsonSerializer.Serialize(new { potentialRecovery = c.TotalOutstanding, currency = c.Currency }, Web);

            created += await Insert("receivable_priority", rule.Key, $"{rule.Label} — {c.CustomerName}",
                $"{c.CustomerName} doit {c.TotalOutstanding:0.##} {c.Currency}, échu depuis jusqu'à {c.OldestOverdueDays} jour(s).",
                evidence, impact, rule.ActionLabel, $"Relancer {c.CustomerName} pour {c.TotalOutstanding:0.##} {c.Currency}.",
                c.TotalOutstanding >= minAmount * 5 ? 4 : 3, urgency,
                3, "high", "Basé sur les créances enregistrées, sans estimation.",
                $"receivables/customer/{c.CustomerId}", today, ct);
        }
        return created;
    }

    // --- §20 Risque de trésorerie (délègue à Phase 28) -----------------------

    private async Task<int> GenerateCashflowRisk(RuleRow rule, DateOnly today, CancellationToken ct)
    {
        var threshold = JsonDocument.Parse(rule.ThresholdJson).RootElement;
        var minBalance = threshold.TryGetProperty("minBalance", out var mb) ? mb.GetDecimal() : 0m;
        var horizon = rule.PeriodDays ?? (threshold.TryGetProperty("horizonDays", out var hd) ? hd.GetInt32() : 30);

        var liquidity = await treasury.LiquidityAsync(horizon, ct);
        if (liquidity.ProjectedGap >= minBalance) return 0;

        var evidence = JsonSerializer.Serialize(new
        {
            immediate = liquidity.ImmediateLiquidity, expectedReceivables = liquidity.ExpectedReceivables,
            commitments = liquidity.ShortTermCommitments, gap = liquidity.ProjectedGap, horizonDays = horizon, minBalance,
        }, Web);
        var impact = JsonSerializer.Serialize(new { projectedShortfall = minBalance - liquidity.ProjectedGap }, Web);

        return await Insert("cashflow_risk", rule.Key, $"{rule.Label} à {horizon} jours",
            $"Le solde projeté ({liquidity.ProjectedGap:0.##}) passe sous le seuil configuré ({minBalance:0.##}) à {horizon} jours.",
            evidence, impact, rule.ActionLabel, "Accélérer le recouvrement ou différer les décaissements non critiques.",
            5, liquidity.ProjectedGap < 0 ? 5 : 3,
            3, "high", "Basé sur les soldes et échéances enregistrés, sans projection statistique.",
            "treasury/liquidity", today, ct);
    }

    // --- §21 Dépassement budgétaire probable ---------------------------------

    private async Task<int> GenerateBudgetOverrun(RuleRow rule, DateOnly today, CancellationToken ct)
    {
        var threshold = JsonDocument.Parse(rule.ThresholdJson).RootElement;
        var overrunPct = threshold.TryGetProperty("overrunPct", out var op) ? op.GetDecimal() : 10m;

        var daysInYear = DateTime.IsLeapYear(today.Year) ? 366 : 365;
        var fraction = today.DayOfYear / (decimal)daysInYear;
        if (fraction < 0.05m) return 0;   // trop tôt dans l'exercice pour projeter (§37 « données suffisantes »)

        var report = await budgetReports.BudgetVsActualAsync(today.Year, new ReportFilter(), ct);
        var created = 0;
        foreach (var row in report.Rows)
        {
            if (row.Budget <= 0) continue;
            var projected = row.Actual / fraction;
            var overrunAmountPct = (projected - row.Budget) / row.Budget * 100m;
            if (overrunAmountPct < overrunPct) continue;

            var (confScore, confLevel, confReason) = fraction >= 0.5m
                ? (3, "high", $"{Math.Round(fraction * 100)} % de l'exercice écoulé.")
                : fraction >= 0.25m
                    ? (2, "medium", $"{Math.Round(fraction * 100)} % de l'exercice écoulé — projection encore incertaine.")
                    : (1, "low", $"Seulement {Math.Round(fraction * 100)} % de l'exercice écoulé — projection peu fiable.");

            var evidence = JsonSerializer.Serialize(new
            { budget = row.Budget, actualToDate = row.Actual, fractionElapsedPct = Math.Round(fraction * 100, 1) }, Web);
            var impact = JsonSerializer.Serialize(new
            { projectedYearEnd = Math.Round(projected, 2), overrunPct = Math.Round(overrunAmountPct, 1) }, Web);

            created += await Insert("budget_overrun", rule.Key, $"{rule.Label} — {row.AccountName}",
                $"Au rythme actuel, « {row.AccountName} » atteindrait {projected:0.##} en fin d'exercice contre un budget de {row.Budget:0.##} ({overrunAmountPct:+0.#;-0.#} %).",
                evidence, impact, rule.ActionLabel, $"Réviser le budget de « {row.AccountName} » ou réduire les dépenses associées.",
                3, overrunAmountPct >= 25 ? 4 : 3, confScore, confLevel, confReason,
                $"budget/{today.Year}/{row.AccountCode}", new DateOnly(today.Year, 1, 1), ct);
        }
        return created;
    }

    // --- Écriture avec déduplication (§37, comme insight_dedup_uk) ----------

    private Task<int> Insert(string category, string ruleKey, string title, string why, string evidenceJson,
        string impactJson, string proposedAction, string expectedResult, int impactScore, int urgencyScore,
        int confScore, string confLevel, string confReason, string drillPath, DateOnly periodStart, CancellationToken ct)
        => Insert(category, ruleKey, title, why, evidenceJson, impactJson, proposedAction, expectedResult,
            impactScore, urgencyScore, confScore, confLevel, confReason, drillPath, periodStart, "risk", ct);

    private async Task<int> Insert(string category, string ruleKey, string title, string why, string evidenceJson,
        string impactJson, string proposedAction, string expectedResult, int impactScore, int urgencyScore,
        int confScore, string confLevel, string confReason, string drillPath, DateOnly periodStart, string nature,
        CancellationToken ct)
    {
        var id = Guid.NewGuid();
        var expiresAt = clock.UtcNow.AddDays(DefaultExpiryDays);
        var rows = await db.Database.ExecuteSqlAsync($"""
            INSERT INTO nexora.recommendation
                (id, company_id, category, rule_key, title, why, evidence, impact_estimate, proposed_action,
                 expected_result, impact_score, urgency_score, confidence_score, confidence_level, confidence_reason,
                 drill_path, period_start, nature, expires_at)
            VALUES ({id}, {Company}, {category}, {ruleKey}, {title}, {why}, {evidenceJson}::jsonb, {impactJson}::jsonb,
                    {proposedAction}, {expectedResult}, {impactScore}, {urgencyScore}, {confScore}, {confLevel},
                    {confReason}, {drillPath}, {periodStart}, {nature}, {expiresAt})
            ON CONFLICT ON CONSTRAINT recommendation_dedup_uk DO NOTHING
            """, ct);
        if (rows > 0) await uow.SaveChangesAsync(ct);
        return rows;
    }

    private static RecommendationDto ToDto(Row r) => new(
        r.Id, r.EstablishmentId, r.Category, r.RuleKey, r.Title, r.Why,
        JsonDocument.Parse(r.Evidence).RootElement, JsonDocument.Parse(r.ImpactEstimate).RootElement,
        r.ProposedAction, r.ExpectedResult, r.ImpactScore, r.UrgencyScore, r.ConfidenceScore, r.ConfidenceLevel,
        r.ConfidenceReason, r.PriorityScore, r.PriorityLevel, r.Nature, r.DrillPath, r.PeriodStart, r.Status,
        r.SnoozeUntil, r.Outcome, r.OutcomeNote, r.ExpiresAt,
        r.OverrideValue is null ? null : JsonDocument.Parse(r.OverrideValue).RootElement, r.CreatedAt, r.ViewedAt);

    // --- §9.2 Ventes : hausse/baisse inhabituelle (Phase 54) -----------------

    private sealed record SalesTrendRow(decimal RecentTotal, int RecentDays, decimal PriorTotal, int PriorDays);

    private async Task<SalesTrendRow> LoadSalesTrendAsync(int periodDays, DateOnly today, CancellationToken ct)
    {
        var recentStart = today.AddDays(-periodDays);
        var priorStart = today.AddDays(-2 * periodDays);
        return await db.Database.SqlQuery<SalesTrendRow>($"""
            SELECT
                COALESCE(SUM(total) FILTER (WHERE sold_at::date > {recentStart}), 0) AS "RecentTotal",
                COUNT(DISTINCT sold_at::date) FILTER (WHERE sold_at::date > {recentStart}) AS "RecentDays",
                COALESCE(SUM(total) FILTER (WHERE sold_at::date <= {recentStart} AND sold_at::date > {priorStart}), 0) AS "PriorTotal",
                COUNT(DISTINCT sold_at::date) FILTER (WHERE sold_at::date <= {recentStart} AND sold_at::date > {priorStart}) AS "PriorDays"
            FROM nexora.sale
            WHERE company_id = {Company} AND status = 'completed' AND sold_at::date > {priorStart}
            """).FirstAsync(ct);
    }

    private async Task<int> GenerateSalesSpike(RuleRow rule, DateOnly today, CancellationToken ct)
    {
        var threshold = JsonDocument.Parse(rule.ThresholdJson).RootElement;
        var growthPct = threshold.TryGetProperty("growthPct", out var gp) ? gp.GetDecimal() : 15m;
        var minHistoryDays = threshold.TryGetProperty("minHistoryDays", out var mh) ? mh.GetInt32() : 10;
        var periodDays = rule.PeriodDays ?? 14;

        var t = await LoadSalesTrendAsync(periodDays, today, ct);
        if (t.RecentDays < minHistoryDays / 2 || t.PriorDays < minHistoryDays / 2 || t.PriorTotal <= 0) return 0;

        var recentAvg = t.RecentTotal / t.RecentDays;
        var priorAvg = t.PriorTotal / t.PriorDays;
        var growth = (recentAvg - priorAvg) / priorAvg * 100m;
        if (growth < growthPct) return 0;

        var evidence = JsonSerializer.Serialize(new
        {
            recentDailyAvg = Math.Round(recentAvg, 2), priorDailyAvg = Math.Round(priorAvg, 2),
            growthPct = Math.Round(growth, 1), periodDays, recentDays = t.RecentDays, priorDays = t.PriorDays,
        }, Web);
        var impact = JsonSerializer.Serialize(new
        { note = "Potentiel commercial à confirmer — pas une projection garantie (§2)." }, Web);

        return await Insert("sales_spike", rule.Key, $"{rule.Label} (+{Math.Round(growth, 1)} %)",
            $"Le chiffre d'affaires moyen quotidien est passé de {priorAvg:0.##} à {recentAvg:0.##} sur les {periodDays} derniers jours (+{growth:0.#} %).",
            evidence, impact, rule.ActionLabel, "Sécuriser le réapprovisionnement pour soutenir la croissance observée.",
            3, 2, t.RecentDays >= periodDays * 2 / 3 ? 3 : 2, t.RecentDays >= periodDays * 2 / 3 ? "high" : "medium",
            $"{t.RecentDays} jour(s) de vente sur la fenêtre récente.", $"sales/spike/{today}", today, "opportunity", ct);
    }

    private async Task<int> GenerateSalesDecline(RuleRow rule, DateOnly today, CancellationToken ct)
    {
        var threshold = JsonDocument.Parse(rule.ThresholdJson).RootElement;
        var declinePct = threshold.TryGetProperty("declinePct", out var dp) ? dp.GetDecimal() : 20m;
        var minHistoryDays = threshold.TryGetProperty("minHistoryDays", out var mh) ? mh.GetInt32() : 10;
        var periodDays = rule.PeriodDays ?? 14;

        var t = await LoadSalesTrendAsync(periodDays, today, ct);
        if (t.RecentDays < minHistoryDays / 2 || t.PriorDays < minHistoryDays / 2 || t.PriorTotal <= 0) return 0;

        var recentAvg = t.RecentTotal / t.RecentDays;
        var priorAvg = t.PriorTotal / t.PriorDays;
        var decline = (priorAvg - recentAvg) / priorAvg * 100m;
        if (decline < declinePct) return 0;

        var evidence = JsonSerializer.Serialize(new
        {
            recentDailyAvg = Math.Round(recentAvg, 2), priorDailyAvg = Math.Round(priorAvg, 2),
            declinePct = Math.Round(decline, 1), periodDays, recentDays = t.RecentDays, priorDays = t.PriorDays,
        }, Web);
        var impact = JsonSerializer.Serialize(new
        { estimatedShortfall = Math.Round((priorAvg - recentAvg) * t.RecentDays, 2) }, Web);

        return await Insert("sales_decline", rule.Key, $"{rule.Label} (-{Math.Round(decline, 1)} %)",
            $"Le chiffre d'affaires moyen quotidien est passé de {priorAvg:0.##} à {recentAvg:0.##} sur les {periodDays} derniers jours (-{decline:0.#} %).",
            evidence, impact, rule.ActionLabel, "Identifier la cause (rupture, concurrence, saisonnalité) avant d'agir.",
            4, decline >= declinePct * 2 ? 5 : 4, t.RecentDays >= periodDays * 2 / 3 ? 3 : 2,
            t.RecentDays >= periodDays * 2 / 3 ? "high" : "medium", $"{t.RecentDays} jour(s) de vente sur la fenêtre récente.",
            $"sales/decline/{today}", today, "risk", ct);
    }

    // --- §9.4 Clients : client devenu inactif (Phase 54) ---------------------

    private sealed record InactiveCustomerRow(Guid CustomerId, string Name, DateTimeOffset LastSaleAt, int TotalOrders, decimal LifetimeValue);

    private async Task<int> GenerateCustomerInactive(RuleRow rule, DateOnly today, CancellationToken ct)
    {
        var threshold = JsonDocument.Parse(rule.ThresholdJson).RootElement;
        var inactiveDays = rule.PeriodDays ?? (threshold.TryGetProperty("inactiveDays", out var id) ? id.GetInt32() : 60);
        var minPriorOrders = threshold.TryGetProperty("minPriorOrders", out var mo) ? mo.GetInt32() : 3;

        var rows = await db.Database.SqlQuery<InactiveCustomerRow>($"""
            SELECT c.id AS "CustomerId", c.name AS "Name", MAX(s.sold_at) AS "LastSaleAt",
                   COUNT(*)::int AS "TotalOrders", SUM(s.total) AS "LifetimeValue"
            FROM nexora.customer c
            JOIN nexora.sale s ON s.customer_id = c.id AND s.company_id = {Company} AND s.status = 'completed'
            WHERE c.company_id = {Company}
            GROUP BY c.id, c.name
            HAVING COUNT(*) >= {minPriorOrders} AND MAX(s.sold_at) < now() - make_interval(days => {inactiveDays})
            ORDER BY SUM(s.total) DESC LIMIT 20
            """).ToListAsync(ct);

        var created = 0;
        foreach (var r in rows)
        {
            var daysSince = (int)(clock.UtcNow - r.LastSaleAt).TotalDays;
            var evidence = JsonSerializer.Serialize(new
            {
                lastSaleAt = r.LastSaleAt, daysSinceLastSale = daysSince, totalOrders = r.TotalOrders,
                lifetimeValue = Math.Round(r.LifetimeValue, 2), inactiveThresholdDays = inactiveDays,
            }, Web);
            var impact = JsonSerializer.Serialize(new
            { note = "Valeur historique du client — pas une prévision de perte définitive (§2)." }, Web);

            created += await Insert("customer_inactive", rule.Key, $"{rule.Label} — {r.Name}",
                $"{r.Name} n'a passé aucune commande depuis {daysSince} jour(s), après {r.TotalOrders} commande(s) historiques totalisant {r.LifetimeValue:0.##}.",
                evidence, impact, rule.ActionLabel, $"Relancer {r.Name} pour ranimer la relation commerciale.",
                3, daysSince >= inactiveDays * 2 ? 4 : 3, 3, "high",
                "Basé sur l'historique de ventes enregistré, sans estimation.",
                $"customers/inactive/{r.CustomerId}", today, "risk", ct);
        }
        return created;
    }

    // --- §9.5 Fournisseurs : hausse des prix (Phase 54) ----------------------

    private sealed record SupplierPriceRow(Guid SupplierId, string SupplierName, Guid ProductId, string ProductName,
        decimal? RecentAvg, decimal? PriorAvg, int TotalOrders);

    private async Task<int> GenerateSupplierPriceIncrease(RuleRow rule, DateOnly today, CancellationToken ct)
    {
        var threshold = JsonDocument.Parse(rule.ThresholdJson).RootElement;
        var increasePct = threshold.TryGetProperty("increasePct", out var ip) ? ip.GetDecimal() : 10m;
        var minEachSide = threshold.TryGetProperty("minOrdersEachSide", out var mo) ? mo.GetInt32() : 2;

        var rows = await db.Database.SqlQuery<SupplierPriceRow>($"""
            WITH lines AS (
                SELECT po.supplier_id, sup.name AS supplier_name, pol.product_id, p.name AS product_name, pol.unit_price,
                       ROW_NUMBER() OVER (PARTITION BY po.supplier_id, pol.product_id ORDER BY po.order_date DESC) AS rn_desc,
                       COUNT(*) OVER (PARTITION BY po.supplier_id, pol.product_id) AS total_orders
                FROM nexora.purchase_order_line pol
                JOIN nexora.purchase_order po ON po.id = pol.purchase_order_id
                JOIN nexora.supplier sup ON sup.id = po.supplier_id
                JOIN nexora.product p ON p.id = pol.product_id
                WHERE po.company_id = {Company} AND po.status NOT IN ('draft', 'cancelled')
            )
            SELECT supplier_id AS "SupplierId", MAX(supplier_name) AS "SupplierName", product_id AS "ProductId",
                   MAX(product_name) AS "ProductName",
                   AVG(unit_price) FILTER (WHERE rn_desc <= {minEachSide}) AS "RecentAvg",
                   AVG(unit_price) FILTER (WHERE rn_desc > {minEachSide}) AS "PriorAvg",
                   MAX(total_orders)::int AS "TotalOrders"
            FROM lines
            GROUP BY supplier_id, product_id
            HAVING MAX(total_orders) >= {minEachSide * 2}
            """).ToListAsync(ct);

        var created = 0;
        foreach (var r in rows)
        {
            if (r.PriorAvg is not { } prior || prior <= 0 || r.RecentAvg is not { } recent) continue;
            var increase = (recent - prior) / prior * 100m;
            if (increase < increasePct) continue;

            var evidence = JsonSerializer.Serialize(new
            {
                recentAvgPrice = Math.Round(recent, 2), priorAvgPrice = Math.Round(prior, 2),
                increasePct = Math.Round(increase, 1), totalOrdersAnalyzed = r.TotalOrders,
            }, Web);
            var impact = JsonSerializer.Serialize(new
            { note = "Écart de prix moyen constaté sur les commandes enregistrées, pas une clause contractuelle (§2)." }, Web);

            created += await Insert("supplier_price_increase", rule.Key, $"{rule.Label} — {r.ProductName} ({r.SupplierName})",
                $"Le prix d'achat moyen de {r.ProductName} chez {r.SupplierName} est passé de {prior:0.##} à {recent:0.##} (+{increase:0.#} %).",
                evidence, impact, rule.ActionLabel, $"Renégocier avec {r.SupplierName} ou comparer avec d'autres fournisseurs pour {r.ProductName}.",
                3, increase >= increasePct * 2 ? 4 : 3, 3, "high",
                $"Basé sur {r.TotalOrders} commande(s) fournisseur enregistrées.",
                $"suppliers/price/{r.SupplierId}/{r.ProductId}", today, "risk", ct);
        }
        return created;
    }
}
