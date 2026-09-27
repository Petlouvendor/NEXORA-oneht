using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Nexora.Application.Abstractions;
using Nexora.Application.Catalog;
using Nexora.Application.Purchasing;
using Nexora.Application.Sales;
using Nexora.Application.Security;
using Nexora.Contracts.Security;
using Nexora.Infrastructure.Persistence;

namespace Nexora.IntegrationTests;

/// <summary>Scénarios de validation Sécurité, Audit & Anti-fraude (Phase 11 §38-40).</summary>
[Collection("finance")]
public sealed class SecurityAuditTests(FinanceEngineFixture fx)
{
    private static readonly Guid Company = Guid.Parse("dce00000-0000-0000-0000-0000000000c1");
    private static readonly Guid CompanyRestaurant = Guid.Parse("dce00000-0000-0000-0000-0000000000c2");
    private static readonly Guid Direction = Guid.Parse("dce00000-0000-0000-0000-0000000000d1");
    private static readonly Guid ChiefAccountant = Guid.Parse("dce00000-0000-0000-0000-0000000000d2");
    private static readonly Guid Cashier = Guid.Parse("dce00000-0000-0000-0000-0000000000d3");
    private static readonly Guid Officine = Guid.Parse("dce00000-0000-0000-0000-00000000a003");
    private static readonly Guid Supplier1 = Guid.Parse("dce00000-0000-0000-0000-00000000b101");
    private static readonly Guid BoxUnit = Guid.Parse("dce00000-0000-0000-0000-00000000a011");
    private static readonly Guid CashAccount = Guid.Parse("dce00000-0000-0000-0000-00000000a051");
    private static readonly Guid CashMethod = Guid.Parse("dce00000-0000-0000-0000-00000000a081");

    private AsyncServiceScope Scope() => fx.Provider.CreateAsyncScope();

    private async Task<Guid> NewCustomer(decimal creditLimit)
    {
        var id = Guid.NewGuid();
        await using var c = new Npgsql.NpgsqlConnection(fx.TestConnectionString);
        await c.OpenAsync();
        await using var cmd = new Npgsql.NpgsqlCommand($"""
            INSERT INTO nexora.customer (id, company_id, customer_number, name, credit_limit, default_currency_id)
            VALUES ('{id}', '{Company}', 'CLI-{id:N}', 'Client audit', {creditLimit.ToString(System.Globalization.CultureInfo.InvariantCulture)},
                    (SELECT id FROM nexora.currency WHERE code='HTG'))
            """, c);
        await cmd.ExecuteNonQueryAsync();
        return id;
    }

    private async Task<Guid> Stocked(IServiceProvider sp, string suffix, decimal price, decimal cost, decimal qty)
    {
        var catalog = sp.GetRequiredService<CatalogService>();
        var id = (await catalog.CreateProductAsync(new CreateProductCommand(
            $"SEC-{suffix}-{Guid.NewGuid():N}", $"Article {suffix}", null, BoxUnit, "stockable", null, null, null, null, false, false))).Value;
        await catalog.SetPriceAsync(new SetProductPriceCommand(id, "sale", price, "HTG", 0m, null, null));
        await sp.GetRequiredService<PurchasingService>().ReceiveAsync(new ReceiveGoodsCommand(
            Guid.NewGuid(), Supplier1, Officine, null, [new ReceiveLine(id, null, qty, cost, null, null)], null, null));
        return id;
    }

    // ---- §39 : chaque action sensible crée un événement d'audit avant/après ----
    [Fact]
    public async Task Audit_records_price_change_with_actor_company_and_before_after()
    {
        await using var s = Scope();
        var catalog = s.ServiceProvider.GetRequiredService<CatalogService>();
        var db = s.ServiceProvider.GetRequiredService<NexoraDbContext>();

        var pid = (await catalog.CreateProductAsync(new CreateProductCommand(
            $"AUD-{Guid.NewGuid():N}", "Produit audit", null, BoxUnit, "stockable", null, null, null, null, false, false))).Value;
        await catalog.SetPriceAsync(new SetProductPriceCommand(pid, "sale", 100m, "HTG", 0m, null, null));
        await catalog.SetPriceAsync(new SetProductPriceCommand(pid, "sale", 130m, "HTG", 0m, null, null));   // révision → clôture de l'ancien prix

        var audit = s.ServiceProvider.GetRequiredService<IAuditQueryService>();
        var page = await audit.QueryAsync(new AuditQueryFilter(EntityType: "product_price", PageSize: 100));

        page.Events.Should().Contain(e => e.EntityType == "product_price" && e.CompanyId == Company && e.UserId == Direction);
        page.Events.Should().Contain(e => e.Action == "INSERT");
        var update = page.Events.FirstOrDefault(e => e.Action == "UPDATE" && e.Changes != null && e.Changes.ContainsKey("valid_to"));
        update.Should().NotBeNull();   // la révision a daté la fin de validité de l'ancien prix
    }

    // ---- §12, §39 : le journal d'audit est immuable ----
    [Fact]
    public async Task Audit_log_cannot_be_modified()
    {
        await using var s = Scope();
        var db = s.ServiceProvider.GetRequiredService<NexoraDbContext>();

        var act = async () => await db.Database.ExecuteSqlAsync(
            $"UPDATE nexora.audit_log SET action = 'TAMPERED' WHERE company_id = {Company}");
        await act.Should().ThrowAsync<Exception>()
            .Where(e => e.Message.Contains("immuable") || e.Message.Contains("insufficient_privilege"));

        var del = async () => await db.Database.ExecuteSqlAsync($"DELETE FROM nexora.audit_log WHERE company_id = {Company}");
        await del.Should().ThrowAsync<Exception>();
    }

    // ---- §9, §39 : la recherche d'audit est bornée au périmètre autorisé ----
    [Fact]
    public async Task Audit_query_is_scoped_to_authorized_companies()
    {
        await using var s = Scope();
        var tc = (TestTenantContext)s.ServiceProvider.GetRequiredService<ITenantContext>();

        // Génère un événement d'audit pour c1.
        var catalog = s.ServiceProvider.GetRequiredService<CatalogService>();
        var pid = (await catalog.CreateProductAsync(new CreateProductCommand(
            $"SCP-{Guid.NewGuid():N}", "Produit scope", null, BoxUnit, "stockable", null, null, null, null, false, false))).Value;
        await catalog.SetPriceAsync(new SetProductPriceCommand(pid, "sale", 50m, "HTG", 0m, null, null));

        // Un utilisateur limité à c2 ne voit aucun événement de c1.
        tc.SetScope([CompanyRestaurant], [], CompanyRestaurant);
        var page = await s.ServiceProvider.GetRequiredService<IAuditQueryService>()
            .QueryAsync(new AuditQueryFilter(PageSize: 200));
        page.Events.Should().NotContain(e => e.CompanyId == Company);
        page.Events.Should().NotContain(e => e.CompanyId != CompanyRestaurant);
    }

    // ---- §38 : détection d'une activité anormale (ventes à crédit inhabituelles) ----
    [Fact]
    public async Task Fraud_scan_flags_unusual_credit_sales_and_review_requires_a_note()
    {
        await using var s = Scope();
        var sp = s.ServiceProvider;
        var sales = sp.GetRequiredService<SalesService>();
        var fraud = sp.GetRequiredService<IFraudDetectionService>();

        var p = await Stocked(sp, "FRD", 100m, 40m, 100m);
        var customer = await NewCustomer(1_000_000m);
        for (var i = 0; i < 3; i++)
        {
            var r = await sales.CheckoutAsync(new CheckoutCommand(
                Guid.NewGuid(), Officine, null, null, null, customer, "HTG",
                [new CheckoutLine(p, 2m, null, 0m, null)], [], 0m, new DateOnly(2026, 12, 1), null, null, null));
            r.IsSuccess.Should().BeTrue(r.Error?.Message);
        }

        var scan = await fraud.ScanAsync();
        scan.RulesEvaluated.Should().BeGreaterThan(0);

        var signals = await fraud.ListAsync("open");
        var signal = signals.FirstOrDefault(x => x.RuleCode == "pos_unusual_credit_sales");
        signal.Should().NotBeNull();
        signal!.ObservedValue.Should().BeGreaterThanOrEqualTo(3m);
        signal.RiskScore.Should().BeInRange(1, 100);
        signal.ActorUserId.Should().Be(Direction);

        // Confirmer un signal exige une note de revue (§17).
        (await fraud.ReviewAsync(signal.Id, new ReviewSignalRequest("confirmed", null))).IsSuccess.Should().BeFalse();
        (await fraud.ReviewAsync(signal.Id, new ReviewSignalRequest("confirmed", "Vérifié avec le caissier — anormal")))
            .IsSuccess.Should().BeTrue();

        var db = sp.GetRequiredService<NexoraDbContext>();
        var reviewed = await db.Database.SqlQuery<string>($"""
            SELECT status AS "Value" FROM nexora.fraud_signal WHERE id = {signal.Id}
            """).FirstAsync();
        reviewed.Should().Be("confirmed");
    }

    // ---- §40 : double approbation — demandeur ≠ approbateurs distincts ----
    [Fact]
    public async Task Refund_above_threshold_requires_two_distinct_approvers()
    {
        await using var s = Scope();
        var sp = s.ServiceProvider;
        var tc = (TestTenantContext)sp.GetRequiredService<ITenantContext>();
        var approvals = sp.GetRequiredService<IApprovalWorkflowService>();
        var db = sp.GetRequiredService<NexoraDbContext>();

        var policyId = Guid.NewGuid();
        await db.Database.ExecuteSqlAsync($"""
            INSERT INTO nexora.approval_policy (id, company_id, request_type, min_amount, required_levels, status)
            VALUES ({policyId}, {Company}, 'refund', 1000, 2, 'active')
            """);

        tc.SetUser(Direction);
        var submit = await approvals.SubmitAsync(new SubmitApprovalRequest("refund", "sale_return", Guid.NewGuid(), 5_000m, "Client mécontent"));
        submit.IsSuccess.Should().BeTrue(submit.Error?.Message);
        submit.Value!.ApprovalRequired.Should().BeTrue();
        submit.Value.RequiredLevels.Should().Be(2);
        var requestId = submit.Value.RequestId!.Value;

        // Le demandeur ne s'approuve pas (§5).
        (await approvals.DecideAsync(requestId, new ApprovalDecisionRequest(true, null))).IsSuccess.Should().BeFalse();

        // Premier approbateur.
        tc.SetUser(ChiefAccountant);
        var first = await approvals.DecideAsync(requestId, new ApprovalDecisionRequest(true, "Niveau 1"));
        first.IsSuccess.Should().BeTrue(first.Error?.Message);
        first.Value!.Status.Should().Be("pending");
        first.Value.CurrentLevel.Should().Be(2);

        // Le même approbateur ne peut pas valider le second niveau (§5).
        (await approvals.DecideAsync(requestId, new ApprovalDecisionRequest(true, "Encore moi"))).IsSuccess.Should().BeFalse();

        // Second approbateur distinct → approuvé.
        tc.SetUser(Cashier);
        var second = await approvals.DecideAsync(requestId, new ApprovalDecisionRequest(true, "Niveau 2"));
        second.Value!.Status.Should().Be("approved");
        second.Value.Actions.Count(a => a.Action == "approve").Should().Be(2);

        tc.SetUser(Direction);
        // En dessous du seuil → aucune approbation requise.
        (await approvals.SubmitAsync(new SubmitApprovalRequest("refund", null, null, 200m, null)))
            .Value!.ApprovalRequired.Should().BeFalse();
    }

    // ---- §7 : ré-authentification renforcée — jeton à usage unique ----
    [Fact]
    public async Task Step_up_challenge_issues_one_time_token()
    {
        await using var s = Scope();
        var stepUp = s.ServiceProvider.GetRequiredService<IStepUpAuthService>();

        (await stepUp.ChallengeAsync(new StepUpChallengeRequest("mauvais-mot-de-passe", "period_close"))).IsSuccess.Should().BeFalse();

        var challenge = await stepUp.ChallengeAsync(new StepUpChallengeRequest("Nexora!2026", "period_close"));
        challenge.IsSuccess.Should().BeTrue(challenge.Error?.Message);
        challenge.Value!.ExpiresAt.Should().BeAfter(DateTimeOffset.UtcNow);

        (await stepUp.VerifyAsync(new StepUpVerifyRequest(challenge.Value.Token, "period_close"))).Should().BeTrue();
        (await stepUp.VerifyAsync(new StepUpVerifyRequest(challenge.Value.Token, "period_close"))).Should().BeFalse();   // consommé
        (await stepUp.VerifyAsync(new StepUpVerifyRequest(challenge.Value.Token, "permission_change"))).Should().BeFalse();
    }

    // ---- §6, §30 : registre des sessions + déconnexion globale ----
    [Fact]
    public async Task Sessions_can_be_listed_revoked_and_globally_logged_out()
    {
        await using var s = Scope();
        var sp = s.ServiceProvider;
        var sessions = sp.GetRequiredService<ISessionSecurityService>();
        var db = sp.GetRequiredService<NexoraDbContext>();

        await sessions.RecordAsync(Direction, Company, "Poste comptoir", "10.0.0.5", "agent/1");
        await sessions.RecordAsync(Direction, Company, "Mobile", "10.0.0.6", "agent/2");

        var list = await sessions.ListAsync();
        list.Should().HaveCountGreaterThanOrEqualTo(2);

        var toRevoke = list.First(x => x.RevokedAt is null);
        (await sessions.RevokeAsync(toRevoke.Id, new RevokeSessionRequest("Appareil perdu"))).IsSuccess.Should().BeTrue();
        (await sessions.RevokeAsync(toRevoke.Id, new RevokeSessionRequest(""))).IsSuccess.Should().BeFalse();

        var epochBefore = await db.Database.SqlQuery<DateTimeOffset>($"""
            SELECT security_epoch AS "Value" FROM nexora.app_user WHERE id = {Direction}
            """).FirstAsync();
        await Task.Delay(20);
        (await sessions.RevokeAllAsync(new RevokeSessionRequest("Compte potentiellement compromis"))).IsSuccess.Should().BeTrue();

        var epochAfter = await db.Database.SqlQuery<DateTimeOffset>($"""
            SELECT security_epoch AS "Value" FROM nexora.app_user WHERE id = {Direction}
            """).FirstAsync();
        epochAfter.Should().BeAfter(epochBefore);
        (await sessions.ListAsync()).Should().OnlyContain(x => x.RevokedAt != null);
    }
}
