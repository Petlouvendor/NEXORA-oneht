using Microsoft.EntityFrameworkCore;
using Nexora.Application.Abstractions;
using Nexora.Application.Reporting;
using Nexora.Contracts.Reporting;
using Nexora.Domain.Common;
using Nexora.Infrastructure.Persistence;

namespace Nexora.Infrastructure.Reporting;

/// <summary>
/// Objectifs & score de performance (Phase 42 §39-40) — active <c>kpi_target</c> (V026). Le score
/// pondère l'écart de chaque objectif actif par son poids ; <c>direction=min</c> (KPI à atteindre,
/// ex. CA) évalue <c>actual/target</c>, <c>direction=max</c> (plafond, ex. dépenses) évalue
/// <c>target/actual</c> — dans les deux cas, ≥100 % signifie l'objectif tenu (§40).
/// </summary>
public sealed class SqlObjectiveService(NexoraDbContext db, ITenantContext tenant, IUnitOfWork uow,
    IKpiService kpi, IDateTimeProvider clock, IKpiGovernanceService kpiCatalog, IPermissionService permissions) : IObjectiveService
{
    private Guid Company => tenant.ActiveCompanyId ?? throw TenantErrors.ActiveCompanyRequired();

    private sealed record Row(Guid Id, Guid CompanyId, Guid? EstablishmentId, string KpiKey, string PeriodType,
        decimal TargetValue, string Direction, decimal Weight, DateOnly EffectiveFrom, DateOnly? EffectiveTo, string Status);

    private const string Select = """
        SELECT id AS "Id", company_id AS "CompanyId", establishment_id AS "EstablishmentId", kpi_key AS "KpiKey",
               period_type AS "PeriodType", target_value AS "TargetValue", direction AS "Direction",
               weight AS "Weight", effective_from AS "EffectiveFrom", effective_to AS "EffectiveTo", status AS "Status"
        FROM nexora.kpi_target
        """;

    public async Task<IReadOnlyList<ObjectiveDto>> ListAsync(CancellationToken ct = default)
    {
        var rows = await db.Database.SqlQueryRaw<Row>(Select + " WHERE company_id = {0} ORDER BY kpi_key, effective_from DESC", Company).ToListAsync(ct);
        // Un objectif de CA ou de marge révèle l'indicateur : mêmes règles de visibilité que les KPI (guide §18).
        var granted = await permissions.CurrentPermissionsAsync(tenant, ct);
        return rows.Select(ToDto).Where(o => KpiCatalog.IsVisible(o.KpiKey, granted)).ToList();
    }

    public async Task<Result<ObjectiveDto>> SetAsync(SetObjectiveRequest request, CancellationToken ct = default)
    {
        var kpiKey = request.KpiKey.Trim().ToLowerInvariant();
        if ((await kpiCatalog.GetAsync(kpiKey, ct)).IsSuccess is false)
            return Error.Validation("objective.kpi_key", "KPI inconnu du catalogue gouverné.");
        var periodType = request.PeriodType.Trim().ToLowerInvariant();
        if (periodType is not ("day" or "week" or "month" or "quarter" or "year"))
            return Error.Validation("objective.period_type", "Période invalide.");
        var direction = request.Direction.Trim().ToLowerInvariant();
        if (direction is not ("min" or "max")) return Error.Validation("objective.direction", "Direction invalide (min/max).");
        if (request.Weight <= 0) return Error.Validation("objective.weight", "Le poids doit être positif.");
        if (request.TargetValue < 0) return Error.Validation("objective.target_value", "La cible ne peut pas être négative.");

        var effectiveFrom = request.EffectiveFrom ?? DateOnly.FromDateTime(clock.UtcNow.Date);
        var id = request.Id ?? Guid.NewGuid();
        await db.Database.ExecuteSqlAsync($"""
            INSERT INTO nexora.kpi_target
                (id, company_id, establishment_id, kpi_key, period_type, target_value, direction, weight,
                 effective_from, effective_to, status)
            VALUES ({id}, {Company}, {request.EstablishmentId}, {kpiKey}, {periodType}, {request.TargetValue},
                    {direction}, {request.Weight}, {effectiveFrom}, {request.EffectiveTo}, 'active')
            ON CONFLICT ON CONSTRAINT kpi_target_uk DO UPDATE SET
                target_value = EXCLUDED.target_value, direction = EXCLUDED.direction, weight = EXCLUDED.weight,
                effective_to = EXCLUDED.effective_to, status = 'active', updated_at = now()
            """, ct);
        await uow.SaveChangesAsync(ct);

        var saved = await db.Database.SqlQueryRaw<Row>(
            Select + " WHERE company_id = {0} AND establishment_id IS NOT DISTINCT FROM {1} AND kpi_key = {2} AND period_type = {3} AND effective_from = {4}",
            Company, (object?)request.EstablishmentId ?? DBNull.Value, kpiKey, periodType, effectiveFrom).FirstAsync(ct);
        return Result.Success(ToDto(saved));
    }

    public async Task<Result> DeleteAsync(Guid id, CancellationToken ct = default)
    {
        var rows = await db.Database.ExecuteSqlAsync($"DELETE FROM nexora.kpi_target WHERE id = {id} AND company_id = {Company}", ct);
        if (rows == 0) return Error.NotFound("objective.not_found", "Objectif introuvable.");
        await uow.SaveChangesAsync(ct);
        return Result.Success();
    }

    public async Task<ObjectiveScoreDto> ScoreAsync(CancellationToken ct = default)
    {
        var today = DateOnly.FromDateTime(clock.UtcNow.Date);
        var active = (await db.Database.SqlQueryRaw<Row>(
            Select + " WHERE company_id = {0} AND status = 'active' AND effective_from <= {1} AND (effective_to IS NULL OR effective_to >= {1})",
            Company, today).ToListAsync(ct));

        var granted = await permissions.CurrentPermissionsAsync(tenant, ct);
        active = active.Where(a => KpiCatalog.IsVisible(a.KpiKey, granted)).ToList();
        var items = new List<ObjectiveScoreItemDto>();
        foreach (var group in active.GroupBy(a => a.PeriodType))
        {
            var (from, to) = PeriodRange(group.Key, today);
            var cc = await kpi.CommandCenterAsync(new ReportFilter { From = from, To = to }, ct);
            foreach (var target in group)
            {
                var actualKpi = cc.Kpis.FirstOrDefault(k => k.Key == target.KpiKey);
                var actual = actualKpi?.Value ?? 0m;
                var ratio = target.Direction == "max"
                    ? (actual <= 0 ? 100m : Math.Round(target.TargetValue / actual * 100m, 1))
                    : (target.TargetValue <= 0 ? 100m : Math.Round(actual / target.TargetValue * 100m, 1));
                items.Add(new ObjectiveScoreItemDto(target.KpiKey, actualKpi?.Label ?? target.KpiKey, target.TargetValue, actual, ratio, target.Weight));
            }
        }

        if (items.Count == 0) return new ObjectiveScoreDto(0, "E", items, clock.UtcNow);

        var totalWeight = items.Sum(i => i.Weight);
        var score = Math.Round(items.Sum(i => Math.Min(i.RatioPct, 150m) * i.Weight) / totalWeight, 1);
        var grade = score switch { >= 110 => "A", >= 100 => "B", >= 85 => "C", >= 70 => "D", _ => "E" };
        return new ObjectiveScoreDto(score, grade, items, clock.UtcNow);
    }

    private static (DateOnly From, DateOnly To) PeriodRange(string periodType, DateOnly today) => periodType switch
    {
        "day" => (today, today),
        "week" => (today.AddDays(-(((int)today.DayOfWeek + 6) % 7)), today),   // lundi ISO (le dimanche compris)
        "quarter" => (new DateOnly(today.Year, ((today.Month - 1) / 3) * 3 + 1, 1), today),
        "year" => (new DateOnly(today.Year, 1, 1), today),
        _ => (new DateOnly(today.Year, today.Month, 1), today),   // month
    };

    private static ObjectiveDto ToDto(Row r) =>
        new(r.Id, r.CompanyId, r.EstablishmentId, r.KpiKey, r.PeriodType, r.TargetValue, r.Direction, r.Weight, r.EffectiveFrom, r.EffectiveTo, r.Status);
}
