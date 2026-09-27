using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Nexora.Application.Abstractions;
using Nexora.Application.Continuity;
using Nexora.Application.Operations;
using Nexora.Application.Reporting;
using Nexora.Contracts.Operations;
using Nexora.Domain.Common;
using Nexora.Infrastructure.Identity;
using Nexora.Infrastructure.Persistence;
using Nexora.Infrastructure.Scheduling;

namespace Nexora.Infrastructure.Operations;

/// <summary>
/// Exploitation &amp; préparation production (Phase 37). Agrège la santé des sous-systèmes, calcule la
/// checklist Go-Live à partir des signaux déjà en place (sauvegardes vérifiées &amp; tests de
/// restauration — Phase 36 ; qualité des données — Phase 31 ; secrets, planificateur, RPO), tient
/// le journal des déploiements et des validations Go-Live.
/// Note : EF <c>SqlQuery</c> traite chaque <c>{…}</c> comme un paramètre — chaque SELECT est écrit
/// en toutes lettres (pas de fragment interpolé) et les scalaires sont alias&#233;s <c>AS "Value"</c>.
/// </summary>
public sealed class SqlOperationsService(
    NexoraDbContext db,
    ITenantContext tenant,
    IUnitOfWork uow,
    IDateTimeProvider clock,
    IOptions<JwtOptions> jwt,
    IOptions<SchedulerOptions> scheduler,
    IContinuityService continuity,
    IDataQualityService dataQuality) : IOperationsService
{
    private Guid Company => tenant.ActiveCompanyId ?? throw new InvalidOperationException("Entreprise active requise.");
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);
    private static readonly string[] Environments = ["local", "test", "staging", "production"];

    // ------------------------------------------------------------- État système (§29, §56)

    public async Task<SystemStatusDto> SystemStatusAsync(CancellationToken ct = default)
    {
        var company = Company;   // une entreprise active est requise, comme partout ailleurs
        var comps = new List<ComponentStatusDto>();

        var dbOk = await SafeAsync(() => db.Database.CanConnectAsync(ct), false);
        comps.Add(new("database", dbOk ? "ok" : "down", dbOk ? "PostgreSQL joignable" : "PostgreSQL injoignable"));

        comps.Add(scheduler.Value.Enabled
            ? new("scheduler", "ok", $"planificateur de fond actif ({scheduler.Value.IntervalSeconds}s)")
            : new("scheduler", "degraded", "planificateur désactivé — relances, alertes et contrôles de santé de fond ne tournent pas"));

        var outbox = await SafeAsync(() => db.Database.SqlQuery<OutboxRow>($"""
            SELECT
              (SELECT COUNT(*)::int FROM nexora.notification_outbox WHERE company_id = {company} AND status = 'pending') AS "Pending",
              (SELECT COUNT(*)::int FROM nexora.notification_outbox WHERE company_id = {company} AND status = 'dead')    AS "Dead"
            """).FirstAsync(ct), new OutboxRow(0, 0));
        comps.Add(outbox.Dead > 0
            ? new("notifications", "degraded", $"{outbox.Dead} notification(s) en échec définitif, {outbox.Pending} en file")
            : new("notifications", outbox.Pending > 100 ? "degraded" : "ok", $"{outbox.Pending} notification(s) externe(s) en file"));

        var errConnectors = await SafeAsync(() => db.Database.SqlQuery<int>($"""
            SELECT COUNT(*)::int AS "Value" FROM nexora.integration_connector
            WHERE company_id = {company} AND status = 'error'
            """).FirstAsync(ct), 0);
        comps.Add(errConnectors > 0
            ? new("integrations", "degraded", $"{errConnectors} connecteur(s) en erreur")
            : new("integrations", "ok", "connecteurs sains"));

        var dr = await TryAsync(() => continuity.DisasterRecoveryStatusAsync(ct));
        comps.Add(dr is null
            ? new("continuity", "degraded", "état de reprise indisponible")
            : new("continuity", dr.Grade is "A" or "B" ? "ok" : dr.Grade == "E" ? "down" : "degraded",
                $"note {dr.Grade} · dernière sauvegarde saine {(dr.LastGoodBackupAgeMinutes is { } m ? $"il y a {m} min" : "aucune")}"));

        var dq = await TryAsync(() => dataQuality.EvaluateAsync(ct));
        comps.Add(dq is null
            ? new("data_quality", "degraded", "contrôle qualité indisponible")
            : new("data_quality", dq.Grade is "A" or "B" or "C" ? "ok" : dq.Grade == "E" ? "down" : "degraded",
                $"score {dq.Score:0} · note {dq.Grade} · {dq.ChecksFailed}/{dq.ChecksRun} contrôle(s) en échec"));

        var openP1 = await SafeAsync(() => db.Database.SqlQuery<int>($"""
            SELECT COUNT(*)::int AS "Value" FROM nexora.continuity_incident
            WHERE company_id = {company} AND status <> 'resolved' AND severity = 'p1'
            """).FirstAsync(ct), 0);
        comps.Add(openP1 > 0
            ? new("incidents", "down", $"{openP1} incident(s) P1 ouvert(s)")
            : new("incidents", "ok", "aucun incident critique ouvert"));

        var overall = comps.Any(c => c.Status == "down") ? "down"
            : comps.Any(c => c.Status == "degraded") ? "degraded" : "ok";
        return new SystemStatusDto(overall, clock.UtcNow, comps);
    }

    private sealed record OutboxRow(int Pending, int Dead);

    // ------------------------------------------------------- Checklist Go-Live (§40, §54)

    public async Task<GoLiveReadinessDto> GoLiveReadinessAsync(CancellationToken ct = default)
    {
        var company = Company;   // une entreprise active est requise
        var checks = new List<GoLiveCheckDto>();
        static GoLiveCheckDto C(string k, string l, string cat, string status, string src, string d) => new(k, l, cat, status, src, d);

        // §2, §57 : tests
        checks.Add(C("automated_tests", "Tests critiques verts", "functional", "manual", "manual",
            "Suites unit + intégration + API + frontend vertes en CI, Debug et Release — à attester."));
        checks.Add(C("no_critical_vulnerabilities", "Aucune vulnérabilité critique ouverte", "security", "manual", "manual",
            "SAST / DAST / scan de dépendances sans faille critique non acceptée (§16-17)."));

        // §40 : sauvegarde vérifiée
        var verifiedBackup = await ScalarBoolAsync($"""
            SELECT EXISTS(
                SELECT 1 FROM nexora.backup
                WHERE company_id = {company} AND status = 'verified' AND verified_at > now() - interval '7 days'
            ) AS "Value"
            """, ct);
        checks.Add(C("backup_verified", "Sauvegarde récente vérifiée", "backup",
            verifiedBackup ? "pass" : "fail", "auto",
            verifiedBackup ? "une sauvegarde a été vérifiée ces 7 derniers jours"
                           : "aucune sauvegarde vérifiée récente — cf. Continuité (§40)"));

        // §40, §50 : restauration testée
        var restoreTested = await ScalarBoolAsync($"""
            SELECT EXISTS(
                SELECT 1 FROM nexora.restore_run
                WHERE company_id = {company} AND mode = 'test' AND status = 'passed' AND started_at > now() - interval '30 days'
            ) AS "Value"
            """, ct);
        checks.Add(C("restore_tested", "Restauration testée", "backup",
            restoreTested ? "pass" : "fail", "auto",
            restoreTested ? "un test de restauration a réussi ces 30 derniers jours" : "aucun test de restauration réussi récent"));

        // §30, §40 : objectifs de reprise
        var dr = await TryAsync(() => continuity.DisasterRecoveryStatusAsync(ct));
        checks.Add(C("dr_targets", "Objectifs RPO tenus", "backup",
            dr is null ? "warn" : dr.RpoBreached ? "fail" : "pass", "auto",
            dr is null ? "état de reprise indisponible" : dr.RpoBreached ? "RPO dépassé" : $"RPO respecté (note {dr.Grade})"));

        // §24-31, §55 : monitoring
        checks.Add(C("monitoring_active", "Monitoring et alertes actifs", "monitoring",
            scheduler.Value.Enabled ? "pass" : "warn", "auto",
            scheduler.Value.Enabled ? "planificateur de fond actif" : "planificateur désactivé — activer `Scheduler:Enabled` en production"));
        checks.Add(C("alerts_tested", "Alertes testées de bout en bout", "monitoring", "manual", "manual",
            "Vérifier qu'une alerte critique atteint bien l'astreinte (§31, §40)."));

        // §18, §40, §51 : secrets de production
        var key = jwt.Value.SigningKey ?? "";
        var weakKey = string.IsNullOrWhiteSpace(key)
            || key.Contains("dev-only", StringComparison.OrdinalIgnoreCase)
            || key.Contains("insecure", StringComparison.OrdinalIgnoreCase)
            || key.Contains("change-me", StringComparison.OrdinalIgnoreCase)
            || key.Length < 32;
        checks.Add(C("production_secrets", "Secrets de production configurés", "security",
            weakKey ? "fail" : "pass", "auto",
            weakKey ? "clé de signature JWT absente / de développement / trop courte — fournir via un gestionnaire de secrets (§18)"
                    : "clé de signature JWT propre à l'environnement"));

        // §36 : migrations
        var migCount = await db.Database.SqlQuery<int>($"""
            SELECT COUNT(*)::int AS "Value" FROM nexora.schema_migration
            """).FirstAsync(ct);
        checks.Add(C("migrations_applied", "Migrations de base validées", "functional",
            migCount >= 50 ? "pass" : "warn", "auto",
            $"{migCount} migration(s) appliquée(s) et suivies par empreinte — à rejouer en staging avant production (§36)"));

        // §31, §46 : qualité des données
        var dq = await TryAsync(() => dataQuality.EvaluateAsync(ct));
        checks.Add(C("data_quality", "Qualité des données acceptable", "functional",
            dq is null ? "warn" : dq.Grade is "A" or "B" or "C" ? "pass" : dq.Grade == "D" ? "warn" : "fail", "auto",
            dq is null ? "contrôle qualité indisponible" : $"score {dq.Score:0} · note {dq.Grade} · {dq.ChecksFailed}/{dq.ChecksRun} en échec"));

        // §39 : plan de rollback
        var hasDeployment = await ScalarBoolAsync($"""
            SELECT EXISTS(SELECT 1 FROM nexora.deployment) AS "Value"
            """, ct);
        checks.Add(C("rollback_plan", "Plan de rollback en place", "rollback",
            hasDeployment ? "pass" : "manual", hasDeployment ? "auto" : "manual",
            hasDeployment ? "des déploiements sont journalisés et réversibles"
                          : "aucun déploiement journalisé — documenter la procédure de retour arrière"));

        // §41-45 : support, formation, responsables
        checks.Add(C("support_ready", "Support et astreinte prêts", "support", "manual", "manual",
            "Tickets, SLA, base de connaissances, on-call (§45)."));
        checks.Add(C("training_done", "Formation et documentation prêtes", "support", "manual", "manual",
            "Guides caissier / manager / comptable / admin, changelog (§43-44)."));
        checks.Add(C("responsibles_identified", "Responsables Go-Live identifiés", "support", "manual", "manual",
            "Décideurs, contacts, chaîne d'escalade (§40)."));

        // §5, §48, §57 (Phase 49) : aucun tracker de défauts applicatif — à attester depuis l'outil de suivi (GitHub/Jira)
        checks.Add(C("no_open_p0", "Aucune anomalie P0 ouverte", "quality", "manual", "manual",
            "Bloquant — NEXORA ONE n'a pas de registre d'anomalies applicatif ; à confirmer depuis l'outil de suivi externe (§48)."));
        checks.Add(C("no_open_p1_waiver", "Aucune anomalie P1 ouverte sans dérogation formelle", "quality", "manual", "manual",
            "Une dérogation P1 doit être écrite et datée, jamais tacite (§57)."));

        // §33, §57 (Phase 49) : performance — aucun banc de charge chiffré exécuté par l'application elle-même
        checks.Add(C("performance_targets_met", "Objectifs de performance atteints", "performance", "manual", "manual",
            "Temps de réponse API/POS/recherche/synchro sous seuil, mesurés hors application (k6/JMeter, §18-23)."));

        // §32, §57 (Phase 49) : isolation multi-tenant — signal RÉEL, pas une simple attestation :
        // si le rôle applicatif contourne RLS, TOUTE l'isolation entreprise s'effondre silencieusement,
        // quel que soit l'état de chaque table (voir MultiTenantIsolationTests pour la preuve fonctionnelle).
        var bypassRls = await ScalarBoolAsync($"""
            SELECT COALESCE((SELECT rolbypassrls FROM pg_roles WHERE rolname = current_user), false) AS "Value"
            """, ct);
        checks.Add(C("multi_tenant_isolation", "Isolation multi-tenant garantie au niveau base", "security",
            bypassRls ? "fail" : "pass", "auto",
            bypassRls ? "le rôle de connexion applicatif contourne RLS (BYPASSRLS) — l'isolation entreprise n'est plus garantie par la base"
                      : "le rôle de connexion applicatif n'a pas BYPASSRLS — l'isolation RLS s'applique réellement (voir aussi MultiTenantIsolationTests, §32)"));

        // §41, §53-54, §57 (Phase 49) : UAT — processus organisationnel, jamais dérivable du code
        checks.Add(C("uat_accepted", "UAT accepté par les utilisateurs représentatifs", "uat", "manual", "manual",
            "Scénarios réels signés par la direction / la caisse / la comptabilité (§53-54)."));

        var pass = checks.Count(c => c.Status == "pass");
        var warn = checks.Count(c => c.Status == "warn");
        var fail = checks.Count(c => c.Status == "fail");
        var manual = checks.Count(c => c.Status == "manual");
        return new GoLiveReadinessDto(fail == 0, pass, warn, fail, manual, checks, clock.UtcNow);
    }

    public async Task<Result<GoLiveSignOffDto>> SignOffAsync(SignOffRequest request, CancellationToken ct = default)
    {
        var env = request.Environment?.Trim().ToLowerInvariant() ?? "production";
        if (!Environments.Contains(env)) return Error.Validation("operations.environment", "Environnement invalide.");
        var verdict = request.Verdict?.Trim().ToLowerInvariant() ?? "";
        if (verdict is not ("go" or "go_with_reservations" or "no_go"))
            return Error.Validation("operations.verdict", "Verdict invalide (go / go_with_reservations / no_go).");

        var readiness = await GoLiveReadinessAsync(ct);
        if (!readiness.CanGoLive && verdict == "go" && !request.Force)
            return Error.Conflict("operations.not_ready",
                $"{readiness.Failed} point(s) de contrôle en échec — corrigez, choisissez « go_with_reservations », ou forcez avec un motif.");
        if (request.Force && string.IsNullOrWhiteSpace(request.ForceReason))
            return Error.Validation("operations.force_reason", "Un motif est requis pour forcer le Go-Live malgré des points en échec.");

        var snapshot = JsonSerializer.Serialize(readiness, Web);
        var id = Guid.NewGuid();
        await db.Database.ExecuteSqlAsync($"""
            INSERT INTO nexora.golive_signoff
                (id, company_id, environment, verdict, checklist_snapshot, can_go_live, forced, force_reason, notes, signed_by)
            VALUES ({id}, {Company}, {env}, {verdict}, {snapshot}::jsonb, {readiness.CanGoLive},
                    {request.Force}, {request.ForceReason}, {request.Notes}, {tenant.UserId})
            """, ct);
        await uow.SaveChangesAsync(ct);
        return Result.Success((await ListSignOffsAsync(ct)).First(s => s.Id == id));
    }

    private sealed record SignOffRow(Guid Id, string Environment, string Verdict, bool CanGoLive, bool Forced,
        string? ForceReason, string? Notes, Guid? SignedBy, DateTimeOffset SignedAt, string Snapshot);

    public async Task<IReadOnlyList<GoLiveSignOffDto>> ListSignOffsAsync(CancellationToken ct = default)
    {
        var rows = await db.Database.SqlQuery<SignOffRow>($"""
            SELECT id AS "Id", environment AS "Environment", verdict AS "Verdict", can_go_live AS "CanGoLive",
                   forced AS "Forced", force_reason AS "ForceReason", notes AS "Notes", signed_by AS "SignedBy",
                   signed_at AS "SignedAt", checklist_snapshot::text AS "Snapshot"
            FROM nexora.golive_signoff WHERE company_id = {Company} ORDER BY signed_at DESC LIMIT 50
            """).ToListAsync(ct);
        return rows.Select(r => new GoLiveSignOffDto(r.Id, r.Environment, r.Verdict, r.CanGoLive, r.Forced,
            r.ForceReason, r.Notes, r.SignedBy, r.SignedAt,
            JsonSerializer.Deserialize<GoLiveReadinessDto>(r.Snapshot, Web)!)).ToList();
    }

    // -------------------------------------------------------- Déploiements (§34, §38-39, §56)

    private sealed record DeploymentRow(Guid Id, string Version, string Environment, string Status, string? SchemaVersion,
        string? Notes, Guid? RolledBackFrom, string? RollbackReason, DateTimeOffset StartedAt, DateTimeOffset? FinishedAt, Guid? CreatedBy);

    public async Task<IReadOnlyList<DeploymentDto>> ListDeploymentsAsync(CancellationToken ct = default)
    {
        var rows = await db.Database.SqlQuery<DeploymentRow>($"""
            SELECT id AS "Id", version AS "Version", environment AS "Environment", status AS "Status",
                   schema_version AS "SchemaVersion", notes AS "Notes", rolled_back_from AS "RolledBackFrom",
                   rollback_reason AS "RollbackReason", started_at AS "StartedAt", finished_at AS "FinishedAt", created_by AS "CreatedBy"
            FROM nexora.deployment ORDER BY started_at DESC LIMIT 100
            """).ToListAsync(ct);
        return rows.Select(ToDto).ToList();
    }

    public async Task<Result<DeploymentDto>> RecordDeploymentAsync(RecordDeploymentRequest request, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(request.Version) || request.Version.Trim().Length > 100)
            return Error.Validation("operations.version", "Version invalide (1–100 caractères).");
        var env = request.Environment?.Trim().ToLowerInvariant() ?? "production";
        if (!Environments.Contains(env)) return Error.Validation("operations.environment", "Environnement invalide.");

        var schemaVersion = await db.Database.SqlQuery<string>($"""
            SELECT COALESCE(max(version), '(aucune)') AS "Value" FROM nexora.schema_migration
            """).FirstAsync(ct);

        var id = Guid.NewGuid();
        await db.Database.ExecuteSqlAsync($"""
            INSERT INTO nexora.deployment (id, version, environment, status, schema_version, notes, finished_at, created_by)
            VALUES ({id}, {request.Version.Trim()}, {env}, 'succeeded', {schemaVersion}, {request.Notes}, now(), {tenant.UserId})
            """, ct);
        await uow.SaveChangesAsync(ct);
        return Result.Success((await ListDeploymentsAsync(ct)).First(d => d.Id == id));
    }

    public async Task<Result<DeploymentDto>> RollBackAsync(Guid deploymentId, RollbackRequest request, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(request.Reason)) return Error.Validation("operations.reason", "Motif de rollback requis.");

        var orig = await db.Database.SqlQuery<DeploymentRow>($"""
            SELECT id AS "Id", version AS "Version", environment AS "Environment", status AS "Status",
                   schema_version AS "SchemaVersion", notes AS "Notes", rolled_back_from AS "RolledBackFrom",
                   rollback_reason AS "RollbackReason", started_at AS "StartedAt", finished_at AS "FinishedAt", created_by AS "CreatedBy"
            FROM nexora.deployment WHERE id = {deploymentId}
            """).FirstOrDefaultAsync(ct);
        if (orig is null) return Error.NotFound("operations.deployment", "Déploiement introuvable.");
        if (orig.Status == "rolled_back") return Error.Conflict("operations.already_rolled_back", "Déploiement déjà annulé.");

        var newId = Guid.NewGuid();
        var toVersion = string.IsNullOrWhiteSpace(request.ToVersion) ? $"rollback de {orig.Version}" : request.ToVersion.Trim();
        var note = $"Retour arrière depuis {orig.Version}";
        await uow.ExecuteInTransactionAsync(async token =>
        {
            await db.Database.ExecuteSqlAsync($"""
                UPDATE nexora.deployment
                SET status = 'rolled_back', rollback_reason = {request.Reason}, updated_at = now()
                WHERE id = {deploymentId}
                """, token);
            await db.Database.ExecuteSqlAsync($"""
                INSERT INTO nexora.deployment
                    (id, version, environment, status, schema_version, notes, rolled_back_from, rollback_reason, finished_at, created_by)
                VALUES ({newId}, {toVersion}, {orig.Environment}, 'succeeded', {orig.SchemaVersion},
                        {note}, {deploymentId}, {request.Reason}, now(), {tenant.UserId})
                """, token);
            return 0;
        }, ct);

        return Result.Success((await ListDeploymentsAsync(ct)).First(d => d.Id == newId));
    }

    // ------------------------------------------------------------- Helpers

    private async Task<bool> ScalarBoolAsync(FormattableString sql, CancellationToken ct)
        => await db.Database.SqlQuery<bool>(sql).FirstAsync(ct);

    private static async Task<T> SafeAsync<T>(Func<Task<T>> fn, T fallback)
    {
        try { return await fn(); }
        catch { return fallback; }
    }

    private static async Task<T?> TryAsync<T>(Func<Task<T>> fn) where T : class
    {
        try { return await fn(); }
        catch { return null; }
    }

    private static DeploymentDto ToDto(DeploymentRow r) => new(r.Id, r.Version, r.Environment, r.Status, r.SchemaVersion,
        r.Notes, r.RolledBackFrom, r.RollbackReason, r.StartedAt, r.FinishedAt, r.CreatedBy);
}

// -----------------------------------------------------------------------------

/// <summary>Compteurs plateforme non sensibles pour <c>/metrics</c> (format texte Prometheus, Phase 37 §27).</summary>
public sealed class SqlPlatformMetrics(NexoraDbContext db, ITenantContext tenant) : IPlatformMetrics
{
    public async Task<IReadOnlyList<(string Name, string Help, double Value)>> GaugesAsync(CancellationToken ct = default)
    {
        using var _ = tenant.BeginSystemScope();
        var m = await db.Database.SqlQuery<MetricRow>($"""
            SELECT
              (SELECT COUNT(*)::int FROM nexora.notification_outbox WHERE status = 'pending') AS "OutboxPending",
              (SELECT COUNT(*)::int FROM nexora.notification_outbox WHERE status = 'dead')    AS "OutboxDead",
              (SELECT COUNT(*)::int FROM nexora.continuity_incident WHERE status <> 'resolved') AS "OpenIncidents",
              (SELECT COUNT(*)::int FROM nexora.integration_connector WHERE status = 'error')  AS "ConnectorsError",
              (SELECT COUNT(*)::int FROM nexora.outbound_webhook_delivery WHERE status IN ('pending','failed')) AS "WebhookDue",
              (SELECT COUNT(*)::int FROM nexora.company WHERE status = 'active') AS "ActiveCompanies"
            """).FirstAsync(ct);

        return
        [
            ("nexora_up", "1 si l'API sert les requêtes", 1),
            ("nexora_notification_outbox_pending", "Notifications externes en file", m.OutboxPending),
            ("nexora_notification_outbox_dead", "Notifications externes en échec définitif", m.OutboxDead),
            ("nexora_continuity_open_incidents", "Incidents de continuité non résolus", m.OpenIncidents),
            ("nexora_integration_connectors_error", "Connecteurs d'intégration en erreur", m.ConnectorsError),
            ("nexora_outbound_webhook_deliveries_due", "Livraisons de webhooks sortants en attente", m.WebhookDue),
            ("nexora_active_companies", "Entreprises actives", m.ActiveCompanies),
        ];
    }

    private sealed record MetricRow(int OutboxPending, int OutboxDead, int OpenIncidents, int ConnectorsError, int WebhookDue, int ActiveCompanies);
}
