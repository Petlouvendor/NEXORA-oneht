using Asp.Versioning;
using Microsoft.AspNetCore.Mvc;
using Nexora.Api.Security;
using Nexora.Application.Finance.Ledger;
using Nexora.Application.Security;
using Nexora.Contracts.Finance;

namespace Nexora.Api.Controllers;

/// <summary>
/// Plan comptable & journaux (Phase 29 §4-8, §51). Lecture ; la personnalisation du plan
/// comptable reste gérée via <c>accounting.manage_chart</c> (Phase 4).
/// </summary>
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/chart-of-accounts")]
public sealed class ChartOfAccountsController(IChartQueries chart, IChartManagementService manage) : ApiControllerBase
{
    /// <summary>Phase 8 : plan comptable avec solde, nombre de lignes et usages de chaque compte.</summary>
    [HttpGet("~/api/v{version:apiVersion}/accounts/manage")]
    [RequirePermission(Permissions.Accounting.View)]
    public async Task<IActionResult> Manage(CancellationToken ct) => Ok(await manage.AccountsAsync(ct));

    [HttpGet("~/api/v{version:apiVersion}/accounting-currencies")]
    [RequirePermission(Permissions.Accounting.View)]
    public async Task<IActionResult> Currencies(CancellationToken ct) => Ok(await manage.CurrenciesAsync(ct));

    [HttpPost("~/api/v{version:apiVersion}/accounts")]
    [RequirePermission(Permissions.Accounting.ManageChart)]
    public async Task<IActionResult> Create([FromBody] SaveChartAccountRequest r, CancellationToken ct)
        => FromResult(await manage.CreateAsync(r, ct));

    [HttpPut("~/api/v{version:apiVersion}/accounts/{id:guid}")]
    [RequirePermission(Permissions.Accounting.ManageChart)]
    public async Task<IActionResult> Update(Guid id, [FromBody] SaveChartAccountRequest r, CancellationToken ct)
        => FromResult(await manage.UpdateAsync(id, r, ct));

    [HttpPost("~/api/v{version:apiVersion}/accounts/{id:guid}/status")]
    [RequirePermission(Permissions.Accounting.ManageChart)]
    public async Task<IActionResult> Status(Guid id, [FromBody] ChartAccountStatusRequest r, CancellationToken ct)
        => FromResult(await manage.SetStatusAsync(id, r, ct));

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
