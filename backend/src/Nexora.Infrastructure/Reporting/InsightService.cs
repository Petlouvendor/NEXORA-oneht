using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Nexora.Application.Abstractions;
using Nexora.Application.Finance.Treasury;
using Nexora.Application.Notifications;
using Nexora.Application.Reporting;
using Nexora.Contracts.Notifications;
using Nexora.Contracts.Reporting;
using Nexora.Domain.Common;
using Nexora.Infrastructure.Persistence;

namespace Nexora.Infrastructure.Reporting;

/// <summary>
/// Insights — anomalies détectées, expliquées et actionnables (Phase 42 §37-38, §58). Chaque
/// règle est un contrôle SQL borné, déduplication par <c>(entreprise, catégorie, chemin, période)</c>
/// (§37) — jamais deux fois le même insight pour la même fenêtre. Aucune action financière n'est
/// jamais déclenchée automatiquement (§38) : seule une notification et, si assigné, une tâche.
/// </summary>
public sealed class SqlInsightService(
    NexoraDbContext db, ITenantContext tenant, IUnitOfWork uow, IDateTimeProvider clock,
    ITreasuryQueries treasury, INotificationService notifications) : IInsightService
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    private Guid Company => tenant.ActiveCompanyId ?? throw new InvalidOperationException("Entreprise active requise.");

    private sealed record Row(Guid Id, string Category, string Severity, string Title, string Detail, string Evidence,
        string? DrillPath, string? SuggestedAction, DateOnly PeriodStart, string Status, Guid? AssignedTo,
        DateTimeOffset? DueAt, DateTimeOffset CreatedAt);

    private const string Select = """
        SELECT id AS "Id", category AS "Category", severity AS "Severity", title AS "Title", detail AS "Detail",
               evidence::text AS "Evidence", drill_path AS "DrillPath", suggested_action AS "SuggestedAction",
               period_start AS "PeriodStart", status AS "Status", assigned_to AS "AssignedTo", due_at AS "DueAt",
               created_at AS "CreatedAt"
        FROM nexora.insight
        """;

    public async Task<IReadOnlyList<InsightDto>> ListAsync(string? status, CancellationToken ct = default)
    {
        var rows = status is null
            ? await db.Database.SqlQueryRaw<Row>(Select + " WHERE company_id = {0} ORDER BY created_at DESC LIMIT 200", Company).ToListAsync(ct)
            : await db.Database.SqlQueryRaw<Row>(Select + " WHERE company_id = {0} AND status = {1} ORDER BY created_at DESC LIMIT 200",
                Company, status.Trim().ToLowerInvariant()).ToListAsync(ct);
        return rows.Select(ToDto).ToList();
    }

    public async Task<Result> DismissAsync(Guid id, DismissInsightRequest request, CancellationToken ct = default)
    {
        var rows = await db.Database.ExecuteSqlAsync($"""
            UPDATE nexora.insight SET status = 'dismissed', dismissed_by = {tenant.UserId}, dismissed_at = now(),
                   dismissed_reason = {request.Reason}, updated_at = now()
            WHERE id = {id} AND company_id = {Company} AND status = 'open'
            """, ct);
        if (rows == 0) return Error.NotFound("insight.not_found", "Insight introuvable ou déjà traité.");
        await uow.SaveChangesAsync(ct);
        return Result.Success();
    }

    public async Task<Result<InsightDto>> AssignAsync(Guid id, AssignInsightRequest request, CancellationToken ct = default)
    {
        var rows = await db.Database.ExecuteSqlAsync($"""
            UPDATE nexora.insight SET status = 'actioned', assigned_to = {request.UserId}, due_at = {request.DueAt}, updated_at = now()
            WHERE id = {id} AND company_id = {Company} AND status = 'open'
            """, ct);
        if (rows == 0) return Error.NotFound("insight.not_found", "Insight introuvable ou déjà traité.");

        var saved = await db.Database.SqlQueryRaw<Row>(Select + " WHERE id = {0}", id).FirstAsync(ct);
        await notifications.NotifyAsync(new NotifyRequest(
            Category: "insight", Recipients: [request.UserId], TemplateKey: "insight.assigned",
            Variables: new Dictionary<string, string?> { ["title"] = saved.Title, ["message"] = saved.Detail },
            Source: "automation", ReferenceType: "insight", ReferenceId: id, ActionUrl: saved.DrillPath), ct);
        await uow.SaveChangesAsync(ct);
        return Result.Success(ToDto(saved));
    }

    public async Task<int> ScanAsync(CancellationToken ct = default)
    {
        var today = DateOnly.FromDateTime(clock.UtcNow.Date);
        var created = 0;
        created += await ScanRevenueDrop(today, ct);
        created += await ScanMarginDrop(today, ct);
        created += await ScanStockAnomaly(today, ct);
        created += await ScanExpenseSpike(today, ct);
        created += await ScanCashGap(today, ct);
        return created;
    }

    // --- Règles (§37) -----------------------------------------------------

    private sealed record TwoPeriodRow(decimal Current, decimal Prior);

    private async Task<int> ScanRevenueDrop(DateOnly today, CancellationToken ct)
    {
        var weekAgo = today.AddDays(-7);
        var twoWeeksAgo = today.AddDays(-14);
        var row = await db.Database.SqlQuery<TwoPeriodRow>($"""
            SELECT
              COALESCE(SUM(subtotal - discount_total) FILTER (WHERE sold_at::date > {weekAgo} AND sold_at::date <= {today}), 0)::numeric AS "Current",
              COALESCE(SUM(subtotal - discount_total) FILTER (WHERE sold_at::date > {twoWeeksAgo} AND sold_at::date <= {weekAgo}), 0)::numeric AS "Prior"
            FROM nexora.sale
            WHERE company_id = {Company} AND status = 'completed' AND sold_at::date > {twoWeeksAgo}
            """).FirstAsync(ct);
        if (row.Prior <= 0) return 0;
        var deltaPct = Math.Round((row.Current - row.Prior) / row.Prior * 100m, 1);
        if (deltaPct > -15m) return 0;

        var evidence = JsonSerializer.Serialize(new { current = row.Current, prior = row.Prior, deltaPct }, Web);
        return await InsertInsight("revenue_drop", "warning",
            $"Chiffre d'affaires en baisse de {Math.Abs(deltaPct):0.#} % sur 7 jours",
            $"Le CA des 7 derniers jours ({row.Current:0.##}) est inférieur de {Math.Abs(deltaPct):0.#} % à celui des 7 jours précédents ({row.Prior:0.##}).",
            evidence, $"revenue/{weekAgo:yyyy-MM-dd}", "Comparer les ventes par produit et par catégorie pour localiser la baisse.", weekAgo, ct);
    }

    private sealed record MarginRow(decimal RevenueCurrent, decimal CostCurrent, decimal RevenuePrior, decimal CostPrior);

    private async Task<int> ScanMarginDrop(DateOnly today, CancellationToken ct)
    {
        var weekAgo = today.AddDays(-7);
        var twoWeeksAgo = today.AddDays(-14);
        var row = await db.Database.SqlQuery<MarginRow>($"""
            SELECT
              COALESCE(SUM(sl.line_total) FILTER (WHERE s.sold_at::date > {weekAgo} AND s.sold_at::date <= {today}), 0)::numeric AS "RevenueCurrent",
              COALESCE(SUM(sl.unit_cost * sl.quantity) FILTER (WHERE s.sold_at::date > {weekAgo} AND s.sold_at::date <= {today}), 0)::numeric AS "CostCurrent",
              COALESCE(SUM(sl.line_total) FILTER (WHERE s.sold_at::date > {twoWeeksAgo} AND s.sold_at::date <= {weekAgo}), 0)::numeric AS "RevenuePrior",
              COALESCE(SUM(sl.unit_cost * sl.quantity) FILTER (WHERE s.sold_at::date > {twoWeeksAgo} AND s.sold_at::date <= {weekAgo}), 0)::numeric AS "CostPrior"
            FROM nexora.sale_line sl JOIN nexora.sale s ON s.id = sl.sale_id
            WHERE sl.company_id = {Company} AND s.status = 'completed' AND s.sold_at::date > {twoWeeksAgo}
            """).FirstAsync(ct);
        if (row.RevenueCurrent <= 0 || row.RevenuePrior <= 0) return 0;

        var currentRate = Math.Round((row.RevenueCurrent - row.CostCurrent) / row.RevenueCurrent * 100m, 1);
        var priorRate = Math.Round((row.RevenuePrior - row.CostPrior) / row.RevenuePrior * 100m, 1);
        if (priorRate - currentRate < 8m) return 0;   // seuil : au moins 8 points de marge perdus

        var evidence = JsonSerializer.Serialize(new { currentRate, priorRate }, Web);
        return await InsertInsight("margin_drop", "warning",
            $"Marge en baisse de {priorRate - currentRate:0.#} points sur 7 jours",
            $"Le taux de marge des 7 derniers jours ({currentRate:0.#} %) est inférieur de {priorRate - currentRate:0.#} points à celui des 7 jours précédents ({priorRate:0.#} %).",
            evidence, $"margin/{weekAgo:yyyy-MM-dd}", "Vérifier les remises accordées et le coût des produits vendus récemment.", weekAgo, ct);
    }

    private sealed record NegativeStockRow(int Count, string TopProducts);

    private async Task<int> ScanStockAnomaly(DateOnly today, CancellationToken ct)
    {
        var row = await db.Database.SqlQuery<NegativeStockRow>($"""
            SELECT
              (SELECT COUNT(*) FROM nexora.stock_balance WHERE company_id = {Company} AND quantity_on_hand < 0)::int AS "Count",
              COALESCE((
                SELECT string_agg(p.name || ' (' || worst.quantity_on_hand || ')', ', ')
                FROM (SELECT product_id, quantity_on_hand FROM nexora.stock_balance
                      WHERE company_id = {Company} AND quantity_on_hand < 0
                      ORDER BY quantity_on_hand LIMIT 5) worst
                JOIN nexora.product p ON p.id = worst.product_id
              ), '') AS "TopProducts"
            """).FirstAsync(ct);
        if (row.Count == 0) return 0;

        var evidence = JsonSerializer.Serialize(new { negativeLines = row.Count, sample = row.TopProducts }, Web);
        return await InsertInsight("stock_anomaly", "critical",
            $"{row.Count} ligne(s) de stock négatif",
            $"{row.Count} produit(s)/emplacement(s) affichent une quantité en stock négative — ex. {row.TopProducts}.",
            evidence, "stock/negative", "Lancer un inventaire ciblé sur les lignes en écart.", today, ct);
    }

    private sealed record ExpenseRow(decimal Current, decimal Prior);

    private async Task<int> ScanExpenseSpike(DateOnly today, CancellationToken ct)
    {
        var monthStart = new DateOnly(today.Year, today.Month, 1);
        var priorMonthStart = monthStart.AddMonths(-1);
        var row = await db.Database.SqlQuery<ExpenseRow>($"""
            SELECT
              COALESCE(SUM(amount) FILTER (WHERE expense_date >= {monthStart}), 0)::numeric AS "Current",
              COALESCE(SUM(amount) FILTER (WHERE expense_date >= {priorMonthStart} AND expense_date < {monthStart}), 0)::numeric AS "Prior"
            FROM nexora.expense
            WHERE company_id = {Company} AND status IN ('approved','paid') AND expense_date >= {priorMonthStart}
            """).FirstAsync(ct);
        if (row.Prior <= 0) return 0;
        var deltaPct = Math.Round((row.Current - row.Prior) / row.Prior * 100m, 1);
        if (deltaPct < 30m) return 0;

        var evidence = JsonSerializer.Serialize(new { current = row.Current, prior = row.Prior, deltaPct }, Web);
        return await InsertInsight("expense_spike", "warning",
            $"Dépenses en hausse de {deltaPct:0.#} % ce mois-ci",
            $"Les dépenses du mois en cours ({row.Current:0.##}) dépassent de {deltaPct:0.#} % celles du mois précédent ({row.Prior:0.##}).",
            evidence, $"expenses/{monthStart:yyyy-MM-dd}", "Passer en revue les nouvelles dépenses approuvées ce mois-ci.", monthStart, ct);
    }

    private async Task<int> ScanCashGap(DateOnly today, CancellationToken ct)
    {
        var liquidity = await treasury.LiquidityAsync(30, ct);
        if (liquidity.ProjectedGap >= 0) return 0;

        var evidence = JsonSerializer.Serialize(new
        {
            immediate = liquidity.ImmediateLiquidity, expectedReceivables = liquidity.ExpectedReceivables,
            commitments = liquidity.ShortTermCommitments, gap = liquidity.ProjectedGap,
        }, Web);
        return await InsertInsight("cash_gap", "critical",
            $"Écart de trésorerie projeté de {liquidity.ProjectedGap:0.##} à 30 jours",
            $"Liquidité immédiate {liquidity.ImmediateLiquidity:0.##} + créances attendues {liquidity.ExpectedReceivables:0.##} ne couvrent pas les engagements à court terme {liquidity.ShortTermCommitments:0.##}.",
            evidence, "treasury/liquidity", "Accélérer le recouvrement ou différer les décaissements non critiques.", today, ct);
    }

    private async Task<int> InsertInsight(string category, string severity, string title, string detail,
        string evidenceJson, string drillPath, string suggestedAction, DateOnly periodStart, CancellationToken ct)
    {
        var id = Guid.NewGuid();
        var rows = await db.Database.ExecuteSqlAsync($"""
            INSERT INTO nexora.insight
                (id, company_id, category, severity, title, detail, evidence, drill_path, suggested_action, period_start)
            VALUES ({id}, {Company}, {category}, {severity}, {title}, {detail}, {evidenceJson}::jsonb, {drillPath}, {suggestedAction}, {periodStart})
            ON CONFLICT ON CONSTRAINT insight_dedup_uk DO NOTHING
            """, ct);
        if (rows > 0) await uow.SaveChangesAsync(ct);
        return rows;
    }

    private static InsightDto ToDto(Row r) => new(r.Id, r.Category, r.Severity, r.Title, r.Detail,
        JsonDocument.Parse(r.Evidence).RootElement, r.DrillPath, r.SuggestedAction, r.PeriodStart, r.Status,
        r.AssignedTo, r.DueAt, r.CreatedAt);
}
