using Asp.Versioning;
using Microsoft.AspNetCore.Mvc;
using Nexora.Api.Security;
using Nexora.Application.Preferences;
using Nexora.Application.Security;
using Nexora.Contracts.Preferences;

namespace Nexora.Api.Controllers;

/// <summary>
/// Personnalisation UX persistée (Phase 40 §13, §21-22, §37, §57) : dispositions de tableau de
/// bord, filtres de tableaux sauvegardés, raccourcis. Libre-service (RLS `user_id`) ; poser un
/// défaut d'entreprise exige `admin.configure_company`.
/// </summary>
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}")]
public sealed class UxPersonalizationController(
    IDashboardLayoutService layouts,
    ISavedFilterService filters,
    IShortcutService shortcuts) : ApiControllerBase
{
    // --- §13, §37 Dispositions de tableau de bord -------------------------

    [HttpGet("dashboard-layouts")]
    public async Task<IActionResult> ListLayouts(CancellationToken ct)
        => Ok(new { effective = await layouts.GetEffectiveAsync(ct), all = await layouts.ListAsync(ct) });

    [HttpPut("dashboard-layouts")]
    public async Task<IActionResult> SaveLayout([FromBody] SaveDashboardLayoutRequest r, CancellationToken ct)
        => FromResult(await layouts.SaveAsync(r, ct));

    [HttpPost("dashboard-layouts/company-default")]
    [RequirePermission(Permissions.Admin.ConfigureCompany)]
    public async Task<IActionResult> SaveCompanyLayout([FromBody] SaveDashboardLayoutRequest r, CancellationToken ct)
        => FromResult(await layouts.SaveCompanyDefaultAsync(r, ct));

    [HttpDelete("dashboard-layouts/{id:guid}")]
    public async Task<IActionResult> DeleteLayout(Guid id, CancellationToken ct) => FromResult(await layouts.DeleteAsync(id, ct));

    // --- §21-22, §44 Filtres sauvegardés --------------------------------

    [HttpGet("saved-filters")]
    public async Task<IActionResult> ListFilters([FromQuery] string? viewKey, CancellationToken ct)
        => Ok(await filters.ListAsync(viewKey, ct));

    [HttpPost("saved-filters")]
    public async Task<IActionResult> SaveFilter([FromBody] SaveFilterRequest r, CancellationToken ct)
        => FromResult(await filters.SaveAsync(r, ct));

    [HttpDelete("saved-filters/{id:guid}")]
    public async Task<IActionResult> DeleteFilter(Guid id, CancellationToken ct) => FromResult(await filters.DeleteAsync(id, ct));

    // --- §11, §20, §37-38 Raccourcis ---------------------------------

    [HttpGet("shortcuts")]
    public async Task<IActionResult> ListShortcuts(CancellationToken ct) => Ok(await shortcuts.ListAsync(ct));

    [HttpPost("shortcuts")]
    public async Task<IActionResult> SaveShortcut([FromBody] SaveShortcutRequest r, CancellationToken ct)
        => FromResult(await shortcuts.SaveAsync(r, ct));

    [HttpDelete("shortcuts/{id:guid}")]
    public async Task<IActionResult> DeleteShortcut(Guid id, CancellationToken ct) => FromResult(await shortcuts.DeleteAsync(id, ct));
}
