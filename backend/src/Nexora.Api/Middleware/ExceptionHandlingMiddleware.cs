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
        catch (Exception ex) when (!context.Response.HasStarted && DatabaseRefusal(ex) is { } refusal)
        {
            // Refus de la base (déclencheur métier, contrainte, conflit de concurrence) levé hors d'une unité de travail
            // (audit 2026-10 éd. 2) : rendu comme un refus explicite plutôt qu'une « erreur inattendue » 500.
            await WriteProblem(context, refusal);
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

    /// <summary>Erreur métier pour une <see cref="Npgsql.PostgresException"/> attendue ; <c>null</c> pour une vraie panne.</summary>
    public static Error? DatabaseRefusal(Exception ex)
    {
        var pg = ex as Npgsql.PostgresException ?? ex.InnerException as Npgsql.PostgresException;
        if (pg is null) return null;
        var message = Nexora.Infrastructure.Persistence.DatabaseErrors.ClientMessage(pg);
        if (Nexora.Infrastructure.Persistence.DatabaseErrors.IsBusinessRefusal(pg)) return Error.Conflict("database.refused", message);
        return pg.SqlState switch
        {
            Npgsql.PostgresErrorCodes.UniqueViolation or Npgsql.PostgresErrorCodes.ForeignKeyViolation
                or Npgsql.PostgresErrorCodes.ExclusionViolation => Error.Conflict("database.conflict", message),
            Npgsql.PostgresErrorCodes.CheckViolation or Npgsql.PostgresErrorCodes.NotNullViolation
                => Error.Validation("database.invalid", message),
            Npgsql.PostgresErrorCodes.SerializationFailure or Npgsql.PostgresErrorCodes.DeadlockDetected
                or Npgsql.PostgresErrorCodes.LockNotAvailable => Error.Conflict("database.retry", message),
            Npgsql.PostgresErrorCodes.InsufficientPrivilege => Error.Forbidden("database.forbidden", message),
            _ => null,
        };
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
