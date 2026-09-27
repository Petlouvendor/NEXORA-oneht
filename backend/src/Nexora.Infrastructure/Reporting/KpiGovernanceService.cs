using Microsoft.EntityFrameworkCore;
using Nexora.Application.Abstractions;
using Nexora.Application.Reporting;
using Nexora.Contracts.Reporting;
using Nexora.Domain.Common;
using Nexora.Infrastructure.Persistence;

namespace Nexora.Infrastructure.Reporting;

/// <summary>
/// Catalogue KPI gouverné (Phase 42 §48, §50) — table plateforme <c>kpi_definition</c>, amorcée
/// depuis <see cref="KpiCatalog"/>. Une définition unique pour tout NEXORA ONE (pas de RLS,
/// comme <c>job_definition</c>) : lecture seule dans cette phase.
/// </summary>
public sealed class SqlKpiGovernanceService(NexoraDbContext db, ITenantContext tenant) : IKpiGovernanceService
{
    private sealed record Row(Guid Id, string Key, string Label, string Category, string Unit, string Source,
        string Formula, string Kind, string Owner, int Version, DateTimeOffset UpdatedAt);

    private const string Select = """
        SELECT id AS "Id", key AS "Key", label AS "Label", category AS "Category", unit AS "Unit",
               source AS "Source", formula AS "Formula", kind AS "Kind", owner AS "Owner",
               version AS "Version", updated_at AS "UpdatedAt"
        FROM nexora.kpi_definition
        """;

    public async Task<IReadOnlyList<KpiDefinitionDto>> ListAsync(CancellationToken ct = default)
    {
        using var _ = tenant.BeginSystemScope();
        var rows = await db.Database.SqlQueryRaw<Row>(Select + " ORDER BY category, key").ToListAsync(ct);
        return rows.Select(ToDto).ToList();
    }

    public async Task<Result<KpiDefinitionDto>> GetAsync(string key, CancellationToken ct = default)
    {
        using var _ = tenant.BeginSystemScope();
        var row = await db.Database.SqlQueryRaw<Row>(Select + " WHERE key = {0}", key.Trim().ToLowerInvariant()).FirstOrDefaultAsync(ct);
        return row is null
            ? Error.NotFound("kpi.not_found", "KPI inconnu.")
            : Result.Success(ToDto(row));
    }

    private static KpiDefinitionDto ToDto(Row r) => new(r.Id, r.Key, r.Label, r.Category, r.Unit, r.Source, r.Formula, r.Kind, r.Owner, r.Version, r.UpdatedAt);
}
