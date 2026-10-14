using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Nexora.Application.Catalog;
using Nexora.Application.Purchasing;
using Nexora.Application.Sales;

namespace Nexora.IntegrationTests;

/// <summary>
/// Audit 2026-10, A-003 : la date de vente venait du client. Un caissier pouvait antidater une vente et l'ajouter à
/// une session déjà comptée et clôturée. Les points d'accès en ligne ignorent désormais cette date ; une vente hors
/// ligne rejouée la garde, mais jamais dans le futur ni avant l'ouverture de sa session.
/// </summary>
[Collection("finance")]
public sealed class SaleDateControlTests(FinanceEngineFixture fx)
{
    private static readonly Guid Officine = Guid.Parse("dce00000-0000-0000-0000-00000000a003");
    private static readonly Guid Supplier1 = Guid.Parse("dce00000-0000-0000-0000-00000000b101");
    private static readonly Guid BoxUnit = Guid.Parse("dce00000-0000-0000-0000-00000000a011");
    private static readonly Guid CashAccount = Guid.Parse("dce00000-0000-0000-0000-00000000a051");
    private static readonly Guid CashRegister = Guid.Parse("dce00000-0000-0000-0000-00000000a061");
    private static readonly Guid CashMethod = Guid.Parse("dce00000-0000-0000-0000-00000000a081");

    private AsyncServiceScope Scope() => fx.Provider.CreateAsyncScope();

    private async Task<Guid> Stocked()
    {
        await using var s = Scope();
        var catalog = s.ServiceProvider.GetRequiredService<CatalogService>();
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var id = (await catalog.CreateProductAsync(new CreateProductCommand(
            $"DAT-{suffix}", $"Article date {suffix}", null, BoxUnit, "stockable", null, null, null, null, false, false))).Value;
        await catalog.SetPriceAsync(new SetProductPriceCommand(id, "sale", 50m, "HTG", 0m, null, null));
        await s.ServiceProvider.GetRequiredService<PurchasingService>().ReceiveAsync(new ReceiveGoodsCommand(
            Guid.NewGuid(), Supplier1, Officine, null, [new ReceiveLine(id, null, 20m, 20m, null, null)], null, null));
        return id;
    }

    private static CheckoutCommand Sale(Guid product, Guid? session, DateTimeOffset? soldAt)
        => new(Guid.NewGuid(), Officine, null, null, session, null, "HTG", [new CheckoutLine(product, 1m, null, 0m, null)],
            [new TenderInput(CashAccount, CashMethod, 50m, null)], 50m, null, null, null, soldAt);

    [Fact]
    public async Task A_sale_dated_in_the_future_is_refused()
    {
        var p = await Stocked();
        await using var s = Scope();
        (await s.ServiceProvider.GetRequiredService<SalesService>().CheckoutAsync(Sale(p, null, DateTimeOffset.UtcNow.AddHours(2))))
            .Error!.Code.Should().Be("pos.sold_at_future");
    }

    [Fact]
    public async Task A_sale_cannot_precede_its_session_nor_join_a_closed_session_after_closing()
    {
        var p = await Stocked();
        Guid session;
        await using (var s = Scope())
            session = (await s.ServiceProvider.GetRequiredService<CashSessionService>()
                .OpenAsync(new OpenSessionCommand(CashRegister, null, 0m))).Value;

        await using (var s = Scope())
        {
            var sales = s.ServiceProvider.GetRequiredService<SalesService>();
            // Antidatée avant l'ouverture de la session (le cas exécuté pendant l'audit).
            (await sales.CheckoutAsync(Sale(p, session, DateTimeOffset.UtcNow.AddDays(-3))))
                .Error!.Code.Should().Be("pos.sold_at_outside_session");
            (await sales.CheckoutAsync(Sale(p, session, null))).IsSuccess.Should().BeTrue();
        }

        await using (var s = Scope())
            (await s.ServiceProvider.GetRequiredService<CashSessionService>()
                .CloseAsync(new CloseSessionCommand(session, 50m, null))).IsSuccess.Should().BeTrue();

        // Après la clôture, une vente datée par le serveur (canal en ligne) est refusée.
        await using (var s = Scope())
            (await s.ServiceProvider.GetRequiredService<SalesService>().CheckoutAsync(Sale(p, session, null)))
                .Error!.Code.Should().Be("pos.session_closed");
    }
}
