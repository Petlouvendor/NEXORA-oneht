using Asp.Versioning;
using Microsoft.AspNetCore.Mvc;
using Nexora.Api.Security;
using Nexora.Application.Finance;
using Nexora.Application.Security;
using Nexora.Contracts.Finance;

namespace Nexora.Api.Controllers;

/// <summary>Taux de change (audit 2026-10, A-017) : consultation et saisie datée ; un taux n'est jamais modifié.</summary>
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/exchange-rates")]
public sealed class ExchangeRatesController(IExchangeRateService rates) : ApiControllerBase
{
    [HttpGet]
    [RequirePermission(Permissions.Finance.View)]
    public async Task<IActionResult> List([FromQuery] string? baseCurrency, [FromQuery] string? quoteCurrency, CancellationToken ct)
        => Ok(await rates.ListAsync(baseCurrency, quoteCurrency, ct));

    [HttpPost]
    [RequirePermission(Permissions.Finance.Update)]
    public async Task<IActionResult> Record([FromBody] RecordExchangeRateRequest request, CancellationToken ct)
        => FromResult(await rates.RecordAsync(request, ct));
}
