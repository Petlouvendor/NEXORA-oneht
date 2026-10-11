using Microsoft.EntityFrameworkCore;
using Nexora.Application.Abstractions;
using Nexora.Application.Operations;
using Nexora.Contracts.Operations;
using Nexora.Infrastructure.Persistence;
using Nexora.Domain.Common;

namespace Nexora.Infrastructure.Operations;

/// <summary>
/// Batterie de diagnostics d'exploitation (Phase 41 §5-6, §13-15, §28, §51) : chaque contrôle est
/// un test prédéfini, borné à l'entreprise active — aucun accès arbitraire à la base (§45). La
/// maintenance base de données (§13-14) est une lecture système (<c>pg_stat_activity</c>/
/// <c>pg_locks</c>/<c>pg_class</c>), sans donnée d'entreprise.
/// </summary>
public sealed class SqlOpsDiagnosticsService(NexoraDbContext db, ITenantContext tenant, IDateTimeProvider clock) : IOpsDiagnosticsService
{
    private Guid Company => tenant.ActiveCompanyId ?? throw TenantErrors.ActiveCompanyRequired();

    private sealed record QueueRow(int Dead, int Pending);
    private sealed record SyncRow(int ActiveDevices, DateTimeOffset? LastSync);
    private sealed record BackupAggRow(int Count, long TotalSize);
    private sealed record TableSizeRow(string Table, long SizeBytes, long RowEstimate);

    public async Task<IReadOnlyList<DiagnosticCheckDto>> RunAllAsync(CancellationToken ct = default)
    {
        var company = Company;
        var checks = new List<DiagnosticCheckDto>();
        var sw = System.Diagnostics.Stopwatch.StartNew();

        sw.Restart();
        var dbOk = await SafeAsync(() => db.Database.CanConnectAsync(ct), false);
        checks.Add(new("database", "Base de données", dbOk ? "ok" : "fail",
            dbOk ? "PostgreSQL joignable" : "PostgreSQL injoignable", Ms(sw)));

        sw.Restart();
        var dbSize = await SafeAsync(() => db.Database.SqlQuery<long>($"""
            SELECT pg_database_size(current_database())::bigint AS "Value"
            """).FirstAsync(ct), 0L);
        checks.Add(new("storage", "Stockage", "ok",
            $"base {FormatBytes(dbSize)} — espace disque libre non mesurable sans accès au système d'exploitation", Ms(sw)));

        sw.Restart();
        var q = await SafeAsync(() => db.Database.SqlQuery<QueueRow>($"""
            SELECT
              (SELECT COUNT(*)::int FROM nexora.notification_outbox WHERE company_id = {company} AND status = 'dead') AS "Dead",
              (SELECT COUNT(*)::int FROM nexora.notification_outbox WHERE company_id = {company} AND status IN ('pending','sending')) AS "Pending"
            """).FirstAsync(ct), new QueueRow(0, 0));
        checks.Add(new("queue", "File de notifications", q.Dead > 0 ? "fail" : q.Pending > 100 ? "warn" : "ok",
            $"{q.Pending} en file, {q.Dead} en échec définitif", Ms(sw)));

        checks.Add(new("email", "Fournisseur e-mail", "skipped",
            "canal simulé (stub Phase 34) — aucun fournisseur externe configuré en environnement de développement", null));
        checks.Add(new("sms", "Fournisseur SMS", "skipped",
            "canal simulé (stub Phase 34) — aucun fournisseur externe configuré en environnement de développement", null));

        sw.Restart();
        var errConnectors = await SafeAsync(() => db.Database.SqlQuery<int>($"""
            SELECT COUNT(*)::int AS "Value" FROM nexora.integration_connector WHERE company_id = {company} AND status = 'error'
            """).FirstAsync(ct), 0);
        checks.Add(new("connector", "Connecteurs", errConnectors > 0 ? "fail" : "ok",
            errConnectors > 0 ? $"{errConnectors} connecteur(s) en erreur" : "connecteurs sains", Ms(sw)));

        checks.Add(new("printer", "Imprimantes", "skipped",
            "pilote physique hors périmètre applicatif (§40) — géré par le poste de caisse", null));

        sw.Restart();
        var sync = await SafeAsync(() => db.Database.SqlQuery<SyncRow>($"""
            SELECT COUNT(*)::int AS "ActiveDevices", MAX(last_sync_at) AS "LastSync"
            FROM nexora.sync_device WHERE company_id = {company} AND status = 'active'
            """).FirstAsync(ct), new SyncRow(0, null));
        checks.Add(sync.ActiveDevices == 0
            ? new("sync", "Synchronisation", "skipped", "aucun terminal enregistré", Ms(sw))
            : new("sync", "Synchronisation",
                sync.LastSync is { } ls && ls < clock.UtcNow.AddHours(-24) ? "warn" : "ok",
                sync.LastSync is { } ls2
                    ? $"dernière synchronisation il y a {(int)(clock.UtcNow - ls2).TotalMinutes} min ({sync.ActiveDevices} terminal(aux) actif(s))"
                    : "aucune synchronisation enregistrée",
                Ms(sw)));

        sw.Restart();
        var backupVerified = await SafeAsync(() => db.Database.SqlQuery<bool>($"""
            SELECT EXISTS(
                SELECT 1 FROM nexora.backup
                WHERE company_id = {company} AND status = 'verified' AND verified_at > now() - interval '7 days'
            ) AS "Value"
            """).FirstAsync(ct), false);
        checks.Add(new("backup", "Sauvegarde", backupVerified ? "ok" : "warn",
            backupVerified ? "sauvegarde vérifiée dans les 7 derniers jours" : "aucune sauvegarde vérifiée récente (§40)", Ms(sw)));

        sw.Restart();
        var sub = await SafeAsync(() => db.Database.SqlQuery<string>($"""
            SELECT s.status AS "Value" FROM nexora.subscription s
            JOIN nexora.platform_customer_company pcc ON pcc.customer_id = s.customer_id
            WHERE pcc.company_id = {company}
            ORDER BY s.current_period_start DESC LIMIT 1
            """).FirstOrDefaultAsync(ct), null);
        checks.Add(sub is null
            ? new("license", "Licence", "skipped", "aucun abonnement plateforme lié à cette entreprise", Ms(sw))
            : new("license", "Licence", sub is "active" or "trialing" ? "ok" : sub == "grace" ? "warn" : "fail",
                $"abonnement « {sub} »", Ms(sw)));

        sw.Restart();
        var dbNow = await SafeAsync(() => db.Database.SqlQuery<DateTimeOffset>($"""SELECT now() AS "Value" """).FirstAsync(ct), clock.UtcNow);
        var driftSeconds = Math.Abs((clock.UtcNow - dbNow).TotalSeconds);
        checks.Add(new("clock", "Horloge & fuseau", driftSeconds > 5 ? "warn" : "ok",
            $"écart application / base : {driftSeconds:0.0} s", Ms(sw)));

        return checks;
    }

    public async Task<StorageStatsDto> StorageAsync(CancellationToken ct = default)
    {
        var company = Company;
        var dbSize = await db.Database.SqlQuery<long>($"""
            SELECT pg_database_size(current_database())::bigint AS "Value"
            """).FirstAsync(ct);
        var backups = await db.Database.SqlQuery<BackupAggRow>($"""
            SELECT COUNT(*)::int AS "Count", COALESCE(SUM(size_bytes), 0)::bigint AS "TotalSize"
            FROM nexora.backup WHERE company_id = {company}
            """).FirstAsync(ct);
        var docs = await db.Database.SqlQuery<int>($"""
            SELECT COUNT(*)::int AS "Value" FROM nexora.issued_document WHERE company_id = {company}
            """).FirstAsync(ct);
        return new StorageStatsDto(dbSize, backups.Count, backups.TotalSize, docs, clock.UtcNow);
    }

    public async Task<DatabaseMaintenanceDto> DatabaseAsync(CancellationToken ct = default)
    {
        using var _ = tenant.BeginSystemScope();   // maintenance base = portée système, aucune donnée d'entreprise
        var dbSize = await db.Database.SqlQuery<long>($"""
            SELECT pg_database_size(current_database())::bigint AS "Value"
            """).FirstAsync(ct);
        var connections = await db.Database.SqlQuery<int>($"""
            SELECT COUNT(*)::int AS "Value" FROM pg_stat_activity WHERE datname = current_database()
            """).FirstAsync(ct);
        var longTx = await db.Database.SqlQuery<int>($"""
            SELECT COUNT(*)::int AS "Value" FROM pg_stat_activity
            WHERE datname = current_database() AND state <> 'idle' AND xact_start < now() - interval '5 minutes'
            """).FirstAsync(ct);
        var locks = await db.Database.SqlQuery<int>($"""
            SELECT COUNT(*)::int AS "Value" FROM pg_locks l
            JOIN pg_database d ON d.oid = l.database WHERE d.datname = current_database()
            """).FirstAsync(ct);
        var tables = await db.Database.SqlQuery<TableSizeRow>($"""
            SELECT c.relname AS "Table", pg_total_relation_size(c.oid)::bigint AS "SizeBytes",
                   GREATEST(c.reltuples, 0)::bigint AS "RowEstimate"
            FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
            WHERE n.nspname = 'nexora' AND c.relkind = 'r'
            ORDER BY pg_total_relation_size(c.oid) DESC LIMIT 10
            """).ToListAsync(ct);

        return new DatabaseMaintenanceDto(dbSize, connections, longTx, locks,
            tables.Select(t => new TableSizeDto(t.Table, t.SizeBytes, t.RowEstimate)).ToList(), clock.UtcNow);
    }

    private static int Ms(System.Diagnostics.Stopwatch sw) => (int)sw.ElapsedMilliseconds;

    private static string FormatBytes(long bytes)
    {
        double v = bytes;
        string[] units = ["o", "Ko", "Mo", "Go", "To"];
        var i = 0;
        while (v >= 1024 && i < units.Length - 1) { v /= 1024; i++; }
        return $"{v:0.#} {units[i]}";
    }

    private static async Task<T> SafeAsync<T>(Func<Task<T>> fn, T fallback)
    {
        try { return await fn(); }
        catch { return fallback; }
    }
}
