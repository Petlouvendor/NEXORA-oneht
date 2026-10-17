using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Nexora.Application.Abstractions;
using Nexora.Application.Catalog;
using Nexora.Application.Finance;
using Nexora.Application.Purchasing;
using Nexora.Application.Sales;
using Nexora.Infrastructure.Persistence;
using Npgsql;

namespace Nexora.IntegrationTests;

/// <summary>
/// Audit 2026-10 éd. 2, A-052 (CRITIQUE, constaté sur les données de recette) : le coût d'une vente en dollars, déjà
/// exprimé en gourdes (valorisation du stock), était présenté au moteur en dollars puis converti au taux du jour.
/// Deux ventes de la Pharmacie ont ainsi passé 40 500 HTG de coût pour 300 HTG de marchandises : le compte de stock du
/// grand livre s'écartait de la valorisation du stock. Même défaut à la réintégration d'un retour.
/// </summary>
[Collection("finance")]
public sealed class ForeignCurrencyCostOfGoodsTests(FinanceEngineFixture fx)
{
    private static readonly Guid Officine = Guid.Parse("dce00000-0000-0000-0000-00000000a003");
    private static readonly Guid Supplier1 = Guid.Parse("dce00000-0000-0000-0000-00000000b101");
    private static readonly Guid BoxUnit = Guid.Parse("dce00000-0000-0000-0000-00000000a011");
    private static readonly Guid UsdBank = Guid.Parse("dce00000-0000-0000-0000-00000000a052");

    private AsyncServiceScope Scope() => fx.Provider.CreateAsyncScope();

    /// <summary>Article vendu 2 USD, reçu au coût de 40 HTG l'unité.</summary>
    private async Task<Guid> DollarPricedProductAsync(decimal qty)
    {
        await using var s = Scope();
        var catalog = s.ServiceProvider.GetRequiredService<CatalogService>();
        var id = (await catalog.CreateProductAsync(new CreateProductCommand(
            $"USD-{Guid.NewGuid():N}"[..20], "Article vendu en dollars", null, BoxUnit, "stockable", null, null, null, null, false, false))).Value;
        await catalog.SetPriceAsync(new SetProductPriceCommand(id, "sale", 2m, "USD", 0m, null, null));
        await s.ServiceProvider.GetRequiredService<PurchasingService>().ReceiveAsync(new ReceiveGoodsCommand(
            Guid.NewGuid(), Supplier1, Officine, null, [new ReceiveLine(id, null, qty, 40m, null, null)], null, null));
        return id;
    }

    /// <summary>Montant de base (HTG) passé sur le compte <paramref name="code"/> par les écritures de la pièce.</summary>
    private async Task<(decimal Debit, decimal Credit)> BaseOnAccountAsync(Guid sourceId, string code)
    {
        await using var s = Scope();
        var rows = await s.ServiceProvider.GetRequiredService<NexoraDbContext>().Database.SqlQuery<DebitCredit>($"""
            SELECT COALESCE(SUM(l.base_debit), 0) AS "Debit", COALESCE(SUM(l.base_credit), 0) AS "Credit"
            FROM nexora.journal_entry e
            JOIN nexora.journal_entry_line l ON l.journal_entry_id = e.id
            JOIN nexora.account a ON a.id = l.account_id
            WHERE e.source_id = {sourceId} AND a.code = {code}
            """).ToListAsync();
        return (rows[0].Debit, rows[0].Credit);
    }

    private sealed record DebitCredit(decimal Debit, decimal Credit);

    [Fact]
    public async Task A_dollar_sale_and_its_return_move_the_stock_account_by_the_cost_in_gourdes()
    {
        var product = await DollarPricedProductAsync(5m);

        Guid saleId, lineId;
        await using (var s = Scope())
        {
            var sale = await s.ServiceProvider.GetRequiredService<SalesService>().CheckoutAsync(new CheckoutCommand(
                Guid.NewGuid(), Officine, null, null, null, null, "USD", [new CheckoutLine(product, 3m, null, 0m, null)],
                [new TenderInput(UsdBank, null, 6m, "CB-USD")], 6m, null, null, null, null));
            sale.IsSuccess.Should().BeTrue(sale.Error?.Message);
            saleId = sale.Value!.SaleId;
            lineId = await s.ServiceProvider.GetRequiredService<NexoraDbContext>().SaleLines.AsNoTracking()
                .Where(l => l.SaleId == saleId).Select(l => l.Id).SingleAsync();
        }

        // 3 unités à 40 HTG : 120 HTG de coût, et non 120 × taux du dollar.
        (await BaseOnAccountAsync(saleId, "6031")).Should().Be((120m, 0m));
        (await BaseOnAccountAsync(saleId, "3110")).Should().Be((0m, 120m));

        Guid returnId;
        await using (var s = Scope())
        {
            var ret = await s.ServiceProvider.GetRequiredService<SalesReturnService>().ReturnAsync(new ReturnCommand(
                Guid.NewGuid(), saleId, Officine, null, "Retour client", "card", UsdBank, [new ReturnLineInput(lineId, 2m, true)]));
            ret.IsSuccess.Should().BeTrue(ret.Error?.Message);
            returnId = ret.Value!.SaleReturnId;
        }

        (await BaseOnAccountAsync(returnId, "3110")).Should().Be((80m, 0m), "2 unités réintégrées à 40 HTG");
        (await BaseOnAccountAsync(returnId, "6031")).Should().Be((0m, 80m));
    }

    /// <summary>
    /// Données passées avant le correctif : la commande <c>correction-cout-devise</c> retire l'excédent par une écriture
    /// corrective, sans toucher à l'origine, et ne corrige qu'une fois. L'écriture faussée est reproduite telle que
    /// l'ancien code la produisait (coût de 300 HTG présenté en USD, converti à 135).
    /// </summary>
    [Fact]
    public async Task The_recovery_command_removes_the_excess_once_without_touching_the_original()
    {
        var company = Guid.NewGuid();
        var entry = Guid.NewGuid();
        await using (var c = new NpgsqlConnection(fx.TestConnectionString))
        {
            await c.OpenAsync();
            await using var cmd = new NpgsqlCommand($"""
                INSERT INTO nexora.company (id, name, default_currency_id)
                VALUES ('{company}', 'Coût en devise {company:N}', (SELECT id FROM nexora.currency WHERE code = 'HTG'));
                SELECT nexora.bootstrap_company_defaults('{company}', (SELECT id FROM nexora.currency WHERE code = 'HTG'), 2026);
                INSERT INTO nexora.journal_entry (id, company_id, journal_id, accounting_period_id, entry_number, entry_date,
                                                  source_type, source_id, description, status, posted_at, origin)
                SELECT '{entry}', '{company}', j.id, p.id, 'VT-A052-1', DATE '2026-03-02', 'sale', gen_random_uuid(),
                       'Vente en USD (ancien calcul)', 'posted', now(), 'system'
                FROM nexora.journal j, nexora.accounting_period p
                WHERE j.company_id = '{company}' AND j.code = 'VT' AND p.company_id = '{company}';
                INSERT INTO nexora.journal_entry_line (journal_entry_id, company_id, account_id, line_number, debit, credit,
                                                       currency_id, base_debit, base_credit)
                SELECT '{entry}', '{company}', m.account_id, x.n, x.d, x.c, (SELECT id FROM nexora.currency WHERE code = 'USD'), x.bd, x.bc
                FROM (VALUES ('cogs', 1, 300, 0, 40500, 0), ('inventory', 2, 0, 300, 0, 40500)) AS x(k, n, d, c, bd, bc)
                JOIN nexora.account_mapping m ON m.company_id = '{company}' AND m.mapping_key = x.k;
                """, c);
            await cmd.ExecuteNonQueryAsync();
        }

        try
        {
            async Task<IReadOnlyList<CostOfGoodsCorrectionLine>> Run(bool apply)
            {
                await using var s = Scope();
                return (await s.ServiceProvider.GetRequiredService<ICostOfGoodsCurrencyRecovery>().RunAsync(apply))
                    .Where(l => l.CompanyId == company).ToList();
            }

            var plan = await Run(apply: false);
            plan.Should().ContainSingle().Which.Should().Match<CostOfGoodsCorrectionLine>(l =>
                l.EntryId == entry && l.Excess == 40_200m && l.Status == "a_corriger");

            var done = await Run(apply: true);
            done.Should().ContainSingle().Which.Status.Should().Be("corrige", done[0].Detail);
            (await Run(apply: true)).Should().BeEmpty("une pièce corrigée n'est plus proposée");

            await using var check = new NpgsqlConnection(fx.TestConnectionString);
            await check.OpenAsync();
            await using var q = new NpgsqlCommand($"""
                SELECT m.mapping_key, SUM(l.base_debit - l.base_credit)
                FROM nexora.journal_entry_line l
                JOIN nexora.account_mapping m ON m.company_id = l.company_id AND m.account_id = l.account_id
                WHERE l.company_id = '{company}' AND m.mapping_key IN ('cogs', 'inventory')
                GROUP BY m.mapping_key ORDER BY 1
                """, check);
            var net = new Dictionary<string, decimal>();
            await using (var rd = await q.ExecuteReaderAsync())
                while (await rd.ReadAsync()) net[rd.GetString(0)] = rd.GetDecimal(1);
            net.Should().Equal(new Dictionary<string, decimal> { ["cogs"] = 300m, ["inventory"] = -300m },
                "après correction, le grand livre porte le vrai coût de 300 HTG");
        }
        finally
        {
            await using var c = new NpgsqlConnection(fx.TestConnectionString);
            await c.OpenAsync();
            await using var cmd = new NpgsqlCommand($"UPDATE nexora.company SET status = 'archived' WHERE id = '{company}'", c);
            await cmd.ExecuteNonQueryAsync();
        }
    }
}
