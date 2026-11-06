using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Nexora.Application.Abstractions;
using Nexora.Application.Operations;
using Nexora.Contracts.Operations;
using Nexora.Domain.Common;
using Nexora.Infrastructure.Persistence;
using Nexora.Infrastructure.Scheduling;

namespace Nexora.Infrastructure.Operations;

/// <summary>
/// Registre des jobs planifiés (Phase 41 §7-9, §51) — table plateforme <c>job_definition</c> /
/// <c>job_run</c>, aucune donnée d'entreprise. <see cref="RunNowAsync"/> résout
/// <see cref="SchedulerRunner"/> à l'exécution via <see cref="IServiceProvider"/> plutôt qu'en
/// dépendance de constructeur, pour casser le cycle : le planificateur consulte ce registre pour
/// savoir si un job est activé et y journalise chaque passage.
/// </summary>
public sealed class SqlJobRegistry(
    NexoraDbContext db, ITenantContext tenant, IUnitOfWork uow,
    IOptions<SchedulerOptions> scheduler, IServiceProvider services) : IJobRegistry
{
    private sealed record DefRow(string Key, string Name, string Category, string Description, bool Enabled, string Owner);
    private sealed record LastRunRow(Guid Id, string JobKey, DateTimeOffset StartedAt, DateTimeOffset? FinishedAt, string Status,
        int? AffectedCount, string? Error, string TriggeredBy, Guid? TriggeredByUser);
    private sealed record StatsRow(int Total, int Succeeded);

    private const string RunSelect = """
        SELECT id AS "Id", job_key AS "JobKey", started_at AS "StartedAt", finished_at AS "FinishedAt",
               status AS "Status", affected_count AS "AffectedCount", error AS "Error",
               triggered_by AS "TriggeredBy", triggered_by_user AS "TriggeredByUser"
        FROM nexora.job_run
        """;

    public async Task<IReadOnlyList<JobStatusDto>> ListAsync(CancellationToken ct = default)
    {
        using var _ = tenant.BeginSystemScope();
        var defs = await db.Database.SqlQuery<DefRow>($"""
            SELECT key AS "Key", name AS "Name", category AS "Category", description AS "Description",
                   enabled AS "Enabled", owner AS "Owner"
            FROM nexora.job_definition ORDER BY key
            """).ToListAsync(ct);

        var interval = scheduler.Value.IntervalSeconds;
        var result = new List<JobStatusDto>(defs.Count);
        foreach (var d in defs)
        {
            var last = await db.Database.SqlQueryRaw<LastRunRow>(
                RunSelect + " WHERE job_key = {0} ORDER BY started_at DESC LIMIT 1", d.Key).FirstOrDefaultAsync(ct);
            var stats = await db.Database.SqlQuery<StatsRow>($"""
                SELECT COUNT(*)::int AS "Total", COUNT(*) FILTER (WHERE status = 'succeeded')::int AS "Succeeded"
                FROM nexora.job_run WHERE job_key = {d.Key} AND started_at > now() - interval '30 days'
                """).FirstAsync(ct);

            double? rate = stats.Total > 0 ? Math.Round(100.0 * stats.Succeeded / stats.Total, 1) : null;
            DateTimeOffset? next = d.Enabled && scheduler.Value.Enabled && last?.FinishedAt is { } f
                ? f.AddSeconds(interval) : null;

            result.Add(new JobStatusDto(d.Key, d.Name, d.Category, d.Description, d.Enabled, d.Owner,
                last?.StartedAt, last?.FinishedAt, last?.Status, last?.AffectedCount, last?.Error,
                next, rate, stats.Total));
        }
        return result;
    }

    public async Task<bool> IsEnabledAsync(string key, CancellationToken ct = default)
    {
        using var _ = tenant.BeginSystemScope();
        return await db.Database.SqlQuery<bool>($"""
            SELECT COALESCE((SELECT enabled FROM nexora.job_definition WHERE key = {key}), true) AS "Value"
            """).FirstAsync(ct);
    }

    public async Task<Guid> StartRunAsync(string key, string triggeredBy, Guid? triggeredByUser, CancellationToken ct = default)
    {
        using var _ = tenant.BeginSystemScope();
        var id = Guid.NewGuid();
        await db.Database.ExecuteSqlAsync($"""
            INSERT INTO nexora.job_run (id, job_key, status, triggered_by, triggered_by_user)
            VALUES ({id}, {key}, 'running', {triggeredBy}, {triggeredByUser})
            """, ct);
        await uow.SaveChangesAsync(ct);
        return id;
    }

    public async Task FinishRunAsync(Guid runId, bool success, int affectedCount, string? error, CancellationToken ct = default)
    {
        using var _ = tenant.BeginSystemScope();
        var status = success ? "succeeded" : "failed";
        await db.Database.ExecuteSqlAsync($"""
            UPDATE nexora.job_run SET finished_at = now(), status = {status},
                   affected_count = {affectedCount}, error = {error}
            WHERE id = {runId}
            """, ct);
        await uow.SaveChangesAsync(ct);
    }

    public async Task<Result<JobRunDto>> RunNowAsync(string key, CancellationToken ct = default)
    {
        // Un job couvre toutes les entreprises de l'installation (audit 2026-10, A-031).
        if (await PlatformWideRefusalAsync(Nexora.Application.Security.Permissions.Operations.Manage, ct) is { } refused) return refused;
        using (tenant.BeginSystemScope())
        {
            var exists = await db.Database.SqlQuery<bool>($"""
                SELECT EXISTS(SELECT 1 FROM nexora.job_definition WHERE key = {key}) AS "Value"
                """).FirstAsync(ct);
            if (!exists) return Error.NotFound("job.not_found", "Job inconnu.");
        }

        // Même verrou consultatif que le planificateur (audit 2026-10 éd. 2) : une exécution manuelle pendant un passage
        // traitait les mêmes lignes en parallèle. Le verrou avale les exceptions de l'action : elles sont capturées ici
        // pour ne pas être confondues avec un verrou déjà pris.
        var runner = services.GetRequiredService<SchedulerRunner>();
        Exception? failure = null;
        var acquired = await services.GetRequiredService<Nexora.Infrastructure.Scheduling.PostgresAdvisoryLock>().TryExecuteAsync(async token =>
        {
            try { await runner.RunSingleJobAsync(key, tenant.UserId, token); }
            catch (Exception ex) when (ex is not OperationCanceledException) { failure = ex; }
        }, ct);
        if (failure is InvalidOperationException invalid) return Error.Validation("job.invalid", invalid.Message);
        if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(failure);
        if (!acquired)
            return Error.Conflict("job.busy", "Le planificateur est en cours d'exécution ; réessayez dans un instant.");

        using var __ = tenant.BeginSystemScope();
        var last = await db.Database.SqlQueryRaw<LastRunRow>(
            RunSelect + " WHERE job_key = {0} AND triggered_by = 'manual' ORDER BY started_at DESC LIMIT 1", key)
            .FirstAsync(ct);
        return Result.Success(ToDto(last));
    }

    public async Task<Result> PauseAsync(string key, CancellationToken ct = default) => await SetEnabledAsync(key, false, ct);
    public async Task<Result> ResumeAsync(string key, CancellationToken ct = default) => await SetEnabledAsync(key, true, ct);

    /// <summary>Appel d'un utilisateur seulement (le planificateur ne passe pas par ici) — résolu à la demande.</summary>
    private Task<Error?> PlatformWideRefusalAsync(string permission, CancellationToken ct)
        => services.GetRequiredService<IPlatformOperationGuard>().RequirePlatformWideAsync(permission, ct);

    private async Task<Result> SetEnabledAsync(string key, bool enabled, CancellationToken ct)
    {
        if (await PlatformWideRefusalAsync(Nexora.Application.Security.Permissions.Operations.Manage, ct) is { } refused) return refused;
        using var _ = tenant.BeginSystemScope();
        var rows = await db.Database.ExecuteSqlAsync($"""
            UPDATE nexora.job_definition SET enabled = {enabled}, updated_at = now() WHERE key = {key}
            """, ct);
        if (rows == 0) return Error.NotFound("job.not_found", "Job inconnu.");
        await uow.SaveChangesAsync(ct);
        return Result.Success();
    }

    public async Task<IReadOnlyList<JobRunDto>> HistoryAsync(string key, CancellationToken ct = default)
    {
        using var _ = tenant.BeginSystemScope();
        var rows = await db.Database.SqlQueryRaw<LastRunRow>(
            RunSelect + " WHERE job_key = {0} ORDER BY started_at DESC LIMIT 50", key).ToListAsync(ct);
        return rows.Select(ToDto).ToList();
    }

    private static JobRunDto ToDto(LastRunRow r) =>
        new(r.Id, r.JobKey, r.StartedAt, r.FinishedAt, r.Status, r.AffectedCount, r.Error, r.TriggeredBy, r.TriggeredByUser);
}
