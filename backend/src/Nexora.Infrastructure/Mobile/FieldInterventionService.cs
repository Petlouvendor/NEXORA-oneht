using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Nexora.Application.Abstractions;
using Nexora.Application.Mobile;
using Nexora.Contracts.Mobile;
using Nexora.Domain.Common;
using Nexora.Infrastructure.Persistence;

namespace Nexora.Infrastructure.Mobile;

/// <summary>
/// Interventions techniciens (Phase 44 §13) — comble un manque réel : aucune fiche d'intervention
/// n'existait avant cette phase (<c>support_ticket</c>, Phase 38, est le support NEXORA↔abonné,
/// sans rapport avec le technicien terrain d'une entreprise cliente).
/// </summary>
public sealed class SqlFieldInterventionService(NexoraDbContext db, ITenantContext tenant, IUnitOfWork uow) : IFieldInterventionService
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);
    private Guid Company => tenant.ActiveCompanyId ?? throw TenantErrors.ActiveCompanyRequired();

    private sealed record Row(Guid Id, Guid? TechnicianUserId, Guid? CustomerId, string EquipmentDescription,
        string Checklist, string PartsUsed, DateTimeOffset? ScheduledAt, DateTimeOffset? StartedAt,
        DateTimeOffset? CompletedAt, int? TimeSpentMinutes, string Status, Guid? SignatureAttachmentId, string ReportText,
        string? CustomerName, string? TechnicianName, string? CompletedByName);

    // Alias « i » : les filtres des appelants sont qualifiés (i.company_id, i.status…).
    private const string Select = """
        SELECT i.id AS "Id", i.technician_user_id AS "TechnicianUserId", i.customer_id AS "CustomerId",
               i.equipment_description AS "EquipmentDescription", i.checklist::text AS "Checklist", i.parts_used::text AS "PartsUsed",
               i.scheduled_at AS "ScheduledAt", i.started_at AS "StartedAt", i.completed_at AS "CompletedAt",
               i.time_spent_minutes AS "TimeSpentMinutes", i.status AS "Status",
               i.signature_attachment_id AS "SignatureAttachmentId", i.report_text AS "ReportText",
               c.name AS "CustomerName", t.full_name AS "TechnicianName", cb.full_name AS "CompletedByName"
        FROM nexora.field_intervention i
        LEFT JOIN nexora.customer c ON c.id = i.customer_id
        LEFT JOIN nexora.app_user t ON t.id = i.technician_user_id
        LEFT JOIN nexora.app_user cb ON cb.id = i.completed_by
        """;

    public async Task<Result<FieldInterventionDto>> CreateAsync(CreateInterventionRequest request, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(request.EquipmentDescription) || request.EquipmentDescription.Trim().Length < 3)
            return Error.Validation("field_intervention.equipment", "Décrivez l'équipement ou le motif de l'intervention.");
        if (request.EquipmentDescription.Length > 300) return Error.Validation("field_intervention.equipment", "300 caractères au plus.");
        if (request.TechnicianUserId is { } tech
            && !(await Nexora.Infrastructure.Customers.CustomerAccountRules.HoldersAsync(db, tenant, Application.Security.Permissions.FieldOps.Manage, null, ct)).Contains(tech))
            return Error.Validation("field_intervention.technician", "Ce technicien n'a pas accès au terrain dans cette entreprise.");
        if (request.CustomerId is { } cid && !await db.Database.SqlQuery<bool>($"""
                SELECT EXISTS(SELECT 1 FROM nexora.customer WHERE id = {cid} AND company_id = {Company}) AS "Value"
                """).FirstAsync(ct))
            return Error.Validation("field_intervention.customer", "Client introuvable.");

        var id = Guid.NewGuid();
        await db.Database.ExecuteSqlAsync($"""
            INSERT INTO nexora.field_intervention (id, company_id, technician_user_id, customer_id, equipment_description, scheduled_at)
            VALUES ({id}, {Company}, {request.TechnicianUserId}, {request.CustomerId}, {request.EquipmentDescription.Trim()}, {request.ScheduledAt})
            """, ct);
        await uow.SaveChangesAsync(ct);
        return await GetAsync(id, ct);
    }

    public async Task<IReadOnlyList<FieldInterventionDto>> ListAsync(string? status, CancellationToken ct = default)
    {
        var rows = status is null
            ? await db.Database.SqlQueryRaw<Row>(Select + " WHERE i.company_id = {0} ORDER BY i.scheduled_at NULLS LAST, i.created_at DESC LIMIT 200", Company).ToListAsync(ct)
            : await db.Database.SqlQueryRaw<Row>(Select + " WHERE i.company_id = {0} AND i.status = {1} ORDER BY i.scheduled_at NULLS LAST, i.created_at DESC LIMIT 200",
                Company, status.Trim().ToLowerInvariant()).ToListAsync(ct);
        return rows.Select(ToDto).ToList();
    }

    public async Task<IReadOnlyList<FieldInterventionDto>> MineAsync(CancellationToken ct = default)
    {
        var rows = await db.Database.SqlQueryRaw<Row>(Select + """
             WHERE i.company_id = {0} AND i.technician_user_id = {1}
               AND (i.status IN ('scheduled','in_progress') OR (i.status = 'completed' AND i.completed_at >= current_date))
             ORDER BY i.scheduled_at NULLS LAST, i.created_at
            """, Company, (object?)tenant.UserId ?? DBNull.Value).ToListAsync(ct);
        return rows.Select(ToDto).ToList();
    }

    public async Task<Result<FieldInterventionDto>> StartAsync(Guid id, CancellationToken ct = default)
    {
        var rows = await db.Database.ExecuteSqlAsync($"""
            UPDATE nexora.field_intervention SET status = 'in_progress', started_at = now(), updated_at = now()
            WHERE id = {id} AND company_id = {Company} AND status = 'scheduled'
            """, ct);
        if (rows == 0) return Error.NotFound("field_intervention.not_found", "Intervention introuvable ou déjà démarrée.");
        await uow.SaveChangesAsync(ct);
        return await GetAsync(id, ct);
    }

    public async Task<Result<FieldInterventionDto>> CompleteAsync(Guid id, CompleteInterventionRequest request, CancellationToken ct = default)
    {
        if (request.TimeSpentMinutes is < 0 or > 24 * 60) return Error.Validation("field_intervention.time", "Temps passé invalide (0 à 24 heures).");
        // Preuve d'intervention : un compte rendu ; la signature du client est facultative mais vérifiée.
        if (string.IsNullOrWhiteSpace(request.ReportText) || request.ReportText.Trim().Length < 3)
            return Error.Validation("field_intervention.report", "Compte rendu d'intervention requis.");
        if (request.ReportText.Length > 4000) return Error.Validation("field_intervention.report", "Compte rendu limité à 4 000 caractères.");
        if ((request.Checklist ?? []).Any(c => string.IsNullOrWhiteSpace(c.Label)))
            return Error.Validation("field_intervention.checklist", "Chaque point de contrôle a un libellé.");
        if (request.SignatureAttachmentId is { } sig && await db.Database.SqlQuery<string?>($"""
                SELECT kind AS "Value" FROM nexora.attachment WHERE id = {sig} AND company_id = {Company}
                """).FirstOrDefaultAsync(ct) != "signature")
            return Error.Validation("field_intervention.signature", "Signature introuvable ou d'un autre type.");

        var existing = await db.Database.SqlQuery<string>($"""
            SELECT status AS "Value" FROM nexora.field_intervention WHERE id = {id} AND company_id = {Company}
            """).FirstOrDefaultAsync(ct);
        if (existing is null) return Error.NotFound("field_intervention.not_found", "Intervention introuvable.");
        if (existing is "completed" or "cancelled") return Error.Conflict("field_intervention.already_closed", "Intervention déjà close.");

        var checklistJson = JsonSerializer.Serialize(request.Checklist, Web);
        var partsJson = JsonSerializer.Serialize(request.PartsUsed, Web);
        await db.Database.ExecuteSqlAsync($"""
            UPDATE nexora.field_intervention SET status = 'completed', checklist = {checklistJson}::jsonb, parts_used = {partsJson}::jsonb,
                   time_spent_minutes = {request.TimeSpentMinutes}, report_text = {request.ReportText},
                   signature_attachment_id = {request.SignatureAttachmentId}, completed_at = now(), completed_by = {tenant.UserId},
                   -- Hors ligne, « démarrer » n'a pas pu être envoyé : le début se déduit du temps passé.
                   started_at = COALESCE(started_at, now() - make_interval(mins => {request.TimeSpentMinutes})),
                   updated_at = now()
            WHERE id = {id} AND company_id = {Company}
            """, ct);
        await uow.SaveChangesAsync(ct);
        return await GetAsync(id, ct);
    }

    private async Task<Result<FieldInterventionDto>> GetAsync(Guid id, CancellationToken ct)
    {
        var row = await db.Database.SqlQueryRaw<Row>(Select + " WHERE i.id = {0} AND i.company_id = {1}", id, Company).FirstOrDefaultAsync(ct);
        return row is null ? Error.NotFound("field_intervention.not_found", "Intervention introuvable.") : Result.Success(ToDto(row));
    }

    private static FieldInterventionDto ToDto(Row r) => new(r.Id, r.TechnicianUserId, r.CustomerId, r.EquipmentDescription,
        JsonDocument.Parse(r.Checklist).RootElement, JsonDocument.Parse(r.PartsUsed).RootElement, r.ScheduledAt, r.StartedAt,
        r.CompletedAt, r.TimeSpentMinutes, r.Status, r.SignatureAttachmentId, r.ReportText, r.CustomerName, r.TechnicianName, r.CompletedByName);
}
