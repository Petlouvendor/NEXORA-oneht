using System.Globalization;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Nexora.Application.Abstractions;
using Nexora.Application.Group;
using Nexora.Contracts.Group;
using Nexora.Domain.Common;
using Nexora.Infrastructure.Persistence;
using Npgsql;

namespace Nexora.IntegrationTests;

/// <summary>Scénarios de validation Multi-entreprises, Groupe & Consolidation (Phase 9 §33).</summary>
[Collection("finance")]
public sealed class GroupConsolidationTests(FinanceEngineFixture fx)
{
    private static readonly Guid Group = Guid.Parse("dce00000-0000-0000-0000-000000000001");
    private static readonly Guid Pharma = Guid.Parse("dce00000-0000-0000-0000-0000000000c1");
    private static readonly Guid Resto = Guid.Parse("dce00000-0000-0000-0000-0000000000c2");
    private static readonly Guid Market = Guid.Parse("dce00000-0000-0000-0000-0000000000c3");

    private AsyncServiceScope GroupScope()
    {
        var s = fx.Provider.CreateAsyncScope();
        var tc = (TestTenantContext)s.ServiceProvider.GetRequiredService<ITenantContext>();
        tc.SetScope([Pharma, Resto, Market], [Group], Pharma);
        return s;
    }

    // --- Raw balanced posted journal entry (bypasses the engine for test data) ---
    private async Task PostEntryAsync(Guid companyId, DateOnly date, string description,
        params (string Code, decimal Debit, decimal Credit)[] lines)
    {
        await using var c = new NpgsqlConnection(fx.TestConnectionString);
        await c.OpenAsync();
        await using var tx = await c.BeginTransactionAsync();

        var journalId = await ScalarAsync<Guid>(c, "SELECT id FROM nexora.journal WHERE company_id = @c AND code = 'OD'", companyId);
        var periodId = await ScalarAsync<Guid>(c,
            "SELECT id FROM nexora.accounting_period WHERE company_id = @c AND status = 'open' ORDER BY start_date DESC LIMIT 1", companyId);
        var currencyId = await ScalarAsync<Guid>(c, "SELECT default_currency_id FROM nexora.company WHERE id = @c", companyId);

        var entryId = Guid.NewGuid();
        var number = $"OD-{Guid.NewGuid():N}"[..14];
        await ExecAsync(c,
            """
            INSERT INTO nexora.journal_entry (id, company_id, journal_id, accounting_period_id, entry_number, entry_date, description, status, posted_at)
            VALUES (@id, @c, @j, @p, @n, @d, @desc, 'posted', now())
            """,
            ("id", entryId), ("c", companyId), ("j", journalId), ("p", periodId), ("n", number),
            ("d", date), ("desc", description));

        var ln = 1;
        foreach (var (code, debit, credit) in lines)
        {
            var accountId = await ScalarAsync<Guid>(c,
                "SELECT id FROM nexora.account WHERE company_id = @c AND code = @code", companyId, code);
            await ExecAsync(c,
                """
                INSERT INTO nexora.journal_entry_line (journal_entry_id, company_id, account_id, line_number, debit, credit, currency_id, base_debit, base_credit)
                VALUES (@e, @c, @a, @ln, @dr, @cr, @cur, @dr, @cr)
                """,
                ("e", entryId), ("c", companyId), ("a", accountId), ("ln", ln++),
                ("dr", debit), ("cr", credit), ("cur", currencyId));
        }
        await tx.CommitAsync();
    }

    private static async Task<T> ScalarAsync<T>(NpgsqlConnection c, string sql, params object[] args)
    {
        await using var cmd = new NpgsqlCommand(sql, c);
        cmd.Parameters.AddWithValue("c", args[0]);
        if (args.Length > 1) cmd.Parameters.AddWithValue("code", args[1]);
        return (T)(await cmd.ExecuteScalarAsync())!;
    }

    private static async Task ExecAsync(NpgsqlConnection c, string sql, params (string Name, object Value)[] ps)
    {
        await using var cmd = new NpgsqlCommand(sql, c);
        foreach (var (n, v) in ps) cmd.Parameters.AddWithValue(n, v);
        await cmd.ExecuteNonQueryAsync();
    }

    // ---- §33 : plan comptable groupe — mapping versionné (§12) ------------
    [Fact]
    public async Task Group_account_mapping_is_versioned()
    {
        await using var s = GroupScope();
        var ledger = s.ServiceProvider.GetRequiredService<IGroupLedgerService>();
        var db = s.ServiceProvider.GetRequiredService<NexoraDbContext>();

        var code = $"T{Guid.NewGuid():N}"[..6];
        await db.Database.ExecuteSqlAsync($"""
            INSERT INTO nexora.account (chart_of_accounts_id, company_id, code, name, account_type)
            SELECT id, {Pharma}, {code}, 'Compte test mapping', 'expense'
            FROM nexora.chart_of_accounts WHERE company_id = {Pharma} AND status = 'active'
            """);

        (await ledger.SetMappingAsync(Group, new SetMappingRequest(Pharma, code, "GA-EXP"))).IsSuccess.Should().BeTrue();
        (await ledger.SetMappingAsync(Group, new SetMappingRequest(Pharma, code, "GA-COGS"))).IsSuccess.Should().BeTrue();

        var mappings = await db.Database.SqlQuery<string>($"""
            SELECT m.status AS "Value" FROM nexora.account_group_mapping m
            JOIN nexora.account a ON a.id = m.account_id
            WHERE a.company_id = {Pharma} AND a.code = {code} ORDER BY m.created_at
            """).ToListAsync();
        mappings.Should().BeEquivalentTo(["superseded", "active"]);
    }

    // ---- §33 : vente intercompany + achat correspondant + rapprochement (§14-15) --
    [Fact]
    public async Task Intercompany_transactions_reconcile_automatically()
    {
        await using var s = GroupScope();
        var ic = s.ServiceProvider.GetRequiredService<IIntercompanyService>();
        var day = new DateOnly(2026, 2, 10);

        var sale = await ic.RecordAsync(Group, new RecordIntercompanyRequest(
            Pharma, Resto, "sale_purchase", 5000m, "HTG", day, "VTE-IC-1", null));
        sale.IsSuccess.Should().BeTrue(sale.Error?.Message);
        var purchase = await ic.RecordAsync(Group, new RecordIntercompanyRequest(
            Resto, Pharma, "sale_purchase", 5000m, "HTG", day, "FAF-IC-1", null));
        purchase.IsSuccess.Should().BeTrue();
        (await ic.ReconcileAsync(Group, new ReconcileIntercompanyRequest(sale.Value, null))).Error!.Code
            .Should().Be("intercompany.not_confirmed", "une opération en brouillon ne se rapproche pas");
        (await ic.ConfirmAsync(Group, sale.Value)).IsSuccess.Should().BeTrue();
        (await ic.ConfirmAsync(Group, purchase.Value)).IsSuccess.Should().BeTrue();

        var match = await ic.ReconcileAsync(Group, new ReconcileIntercompanyRequest(sale.Value, null));
        match.IsSuccess.Should().BeTrue(match.Error?.Message);
        match.Value!.Status.Should().Be("matched");
        match.Value.CounterpartTransactionId.Should().Be(purchase.Value);

        var unreconciled = await ic.UnreconciledAsync(Group);
        unreconciled.Should().NotContain(t => t.Id == sale.Value || t.Id == purchase.Value);

        // Montants asymétriques → statut 'asymmetric'.
        var s2 = await ic.RecordAsync(Group, new RecordIntercompanyRequest(Pharma, Resto, "loan", 5000m, "HTG", day, null, null));
        var c2 = await ic.RecordAsync(Group, new RecordIntercompanyRequest(Resto, Pharma, "loan", 4800m, "HTG", day, null, null));
        await ic.ConfirmAsync(Group, s2.Value);
        await ic.ConfirmAsync(Group, c2.Value);
        var m2 = await ic.ReconcileAsync(Group, new ReconcileIntercompanyRequest(s2.Value, c2.Value));
        m2.Value!.Status.Should().Be("asymmetric");
        m2.Value.AmountDifference.Should().Be(200m);
    }

    // ---- §33 : agréger + convertir + générer un bilan consolidé équilibré ----
    [Fact]
    public async Task Consolidation_aggregates_and_balances()
    {
        await using var s = GroupScope();
        var consol = s.ServiceProvider.GetRequiredService<IConsolidationService>();
        var day = new DateOnly(2026, 3, 15);

        // Pharmacie : capital 100 000 en trésorerie ; puis vente 12 000 encaissée, COGS 7 000.
        await PostEntryAsync(Pharma, day, "Apport", ("5711", 100_000m, 0m), ("1011", 0m, 100_000m));
        await PostEntryAsync(Pharma, day, "Vente", ("5711", 12_000m, 0m), ("7011", 0m, 12_000m));
        await PostEntryAsync(Pharma, day, "COGS", ("6031", 7_000m, 0m), ("3110", 0m, 7_000m));
        // Restaurant : capital 40 000 ; vente 8 000 ; charges 3 000.
        await PostEntryAsync(Resto, day, "Apport", ("5711", 40_000m, 0m), ("1011", 0m, 40_000m));
        await PostEntryAsync(Resto, day, "Vente", ("5711", 8_000m, 0m), ("7011", 0m, 8_000m));
        await PostEntryAsync(Resto, day, "Charges", ("6800", 3_000m, 0m), ("5711", 0m, 3_000m));

        var run = await consol.CreateRunAsync(Group, new CreateConsolidationRunRequest(
            "Mars 2026", new DateOnly(2026, 3, 1), new DateOnly(2026, 3, 31), [Pharma, Resto], null));
        run.IsSuccess.Should().BeTrue(run.Error?.Message);

        var executed = await consol.ExecuteAsync(Group, run.Value!.Id);
        executed.IsSuccess.Should().BeTrue(executed.Error?.Message);
        executed.Value!.Status.Should().Be("ready");
        executed.Value.CoherenceOk.Should().BeTrue();
        executed.Value.TotalDebit.Should().Be(executed.Value.TotalCredit);
        executed.Value.Companies.Should().OnlyContain(c => c.MappingComplete);

        var statements = await consol.GetStatementsAsync(Group, run.Value.Id);
        statements.IsSuccess.Should().BeTrue(statements.Error?.Message);
        statements.Value!.TrialBalanceBalanced.Should().BeTrue();
        // CA consolidé = 12 000 + 8 000 ; résultat = CA − COGS − charges.
        statements.Value.TotalRevenue.Should().Be(20_000m);
        statements.Value.NetResult.Should().Be(10_000m);   // 20 000 − 7 000 − 3 000
        statements.Value.BalanceSheetBalanced.Should().BeTrue();
        statements.Value.ByCompany.Should().HaveCount(2);
    }

    // ---- §33 : appliquer une élimination intercompany (§16) -------------
    [Fact]
    public async Task Consolidation_eliminates_matched_intercompany()
    {
        await using var s = GroupScope();
        var consol = s.ServiceProvider.GetRequiredService<IConsolidationService>();
        var ic = s.ServiceProvider.GetRequiredService<IIntercompanyService>();
        var day = new DateOnly(2026, 4, 12);

        // A vend à B à crédit 6 000 : A -> Clients / Ventes ; B -> Charges / Fournisseurs.
        await PostEntryAsync(Pharma, day, "Apport", ("5711", 50_000m, 0m), ("1011", 0m, 50_000m));
        await PostEntryAsync(Pharma, day, "Vente intragroupe", ("4111", 6_000m, 0m), ("7011", 0m, 6_000m));
        await PostEntryAsync(Resto, day, "Apport", ("5711", 20_000m, 0m), ("1011", 0m, 20_000m));
        await PostEntryAsync(Resto, day, "Achat intragroupe", ("6011", 6_000m, 0m), ("4011", 0m, 6_000m));

        var sale = await ic.RecordAsync(Group, new RecordIntercompanyRequest(Pharma, Resto, "sale_purchase", 6_000m, "HTG", day, null, null));
        var purchase = await ic.RecordAsync(Group, new RecordIntercompanyRequest(Resto, Pharma, "sale_purchase", 6_000m, "HTG", day, null, null));
        await ic.ConfirmAsync(Group, sale.Value);
        await ic.ConfirmAsync(Group, purchase.Value);
        (await ic.ReconcileAsync(Group, new ReconcileIntercompanyRequest(sale.Value, purchase.Value))).Value!.Status.Should().Be("matched");

        var run = await consol.CreateRunAsync(Group, new CreateConsolidationRunRequest(
            "Avril 2026", new DateOnly(2026, 4, 1), new DateOnly(2026, 4, 30), [Pharma, Resto], null));
        var executed = await consol.ExecuteAsync(Group, run.Value!.Id);
        executed.IsSuccess.Should().BeTrue(executed.Error?.Message);
        executed.Value!.EliminationTotal.Should().Be(6_000m);
        executed.Value.CoherenceOk.Should().BeTrue();
        executed.Value.TotalDebit.Should().Be(executed.Value.TotalCredit);

        var st = await consol.GetStatementsAsync(Group, run.Value.Id);
        var ar = st.Value!.TrialBalance.Single(l => l.GroupAccountCode == "GA-AR");
        var ap = st.Value.TrialBalance.Single(l => l.GroupAccountCode == "GA-AP");
        ar.EliminationDebit.Should().Be(6_000m);    // créance intragroupe retirée du débit
        ap.EliminationCredit.Should().Be(6_000m);   // dette intragroupe retirée du crédit
        ar.ConsolidatedDebit.Should().Be(0m);
        ap.ConsolidatedCredit.Should().Be(0m);
        st.Value.TrialBalanceBalanced.Should().BeTrue();
    }

    // ---- §33 : cycle de vie brouillon → validé → publié (§18) ----------
    [Fact]
    public async Task Consolidation_lifecycle_is_enforced()
    {
        await using var s = GroupScope();
        var consol = s.ServiceProvider.GetRequiredService<IConsolidationService>();
        var day = new DateOnly(2026, 5, 9);
        await PostEntryAsync(Pharma, day, "Apport", ("5711", 10_000m, 0m), ("1011", 0m, 10_000m));

        var run = (await consol.CreateRunAsync(Group, new CreateConsolidationRunRequest(
            "Mai 2026", new DateOnly(2026, 5, 1), new DateOnly(2026, 5, 31), [Pharma], null))).Value!;

        (await consol.ValidateAsync(Group, run.Id)).IsSuccess.Should().BeFalse();   // pas encore exécuté

        (await consol.ExecuteAsync(Group, run.Id)).Value!.Status.Should().Be("ready");
        (await consol.PublishAsync(Group, run.Id)).IsSuccess.Should().BeFalse();     // pas encore validé

        (await consol.ValidateAsync(Group, run.Id)).Value!.Status.Should().Be("validated");
        (await consol.PublishAsync(Group, run.Id)).Value!.Status.Should().Be("published");

        (await consol.ReopenAsync(Group, run.Id, new ReopenRunRequest(""))).IsSuccess.Should().BeFalse();   // motif requis
        (await consol.ReopenAsync(Group, run.Id, new ReopenRunRequest("Correction TVA"))).Value!.Status.Should().Be("reopened");
    }

    // ---- §33 : centre de contrôle + tâches groupe (§24-25) -------------
    [Fact]
    public async Task Control_center_and_group_tasks()
    {
        await using var s = GroupScope();
        var cc = s.ServiceProvider.GetRequiredService<IGroupControlCenter>();
        var tasks = s.ServiceProvider.GetRequiredService<IGroupTaskService>();

        var summary = await cc.SummaryAsync(Group);
        summary.Companies.Select(c => c.CompanyId).Should().Contain([Pharma, Resto, Market]);

        var taskId = await tasks.CreateAsync(Group, new CreateGroupTaskRequest(
            "Clôturer mars — Restaurant", null, Resto, fx.UserId, "high", TestDates.Today.AddDays(15)));
        taskId.IsSuccess.Should().BeTrue(taskId.Error?.Message);
        (await tasks.ListAsync(Group)).Should().Contain(t => t.Id == taskId.Value && t.Priority == "high");

        (await tasks.UpdateAsync(Group, taskId.Value, new UpdateGroupTaskRequest("done", "Fait"))).IsSuccess.Should().BeTrue();
        (await tasks.ListAsync(Group)).First(t => t.Id == taskId.Value).Status.Should().Be("done");
    }

    // ---- §33 : ajouter une entreprise sans nouvelle installation, puis archiver (§28-29) --
    [Fact]
    public async Task Company_provisioned_then_archived_keeps_history()
    {
        await using var s = GroupScope();
        var prov = s.ServiceProvider.GetRequiredService<ICompanyProvisioningService>();
        var db = s.ServiceProvider.GetRequiredService<NexoraDbContext>();

        var created = await prov.ProvisionAsync(new ProvisionCompanyRequest($"Services ABC {Guid.NewGuid():N}"[..18], "services", "HTG", Group));
        created.IsSuccess.Should().BeTrue(created.Error?.Message);
        var companyId = created.Value;

        var wired = await db.Database.SqlQuery<int>($"""
            SELECT (SELECT COUNT(*) FROM nexora.journal WHERE company_id = {companyId})
                 + (SELECT COUNT(*) FROM nexora.accounting_period WHERE company_id = {companyId})
                 + (SELECT COUNT(*) FROM nexora.chart_of_accounts WHERE company_id = {companyId}) AS "Value"
            """).FirstAsync();
        wired.Should().Be(10);  // 8 journaux (dont AN/CL et PA — paie, V076) + 1 période + 1 plan comptable

        // Résumé fonctionnel §20 (V072) : l'entreprise créée peut travailler tout de suite — plan
        // comptable rempli, correspondances du moteur comptable, caisse enregistreuse prête.
        var ready = await db.Database.SqlQuery<ReadyState>($"""
            SELECT (SELECT COUNT(*)::int FROM nexora.account WHERE company_id = {companyId}) AS "Accounts",
                   (SELECT COUNT(*)::int FROM nexora.account_mapping WHERE company_id = {companyId}) AS "Mappings",
                   (SELECT COUNT(*)::int FROM nexora.payment_method WHERE company_id = {companyId}) AS "PaymentMethods",
                   (SELECT COUNT(*)::int FROM nexora.cash_register r
                      JOIN nexora.financial_account f ON f.id = r.financial_account_id AND f.ledger_account_id IS NOT NULL
                      JOIN nexora.inventory_location l ON l.store_id = r.store_id
                     WHERE r.company_id = {companyId}) AS "ReadyRegisters"
            """).FirstAsync();
        ready.Should().Be(new ReadyState(30, 36, 4, 1));   // + 8 comptes et 8 correspondances de paie (V076), + 4081 (V096)
        (await db.Database.SqlQuery<bool>($"""
            SELECT EXISTS(SELECT 1 FROM nexora.company_activity WHERE company_id = {companyId} AND is_primary) AS "Value"
            """).FirstAsync()).Should().BeTrue();

        // Archivage : désactive les nouvelles opérations, conserve tout l'historique (§29).
        (await prov.ArchiveAsync(Group, companyId, new ArchiveCompanyRequest(""))).IsSuccess.Should().BeFalse();   // motif requis
        (await prov.ArchiveAsync(Group, companyId, new ArchiveCompanyRequest("Cessation d'activité"))).IsSuccess.Should().BeTrue();
        (await prov.ArchiveAsync(Group, companyId, new ArchiveCompanyRequest("Encore"))).Error!.Code.Should().Be("company.already_archived");
        (await db.Database.SqlQuery<string>($"SELECT archive_reason AS \"Value\" FROM nexora.company WHERE id = {companyId}").FirstAsync())
            .Should().Be("Cessation d'activité", "le motif d'archivage n'était conservé nulle part");

        var archived = await db.Database.SqlQuery<ArchivedState>($"""
            SELECT status AS "Status", (archived_at IS NOT NULL) AS "Stamped",
                   (SELECT COUNT(*)::int FROM nexora.journal WHERE company_id = {companyId}) AS "Journals",
                   (SELECT COUNT(*)::int FROM nexora.company_module WHERE company_id = {companyId} AND enabled) AS "EnabledModules"
            FROM nexora.company WHERE id = {companyId}
            """).FirstAsync();
        archived.Status.Should().Be("archived");
        archived.Stamped.Should().BeTrue();
        archived.Journals.Should().Be(8);          // historique conservé
        archived.EnabledModules.Should().Be(0);    // plus de nouvelles opérations
    }

    private sealed record ArchivedState(string Status, bool Stamped, int Journals, int EnabledModules);
    private sealed record ReadyState(int Accounts, int Mappings, int PaymentMethods, int ReadyRegisters);

    // ---- §33 : tenter un accès interdit (hors périmètre groupe) (§21-22) --
    [Fact]
    public async Task Group_access_denied_outside_scope()
    {
        await using var s = fx.Provider.CreateAsyncScope();
        var tc = (TestTenantContext)s.ServiceProvider.GetRequiredService<ITenantContext>();
        tc.SetScope([Pharma], [], Pharma);   // aucun périmètre groupe

        var ledger = s.ServiceProvider.GetRequiredService<IGroupLedgerService>();
        var act = async () => await ledger.ListAccountsAsync(Group);
        (await act.Should().ThrowAsync<DomainException>()).Which.Error.Kind.Should().Be(ErrorKind.Forbidden);

        var consol = s.ServiceProvider.GetRequiredService<IConsolidationService>();
        var act2 = async () => await consol.ListRunsAsync(Group);
        await act2.Should().ThrowAsync<DomainException>();
    }

    // ======================= Finalisation phase 14 : isolation et règles du groupe =======================

    private async Task<Guid> UserWithRoleAsync(string role, string scopeType, Guid? companyId)
    {
        await using var s = fx.Provider.CreateAsyncScope();
        var db = s.ServiceProvider.GetRequiredService<NexoraDbContext>();
        var userId = Guid.NewGuid();
        await db.Database.ExecuteSqlAsync($"""
            INSERT INTO nexora.app_user (id, email, full_name, password_hash, status)
            VALUES ({userId}, {$"g{userId:N}@test.local"}::citext, 'Test groupe', 'x', 'active')
            """);
        var scopeId = Guid.NewGuid();
        if (scopeType == "GROUP")
            await db.Database.ExecuteSqlAsync($"INSERT INTO nexora.access_scope (id, scope_type, group_id) VALUES ({scopeId}, 'GROUP', {Group})");
        else
            await db.Database.ExecuteSqlAsync($"INSERT INTO nexora.access_scope (id, scope_type, company_id) VALUES ({scopeId}, 'COMPANY', {companyId})");
        await db.Database.ExecuteSqlAsync($"""
            INSERT INTO nexora.user_role (user_id, role_id, scope_id)
            SELECT {userId}, id, {scopeId} FROM nexora.role WHERE name = {role} AND system_role
            """);
        return userId;
    }

    [Fact]
    public async Task Group_rights_come_only_from_a_role_held_at_group_level()
    {
        await using var s = GroupScope();
        var perms = s.ServiceProvider.GetRequiredService<IPermissionService>();

        // Le gérant d'UNE société (rôle manager, qui porte group.view) ne voit pas les sociétés sœurs.
        var manager = await perms.LoadProfileAsync(await UserWithRoleAsync("manager", "COMPANY", Market));
        manager.PermissionsFor(Market).Should().NotContain("group.view", "un rôle de société ouvrait tout le groupe");
        manager.PermissionsFor(Market).Should().Contain("sales.view");
        manager.GroupPermissionsFor(Group).Should().BeEmpty();

        // Le même type de droit, attribué au niveau du groupe, vaut pour le groupe entier.
        var auditor = await perms.LoadProfileAsync(await UserWithRoleAsync("auditor", "GROUP", null));
        auditor.GroupPermissionsFor(Group).Should().Contain(["group.view", "reporting.view_group"]);   // consolidation.view : supprimée (V097, jamais vérifiée)
        auditor.PermissionsFor(Resto).Should().Contain("group.view");
    }

    [Fact]
    public async Task Group_level_roles_are_granted_and_revoked_from_group_level_only()
    {
        var target = await UserWithRoleAsync("cashier", "COMPANY", Market);
        var companyAdmin = await UserWithRoleAsync("owner_direction", "COMPANY", Market);

        // Direction (rôle de groupe) : attribue un rôle au niveau du groupe.
        Guid assignment;
        await using (var s = GroupScope())
        {
            var users = s.ServiceProvider.GetRequiredService<Nexora.Application.Identity.IUserAssignmentService>();
            var granted = await users.GrantAsync(target, new Nexora.Contracts.Identity.GrantAssignmentRequest("auditor", "GROUP", Market, null, null, null, "Audit annuel"));
            granted.IsSuccess.Should().BeTrue(granted.Error?.Message);
            assignment = granted.Value;
            var profile = await s.ServiceProvider.GetRequiredService<IPermissionService>().LoadProfileAsync(target);
            profile.GroupPermissionsFor(Group).Should().Contain("group.view");

            // Retirée puis rendue : l'identifiant renvoyé est celui de l'affectation réactivée (il n'existait pas).
            (await users.RevokeAsync(assignment, new Nexora.Contracts.Identity.RevokeAssignmentRequest("Pause"))).IsSuccess.Should().BeTrue();
            var again = await users.GrantAsync(target, new Nexora.Contracts.Identity.GrantAssignmentRequest("auditor", "GROUP", Market, null, null, null, "Reprise"));
            again.Value.Should().Be(assignment);
            (await users.RevokeAsync(again.Value, new Nexora.Contracts.Identity.RevokeAssignmentRequest("Fin"))).IsSuccess.Should().BeTrue();
            (await users.GrantAsync(target, new Nexora.Contracts.Identity.GrantAssignmentRequest("auditor", "GROUP", Market, null, null, null, "Retour"))).IsSuccess.Should().BeTrue();
        }

        // Administrateur d'une seule société : ni attribution au niveau du groupe, ni retrait du rôle de groupe.
        await using (var s = fx.Provider.CreateAsyncScope())
        {
            var tc = (TestTenantContext)s.ServiceProvider.GetRequiredService<ITenantContext>();
            tc.SetUser(companyAdmin);
            tc.SetScope([Market], [Group], Market);
            var users = s.ServiceProvider.GetRequiredService<Nexora.Application.Identity.IUserAssignmentService>();
            (await users.GrantAsync(target, new Nexora.Contracts.Identity.GrantAssignmentRequest("auditor", "GROUP", Market, null, null, null, null)))
                .Error!.Code.Should().Be("identity.escalation");
            (await users.RevokeAsync(assignment, new Nexora.Contracts.Identity.RevokeAssignmentRequest("Départ")))
                .Error!.Code.Should().Be("identity.group_scope", "un administrateur de société retirait l'accès de la direction du groupe");
            // Au niveau de sa société, il attribue toujours un rôle qui porte des droits de groupe (inertes).
            (await users.GrantAsync(target, new Nexora.Contracts.Identity.GrantAssignmentRequest("manager", "COMPANY", Market, null, null, null, null)))
                .IsSuccess.Should().BeTrue();
        }
    }

    [Fact]
    public async Task Intercompany_reconciliation_refuses_double_elimination_and_wrong_direction()
    {
        await using var s = GroupScope();
        var ic = s.ServiceProvider.GetRequiredService<IIntercompanyService>();
        var day = new DateOnly(2026, 6, 3);
        (await ic.RecordAsync(Group, new RecordIntercompanyRequest(Pharma, Resto, "loan", 0m, "HTG", day, null, null)))
            .Error!.Code.Should().Be("intercompany.amount");

        async Task<Guid> Confirmed(Guid from, Guid to, decimal amount, string? doc = null)
        {
            var id = (await ic.RecordAsync(Group, new RecordIntercompanyRequest(from, to, "transfer", amount, "HTG", day, doc, null))).Value;
            (await ic.ConfirmAsync(Group, id)).IsSuccess.Should().BeTrue();
            return id;
        }
        var a = await Confirmed(Pharma, Resto, 900m, "VIR-77");
        var sameWay = await Confirmed(Pharma, Resto, 900m);
        var b = await Confirmed(Resto, Pharma, 900m);

        (await ic.ReconcileAsync(Group, new ReconcileIntercompanyRequest(a, sameWay))).Error!.Code
            .Should().Be("intercompany.direction", "une opération de même sens n'est pas une contrepartie");
        (await ic.ReconcileAsync(Group, new ReconcileIntercompanyRequest(a, b))).Value!.Status.Should().Be("matched");
        // Repartir de la contrepartie créait un second rapprochement : la vente aurait été éliminée deux fois.
        (await ic.ReconcileAsync(Group, new ReconcileIntercompanyRequest(b, null))).Error!.Code.Should().Be("intercompany.already_matched");
        (await ic.ReconcileAsync(Group, new ReconcileIntercompanyRequest(sameWay, a))).Error!.Code.Should().Be("intercompany.direction");

        var listed = (await ic.ListAsync(Group)).Single(t => t.Id == a);
        listed.SourceDocument.Should().Be("VIR-77");
        listed.OccurredAt.Should().Be(day, "la date était relue en heure locale : la veille, après 20 h à Haïti");
        listed.CounterpartId.Should().Be(b);
        (await ic.ListAsync(Group)).Single(t => t.Id == b).MatchStatus.Should().Be("matched");
    }

    [Fact]
    public async Task Group_policy_and_mappings_are_really_saved_and_consistent()
    {
        await using var s = GroupScope();
        var ledger = s.ServiceProvider.GetRequiredService<IGroupLedgerService>();
        var db = s.ServiceProvider.GetRequiredService<NexoraDbContext>();

        // Politique absente : la mise à jour seule renvoyait un succès sans rien enregistrer.
        await db.Database.ExecuteSqlAsync($"DELETE FROM nexora.group_policy WHERE group_id = {Group}");
        (await ledger.SetPolicyAsync(Group, new SetGroupPolicyRequest(null, null, 25m))).IsSuccess.Should().BeTrue();
        (await ledger.GetPolicyAsync(Group)).IntercompanyTolerance.Should().Be(25m);
        (await ledger.SetPolicyAsync(Group, new SetGroupPolicyRequest(null, "average_rate", null))).Error!.Code
            .Should().Be("group.fx_method_unsupported", "la consolidation n'applique que le taux de clôture");

        (await ledger.SetMappingAsync(Group, new SetMappingRequest(Pharma, "7011", "GA-AR"))).Error!.Code
            .Should().Be("group.mapping_type", "un produit rattaché à un compte d'actif fausse les états");
        var mappings = (await ledger.ListMappingsAsync(Group, Pharma)).Value!;
        var sales = mappings.Single(m => m.Code == "7011");
        sales.GroupAccountCode.Should().NotBeNull();
        (await ledger.SetMappingAsync(Group, new SetMappingRequest(Pharma, "7011", sales.GroupAccountCode!))).IsSuccess.Should().BeTrue();
        (await ledger.ListMappingsAsync(Group, Pharma)).Value!.Single(m => m.Code == "7011").Versions
            .Should().Be(sales.Versions, "reposer la même correspondance ne crée pas de version");
        (await ledger.UpsertAccountAsync(Group, new UpsertGroupAccountRequest(sales.GroupAccountCode!, "Ventes", "asset"))).Error!.Code
            .Should().Be("group.account_type_in_use");

        var options = await ledger.OptionsAsync(Group);
        options.Companies.Select(c => c.Id).Should().Contain([Pharma, Resto, Market]);
        options.SupportedFxMethods.Should().Equal("closing_rate");
        options.Users.Should().Contain(u => u.Id == fx.UserId);
    }

    [Fact]
    public async Task Consolidation_without_exchange_rate_cannot_be_validated()
    {
        await using var s = GroupScope();
        var consol = s.ServiceProvider.GetRequiredService<IConsolidationService>();
        var db = s.ServiceProvider.GetRequiredService<NexoraDbContext>();
        var day = new DateOnly(2026, 7, 8);
        await PostEntryAsync(Pharma, day, "Apport", ("5711", 1_000m, 0m), ("1011", 0m, 1_000m));
        // Consolidation en CAD : aucun taux HTG → CAD.
        await db.Database.ExecuteSqlAsync($"""
            UPDATE nexora.group_policy SET consolidation_currency_id = (SELECT id FROM nexora.currency WHERE code = 'CAD')
            WHERE group_id = {Group}
            """);
        try
        {
            (await consol.CreateRunAsync(Group, new CreateConsolidationRunRequest(" ", day, day, [Pharma], null))).Error!.Code
                .Should().Be("consolidation.label");
            var run = (await consol.CreateRunAsync(Group, new CreateConsolidationRunRequest(
                "Juillet CAD", new DateOnly(2026, 7, 1), new DateOnly(2026, 7, 31), [Pharma], null))).Value!;
            var executed = (await consol.ExecuteAsync(Group, run.Id)).Value!;
            executed.CoherenceOk.Should().BeFalse("une conversion à 1 donnerait des états faux");
            executed.Diagnostics.Should().Contain(d => d.Contains("non validable"));
            (await consol.ValidateAsync(Group, run.Id)).IsSuccess.Should().BeFalse();

            // Taux de clôture saisi : la conversion s'applique et la consolidation devient validable.
            await db.Database.ExecuteSqlAsync($"""
                INSERT INTO nexora.exchange_rate (base_currency_id, quote_currency_id, rate, effective_at)
                SELECT h.id, c.id, 0.0104, '2026-07-31 12:00:00+00' FROM nexora.currency h, nexora.currency c
                WHERE h.code = 'HTG' AND c.code = 'CAD'
                """);
            var converted = (await consol.ExecuteAsync(Group, run.Id)).Value!;
            converted.Companies.Single().FxRate.Should().Be(0.0104m);
            converted.CoherenceOk.Should().BeTrue();
            // D'autres tests de la collection écrivent aussi en juillet chez Pharmacie : on compare au total non converti.
            converted.TotalDebit.Should().BeApproximately(Math.Round(executed.TotalDebit * 0.0104m, 2), 0.05m);
            converted.TotalDebit.Should().BeGreaterThanOrEqualTo(10.40m);   // au moins nos 1 000 HTG × 0,0104
            (await consol.ValidateAsync(Group, run.Id)).Value!.Status.Should().Be("validated");
        }
        finally
        {
            await db.Database.ExecuteSqlAsync($"""
                UPDATE nexora.group_policy SET consolidation_currency_id = (SELECT id FROM nexora.currency WHERE code = 'HTG')
                WHERE group_id = {Group}
                """);
        }
    }

    [Fact]
    public async Task Archiving_and_tasks_stay_inside_the_group()
    {
        await using var s = GroupScope();
        var prov = s.ServiceProvider.GetRequiredService<ICompanyProvisioningService>();
        var tasks = s.ServiceProvider.GetRequiredService<IGroupTaskService>();
        var db = s.ServiceProvider.GetRequiredService<NexoraDbContext>();

        // Une société indépendante (un autre client) ne s'archive pas depuis ce groupe.
        var outsider = Guid.NewGuid();
        await db.Database.ExecuteSqlAsync($"""
            INSERT INTO nexora.company (id, name, main_activity_type, default_currency_id, complexity_level, deployment_mode)
            SELECT {outsider}, 'Client indépendant', 'services', id, 'STANDARD', 'HYBRID' FROM nexora.currency WHERE code = 'HTG'
            """);
        (await prov.ArchiveAsync(Group, outsider, new ArchiveCompanyRequest("Tentative"))).Error!.Code.Should().Be("company.not_found");
        (await db.Database.SqlQuery<string>($"SELECT status AS \"Value\" FROM nexora.company WHERE id = {outsider}").FirstAsync())
            .Should().Be("active");

        (await prov.ProvisionAsync(new ProvisionCompanyRequest("pharmacie abc", "pharmacy", "HTG", Group))).Error!.Code
            .Should().Be("company.name_exists");

        var stranger = Guid.NewGuid();
        await db.Database.ExecuteSqlAsync($"""
            INSERT INTO nexora.app_user (id, email, full_name, password_hash, status)
            VALUES ({stranger}, {$"x{stranger:N}@ailleurs.test"}::citext, 'Hors groupe', 'x', 'active')
            """);
        var due = TestDates.Today.AddDays(5);
        (await tasks.CreateAsync(Group, new CreateGroupTaskRequest("Contrôle", null, null, stranger, "normal", due))).Error!.Code
            .Should().Be("group.task_assignee");
        (await tasks.CreateAsync(Group, new CreateGroupTaskRequest("Contrôle", null, null, null, "normal", new DateOnly(2020, 1, 1)))).Error!.Code
            .Should().Be("group.task_due");
        var t = (await tasks.CreateAsync(Group, new CreateGroupTaskRequest("Contrôle", null, Pharma, fx.UserId, "normal", due))).Value;
        (await tasks.UpdateAsync(Group, t, new UpdateGroupTaskRequest("blocked", null))).Error!.Code.Should().Be("group.task_notes_required");
    }
}
