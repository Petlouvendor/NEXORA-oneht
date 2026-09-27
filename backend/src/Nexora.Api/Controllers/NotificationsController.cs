using Asp.Versioning;
using Microsoft.AspNetCore.Mvc;
using Nexora.Application.Notifications;
using Nexora.Contracts.Notifications;

namespace Nexora.Api.Controllers;

/// <summary>
/// Centre de notifications de l'utilisateur courant (Phase 34 §4, §45). Chaque entrée est
/// isolée par la RLS utilisateur — un utilisateur ne voit jamais les notifications d'un autre.
/// </summary>
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/notifications")]
public sealed class NotificationsController(INotificationService notifications) : ApiControllerBase
{
    [HttpGet]
    public async Task<IActionResult> List([FromQuery] NotificationQuery query, CancellationToken ct)
        => Ok(await notifications.ListAsync(query, ct));

    [HttpGet("unread-count")]
    public async Task<IActionResult> UnreadCount(CancellationToken ct)
        => Ok(new { unread = await notifications.UnreadCountAsync(ct) });

    [HttpPatch("{id:guid}/read")]
    public async Task<IActionResult> MarkRead(Guid id, CancellationToken ct)
        => FromResult(await notifications.MarkReadAsync(id, true, ct));

    [HttpPatch("{id:guid}/unread")]
    public async Task<IActionResult> MarkUnread(Guid id, CancellationToken ct)
        => FromResult(await notifications.MarkReadAsync(id, false, ct));

    [HttpPost("read-all")]
    public async Task<IActionResult> MarkAllRead(CancellationToken ct)
        => Ok(new { updated = await notifications.MarkAllReadAsync(ct) });

    [HttpPost("{id:guid}/pin")]
    public async Task<IActionResult> Pin(Guid id, CancellationToken ct)
        => FromResult(await notifications.SetPinnedAsync(id, true, ct));

    [HttpPost("{id:guid}/unpin")]
    public async Task<IActionResult> Unpin(Guid id, CancellationToken ct)
        => FromResult(await notifications.SetPinnedAsync(id, false, ct));

    [HttpPost("{id:guid}/archive")]
    public async Task<IActionResult> Archive(Guid id, CancellationToken ct)
        => FromResult(await notifications.SetArchivedAsync(id, true, ct));

    [HttpPost("{id:guid}/unarchive")]
    public async Task<IActionResult> Unarchive(Guid id, CancellationToken ct)
        => FromResult(await notifications.SetArchivedAsync(id, false, ct));
}

/// <summary>Préférences de notification — canal par catégorie, priorité minimale, heures calmes (§5).</summary>
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/notification-preferences")]
public sealed class NotificationPreferencesController(INotificationPreferenceService preferences) : ApiControllerBase
{
    [HttpGet]
    public async Task<IActionResult> List(CancellationToken ct) => Ok(await preferences.ListAsync(ct));

    [HttpPost]
    public async Task<IActionResult> Upsert([FromBody] UpsertNotificationPreferenceRequest r, CancellationToken ct)
        => FromResult(await preferences.UpsertAsync(r, ct));

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
        => FromResult(await preferences.DeleteAsync(id, ct));
}
