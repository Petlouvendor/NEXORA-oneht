using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Nexora.Application.Abstractions;
using Nexora.Application.Intelligence;
using Nexora.Contracts.Intelligence;
using Nexora.Contracts.Reporting;
using Npgsql;

namespace Nexora.IntegrationTests;

/// <summary>
/// Intelligence métier & recommandations (Phase 43 §2, §4-5, §12-13, §18, §20-21, §31, §33-39,
/// §45-48) : 4 règles de recommandation (réapprovisionnement, dormant/surstock, priorisation du
/// recouvrement, risque de trésorerie — budget_overrun couvert par un test de non-régression
/// silencieux car il réutilise <c>IBudgetReports</c> déjà testé aux Phases 29/42), scoring
/// explicable, règles configurables, tâches, assistant par rôle.
/// </summary>
[Collection("finance")]
public sealed class IntelligenceEngineTests(FinanceEngineFixture fx)
{
    private static readonly Guid Company = Guid.Parse("dce00000-0000-0000-0000-0000000000c2");
    private static readonly Guid Direction = Guid.Parse("dce00000-0000-0000-0000-0000000000d1");

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

    private async Task<(Guid ProductId, Guid LocationId)> MakeProduct(string sku)
    {
        var locationId = Guid.NewGuid();
        var productId = Guid.NewGuid();
        var unitId = Guid.NewGuid();
        var unitCode = $"IU{Guid.NewGuid():N}"[..8];
        await Sql($"""
            INSERT INTO nexora.inventory_location (id, company_id, name, location_type)
            VALUES ('{locationId}', '{Company}', 'Local intelligence {sku}', 'virtual');
            INSERT INTO nexora.unit (id, company_id, name, code) VALUES ('{unitId}', '{Company}', 'Unité {sku}', '{unitCode}');
            INSERT INTO nexora.product (id, company_id, sku, name, unit_id) VALUES ('{productId}', '{Company}', '{sku}', 'Produit {sku}', '{unitId}');
            """);
        return (productId, locationId);
    }

    /// <summary>Vente ; avec <paramref name="location"/>, la sortie de stock correspondante à cet emplacement.</summary>
    private async Task InsertSaleLine(Guid productId, decimal quantity, decimal unitPrice, decimal unitCost, DateTimeOffset soldAt,
        Guid? location = null)
    {
        var saleId = Guid.NewGuid();
        var lineTotal = quantity * unitPrice;
        await Sql($"""
            INSERT INTO nexora.sale (id, company_id, document_number, status, currency_id, sold_at, subtotal, total, amount_paid)
            VALUES ('{saleId}', '{Company}', 'INT43-{Guid.NewGuid():N}', 'completed',
                    (SELECT id FROM nexora.currency WHERE code = 'HTG'), '{soldAt:O}', {lineTotal}, {lineTotal}, {lineTotal});
            INSERT INTO nexora.sale_line (id, sale_id, company_id, product_id, quantity, unit_price, unit_cost, line_total)
            VALUES ('{Guid.NewGuid()}', '{saleId}', '{Company}', '{productId}', {quantity}, {unitPrice}, {unitCost}, {lineTotal});
            """);
        if (location is { } at)
            await Sql($"""
                INSERT INTO nexora.stock_movement (company_id, product_id, inventory_location_id, movement_type, quantity, unit_cost,
                                                   reference_type, reference_id, occurred_at)
                VALUES ('{Company}', '{productId}', '{at}', 'sale', {-quantity}, {unitCost}, 'sale', '{saleId}', '{soldAt:O}');
                """);
    }

    private Task Opening(Guid productId, Guid location, decimal quantity, DateTimeOffset at) => Sql($"""
        INSERT INTO nexora.stock_movement (company_id, product_id, inventory_location_id, movement_type, quantity, unit_cost,
                                           reference_type, reference_id, occurred_at)
        VALUES ('{Company}', '{productId}', '{location}', 'opening', {quantity}, 6, 'opening', '{Guid.NewGuid()}', '{at:O}');
        """);

    // ---- §12-13, §51 : réapprovisionnement intelligent & produit dormant/surstock ----
    [Fact]
    public async Task Generation_stock_reorder_and_dormant_explain_confidence_and_match_scenario()
    {
        // Produit A : vélocité régulière sur 20 jours distincts, stock sous le seuil, délai fournisseur connu.
        // Audit 2026-10 éd. 2 : un second dépôt, dix fois plus actif, ne gonfle pas la vélocité de celui-ci.
        var (reorderProduct, reorderLocation) = await MakeProduct($"REORD-{Guid.NewGuid():N}"[..12]);
        var busyStore = Guid.NewGuid();
        await Sql($"""
            INSERT INTO nexora.inventory_location (id, company_id, name, location_type)
            VALUES ('{busyStore}', '{Company}', 'Dépôt actif {busyStore:N}', 'virtual');
            """);
        await Opening(reorderProduct, reorderLocation, 105, DateTimeOffset.UtcNow.AddDays(-30));
        await Opening(reorderProduct, busyStore, 5_000, DateTimeOffset.UtcNow.AddDays(-30));
        for (var d = 1; d <= 20; d++)
        {
            await InsertSaleLine(reorderProduct, 5, 10, 6, DateTimeOffset.UtcNow.AddDays(-d), reorderLocation);
            await InsertSaleLine(reorderProduct, 50, 10, 6, DateTimeOffset.UtcNow.AddDays(-d), busyStore);
        }
        await Sql($"""
            INSERT INTO nexora.product_reorder_rule (id, company_id, product_id, reorder_point, reorder_quantity, lead_time_days, status)
            VALUES ('{Guid.NewGuid()}', '{Company}', '{reorderProduct}', 50, 100, 10, 'active');
            """);

        // Produit B : scénario §51 — aucune vente récente (jamais de mouvement 'sale'), marge historique faible, stock élevé.
        var (dormantProduct, dormantLocation) = await MakeProduct($"DORM-{Guid.NewGuid():N}"[..12]);
        await InsertSaleLine(dormantProduct, 10, 100, 95, DateTimeOffset.UtcNow.AddDays(-200));   // marge 5 % — hors fenêtre, jamais de stock_movement 'sale'
        await Sql($"""
            INSERT INTO nexora.stock_balance (id, product_id, inventory_location_id, company_id, quantity_on_hand)
            VALUES ('{Guid.NewGuid()}', '{dormantProduct}', '{dormantLocation}', '{Company}', 40);
            """);

        await using var s = Scope();
        var recs = For<IRecommendationService>(s.ServiceProvider, Direction);

        var created = await recs.GenerateAsync();
        created.Should().BeGreaterThanOrEqualTo(2);

        var all = await recs.ListAsync("new", null);
        var reorder = all.Should().ContainSingle(r => r.Category == "stock_reorder" && r.DrillPath == $"stock/reorder/{reorderProduct}/{reorderLocation}").Which;
        reorder.Evidence.GetProperty("onHand").GetDecimal().Should().Be(5m);
        reorder.Evidence.GetProperty("velocityPerDay").GetDecimal().Should().Be(Math.Round(100m / 60m, 2),
            "100 unités vendues à CET emplacement sur la fenêtre de 60 jours — pas les 1 100 de l'entreprise");
        reorder.ConfidenceLevel.Should().Be("medium", "20 jours d'historique dépasse le minimum (14) sans atteindre le seuil « élevé » (45)");
        reorder.ImpactEstimate.GetProperty("suggestedQuantity").GetDecimal().Should().BeGreaterThan(0);
        reorder.Why.Should().Contain("seuil de réapprovisionnement");
        reorder.PriorityScore.Should().Be(reorder.ImpactScore * reorder.UrgencyScore * reorder.ConfidenceScore, "§46 impact × urgence × confiance");

        var dormant = all.Should().ContainSingle(r => r.Category == "stock_dormant" && r.DrillPath == $"stock/dormant/{dormantProduct}").Which;
        dormant.ProposedAction.Should().Be("Réduire le réapprovisionnement et réévaluer la promotion", "reprend exactement le scénario §51");
        dormant.ConfidenceLevel.Should().Be("high", "la marge est calculée sur des ventes historiques réelles, pas une estimation");
        dormant.Evidence.GetProperty("marginRatePct").GetDecimal().Should().BeLessThan(15m);

        // §37 : un second passage ne recrée pas les mêmes recommandations (déduplication).
        (await recs.GenerateAsync()).Should().Be(0);
    }

    // ---- §18 : priorisation du recouvrement ----
    [Fact]
    public async Task Generation_receivable_priority_ranks_overdue_customer_without_credit_decision()
    {
        var customerId = Guid.NewGuid();
        await Sql($"""
            INSERT INTO nexora.customer (id, company_id, customer_number, name, default_currency_id)
            VALUES ('{customerId}', '{Company}', 'INT43-{Guid.NewGuid():N}'::text, 'Client en retard test',
                    (SELECT id FROM nexora.currency WHERE code = 'HTG'));
            INSERT INTO nexora.customer_receivable (id, company_id, customer_id, currency_id, original_amount, issue_date, due_date, status)
            VALUES ('{Guid.NewGuid()}', '{Company}', '{customerId}', (SELECT id FROM nexora.currency WHERE code = 'HTG'),
                    750, current_date - 60, current_date - 40, 'OVERDUE');
            """);

        await using var s = Scope();
        var recs = For<IRecommendationService>(s.ServiceProvider, Direction);
        await recs.GenerateAsync();

        var all = await recs.ListAsync("new", "receivable_priority");
        var rec = all.Should().ContainSingle(r => r.DrillPath == $"receivables/customer/{customerId}").Which;
        rec.ConfidenceLevel.Should().Be("high", "aucune estimation — les créances enregistrées sont un fait, §18");
        rec.Why.Should().Contain("750");
        rec.ProposedAction.Should().Contain("Relancer");
    }

    // ---- §20, §34 : risque de trésorerie, seuil configurable ----
    [Fact]
    public async Task Generation_cashflow_risk_fires_once_rule_threshold_is_overridden()
    {
        // Repart d'un état propre : un autre test de cette classe partage la même base clonée et
        // peut déjà avoir généré une recommandation cashflow_risk via le seuil système par défaut.
        await Sql($"DELETE FROM nexora.recommendation WHERE company_id = '{Company}' AND category = 'cashflow_risk'");

        await using var s = Scope();
        var rules = For<IIntelligenceRuleService>(s.ServiceProvider, Direction);
        var recs = For<IRecommendationService>(s.ServiceProvider, Direction);

        // Seuil système par défaut (0) peut ne jamais se déclencher sur des données de démo saines —
        // §34 : l'entreprise surcharge sa propre règle avec un seuil qui déclenche à coup sûr.
        var saved = await rules.SaveAsync(new SaveIntelligenceRuleRequest(
            "cashflow_risk", "treasury", "Risque de trésorerie (test)",
            JsonDocument.Parse("""{"minBalance":999999999,"horizonDays":30}""").RootElement,
            30, "critical", null, "Sécuriser la trésorerie", null, true, null));
        saved.IsSuccess.Should().BeTrue(saved.Error?.Message);
        saved.Value!.IsSystemDefault.Should().BeFalse();

        await recs.GenerateAsync();
        var all = await recs.ListAsync("new", "cashflow_risk");
        var rec = all.Should().ContainSingle(r => r.DrillPath == "treasury/liquidity").Which;
        rec.Evidence.GetProperty("minBalance").GetInt64().Should().Be(999999999, "prouve que le seuil de l'entreprise a bien été appliqué, pas le gabarit système");

        (await rules.ListAsync()).Should().Contain(r => r.Key == "cashflow_risk" && r.IsSystemDefault);   // le gabarit système reste visible
        (await rules.DeleteAsync(saved.Value.Id)).IsSuccess.Should().BeTrue();
        (await rules.ListAsync()).Should().NotContain(r => r.Id == saved.Value.Id);
    }

    // ---- §31, §33 : cycle de vie — accepter (+tâche), ignorer, reporter, résoudre ----
    [Fact]
    public async Task Recommendation_lifecycle_accept_creates_task_dismiss_snooze_and_resolve_outcome()
    {
        var customerId = Guid.NewGuid();
        await Sql($"""
            INSERT INTO nexora.customer (id, company_id, customer_number, name, default_currency_id)
            VALUES ('{customerId}', '{Company}', 'INT43L-{Guid.NewGuid():N}'::text, 'Client cycle de vie',
                    (SELECT id FROM nexora.currency WHERE code = 'HTG'));
            INSERT INTO nexora.customer_receivable (id, company_id, customer_id, currency_id, original_amount, issue_date, due_date, status)
            VALUES ('{Guid.NewGuid()}', '{Company}', '{customerId}', (SELECT id FROM nexora.currency WHERE code = 'HTG'),
                    500, current_date - 60, current_date - 35, 'OVERDUE');
            """);

        await using var s = Scope();
        var recs = For<IRecommendationService>(s.ServiceProvider, Direction);
        var tasks = For<ITaskService>(s.ServiceProvider, Direction);
        await recs.GenerateAsync();
        var rec = (await recs.ListAsync("new", "receivable_priority")).Single(r => r.DrillPath == $"receivables/customer/{customerId}");

        var accepted = await recs.AcceptAsync(rec.Id, new AcceptRecommendationRequest(CreateTask: true, AssignTo: Direction, Note: "on relance"));
        accepted.IsSuccess.Should().BeTrue(accepted.Error?.Message);
        accepted.Value!.Status.Should().Be("accepted");

        var myTasks = await tasks.ListAsync(null);
        var createdTask = myTasks.Should().ContainSingle(t => t.ReferenceType == "recommendation" && t.ReferenceId == rec.Id).Which;
        createdTask.AssignedTo.Should().Be(Direction);
        createdTask.Status.Should().Be("open");

        var resolved = await recs.ResolveAsync(rec.Id, new ResolveRecommendationRequest("positive", "recouvré"));
        resolved.IsSuccess.Should().BeTrue(resolved.Error?.Message);
        resolved.Value!.Status.Should().Be("resolved");
        resolved.Value.Outcome.Should().Be("positive");

        (await tasks.CompleteAsync(createdTask.Id)).Value!.Status.Should().Be("done");

        // Une recommandation déjà résolue ne peut plus être ignorée.
        (await recs.DismissAsync(rec.Id, new DismissRecommendationRequest())).Error!.Code.Should().Be("recommendation.not_found");
    }

    // ---- §35 : scoring explicable ----
    [Fact]
    public async Task Scoring_computes_explainable_stock_and_credit_scores()
    {
        var (productId, locationId) = await MakeProduct($"SCORE-{Guid.NewGuid():N}"[..12]);
        for (var d = 1; d <= 10; d++) await InsertSaleLine(productId, 2, 20, 12, DateTimeOffset.UtcNow.AddDays(-d));
        await Sql($"""
            INSERT INTO nexora.stock_balance (id, product_id, inventory_location_id, company_id, quantity_on_hand)
            VALUES ('{Guid.NewGuid()}', '{productId}', '{locationId}', '{Company}', 3);
            """);

        var customerId = Guid.NewGuid();
        await Sql($"""
            INSERT INTO nexora.customer (id, company_id, customer_number, name, default_currency_id)
            VALUES ('{customerId}', '{Company}', 'INT43S-{Guid.NewGuid():N}'::text, 'Client score',
                    (SELECT id FROM nexora.currency WHERE code = 'HTG'));
            INSERT INTO nexora.customer_receivable (id, company_id, customer_id, currency_id, original_amount, issue_date, due_date, status)
            VALUES ('{Guid.NewGuid()}', '{Company}', '{customerId}', (SELECT id FROM nexora.currency WHERE code = 'HTG'),
                    5000, current_date - 120, current_date - 100, 'OVERDUE');
            """);

        await using var s = Scope();
        var scoring = For<IScoringService>(s.ServiceProvider, Direction);
        (await scoring.ComputeAsync()).Should().BeGreaterThan(0);

        var stockScores = await scoring.ListAsync("product");
        var stockScore = stockScores.Should().ContainSingle(x => x.EntityId == productId).Which;
        stockScore.ScoreKey.Should().Be("stock_coverage");
        stockScore.Explanation.GetProperty("coverageDays").GetDecimal().Should().BeGreaterThan(0);

        var creditScores = await scoring.ListAsync("customer");
        var creditScore = creditScores.Should().ContainSingle(x => x.EntityId == customerId).Which;
        creditScore.Level.Should().Be("high", "ancienneté > 90 jours (poids 40) + montant élevé (poids 20) atteint le seuil de 60");
    }

    // ---- §6-10, §28-30, §38 : assistants par rôle & question journalisée ----
    [Fact]
    public async Task Assistant_briefing_reuses_dashboards_per_role_and_query_is_logged()
    {
        await using var s = Scope();
        var assistant = For<IAssistantService>(s.ServiceProvider, Direction);

        var direction = await assistant.BriefingAsync("direction", null);
        direction.Role.Should().Be("direction");
        direction.Dashboard.TryGetProperty("kpis", out _).Should().BeTrue("réutilise CommandCenterResponse tel quel");

        var manager = await assistant.BriefingAsync("manager", null);
        manager.Dashboard.TryGetProperty("criticalStock", out _).Should().BeTrue("réutilise ManagerDashboardResponse tel quel");

        var caissier = await assistant.BriefingAsync("caissier", null);
        caissier.Headline.Should().Contain("Aucune session de caisse ouverte");

        var response = await assistant.QueryAsync(new AskRequest("Combien avons-nous vendu ce mois-ci ?", null));
        response.Intent.Should().NotBeNullOrEmpty();

        var logged = await Scalar<long>($"""
            SELECT COUNT(*) FROM nexora.assistant_query WHERE company_id = '{Company}' AND question LIKE 'Combien avons-nous vendu%'
            """);
        logged.Should().BeGreaterThanOrEqualTo(1);
    }

    private async Task<T> Scalar<T>(string sql)
    {
        await using var c = new NpgsqlConnection(fx.TestConnectionString);
        await c.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, c);
        return (T)(await cmd.ExecuteScalarAsync())!;
    }

    // =========================================================================
    // Phase 54 — Intelligence & Business Decision Engine : les 3 domaines que la Phase 43
    // documentait explicitement en backlog (ventes/clients/fournisseurs), la priorité catégorielle
    // (§7), l'action « modifier » et l'expiration effective (§6, §16, §19, §21), la vue générale (§5).
    // =========================================================================

    /// <summary>Entreprise dédiée et isolée — évite toute pollution de l'agrégat "ventes de
    /// l'entreprise" par les autres tests de cette classe qui insèrent aussi des ventes sur
    /// <see cref="Company"/> (contrairement aux règles produit/client/fournisseur, qui restent
    /// isolées par leurs propres identifiants et peuvent donc partager <see cref="Company"/>).</summary>
    private async Task<Guid> MakeCompany()
    {
        var id = Guid.NewGuid();
        await Sql($"""
            INSERT INTO nexora.company (id, group_id, name, legal_name, default_currency_id)
            VALUES ('{id}', NULL, 'Test Intelligence {id:N}', 'Test Intelligence {id:N} SARL',
                    (SELECT id FROM nexora.currency WHERE code = 'HTG'));
            INSERT INTO nexora.access_scope (scope_type, company_id) VALUES ('COMPANY', '{id}');
            """);
        return id;
    }

    private static T ForCompany<T>(IServiceProvider sp, Guid user, Guid companyId) where T : notnull
    {
        var tc = (TestTenantContext)sp.GetRequiredService<ITenantContext>();
        tc.SetUser(user);
        tc.SetScope([companyId], [], companyId);
        return sp.GetRequiredService<T>();
    }

    private async Task InsertCompanySale(Guid companyId, decimal total, DateTimeOffset soldAt)
    {
        await Sql($"""
            INSERT INTO nexora.sale (id, company_id, document_number, status, currency_id, sold_at, subtotal, total, amount_paid)
            VALUES ('{Guid.NewGuid()}', '{companyId}', 'INT54-{Guid.NewGuid():N}', 'completed',
                    (SELECT id FROM nexora.currency WHERE code = 'HTG'), '{soldAt:O}', {total}, {total}, {total});
            """);
    }

    // ---- §9.2, §7 : hausse inhabituelle = opportunité, jamais confondue avec un risque ----------
    [Fact]
    public async Task Generation_sales_spike_is_an_opportunity_not_a_risk()
    {
        var companyId = await MakeCompany();
        // Fenêtre antérieure (j-28..j-15) : ventes faibles et régulières.
        for (var d = 15; d <= 28; d++) await InsertCompanySale(companyId, 100, DateTimeOffset.UtcNow.AddDays(-d));
        // Fenêtre récente (j-1..j-14) : ventes doublées — hausse très supérieure au seuil (15 %).
        for (var d = 1; d <= 14; d++) await InsertCompanySale(companyId, 220, DateTimeOffset.UtcNow.AddDays(-d));

        await using var s = Scope();
        var recs = ForCompany<IRecommendationService>(s.ServiceProvider, await fx.NewCompanyDirectorAsync(companyId), companyId);
        await recs.GenerateAsync();

        var all = await recs.ListAsync("new", null);
        var spike = all.Should().ContainSingle(r => r.Category == "sales_spike").Which;
        spike.Nature.Should().Be("opportunity", "une croissance de ventes est une opportunité à saisir, jamais un risque (§7)");
        spike.PriorityLevel.Should().Be("opportunity", "le niveau de priorité suit la nature, pas seulement le score (§7)");
        spike.Evidence.GetProperty("growthPct").GetDecimal().Should().BeGreaterThan(15m);
        all.Should().NotContain(r => r.Category == "sales_decline");
    }

    // ---- §9.2, §7 : baisse inhabituelle = risque, priorité critique si sévère --------------------
    [Fact]
    public async Task Generation_sales_decline_flags_a_risk_with_high_priority()
    {
        var companyId = await MakeCompany();
        // Fenêtre antérieure : ventes élevées. Fenêtre récente : ventes à moins de la moitié (baisse > 40 %).
        for (var d = 15; d <= 28; d++) await InsertCompanySale(companyId, 200, DateTimeOffset.UtcNow.AddDays(-d));
        for (var d = 1; d <= 14; d++) await InsertCompanySale(companyId, 80, DateTimeOffset.UtcNow.AddDays(-d));

        await using var s = Scope();
        var recs = ForCompany<IRecommendationService>(s.ServiceProvider, await fx.NewCompanyDirectorAsync(companyId), companyId);
        await recs.GenerateAsync();

        var decline = (await recs.ListAsync("new", "sales_decline")).Should().ContainSingle().Which;
        decline.Nature.Should().Be("risk");
        decline.PriorityLevel.Should().BeOneOf("critical", "important");
        decline.Evidence.GetProperty("declinePct").GetDecimal().Should().BeGreaterThan(20m);
        decline.ExpectedResult.Should().Contain("Identifier la cause");
    }

    // ---- §9.4 : un client autrefois actif devient inactif, jamais une décision de crédit ---------
    [Fact]
    public async Task Generation_customer_inactive_flags_a_previously_active_customer_only()
    {
        var stillActive = Guid.NewGuid();
        var goneInactive = Guid.NewGuid();
        await Sql($"""
            INSERT INTO nexora.customer (id, company_id, customer_number, name, default_currency_id) VALUES
                ('{stillActive}', '{Company}', 'INT54-{Guid.NewGuid():N}', 'Client encore actif', (SELECT id FROM nexora.currency WHERE code = 'HTG')),
                ('{goneInactive}', '{Company}', 'INT54-{Guid.NewGuid():N}', 'Client devenu inactif', (SELECT id FROM nexora.currency WHERE code = 'HTG'));
            """);
        // Client encore actif : 3 achats historiques + 1 achat récent (< 60 jours) → jamais inactif.
        for (var d = 1; d <= 3; d++)
        {
            await Sql($"""
                INSERT INTO nexora.sale (id, company_id, customer_id, document_number, status, currency_id, sold_at, subtotal, total, amount_paid)
                VALUES ('{Guid.NewGuid()}', '{Company}', '{stillActive}', 'INT54-{Guid.NewGuid():N}', 'completed',
                        (SELECT id FROM nexora.currency WHERE code = 'HTG'), now() - interval '{d} days', 50, 50, 50);
                """);
        }
        // Client devenu inactif : 4 achats historiques, tous vieux de plus de 60 jours.
        for (var d = 90; d <= 120; d += 10)
        {
            await Sql($"""
                INSERT INTO nexora.sale (id, company_id, customer_id, document_number, status, currency_id, sold_at, subtotal, total, amount_paid)
                VALUES ('{Guid.NewGuid()}', '{Company}', '{goneInactive}', 'INT54-{Guid.NewGuid():N}', 'completed',
                        (SELECT id FROM nexora.currency WHERE code = 'HTG'), now() - interval '{d} days', 50, 50, 50);
                """);
        }

        await using var s = Scope();
        var recs = For<IRecommendationService>(s.ServiceProvider, Direction);
        await recs.GenerateAsync();

        var all = await recs.ListAsync("new", "customer_inactive");
        all.Should().Contain(r => r.DrillPath == $"customers/inactive/{goneInactive}");
        all.Should().NotContain(r => r.DrillPath == $"customers/inactive/{stillActive}");
        var flagged = all.First(r => r.DrillPath == $"customers/inactive/{goneInactive}");
        flagged.Nature.Should().Be("risk");
        flagged.ConfidenceLevel.Should().Be("high", "basé sur l'historique de ventes réel, sans estimation");
    }

    // ---- §9.5 : hausse des prix fournisseur, par couple produit/fournisseur ----------------------
    [Fact]
    public async Task Generation_supplier_price_increase_compares_recent_to_prior_orders()
    {
        var (productId, _) = await MakeProduct($"SUPP-{Guid.NewGuid():N}"[..12]);
        var supplierId = Guid.NewGuid();
        await Sql($"""
            INSERT INTO nexora.supplier (id, company_id, supplier_number, name, default_currency_id)
            VALUES ('{supplierId}', '{Company}', 'INT54-{Guid.NewGuid():N}', 'Fournisseur test hausse prix',
                    (SELECT id FROM nexora.currency WHERE code = 'HTG'));
            """);

        async Task InsertPo(decimal unitPrice, int daysAgo)
        {
            var poId = Guid.NewGuid();
            await Sql($"""
                INSERT INTO nexora.purchase_order (id, company_id, supplier_id, document_number, status, currency_id, order_date, total)
                VALUES ('{poId}', '{Company}', '{supplierId}', 'INT54-{Guid.NewGuid():N}', 'approved',
                        (SELECT id FROM nexora.currency WHERE code = 'HTG'), current_date - {daysAgo}, {unitPrice});
                INSERT INTO nexora.purchase_order_line (id, purchase_order_id, company_id, product_id, quantity_ordered, unit_price, line_total)
                VALUES ('{Guid.NewGuid()}', '{poId}', '{Company}', '{productId}', 1, {unitPrice}, {unitPrice});
                """);
        }
        // 2 commandes anciennes à 100, 2 commandes récentes à 130 (+30 %, bien au-delà du seuil 10 %).
        await InsertPo(100, 200);
        await InsertPo(100, 180);
        await InsertPo(130, 10);
        await InsertPo(130, 5);

        await using var s = Scope();
        var recs = For<IRecommendationService>(s.ServiceProvider, Direction);
        await recs.GenerateAsync();

        var flagged = (await recs.ListAsync("new", "supplier_price_increase"))
            .Should().ContainSingle(r => r.DrillPath == $"suppliers/price/{supplierId}/{productId}").Which;
        flagged.Evidence.GetProperty("recentAvgPrice").GetDecimal().Should().BeApproximately(130m, 0.01m);
        flagged.Evidence.GetProperty("priorAvgPrice").GetDecimal().Should().BeApproximately(100m, 0.01m);
        flagged.ProposedAction.Should().Contain("Renégocier");
    }

    // ---- §16, §19 : modifier conserve la proposition d'origine ; §6, §21 : expiration effective ---
    [Fact]
    public async Task Modify_keeps_the_original_proposal_and_expired_recommendations_leave_new_status()
    {
        var toModify = Guid.NewGuid();
        var toExpire = Guid.NewGuid();
        const string originalImpact = "{\"suggestedQuantity\":250}";
        const string emptyJson = "{}";
        await Sql($"""
            INSERT INTO nexora.recommendation
                (id, company_id, category, rule_key, title, why, impact_estimate, proposed_action, period_start, expires_at)
            VALUES
                ('{toModify}', '{Company}', 'stock_reorder', 'stock_reorder', 'À modifier', 'motif', '{originalImpact}'::jsonb,
                 'Réapprovisionner de 250 unités', current_date, now() + interval '10 days'),
                ('{toExpire}', '{Company}', 'stock_reorder', 'stock_reorder', 'À expirer', 'motif', '{emptyJson}'::jsonb,
                 'Action non traitée', current_date - 40, now() - interval '1 day');
            """);

        await using var s = Scope();
        var recs = For<IRecommendationService>(s.ServiceProvider, Direction);

        var modified = await recs.ModifyAsync(toModify, new ModifyRecommendationRequest(
            JsonSerializer.SerializeToElement(new { suggestedQuantity = 400 }), "Ajusté après vérification manuelle du stock."));
        modified.IsSuccess.Should().BeTrue(modified.Error?.Message);
        modified.Value!.Status.Should().Be("modified");
        modified.Value.OverrideValue!.Value.GetProperty("suggestedQuantity").GetInt32().Should().Be(400);
        modified.Value.ImpactEstimate.GetProperty("suggestedQuantity").GetInt32().Should().Be(250, "la proposition d'origine n'est jamais écrasée (§18)");

        await recs.GenerateAsync();   // expire d'abord les recommandations `new` échues (§6, §21)

        (await recs.GetAsync(toExpire)).Value!.Status.Should().Be("expired");
        (await recs.GetAsync(toModify)).Value!.Status.Should().Be("modified", "seul le statut `new` peut expirer, une décision humaine ne s'efface pas toute seule");
    }

    // ---- §5, §20 : la vue générale compte par priorité et distingue risques et opportunités -------
    [Fact]
    public async Task Dashboard_counts_new_recommendations_by_priority_level()
    {
        var companyId = await MakeCompany();
        await Sql($"""
            INSERT INTO nexora.recommendation
                (id, company_id, category, rule_key, title, why, proposed_action, period_start, impact_score, urgency_score, confidence_score, nature)
            VALUES
                ('{Guid.NewGuid()}', '{companyId}', 'sales_decline', 'sales_decline', 'Critique', 'motif', 'agir', current_date, 5, 5, 3, 'risk'),
                ('{Guid.NewGuid()}', '{companyId}', 'stock_reorder', 'stock_reorder', 'Important', 'motif', 'agir', current_date, 4, 3, 2, 'risk'),
                ('{Guid.NewGuid()}', '{companyId}', 'sales_spike', 'sales_spike', 'Opportunité', 'motif', 'agir', current_date, 3, 2, 3, 'opportunity');
            """);

        await using var s = Scope();
        var recs = ForCompany<IRecommendationService>(s.ServiceProvider, await fx.NewCompanyDirectorAsync(companyId), companyId);

        var dashboard = await recs.DashboardAsync();
        dashboard.CriticalCount.Should().Be(1);
        dashboard.ImportantCount.Should().Be(1);
        dashboard.WatchCount.Should().Be(0);
        dashboard.OpportunityCount.Should().Be(1);
        dashboard.TopActions.Should().ContainSingle(r => r.Title == "Critique");
        dashboard.TopOpportunities.Should().ContainSingle(r => r.Title == "Opportunité");
    }
}
