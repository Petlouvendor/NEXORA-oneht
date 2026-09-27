using Asp.Versioning;
using Microsoft.AspNetCore.Mvc;
using Nexora.Application.Preferences;

namespace Nexora.Api.Controllers;

/// <summary>
/// Design system NEXORA ONE (Phase 40 §50, §57). Version, cible WCAG, thèmes, densités, groupes de
/// tokens et catalogue de composants — pour le contrôle de compatibilité front &amp; le handoff.
/// </summary>
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/design-system")]
public sealed class DesignSystemController(IDesignSystemService designSystem) : ApiControllerBase
{
    [HttpGet("version")]
    public IActionResult Version() => Ok(designSystem.GetVersion());
}
