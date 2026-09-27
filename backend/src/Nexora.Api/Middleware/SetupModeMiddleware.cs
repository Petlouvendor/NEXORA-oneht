using Microsoft.AspNetCore.Mvc;
using Nexora.Infrastructure.Setup;

namespace Nexora.Api.Middleware;

/// <summary>
/// Résumé fonctionnel §20 : en mode installation (aucune base configurée), seules les routes de
/// l'assistant et de santé répondent ; tout le reste renvoie 503 <c>setup.required</c> — puis
/// <c>setup.restart_required</c> une fois l'installation faite, jusqu'au redémarrage.
/// </summary>
public sealed class SetupModeMiddleware(RequestDelegate next, SetupState state)
{
    public async Task InvokeAsync(HttpContext context)
    {
        var path = context.Request.Path;
        if (!state.Required
            || path.StartsWithSegments("/api/v1/setup")
            || path.StartsWithSegments("/health")
            || path.StartsWithSegments("/api/v1/health")
            || path.StartsWithSegments("/swagger"))
        {
            await next(context);
            return;
        }

        var problem = new ProblemDetails
        {
            Status = StatusCodes.Status503ServiceUnavailable,
            Title = state.Installed ? "setup.restart_required" : "setup.required",
            Detail = state.Installed
                ? "Installation terminée : redémarrez le service Nexora One pour charger la configuration."
                : "Nexora One n'est pas encore installé : ouvrez l'assistant d'installation (/setup).",
        };
        problem.Extensions["correlationId"] = context.TraceIdentifier;
        context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
        await context.Response.WriteAsJsonAsync(problem, (System.Text.Json.JsonSerializerOptions?)null, "application/problem+json");
    }
}
