using Asp.Versioning;
using Microsoft.AspNetCore.Mvc;
using Nexora.Api.Security;
using Nexora.Application.Security;
using Nexora.Application.UseCases.Organization;
using Nexora.Contracts.Organization;

namespace Nexora.Api.Controllers;

[ApiVersion("1.0")]
public sealed class CompaniesController(ListCompaniesHandler listCompanies) : ApiControllerBase
{
    /// <summary>Entreprises visibles dans le périmètre courant (isolation RLS PostgreSQL).</summary>
    [HttpGet]
    [RequirePermission(Permissions.Organizations.View)]
    [ProducesResponseType<IReadOnlyList<CompanyResponse>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> List(CancellationToken ct)
        => Ok(await listCompanies.HandleAsync(ct));
}
