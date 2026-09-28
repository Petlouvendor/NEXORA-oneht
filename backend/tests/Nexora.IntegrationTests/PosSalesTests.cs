using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Nexora.Application.Catalog;
using Nexora.Application.Inventory;
using Nexora.Application.Purchasing;
using Nexora.Application.Sales;
using Nexora.Infrastructure.Persistence;

namespace Nexora.IntegrationTests;

/// <summary>Scénarios de validation Ventes & POS (Phase 6 §42).</summary>
[Collection("finance")]
public sealed class PosSalesTests(FinanceEngineFixture fx)
{
    private static readonly Guid Company = Guid.Parse("dce00000-0000-0000-0000-0000000000c1");
    private static readonly Guid Officine = Guid.Parse("dce00000-0000-0000-0000-00000000a003");
    private static readonly Guid Supplier1 = Guid.Parse("dce00000-0000-0000-0000-00000000b101");
    private static readonly Guid BoxUnit = Guid.Parse("dce00000-0000-0000-0000-00000000a011");
    private static readonly Guid Customer = Guid.Parse("dce00000-0000-0000-0000-00000000b001");
    private static readonly Guid CashAccount = Guid.Parse("dce00000-0000-0000-0000-00000000a051");
    private static readonly Guid CardAccount = Guid.Parse("dce00000-0000-0000-0000-00000000a052");
    private static readonly Guid CashRegister = Guid.Parse("dce00000-0000-0000-0000-00000000a061");
    private static readonly Guid CashMethod = Guid.Parse("dce00000-0000-0000-0000-00000000a081");
    private static readonly Guid CardMethod = Guid.Parse("dce00000-0000-0000-0000-00000000a082");

    private AsyncServiceScope Scope() => fx.Provider.CreateAsyncScope();

    // Échéance future : un crédit échu bloque désormais toute nouvelle vente à crédit du client (module clients §12).
    private static DateOnly Due => DateOnly.FromDateTime(DateTime.UtcNow).AddDays(30);

    private async Task<Guid> NewCustomer(decimal creditLimit)
    {
        var id = Guid.NewGuid();
        await using var c = new Npgsql.NpgsqlConnection(fx.TestConnectionString);
        await c.OpenAsync();
        await using var cmd = new Npgsql.NpgsqlCommand($"""
            INSERT INTO nexora.customer (id, company_id, customer_number, name, credit_limit, default_currency_id)
            VALUES ('{id}', '{Company}', 'CLI-{id:N}', 'Client test', {creditLimit.ToString(System.Globalization.CultureInfo.InvariantCulture)},
                    (SELECT id FROM nexora.currency WHERE code='HTG'))
            """, c);
        await cmd.ExecuteNonQueryAsync();
        return id;
    }

    /// <summary>Crée un produit avec prix de vente et stock initial.</summary>
    private async Task<Guid> Stocked(string suffix, decimal salePrice, decimal cost, decimal qty, Guid? taxId = null)
    {
        await using var s = Scope();
        var catalog = s.ServiceProvider.GetRequiredService<CatalogService>();
        var id = (await catalog.CreateProductAsync(new CreateProductCommand(
            $"POS-{suffix}", $"Article {suffix}", null, BoxUnit, "stockable", null, null, null, taxId, false, false))).Value;
        await catalog.SetPriceAsync(new SetProductPriceCommand(id, "sale", salePrice, "HTG", 0m, null, null));
        await s.ServiceProvider.GetRequiredService<PurchasingService>().ReceiveAsync(new ReceiveGoodsCommand(
            Guid.NewGuid(), Supplier1, Officine, null, [new ReceiveLine(id, null, qty, cost, null, null)], null, null));
        return id;
    }

    private static CheckoutCommand Checkout(Guid product, decimal qty, IReadOnlyList<TenderInput> tenders,
        Guid? customer = null, decimal tendered = 0m, DateOnly? due = null, decimal lineDiscount = 0m, string? coupon = null,
        string? overrideReason = null, Guid? session = null)
        => new(Guid.NewGuid(), Officine, null, null, session, customer, "HTG",
            [new CheckoutLine(product, qty, null, lineDiscount, null)], tenders, tendered, due, coupon, overrideReason, null);

    // ---- §42 : vente simple, plusieurs produits, taxe ----------------
    [Fact]
    public async Task Cash_sale_decrements_stock_moves_cash_and_posts_balanced_entry()
    {
        var p = await Stocked("A", salePrice: 100m, cost: 60m, qty: 50m);
        await using var s = Scope();
        var db = s.ServiceProvider.GetRequiredService<NexoraDbContext>();
        var cashBefore = await db.FinancialAccounts.AsNoTracking().Where(a => a.Id == CashAccount).Select(a => a.CurrentBalance).FirstAsync();

        var r = await s.ServiceProvider.GetRequiredService<SalesService>().CheckoutAsync(
            Checkout(p, 3m, [new TenderInput(CashAccount, CashMethod, 300m, null)]));
        r.IsSuccess.Should().BeTrue(r.Error?.Message);
        r.Value!.Total.Should().Be(300m);
        r.Value.SaleType.Should().Be("cash");

        (await s.ServiceProvider.GetRequiredService<IStockLedger>().OnHandAsync(p, Officine)).Should().Be(47m);
        var cashAfter = await db.FinancialAccounts.AsNoTracking().Where(a => a.Id == CashAccount).Select(a => a.CurrentBalance).FirstAsync();
        (cashAfter - cashBefore).Should().Be(300m);

        var lines = await db.JournalEntryLines.AsNoTracking().Where(l => l.JournalEntryId == r.Value.JournalEntryId).ToListAsync();
        lines.Sum(l => l.BaseDebit).Should().Be(lines.Sum(l => l.BaseCredit));
    }

    // ---- §42 : vente avec taxe -----------------------------------
    [Fact]
    public async Task Sale_with_tax_records_tax_amount()
    {
        Guid taxId;
        await using (var s = Scope())
        {
            var db = s.ServiceProvider.GetRequiredService<NexoraDbContext>();
            taxId = Guid.NewGuid();
            await db.Database.ExecuteSqlAsync($"""
                INSERT INTO nexora.tax (id, company_id, name, code, rate, type)
                VALUES ({taxId}, {Company}, 'TCA 10%', 'TCA10-{taxId:N}', 10, 'vat')
                """);
        }
        var p = await Stocked("B", 100m, 55m, 20m, taxId);

        await using var scope = Scope();
        var r = await scope.ServiceProvider.GetRequiredService<SalesService>().CheckoutAsync(
            Checkout(p, 2m, [new TenderInput(CashAccount, CashMethod, 220m, null)]));
        r.IsSuccess.Should().BeTrue(r.Error?.Message);
        r.Value!.TaxTotal.Should().Be(20m);   // 200 net * 10%
        r.Value.Total.Should().Be(220m);
    }

    // ---- §42 : vente avec remise ligne --------------------------
    [Fact]
    public async Task Sale_with_line_discount_reduces_total()
    {
        var p = await Stocked("C", 100m, 50m, 20m);
        await using var s = Scope();
        var r = await s.ServiceProvider.GetRequiredService<SalesService>().CheckoutAsync(
            Checkout(p, 5m, [new TenderInput(CashAccount, CashMethod, 450m, null)], lineDiscount: 50m));
        r.IsSuccess.Should().BeTrue(r.Error?.Message);
        r.Value!.DiscountTotal.Should().Be(50m);
        r.Value.Total.Should().Be(450m);   // 500 - 50
    }

    // ---- §42 : paiement multiple + rendu monnaie ---------------
    [Fact]
    public async Task Multi_tender_sale_with_change()
    {
        var p = await Stocked("D", 100m, 40m, 20m);
        await using var s = Scope();
        // Phase 5 : une carte payée en HTG s'encaisse sur un compte bancaire en HTG (le compte Unibank de la
        // démo est en USD — ce test l'utilisait à tort).
        var cardHtg = Guid.NewGuid();
        await s.ServiceProvider.GetRequiredService<NexoraDbContext>().Database.ExecuteSqlAsync($"""
            INSERT INTO nexora.financial_account (id, company_id, account_type, name, currency_id, ledger_account_id)
            SELECT {cardHtg}, company_id, 'bank', 'Banque HTG (test carte)', (SELECT id FROM nexora.currency WHERE code = 'HTG'), ledger_account_id
            FROM nexora.financial_account WHERE id = {CardAccount}
            """);
        var r = await s.ServiceProvider.GetRequiredService<SalesService>().CheckoutAsync(
            new CheckoutCommand(Guid.NewGuid(), Officine, null, null, null, null, "HTG",
                [new CheckoutLine(p, 10m, null, 0m, null)],
                [new TenderInput(cardHtg, CardMethod, 600m, "AUTH-1"), new TenderInput(CashAccount, CashMethod, 500m, null)],
                1100m, null, null, null, null));
        r.IsSuccess.Should().BeTrue(r.Error?.Message);
        r.Value!.Total.Should().Be(1000m);
        r.Value.ChangeGiven.Should().Be(100m);      // tendered 1100 - total 1000
        r.Value.AmountPaid.Should().Be(1000m);

        var db = s.ServiceProvider.GetRequiredService<NexoraDbContext>();
        var lines = await db.JournalEntryLines.AsNoTracking().Where(l => l.JournalEntryId == r.Value.JournalEntryId).ToListAsync();
        lines.Sum(l => l.BaseDebit).Should().Be(lines.Sum(l => l.BaseCredit));
    }

    // ---- §42 : vente à crédit sans / avec paiement initial ----
    [Fact]
    public async Task Credit_sale_creates_receivable_with_optional_initial_payment()
    {
        var p = await Stocked("E", 100m, 40m, 50m);
        var customer = await NewCustomer(50_000m);

        Guid recvId;
        await using (var s = Scope())
        {
            var r = await s.ServiceProvider.GetRequiredService<SalesService>().CheckoutAsync(
                Checkout(p, 10m, [], customer, due: Due));
            r.IsSuccess.Should().BeTrue(r.Error?.Message);
            r.Value!.SaleType.Should().Be("credit");
            r.Value.CreditAmount.Should().Be(1000m);
            r.Value.ReceivableId.Should().NotBeNull();
            recvId = r.Value.ReceivableId!.Value;
        }
        await using (var s = Scope())
        {
            var db = s.ServiceProvider.GetRequiredService<NexoraDbContext>();
            var rec = await db.Receivables.AsNoTracking().FirstAsync(x => x.Id == recvId);
            rec.OriginalAmount.Should().Be(1000m);
            rec.Status.Should().Be("OPEN");
        }

        await using (var s = Scope())
        {
            var mixed = await s.ServiceProvider.GetRequiredService<SalesService>().CheckoutAsync(
                Checkout(p, 10m, [new TenderInput(CashAccount, CashMethod, 400m, null)], customer, due: Due));
            mixed.IsSuccess.Should().BeTrue(mixed.Error?.Message);
            mixed.Value!.SaleType.Should().Be("mixed");
            mixed.Value.CreditAmount.Should().Be(600m);
        }
    }

    // ---- §42 : dépassement de limite + autorisation ----------
    [Fact]
    public async Task Credit_over_limit_needs_override()
    {
        var p = await Stocked("F", 100_000m, 10_000m, 20m);
        var customer = await NewCustomer(500_000m);
        await using var s = Scope();
        var sales = s.ServiceProvider.GetRequiredService<SalesService>();

        var refused = await sales.CheckoutAsync(Checkout(p, 10m, [], customer, due: Due));
        refused.IsSuccess.Should().BeFalse();
        refused.Error!.Code.Should().Be("pos.credit_limit");   // 1M > limite 500k

        var authorized = await sales.CheckoutAsync(Checkout(p, 10m, [], customer,
            due: Due, overrideReason: "Client stratégique — accord direction"));
        authorized.IsSuccess.Should().BeTrue(authorized.Error?.Message);
        authorized.Value!.CreditOverrideApplied.Should().BeTrue();
    }

    // ---- §42 : idempotence de synchronisation (doublon offline) --
    [Fact]
    public async Task Duplicate_checkout_operation_is_not_double_posted()
    {
        var p = await Stocked("G", 100m, 40m, 20m);
        var cmd = Checkout(p, 2m, [new TenderInput(CashAccount, CashMethod, 200m, null)]);

        Guid saleId;
        await using (var s = Scope())
            saleId = (await s.ServiceProvider.GetRequiredService<SalesService>().CheckoutAsync(cmd)).Value!.SaleId;
        await using (var s = Scope())
        {
            var replay = await s.ServiceProvider.GetRequiredService<SalesService>().CheckoutAsync(cmd);
            replay.Value!.SaleId.Should().Be(saleId);
        }
        await using var check = Scope();
        var db = check.ServiceProvider.GetRequiredService<NexoraDbContext>();
        (await db.Sales.CountAsync(x => x.SourceOperationUuid == cmd.OperationUuid)).Should().Be(1);
        (await db.StockMovements.CountAsync(x => x.SourceOperationUuid == cmd.OperationUuid)).Should().Be(1);
    }

    // ---- §42 : retour partiel + remboursement --------------
    [Fact]
    public async Task Partial_return_restocks_and_posts_correction_entry()
    {
        var p = await Stocked("H", 100m, 45m, 30m);
        Guid saleId; Guid saleLineId;
        await using (var s = Scope())
        {
            var r = await s.ServiceProvider.GetRequiredService<SalesService>().CheckoutAsync(
                Checkout(p, 10m, [new TenderInput(CashAccount, CashMethod, 1000m, null)]));
            saleId = r.Value!.SaleId;
            var db = s.ServiceProvider.GetRequiredService<NexoraDbContext>();
            saleLineId = await db.SaleLines.AsNoTracking().Where(l => l.SaleId == saleId).Select(l => l.Id).FirstAsync();
        }

        await using (var s = Scope())
        {
            var ret = await s.ServiceProvider.GetRequiredService<SalesReturnService>().ReturnAsync(new ReturnCommand(
                Guid.NewGuid(), saleId, Officine, null, "Article défectueux", "cash", CashAccount,
                [new ReturnLineInput(saleLineId, 4m, true)]));
            ret.IsSuccess.Should().BeTrue(ret.Error?.Message);
            ret.Value!.RefundTotal.Should().Be(400m);

            var db = s.ServiceProvider.GetRequiredService<NexoraDbContext>();
            (await s.ServiceProvider.GetRequiredService<IStockLedger>().OnHandAsync(p, Officine)).Should().Be(24m); // 30 - 10 + 4
            var lines = await db.JournalEntryLines.AsNoTracking().Where(l => l.JournalEntryId == ret.Value.JournalEntryId).ToListAsync();
            lines.Sum(l => l.BaseDebit).Should().Be(lines.Sum(l => l.BaseCredit));
            (await db.Sales.AsNoTracking().Where(x => x.Id == saleId).Select(x => x.Status).FirstAsync())
                .Should().Be("partially_refunded");
        }
    }

    // ---- §42 : ouverture et clôture de caisse + écart -----
    [Fact]
    public async Task Session_open_sell_close_with_variance()
    {
        var p = await Stocked("I", 100m, 40m, 20m);
        Guid sessionId;
        await using (var s = Scope())
        {
            var open = await s.ServiceProvider.GetRequiredService<CashSessionService>()
                .OpenAsync(new OpenSessionCommand(CashRegister, null, 5_000m));
            open.IsSuccess.Should().BeTrue(open.Error?.Message);
            sessionId = open.Value;
        }
        await using (var s = Scope())
        {
            var sale = await s.ServiceProvider.GetRequiredService<SalesService>().CheckoutAsync(
                Checkout(p, 3m, [new TenderInput(CashAccount, CashMethod, 300m, null)], session: sessionId));
            sale.IsSuccess.Should().BeTrue(sale.Error?.Message);
        }
        await using (var s = Scope())
        {
            var close = await s.ServiceProvider.GetRequiredService<CashSessionService>()
                .CloseAsync(new CloseSessionCommand(sessionId, 5_280m, "Écart de -20"));
            close.IsSuccess.Should().BeTrue(close.Error?.Message);
            close.Value!.ExpectedBalance.Should().Be(5_300m);   // 5000 + 300 sale
            close.Value.Difference.Should().Be(-20m);
            close.Value.SaleCount.Should().Be(1);
        }
    }

    // ---- §42 : deux caisses vendant simultanément le même stock --
    [Fact]
    public async Task Two_registers_selling_same_stock_never_oversell()
    {
        var p = await Stocked("J", 100m, 40m, 10m);   // 10 en stock

        await using var s1 = Scope();
        await using var s2 = Scope();
        var r1 = await s1.ServiceProvider.GetRequiredService<SalesService>().CheckoutAsync(
            Checkout(p, 7m, [new TenderInput(CashAccount, CashMethod, 700m, null)]));
        var r2 = await s2.ServiceProvider.GetRequiredService<SalesService>().CheckoutAsync(
            Checkout(p, 7m, [new TenderInput(CashAccount, CashMethod, 700m, null)]));

        new[] { r1.IsSuccess, r2.IsSuccess }.Count(x => x).Should().Be(1);   // une seule des deux passe
        (await s1.ServiceProvider.GetRequiredService<IStockLedger>().OnHandAsync(p, Officine)).Should().Be(3m);
    }

    // =====================================================================================
    // Recette Phase 55 (POS-006, FIN-014 — défaut critique corrigé) : le client ne peut plus
    // imposer un prix ou une remise que ses droits et la politique de l'entreprise n'autorisent pas.
    // =====================================================================================

    private static readonly Guid Cashier = Guid.Parse("dce00000-0000-0000-0000-0000000000d3");

    private static CheckoutCommand Priced(Guid product, decimal qty, decimal? price, decimal discount, decimal tendered)
        => new(Guid.NewGuid(), Officine, null, null, null, null, "HTG",
            [new CheckoutLine(product, qty, price, discount, null)],
            [new TenderInput(CashAccount, CashMethod, tendered, null)], tendered, null, null, null, null);

    [Fact]
    public async Task A_cashier_cannot_force_a_price_but_can_discount_within_the_cap()
    {
        var p = await Stocked("G1", 100m, 50m, 20m);
        await using var s = Scope();
        ((TestTenantContext)s.ServiceProvider.GetRequiredService<Nexora.Application.Abstractions.ITenantContext>()).SetUser(Cashier);
        var sales = s.ServiceProvider.GetRequiredService<SalesService>();

        (await sales.CheckoutAsync(Priced(p, 1m, 0.01m, 0m, 0.01m))).Error!.Code
            .Should().Be("sale.price_override_forbidden", "un caissier n'a pas pos.change_price");

        // Guide fonctionnel §8, §12 : la limite du caissier est celle de son rôle (5 %), sous le plafond
        // d'entreprise ; au-delà, c'est une remise exceptionnelle soumise à validation Manager.
        var withinCap = await sales.CheckoutAsync(Priced(p, 1m, null, 5m, 95m));
        withinCap.IsSuccess.Should().BeTrue(withinCap.Error?.Message);
        (await sales.CheckoutAsync(Priced(p, 1m, null, 10m, 90m))).Error!.Code
            .Should().Be("sale.discount_over_limit", "10 % dépasse la limite de 5 % du rôle caissier");
    }

    [Fact]
    public async Task Discounts_beyond_the_configured_cap_or_the_line_amount_are_refused_for_everyone()
    {
        var p = await Stocked("G2", 100m, 50m, 20m);
        await using var s = Scope();
        var sales = s.ServiceProvider.GetRequiredService<SalesService>();   // direction : toutes les permissions

        (await sales.CheckoutAsync(Priced(p, 1m, null, 16m, 84m))).Error!.Code
            .Should().Be("sale.discount_over_limit", "le plafond système pos.max_discount_percent est de 15 %");
        (await sales.CheckoutAsync(Priced(p, 1m, 50m, 60m, 0m))).Error!.Code
            .Should().Be("sale.discount_exceeds_line", "une remise supérieure à la ligne donnerait un total négatif");
        (await sales.CheckoutAsync(Priced(p, 1m, null, -5m, 105m))).Error!.Code.Should().Be("sale.discount_negative");
        (await sales.CheckoutAsync(Priced(p, 0m, null, 0m, 0m))).Error!.Code.Should().Be("sale.quantity");
        (await sales.CheckoutAsync(Priced(p, -2m, null, 0m, 0m))).Error!.Code.Should().Be("sale.quantity");
    }

    [Fact]
    public async Task No_price_can_go_below_the_floor_price_even_with_the_right_permission()
    {
        var p = await Stocked("G3", 100m, 50m, 20m);
        await using (var setup = Scope())
            await setup.ServiceProvider.GetRequiredService<CatalogService>()
                .SetPriceAsync(new SetProductPriceCommand(p, "sale", 100m, "HTG", 0m, null, null, MinPrice: 95m));

        await using var s = Scope();
        var sales = s.ServiceProvider.GetRequiredService<SalesService>();
        (await sales.CheckoutAsync(Priced(p, 1m, 80m, 0m, 80m))).Error!.Code.Should().Be("sale.below_floor_price");
        (await sales.CheckoutAsync(Priced(p, 2m, null, 12m, 188m))).Error!.Code
            .Should().Be("sale.below_floor_price", "94 net par unité < plancher 95, même sous le plafond de remise");

        var ok = await sales.CheckoutAsync(Priced(p, 1m, 96m, 0m, 96m));
        ok.IsSuccess.Should().BeTrue(ok.Error?.Message);
        ok.Value!.Total.Should().Be(96m);
    }
}
