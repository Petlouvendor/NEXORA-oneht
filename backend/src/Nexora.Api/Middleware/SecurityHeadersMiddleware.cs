namespace Nexora.Api.Middleware;

/// <summary>En-têtes de sécurité HTTP (Phase 3 §13 ; CSP ajoutée Phase 47 §22).</summary>
public sealed class SecurityHeadersMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context)
    {
        var headers = context.Response.Headers;
        headers["X-Content-Type-Options"] = "nosniff";
        headers["X-Frame-Options"] = "DENY";
        headers["Referrer-Policy"] = "no-referrer";
        headers["Cross-Origin-Opener-Policy"] = "same-origin";
        headers["Permissions-Policy"] = "geolocation=(), camera=(), microphone=()";
        // API pure JSON — aucun script/style/frame propre à servir ; posture la plus stricte possible.
        headers["Content-Security-Policy"] = "default-src 'none'; frame-ancestors 'none'; base-uri 'none'";
        await next(context);
    }
}
