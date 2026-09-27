using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Nexora.Application.Abstractions;
using Nexora.Application.Integrations;
using Nexora.Contracts.Finance;
using Nexora.Contracts.Integrations;
using Nexora.Infrastructure.Persistence;

namespace Nexora.IntegrationTests;

/// <summary>Scénarios de validation Intégrations, Paiements & Écosystème (Phase 12 §43).</summary>
[Collection("finance")]
public sealed class IntegrationsEcosystemTests(FinanceEngineFixture fx)
{
    private static readonly Guid Company = Guid.Parse("dce00000-0000-0000-0000-0000000000c1");   // Pharmacie ABC
    private static readonly Guid CompanyResto = Guid.Parse("dce00000-0000-0000-0000-0000000000c2");
    private static readonly Guid MonCashConnector = Guid.Parse("dce00000-0000-0000-0000-00000000e401");
    private static readonly Guid SettlementAccount = Guid.Parse("dce00000-0000-0000-0000-00000000a053");
    private static readonly Guid Customer = Guid.Parse("dce00000-0000-0000-0000-00000000b001");
    private const string DemoSecret = "whsec_demo_pharma_moncash";

    private AsyncServiceScope Scope() => fx.Provider.CreateAsyncScope();

    private static CreatePaymentIntentRequest NewIntent(decimal amount, string method = "card", Guid? connector = null) => new(
        connector ?? MonCashConnector, Guid.NewGuid().ToString("N"), amount, "HTG", method, "link",
        Customer, SettlementAccount, null, null, 30, "REF-" + Guid.NewGuid().ToString("N")[..8]);

    private static string SignedBody(string eventId, string type, string reference, decimal? amount)
    {
        var payload = new Dictionary<string, object?>
        {
            ["id"] = eventId,
            ["type"] = type,
            ["data"] = amount is null
                ? new Dictionary<string, object?> { ["reference"] = reference }
                : new Dictionary<string, object?> { ["reference"] = reference, ["amount"] = amount },
        };
        return JsonSerializer.Serialize(payload);
    }

    private static string Hmac(string secret, string body)
        => Convert.ToHexString(System.Security.Cryptography.HMACSHA256.HashData(
            Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(body))).ToLowerInvariant();

    private async Task<string> TxnDateAsync(Guid intentId)
    {
        await using var c = new Npgsql.NpgsqlConnection(fx.TestConnectionString);
        await c.OpenAsync();
        await using var cmd = new Npgsql.NpgsqlCommand($"""
            SELECT t.occurred_at::date::text
            FROM nexora.financial_transaction t
            JOIN nexora.payment p ON p.id = t.reference_id AND t.reference_type = 'payment'
            JOIN nexora.payment_intent pi ON pi.payment_id = p.id
            WHERE pi.id = '{intentId}'
            LIMIT 1
            """, c);
        return (string)(await cmd.ExecuteScalarAsync())!;
    }

    // ---- §43 : paiement carte confirmé → paiement réel enregistré ----
    [Fact]
    public async Task Card_payment_confirms_via_provider_callback_and_records_a_real_payment()
    {
        await using var s = Scope();
        var gw = s.ServiceProvider.GetRequiredService<IPaymentGateway>();
        var db = s.ServiceProvider.GetRequiredService<NexoraDbContext>();

        var intent = (await gw.CreateIntentAsync(NewIntent(500m))).Value!;
        intent.Status.Should().Be("pending");
        intent.ProviderReference.Should().NotBeNullOrEmpty();

        var confirmed = await gw.ProviderCallbackAsync(intent.Id, new ProviderCallbackRequest("confirmed", 500m, null));
        confirmed.IsSuccess.Should().BeTrue(confirmed.Error?.Message);
        confirmed.Value!.Status.Should().Be("confirmed");
        confirmed.Value.PaymentId.Should().NotBeNull();

        var paid = await db.Database.SqlQuery<decimal>($"""
            SELECT amount AS "Value" FROM nexora.payment WHERE id = {confirmed.Value.PaymentId!.Value}
            """).FirstAsync();
        paid.Should().Be(500m);

        // Idempotent : un second rappel ne recrée pas de paiement.
        var again = await gw.ProviderCallbackAsync(intent.Id, new ProviderCallbackRequest("confirmed", 500m, null));
        again.Value!.PaymentId.Should().Be(confirmed.Value.PaymentId);
    }

    // ---- §43 : Mobile Money confirmé par webhook signé, reçu deux fois → traité une fois ----
    [Fact]
    public async Task Mobile_money_webhook_is_idempotent_when_received_twice()
    {
        await using var s = Scope();
        var gw = s.ServiceProvider.GetRequiredService<IPaymentGateway>();
        var ingest = s.ServiceProvider.GetRequiredService<IWebhookIngestService>();
        var db = s.ServiceProvider.GetRequiredService<NexoraDbContext>();

        var intent = (await gw.CreateIntentAsync(NewIntent(750m, "mobile_money"))).Value!;
        var evtId = "evt-" + Guid.NewGuid().ToString("N")[..12];
        var body = SignedBody(evtId, "payment.confirmed", intent.ProviderReference!, 750m);
        var sig = Hmac(DemoSecret, body);

        var first = await ingest.IngestAsync("moncash-sim", sig, body);
        first.Status.Should().Be("processed");
        first.SignatureValid.Should().BeTrue();

        var second = await ingest.IngestAsync("moncash-sim", sig, body);
        second.Status.Should().Be("duplicate");

        var payments = await db.Database.SqlQuery<int>($"""
            SELECT COUNT(*)::int AS "Value" FROM nexora.payment WHERE source_operation_uuid = {intent.Id}
            """).FirstAsync();
        payments.Should().Be(1);

        (await gw.GetIntentAsync(intent.Id)).Value!.Status.Should().Be("confirmed");
    }

    // ---- §43 : webhook avec signature invalide → rejeté et journalisé ----
    [Fact]
    public async Task Webhook_with_invalid_signature_is_rejected_and_logged()
    {
        await using var s = Scope();
        var gw = s.ServiceProvider.GetRequiredService<IPaymentGateway>();
        var ingest = s.ServiceProvider.GetRequiredService<IWebhookIngestService>();
        var db = s.ServiceProvider.GetRequiredService<NexoraDbContext>();

        var intent = (await gw.CreateIntentAsync(NewIntent(120m))).Value!;
        var evtId = "evt-" + Guid.NewGuid().ToString("N")[..12];
        var body = SignedBody(evtId, "payment.confirmed", intent.ProviderReference!, 120m);

        var res = await ingest.IngestAsync("moncash-sim", "deadbeef", body);
        res.Status.Should().Be("rejected");
        res.SignatureValid.Should().BeFalse();

        // L'événement non vérifié est journalisé sous une clé synthétique, sans empoisonner la déduplication.
        var evt = await db.Database.SqlQuery<string>($"""
            SELECT status AS "Value" FROM nexora.webhook_event
            WHERE provider_event_id LIKE {"unverified:" + evtId + ":%"} AND signature_valid = false
            """).FirstAsync();
        evt.Should().Be("failed");

        (await gw.GetIntentAsync(intent.Id)).Value!.Status.Should().Be("pending");
    }

    // ---- §43 : montant différent → jamais cru, aucun paiement, anomalie au rapprochement ----
    [Fact]
    public async Task Webhook_amount_mismatch_creates_no_payment_and_shows_in_reconciliation()
    {
        await using var s = Scope();
        var gw = s.ServiceProvider.GetRequiredService<IPaymentGateway>();
        var ingest = s.ServiceProvider.GetRequiredService<IWebhookIngestService>();
        var recon = s.ServiceProvider.GetRequiredService<IPaymentReconciliationService>();
        var db = s.ServiceProvider.GetRequiredService<NexoraDbContext>();

        var intent = (await gw.CreateIntentAsync(NewIntent(1000m))).Value!;
        var evtId = "evt-" + Guid.NewGuid().ToString("N")[..12];
        var body = SignedBody(evtId, "payment.confirmed", intent.ProviderReference!, 900m);
        var res = await ingest.IngestAsync("moncash-sim", Hmac(DemoSecret, body), body);
        res.Status.Should().Be("failed");

        var updated = (await gw.GetIntentAsync(intent.Id)).Value!;
        updated.Status.Should().Be("amount_mismatch");
        updated.PaymentId.Should().BeNull();

        var payments = await db.Database.SqlQuery<int>($"""
            SELECT COUNT(*)::int AS "Value" FROM nexora.payment WHERE source_operation_uuid = {intent.Id}
            """).FirstAsync();
        payments.Should().Be(0);

        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var report = await recon.ReconcileAsync(today.AddDays(-1), today.AddDays(1));
        report.Findings.Should().Contain(f => f.Kind == "amount_mismatch" && f.IntentId == intent.Id);
    }

    // ---- §43 : paiement timeout — intention expirée, confirmation tardive refusée ----
    [Fact]
    public async Task Payment_intent_expires_and_a_late_confirmation_is_refused()
    {
        await using var s = Scope();
        var gw = s.ServiceProvider.GetRequiredService<IPaymentGateway>();
        var db = s.ServiceProvider.GetRequiredService<NexoraDbContext>();

        var intent = (await gw.CreateIntentAsync(NewIntent(60m))).Value!;
        await db.Database.ExecuteSqlAsync($"""
            UPDATE nexora.payment_intent SET expires_at = now() - interval '1 minute' WHERE id = {intent.Id}
            """);

        var expired = await gw.ExpireStaleAsync();
        expired.Should().BeGreaterThanOrEqualTo(1);
        (await gw.GetIntentAsync(intent.Id)).Value!.Status.Should().Be("expired");

        var late = await gw.ProviderCallbackAsync(intent.Id, new ProviderCallbackRequest("confirmed", 60m, null));
        late.IsSuccess.Should().BeFalse();
        late.Error!.Code.Should().Be("integrations.intent_terminal");
    }

    // ---- §43 : remboursement d'un paiement confirmé ----
    [Fact]
    public async Task Refund_of_a_confirmed_payment_is_tracked()
    {
        await using var s = Scope();
        var gw = s.ServiceProvider.GetRequiredService<IPaymentGateway>();

        var intent = (await gw.CreateIntentAsync(NewIntent(400m))).Value!;
        await gw.ProviderCallbackAsync(intent.Id, new ProviderCallbackRequest("confirmed", 400m, null));

        var partial = await gw.RefundAsync(intent.Id, new RefundPaymentRequest(150m, "Retour partiel"));
        partial.Value!.Status.Should().Be("partially_refunded");
        partial.Value.RefundedAmount.Should().Be(150m);

        var full = await gw.RefundAsync(intent.Id, new RefundPaymentRequest(250m, "Solde"));
        full.Value!.Status.Should().Be("refunded");

        (await gw.RefundAsync(intent.Id, new RefundPaymentRequest(1m, "Trop"))).IsSuccess.Should().BeFalse();
    }

    // ---- §43 : import relevé avec doublon + rapprochement bancaire automatique ----
    [Fact]
    public async Task Bank_statement_import_skips_duplicates_and_auto_matches()
    {
        await using var s = Scope();
        var gw = s.ServiceProvider.GetRequiredService<IPaymentGateway>();
        var bank = s.ServiceProvider.GetRequiredService<IBankReconciliationService>();

        var intent = (await gw.CreateIntentAsync(NewIntent(321m, "mobile_money"))).Value!;
        var confirm = await gw.ProviderCallbackAsync(intent.Id, new ProviderCallbackRequest("confirmed", 321m, null));
        confirm.IsSuccess.Should().BeTrue(confirm.Error?.Message);

        var valueDate = DateOnly.Parse(await TxnDateAsync(intent.Id));
        var import = await bank.ImportStatementAsync(new ImportStatementRequest(
            SettlementAccount, "REL-" + Guid.NewGuid().ToString("N")[..6], valueDate, 0m, 321m,
            [
                new StatementLineInput(valueDate, 321m, "Encaissement MonCash", intent.ProviderReference),
                new StatementLineInput(valueDate, 321m, "Encaissement MonCash", intent.ProviderReference), // doublon
                new StatementLineInput(valueDate, 999m, "Frais inconnus", null),
            ]));

        import.IsSuccess.Should().BeTrue(import.Error?.Message);
        import.Value!.LinesImported.Should().Be(2);
        import.Value.DuplicatesSkipped.Should().Be(1);
        import.Value.AutoMatched.Should().BeGreaterThanOrEqualTo(1);

        var lines = (await bank.LinesAsync(import.Value.StatementId)).Value!;
        lines.Should().Contain(l => l.Amount == 321m && l.Status == "matched");
        lines.Should().Contain(l => l.Amount == 999m && l.Status == "unmatched");
    }

    // ---- §43 : webhook sortant échoue puis réussit (retry) ----
    [Fact]
    public async Task Outbound_webhook_retries_after_failure_then_succeeds()
    {
        await using var s = Scope();
        var outbound = s.ServiceProvider.GetRequiredService<IOutboundWebhookService>();
        var db = s.ServiceProvider.GetRequiredService<NexoraDbContext>();

        var evt = "test.retry." + Guid.NewGuid().ToString("N")[..8];
        var hookId = (await outbound.RegisterAsync(new RegisterOutboundWebhookRequest(
            "Cible instable", "https://sim.webhook.local/fail", [evt]))).Value;

        var eventId = Guid.NewGuid();
        (await outbound.EmitAsync(evt, eventId, new { hello = "world" })).Should().Be(1);

        var deliveries = await outbound.DeliveriesAsync(hookId);
        deliveries.Should().HaveCount(1);
        deliveries[0].Status.Should().Be("failed");
        deliveries[0].Attempts.Should().Be(1);
        deliveries[0].LastError.Should().NotBeNullOrEmpty();

        // La cible est corrigée, on rejoue.
        await db.Database.ExecuteSqlAsync($"""
            UPDATE nexora.outbound_webhook SET target_url = 'https://sim.webhook.local/ok' WHERE id = {hookId}
            """);
        (await outbound.RetryAsync(deliveries[0].Id)).IsSuccess.Should().BeTrue();

        (await outbound.DeliveriesAsync(hookId))[0].Status.Should().Be("delivered");
    }

    // ---- §43 : connecteur désactivé → nouveau paiement refusé ----
    [Fact]
    public async Task Disabled_connector_refuses_new_payment_intents()
    {
        await using var s = Scope();
        var registry = s.ServiceProvider.GetRequiredService<IIntegrationRegistry>();
        var gw = s.ServiceProvider.GetRequiredService<IPaymentGateway>();

        var connId = (await registry.UpsertAsync(new UpsertConnectorRequest(
            "off-" + Guid.NewGuid().ToString("N")[..6], "Connecteur test", "payment", "simpay", "test", null))).Value;
        (await registry.SetStatusAsync(connId, "active")).IsSuccess.Should().BeTrue();
        (await gw.CreateIntentAsync(NewIntent(10m, connector: connId))).IsSuccess.Should().BeTrue();

        (await registry.SetStatusAsync(connId, "inactive")).IsSuccess.Should().BeTrue();
        var refused = await gw.CreateIntentAsync(NewIntent(10m, connector: connId));
        refused.IsSuccess.Should().BeFalse();
        refused.Error!.Code.Should().Be("integrations.connector_inactive");
    }

    // ---- §43 : rotation d'un secret invalide la signature précédente ----
    [Fact]
    public async Task Rotating_a_secret_invalidates_the_previous_webhook_signature()
    {
        await using var s = Scope();
        var registry = s.ServiceProvider.GetRequiredService<IIntegrationRegistry>();
        var gw = s.ServiceProvider.GetRequiredService<IPaymentGateway>();
        var ingest = s.ServiceProvider.GetRequiredService<IWebhookIngestService>();

        var code = "rot-" + Guid.NewGuid().ToString("N")[..6];
        var connId = (await registry.UpsertAsync(new UpsertConnectorRequest(
            code, "Rotation test", "payment", "simpay", "test", null))).Value;
        await registry.SetStatusAsync(connId, "active");
        var s1 = (await registry.RotateSecretAsync(connId)).Value!.Secret;

        var intent = (await gw.CreateIntentAsync(NewIntent(200m, connector: connId))).Value!;
        var body1 = SignedBody("evt-" + Guid.NewGuid().ToString("N")[..10], "payment.confirmed", intent.ProviderReference!, 200m);
        (await ingest.IngestAsync(code, Hmac(s1, body1), body1)).Status.Should().Be("processed");

        var s2 = (await registry.RotateSecretAsync(connId)).Value!.Secret;
        s2.Should().NotBe(s1);

        var intent2 = (await gw.CreateIntentAsync(NewIntent(210m, connector: connId))).Value!;
        var body2 = SignedBody("evt-" + Guid.NewGuid().ToString("N")[..10], "payment.confirmed", intent2.ProviderReference!, 210m);

        (await ingest.IngestAsync(code, Hmac(s1, body2), body2)).Status.Should().Be("rejected");   // ancien secret
        (await ingest.IngestAsync(code, Hmac(s2, body2), body2)).Status.Should().Be("processed");  // nouveau secret
    }

    // ---- §6, §43 : entreprise A fournisseur 1, entreprise B fournisseur 2 — isolées ----
    [Fact]
    public async Task Companies_use_their_own_providers_in_isolation()
    {
        await using var s = Scope();
        var tc = (TestTenantContext)s.ServiceProvider.GetRequiredService<ITenantContext>();
        var registry = s.ServiceProvider.GetRequiredService<IIntegrationRegistry>();

        var codeB = "flashpay-" + Guid.NewGuid().ToString("N")[..6];
        tc.SetScope([CompanyResto], [], CompanyResto);
        await registry.UpsertAsync(new UpsertConnectorRequest(codeB, "FlashPay", "payment", "flashpay", "test", null));

        var listB = await registry.ListAsync();
        listB.Should().Contain(c => c.Code == codeB);
        listB.Should().NotContain(c => c.Provider == "moncash-sim");

        tc.SetScope([Company], [], Company);
        var listA = await registry.ListAsync();
        listA.Should().Contain(c => c.Provider == "moncash-sim");
        listA.Should().NotContain(c => c.Code == codeB);
    }
}
