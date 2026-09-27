using System.Globalization;
using System.Text;
using Asp.Versioning;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Nexora.Application.Operations;

namespace Nexora.Api.Controllers;

/// <summary>
/// Exposition de métriques au format texte Prometheus (Phase 37 §27). Agrégats plateforme
/// non sensibles uniquement — aucune donnée d'entreprise. Ouvert : un collecteur de métriques
/// n'a pas de session (le scraping est protégé au niveau réseau, comme <c>/health</c>).
/// </summary>
[ApiVersion("1.0")]
[AllowAnonymous]
[Route("api/v{version:apiVersion}/metrics")]
public sealed class MetricsController(IPlatformMetrics metrics) : ControllerBase
{
    [HttpGet]
    [Produces("text/plain")]
    public async Task<IActionResult> Get(CancellationToken ct)
    {
        var gauges = await metrics.GaugesAsync(ct);
        var sb = new StringBuilder();
        foreach (var (name, help, value) in gauges)
        {
            sb.Append("# HELP ").Append(name).Append(' ').Append(help.Replace('\n', ' ')).Append('\n');
            sb.Append("# TYPE ").Append(name).Append(" gauge\n");
            sb.Append(name).Append(' ').Append(value.ToString("0.######", CultureInfo.InvariantCulture)).Append('\n');
        }
        return Content(sb.ToString(), "text/plain; version=0.0.4; charset=utf-8");
    }
}
