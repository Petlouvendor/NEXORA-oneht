using Asp.Versioning;
using Microsoft.AspNetCore.Mvc;
using Nexora.Api.Security;
using Nexora.Application.Sales;
using Nexora.Application.Security;
using Nexora.Contracts.Sales;

namespace Nexora.Api.Controllers;

/// <summary>Commandes clients (Phase 25 §18-20, §43) : création → confirmation → réservation → acompte → exécution.</summary>
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/orders")]
public sealed class OrdersController(OrderService orders, ISalesQueries queries) : ApiControllerBase
{
    [HttpGet]
    [RequirePermission(Permissions.Pos.ManageOrder)]
    public async Task<IActionResult> List([FromQuery] string? status, [FromQuery] int limit = 50, CancellationToken ct = default)
        => Ok(await queries.ListOrdersAsync(status, limit, ct));

    [HttpPost]
    [RequirePermission(Permissions.Pos.ManageOrder)]
    public async Task<IActionResult> Create([FromBody] CreateOrderRequest r, CancellationToken ct)
        => FromResult(await orders.CreateOrderAsync(r, ct));

    [HttpGet("{id:guid}")]
    [RequirePermission(Permissions.Pos.ManageOrder)]
    public async Task<IActionResult> Get(Guid id, CancellationToken ct)
    {
        var o = await queries.GetOrderAsync(id, ct);
        return o is null ? NotFound() : Ok(o);
    }

    [HttpPost("{id:guid}/confirm")]
    [RequirePermission(Permissions.Pos.ManageOrder)]
    public async Task<IActionResult> Confirm(Guid id, CancellationToken ct)
        => FromResult(await orders.ConfirmOrderAsync(id, ct));

    [HttpPost("{id:guid}/reserve")]
    [RequirePermission(Permissions.Pos.ManageOrder)]
    public async Task<IActionResult> Reserve(Guid id, CancellationToken ct)
        => FromResult(await orders.ReserveOrderAsync(id, ct));

    [HttpPost("{id:guid}/deposit")]
    [RequirePermission(Permissions.Pos.ManageOrder)]
    [Idempotent]
    public async Task<IActionResult> Deposit(Guid id, [FromBody] OrderDepositRequest r, CancellationToken ct)
        => FromResult(await orders.RecordDepositAsync(id, r, ct));

    [HttpPost("{id:guid}/complete")]
    [RequirePermission(Permissions.Pos.ManageOrder)]
    [Idempotent]
    public async Task<IActionResult> Complete(Guid id, [FromBody] CompleteOrderRequest r, CancellationToken ct)
        => FromResult(await orders.CompleteOrderAsync(id, r, ct));

    [HttpPost("{id:guid}/cancel")]
    [RequirePermission(Permissions.Pos.ManageOrder)]
    public async Task<IActionResult> Cancel(Guid id, [FromBody] ReasonBody body, CancellationToken ct)
        => FromResult(await orders.CancelOrderAsync(id, body.Reason, ct));

    public sealed record ReasonBody(string Reason);
}
