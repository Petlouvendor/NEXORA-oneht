using Microsoft.EntityFrameworkCore;
using Nexora.Infrastructure.Persistence;

namespace Nexora.Infrastructure.Common;

/// <summary>
/// Appartenance d'un utilisateur à une entreprise : une affectation non révoquée sur l'entreprise ou sur son groupe
/// (audit 2026-10 éd. 2). Les assignations (tâche, anomalie) et les destinataires de rapports n'étaient pas vérifiés :
/// un identifiant d'utilisateur quelconque, d'une autre entreprise, était accepté.
/// </summary>
public static class CompanyMembers
{
    public static async Task<bool> AreMembersAsync(NexoraDbContext db, Guid companyId, IReadOnlyCollection<Guid> userIds,
        CancellationToken ct)
    {
        if (userIds.Count == 0) return true;
        var ids = userIds.Distinct().ToArray();
        var members = await db.Database.SqlQuery<int>($"""
            SELECT COUNT(DISTINCT ur.user_id)::int AS "Value"
            FROM nexora.user_role ur
            JOIN nexora.access_scope sc ON sc.id = ur.scope_id
            JOIN nexora.company co ON co.id = {companyId}
            WHERE ur.user_id = ANY({ids}) AND ur.revoked_at IS NULL
              AND (sc.company_id = co.id OR (sc.group_id IS NOT NULL AND sc.group_id = co.group_id))
            """).FirstAsync(ct);
        return members == ids.Length;
    }
}
