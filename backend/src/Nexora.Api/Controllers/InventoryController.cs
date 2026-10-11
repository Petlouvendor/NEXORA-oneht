using Asp.Versioning;
using Microsoft.AspNetCore.Mvc;
using Nexora.Api.Security;
using Nexora.Application.Inventory;
using Nexora.Application.Security;

namespace Nexora.Api.Controllers;

/// <summary>
/// Opérations de stock (Phase 5 §14-16 ; finalisation produit, phase 2) : transferts, inventaires,
/// pertes et ajustements — écriture et lecture. Le stock ne bouge jamais sans mouvement tracé.
/// </summary>
[ApiVersion("1.0")]
public sealed class InventoryController(
    InventoryOperationsService ops,
    IStockLedger stock,
    IReorderService reorder,
    IInventoryDocumentQueries documents) : ApiControllerBase
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

    // --- Transferts -------------------------------------------------------------
    [HttpGet("transfers")]
    [RequirePermission(Permissions.Inventory.View)]
    public async Task<IActionResult> Transfers([FromQuery] string? status, [FromQuery] int limit = 100, CancellationToken ct = default)
        => Ok(await documents.TransfersAsync(status, limit, ct));

    [HttpGet("transfers/{id:guid}")]
    [RequirePermission(Permissions.Inventory.View)]
    public async Task<IActionResult> Transfer(Guid id, CancellationToken ct)
        => await documents.TransferAsync(id, ct) is { } t ? Ok(t) : NotFound();

    [HttpPost("transfers")]
    [RequirePermission(Permissions.Inventory.Transfer)]
    public async Task<IActionResult> CreateTransfer([FromBody] CreateTransferCommand c, CancellationToken ct)
        => FromResult(await ops.CreateAndShipTransferAsync(c, ct));

    [HttpPost("transfers/{id:guid}/receive")]
    [RequirePermission(Permissions.Inventory.Transfer)]
    public async Task<IActionResult> ReceiveTransfer(Guid id, [FromBody] IReadOnlyList<ReceiveTransferLineInput> lines, CancellationToken ct)
        => FromResult(await ops.ReceiveTransferAsync(id, lines, ct));

    /// <summary>Clôture avec écart : le non-reçu est constaté en perte en transit (comptabilisée).</summary>
    [HttpPost("transfers/{id:guid}/close")]
    [RequirePermission(Permissions.Inventory.Transfer)]
    [Idempotent]
    public async Task<IActionResult> CloseTransfer(Guid id, [FromBody] CloseTransferBody body, CancellationToken ct)
        => FromResult(await ops.CloseTransferAsync(id, body.Reason, body.OperationUuid, ct));

    [HttpPost("transfers/{id:guid}/cancel")]
    [RequirePermission(Permissions.Inventory.Transfer)]
    public async Task<IActionResult> CancelTransfer(Guid id, [FromBody] ReasonBody body, CancellationToken ct)
        => FromResult(await ops.CancelTransferAsync(id, body.Reason, ct));

    // --- Inventaires --------------------------------------------------------------
    [HttpGet("counts")]
    [RequirePermission(Permissions.Inventory.View)]
    public async Task<IActionResult> Counts([FromQuery] string? status, [FromQuery] int limit = 100, CancellationToken ct = default)
        => Ok(await documents.CountsAsync(status, limit, ct));

    [HttpGet("counts/{id:guid}")]
    [RequirePermission(Permissions.Inventory.View)]
    public async Task<IActionResult> Count(Guid id, CancellationToken ct)
        => await documents.CountAsync(id, ct) is { } c ? Ok(c) : NotFound();

    /// <summary>Ouverture : liste d'articles, ou inventaire complet de l'emplacement (type full, liste vide).</summary>
    [HttpPost("counts")]
    [RequirePermission(Permissions.Inventory.Count)]
    public async Task<IActionResult> StartCount([FromBody] StartCountBody body, CancellationToken ct)
        => FromResult(await ops.StartCountAsync(body.LocationId, body.CountType, body.ProductIds ?? [], ct));

    /// <summary>Saisie des quantités comptées et justifications, sans impact sur le stock.</summary>
    [HttpPut("counts/{id:guid}/lines")]
    [RequirePermission(Permissions.Inventory.Count)]
    public async Task<IActionResult> SaveCount(Guid id, [FromBody] IReadOnlyList<CountLineInput> lines, CancellationToken ct)
        => FromResult(await ops.SaveCountAsync(id, lines, ct));

    /// <summary>Validation : un commentaire est exigé dès qu'un écart existe (justification auditée).</summary>
    [HttpPost("counts/{id:guid}/validate")]
    [RequirePermission(Permissions.Inventory.ValidateAdjustment)]
    [Idempotent]
    public async Task<IActionResult> ValidateCount(Guid id, [FromBody] ValidateCountBody body, CancellationToken ct)
        => FromResult(await ops.ValidateCountAsync(id, body.Lines ?? [], body.OperationUuid, ct, body.Note, requireJustification: true));

    [HttpPost("counts/{id:guid}/cancel")]
    [RequirePermission(Permissions.Inventory.Count)]
    public async Task<IActionResult> CancelCount(Guid id, [FromBody] ReasonBody body, CancellationToken ct)
        => FromResult(await ops.CancelCountAsync(id, body.Reason, ct));

    // --- Pertes & ajustements ---------------------------------------------------------
    [HttpGet("adjustments")]
    [RequirePermission(Permissions.Inventory.View)]
    public async Task<IActionResult> Adjustments(
        [FromQuery] string? type, [FromQuery] DateTimeOffset? from, [FromQuery] DateTimeOffset? to,
        [FromQuery] int limit = 100, CancellationToken ct = default)
        => Ok(await documents.AdjustmentsAsync(type, from, to, limit, ct));

    [HttpGet("adjustments/{id:guid}")]
    [RequirePermission(Permissions.Inventory.View)]
    public async Task<IActionResult> Adjustment(Guid id, CancellationToken ct)
        => await documents.AdjustmentAsync(id, ct) is { } a ? Ok(a) : NotFound();

    [HttpPost("losses")]
    [RequirePermission(Permissions.Inventory.WriteOff)]
    [Idempotent]
    public async Task<IActionResult> RecordLoss([FromBody] RecordLossCommand c, CancellationToken ct)
        => FromResult(await ops.RecordLossAsync(c, ct));

    /// <summary>Ajustement manuel ± (correction, stock retrouvé) avec motif obligatoire.</summary>
    [HttpPost("adjustments")]
    [RequirePermission(Permissions.Inventory.Adjust)]
    [Idempotent]
    public async Task<IActionResult> RecordAdjustment([FromBody] RecordAdjustmentCommand c, CancellationToken ct)
        => FromResult(await ops.RecordAdjustmentAsync(c, ct));

    [HttpGet("reorder-suggestions")]
    [RequirePermission(Permissions.Purchasing.View)]
    public async Task<IActionResult> ReorderSuggestions(CancellationToken ct)
        => Ok(await reorder.SuggestionsAsync(ct));

    public sealed record StartCountBody(Guid LocationId, string CountType, IReadOnlyList<Guid>? ProductIds);
    public sealed record ValidateCountBody(Guid OperationUuid, IReadOnlyList<CountLineInput>? Lines, string? Note = null);
    public sealed record CloseTransferBody(Guid OperationUuid, string Reason);
    public sealed record ReasonBody(string Reason);
}
