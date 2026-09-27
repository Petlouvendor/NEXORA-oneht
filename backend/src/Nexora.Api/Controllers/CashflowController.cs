using Asp.Versioning;
using Microsoft.AspNetCore.Mvc;
using Nexora.Api.Security;
using Nexora.Application.Finance.Treasury;
using Nexora.Application.Security;

namespace Nexora.Api.Controllers;

/// <summary>Prévision de trésorerie par maturité (Phase 28 §22-23, §50).</summary>
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/cashflow")]
public sealed class CashflowController(ITreasuryQueries queries) : ApiControllerBase
{
    [HttpGet("forecast")]
    [RequirePermission(Permissions.Finance.View)]
    public async Task<IActionResult> Forecast(CancellationToken ct)
        => Ok(await queries.MaturityForecastAsync(ct));
}
