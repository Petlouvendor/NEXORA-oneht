using Nexora.Application.Abstractions;
using Nexora.Application.Operations;
using Nexora.Application.Security;

namespace Nexora.Api.Middleware;

/// <summary>
/// Dégrade l'API pendant une fenêtre de maintenance <c>in_progress</c> (Phase 41 §23-24, §51) :
/// les lectures restent servies, les écritures sont refusées (503) — sauf pour un titulaire de
/// <c>operations.manage</c>, qui doit pouvoir piloter et clore la maintenance depuis l'API
/// elle-même. Les routes santé, exploitation et authentification restent toujours accessibles.
/// Par défaut (aucune fenêtre <c>in_progress</c>), ce middleware ne fait qu'une lecture rapide et
/// laisse passer — aucun impact sur le comportement existant.
/// </summary>
public sealed partial class MaintenanceModeMiddleware(RequestDelegate next)
{
    private static readonly HashSet<string> SafeMethods = new(StringComparer.OrdinalIgnoreCase) { "GET", "HEAD", "OPTIONS" };

    public async Task InvokeAsync(HttpContext context, IMaintenanceService maintenance, ITenantContext tenant, IPermissionService permissions)
    {
        var path = context.Request.Path.Value ?? "";
        if (SafeMethods.Contains(context.Request.Method) || IsExempt(path))
        {
            await next(context);
            return;
        }

        if (!await maintenance.IsActiveAsync(context.RequestAborted))
        {
            await next(context);
            return;
        }

        if (tenant.UserId is { } userId)
        {
            var profile = await Security.RequestPermissionProfile.GetAsync(context, permissions, userId, context.RequestAborted);
            if (profile.HasPermission(Permissions.Operations.Manage, tenant.ActiveCompanyId))
            {
                await next(context);
                return;
            }
        }

        context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
        context.Response.Headers["Retry-After"] = "300";
        await context.Response.WriteAsJsonAsync(new
        {
            title = "maintenance_in_progress",
            detail = "NEXORA ONE est en maintenance planifiée — les écritures sont temporairement suspendues.",
            correlationId = context.TraceIdentifier,
        });
    }

    /// <summary>
    /// Exemptions sur le PREMIER segment de route, après le préfixe de version (audit 2026-10 éd. 2) : la recherche par
    /// sous-chaîne exemptait aussi toute route contenant « /ready », « /health » ou « /auth/ » plus loin dans le chemin.
    /// L'assistant d'installation (§20) est exempté : aucune base n'existe encore, rien à protéger.
    /// </summary>
    public static bool IsExempt(string path) =>
        ExemptRoute().IsMatch(path) || path.StartsWith("/swagger", StringComparison.OrdinalIgnoreCase);

    [System.Text.RegularExpressions.GeneratedRegex(@"^/(api/v[0-9]+(\.[0-9]+)?/)?(ops|auth|health|ready|setup)(/|$)",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant)]
    private static partial System.Text.RegularExpressions.Regex ExemptRoute();
}
