using Asp.Versioning;
using Microsoft.AspNetCore.Mvc;
using Nexora.Api.Security;
using Nexora.Application.Inventory;
using Nexora.Application.Security;
using Nexora.Contracts.Inventory;

namespace Nexora.Api.Controllers;

/// <summary>Entrepôts & emplacements de stock (Phase 24 §4-6, §41).</summary>
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/warehouses")]
public sealed class WarehouseController(IStockQueries queries, IWarehouseService service, IInventoryDocumentQueries documents)
    : ApiControllerBase
{
    /// <summary>Établissements et magasins de rattachement (formulaires de création).</summary>
    [HttpGet("sites")]
    [RequirePermission(Permissions.Inventory.View)]
    public async Task<IActionResult> Sites(CancellationToken ct) => Ok(await documents.SitesAsync(ct));

    [HttpGet]
    [RequirePermission(Permissions.Inventory.View)]
    public async Task<IActionResult> List(CancellationToken ct) => Ok(await queries.WarehousesAsync(ct));

    [HttpPost]
    [RequirePermission(Permissions.Inventory.ManageWarehouse)]
    public async Task<IActionResult> Create([FromBody] CreateWarehouseRequest r, CancellationToken ct)
        => FromResult(await service.CreateWarehouseAsync(r, ct));

    [HttpGet("locations")]
    [RequirePermission(Permissions.Inventory.View)]
    public async Task<IActionResult> Locations([FromQuery] Guid? warehouseId, CancellationToken ct)
        => Ok(await queries.LocationsAsync(warehouseId, ct));

    [HttpPost("locations")]
    [RequirePermission(Permissions.Inventory.ManageWarehouse)]
    public async Task<IActionResult> CreateLocation([FromBody] CreateLocationRequest r, CancellationToken ct)
        => FromResult(await service.CreateLocationAsync(r, ct));
}
