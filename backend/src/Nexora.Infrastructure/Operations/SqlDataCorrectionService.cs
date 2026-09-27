using Microsoft.EntityFrameworkCore;
using Nexora.Application.Abstractions;
using Nexora.Application.Operations;
using Nexora.Contracts.Operations;
using Nexora.Domain.Common;
using Nexora.Infrastructure.Persistence;

namespace Nexora.Infrastructure.Operations;

/// <summary>
/// Correction de donnée contrôlée (Phase 41 §44, §51) : jamais d'<c>UPDATE</c> direct — la liste des
/// couples table/champ corrigeables est fermée et codée en dur (aucune saisie libre d'identifiant
/// SQL). Demandeur ≠ approbateur (séparation des responsabilités, comme le workflow Phase 11).
/// Outil d'administration transverse : un titulaire de <c>operations.correct_data</c> voit toutes les
/// demandes, quelle que soit l'entreprise porteuse — comme le reste de la console d'exploitation
/// (Phase 37 : déploiements, Go-Live).
/// </summary>
public sealed class SqlDataCorrectionService(NexoraDbContext db, ITenantContext tenant, IUnitOfWork uow) : IDataCorrectionService
{
    private static readonly Dictionary<string, string[]> Allowlist = new()
    {
        ["app_user"] = ["full_name", "username"],
        ["customer"] = ["name", "phone", "email", "address", "tax_identifier"],
        ["supplier"] = ["name", "phone", "email", "address", "tax_identifier"],
        ["product"] = ["name", "short_name", "description", "barcode"],
    };
    private static readonly HashSet<string> CompanyScopedTables = ["customer", "supplier", "product"];

    private sealed record RequestRow(Guid Id, string TableName, Guid RecordId, string FieldName, string? OldValue,
        string NewValue, string Reason, string Status, Guid RequestedBy, Guid? ApprovedBy, string? Notes,
        DateTimeOffset? AppliedAt, DateTimeOffset CreatedAt);
    private sealed record CurrentValueRow(Guid? CompanyId, string? Value);

    private const string Select = """
        SELECT id AS "Id", table_name AS "TableName", record_id AS "RecordId", field_name AS "FieldName",
               old_value AS "OldValue", new_value AS "NewValue", reason AS "Reason", status AS "Status",
               requested_by AS "RequestedBy", approved_by AS "ApprovedBy", notes AS "Notes",
               applied_at AS "AppliedAt", created_at AS "CreatedAt"
        FROM nexora.data_correction_request
        """;

    public async Task<Result<DataCorrectionDto>> RequestAsync(RequestDataCorrectionRequest request, CancellationToken ct = default)
    {
        var table = request.TableName.Trim().ToLowerInvariant();
        if (!Allowlist.TryGetValue(table, out var fields)) return Error.Validation("correction.table", "Table non corrigeable.");
        var field = request.FieldName.Trim().ToLowerInvariant();
        if (!fields.Contains(field)) return Error.Validation("correction.field", "Champ non corrigeable pour cette table.");
        if (string.IsNullOrWhiteSpace(request.Reason)) return Error.Validation("correction.reason", "Motif requis.");
        if (string.IsNullOrWhiteSpace(request.NewValue)) return Error.Validation("correction.new_value", "Nouvelle valeur requise.");

        using var _ = tenant.BeginSystemScope();
        var companyExpr = CompanyScopedTables.Contains(table) ? "company_id" : "NULL::uuid";
        var lookupSql = $"SELECT {companyExpr} AS \"CompanyId\", {field}::text AS \"Value\" FROM nexora.{table} WHERE id = {{0}}";
        var current = await db.Database.SqlQueryRaw<CurrentValueRow>(lookupSql, request.RecordId).FirstOrDefaultAsync(ct);
        if (current is null) return Error.NotFound("correction.record_not_found", "Enregistrement introuvable.");

        var id = Guid.NewGuid();
        await db.Database.ExecuteSqlAsync($"""
            INSERT INTO nexora.data_correction_request
                (id, table_name, record_id, field_name, old_value, new_value, reason, requested_by, company_id)
            VALUES ({id}, {table}, {request.RecordId}, {field}, {current.Value}, {request.NewValue}, {request.Reason},
                    {tenant.UserId}, {current.CompanyId})
            """, ct);
        await uow.SaveChangesAsync(ct);
        return Result.Success((await GetAsync(id, ct))!);
    }

    public async Task<Result<DataCorrectionDto>> DecideAsync(Guid id, DecideDataCorrectionRequest decision, CancellationToken ct = default)
    {
        using var _ = tenant.BeginSystemScope();
        var row = await db.Database.SqlQueryRaw<RequestRow>(Select + " WHERE id = {0}", id).FirstOrDefaultAsync(ct);
        if (row is null) return Error.NotFound("correction.not_found", "Demande introuvable.");
        if (row.Status != "requested") return Error.Conflict("correction.already_decided", "Cette demande a déjà été traitée.");
        if (row.RequestedBy == tenant.UserId)
            return Error.Forbidden("correction.four_eyes", "Le demandeur ne peut pas décider de sa propre demande.");

        if (!decision.Approve)
        {
            await db.Database.ExecuteSqlAsync($"""
                UPDATE nexora.data_correction_request
                SET status = 'rejected', approved_by = {tenant.UserId}, notes = {decision.Notes}, updated_at = now()
                WHERE id = {id}
                """, ct);
            await uow.SaveChangesAsync(ct);
            return Result.Success((await GetAsync(id, ct))!);
        }

        if (!Allowlist.TryGetValue(row.TableName, out var fields) || !fields.Contains(row.FieldName))
            return Error.Validation("correction.field", "Champ non corrigeable (politique modifiée depuis la demande).");

        // table_name/field_name viennent d'une ligne dont les DEUX valeurs ont déjà été validées
        // contre la liste fermée ci-dessus au moment de la demande et à l'instant — jamais de saisie
        // libre d'identifiant SQL (§44).
        var updateSql = $"UPDATE nexora.{row.TableName} SET {row.FieldName} = {{0}}, updated_at = now() WHERE id = {{1}}";
        await uow.ExecuteInTransactionAsync(async token =>
        {
            await db.Database.ExecuteSqlRawAsync(updateSql, [row.NewValue, row.RecordId], token);
            await db.Database.ExecuteSqlAsync($"""
                UPDATE nexora.data_correction_request
                SET status = 'applied', approved_by = {tenant.UserId}, applied_at = now(), notes = {decision.Notes}, updated_at = now()
                WHERE id = {id}
                """, token);
            return 0;
        }, ct);

        return Result.Success((await GetAsync(id, ct))!);
    }

    public async Task<IReadOnlyList<DataCorrectionDto>> ListAsync(string? status, CancellationToken ct = default)
    {
        using var _ = tenant.BeginSystemScope();
        var rows = status is null
            ? await db.Database.SqlQueryRaw<RequestRow>(Select + " ORDER BY created_at DESC LIMIT 200").ToListAsync(ct)
            : await db.Database.SqlQueryRaw<RequestRow>(Select + " WHERE status = {0} ORDER BY created_at DESC LIMIT 200", status).ToListAsync(ct);
        return rows.Select(ToDto).ToList();
    }

    private async Task<DataCorrectionDto?> GetAsync(Guid id, CancellationToken ct)
    {
        using var _ = tenant.BeginSystemScope();
        var row = await db.Database.SqlQueryRaw<RequestRow>(Select + " WHERE id = {0}", id).FirstOrDefaultAsync(ct);
        return row is null ? null : ToDto(row);
    }

    private static DataCorrectionDto ToDto(RequestRow r) => new(r.Id, r.TableName, r.RecordId, r.FieldName, r.OldValue,
        r.NewValue, r.Reason, r.Status, r.RequestedBy, r.ApprovedBy, r.Notes, r.AppliedAt, r.CreatedAt);
}
