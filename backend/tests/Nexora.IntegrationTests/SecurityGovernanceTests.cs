using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Nexora.Application.Abstractions;
using Nexora.Application.Continuity;
using Nexora.Application.Security;
using Nexora.Contracts.Continuity;
using Nexora.Contracts.Security;

namespace Nexora.IntegrationTests;

/// <summary>
/// Sécurité, IAM & Conformité (Phase 47). Accès d'urgence « break-glass » (§12, réutilise le
/// moteur JIT de la Phase 22) ; vulnérabilités (§42-44) ; certificats (§53) ; conformité
/// auto-évaluée (§35) ; incidents de sécurité (§29-30, extension de <c>continuity_incident</c>) ;
/// score & tableau de bord (§56-57). Findings/certificates/compliance sont des tables PLATEFORME
/// (comme <c>platform_plan</c>) — partagées entre tous les tests de la collection ; les assertions
/// portent donc sur la ligne créée elle-même ou sur des deltas, jamais sur un compte absolu.
/// </summary>
[Collection("finance")]
public sealed class SecurityGovernanceTests(FinanceEngineFixture fx)
{
    private static readonly Guid Company = Guid.Parse("dce00000-0000-0000-0000-0000000000c2");
    private static readonly Guid Direction = Guid.Parse("dce00000-0000-0000-0000-0000000000d1");
    private static readonly Guid Compta = Guid.Parse("dce00000-0000-0000-0000-0000000000d2");

    private AsyncServiceScope Scope() => fx.Provider.CreateAsyncScope();

    private static void As(IServiceProvider sp, Guid user)
    {
        var tc = (TestTenantContext)sp.GetRequiredService<ITenantContext>();
        tc.SetUser(user);
        tc.SetScope([Company], [], Company);
    }

    // ---- §6, §12 : jamais auto-approuvé ; l'octroi réel réutilise le moteur JIT (Phase 22) -----
    [Fact]
    public async Task Emergency_access_requires_a_third_party_and_grants_a_real_time_boxed_role()
    {
        await using var s = Scope();
        As(s.ServiceProvider, Compta);
        var emergency = s.ServiceProvider.GetRequiredService<IEmergencyAccessService>();

        var requested = await emergency.RequestAsync(new RequestEmergencyAccessRequest(
            "manager", "Panne de l'approbateur habituel, besoin urgent d'un accès manager.", 2));
        requested.IsSuccess.Should().BeTrue(requested.Error?.Message);
        requested.Value!.Status.Should().Be("pending");

        // le demandeur ne peut pas décider de sa propre demande
        var selfDecide = await emergency.DecideAsync(requested.Value.Id, new DecideEmergencyAccessRequest(true, null));
        selfDecide.IsSuccess.Should().BeFalse();
        selfDecide.Error!.Code.Should().Be("emergency_access.self_decision");

        // un tiers (Direction, owner_direction ⊇ manager) approuve
        As(s.ServiceProvider, Direction);
        var approved = await emergency.DecideAsync(requested.Value.Id, new DecideEmergencyAccessRequest(true, "Confirmé par téléphone."));
        approved.IsSuccess.Should().BeTrue(approved.Error?.Message);
        approved.Value!.Status.Should().Be("approved");
        approved.Value.IsActive.Should().BeTrue();
        approved.Value.ExpiresAt.Should().NotBeNull().And.BeCloseTo(DateTimeOffset.UtcNow.AddHours(2), TimeSpan.FromMinutes(1));

        // l'octroi est réel : Compta (chief_accountant) détient désormais une permission propre au
        // rôle manager qu'elle ne possédait pas via son propre rôle (§8 : preuve de l'octroi, pas
        // seulement un changement de statut).
        var permissions = s.ServiceProvider.GetRequiredService<IPermissionService>();
        var profile = await permissions.LoadProfileAsync(Compta);
        profile.PermissionsFor(Company).Should().Contain("catalog.view");

        // révoquer retire l'accès immédiatement — pas seulement un changement de statut cosmétique
        var revoked = await emergency.RevokeAsync(requested.Value.Id, "Incident résolu, accès non-utile.");
        revoked.IsSuccess.Should().BeTrue(revoked.Error?.Message);

        var profileAfter = await permissions.LoadProfileAsync(Compta);
        profileAfter.PermissionsFor(Company).Should().NotContain("catalog.view");
    }

    // ---- §12 : motif obligatoire, durée bornée (1 à 8 heures) -----------------------------------
    [Fact]
    public async Task Emergency_access_validates_reason_length_and_duration_bounds()
    {
        await using var s = Scope();
        As(s.ServiceProvider, Compta);
        var emergency = s.ServiceProvider.GetRequiredService<IEmergencyAccessService>();

        (await emergency.RequestAsync(new RequestEmergencyAccessRequest("manager", "trop bref", 2)))
            .Error!.Code.Should().Be("emergency_access.reason");
        (await emergency.RequestAsync(new RequestEmergencyAccessRequest("manager", "Motif suffisamment détaillé pour l'urgence.", 12)))
            .Error!.Code.Should().Be("emergency_access.duration");
        (await emergency.RequestAsync(new RequestEmergencyAccessRequest("manager", "Motif suffisamment détaillé pour l'urgence.", 0)))
            .Error!.Code.Should().Be("emergency_access.duration");
    }

    // ---- §6 : un refus est définitif, aucun octroi n'a lieu -------------------------------------
    [Fact]
    public async Task Emergency_access_rejection_grants_nothing_and_cannot_be_replayed()
    {
        await using var s = Scope();
        As(s.ServiceProvider, Compta);
        var emergency = s.ServiceProvider.GetRequiredService<IEmergencyAccessService>();
        var requested = await emergency.RequestAsync(new RequestEmergencyAccessRequest(
            "manager", "Demande à rejeter pour le test du workflow d'urgence.", 1));

        As(s.ServiceProvider, Direction);
        var rejected = await emergency.DecideAsync(requested.Value!.Id, new DecideEmergencyAccessRequest(false, "Pas justifié."));
        rejected.IsSuccess.Should().BeTrue(rejected.Error?.Message);
        rejected.Value!.Status.Should().Be("rejected");
        rejected.Value.IsActive.Should().BeFalse();

        (await emergency.DecideAsync(requested.Value.Id, new DecideEmergencyAccessRequest(true, null)))
            .Error!.Code.Should().Be("emergency_access.not_pending");
    }

    // ---- §42-44 : SLA par sévérité, cycle de vie d'un constat ------------------------------------
    [Fact]
    public async Task Security_findings_assign_sla_by_severity_and_track_resolution()
    {
        await using var s = Scope();
        As(s.ServiceProvider, Direction);
        var findings = s.ServiceProvider.GetRequiredService<ISecurityFindingService>();

        var bad = await findings.ReportAsync(new ReportFindingRequest("manual", "not_a_severity", "Test"));
        bad.Error!.Code.Should().Be("finding.severity");

        var critical = await findings.ReportAsync(new ReportFindingRequest("sca", "critical", "Dépendance vulnérable détectée", CveId: "CVE-2026-0001"));
        critical.IsSuccess.Should().BeTrue(critical.Error?.Message);
        critical.Value!.SlaDueAt.Should().NotBeNull().And.BeCloseTo(DateTimeOffset.UtcNow.AddDays(7), TimeSpan.FromMinutes(1));

        var low = await findings.ReportAsync(new ReportFindingRequest("manual", "low", "Amélioration mineure"));
        low.Value!.SlaDueAt.Should().BeCloseTo(DateTimeOffset.UtcNow.AddDays(180), TimeSpan.FromMinutes(1));

        var resolved = await findings.UpdateStatusAsync(critical.Value.Id, new UpdateFindingStatusRequest("resolved", "Dépendance mise à jour."));
        resolved.IsSuccess.Should().BeTrue(resolved.Error?.Message);
        resolved.Value!.ResolvedAt.Should().NotBeNull();
        resolved.Value.RemediationNotes.Should().Be("Dépendance mise à jour.");
    }

    // ---- §53 : classification par échéance, rotation avec traçabilité ---------------------------
    [Fact]
    public async Task Certificates_are_classified_by_expiry_and_rotation_preserves_lineage()
    {
        await using var s = Scope();
        As(s.ServiceProvider, Direction);
        var certs = s.ServiceProvider.GetRequiredService<ISecurityCertificateService>();
        var today = DateOnly.FromDateTime(DateTime.UtcNow);

        var expiring = await certs.RegisterAsync(new RegisterCertificateRequest(
            "api.nexora.test", "tls", "Test CA", "aa:bb", today.AddDays(-30), today.AddDays(10)));
        expiring.Value!.Status.Should().Be("expiring_soon");

        var healthy = await certs.RegisterAsync(new RegisterCertificateRequest(
            "webhook-signing.nexora.test", "webhook", null, null, today, today.AddDays(400)));
        healthy.Value!.Status.Should().Be("active");

        var expired = await certs.RegisterAsync(new RegisterCertificateRequest(
            "legacy.nexora.test", "other", null, null, today.AddDays(-800), today.AddDays(-1)));
        expired.Value!.Status.Should().Be("expired");

        var rotated = await certs.RotateAsync(expiring.Value.Id, new RotateCertificateRequest(
            "api.nexora.test", today, today.AddDays(365), "cc:dd"));
        rotated.IsSuccess.Should().BeTrue(rotated.Error?.Message);
        rotated.Value!.Status.Should().Be("active");

        var all = await certs.ListAsync();
        all.Should().ContainSingle(c => c.Id == expiring.Value.Id).Which.Status.Should().Be("rotated");
        all.Should().ContainSingle(c => c.Id == expiring.Value.Id).Which.RotatedTo.Should().Be(rotated.Value.Id);
    }

    // ---- §35 : auto-évaluation honnête, révisable avec preuve ------------------------------------
    [Fact]
    public async Task Compliance_controls_are_seeded_honestly_and_can_be_reviewed_with_evidence()
    {
        await using var s = Scope();
        As(s.ServiceProvider, Direction);
        var compliance = s.ServiceProvider.GetRequiredService<IComplianceRegistryService>();

        var iam = await compliance.ListAsync("iam");
        iam.Should().ContainSingle(c => c.Code == "iam-mfa").Which.Status.Should().Be("partial"); // MFA existe, application non bloquante
        var gap = iam.Should().ContainSingle(c => c.Code == "iam-jit").Subject;

        var reviewed = await compliance.ReviewAsync(gap.Id, new ReviewComplianceControlRequest(
            "met", "V035, UserAssignmentService — revérifié en Phase 47.", "Confirmé sans changement."));
        reviewed.IsSuccess.Should().BeTrue(reviewed.Error?.Message);
        reviewed.Value!.ReviewedBy.Should().NotBeNull();
        reviewed.Value.ReviewedAt.Should().NotBeNull();

        (await compliance.ReviewAsync(gap.Id, new ReviewComplianceControlRequest("not_a_status", null, null)))
            .Error!.Code.Should().Be("compliance.status");
    }

    // ---- §29-30 : un second domaine sur le MÊME registre d'incidents, jamais dupliqué -----------
    [Fact]
    public async Task Continuity_incidents_extend_to_a_security_domain_with_dedicated_categories()
    {
        await using var s = Scope();
        As(s.ServiceProvider, Direction);
        var continuity = s.ServiceProvider.GetRequiredService<IContinuityService>();

        // une catégorie de disponibilité est refusée pour le domaine sécurité
        var wrongCategory = await continuity.OpenIncidentAsync(new OpenIncidentRequest(
            "p2", "ransomware", "Mauvaise catégorie pour ce domaine", Domain: "security"));
        wrongCategory.Error!.Code.Should().Be("continuity.category");

        var security = await continuity.OpenIncidentAsync(new OpenIncidentRequest(
            "p1", "unauthorized_access", "Tentative d'accès non autorisé détectée", Domain: "security"));
        security.IsSuccess.Should().BeTrue(security.Error?.Message);
        security.Value!.Domain.Should().Be("security");

        var availability = await continuity.OpenIncidentAsync(new OpenIncidentRequest(
            "p3", "server_failure", "Panne serveur ordinaire", Domain: "availability"));
        availability.Value!.Domain.Should().Be("availability");

        var securityOnly = await continuity.ListIncidentsAsync(null, "security");
        securityOnly.Should().Contain(i => i.Id == security.Value.Id);
        securityOnly.Should().NotContain(i => i.Id == availability.Value.Id);
    }

    // ---- §56-57 : le tableau de bord reflète les signaux des autres services de sécurité ---------
    [Fact]
    public async Task Security_dashboard_reflects_new_findings_certificates_and_incidents()
    {
        await using var s = Scope();
        As(s.ServiceProvider, Direction);
        var dashboard = s.ServiceProvider.GetRequiredService<ISecurityScoreService>();
        var findings = s.ServiceProvider.GetRequiredService<ISecurityFindingService>();
        var certs = s.ServiceProvider.GetRequiredService<ISecurityCertificateService>();
        var continuity = s.ServiceProvider.GetRequiredService<IContinuityService>();

        var before = await dashboard.DashboardAsync();

        await findings.ReportAsync(new ReportFindingRequest("pentest", "critical", "Test tableau de bord"));
        await certs.RegisterAsync(new RegisterCertificateRequest("dashboard-test.nexora.test", "tls", null, null,
            DateOnly.FromDateTime(DateTime.UtcNow), DateOnly.FromDateTime(DateTime.UtcNow).AddDays(5)));
        await continuity.OpenIncidentAsync(new OpenIncidentRequest("p2", "phishing", "Test tableau de bord", Domain: "security"));

        var after = await dashboard.DashboardAsync();
        after.OpenFindingsCritical.Should().BeGreaterThanOrEqualTo(before.OpenFindingsCritical + 1);
        after.CertificatesExpiringSoon.Should().BeGreaterThanOrEqualTo(before.CertificatesExpiringSoon + 1);
        after.OpenSecurityIncidents.Should().Be(before.OpenSecurityIncidents + 1);
        after.Score.Score.Should().BeInRange(0, 100);
        after.Score.Grade.Should().BeOneOf("A", "B", "C", "D", "E");
    }
}
