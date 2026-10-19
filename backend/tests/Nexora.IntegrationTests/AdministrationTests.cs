using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Nexora.Application.Abstractions;
using Nexora.Application.Administration;
using Nexora.Application.Catalog;
using Nexora.Application.Security;
using Nexora.Contracts.Administration;
using Nexora.Infrastructure.Persistence;

namespace Nexora.IntegrationTests;

/// <summary>Scénarios de validation Administration, Paramétrage & Automatisation (Phase 14 §47).</summary>
[Collection("finance")]
public sealed class AdministrationTests(FinanceEngineFixture fx)
{
    private static readonly Guid Company = Guid.Parse("dce00000-0000-0000-0000-0000000000c1");
    private static readonly Guid Direction = Guid.Parse("dce00000-0000-0000-0000-0000000000d1");
    private static readonly Guid ChiefAccountant = Guid.Parse("dce00000-0000-0000-0000-0000000000d2");
    private static readonly Guid Establishment = Guid.Parse("dce00000-0000-0000-0000-0000000000f1");
    private static readonly Guid BoxUnit = Guid.Parse("dce00000-0000-0000-0000-00000000a011");

    private AsyncServiceScope Scope() => fx.Provider.CreateAsyncScope();
    private static JsonElement J(string s) => JsonDocument.Parse(s).RootElement;

    private async Task Exec(string sql)
    {
        await using var c = new Npgsql.NpgsqlConnection(fx.TestConnectionString);
        await c.OpenAsync();
        await using var cmd = new Npgsql.NpgsqlCommand(sql, c);
        await cmd.ExecuteNonQueryAsync();
    }

    // ---- §3, §47 : la valeur effective suit la hiérarchie de périmètre ----
    [Fact]
    public async Task Setting_resolution_follows_scope_hierarchy()
    {
        await using var s = Scope();
        var svc = s.ServiceProvider.GetRequiredService<ISettingsService>();

        // La démo pose pos.max_discount_percent = 10 au niveau entreprise.
        (await svc.ResolveAsync("pos.max_discount_percent")).Value.GetDecimal().Should().Be(10);
        (await svc.ResolveAsync("pos.max_discount_percent")).Source.Should().Be("company");

        // Un paramètre non surchargé retombe sur le défaut de sa définition.
        var rounding = await svc.ResolveAsync("finance.rounding_mode");
        rounding.Source.Should().Be("definition");
        rounding.Value.GetString().Should().Be("half_up");

        // Surcharge à l'établissement → le périmètre le plus fin gagne.
        var set = await svc.SetAsync(new SetSettingRequest("pos.max_discount_percent", "establishment", Establishment, J("5"), null));
        set.IsSuccess.Should().BeTrue(set.Error?.Message);

        (await svc.ResolveAsync("pos.max_discount_percent", Establishment)).Value.GetDecimal().Should().Be(5);
        (await svc.ResolveAsync("pos.max_discount_percent", Establishment)).Source.Should().Be("establishment");
        (await svc.ResolveAsync("pos.max_discount_percent")).Value.GetDecimal().Should().Be(10);   // sans établissement : entreprise
    }

    // ---- §2, §38, §45 : paramètre sensible → motif requis + audit + avertissement ----
    [Fact]
    public async Task Sensitive_setting_requires_a_reason_and_is_audited()
    {
        await using var s = Scope();
        var svc = s.ServiceProvider.GetRequiredService<ISettingsService>();
        var db = s.ServiceProvider.GetRequiredService<NexoraDbContext>();

        (await svc.SetAsync(new SetSettingRequest("stock.valuation_method", "company", null, J("\"fifo\""), null)))
            .IsSuccess.Should().BeFalse();

        var ok = await svc.SetAsync(new SetSettingRequest("stock.valuation_method", "company", null, J("\"fifo\""), "Alignement groupe"));
        ok.IsSuccess.Should().BeTrue(ok.Error?.Message);
        ok.Value!.Warnings.Should().Contain(w => w.Contains("transactions futures"));

        var audited = await db.Database.SqlQuery<int>($"""
            SELECT COUNT(*)::int AS "Value" FROM nexora.audit_log
            WHERE company_id = {Company} AND entity_type = 'setting'
            """).FirstAsync();
        audited.Should().BeGreaterThan(0);

        // Valeur hors référentiel enum → refus.
        (await svc.SetAsync(new SetSettingRequest("stock.valuation_method", "company", null, J("\"lifo\""), "x")))
            .IsSuccess.Should().BeFalse();
    }

    // ---- §3 : un paramètre ne se pose pas plus fin que son périmètre maximal ----
    [Fact]
    public async Task Setting_cannot_be_set_finer_than_its_max_scope()
    {
        await using var s = Scope();
        var svc = s.ServiceProvider.GetRequiredService<ISettingsService>();
        var r = await svc.SetAsync(new SetSettingRequest("finance.rounding_mode", "establishment", Establishment, J("\"down\""), null));
        r.IsSuccess.Should().BeFalse();
        r.Error!.Code.Should().Be("settings.scope_too_fine");
    }

    // ---- §10-11 : rôle personnalisé d'entreprise + prévisualisation des accès ----
    [Fact]
    public async Task Custom_role_is_created_and_previews_its_permissions()
    {
        await using var s = Scope();
        var roles = s.ServiceProvider.GetRequiredService<ICustomRoleService>();

        var id = (await roles.CreateAsync(new CreateRoleRequest(
            $"Comptable Officine {Guid.NewGuid():N}"[..24], "Limité à la Pharmacie", null,
            ["accounting.view", "reporting.view"]))).Value;

        var preview = await roles.PreviewAsync(id);
        preview.IsSuccess.Should().BeTrue(preview.Error?.Message);
        preview.Value!.Permissions.Should().Contain(["accounting.view", "reporting.view"]);
        preview.Value.AssignmentCount.Should().Be(0);
        preview.Value.SystemRole.Should().BeFalse();

        (await roles.CreateAsync(new CreateRoleRequest("x", null, null, [])))
            .IsSuccess.Should().BeFalse();   // nom trop court
        (await roles.SetPermissionsAsync(id, new SetRolePermissionsRequest(["not.a.permission"])))
            .IsSuccess.Should().BeFalse();
    }

    // ---- §45 : garde-fou — désactiver un rôle assigné exige une confirmation ----
    [Fact]
    public async Task Assigned_role_cannot_be_disabled_without_force()
    {
        await using var s = Scope();
        var roles = s.ServiceProvider.GetRequiredService<ICustomRoleService>();
        var id = (await roles.CreateAsync(new CreateRoleRequest(
            $"Rôle test {Guid.NewGuid():N}"[..18], null, null, ["sales.view"]))).Value;

        var scopeId = Guid.NewGuid();
        await Exec($"""
            INSERT INTO nexora.access_scope (id, scope_type, company_id) VALUES ('{scopeId}', 'COMPANY', '{Company}');
            INSERT INTO nexora.user_role (user_id, role_id, scope_id) VALUES ('{ChiefAccountant}', '{id}', '{scopeId}');
            """);

        (await roles.DisableAsync(id, force: false)).IsSuccess.Should().BeFalse();
        (await roles.DisableAsync(id, force: true)).IsSuccess.Should().BeTrue();
    }

    // ---- §14-16, §47 : workflow de remise — testable avant activation, puis crée l'approbation ----
    [Fact]
    public async Task Discount_workflow_is_tested_then_activated_then_creates_an_approval()
    {
        await using var s = Scope();
        var wf = s.ServiceProvider.GetRequiredService<IWorkflowService>();
        var tc = (TestTenantContext)s.ServiceProvider.GetRequiredService<ITenantContext>();

        var code = $"discount-{Guid.NewGuid():N}"[..16];
        var def = new WorkflowDefinitionDto(
            "all",
            [new WorkflowConditionDto("amount", "gt", J("3000"))],
            new WorkflowApprovalDto(1, "manager"),
            [], []);   // finalisation phase 11 : actions / notifications non exécutées → refusées
        var id = (await wf.UpsertAsync(new UpsertWorkflowRequest(code, "Approbation remise", "discount_applied", def))).Value;

        (await wf.ActivateAsync(id)).IsSuccess.Should().BeFalse();   // pas encore testé (§16)

        var high = await wf.TestAsync(id, new WorkflowTestRequest(J("""{"amount":5000}""")));
        high.Value!.Matched.Should().BeTrue();
        high.Value.Conditions.Should().ContainSingle().Which.Passed.Should().BeTrue();
        high.Value.PlannedApprovals.Should().NotBeEmpty();

        var low = await wf.TestAsync(id, new WorkflowTestRequest(J("""{"amount":1000}""")));
        low.Value!.Matched.Should().BeFalse();

        (await wf.ActivateAsync(id)).IsSuccess.Should().BeTrue();

        tc.SetUser(Direction);
        var fired = await wf.EvaluateAsync(new WorkflowTriggerRequest("discount_applied", J("""{"amount":4200}"""), "sale", Guid.NewGuid()));
        fired.Value!.Matched.Should().BeTrue();
        fired.Value.ApprovalRequestId.Should().NotBeNull();

        var quiet = await wf.EvaluateAsync(new WorkflowTriggerRequest("discount_applied", J("""{"amount":500}"""), null, null));
        quiet.Value!.Matched.Should().BeFalse();
    }

    // ---- Audit 2026-10 éd. 2 : un workflow d'avance sur salaire crée réellement sa demande d'approbation ----
    [Fact]
    public async Task A_salary_advance_workflow_creates_its_approval_request()
    {
        await using var s = Scope();
        var wf = s.ServiceProvider.GetRequiredService<IWorkflowService>();
        var tc = (TestTenantContext)s.ServiceProvider.GetRequiredService<ITenantContext>();
        var def = new WorkflowDefinitionDto("all", [new WorkflowConditionDto("amount", "gt", J("1000"))], new WorkflowApprovalDto(1, "manager"), [], []);
        var id = (await wf.UpsertAsync(new UpsertWorkflowRequest($"avance-{Guid.NewGuid():N}"[..16], "Avance", "salary_advance", def))).Value;
        (await wf.TestAsync(id, new WorkflowTestRequest(J("""{"amount":5000}""")))).IsSuccess.Should().BeTrue();
        (await wf.ActivateAsync(id)).IsSuccess.Should().BeTrue();
        try
        {
            tc.SetUser(Direction);
            var fired = await wf.EvaluateAsync(new WorkflowTriggerRequest("salary_advance", J("""{"amount":5000}"""), "employee_advance", Guid.NewGuid()));
            fired.IsSuccess.Should().BeTrue(fired.Error?.Message);
            fired.Value!.ApprovalRequestId.Should().NotBeNull("la demande était refusée en silence (type inconnu du service)");
        }
        finally
        {
            await wf.SetStatusAsync(id, "inactive");
        }
    }

    // ---- §19, §47 : automatisation de relance — cible les retards, ignore les désinscrits ----
    [Fact]
    public async Task Dunning_automation_flags_overdue_and_skips_opted_out_customers()
    {
        await using var s = Scope();
        var autos = s.ServiceProvider.GetRequiredService<IAutomationService>();
        var db = s.ServiceProvider.GetRequiredService<NexoraDbContext>();

        var payer = Guid.NewGuid();
        var optedOut = Guid.NewGuid();
        var htg = "(SELECT id FROM nexora.currency WHERE code='HTG')";
        await Exec($"""
            INSERT INTO nexora.customer (id, company_id, customer_number, name, default_currency_id, dunning_opt_out)
              VALUES ('{payer}', '{Company}', 'DUN-{payer:N}', 'Client en retard', {htg}, false),
                     ('{optedOut}', '{Company}', 'DUN-{optedOut:N}', 'Client désinscrit', {htg}, true);
            INSERT INTO nexora.customer_receivable (company_id, customer_id, currency_id, original_amount, issue_date, due_date, status)
              VALUES ('{Company}', '{payer}', {htg}, 9000, current_date - 40, current_date - 20, 'OPEN'),
                     ('{Company}', '{optedOut}', {htg}, 4000, current_date - 40, current_date - 20, 'OPEN');
            """);

        var autoId = (await autos.UpsertAsync(new UpsertAutomationRequest(
            $"dun-{Guid.NewGuid():N}"[..14], "Relance test", "dunning", J("""{"frequency":"daily","atTime":"08:00"}"""), J("{}")))).Value;

        // 08:00 est l'heure de l'entreprise (Port-au-Prince), pas 08:00 UTC (audit 2026-10 éd. 2).
        (await db.Database.SqlQuery<int>($"""
            SELECT extract(hour FROM next_run_at AT TIME ZONE 'America/Port-au-Prince')::int AS "Value" FROM nexora.automation WHERE id = {autoId}
            """).FirstAsync()).Should().Be(8);

        var run = await autos.RunNowAsync(autoId, preview: false);
        run.IsSuccess.Should().BeTrue(run.Error?.Message);
        run.Value!.Status.Should().Be("success");
        run.Value.ItemsProcessed.Should().BeGreaterThanOrEqualTo(1);

        var dunned = await db.Database.SqlQuery<int>($"""
            SELECT COUNT(*)::int AS "Value" FROM nexora.notification
            WHERE company_id = {Company} AND type = 'dunning' AND reference_id = {payer}
            """).FirstAsync();
        dunned.Should().BeGreaterThan(0);

        var skipped = await db.Database.SqlQuery<int>($"""
            SELECT COUNT(*)::int AS "Value" FROM nexora.notification
            WHERE company_id = {Company} AND type = 'dunning' AND reference_id = {optedOut}
            """).FirstAsync();
        skipped.Should().Be(0);
    }

    // ---- §39, §47 : une nouvelle version de configuration ne réécrit pas l'historique ----
    [Fact]
    public async Task Config_version_snapshot_preserves_which_rule_was_active_at_a_given_time()
    {
        await using var s = Scope();
        var versions = s.ServiceProvider.GetRequiredService<IConfigVersionService>();

        var v1 = (await versions.SnapshotAsync("numbering", "état initial")).Value!;
        var past = DateTimeOffset.UtcNow.AddDays(-30);
        (await versions.ActivateAsync(v1.Id, new ActivateVersionRequest(past, null))).IsSuccess.Should().BeTrue();

        await Exec($"UPDATE nexora.document_sequence SET prefix = 'VNT' WHERE company_id = '{Company}' AND document_type = 'sale'");

        var v2 = (await versions.SnapshotAsync("numbering", "nouveau préfixe ventes")).Value!;
        v2.VersionNumber.Should().Be(v1.VersionNumber + 1);
        var now = DateTimeOffset.UtcNow;
        (await versions.ActivateAsync(v2.Id, new ActivateVersionRequest(now, null))).IsSuccess.Should().BeTrue();

        (await versions.ActiveAtAsync("numbering", now.AddDays(-15)))!.VersionNumber.Should().Be(v1.VersionNumber);
        (await versions.ActiveAtAsync("numbering", now.AddMinutes(1)))!.VersionNumber.Should().Be(v2.VersionNumber);

        var list = await versions.ListAsync("numbering");
        list.First(v => v.Id == v1.Id).Status.Should().Be("superseded");
        list.First(v => v.Id == v1.Id).EffectiveTo.Should().NotBeNull();
    }

    // ---- §44 : le centre de tâches détecte les lacunes de configuration ----
    [Fact]
    public async Task Admin_tasks_detect_products_without_a_sale_price()
    {
        await using var s = Scope();
        var catalog = s.ServiceProvider.GetRequiredService<CatalogService>();
        var tasks = s.ServiceProvider.GetRequiredService<IAdminTaskService>();

        await catalog.CreateProductAsync(new CreateProductCommand(
            $"NOPRICE-{Guid.NewGuid():N}", "Article sans prix", null, BoxUnit, "stockable", null, null, null, null, false, false));

        var list = await tasks.ListAsync();
        var task = list.FirstOrDefault(t => t.Key == "products_without_price");
        task.Should().NotBeNull();
        task!.Count.Should().BeGreaterThanOrEqualTo(1);
        task.Severity.Should().Be("warning");
    }

    // ---- Finalisation phase 11 : rôles — moindre privilège, rôle inactif sans effet ----
    [Fact]
    public async Task Role_cannot_grant_what_the_admin_does_not_hold_and_a_disabled_role_grants_nothing()
    {
        await using var s = Scope();
        var roles = s.ServiceProvider.GetRequiredService<ICustomRoleService>();
        var perms = s.ServiceProvider.GetRequiredService<IPermissionService>();

        (await roles.CreateAsync(new CreateRoleRequest($"Escalade {Guid.NewGuid():N}"[..20], null, null, ["sales.view", "platform.manage_billing"])))
            .Error!.Code.Should().Be("roles.escalation");
        var id = (await roles.CreateAsync(new CreateRoleRequest($"Magasin {Guid.NewGuid():N}"[..20], "Manager magasin", null,
            ["sales.view", "sales.create", "inventory.view"]))).Value;
        (await roles.SetPermissionsAsync(id, new SetRolePermissionsRequest(["sales.view", "platform.manage_billing"])))
            .Error!.Code.Should().Be("roles.escalation", "élargir un rôle (peut-être le sien) au-delà de ses propres droits est refusé");
        (await roles.SetPermissionsAsync(id, new SetRolePermissionsRequest([]))).Error!.Code.Should().Be("roles.permission");
        (await roles.SetPermissionsAsync(id, new SetRolePermissionsRequest(["sales.view", "sales.create"]))).IsSuccess.Should().BeTrue();

        (await roles.UpdateAsync(id, new UpdateRoleRequest("manager", null))).Error!.Code.Should().Be("roles.duplicate", "nom d'un rôle système");
        (await roles.UpdateAsync(id, new UpdateRoleRequest($"Responsable magasin {Guid.NewGuid():N}"[..28], "Ventes"))).IsSuccess.Should().BeTrue();

        var list = await roles.ListAsync();
        list.Should().Contain(r => r.Name == "manager" && r.SystemRole, "les rôles système sont visibles (base de copie)");
        list.Should().NotContain(r => r.Name.StartsWith("platform_"));
        var system = list.First(r => r.Name == "manager");
        (await roles.SetPermissionsAsync(system.Id, new SetRolePermissionsRequest(["sales.view"]))).IsSuccess.Should().BeFalse();
        (await roles.PermissionCatalogAsync()).Count.Should().BeGreaterThan(100);

        // Affecté à un utilisateur : désactivé (confirmé), il ne donne plus aucune permission.
        var user = Guid.NewGuid();
        var scopeId = Guid.NewGuid();
        await Exec($"""
            INSERT INTO nexora.app_user (id, email, full_name, password_hash, status)
            VALUES ('{user}', 'mag-{user:N}@test.local', 'Test Magasin', 'x', 'active');
            INSERT INTO nexora.access_scope (id, scope_type, company_id) VALUES ('{scopeId}', 'COMPANY', '{Company}');
            INSERT INTO nexora.user_role (user_id, role_id, scope_id) VALUES ('{user}', '{id}', '{scopeId}');
            """);
        (await perms.LoadProfileAsync(user)).PermissionsFor(Company).Should().Contain("sales.create");
        (await roles.DisableAsync(id, force: false)).Error!.Code.Should().Be("roles.assigned");
        (await roles.DisableAsync(id, force: true)).IsSuccess.Should().BeTrue();
        (await perms.LoadProfileAsync(user)).PermissionsFor(Company).Should().NotContain("sales.create");
        (await roles.EnableAsync(id)).IsSuccess.Should().BeTrue();
        (await perms.LoadProfileAsync(user)).PermissionsFor(Company).Should().Contain("sales.create");
    }

    // ---- Finalisation phase 11 : workflow — validation, seuil réel, désactivation effective, rôle approbateur imposé ----
    [Fact]
    public async Task Workflow_rules_threshold_deactivation_and_required_approver_role()
    {
        await using var s = Scope();
        var wf = s.ServiceProvider.GetRequiredService<IWorkflowService>();
        var approvals = s.ServiceProvider.GetRequiredService<IApprovalWorkflowService>();
        var tc = (TestTenantContext)s.ServiceProvider.GetRequiredService<ITenantContext>();
        var code = $"credit-{Guid.NewGuid():N}"[..16];
        WorkflowDefinitionDto Def(string op, string value, WorkflowApprovalDto? appr, IReadOnlyList<string>? actions = null)
            => new("all", [new WorkflowConditionDto("amount", op, J(value))], appr, actions ?? [], []);

        (await wf.CatalogAsync()).Triggers.Should().Contain(t => t.Key == "credit_sale" && t.Automatic);
        (await wf.UpsertAsync(new UpsertWorkflowRequest(code, "x", "pluie", Def("gte", "100", new(1, "manager"))))).Error!.Code.Should().Be("workflow.trigger");
        (await wf.UpsertAsync(new UpsertWorkflowRequest(code, "x", "credit_sale", Def("gte", "100", null)))).Error!.Code.Should().Be("workflow.approval");
        (await wf.UpsertAsync(new UpsertWorkflowRequest(code, "x", "credit_sale", Def("gte", "100", new(1, "inexistant"))))).Error!.Code.Should().Be("workflow.approver_role");
        (await wf.UpsertAsync(new UpsertWorkflowRequest(code, "x", "credit_sale", Def("gte", "100", new(1, "manager"), ["envoyer_sms"])))).Error!.Code.Should().Be("workflow.actions");
        (await wf.UpsertAsync(new UpsertWorkflowRequest(code, "x", "credit_sale",
            new WorkflowDefinitionDto("all", [new WorkflowConditionDto("customer_type", "eq", J("\"vip\""))], new(1, "manager"), [], []))))
            .Error!.Code.Should().Be("workflow.condition", "les opérations réelles ne connaissent que le montant");

        var id = (await wf.UpsertAsync(new UpsertWorkflowRequest(code, "Crédit hors limite", "credit_sale", Def("gte", "50000", new(1, "chief_accountant"))))).Value;
        (await wf.TestAsync(id, new WorkflowTestRequest(J("""{"amount":60000}""")))).Value!.Matched.Should().BeTrue();
        (await wf.ActivateAsync(id)).IsSuccess.Should().BeTrue();
        (await Scalar($"SELECT min_amount::text || '|' || status FROM nexora.approval_policy WHERE configuration->>'workflow' = '{code}'"))
            .Should().Be("50000.0000|active", "le seuil du workflow devient celui de la règle réellement appliquée");
        (await wf.UpsertAsync(new UpsertWorkflowRequest(code, "Crédit", "credit_sale", Def("gte", "1", new(1, "manager"))))).Error!.Code.Should().Be("workflow.active");

        // Rôle approbateur imposé : Direction (owner_direction) n'est pas « chief_accountant ».
        tc.SetUser(ChiefAccountant);
        var req = await approvals.SubmitAsync(new Nexora.Contracts.Security.SubmitApprovalRequest("credit_sale", "test", Guid.NewGuid(), 60000m, "test"));
        req.Value!.ApprovalRequired.Should().BeTrue();
        tc.SetUser(Direction);
        (await approvals.DecideAsync(req.Value.RequestId!.Value, new Nexora.Contracts.Security.ApprovalDecisionRequest(true, "ok")))
            .Error!.Code.Should().Be("security.approver_role");

        // Désactivé : la règle cesse de s'appliquer aux opérations réelles.
        (await wf.SetStatusAsync(id, "inactive")).IsSuccess.Should().BeTrue();
        (await Scalar($"SELECT status FROM nexora.approval_policy WHERE configuration->>'workflow' = '{code}'")).Should().Be("inactive");
        (await wf.UpsertAsync(new UpsertWorkflowRequest(code, "Crédit hors limite", "credit_sale", Def("gt", "70000", new(2, "chief_accountant"))))).IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task Automation_without_an_executor_cannot_be_created()
    {
        await using var s = Scope();
        var autos = s.ServiceProvider.GetRequiredService<IAutomationService>();
        (await autos.UpsertAsync(new UpsertAutomationRequest($"rep-{Guid.NewGuid():N}"[..14], "Rapport", "scheduled_report",
            J("""{"frequency":"daily"}"""), J("{}")))).Error!.Code.Should().Be("automation.kind");
        (await autos.UpsertAsync(new UpsertAutomationRequest($"exp-{Guid.NewGuid():N}"[..14], "Péremption", "expiry_alert",
            J("{}"), J("""{"withinDays":15}""")))).Error!.Code.Should().Be("automation.schedule");
        (await autos.UpsertAsync(new UpsertAutomationRequest($"exp-{Guid.NewGuid():N}"[..14], "Péremption", "expiry_alert",
            J("""{"frequency":"weekly"}"""), J("""{"withinDays":15}""")))).IsSuccess.Should().BeTrue();
    }

    private async Task<string?> Scalar(string sql)
    {
        await using var c = new Npgsql.NpgsqlConnection(fx.TestConnectionString);
        await c.OpenAsync();
        await using var cmd = new Npgsql.NpgsqlCommand(sql, c);
        return (await cmd.ExecuteScalarAsync())?.ToString();
    }
}
