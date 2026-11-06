using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Nexora.Application.Abstractions;
using Nexora.Application.Experience;
using Nexora.Application.Finance.Ledger;
using Nexora.Application.Intelligence;
using Nexora.Application.Operations;
using Nexora.Application.Preferences;
using Nexora.Application.Reporting;
using Nexora.Contracts.Finance;
using Nexora.Contracts.Intelligence;
using Nexora.Contracts.Operations;
using Nexora.Contracts.Preferences;
using Nexora.Contracts.Reporting;
using Npgsql;

namespace Nexora.IntegrationTests;

/// <summary>
/// Constats faibles de l'audit 2026-10 éd. 2 (lot 7) : appartenance des personnes assignées et des destinataires,
/// disposition partagée, double contrôle facultatif des écritures manuelles, politique de purge « archive » et date
/// limite bornée, identifiant de connexion partagé entre plusieurs clients.
/// </summary>
[Collection("finance")]
public sealed class WeakFindingsTests(FinanceEngineFixture fx)
{
    private static readonly Guid Company = Guid.Parse("dce00000-0000-0000-0000-0000000000c1");
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

    private async Task<T?> Scalar<T>(string sql)
    {
        await using var c = new NpgsqlConnection(fx.TestConnectionString);
        await c.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, c);
        var v = await cmd.ExecuteScalarAsync();
        return v is null or DBNull ? default : (T)v;
    }

    /// <summary>Utilisateur sans aucun rôle ; avec <paramref name="role"/>, membre de l'entreprise avec ce rôle système.</summary>
    private async Task<Guid> NewUser(string? role = null)
    {
        var id = Guid.NewGuid();
        await Sql($"""
            INSERT INTO nexora.app_user (id, email, password_hash, full_name, status)
            VALUES ('{id}', 'weak-{id:N}@test.demo', crypt('x', gen_salt('bf', 4)), 'Constat faible', 'active')
            """);
        if (role is not null)
            await Sql($"""
                INSERT INTO nexora.user_role (user_id, role_id, scope_id)
                SELECT '{id}', r.id, sc.id FROM nexora.role r, nexora.access_scope sc
                WHERE r.name = '{role}' AND r.system_role AND sc.scope_type = 'COMPANY' AND sc.company_id = '{Company}'
                LIMIT 1
                """);
        return id;
    }

    // ---- Tâches : l'assigné appartient à l'entreprise ; seul l'assigné ou l'auteur termine une tâche assignée ----
    [Fact]
    public async Task A_task_is_assigned_to_a_member_and_completed_by_the_assignee_or_its_author()
    {
        var stranger = await NewUser();
        var colleague = await NewUser("cashier");
        await using var s = Scope();

        (await For<ITaskService>(s.ServiceProvider, Direction).CreateAsync(new CreateTaskRequest("Hors entreprise", null, null, null, stranger)))
            .Error!.Code.Should().Be("task.assignee");

        var task = await For<ITaskService>(s.ServiceProvider, Direction).CreateAsync(new CreateTaskRequest("Pour la compta", null, null, null, Compta));
        task.IsSuccess.Should().BeTrue(task.Error?.Message);

        (await For<ITaskService>(s.ServiceProvider, colleague).CompleteAsync(task.Value!.Id))
            .Error!.Code.Should().Be("task.not_assignee", "un collègue ne termine pas la tâche d'un autre");
        (await For<ITaskService>(s.ServiceProvider, Compta).CompleteAsync(task.Value.Id)).IsSuccess.Should().BeTrue();
    }

    // ---- Anomalies : l'assigné appartient à l'entreprise ----
    [Fact]
    public async Task An_insight_cannot_be_assigned_to_someone_outside_the_company()
    {
        var stranger = await NewUser();
        var insight = Guid.NewGuid();
        await Sql($"""
            INSERT INTO nexora.insight (id, company_id, category, title, detail, drill_path, period_start)
            VALUES ('{insight}', '{Company}', 'cash_gap', 'Écart', 'Test', '/weak/{insight:N}', '2026-03-01')
            """);
        await using var s = Scope();

        (await For<IInsightService>(s.ServiceProvider, Direction).AssignAsync(insight, new AssignInsightRequest(stranger)))
            .Error!.Code.Should().Be("insight.assignee");
        (await For<IInsightService>(s.ServiceProvider, Direction).AssignAsync(insight, new AssignInsightRequest(Compta)))
            .IsSuccess.Should().BeTrue();
    }

    // ---- Rapports planifiés : chaque destinataire est un utilisateur de l'entreprise ----
    [Fact]
    public async Task A_report_schedule_only_sends_to_members_of_the_company()
    {
        var stranger = await NewUser();
        await using var s = Scope();
        var schedules = For<IReportScheduleService>(s.ServiceProvider, Direction);

        (await schedules.SaveAsync(new SaveReportScheduleRequest(null, "Fuite", "sales_journal", "weekly", null, "csv",
            [Direction.ToString(), stranger.ToString()]))).Error!.Code.Should().Be("schedule.recipients");
        (await schedules.SaveAsync(new SaveReportScheduleRequest(null, "Adresse libre", "sales_journal", "weekly", null, "csv",
            ["quelquun@exemple.test"]))).Error!.Code.Should().Be("schedule.recipients");
    }

    // ---- Disposition partagée : seul un administrateur de l'entreprise la supprime ----
    [Fact]
    public async Task Only_a_company_administrator_deletes_a_shared_dashboard_layout()
    {
        var cashier = await NewUser("cashier");
        await using var s = Scope();
        var shared = await For<IDashboardLayoutService>(s.ServiceProvider, Direction).SaveCompanyDefaultAsync(new SaveDashboardLayoutRequest(
            null, $"Partagé {Guid.NewGuid():N}", JsonDocument.Parse("""[{"key":"cash","x":0,"y":0,"w":4,"h":2}]""").RootElement.Clone(),
            AsCompanyDefault: true));
        shared.IsSuccess.Should().BeTrue(shared.Error?.Message);

        (await For<IDashboardLayoutService>(s.ServiceProvider, cashier).DeleteAsync(shared.Value!.Id))
            .Error!.Code.Should().Be("layout.shared");
        (await Scalar<long>($"SELECT count(*) FROM nexora.dashboard_layout WHERE id = '{shared.Value.Id}'")).Should().Be(1);
        (await For<IDashboardLayoutService>(s.ServiceProvider, Direction).DeleteAsync(shared.Value.Id)).IsSuccess.Should().BeTrue();
    }

    // ---- Écritures manuelles : double contrôle sur paramètre de l'entreprise (désactivé par défaut) ----
    [Fact]
    public async Task Manual_entry_four_eyes_applies_only_when_the_company_enables_it()
    {
        static CreateJournalEntryRequest Entry() => new("OD", new DateOnly(2026, 6, 6), "HTG", "Double contrôle",
            [new JournalEntryLineInput("5711", 500m, 0m), new JournalEntryLineInput("7011", 0m, 500m)]);

        await using var s = Scope();
        // Par défaut : l'auteur valide sa propre écriture (structure à un seul comptable).
        var solo = await For<IManualJournalService>(s.ServiceProvider, Compta).CreateDraftAsync(Entry());
        (await For<IManualJournalService>(s.ServiceProvider, Compta).PostAsync(solo.Value)).IsSuccess.Should().BeTrue();

        await Sql($"""
            INSERT INTO nexora.setting (key, scope, scope_id, company_id, value)
            VALUES ('accounting.manual_entry_four_eyes', 'company', '{Company}', '{Company}', 'true'::jsonb)
            ON CONFLICT (key, scope, scope_id) DO UPDATE SET value = 'true'::jsonb
            """);
        try
        {
            await using var s2 = Scope();
            var draft = await For<IManualJournalService>(s2.ServiceProvider, Compta).CreateDraftAsync(Entry());
            (await For<IManualJournalService>(s2.ServiceProvider, Compta).PostAsync(draft.Value))
                .Error!.Code.Should().Be("accounting.four_eyes");
            var byOther = await For<IManualJournalService>(s2.ServiceProvider, Direction).PostAsync(draft.Value);
            byOther.IsSuccess.Should().BeTrue(byOther.Error?.Message);
        }
        finally
        {
            await Sql($"DELETE FROM nexora.setting WHERE key = 'accounting.manual_entry_four_eyes' AND scope_id = '{Company}'");
        }
    }

    // ---- Fil d'activité : une écriture manuelle extournée reste visible, signalée comme telle (A-043) ----
    [Fact]
    public async Task A_reversed_manual_entry_stays_in_the_activity_feed()
    {
        await using var s = Scope();
        var journal = For<IManualJournalService>(s.ServiceProvider, Compta);
        var draft = await journal.CreateDraftAsync(new CreateJournalEntryRequest("OD", new DateOnly(2026, 6, 9), "HTG", "Extournée au fil",
            [new JournalEntryLineInput("5711", 700m, 0m), new JournalEntryLineInput("7011", 0m, 700m)]));
        var posted = await journal.PostAsync(draft.Value);
        posted.IsSuccess.Should().BeTrue(posted.Error?.Message);
        var reversed = await For<IManualJournalService>(s.ServiceProvider, Direction).ReverseAsync(draft.Value, new ReverseJournalEntryRequest("Erreur d'imputation"));
        reversed.IsSuccess.Should().BeTrue(reversed.Error?.Message);

        var feed = await For<IActivityFeedService>(s.ServiceProvider, Compta).RecentAsync(50);
        feed.Should().Contain(e => e.Kind == "journal_entry" && e.Title == $"Écriture manuelle {posted.Value!.EntryNumber} (extournée)");
    }

    // ---- Mise en production : l'exercice comptable couvre aujourd'hui et les semaines qui suivent (A-042) ----
    [Fact]
    public async Task The_go_live_checklist_checks_the_fiscal_year_ahead()
    {
        var company = Guid.NewGuid();
        await Sql($"""
            INSERT INTO nexora.company (id, name, default_currency_id)
            VALUES ('{company}', 'Exercice {company:N}', (SELECT id FROM nexora.currency WHERE code = 'HTG'));
            """);
        async Task<string> Status()
        {
            await using var s = Scope();
            var tc = (TestTenantContext)s.ServiceProvider.GetRequiredService<ITenantContext>();
            tc.SetUser(Direction);
            tc.SetScope([company], [], company);
            var r = await s.ServiceProvider.GetRequiredService<IOperationsService>().GoLiveReadinessAsync();
            return r.Checks.Single(c => c.Key == "fiscal_year_ahead").Status;
        }

        (await Status()).Should().Be("fail", "aucun exercice");
        await Sql($"""
            INSERT INTO nexora.accounting_period (company_id, name, start_date, end_date)
            VALUES ('{company}', 'Court', current_date - 300, current_date + 20)
            """);
        (await Status()).Should().Be("warn", "l'exercice se termine dans vingt jours sans suivant");
        await Sql($"""
            INSERT INTO nexora.accounting_period (company_id, name, start_date, end_date)
            VALUES ('{company}', 'Suivant', current_date + 21, current_date + 385)
            """);
        (await Status()).Should().Be("pass");
    }

    // ---- Règles d'intelligence : seuil validé ; une règle illisible n'empêche pas les autres ----
    [Fact]
    public async Task An_unreadable_rule_threshold_is_refused_and_never_blocks_the_other_rules()
    {
        await using var s = Scope();
        var rules = For<IIntelligenceRuleService>(s.ServiceProvider, Direction);
        SaveIntelligenceRuleRequest Rule(string json) => new("stock_reorder", "stock", "Réassort", JsonDocument.Parse(json).RootElement.Clone(),
            null, "medium", null, "Commander", null, true, null);
        (await rules.SaveAsync(Rule("""{"safetyDays": "sept"}"""))).Error!.Code.Should().Be("intelligence_rule.threshold");
        (await rules.SaveAsync(Rule("""{"safetyDays": 2.5}"""))).Error!.Code.Should().Be("intelligence_rule.threshold");
        (await rules.SaveAsync(Rule("""[7]"""))).Error!.Code.Should().Be("intelligence_rule.threshold");

        // Seuil illisible déjà en base (enregistré avant la validation) : la génération continue.
        await Sql($$"""
            INSERT INTO nexora.intelligence_rule (company_id, key, category, label, threshold, default_priority, action_label, enabled)
            VALUES ('{{Company}}', 'stock_reorder', 'stock', 'Cassée', '{"safetyDays": "sept"}', 'medium', 'Commander', true)
            ON CONFLICT ON CONSTRAINT intelligence_rule_key_uk DO UPDATE SET threshold = EXCLUDED.threshold
            """);
        try
        {
            var generate = () => For<IRecommendationService>(s.ServiceProvider, Direction).GenerateAsync();
            await generate.Should().NotThrowAsync();
        }
        finally
        {
            await Sql($"DELETE FROM nexora.intelligence_rule WHERE company_id = '{Company}' AND key = 'stock_reorder'");
        }
    }

    // ---- Anomalie « baisse de marge » : calculée hors taxe ----
    [Fact]
    public async Task The_margin_drop_insight_is_computed_before_tax()
    {
        // Semaine précédente : 100 HT, coût 50 (marge 50 %). Cette semaine : 100 HT + 10 de taxe, coût 60 (marge 40 %).
        // Sur le TTC (110), la marge courante paraissait de 45,5 % : la baisse de 10 points tombait à 4,5 et passait inaperçue.
        var company = Guid.NewGuid();
        var unit = Guid.NewGuid();
        var product = Guid.NewGuid();
        await Sql($"""
            INSERT INTO nexora.company (id, name, default_currency_id)
            VALUES ('{company}', 'Marge {company:N}', (SELECT id FROM nexora.currency WHERE code = 'HTG'));
            INSERT INTO nexora.unit (id, company_id, name, code) VALUES ('{unit}', '{company}', 'Unité', 'U{company:N}'::text);
            INSERT INTO nexora.product (id, company_id, sku, name, unit_id) VALUES ('{product}', '{company}', 'MRG-1', 'Produit marge', '{unit}');
            """);
        foreach (var (daysAgo, tax, cost) in new[] { (10, 0m, 50m), (9, 0m, 50m), (3, 10m, 60m), (2, 10m, 60m) })
        {
            var sale = Guid.NewGuid();
            var total = 100m + tax;
            await Sql($"""
                INSERT INTO nexora.sale (id, company_id, document_number, status, currency_id, sold_at, subtotal, tax_total, total, amount_paid)
                VALUES ('{sale}', '{company}', 'MRG-{sale:N}', 'completed', (SELECT id FROM nexora.currency WHERE code = 'HTG'),
                        now() - interval '{daysAgo} days', 100, {tax}, {total}, {total});
                INSERT INTO nexora.sale_line (sale_id, company_id, product_id, quantity, unit_price, tax_amount, line_total, unit_cost)
                VALUES ('{sale}', '{company}', '{product}', 1, 100, {tax}, {total}, {cost});
                """);
        }

        await using var s = Scope();
        var tc = (TestTenantContext)s.ServiceProvider.GetRequiredService<ITenantContext>();
        tc.SetUser(Direction);
        tc.SetScope([company], [], company);
        await s.ServiceProvider.GetRequiredService<IInsightService>().ScanAsync();

        (await Scalar<string>($"SELECT evidence->>'currentRate' FROM nexora.insight WHERE company_id = '{company}' AND category = 'margin_drop'"))
            .Should().Be("40.0", "marge hors taxe de la semaine : (100 - 60) / 100");
    }

    // ---- Rapports : totaux du journal filtrés, coût moyen pondéré, une ligne de rupture par emplacement ----
    [Fact]
    public async Task Reports_filter_journal_totals_weight_average_cost_and_list_each_rupture_once()
    {
        var company = Guid.NewGuid();
        var (unit, product, empty, loc1, loc2, clientA, clientB) =
            (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        await Sql($"""
            INSERT INTO nexora.company (id, name, default_currency_id)
            VALUES ('{company}', 'Rapports {company:N}', (SELECT id FROM nexora.currency WHERE code = 'HTG'));
            INSERT INTO nexora.unit (id, company_id, name, code) VALUES ('{unit}', '{company}', 'Unité', 'R{company:N}'::text);
            INSERT INTO nexora.product (id, company_id, sku, name, unit_id) VALUES
                ('{product}', '{company}', 'RPT-1', 'Produit valorisé', '{unit}'),
                ('{empty}', '{company}', 'RPT-2', 'Produit en rupture', '{unit}');
            INSERT INTO nexora.inventory_location (id, company_id, name, location_type) VALUES ('{loc1}', '{company}', 'Petit dépôt', 'virtual'), ('{loc2}', '{company}', 'Grand dépôt', 'virtual');
            INSERT INTO nexora.stock_balance (product_id, inventory_location_id, company_id, quantity_on_hand, average_cost) VALUES
                ('{product}', '{loc1}', '{company}', 2, 10), ('{product}', '{loc2}', '{company}', 2000, 1),
                ('{empty}', '{loc1}', '{company}', 0, 0);
            INSERT INTO nexora.product_reorder_rule (company_id, product_id, inventory_location_id, reorder_point) VALUES
                ('{company}', '{empty}', '{loc1}', 5), ('{company}', '{empty}', NULL, 3);
            INSERT INTO nexora.customer (id, company_id, customer_number, name, default_currency_id) VALUES
                ('{clientA}', '{company}', 'A', 'Client A', (SELECT id FROM nexora.currency WHERE code = 'HTG')),
                ('{clientB}', '{company}', 'B', 'Client B', (SELECT id FROM nexora.currency WHERE code = 'HTG'));
            INSERT INTO nexora.sale (company_id, customer_id, document_number, status, currency_id, sold_at, subtotal, total, amount_paid) VALUES
                ('{company}', '{clientA}', 'RPT-A', 'completed', (SELECT id FROM nexora.currency WHERE code = 'HTG'), now() - interval '1 minute', 300, 300, 300),
                ('{company}', '{clientB}', 'RPT-B', 'completed', (SELECT id FROM nexora.currency WHERE code = 'HTG'), now() - interval '1 minute', 900, 900, 900);
            """);

        await using var s = Scope();
        var tc = (TestTenantContext)s.ServiceProvider.GetRequiredService<ITenantContext>();
        tc.SetUser(Direction);
        tc.SetScope([company], [], company);

        var journal = await s.ServiceProvider.GetRequiredService<ISalesReports>().JournalAsync(new ReportFilter { Preset = "today", CustomerId = clientA });
        journal.Rows.Should().ContainSingle();
        journal.TotalRevenue.Should().Be(300m, "les totaux portent sur les ventes affichées");
        journal.Count.Should().Be(1);

        var stock = s.ServiceProvider.GetRequiredService<IStockReports>();
        var valuation = (await stock.ValuationAsync(new ReportFilter())).Rows.Single(r => r.ProductId == product);
        valuation.AverageCost.Should().BeApproximately(2020m / 2002m, 0.0001m, "coût moyen pondéré par les quantités");
        (await stock.RupturesAsync(new ReportFilter())).Where(r => r.ProductId == empty).Should().ContainSingle()
            .Which.ReorderPoint.Should().Be(5m, "la règle de l'emplacement l'emporte sur la règle générale");
    }

    // ---- Catalogue : la liste se lit page par page au-delà de 500 produits ----
    [Fact]
    public async Task The_product_list_is_paged()
    {
        await using var s = Scope();
        var catalog = For<Nexora.Application.Catalog.ICatalogQueries>(s.ServiceProvider, Direction);
        var firstTwo = await catalog.ListProductsAsync(null, null, null, 2);
        var second = await catalog.ListProductsAsync(null, null, null, 1, offset: 1);
        firstTwo.Should().HaveCount(2);
        second.Should().ContainSingle().Which.Id.Should().Be(firstTwo[1].Id, "la page suivante reprend là où la précédente s'arrête");
    }

    // ---- Préférences : fuseau reconnu ; logo intégré, ni tronqué ni externe ----
    [Fact]
    public async Task Timezone_and_logo_are_validated()
    {
        await using var s = Scope();
        (await For<IUserPreferenceService>(s.ServiceProvider, Compta).UpdateAsync(new UpdateUserPreferenceRequest(
                null, null, null, null, null, null, null, null, Timezone: "Mars/Olympus_Mons")))
            .Error!.Code.Should().Be("preferences.timezone");

        var branding = For<Nexora.Application.Preferences.ICompanyBrandingService>(s.ServiceProvider, Direction);
        UpdateCompanyBrandingRequest Logo(string value) => new(null, null, value, null, null, null, null, null);
        (await branding.UpdateAsync(Logo("https://exemple.test/logo.png"))).Error!.Code.Should().Be("branding.logo_url");
        (await branding.UpdateAsync(Logo("data:image/png;base64," + Convert.ToBase64String("<svg onload=alert(1)>"u8.ToArray()))))
            .Error!.Code.Should().Be("branding.logo_url", "le contenu n'est pas une image");

        // Un vrai PNG de plus de 500 caractères est conservé en entier.
        var png = new byte[2_000];
        new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }.CopyTo(png, 0);
        var dataUrl = "data:image/png;base64," + Convert.ToBase64String(png);
        try
        {
            var saved = await branding.UpdateAsync(Logo(dataUrl));
            saved.IsSuccess.Should().BeTrue(saved.Error?.Message);
            saved.Value!.LogoUrl.Should().Be(dataUrl);
        }
        finally
        {
            await branding.UpdateAsync(Logo(""));
        }
    }

    // ---- Journal de sécurité : un échec avalé n'avorte pas la transaction de l'appelant ----
    [Fact]
    public async Task A_failed_security_log_does_not_abort_the_callers_transaction()
    {
        await using var s = Scope();
        For<IUnitOfWork>(s.ServiceProvider, Direction);
        var uow = s.ServiceProvider.GetRequiredService<IUnitOfWork>();
        var log = s.ServiceProvider.GetRequiredService<Nexora.Application.Security.ISecurityEventLog>();
        var db = s.ServiceProvider.GetRequiredService<Nexora.Infrastructure.Persistence.NexoraDbContext>();

        var after = await uow.ExecuteInTransactionAsync(async token =>
        {
            // Adresse IP invalide : l'insertion échoue (cast inet) et l'échec est avalé par le journal.
            await log.RecordAsync("test.invalid_ip", Direction, Company, ipAddress: "pas-une-adresse", ct: token);
            return await db.Database.SqlQuery<int>($"SELECT 1 AS \"Value\"").FirstAsync(token);
        });
        after.Should().Be(1, "la transaction reste utilisable après l'échec de journalisation");
    }

    // ---- Rapprochement bancaire : un mouvement n'est rapproché que d'une ligne, même par deux demandes simultanées ----
    [Fact]
    public async Task A_treasury_movement_is_matched_to_one_statement_line_only()
    {
        var statement = Guid.NewGuid();
        await Sql($"""
            INSERT INTO nexora.bank_statement (id, company_id, financial_account_id, statement_date)
            VALUES ('{statement}', '{Company}', 'dce00000-0000-0000-0000-00000000a051', current_date);
            """);
        var movement = await Scalar<Guid>($"SELECT id FROM nexora.financial_transaction WHERE company_id = '{Company}' LIMIT 1");
        var insert = () => Sql($"""
            INSERT INTO nexora.bank_statement_line (bank_statement_id, company_id, value_date, amount, status, matched_transaction_id)
            VALUES ('{statement}', '{Company}', current_date, 1, 'matched', '{movement}');
            """);
        await insert();
        (await insert.Should().ThrowAsync<PostgresException>()).Which.ConstraintName.Should().Be("bank_statement_line_matched_transaction_uk");
    }

    // ---- Suspens : passage en perte adossé à une écriture validée ----
    [Fact]
    public async Task A_suspense_write_off_requires_a_posted_entry()
    {
        await using var s = Scope();
        var suspense = For<Nexora.Application.Finance.Treasury.ISuspenseService>(s.ServiceProvider, Compta);
        var item = await suspense.OpenAsync(new OpenSuspenseRequest(null, "unidentified", 250m, "HTG", "Écart inexpliqué", null, null, null));
        item.IsSuccess.Should().BeTrue(item.Error?.Message);

        (await suspense.ResolveAsync(item.Value, new ResolveSuspenseRequest("written_off", "Perte constatée")))
            .Error!.Code.Should().Be("finance.suspense_entry");
        (await suspense.ResolveAsync(item.Value, new ResolveSuspenseRequest("written_off", "Perte constatée", "9999-999999")))
            .Error!.Code.Should().Be("finance.suspense_entry");

        var journal = For<IManualJournalService>(s.ServiceProvider, Compta);
        var draft = await journal.CreateDraftAsync(new CreateJournalEntryRequest("OD", new DateOnly(2026, 6, 11), "HTG", "Perte sur suspens",
            [new JournalEntryLineInput("6580", 250m, 0m), new JournalEntryLineInput("5711", 0m, 250m)]));
        draft.IsSuccess.Should().BeTrue(draft.Error?.Message);
        var posted = await journal.PostAsync(draft.Value);
        posted.IsSuccess.Should().BeTrue(posted.Error?.Message);

        (await For<Nexora.Application.Finance.Treasury.ISuspenseService>(s.ServiceProvider, Compta)
                .ResolveAsync(item.Value, new ResolveSuspenseRequest("written_off", "Perte constatée", posted.Value!.EntryNumber)))
            .IsSuccess.Should().BeTrue();
        (await Scalar<Guid>($"SELECT journal_entry_id FROM nexora.treasury_suspense_item WHERE id = '{item.Value}'"))
            .Should().NotBe(Guid.Empty);
    }

    // ---- Calendrier : le fuseau suit l'entreprise active, même dans une portée qui en parcourt plusieurs ----
    [Fact]
    public async Task The_business_calendar_follows_the_active_company()
    {
        var paris = Guid.NewGuid();
        await Sql($"""
            INSERT INTO nexora.company (id, name, default_currency_id, timezone)
            VALUES ('{paris}', 'Fuseau {paris:N}', (SELECT id FROM nexora.currency WHERE code = 'HTG'), 'Europe/Paris');
            """);
        await using var s = Scope();
        var tc = (TestTenantContext)s.ServiceProvider.GetRequiredService<ITenantContext>();
        var calendar = s.ServiceProvider.GetRequiredService<IBusinessCalendar>();

        tc.SetScope([Company], [], Company);
        (await calendar.ZoneAsync()).Id.Should().Contain("Port-au-Prince");
        tc.SetScope([paris], [], paris);
        (await calendar.ZoneAsync()).Id.Should().Be("Europe/Paris", "la première entreprise ne fixe plus le fuseau des suivantes");
        calendar.Zone.Id.Should().Be("Europe/Paris");
    }

    // ---- Export d'audit : au-delà de 200 lignes, et troncature signalée ----
    [Fact]
    public async Task The_audit_export_goes_beyond_two_hundred_lines()
    {
        await using var s = Scope();
        For<IUnitOfWork>(s.ServiceProvider, Direction);
        var audit = s.ServiceProvider.GetRequiredService<Nexora.Application.Security.IAuditQueryService>();
        var total = (await audit.QueryAsync(new Nexora.Contracts.Security.AuditQueryFilter())).Total;
        total.Should().BeGreaterThan(200, "le jeu de démonstration a un journal d'audit fourni");

        var export = await audit.ExportAsync(new Nexora.Contracts.Security.AuditQueryFilter());
        export.IsSuccess.Should().BeTrue(export.Error?.Message);
        var dataLines = export.Value.Content.Split('\n', StringSplitOptions.RemoveEmptyEntries).Count(l => !l.StartsWith('#')) - 1;
        dataLines.Should().Be((int)Math.Min(total, 10_000), "toutes les lignes jusqu'au plafond, pas les 200 premières");
        if (total > 10_000) export.Value.Content.Should().Contain("export tronqué");
    }

    // ---- Purge : la politique « archive » archive sans supprimer ; une date limite future ne dépasse pas la politique ----
    [Fact]
    public async Task Purge_honours_the_archive_policy_and_never_goes_beyond_the_retention_period()
    {
        var owner = await NewUser("cashier");
        var expired = Guid.NewGuid();
        var recentArchived = Guid.NewGuid();
        await Sql($"""
            INSERT INTO nexora.notification (id, company_id, user_id, type, category, title, message, archived_at)
            VALUES ('{recentArchived}', '{Company}', '{owner}', 'system', 'system', 'Récente', 'x', now() - interval '1 day');
            """);
        await using var s = Scope();
        var retention = For<IRetentionPurgeService>(s.ServiceProvider, Direction);

        // Date limite demandée dans le futur : bornée par la politique (la notification archivée hier reste).
        (await retention.ExecuteAsync(new PurgePreviewRequest("resolved_notifications", Company, DateTimeOffset.UtcNow.AddDays(30))))
            .IsSuccess.Should().BeTrue();
        (await Scalar<long>($"SELECT count(*) FROM nexora.notification WHERE id = '{recentArchived}'")).Should().Be(1);

        // Politique « archive » pour l'entreprise : la notification expirée est archivée, pas supprimée.
        await Sql($"""
            INSERT INTO nexora.notification (id, company_id, user_id, type, category, title, message, expires_at, created_at)
            VALUES ('{expired}', '{Company}', '{owner}', 'system', 'system', 'Ancienne', 'x', now() - interval '400 days', now() - interval '500 days')
            """);
        (await retention.SavePolicyAsync(new SaveRetentionPolicyRequest("resolved_notifications", 30, "archive", CompanyId: Company)))
            .IsSuccess.Should().BeTrue();
        try
        {
            var run = await retention.ExecuteAsync(new PurgePreviewRequest("resolved_notifications", Company));
            run.IsSuccess.Should().BeTrue(run.Error?.Message);
            (await Scalar<bool>($"SELECT archived_at IS NOT NULL FROM nexora.notification WHERE id = '{expired}'"))
                .Should().BeTrue("la politique demande d'archiver, pas de supprimer");
        }
        finally
        {
            await Sql($"DELETE FROM nexora.retention_policy WHERE data_type = 'resolved_notifications' AND company_id = '{Company}'");
        }
    }

    // ---- Correction de données : l'identifiant de connexion d'un utilisateur partagé avec un autre client ----
    [Fact]
    public async Task A_login_shared_with_another_client_is_corrected_only_by_the_platform_operator()
    {
        var other = Guid.NewGuid();
        await Sql($"""
            INSERT INTO nexora.company (id, name, default_currency_id)
            VALUES ('{other}', 'Autre client {other:N}', (SELECT id FROM nexora.currency WHERE code = 'HTG'));
            INSERT INTO nexora.access_scope (scope_type, company_id)
            SELECT 'COMPANY', '{other}' WHERE NOT EXISTS (SELECT 1 FROM nexora.access_scope WHERE scope_type = 'COMPANY' AND company_id = '{other}');
            """);
        var shared = await NewUser("cashier");
        var local = await NewUser("cashier");
        await Sql($"""
            INSERT INTO nexora.user_role (user_id, role_id, scope_id)
            SELECT '{shared}', r.id, sc.id FROM nexora.role r, nexora.access_scope sc
            WHERE r.name = 'cashier' AND r.system_role AND sc.scope_type = 'COMPANY' AND sc.company_id = '{other}'
            LIMIT 1
            """);

        var director = await fx.NewCompanyDirectorAsync(Company);
        await using var s = Scope();
        (await For<IDataCorrectionService>(s.ServiceProvider, director)
                .RequestAsync(new RequestDataCorrectionRequest("app_user", shared, "username", $"u{Guid.NewGuid():N}", "Erreur de saisie")))
            .Error!.Code.Should().Be("correction.shared_identity");
        var ok = await For<IDataCorrectionService>(s.ServiceProvider, director)
            .RequestAsync(new RequestDataCorrectionRequest("app_user", local, "username", $"u{Guid.NewGuid():N}", "Erreur de saisie"));
        ok.IsSuccess.Should().BeTrue(ok.Error?.Message);
    }
}
