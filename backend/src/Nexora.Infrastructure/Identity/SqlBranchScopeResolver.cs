using Microsoft.EntityFrameworkCore;
using Nexora.Application.Abstractions;
using Nexora.Infrastructure.Persistence;

namespace Nexora.Infrastructure.Identity;

/// <summary>
/// Résumé fonctionnel §4 — voir <see cref="IBranchScopeResolver"/>. Pour chaque entreprise du périmètre :
/// une affectation entreprise / groupe / plateforme ⇒ toute l'entreprise ; sinon seulement les
/// établissements de ses affectations « ESTABLISHMENT ». Si aucune entreprise n'est restreinte, rien
/// n'est restreint (<c>null</c>) ; sinon la liste réunit les succursales autorisées de chaque entreprise
/// (toutes celles des entreprises non restreintes).
/// </summary>
public sealed class SqlBranchScopeResolver(NexoraDbContext db, ITenantContext tenant) : IBranchScopeResolver
{
    private sealed record ScopeRow(Guid CompanyId, string ScopeType, Guid? EstablishmentId);

    public async Task<IReadOnlyCollection<Guid>?> ResolveAsync(Guid userId, IReadOnlyCollection<Guid> companyIds, CancellationToken ct = default)
    {
        var companies = companyIds.ToArray();
        List<ScopeRow> rows;
        using (tenant.BeginSystemScope())
            rows = await db.Database.SqlQuery<ScopeRow>($"""
                SELECT c.id AS "CompanyId", sc.scope_type AS "ScopeType", sc.establishment_id AS "EstablishmentId"
                FROM nexora.user_role ur
                JOIN nexora.access_scope sc ON sc.id = ur.scope_id
                JOIN nexora.company c ON c.id = ANY({companies})
                     AND (sc.scope_type = 'PLATFORM' OR sc.company_id = c.id
                          OR (sc.group_id IS NOT NULL AND sc.group_id = c.group_id))
                WHERE ur.user_id = {userId} AND ur.revoked_at IS NULL
                  AND (ur.valid_from IS NULL OR ur.valid_from <= now())
                  AND (ur.valid_until IS NULL OR ur.valid_until > now())
                """).ToListAsync(ct);

        var restricted = rows.GroupBy(r => r.CompanyId)
            .Where(g => g.All(r => r.ScopeType == "ESTABLISHMENT"))
            .ToDictionary(g => g.Key, g => g.Where(r => r.EstablishmentId is not null).Select(r => r.EstablishmentId!.Value).ToList());
        if (restricted.Count == 0) return null;

        var allowed = restricted.Values.SelectMany(v => v).ToHashSet();
        var unrestricted = companies.Where(c => !restricted.ContainsKey(c)).ToArray();
        if (unrestricted.Length > 0)
        {
            using (tenant.BeginSystemScope())
                allowed.UnionWith(await db.Database.SqlQuery<Guid>($"""
                    SELECT id AS "Value" FROM nexora.establishment WHERE company_id = ANY({unrestricted})
                    """).ToListAsync(ct));
        }
        // Restreint sans aucune succursale valide : un identifiant impossible plutôt qu'une liste vide
        // (vide = « aucune restriction » côté RLS).
        return allowed.Count > 0 ? allowed : [Guid.Empty];
    }
}
