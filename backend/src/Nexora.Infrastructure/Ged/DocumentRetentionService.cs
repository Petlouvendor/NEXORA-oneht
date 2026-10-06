using Microsoft.EntityFrameworkCore;
using Nexora.Application.Abstractions;
using Nexora.Application.Ged;
using Nexora.Contracts.Ged;
using Nexora.Domain.Common;
using Nexora.Infrastructure.Persistence;

namespace Nexora.Infrastructure.Ged;

/// <summary>
/// Rétention DÉDIÉE aux documents métier (Phase 45 §27-29) — distincte de
/// <c>Operations.SqlRetentionPurgeService</c> (Phase 41), dont l'allowlist ferme explicitement la
/// porte à toute donnée métier/comptable. Un document sous <c>legal_hold</c> ou dont la rétention
/// n'est pas échue n'est jamais listé comme éligible, quelle que soit la politique générique.
/// </summary>
public sealed class SqlDocumentRetentionService(
    NexoraDbContext db, ITenantContext tenant, IUnitOfWork uow, IDateTimeProvider clock, IGedService ged) : IDocumentRetentionService
{
    private Guid Company => tenant.ActiveCompanyId ?? throw TenantErrors.ActiveCompanyRequired();

    private sealed record PolicyRow(Guid Id, Guid? CompanyId, string? DocumentType, int RetentionYears, string LegalBasis);

    public async Task<IReadOnlyList<DocumentRetentionPolicyDto>> ListPoliciesAsync(CancellationToken ct = default)
    {
        var rows = await db.Database.SqlQueryRaw<PolicyRow>("""
            SELECT id AS "Id", company_id AS "CompanyId", document_type AS "DocumentType",
                   retention_years AS "RetentionYears", legal_basis AS "LegalBasis"
            FROM nexora.document_retention_policy
            WHERE company_id = {0} OR company_id IS NULL
            ORDER BY document_type NULLS LAST, company_id NULLS LAST
            """, Company).ToListAsync(ct);
        return rows.Select(r => new DocumentRetentionPolicyDto(r.Id, r.CompanyId is null, r.DocumentType, r.RetentionYears, r.LegalBasis)).ToList();
    }

    public async Task<Result<DocumentRetentionPolicyDto>> SavePolicyAsync(SaveDocumentRetentionPolicyRequest request, CancellationToken ct = default)
    {
        if (request.RetentionYears <= 0) return Error.Validation("retention.years", "Durée invalide.");
        var type = string.IsNullOrWhiteSpace(request.DocumentType) ? null : request.DocumentType.Trim().ToLowerInvariant();

        await db.Database.ExecuteSqlAsync($"""
            INSERT INTO nexora.document_retention_policy (id, company_id, document_type, retention_years, legal_basis, created_by)
            VALUES ({Guid.NewGuid()}, {Company}, {type}, {request.RetentionYears}, {request.LegalBasis}, {tenant.UserId})
            ON CONFLICT ON CONSTRAINT document_retention_policy_uk DO UPDATE SET
                retention_years = EXCLUDED.retention_years, legal_basis = EXCLUDED.legal_basis, updated_at = now()
            """, ct);
        await uow.SaveChangesAsync(ct);

        var saved = await db.Database.SqlQueryRaw<PolicyRow>("""
            SELECT id AS "Id", company_id AS "CompanyId", document_type AS "DocumentType",
                   retention_years AS "RetentionYears", legal_basis AS "LegalBasis"
            FROM nexora.document_retention_policy WHERE company_id = {0} AND document_type IS NOT DISTINCT FROM {1}
            """, Company, (object?)type ?? DBNull.Value).FirstAsync(ct);
        return Result.Success(new DocumentRetentionPolicyDto(saved.Id, saved.CompanyId is null, saved.DocumentType, saved.RetentionYears, saved.LegalBasis));
    }

    public async Task<Result> SetLegalHoldAsync(Guid documentId, bool hold, string? reason, CancellationToken ct = default)
    {
        if (hold && string.IsNullOrWhiteSpace(reason))
            return Error.Validation("legal_hold.reason", "Motif requis pour poser un legal hold (§29).");

        var rows = await db.Database.ExecuteSqlAsync($"""
            UPDATE nexora.document SET legal_hold = {hold}, legal_hold_reason = {(hold ? reason : null)}, updated_at = now()
            WHERE id = {documentId} AND company_id = {Company}
            """, ct);
        if (rows == 0) return Error.NotFound("document.not_found", "Document introuvable.");
        await uow.SaveChangesAsync(ct);
        return Result.Success();
    }

    public async Task<IReadOnlyList<GedDocumentDto>> ListEligibleForDeletionAsync(CancellationToken ct = default)
    {
        var today = DateOnly.FromDateTime(clock.UtcNow.UtcDateTime);
        var ids = await db.Database.SqlQueryRaw<Guid>("""
            SELECT id AS "Value" FROM nexora.document
            WHERE company_id = {0} AND status = 'archived' AND legal_hold = false
              AND retention_until IS NOT NULL AND retention_until <= {1}
            ORDER BY retention_until
            """, Company, today).ToListAsync(ct);

        var result = new List<GedDocumentDto>();
        foreach (var id in ids)
        {
            var doc = await ged.GetAsync(id, ct);
            if (doc.IsSuccess) result.Add(doc.Value!);
        }
        return result;
    }

    public async Task<int> ApplyRetentionAsync(CancellationToken ct = default)
    {
        var rows = await db.Database.ExecuteSqlAsync($"""
            UPDATE nexora.document d SET retention_until = (
                d.created_at::date + (
                    SELECT p.retention_years FROM nexora.document_retention_policy p
                    WHERE (p.company_id = d.company_id OR p.company_id IS NULL)
                      AND (p.document_type = d.document_type OR p.document_type IS NULL)
                    ORDER BY (p.company_id IS NOT NULL) DESC, (p.document_type IS NOT NULL) DESC
                    LIMIT 1
                ) * INTERVAL '1 year'
            )::date, updated_at = now()
            WHERE d.company_id = {Company} AND d.retention_until IS NULL
            """, ct);
        if (rows > 0) await uow.SaveChangesAsync(ct);
        return rows;
    }
}
