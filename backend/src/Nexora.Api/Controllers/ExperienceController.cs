using Asp.Versioning;
using Microsoft.AspNetCore.Mvc;
using Nexora.Application.Experience;

namespace Nexora.Api.Controllers;

/// <summary>
/// NEXORA ONE — expérience intelligente (docs/nexora-one-experience-intelligente.md §5-6).
/// Aucune permission unique à l'entrée : chaque groupe de résultats / chaque source d'activité est
/// filtré côté serveur par la permission de son propre écran (authentification exigée par la
/// politique de repli « deny-by-default »).
/// </summary>
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}")]
public sealed class ExperienceController(IGlobalSearchService search, IActivityFeedService activity) : ApiControllerBase
{
    /// <summary>Recherche globale Ctrl+K (§5) : produits, clients, fournisseurs, employés, documents, ventes.</summary>
    [HttpGet("search")]
    public async Task<IActionResult> Search([FromQuery] string q, [FromQuery] int limit = 5, CancellationToken ct = default)
        => Ok(await search.SearchAsync(q, limit, ct));

    /// <summary>Fil des opérations importantes récentes (§6 « ACTIVITY »).</summary>
    [HttpGet("dashboard/activity")]
    public async Task<IActionResult> Activity([FromQuery] int limit = 12, CancellationToken ct = default)
        => Ok(await activity.RecentAsync(limit, ct));
}
