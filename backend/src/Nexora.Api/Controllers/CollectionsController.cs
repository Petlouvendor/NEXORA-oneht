using Asp.Versioning;
using Microsoft.AspNetCore.Mvc;
using Nexora.Api.Security;
using Nexora.Application.Credit;
using Nexora.Application.Security;
using Nexora.Contracts.Credit;

namespace Nexora.Api.Controllers;

/// <summary>
/// Recouvrement & crédit client (Phase 27 §21-27, §37, §44). Écart de nommage vs §44 :
/// endpoints regroupés sous <c>/credit/</c> ; <c>/receivables/{id}/write-off</c> pour la radiation.
/// </summary>
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/credit")]
public sealed class CollectionsController(CollectionsService collections, ICreditPortfolioQueries queries) : ApiControllerBase
{
    // --- Portefeuille & aging (§15, §29) --------------------------------
    [HttpGet("collections")]
    [RequirePermission(Permissions.Credit.ViewPortfolio)]
    public async Task<IActionResult> Collections([FromQuery] bool onlyOverdue = true, CancellationToken ct = default)
        => Ok(await queries.CollectionsAsync(onlyOverdue, ct));

    [HttpGet("aging")]
    [RequirePermission(Permissions.Credit.ViewPortfolio)]
    public async Task<IActionResult> Aging(CancellationToken ct) => Ok(await queries.AgingSummaryAsync(ct));

    [HttpGet("statements/{customerId:guid}")]
    [RequirePermission(Permissions.Credit.ViewPortfolio)]
    public async Task<IActionResult> Statement(Guid customerId, [FromQuery] DateOnly? from, [FromQuery] DateOnly? to, CancellationToken ct)
    {
        var s = await queries.CustomerStatementAsync(customerId, from, to, ct);
        return s is null ? NotFound() : Ok(s);
    }

    // --- Promesses de paiement (§24) --------------------------------
    [HttpGet("promises")]
    [RequirePermission(Permissions.Credit.ViewPortfolio)]
    public async Task<IActionResult> Promises([FromQuery] Guid? customerId, [FromQuery] string? status, CancellationToken ct)
        => Ok(await queries.PromisesAsync(customerId, status, ct));

    [HttpPost("promises")]
    [RequirePermission(Permissions.Credit.Collect)]
    public async Task<IActionResult> CreatePromise([FromBody] CreatePromiseRequest r, CancellationToken ct)
        => FromResult(await collections.CreatePromiseAsync(r, ct));

    [HttpPost("promises/{id:guid}/settle")]
    [RequirePermission(Permissions.Credit.Collect)]
    public async Task<IActionResult> SettlePromise(Guid id, CancellationToken ct)
        => FromResult(await collections.SettlePromiseAsync(id, ct));

    [HttpPost("promises/{id:guid}/cancel")]
    [RequirePermission(Permissions.Credit.Collect)]
    public async Task<IActionResult> CancelPromise(Guid id, [FromBody] CancelPromiseRequest r, CancellationToken ct)
        => FromResult(await collections.CancelPromiseAsync(id, r, ct));

    // --- Phase 9 : dossier client, agenda, radiations ----------------
    [HttpGet("customers/{customerId:guid}/receivables")]
    [RequirePermission(Permissions.Credit.ViewPortfolio)]
    public async Task<IActionResult> Receivables(Guid customerId, [FromQuery] bool onlyOpen = true, CancellationToken ct = default)
        => Ok(await queries.ReceivablesAsync(customerId, onlyOpen, ct));

    [HttpGet("follow-ups")]
    [RequirePermission(Permissions.Credit.ViewPortfolio)]
    public async Task<IActionResult> FollowUps(CancellationToken ct) => Ok(await queries.FollowUpsAsync(ct));

    [HttpGet("write-offs")]
    [RequirePermission(Permissions.Credit.ViewPortfolio)]
    public async Task<IActionResult> WriteOffs([FromQuery] Guid? customerId, CancellationToken ct)
        => Ok(await queries.WriteOffsAsync(customerId, ct));

    // --- Litiges (§25) --------------------------------------------
    [HttpGet("disputes")]
    [RequirePermission(Permissions.Credit.ViewPortfolio)]
    public async Task<IActionResult> Disputes([FromQuery] Guid? customerId, [FromQuery] string? status, CancellationToken ct)
        => Ok(await queries.DisputesAsync(customerId, status, ct));

    [HttpPost("disputes")]
    [RequirePermission(Permissions.Credit.Dispute)]
    public async Task<IActionResult> OpenDispute([FromBody] OpenDisputeRequest r, CancellationToken ct)
        => FromResult(await collections.OpenDisputeAsync(r, ct));

    [HttpPost("disputes/{id:guid}/resolve")]
    [RequirePermission(Permissions.Credit.Dispute)]
    public async Task<IActionResult> ResolveDispute(Guid id, [FromBody] ResolveDisputeRequest r, CancellationToken ct)
        => FromResult(await collections.ResolveDisputeAsync(id, r, ct));

    // --- Blocages (§26-27) ---------------------------------------
    [HttpGet("blocks")]
    [RequirePermission(Permissions.Credit.ViewPortfolio)]
    public async Task<IActionResult> Blocks([FromQuery] Guid? customerId, [FromQuery] bool onlyActive = true, CancellationToken ct = default)
        => Ok(await queries.BlocksAsync(customerId, onlyActive, ct));

    [HttpPost("blocks")]
    [RequirePermission(Permissions.Credit.Block)]
    public async Task<IActionResult> Block([FromBody] BlockCustomerRequest r, CancellationToken ct)
        => FromResult(await collections.BlockCustomerAsync(r, ct));

    [HttpPost("blocks/{id:guid}/release")]
    [RequirePermission(Permissions.Credit.Block)]
    public async Task<IActionResult> Release(Guid id, [FromBody] ReleaseBlockRequest r, CancellationToken ct)
        => FromResult(await collections.ReleaseBlockAsync(id, r, ct));

    // --- Radiation de créance (§37) ----------------------------
    [HttpPost("receivables/{id:guid}/write-off")]
    [RequireStepUp(StepUpPurposes.AccountingValidation)]
    [RequirePermission(Permissions.Credit.WriteOff)]
    [Idempotent]
    public async Task<IActionResult> WriteOff(Guid id, [FromBody] WriteOffReceivableRequest r, CancellationToken ct)
        => FromResult(await collections.WriteOffReceivableAsync(id, r, ct));
}
