using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Nexora.Application.Abstractions;
using Nexora.Application.Catalog;
using Nexora.Application.Purchasing;
using Nexora.Application.Sales;
using Nexora.Infrastructure.Persistence;

namespace Nexora.IntegrationTests;

/// <summary>
/// Finalisation produit, phase 5 — encaissement en plusieurs moyens à la caisse : chaque encaissement est
/// contrôlé (compte, devise, moyen, référence), les espèces vont dans la caisse de la session, la monnaie
/// n'est rendue que sur les espèces, et seul le liquide entre dans l'attendu du tiroir.
/// </summary>
[Collection("finance")]
public sealed class PosTenderTests(FinanceEngineFixture fx)
{
    private static readonly Guid Company = Guid.Parse("dce00000-0000-0000-0000-0000000000c1");
    private static readonly Guid Direction = Guid.Parse("dce00000-0000-0000-0000-0000000000d1");
    private static readonly Guid Officine = Guid.Parse("dce00000-0000-0000-0000-00000000a003");
    private static readonly Guid Supplier1 = Guid.Parse("dce00000-0000-0000-0000-00000000b101");
    private static readonly Guid BoxUnit = Guid.Parse("dce00000-0000-0000-0000-00000000a011");
    private static readonly Guid CashAccount = Guid.Parse("dce00000-0000-0000-0000-00000000a051");
    private static readonly Guid UsdBank = Guid.Parse("dce00000-0000-0000-0000-00000000a052");
    private static readonly Guid MobileAccount = Guid.Parse("dce00000-0000-0000-0000-00000000a053");
    private static readonly Guid CashMethod = Guid.Parse("dce00000-0000-0000-0000-00000000a081");
    private static readonly Guid CardMethod = Guid.Parse("dce00000-0000-0000-0000-00000000a082");

    private AsyncServiceScope Scope()
    {
        var s = fx.Provider.CreateAsyncScope();
        var tc = (TestTenantContext)s.ServiceProvider.GetRequiredService<ITenantContext>();
        tc.SetUser(Direction);
        tc.SetScope([Company], [], Company);
        return s;
    }

    private static string U() => Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();

    private static async Task<Guid> Stocked(IServiceProvider sp, decimal price)
    {
        var catalog = sp.GetRequiredService<CatalogService>();
        var id = (await catalog.CreateProductAsync(new CreateProductCommand(
            $"TND-{U()}", "Article caisse", null, BoxUnit, "stockable", null, null, null, null, false, false))).Value;
        await catalog.SetPriceAsync(new SetProductPriceCommand(id, "sale", price, "HTG", 0m, null, null));
        (await sp.GetRequiredService<PurchasingService>().ReceiveAsync(new ReceiveGoodsCommand(
            Guid.NewGuid(), Supplier1, Officine, null, [new ReceiveLine(id, null, 50m, price / 2m, null, null)], null, null)))
            .IsSuccess.Should().BeTrue();
        return id;
    }

    /// <summary>Compte bancaire HTG (la banque de la démo est en USD) et moyen mobile money exigeant une référence.</summary>
    private static async Task<(Guid BankHtg, Guid MobileMethod)> HtgAccounts(NexoraDbContext db)
    {
        var bank = Guid.NewGuid();
        var mobile = Guid.NewGuid();
        await db.Database.ExecuteSqlAsync($"""
            INSERT INTO nexora.financial_account (id, company_id, account_type, name, currency_id, ledger_account_id)
            SELECT {bank}, company_id, 'bank', {"Banque HTG " + U()}, (SELECT id FROM nexora.currency WHERE code = 'HTG'), ledger_account_id
            FROM nexora.financial_account WHERE id = {UsdBank}
            """);
        await db.Database.ExecuteSqlAsync($"""
            INSERT INTO nexora.payment_method (id, company_id, name, type, currency_id, requires_reference)
            VALUES ({mobile}, {Company}, {"MonCash " + U()}, 'mobile_money', (SELECT id FROM nexora.currency WHERE code = 'HTG'), true)
            """);
        return (bank, mobile);
    }

    private static CheckoutCommand Sale(Guid product, decimal qty, IReadOnlyList<TenderInput> tenders, Guid? session = null)
        => new(Guid.NewGuid(), Officine, null, null, session, null, "HTG", [new CheckoutLine(product, qty, null, 0m, null)],
            tenders, tenders.Sum(t => t.Amount), null, null, null, null);

    [Fact]
    public async Task Tenders_are_checked_account_currency_method_reference_and_cash_goes_to_a_cash_account()
    {
        await using var s = Scope();
        var sales = s.ServiceProvider.GetRequiredService<SalesService>();
        var (bank, mobile) = await HtgAccounts(s.ServiceProvider.GetRequiredService<NexoraDbContext>());
        var p = await Stocked(s.ServiceProvider, 100m);

        (await sales.CheckoutAsync(Sale(p, 1m, [new TenderInput(Guid.NewGuid(), CashMethod, 100m, null)])))
            .Error!.Code.Should().Be("pos.tender_account");
        (await sales.CheckoutAsync(Sale(p, 1m, [new TenderInput(UsdBank, CardMethod, 100m, "A1")])))
            .Error!.Code.Should().Be("pos.tender_currency");
        (await sales.CheckoutAsync(Sale(p, 1m, [new TenderInput(bank, CardMethod, 100m, " ")])))
            .Error!.Code.Should().Be("pos.tender_reference", "la carte exige un n° d'autorisation");
        (await sales.CheckoutAsync(Sale(p, 1m, [new TenderInput(CashAccount, CardMethod, 100m, "A1")])))
            .Error!.Code.Should().Be("pos.tender_account_type", "une carte ne s'encaisse pas dans le tiroir");
        (await sales.CheckoutAsync(Sale(p, 1m, [new TenderInput(bank, CashMethod, 100m, null)])))
            .Error!.Code.Should().Be("pos.tender_account_type", "des espèces ne vont pas sur la banque");
        (await sales.CheckoutAsync(Sale(p, 1m, [new TenderInput(MobileAccount, mobile, 100m, null)])))
            .Error!.Code.Should().Be("pos.tender_reference");
        (await sales.CheckoutAsync(Sale(p, 1m, [new TenderInput(CashAccount, CashMethod, -5m, null)])))
            .Error!.Code.Should().Be("pos.tender_amount");
    }

    [Fact]
    public async Task Only_cash_gives_change_and_a_card_can_never_exceed_what_is_due()
    {
        await using var s = Scope();
        var sales = s.ServiceProvider.GetRequiredService<SalesService>();
        var (bank, mobile) = await HtgAccounts(s.ServiceProvider.GetRequiredService<NexoraDbContext>());
        var p = await Stocked(s.ServiceProvider, 100m);

        // 1 000 dus : 1 200 par carte → refus (rendre 200 « sur la carte » n'existe pas).
        (await sales.CheckoutAsync(Sale(p, 10m, [new TenderInput(bank, CardMethod, 1200m, "A1")])))
            .Error!.Code.Should().Be("pos.non_cash_overpayment");

        // Espèces en premier puis carte : la monnaie est rendue sur les espèces, jamais déduite de la carte.
        var r = await sales.CheckoutAsync(Sale(p, 10m,
        [
            new TenderInput(CashAccount, CashMethod, 500m, null),
            new TenderInput(bank, CardMethod, 400m, "AUTH-9"),
            new TenderInput(MobileAccount, mobile, 200m, "MC-123"),
        ]));
        r.IsSuccess.Should().BeTrue(r.Error?.Message);
        r.Value!.ChangeGiven.Should().Be(100m);
        r.Value.AmountPaid.Should().Be(1000m);

        var payments = await s.ServiceProvider.GetRequiredService<NexoraDbContext>().Database.SqlQuery<PaymentRow>($"""
            SELECT p.financial_account_id AS "Account", p.amount AS "Amount", p.reference AS "Reference"
            FROM nexora.sale_payment sp JOIN nexora.payment p ON p.id = sp.payment_id
            WHERE sp.sale_id = {r.Value.SaleId}
            """).ToListAsync();
        payments.Should().BeEquivalentTo(new[]
        {
            new PaymentRow(CashAccount, 400m, null),
            new PaymentRow(bank, 400m, "AUTH-9"),
            new PaymentRow(MobileAccount, 200m, "MC-123"),
        });
    }

    [Fact]
    public async Task Only_cash_enters_the_drawer_of_the_session()
    {
        Guid register = Guid.NewGuid(), session;
        await using (var s = Scope())
        {
            await s.ServiceProvider.GetRequiredService<NexoraDbContext>().Database.ExecuteSqlAsync($"""
                INSERT INTO nexora.cash_register (id, company_id, financial_account_id, name, code, currency_id)
                VALUES ({register}, {Company}, {CashAccount}, 'Caisse multi-paiement', {"MP" + register.ToString("N")[..6]},
                        (SELECT id FROM nexora.currency WHERE code = 'HTG'))
                """);
            session = (await s.ServiceProvider.GetRequiredService<CashSessionService>().OpenAsync(new OpenSessionCommand(register, null, 0m))).Value;
        }

        await using var sc = Scope();
        var db = sc.ServiceProvider.GetRequiredService<NexoraDbContext>();
        var (bank, _) = await HtgAccounts(db);
        var p = await Stocked(sc.ServiceProvider, 100m);
        var sales = sc.ServiceProvider.GetRequiredService<SalesService>();

        // Espèces sur un autre compte caisse que celui de la session : refus.
        var otherCash = Guid.NewGuid();
        await db.Database.ExecuteSqlAsync($"""
            INSERT INTO nexora.financial_account (id, company_id, account_type, name, currency_id, ledger_account_id)
            SELECT {otherCash}, company_id, 'cash', {"Autre caisse " + U()}, currency_id, ledger_account_id
            FROM nexora.financial_account WHERE id = {CashAccount}
            """);
        (await sales.CheckoutAsync(Sale(p, 1m, [new TenderInput(otherCash, CashMethod, 100m, null)], session)))
            .Error!.Code.Should().Be("pos.cash_account_mismatch");

        var r = await sales.CheckoutAsync(Sale(p, 5m,
            [new TenderInput(bank, CardMethod, 300m, "AUTH-1"), new TenderInput(CashAccount, CashMethod, 250m, null)], session));
        r.IsSuccess.Should().BeTrue(r.Error?.Message);
        r.Value!.ChangeGiven.Should().Be(50m);

        var drawer = await db.Database.SqlQuery<decimal>($"""
            SELECT COALESCE(SUM(amount), 0) AS "Value" FROM nexora.cash_movement
            WHERE cash_session_id = {session} AND movement_type = 'sale_payment'
            """).FirstAsync();
        drawer.Should().Be(200m, "seules les espèces nettes (250 − 50 rendus) entrent dans le tiroir, pas la carte");
    }

    public sealed record PaymentRow(Guid Account, decimal Amount, string? Reference);
}
