using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Nexora.Application.Abstractions;
using Nexora.Application.Administration;
using Nexora.Application.Catalog;
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
            ["notify_manager"], ["manager"]);
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
}
