using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Nexora.Application.Abstractions;
using Nexora.Application.Continuity;
using Nexora.Application.Integrations;
using Nexora.Contracts.Continuity;
using Nexora.Contracts.Integrations;
using Npgsql;

namespace Nexora.IntegrationTests;

/// <summary>Registre de continuité — sauvegardes, vérification, tests de restauration, incidents (Phase 36 §48).</summary>
[Collection("finance")]
public sealed class ContinuityRegisterTests(FinanceEngineFixture fx)
{
    private static readonly Guid Company = Guid.Parse("dce00000-0000-0000-0000-0000000000c1");
    private static readonly Guid Direction = Guid.Parse("dce00000-0000-0000-0000-0000000000d1");
    private static readonly Guid Compta = Guid.Parse("dce00000-0000-0000-0000-0000000000d2");

    private AsyncServiceScope Scope() => fx.Provider.CreateAsyncScope();

    private async Task Sql(string sql)
    {
        await using var c = new NpgsqlConnection(fx.TestConnectionString);
        await c.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, c);
        await cmd.ExecuteNonQueryAsync();
    }
    private async Task<T?> Scalar<T>(string sql)
    {
        await using var c = new NpgsqlConnection(fx.TestConnectionString);
        await c.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, c);
        var v = await cmd.ExecuteScalarAsync();
        return v is null or DBNull ? default : (T)v;
    }

    private static (IContinuityService Svc, TestTenantContext Tc) Ctx(IServiceProvider sp, Guid user)
    {
        var tc = (TestTenantContext)sp.GetRequiredService<ITenantContext>();
        tc.SetUser(user);
        tc.SetScope([Company], [], Company);
        return (sp.GetRequiredService<IContinuityService>(), tc);
    }

    private static string Sha() => new('a', 64);

    // ---- §7, §13 : l'expiration dérive de la rétention de la politique ----
    [Fact]
    public async Task Register_backup_computes_expiry_from_the_policy_retention()
    {
        await using var s = Scope();
        var (svc, _) = Ctx(s.ServiceProvider, Direction);
        await svc.SetPolicyAsync((await svc.GetPolicyAsync()) with { RetentionDays = 14 });

        var covers = DateTimeOffset.UtcNow.AddMinutes(-5);
        var res = await svc.RegisterBackupAsync(new RegisterBackupRequest("full", covers, SizeBytes: 1024, Checksum: Sha(), ObjectCount: 10, LocationRef: "nexora_full.dump"));

        res.IsSuccess.Should().BeTrue(res.Error?.Message);
        res.Value!.Status.Should().Be("completed");
        res.Value.ExpiresAt.Should().BeCloseTo(covers.AddDays(14), TimeSpan.FromMinutes(1));
    }

    // ---- §21 : la vérification passe les contrôles et bascule le statut à « verified » ----
    [Fact]
    public async Task Verify_passes_the_integrity_checks_and_flips_status_to_verified()
    {
        await using var s = Scope();
        var (svc, _) = Ctx(s.ServiceProvider, Direction);
        var b = await svc.RegisterBackupAsync(new RegisterBackupRequest("full", DateTimeOffset.UtcNow.AddMinutes(-1), Checksum: Sha(), ObjectCount: 5));

        var verif = await svc.VerifyBackupAsync(b.Value!.Id);

        verif.IsSuccess.Should().BeTrue(verif.Error?.Message);
        verif.Value!.Ok.Should().BeTrue(verif.Value.Detail);
        (await Scalar<string>($"SELECT status FROM nexora.backup WHERE id = '{b.Value.Id}'")).Should().Be("verified");
        (await Scalar<DateTime?>($"SELECT verified_at FROM nexora.backup WHERE id = '{b.Value.Id}'")).Should().NotBeNull();
    }

    // ---- §2 : « une sauvegarde n'est fiable que si vérifiée » — empreinte absente → échec ----
    [Fact]
    public async Task Verify_fails_when_the_checksum_is_missing()
    {
        await using var s = Scope();
        var (svc, _) = Ctx(s.ServiceProvider, Direction);
        var b = await svc.RegisterBackupAsync(new RegisterBackupRequest("config", DateTimeOffset.UtcNow.AddMinutes(-1), ObjectCount: 5));

        var verif = await svc.VerifyBackupAsync(b.Value!.Id);

        verif.Value!.Ok.Should().BeFalse();
        verif.Value.Detail.Should().Contain("empreinte");
        (await Scalar<string>($"SELECT status FROM nexora.backup WHERE id = '{b.Value.Id}'")).Should().Be("completed");
    }

    // ---- §21 : un test de restauration enregistre un run et un verdict ----
    [Fact]
    public async Task Restore_test_records_a_run_and_a_verdict()
    {
        await using var s = Scope();
        var (svc, _) = Ctx(s.ServiceProvider, Direction);
        var b = await svc.RegisterBackupAsync(new RegisterBackupRequest("full", DateTimeOffset.UtcNow.AddMinutes(-1), Checksum: Sha(), ObjectCount: 3));

        var run = await svc.RunRestoreTestAsync(new RestoreTestRequest(b.Value!.Id, "sandbox"));

        run.IsSuccess.Should().BeTrue(run.Error?.Message);
        run.Value!.Mode.Should().Be("test");
        run.Value.Status.Should().Be("passed");
        run.Value.ChecksumOk.Should().BeTrue();
        (await svc.ListRestoreRunsAsync()).Should().Contain(r => r.Id == run.Value.Id);
    }

    // ---- §39 : une restauration en production exige une seconde personne ----
    [Fact]
    public async Task A_production_restore_requires_a_second_person()
    {
        var backupId = Guid.NewGuid();
        await using var s = Scope();
        var (svc, tc) = Ctx(s.ServiceProvider, Direction);
        var b = await svc.RegisterBackupAsync(new RegisterBackupRequest("full", DateTimeOffset.UtcNow.AddMinutes(-1), Checksum: Sha(), ObjectCount: 3));
        backupId = b.Value!.Id;

        var req = await svc.RequestProductionRestoreAsync(new RestoreRequest(backupId, "prod-blue"));
        req.IsSuccess.Should().BeTrue(req.Error?.Message);
        req.Value!.Status.Should().Be("pending_approval");

        // le demandeur ne peut pas approuver
        (await svc.ApproveProductionRestoreAsync(req.Value.Id)).Error!.Code.Should().Be("continuity.self_approval");

        // un tiers approuve
        tc.SetUser(Compta);
        var approved = await svc.ApproveProductionRestoreAsync(req.Value.Id);
        approved.IsSuccess.Should().BeTrue(approved.Error?.Message);
        approved.Value!.Status.Should().Be("passed");
        approved.Value.ApprovedBy.Should().Be(Compta);
    }

    // ---- §36 : l'état de reprise signale une violation de RPO si la dernière sauvegarde est ancienne ----
    [Fact]
    public async Task DR_status_reports_an_RPO_breach_when_the_last_backup_is_old()
    {
        // Entreprise dédiée (supermarché) — pas d'interférence avec les sauvegardes récentes de c1.
        var iso = Guid.Parse("dce00000-0000-0000-0000-0000000000c3");
        await Sql($"DELETE FROM nexora.backup WHERE company_id = '{iso}'");

        await using var s = Scope();
        var svc = s.ServiceProvider.GetRequiredService<IContinuityService>();
        var tc = (TestTenantContext)s.ServiceProvider.GetRequiredService<ITenantContext>();
        tc.SetUser(Direction);
        tc.SetScope([iso], [], iso);

        await svc.SetPolicyAsync((await svc.GetPolicyAsync()) with { RpoTargetMinutes = 60, Frequency = "hourly" });
        await svc.RegisterBackupAsync(new RegisterBackupRequest("full", DateTimeOffset.UtcNow.AddDays(-3), Checksum: Sha(), ObjectCount: 3));

        var dr = await svc.DisasterRecoveryStatusAsync();

        dr.RpoBreached.Should().BeTrue();
        dr.BackupOverdue.Should().BeTrue();
        dr.LastGoodBackupAgeMinutes.Should().BeGreaterThan(60);
        dr.Grade.Should().NotBe("A");
    }

    // ---- §43-45 : ouvrir → avancer dans le runbook → résoudre avec un post-mortem ----
    [Fact]
    public async Task Incident_runbook_open_advance_and_resolve_with_a_post_mortem()
    {
        await using var s = Scope();
        var (svc, _) = Ctx(s.ServiceProvider, Direction);

        var opened = await svc.OpenIncidentAsync(new OpenIncidentRequest("p2", "human_error", "Suppression accidentelle de produits", "user_report"));
        opened.IsSuccess.Should().BeTrue(opened.Error?.Message);
        opened.Value!.RunbookStep.Should().Be(1);

        (await svc.AdvanceIncidentAsync(opened.Value.Id, new AdvanceIncidentRequest(5, "restoring"))).Value!.RunbookStep.Should().Be(5);

        // post-mortem incomplet → refusé (§45)
        (await svc.ResolveIncidentAsync(opened.Value.Id, new ResolveIncidentRequest(30, 90, new Dictionary<string, string> { ["cause"] = "erreur humaine" })))
            .Error!.Code.Should().Be("continuity.post_mortem");

        var pm = new Dictionary<string, string>
        {
            ["cause"] = "suppression manuelle", ["impact"] = "12 produits", ["actions"] = "restauration ciblée",
            ["prevention"] = "confirmation double", ["owner"] = "direction",
        };
        var resolved = await svc.ResolveIncidentAsync(opened.Value.Id, new ResolveIncidentRequest(30, 90, pm));
        resolved.IsSuccess.Should().BeTrue(resolved.Error?.Message);
        resolved.Value!.Status.Should().Be("resolved");
        resolved.Value.PostMortem!["owner"].Should().Be("direction");
    }

    // ---- §36-37 : le contrôle de santé alerte les administrateurs quand aucune sauvegarde n'existe ----
    [Fact]
    public async Task The_health_check_alerts_admins_when_backups_are_missing()
    {
        var isolated = Guid.Parse("dce00000-0000-0000-0000-0000000000c3");   // supermarché — pas de sauvegarde
        await Sql($"DELETE FROM nexora.notification WHERE category = 'continuity.backup_overdue' AND company_id = '{isolated}'");

        await using var s = Scope();
        var tc = (TestTenantContext)s.ServiceProvider.GetRequiredService<ITenantContext>();
        tc.SetUser(Direction);
        tc.SetScope([isolated], [], isolated);
        var svc = s.ServiceProvider.GetRequiredService<IContinuityService>();

        var alerts = await svc.RunHealthChecksAsync();

        alerts.Should().BeGreaterThan(0);
        (await Scalar<long>($"SELECT COUNT(*) FROM nexora.notification WHERE category = 'continuity.backup_overdue' AND company_id = '{isolated}'"))
            .Should().BeGreaterThan(0);
    }

    // =========================================================================
    // Phase 48 — Résilience, Haute Disponibilité & Disaster Recovery. Le registre de continuité
    // (Phase 36) reste la source de vérité ; cette phase ajoute le confinement réel (§32, §55), les
    // exercices DR au-delà du simple test de restauration (§44-45) et la page de statut composée
    // des signaux Phase 41 déjà mesurés (§7, §39).
    // =========================================================================

    /// <summary>Entreprise isolée + rôle réel pour l'utilisateur donné — le verrouillage d'urgence
    /// et la page de statut sont des opérations agrégées à l'échelle de l'entreprise ; les tester
    /// sur l'entreprise partagée <see cref="Company"/> risquerait d'affecter les sessions/clés des
    /// autres suites de cette même collection.</summary>
    private async Task<Guid> MakeCompanyWithRole(Guid userId, string roleName = "owner_direction")
    {
        var companyId = Guid.NewGuid();
        var scopeId = Guid.NewGuid();
        await Sql($"""
            INSERT INTO nexora.company (id, group_id, name, legal_name, default_currency_id)
            VALUES ('{companyId}', NULL, 'Test Continuité {companyId:N}', 'Test Continuité {companyId:N} SARL',
                    (SELECT id FROM nexora.currency WHERE code = 'HTG'));
            INSERT INTO nexora.access_scope (id, scope_type, company_id) VALUES ('{scopeId}', 'COMPANY', '{companyId}');
            INSERT INTO nexora.user_role (user_id, role_id, scope_id)
            SELECT '{userId}', id, '{scopeId}' FROM nexora.role WHERE name = '{roleName}' AND system_role;
            """);
        return companyId;
    }

    // ---- §32, §55 : le verrouillage d'urgence révoque sessions et clés, jamais un accès résiduel ----
    [Fact]
    public async Task LockDown_revokes_all_sessions_and_api_keys_and_journals_the_containment_action()
    {
        var companyId = await MakeCompanyWithRole(Direction);
        var sessionId = Guid.NewGuid();
        await Sql($"""
            INSERT INTO nexora.user_session (id, user_id, active_company_id, device_label, expires_at)
            VALUES ('{sessionId}', '{Direction}', '{companyId}', 'Session test verrouillage', now() + interval '1 hour');
            """);

        await using var s = Scope();
        var tc = (TestTenantContext)s.ServiceProvider.GetRequiredService<ITenantContext>();
        tc.SetUser(Direction);
        tc.SetScope([companyId], [], companyId);
        var svc = s.ServiceProvider.GetRequiredService<IContinuityService>();
        var keys = s.ServiceProvider.GetRequiredService<IApiKeyService>();

        var created = await keys.CreateAsync(new CreateApiKeyRequest("Clé test verrouillage", ["catalog.view"]));
        created.IsSuccess.Should().BeTrue(created.Error?.Message);

        var opened = await svc.OpenIncidentAsync(new OpenIncidentRequest("p1", "unauthorized_access", "Test verrouillage", "user_report", "security"));

        var lockdown = await svc.LockDownAsync(opened.Value!.Id);
        lockdown.IsSuccess.Should().BeTrue(lockdown.Error?.Message);
        lockdown.Value!.SessionsRevoked.Should().BeGreaterThanOrEqualTo(1);
        lockdown.Value.ApiKeysRevoked.Should().BeGreaterThanOrEqualTo(1);
        lockdown.Value.UsersAffected.Should().BeGreaterThanOrEqualTo(1);

        (await Scalar<DateTime?>($"SELECT revoked_at FROM nexora.user_session WHERE id = '{sessionId}'")).Should().NotBeNull();
        (await Scalar<DateTime?>($"SELECT revoked_at FROM nexora.api_key WHERE id = '{created.Value!.Key.Id}'")).Should().NotBeNull();

        var incident = (await svc.ListIncidentsAsync(null)).Should().ContainSingle(i => i.Id == opened.Value.Id).Which;
        incident.ContainmentActions.Should().ContainSingle(a => a.Action == "lockdown");

        // un incident déjà résolu ne peut plus être verrouillé — le confinement précède la clôture
        var pm = new Dictionary<string, string>
        {
            ["cause"] = "test", ["impact"] = "aucun", ["actions"] = "aucune", ["prevention"] = "aucune", ["owner"] = "direction",
        };
        await svc.ResolveIncidentAsync(opened.Value.Id, new ResolveIncidentRequest(null, null, pm));
        (await svc.LockDownAsync(opened.Value.Id)).Error!.Code.Should().Be("continuity.incident_resolved");
    }

    // ---- §44-45 : un exercice DR se programme, se consigne, ne se rejoue pas deux fois ----
    [Fact]
    public async Task DrTest_lifecycle_schedules_records_a_result_and_rejects_an_invalid_type()
    {
        await using var s = Scope();
        var (svc, _) = Ctx(s.ServiceProvider, Direction);

        var badType = await svc.ScheduleDrTestAsync(new ScheduleDrTestRequest("invalid_type", "Exercice invalide"));
        badType.Error!.Code.Should().Be("continuity.dr_test_type");

        var scheduled = await svc.ScheduleDrTestAsync(new ScheduleDrTestRequest(
            "tabletop", "Exercice tabletop annuel", DateOnly.FromDateTime(DateTime.UtcNow.AddDays(7)),
            "Toute l'entreprise", ["Direction", "Comptable"]));
        scheduled.IsSuccess.Should().BeTrue(scheduled.Error?.Message);
        scheduled.Value!.Status.Should().Be("scheduled");
        scheduled.Value.Participants.Should().BeEquivalentTo(["Direction", "Comptable"]);

        var recorded = await svc.RecordDrTestResultAsync(scheduled.Value.Id, new RecordDrTestResultRequest(
            true, "Tout s'est bien passé", "Aucune", DateOnly.FromDateTime(DateTime.UtcNow.AddYears(1))));
        recorded.IsSuccess.Should().BeTrue(recorded.Error?.Message);
        recorded.Value!.Status.Should().Be("passed");
        recorded.Value.ExecutedAt.Should().NotBeNull();

        // ne peut pas être re-consigné une fois passé
        (await svc.RecordDrTestResultAsync(scheduled.Value.Id, new RecordDrTestResultRequest(true)))
            .Error!.Code.Should().Be("continuity.dr_test_not_found");

        (await svc.ListDrTestsAsync("passed")).Should().Contain(t => t.Id == scheduled.Value.Id);
        (await svc.ListDrTestsAsync("scheduled")).Should().NotContain(t => t.Id == scheduled.Value.Id);
    }

    // ---- §7, §39 : la page de statut compose les diagnostics Phase 41 + l'état de reprise, sans second calcul ----
    [Fact]
    public async Task StatusPage_is_operational_by_default_and_becomes_incident_after_a_p1()
    {
        var companyId = await MakeCompanyWithRole(Direction);
        await using var s = Scope();
        var tc = (TestTenantContext)s.ServiceProvider.GetRequiredService<ITenantContext>();
        tc.SetUser(Direction);
        tc.SetScope([companyId], [], companyId);
        var svc = s.ServiceProvider.GetRequiredService<IContinuityService>();

        var before = await svc.StatusPageAsync();
        before.OverallStatus.Should().Be("operational");
        before.Components.Should().Contain(c => c.Key == "database" && c.Criticality == "critical");

        await svc.OpenIncidentAsync(new OpenIncidentRequest("p1", "server_failure", "Panne critique test statut", "user_report"));

        var after = await svc.StatusPageAsync();
        after.OverallStatus.Should().Be("incident");
        after.RecentIncidents.Should().Contain(i => i.Title == "Panne critique test statut");
    }

    // ---- §11 : les nouveaux types de sauvegarde sont acceptés -----------------------------------
    [Fact]
    public async Task Register_backup_accepts_the_extended_kinds()
    {
        await using var s = Scope();
        var (svc, _) = Ctx(s.ServiceProvider, Direction);

        foreach (var kind in new[] { "differential", "metadata", "keys" })
        {
            var res = await svc.RegisterBackupAsync(new RegisterBackupRequest(kind, DateTimeOffset.UtcNow.AddMinutes(-1), Checksum: Sha(), ObjectCount: 1));
            res.IsSuccess.Should().BeTrue($"{kind} : {res.Error?.Message}");
            res.Value!.Kind.Should().Be(kind);
        }
    }
}
