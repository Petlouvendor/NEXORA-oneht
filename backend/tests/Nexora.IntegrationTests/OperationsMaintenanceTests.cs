using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Nexora.Application.Abstractions;
using Nexora.Application.Operations;
using Nexora.Contracts.Operations;
using Nexora.Infrastructure.Scheduling;
using Npgsql;

namespace Nexora.IntegrationTests;

/// <summary>
/// Administration opérationnelle & maintenance (Phase 41 §7-9, §10-12, §16-19, §23-24, §27, §44,
/// §51, §58) : registre des jobs planifiés (activer/désactiver/exécuter maintenant), files
/// d'attente & dead-letter, diagnostics, bascules de fonctionnalité, fenêtres de maintenance,
/// rétention & purge technique, correction de donnée contrôlée.
/// </summary>
[Collection("finance")]
public sealed class OperationsMaintenanceTests(FinanceEngineFixture fx)
{
    private static readonly Guid Company = Guid.Parse("dce00000-0000-0000-0000-0000000000c3");
    private static readonly Guid Direction = Guid.Parse("dce00000-0000-0000-0000-0000000000d1");
    private static readonly Guid Compta = Guid.Parse("dce00000-0000-0000-0000-0000000000d2");

    private AsyncServiceScope Scope() => fx.Provider.CreateAsyncScope();

    private static T For<T>(IServiceProvider sp, Guid user) where T : notnull
    {
        var tc = (TestTenantContext)sp.GetRequiredService<ITenantContext>();
        tc.SetUser(user);
        tc.SetScope([Company], [], Company);
        return sp.GetRequiredService<T>();
    }

    private async Task Sql(string sql)
    {
        await using var c = new NpgsqlConnection(fx.TestConnectionString);
        await c.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, c);
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task<T> Scalar<T>(string sql)
    {
        await using var c = new NpgsqlConnection(fx.TestConnectionString);
        await c.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, c);
        return (T)(await cmd.ExecuteScalarAsync())!;
    }

    // ---- §7-9 : le registre liste les 9 jobs seedés avec leur catégorie ----
    [Fact]
    public async Task Job_registry_lists_seeded_jobs_with_categories()
    {
        await using var s = Scope();
        var jobs = For<IJobRegistry>(s.ServiceProvider, Direction);

        var list = await jobs.ListAsync();

        list.Select(j => j.Key).Should().Contain([
            "automations.due", "webhooks.deliver", "payments.expire_stale", "lots.sweep_expired",
            "continuity.health_check", "notifications.dispatch", "subscriptions.lifecycle",
            "imports.resume_stalled", "maintenance.purge"]);
        list.Single(j => j.Key == "automations.due").Category.Should().Be("business");
        list.Single(j => j.Key == "webhooks.deliver").Category.Should().Be("technical");
        list.Should().OnlyContain(j => j.Enabled);   // rien n'est en pause au départ
    }

    // ---- §7 : un job en pause n'est pas exécuté par le planificateur ; repris à la reprise ----
    [Fact]
    public async Task Pausing_a_job_skips_it_on_the_next_scheduler_tick_then_resuming_runs_it_again()
    {
        const string key = "imports.resume_stalled";
        await using var s = Scope();
        var jobs = For<IJobRegistry>(s.ServiceProvider, Direction);
        var runner = s.ServiceProvider.GetRequiredService<SchedulerRunner>();

        async Task<int> RunCount() => (int)await Scalar<long>($"SELECT COUNT(*) FROM nexora.job_run WHERE job_key = '{key}'");

        try
        {
            (await jobs.PauseAsync(key)).IsSuccess.Should().BeTrue();
            (await jobs.ListAsync()).Single(j => j.Key == key).Enabled.Should().BeFalse();

            var before = await RunCount();
            await runner.RunOnceAsync();
            (await RunCount()).Should().Be(before, "un job en pause ne doit générer aucun nouveau passage planifié");

            (await jobs.ResumeAsync(key)).IsSuccess.Should().BeTrue();
            await runner.RunOnceAsync();
            (await RunCount()).Should().BeGreaterThan(before, "une fois repris, le job tourne de nouveau au passage suivant");
        }
        finally
        {
            await jobs.ResumeAsync(key);   // jamais laisser un job global en pause pour les autres tests
        }
    }

    // ---- §7, §51 : « exécuter maintenant » outrepasse la pause et est journalisé « manual » ----
    [Fact]
    public async Task Manual_run_bypasses_pause_and_is_recorded_as_manual()
    {
        const string key = "imports.resume_stalled";
        await using var s = Scope();
        var jobs = For<IJobRegistry>(s.ServiceProvider, Direction);

        try
        {
            await jobs.PauseAsync(key);
            var run = await jobs.RunNowAsync(key);
            run.IsSuccess.Should().BeTrue(run.Error?.Message);
            run.Value!.TriggeredBy.Should().Be("manual");
            run.Value.Status.Should().Be("succeeded");

            (await jobs.HistoryAsync(key)).Should().Contain(r => r.Id == run.Value.Id && r.TriggeredBy == "manual");
        }
        finally
        {
            await jobs.ResumeAsync(key);
        }

        (await jobs.RunNowAsync($"unknown.job.{Guid.NewGuid():N}")).Error!.Code.Should().Be("job.not_found");
    }

    // ---- §10-12 : statistiques de file, rejeu et suppression contrôlée d'un message mort ----
    [Fact]
    public async Task Queue_stats_and_dead_letter_replay_and_delete()
    {
        await using var s = Scope();
        var queues = For<IQueueAdminService>(s.ServiceProvider, Direction);

        var dead = Guid.NewGuid();
        var toDelete = Guid.NewGuid();
        var pending = Guid.NewGuid();
        var tag = Guid.NewGuid().ToString("N");
        await Sql($"""
            INSERT INTO nexora.notification_outbox (id, company_id, channel, subject, body, status, attempts, dedup_key)
            VALUES
              ('{dead}', '{Company}', 'email', 'dead {tag}', 'x', 'dead', 5, 'ops-test-dead-{tag}'),
              ('{toDelete}', '{Company}', 'email', 'dead2 {tag}', 'x', 'dead', 5, 'ops-test-dead2-{tag}'),
              ('{pending}', '{Company}', 'email', 'pending {tag}', 'x', 'pending', 0, 'ops-test-pending-{tag}')
            """);

        var stats = await queues.StatsAsync();
        var notif = stats.Single(q => q.Queue == "notifications");
        notif.Dead.Should().BeGreaterThanOrEqualTo(2);
        notif.Pending.Should().BeGreaterThanOrEqualTo(1);

        var deadLetters = await queues.DeadLettersAsync();
        deadLetters.Should().Contain(d => d.Id == dead && d.Queue == "notifications");

        (await queues.ReplayDeadLetterAsync(dead)).IsSuccess.Should().BeTrue();
        (await Scalar<string>($"SELECT status FROM nexora.notification_outbox WHERE id = '{dead}'")).Should().Be("pending");
        (await Scalar<int>($"SELECT attempts::int FROM nexora.notification_outbox WHERE id = '{dead}'")).Should().Be(0);

        (await queues.DeleteDeadLetterAsync(toDelete)).IsSuccess.Should().BeTrue();
        (await Scalar<long>($"SELECT COUNT(*) FROM nexora.notification_outbox WHERE id = '{toDelete}'")).Should().Be(0);

        (await queues.RetryAsync(Guid.NewGuid())).Error!.Code.Should().Be("queue.message_not_found");
        (await queues.ReplayDeadLetterAsync(Guid.NewGuid())).Error!.Code.Should().Be("queue.dead_letter_not_found");

        // Le rejeu remet « dead » en « pending » : sans nettoyage, la ligne resterait éligible au
        // vrai dispatcher (Phase 34) dans les tests suivants, qui partagent la même base clonée.
        await Sql($"DELETE FROM nexora.notification_outbox WHERE id IN ('{dead}', '{pending}')");
    }

    // ---- §5-6, §13-15 : la batterie de diagnostics couvre tous les contrôles documentés ----
    [Fact]
    public async Task Diagnostics_run_all_covers_the_documented_checks()
    {
        await using var s = Scope();
        var diag = For<IOpsDiagnosticsService>(s.ServiceProvider, Direction);

        var checks = await diag.RunAllAsync();

        checks.Select(c => c.Key).Should().Contain([
            "database", "storage", "queue", "email", "sms", "connector", "printer", "sync", "backup", "license", "clock"]);
        checks.Should().OnlyContain(c => new[] { "ok", "warn", "fail", "skipped" }.Contains(c.Status));
        checks.Single(c => c.Key == "database").Status.Should().Be("ok");

        var storage = await diag.StorageAsync();
        storage.DatabaseSizeBytes.Should().BeGreaterThan(0);

        var database = await diag.DatabaseAsync();
        database.ActiveConnections.Should().BeGreaterThan(0);
        database.TopTables.Should().NotBeEmpty();
    }

    // ---- §27 : résolution par spécificité + kill switch global ----
    [Fact]
    public async Task Feature_flag_resolution_respects_specificity_and_kill_switch()
    {
        await using var s = Scope();
        var flags = For<IFeatureFlagService>(s.ServiceProvider, Direction);
        var key = $"ops.test.{Guid.NewGuid():N}"[..40];
        var user = Compta;   // utilisateur réel du seed (contrainte FK feature_flag.user_id)

        (await flags.SaveAsync(new SaveFeatureFlagRequest(null, key, "global", null, null, true))).IsSuccess.Should().BeTrue();
        (await flags.IsEnabledAsync(key, Company, null)).Should().BeTrue("aucun réglage plus spécifique : le global s'applique");

        (await flags.SaveAsync(new SaveFeatureFlagRequest(null, key, "company", Company, null, false))).IsSuccess.Should().BeTrue();
        (await flags.IsEnabledAsync(key, Company, null)).Should().BeFalse("l'entreprise a désactivé la bascule");

        (await flags.SaveAsync(new SaveFeatureFlagRequest(null, key, "user", null, user, true))).IsSuccess.Should().BeTrue();
        (await flags.IsEnabledAsync(key, Company, user)).Should().BeTrue("l'utilisateur est plus spécifique que l'entreprise");

        // Kill switch : le global désactivé l'emporte toujours, même sur un réglage utilisateur activé.
        (await flags.SaveAsync(new SaveFeatureFlagRequest(null, key, "global", null, null, false))).IsSuccess.Should().BeTrue();
        (await flags.IsEnabledAsync(key, Company, user)).Should().BeFalse();

        var all = (await flags.ListAsync()).Where(f => f.Key == key).ToList();
        all.Should().HaveCount(3);
        (await flags.DeleteAsync(all[0].Id)).IsSuccess.Should().BeTrue();
    }

    // ---- §23-24 : la fenêtre de maintenance suit l'état machine et refuse les sauts ----
    [Fact]
    public async Task Maintenance_window_follows_the_allowed_state_machine()
    {
        await using var s = Scope();
        var maintenance = For<IMaintenanceService>(s.ServiceProvider, Direction);

        var created = await maintenance.ScheduleAsync(new ScheduleMaintenanceRequest(
            DateTimeOffset.UtcNow.AddDays(1), 60, "mise à niveau PostgreSQL", ["api", "database"], "DSI", "restaurer la sauvegarde pré-maintenance"));
        created.IsSuccess.Should().BeTrue(created.Error?.Message);
        var id = created.Value!.Id;
        created.Value.Status.Should().Be("planned");

        (await maintenance.TransitionAsync(id, new TransitionMaintenanceRequest("closed"))).Error!.Code
            .Should().Be("maintenance.invalid_transition");

        (await maintenance.TransitionAsync(id, new TransitionMaintenanceRequest("notified"))).Value!.Status.Should().Be("notified");
        (await maintenance.TransitionAsync(id, new TransitionMaintenanceRequest("backed_up"))).Value!.Status.Should().Be("backed_up");

        (await maintenance.IsActiveAsync()).Should().BeFalse();
        var inProgress = await maintenance.TransitionAsync(id, new TransitionMaintenanceRequest("in_progress"));
        inProgress.Value!.Status.Should().Be("in_progress");
        inProgress.Value.StartedAt.Should().NotBeNull();
        (await maintenance.IsActiveAsync()).Should().BeTrue();

        (await maintenance.TransitionAsync(id, new TransitionMaintenanceRequest("verifying"))).Value!.Status.Should().Be("verifying");
        var closed = await maintenance.TransitionAsync(id, new TransitionMaintenanceRequest("closed"));
        closed.Value!.Status.Should().Be("closed");
        closed.Value.EndedAt.Should().NotBeNull();
        (await maintenance.IsActiveAsync()).Should().BeFalse();
    }

    // ---- §16-19 : la purge protège les données non éligibles et ne touche que le technique ----
    [Fact]
    public async Task Purge_removes_only_the_eligible_technical_rows_and_protects_the_rest()
    {
        await using var s = Scope();
        var retention = For<IRetentionPurgeService>(s.ServiceProvider, Direction);
        var user = Guid.NewGuid();
        await Sql($"""
            INSERT INTO nexora.app_user (id, email, password_hash, full_name)
            VALUES ('{user}', 'ops-purge-{Guid.NewGuid():N}@nexora.test', 'x', 'Purge Test')
            """);

        var expiredSession = Guid.NewGuid();
        var activeSession = Guid.NewGuid();
        await Sql($"""
            INSERT INTO nexora.user_session (id, user_id, expires_at) VALUES ('{expiredSession}', '{user}', now() - interval '10 days');
            INSERT INTO nexora.user_session (id, user_id, expires_at) VALUES ('{activeSession}', '{user}', now() + interval '10 days');
            """);

        var preview = (await retention.PreviewAsync(new PurgePreviewRequest("expired_sessions", null, DateTimeOffset.UtcNow))).Value!;
        preview.DryRun.Should().BeTrue();
        preview.ObjectCount.Should().BeGreaterThanOrEqualTo(1);
        (await Scalar<long>($"SELECT COUNT(*) FROM nexora.user_session WHERE id = '{expiredSession}'")).Should().Be(1, "l'aperçu n'écrit rien");

        var executed = await retention.ExecuteAsync(new PurgePreviewRequest("expired_sessions", null, DateTimeOffset.UtcNow));
        executed.IsSuccess.Should().BeTrue();
        executed.Value!.Status.Should().Be("executed");
        (await Scalar<long>($"SELECT COUNT(*) FROM nexora.user_session WHERE id = '{expiredSession}'")).Should().Be(0);
        (await Scalar<long>($"SELECT COUNT(*) FROM nexora.user_session WHERE id = '{activeSession}'")).Should().Be(1, "une session active n'est jamais purgée");

        // job_run : seul un passage « succeeded » ancien est purgeable, un « failed » ancien reste (postmortem).
        // Clé de job dédiée au test — ne jamais réutiliser une clé réelle du planificateur ici : la
        // purge globale (job "maintenance.purge") tourne aussi dans d'autres tests et pourrait sinon
        // supprimer des lignes historiques d'un job partagé entre tests.
        var testJobKey = $"test.purge.{Guid.NewGuid():N}";
        await Sql($"""
            INSERT INTO nexora.job_definition (key, name, category) VALUES ('{testJobKey}', 'Job de test', 'technical');
            """);
        var okRun = Guid.NewGuid();
        var failedRun = Guid.NewGuid();
        await Sql($"""
            INSERT INTO nexora.job_run (id, job_key, started_at, finished_at, status)
            VALUES ('{okRun}', '{testJobKey}', '2020-01-01', '2020-01-01', 'succeeded');
            INSERT INTO nexora.job_run (id, job_key, started_at, finished_at, status)
            VALUES ('{failedRun}', '{testJobKey}', '2020-01-01', '2020-01-01', 'failed');
            """);
        var cutoff = new DateTimeOffset(2021, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var jobsExec = await retention.ExecuteAsync(new PurgePreviewRequest("completed_job_runs", null, cutoff));
        jobsExec.IsSuccess.Should().BeTrue();
        jobsExec.Value!.ProtectedExcludedCount.Should().BeGreaterThanOrEqualTo(1);
        (await Scalar<long>($"SELECT COUNT(*) FROM nexora.job_run WHERE id = '{okRun}'")).Should().Be(0);
        (await Scalar<long>($"SELECT COUNT(*) FROM nexora.job_run WHERE id = '{failedRun}'")).Should().Be(1, "un échec passé reste disponible pour post-mortem");

        var history = await retention.HistoryAsync();
        history.Should().Contain(h => h.DataType == "expired_sessions" && h.Status == "executed");

        // Politique de rétention : défauts système présents, surchargeable par entreprise.
        var policies = await retention.ListPoliciesAsync();
        policies.Should().Contain(p => p.DataType == "resolved_notifications" && p.CompanyId == null);
        var overridden = await retention.SavePolicyAsync(new SaveRetentionPolicyRequest("resolved_notifications", 10, "delete", CompanyId: Company));
        overridden.IsSuccess.Should().BeTrue();
        overridden.Value!.DurationDays.Should().Be(10);

        (await retention.RunDueAsync()).Should().BeGreaterThanOrEqualTo(0);
    }

    // ---- §44 : demandeur ≠ approbateur, avant/après conservés, champ hors liste refusé ----
    [Fact]
    public async Task Data_correction_requires_a_different_approver_and_records_before_after()
    {
        await using var s = Scope();
        var customerId = Guid.NewGuid();
        await Sql($"""
            INSERT INTO nexora.customer (id, company_id, customer_number, name, phone, default_currency_id)
            VALUES ('{customerId}', '{Company}', 'OPS-{Guid.NewGuid():N}'::text, 'Client test correction', '+509-0000-0000',
                    (SELECT id FROM nexora.currency WHERE code = 'HTG'))
            """);

        // `For<T>` re-stampe l'utilisateur ambiant sur le contexte partagé de la portée DI : les deux
        // acteurs (demandeur / approbateur) ne peuvent pas être mis en cache dans des variables — on
        // rappelle `For<T>` juste avant chaque action pour fixer l'identité de CETTE action précise.
        var req = await For<IDataCorrectionService>(s.ServiceProvider, Direction)
            .RequestAsync(new RequestDataCorrectionRequest("customer", customerId, "phone", "+509-1111-1111", "numéro erroné signalé par le client"));
        req.IsSuccess.Should().BeTrue(req.Error?.Message);
        req.Value!.OldValue.Should().Be("+509-0000-0000");
        req.Value.Status.Should().Be("requested");

        (await For<IDataCorrectionService>(s.ServiceProvider, Direction).DecideAsync(req.Value.Id, new DecideDataCorrectionRequest(true)))
            .Error!.Code.Should().Be("correction.four_eyes", "le demandeur ne peut pas approuver sa propre demande");

        var applied = await For<IDataCorrectionService>(s.ServiceProvider, Compta)
            .DecideAsync(req.Value.Id, new DecideDataCorrectionRequest(true, "vérifié auprès du client"));
        applied.IsSuccess.Should().BeTrue(applied.Error?.Message);
        applied.Value!.Status.Should().Be("applied");
        (await Scalar<string>($"SELECT phone FROM nexora.customer WHERE id = '{customerId}'")).Should().Be("+509-1111-1111");

        // Rejet : la valeur n'est jamais appliquée.
        var req2 = await For<IDataCorrectionService>(s.ServiceProvider, Direction)
            .RequestAsync(new RequestDataCorrectionRequest("customer", customerId, "phone", "+509-2222-2222", "second essai"));
        req2.IsSuccess.Should().BeTrue(req2.Error?.Message);
        var decided2 = await For<IDataCorrectionService>(s.ServiceProvider, Compta)
            .DecideAsync(req2.Value!.Id, new DecideDataCorrectionRequest(false, "motif insuffisant"));
        decided2.IsSuccess.Should().BeTrue(decided2.Error?.Message);
        decided2.Value!.Status.Should().Be("rejected");
        (await Scalar<string>($"SELECT phone FROM nexora.customer WHERE id = '{customerId}'")).Should().Be("+509-1111-1111");

        var requesterAgain = For<IDataCorrectionService>(s.ServiceProvider, Direction);
        (await requesterAgain.RequestAsync(new RequestDataCorrectionRequest("customer", customerId, "credit_limit", "999999", "motif")))
            .Error!.Code.Should().Be("correction.field");
        (await requesterAgain.RequestAsync(new RequestDataCorrectionRequest("journal_entry", Guid.NewGuid(), "amount", "1", "motif")))
            .Error!.Code.Should().Be("correction.table");

        (await requesterAgain.ListAsync("applied")).Should().Contain(c => c.Id == req.Value.Id);
    }
}
