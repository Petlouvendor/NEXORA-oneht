using Microsoft.EntityFrameworkCore;
using Nexora.Application.Abstractions;
using Nexora.Application.Operations;
using Nexora.Domain.Common;
using Nexora.Infrastructure.Persistence;

namespace Nexora.Infrastructure.Operations;

/// <summary>
/// Garde des opérations d'exploitation qui agissent sur toute l'installation (audit 2026-10, A-031) : fenêtre de
/// maintenance (qui suspend les écritures de toutes les entreprises), tâches planifiées, bascules globales, purge
/// technique, journal des déploiements, cache. Les permissions <c>operations.*</c> sont portées par des rôles
/// d'entreprise (direction) : sur une plateforme qui héberge plusieurs clients, un directeur pouvait ainsi geler les
/// caisses des autres clients. Règle :
/// <list type="bullet">
/// <item>permission détenue à la portée PLATEFORME (exploitant) → autorisé ;</item>
/// <item>sinon permission détenue dans l'entreprise active ET installation d'un seul client (toutes les entreprises
/// actives appartiennent au groupe de l'entreprise active, ou l'entreprise est seule) → autorisé : sur site, la
/// direction reste l'exploitant de sa propre installation ;</item>
/// <item>sinon → refus <c>operations.platform_only</c>.</item>
/// </list>
/// </summary>
public sealed class PlatformOperationGuard(NexoraDbContext db, ITenantContext tenant, IPermissionService permissions)
    : IPlatformOperationGuard
{
    public async Task<Error?> RequirePlatformWideAsync(string permission, CancellationToken ct = default)
    {
        if (tenant.UserId is not { } userId)
            return Error.Forbidden("operations.platform_only", "Opération réservée à l'exploitant de la plateforme.");
        var profile = await permissions.LoadProfileAsync(userId, ct);
        if (profile.PlatformPermissions is { } platform && platform.Contains(permission))
            return null;
        if (tenant.ActiveCompanyId is not { } company || !profile.HasPermission(permission, company))
            return Error.Forbidden("operations.forbidden", "Permission d'exploitation absente pour cette entreprise.");

        int otherClients;
        using (tenant.BeginSystemScope())
            otherClients = await db.Database.SqlQuery<int>($"""
                SELECT COUNT(*)::int AS "Value"
                FROM nexora.company c, (SELECT group_id FROM nexora.company WHERE id = {company}) me
                WHERE c.id <> {company} AND c.status <> 'archived'
                  AND (me.group_id IS NULL OR c.group_id IS DISTINCT FROM me.group_id)
                """).FirstAsync(ct);
        return otherClients == 0
            ? null
            : Error.Forbidden("operations.platform_only",
                "Cette opération agit sur toute la plateforme, qui héberge d'autres clients : elle est réservée à l'exploitant de la plateforme.");
    }
}
