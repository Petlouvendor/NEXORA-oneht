using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Nexora.Application.Abstractions;
using Nexora.Application.Catalog;
using Nexora.Application.Finance;
using Nexora.Application.Finance.Reports;
using Nexora.Application.Purchasing;
using Nexora.Application.Sales;
using Nexora.Contracts.Finance;
using Nexora.Infrastructure.Persistence;
using Npgsql;

namespace Nexora.IntegrationTests;

/// <summary>
/// Audit 2026-10 — A-019 : apports, sorties et prélèvements de caisse ne touchaient que la session ; A-022 : balances
/// âgées et prévision ignoraient les avoirs fournisseurs ; A-004 : trésorerie datée de l'instant du traitement, doublon
/// simultané d'une opération en erreur, imputation sans contrôle de devise.
/// </summary>
[Collection("finance")]
public sealed class CashAndLedgerConsistencyTests(FinanceEngineFixture fx)
{
    private static readonly Guid Company = Guid.Parse("dce00000-0000-0000-0000-0000000000c1");
    private static readonly Guid Direction = Guid.Parse("dce00000-0000-0000-0000-0000000000d1");
    private static readonly Guid Officine = Guid.Parse("dce00000-0000-0000-0000-00000000a003");
    private static readonly Guid Supplier1 = Guid.Parse("dce00000-0000-0000-0000-00000000b101");
    private static readonly Guid BoxUnit = Guid.Parse("dce00000-0000-0000-0000-00000000a011");
    private static readonly Guid CashAccount = Guid.Parse("dce00000-0000-0000-0000-00000000a051");
    private static readonly Guid UsdBank = Guid.Parse("dce00000-0000-0000-0000-00000000a052");

    private AsyncServiceScope Scope()
    {
        var s = fx.Provider.CreateAsyncScope();
        var tc = (TestTenantContext)s.ServiceProvider.GetRequiredService<ITenantContext>();
        tc.SetUser(Direction);
        tc.SetScope([Company], [], Company);
        return s;
    }

    private async Task<T> Scalar<T>(string sql)
    {
        await using var c = new NpgsqlConnection(fx.TestConnectionString);
        await c.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, c);
        return (T)(await cmd.ExecuteScalarAsync())!;
    }

    private async Task<decimal> Balance(Guid account) =>
        await Scalar<decimal>($"SELECT current_balance FROM nexora.financial_account WHERE id = '{account}'");

    [Fact]
    public async Task A_cash_pickup_moves_treasury_and_ledger_and_closes_without_variance()
    {
        // Caisse propre au test : le compte « caisse » partagé par les autres tests peut être fortement négatif, et le
        // transfert refuse à juste titre de sortir plus que le solde d'une caisse.
        Guid safe, drawer, session;
        var op = Guid.NewGuid();
        var register = Guid.NewGuid();
        await using (var s = Scope())
        {
            var t = s.ServiceProvider.GetRequiredService<ITreasuryManagementService>();
            safe = (await t.CreateAccountAsync(new SaveFinancialAccountRequest($"Coffre {Guid.NewGuid():N}"[..30], "cash", "HTG", null, null))).Value;
            drawer = (await t.CreateAccountAsync(new SaveFinancialAccountRequest($"Tiroir {Guid.NewGuid():N}"[..30], "cash", "HTG", null, null))).Value;
        }
        await using (var c = new NpgsqlConnection(fx.TestConnectionString))
        {
            await c.OpenAsync();
            await using var cmd = new NpgsqlCommand($"""
                INSERT INTO nexora.cash_register (id, company_id, financial_account_id, name, code, currency_id)
                VALUES ('{register}', '{Company}', '{drawer}', 'Caisse test {register:N}', 'T{register:N}'::text,
                        (SELECT id FROM nexora.currency WHERE code = 'HTG'))
                """, c);
            await cmd.ExecuteNonQueryAsync();
        }
        await using (var s = Scope())
            session = (await s.ServiceProvider.GetRequiredService<CashSessionService>()
                .OpenAsync(new OpenSessionCommand(register, null, 1_000m))).Value;
        // La caisse partagée par les tests peut être vide : une vente espèces de 300 dans la session l'alimente.
        await using (var s = Scope())
        {
            var catalog = s.ServiceProvider.GetRequiredService<CatalogService>();
            var suffix = Guid.NewGuid().ToString("N")[..8];
            var product = (await catalog.CreateProductAsync(new CreateProductCommand(
                $"PCK-{suffix}", $"Article prélèvement {suffix}", null, BoxUnit, "stockable", null, null, null, null, false, false))).Value;
            await catalog.SetPriceAsync(new SetProductPriceCommand(product, "sale", 100m, "HTG", 0m, null, null));
            await s.ServiceProvider.GetRequiredService<PurchasingService>().ReceiveAsync(new ReceiveGoodsCommand(
                Guid.NewGuid(), Supplier1, Officine, null, [new ReceiveLine(product, null, 3m, 40m, null, null)], null, null));
            var sale = await s.ServiceProvider.GetRequiredService<SalesService>().CheckoutAsync(new CheckoutCommand(
                Guid.NewGuid(), Officine, null, null, session, null, "HTG", [new CheckoutLine(product, 3m, null, 0m, null)],
                [new TenderInput(drawer, Guid.Parse("dce00000-0000-0000-0000-00000000a081"), 300m, null)], 300m, null, null, null, null));
            sale.IsSuccess.Should().BeTrue(sale.Error?.Message);
        }
        var cashBefore = await Balance(drawer);

        await using (var s = Scope())
        {
            var sessions = s.ServiceProvider.GetRequiredService<CashSessionService>();
            (await sessions.CashInOutAsync(new CashInOutCommand(session, "pickup", 300m, "Mise au coffre")))
                .Error!.Code.Should().Be("pos.counterpart_required", "l'argent doit aller quelque part");
            var pickup = await sessions.CashInOutAsync(new CashInOutCommand(session, "pickup", 300m, "Mise au coffre", safe, op));
            pickup.IsSuccess.Should().BeTrue(pickup.Error?.Message);
            (await sessions.CashInOutAsync(new CashInOutCommand(session, "pickup", 300m, "Mise au coffre", safe, op)))
                .IsSuccess.Should().BeTrue("rejeu de la même opération : rien n'est doublé");
        }

        (await Balance(drawer)).Should().Be(cashBefore - 300m);
        (await Balance(safe)).Should().Be(300m);
        (await Scalar<long>($"SELECT COUNT(*) FROM nexora.cash_movement WHERE cash_session_id = '{session}' AND movement_type = 'pickup'"))
            .Should().Be(1);
        (await Scalar<long>($"SELECT COUNT(*) FROM nexora.journal_entry WHERE source_operation_uuid = '{op}' AND status = 'posted'"))
            .Should().Be(1, "le transfert est comptabilisé, une seule fois");

        await using (var s = Scope())
        {
            // Fond 1 000 + vente 300 − prélèvement 300.
            var close = await s.ServiceProvider.GetRequiredService<CashSessionService>().CloseAsync(new CloseSessionCommand(session, 1_000m, null));
            close.IsSuccess.Should().BeTrue(close.Error?.Message);
            close.Value!.Difference.Should().Be(0m, "le prélèvement est dans l'attendu");

            // Fin de test : l'argent retourne en caisse, le coffre est clôturé.
            var t = s.ServiceProvider.GetRequiredService<ITreasuryManagementService>();
            (await t.TransferAsync(new TreasuryTransferRequest(Guid.NewGuid(), safe, drawer, 300m, null))).IsSuccess.Should().BeTrue();
            (await t.SetAccountStatusAsync(safe, new FinancialAccountStatusRequest("closed", "Fin de test"))).IsSuccess.Should().BeTrue();
            (await t.TransferAsync(new TreasuryTransferRequest(Guid.NewGuid(), drawer, CashAccount, await Balance(drawer), null))).IsSuccess.Should().BeTrue();
            (await t.SetAccountStatusAsync(drawer, new FinancialAccountStatusRequest("closed", "Fin de test"))).IsSuccess.Should().BeTrue();
        }
    }

    [Fact]
    public async Task Aged_payables_are_net_of_supplier_credits()
    {
        var supplier = Guid.NewGuid();
        await using (var c = new NpgsqlConnection(fx.TestConnectionString))
        {
            await c.OpenAsync();
            await using var cmd = new NpgsqlCommand($"""
                INSERT INTO nexora.supplier (id, company_id, supplier_number, name, default_currency_id)
                VALUES ('{supplier}', '{Company}', 'FRN-{supplier:N}', 'Fournisseur avoir {supplier:N}', (SELECT id FROM nexora.currency WHERE code = 'HTG'));
                INSERT INTO nexora.supplier_payable (company_id, supplier_id, currency_id, original_amount, paid_amount, credited_amount, issue_date, due_date, status)
                VALUES ('{Company}', '{supplier}', (SELECT id FROM nexora.currency WHERE code = 'HTG'), 1000, 0, 200, current_date - 40, current_date - 10, 'OPEN');
                """, c);
            await cmd.ExecuteNonQueryAsync();
        }

        await using var s = Scope();
        var aged = await s.ServiceProvider.GetRequiredService<IFinancialReports>().AgedPayablesAsync(TestDates.Today);
        aged.Rows.Single(r => r.PartyName.StartsWith($"Fournisseur avoir {supplier:N}")).Total
            .Should().Be(800m, "1 000 facturés − 200 d'avoir (l'ancien calcul donnait 1 000)");
    }

    [Fact]
    public async Task Treasury_is_dated_like_the_operation_and_a_parallel_duplicate_is_replayed()
    {
        // Paiement fournisseur daté d'hier (période ouverte) : la transaction de trésorerie porte cette date.
        var payable = Guid.NewGuid();
        await using (var c = new NpgsqlConnection(fx.TestConnectionString))
        {
            await c.OpenAsync();
            await using var cmd = new NpgsqlCommand($"""
                INSERT INTO nexora.supplier_payable (id, company_id, supplier_id, currency_id, original_amount, paid_amount, issue_date, due_date, status)
                VALUES ('{payable}', '{Company}', '{Supplier1}', (SELECT id FROM nexora.currency WHERE code = 'HTG'), 500, 0, current_date, current_date + 30, 'OPEN');
                """, c);
            await cmd.ExecuteNonQueryAsync();
        }
        var at = DateTimeOffset.UtcNow.AddDays(-1);
        var op = Guid.NewGuid();
        var request = new RecordPaymentRequest(op, at, Supplier1, 50m, "HTG", CashAccount,
            [new AllocationInput(payable, 50m, IsReceivable: false)], "A-004");

        var start = new TaskCompletionSource();
        async Task<string> Pay()
        {
            await start.Task;
            await using var s = Scope();
            var r = await s.ServiceProvider.GetRequiredService<IFinanceOperations>().RecordSupplierPaymentAsync(request);
            return r.IsSuccess ? "ok" : $"{r.Error!.Code} : {r.Error.Message}";
        }
        var both = new[] { Pay(), Pay() };
        start.SetResult();
        (await Task.WhenAll(both)).Should().AllBeEquivalentTo("ok", "le doublon simultané est rejoué, pas en erreur");

        (await Scalar<long>($"SELECT COUNT(*) FROM nexora.journal_entry WHERE source_operation_uuid = '{op}'")).Should().Be(1);
        var occurred = await Scalar<DateTime>($"SELECT occurred_at FROM nexora.financial_transaction WHERE source_operation_uuid = '{op}'");
        occurred.Should().BeCloseTo(at.UtcDateTime, TimeSpan.FromSeconds(1), "trésorerie datée de l'opération");

        // Imputation dans une autre devise que la dette : refusée.
        await using var check = Scope();
        (await check.ServiceProvider.GetRequiredService<IFinanceOperations>().RecordSupplierPaymentAsync(new RecordPaymentRequest(
                Guid.NewGuid(), DateTimeOffset.UtcNow, Supplier1, 5m, "USD", UsdBank,
                [new AllocationInput(payable, 5m, IsReceivable: false)], null)))
            .IsSuccess.Should().BeFalse("une dette en HTG ne se solde pas avec un paiement en USD");
    }

    /// <summary>Audit 2026-10, A-023 : un compte était toujours créé à zéro, sans moyen d'enregistrer l'argent déjà présent.</summary>
    [Fact]
    public async Task An_opening_balance_enters_treasury_and_the_ledger_once_and_only_before_any_movement()
    {
        await using var s = Scope();
        var t = s.ServiceProvider.GetRequiredService<ITreasuryManagementService>();
        var account = (await t.CreateAccountAsync(new SaveFinancialAccountRequest($"Ouverture {Guid.NewGuid():N}"[..30], "cash", "HTG", null, null))).Value;

        var op = Guid.NewGuid();
        var r = await t.RecordOpeningBalanceAsync(account, new TreasuryOpeningBalanceRequest(op, 2_500m, null, "Fond trouvé à la mise en service"));
        r.IsSuccess.Should().BeTrue(r.Error?.Message);
        (await t.RecordOpeningBalanceAsync(account, new TreasuryOpeningBalanceRequest(op, 2_500m))).Value!.WasReplay.Should().BeTrue();

        (await Balance(account)).Should().Be(2_500m);
        var lines = await s.ServiceProvider.GetRequiredService<NexoraDbContext>().JournalEntryLines.AsNoTracking()
            .Where(l => l.JournalEntryId == r.Value!.JournalEntryId).ToListAsync();
        lines.Sum(l => l.BaseDebit).Should().Be(2_500m).And.Be(lines.Sum(l => l.BaseCredit));

        // Une seule fois, avant toute opération ; jamais négatif sur une caisse.
        (await t.RecordOpeningBalanceAsync(account, new TreasuryOpeningBalanceRequest(Guid.NewGuid(), 100m)))
            .Error!.Code.Should().Be("treasury.opening_after_movements");
        var other = (await t.CreateAccountAsync(new SaveFinancialAccountRequest($"Ouverture {Guid.NewGuid():N}"[..30], "cash", "HTG", null, null))).Value;
        (await t.RecordOpeningBalanceAsync(other, new TreasuryOpeningBalanceRequest(Guid.NewGuid(), -50m)))
            .Error!.Code.Should().Be("treasury.amount");

        // Fin de test : comptes vidés et clôturés (agrégats de trésorerie des autres tests intacts).
        (await t.TransferAsync(new TreasuryTransferRequest(Guid.NewGuid(), account, CashAccount, 2_500m, null))).IsSuccess.Should().BeTrue();
        (await t.SetAccountStatusAsync(account, new FinancialAccountStatusRequest("closed", "Fin de test"))).IsSuccess.Should().BeTrue();
        (await t.SetAccountStatusAsync(other, new FinancialAccountStatusRequest("closed", "Fin de test"))).IsSuccess.Should().BeTrue();
    }
}
