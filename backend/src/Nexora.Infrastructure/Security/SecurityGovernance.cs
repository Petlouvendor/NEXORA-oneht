using Microsoft.EntityFrameworkCore;
using Nexora.Application.Abstractions;
using Nexora.Application.Identity;
using Nexora.Application.Security;
using Nexora.Contracts.Identity;
using Nexora.Contracts.Security;
using Nexora.Domain.Common;
using Nexora.Infrastructure.Persistence;

namespace Nexora.Infrastructure.Security;

// =============================================================================
// Sécurité, IAM & Conformité (Phase 47) — vulnérabilités, certificats, conformité auto-évaluée,
// accès d'urgence (réutilise le moteur JIT de la Phase 22), score & tableau de bord.
// =============================================================================

/// <summary>Registre des vulnérabilités / résultats SAST-DAST-SCA-pentest (§42-44). Table plateforme.</summary>
public sealed class SqlSecurityFindingService(NexoraDbContext db, ITenantContext tenant, IUnitOfWork uow, IDateTimeProvider clock)
    : ISecurityFindingService
{
    private static readonly string[] Sources = ["sast", "dast", "sca", "dependency", "pentest", "manual"];
    private static readonly string[] Severities = ["critical", "high", "medium", "low", "info"];
    private static readonly string[] Statuses = ["open", "triaged", "remediating", "resolved", "accepted_risk", "false_positive"];
    private static readonly Dictionary<string, int> SlaDaysBySeverity = new()
    {
        ["critical"] = 7, ["high"] = 30, ["medium"] = 90, ["low"] = 180, ["info"] = 365,
    };

    private const string Select = """
        SELECT id AS "Id", source AS "Source", severity AS "Severity", title AS "Title", description AS "Description",
               cve_id AS "CveId", component AS "Component", status AS "Status", sla_due_at AS "SlaDueAt",
               discovered_at AS "DiscoveredAt", resolved_at AS "ResolvedAt", remediation_notes AS "RemediationNotes"
        FROM nexora.security_finding
        """;

    private sealed record Row(Guid Id, string Source, string Severity, string Title, string? Description, string? CveId,
        string? Component, string Status, DateTimeOffset? SlaDueAt, DateTimeOffset DiscoveredAt, DateTimeOffset? ResolvedAt,
        string? RemediationNotes);

    private static SecurityFindingDto ToDto(Row r) => new(r.Id, r.Source, r.Severity, r.Title, r.Description, r.CveId,
        r.Component, r.Status, r.SlaDueAt, r.DiscoveredAt, r.ResolvedAt, r.RemediationNotes);

    public async Task<IReadOnlyList<SecurityFindingDto>> ListAsync(string? status, string? severity, CancellationToken ct = default)
    {
        var rows = await db.Database.SqlQueryRaw<Row>(
            Select + " WHERE ({0}::text IS NULL OR status = {0}) AND ({1}::text IS NULL OR severity = {1}) " +
            "ORDER BY (status NOT IN ('resolved','false_positive')) DESC, discovered_at DESC",
            status!, severity!).ToListAsync(ct);
        return rows.Select(ToDto).ToList();
    }

    public async Task<Result<SecurityFindingDto>> ReportAsync(ReportFindingRequest r, CancellationToken ct = default)
    {
        if (!Sources.Contains(r.Source)) return Error.Validation("finding.source", "Source invalide (sast/dast/sca/dependency/pentest/manual).");
        if (!Severities.Contains(r.Severity)) return Error.Validation("finding.severity", "Sévérité invalide.");
        if (string.IsNullOrWhiteSpace(r.Title)) return Error.Validation("finding.title", "Titre requis.");

        var id = Guid.NewGuid();
        var slaDue = clock.UtcNow.AddDays(SlaDaysBySeverity[r.Severity]);
        await db.Database.ExecuteSqlAsync($"""
            INSERT INTO nexora.security_finding (id, source, severity, title, description, cve_id, component, sla_due_at, reported_by)
            VALUES ({id}, {r.Source}, {r.Severity}, {r.Title.Trim()}, {r.Description}, {r.CveId}, {r.Component}, {slaDue}, {tenant.UserId})
            """, ct);
        await uow.SaveChangesAsync(ct);
        return Result.Success(ToDto((await db.Database.SqlQueryRaw<Row>(Select + " WHERE id = {0}", id).FirstAsync(ct))));
    }

    public async Task<Result<SecurityFindingDto>> UpdateStatusAsync(Guid id, UpdateFindingStatusRequest r, CancellationToken ct = default)
    {
        if (!Statuses.Contains(r.Status)) return Error.Validation("finding.status", "Statut invalide.");
        var resolved = r.Status is "resolved" or "false_positive";
        var n = await db.Database.ExecuteSqlAsync($"""
            UPDATE nexora.security_finding
            SET status = {r.Status}, remediation_notes = COALESCE({r.RemediationNotes}, remediation_notes),
                resolved_at = CASE WHEN {resolved} AND resolved_at IS NULL THEN now() ELSE resolved_at END,
                resolved_by = CASE WHEN {resolved} AND resolved_at IS NULL THEN {tenant.UserId} ELSE resolved_by END,
                updated_at = now()
            WHERE id = {id}
            """, ct);
        if (n == 0) return Error.NotFound("finding.not_found", "Constat introuvable.");
        await uow.SaveChangesAsync(ct);
        return Result.Success(ToDto((await db.Database.SqlQueryRaw<Row>(Select + " WHERE id = {0}", id).FirstAsync(ct))));
    }
}

// -----------------------------------------------------------------------------

/// <summary>Inventaire de certificats (§53). Table plateforme.</summary>
public sealed class SqlSecurityCertificateService(NexoraDbContext db, IUnitOfWork uow, IDateTimeProvider clock)
    : ISecurityCertificateService
{
    private static readonly string[] UsageTypes = ["tls", "signing", "webhook", "other"];
    private const int ExpiringSoonDays = 30;

    private const string Select = """
        SELECT id AS "Id", name AS "Name", usage_type AS "UsageType", issuer AS "Issuer", fingerprint AS "Fingerprint",
               not_before AS "NotBefore", not_after AS "NotAfter", status AS "Status", rotated_to AS "RotatedTo", notes AS "Notes"
        FROM nexora.security_certificate
        """;

    private sealed record Row(Guid Id, string Name, string UsageType, string? Issuer, string? Fingerprint,
        DateOnly? NotBefore, DateOnly NotAfter, string Status, Guid? RotatedTo, string? Notes);

    private static SecurityCertificateDto ToDto(Row r) =>
        new(r.Id, r.Name, r.UsageType, r.Issuer, r.Fingerprint, r.NotBefore, r.NotAfter, r.Status, r.RotatedTo, r.Notes);

    public async Task<IReadOnlyList<SecurityCertificateDto>> ListAsync(CancellationToken ct = default)
        => (await db.Database.SqlQueryRaw<Row>(Select + " ORDER BY not_after").ToListAsync(ct)).Select(ToDto).ToList();

    public async Task<Result<SecurityCertificateDto>> RegisterAsync(RegisterCertificateRequest r, CancellationToken ct = default)
    {
        if (!UsageTypes.Contains(r.UsageType)) return Error.Validation("certificate.usage_type", "Usage invalide (tls/signing/webhook/other).");
        if (string.IsNullOrWhiteSpace(r.Name)) return Error.Validation("certificate.name", "Nom requis.");
        var today = DateOnly.FromDateTime(clock.UtcNow.UtcDateTime);
        var status = r.NotAfter <= today ? "expired" : r.NotAfter <= today.AddDays(ExpiringSoonDays) ? "expiring_soon" : "active";

        var id = Guid.NewGuid();
        await db.Database.ExecuteSqlAsync($"""
            INSERT INTO nexora.security_certificate (id, name, usage_type, issuer, fingerprint, not_before, not_after, status, notes)
            VALUES ({id}, {r.Name.Trim()}, {r.UsageType}, {r.Issuer}, {r.Fingerprint}, {r.NotBefore}, {r.NotAfter}, {status}, {r.Notes})
            """, ct);
        await uow.SaveChangesAsync(ct);
        return Result.Success(ToDto((await db.Database.SqlQueryRaw<Row>(Select + " WHERE id = {0}", id).FirstAsync(ct))));
    }

    public async Task<Result<SecurityCertificateDto>> RotateAsync(Guid id, RotateCertificateRequest r, CancellationToken ct = default)
    {
        var existing = await db.Database.SqlQueryRaw<Row>(Select + " WHERE id = {0}", id).FirstOrDefaultAsync(ct);
        if (existing is null) return Error.NotFound("certificate.not_found", "Certificat introuvable.");

        var today = DateOnly.FromDateTime(clock.UtcNow.UtcDateTime);
        var status = r.NotAfter <= today ? "expired" : r.NotAfter <= today.AddDays(ExpiringSoonDays) ? "expiring_soon" : "active";
        var newId = Guid.NewGuid();
        await uow.ExecuteInTransactionAsync(async token =>
        {
            await db.Database.ExecuteSqlAsync($"""
                INSERT INTO nexora.security_certificate (id, name, usage_type, issuer, fingerprint, not_before, not_after, status)
                VALUES ({newId}, {r.Name.Trim()}, {existing.UsageType}, {existing.Issuer}, {r.Fingerprint}, {r.NotBefore}, {r.NotAfter}, {status})
                """, token);
            await db.Database.ExecuteSqlAsync($"""
                UPDATE nexora.security_certificate SET status = 'rotated', rotated_to = {newId}, updated_at = now() WHERE id = {id}
                """, token);
            return 0;
        }, ct);
        return Result.Success(ToDto((await db.Database.SqlQueryRaw<Row>(Select + " WHERE id = {0}", newId).FirstAsync(ct))));
    }

    public async Task<int> RefreshExpiryStatusAsync(CancellationToken ct = default)
    {
        var today = DateOnly.FromDateTime(clock.UtcNow.UtcDateTime);
        var n = await db.Database.ExecuteSqlAsync($"""
            UPDATE nexora.security_certificate
            SET status = CASE
                    WHEN not_after <= {today} THEN 'expired'
                    WHEN not_after <= {today.AddDays(ExpiringSoonDays)} THEN 'expiring_soon'
                    ELSE 'active'
                END,
                updated_at = now()
            WHERE status IN ('active', 'expiring_soon', 'expired')
            """, ct);
        await uow.SaveChangesAsync(ct);
        return n;
    }
}

// -----------------------------------------------------------------------------

/// <summary>Registre de conformité auto-évalué (§35). Table plateforme.</summary>
public sealed class SqlComplianceRegistryService(NexoraDbContext db, ITenantContext tenant, IUnitOfWork uow, IDateTimeProvider clock)
    : IComplianceRegistryService
{
    private static readonly string[] Statuses = ["met", "partial", "not_met", "not_applicable", "not_assessed"];

    private const string Select = """
        SELECT id AS "Id", code AS "Code", domain AS "Domain", title AS "Title", description AS "Description",
               status AS "Status", evidence_ref AS "EvidenceRef", reviewed_by AS "ReviewedBy",
               reviewed_at AS "ReviewedAt", review_notes AS "ReviewNotes"
        FROM nexora.compliance_control
        """;

    private sealed record Row(Guid Id, string Code, string Domain, string Title, string? Description, string Status,
        string? EvidenceRef, Guid? ReviewedBy, DateTimeOffset? ReviewedAt, string? ReviewNotes);

    private static ComplianceControlDto ToDto(Row r) =>
        new(r.Id, r.Code, r.Domain, r.Title, r.Description, r.Status, r.EvidenceRef, r.ReviewedBy, r.ReviewedAt, r.ReviewNotes);

    public async Task<IReadOnlyList<ComplianceControlDto>> ListAsync(string? domain, CancellationToken ct = default)
        => (await db.Database.SqlQueryRaw<Row>(Select + " WHERE ({0}::text IS NULL OR domain = {0}) ORDER BY domain, code", domain!)
            .ToListAsync(ct)).Select(ToDto).ToList();

    public async Task<Result<ComplianceControlDto>> ReviewAsync(Guid id, ReviewComplianceControlRequest r, CancellationToken ct = default)
    {
        if (!Statuses.Contains(r.Status)) return Error.Validation("compliance.status", "Statut invalide.");
        var n = await db.Database.ExecuteSqlAsync($"""
            UPDATE nexora.compliance_control
            SET status = {r.Status}, evidence_ref = COALESCE({r.EvidenceRef}, evidence_ref), review_notes = {r.ReviewNotes},
                reviewed_by = {tenant.UserId}, reviewed_at = {clock.UtcNow}, updated_at = now()
            WHERE id = {id}
            """, ct);
        if (n == 0) return Error.NotFound("compliance.not_found", "Contrôle introuvable.");
        await uow.SaveChangesAsync(ct);
        return Result.Success(ToDto((await db.Database.SqlQueryRaw<Row>(Select + " WHERE id = {0}", id).FirstAsync(ct))));
    }
}

// -----------------------------------------------------------------------------

/// <summary>
/// Accès d'urgence « break-glass » (§12). Réutilise intégralement le moteur JIT de la Phase 22
/// (<see cref="IUserAssignmentService"/>) — cette table ne porte que la demande et sa décision,
/// l'octroi réel de permission reste celui, déjà testé, de <c>GrantAsync</c>. Jamais auto-approuvé.
/// </summary>
public sealed class SqlEmergencyAccessService(
    NexoraDbContext db, ITenantContext tenant, IUnitOfWork uow, IDateTimeProvider clock,
    IUserAssignmentService assignments, ISecurityEventLog events) : IEmergencyAccessService
{
    private Guid Company => tenant.ActiveCompanyId ?? throw TenantErrors.ActiveCompanyRequired();
    private Guid CurrentUser => tenant.UserId ?? throw new InvalidOperationException("Utilisateur requis.");

    private const string Select = """
        SELECT g.id AS "Id", g.requested_by AS "RequestedBy", g.role_name AS "RoleName", g.scope_type AS "ScopeType",
               g.establishment_id AS "EstablishmentId", g.reason AS "Reason",
               g.requested_duration_hours AS "RequestedDurationHours", g.status AS "Status", g.requested_at AS "RequestedAt",
               g.decided_by AS "DecidedBy", g.decided_at AS "DecidedAt", g.decision_note AS "DecisionNote",
               g.underlying_assignment_id AS "UnderlyingAssignmentId", g.revoked_at AS "RevokedAt",
               ur.valid_until AS "ExpiresAt", ur.revoked_at AS "AssignmentRevokedAt"
        FROM nexora.emergency_access_grant g
        LEFT JOIN nexora.user_role ur ON ur.id = g.underlying_assignment_id
        """;

    private sealed record Row(Guid Id, Guid RequestedBy, string RoleName, string ScopeType, Guid? EstablishmentId,
        string Reason, short RequestedDurationHours, string Status, DateTimeOffset RequestedAt, Guid? DecidedBy,
        DateTimeOffset? DecidedAt, string? DecisionNote, Guid? UnderlyingAssignmentId, DateTimeOffset? RevokedAt,
        DateTimeOffset? ExpiresAt, DateTimeOffset? AssignmentRevokedAt);

    private EmergencyAccessGrantDto ToDto(Row r)
    {
        var isActive = r.Status == "approved" && r.AssignmentRevokedAt is null
            && (r.ExpiresAt is null || r.ExpiresAt > clock.UtcNow);
        return new EmergencyAccessGrantDto(r.Id, r.RequestedBy, r.RoleName, r.ScopeType, r.EstablishmentId, r.Reason,
            r.RequestedDurationHours, r.Status, r.RequestedAt, r.DecidedBy, r.DecidedAt, r.DecisionNote, r.RevokedAt,
            r.ExpiresAt, isActive);
    }

    public async Task<IReadOnlyList<EmergencyAccessGrantDto>> ListAsync(string? status, CancellationToken ct = default)
        => (await db.Database.SqlQueryRaw<Row>(
                Select + " WHERE g.company_id = {0} AND ({1}::text IS NULL OR g.status = {1}) ORDER BY g.requested_at DESC",
                Company, status!).ToListAsync(ct))
            .Select(ToDto).ToList();

    public async Task<Result<EmergencyAccessGrantDto>> RequestAsync(RequestEmergencyAccessRequest r, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(r.Reason) || r.Reason.Trim().Length < 10)
            return Error.Validation("emergency_access.reason", "Motif requis (≥ 10 caractères, §12).");
        if (r.RequestedDurationHours is < 1 or > 8)
            return Error.Validation("emergency_access.duration", "Durée hors bornes (1 à 8 heures, §12).");
        var scopeType = r.ScopeType.Trim().ToUpperInvariant();
        if (scopeType is not ("COMPANY" or "ESTABLISHMENT"))
            return Error.Validation("emergency_access.scope_type", "Périmètre autorisé : COMPANY ou ESTABLISHMENT.");
        if (string.IsNullOrWhiteSpace(r.RoleName)) return Error.Validation("emergency_access.role", "Rôle requis.");

        var id = Guid.NewGuid();
        await db.Database.ExecuteSqlAsync($"""
            INSERT INTO nexora.emergency_access_grant
                (id, company_id, requested_by, role_name, scope_type, establishment_id, reason, requested_duration_hours)
            VALUES ({id}, {Company}, {CurrentUser}, {r.RoleName.Trim()}, {scopeType}, {r.EstablishmentId},
                    {r.Reason.Trim()}, {r.RequestedDurationHours})
            """, ct);
        await uow.SaveChangesAsync(ct);
        await events.RecordAsync("security.emergency_access_requested", CurrentUser, Company, detail: r.Reason.Trim(), ct: ct);
        return Result.Success(ToDto((await db.Database.SqlQueryRaw<Row>(Select + " WHERE g.id = {0}", id).FirstAsync(ct))));
    }

    public async Task<Result<EmergencyAccessGrantDto>> DecideAsync(Guid id, DecideEmergencyAccessRequest decision, CancellationToken ct = default)
    {
        var row = await db.Database.SqlQueryRaw<Row>(Select + " WHERE g.id = {0} AND g.company_id = {1}", id, Company).FirstOrDefaultAsync(ct);
        if (row is null) return Error.NotFound("emergency_access.not_found", "Demande introuvable.");
        if (row.Status != "pending") return Error.Conflict("emergency_access.not_pending", "Demande déjà tranchée.");
        // Séparation des responsabilités (§6) : jamais auto-approuvé, même par un super-administrateur.
        if (row.RequestedBy == CurrentUser)
            return Error.Forbidden("emergency_access.self_decision", "Vous ne pouvez pas décider de votre propre demande (§6).");

        if (!decision.Approve)
        {
            await db.Database.ExecuteSqlAsync($"""
                UPDATE nexora.emergency_access_grant
                SET status = 'rejected', decided_by = {CurrentUser}, decided_at = now(), decision_note = {decision.Note}, updated_at = now()
                WHERE id = {id}
                """, ct);
            await uow.SaveChangesAsync(ct);
            await events.RecordAsync("security.emergency_access_rejected", row.RequestedBy, Company, ct: ct);
            return Result.Success(ToDto((await db.Database.SqlQueryRaw<Row>(Select + " WHERE g.id = {0}", id).FirstAsync(ct))));
        }

        var validFrom = clock.UtcNow;
        var validUntil = validFrom.AddHours(row.RequestedDurationHours);
        // Réutilise le moteur JIT de la Phase 22 — hérite gratuitement de sa garde anti-élévation
        // de privilège (l'approbateur ne peut accorder que ce qu'il détient lui-même).
        var granted = await assignments.GrantAsync(row.RequestedBy, new GrantAssignmentRequest(
            row.RoleName, row.ScopeType, Company, row.EstablishmentId, validFrom, validUntil,
            $"Accès d'urgence (break-glass) : {row.Reason}"), ct);
        if (!granted.IsSuccess) return granted.Error!;

        await db.Database.ExecuteSqlAsync($"""
            UPDATE nexora.emergency_access_grant
            SET status = 'approved', decided_by = {CurrentUser}, decided_at = now(), decision_note = {decision.Note},
                underlying_assignment_id = {granted.Value}, updated_at = now()
            WHERE id = {id}
            """, ct);
        await uow.SaveChangesAsync(ct);
        await events.RecordAsync("security.emergency_access_approved", row.RequestedBy, Company,
            detail: $"rôle {row.RoleName}, {row.RequestedDurationHours}h", ct: ct);
        return Result.Success(ToDto((await db.Database.SqlQueryRaw<Row>(Select + " WHERE g.id = {0}", id).FirstAsync(ct))));
    }

    public async Task<Result> RevokeAsync(Guid id, string reason, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(reason)) return Error.Validation("emergency_access.reason", "Motif de révocation requis.");
        var row = await db.Database.SqlQueryRaw<Row>(Select + " WHERE g.id = {0} AND g.company_id = {1}", id, Company).FirstOrDefaultAsync(ct);
        if (row is null) return Error.NotFound("emergency_access.not_found", "Demande introuvable.");
        if (row.Status != "approved") return Error.Conflict("emergency_access.not_active", "Cet accès n'est pas actif.");

        if (row.UnderlyingAssignmentId is { } assignmentId)
            await assignments.RevokeAsync(assignmentId, new RevokeAssignmentRequest(reason), ct);

        await db.Database.ExecuteSqlAsync($"""
            UPDATE nexora.emergency_access_grant SET status = 'revoked', revoked_at = now(), revoked_by = {CurrentUser}, updated_at = now()
            WHERE id = {id}
            """, ct);
        await uow.SaveChangesAsync(ct);
        await events.RecordAsync("security.emergency_access_revoked", row.RequestedBy, Company, detail: reason, ct: ct);
        return Result.Success();
    }
}

// -----------------------------------------------------------------------------

/// <summary>
/// Score & tableau de bord de sécurité (§56-57) — agrège des signaux déjà produits par d'autres
/// services, calculé à la lecture, jamais un nouveau stockage.
/// </summary>
public sealed class SqlSecurityScoreService(NexoraDbContext db, ITenantContext tenant, IDateTimeProvider clock) : ISecurityScoreService
{
    private Guid Company => tenant.ActiveCompanyId ?? throw TenantErrors.ActiveCompanyRequired();

    private sealed record CompanyStatsRow(int TotalUsers, int MfaEnrolledUsers, int ActiveSessions, int TrustedDevices,
        int OpenFraudSignals, int PendingApprovals, int OpenSecurityIncidents, int PendingEmergencyAccess);
    private sealed record PlatformStatsRow(int OpenFindingsCritical, int OpenFindingsHigh, int CertificatesExpiringSoon);

    public async Task<SecurityScoreDto> ComputeAsync(CancellationToken ct = default)
    {
        var (company, platform) = await LoadStatsAsync(ct);
        var factors = new List<SecurityScoreFactorDto>();

        var mfaScore = company.TotalUsers == 0 ? 25 : (int)Math.Round(25.0 * company.MfaEnrolledUsers / company.TotalUsers);
        factors.Add(new SecurityScoreFactorDto("mfa_coverage", "Couverture MFA", mfaScore, 25,
            $"{company.MfaEnrolledUsers}/{company.TotalUsers} utilisateur(s) avec MFA activée."));

        var findingsScore = Math.Max(0, 20 - platform.OpenFindingsCritical * 10 - platform.OpenFindingsHigh * 3);
        factors.Add(new SecurityScoreFactorDto("vulnerabilities", "Vulnérabilités ouvertes", findingsScore, 20,
            $"{platform.OpenFindingsCritical} critique(s), {platform.OpenFindingsHigh} élevée(s) en cours."));

        var certScore = Math.Max(0, 15 - platform.CertificatesExpiringSoon * 5);
        factors.Add(new SecurityScoreFactorDto("certificates", "Certificats à échéance", certScore, 15,
            $"{platform.CertificatesExpiringSoon} certificat(s) bientôt expiré(s) ou expiré(s)."));

        var incidentScore = Math.Max(0, 15 - company.OpenSecurityIncidents * 5);
        factors.Add(new SecurityScoreFactorDto("incidents", "Incidents de sécurité ouverts", incidentScore, 15,
            $"{company.OpenSecurityIncidents} incident(s) de sécurité non résolu(s)."));

        var fraudScore = Math.Max(0, 15 - company.OpenFraudSignals * 3);
        factors.Add(new SecurityScoreFactorDto("fraud_signals", "Signaux anti-fraude non revus", fraudScore, 15,
            $"{company.OpenFraudSignals} signal/signaux non traité(s)."));

        var emergencyScore = Math.Max(0, 10 - company.PendingEmergencyAccess * 5);
        factors.Add(new SecurityScoreFactorDto("emergency_access", "Accès d'urgence en attente", emergencyScore, 10,
            $"{company.PendingEmergencyAccess} demande(s) d'accès d'urgence en attente de décision."));

        var total = factors.Sum(f => f.Score);
        var grade = total switch { >= 90 => "A", >= 75 => "B", >= 60 => "C", >= 40 => "D", _ => "E" };
        return new SecurityScoreDto(total, grade, factors, clock.UtcNow);
    }

    public async Task<SecurityDashboardDto> DashboardAsync(CancellationToken ct = default)
    {
        var score = await ComputeAsync(ct);
        var (company, platform) = await LoadStatsAsync(ct);
        return new SecurityDashboardDto(score, company.ActiveSessions, company.TrustedDevices, company.OpenFraudSignals,
            company.PendingApprovals, platform.OpenFindingsCritical, platform.OpenFindingsHigh, platform.CertificatesExpiringSoon,
            company.OpenSecurityIncidents, company.PendingEmergencyAccess, company.MfaEnrolledUsers, company.TotalUsers);
    }

    private async Task<(CompanyStatsRow Company, PlatformStatsRow Platform)> LoadStatsAsync(CancellationToken ct)
    {
        var companyId = Company;
        var company = await db.Database.SqlQuery<CompanyStatsRow>($"""
            SELECT
                (SELECT count(DISTINCT ur.user_id)::int FROM nexora.user_role ur
                    JOIN nexora.access_scope sc ON sc.id = ur.scope_id
                    WHERE sc.company_id = {companyId} AND ur.revoked_at IS NULL) AS "TotalUsers",
                (SELECT count(DISTINCT ur.user_id)::int FROM nexora.user_role ur
                    JOIN nexora.access_scope sc ON sc.id = ur.scope_id
                    JOIN nexora.app_user u ON u.id = ur.user_id
                    WHERE sc.company_id = {companyId} AND ur.revoked_at IS NULL AND u.mfa_enabled) AS "MfaEnrolledUsers",
                (SELECT count(*)::int FROM nexora.user_session s
                    JOIN nexora.user_role ur ON ur.user_id = s.user_id
                    JOIN nexora.access_scope sc ON sc.id = ur.scope_id
                    WHERE sc.company_id = {companyId} AND s.revoked_at IS NULL AND s.expires_at > now()) AS "ActiveSessions",
                (SELECT count(*)::int FROM nexora.user_session s
                    JOIN nexora.user_role ur ON ur.user_id = s.user_id
                    JOIN nexora.access_scope sc ON sc.id = ur.scope_id
                    WHERE sc.company_id = {companyId} AND s.revoked_at IS NULL AND s.trusted) AS "TrustedDevices",
                (SELECT count(*)::int FROM nexora.fraud_signal WHERE company_id = {companyId} AND status = 'open') AS "OpenFraudSignals",
                (SELECT count(*)::int FROM nexora.approval_request WHERE company_id = {companyId} AND status = 'pending') AS "PendingApprovals",
                (SELECT count(*)::int FROM nexora.continuity_incident
                    WHERE company_id = {companyId} AND domain = 'security' AND status <> 'resolved') AS "OpenSecurityIncidents",
                (SELECT count(*)::int FROM nexora.emergency_access_grant
                    WHERE company_id = {companyId} AND status = 'pending') AS "PendingEmergencyAccess"
            """).FirstAsync(ct);

        var platform = await db.Database.SqlQuery<PlatformStatsRow>($"""
            SELECT
                (SELECT count(*)::int FROM nexora.security_finding
                    WHERE severity = 'critical' AND status NOT IN ('resolved','false_positive')) AS "OpenFindingsCritical",
                (SELECT count(*)::int FROM nexora.security_finding
                    WHERE severity = 'high' AND status NOT IN ('resolved','false_positive')) AS "OpenFindingsHigh",
                (SELECT count(*)::int FROM nexora.security_certificate WHERE status IN ('expiring_soon','expired')) AS "CertificatesExpiringSoon"
            """).FirstAsync(ct);

        return (company, platform);
    }
}
