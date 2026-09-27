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
public sealed class MaintenanceModeMiddleware(RequestDelegate next)
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
            var profile = await permissions.LoadProfileAsync(userId, context.RequestAborted);
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

    private static bool IsExempt(string path) =>
        path.Contains("/ops/", StringComparison.OrdinalIgnoreCase)
        || path.EndsWith("/ops", StringComparison.OrdinalIgnoreCase)
        || path.Contains("/auth/", StringComparison.OrdinalIgnoreCase)
        || path.Contains("/health", StringComparison.OrdinalIgnoreCase)
        || path.Contains("/ready", StringComparison.OrdinalIgnoreCase)
        || path.StartsWith("/swagger", StringComparison.OrdinalIgnoreCase)
        // Assistant d'installation (§20) : aucune base n'existe encore, rien à protéger ici.
        || path.StartsWith("/api/v1/setup", StringComparison.OrdinalIgnoreCase);
}
