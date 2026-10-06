using System.Text.Json;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Nexora.Api.HealthChecks;

/// <summary>
/// Réponse JSON de la sonde readiness (finalisation phase 18) : statut global et par contrôle, durée — lisible par un
/// orchestrateur comme par un humain. Jamais de message d'exception ni de chaîne de connexion (sonde anonyme).
/// </summary>
public static class HealthResponseWriter
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,   // accents lisibles
    };

    public static Task WriteAsync(HttpContext context, HealthReport report)
    {
        context.Response.ContentType = "application/json; charset=utf-8";
        context.Response.Headers.CacheControl = "no-store";
        var body = new
        {
            status = report.Status.ToString(),
            durationMs = Math.Round(report.TotalDuration.TotalMilliseconds, 1),
            checks = report.Entries.Select(e => new
            {
                name = e.Key,
                status = e.Value.Status.ToString(),
                description = e.Value.Description,
                durationMs = Math.Round(e.Value.Duration.TotalMilliseconds, 1),
            }),
        };
        return context.Response.WriteAsync(JsonSerializer.Serialize(body, Options));
    }
}
