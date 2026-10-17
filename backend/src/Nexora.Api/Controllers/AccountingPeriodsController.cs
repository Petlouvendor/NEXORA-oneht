using Asp.Versioning;
using Microsoft.AspNetCore.Mvc;
using Nexora.Api.Security;
using Nexora.Application.Finance.Abstractions;
using Nexora.Application.Finance.Ledger;
using Nexora.Application.Finance.Periods;
using Nexora.Application.Security;
using Nexora.Contracts.Finance;

namespace Nexora.Api.Controllers;

/// <summary>
/// Périodes & exercices comptables (Phase 29 §15-19, §51). Clôture / réouverture (Phase 4),
/// plus la clôture d'exercice (résultat + report) et la génération des à-nouveaux.
/// </summary>
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/accounting-periods")]
public sealed class AccountingPeriodsController(
    IAccountingPeriods periods,
    IAccountingPeriodService periodService,
    IFiscalYearService fiscalYear) : ApiControllerBase
{
    [HttpGet]
    [RequirePermission(Permissions.Accounting.View)]
    public async Task<IActionResult> List(CancellationToken ct)
        => Ok((await periods.ListAsync(ct)).Select(p => new
        {
            id = p.Id, name = p.Name, startDate = p.StartDate, endDate = p.EndDate,
            status = p.Status.ToString().ToLowerInvariant(),
        }));

    /// <summary>Ouvre l'exercice qui suit le dernier (audit 2026-10 éd. 2, A-042).</summary>
    [HttpPost("next-year")]
    [RequirePermission(Permissions.Accounting.ClosePeriod)]
    public async Task<IActionResult> OpenNextYear(CancellationToken ct)
    {
        var r = await periodService.OpenNextYearAsync(ct);
        return r.IsSuccess
            ? Ok(new
            {
                id = r.Value!.Id, name = r.Value.Name, startDate = r.Value.StartDate, endDate = r.Value.EndDate,
                status = r.Value.Status.ToString().ToLowerInvariant(),
            })
            : FromResult(r);
    }

    [HttpPost("{id:guid}/close")]
    [RequireStepUp(StepUpPurposes.AccountingValidation)]
    [RequirePermission(Permissions.Accounting.ClosePeriod)]
    public async Task<IActionResult> Close(Guid id, CancellationToken ct)
        => FromResult(await periodService.CloseAsync(id, ct));

    [HttpPost("{id:guid}/reopen")]
    [RequireStepUp(StepUpPurposes.AccountingValidation)]
    [RequirePermission(Permissions.Accounting.ReopenPeriod)]
    public async Task<IActionResult> Reopen(Guid id, [FromBody] ReverseJournalEntryRequest body, CancellationToken ct)
        => FromResult(await periodService.ReopenAsync(id, body.Reason, ct));

    // --- Clôture d'exercice (§18) --------------------------------------

    [HttpGet("{id:guid}/close-year")]
    [RequirePermission(Permissions.Accounting.View)]
    public async Task<IActionResult> PreviewCloseYear(Guid id, CancellationToken ct)
        => FromResult(await fiscalYear.PreviewCloseAsync(id, ct));

    [HttpPost("{id:guid}/close-year")]
    [RequireStepUp(StepUpPurposes.AccountingValidation)]
    [RequirePermission(Permissions.Accounting.CarryForward)]
    public async Task<IActionResult> CloseYear(Guid id, [FromBody] CloseYearRequest? r, CancellationToken ct)
        => FromResult(await fiscalYear.CloseYearAsync(id, r ?? new CloseYearRequest(), ct));

    // --- À-nouveaux (§19) --------------------------------------------

    [HttpPost("{id:guid}/opening-balances")]
    [RequirePermission(Permissions.Accounting.CarryForward)]
    public async Task<IActionResult> OpeningBalances(Guid id, [FromBody] GenerateOpeningBalancesRequest r, CancellationToken ct)
        => FromResult(await fiscalYear.GenerateOpeningBalancesAsync(id, r, ct));
}
