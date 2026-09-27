using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Nexora.Application.Catalog;
using Nexora.Application.Inventory;
using Nexora.Application.Purchasing;
using Nexora.Application.Sales;
using Nexora.Contracts.Sales;
using Nexora.Infrastructure.Persistence;

namespace Nexora.IntegrationTests;

/// <summary>Commandes clients — scénarios de validation Phase 25 §18-20, §45-46.</summary>
[Collection("finance")]
public sealed class SalesOrderTests(FinanceEngineFixture fx)
{
    private static readonly Guid Company = Guid.Parse("dce00000-0000-0000-0000-0000000000c1");
    private static readonly Guid Officine = Guid.Parse("dce00000-0000-0000-0000-00000000a003");
    private static readonly Guid Supplier1 = Guid.Parse("dce00000-0000-0000-0000-00000000b101");
    private static readonly Guid BoxUnit = Guid.Parse("dce00000-0000-0000-0000-00000000a011");
    private static readonly Guid CashAccount = Guid.Parse("dce00000-0000-0000-0000-00000000a051");
    private static readonly Guid CashMethod = Guid.Parse("dce00000-0000-0000-0000-00000000a081");

    private AsyncServiceScope Scope() => fx.Provider.CreateAsyncScope();

    private async Task<Guid> NewCustomer(decimal creditLimit)
    {
        var id = Guid.NewGuid();
        await using var c = new Npgsql.NpgsqlConnection(fx.TestConnectionString);
        await c.OpenAsync();
        await using var cmd = new Npgsql.NpgsqlCommand($"""
            INSERT INTO nexora.customer (id, company_id, customer_number, name, credit_limit, default_currency_id)
            VALUES ('{id}', '{Company}', 'ORD-{id:N}', 'Client commande',
                    {creditLimit.ToString(System.Globalization.CultureInfo.InvariantCulture)},
                    (SELECT id FROM nexora.currency WHERE code='HTG'))
            """, c);
        await cmd.ExecuteNonQueryAsync();
        return id;
    }

    private async Task<Guid> NewProduct(IServiceProvider sp, string suffix, decimal price, decimal cost, decimal stock)
    {
        var catalog = sp.GetRequiredService<CatalogService>();
        var id = (await catalog.CreateProductAsync(new CreateProductCommand(
            $"ORD-{suffix}", $"Article {suffix}", null, BoxUnit, "stockable", null, null, null, null, false, false))).Value;
        await catalog.SetPriceAsync(new SetProductPriceCommand(id, "sale", price, "HTG", 0m, null, null));
        if (stock > 0m)
            (await sp.GetRequiredService<PurchasingService>().ReceiveAsync(new ReceiveGoodsCommand(
                Guid.NewGuid(), Supplier1, Officine, null, [new ReceiveLine(id, null, stock, cost, null, null)], null, null)))
                .IsSuccess.Should().BeTrue();
        return id;
    }

    private static CreateOrderRequest Order(Guid customer, string type, bool onCredit, params (Guid Product, decimal Qty)[] lines)
        => new(customer, "HTG", type, Officine, onCredit, null, onCredit ? new DateOnly(2026, 12, 31) : null, null, null,
            lines.Select(l => new CreateOrderLineInput(l.Product, l.Qty, null, 0m, null)).ToList());

    private static CompleteOrderRequest Complete(decimal cash, bool onCredit = false)
        => new(Guid.NewGuid(), null, onCredit,
            onCredit ? new DateOnly(2026, 12, 31) : null,
            cash > 0m ? [new OrderTenderInput(CashAccount, CashMethod, cash, null)] : [],
            null);

    // ---- §18, §10 : la commande fige le prix des lignes et calcule les totaux ----
    [Fact]
    public async Task Create_order_prices_lines_and_totals()
    {
        await using var s = Scope();
        var orders = s.ServiceProvider.GetRequiredService<OrderService>();
        var q = s.ServiceProvider.GetRequiredService<ISalesQueries>();
        var cust = await NewCustomer(0m);
        var p1 = await NewProduct(s.ServiceProvider, "T1", 100m, 60m, 0m);
        var p2 = await NewProduct(s.ServiceProvider, "T2", 250m, 150m, 0m);

        var r = await orders.CreateOrderAsync(Order(cust, "pickup", false, (p1, 3m), (p2, 2m)));
        r.IsSuccess.Should().BeTrue(r.Error?.Message);

        var dto = await q.GetOrderAsync(r.Value);
        dto!.Status.Should().Be("draft");
        dto.Total.Should().Be(800m);   // 3*100 + 2*250
        dto.Lines.Should().HaveCount(2);
    }

    // ---- §18, §38 : réservation → exécution crée la vente, décrémente le stock ----
    [Fact]
    public async Task Reserve_holds_stock_then_complete_creates_the_sale()
    {
        await using var s = Scope();
        var orders = s.ServiceProvider.GetRequiredService<OrderService>();
        var ledger = s.ServiceProvider.GetRequiredService<IStockLedger>();
        var db = s.ServiceProvider.GetRequiredService<NexoraDbContext>();
        var cust = await NewCustomer(0m);
        var p = await NewProduct(s.ServiceProvider, "RS", 100m, 60m, 50m);

        var oid = (await orders.CreateOrderAsync(Order(cust, "pickup", false, (p, 10m)))).Value;
        (await orders.ConfirmOrderAsync(oid)).IsSuccess.Should().BeTrue();
        (await orders.ReserveOrderAsync(oid)).IsSuccess.Should().BeTrue();

        (await ledger.OnHandAsync(p, Officine)).Should().Be(50m);
        (await ledger.AvailableAsync(p, Officine)).Should().Be(40m);   // 10 réservés

        var done = await orders.CompleteOrderAsync(oid, Complete(1000m));
        done.IsSuccess.Should().BeTrue(done.Error?.Message);

        (await ledger.OnHandAsync(p, Officine)).Should().Be(40m);
        (await ledger.AvailableAsync(p, Officine)).Should().Be(40m);   // réservation levée
        var sale = await db.Sales.AsNoTracking().FirstAsync(x => x.Id == done.Value!.SaleId);
        sale.SalesOrderId.Should().Be(oid);
        var order = await db.SalesOrders.AsNoTracking().FirstAsync(x => x.Id == oid);
        order.Status.Should().Be("fulfilled");
    }

    // ---- §38 : la réservation échoue si le stock disponible est insuffisant ----
    [Fact]
    public async Task Reserve_fails_when_stock_is_insufficient()
    {
        await using var s = Scope();
        var orders = s.ServiceProvider.GetRequiredService<OrderService>();
        var cust = await NewCustomer(0m);
        var p = await NewProduct(s.ServiceProvider, "SH", 100m, 60m, 5m);

        var oid = (await orders.CreateOrderAsync(Order(cust, "pickup", false, (p, 12m)))).Value;
        await orders.ConfirmOrderAsync(oid);
        var res = await orders.ReserveOrderAsync(oid);
        res.IsSuccess.Should().BeFalse();
        res.Error!.Code.Should().Be("order.insufficient_stock");
    }

    // ---- §18 : exécution partielle → commande partiellement livrée ----
    [Fact]
    public async Task Partial_fulfillment_leaves_the_order_partially_fulfilled()
    {
        await using var s = Scope();
        var orders = s.ServiceProvider.GetRequiredService<OrderService>();
        var db = s.ServiceProvider.GetRequiredService<NexoraDbContext>();
        var cust = await NewCustomer(0m);
        var p = await NewProduct(s.ServiceProvider, "PF", 100m, 60m, 50m);

        var oid = (await orders.CreateOrderAsync(Order(cust, "pickup", false, (p, 10m)))).Value;
        await orders.ConfirmOrderAsync(oid);
        var order = await db.SalesOrders.AsNoTracking().Include(o => o.Lines).FirstAsync(o => o.Id == oid);
        var lineId = order.Lines.Single().Id;

        var done = await orders.CompleteOrderAsync(oid, Complete(400m) with
        {
            LineQuantities = new Dictionary<Guid, decimal> { [lineId] = 4m },
        });
        done.IsSuccess.Should().BeTrue(done.Error?.Message);

        var refreshed = await db.SalesOrders.AsNoTracking().Include(o => o.Lines).FirstAsync(o => o.Id == oid);
        refreshed.Status.Should().Be("partially_fulfilled");
        refreshed.Lines.Single().QuantityFulfilled.Should().Be(4m);

        // Le reliquat s'exécute ensuite.
        (await orders.CompleteOrderAsync(oid, Complete(600m))).IsSuccess.Should().BeTrue();
        (await db.SalesOrders.AsNoTracking().FirstAsync(o => o.Id == oid)).Status.Should().Be("fulfilled");
    }

    // ---- §29 : annulation d'une commande réservée libère le stock ----
    [Fact]
    public async Task Cancel_releases_reserved_stock()
    {
        await using var s = Scope();
        var orders = s.ServiceProvider.GetRequiredService<OrderService>();
        var ledger = s.ServiceProvider.GetRequiredService<IStockLedger>();
        var cust = await NewCustomer(0m);
        var p = await NewProduct(s.ServiceProvider, "CX", 100m, 60m, 30m);

        var oid = (await orders.CreateOrderAsync(Order(cust, "pickup", false, (p, 8m)))).Value;
        await orders.ConfirmOrderAsync(oid);
        await orders.ReserveOrderAsync(oid);
        (await ledger.AvailableAsync(p, Officine)).Should().Be(22m);

        (await orders.CancelOrderAsync(oid, "")).IsSuccess.Should().BeFalse();
        (await orders.CancelOrderAsync(oid, "Client s'est désisté")).IsSuccess.Should().BeTrue();
        (await ledger.AvailableAsync(p, Officine)).Should().Be(30m);
    }

    // ---- §18-19 : acompte enregistré comme avance client ----
    [Fact]
    public async Task Deposit_is_recorded_as_a_customer_advance()
    {
        await using var s = Scope();
        var orders = s.ServiceProvider.GetRequiredService<OrderService>();
        var db = s.ServiceProvider.GetRequiredService<NexoraDbContext>();
        var q = s.ServiceProvider.GetRequiredService<ISalesQueries>();
        var cust = await NewCustomer(0m);
        var p = await NewProduct(s.ServiceProvider, "DP", 100m, 60m, 20m);

        var oid = (await orders.CreateOrderAsync(Order(cust, "pickup", false, (p, 5m)))).Value;
        var dep = await orders.RecordDepositAsync(oid, new OrderDepositRequest(Guid.NewGuid(), 200m, CashAccount));
        dep.IsSuccess.Should().BeTrue(dep.Error?.Message);

        (await q.GetOrderAsync(oid))!.DepositAmount.Should().Be(200m);
        (await q.GetOrderAsync(oid))!.RemainingBalance.Should().Be(300m);   // 500 - 200

        var paid = await db.Database.SqlQuery<decimal>($"""
            SELECT COALESCE(SUM(amount),0) AS "Value" FROM nexora.payment
            WHERE company_id = {Company} AND customer_id = {cust} AND direction = 'inbound'
            """).FirstAsync();
        paid.Should().Be(200m);

        // Un acompte supérieur au solde est refusé.
        (await orders.RecordDepositAsync(oid, new OrderDepositRequest(Guid.NewGuid(), 999m, CashAccount)))
            .Error!.Code.Should().Be("order.deposit_exceeds");
    }

    // ---- §19, §39-40 : commande à crédit → la vente crée une créance ----
    [Fact]
    public async Task Credit_order_completion_creates_a_receivable()
    {
        await using var s = Scope();
        var orders = s.ServiceProvider.GetRequiredService<OrderService>();
        var db = s.ServiceProvider.GetRequiredService<NexoraDbContext>();
        var cust = await NewCustomer(100000m);
        var p = await NewProduct(s.ServiceProvider, "CR", 100m, 60m, 30m);

        var oid = (await orders.CreateOrderAsync(Order(cust, "wholesale", true, (p, 10m)))).Value;
        await orders.ConfirmOrderAsync(oid);
        var done = await orders.CompleteOrderAsync(oid, Complete(0m, onCredit: true));
        done.IsSuccess.Should().BeTrue(done.Error?.Message);
        done.Value!.ReceivableId.Should().NotBeNull();

        var owed = await db.Receivables.AsNoTracking()
            .Where(r => r.Id == done.Value.ReceivableId).Select(r => r.OriginalAmount).FirstAsync();
        owed.Should().Be(1000m);
    }

    // ---- §9 : le prix de la ligne est figé à la création — une révision ultérieure n'y touche pas ----
    [Fact]
    public async Task Order_line_price_is_frozen_at_order_time()
    {
        await using var s = Scope();
        var orders = s.ServiceProvider.GetRequiredService<OrderService>();
        var catalog = s.ServiceProvider.GetRequiredService<CatalogService>();
        var db = s.ServiceProvider.GetRequiredService<NexoraDbContext>();
        var cust = await NewCustomer(0m);
        var p = await NewProduct(s.ServiceProvider, "FZ", 100m, 60m, 20m);

        var oid = (await orders.CreateOrderAsync(Order(cust, "pickup", false, (p, 4m)))).Value;
        await orders.ConfirmOrderAsync(oid);

        // Le prix catalogue double après la prise de commande.
        (await catalog.SetPriceAsync(new SetProductPriceCommand(p, "sale", 200m, "HTG", 0m, null, DateTimeOffset.UtcNow)))
            .IsSuccess.Should().BeTrue();

        var done = await orders.CompleteOrderAsync(oid, Complete(400m));
        done.IsSuccess.Should().BeTrue(done.Error?.Message);
        var line = await db.SaleLines.AsNoTracking().FirstAsync(l => l.SaleId == done.Value!.SaleId);
        line.UnitPrice.Should().Be(100m);   // prix de la commande, pas le nouveau prix
    }
}
