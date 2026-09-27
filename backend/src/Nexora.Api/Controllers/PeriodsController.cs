using Asp.Versioning;
using Microsoft.AspNetCore.Mvc;
using Nexora.Api.Security;
using Nexora.Application.Finance.Abstractions;
using Nexora.Application.Finance.Treasury;
using Nexora.Application.Security;
using Nexora.Contracts.Finance;

namespace Nexora.Api.Controllers;

/// <summary>
/// Clôture financière d'une période (Phase 28 §38, §50). Distincte de la clôture
/// comptable (Phase 4 §27) : ici on contrôle rapprochements, sessions, suspens et soldes,
/// on ne produit aucune écriture.
/// </summary>
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/periods")]
public sealed class PeriodsController(IFinancialCloseService close, IAccountingPeriods periods) : ApiControllerBase
{
    [HttpGet]
    [RequirePermission(Permissions.Finance.View)]
    public async Task<IActionResult> List(CancellationToken ct)
        => Ok((await periods.ListAsync(ct)).Select(p => new
        {
            id = p.Id, name = p.Name, startDate = p.StartDate, endDate = p.EndDate,
            status = p.Status.ToString().ToLowerInvariant(),
        }));

    [HttpGet("{id:guid}/financial-close")]
    [RequirePermission(Permissions.Finance.View)]
    public async Task<IActionResult> Preview(Guid id, CancellationToken ct)
        => FromResult(await close.PreviewAsync(id, ct));

    [HttpPost("{id:guid}/financial-close")]
    [RequirePermission(Permissions.Finance.ClosePeriod)]
    public async Task<IActionResult> Complete(Guid id, [FromBody] CompleteFinancialCloseRequest r, CancellationToken ct)
        => FromResult(await close.CompleteAsync(id, r ?? new CompleteFinancialCloseRequest(), ct));
}
