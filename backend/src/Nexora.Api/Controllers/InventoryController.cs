using Asp.Versioning;
using Microsoft.AspNetCore.Mvc;
using Nexora.Api.Security;
using Nexora.Application.Inventory;
using Nexora.Application.Security;

namespace Nexora.Api.Controllers;

[ApiVersion("1.0")]
public sealed class InventoryController(
    InventoryOperationsService ops,
    IStockLedger stock,
    IReorderService reorder) : ApiControllerBase
{
    [HttpGet("balance")]
    [RequirePermission(Permissions.Inventory.View)]
    public async Task<IActionResult> Balance([FromQuery] Guid productId, [FromQuery] Guid locationId, CancellationToken ct)
        => Ok(new
        {
            onHand = await stock.OnHandAsync(productId, locationId, ct),
            available = await stock.AvailableAsync(productId, locationId, ct),
            averageCost = await stock.AverageCostAsync(productId, locationId, ct),
        });

    [HttpPost("transfers")]
    [RequirePermission(Permissions.Inventory.Transfer)]
    public async Task<IActionResult> CreateTransfer([FromBody] CreateTransferCommand c, CancellationToken ct)
        => FromResult(await ops.CreateAndShipTransferAsync(c, ct));

    [HttpPost("transfers/{id:guid}/receive")]
    [RequirePermission(Permissions.Inventory.Transfer)]
    public async Task<IActionResult> ReceiveTransfer(Guid id, [FromBody] IReadOnlyList<ReceiveTransferLineInput> lines, CancellationToken ct)
        => FromResult(await ops.ReceiveTransferAsync(id, lines, ct));

    [HttpPost("counts")]
    [RequirePermission(Permissions.Inventory.Count)]
    public async Task<IActionResult> StartCount([FromBody] StartCountBody body, CancellationToken ct)
        => FromResult(await ops.StartCountAsync(body.LocationId, body.CountType, body.ProductIds, ct));

    [HttpPost("counts/{id:guid}/validate")]
    [RequirePermission(Permissions.Inventory.ValidateAdjustment)]
    [Idempotent]
    public async Task<IActionResult> ValidateCount(Guid id, [FromBody] ValidateCountBody body, CancellationToken ct)
        => FromResult(await ops.ValidateCountAsync(id, body.Lines, body.OperationUuid, ct));

    [HttpPost("losses")]
    [RequirePermission(Permissions.Inventory.WriteOff)]
    [Idempotent]
    public async Task<IActionResult> RecordLoss([FromBody] RecordLossCommand c, CancellationToken ct)
        => FromResult(await ops.RecordLossAsync(c, ct));

    [HttpGet("reorder-suggestions")]
    [RequirePermission(Permissions.Purchasing.View)]
    public async Task<IActionResult> ReorderSuggestions(CancellationToken ct)
        => Ok(await reorder.SuggestionsAsync(ct));

    public sealed record StartCountBody(Guid LocationId, string CountType, IReadOnlyList<Guid> ProductIds);
    public sealed record ValidateCountBody(Guid OperationUuid, IReadOnlyList<CountLineInput> Lines);
}
