using Microsoft.EntityFrameworkCore;
using Nexora.Infrastructure.Persistence;

namespace Nexora.Infrastructure.Catalog;

/// <summary>
/// Liste tarifaire applicable à un client (audit 2026-10, A-013) : la liste ACTIVE rattachée au groupe du client, dans
/// la devise de la vente (ou sans devise), la plus haute priorité gagnant. Partagée par l'encaissement, l'aperçu du
/// panier, les commandes, les devis et le simulateur de prix : avant, les listes n'étaient jamais appliquées à une
/// vente réelle et le simulateur pouvait annoncer un prix que la caisse ne facturait pas.
/// </summary>
public static class PriceListResolver
{
    public static async Task<Guid?> ForCustomerGroupAsync(
        NexoraDbContext db, Guid companyId, Guid? customerGroupId, Guid currencyId, CancellationToken ct)
    {
        if (customerGroupId is null) return null;
        return await db.Database.SqlQuery<Guid?>($"""
            SELECT id AS "Value" FROM nexora.price_list
            WHERE company_id = {companyId} AND status = 'active' AND customer_group_id = {customerGroupId}
              AND (currency_id IS NULL OR currency_id = {currencyId})
            ORDER BY priority DESC, created_at
            LIMIT 1
            """).FirstOrDefaultAsync(ct);
    }
}
