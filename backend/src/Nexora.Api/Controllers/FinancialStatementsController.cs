using Asp.Versioning;
using Microsoft.AspNetCore.Mvc;
using Nexora.Api.Security;
using Nexora.Application.Finance.Reports;
using Nexora.Application.Security;

namespace Nexora.Api.Controllers;

/// <summary>
/// États financiers canoniques (Phase 29 §38-41, §51). Dérivés des écritures validées
/// (Phase 4 <c>IFinancialReports</c>) ; le compte de résultat exclut les écritures de clôture.
/// </summary>
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/financial-statements")]
public sealed class FinancialStatementsController(IFinancialReports reports) : ApiControllerBase
{
    [HttpGet]
    [RequirePermission(Permissions.Accounting.View)]
    public async Task<IActionResult> Statements([FromQuery] DateOnly from, [FromQuery] DateOnly to, CancellationToken ct)
        => Ok(new
        {
            incomeStatement = await reports.IncomeStatementAsync(from, to, ct),
            balanceSheet = await reports.BalanceSheetAsync(to, ct),
        });

    [HttpGet("~/api/v{version:apiVersion}/trial-balance")]
    [RequirePermission(Permissions.Accounting.View)]
    public async Task<IActionResult> TrialBalance([FromQuery] DateOnly asOf, CancellationToken ct)
        => Ok(await reports.TrialBalanceAsync(asOf, ct));

    [HttpGet("~/api/v{version:apiVersion}/general-ledger")]
    [RequirePermission(Permissions.Accounting.View)]
    public async Task<IActionResult> GeneralLedger(
        [FromQuery] string account, [FromQuery] DateOnly from, [FromQuery] DateOnly to, CancellationToken ct)
        => Ok(await reports.GeneralLedgerAsync(account, from, to, ct));
}
