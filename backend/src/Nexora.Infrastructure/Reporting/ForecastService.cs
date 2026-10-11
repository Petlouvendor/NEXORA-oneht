using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Nexora.Application.Abstractions;
using Nexora.Application.Finance.Treasury;
using Nexora.Application.Reporting;
using Nexora.Contracts.Reporting;
using Nexora.Domain.Common;
using Nexora.Infrastructure.Persistence;

namespace Nexora.Infrastructure.Reporting;

/// <summary>
/// Prévisions (Phase 42 §35-36) — projection simple et transparente, toujours présentée comme une
/// <b>estimation</b> (§35) : méthode et hypothèses explicites, niveau de confiance dérivé de la
/// profondeur d'historique disponible. <c>cash</c> délègue à la prévision de trésorerie déjà
/// livrée en Phase 28 (<see cref="ITreasuryQueries.MaturityForecastAsync"/>) plutôt que de
/// dupliquer une troisième implémentation parallèle du même calcul.
/// </summary>
public sealed class SqlForecastService(
    NexoraDbContext db, ITenantContext tenant, IUnitOfWork uow,
    ITreasuryQueries treasury, IStockReports stock, IBusinessCalendar calendar) : IForecastService
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);
    private static readonly string[] Metrics = ["revenue", "cash", "stock", "receivables", "expenses"];
    private static readonly string[] Scenarios = ["base", "optimistic", "prudent", "custom"];

    private Guid Company => tenant.ActiveCompanyId ?? throw TenantErrors.ActiveCompanyRequired();

    private sealed record Row(Guid Id, string Metric, string Method, string Scenario, decimal AdjustmentPct,
        int HorizonDays, string ConfidenceLevel, string Assumptions, string Points, DateTimeOffset GeneratedAt);

    private const string Select = """
        SELECT id AS "Id", metric AS "Metric", method AS "Method", scenario AS "Scenario",
               adjustment_pct AS "AdjustmentPct", horizon_days AS "HorizonDays", confidence_level AS "ConfidenceLevel",
               assumptions AS "Assumptions", points::text AS "Points", generated_at AS "GeneratedAt"
        FROM nexora.forecast
        """;

    public async Task<IReadOnlyList<ForecastDto>> ListAsync(string? metric, CancellationToken ct = default)
    {
        var rows = metric is null
            ? await db.Database.SqlQueryRaw<Row>(Select + " WHERE company_id = {0} ORDER BY generated_at DESC LIMIT 50", Company).ToListAsync(ct)
            : await db.Database.SqlQueryRaw<Row>(Select + " WHERE company_id = {0} AND metric = {1} ORDER BY generated_at DESC LIMIT 50",
                Company, metric.Trim().ToLowerInvariant()).ToListAsync(ct);
        return rows.Select(ToDto).ToList();
    }

    public async Task<Result<ForecastDto>> GenerateAsync(GenerateForecastRequest request, CancellationToken ct = default)
    {
        var metric = request.Metric.Trim().ToLowerInvariant();
        if (!Metrics.Contains(metric)) return Error.Validation("forecast.metric", "Indicateur invalide.");
        var scenario = request.Scenario.Trim().ToLowerInvariant();
        if (!Scenarios.Contains(scenario)) return Error.Validation("forecast.scenario", "Scénario invalide.");
        if (request.HorizonDays is < 1 or > 365) return Error.Validation("forecast.horizon", "Horizon invalide (1-365 jours).");
        if (scenario == "custom" && request.AdjustmentPct is null)
            return Error.Validation("forecast.adjustment_pct", "Un scénario personnalisé requiert un pourcentage d'ajustement.");

        var adjustmentPct = scenario switch
        {
            "optimistic" => 10m,
            "prudent" => -10m,
            "custom" => request.AdjustmentPct!.Value,
            _ => 0m,
        };

        var (method, confidence, assumptions, points) = metric == "cash"
            ? await FromTreasuryForecast(ct)
            : await ProjectFromHistory(metric, request.HorizonDays, adjustmentPct, ct);

        var id = Guid.NewGuid();
        var pointsJson = JsonSerializer.Serialize(points, Web);
        await db.Database.ExecuteSqlAsync($"""
            INSERT INTO nexora.forecast
                (id, company_id, metric, method, scenario, adjustment_pct, horizon_days, confidence_level,
                 assumptions, points, created_by)
            VALUES ({id}, {Company}, {metric}, {method}, {scenario}, {adjustmentPct}, {request.HorizonDays},
                    {confidence}, {assumptions}, {pointsJson}::jsonb, {tenant.UserId})
            """, ct);
        await uow.SaveChangesAsync(ct);

        var saved = await db.Database.SqlQueryRaw<Row>(Select + " WHERE id = {0}", id).FirstAsync(ct);
        return Result.Success(ToDto(saved));
    }

    /// <summary>Moyenne mobile sur l'historique récent, projetée en ligne plate ajustée du scénario —
    /// jamais un modèle statistique sophistiqué : la transparence prime (§35).</summary>
    private async Task<(string Method, string Confidence, string Assumptions, List<ForecastPointDto>)> ProjectFromHistory(
        string metric, int horizonDays, decimal adjustmentPct, CancellationToken ct)
    {
        if (metric == "stock")
        {
            var valuation = await stock.ValuationAsync(new Contracts.Reporting.ReportFilter(), ct);
            var flat = valuation.TotalValue * (1 + adjustmentPct / 100m);
            var today = calendar.Today;
            var pts = Enumerable.Range(1, horizonDays).Select(d => new ForecastPointDto(today.AddDays(d), flat)).ToList();
            return ("manual", "low",
                "Projection à plat de la valeur de stock actuelle, ajustée du scénario — aucune tendance historique de valorisation disponible.",
                pts);
        }

        var (table, dateCol, amountCol, statusFilter) = metric switch
        {
            "revenue" => ("sale", "sold_at", "(subtotal - discount_total)", "status = 'completed'"),
            "expenses" => ("expense", "expense_date", "amount", "status IN ('approved','paid')"),
            "receivables" => ("customer_receivable", "issue_date", "original_amount", "true"),
            _ => throw new InvalidOperationException("Indicateur non projetable par historique."),
        };

        var sql = $"SELECT {dateCol}::date AS \"Day\", COALESCE(SUM({amountCol}), 0)::numeric AS \"Value\" " +
                  $"FROM nexora.{table} WHERE company_id = {{0}} AND {statusFilter} " +
                  $"AND {dateCol} >= now() - interval '60 days' GROUP BY {dateCol}::date";
        var history = await db.Database.SqlQueryRaw<HistoryRow>(sql, Company).ToListAsync(ct);

        var daysWithData = history.Count;
        var dailyAverage = daysWithData == 0 ? 0m : history.Sum(h => h.Value) / Math.Max(daysWithData, 1);
        var adjusted = dailyAverage * (1 + adjustmentPct / 100m);
        var confidence = daysWithData >= 45 ? "high" : daysWithData >= 14 ? "medium" : "low";

        var todayDate = calendar.Today;
        var points = Enumerable.Range(1, horizonDays).Select(d => new ForecastPointDto(todayDate.AddDays(d), Math.Round(adjusted, 2))).ToList();
        var assumptions = $"Moyenne mobile sur {daysWithData} jour(s) d'historique (60 derniers jours), projetée à plat sur {horizonDays} jour(s), ajustement de scénario {adjustmentPct:+0.##;-0.##;0}%.";
        return ("moving_average", confidence, assumptions, points);
    }

    private async Task<(string, string, string, List<ForecastPointDto>)> FromTreasuryForecast(CancellationToken ct)
    {
        var cf = await treasury.MaturityForecastAsync(ct);
        var today = calendar.Today;
        var points = cf.Buckets.Select(b => new ForecastPointDto(today.AddDays(b.DayTo), b.ProjectedBalance)).ToList();
        return ("manual", "medium",
            "Reprend la prévision de trésorerie par échéance (Phase 28) : encaissements/décaissements attendus depuis les créances, dettes et flux en attente déjà enregistrés — pas de nouvelle hypothèse statistique.",
            points);
    }

    private sealed record HistoryRow(DateOnly Day, decimal Value);

    private static ForecastDto ToDto(Row r) => new(r.Id, r.Metric, r.Method, r.Scenario, r.AdjustmentPct, r.HorizonDays,
        r.ConfidenceLevel, r.Assumptions, JsonSerializer.Deserialize<List<ForecastPointDto>>(r.Points, Web) ?? [], r.GeneratedAt);
}
