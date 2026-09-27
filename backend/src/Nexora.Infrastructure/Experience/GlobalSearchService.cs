using Microsoft.EntityFrameworkCore;
using Nexora.Application.Abstractions;
using Nexora.Application.Experience;
using Nexora.Application.Security;
using Nexora.Contracts.Experience;
using Nexora.Infrastructure.Persistence;

namespace Nexora.Infrastructure.Experience;

/// <summary>
/// Recherche globale Ctrl+K (NEXORA ONE §5). Une requête bornée par type d'objet, insensible à la casse
/// et aux accents (<c>nexora.search_norm</c>, Phase 23). Chaque groupe est conditionné par la permission
/// qui ouvre l'écran correspondant — exactement celle du menu — pour ne jamais révéler une donnée que
/// l'utilisateur ne pourrait pas consulter. Filtre explicite sur l'entreprise active : la RLS reste la
/// seconde barrière.
/// </summary>
public sealed class GlobalSearchService(
    NexoraDbContext db, ITenantContext tenant, IPermissionService permissions) : IGlobalSearchService
{
    private Guid Company => tenant.ActiveCompanyId ?? throw new InvalidOperationException("Entreprise active requise.");

    private sealed record Row(Guid Id, string Title, string? Subtitle, string? Key);

    private static readonly Dictionary<string, string> DocumentLabels = new()
    {
        ["quote"] = "Devis", ["proforma"] = "Proforma", ["invoice"] = "Facture", ["delivery_note"] = "Bon de livraison",
        ["purchase_order"] = "Bon de commande", ["credit_note"] = "Avoir", ["debit_note"] = "Note de débit",
        ["customer_statement"] = "Relevé client", ["supplier_statement"] = "Relevé fournisseur", ["receipt"] = "Reçu",
        ["other"] = "Document",
    };

    public async Task<GlobalSearchResponse> SearchAsync(string query, int limitPerGroup, CancellationToken ct = default)
    {
        var q = (query ?? "").Trim();
        if (q.Length < 2) return new GlobalSearchResponse(q, []);
        if (q.Length > 100) q = q[..100];
        var take = Math.Clamp(limitPerGroup, 1, 10);
        var granted = await permissions.CurrentPermissionsAsync(tenant, ct);
        var groups = new List<SearchGroupDto>();

        if (granted.Contains(Permissions.Catalog.View))
        {
            // Stock disponible affiché seulement à qui voit le stock (§5 : « Riz 25kg → Produit → Stock »).
            var withStock = granted.Contains(Permissions.Inventory.View);
            var rows = await db.Database.SqlQuery<Row>($"""
                SELECT p.id AS "Id", p.name AS "Title",
                       p.sku || COALESCE(' · ' || p.barcode, '')
                       || CASE WHEN {withStock} THEN ' · stock ' || trim_scale(COALESCE(
                              (SELECT SUM(b.quantity_on_hand - b.quantity_reserved) FROM nexora.stock_balance b
                               WHERE b.company_id = p.company_id AND b.product_id = p.id), 0)::numeric)::text
                               ELSE '' END AS "Subtitle",
                       p.sku AS "Key"
                FROM nexora.product p
                CROSS JOIN LATERAL (SELECT nexora.search_norm({q}) AS n) s
                WHERE p.company_id = {Company} AND p.status <> 'archived'
                  AND (nexora.search_norm(p.name) LIKE '%' || s.n || '%'
                       OR nexora.search_norm(p.sku) LIKE '%' || s.n || '%'
                       OR p.barcode = {q})
                ORDER BY (nexora.search_norm(p.name) LIKE s.n || '%') DESC, length(p.name), p.name
                LIMIT {take}
                """).ToListAsync(ct);
            Add(groups, "product", "Produits", rows, r => $"/catalog?q={Uri.EscapeDataString(r.Key ?? r.Title)}");
        }

        if (granted.Contains(Permissions.Customers.View))
        {
            var rows = await db.Database.SqlQuery<Row>($"""
                SELECT c.id AS "Id", trim(COALESCE(c.first_name || ' ', '') || c.name) AS "Title",
                       c.customer_number || COALESCE(' · ' || c.phone, '') AS "Subtitle", NULL::text AS "Key"
                FROM nexora.customer c
                CROSS JOIN LATERAL (SELECT nexora.search_norm({q}) AS n) s
                WHERE c.company_id = {Company} AND c.status <> 'archived'
                  AND (nexora.search_norm(COALESCE(c.first_name, '') || ' ' || c.name) LIKE '%' || s.n || '%'
                       OR nexora.search_norm(c.customer_number) LIKE '%' || s.n || '%'
                       OR c.phone LIKE '%' || {q} || '%')
                ORDER BY c.name
                LIMIT {take}
                """).ToListAsync(ct);
            Add(groups, "customer", "Clients", rows, r => $"/customers/{r.Id}");
        }

        // Même règle que l'écran Fournisseurs (PartiesPage) : suppliers.view ET purchasing.view.
        if (granted.Contains(Permissions.Suppliers.View) && granted.Contains(Permissions.Purchasing.View))
        {
            var rows = await db.Database.SqlQuery<Row>($"""
                SELECT su.id AS "Id", su.name AS "Title",
                       su.supplier_number || COALESCE(' · ' || su.phone, '') AS "Subtitle", su.name AS "Key"
                FROM nexora.supplier su
                CROSS JOIN LATERAL (SELECT nexora.search_norm({q}) AS n) s
                WHERE su.company_id = {Company} AND su.status <> 'archived'
                  AND (nexora.search_norm(su.name) LIKE '%' || s.n || '%'
                       OR nexora.search_norm(su.supplier_number) LIKE '%' || s.n || '%')
                ORDER BY su.name
                LIMIT {take}
                """).ToListAsync(ct);
            Add(groups, "supplier", "Fournisseurs", rows, r => $"/suppliers?q={Uri.EscapeDataString(r.Key ?? r.Title)}");
        }

        if (granted.Contains(Permissions.Hr.ViewEmployees))
        {
            // Jamais de donnée sensible (salaire, banque, identifiants) : nom, matricule, poste.
            var rows = await db.Database.SqlQuery<Row>($"""
                SELECT e.id AS "Id", e.first_name || ' ' || e.last_name AS "Title",
                       e.employee_number || COALESCE(' · ' || e.job_title, '') AS "Subtitle", NULL::text AS "Key"
                FROM nexora.employee e
                CROSS JOIN LATERAL (SELECT nexora.search_norm({q}) AS n) s
                WHERE e.company_id = {Company}
                  AND (nexora.search_norm(e.first_name || ' ' || e.last_name) LIKE '%' || s.n || '%'
                       OR nexora.search_norm(e.last_name || ' ' || e.first_name) LIKE '%' || s.n || '%'
                       OR nexora.search_norm(e.employee_number) LIKE '%' || s.n || '%')
                ORDER BY e.last_name, e.first_name
                LIMIT {take}
                """).ToListAsync(ct);
            Add(groups, "employee", "Employés", rows, r => $"/hr/employees/{r.Id}");
        }

        if (granted.Contains(Permissions.Documents.View))
        {
            // Guide §7.2 : sans documents.view_all, seuls les documents émis par l'utilisateur.
            var all = granted.Contains(Permissions.Documents.ViewAll);
            var rows = await db.Database.SqlQuery<Row>($"""
                SELECT d.id AS "Id", d.document_number AS "Title",
                       to_char(d.issue_date, 'DD/MM/YYYY') || ' · ' || trim(to_char(d.total, 'FM999G999G990D00'))
                       || COALESCE(' ' || cur.code, '') AS "Subtitle",
                       d.document_type AS "Key"
                FROM nexora.issued_document d
                LEFT JOIN nexora.currency cur ON cur.id = d.currency_id
                WHERE d.company_id = {Company}
                  AND ({all} OR d.created_by = {tenant.UserId} OR d.issued_by = {tenant.UserId})
                  AND upper(d.document_number) LIKE '%' || upper({q}) || '%'
                ORDER BY d.issue_date DESC, d.document_number DESC
                LIMIT {take}
                """).ToListAsync(ct);
            groups.AddIfAny(new SearchGroupDto("document", "Factures & documents", rows.Select(r =>
                new SearchHitDto("document", r.Id,
                    $"{DocumentLabels.GetValueOrDefault(r.Key ?? "other", "Document")} {r.Title}", r.Subtitle,
                    $"/documents?q={Uri.EscapeDataString(r.Title)}")).ToList()));
        }

        if (granted.Contains(Permissions.Pos.View)
            && (granted.Contains(Permissions.Sales.View) || granted.Contains(Permissions.Reporting.ViewSales)))
        {
            var rows = await db.Database.SqlQuery<Row>($"""
                SELECT s.id AS "Id", 'Vente ' || s.document_number AS "Title",
                       to_char(s.sold_at, 'DD/MM/YYYY HH24:MI') || ' · ' || trim(to_char(s.total, 'FM999G999G990D00'))
                       || ' ' || cur.code || COALESCE(' · ' || cu.name, '') AS "Subtitle", NULL::text AS "Key"
                FROM nexora.sale s
                JOIN nexora.currency cur ON cur.id = s.currency_id
                LEFT JOIN nexora.customer cu ON cu.id = s.customer_id
                WHERE s.company_id = {Company} AND s.status <> 'draft'
                  AND upper(s.document_number) LIKE '%' || upper({q}) || '%'
                ORDER BY s.sold_at DESC
                LIMIT {take}
                """).ToListAsync(ct);
            Add(groups, "sale", "Ventes", rows, r => $"/sales/{r.Id}");
        }

        return new GlobalSearchResponse(q, groups);
    }

    private static void Add(List<SearchGroupDto> groups, string kind, string label, List<Row> rows, Func<Row, string> route)
        => groups.AddIfAny(new SearchGroupDto(kind, label,
            rows.Select(r => new SearchHitDto(kind, r.Id, r.Title, r.Subtitle, route(r))).ToList()));
}

internal static class SearchGroupListExtensions
{
    public static void AddIfAny(this List<SearchGroupDto> groups, SearchGroupDto group)
    {
        if (group.Hits.Count > 0) groups.Add(group);
    }
}
