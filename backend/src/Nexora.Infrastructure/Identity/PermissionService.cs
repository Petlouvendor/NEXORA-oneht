using Microsoft.EntityFrameworkCore;
using Nexora.Application.Abstractions;
using Nexora.Domain.Identity;
using Nexora.Infrastructure.Persistence;

namespace Nexora.Infrastructure.Identity;

/// <summary>
/// Reconstitue le profil de sécurité effectif d'un utilisateur :
/// entreprises accessibles (via les scopes GROUP/COMPANY) et permissions par entreprise
/// (via les rôles). Deny-by-default : rien n'est accordé implicitement (Phase 3 §10).
/// Seules les affectations <b>actives à l'instant présent</b> comptent — un accès temporaire
/// expire de lui-même (Phase 22 §9, §18, §32).
/// </summary>
public sealed class PermissionService(NexoraDbContext db, ITenantContext tenant, IDateTimeProvider clock) : IPermissionService
{
    public async Task<UserSecurityProfile> LoadProfileAsync(Guid userId, CancellationToken ct = default)
    {
        using (tenant.BeginSystemScope())
        {
            var now = clock.UtcNow;
            var assignments = await db.UserRoles.AsNoTracking()
                .Where(ur => ur.UserId == userId
                    && ur.RevokedAt == null
                    && (ur.ValidFrom == null || ur.ValidFrom <= now)
                    && (ur.ValidUntil == null || ur.ValidUntil > now))
                .Select(ur => new
                {
                    ur.Role!.Name,
                    Scope = ur.Scope!,
                    Permissions = ur.Role!.Permissions.Select(p => p.Code).ToList(),
                })
                .ToListAsync(ct);

            // Groupes accessibles → toutes leurs entreprises.
            var groupIds = assignments
                .Where(a => a.Scope.ScopeTypeValue == ScopeType.Group && a.Scope.GroupId is not null)
                .Select(a => a.Scope.GroupId!.Value)
                .Distinct()
                .ToList();

            var companiesByGroup = groupIds.Count == 0
                ? []
                : await db.Companies.AsNoTracking()
                    .Where(c => c.GroupId != null && groupIds.Contains(c.GroupId.Value))
                    .Select(c => new { c.Id, c.Name, c.GroupId })
                    .ToListAsync(ct);

            var directCompanyIds = assignments
                .Where(a => a.Scope.CompanyId is not null)
                .Select(a => a.Scope.CompanyId!.Value)
                .Distinct()
                .ToList();

            var directCompanies = directCompanyIds.Count == 0
                ? []
                : await db.Companies.AsNoTracking()
                    .Where(c => directCompanyIds.Contains(c.Id))
                    .Select(c => new { c.Id, c.Name, c.GroupId })
                    .ToListAsync(ct);

            var allCompanies = companiesByGroup.Concat(directCompanies)
                .GroupBy(c => c.Id)
                .Select(g => g.First())
                .ToList();

            var memberships = allCompanies.Select(c =>
            {
                var applicable = assignments.Where(a =>
                    (a.Scope.ScopeTypeValue == ScopeType.Group && a.Scope.GroupId == c.GroupId) ||
                    (a.Scope.CompanyId == c.Id) ||
                    a.Scope.ScopeTypeValue == ScopeType.Platform).ToList();

                return new CompanyMembership(
                    c.Id,
                    c.Name,
                    c.GroupId,
                    applicable.Select(a => a.Name).Distinct().ToArray(),
                    applicable.SelectMany(a => a.Permissions).Distinct().ToArray());
            }).ToList();

            // Rôles à scope PLATFORM (Phase 38 §35) : permissions plateforme sans entreprise de rattachement.
            var platformPermissions = assignments
                .Where(a => a.Scope.ScopeTypeValue == ScopeType.Platform)
                .SelectMany(a => a.Permissions)
                .Distinct()
                .ToArray();

            return new UserSecurityProfile(userId, memberships, platformPermissions);
        }
    }
}
