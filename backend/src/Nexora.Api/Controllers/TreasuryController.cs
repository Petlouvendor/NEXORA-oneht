using Asp.Versioning;
using Microsoft.AspNetCore.Mvc;
using Nexora.Api.Security;
using Nexora.Application.Finance.Treasury;
using Nexora.Application.Security;
using Nexora.Contracts.Finance;

namespace Nexora.Api.Controllers;

/// <summary>
/// Finance & trésorerie — cockpit de pilotage (Phase 28 §27-28, §31-32, §50).
/// Vue opérationnelle : la comptabilité reste le système de référence pour les états financiers.
/// </summary>
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/treasury")]
public sealed class TreasuryController(ITreasuryQueries queries, ISuspenseService suspense) : ApiControllerBase
{
    // --- Position & liquidité (§27-28) ---------------------------------

    [HttpGet("position")]
    [RequirePermission(Permissions.Finance.View)]
    public async Task<IActionResult> Position(CancellationToken ct)
        => Ok(await queries.PositionAsync(ct));

    [HttpGet("liquidity")]
    [RequirePermission(Permissions.Finance.View)]
    public async Task<IActionResult> Liquidity([FromQuery] int? horizonDays, CancellationToken ct)
        => Ok(await queries.LiquidityAsync(horizonDays, ct));

    // --- Comptes de suspense (§31-32) ---------------------------------

    [HttpGet("suspense")]
    [RequirePermission(Permissions.Finance.Reconcile)]
    public async Task<IActionResult> Suspense([FromQuery] string? status, [FromQuery] bool onlyOverdue = false, CancellationToken ct = default)
        => Ok(await suspense.ListAsync(status, onlyOverdue, ct));

    [HttpPost("suspense")]
    [RequirePermission(Permissions.Finance.ManageSuspense)]
    public async Task<IActionResult> OpenSuspense([FromBody] OpenSuspenseRequest r, CancellationToken ct)
        => FromResult(await suspense.OpenAsync(r, ct));

    [HttpPost("suspense/{id:guid}/resolve")]
    [RequirePermission(Permissions.Finance.ManageSuspense)]
    public async Task<IActionResult> ResolveSuspense(Guid id, [FromBody] ResolveSuspenseRequest r, CancellationToken ct)
        => FromResult(await suspense.ResolveAsync(id, r, ct));
}
