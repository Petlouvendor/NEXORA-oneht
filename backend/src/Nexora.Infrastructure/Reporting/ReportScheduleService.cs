using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Nexora.Application.Abstractions;
using Nexora.Application.Notifications;
using Nexora.Application.Reporting;
using Nexora.Contracts.Notifications;
using Nexora.Contracts.Reporting;
using Nexora.Domain.Common;
using Nexora.Infrastructure.Persistence;

namespace Nexora.Infrastructure.Reporting;

/// <summary>
/// Rapports planifiés (Phase 42 §41) — active <c>report_schedule</c>/<c>report_execution</c>
/// (V026). La livraison passe par <see cref="INotificationService"/> — un destinataire non
/// résolu en utilisateur NEXORA (adresse e-mail brute, canal externe non branché) est journalisé
/// dans <c>report_execution.error</c> mais n'interrompt pas la livraison aux autres.
/// </summary>
public sealed class SqlReportScheduleService(
    NexoraDbContext db, ITenantContext tenant, IUnitOfWork uow, IDateTimeProvider clock,
    IReportCatalogService catalog, INotificationService notifications) : IReportScheduleService
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);
    private static readonly string[] Frequencies = ["daily", "weekly", "monthly", "on_close"];
    private static readonly string[] Formats = ["csv", "xlsx", "pdf"];

    private Guid Company => tenant.ActiveCompanyId ?? throw TenantErrors.ActiveCompanyRequired();

    private sealed record Row(Guid Id, string Name, string ReportKey, string Filter, string Frequency, string Format,
        string Recipients, string Status, DateTimeOffset? LastRunAt, DateTimeOffset? NextRunAt);

    private const string Select = """
        SELECT id AS "Id", name AS "Name", report_key AS "ReportKey", filter::text AS "Filter",
               frequency AS "Frequency", format AS "Format", recipients::text AS "Recipients",
               status AS "Status", last_run_at AS "LastRunAt", next_run_at AS "NextRunAt"
        FROM nexora.report_schedule
        """;

    public async Task<IReadOnlyList<ReportScheduleDto>> ListAsync(CancellationToken ct = default)
    {
        var rows = await db.Database.SqlQueryRaw<Row>(Select + " WHERE company_id = {0} ORDER BY name", Company).ToListAsync(ct);
        return rows.Select(ToDto).ToList();
    }

    public async Task<Result<ReportScheduleDto>> SaveAsync(SaveReportScheduleRequest request, CancellationToken ct = default)
    {
        var frequency = request.Frequency.Trim().ToLowerInvariant();
        if (!Frequencies.Contains(frequency)) return Error.Validation("schedule.frequency", "Fréquence invalide.");
        var format = request.Format.Trim().ToLowerInvariant();
        if (!Formats.Contains(format)) return Error.Validation("schedule.format", "Format invalide.");
        if (string.IsNullOrWhiteSpace(request.Name)) return Error.Validation("schedule.name", "Nom requis.");
        if (request.Recipients.Count == 0) return Error.Validation("schedule.recipients", "Au moins un destinataire requis.");
        // Destinataires : des utilisateurs de l'entreprise (audit 2026-10 éd. 2 : identifiants non vérifiés).
        var recipientIds = request.Recipients.Select(r => Guid.TryParse(r, out var g) ? g : Guid.Empty).ToList();
        if (recipientIds.Contains(Guid.Empty)
            || !await Nexora.Infrastructure.Common.CompanyMembers.AreMembersAsync(db, Company, recipientIds, ct))
            return Error.Validation("schedule.recipients", "Chaque destinataire doit être un utilisateur de cette entreprise.");

        var filterJson = (request.Filter ?? JsonDocument.Parse("{}").RootElement).GetRawText();
        var recipientsJson = JsonSerializer.Serialize(request.Recipients, Web);
        var nextRun = NextRunAt(frequency, clock.UtcNow);

        Guid id;
        if (request.Id is { } existingId)
        {
            id = existingId;
            var updated = await db.Database.ExecuteSqlAsync($"""
                UPDATE nexora.report_schedule
                SET name = {request.Name}, report_key = {request.ReportKey}, frequency = {frequency},
                    filter = {filterJson}::jsonb, format = {format}, recipients = {recipientsJson}::jsonb, updated_at = now()
                WHERE id = {existingId} AND company_id = {Company}
                """, ct);
            if (updated == 0) return Error.NotFound("schedule.not_found", "Rapport planifié introuvable.");
        }
        else
        {
            id = Guid.NewGuid();
            await db.Database.ExecuteSqlAsync($"""
                INSERT INTO nexora.report_schedule
                    (id, company_id, name, report_key, frequency, filter, format, recipients, next_run_at, created_by)
                VALUES ({id}, {Company}, {request.Name}, {request.ReportKey}, {frequency}, {filterJson}::jsonb,
                        {format}, {recipientsJson}::jsonb, {nextRun}, {tenant.UserId})
                """, ct);
        }
        await uow.SaveChangesAsync(ct);

        var saved = await db.Database.SqlQueryRaw<Row>(Select + " WHERE id = {0}", id).FirstAsync(ct);
        return Result.Success(ToDto(saved));
    }

    public async Task<Result> DeleteAsync(Guid id, CancellationToken ct = default)
    {
        var rows = await db.Database.ExecuteSqlAsync($"DELETE FROM nexora.report_schedule WHERE id = {id} AND company_id = {Company}", ct);
        if (rows == 0) return Error.NotFound("schedule.not_found", "Rapport planifié introuvable.");
        await uow.SaveChangesAsync(ct);
        return Result.Success();
    }

    private sealed record ExecRow(Guid Id, Guid? ReportScheduleId, string ReportKey, string Format, string Status,
        int? RowCount, string? Error, DateTimeOffset StartedAt, DateTimeOffset? FinishedAt);

    public async Task<IReadOnlyList<ReportExecutionDto>> HistoryAsync(CancellationToken ct = default)
    {
        var rows = await db.Database.SqlQuery<ExecRow>($"""
            SELECT id AS "Id", report_schedule_id AS "ReportScheduleId", report_key AS "ReportKey", format AS "Format",
                   status AS "Status", row_count AS "RowCount", error AS "Error", started_at AS "StartedAt", finished_at AS "FinishedAt"
            FROM nexora.report_execution WHERE company_id = {Company} ORDER BY started_at DESC LIMIT 100
            """).ToListAsync(ct);
        return rows.Select(r => new ReportExecutionDto(r.Id, r.ReportScheduleId, r.ReportKey, r.Format, r.Status, r.RowCount, r.Error, r.StartedAt, r.FinishedAt)).ToList();
    }

    public async Task<int> RunDueAsync(CancellationToken ct = default)
    {
        var due = await db.Database.SqlQueryRaw<Row>(
            Select + " WHERE company_id = {0} AND status = 'active' AND (next_run_at IS NULL OR next_run_at <= now())", Company)
            .ToListAsync(ct);

        var delivered = 0;
        foreach (var s in due)
        {
            var executionId = Guid.NewGuid();
            await db.Database.ExecuteSqlAsync($"""
                INSERT INTO nexora.report_execution (id, company_id, report_schedule_id, report_key, filter, format, status, started_at)
                VALUES ({executionId}, {Company}, {s.Id}, {s.ReportKey}, {s.Filter}::jsonb, {s.Format}, 'running', now())
                """, ct);

            string status = "succeeded";
            int? rowCount = null;
            string? error = null;
            try
            {
                var result = await catalog.RunByKeyAsync(s.ReportKey, s.Filter, ct);
                if (!result.IsSuccess) throw new InvalidOperationException(result.Error!.Message);
                rowCount = CountRows(result.Value);

                var recipients = JsonSerializer.Deserialize<List<string>>(s.Recipients, Web) ?? [];
                var userIds = recipients.Where(r => Guid.TryParse(r, out _)).Select(Guid.Parse).ToList();
                if (userIds.Count > 0)
                {
                    await notifications.NotifyAsync(new NotifyRequest(
                        Category: "report", Recipients: userIds, TemplateKey: "report.delivered",
                        Variables: new Dictionary<string, string?>
                        {
                            ["title"] = $"Rapport « {s.Name} » disponible",
                            ["message"] = $"Le rapport planifié « {s.Name} » ({s.ReportKey}) vient d'être généré ({rowCount} ligne(s)).",
                        },
                        Source: "automation", ReferenceType: "report_execution", ReferenceId: executionId), ct);
                }
                if (recipients.Count > userIds.Count)
                    error = $"{recipients.Count - userIds.Count} destinataire(s) non résolu(s) en utilisateur NEXORA (canal externe non branché).";
                delivered++;
            }
            catch (Exception ex)
            {
                status = "failed";
                error = ex.Message;
            }

            await db.Database.ExecuteSqlAsync($"""
                UPDATE nexora.report_execution SET status = {status}, row_count = {rowCount}, error = {error}, finished_at = now()
                WHERE id = {executionId}
                """, ct);
            await db.Database.ExecuteSqlAsync($"""
                UPDATE nexora.report_schedule SET last_run_at = now(), next_run_at = {NextRunAt(s.Frequency, clock.UtcNow)}, updated_at = now()
                WHERE id = {s.Id}
                """, ct);
        }
        await uow.SaveChangesAsync(ct);
        return delivered;
    }

    private static int? CountRows(JsonElement result)
    {
        if (result.ValueKind == JsonValueKind.Object && result.TryGetProperty("rows", out var rows) && rows.ValueKind == JsonValueKind.Array)
            return rows.GetArrayLength();
        return null;
    }

    // §55 : toujours en UTC — `DateTimeOffset.Date` renvoie un DateTime « Unspecified » dont la
    // conversion implicite adopterait le fuseau LOCAL de la machine, pas UTC (même piège que
    // DateTimeOffset.Parse sans « Z », déjà rencontré Phase 41).
    private static DateTimeOffset NextRunAt(string frequency, DateTimeOffset from)
    {
        var utcDate = from.UtcDateTime.Date;
        return frequency switch
        {
            "daily" => new DateTimeOffset(utcDate.AddDays(1), TimeSpan.Zero),
            "weekly" => new DateTimeOffset(utcDate.AddDays(7 - (int)utcDate.DayOfWeek + 1), TimeSpan.Zero),
            "monthly" => new DateTimeOffset(new DateTime(utcDate.Year, utcDate.Month, 1), TimeSpan.Zero).AddMonths(1),
            _ => new DateTimeOffset(utcDate.AddDays(1), TimeSpan.Zero),   // on_close : déclenché manuellement, échéance de secours 24h
        };
    }

    private static ReportScheduleDto ToDto(Row r) => new(r.Id, r.Name, r.ReportKey, r.Frequency,
        JsonDocument.Parse(r.Filter).RootElement, r.Format, JsonSerializer.Deserialize<List<string>>(r.Recipients, Web) ?? [],
        r.Status, r.LastRunAt, r.NextRunAt);
}
