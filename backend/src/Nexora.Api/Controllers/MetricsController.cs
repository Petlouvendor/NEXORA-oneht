using System.Globalization;
using System.Text;
using Asp.Versioning;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Nexora.Application.Operations;

namespace Nexora.Api.Controllers;

/// <summary>
/// Exposition de métriques au format texte Prometheus (Phase 37 §27). Agrégats plateforme
/// non sensibles uniquement — aucune donnée d'entreprise. Un collecteur de métriques n'a pas de session : la page est
/// servie à une adresse interne (réseau du collecteur, boucle locale) ou sur présentation du jeton
/// <c>Metrics:Token</c>. Avant (audit 2026-10, A-024), elle était ouverte à tous et seul le chemin exact était bloqué
/// par le proxy : <c>/api/v1.0/metrics</c> ou <c>/api/v1/metrics/</c> passaient.
/// </summary>
[ApiVersion("1.0")]
[AllowAnonymous]
[Route("api/v{version:apiVersion}/metrics")]
public sealed class MetricsController(IPlatformMetrics metrics, Microsoft.Extensions.Configuration.IConfiguration configuration)
    : ControllerBase
{
    [HttpGet]
    [Produces("text/plain")]
    public async Task<IActionResult> Get(CancellationToken ct)
    {
        if (!Allowed()) return NotFound();
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

    /// <summary>Adresse interne (après résolution des en-têtes du proxy), ou jeton de collecte valide.</summary>
    private bool Allowed()
    {
        // Comparaison en temps constant (audit 2026-10 éd. 2) : la durée ne révèle pas le préfixe correct du jeton.
        if (configuration["Metrics:Token"] is { Length: >= 16 } token
            && System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(
                System.Text.Encoding.UTF8.GetBytes(Request.Headers.Authorization.ToString()),
                System.Text.Encoding.UTF8.GetBytes($"Bearer {token}")))
            return true;
        var ip = HttpContext.Connection.RemoteIpAddress;
        return ip is null || !Nexora.Infrastructure.Integrations.WebhookTargetPolicy.IsPublic(ip);
    }
}
