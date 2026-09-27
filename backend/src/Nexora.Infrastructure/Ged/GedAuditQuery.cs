using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Nexora.Application.Abstractions;
using Nexora.Application.Ged;
using Nexora.Contracts.Ged;
using Nexora.Infrastructure.Persistence;

namespace Nexora.Infrastructure.Ged;

/// <summary>
/// Observabilité GED (Phase 45 §45) — ne fait que lire <c>audit_log</c> (immuable, Phase 11),
/// filtré aux tables de ce module (<c>trg_audit_*</c>, V058). Aucune table dupliquée.
/// </summary>
public sealed class SqlGedAuditQuery(NexoraDbContext db, ITenantContext tenant) : IGedAuditQuery
{
    private static readonly string[] Tables =
    [
        "document", "document_version", "document_link", "document_share",
        "document_signature_request", "document_signer", "document_retention_policy",
    ];

    private Guid Company => tenant.ActiveCompanyId ?? throw new InvalidOperationException("Entreprise active requise.");

    private sealed record Row(Guid Id, string EntityType, string Action, string? Changes, Guid? UserId, DateTimeOffset OccurredAt);

    public async Task<IReadOnlyList<GedAuditEntryDto>> RecentAsync(Guid? documentId, int limit, CancellationToken ct = default)
    {
        var take = Math.Clamp(limit, 1, 500);
        var rows = documentId is null
            ? await db.Database.SqlQueryRaw<Row>("""
                SELECT id AS "Id", entity_type AS "EntityType", action AS "Action", new_values::text AS "Changes",
                       user_id AS "UserId", occurred_at AS "OccurredAt"
                FROM nexora.audit_log WHERE company_id = {0} AND entity_type = ANY({1}) ORDER BY occurred_at DESC LIMIT {2}
                """, Company, Tables, take).ToListAsync(ct)
            : await db.Database.SqlQueryRaw<Row>("""
                SELECT id AS "Id", entity_type AS "EntityType", action AS "Action", new_values::text AS "Changes",
                       user_id AS "UserId", occurred_at AS "OccurredAt"
                FROM nexora.audit_log WHERE company_id = {0} AND entity_id = {1} AND entity_type = ANY({2}) ORDER BY occurred_at DESC LIMIT {3}
                """, Company, documentId, Tables, take).ToListAsync(ct);
        return rows.Select(r => new GedAuditEntryDto(
            r.Id, r.EntityType, r.Action, r.Changes is null ? null : JsonDocument.Parse(r.Changes).RootElement, r.UserId, r.OccurredAt)).ToList();
    }
}
