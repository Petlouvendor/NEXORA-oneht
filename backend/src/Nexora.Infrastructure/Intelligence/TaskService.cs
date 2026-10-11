using Microsoft.EntityFrameworkCore;
using Nexora.Application.Abstractions;
using Nexora.Application.Intelligence;
using Nexora.Contracts.Intelligence;
using Nexora.Domain.Common;
using Nexora.Infrastructure.Persistence;

namespace Nexora.Infrastructure.Intelligence;

/// <summary>
/// Tâche générique (Phase 43 §31) — <c>task</c> (V056), comble un manque réel : <c>group_task</c>
/// (V028) exige un groupe, <c>onboarding_task</c> (V052) est une checklist fixe. Reliée le cas
/// échéant à une recommandation via <c>reference_type</c>/<c>reference_id</c>, comme <c>notification</c>.
/// </summary>
public sealed class SqlTaskService(NexoraDbContext db, ITenantContext tenant, IUnitOfWork uow) : ITaskService
{
    private Guid Company => tenant.ActiveCompanyId ?? throw TenantErrors.ActiveCompanyRequired();

    private sealed record Row(Guid Id, string Title, string Description, string? ReferenceType, Guid? ReferenceId,
        Guid? AssignedTo, string Priority, string Status, DateTimeOffset? DueAt, DateTimeOffset CreatedAt);

    private const string Select = """
        SELECT id AS "Id", title AS "Title", description AS "Description", reference_type AS "ReferenceType",
               reference_id AS "ReferenceId", assigned_to AS "AssignedTo", priority AS "Priority", status AS "Status",
               due_at AS "DueAt", created_at AS "CreatedAt"
        FROM nexora.task
        """;

    public async Task<IReadOnlyList<TaskDto>> ListAsync(string? status, CancellationToken ct = default)
    {
        var rows = status is null
            ? await db.Database.SqlQueryRaw<Row>(
                Select + " WHERE company_id = {0} ORDER BY due_at NULLS LAST, created_at DESC LIMIT 200", Company).ToListAsync(ct)
            : await db.Database.SqlQueryRaw<Row>(
                Select + " WHERE company_id = {0} AND status = {1} ORDER BY due_at NULLS LAST, created_at DESC LIMIT 200",
                Company, status.Trim().ToLowerInvariant()).ToListAsync(ct);
        return rows.Select(ToDto).ToList();
    }

    public async Task<Result<TaskDto>> CreateAsync(CreateTaskRequest request, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(request.Title)) return Error.Validation("task.title", "Titre requis.");

        var id = Guid.NewGuid();
        await db.Database.ExecuteSqlAsync($"""
            INSERT INTO nexora.task (id, company_id, title, description, reference_type, reference_id, assigned_to, priority, due_at, created_by)
            VALUES ({id}, {Company}, {request.Title}, {request.Description ?? ""}, {request.ReferenceType}, {request.ReferenceId},
                    {request.AssignedTo}, {request.Priority}, {request.DueAt}, {tenant.UserId})
            """, ct);
        await uow.SaveChangesAsync(ct);
        return await GetAsync(id, ct);
    }

    public async Task<Result<TaskDto>> CompleteAsync(Guid id, CancellationToken ct = default)
    {
        var rows = await db.Database.ExecuteSqlAsync($"""
            UPDATE nexora.task SET status = 'done', updated_at = now() WHERE id = {id} AND company_id = {Company} AND status <> 'done'
            """, ct);
        if (rows == 0) return Error.NotFound("task.not_found", "Tâche introuvable ou déjà terminée.");
        await uow.SaveChangesAsync(ct);
        return await GetAsync(id, ct);
    }

    private async Task<Result<TaskDto>> GetAsync(Guid id, CancellationToken ct)
    {
        var row = await db.Database.SqlQueryRaw<Row>(Select + " WHERE id = {0} AND company_id = {1}", id, Company).FirstOrDefaultAsync(ct);
        return row is null ? Error.NotFound("task.not_found", "Tâche introuvable.") : Result.Success(ToDto(row));
    }

    private static TaskDto ToDto(Row r) =>
        new(r.Id, r.Title, r.Description, r.ReferenceType, r.ReferenceId, r.AssignedTo, r.Priority, r.Status, r.DueAt, r.CreatedAt);
}
