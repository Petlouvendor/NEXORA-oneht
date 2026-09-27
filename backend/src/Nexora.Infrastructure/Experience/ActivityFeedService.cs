using Microsoft.EntityFrameworkCore;
using Nexora.Application.Abstractions;
using Nexora.Application.Experience;
using Nexora.Application.Security;
using Nexora.Contracts.Experience;
using Nexora.Infrastructure.Persistence;

namespace Nexora.Infrastructure.Experience;

/// <summary>
/// Fil d'activité du dashboard (NEXORA ONE §6 « ACTIVITY »). Lecture directe des tables métier — ventes,
/// paiements, réceptions, clôtures de caisse, écritures manuelles, décisions sur les recommandations —
/// jamais une table d'événements parallèle qui pourrait diverger. Chaque source n'est interrogée que si
/// l'utilisateur détient la permission de l'écran qui la montre (un caissier ne voit pas les encaissements
/// de l'entreprise, §5 « navigation adaptative au rôle »).
/// </summary>
public sealed class ActivityFeedService(
    NexoraDbContext db, ITenantContext tenant, IPermissionService permissions) : IActivityFeedService
{
    private Guid Company => tenant.ActiveCompanyId ?? throw new InvalidOperationException("Entreprise active requise.");

    private sealed record Row(
        string Kind, string Title, string Detail, decimal? Amount, string? Currency,
        DateTimeOffset OccurredAt, string? Route, string Tone);

    public async Task<IReadOnlyList<ActivityEventDto>> RecentAsync(int limit, CancellationToken ct = default)
    {
        var take = Math.Clamp(limit, 1, 50);
        var granted = await permissions.CurrentPermissionsAsync(tenant, ct);
        var rows = new List<Row>();

        if (granted.Contains(Permissions.Reporting.ViewSales) || granted.Contains(Permissions.Sales.View))
        {
            var openable = granted.Contains(Permissions.Pos.View);
            rows.AddRange(await db.Database.SqlQuery<Row>($"""
                SELECT 'sale' AS "Kind",
                       CASE s.status WHEN 'refunded' THEN 'Vente remboursée ' WHEN 'partially_refunded' THEN 'Vente partiellement remboursée '
                                     WHEN 'void' THEN 'Vente annulée ' ELSE 'Vente ' END || s.document_number AS "Title",
                       COALESCE(cu.name, 'Client comptoir')
                       || CASE WHEN s.sale_type = 'credit' THEN ' · à crédit' ELSE '' END AS "Detail",
                       s.total AS "Amount", cur.code AS "Currency", s.sold_at AS "OccurredAt",
                       CASE WHEN {openable} THEN '/sales/' || s.id::text END AS "Route",
                       CASE s.status WHEN 'completed' THEN 'success' WHEN 'void' THEN 'error' ELSE 'warning' END AS "Tone"
                FROM nexora.sale s
                JOIN nexora.currency cur ON cur.id = s.currency_id
                LEFT JOIN nexora.customer cu ON cu.id = s.customer_id
                WHERE s.company_id = {Company} AND s.status IN ('completed', 'partially_refunded', 'refunded', 'void')
                ORDER BY s.sold_at DESC
                LIMIT {take}
                """).ToListAsync(ct));
        }

        if (granted.Contains(Permissions.Finance.View))
        {
            rows.AddRange(await db.Database.SqlQuery<Row>($"""
                SELECT 'payment' AS "Kind",
                       CASE p.direction WHEN 'inbound' THEN 'Encaissement ' ELSE 'Décaissement ' END || p.payment_number AS "Title",
                       COALESCE(cu.name, su.name, 'Sans tiers') || COALESCE(' · ' || pm.name, '') AS "Detail",
                       p.amount AS "Amount", cur.code AS "Currency", p.received_at AS "OccurredAt",
                       CASE WHEN p.customer_id IS NOT NULL THEN '/customers/' || p.customer_id::text ELSE '/finance' END AS "Route",
                       CASE p.direction WHEN 'inbound' THEN 'success' ELSE 'info' END AS "Tone"
                FROM nexora.payment p
                JOIN nexora.currency cur ON cur.id = p.currency_id
                LEFT JOIN nexora.customer cu ON cu.id = p.customer_id
                LEFT JOIN nexora.supplier su ON su.id = p.supplier_id
                LEFT JOIN nexora.payment_method pm ON pm.id = p.payment_method_id
                WHERE p.company_id = {Company} AND p.status = 'completed'
                ORDER BY p.received_at DESC
                LIMIT {take}
                """).ToListAsync(ct));
        }

        if (granted.Contains(Permissions.Purchasing.View))
        {
            rows.AddRange(await db.Database.SqlQuery<Row>($"""
                SELECT 'goods_receipt' AS "Kind", 'Réception ' || gr.document_number AS "Title",
                       su.name || COALESCE(' · commande ' || po.document_number, '') AS "Detail",
                       NULL::numeric AS "Amount", NULL::text AS "Currency", gr.received_at AS "OccurredAt",
                       '/purchasing' AS "Route", 'info' AS "Tone"
                FROM nexora.goods_receipt gr
                JOIN nexora.supplier su ON su.id = gr.supplier_id
                LEFT JOIN nexora.purchase_order po ON po.id = gr.purchase_order_id
                WHERE gr.company_id = {Company} AND gr.status = 'posted'
                ORDER BY gr.received_at DESC
                LIMIT {take}
                """).ToListAsync(ct));
        }

        if (granted.Contains(Permissions.Pos.ManageSessions))
        {
            // Écart de caisse = signal de risque opérationnel : ton « warning » dès qu'il n'est pas nul.
            rows.AddRange(await db.Database.SqlQuery<Row>($"""
                SELECT 'cash_session' AS "Kind", 'Clôture de caisse · ' || r.name AS "Title",
                       COALESCE(u.full_name, u.email::text)
                       || CASE WHEN COALESCE(cs.difference, 0) <> 0
                               THEN ' · écart ' || trim(to_char(cs.difference, 'FMS999G999G990D00'))
                               ELSE ' · sans écart' END AS "Detail",
                       cs.counted_balance AS "Amount", NULL::text AS "Currency", cs.closed_at AS "OccurredAt",
                       '/pos' AS "Route",
                       CASE WHEN COALESCE(cs.difference, 0) <> 0 THEN 'warning' ELSE 'success' END AS "Tone"
                FROM nexora.cash_session cs
                JOIN nexora.cash_register r ON r.id = cs.cash_register_id
                JOIN nexora.app_user u ON u.id = cs.user_id
                WHERE cs.company_id = {Company} AND cs.closed_at IS NOT NULL
                ORDER BY cs.closed_at DESC
                LIMIT {take}
                """).ToListAsync(ct));
        }

        if (granted.Contains(Permissions.Accounting.View))
        {
            rows.AddRange(await db.Database.SqlQuery<Row>($"""
                SELECT 'journal_entry' AS "Kind", 'Écriture manuelle ' || je.entry_number AS "Title",
                       COALESCE(je.description, 'Sans libellé') AS "Detail",
                       NULL::numeric AS "Amount", NULL::text AS "Currency", je.posted_at AS "OccurredAt",
                       '/accounting' AS "Route", 'info' AS "Tone"
                FROM nexora.journal_entry je
                WHERE je.company_id = {Company} AND je.origin = 'manual' AND je.status = 'posted' AND je.posted_at IS NOT NULL
                ORDER BY je.posted_at DESC
                LIMIT {take}
                """).ToListAsync(ct));
        }

        if (granted.Contains(Permissions.Intelligence.View))
        {
            rows.AddRange(await db.Database.SqlQuery<Row>($"""
                SELECT 'recommendation' AS "Kind",
                       CASE r.status WHEN 'accepted' THEN 'Recommandation acceptée' WHEN 'modified' THEN 'Recommandation ajustée'
                                     WHEN 'dismissed' THEN 'Recommandation ignorée' WHEN 'resolved' THEN 'Recommandation exécutée'
                                     ELSE 'Recommandation reportée' END AS "Title",
                       r.title || COALESCE(' · ' || u.full_name, '') AS "Detail",
                       NULL::numeric AS "Amount", NULL::text AS "Currency", r.decided_at AS "OccurredAt",
                       '/intelligence' AS "Route",
                       CASE WHEN r.status IN ('accepted', 'modified', 'resolved') THEN 'success' ELSE 'info' END AS "Tone"
                FROM nexora.recommendation r
                LEFT JOIN nexora.app_user u ON u.id = r.decided_by
                WHERE r.company_id = {Company} AND r.decided_at IS NOT NULL
                  AND r.status IN ('accepted', 'modified', 'dismissed', 'resolved', 'snoozed')
                ORDER BY r.decided_at DESC
                LIMIT {take}
                """).ToListAsync(ct));
        }

        return rows
            .OrderByDescending(r => r.OccurredAt)
            .Take(take)
            .Select(r => new ActivityEventDto(r.Kind, r.Title, r.Detail, r.Amount, r.Currency, r.OccurredAt, r.Route, r.Tone))
            .ToList();
    }
}
