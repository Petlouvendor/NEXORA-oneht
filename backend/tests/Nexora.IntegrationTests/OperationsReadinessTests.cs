using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Nexora.Application.Abstractions;
using Nexora.Application.Continuity;
using Nexora.Application.Operations;
using Nexora.Contracts.Continuity;
using Nexora.Contracts.Operations;
using Npgsql;

namespace Nexora.IntegrationTests;

/// <summary>
/// Qualité, exploitation &amp; préparation production (Phase 37 §40, §54, §56).
/// Checklist Go-Live dérivée en direct des signaux existants, validation Go-Live avec instantané
/// figé, journal des déploiements réversibles, tableau d'état système.
/// </summary>
[Collection("finance")]
public sealed class OperationsReadinessTests(FinanceEngineFixture fx)
{
    // Entreprise dédiée (supermarché) — pas d'interférence avec les sauvegardes/tests des autres classes.
    private static readonly Guid Company = Guid.Parse("dce00000-0000-0000-0000-0000000000c3");
    private static readonly Guid Direction = Guid.Parse("dce00000-0000-0000-0000-0000000000d1");

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

    private (IOperationsService Ops, IContinuityService Cont, TestTenantContext Tc) Ctx(IServiceProvider sp)
    {
        var tc = (TestTenantContext)sp.GetRequiredService<ITenantContext>();
        tc.SetUser(Direction);
        tc.SetScope([Company], [], Company);
        return (sp.GetRequiredService<IOperationsService>(), sp.GetRequiredService<IContinuityService>(), tc);
    }

    private static string Sha() => new('b', 64);

    // ---- §40, §54 : la checklist expose tous les points de contrôle attendus ----
    [Fact]
    public async Task Go_live_checklist_lists_every_expected_control()
    {
        await using var s = Scope();
        var (ops, _, _) = Ctx(s.ServiceProvider);

        var r = await ops.GoLiveReadinessAsync();

        r.Checks.Select(c => c.Key).Should().Contain(
            ["automated_tests", "backup_verified", "restore_tested", "dr_targets", "monitoring_active",
             "production_secrets", "migrations_applied", "data_quality", "rollback_plan",
             // Phase 49 §57 : critères RC ajoutés au même mécanisme, jamais un second registre
             "no_open_p0", "no_open_p1_waiver", "performance_targets_met", "multi_tenant_isolation", "uat_accepted"]);
        r.Checks.Should().OnlyContain(c => new[] { "pass", "warn", "fail", "manual" }.Contains(c.Status));
        (r.Passed + r.Warnings + r.Failed + r.Manual).Should().Be(r.Checks.Count);
        // Finalisation phase 18 : contrôle réel (écart livré / appliqué). « warn » signale une migration retouchée après
        // application sur une base de poste — jamais une migration en attente (« fail »), cf. ProductionReadinessTests.
        r.Checks.Single(c => c.Key == "migrations_applied").Status.Should().BeOneOf("pass", "warn");
    }

    // ---- §32, §57 (Phase 49) : le signal d'isolation multi-tenant reflète la réalité du rôle connecté,
    // jamais une simple attestation — ici la fixture se connecte en superutilisateur (BYPASSRLS vrai
    // par nature), donc le point doit honnêtement remonter « fail » ; la preuve que le rôle applicatif
    // réel `nexora_app` n'a PAS ce contournement est vérifiée séparément côté API (ResilienceEndpointsTests
    // / SmokeTests, connexion `nexora_app`) ----
    [Fact]
    public async Task Multi_tenant_isolation_check_matches_the_real_bypassrls_flag_of_the_connected_role()
    {
        await using var s = Scope();
        var (ops, _, _) = Ctx(s.ServiceProvider);

        var bypassRls = await Scalar<bool>("SELECT COALESCE((SELECT rolbypassrls FROM pg_roles WHERE rolname = current_user), false)");
        var r = await ops.GoLiveReadinessAsync();

        var check = r.Checks.Single(c => c.Key == "multi_tenant_isolation");
        check.Source.Should().Be("auto");
        check.Status.Should().Be(bypassRls ? "fail" : "pass");
    }

    // ---- §40 : « backup vérifié » et « restauration testée » passent une fois les preuves en place ----
    [Fact]
    public async Task Backup_and_restore_controls_turn_green_once_the_evidence_exists()
    {
        await Sql($"DELETE FROM nexora.restore_run WHERE company_id = '{Company}'");
        await Sql($"DELETE FROM nexora.backup WHERE company_id = '{Company}'");

        await using var s = Scope();
        var (ops, cont, _) = Ctx(s.ServiceProvider);

        (await ops.GoLiveReadinessAsync()).Checks.Single(c => c.Key == "backup_verified").Status.Should().Be("fail");

        var b = await cont.RegisterBackupAsync(new RegisterBackupRequest("full", DateTimeOffset.UtcNow.AddMinutes(-1), Checksum: Sha(), ObjectCount: 3));
        await cont.VerifyBackupAsync(b.Value!.Id);
        await cont.RunRestoreTestAsync(new RestoreTestRequest(b.Value.Id, "sandbox"));

        var r = await ops.GoLiveReadinessAsync();
        r.Checks.Single(c => c.Key == "backup_verified").Status.Should().Be("pass");
        r.Checks.Single(c => c.Key == "restore_tested").Status.Should().Be("pass");
    }

    // ---- §40 : un « go » franc est refusé tant que des contrôles sont en échec ----
    [Fact]
    public async Task A_plain_go_is_refused_while_controls_are_failing()
    {
        await Sql($"DELETE FROM nexora.backup WHERE company_id = '{Company}'");
        await Sql($"DELETE FROM nexora.restore_run WHERE company_id = '{Company}'");

        await using var s = Scope();
        var (ops, _, _) = Ctx(s.ServiceProvider);

        var refused = await ops.SignOffAsync(new SignOffRequest("production", "go"));
        refused.IsSuccess.Should().BeFalse();
        refused.Error!.Code.Should().Be("operations.not_ready");

        // « go_with_reservations » est accepté et journalisé
        var reserved = await ops.SignOffAsync(new SignOffRequest("production", "go_with_reservations", Notes: "pilote interne"));
        reserved.IsSuccess.Should().BeTrue(reserved.Error?.Message);
        reserved.Value!.Verdict.Should().Be("go_with_reservations");
        reserved.Value.ChecklistSnapshot.Checks.Should().NotBeEmpty();
    }

    // ---- §40, §54 : forcer le Go-Live exige un motif et fige l'instantané de la checklist ----
    [Fact]
    public async Task Forcing_go_live_requires_a_reason_and_freezes_the_checklist_snapshot()
    {
        await Sql($"DELETE FROM nexora.backup WHERE company_id = '{Company}'");

        await using var s = Scope();
        var (ops, _, _) = Ctx(s.ServiceProvider);

        (await ops.SignOffAsync(new SignOffRequest("production", "go", Force: true)))
            .Error!.Code.Should().Be("operations.force_reason");

        var forced = await ops.SignOffAsync(new SignOffRequest("production", "go", Force: true, ForceReason: "risque accepté par la direction"));
        forced.IsSuccess.Should().BeTrue(forced.Error?.Message);
        forced.Value!.Forced.Should().BeTrue();
        forced.Value.ForceReason.Should().Be("risque accepté par la direction");
        forced.Value.ChecklistSnapshot.Failed.Should().BeGreaterThan(0);

        (await ops.ListSignOffsAsync()).Should().Contain(x => x.Id == forced.Value.Id);
    }

    // ---- §34, §38-39 : un déploiement est journalisé puis réversible ----
    [Fact]
    public async Task A_deployment_is_logged_then_reversible()
    {
        await using var s = Scope();
        var (ops, _, _) = Ctx(s.ServiceProvider);

        var dep = await ops.RecordDeploymentAsync(new RecordDeploymentRequest($"v37.{Guid.NewGuid():N}", "staging", "release phase 37"));
        dep.IsSuccess.Should().BeTrue(dep.Error?.Message);
        dep.Value!.Status.Should().Be("succeeded");
        dep.Value.SchemaVersion.Should().NotBeNullOrEmpty();

        var back = await ops.RollBackAsync(dep.Value.Id, new RollbackRequest("régression au checkout"));
        back.IsSuccess.Should().BeTrue(back.Error?.Message);
        back.Value!.RolledBackFrom.Should().Be(dep.Value.Id);
        back.Value.Environment.Should().Be("staging");

        var list = await ops.ListDeploymentsAsync();
        list.Single(d => d.Id == dep.Value.Id).Status.Should().Be("rolled_back");

        // on ne rejoue pas un rollback déjà appliqué
        (await ops.RollBackAsync(dep.Value.Id, new RollbackRequest("x"))).Error!.Code.Should().Be("operations.already_rolled_back");
    }

    // ---- §29, §56 : le tableau d'état système consolide les sous-systèmes ----
    [Fact]
    public async Task System_status_consolidates_the_subsystems()
    {
        await using var s = Scope();
        var (ops, _, _) = Ctx(s.ServiceProvider);

        var st = await ops.SystemStatusAsync();

        st.Components.Select(c => c.Component).Should().Contain(["database", "scheduler", "notifications", "continuity", "data_quality", "incidents"]);
        st.Components.Single(c => c.Component == "database").Status.Should().Be("ok");
        new[] { "ok", "degraded", "down" }.Should().Contain(st.Overall);
    }

    // ---- §27 : les métriques plateforme sont exposées sans donnée d'entreprise ----
    [Fact]
    public async Task Platform_metrics_are_exposed()
    {
        await using var s = Scope();
        var metrics = s.ServiceProvider.GetRequiredService<IPlatformMetrics>();

        var gauges = await metrics.GaugesAsync();

        gauges.Should().Contain(g => g.Name == "nexora_up" && g.Value == 1);
        gauges.Should().Contain(g => g.Name == "nexora_active_companies");
        gauges.Should().OnlyContain(g => g.Name.StartsWith("nexora_"));
    }
}
