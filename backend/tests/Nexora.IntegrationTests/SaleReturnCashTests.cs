using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Nexora.Application.Catalog;
using Nexora.Application.Purchasing;
using Nexora.Application.Sales;
using Nexora.Infrastructure.Persistence;

namespace Nexora.IntegrationTests;

/// <summary>
/// Audit 2026-10, A-002 et A-005 : un remboursement en espèces ne laissait aucune trace dans la session de caisse —
/// la clôture constatait un faux manquant (imputé au caissier), comptabilisé ensuite comme écart, ce qui faisait
/// baisser la caisse une seconde fois. Les contrôles du retour se faisaient hors transaction et sans verrou.
/// </summary>
[Collection("finance")]
public sealed class SaleReturnCashTests(FinanceEngineFixture fx)
{
    private static readonly Guid Officine = Guid.Parse("dce00000-0000-0000-0000-00000000a003");
    private static readonly Guid Supplier1 = Guid.Parse("dce00000-0000-0000-0000-00000000b101");
    private static readonly Guid BoxUnit = Guid.Parse("dce00000-0000-0000-0000-00000000a011");
    private static readonly Guid CashAccount = Guid.Parse("dce00000-0000-0000-0000-00000000a051");
    private static readonly Guid CashRegister = Guid.Parse("dce00000-0000-0000-0000-00000000a061");
    private static readonly Guid CashMethod = Guid.Parse("dce00000-0000-0000-0000-00000000a081");

    private AsyncServiceScope Scope() => fx.Provider.CreateAsyncScope();

    private async Task<Guid> Stocked(decimal salePrice, decimal qty)
    {
        await using var s = Scope();
        var catalog = s.ServiceProvider.GetRequiredService<CatalogService>();
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var id = (await catalog.CreateProductAsync(new CreateProductCommand(
            $"RET-{suffix}", $"Article retour {suffix}", null, BoxUnit, "stockable", null, null, null, null, false, false))).Value;
        await catalog.SetPriceAsync(new SetProductPriceCommand(id, "sale", salePrice, "HTG", 0m, null, null));
        await s.ServiceProvider.GetRequiredService<PurchasingService>().ReceiveAsync(new ReceiveGoodsCommand(
            Guid.NewGuid(), Supplier1, Officine, null, [new ReceiveLine(id, null, qty, 40m, null, null)], null, null));
        return id;
    }

    private async Task<(Guid SaleId, Guid LineId)> Sell(Guid product, decimal qty, decimal amount, Guid? session = null)
    {
        await using var s = Scope();
        var r = await s.ServiceProvider.GetRequiredService<SalesService>().CheckoutAsync(new CheckoutCommand(
            Guid.NewGuid(), Officine, null, null, session, null, "HTG", [new CheckoutLine(product, qty, null, 0m, null)],
            [new TenderInput(CashAccount, CashMethod, amount, null)], amount, null, null, null, null));
        r.IsSuccess.Should().BeTrue(r.Error?.Message);
        var line = await s.ServiceProvider.GetRequiredService<NexoraDbContext>().SaleLines.AsNoTracking()
            .Where(l => l.SaleId == r.Value!.SaleId).Select(l => l.Id).FirstAsync();
        return (r.Value!.SaleId, line);
    }

    private static ReturnCommand Return(Guid sale, Guid line, decimal qty, string method = "cash", Guid? account = null,
        Guid? session = null, bool restock = true)
        => new(Guid.NewGuid(), sale, Officine, session, "Retour client", method, account,
            [new ReturnLineInput(line, qty, restock)]);

    private async Task<decimal> Balance(Guid account)
    {
        await using var s = Scope();
        return await s.ServiceProvider.GetRequiredService<NexoraDbContext>().Database
            .SqlQuery<decimal>($"SELECT current_balance AS \"Value\" FROM nexora.financial_account WHERE id = {account}").FirstAsync();
    }

    [Fact]
    public async Task Cash_refund_from_the_session_drawer_leaves_no_variance_at_closing()
    {
        var p = await Stocked(100m, 10m);
        var cashBefore = await Balance(CashAccount);
        Guid session;
        await using (var s = Scope())
            session = (await s.ServiceProvider.GetRequiredService<CashSessionService>()
                .OpenAsync(new OpenSessionCommand(CashRegister, null, 1_000m))).Value;

        var (sale, line) = await Sell(p, 3m, 300m, session);
        await using (var s = Scope())
        {
            var ret = await s.ServiceProvider.GetRequiredService<SalesReturnService>()
                .ReturnAsync(Return(sale, line, 3m, account: CashAccount, session: session));
            ret.IsSuccess.Should().BeTrue(ret.Error?.Message);
            ret.Value!.RefundTotal.Should().Be(300m);

            var movement = await s.ServiceProvider.GetRequiredService<NexoraDbContext>().CashMovements.AsNoTracking()
                .Where(m => m.CashSessionId == session && m.MovementType == "sale_refund").ToListAsync();
            movement.Should().ContainSingle().Which.Amount.Should().Be(-300m);
        }

        // Le tiroir contient le fond de caisse : la clôture ne constate aucun écart, rien n'est imputé au caissier.
        await using (var s = Scope())
        {
            var close = await s.ServiceProvider.GetRequiredService<CashSessionService>()
                .CloseAsync(new CloseSessionCommand(session, 1_000m, null));
            close.IsSuccess.Should().BeTrue(close.Error?.Message);
            close.Value!.ExpectedBalance.Should().Be(1_000m);
            close.Value.Difference.Should().Be(0m);
        }

        // Trésorerie : +300 à la vente, −300 au remboursement, une seule fois.
        (await Balance(CashAccount)).Should().Be(cashBefore);
    }

    [Fact]
    public async Task Cash_refund_needs_an_account_and_an_open_session()
    {
        var p = await Stocked(100m, 10m);
        var (sale, line) = await Sell(p, 2m, 200m);
        await using var s = Scope();
        var returns = s.ServiceProvider.GetRequiredService<SalesReturnService>();

        (await returns.ReturnAsync(Return(sale, line, 1m))).Error!.Code.Should().Be("pos.refund_account_required");

        // Session close : on ne rembourse plus depuis son tiroir.
        var sessions = s.ServiceProvider.GetRequiredService<CashSessionService>();
        var closed = (await sessions.OpenAsync(new OpenSessionCommand(CashRegister, null, 0m))).Value;
        (await sessions.CloseAsync(new CloseSessionCommand(closed, 0m, null))).IsSuccess.Should().BeTrue();
        (await returns.ReturnAsync(Return(sale, line, 1m, account: CashAccount, session: closed)))
            .Error!.Code.Should().Be("pos.cash_session_closed");
    }

    [Fact]
    public async Task Two_simultaneous_returns_cannot_exceed_the_quantity_sold()
    {
        var p = await Stocked(100m, 20m);
        var (sale, line) = await Sell(p, 10m, 1_000m);

        var start = new TaskCompletionSource();
        async Task<bool> ReturnSix()
        {
            await start.Task;
            await using var s = Scope();
            return (await s.ServiceProvider.GetRequiredService<SalesReturnService>()
                .ReturnAsync(Return(sale, line, 6m, account: CashAccount))).IsSuccess;
        }
        var tasks = new[] { ReturnSix(), ReturnSix() };
        start.SetResult();
        var results = await Task.WhenAll(tasks);

        results.Count(ok => ok).Should().Be(1, "6 + 6 dépasserait les 10 vendus");
        await using var check = Scope();
        (await check.ServiceProvider.GetRequiredService<NexoraDbContext>().SaleReturns.AsNoTracking()
            .Where(r => r.SaleId == sale).SelectMany(r => r.Lines).SumAsync(l => l.Quantity)).Should().Be(6m);
    }

    [Fact]
    public async Task A_return_without_restocking_is_not_flagged_as_restocked()
    {
        var p = await Stocked(100m, 10m);
        var (sale, line) = await Sell(p, 2m, 200m);
        await using var s = Scope();
        var ret = await s.ServiceProvider.GetRequiredService<SalesReturnService>()
            .ReturnAsync(Return(sale, line, 1m, account: CashAccount, restock: false));
        ret.IsSuccess.Should().BeTrue(ret.Error?.Message);
        (await s.ServiceProvider.GetRequiredService<NexoraDbContext>().SaleReturns.AsNoTracking()
            .Where(r => r.Id == ret.Value!.SaleReturnId).Select(r => r.Restocked).FirstAsync()).Should().BeFalse();
    }
}
