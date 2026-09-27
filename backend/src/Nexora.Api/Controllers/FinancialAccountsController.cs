using Asp.Versioning;
using Microsoft.AspNetCore.Mvc;
using Nexora.Api.Security;
using Nexora.Application.Finance.Treasury;
using Nexora.Application.Security;

namespace Nexora.Api.Controllers;

/// <summary>
/// Comptes financiers de l'entreprise (Phase 28 §3-4, §50) : caisse / banque / Mobile Money /
/// portefeuille, avec solde système et solde converti en devise fonctionnelle.
/// </summary>
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/financial-accounts")]
public sealed class FinancialAccountsController(ITreasuryQueries queries) : ApiControllerBase
{
    [HttpGet]
    [RequirePermission(Permissions.Finance.View)]
    public async Task<IActionResult> List(CancellationToken ct)
        => Ok(await queries.AccountsAsync(ct));
}
