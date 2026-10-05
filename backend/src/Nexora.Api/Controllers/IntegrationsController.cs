using System.Text;
using Asp.Versioning;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Nexora.Api.Security;
using Nexora.Application.Integrations;
using Nexora.Application.Security;
using Nexora.Contracts.Integrations;

namespace Nexora.Api.Controllers;

/// <summary>
/// Intégrations, paiements & écosystème (Phase 12). Le cœur métier reste indépendant du
/// fournisseur : tout passe par un connecteur au contrat commun. Les webhooks entrants sont
/// authentifiés (signature HMAC) et idempotents ; une erreur externe devient une anomalie
/// tracée, jamais un échec silencieux.
/// </summary>
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/integrations")]
public sealed class IntegrationsController(
    IIntegrationRegistry registry,
    IPaymentGateway payments,
    IPaymentReconciliationService reconciliation,
    IWebhookIngestService webhooks,
    IOutboundWebhookService outbound,
    IBankReconciliationService bank,
    IIntegrationCenter center) : ApiControllerBase
{
    // --- Registre des connecteurs (§5-6) ---------------------------

    [HttpGet("connectors")]
    [RequirePermission(Permissions.Integrations.View)]
    public async Task<IActionResult> Connectors(CancellationToken ct) => Ok(await registry.ListAsync(ct));

    [HttpGet("connectors/{id:guid}")]
    [RequirePermission(Permissions.Integrations.View)]
    public async Task<IActionResult> Connector(Guid id, CancellationToken ct) => FromResult(await registry.GetAsync(id, ct));

    [HttpPost("connectors")]
    [RequirePermission(Permissions.Integrations.Configure)]
    public async Task<IActionResult> UpsertConnector([FromBody] UpsertConnectorRequest r, CancellationToken ct)
        => FromResult(await registry.UpsertAsync(r, ct));

    [HttpPost("connectors/{id:guid}/status")]
    [RequirePermission(Permissions.Integrations.Configure)]
    public async Task<IActionResult> SetConnectorStatus(Guid id, [FromBody] SetStatusRequest r, CancellationToken ct)
        => FromResult(await registry.SetStatusAsync(id, r.Status, ct));

    [HttpPost("connectors/{id:guid}/test")]
    [RequirePermission(Permissions.Integrations.Configure)]
    public async Task<IActionResult> TestConnector(Guid id, CancellationToken ct)
        => FromResult(await registry.TestConnectionAsync(id, ct));

    /// <summary>Identifiants fournisseur (écriture seule, chiffrés au repos ; jamais relus).</summary>
    [HttpPut("connectors/{id:guid}/credentials")]
    [RequirePermission(Permissions.Integrations.Configure)]
    public async Task<IActionResult> SetCredentials(Guid id, [FromBody] SetConnectorCredentialsRequest r, CancellationToken ct)
        => FromResult(await registry.SetCredentialsAsync(id, r, ct));

    [HttpGet("payment-providers")]
    [RequirePermission(Permissions.Integrations.View)]
    public IActionResult PaymentProviders() => Ok(registry.Providers());

    [HttpPost("connectors/{id:guid}/rotate-secret")]
    [RequirePermission(Permissions.Integrations.Configure)]
    public async Task<IActionResult> RotateSecret(Guid id, CancellationToken ct)
        => FromResult(await registry.RotateSecretAsync(id, ct));

    // --- Alias canoniques §58 (mêmes services, jamais une seconde logique) ----

    [HttpGet]
    [RequirePermission(Permissions.Integrations.View)]
    public async Task<IActionResult> ListIntegrations(CancellationToken ct) => Ok(await registry.ListAsync(ct));

    [HttpPost]
    [RequirePermission(Permissions.Integrations.Configure)]
    public async Task<IActionResult> CreateIntegration([FromBody] UpsertConnectorRequest r, CancellationToken ct)
        => FromResult(await registry.UpsertAsync(r, ct));

    [HttpGet("{id:guid}/health")]
    [RequirePermission(Permissions.Integrations.View)]
    public async Task<IActionResult> Health(Guid id, CancellationToken ct) => FromResult(await registry.GetAsync(id, ct));

    [HttpPost("{id:guid}/test")]
    [RequirePermission(Permissions.Integrations.Configure)]
    public async Task<IActionResult> TestIntegration(Guid id, CancellationToken ct) => FromResult(await registry.TestConnectionAsync(id, ct));

    /// <summary>
    /// « Synchroniser » (§37, §58) : aucun connecteur ne synchronise encore de données. La route le dit au lieu de
    /// renvoyer un test de connexion présenté comme une synchronisation (finalisation phase 15).
    /// </summary>
    [HttpPost("{id:guid}/sync")]
    [RequirePermission(Permissions.Integrations.Configure)]
    public IActionResult SyncIntegration(Guid id)
        => FromResult(Nexora.Domain.Common.Result.Failure(Nexora.Domain.Common.Error.Validation("integrations.sync_unsupported",
            "Aucune synchronisation de données n'est disponible pour ce connecteur ; utilisez « Tester la connexion ».")));

    // --- Abstraction des paiements (§7-11) -------------------------

    [HttpGet("payments/intents")]
    [RequirePermission(Permissions.Integrations.Payments)]
    public async Task<IActionResult> Intents([FromQuery] string? status, CancellationToken ct)
        => Ok(await payments.ListIntentsAsync(status, ct));

    [HttpPost("payments/intents")]
    [RequirePermission(Permissions.Integrations.Payments)]
    public async Task<IActionResult> CreateIntent([FromBody] CreatePaymentIntentRequest r, CancellationToken ct)
        => FromResult(await payments.CreateIntentAsync(r, ct));

    [HttpGet("payments/intents/{id:guid}")]
    [RequirePermission(Permissions.Integrations.Payments)]
    public async Task<IActionResult> Intent(Guid id, CancellationToken ct) => FromResult(await payments.GetIntentAsync(id, ct));

    /// <summary>Rappel fournisseur simulé (bac à sable §42).</summary>
    [HttpPost("payments/intents/{id:guid}/callback")]
    [RequirePermission(Permissions.Integrations.Payments)]
    public async Task<IActionResult> ProviderCallback(Guid id, [FromBody] ProviderCallbackRequest r, CancellationToken ct)
        => FromResult(await payments.ProviderCallbackAsync(id, r, ct));

    /// <summary>Remboursement d'un paiement électronique : en plus de l'accès aux paiements, exige le
    /// droit de rembourser (guide §10 — jamais le caissier seul).</summary>
    /// <summary>Paiement réel : interroge le fournisseur et confirme s'il est payé au bon montant.</summary>
    [HttpPost("payments/intents/{id:guid}/verify")]
    [RequirePermission(Permissions.Integrations.Payments)]
    public async Task<IActionResult> VerifyIntent(Guid id, CancellationToken ct) => FromResult(await payments.VerifyAsync(id, ct));

    [HttpPost("payments/intents/{id:guid}/refund")]
    [RequirePermission(Permissions.Integrations.Payments)]
    [RequirePermission(Permissions.Pos.Refund)]
    public async Task<IActionResult> Refund(Guid id, [FromBody] RefundPaymentRequest r, CancellationToken ct)
        => FromResult(await payments.RefundAsync(id, r, ct));

    [HttpPost("payments/intents/expire")]
    [RequirePermission(Permissions.Integrations.Payments)]
    public async Task<IActionResult> ExpireStale(CancellationToken ct) => Ok(new { expired = await payments.ExpireStaleAsync(ct) });

    [HttpGet("payments/reconciliation")]
    [RequirePermission(Permissions.Integrations.Payments)]
    public async Task<IActionResult> ReconcilePayments([FromQuery] DateOnly from, [FromQuery] DateOnly to, CancellationToken ct)
        => Ok(await reconciliation.ReconcileAsync(from, to, ct));

    // --- Webhooks entrants (§12-13) — endpoint public signé --------

    [HttpPost("webhooks/inbound/{connectorCode}")]
    [AllowAnonymous]
    public async Task<IActionResult> InboundWebhook(string connectorCode, CancellationToken ct)
    {
        using var reader = new StreamReader(Request.Body, Encoding.UTF8);
        var body = await reader.ReadToEndAsync(ct);
        var signature = Request.Headers["X-Nexora-Signature"].FirstOrDefault()
                        ?? Request.Headers["X-Webhook-Signature"].FirstOrDefault();
        var result = await webhooks.IngestAsync(connectorCode, signature, body, ct);
        return result.Status is "rejected" or "failed" ? BadRequest(result) : Ok(result);
    }

    [HttpGet("webhooks/events")]
    [RequirePermission(Permissions.Integrations.Webhooks)]
    public async Task<IActionResult> WebhookEvents([FromQuery] string? status, CancellationToken ct)
        => Ok(await webhooks.ListAsync(status, ct));

    // --- Webhooks sortants (§32) ---------------------------------

    [HttpGet("webhooks/outbound")]
    [RequirePermission(Permissions.Integrations.Webhooks)]
    public async Task<IActionResult> OutboundWebhooks(CancellationToken ct) => Ok(await outbound.ListAsync(ct));

    [HttpPost("webhooks/outbound")]
    [RequirePermission(Permissions.Integrations.Webhooks)]
    public async Task<IActionResult> RegisterOutbound([FromBody] RegisterOutboundWebhookRequest r, CancellationToken ct)
        => FromResult(await outbound.RegisterAsync(r, ct));

    [HttpPost("webhooks/outbound/{id:guid}/status")]
    [RequirePermission(Permissions.Integrations.Webhooks)]
    public async Task<IActionResult> SetOutboundStatus(Guid id, [FromBody] SetStatusRequest r, CancellationToken ct)
        => FromResult(await outbound.SetStatusAsync(id, r.Status, ct));

    [HttpPost("webhooks/outbound/emit")]
    [RequirePermission(Permissions.Integrations.Webhooks)]
    public async Task<IActionResult> EmitEvent([FromBody] EmitEventRequest r, CancellationToken ct)
    {
        var payload = r.Payload ?? System.Text.Json.JsonSerializer.SerializeToElement(new { note = "manual" });
        var n = await outbound.EmitAsync(r.EventType, Guid.NewGuid(), payload, ct);
        return Ok(new { subscribers = n });
    }

    [HttpGet("webhooks/outbound/deliveries")]
    [RequirePermission(Permissions.Integrations.Webhooks)]
    public async Task<IActionResult> Deliveries([FromQuery] Guid? webhookId, CancellationToken ct)
        => Ok(await outbound.DeliveriesAsync(webhookId, ct));

    [HttpPost("webhooks/outbound/deliveries/{id:guid}/retry")]
    [RequirePermission(Permissions.Integrations.Webhooks)]
    public async Task<IActionResult> RetryDelivery(Guid id, CancellationToken ct) => FromResult(await outbound.RetryAsync(id, ct));

    [HttpPost("webhooks/outbound/deliver-due")]
    [RequirePermission(Permissions.Integrations.Webhooks)]
    public async Task<IActionResult> DeliverDue(CancellationToken ct) => Ok(new { attempted = await outbound.DeliverDueAsync(ct) });

    // --- Relevés bancaires (§16-17) -----------------------------

    [HttpGet("bank/statements")]
    [RequirePermission(Permissions.Integrations.Bank)]
    public async Task<IActionResult> Statements(CancellationToken ct) => Ok(await bank.ListStatementsAsync(ct));

    [HttpPost("bank/statements")]
    [RequirePermission(Permissions.Integrations.Bank)]
    public async Task<IActionResult> ImportStatement([FromBody] ImportStatementRequest r, CancellationToken ct)
        => FromResult(await bank.ImportStatementAsync(r, ct));

    [HttpGet("bank/statements/{id:guid}/lines")]
    [RequirePermission(Permissions.Integrations.Bank)]
    public async Task<IActionResult> StatementLines(Guid id, CancellationToken ct) => FromResult(await bank.LinesAsync(id, ct));

    [HttpPost("bank/statements/{id:guid}/auto-match")]
    [RequirePermission(Permissions.Integrations.Bank)]
    public async Task<IActionResult> AutoMatch(Guid id, CancellationToken ct) => FromResult(await bank.AutoMatchAsync(id, ct));

    [HttpPost("bank/statements/{id:guid}/validate")]
    [RequirePermission(Permissions.Integrations.Bank)]
    public async Task<IActionResult> ValidateStatement(Guid id, CancellationToken ct) => FromResult(await bank.ValidateAsync(id, ct));

    [HttpGet("bank/lines/{lineId:guid}/candidates")]
    [RequirePermission(Permissions.Integrations.Bank)]
    public async Task<IActionResult> LineCandidates(Guid lineId, CancellationToken ct) => FromResult(await bank.CandidatesAsync(lineId, ct));

    [HttpPost("bank/lines/{lineId:guid}/unmatch")]
    [RequirePermission(Permissions.Integrations.Bank)]
    public async Task<IActionResult> UnmatchLine(Guid lineId, CancellationToken ct) => FromResult(await bank.UnmatchLineAsync(lineId, ct));

    [HttpPost("bank/lines/{lineId:guid}/match")]
    [RequirePermission(Permissions.Integrations.Bank)]
    public async Task<IActionResult> MatchLine(Guid lineId, [FromBody] MatchLineRequest r, CancellationToken ct)
        => FromResult(await bank.MatchLineAsync(lineId, r, ct));

    // --- Centre des intégrations (§39-40) -----------------------

    [HttpGet("center")]
    [RequirePermission(Permissions.Integrations.View)]
    public async Task<IActionResult> Center(CancellationToken ct) => Ok(await center.OverviewAsync(ct));
}
