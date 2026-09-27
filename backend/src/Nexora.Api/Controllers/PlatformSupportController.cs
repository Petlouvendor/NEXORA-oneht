using Asp.Versioning;
using Microsoft.AspNetCore.Mvc;
using Nexora.Api.Security;
using Nexora.Application.Platform;
using Nexora.Application.Security;
using Nexora.Contracts.Platform;

namespace Nexora.Api.Controllers;

/// <summary>
/// Support plateforme (Phase 38 §37-38, §50). Les rôles support voient tous les tickets ; un
/// administrateur client ne voit et ne répond qu'aux tickets de son organisation.
/// </summary>
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/support")]
public sealed class PlatformSupportController(ISupportDeskService support, IPlatformScope scope) : ApiControllerBase
{
    [HttpGet("tickets")]
    [RequirePermission(Permissions.Platform.Portal)]
    public async Task<IActionResult> MyTickets([FromQuery] string? status, CancellationToken ct)
        => Ok(await support.ListTicketsAsync(await scope.CurrentCustomerIdAsync(ct), status, ct));

    [HttpGet("tickets/{id:guid}")]
    [RequirePermission(Permissions.Platform.Portal)]
    public async Task<IActionResult> MyTicket(Guid id, CancellationToken ct)
        => FromResult(await support.GetTicketAsync(id, await scope.CurrentCustomerIdAsync(ct), ct));

    [HttpPost("tickets")]
    [RequirePermission(Permissions.Platform.Portal)]
    public async Task<IActionResult> OpenTicket([FromBody] CreateTicketRequest r, CancellationToken ct)
    {
        var customerId = await scope.CurrentCustomerIdAsync(ct);
        return FromResult(await support.CreateTicketAsync(r with { CustomerId = customerId }, ct));
    }

    [HttpPost("tickets/{id:guid}/reply")]
    [RequirePermission(Permissions.Platform.Portal)]
    public async Task<IActionResult> Reply(Guid id, [FromBody] TicketReplyRequest r, CancellationToken ct)
        => FromResult(await support.ReplyAsync(id, r with { Internal = false }, await scope.CurrentCustomerIdAsync(ct), ct));

    // --- Rôles support --------------------------------------------------

    [HttpGet("~/api/v{version:apiVersion}/platform/support/tickets")]
    [RequirePermission(Permissions.Platform.Support)]
    public async Task<IActionResult> AllTickets([FromQuery] Guid? customerId, [FromQuery] string? status, CancellationToken ct)
        => Ok(await support.ListTicketsAsync(customerId, status, ct));

    [HttpGet("~/api/v{version:apiVersion}/platform/support/tickets/{id:guid}")]
    [RequirePermission(Permissions.Platform.Support)]
    public async Task<IActionResult> StaffTicket(Guid id, CancellationToken ct) => FromResult(await support.GetTicketAsync(id, null, ct));

    [HttpPost("~/api/v{version:apiVersion}/platform/support/tickets")]
    [RequirePermission(Permissions.Platform.Support)]
    public async Task<IActionResult> StaffCreate([FromBody] CreateTicketRequest r, CancellationToken ct) => FromResult(await support.CreateTicketAsync(r, ct));

    [HttpPost("~/api/v{version:apiVersion}/platform/support/tickets/{id:guid}/reply")]
    [RequirePermission(Permissions.Platform.Support)]
    public async Task<IActionResult> StaffReply(Guid id, [FromBody] TicketReplyRequest r, CancellationToken ct)
        => FromResult(await support.ReplyAsync(id, r, null, ct));

    [HttpPatch("~/api/v{version:apiVersion}/platform/support/tickets/{id:guid}")]
    [RequirePermission(Permissions.Platform.Support)]
    public async Task<IActionResult> StaffUpdate(Guid id, [FromBody] UpdateTicketRequest r, CancellationToken ct)
        => FromResult(await support.UpdateTicketAsync(id, r, ct));
}
