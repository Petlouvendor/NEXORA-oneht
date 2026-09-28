using Microsoft.EntityFrameworkCore;
using Nexora.Application.Abstractions;
using Nexora.Application.Sales;
using Nexora.Contracts.Sales;
using Nexora.Infrastructure.Persistence;

namespace Nexora.Infrastructure.Sales;

/// <summary>
/// Moyens d'encaissement de la caisse (phase 5) : chaque moyen actif avec ses comptes compatibles — caisse
/// pour les espèces, banque / mobile money pour les autres — et le compte proposé par défaut
/// (<c>payment_method.settings.settlementAccountId</c>, sinon le seul compte compatible de la devise).
/// Aucun solde n'est renvoyé.
/// </summary>
public sealed class SqlTenderOptionsQuery(NexoraDbContext db, ITenantContext tenant) : ITenderOptionsQuery
{
    private Guid Company => SalesHelpers.Company(tenant);

    private sealed record MethodRow(Guid Id, string Name, string Type, string? Currency, bool RequiresReference, string? SettlementAccountId);

    public async Task<IReadOnlyList<TenderMethodOptionDto>> ListAsync(string currency, CancellationToken ct = default)
    {
        var methods = await db.Database.SqlQuery<MethodRow>($"""
            SELECT pm.id AS "Id", pm.name AS "Name", pm.type AS "Type", cur.code AS "Currency",
                   pm.requires_reference AS "RequiresReference", pm.settings->>'settlementAccountId' AS "SettlementAccountId"
            FROM nexora.payment_method pm LEFT JOIN nexora.currency cur ON cur.id = pm.currency_id
            WHERE pm.company_id = {Company} AND pm.status = 'active'
              AND pm.type NOT IN ('credit', 'store_credit')
              AND (pm.currency_id IS NULL OR cur.code = {currency})
            ORDER BY CASE pm.type WHEN 'cash' THEN 0 WHEN 'card' THEN 1 WHEN 'mobile_money' THEN 2 ELSE 3 END, pm.name
            """).ToListAsync(ct);
        var accounts = await db.Database.SqlQuery<TenderAccountOptionDto>($"""
            SELECT fa.id AS "Id", fa.name AS "Name", fa.account_type AS "AccountType"
            FROM nexora.financial_account fa JOIN nexora.currency cur ON cur.id = fa.currency_id
            WHERE fa.company_id = {Company} AND fa.status = 'active' AND cur.code = {currency}
            ORDER BY fa.name
            """).ToListAsync(ct);

        return methods.Select(m =>
        {
            var compatible = accounts.Where(a => m.Type switch
            {
                "cash" => a.AccountType == "cash",
                "card" or "bank_transfer" or "check" => a.AccountType == "bank",
                "mobile_money" => a.AccountType == "mobile_money",
                _ => a.AccountType != "cash",
            }).ToList();
            // Un mobile money sans compte mobile money dédié peut encaisser sur la banque.
            if (compatible.Count == 0 && m.Type == "mobile_money")
                compatible = accounts.Where(a => a.AccountType != "cash").ToList();
            Guid? preferred = Guid.TryParse(m.SettlementAccountId, out var s) && compatible.Any(a => a.Id == s) ? s
                : compatible.Count == 1 ? compatible[0].Id : null;
            return new TenderMethodOptionDto(m.Id, m.Name, m.Type, m.RequiresReference, compatible, preferred);
        }).ToList();
    }
}
