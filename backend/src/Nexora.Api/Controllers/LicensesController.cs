using Microsoft.AspNetCore.RateLimiting;
using Asp.Versioning;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Nexora.Api.Security;
using Nexora.Application.Platform;
using Nexora.Application.Security;
using Nexora.Contracts.Platform;
using Nexora.Domain.Common;

namespace Nexora.Api.Controllers;

/// <summary>
/// Licences (Phase 38 §27-29, §50) : émission / révocation côté plateforme, jetons offline signés
/// pour le portail (§28), vérification locale publique.
/// </summary>
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/licenses")]
public sealed class LicensesController(ILicenseService licenses, IPlatformScope scope) : ApiControllerBase
{
    private async Task<Guid> CustomerAsync(CancellationToken ct)
        => await scope.CurrentCustomerIdAsync(ct)
           ?? throw new DomainException(Error.NotFound("platform.no_customer",
               "Aucune organisation cliente n'est rattachée à votre entreprise active."));

    /// <summary>Mes licences (portail).</summary>
    [HttpGet]
    [RequirePermission(Permissions.Platform.Portal)]
    public async Task<IActionResult> Mine(CancellationToken ct) => Ok(await licenses.ListForCustomerAsync(await CustomerAsync(ct), ct));

    /// <summary>Jeton offline pour une de mes licences (§28).</summary>
    [HttpPost("{id:guid}/token")]
    [RequirePermission(Permissions.Platform.Portal)]
    public async Task<IActionResult> Token(Guid id, CancellationToken ct)
        => FromResult(await licenses.IssueOfflineTokenAsync(id, await CustomerAsync(ct), ct));

    // --- Côté plateforme -------------------------------------------------

    [HttpGet("~/api/v{version:apiVersion}/platform/customers/{customerId:guid}/licenses")]
    [RequirePermission(Permissions.Platform.View)]
    public async Task<IActionResult> ForCustomer(Guid customerId, CancellationToken ct) => Ok(await licenses.ListForCustomerAsync(customerId, ct));

    [HttpPost("~/api/v{version:apiVersion}/platform/licenses")]
    [RequirePermission(Permissions.Platform.ManageLicenses)]
    public async Task<IActionResult> Issue([FromBody] IssueLicenseRequest r, CancellationToken ct) => FromResult(await licenses.IssueAsync(r, ct));

    [HttpPost("{id:guid}/revoke")]
    [RequirePermission(Permissions.Platform.ManageLicenses)]
    public async Task<IActionResult> Revoke(Guid id, [FromBody] RevokeLicenseRequest r, CancellationToken ct) => FromResult(await licenses.RevokeAsync(id, r, ct));

    /// <summary>Vérification locale d'un jeton de licence offline (§28). Publique — le client la fait sans session.</summary>
    [EnableRateLimiting("public")]
    [HttpPost("verify")]
    [AllowAnonymous]
    public async Task<IActionResult> Verify([FromBody] LicenseVerifyRequest r, CancellationToken ct) => Ok(await licenses.VerifyOfflineTokenAsync(r.Token, ct));
}
