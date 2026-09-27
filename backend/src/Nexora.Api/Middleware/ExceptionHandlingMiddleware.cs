using System.Net;
using Microsoft.AspNetCore.Mvc;
using Nexora.Domain.Common;

namespace Nexora.Api.Middleware;

/// <summary>Erreurs structurées RFC 7807 (Phase 3 §8). Ne fuit jamais de détail interne en production.</summary>
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
        catch (Exception ex)
        {
            logger.LogError(ex, "Erreur non gérée sur {Method} {Path}", context.Request.Method, context.Request.Path);
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

    private static Task WriteProblem(HttpContext context, Error error)
    {
        var status = error.Kind switch
        {
            ErrorKind.Validation => HttpStatusCode.UnprocessableEntity,
            ErrorKind.NotFound => HttpStatusCode.NotFound,
            ErrorKind.Conflict => HttpStatusCode.Conflict,
            ErrorKind.Forbidden => HttpStatusCode.Forbidden,
            ErrorKind.Unauthorized => HttpStatusCode.Unauthorized,
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
        context.Response.StatusCode = (int)status;
        return context.Response.WriteAsJsonAsync(problem);
    }
}
