using Asp.Versioning;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Nexora.Api.Security;
using Nexora.Application.Platform;
using Nexora.Application.Security;
using Nexora.Contracts.Platform;
using Nexora.Domain.Common;

namespace Nexora.Api.Controllers;

/// <summary>
/// Portail client — facturation, paiements, quotas &amp; usage (Phase 38 §45-46, §50). Restreint à
/// l'organisation cliente de l'appelant. Le webhook de paiement (§54) est ouvert mais signé.
/// </summary>
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/billing")]
public sealed class BillingController(
    IPlatformBillingService billing,
    IQuotaService quotas,
    IPlatformScope scope,
    IConfiguration config) : ApiControllerBase
{
    private async Task<Guid> CustomerAsync(CancellationToken ct)
        => await scope.CurrentCustomerIdAsync(ct)
           ?? throw new DomainException(Error.NotFound("platform.no_customer",
               "Aucune organisation cliente n'est rattachée à votre entreprise active."));

    [HttpGet("invoices")]
    [RequirePermission(Permissions.Platform.Portal)]
    public async Task<IActionResult> Invoices(CancellationToken ct) => Ok(await billing.ListInvoicesAsync(await CustomerAsync(ct), ct));

    [HttpGet("invoices/{id:guid}")]
    [RequirePermission(Permissions.Platform.Portal)]
    public async Task<IActionResult> Invoice(Guid id, CancellationToken ct) => FromResult(await billing.GetInvoiceAsync(id, await CustomerAsync(ct), ct));

    [HttpGet("payments")]
    [RequirePermission(Permissions.Platform.Portal)]
    public async Task<IActionResult> Payments(CancellationToken ct) => Ok(await billing.ListPaymentsAsync(await CustomerAsync(ct), ct));

    [HttpGet("payment-methods")]
    [RequirePermission(Permissions.Platform.Portal)]
    public async Task<IActionResult> PaymentMethods(CancellationToken ct) => Ok(await billing.ListPaymentMethodsAsync(await CustomerAsync(ct), ct));

    [HttpPost("payment-methods")]
    [RequirePermission(Permissions.Platform.Portal)]
    public async Task<IActionResult> AddPaymentMethod([FromBody] AddPaymentMethodRequest r, CancellationToken ct)
        => FromResult(await billing.AddPaymentMethodAsync(await CustomerAsync(ct), r, ct));

    [HttpGet("~/api/v{version:apiVersion}/usage")]
    [RequirePermission(Permissions.Platform.Portal)]
    public async Task<IActionResult> Usage(CancellationToken ct) => FromResult(await quotas.GetReportAsync(await CustomerAsync(ct), ct));

    [HttpGet("~/api/v{version:apiVersion}/quotas")]
    [RequirePermission(Permissions.Platform.Portal)]
    public async Task<IActionResult> Quotas(CancellationToken ct) => FromResult(await quotas.GetReportAsync(await CustomerAsync(ct), ct));

    // --- §54 Webhook de paiement — idempotent, jamais de suspension sur un événement ambigu ---

    [HttpPost("webhooks/payment")]
    [AllowAnonymous]
    public async Task<IActionResult> PaymentWebhook([FromBody] PaymentWebhookEvent evt, [FromHeader(Name = "X-Platform-Webhook-Token")] string? token, CancellationToken ct)
    {
        // Audit 2026-10 éd. 2, A-049 : le jeton par défaut « dev-platform-webhook-token », public, était accepté en
        // production. Sans jeton configuré, la route n'existe pas (installation d'un seul client, sans facturation
        // plateforme) ; la force du jeton est vérifiée au démarrage (ProductionConfiguration.SecretProblems).
        var expected = config["PlatformBilling:WebhookToken"];
        if (string.IsNullOrWhiteSpace(expected)) return NotFound();
        if (token is null || !System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(
                System.Text.Encoding.UTF8.GetBytes(token), System.Text.Encoding.UTF8.GetBytes(expected)))
            return Unauthorized(new { title = "webhook_unauthorized" });
        return FromResult(await billing.HandlePaymentEventAsync(evt, ct));
    }
}
