using System.Net;
using Microsoft.AspNetCore.Mvc;
using Nexora.Application.Observability;
using Nexora.Domain.Common;

namespace Nexora.Api.Middleware;

/// <summary>
/// Erreurs structurées RFC 7807 (Phase 3 §8). Ne fuit jamais de détail interne en production.
/// Finalisation phase 18 : placé APRÈS la journalisation des requêtes, un refus métier (400/404/409/422) est
/// journalisé avec son statut réel et son code, sans pile d'appels ; seule une vraie panne (500) l'est en erreur.
/// </summary>
public sealed class ExceptionHandlingMiddleware(
    RequestDelegate next,
    ILogger<ExceptionHandlingMiddleware> logger,
    IHostEnvironment env)
{
    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await next(context);
        }
        catch (DomainException dex)
        {
            await WriteProblem(context, dex.Error);
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            // Client parti : rien à répondre, ce n'est pas une panne du serveur.
            if (!context.Response.HasStarted) context.Response.StatusCode = 499;
        }
        catch (Exception ex)
        {
            context.Items[ProblemCodeItem] = "internal";
            NexoraMetrics.RecordError("internal", 500);
            logger.LogError(ex, "Erreur non gérée sur {Method} {Path}", context.Request.Method, context.Request.Path);
            if (context.Response.HasStarted) throw;   // réponse déjà partiellement envoyée : impossible de la remplacer
            var problem = new ProblemDetails
            {
                Status = (int)HttpStatusCode.InternalServerError,
                Title = "Erreur interne",
                Detail = env.IsDevelopment() ? ex.ToString() : "Une erreur inattendue est survenue.",
                Type = "https://nexora/errors/internal",
            };
            problem.Extensions["correlationId"] = context.TraceIdentifier;
            context.Response.StatusCode = problem.Status.Value;
            await context.Response.WriteAsJsonAsync(problem);
        }
    }

    /// <summary>Clé de <see cref="HttpContext.Items"/> portant le code d'erreur, repris par le journal des requêtes.</summary>
    public const string ProblemCodeItem = "nexora.problem_code";

    private static Task WriteProblem(HttpContext context, Error error)
    {
        var status = error.Kind switch
        {
            ErrorKind.Validation => HttpStatusCode.UnprocessableEntity,
            ErrorKind.NotFound => HttpStatusCode.NotFound,
            ErrorKind.Conflict => HttpStatusCode.Conflict,
            ErrorKind.Forbidden => HttpStatusCode.Forbidden,
            ErrorKind.Unauthorized => HttpStatusCode.Unauthorized,
            ErrorKind.BadRequest => HttpStatusCode.BadRequest,
            _ => HttpStatusCode.InternalServerError,
        };

        var problem = new ProblemDetails
        {
            Status = (int)status,
            Title = error.Code,
            Detail = error.Message,
            Type = $"https://nexora/errors/{error.Code}",
        };
        problem.Extensions["correlationId"] = context.TraceIdentifier;
        context.Items[ProblemCodeItem] = error.Code;
        NexoraMetrics.RecordError(error.Code, (int)status);
        context.Response.StatusCode = (int)status;
        return context.Response.WriteAsJsonAsync(problem);
    }
}
