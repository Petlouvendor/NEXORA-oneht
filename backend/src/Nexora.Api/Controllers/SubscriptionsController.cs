using Asp.Versioning;
using Microsoft.AspNetCore.Mvc;
using Nexora.Api.Security;
using Nexora.Application.Platform;
using Nexora.Application.Security;
using Nexora.Contracts.Platform;
using Nexora.Domain.Common;

namespace Nexora.Api.Controllers;

/// <summary>
/// Portail client — abonnement self-service (Phase 38 §45-46, §50). Restreint à l'organisation
/// cliente rattachée à l'entreprise active de l'appelant. Les rôles plateforme passent par
/// <c>/api/v1/platform/*</c>.
/// </summary>
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/subscriptions")]
[RequirePermission(Permissions.Platform.Portal)]
public sealed class SubscriptionsController(ISubscriptionService subscriptions, IPlatformScope scope) : ApiControllerBase
{
    private async Task<Guid> CustomerAsync(CancellationToken ct)
        => await scope.CurrentCustomerIdAsync(ct)
           ?? throw new DomainException(Error.NotFound("platform.no_customer",
               "Aucune organisation cliente n'est rattachée à votre entreprise active."));

    [HttpGet("~/api/v{version:apiVersion}/subscription")]
    public async Task<IActionResult> Current(CancellationToken ct)
    {
        var list = await subscriptions.ListForCustomerAsync(await CustomerAsync(ct), ct);
        var live = list.FirstOrDefault(s => s.Status is "trialing" or "active" or "past_due" or "grace" or "suspended")
                   ?? list.FirstOrDefault();
        return live is null ? NoContent() : Ok(live);
    }

    [HttpGet]
    public async Task<IActionResult> History(CancellationToken ct) => Ok(await subscriptions.ListForCustomerAsync(await CustomerAsync(ct), ct));

    [HttpGet("{id:guid}/preview-plan/{planId:guid}")]
    public async Task<IActionResult> Preview(Guid id, Guid planId, CancellationToken ct)
        => FromResult(await subscriptions.PreviewPlanChangeAsync(id, planId, await CustomerAsync(ct), ct));

    [HttpPost("{id:guid}/upgrade")]
    public async Task<IActionResult> Upgrade(Guid id, [FromBody] ChangePlanRequest r, CancellationToken ct)
        => FromResult(await subscriptions.UpgradeAsync(id, r, await CustomerAsync(ct), ct));

    [HttpPost("{id:guid}/downgrade")]
    public async Task<IActionResult> Downgrade(Guid id, [FromBody] ChangePlanRequest r, CancellationToken ct)
        => FromResult(await subscriptions.DowngradeAsync(id, r, await CustomerAsync(ct), ct));

    [HttpPost("{id:guid}/addons")]
    public async Task<IActionResult> SetAddon(Guid id, [FromBody] AddonChangeRequest r, CancellationToken ct)
        => FromResult(await subscriptions.SetAddonAsync(id, r, await CustomerAsync(ct), ct));

    [HttpPost("{id:guid}/cancel")]
    public async Task<IActionResult> Cancel(Guid id, [FromBody] CancelSubscriptionRequest r, CancellationToken ct)
        => FromResult(await subscriptions.CancelAsync(id, r, await CustomerAsync(ct), ct));

    [HttpPost("{id:guid}/reactivate")]
    public async Task<IActionResult> Reactivate(Guid id, CancellationToken ct)
        => FromResult(await subscriptions.ReactivateAsync(id, await CustomerAsync(ct), ct));
}
