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
    private Guid Company => tenant.ActiveCompanyId ?? throw new InvalidOperationException("Entreprise active requise.");

    private sealed record Row(Guid Id, Guid? TechnicianUserId, Guid? CustomerId, string EquipmentDescription,
        string Checklist, string PartsUsed, DateTimeOffset? ScheduledAt, DateTimeOffset? StartedAt,
        DateTimeOffset? CompletedAt, int? TimeSpentMinutes, string Status, Guid? SignatureAttachmentId, string ReportText);

    private const string Select = """
        SELECT id AS "Id", technician_user_id AS "TechnicianUserId", customer_id AS "CustomerId",
               equipment_description AS "EquipmentDescription", checklist::text AS "Checklist", parts_used::text AS "PartsUsed",
               scheduled_at AS "ScheduledAt", started_at AS "StartedAt", completed_at AS "CompletedAt",
               time_spent_minutes AS "TimeSpentMinutes", status AS "Status",
               signature_attachment_id AS "SignatureAttachmentId", report_text AS "ReportText"
        FROM nexora.field_intervention
        """;

    public async Task<Result<FieldInterventionDto>> CreateAsync(CreateInterventionRequest request, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(request.EquipmentDescription))
            return Error.Validation("field_intervention.equipment", "Description de l'équipement requise.");

        var id = Guid.NewGuid();
        await db.Database.ExecuteSqlAsync($"""
            INSERT INTO nexora.field_intervention (id, company_id, technician_user_id, customer_id, equipment_description, scheduled_at)
            VALUES ({id}, {Company}, {request.TechnicianUserId}, {request.CustomerId}, {request.EquipmentDescription}, {request.ScheduledAt})
            """, ct);
        await uow.SaveChangesAsync(ct);
        return await GetAsync(id, ct);
    }

    public async Task<IReadOnlyList<FieldInterventionDto>> ListAsync(string? status, CancellationToken ct = default)
    {
        var rows = status is null
            ? await db.Database.SqlQueryRaw<Row>(Select + " WHERE company_id = {0} ORDER BY scheduled_at NULLS LAST, created_at DESC LIMIT 200", Company).ToListAsync(ct)
            : await db.Database.SqlQueryRaw<Row>(Select + " WHERE company_id = {0} AND status = {1} ORDER BY scheduled_at NULLS LAST, created_at DESC LIMIT 200",
                Company, status.Trim().ToLowerInvariant()).ToListAsync(ct);
        return rows.Select(ToDto).ToList();
    }

    public async Task<IReadOnlyList<FieldInterventionDto>> MineAsync(CancellationToken ct = default)
    {
        var rows = await db.Database.SqlQueryRaw<Row>(Select + """
             WHERE company_id = {0} AND technician_user_id = {1} AND status IN ('scheduled','in_progress')
             ORDER BY scheduled_at NULLS LAST, created_at
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
        if (request.TimeSpentMinutes < 0) return Error.Validation("field_intervention.time", "Temps passé invalide.");

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
                   signature_attachment_id = {request.SignatureAttachmentId}, completed_at = now(), updated_at = now()
            WHERE id = {id} AND company_id = {Company}
            """, ct);
        await uow.SaveChangesAsync(ct);
        return await GetAsync(id, ct);
    }

    private async Task<Result<FieldInterventionDto>> GetAsync(Guid id, CancellationToken ct)
    {
        var row = await db.Database.SqlQueryRaw<Row>(Select + " WHERE id = {0} AND company_id = {1}", id, Company).FirstOrDefaultAsync(ct);
        return row is null ? Error.NotFound("field_intervention.not_found", "Intervention introuvable.") : Result.Success(ToDto(row));
    }

    private static FieldInterventionDto ToDto(Row r) => new(r.Id, r.TechnicianUserId, r.CustomerId, r.EquipmentDescription,
        JsonDocument.Parse(r.Checklist).RootElement, JsonDocument.Parse(r.PartsUsed).RootElement, r.ScheduledAt, r.StartedAt,
        r.CompletedAt, r.TimeSpentMinutes, r.Status, r.SignatureAttachmentId, r.ReportText);
}
