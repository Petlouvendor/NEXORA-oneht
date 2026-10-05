using Asp.Versioning;
using Microsoft.AspNetCore.Mvc;
using Nexora.Api.Security;
using Nexora.Application.Notifications;
using Nexora.Application.Security;
using Nexora.Contracts.Notifications;

namespace Nexora.Api.Controllers;

/// <summary>
/// Administration des envois (finalisation phase 16) : fournisseurs e-mail / SMS / push et leur état,
/// test réel de chaque canal, message de test, journal des envois et des tentatives, relances. Un canal
/// sans fournisseur configuré n'envoie rien et l'affiche (« fournisseur non configuré »).
/// </summary>
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/notification-delivery")]
public sealed class NotificationDeliveryController(INotificationDeliveryAdmin admin) : ApiControllerBase
{
    [HttpGet("providers")]
    [RequirePermission(Permissions.Notifications.ManageDelivery)]
    public async Task<IActionResult> Providers(CancellationToken ct) => Ok(await admin.ProvidersAsync(ct));

    [HttpPost("providers/{channel}/test")]
    [RequirePermission(Permissions.Notifications.ManageDelivery)]
    public async Task<IActionResult> Test(string channel, CancellationToken ct) => FromResult(await admin.TestChannelAsync(channel, ct));

    [HttpPost("test-message")]
    [RequirePermission(Permissions.Notifications.ManageDelivery)]
    public async Task<IActionResult> SendTest([FromBody] SendTestMessageRequest r, CancellationToken ct)
        => FromResult(await admin.SendTestAsync(r, ct));

    [HttpGet("summary")]
    [RequirePermission(Permissions.Notifications.ManageDelivery)]
    public async Task<IActionResult> Summary(CancellationToken ct) => Ok(await admin.SummaryAsync(ct));

    [HttpGet("outbox")]
    [RequirePermission(Permissions.Notifications.ManageDelivery)]
    public async Task<IActionResult> Outbox([FromQuery] string? status, [FromQuery] string? channel, CancellationToken ct)
        => Ok(await admin.ListAsync(status, channel, ct));

    [HttpGet("outbox/{id:guid}/log")]
    [RequirePermission(Permissions.Notifications.ManageDelivery)]
    public async Task<IActionResult> Log(Guid id, CancellationToken ct) => FromResult(await admin.LogAsync(id, ct));

    [HttpPost("outbox/{id:guid}/retry")]
    [RequirePermission(Permissions.Notifications.ManageDelivery)]
    public async Task<IActionResult> Retry(Guid id, CancellationToken ct) => FromResult(await admin.RetryAsync(id, ct));

    [HttpPost("providers/{channel}/requeue")]
    [RequirePermission(Permissions.Notifications.ManageDelivery)]
    public async Task<IActionResult> Requeue(string channel, CancellationToken ct) => FromResult(await admin.RequeueNotConfiguredAsync(channel, ct));

    /// <summary>Traite tout de suite les envois dus de l'entreprise (utile quand le planificateur est arrêté).</summary>
    [HttpPost("dispatch")]
    [RequirePermission(Permissions.Notifications.ManageDelivery)]
    public async Task<IActionResult> Dispatch(CancellationToken ct) => Ok(new { dispatched = await admin.DispatchNowAsync(ct) });
}
