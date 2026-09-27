using System.Security.Claims;
using Microsoft.Extensions.Caching.Memory;
using Nexora.Application.Integrations;
using Nexora.Infrastructure.Identity;

namespace Nexora.Api.Middleware;

/// <summary>
/// Authentification machine-à-machine par clé d'API (Phase 35 §13-14). S'exécute après
/// <c>UseAuthentication</c> : si aucune identité JWT n'est présente et qu'un en-tête
/// <c>X-Api-Key</c> (ou <c>Authorization: Bearer nxk_…</c>) porte une clé NEXORA, la clé est
/// résolue et une identité « ApiKey » est posée sur la requête, avec les scopes de la clé.
/// Le <see cref="TenantResolutionMiddleware"/> qui suit lit les mêmes claims que pour un JWT.
/// </summary>
public sealed class ApiKeyAuthenticationMiddleware(RequestDelegate next)
{
    public const string HeaderName = "X-Api-Key";
    public const string ScopeClaim = "nx_api_scopes";

    public async Task InvokeAsync(HttpContext context, IApiKeyAuthenticator authenticator, IMemoryCache cache, IApiKeyUsageRecorder usage)
    {
        if (context.User.Identity?.IsAuthenticated == true || !TryReadKey(context.Request, out var presented))
        {
            await next(context);
            return;
        }

        var ip = context.Connection.RemoteIpAddress?.ToString();
        var resolution = await authenticator.ResolveAsync(presented, ip, context.RequestAborted);

        if (resolution.Outcome != ApiKeyOutcome.Ok)
        {
            await Deny(context, 401, resolution.Outcome switch
            {
                ApiKeyOutcome.Expired => "api_key_expired",
                ApiKeyOutcome.Revoked => "api_key_revoked",
                _ => "api_key_invalid",
            }, "Clé d'API invalide, expirée ou révoquée.");
            return;
        }

        // Quota par minute (§13, §33) — fenêtre glissante en mémoire, par clé.
        var bucketKey = $"apikey:{resolution.KeyId}:{DateTimeOffset.UtcNow:yyyyMMddHHmm}";
        // Cache borné (SizeLimit, REQ-02) : chaque entrée déclare sa taille, sinon MemoryCache lève une exception.
        var count = cache.GetOrCreate(bucketKey, e => { e.AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(2); e.Size = 1; return 0; });
        var rateLimited = count >= resolution.RateLimitPerMinute;

        // Usage quotidien (Phase 46 §9, §47-48) — portail développeur. Best-effort.
        await usage.RecordAsync(resolution.KeyId!.Value, resolution.CompanyId!.Value, rateLimited, context.RequestAborted);

        if (rateLimited)
        {
            context.Response.Headers.RetryAfter = "60";
            await Deny(context, 429, "api_key_rate_limited", "Quota de la clé d'API dépassé — réessayez dans une minute.");
            return;
        }
        cache.Set(bucketKey, count + 1, new MemoryCacheEntryOptions { AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(2), Size = 1 });

        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
        var claims = new List<Claim>
        {
            new(NexoraClaims.UserId, resolution.OwnerUserId!.Value.ToString()),
            new(NexoraClaims.CompanyIds, resolution.CompanyId!.Value.ToString()),
            new(NexoraClaims.GroupIds, ""),
            new("iat", now),
            new(ScopeClaim, string.Join(',', resolution.Scopes ?? [])),
        };
        context.User = new ClaimsPrincipal(new ClaimsIdentity(claims, "ApiKey"));

        await next(context);
    }

    private static bool TryReadKey(HttpRequest request, out string key)
    {
        key = "";
        if (request.Headers.TryGetValue(HeaderName, out var raw) && !string.IsNullOrWhiteSpace(raw))
        {
            key = raw.ToString().Trim();
            return true;
        }
        var auth = request.Headers.Authorization.ToString();
        if (auth.StartsWith("Bearer nxk_", StringComparison.Ordinal))
        {
            key = auth["Bearer ".Length..].Trim();
            return true;
        }
        return false;
    }

    private static async Task Deny(HttpContext context, int status, string title, string detail)
    {
        context.Response.StatusCode = status;
        await context.Response.WriteAsJsonAsync(new { title, detail, correlationId = context.TraceIdentifier });
    }
}
