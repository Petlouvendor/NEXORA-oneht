using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;

namespace Nexora.Api.Security;

/// <summary>
/// Idempotence des opérations rejouables (Phase 18 §12, §16 ; Phase 3 §8) : une même
/// <c>Idempotency-Key</c> pour la même charge utile renvoie <b>exactement la même réponse</b>,
/// sans re-traitement. Une clé réutilisée avec une charge différente est refusée (422).
/// Scaffold : cache mémoire (24 h). En production → table dédiée / cache distribué.
/// La sécurité financière de fond reste garantie par les contraintes DB (`operation_uuid` unique).
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class IdempotentAttribute : Attribute, IFilterFactory
{
    public bool IsReusable => false;

    public IFilterMetadata CreateInstance(IServiceProvider serviceProvider) =>
        ActivatorUtilities.CreateInstance<IdempotencyFilter>(serviceProvider);
}

public sealed class IdempotencyFilter(IMemoryCache cache, ILogger<IdempotencyFilter> logger, IOptions<JsonOptions> mvcJson) : IAsyncActionFilter
{
    public const string HeaderName = "Idempotency-Key";
    public const string ReplayHeader = "Idempotency-Replay";
    private static readonly TimeSpan Ttl = TimeSpan.FromHours(24);
    private static readonly JsonSerializerOptions Json = new() { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.Never };

    private sealed record CachedResponse(int StatusCode, string RequestHash, string? Body, string? ContentType);

    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        var http = context.HttpContext;

        if (!http.Request.Headers.TryGetValue(HeaderName, out var keyValues) || string.IsNullOrWhiteSpace(keyValues))
        {
            context.Result = Problem(400, "idempotency_key_required",
                $"L'en-tête {HeaderName} est obligatoire pour cette opération.");
            return;
        }

        var key = keyValues.ToString().Trim();
        if (key.Length is < 8 or > 200)
        {
            context.Result = Problem(400, "idempotency_key_invalid", "Clé d'idempotence invalide (8 à 200 caractères).");
            return;
        }

        var user = http.User.FindFirst("nx_uid")?.Value ?? "anon";
        var cacheKey = $"idem:{user}:{key}";
        var requestHash = HashArguments(context.ActionArguments);

        if (cache.TryGetValue<CachedResponse>(cacheKey, out var cached) && cached is not null)
        {
            if (!string.Equals(cached.RequestHash, requestHash, StringComparison.Ordinal))
            {
                context.Result = Problem(422, "idempotency_key_conflict",
                    "Cette clé d'idempotence a déjà servi pour une charge utile différente.");
                return;
            }

            logger.LogInformation("Rejeu idempotent — clé {Key}", key);
            http.Response.Headers[ReplayHeader] = "true";
            context.Result = cached.Body is null
                ? new StatusCodeResult(cached.StatusCode)
                : new ContentResult
                {
                    StatusCode = cached.StatusCode,
                    Content = cached.Body,
                    ContentType = cached.ContentType ?? "application/json",
                };
            return;
        }

        var executed = await next();

        if (executed.Result is IStatusCodeActionResult sc && sc.StatusCode is >= 200 and < 300)
        {
            string? body = null;
            string? contentType = null;
            if (executed.Result is ObjectResult { Value: { } value })
            {
                // Mêmes options que la réponse d'origine (camelCase, énumérations…) : un rejeu est indiscernable.
                body = JsonSerializer.Serialize(value, value.GetType(), mvcJson.Value.JsonSerializerOptions);
                contentType = "application/json";
            }
            // Cache borné (SizeLimit, REQ-02) : la taille de l'entrée est obligatoire.
            cache.Set(cacheKey, new CachedResponse(sc.StatusCode ?? 200, requestHash, body, contentType),
                new MemoryCacheEntryOptions { AbsoluteExpirationRelativeToNow = Ttl, Size = 1 });
        }
    }

    private static string HashArguments(IDictionary<string, object?> args)
    {
        // Le hash porte sur les arguments désérialisés de l'action (le DTO), pas sur le flux brut.
        // On écarte les valeurs injectées par le framework et non sérialisables (jeton d'annulation).
        var payload = JsonSerializer.Serialize(
            args.Where(a => a.Value is not CancellationToken)
                .OrderBy(a => a.Key, StringComparer.Ordinal)
                .ToDictionary(a => a.Key, a => a.Value),
            Json);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload)));
    }

    private static ObjectResult Problem(int status, string title, string detail) =>
        new(new ProblemDetails { Status = status, Title = title, Detail = detail }) { StatusCode = status };
}
