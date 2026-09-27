using Asp.Versioning;
using Microsoft.AspNetCore.Mvc;
using Nexora.Api.Security;
using Nexora.Application.Inventory;
using Nexora.Application.Security;
using Nexora.Contracts.Inventory;

namespace Nexora.Api.Controllers;

/// <summary>
/// Lectures et cycle de vie du stock (Phase 24 §13-15, §38-41).
/// Écart de nommage vs §41 : préfixe <c>/stock/</c> (cohérence avec les autres modules) —
/// <c>/stock/movements</c>, <c>/stock/lots</c>, <c>/stock/alerts</c>.
/// </summary>
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/stock")]
public sealed class StockController(IStockQueries queries, ILotService lots) : ApiControllerBase
{
    [HttpGet("balances")]
    [RequirePermission(Permissions.Inventory.View)]
    public async Task<IActionResult> Balances(
        [FromQuery] Guid? locationId, [FromQuery] Guid? productId, [FromQuery] bool onlyPositive = false, CancellationToken ct = default)
        => Ok(await queries.BalancesAsync(locationId, productId, onlyPositive, ct));

    [HttpGet("movements")]
    [RequirePermission(Permissions.Inventory.View)]
    public async Task<IActionResult> Movements(
        [FromQuery] Guid? productId, [FromQuery] Guid? locationId,
        [FromQuery] DateTimeOffset? from, [FromQuery] DateTimeOffset? to,
        [FromQuery] int limit = 100, [FromQuery] int offset = 0, CancellationToken ct = default)
        => Ok(await queries.MovementsAsync(productId, locationId, from, to, limit, offset, ct));

    [HttpGet("lots")]
    [RequirePermission(Permissions.Inventory.View)]
    public async Task<IActionResult> Lots(
        [FromQuery] Guid? productId, [FromQuery] Guid? locationId, [FromQuery] string? status,
        [FromQuery] int? expiringWithinDays, CancellationToken ct = default)
        => Ok(await queries.LotsAsync(productId, locationId, status, expiringWithinDays, ct));

    [HttpGet("lots/expiring")]
    [RequirePermission(Permissions.Inventory.View)]
    public async Task<IActionResult> Expiring([FromQuery] int? withinDays, CancellationToken ct)
        => Ok(await lots.ExpiringAsync(withinDays, ct));

    [HttpGet("alerts")]
    [RequirePermission(Permissions.Inventory.View)]
    public async Task<IActionResult> Alerts(CancellationToken ct) => Ok(await queries.AlertsAsync(ct));

    [HttpPost("lots/{id:guid}/block")]
    [RequirePermission(Permissions.Inventory.ManageLot)]
    public async Task<IActionResult> Block(Guid id, [FromBody] BlockLotRequest r, CancellationToken ct)
        => FromResult(await lots.BlockAsync(id, r.Reason, ct));

    [HttpPost("lots/{id:guid}/unblock")]
    [RequirePermission(Permissions.Inventory.ManageLot)]
    public async Task<IActionResult> Unblock(Guid id, [FromBody] BlockLotRequest r, CancellationToken ct)
        => FromResult(await lots.UnblockAsync(id, r.Reason, ct));

    [HttpPost("lots/{id:guid}/recall")]
    [RequirePermission(Permissions.Inventory.ManageLot)]
    public async Task<IActionResult> Recall(Guid id, [FromBody] RecallLotRequest r, CancellationToken ct)
        => FromResult(await lots.RecallAsync(id, r.Reference, r.Reason, ct));

    [HttpPost("expire-lots")]
    [RequirePermission(Permissions.Inventory.WriteOff)]
    [Idempotent]
    public async Task<IActionResult> ExpireLots(CancellationToken ct)
        => FromResult(await lots.SweepExpiredAsync(ct));
}
