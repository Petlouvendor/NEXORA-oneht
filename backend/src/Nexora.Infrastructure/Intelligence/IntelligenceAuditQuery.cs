using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Nexora.Application.Abstractions;
using Nexora.Application.Intelligence;
using Nexora.Contracts.Intelligence;
using Nexora.Infrastructure.Persistence;

namespace Nexora.Infrastructure.Intelligence;

/// <summary>
/// Observabilité IA (Phase 43 §38) — ne fait que lire <c>audit_log</c> (immuable, Phase 11),
/// filtré aux tables de ce module (<c>trg_audit_*</c>, V056). Aucune table dupliquée : l'audit
/// reste la source unique de vérité pour « qui a fait quoi, quand ».
/// </summary>
public sealed class SqlIntelligenceAuditQuery(NexoraDbContext db, ITenantContext tenant) : IIntelligenceAuditQuery
{
    private Guid Company => tenant.ActiveCompanyId ?? throw new InvalidOperationException("Entreprise active requise.");

    private sealed record Row(Guid Id, string EntityType, string Action, string? Changes, Guid? UserId, DateTimeOffset OccurredAt);

    public async Task<IReadOnlyList<IntelligenceAuditEntryDto>> RecentAsync(int limit, CancellationToken ct = default)
    {
        var take = Math.Clamp(limit, 1, 500);
        var rows = await db.Database.SqlQuery<Row>($"""
            SELECT id AS "Id", entity_type AS "EntityType", action AS "Action",
                   new_values::text AS "Changes", user_id AS "UserId", occurred_at AS "OccurredAt"
            FROM nexora.audit_log
            WHERE company_id = {Company}
              AND entity_type IN ('recommendation','intelligence_rule','entity_score','task')
            ORDER BY occurred_at DESC
            LIMIT {take}
            """).ToListAsync(ct);
        return rows.Select(r => new IntelligenceAuditEntryDto(
            r.Id, r.EntityType, r.Action, r.Changes is null ? null : JsonDocument.Parse(r.Changes).RootElement, r.UserId, r.OccurredAt)).ToList();
    }
}
