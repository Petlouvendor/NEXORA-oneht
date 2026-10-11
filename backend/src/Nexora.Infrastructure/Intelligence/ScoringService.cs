using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Nexora.Application.Abstractions;
using Nexora.Application.Credit;
using Nexora.Application.Intelligence;
using Nexora.Contracts.Intelligence;
using Nexora.Infrastructure.Persistence;
using Nexora.Domain.Common;

namespace Nexora.Infrastructure.Intelligence;

/// <summary>
/// Scoring explicable (Phase 43 §35) — trois scores concrets, chacun avec ses variables visibles
/// (<c>explanation</c>) : aucune boîte noire. <c>level</c> exprime uniformément un NIVEAU DE RISQUE
/// (high = risque élevé), même quand <c>value</c> porte une grandeur différente selon le score
/// (jours de couverture, score 0-100, % de fiabilité) — le sens exact est documenté dans
/// <c>explanation.meaning</c> de chaque entrée.
/// </summary>
public sealed class SqlScoringService(NexoraDbContext db, ITenantContext tenant, IUnitOfWork uow, ICreditPortfolioQueries credit) : IScoringService
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);
    private Guid Company => tenant.ActiveCompanyId ?? throw TenantErrors.ActiveCompanyRequired();

    private sealed record Row(Guid EntityId, string? EntityLabel, string EntityType, string ScoreKey,
        decimal Value, string Level, string Explanation, DateTimeOffset ComputedAt);

    private const string Select = """
        SELECT es.entity_id AS "EntityId",
               CASE es.entity_type WHEN 'product' THEN p.name WHEN 'customer' THEN c.name WHEN 'supplier' THEN s.name END AS "EntityLabel",
               es.entity_type AS "EntityType", es.score_key AS "ScoreKey", es.value AS "Value", es.level AS "Level",
               es.explanation::text AS "Explanation", es.computed_at AS "ComputedAt"
        FROM nexora.entity_score es
        LEFT JOIN nexora.product  p ON es.entity_type = 'product'  AND p.id = es.entity_id
        LEFT JOIN nexora.customer c ON es.entity_type = 'customer' AND c.id = es.entity_id
        LEFT JOIN nexora.supplier s ON es.entity_type = 'supplier' AND s.id = es.entity_id
        """;

    public async Task<IReadOnlyList<EntityScoreDto>> ListAsync(string? entityType, CancellationToken ct = default)
    {
        var rows = entityType is null
            ? await db.Database.SqlQueryRaw<Row>(Select + " WHERE es.company_id = {0} ORDER BY es.value DESC", Company).ToListAsync(ct)
            : await db.Database.SqlQueryRaw<Row>(Select + " WHERE es.company_id = {0} AND es.entity_type = {1} ORDER BY es.value DESC",
                Company, entityType.Trim().ToLowerInvariant()).ToListAsync(ct);
        return rows.Select(r => new EntityScoreDto(r.EntityId, r.EntityLabel ?? "—", r.EntityType, r.ScoreKey, r.Value, r.Level,
            JsonDocument.Parse(r.Explanation).RootElement, r.ComputedAt)).ToList();
    }

    public async Task<int> ComputeAsync(CancellationToken ct = default)
    {
        var count = 0;
        count += await ComputeStockCoverage(ct);
        count += await ComputeCreditRisk(ct);
        count += await ComputeSupplierReliability(ct);
        return count;
    }

    // --- score_stock : jours de couverture (§35) -----------------------------

    private sealed record StockRow(Guid ProductId, decimal OnHand, decimal TotalQty);

    private async Task<int> ComputeStockCoverage(CancellationToken ct)
    {
        var rows = await db.Database.SqlQuery<StockRow>($"""
            SELECT p.id AS "ProductId", SUM(sb.quantity_on_hand) AS "OnHand", COALESCE(v.total_qty, 0) AS "TotalQty"
            FROM nexora.product p
            JOIN nexora.stock_balance sb ON sb.product_id = p.id
            LEFT JOIN LATERAL (
                SELECT SUM(sl.quantity) total_qty FROM nexora.sale_line sl JOIN nexora.sale s ON s.id = sl.sale_id
                WHERE sl.product_id = p.id AND s.company_id = {Company} AND s.status = 'completed'
                  AND s.sold_at::date > current_date - 60
            ) v ON true
            WHERE p.company_id = {Company}
            GROUP BY p.id, v.total_qty
            HAVING SUM(sb.quantity_on_hand) > 0
            """).ToListAsync(ct);

        var count = 0;
        foreach (var r in rows)
        {
            var velocity = r.TotalQty / 60m;
            if (velocity <= 0) continue;   // pas de vélocité connue => pas de couverture calculable
            var coverageDays = Math.Round(r.OnHand / velocity, 1);
            var level = coverageDays < 7 ? "high" : coverageDays < 21 ? "medium" : "low";
            var explanation = JsonSerializer.Serialize(new
            {
                onHand = r.OnHand, velocityPerDay = Math.Round(velocity, 2), coverageDays,
                meaning = "value = jours de couverture ; level = risque de rupture (high = risque élevé)",
            }, Web);
            count += await Upsert("product", r.ProductId, "stock_coverage", coverageDays, level, explanation, ct);
        }
        return count;
    }

    // --- score_risque_creance : ancienneté + montant + litiges + blocage (§35) --

    private async Task<int> ComputeCreditRisk(CancellationToken ct)
    {
        var rows = await credit.CollectionsAsync(onlyOverdue: false, ct);
        var count = 0;
        foreach (var c in rows)
        {
            if (c.TotalOutstanding <= 0) continue;
            var agingWeight = c.OldestOverdueDays switch { > 90 => 40m, > 60 => 30m, > 30 => 20m, > 0 => 10m, _ => 0m };
            var disputeWeight = c.OpenDisputes > 0 ? 15m : 0m;
            var blockedWeight = c.Blocked ? 25m : 0m;
            var amountWeight = Math.Min(20m, c.TotalOutstanding / 1000m * 5m);
            var score = Math.Min(100m, agingWeight + disputeWeight + blockedWeight + amountWeight);
            var level = score >= 60 ? "high" : score >= 30 ? "medium" : "low";
            var explanation = JsonSerializer.Serialize(new
            {
                oldestOverdueDays = c.OldestOverdueDays, openDisputes = c.OpenDisputes, blocked = c.Blocked,
                totalOutstanding = c.TotalOutstanding, meaning = "value = score de risque 0-100 (plus élevé = plus risqué)",
            }, Web);
            count += await Upsert("customer", c.CustomerId, "credit_risk", Math.Round(score, 1), level, explanation, ct);
        }
        return count;
    }

    // --- score_fournisseur : livraison à temps + factures sans écart (§35) -----

    private sealed record SupplierRow(Guid SupplierId, decimal OnTimeRatio, decimal CleanMatchRatio, int ReceiptCount);

    private async Task<int> ComputeSupplierReliability(CancellationToken ct)
    {
        var rows = await db.Database.SqlQuery<SupplierRow>($"""
            SELECT s.id AS "SupplierId", COALESCE(ot.on_time_ratio, 1) AS "OnTimeRatio",
                   COALESCE(mi.clean_ratio, 1) AS "CleanMatchRatio", COALESCE(ot.receipt_count, 0)::int AS "ReceiptCount"
            FROM nexora.supplier s
            LEFT JOIN LATERAL (
                SELECT COUNT(*) receipt_count,
                       AVG(CASE WHEN gr.received_at::date <= po.expected_date THEN 1 ELSE 0 END) on_time_ratio
                FROM nexora.goods_receipt gr JOIN nexora.purchase_order po ON po.id = gr.purchase_order_id
                WHERE gr.supplier_id = s.id AND gr.company_id = {Company} AND gr.status = 'posted' AND po.expected_date IS NOT NULL
            ) ot ON true
            LEFT JOIN LATERAL (
                SELECT AVG(CASE WHEN si.match_status = 'matched' THEN 1 ELSE 0 END) clean_ratio
                FROM nexora.supplier_invoice si WHERE si.supplier_id = s.id AND si.company_id = {Company} AND si.match_status IS NOT NULL
            ) mi ON true
            WHERE s.company_id = {Company}
            """).ToListAsync(ct);

        var count = 0;
        foreach (var r in rows)
        {
            if (r.ReceiptCount == 0) continue;   // aucune réception rapprochée à une commande => pas de score fiable
            var reliabilityPct = Math.Round((r.OnTimeRatio * 0.6m + r.CleanMatchRatio * 0.4m) * 100m, 1);
            var level = reliabilityPct < 60 ? "high" : reliabilityPct < 85 ? "medium" : "low";
            var explanation = JsonSerializer.Serialize(new
            {
                onTimeRatioPct = Math.Round(r.OnTimeRatio * 100, 1), cleanMatchRatioPct = Math.Round(r.CleanMatchRatio * 100, 1),
                receiptsAnalyzed = r.ReceiptCount,
                meaning = "value = fiabilité % (plus élevé = plus fiable) ; level = risque d'être peu fiable",
            }, Web);
            count += await Upsert("supplier", r.SupplierId, "supplier_reliability", reliabilityPct, level, explanation, ct);
        }
        return count;
    }

    private async Task<int> Upsert(string entityType, Guid entityId, string scoreKey, decimal value, string level,
        string explanationJson, CancellationToken ct)
    {
        var rows = await db.Database.ExecuteSqlAsync($"""
            INSERT INTO nexora.entity_score (id, company_id, entity_type, entity_id, score_key, value, level, explanation, computed_at)
            VALUES ({Guid.NewGuid()}, {Company}, {entityType}, {entityId}, {scoreKey}, {value}, {level}, {explanationJson}::jsonb, now())
            ON CONFLICT ON CONSTRAINT entity_score_uk DO UPDATE SET
                value = EXCLUDED.value, level = EXCLUDED.level, explanation = EXCLUDED.explanation, computed_at = EXCLUDED.computed_at
            """, ct);
        if (rows > 0) await uow.SaveChangesAsync(ct);
        return rows;
    }
}
