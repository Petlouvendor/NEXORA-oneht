using Asp.Versioning;
using Microsoft.AspNetCore.Mvc;
using Nexora.Api.Security;
using Nexora.Application.Platform;
using Nexora.Application.Security;
using Nexora.Contracts.Platform;

namespace Nexora.Api.Controllers;

/// <summary>
/// Console d'administration de la plateforme NEXORA (Phase 38 §34-36, §50). Réservé aux rôles
/// plateforme ; la facturation de NEXORA est distincte de la comptabilité des clients (§2, §43-44).
/// </summary>
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/platform")]
public sealed class PlatformController(
    IPlatformCatalogService catalogue,
    ICustomerAccountService customers,
    ISubscriptionService subscriptions,
    IPlatformBillingService billing,
    IQuotaService quotas,
    ISupportDeskService support,
    ISaasMetricsService metrics) : ApiControllerBase
{
    // --- §55 Métriques SaaS -------------------------------------------------

    [HttpGet("metrics")]
    [RequirePermission(Permissions.Platform.View)]
    public async Task<IActionResult> Metrics(CancellationToken ct) => Ok(await metrics.GetAsync(ct));

    // --- §4-6, §20, §39-40 Catalogue -------------------------------------

    [HttpGet("plans")]
    [RequirePermission(Permissions.Platform.View)]
    public async Task<IActionResult> Plans(CancellationToken ct) => Ok(await catalogue.ListPlansAsync(false, ct));

    [HttpPost("plans")]
    [RequirePermission(Permissions.Platform.ManageCatalogue)]
    public async Task<IActionResult> UpsertPlan([FromBody] UpsertPlanRequest r, CancellationToken ct) => FromResult(await catalogue.UpsertPlanAsync(r, ct));

    [HttpGet("addons")]
    [RequirePermission(Permissions.Platform.View)]
    public async Task<IActionResult> Addons(CancellationToken ct) => Ok(await catalogue.ListAddonsAsync(false, ct));

    [HttpPost("addons")]
    [RequirePermission(Permissions.Platform.ManageCatalogue)]
    public async Task<IActionResult> UpsertAddon([FromBody] UpsertAddonRequest r, CancellationToken ct) => FromResult(await catalogue.UpsertAddonAsync(r, ct));

    [HttpGet("coupons")]
    [RequirePermission(Permissions.Platform.ManageCatalogue)]
    public async Task<IActionResult> Coupons(CancellationToken ct) => Ok(await catalogue.ListCouponsAsync(ct));

    [HttpPost("coupons")]
    [RequirePermission(Permissions.Platform.ManageCatalogue)]
    public async Task<IActionResult> UpsertCoupon([FromBody] UpsertCouponRequest r, CancellationToken ct) => FromResult(await catalogue.UpsertCouponAsync(r, ct));

    // --- §7-9 Comptes clients ------------------------------------------

    [HttpGet("customers")]
    [RequirePermission(Permissions.Platform.View)]
    public async Task<IActionResult> Customers([FromQuery] string? status, CancellationToken ct) => Ok(await customers.ListAsync(status, ct));

    [HttpGet("customers/{id:guid}")]
    [RequirePermission(Permissions.Platform.View)]
    public async Task<IActionResult> Customer(Guid id, CancellationToken ct) => FromResult(await customers.GetAsync(id, ct));

    [HttpPost("customers")]
    [RequirePermission(Permissions.Platform.ManageCustomers)]
    public async Task<IActionResult> CreateCustomer([FromBody] CreateCustomerRequest r, CancellationToken ct) => FromResult(await customers.CreateAsync(r, ct));

    [HttpPatch("customers/{id:guid}")]
    [RequirePermission(Permissions.Platform.ManageCustomers)]
    public async Task<IActionResult> UpdateCustomer(Guid id, [FromBody] UpdateCustomerRequest r, CancellationToken ct) => FromResult(await customers.UpdateAsync(id, r, ct));

    [HttpPost("customers/{id:guid}/companies")]
    [RequirePermission(Permissions.Platform.ManageCustomers)]
    public async Task<IActionResult> LinkCompany(Guid id, [FromBody] LinkCompanyRequest r, CancellationToken ct) => FromResult(await customers.LinkCompanyAsync(id, r.CompanyId, ct));

    [HttpDelete("customers/{id:guid}/companies/{companyId:guid}")]
    [RequirePermission(Permissions.Platform.ManageCustomers)]
    public async Task<IActionResult> UnlinkCompany(Guid id, Guid companyId, CancellationToken ct) => FromResult(await customers.UnlinkCompanyAsync(id, companyId, ct));

    // --- §10-19, §29-33 Abonnements (côté plateforme) ----------------

    [HttpGet("customers/{id:guid}/subscriptions")]
    [RequirePermission(Permissions.Platform.View)]
    public async Task<IActionResult> CustomerSubscriptions(Guid id, CancellationToken ct) => Ok(await subscriptions.ListForCustomerAsync(id, ct));

    [HttpPost("customers/{id:guid}/subscription")]
    [RequirePermission(Permissions.Platform.ManageSubscriptions)]
    public async Task<IActionResult> CreateSubscription(Guid id, [FromBody] CreateSubscriptionRequest r, CancellationToken ct) => FromResult(await subscriptions.CreateAsync(id, r, ct));

    [HttpPost("subscriptions/{id:guid}/activate")]
    [RequirePermission(Permissions.Platform.ManageSubscriptions)]
    public async Task<IActionResult> Activate(Guid id, CancellationToken ct) => FromResult(await subscriptions.ActivateAsync(id, ct));

    [HttpPost("subscriptions/{id:guid}/upgrade")]
    [RequirePermission(Permissions.Platform.ManageSubscriptions)]
    public async Task<IActionResult> Upgrade(Guid id, [FromBody] ChangePlanRequest r, CancellationToken ct) => FromResult(await subscriptions.UpgradeAsync(id, r, null, ct));

    [HttpPost("subscriptions/{id:guid}/downgrade")]
    [RequirePermission(Permissions.Platform.ManageSubscriptions)]
    public async Task<IActionResult> Downgrade(Guid id, [FromBody] ChangePlanRequest r, CancellationToken ct) => FromResult(await subscriptions.DowngradeAsync(id, r, null, ct));

    [HttpPost("subscriptions/{id:guid}/addons")]
    [RequirePermission(Permissions.Platform.ManageSubscriptions)]
    public async Task<IActionResult> SetAddon(Guid id, [FromBody] AddonChangeRequest r, CancellationToken ct) => FromResult(await subscriptions.SetAddonAsync(id, r, null, ct));

    [HttpPost("subscriptions/{id:guid}/cancel")]
    [RequirePermission(Permissions.Platform.ManageSubscriptions)]
    public async Task<IActionResult> Cancel(Guid id, [FromBody] CancelSubscriptionRequest r, CancellationToken ct) => FromResult(await subscriptions.CancelAsync(id, r, null, ct));

    [HttpPost("subscriptions/{id:guid}/reactivate")]
    [RequirePermission(Permissions.Platform.ManageSubscriptions)]
    public async Task<IActionResult> Reactivate(Guid id, CancellationToken ct) => FromResult(await subscriptions.ReactivateAsync(id, null, ct));

    [HttpPost("subscriptions/{id:guid}/suspend")]
    [RequirePermission(Permissions.Platform.ManageSubscriptions)]
    public async Task<IActionResult> Suspend(Guid id, [FromBody] SuspendSubscriptionRequest r, CancellationToken ct) => FromResult(await subscriptions.SuspendAsync(id, r, ct));

    // --- §13-16, §54 Facturation & paiements (côté plateforme) -----

    [HttpGet("customers/{id:guid}/invoices")]
    [RequirePermission(Permissions.Platform.View)]
    public async Task<IActionResult> CustomerInvoices(Guid id, CancellationToken ct) => Ok(await billing.ListInvoicesAsync(id, ct));

    [HttpGet("customers/{id:guid}/payments")]
    [RequirePermission(Permissions.Platform.View)]
    public async Task<IActionResult> CustomerPayments(Guid id, CancellationToken ct) => Ok(await billing.ListPaymentsAsync(id, ct));

    [HttpGet("customers/{id:guid}/quotas")]
    [RequirePermission(Permissions.Platform.View)]
    public async Task<IActionResult> CustomerQuotas(Guid id, CancellationToken ct) => FromResult(await quotas.GetReportAsync(id, ct));

    [HttpPost("invoices/{id:guid}/credit-note")]
    [RequirePermission(Permissions.Platform.ManageBilling)]
    public async Task<IActionResult> CreditNote(Guid id, [FromBody] CreditNoteRequest r, CancellationToken ct) => FromResult(await billing.IssueCreditNoteAsync(id, r, ct));

    [HttpGet("payments/review")]
    [RequirePermission(Permissions.Platform.ManageBilling)]
    public async Task<IActionResult> PaymentsForReview(CancellationToken ct) => Ok(await billing.ListPaymentsAwaitingReviewAsync(ct));

    [HttpPost("payments")]
    [RequirePermission(Permissions.Platform.RecordPayment)]
    public async Task<IActionResult> RecordPayment([FromBody] RecordManualPaymentRequest r, CancellationToken ct) => FromResult(await billing.RecordManualPaymentAsync(r, ct));

    [HttpPost("payments/{id:guid}/confirm")]
    [RequirePermission(Permissions.Platform.RecordPayment)]
    public async Task<IActionResult> ConfirmPayment(Guid id, CancellationToken ct) => FromResult(await billing.ConfirmPaymentAsync(id, ct));

    // --- §38 Accès assisté ------------------------------------------

    [HttpGet("customers/{id:guid}/support-access")]
    [RequirePermission(Permissions.Platform.SupportAccess)]
    public async Task<IActionResult> AccessGrants(Guid id, CancellationToken ct) => Ok(await support.ListAccessGrantsAsync(id, ct));

    [HttpPost("support-access")]
    [RequirePermission(Permissions.Platform.SupportAccess)]
    public async Task<IActionResult> RequestAccess([FromBody] RequestSupportAccessRequest r, CancellationToken ct) => FromResult(await support.RequestAccessAsync(r, ct));

    [HttpPost("support-access/{id:guid}/approve")]
    [RequirePermission(Permissions.Platform.SupportAccess)]
    public async Task<IActionResult> ApproveAccess(Guid id, CancellationToken ct) => FromResult(await support.ApproveAccessAsync(id, ct));

    [HttpPost("support-access/{id:guid}/revoke")]
    [RequirePermission(Permissions.Platform.SupportAccess)]
    public async Task<IActionResult> RevokeAccess(Guid id, CancellationToken ct) => FromResult(await support.RevokeAccessAsync(id, ct));
}
