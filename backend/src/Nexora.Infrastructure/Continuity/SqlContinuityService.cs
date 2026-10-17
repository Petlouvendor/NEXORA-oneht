using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Nexora.Application.Abstractions;
using Nexora.Application.Continuity;
using Nexora.Application.Notifications;
using Nexora.Application.Operations;
using Nexora.Contracts.Continuity;
using Nexora.Contracts.Notifications;
using Nexora.Domain.Common;
using Nexora.Infrastructure.Persistence;

namespace Nexora.Infrastructure.Continuity;

/// <summary>
/// Registre applicatif de continuité (Phase 36 ; étendu Phase 48 §32, §37, §39, §44-45, §55).
/// Catalogue des sauvegardes, vérification, tests de restauration, runbook d'incident, état de
/// reprise, exercices DR, verrouillage d'urgence, page de statut. La sauvegarde physique et le
/// failover restent des opérations d'infrastructure — ce service les pilote et les surveille.
/// </summary>
public sealed class SqlContinuityService(
    NexoraDbContext db, ITenantContext tenant, IUnitOfWork uow, IDateTimeProvider clock,
    INotificationService notifications, IOpsDiagnosticsService diagnostics, IMaintenanceService maintenance,
    ISessionCacheInvalidator sessionCache)
    : IContinuityService
{
    private Guid Company => tenant.ActiveCompanyId ?? throw TenantErrors.ActiveCompanyRequired();
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    private static readonly string[] Kinds =
        ["full", "incremental", "differential", "config", "documents", "snapshot", "metadata", "keys"];
    private static readonly Dictionary<string, int> FrequencyMinutes = new()
    {
        ["hourly"] = 60, ["daily"] = 1440, ["weekly"] = 10080, ["monthly"] = 44640,
    };
    private static readonly string[] RunbookLabels =
    [
        "", "Détecter", "Protéger", "Isoler", "Évaluer", "Restaurer / Basculer",
        "Vérifier", "Rouvrir", "Analyser", "Améliorer",
    ];

    // ------------------------------------------------------------- Politique

    private sealed record PolicyRow(string Name, string Kinds, string Frequency, int RetentionDays, string Destination,
        bool Encrypted, bool Immutable, int RpoTargetMinutes, int RtoTargetMinutes, string Status);

    public async Task<BackupPolicyDto> GetPolicyAsync(CancellationToken ct = default)
    {
        var p = await db.Database.SqlQuery<PolicyRow>($"""
            SELECT name AS "Name", kinds::text AS "Kinds", frequency AS "Frequency", retention_days AS "RetentionDays",
                   destination AS "Destination", encrypted AS "Encrypted", immutable AS "Immutable",
                   rpo_target_minutes AS "RpoTargetMinutes", rto_target_minutes AS "RtoTargetMinutes", status AS "Status"
            FROM nexora.backup_policy WHERE company_id = {Company}
            """).FirstOrDefaultAsync(ct);

        if (p is null)
        {
            await db.Database.ExecuteSqlAsync($"""
                INSERT INTO nexora.backup_policy (company_id, updated_by) VALUES ({Company}, {tenant.UserId})
                ON CONFLICT ON CONSTRAINT backup_policy_company_uk DO NOTHING
                """, ct);
            return new BackupPolicyDto("Politique par défaut", ["full", "config", "documents"], "daily", 30,
                "offsite", true, false, 1440, 240, "active");
        }
        return new BackupPolicyDto(p.Name, JsonSerializer.Deserialize<List<string>>(p.Kinds) ?? [], p.Frequency,
            p.RetentionDays, p.Destination, p.Encrypted, p.Immutable, p.RpoTargetMinutes, p.RtoTargetMinutes, p.Status);
    }

    public async Task<Result<BackupPolicyDto>> SetPolicyAsync(BackupPolicyDto policy, CancellationToken ct = default)
    {
        if (!FrequencyMinutes.ContainsKey(policy.Frequency)) return Error.Validation("continuity.frequency", "Fréquence invalide.");
        if (policy.RetentionDays is < 1 or > 3650) return Error.Validation("continuity.retention", "Rétention hors bornes (1–3650 j).");
        if (policy.RpoTargetMinutes is < 1 or > 10080 || policy.RtoTargetMinutes is < 1 or > 10080)
            return Error.Validation("continuity.targets", "Objectifs RPO/RTO hors bornes (1–10080 min).");
        if (policy.Destination is not ("local" or "nas" or "cloud_object" or "offsite"))
            return Error.Validation("continuity.destination", "Destination invalide.");
        var kinds = (policy.Kinds ?? []).Where(k => Kinds.Contains(k)).Distinct().ToArray();
        if (kinds.Length == 0) return Error.Validation("continuity.kinds", "Au moins un type de sauvegarde.");

        await db.Database.ExecuteSqlAsync($"""
            INSERT INTO nexora.backup_policy
                (company_id, name, kinds, frequency, retention_days, destination, encrypted, immutable,
                 rpo_target_minutes, rto_target_minutes, status, updated_by)
            VALUES ({Company}, {policy.Name ?? "Politique par défaut"}, {JsonSerializer.Serialize(kinds)}::jsonb,
                    {policy.Frequency}, {policy.RetentionDays}, {policy.Destination}, {policy.Encrypted}, {policy.Immutable},
                    {policy.RpoTargetMinutes}, {policy.RtoTargetMinutes},
                    {(policy.Status is "paused" ? "paused" : "active")}, {tenant.UserId})
            ON CONFLICT ON CONSTRAINT backup_policy_company_uk DO UPDATE SET
                name = EXCLUDED.name, kinds = EXCLUDED.kinds, frequency = EXCLUDED.frequency,
                retention_days = EXCLUDED.retention_days, destination = EXCLUDED.destination,
                encrypted = EXCLUDED.encrypted, immutable = EXCLUDED.immutable,
                rpo_target_minutes = EXCLUDED.rpo_target_minutes, rto_target_minutes = EXCLUDED.rto_target_minutes,
                status = EXCLUDED.status, updated_by = EXCLUDED.updated_by, updated_at = now()
            """, ct);
        await uow.SaveChangesAsync(ct);
        return Result.Success(await GetPolicyAsync(ct));
    }

    // ------------------------------------------------------------ Sauvegardes

    private sealed record BackupRow(Guid Id, string Kind, string Status, DateTimeOffset StartedAt, DateTimeOffset? FinishedAt,
        long? SizeBytes, string? Checksum, long? ObjectCount, DateTimeOffset CoversUpTo, string? LocationRef, bool Encrypted,
        DateTimeOffset? ImmutableUntil, DateTimeOffset? VerifiedAt, DateTimeOffset? ExpiresAt, string? Error);

    public async Task<IReadOnlyList<BackupDto>> ListBackupsAsync(CancellationToken ct = default)
    {
        var rows = await db.Database.SqlQuery<BackupRow>($"""
            SELECT id AS "Id", kind AS "Kind", status AS "Status", started_at AS "StartedAt", finished_at AS "FinishedAt",
                   size_bytes AS "SizeBytes", checksum AS "Checksum", object_count AS "ObjectCount", covers_up_to AS "CoversUpTo",
                   location_ref AS "LocationRef", encrypted AS "Encrypted", immutable_until AS "ImmutableUntil",
                   verified_at AS "VerifiedAt", expires_at AS "ExpiresAt", error AS "Error"
            FROM nexora.backup WHERE company_id = {Company} ORDER BY covers_up_to DESC LIMIT 200
            """).ToListAsync(ct);
        return rows.Select(ToDto).ToList();
    }

    public async Task<Result<BackupDto>> RegisterBackupAsync(RegisterBackupRequest r, CancellationToken ct = default)
    {
        if (!Kinds.Contains(r.Kind)) return Error.Validation("continuity.kind", $"Type inconnu. Attendu : {string.Join(", ", Kinds)}.");
        if (r.CoversUpTo > clock.UtcNow.AddMinutes(5)) return Error.Validation("continuity.covers_up_to", "Le point de couverture ne peut pas être dans le futur.");
        if (r.Checksum is { } c && (c.Length != 64 || !c.All(Uri.IsHexDigit)))
            return Error.Validation("continuity.checksum", "Empreinte attendue : SHA-256 (64 hexadécimaux).");

        var policy = await GetPolicyAsync(ct);
        var expires = r.CoversUpTo.AddDays(policy.RetentionDays);
        var id = Guid.NewGuid();
        await db.Database.ExecuteSqlAsync($"""
            INSERT INTO nexora.backup
                (id, company_id, kind, status, started_at, finished_at, size_bytes, checksum, object_count,
                 covers_up_to, location_ref, encrypted, immutable_until, expires_at, error, created_by)
            VALUES ({id}, {Company}, {r.Kind}, {(r.Failed ? "failed" : "completed")}, {r.CoversUpTo}, now(),
                    {r.SizeBytes}, {r.Checksum}, {r.ObjectCount}, {r.CoversUpTo}, {r.LocationRef}, {r.Encrypted},
                    {(policy.Immutable ? expires : (DateTimeOffset?)null)}, {expires}, {r.Error}, {tenant.UserId})
            """, ct);
        await uow.SaveChangesAsync(ct);
        return Result.Success(await GetBackupAsync(id, ct) ?? throw new InvalidOperationException());
    }

    /// <summary>
    /// Appel de l'outil de restauration (audit 2026-10 éd. 2, A-051) : <c>database/scripts/restore.sh --verify</c>
    /// restaure réellement la sauvegarde dans une base jetable, puis appelle l'API avec une clé d'API. Un contrôle lancé
    /// depuis l'écran ne lit que des métadonnées saisies : il ne peut pas rendre une sauvegarde « vérifiée » ni un test
    /// « réussi » — c'est ce qui alimentait la liste de mise en production et la note de reprise.
    /// </summary>
    private bool FromRestoreTool => tenant.ApiScopes is not null;

    public async Task<Result<BackupVerificationDto>> VerifyBackupAsync(Guid backupId, CancellationToken ct = default)
    {
        var b = await LoadBackupAsync(backupId, ct);
        if (b is null) return Error.NotFound("continuity.backup", "Sauvegarde introuvable.");
        if (b.Status == "failed") return Error.Conflict("continuity.backup_failed", "Une sauvegarde en échec ne peut pas être vérifiée.");

        var live = await LiveObjectCountAsync(ct);
        var problems = new List<string>();
        if (string.IsNullOrEmpty(b.Checksum)) problems.Add("empreinte absente");
        if (b.ObjectCount is null) problems.Add("comptage d'objets absent");
        if (b.CoversUpTo > clock.UtcNow) problems.Add("point de couverture dans le futur");
        if (b.ExpiresAt is { } e && e <= clock.UtcNow) problems.Add("rétention expirée");
        if (b.ObjectCount is { } oc && oc > live * 2 + 1000) problems.Add($"comptage déclaré {oc} incohérent avec l'état actuel {live}");

        var metadataOk = problems.Count == 0;
        var ok = metadataOk && FromRestoreTool;
        var detail = !metadataOk ? string.Join(" ; ", problems)
            : ok ? "Restauration réelle réussie (outil de restauration) et contrôles d'intégrité passés."
            : "Métadonnées cohérentes — contrôle déclaratif. La sauvegarde ne sera « vérifiée » qu'après une restauration "
              + "réelle : database/scripts/restore.sh --verify.";
        var verif = JsonSerializer.Serialize(new
        {
            ok, declarative = !FromRestoreTool, detail, liveObjectCount = live, backupObjectCount = b.ObjectCount,
            checkedAt = clock.UtcNow,
        }, Web);

        await db.Database.ExecuteSqlAsync($"""
            UPDATE nexora.backup
            SET verified_at = {(ok ? clock.UtcNow : (DateTimeOffset?)null)},
                status = CASE WHEN {ok} AND status = 'completed' THEN 'verified' ELSE status END,
                verification_detail = {verif}::jsonb, updated_at = now()
            WHERE id = {backupId} AND company_id = {Company}
            """, ct);

        return Result.Success(new BackupVerificationDto(ok, detail, live, b.ObjectCount));
    }

    // --------------------------------------------------------- Restaurations

    private sealed record RestoreRow(Guid Id, Guid BackupId, string Mode, string? Target, string Status, bool? ChecksumOk,
        long? ObjectCountExpected, long? ObjectCountActual, DateTimeOffset StartedAt, DateTimeOffset? FinishedAt,
        Guid? RequestedBy, Guid? ApprovedBy);

    public async Task<IReadOnlyList<RestoreRunDto>> ListRestoreRunsAsync(CancellationToken ct = default)
    {
        var rows = await db.Database.SqlQuery<RestoreRow>($"""
            SELECT id AS "Id", backup_id AS "BackupId", mode AS "Mode", target AS "Target", status AS "Status",
                   checksum_ok AS "ChecksumOk", object_count_expected AS "ObjectCountExpected", object_count_actual AS "ObjectCountActual",
                   started_at AS "StartedAt", finished_at AS "FinishedAt", requested_by AS "RequestedBy", approved_by AS "ApprovedBy"
            FROM nexora.restore_run WHERE company_id = {Company} ORDER BY started_at DESC LIMIT 100
            """).ToListAsync(ct);
        return rows.Select(ToDto).ToList();
    }

    public async Task<Result<RestoreRunDto>> RunRestoreTestAsync(RestoreTestRequest request, CancellationToken ct = default)
    {
        var b = await LoadBackupAsync(request.BackupId, ct);
        if (b is null) return Error.NotFound("continuity.backup", "Sauvegarde introuvable.");
        if (b.Status is "failed" or "expired") return Error.Conflict("continuity.backup_unusable", "Sauvegarde en échec ou expirée.");
        if (!FromRestoreTool)
            return Error.Validation("continuity.restore_test_tool_only",
                "Un test de restauration est enregistré par l'outil de restauration (database/scripts/restore.sh --verify, "
                + "avec une clé d'API) : il restaure réellement la sauvegarde dans une base jetable. Un test déclaratif ne peut pas être « réussi ».");

        var live = await LiveObjectCountAsync(ct);
        var checksumOk = !string.IsNullOrEmpty(b.Checksum);
        var countOk = b.ObjectCount is { } oc && oc <= live * 2 + 1000;
        var passed = checksumOk && countOk && b.CoversUpTo <= clock.UtcNow && (b.ExpiresAt is null || b.ExpiresAt > clock.UtcNow);
        var report = JsonSerializer.Serialize(new
        {
            checksumPresent = checksumOk,
            objectCountConsistent = countOk,
            liveObjectCount = live,
            backupObjectCount = b.ObjectCount,
            coversUpTo = b.CoversUpTo,
            note = "Enregistré par l'outil de restauration après une restauration réelle dans une base jetable.",
        }, Web);

        var id = Guid.NewGuid();
        await db.Database.ExecuteSqlAsync($"""
            INSERT INTO nexora.restore_run
                (id, company_id, backup_id, mode, target, status, checksum_ok, object_count_expected, object_count_actual,
                 report, finished_at, requested_by)
            VALUES ({id}, {Company}, {request.BackupId}, 'test', {request.Target ?? "sandbox"},
                    {(passed ? "passed" : "failed")}, {checksumOk}, {b.ObjectCount}, {live}, {report}::jsonb, now(), {tenant.UserId})
            """, ct);
        await uow.SaveChangesAsync(ct);
        return Result.Success(await GetRestoreRunAsync(id, ct) ?? throw new InvalidOperationException());
    }

    public async Task<Result<RestoreRunDto>> RequestProductionRestoreAsync(RestoreRequest request, CancellationToken ct = default)
    {
        var b = await LoadBackupAsync(request.BackupId, ct);
        if (b is null) return Error.NotFound("continuity.backup", "Sauvegarde introuvable.");
        if (b.Status is "failed" or "expired") return Error.Conflict("continuity.backup_unusable", "Sauvegarde en échec ou expirée.");
        if (string.IsNullOrWhiteSpace(request.Target)) return Error.Validation("continuity.target", "Cible de restauration requise.");

        var id = Guid.NewGuid();
        await db.Database.ExecuteSqlAsync($"""
            INSERT INTO nexora.restore_run
                (id, company_id, backup_id, mode, target, status, object_count_expected, requested_by)
            VALUES ({id}, {Company}, {request.BackupId}, 'production', {request.Target}, 'pending_approval', {b.ObjectCount}, {tenant.UserId})
            """, ct);
        await uow.SaveChangesAsync(ct);
        return Result.Success(await GetRestoreRunAsync(id, ct) ?? throw new InvalidOperationException());
    }

    public async Task<Result<RestoreRunDto>> ApproveProductionRestoreAsync(Guid restoreRunId, CancellationToken ct = default)
    {
        var r = await db.Database.SqlQuery<RestoreRow>($"""
            SELECT id AS "Id", backup_id AS "BackupId", mode AS "Mode", target AS "Target", status AS "Status",
                   checksum_ok AS "ChecksumOk", object_count_expected AS "ObjectCountExpected", object_count_actual AS "ObjectCountActual",
                   started_at AS "StartedAt", finished_at AS "FinishedAt", requested_by AS "RequestedBy", approved_by AS "ApprovedBy"
            FROM nexora.restore_run WHERE id = {restoreRunId} AND company_id = {Company}
            """).FirstOrDefaultAsync(ct);
        if (r is null) return Error.NotFound("continuity.restore", "Demande de restauration introuvable.");
        if (r.Mode != "production" || r.Status != "pending_approval")
            return Error.Conflict("continuity.restore_state", "Cette demande n'est pas en attente d'approbation.");
        if (r.RequestedBy is { } rb && rb == tenant.UserId)
            return Error.Forbidden("continuity.self_approval", "Le demandeur ne peut pas approuver sa propre restauration (§39).");

        await db.Database.ExecuteSqlAsync($"""
            UPDATE nexora.restore_run
            SET status = 'running', approved_by = {tenant.UserId},
                report = jsonb_build_object('approved', true, 'approvedBy', {tenant.UserId}::text, 'approvedAt', {clock.UtcNow}::text,
                                            'note', 'Approuvée : à exécuter par l''outil de restauration (A-051 : l''approbation n''est pas la restauration).'),
                updated_at = now()
            WHERE id = {restoreRunId}
            """, ct);
        await uow.SaveChangesAsync(ct);
        return Result.Success((await GetRestoreRunAsync(restoreRunId, ct))!);
    }

    // --------------------------------------------------- État de reprise

    public async Task<DisasterRecoveryStatusDto> DisasterRecoveryStatusAsync(CancellationToken ct = default)
    {
        var policy = await GetPolicyAsync(ct);
        var s = await db.Database.SqlQuery<DrRow>($"""
            SELECT
              (SELECT max(covers_up_to) FROM nexora.backup WHERE company_id = {Company} AND status IN ('completed','verified')) AS "LastGood",
              (SELECT max(verified_at)  FROM nexora.backup WHERE company_id = {Company} AND status = 'verified') AS "LastVerified",
              (SELECT count(*)::int FROM nexora.backup WHERE company_id = {Company} AND status = 'completed') AS "AwaitingVerification",
              (SELECT count(*)::int FROM nexora.continuity_incident WHERE company_id = {Company} AND status <> 'resolved') AS "Unresolved",
              (SELECT count(*)::int FROM nexora.continuity_incident WHERE company_id = {Company} AND status <> 'resolved' AND severity = 'p1') AS "OpenP1"
            """).FirstAsync(ct);

        int? ageMin = s.LastGood is { } lg ? (int)(clock.UtcNow - lg).TotalMinutes : null;
        var window = FrequencyMinutes[policy.Frequency];
        var rpoBreached = ageMin is null || ageMin > policy.RpoTargetMinutes;
        var overdue = ageMin is null || ageMin > window * 3 / 2;

        var demerits = (rpoBreached ? 2 : 0) + (overdue ? 1 : 0) + s.Unresolved + s.OpenP1 * 2
            + (s.LastVerified is null || s.LastVerified < clock.UtcNow.AddDays(-7) ? 1 : 0);
        var grade = demerits switch { 0 => "A", 1 => "B", 2 or 3 => "C", 4 or 5 => "D", _ => "E" };

        return new DisasterRecoveryStatusDto(s.LastGood, ageMin, s.LastVerified,
            policy.RpoTargetMinutes, policy.RtoTargetMinutes, rpoBreached, overdue,
            s.Unresolved, s.OpenP1, s.AwaitingVerification, grade);
    }
    private sealed record DrRow(DateTimeOffset? LastGood, DateTimeOffset? LastVerified, int AwaitingVerification, int Unresolved, int OpenP1);

    // ------------------------------------------------------------ Incidents

    private static readonly string[] Severities = ["p1", "p2", "p3", "p4"];
    private static readonly string[] Categories =
    [
        "server_failure", "disk_failure", "db_corruption", "human_error", "accidental_deletion", "ransomware",
        "network_loss", "cloud_provider", "power", "site_disaster", "deploy_error", "other",
    ];
    /// <summary>Phase 47 §29-30 : mêmes sévérité/runbook/post-mortem/audit, un second domaine seulement.</summary>
    private static readonly string[] SecurityCategories =
        ["breach", "unauthorized_access", "credential_leak", "data_exfiltration", "malware", "phishing"];
    private static readonly string[] Domains = ["availability", "security"];
    private static readonly string[] DetectedVia = ["monitoring", "alert", "user_report", "scheduled_test", "deploy"];

    private sealed record IncidentRow(Guid Id, string Severity, string Category, string Title, string DetectedVia, string Status,
        short RunbookStep, int? RpoActualMinutes, int? RtoActualMinutes, string? PostMortem, DateTimeOffset OpenedAt,
        DateTimeOffset? ResolvedAt, string Domain, string ContainmentActions);

    public async Task<IReadOnlyList<ContinuityIncidentDto>> ListIncidentsAsync(string? status, CancellationToken ct = default)
        => await ListIncidentsAsync(status, null, ct);

    public async Task<IReadOnlyList<ContinuityIncidentDto>> ListIncidentsAsync(string? status, string? domain, CancellationToken ct = default)
    {
        var rows = await db.Database.SqlQuery<IncidentRow>($"""
            SELECT id AS "Id", severity AS "Severity", category AS "Category", title AS "Title", detected_via AS "DetectedVia",
                   status AS "Status", runbook_step AS "RunbookStep", rpo_actual_minutes AS "RpoActualMinutes",
                   rto_actual_minutes AS "RtoActualMinutes", post_mortem::text AS "PostMortem", opened_at AS "OpenedAt",
                   resolved_at AS "ResolvedAt", domain AS "Domain", containment_actions::text AS "ContainmentActions"
            FROM nexora.continuity_incident
            WHERE company_id = {Company} AND ({status}::text IS NULL OR status = {status}::text)
              AND ({domain}::text IS NULL OR domain = {domain}::text)
            ORDER BY (status <> 'resolved') DESC, opened_at DESC LIMIT 200
            """).ToListAsync(ct);
        return rows.Select(ToDto).ToList();
    }

    public async Task<Result<ContinuityIncidentDto>> OpenIncidentAsync(OpenIncidentRequest r, CancellationToken ct = default)
    {
        if (!Severities.Contains(r.Severity)) return Error.Validation("continuity.severity", "Sévérité invalide (p1..p4).");
        if (!Domains.Contains(r.Domain)) return Error.Validation("continuity.domain", "Domaine invalide (availability/security).");
        var validCategories = r.Domain == "security" ? SecurityCategories : Categories;
        if (!validCategories.Contains(r.Category))
            return Error.Validation("continuity.category", "Catégorie invalide pour ce domaine d'incident.");
        if (!DetectedVia.Contains(r.DetectedVia)) return Error.Validation("continuity.detected_via", "Origine de détection invalide.");
        if (string.IsNullOrWhiteSpace(r.Title)) return Error.Validation("continuity.title", "Titre requis.");

        var id = Guid.NewGuid();
        await db.Database.ExecuteSqlAsync($"""
            INSERT INTO nexora.continuity_incident (id, company_id, severity, category, title, detected_via, opened_by, domain)
            VALUES ({id}, {Company}, {r.Severity}, {r.Category}, {r.Title.Trim()}, {r.DetectedVia}, {tenant.UserId}, {r.Domain})
            """, ct);
        await uow.SaveChangesAsync(ct);
        return Result.Success((await GetIncidentAsync(id, ct))!);
    }

    public async Task<Result<ContinuityIncidentDto>> AdvanceIncidentAsync(Guid incidentId, AdvanceIncidentRequest r, CancellationToken ct = default)
    {
        if (r.RunbookStep is < 1 or > 9) return Error.Validation("continuity.step", "Étape du runbook entre 1 et 9.");
        var status = r.Status;
        if (status is not null && status is not ("open" or "mitigating" or "restoring" or "verifying"))
            return Error.Validation("continuity.status", "Statut invalide (utilisez resolve pour clôturer).");

        var n = await db.Database.ExecuteSqlAsync($"""
            UPDATE nexora.continuity_incident
            SET runbook_step = {r.RunbookStep},
                status = COALESCE({status}::text, status),
                updated_at = now()
            WHERE id = {incidentId} AND company_id = {Company} AND status <> 'resolved'
            """, ct);
        return n == 0
            ? Error.NotFound("continuity.incident", "Incident introuvable ou déjà résolu.")
            : Result.Success((await GetIncidentAsync(incidentId, ct))!);
    }

    public async Task<Result<ContinuityIncidentDto>> ResolveIncidentAsync(Guid incidentId, ResolveIncidentRequest r, CancellationToken ct = default)
    {
        var required = new[] { "cause", "impact", "actions", "prevention", "owner" };
        var missing = required.Where(k => !r.PostMortem.TryGetValue(k, out var v) || string.IsNullOrWhiteSpace(v)).ToArray();
        if (missing.Length > 0) return Error.Validation("continuity.post_mortem", $"Post-mortem incomplet : {string.Join(", ", missing)} (§45).");

        var pm = JsonSerializer.Serialize(r.PostMortem, Web);
        var n = await db.Database.ExecuteSqlAsync($"""
            UPDATE nexora.continuity_incident
            SET status = 'resolved', runbook_step = 9, resolved_at = now(), resolved_by = {tenant.UserId},
                rpo_actual_minutes = {r.RpoActualMinutes}, rto_actual_minutes = {r.RtoActualMinutes},
                post_mortem = {pm}::jsonb, updated_at = now()
            WHERE id = {incidentId} AND company_id = {Company} AND status <> 'resolved'
            """, ct);
        return n == 0
            ? Error.NotFound("continuity.incident", "Incident introuvable ou déjà résolu.")
            : Result.Success((await GetIncidentAsync(incidentId, ct))!);
    }

    // --------------------------------------------------- Contrôles de santé (§36-37)

    public async Task<int> RunHealthChecksAsync(CancellationToken ct = default)
    {
        var policy = await GetPolicyAsync(ct);
        if (policy.Status == "paused") return 0;

        var status = await DisasterRecoveryStatusAsync(ct);
        var alerts = 0;

        if (status.BackupOverdue)
            alerts += await AlertAdminsAsync("continuity.backup_overdue",
                "Sauvegarde en retard",
                status.LastGoodBackupAt is { } t
                    ? $"Dernière sauvegarde saine il y a {status.LastGoodBackupAgeMinutes} min (objectif : {policy.RpoTargetMinutes} min, fréquence {policy.Frequency})."
                    : "Aucune sauvegarde enregistrée pour cette entreprise.",
                priority: status.RpoBreached ? 1 : 2,
                dedup: $"continuity.backup_overdue:{Company}:{clock.UtcNow:yyyyMMdd}", ct);

        var failedTest = await db.Database.SqlQuery<int>($"""
            SELECT COUNT(*)::int AS "Value" FROM nexora.restore_run
            WHERE company_id = {Company} AND mode = 'test' AND status = 'failed' AND started_at > now() - interval '7 days'
            """).FirstAsync(ct);
        if (failedTest > 0)
            alerts += await AlertAdminsAsync("continuity.restore_test_failed",
                "Test de restauration en échec",
                $"{failedTest} test(s) de restauration ont échoué ces 7 derniers jours — la fiabilité des sauvegardes n'est plus prouvée (§2).",
                priority: 2, dedup: $"continuity.restore_test_failed:{Company}:{clock.UtcNow:yyyyMMdd}", ct);

        return alerts;
    }

    // ------------------------------------------------------- §32, §55 Verrouillage d'urgence

    public async Task<Result<LockdownResultDto>> LockDownAsync(Guid incidentId, CancellationToken ct = default)
    {
        var incident = await db.Database.SqlQuery<string>($"""
            SELECT status AS "Value" FROM nexora.continuity_incident WHERE id = {incidentId} AND company_id = {Company}
            """).FirstOrDefaultAsync(ct);
        if (incident is null) return Error.NotFound("continuity.incident_not_found", "Incident introuvable.");
        if (incident == "resolved") return Error.Conflict("continuity.incident_resolved", "Cet incident est déjà résolu.");

        // Tout utilisateur ayant un rôle actif dans cette entreprise — jamais un accès résiduel (§32).
        var affectedUsers = (await db.Database.SqlQuery<Guid>($"""
            SELECT DISTINCT ur.user_id AS "Value"
            FROM nexora.user_role ur JOIN nexora.access_scope sc ON sc.id = ur.scope_id
            WHERE sc.company_id = {Company} AND ur.revoked_at IS NULL
            """).ToListAsync(ct)).ToArray();

        var at = clock.UtcNow;
        var sessionsRevoked = 0;
        var apiKeysRevoked = 0;
        if (affectedUsers.Length > 0)
        {
            await db.Database.ExecuteSqlAsync($"""
                UPDATE nexora.app_user SET security_epoch = {at}, updated_at = now() WHERE id = ANY({affectedUsers})
                """, ct);
            sessionsRevoked = await db.Database.ExecuteSqlAsync($"""
                UPDATE nexora.user_session SET revoked_at = {at}, revoked_by = {tenant.UserId},
                       revoke_reason = 'Verrouillage d''urgence (incident continuité)', updated_at = now()
                WHERE revoked_at IS NULL AND user_id = ANY({affectedUsers})
                """, ct);
            foreach (var u in affectedUsers) sessionCache.InvalidateUser(u);
        }
        apiKeysRevoked = await db.Database.ExecuteSqlAsync($"""
            UPDATE nexora.api_key SET revoked_at = {at}, revoked_by = {tenant.UserId}, updated_at = now()
            WHERE company_id = {Company} AND revoked_at IS NULL
            """, ct);

        var action = JsonSerializer.Serialize(new ContainmentActionDto("lockdown", at, tenant.UserId!.Value,
            $"{sessionsRevoked} session(s) et {apiKeysRevoked} clé(s) d'API révoquée(s), {affectedUsers.Length} utilisateur(s) affecté(s)."), Web);
        await db.Database.ExecuteSqlAsync($"""
            UPDATE nexora.continuity_incident
            SET containment_actions = containment_actions || {action}::jsonb, updated_at = now()
            WHERE id = {incidentId}
            """, ct);
        await uow.SaveChangesAsync(ct);

        return Result.Success(new LockdownResultDto(sessionsRevoked, apiKeysRevoked, affectedUsers.Length, at));
    }

    // ------------------------------------------------------- §44-45 Exercices DR

    private static readonly string[] DrTestTypes =
    [
        "tabletop", "restore", "failover_db", "failover_app", "network_outage",
        "server_outage", "storage_outage", "ransomware_simulation", "site_unavailable", "return_to_normal",
    ];

    private sealed record DrTestRow(Guid Id, string TestType, string Title, DateOnly? ScheduledFor, DateTimeOffset? ExecutedAt,
        string Status, string? Scope, string Participants, string? Findings, string? CorrectiveActions,
        DateOnly? NextTestDue, DateTimeOffset CreatedAt);

    private const string DrTestSelect = """
        SELECT id AS "Id", test_type AS "TestType", title AS "Title", scheduled_for AS "ScheduledFor",
               executed_at AS "ExecutedAt", status AS "Status", scope AS "Scope", participants::text AS "Participants",
               findings AS "Findings", corrective_actions AS "CorrectiveActions", next_test_due AS "NextTestDue",
               created_at AS "CreatedAt"
        FROM nexora.dr_test
        """;

    private static DrTestDto ToDto(DrTestRow r) => new(r.Id, r.TestType, r.Title, r.ScheduledFor, r.ExecutedAt, r.Status,
        r.Scope, JsonSerializer.Deserialize<List<string>>(r.Participants) ?? [], r.Findings, r.CorrectiveActions,
        r.NextTestDue, r.CreatedAt);

    public async Task<IReadOnlyList<DrTestDto>> ListDrTestsAsync(string? status, CancellationToken ct = default)
        => (await db.Database.SqlQueryRaw<DrTestRow>(
                DrTestSelect + " WHERE company_id = {0} AND ({1}::text IS NULL OR status = {1}) ORDER BY scheduled_for DESC NULLS LAST, created_at DESC",
                Company, status!).ToListAsync(ct))
            .Select(ToDto).ToList();

    public async Task<Result<DrTestDto>> ScheduleDrTestAsync(ScheduleDrTestRequest request, CancellationToken ct = default)
    {
        if (!DrTestTypes.Contains(request.TestType))
            return Error.Validation("continuity.dr_test_type", $"Type d'exercice invalide. Attendu : {string.Join(", ", DrTestTypes)}.");
        if (string.IsNullOrWhiteSpace(request.Title)) return Error.Validation("continuity.dr_test_title", "Titre requis.");

        var id = Guid.NewGuid();
        var participants = JsonSerializer.Serialize(request.Participants ?? [], Web);
        await db.Database.ExecuteSqlAsync($"""
            INSERT INTO nexora.dr_test (id, company_id, test_type, title, scheduled_for, scope, participants, created_by)
            VALUES ({id}, {Company}, {request.TestType}, {request.Title.Trim()}, {request.ScheduledFor}, {request.Scope},
                    {participants}::jsonb, {tenant.UserId})
            """, ct);
        await uow.SaveChangesAsync(ct);
        var row = await db.Database.SqlQueryRaw<DrTestRow>(DrTestSelect + " WHERE id = {0}", id).FirstAsync(ct);
        return Result.Success(ToDto(row));
    }

    public async Task<Result<DrTestDto>> RecordDrTestResultAsync(Guid id, RecordDrTestResultRequest request, CancellationToken ct = default)
    {
        var status = request.Passed ? "passed" : "failed";
        var n = await db.Database.ExecuteSqlAsync($"""
            UPDATE nexora.dr_test
            SET status = {status}, executed_at = now(), findings = {request.Findings},
                corrective_actions = {request.CorrectiveActions}, next_test_due = {request.NextTestDue}, updated_at = now()
            WHERE id = {id} AND company_id = {Company} AND status = 'scheduled'
            """, ct);
        if (n == 0) return Error.NotFound("continuity.dr_test_not_found", "Exercice introuvable ou déjà consigné.");
        await uow.SaveChangesAsync(ct);
        var row = await db.Database.SqlQueryRaw<DrTestRow>(DrTestSelect + " WHERE id = {0}", id).FirstAsync(ct);
        return Result.Success(ToDto(row));
    }

    // ------------------------------------------------------- §7, §39 Page de statut

    /// <summary>Criticité par contrôle de diagnostic (Phase 41) — référence statique, §7.</summary>
    private static readonly Dictionary<string, string> ComponentCriticality = new()
    {
        ["database"] = "critical", ["backup"] = "critical",
        ["sync"] = "high", ["storage"] = "high", ["queue"] = "high",
        ["connector"] = "normal", ["license"] = "normal",
        ["email"] = "low", ["sms"] = "low", ["printer"] = "low", ["clock"] = "low",
    };

    public async Task<StatusPageDto> StatusPageAsync(CancellationToken ct = default)
    {
        var checks = await diagnostics.RunAllAsync(ct);
        var components = checks.Select(c => new StatusComponentDto(
            c.Key, c.Label, ComponentCriticality.GetValueOrDefault(c.Key, "normal"), c.Status, c.Detail)).ToList();

        var drStatus = await DisasterRecoveryStatusAsync(ct);
        var windows = await maintenance.ListAsync(ct);
        var underMaintenance = windows.Any(w => w.Status == "in_progress");

        var overall =
            underMaintenance ? "maintenance" :
            drStatus.OpenP1Incidents > 0 ? "incident" :
            drStatus.UnresolvedIncidents > 0 || components.Any(c => c.Criticality == "critical" && c.Status == "fail") ? "degraded" :
            "operational";

        var recent = await ListIncidentsAsync(null, ct);
        return new StatusPageDto(overall, components, recent.Take(10).ToList(), clock.UtcNow);
    }

    private async Task<int> AlertAdminsAsync(string category, string title, string message, int priority, string dedup, CancellationToken ct)
    {
        var admins = await db.Database.SqlQuery<Guid>($"""
            SELECT DISTINCT ur.user_id AS "Value"
            FROM nexora.user_role ur
            JOIN nexora.role_permission rp ON rp.role_id = ur.role_id
            JOIN nexora.permission p ON p.id = rp.permission_id
            JOIN nexora.access_scope sc ON sc.id = ur.scope_id
            WHERE ur.revoked_at IS NULL AND p.code = 'continuity.view'
              AND (sc.company_id = {Company} OR sc.scope_type IN ('GROUP','PLATFORM'))
            LIMIT 20
            """).ToListAsync(ct);
        if (admins.Count == 0) return 0;

        var res = await notifications.NotifyAsync(new NotifyRequest(
            Category: category,
            Recipients: admins,
            TemplateKey: "continuity.alert",
            Variables: new Dictionary<string, string?> { ["title"] = title, ["message"] = message },
            Priority: priority,
            Severity: priority == 1 ? "error" : "warning",
            Source: "alert",
            DedupKey: dedup), ct);
        return res.NotificationsCreated;
    }

    // ------------------------------------------------------------- Helpers
    // Note : EF `SqlQuery` traite chaque `{…}` comme un paramètre — le SELECT est donc écrit
    // en toutes lettres dans chaque requête (pas de fragment interpolé).

    private async Task<BackupRow?> LoadBackupAsync(Guid id, CancellationToken ct)
        => await db.Database.SqlQuery<BackupRow>($"""
            SELECT id AS "Id", kind AS "Kind", status AS "Status", started_at AS "StartedAt", finished_at AS "FinishedAt",
                   size_bytes AS "SizeBytes", checksum AS "Checksum", object_count AS "ObjectCount", covers_up_to AS "CoversUpTo",
                   location_ref AS "LocationRef", encrypted AS "Encrypted", immutable_until AS "ImmutableUntil",
                   verified_at AS "VerifiedAt", expires_at AS "ExpiresAt", error AS "Error"
            FROM nexora.backup WHERE id = {id} AND company_id = {Company}
            """).FirstOrDefaultAsync(ct);

    private async Task<BackupDto?> GetBackupAsync(Guid id, CancellationToken ct)
        => await LoadBackupAsync(id, ct) is { } r ? ToDto(r) : null;

    private async Task<RestoreRunDto?> GetRestoreRunAsync(Guid id, CancellationToken ct)
        => await db.Database.SqlQuery<RestoreRow>($"""
            SELECT id AS "Id", backup_id AS "BackupId", mode AS "Mode", target AS "Target", status AS "Status",
                   checksum_ok AS "ChecksumOk", object_count_expected AS "ObjectCountExpected", object_count_actual AS "ObjectCountActual",
                   started_at AS "StartedAt", finished_at AS "FinishedAt", requested_by AS "RequestedBy", approved_by AS "ApprovedBy"
            FROM nexora.restore_run WHERE id = {id} AND company_id = {Company}
            """).FirstOrDefaultAsync(ct) is { } r ? ToDto(r) : null;

    private async Task<ContinuityIncidentDto?> GetIncidentAsync(Guid id, CancellationToken ct)
        => (await ListIncidentsAsync(null, ct)).FirstOrDefault(i => i.Id == id);

    private async Task<long> LiveObjectCountAsync(CancellationToken ct)
        => await db.Database.SqlQuery<long>($"""
            SELECT (
              (SELECT count(*) FROM nexora.sale WHERE company_id = {Company})
            + (SELECT count(*) FROM nexora.journal_entry WHERE company_id = {Company})
            + (SELECT count(*) FROM nexora.product WHERE company_id = {Company})
            + (SELECT count(*) FROM nexora.customer WHERE company_id = {Company})
            + (SELECT count(*) FROM nexora.issued_document WHERE company_id = {Company})
            )::bigint AS "Value"
            """).FirstAsync(ct);

    private static BackupDto ToDto(BackupRow r) => new(r.Id, r.Kind, r.Status, r.StartedAt, r.FinishedAt, r.SizeBytes,
        r.Checksum, r.ObjectCount, r.CoversUpTo, r.LocationRef, r.Encrypted, r.ImmutableUntil, r.VerifiedAt, r.ExpiresAt, r.Error);

    private static RestoreRunDto ToDto(RestoreRow r) => new(r.Id, r.BackupId, r.Mode, r.Target, r.Status, r.ChecksumOk,
        r.ObjectCountExpected, r.ObjectCountActual, r.StartedAt, r.FinishedAt, r.RequestedBy, r.ApprovedBy);

    private static ContinuityIncidentDto ToDto(IncidentRow r)
    {
        Dictionary<string, string>? pm = null;
        if (r.PostMortem is { } j && j != "null")
            pm = JsonSerializer.Deserialize<Dictionary<string, string>>(j);
        var containment = JsonSerializer.Deserialize<List<ContainmentActionDto>>(r.ContainmentActions, Web) ?? [];
        return new ContinuityIncidentDto(r.Id, r.Severity, r.Category, r.Title, r.DetectedVia, r.Status, r.RunbookStep,
            RunbookLabels[Math.Clamp((int)r.RunbookStep, 1, 9)], r.RpoActualMinutes, r.RtoActualMinutes, pm, r.OpenedAt, r.ResolvedAt,
            r.Domain, containment);
    }
}
