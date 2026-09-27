using Asp.Versioning;
using Microsoft.AspNetCore.Mvc;
using Nexora.Api.Security;
using Nexora.Application.Finance.Ledger;
using Nexora.Application.Security;

namespace Nexora.Api.Controllers;

/// <summary>
/// Plan comptable & journaux (Phase 29 §4-8, §51). Lecture ; la personnalisation du plan
/// comptable reste gérée via <c>accounting.manage_chart</c> (Phase 4).
/// </summary>
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/chart-of-accounts")]
public sealed class ChartOfAccountsController(IChartQueries chart) : ApiControllerBase
{
    [HttpGet]
    [RequirePermission(Permissions.Accounting.View)]
    public async Task<IActionResult> Charts(CancellationToken ct)
        => Ok(await chart.ChartsAsync(ct));

    [HttpGet("~/api/v{version:apiVersion}/accounts")]
    [RequirePermission(Permissions.Accounting.View)]
    public async Task<IActionResult> Accounts([FromQuery] string? accountType, [FromQuery] bool onlyPostable = false, CancellationToken ct = default)
        => Ok(await chart.AccountsAsync(accountType, onlyPostable, ct));

    [HttpGet("~/api/v{version:apiVersion}/journals")]
    [RequirePermission(Permissions.Accounting.View)]
    public async Task<IActionResult> Journals(CancellationToken ct)
        => Ok(await chart.JournalsAsync(ct));
}
