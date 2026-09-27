using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Nexora.Application.Abstractions;
using Nexora.Application.Migration;
using Nexora.Contracts.Migration;
using Nexora.Domain.Common;
using Nexora.Infrastructure.Persistence;

namespace Nexora.Infrastructure.Migration;

// =============================================================================
// Pipeline d'import (Phase 39 §2, §5, §23-29, §35-37, §45-46).
//   created → profiled → mapped → validated → simulated → executing → completed
// `simulate` n'écrit rien (§23) ; `cancel` défait les objets créés tant que possible (§37).
// =============================================================================
public sealed class SqlImportService(
    NexoraDbContext db,
    ITenantContext tenant,
    IUnitOfWork uow,
    IDateTimeProvider clock,
    IEnumerable<IEntityImporter> importers) : IImportService
{
    private const int MaxContentBytes = 8 * 1024 * 1024;
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);
    private Guid Company => tenant.ActiveCompanyId ?? throw new InvalidOperationException("Entreprise active requise.");
    private readonly Dictionary<string, IEntityImporter> _byKind = importers.ToDictionary(i => i.Kind);

    private static readonly string[] Kinds = ["customers", "suppliers", "products", "opening_stock"];
    private static readonly string[] Modes = ["create_only", "update_only", "upsert", "skip_duplicates", "reject_duplicates"];

    // ------------------------------------------------------------- Lecture

    private sealed record JobRow(Guid Id, string Kind, string SourceType, string? FileName, string Status, string Mode,
        int BatchSize, int TotalRows, int ProcessedRows, int OkRows, int WarningRows, int ErrorRows, int DuplicateRows,
        int CreatedCount, int UpdatedCount, int SkippedCount, int RejectedCount, string? Profile, string? Mapping,
        string? Simulation, string? Reconciliation, string? ErrorMessage, DateTimeOffset? FinalizedAt, Guid? CreatedBy,
        DateTimeOffset CreatedAt, string? Content, DateTimeOffset? PausedAt);

    private const string JobSelect = """
        SELECT id AS "Id", kind AS "Kind", source_type AS "SourceType", file_name AS "FileName", status AS "Status",
               mode AS "Mode", batch_size AS "BatchSize", total_rows AS "TotalRows", processed_rows AS "ProcessedRows",
               ok_rows AS "OkRows", warning_rows AS "WarningRows", error_rows AS "ErrorRows", duplicate_rows AS "DuplicateRows",
               created_count AS "CreatedCount", updated_count AS "UpdatedCount", skipped_count AS "SkippedCount",
               rejected_count AS "RejectedCount", profile::text AS "Profile", mapping::text AS "Mapping",
               simulation::text AS "Simulation", reconciliation::text AS "Reconciliation", error_message AS "ErrorMessage",
               finalized_at AS "FinalizedAt", created_by AS "CreatedBy", created_at AS "CreatedAt",
               content AS "Content", paused_at AS "PausedAt"
        FROM nexora.import_job
        """;

    public async Task<IReadOnlyList<ImportJobDto>> ListAsync(CancellationToken ct = default)
    {
        var rows = await db.Database.SqlQueryRaw<JobRow>(JobSelect + " WHERE company_id = {0} ORDER BY created_at DESC LIMIT 100", Company).ToListAsync(ct);
        return rows.Select(ToDto).ToList();
    }

    public async Task<Result<ImportJobDto>> GetAsync(Guid id, CancellationToken ct = default)
    {
        var row = await LoadAsync(id, ct);
        return row is null ? Error.NotFound("migration.import", "Import introuvable.") : Result.Success(ToDto(row));
    }

    // ------------------------------------------------------------- Création (§2)

    public async Task<Result<ImportJobDto>> CreateAsync(CreateImportRequest r, CancellationToken ct = default)
    {
        if (!Kinds.Contains(r.Kind)) return Error.Validation("migration.kind", "Type d'import inconnu.");
        var source = r.SourceType is "csv" or "json" or "manual" ? (r.SourceType == "manual" ? "csv" : r.SourceType) : null;
        if (source is null) return Error.Validation("migration.source", "Source prise en charge : csv, json (xlsx → exportez en CSV).");
        if (string.IsNullOrWhiteSpace(r.Content)) return Error.Validation("migration.content", "Contenu vide.");
        if (Encoding.UTF8.GetByteCount(r.Content) > MaxContentBytes) return Error.Validation("migration.content", "Fichier trop volumineux (max 8 Mo).");
        if (!Modes.Contains(r.Mode)) return Error.Validation("migration.mode", "Mode d'import invalide.");

        ParsedTable table;
        try { table = TabularParser.Parse(source, r.Content); }
        catch (Exception ex) { return Error.Validation("migration.parse", $"Fichier illisible : {ex.Message}"); }
        if (table.Headers.Count == 0) return Error.Validation("migration.parse", "Aucune colonne détectée.");

        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(r.Content))).ToLowerInvariant();
        var id = Guid.NewGuid();
        var batch = Math.Clamp(r.BatchSize, 1, 10000);

        await db.Database.ExecuteSqlAsync($"""
            INSERT INTO nexora.import_job (id, company_id, kind, source_type, file_name, content, content_hash, mode, batch_size, total_rows, created_by)
            VALUES ({id}, {Company}, {r.Kind}, {source}, {r.FileName}, {r.Content}, {hash}, {r.Mode}, {batch}, {table.Rows.Count}, {tenant.UserId})
            """, ct);
        await LogAsync(id, MigrationEvents.ImportCreated, new { r.Kind, source, rows = table.Rows.Count }, ct);
        await uow.SaveChangesAsync(ct);
        return await GetAsync(id, ct);
    }

    // ------------------------------------------------------------- Profilage (§6)

    public async Task<Result<ImportJobDto>> ProfileAsync(Guid id, CancellationToken ct = default)
    {
        var job = await LoadAsync(id, ct);
        if (job is null) return Error.NotFound("migration.import", "Import introuvable.");
        if (job.Status is "executing" or "completed" or "canceled") return Error.Conflict("migration.state", "Import déjà exécuté.");

        var table = TabularParser.Parse(job.SourceType, job.Content ?? "");
        var profile = ImportProfiler.Profile(table, clock.UtcNow);
        await db.Database.ExecuteSqlAsync($"""
            UPDATE nexora.import_job SET profile = {JsonSerializer.Serialize(profile, Web)}::jsonb,
                status = 'profiled', total_rows = {table.Rows.Count}, updated_at = now()
            WHERE id = {id} AND company_id = {Company}
            """, ct);
        await LogAsync(id, MigrationEvents.ImportProfiled, new { profile.RowCount, columns = profile.Columns.Count, profile.DuplicateRows }, ct);
        await uow.SaveChangesAsync(ct);
        return await GetAsync(id, ct);
    }

    // ------------------------------------------------------------- Mapping (§7)

    public async Task<Result<ImportJobDto>> SetMappingAsync(Guid id, SetMappingRequest r, CancellationToken ct = default)
    {
        var job = await LoadAsync(id, ct);
        if (job is null) return Error.NotFound("migration.import", "Import introuvable.");
        if (job.Status is "executing" or "completed" or "canceled") return Error.Conflict("migration.state", "Import déjà exécuté.");
        if (!_byKind.TryGetValue(job.Kind, out var importer)) return Error.Validation("migration.kind", "Type non pris en charge.");

        var required = importer.TargetFields.Where(f => f.Required).Select(f => f.Field).ToHashSet();
        var mapped = (r.Mapping ?? []).Where(x => !string.IsNullOrWhiteSpace(x.SourceColumn) || !string.IsNullOrWhiteSpace(x.DefaultValue))
            .Select(x => x.TargetField).ToHashSet();
        var missing = required.Except(mapped).ToList();
        if (missing.Count > 0) return Error.Validation("migration.mapping", $"Champs obligatoires non mappés : {string.Join(", ", missing)}.");

        await db.Database.ExecuteSqlAsync($"""
            UPDATE nexora.import_job SET mapping = {JsonSerializer.Serialize(r.Mapping, Web)}::jsonb, status = 'mapped', updated_at = now()
            WHERE id = {id} AND company_id = {Company}
            """, ct);

        if (!string.IsNullOrWhiteSpace(r.SaveAsTemplate))
            await db.Database.ExecuteSqlAsync($"""
                INSERT INTO nexora.import_mapping_template (company_id, kind, name, mapping, created_by)
                VALUES ({Company}, {job.Kind}, {r.SaveAsTemplate.Trim()}, {JsonSerializer.Serialize(r.Mapping, Web)}::jsonb, {tenant.UserId})
                ON CONFLICT ON CONSTRAINT import_mapping_template_uk DO UPDATE SET mapping = EXCLUDED.mapping, updated_at = now()
                """, ct);

        await LogAsync(id, MigrationEvents.ImportMapped, new { fields = mapped.Count }, ct);
        await uow.SaveChangesAsync(ct);
        return await GetAsync(id, ct);
    }

    // ------------------------------------------------------------- Validation (§25)

    public async Task<Result<ImportJobDto>> ValidateAsync(Guid id, CancellationToken ct = default)
    {
        var job = await LoadAsync(id, ct);
        if (job is null) return Error.NotFound("migration.import", "Import introuvable.");
        if (job.Mapping is null) return Error.Conflict("migration.state", "Définissez d'abord le mapping.");
        if (job.Status is "executing" or "completed" or "canceled") return Error.Conflict("migration.state", "Import déjà exécuté.");
        var importer = _byKind[job.Kind];
        var mapping = JsonSerializer.Deserialize<List<ColumnMappingDto>>(job.Mapping, Web) ?? [];
        var table = TabularParser.Parse(job.SourceType, job.Content ?? "");

        var ctx = new ImportContext(Company, id, tenant.UserId, job.Mode, DryRun: true);
        int ok = 0, warn = 0, err = 0;
        var seenKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var dup = 0;

        await uow.ExecuteInTransactionAsync(async token =>
        {
            await db.Database.ExecuteSqlAsync($"DELETE FROM nexora.import_row WHERE import_job_id = {id}", token);
            var n = 1;
            foreach (var raw in table.Rows)
            {
                var mappedRow = ApplyMapping(raw, mapping);
                var res = await importer.ValidateAsync(mappedRow, ctx, token);
                var isDup = res.DedupKey is { } k && !seenKeys.Add(k);
                if (isDup) dup++;
                var status = isDup ? "warning" : res.Status;
                var messages = isDup
                    ? res.Messages.Append(new RowMessageDto("warning", res.DedupKey is null ? null : "code", "Doublon dans le fichier.")).ToList()
                    : res.Messages.ToList();
                if (status == "error") err++; else if (status == "warning" || messages.Any(x => x.Level == "warning")) { warn++; status = status == "valid" ? "warning" : status; } else ok++;

                await db.Database.ExecuteSqlAsync($"""
                    INSERT INTO nexora.import_row (import_job_id, company_id, row_number, source, dedup_key, match_type, status, messages)
                    VALUES ({id}, {Company}, {n}, {JsonSerializer.Serialize(raw, Web)}::jsonb, {res.DedupKey}, {res.MatchType},
                            {status}, {JsonSerializer.Serialize(messages, Web)}::jsonb)
                    """, token);
                n++;
            }
            return 0;
        }, ct);

        await db.Database.ExecuteSqlAsync($"""
            UPDATE nexora.import_job SET status = 'validated', ok_rows = {ok}, warning_rows = {warn}, error_rows = {err},
                duplicate_rows = {dup}, total_rows = {table.Rows.Count}, updated_at = now()
            WHERE id = {id} AND company_id = {Company}
            """, ct);
        await LogAsync(id, MigrationEvents.ImportValidated, new { ok, warn, err, dup }, ct);
        await uow.SaveChangesAsync(ct);
        return await GetAsync(id, ct);
    }

    // ------------------------------------------------------------- Simulation (§23-24)

    public async Task<Result<ImportJobDto>> SimulateAsync(Guid id, CancellationToken ct = default)
    {
        var job = await LoadAsync(id, ct);
        if (job is null) return Error.NotFound("migration.import", "Import introuvable.");
        if (job.Status is not ("validated" or "simulated")) return Error.Conflict("migration.state", "Validez d'abord l'import.");
        var importer = _byKind[job.Kind];
        var mapping = JsonSerializer.Deserialize<List<ColumnMappingDto>>(job.Mapping!, Web) ?? [];
        var rows = await LoadRowsRawAsync(id, null, ct);
        var ctx = new ImportContext(Company, id, tenant.UserId, job.Mode, DryRun: true);

        int wouldCreate = 0, wouldUpdate = 0, wouldReject = 0, rowsOk = 0, warnings = 0, errors = 0;
        var sample = new List<ImportRowDto>();
        var seenKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // §23 : aucune modification en production — les importeurs en DryRun ne font que lire.
        foreach (var rr in rows)
        {
            if (rr.Status == "error") { errors++; continue; }
            var mappedRow = ApplyMapping(rr.Source, mapping);
            var res = await importer.ProcessAsync(mappedRow, ctx, ct);
            if (res.DedupKey is { } dk && !seenKeys.Add(dk))
                res = res with { Status = job.Mode == "reject_duplicates" ? "rejected" : "skipped" };
            switch (res.Status)
            {
                case "created": wouldCreate++; rowsOk++; break;
                case "updated": wouldUpdate++; rowsOk++; break;
                case "skipped": break;
                case "rejected": wouldReject++; break;
                case "error": errors++; break;
                default: rowsOk++; break;
            }
            if (res.Messages.Any(x => x.Level == "warning")) warnings++;
            if (sample.Count < 25)
                sample.Add(new ImportRowDto(rr.RowNumber, res.Status, res.MatchType, res.DedupKey, res.TargetId, rr.Source, res.Messages));
        }

        var sim = new SimulationDto(rowsOk, warnings, errors, job.DuplicateRows, wouldCreate, wouldUpdate, wouldReject, sample, clock.UtcNow);
        await db.Database.ExecuteSqlAsync($"""
            UPDATE nexora.import_job SET simulation = {JsonSerializer.Serialize(sim, Web)}::jsonb, status = 'simulated', updated_at = now()
            WHERE id = {id} AND company_id = {Company}
            """, ct);
        await LogAsync(id, MigrationEvents.ImportSimulationCompleted, new { wouldCreate, wouldUpdate, wouldReject, errors }, ct);
        await uow.SaveChangesAsync(ct);
        return await GetAsync(id, ct);
    }

    // ------------------------------------------------------------- Exécution (§27-29)

    public Task<Result<ImportJobDto>> ExecuteAsync(Guid id, CancellationToken ct = default) => RunAsync(id, fresh: true, ct);
    public Task<Result<ImportJobDto>> ResumeAsync(Guid id, CancellationToken ct = default) => RunAsync(id, fresh: false, ct);

    private async Task<Result<ImportJobDto>> RunAsync(Guid id, bool fresh, CancellationToken ct)
    {
        var job = await LoadAsync(id, ct);
        if (job is null) return Error.NotFound("migration.import", "Import introuvable.");
        if (fresh && job.Status != "simulated") return Error.Conflict("migration.state", "Simulez d'abord l'import (§23).");
        if (!fresh && job.Status != "paused") return Error.Conflict("migration.state", "L'import n'est pas en pause.");

        var importer = _byKind[job.Kind];
        var mapping = JsonSerializer.Deserialize<List<ColumnMappingDto>>(job.Mapping!, Web) ?? [];
        var ctx = new ImportContext(Company, id, tenant.UserId, job.Mode, DryRun: false);

        await db.Database.ExecuteSqlAsync($"""
            UPDATE nexora.import_job SET status = 'executing', paused_at = NULL, error_message = NULL, updated_at = now()
            WHERE id = {id} AND company_id = {Company}
            """, ct);
        await LogAsync(id, fresh ? MigrationEvents.ImportStarted : MigrationEvents.ImportProgressed, new { }, ct);
        await uow.SaveChangesAsync(ct);

        try
        {
            while (true)
            {
                var pending = await LoadRowsRawAsync(id, "pending,valid,warning", ct, limit: job.BatchSize);
                if (pending.Count == 0) break;

                await uow.ExecuteInTransactionAsync(async token =>
                {
                    foreach (var rr in pending)
                    {
                        var mappedRow = ApplyMapping(rr.Source, mapping);
                        var res = await importer.ProcessAsync(mappedRow, ctx, token);
                        await db.Database.ExecuteSqlAsync($"""
                            UPDATE nexora.import_row SET status = {res.Status}, match_type = {res.MatchType}, target_id = {res.TargetId},
                                messages = {JsonSerializer.Serialize(res.Messages, Web)}::jsonb, updated_at = now()
                            WHERE import_job_id = {id} AND row_number = {rr.RowNumber}
                            """, token);
                        if (res is { RecordEntityType: { } et, RecordAction: { } act, TargetId: { } tid })
                            await db.Database.ExecuteSqlAsync($"""
                                INSERT INTO nexora.import_record (import_job_id, company_id, entity_type, entity_id, action)
                                VALUES ({id}, {Company}, {et}, {tid}, {act})
                                """, token);
                        await BumpCountersAsync(id, res.Status, token);
                    }
                    return 0;
                }, ct);

                var refreshed = await LoadAsync(id, ct);
                if (refreshed?.PausedAt is not null)
                {
                    await LogAsync(id, MigrationEvents.ImportPaused, new { }, ct);
                    await uow.SaveChangesAsync(ct);
                    return await GetAsync(id, ct);
                }
            }

            await db.Database.ExecuteSqlAsync($"""
                UPDATE nexora.import_job SET status = 'completed', updated_at = now(),
                    processed_rows = (SELECT COUNT(*) FROM nexora.import_row WHERE import_job_id = {id}
                                      AND status IN ('created','updated','skipped','rejected'))
                WHERE id = {id} AND company_id = {Company}
                """, ct);
            await LogAsync(id, MigrationEvents.ImportCompleted, new { }, ct);
            await uow.SaveChangesAsync(ct);
        }
        catch (Exception ex)
        {
            await db.Database.ExecuteSqlAsync($"""
                UPDATE nexora.import_job SET status = 'failed', error_message = {ex.Message[..Math.Min(500, ex.Message.Length)]}, updated_at = now()
                WHERE id = {id} AND company_id = {Company}
                """, ct);
            await LogAsync(id, MigrationEvents.ImportFailed, new { error = ex.Message }, ct);
            await uow.SaveChangesAsync(ct);
            return Error.Unexpected("migration.execute", $"Import interrompu : {ex.Message}");
        }

        return await GetAsync(id, ct);
    }

    public async Task<Result<ImportJobDto>> PauseAsync(Guid id, CancellationToken ct = default)
    {
        var n = await db.Database.ExecuteSqlAsync($"""
            UPDATE nexora.import_job SET paused_at = {clock.UtcNow}, status = CASE WHEN status = 'executing' THEN 'paused' ELSE status END, updated_at = now()
            WHERE id = {id} AND company_id = {Company} AND status IN ('executing','paused')
            """, ct);
        await uow.SaveChangesAsync(ct);
        return n == 0 ? Error.Conflict("migration.state", "L'import n'est pas en cours.") : await GetAsync(id, ct);
    }

    public async Task<Result<ImportJobDto>> CancelAsync(Guid id, CancellationToken ct = default)
    {
        var job = await LoadAsync(id, ct);
        if (job is null) return Error.NotFound("migration.import", "Import introuvable.");
        if (job.Status == "canceled") return Error.Conflict("migration.state", "Import déjà annulé.");
        if (job.FinalizedAt is not null) return Error.Conflict("migration.finalized", "Import finalisé — rollback impossible (§37).");

        var records = await db.Database.SqlQuery<ImportRecordRow>($"""
            SELECT id AS "Id", entity_type AS "EntityType", entity_id AS "EntityId", action AS "Action"
            FROM nexora.import_record WHERE import_job_id = {id} AND reverted_at IS NULL AND action = 'created'
            ORDER BY created_at DESC
            """).ToListAsync(ct);

        var reverted = 0;
        var kept = 0;
        foreach (var rec in records)
        {
            // Le stock d'ouverture n'est pas supprimable sans casser le solde — procédure corrective (§37).
            var table = rec.EntityType switch
            {
                "customer" => "customer", "supplier" => "supplier", "product" => "product",
                "product_category" => "product_category", "unit" => "unit", "product_price" => "product_price",
                _ => (string?)null,
            };
            if (table is null) { kept++; continue; }
            try
            {
                var deleteSql = "DELETE FROM nexora." + table + " WHERE id = {0} AND company_id = {1}";
                await uow.ExecuteInTransactionAsync(async token =>
                {
                    await db.Database.ExecuteSqlRawAsync("SET LOCAL nexora.allow_hard_delete = 'on'", token);
                    await db.Database.ExecuteSqlRawAsync(deleteSql, [rec.EntityId, Company], token);
                    await db.Database.ExecuteSqlRawAsync(
                        "UPDATE nexora.import_record SET reverted_at = now() WHERE id = {0}", [rec.Id], token);
                    return 0;
                }, ct);
                reverted++;
            }
            catch
            {
                kept++;   // référencé ailleurs → procédure corrective manuelle (§37)
            }
        }

        await db.Database.ExecuteSqlAsync($"""
            UPDATE nexora.import_job SET status = 'canceled', error_message = {(kept > 0 ? $"{kept} objet(s) conservé(s) car déjà utilisé(s)" : null)}, updated_at = now()
            WHERE id = {id} AND company_id = {Company}
            """, ct);
        await LogAsync(id, MigrationEvents.ImportCancelled, new { reverted, kept }, ct);
        await uow.SaveChangesAsync(ct);
        return await GetAsync(id, ct);
    }

    private sealed record ImportRecordRow(Guid Id, string EntityType, Guid EntityId, string Action);

    // ------------------------------------------------------------- Correction & rapprochement

    public async Task<Result<ImportRowDto>> CorrectRowAsync(Guid id, CorrectRowRequest r, CancellationToken ct = default)
    {
        var job = await LoadAsync(id, ct);
        if (job is null) return Error.NotFound("migration.import", "Import introuvable.");
        if (job.Status is "executing" or "completed" or "canceled") return Error.Conflict("migration.state", "Import déjà exécuté.");
        var importer = _byKind[job.Kind];
        var mapping = JsonSerializer.Deserialize<List<ColumnMappingDto>>(job.Mapping ?? "[]", Web) ?? [];

        var mappedRow = ApplyMapping(r.Source, mapping);
        var res = await importer.ValidateAsync(mappedRow, new ImportContext(Company, id, tenant.UserId, job.Mode, true), ct);
        await db.Database.ExecuteSqlAsync($"""
            UPDATE nexora.import_row SET source = {JsonSerializer.Serialize(r.Source, Web)}::jsonb, status = {res.Status},
                dedup_key = {res.DedupKey}, match_type = {res.MatchType}, messages = {JsonSerializer.Serialize(res.Messages, Web)}::jsonb, updated_at = now()
            WHERE import_job_id = {id} AND row_number = {r.RowNumber}
            """, ct);
        await uow.SaveChangesAsync(ct);
        var rows = await ListRowsAsync(id, null, ct);
        return rows.FirstOrDefault(x => x.RowNumber == r.RowNumber) is { } dto ? Result.Success(dto)
            : Error.NotFound("migration.row", "Ligne introuvable.");
    }

    public async Task<Result<ReconciliationDto>> ReconcileAsync(Guid id, CancellationToken ct = default)
    {
        var job = await LoadAsync(id, ct);
        if (job is null) return Error.NotFound("migration.import", "Import introuvable.");
        if (job.Status is not ("completed" or "simulated")) return Error.Conflict("migration.state", "Rapprochement après import.");

        var lines = new List<ReconciliationLineDto>();
        var sourceRows = job.TotalRows;
        var integrated = job.CreatedCount + job.UpdatedCount + job.SkippedCount;
        lines.Add(new("Lignes source vs intégrées", sourceRows, integrated, integrated - sourceRows, integrated + job.RejectedCount + job.ErrorRows >= sourceRows));

        if (job.Kind == "customers")
            lines.Add(await CountLineAsync("Clients dans NEXORA ONE", job.CreatedCount, "customer", ct));
        else if (job.Kind == "suppliers")
            lines.Add(await CountLineAsync("Fournisseurs dans NEXORA ONE", job.CreatedCount, "supplier", ct));
        else if (job.Kind == "products")
            lines.Add(await CountLineAsync("Produits dans NEXORA ONE", job.CreatedCount, "product", ct));
        else if (job.Kind == "opening_stock")
        {
            var totalQty = await db.Database.SqlQuery<decimal>($"""
                SELECT COALESCE(SUM(quantity), 0)::numeric AS "Value" FROM nexora.stock_movement
                WHERE company_id = {Company} AND movement_type = 'opening' AND reference_type = 'import' AND reference_id = {id}
                """).FirstAsync(ct);
            lines.Add(new("Quantité d'ouverture importée", totalQty, totalQty, 0, true));
        }

        var recon = new ReconciliationDto(lines, lines.All(l => l.Ok), clock.UtcNow);
        await db.Database.ExecuteSqlAsync($"""
            UPDATE nexora.import_job SET reconciliation = {JsonSerializer.Serialize(recon, Web)}::jsonb, updated_at = now()
            WHERE id = {id} AND company_id = {Company}
            """, ct);
        await LogAsync(id, MigrationEvents.MigrationReconciled, new { recon.AllReconciled }, ct);
        await uow.SaveChangesAsync(ct);
        return Result.Success(recon);
    }

    private async Task<ReconciliationLineDto> CountLineAsync(string metric, int created, string table, CancellationToken ct)
    {
        var recorded = await db.Database.SqlQuery<int>($"""
            SELECT COUNT(*)::int AS "Value" FROM nexora.import_record ir
            WHERE ir.company_id = {Company} AND ir.entity_type = {table} AND ir.reverted_at IS NULL
            """).FirstAsync(ct);
        return new(metric, created, recorded, recorded - created, recorded >= created);
    }

    // ------------------------------------------------------------- Lignes / événements / mapping

    public async Task<IReadOnlyList<ImportRowDto>> ListRowsAsync(Guid id, string? status, CancellationToken ct = default)
    {
        var rows = await LoadRowsRawAsync(id, status, ct, limit: 500);
        return rows.Select(r => new ImportRowDto(r.RowNumber, r.Status, r.MatchType, r.DedupKey, r.TargetId, r.Source,
            JsonSerializer.Deserialize<List<RowMessageDto>>(r.Messages, Web) ?? [])).ToList();
    }

    public async Task<IReadOnlyList<ImportEventDto>> ListEventsAsync(Guid id, CancellationToken ct = default)
        => await db.Database.SqlQuery<ImportEventDto>($"""
            SELECT event_type AS "EventType", occurred_at AS "OccurredAt" FROM nexora.import_event
            WHERE import_job_id = {id} AND company_id = {Company} ORDER BY occurred_at
            """).ToListAsync(ct);

    public Task<IReadOnlyList<TargetFieldDto>> TargetFieldsAsync(string kind, CancellationToken ct = default)
        => Task.FromResult(_byKind.TryGetValue(kind, out var imp) ? imp.TargetFields : (IReadOnlyList<TargetFieldDto>)[]);

    public Task<IReadOnlyList<ColumnMappingDto>> SuggestMappingAsync(string kind, IReadOnlyList<string> columns, CancellationToken ct = default)
    {
        if (!_byKind.TryGetValue(kind, out var imp)) return Task.FromResult<IReadOnlyList<ColumnMappingDto>>([]);
        static string Norm(string s) => new(s.ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());
        var synonyms = new Dictionary<string, string[]>
        {
            ["code"] = ["code", "reference", "ref", "id", "num", "numero", "matricule"],
            ["name"] = ["name", "nom", "designation", "libelle", "raisonsociale", "label", "intitule"],
            ["phone"] = ["phone", "tel", "telephone", "mobile", "gsm", "portable"],
            ["email"] = ["email", "mail", "courriel", "e-mail"],
            ["address"] = ["address", "adresse", "addr"],
            ["credit_limit"] = ["creditlimit", "limite", "limitecredit", "encours", "plafond"],
            ["tax_id"] = ["taxid", "nif", "ncc", "rccm", "siret", "tva", "numerofiscal"],
            ["sku"] = ["sku", "code", "reference", "ref", "codeproduit", "codearticle", "article"],
            ["barcode"] = ["barcode", "codebarre", "codebarres", "ean", "gencod", "gtin"],
            ["category"] = ["category", "categorie", "famille", "rayon", "groupe"],
            ["unit"] = ["unit", "unite", "uom", "conditionnement"],
            ["sale_price"] = ["saleprice", "prix", "prixvente", "pvttc", "pvht", "price", "tarif"],
            ["cost"] = ["cost", "cout", "prixachat", "pa", "coutunitaire", "cmup"],
            ["product_type"] = ["type", "typeproduit", "nature"],
            ["quantity"] = ["quantity", "quantite", "qte", "qty", "stock", "solde"],
            ["location"] = ["location", "emplacement", "entrepot", "depot", "warehouse", "magasin"],
            ["count_date"] = ["date", "datedinventaire", "dateinventaire", "asof"],
        };
        var normCols = columns.ToDictionary(Norm, c => c);
        var result = new List<ColumnMappingDto>();
        foreach (var field in imp.TargetFields)
        {
            var keys = synonyms.GetValueOrDefault(field.Field, [Norm(field.Field)]);
            var hit = keys.Select(k => normCols.GetValueOrDefault(k))
                          .FirstOrDefault(c => c is not null)
                      ?? columns.FirstOrDefault(c => keys.Any(k => Norm(c).Contains(k)));
            if (hit is not null) result.Add(new ColumnMappingDto(field.Field, hit, null, DefaultTransform(field.Type)));
        }
        return Task.FromResult<IReadOnlyList<ColumnMappingDto>>(result);
    }

    private static string? DefaultTransform(string type) => type switch
    {
        "decimal" => "decimal", "phone" => "phone", "email" => "email", "date" => "date", "boolean" => "boolean", _ => "trim",
    };

    public async Task<IReadOnlyList<MappingTemplateDto>> ListTemplatesAsync(string? kind, CancellationToken ct = default)
    {
        var rows = await db.Database.SqlQuery<TemplateRow>($"""
            SELECT id AS "Id", kind AS "Kind", name AS "Name", mapping::text AS "Mapping"
            FROM nexora.import_mapping_template
            WHERE company_id = {Company} AND ({kind}::text IS NULL OR kind = {kind}::text)
            ORDER BY kind, name
            """).ToListAsync(ct);
        return rows.Select(t => new MappingTemplateDto(t.Id, t.Kind, t.Name,
            JsonSerializer.Deserialize<List<ColumnMappingDto>>(t.Mapping, Web) ?? [])).ToList();
    }
    private sealed record TemplateRow(Guid Id, string Kind, string Name, string Mapping);

    public async Task<int> ResumeStalledAsync(CancellationToken ct = default)
    {
        using var _ = tenant.BeginSystemScope();
        var stalled = await db.Database.SqlQuery<Guid>($"""
            SELECT id AS "Value" FROM nexora.import_job
            WHERE status = 'executing' AND updated_at < now() - interval '10 minutes'
            """).ToListAsync(ct);
        foreach (var id in stalled)
        {
            var companyId = await db.Database.SqlQuery<Guid>($"""SELECT company_id AS "Value" FROM nexora.import_job WHERE id = {id}""").FirstAsync(ct);
            var createdBy = await db.Database.SqlQuery<Guid?>($"""SELECT created_by AS "Value" FROM nexora.import_job WHERE id = {id}""").FirstOrDefaultAsync(ct);
            tenant.SetUser(createdBy ?? Guid.Empty);
            tenant.SetScope([companyId], [], companyId);
            await db.Database.ExecuteSqlAsync($"UPDATE nexora.import_job SET status = 'paused', paused_at = now() WHERE id = {id}", ct);
            await uow.SaveChangesAsync(ct);
            await ResumeAsync(id, ct);
        }
        return stalled.Count;
    }

    // ------------------------------------------------------------- Helpers

    private static Dictionary<string, string?> ApplyMapping(IReadOnlyDictionary<string, string?> raw, List<ColumnMappingDto> mapping)
    {
        var result = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var map in mapping)
        {
            string? value = null;
            if (!string.IsNullOrWhiteSpace(map.SourceColumn) && raw.TryGetValue(map.SourceColumn!, out var v)) value = v;
            value ??= map.DefaultValue;
            result[map.TargetField] = TabularParser.ApplyTransform(value, map.Transform);
        }
        return result;
    }

    private async Task BumpCountersAsync(Guid id, string rowStatus, CancellationToken ct)
    {
        var task = rowStatus switch
        {
            "created" => db.Database.ExecuteSqlAsync($"UPDATE nexora.import_job SET created_count = created_count + 1, processed_rows = processed_rows + 1, updated_at = now() WHERE id = {id}", ct),
            "updated" => db.Database.ExecuteSqlAsync($"UPDATE nexora.import_job SET updated_count = updated_count + 1, processed_rows = processed_rows + 1, updated_at = now() WHERE id = {id}", ct),
            "skipped" => db.Database.ExecuteSqlAsync($"UPDATE nexora.import_job SET skipped_count = skipped_count + 1, processed_rows = processed_rows + 1, updated_at = now() WHERE id = {id}", ct),
            "rejected" => db.Database.ExecuteSqlAsync($"UPDATE nexora.import_job SET rejected_count = rejected_count + 1, processed_rows = processed_rows + 1, updated_at = now() WHERE id = {id}", ct),
            _ => Task.FromResult(0),
        };
        await task;
    }

    private sealed record RowRaw(int RowNumber, string Status, string MatchType, string? DedupKey, Guid? TargetId,
        Dictionary<string, string?> Source, string Messages);

    private async Task<List<RowRaw>> LoadRowsRawAsync(Guid id, string? statusCsv, CancellationToken ct, int limit = 100000)
    {
        var filter = statusCsv?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToArray();
        var raw = await db.Database.SqlQuery<RowRawJson>($"""
            SELECT row_number AS "RowNumber", status AS "Status", match_type AS "MatchType", dedup_key AS "DedupKey",
                   target_id AS "TargetId", source::text AS "Source", messages::text AS "Messages"
            FROM nexora.import_row
            WHERE import_job_id = {id} AND company_id = {Company}
              AND ({filter}::text[] IS NULL OR status = ANY({filter}::text[]))
            ORDER BY row_number
            LIMIT {limit}
            """).ToListAsync(ct);
        return raw
            .Select(r => new RowRaw(r.RowNumber, r.Status, r.MatchType, r.DedupKey, r.TargetId,
                JsonSerializer.Deserialize<Dictionary<string, string?>>(r.Source, Web) ?? new(), r.Messages))
            .ToList();
    }
    private sealed record RowRawJson(int RowNumber, string Status, string MatchType, string? DedupKey, Guid? TargetId, string Source, string Messages);

    private async Task<JobRow?> LoadAsync(Guid id, CancellationToken ct)
        => await db.Database.SqlQueryRaw<JobRow>(JobSelect + " WHERE id = {0} AND company_id = {1}", id, Company).FirstOrDefaultAsync(ct);

    private async Task LogAsync(Guid id, string eventType, object payload, CancellationToken ct)
        => await db.Database.ExecuteSqlAsync($"""
            INSERT INTO nexora.import_event (import_job_id, company_id, event_type, payload, actor_user_id)
            VALUES ({id}, {Company}, {eventType}, {JsonSerializer.Serialize(payload, Web)}::jsonb, {tenant.UserId})
            """, ct);

    private ImportJobDto ToDto(JobRow j) => new(j.Id, j.Kind, j.SourceType, j.FileName, j.Status, j.Mode, j.BatchSize,
        j.TotalRows, j.ProcessedRows, j.OkRows, j.WarningRows, j.ErrorRows, j.DuplicateRows,
        j.CreatedCount, j.UpdatedCount, j.SkippedCount, j.RejectedCount,
        j.Profile is null ? null : JsonSerializer.Deserialize<ImportProfileDto>(j.Profile, Web),
        j.Mapping is null ? null : JsonSerializer.Deserialize<List<ColumnMappingDto>>(j.Mapping, Web),
        j.Simulation is null ? null : JsonSerializer.Deserialize<SimulationDto>(j.Simulation, Web),
        j.Reconciliation is null ? null : JsonSerializer.Deserialize<ReconciliationDto>(j.Reconciliation, Web),
        j.ErrorMessage, j.FinalizedAt, j.CreatedBy, j.CreatedAt);
}
