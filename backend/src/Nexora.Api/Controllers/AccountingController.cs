using Asp.Versioning;
using Microsoft.AspNetCore.Mvc;
using Nexora.Api.Security;
using Nexora.Application.Finance.Abstractions;
using Nexora.Application.Finance.Periods;
using Nexora.Application.Finance.Reports;
using Nexora.Application.Finance.Reversals;
using Nexora.Application.Security;
using Nexora.Domain.Common;
using Nexora.Contracts.Finance;

namespace Nexora.Api.Controllers;

[ApiVersion("1.0")]
public sealed class AccountingController(
    IAccountingPeriods periods,
    IAccountingPeriodService periodService,
    IReversalService reversals,
    IFinancialReports reports) : ApiControllerBase
{
    // --- Périodes (§27) ---------------------------------------------------

    [HttpGet("periods")]
    [RequirePermission(Permissions.Accounting.View)]
    public async Task<IActionResult> Periods(CancellationToken ct)
        => Ok(await periods.ListAsync(ct));

    [HttpPost("periods/{id:guid}/close")]
    [RequireStepUp(StepUpPurposes.AccountingValidation)]
    [RequirePermission(Permissions.Accounting.ClosePeriod)]
    public async Task<IActionResult> Close(Guid id, CancellationToken ct)
        => FromResult(await periodService.CloseAsync(id, ct));

    [HttpPost("periods/{id:guid}/reopen")]
    [RequireStepUp(StepUpPurposes.AccountingValidation)]
    [RequirePermission(Permissions.Accounting.ReopenPeriod)]
    public async Task<IActionResult> Reopen(Guid id, [FromBody] ReverseEntryRequest body, CancellationToken ct)
        => FromResult(await periodService.ReopenAsync(id, body.Reason, ct));

    // --- Extourne (§28) -------------------------------------------------

    [HttpPost("journal-entries/{id:guid}/reverse")]
    [RequireStepUp(StepUpPurposes.AccountingValidation)]
    [RequirePermission(Permissions.Accounting.Reverse)]
    public async Task<IActionResult> Reverse(Guid id, [FromBody] ReverseEntryRequest body, CancellationToken ct)
        => FromResult(await reversals.ReverseUserEntryAsync(id, body.Reason, body.ReversalDate, ct));

    // --- Rapports (§34) -----------------------------------------------

    [HttpGet("reports/trial-balance")]
    [RequirePermission(Permissions.Accounting.View)]
    public async Task<IActionResult> TrialBalance([FromQuery] DateOnly asOf, CancellationToken ct)
        => Ok(await reports.TrialBalanceAsync(asOf, ct));

    [HttpGet("reports/general-ledger")]
    [RequirePermission(Permissions.Accounting.View)]
    public async Task<IActionResult> GeneralLedger(
        [FromQuery] string account, [FromQuery] DateOnly from, [FromQuery] DateOnly to, CancellationToken ct)
    {
        // Compte inconnu : 404 explicite (une exception non gérée renvoyait une erreur 500).
        try { return Ok(await reports.GeneralLedgerAsync(account, from, to, ct)); }
        catch (InvalidOperationException ex) { throw new DomainException(Error.NotFound("accounting.account_not_found", ex.Message)); }
    }

    [HttpGet("reports/income-statement")]
    [RequirePermission(Permissions.Accounting.View)]
    public async Task<IActionResult> IncomeStatement([FromQuery] DateOnly from, [FromQuery] DateOnly to, CancellationToken ct)
        => Ok(await reports.IncomeStatementAsync(from, to, ct));

    [HttpGet("reports/balance-sheet")]
    [RequirePermission(Permissions.Accounting.View)]
    public async Task<IActionResult> BalanceSheet([FromQuery] DateOnly asOf, CancellationToken ct)
        => Ok(await reports.BalanceSheetAsync(asOf, ct));

    [HttpGet("reports/aged-receivables")]
    [RequirePermission(Permissions.Finance.View)]
    public async Task<IActionResult> AgedReceivables([FromQuery] DateOnly asOf, CancellationToken ct)
        => Ok(await reports.AgedReceivablesAsync(asOf, ct));

    [HttpGet("reports/aged-payables")]
    [RequirePermission(Permissions.Finance.View)]
    public async Task<IActionResult> AgedPayables([FromQuery] DateOnly asOf, CancellationToken ct)
        => Ok(await reports.AgedPayablesAsync(asOf, ct));

    [HttpGet("reports/cash-flow-forecast")]
    [RequirePermission(Permissions.Finance.View)]
    public async Task<IActionResult> CashFlowForecast([FromQuery] DateOnly from, [FromQuery] DateOnly to, CancellationToken ct)
        => Ok(await reports.CashFlowForecastAsync(from, to, ct));
}
