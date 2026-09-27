using Asp.Versioning;
using Microsoft.AspNetCore.Mvc;
using Nexora.Api.Security;
using Nexora.Application.Preferences;
using Nexora.Application.Security;
using Nexora.Contracts.Preferences;

namespace Nexora.Api.Controllers;

/// <summary>
/// Personnalisation UX (Phase 13 §35-36). Préférences utilisateur en libre-service (isolées par
/// RLS) ; personnalisation d'entreprise en lecture pour tout membre, en écriture pour
/// `admin.configure_company`. L'accent est validé pour ne jamais dégrader l'accessibilité.
/// </summary>
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}")]
public sealed class PreferencesController(
    IUserPreferenceService preferences,
    ICompanyBrandingService branding) : ApiControllerBase
{
    // Routes historiques (Phase 13) + alias canoniques §57.
    [HttpGet("me/preferences")]
    [HttpGet("~/api/v{version:apiVersion}/user-preferences")]
    [HttpGet("~/api/v{version:apiVersion}/ui-preferences")]
    public async Task<IActionResult> GetPreferences(CancellationToken ct) => Ok(await preferences.GetAsync(ct));

    [HttpPut("me/preferences")]
    [HttpPut("~/api/v{version:apiVersion}/user-preferences")]
    [HttpPut("~/api/v{version:apiVersion}/ui-preferences")]
    public async Task<IActionResult> UpdatePreferences([FromBody] UpdateUserPreferenceRequest r, CancellationToken ct)
        => FromResult(await preferences.UpdateAsync(r, ct));

    [HttpGet("companies/branding")]
    [HttpGet("~/api/v{version:apiVersion}/company-branding")]
    public async Task<IActionResult> GetBranding(CancellationToken ct) => Ok(await branding.GetAsync(ct));

    [HttpPut("companies/branding")]
    [HttpPut("~/api/v{version:apiVersion}/company-branding")]
    [RequirePermission(Permissions.Admin.ConfigureCompany)]
    public async Task<IActionResult> UpdateBranding([FromBody] UpdateCompanyBrandingRequest r, CancellationToken ct)
        => FromResult(await branding.UpdateAsync(r, ct));
}
