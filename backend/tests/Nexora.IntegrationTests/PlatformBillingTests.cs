using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Nexora.Application.Abstractions;
using Nexora.Application.Platform;
using Nexora.Contracts.Platform;
using Npgsql;

namespace Nexora.IntegrationTests;

/// <summary>
/// Administration SaaS &amp; monétisation (Phase 38 §52) : essai, activation, prorata, downgrade
/// avec quotas dépassés, webhook idempotent, événement ambigu sans suspension, grâce → suspension,
/// quotas, licence offline.
/// </summary>
[Collection("finance")]
public sealed class PlatformBillingTests(FinanceEngineFixture fx)
{
    private static readonly Guid Actor = Guid.Parse("dce00000-0000-0000-0000-0000000000d1");

    private AsyncServiceScope Scope() => fx.Provider.CreateAsyncScope();

    private static (IPlatformCatalogService Cat, ICustomerAccountService Cust, ISubscriptionService Sub,
        IPlatformBillingService Bill, IQuotaService Quota, ILicenseService Lic) Svcs(IServiceProvider sp)
    {
        var tc = (TestTenantContext)sp.GetRequiredService<ITenantContext>();
        tc.SetUser(Actor);
        return (sp.GetRequiredService<IPlatformCatalogService>(), sp.GetRequiredService<ICustomerAccountService>(),
            sp.GetRequiredService<ISubscriptionService>(), sp.GetRequiredService<IPlatformBillingService>(),
            sp.GetRequiredService<IQuotaService>(), sp.GetRequiredService<ILicenseService>());
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

    private static async Task<Guid> PlanAsync(IPlatformCatalogService cat, string code, string tier, decimal price, int trialDays,
        Dictionary<string, long>? quotas = null, Dictionary<string, string>? features = null)
    {
        var res = await cat.UpsertPlanAsync(new UpsertPlanRequest(code, code, tier, null, price, "USD", "monthly", trialDays,
            quotas, features));
        res.IsSuccess.Should().BeTrue(res.Error?.Message);
        return res.Value!.Id;
    }

    private async Task<Guid> CustomerAsync(ICustomerAccountService cust, string tag)
    {
        var res = await cust.CreateAsync(new CreateCustomerRequest($"IT {tag} {Guid.NewGuid():N}", "IT", "it@test.demo"));
        res.IsSuccess.Should().BeTrue(res.Error?.Message);
        return res.Value!.Id;
    }

    // ---- §12 : la souscription avec essai démarre un essai ----
    [Fact]
    public async Task Create_subscription_with_trial_starts_a_trial()
    {
        await using var s = Scope();
        var (cat, cust, sub, _, _, _) = Svcs(s.ServiceProvider);
        var plan = await PlanAsync(cat, $"it-trial-{Guid.NewGuid():N}"[..20], "starter", 19m, 14);
        var customer = await CustomerAsync(cust, "trial");

        var res = await sub.CreateAsync(customer, new CreateSubscriptionRequest(plan, StartTrial: true));

        res.IsSuccess.Should().BeTrue(res.Error?.Message);
        res.Value!.Status.Should().Be("trialing");
        res.Value.TrialEndsAt.Should().NotBeNull();
        (await Scalar<long>($"SELECT COUNT(*) FROM nexora.billing_event WHERE subscription_id = '{res.Value.Id}' AND event_type = 'TrialStarted'"))
            .Should().Be(1);
    }

    // ---- §19, §29 : un abonnement payant s'active au paiement (facture + licence) ----
    [Fact]
    public async Task Paid_subscription_activates_on_payment_with_invoice_and_license()
    {
        await using var s = Scope();
        var (cat, cust, sub, bill, _, _) = Svcs(s.ServiceProvider);
        var plan = await PlanAsync(cat, $"it-paid-{Guid.NewGuid():N}"[..19], "business", 49m, 0);
        var customer = await CustomerAsync(cust, "paid");

        var created = await sub.CreateAsync(customer, new CreateSubscriptionRequest(plan, StartTrial: false));
        created.Value!.Status.Should().Be("past_due");
        (await Scalar<long>($"SELECT COUNT(*) FROM nexora.platform_invoice WHERE customer_id = '{customer}' AND status = 'issued'")).Should().Be(1);

        var evt = new PaymentWebhookEvent($"evt-{Guid.NewGuid():N}", "charge.succeeded", "sim", null, null, customer, null, 49m, "USD", null, "succeeded");
        var pay = await bill.HandlePaymentEventAsync(evt);
        pay.IsSuccess.Should().BeTrue(pay.Error?.Message);

        (await sub.GetAsync(created.Value.Id)).Value!.Status.Should().Be("active");
        (await Scalar<long>($"SELECT COUNT(*) FROM nexora.platform_invoice WHERE customer_id = '{customer}' AND status = 'paid'")).Should().Be(1);
        (await Scalar<long>($"SELECT COUNT(*) FROM nexora.platform_license WHERE customer_id = '{customer}' AND status = 'active'")).Should().Be(1);
    }

    // ---- Audit 2026-10 éd. 2, A-049 : une facture n'est soldée que par un paiement de son client, à hauteur de son total ----
    [Fact]
    public async Task A_payment_settles_only_its_own_customers_invoice_and_only_up_to_its_total()
    {
        await using var s = Scope();
        var (cat, cust, sub, bill, _, _) = Svcs(s.ServiceProvider);
        var plan = await PlanAsync(cat, $"it-a049-{Guid.NewGuid():N}"[..19], "business", 49m, 0);
        var owner = await CustomerAsync(cust, "a049-owner");
        var stranger = await CustomerAsync(cust, "a049-other");
        var created = await sub.CreateAsync(owner, new CreateSubscriptionRequest(plan, StartTrial: false));
        var invoice = await Scalar<Guid>($"SELECT id FROM nexora.platform_invoice WHERE customer_id = '{owner}' AND status = 'issued'");

        // Paiement d'un autre client visant cette facture : avant, la facture passait payée en totalité.
        var foreign = await bill.HandlePaymentEventAsync(new PaymentWebhookEvent($"evt-{Guid.NewGuid():N}", "charge.succeeded", "sim",
            null, null, stranger, invoice, 1m, "USD", null, "succeeded"));
        foreign.IsSuccess.Should().BeTrue(foreign.Error?.Message);
        foreign.Value!.ReviewRequired.Should().BeTrue("paiement non imputable : revue humaine");
        (await Scalar<string>($"SELECT status FROM nexora.platform_invoice WHERE id = '{invoice}'")).Should().Be("issued");

        // Paiement partiel du bon client : imputé, la facture reste ouverte et l'abonnement ne s'active pas.
        (await bill.HandlePaymentEventAsync(new PaymentWebhookEvent($"evt-{Guid.NewGuid():N}", "charge.succeeded", "sim",
            null, null, owner, invoice, 20m, "USD", null, "succeeded"))).IsSuccess.Should().BeTrue();
        (await Scalar<string>($"SELECT status FROM nexora.platform_invoice WHERE id = '{invoice}'")).Should().Be("issued");
        (await Scalar<decimal>($"SELECT paid_amount FROM nexora.platform_invoice WHERE id = '{invoice}'")).Should().Be(20m);
        (await sub.GetAsync(created.Value!.Id)).Value!.Status.Should().Be("past_due");

        // Le solde : facture payée, abonnement actif.
        (await bill.HandlePaymentEventAsync(new PaymentWebhookEvent($"evt-{Guid.NewGuid():N}", "charge.succeeded", "sim",
            null, null, owner, invoice, 29m, "USD", null, "succeeded"))).IsSuccess.Should().BeTrue();
        (await Scalar<string>($"SELECT status FROM nexora.platform_invoice WHERE id = '{invoice}'")).Should().Be("paid");
        (await sub.GetAsync(created.Value.Id)).Value!.Status.Should().Be("active");
    }

    // ---- §14 : l'upgrade calcule le prorata et bascule le plan ----
    [Fact]
    public async Task Upgrade_prorates_the_difference_and_switches_plan()
    {
        await using var s = Scope();
        var (cat, cust, sub, _, _, _) = Svcs(s.ServiceProvider);
        var small = await PlanAsync(cat, $"it-up-s-{Guid.NewGuid():N}"[..18], "starter", 20m, 0);
        var big = await PlanAsync(cat, $"it-up-b-{Guid.NewGuid():N}"[..18], "professional", 120m, 0);
        var customer = await CustomerAsync(cust, "up");
        var created = await sub.CreateAsync(customer, new CreateSubscriptionRequest(small, StartTrial: false));
        await sub.ActivateAsync(created.Value!.Id);

        var up = await sub.UpgradeAsync(created.Value.Id, new ChangePlanRequest(big), null);

        up.IsSuccess.Should().BeTrue(up.Error?.Message);
        up.Value!.PlanId.Should().Be(big);
        up.Value.PriceAmount.Should().Be(120m);
        (await Scalar<long>($"SELECT COUNT(*) FROM nexora.platform_invoice_line l JOIN nexora.platform_invoice i ON i.id = l.invoice_id WHERE i.customer_id = '{customer}' AND l.line_type = 'proration' AND l.amount > 0"))
            .Should().BeGreaterThan(0);
        (await Scalar<long>($"SELECT COUNT(*) FROM nexora.billing_event WHERE subscription_id = '{created.Value.Id}' AND event_type = 'SubscriptionUpgraded'")).Should().Be(1);
    }

    // ---- §18 : le downgrade conserve les données et signale les quotas dépassés ----
    [Fact]
    public async Task Downgrade_keeps_data_and_reports_quota_breaches()
    {
        await using var s = Scope();
        var (cat, cust, sub, _, quota, _) = Svcs(s.ServiceProvider);
        var big = await PlanAsync(cat, $"it-dn-b-{Guid.NewGuid():N}"[..18], "professional", 120m, 0,
            quotas: new() { ["companies"] = 10, ["users"] = 50 });
        var small = await PlanAsync(cat, $"it-dn-s-{Guid.NewGuid():N}"[..18], "starter", 20m, 0,
            quotas: new() { ["companies"] = 1, ["users"] = 3 });
        var customer = await CustomerAsync(cust, "dn");
        var created = await sub.CreateAsync(customer, new CreateSubscriptionRequest(big, StartTrial: false));
        await sub.ActivateAsync(created.Value!.Id);
        await quota.RecordUsageAsync(customer, "companies", 4);

        var impact = await sub.PreviewPlanChangeAsync(created.Value.Id, small, null);
        impact.IsSuccess.Should().BeTrue(impact.Error?.Message);
        impact.Value!.Direction.Should().Be("downgrade");
        impact.Value.QuotaBreaches.Should().Contain(b => b.Resource == "companies" && b.CurrentUsage == 4 && b.Limit == 1);

        var dn = await sub.DowngradeAsync(created.Value.Id, new ChangePlanRequest(small), null);
        dn.IsSuccess.Should().BeTrue(dn.Error?.Message);
        dn.Value!.PriceAmount.Should().Be(20m);
        // §18 : aucune donnée supprimée — le compteur d'usage subsiste.
        (await Scalar<long>($"SELECT value FROM nexora.usage_counter WHERE customer_id = '{customer}' AND resource = 'companies'")).Should().Be(4);
        (await Scalar<long>($"SELECT COUNT(*) FROM nexora.platform_invoice WHERE customer_id = '{customer}' AND kind = 'credit_note'")).Should().BeGreaterThan(0);
    }

    // ---- §54 : le webhook de paiement est idempotent ----
    [Fact]
    public async Task Payment_webhook_is_idempotent()
    {
        await using var s = Scope();
        var (cat, cust, sub, bill, _, _) = Svcs(s.ServiceProvider);
        var plan = await PlanAsync(cat, $"it-idem-{Guid.NewGuid():N}"[..18], "business", 49m, 0);
        var customer = await CustomerAsync(cust, "idem");
        await sub.CreateAsync(customer, new CreateSubscriptionRequest(plan, StartTrial: false));

        var evt = new PaymentWebhookEvent("evt-idem-fixed", "charge.succeeded", "sim", null, null, customer, null, 49m, "USD", null, "succeeded");
        var first = await bill.HandlePaymentEventAsync(evt);
        var second = await bill.HandlePaymentEventAsync(evt);

        first.Value!.Id.Should().Be(second.Value!.Id);
        (await Scalar<long>($"SELECT COUNT(*) FROM nexora.platform_payment WHERE provider_event_id = 'evt-idem-fixed'")).Should().Be(1);
    }

    // ---- §54 : un événement ambigu passe en revue, ne suspend jamais ----
    [Fact]
    public async Task Ambiguous_event_goes_to_review_and_never_suspends()
    {
        await using var s = Scope();
        var (cat, cust, sub, bill, _, _) = Svcs(s.ServiceProvider);
        var plan = await PlanAsync(cat, $"it-amb-{Guid.NewGuid():N}"[..18], "business", 49m, 0);
        var customer = await CustomerAsync(cust, "amb");
        var created = await sub.CreateAsync(customer, new CreateSubscriptionRequest(plan, StartTrial: false));
        await sub.ActivateAsync(created.Value!.Id);

        var evt = new PaymentWebhookEvent($"evt-amb-{Guid.NewGuid():N}", "charge.disputed", "sim", null, null, customer, null, 49m, "USD", null, "ambiguous");
        var res = await bill.HandlePaymentEventAsync(evt);

        res.Value!.Status.Should().Be("pending");
        res.Value.ReviewRequired.Should().BeTrue();
        (await sub.GetAsync(created.Value.Id)).Value!.Status.Should().Be("active");
        (await bill.ListPaymentsAwaitingReviewAsync()).Should().Contain(p => p.Id == res.Value.Id);
    }

    // ---- §16-17 : échec de paiement → grâce, puis l'expiration de la grâce suspend ----
    [Fact]
    public async Task Failed_payment_moves_to_grace_then_grace_expiry_suspends()
    {
        await using var s = Scope();
        var (cat, cust, sub, bill, _, _) = Svcs(s.ServiceProvider);
        var plan = await PlanAsync(cat, $"it-grace-{Guid.NewGuid():N}"[..18], "business", 49m, 0);
        var customer = await CustomerAsync(cust, "grace");
        var created = await sub.CreateAsync(customer, new CreateSubscriptionRequest(plan, StartTrial: false));
        await sub.ActivateAsync(created.Value!.Id);

        var evt = new PaymentWebhookEvent($"evt-f-{Guid.NewGuid():N}", "charge.failed", "sim", null, null, customer, null, 49m, "USD", null, "failed");
        await bill.HandlePaymentEventAsync(evt);
        (await sub.GetAsync(created.Value.Id)).Value!.Status.Should().Be("past_due");

        await Sql($"UPDATE nexora.subscription SET grace_ends_at = now() - interval '1 hour' WHERE id = '{created.Value.Id}'");
        await sub.RunDueAsync();

        (await sub.GetAsync(created.Value.Id)).Value!.Status.Should().Be("suspended");
    }

    // ---- §21 : la vérification de quota bloque au-delà de la limite ----
    [Fact]
    public async Task Quota_check_blocks_when_over_limit()
    {
        await using var s = Scope();
        var (cat, cust, sub, _, quota, _) = Svcs(s.ServiceProvider);
        var plan = await PlanAsync(cat, $"it-q-{Guid.NewGuid():N}"[..18], "starter", 19m, 0, quotas: new() { ["users"] = 3 });
        var customer = await CustomerAsync(cust, "q");
        var created = await sub.CreateAsync(customer, new CreateSubscriptionRequest(plan, StartTrial: false));
        await sub.ActivateAsync(created.Value!.Id);

        await quota.RecordUsageAsync(customer, "users", 3);
        var check = await quota.CheckAsync(customer, "users", 1);

        check.Allowed.Should().BeFalse();
        check.Limit.Should().Be(3);
        check.Used.Should().Be(3);
        (await quota.GetReportAsync(customer)).Value!.Lines.Should().Contain(l => l.Resource == "users" && l.Used == 3 && l.Limit == 3 && l.Percent == 100m);
    }

    // ---- §28 : jeton de licence offline — vérification locale, détection de révocation ----
    [Fact]
    public async Task License_offline_token_verifies_and_detects_revocation()
    {
        await using var s = Scope();
        var (cat, cust, sub, _, _, lic) = Svcs(s.ServiceProvider);
        var plan = await PlanAsync(cat, $"it-lic-{Guid.NewGuid():N}"[..18], "business", 49m, 0);
        var customer = await CustomerAsync(cust, "lic");
        await sub.CreateAsync(customer, new CreateSubscriptionRequest(plan, StartTrial: false));

        var issued = await lic.IssueAsync(new IssueLicenseRequest(customer, "organization", null, 7, 35));
        issued.IsSuccess.Should().BeTrue(issued.Error?.Message);
        var tok = await lic.IssueOfflineTokenAsync(issued.Value!.Id, null);
        tok.IsSuccess.Should().BeTrue(tok.Error?.Message);

        var ok = await lic.VerifyOfflineTokenAsync(tok.Value!.Token);
        ok.Valid.Should().BeTrue(ok.Reason);
        ok.CustomerId.Should().Be(customer);

        (await lic.VerifyOfflineTokenAsync(tok.Value.Token + "x")).Valid.Should().BeFalse();

        await lic.RevokeAsync(issued.Value.Id, new RevokeLicenseRequest("test"));
        (await lic.VerifyOfflineTokenAsync(tok.Value.Token)).Valid.Should().BeFalse("la licence est révoquée");
    }

    // ---- §31, §33 : annulation en fin de période puis réactivation ----
    [Fact]
    public async Task Cancel_at_period_end_then_reactivate()
    {
        await using var s = Scope();
        var (cat, cust, sub, _, _, _) = Svcs(s.ServiceProvider);
        var plan = await PlanAsync(cat, $"it-cx-{Guid.NewGuid():N}"[..18], "business", 49m, 0);
        var customer = await CustomerAsync(cust, "cx");
        var created = await sub.CreateAsync(customer, new CreateSubscriptionRequest(plan, StartTrial: false));
        await sub.ActivateAsync(created.Value!.Id);

        var canceled = await sub.CancelAsync(created.Value.Id, new CancelSubscriptionRequest(Immediate: false, "trop cher"), null);
        canceled.Value!.CancelAtPeriodEnd.Should().BeTrue();
        canceled.Value.Status.Should().Be("active");

        var reactivated = await sub.ReactivateAsync(created.Value.Id, null);
        reactivated.Value!.CancelAtPeriodEnd.Should().BeFalse();
        reactivated.Value.Status.Should().Be("active");
    }
}
