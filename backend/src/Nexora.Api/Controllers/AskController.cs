using Asp.Versioning;
using Microsoft.AspNetCore.Mvc;
using Nexora.Api.Security;
using Nexora.Application.Reporting;
using Nexora.Application.Security;
using Nexora.Contracts.Reporting;

namespace Nexora.Api.Controllers;

/// <summary>
/// Ask Nexora (Phase 7 §33) — questions en langage naturel converties en indicateurs
/// autorisés. Les réponses citent leurs sources et signalent explicitement toute
/// projection (jamais présentée comme un fait constaté).
/// </summary>
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/ask")]
public sealed class AskController(IAskNexora ask) : ApiControllerBase
{
    [HttpPost]
    [RequirePermission(Permissions.Reporting.Ask)]
    public async Task<IActionResult> Ask([FromBody] AskRequest request, CancellationToken ct)
        => Ok(await ask.AskAsync(request, ct));
}
