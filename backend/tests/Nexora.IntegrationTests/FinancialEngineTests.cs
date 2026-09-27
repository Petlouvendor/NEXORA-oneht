using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Nexora.Application.Finance;
using Nexora.Application.Finance.Abstractions;
using Nexora.Application.Finance.Engine;
using Nexora.Application.Finance.Events;
using Nexora.Application.Finance.Periods;
using Nexora.Application.Finance.Reversals;
using Nexora.Domain.Accounting;
using Nexora.Domain.Common;
using Nexora.Infrastructure.Persistence;

namespace Nexora.IntegrationTests;

/// <summary>Scénarios de validation du moteur financier (Phase 4 §35).</summary>
[Collection("finance")]
public sealed class FinancialEngineTests(FinanceEngineFixture fx)
{
    private static readonly Guid Company = Guid.Parse("dce00000-0000-0000-0000-0000000000c1");
    private static readonly Guid CashHtg = Guid.Parse("dce00000-0000-0000-0000-00000000a051");
    private static readonly Guid BankUsd = Guid.Parse("dce00000-0000-0000-0000-00000000a052");
    private static readonly Guid Customer = Guid.Parse("dce00000-0000-0000-0000-00000000b001");
    private static readonly Guid Supplier = Guid.Parse("dce00000-0000-0000-0000-00000000b101");

    private static DateTimeOffset On(int m, int d) => new(2026, m, d, 10, 0, 0, TimeSpan.Zero);

    private AsyncServiceScope Scope() => fx.Provider.CreateAsyncScope();

    private async Task<Guid> NewSale()
    {
        var id = Guid.NewGuid();
        await Sql($"""
            INSERT INTO nexora.sale (id, company_id, document_number, status, sale_type, currency_id, total)
            VALUES ('{id}', '{Company}', 'IT-{id:N}', 'completed', 'credit',
                    (SELECT id FROM nexora.currency WHERE code='HTG'), 0);
            """);
        return id;
    }

    private async Task<Guid> NewSupplierInvoice()
    {
        var id = Guid.NewGuid();
        await Sql($"""
            INSERT INTO nexora.supplier_invoice (id, company_id, supplier_id, document_number, currency_id, status, total)
            VALUES ('{id}', '{Company}', '{Supplier}', 'IT-{id:N}',
                    (SELECT id FROM nexora.currency WHERE code='HTG'), 'validated', 0);
            """);
        return id;
    }

    private async Task Sql(string sql)
    {
        await using var c = new Npgsql.NpgsqlConnection(fx.TestConnectionString);
        await c.OpenAsync();
        await using var cmd = new Npgsql.NpgsqlCommand(sql, c);
        await cmd.ExecuteNonQueryAsync();
    }

    // ---- 1. Vente comptant simple ---------------------------------------
    [Fact]
    public async Task CashSale_posts_balanced_entry_and_moves_cash()
    {
        await using var s = Scope();
        var engine = s.ServiceProvider.GetRequiredService<IFinancialEngine>();

        var before = await CashBalance();
        var r = await engine.HandleAsync(new CashSaleRecorded(
            Company, Guid.NewGuid(), On(2, 1), Guid.NewGuid(),
            new Money(1000m, "HTG"), new Money(100m, "HTG"), CashHtg));

        r.IsSuccess.Should().BeTrue(r.Error?.Message);
        var (debit, credit, lines) = await EntryTotals(r.Value!.JournalEntryId);
        debit.Should().Be(1100m);
        credit.Should().Be(1100m);
        lines.Should().BeGreaterThanOrEqualTo(3);
        (await CashBalance() - before).Should().Be(1100m);
    }

    // ---- 2. Vente à crédit : paiement partiel puis final ---------------
    [Fact]
    public async Task CreditSale_then_partial_then_final_payment_settles_receivable()
    {
        Guid receivableId;
        await using (var s = Scope())
        {
            var engine = s.ServiceProvider.GetRequiredService<IFinancialEngine>();
            var sale = await engine.HandleAsync(new CreditSaleRecorded(
                Company, Guid.NewGuid(), On(2, 2), await NewSale(), Customer,
                new Money(5000m, "HTG"), Money.Zero("HTG"), new DateOnly(2026, 3, 4)));
            sale.IsSuccess.Should().BeTrue(sale.Error?.Message);
            receivableId = sale.Value!.ReceivableId!.Value;
        }
        (await ReceivableStatusOf(receivableId)).Should().Be("OPEN");

        await using (var s = Scope())
        {
            var engine = s.ServiceProvider.GetRequiredService<IFinancialEngine>();
            var pay1 = await engine.HandleAsync(new CustomerPaymentReceived(
                Company, Guid.NewGuid(), On(2, 10), Customer, new Money(2000m, "HTG"), CashHtg,
                [new AllocationInstruction(receivableId, 2000m, true)]));
            pay1.IsSuccess.Should().BeTrue(pay1.Error?.Message);
        }
        (await ReceivableRemaining(receivableId)).Should().Be(3000m);
        (await ReceivableStatusOf(receivableId)).Should().Be("PARTIALLY_PAID");

        await using (var s = Scope())
        {
            var engine = s.ServiceProvider.GetRequiredService<IFinancialEngine>();
            var pay2 = await engine.HandleAsync(new CustomerPaymentReceived(
                Company, Guid.NewGuid(), On(2, 20), Customer, new Money(3000m, "HTG"), CashHtg,
                [new AllocationInstruction(receivableId, 3000m, true)]));
            pay2.IsSuccess.Should().BeTrue(pay2.Error?.Message);
        }
        (await ReceivableRemaining(receivableId)).Should().Be(0m);
        (await ReceivableStatusOf(receivableId)).Should().Be("PAID");
    }

    // ---- 3. Achat à crédit : paiement partiel puis final -------------
    [Fact]
    public async Task SupplierInvoice_then_payments_settle_payable()
    {
        Guid payableId;
        await using (var s = Scope())
        {
            var engine = s.ServiceProvider.GetRequiredService<IFinancialEngine>();
            var inv = await engine.HandleAsync(new SupplierInvoiceRecorded(
                Company, Guid.NewGuid(), On(3, 1), await NewSupplierInvoice(), Supplier,
                new Money(4000m, "HTG"), Money.Zero("HTG"), new DateOnly(2026, 5, 1), CapitaliseToInventory: true));
            inv.IsSuccess.Should().BeTrue(inv.Error?.Message);
            payableId = inv.Value!.PayableId!.Value;
        }

        await using (var s = Scope())
            await s.ServiceProvider.GetRequiredService<IFinancialEngine>().HandleAsync(new SupplierPaymentMade(
                Company, Guid.NewGuid(), On(3, 15), Supplier, new Money(1500m, "HTG"), CashHtg,
                [new AllocationInstruction(payableId, 1500m, false)]));
        (await PayableStatusOf(payableId)).Should().Be("PARTIALLY_PAID");

        await using (var s = Scope())
            await s.ServiceProvider.GetRequiredService<IFinancialEngine>().HandleAsync(new SupplierPaymentMade(
                Company, Guid.NewGuid(), On(3, 25), Supplier, new Money(2500m, "HTG"), CashHtg,
                [new AllocationInstruction(payableId, 2500m, false)]));
        (await PayableStatusOf(payableId)).Should().Be("PAID");
    }

    // ---- 4. Dépense ------------------------------------------------
    [Fact]
    public async Task Expense_reduces_cash_and_posts_balanced()
    {
        await using var s = Scope();
        var engine = s.ServiceProvider.GetRequiredService<IFinancialEngine>();
        var before = await CashBalance();
        var r = await engine.HandleAsync(new ExpenseRecorded(
            Company, Guid.NewGuid(), On(4, 1), Guid.NewGuid(),
            new Money(800m, "HTG"), Money.Zero("HTG"), CashHtg));
        r.IsSuccess.Should().BeTrue(r.Error?.Message);
        var (d, c, _) = await EntryTotals(r.Value!.JournalEntryId);
        d.Should().Be(c);
        (await CashBalance() - before).Should().Be(-800m);
    }

    // ---- 5. Écart de caisse -------------------------------------
    [Fact]
    public async Task CashCount_shortage_posts_variance()
    {
        await using var s = Scope();
        var engine = s.ServiceProvider.GetRequiredService<IFinancialEngine>();
        var r = await engine.HandleAsync(new CashCountRecorded(
            Company, Guid.NewGuid(), On(4, 5), Guid.NewGuid(), CashHtg,
            new Money(5000m, "HTG"), new Money(4900m, "HTG")));
        r.IsSuccess.Should().BeTrue(r.Error?.Message);
        var (d, c, _) = await EntryTotals(r.Value!.JournalEntryId);
        d.Should().Be(100m);
        c.Should().Be(100m);
    }

    // ---- 6. Multi-devises : conversion au taux du jour --------
    [Fact]
    public async Task CashSale_in_usd_is_converted_to_functional_currency()
    {
        await using var s = Scope();
        var engine = s.ServiceProvider.GetRequiredService<IFinancialEngine>();
        var r = await engine.HandleAsync(new CashSaleRecorded(
            Company, Guid.NewGuid(), On(2, 15), Guid.NewGuid(),
            new Money(100m, "USD"), Money.Zero("USD"), BankUsd));
        r.IsSuccess.Should().BeTrue(r.Error?.Message);
        var (d, c, _) = await EntryTotals(r.Value!.JournalEntryId);
        d.Should().Be(13200m);   // 100 USD * 132 (taux 2026-01-01)
        c.Should().Be(13200m);
    }

    // ---- 7. Différence de change au règlement -----------------
    [Fact]
    public async Task Fx_gain_recognised_when_payment_rate_differs_from_issue_rate()
    {
        Guid receivableId;
        await using (var s = Scope())
        {
            var sale = await s.ServiceProvider.GetRequiredService<IFinancialEngine>().HandleAsync(new CreditSaleRecorded(
                Company, Guid.NewGuid(), On(1, 15), await NewSale(), Customer,
                new Money(100m, "USD"), Money.Zero("USD"), new DateOnly(2026, 7, 1)));
            sale.IsSuccess.Should().BeTrue(sale.Error?.Message);
            receivableId = sale.Value!.ReceivableId!.Value;
        }

        Guid entryId;
        await using (var s = Scope())
        {
            var pay = await s.ServiceProvider.GetRequiredService<IFinancialEngine>().HandleAsync(new CustomerPaymentReceived(
                Company, Guid.NewGuid(), On(7, 1), Customer, new Money(100m, "USD"), BankUsd,
                [new AllocationInstruction(receivableId, 100m, true)]));
            pay.IsSuccess.Should().BeTrue(pay.Error?.Message);
            entryId = pay.Value!.JournalEntryId;
        }

        (await ReceivableStatusOf(receivableId)).Should().Be("PAID");
        var fxLine = await FxLineAmount(entryId);
        fxLine.Should().Be(300m);   // 100 * (135 - 132)
        var (d, c, _) = await EntryTotals(entryId);
        d.Should().Be(c);
    }

    // ---- 8. Extourne d'une écriture validée -------------------
    [Fact]
    public async Task Reversal_creates_mirror_entry_and_marks_source_reversed()
    {
        Guid entryId;
        await using (var s = Scope())
        {
            var r = await s.ServiceProvider.GetRequiredService<IFinancialEngine>().HandleAsync(new CashSaleRecorded(
                Company, Guid.NewGuid(), On(5, 1), Guid.NewGuid(),
                new Money(2000m, "HTG"), Money.Zero("HTG"), CashHtg));
            entryId = r.Value!.JournalEntryId;
        }

        await using (var s = Scope())
        {
            var rev = await s.ServiceProvider.GetRequiredService<IReversalService>()
                .ReverseAsync(entryId, "Erreur de saisie", new DateOnly(2026, 5, 2));
            rev.IsSuccess.Should().BeTrue(rev.Error?.Message);

            var db = s.ServiceProvider.GetRequiredService<NexoraDbContext>();
            var source = await db.JournalEntries.AsNoTracking().FirstAsync(e => e.Id == entryId);
            source.Status.Should().Be(JournalEntryStatus.Reversed);

            var reversal = await db.JournalEntries.AsNoTracking().Include(e => e.Lines)
                .FirstAsync(e => e.Id == rev.Value);
            reversal.ReversalOfEntryId.Should().Be(entryId);
            reversal.Lines.Sum(l => l.BaseDebit).Should().Be(reversal.Lines.Sum(l => l.BaseCredit));
        }
    }

    // ---- 9. Idempotence (§33) --------------------------------
    [Fact]
    public async Task Same_operation_uuid_is_not_double_posted()
    {
        var op = Guid.NewGuid();
        var evt = new CashSaleRecorded(Company, op, On(6, 1), Guid.NewGuid(),
            new Money(500m, "HTG"), Money.Zero("HTG"), CashHtg);

        Guid first;
        await using (var s = Scope())
            first = (await s.ServiceProvider.GetRequiredService<IFinancialEngine>().HandleAsync(evt)).Value!.JournalEntryId;

        await using (var s = Scope())
        {
            var replay = await s.ServiceProvider.GetRequiredService<IFinancialEngine>().HandleAsync(evt);
            replay.Value!.WasReplay.Should().BeTrue();
            replay.Value.JournalEntryId.Should().Be(first);
        }

        await using var check = Scope();
        var db = check.ServiceProvider.GetRequiredService<NexoraDbContext>();
        (await db.JournalEntries.CountAsync(e => e.SourceOperationUuid == op)).Should().Be(1);
    }

    // ---- 10. Clôture puis tentative de modification (§27) ---
    [Fact]
    public async Task Posting_into_a_closed_period_is_rejected()
    {
        Guid periodId;
        await using (var s = Scope())
        {
            var periods = s.ServiceProvider.GetRequiredService<IAccountingPeriods>();
            periodId = (await periods.ListAsync()).First(p => p.StartDate.Year == 2026).Id;
        }

        try
        {
            await using (var s = Scope())
            {
                var close = await s.ServiceProvider.GetRequiredService<IAccountingPeriodService>().CloseAsync(periodId);
                close.IsSuccess.Should().BeTrue(close.Error?.Message);
            }

            await using (var s = Scope())
            {
                var r = await s.ServiceProvider.GetRequiredService<IFinancialEngine>().HandleAsync(new CashSaleRecorded(
                    Company, Guid.NewGuid(), On(8, 1), Guid.NewGuid(),
                    new Money(100m, "HTG"), Money.Zero("HTG"), CashHtg));
                r.IsSuccess.Should().BeFalse();
                r.Error!.Code.Should().Be("accounting.period_closed");
            }
        }
        finally
        {
            await using var s = Scope();
            await s.ServiceProvider.GetRequiredService<IAccountingPeriodService>().ReopenAsync(periodId, "fin de test");
        }
    }

    // ---- helpers de vérification ------------------------------------

    private async Task<decimal> CashBalance()
    {
        await using var s = Scope();
        var db = s.ServiceProvider.GetRequiredService<NexoraDbContext>();
        return await db.FinancialAccounts.AsNoTracking().Where(a => a.Id == CashHtg)
            .Select(a => a.CurrentBalance).FirstAsync();
    }

    private async Task<(decimal Debit, decimal Credit, int Lines)> EntryTotals(Guid entryId)
    {
        await using var s = Scope();
        var db = s.ServiceProvider.GetRequiredService<NexoraDbContext>();
        var lines = await db.JournalEntryLines.AsNoTracking().Where(l => l.JournalEntryId == entryId).ToListAsync();
        return (lines.Sum(l => l.BaseDebit), lines.Sum(l => l.BaseCredit), lines.Count);
    }

    private async Task<decimal> FxLineAmount(Guid entryId)
    {
        await using var s = Scope();
        var db = s.ServiceProvider.GetRequiredService<NexoraDbContext>();
        return await (from l in db.JournalEntryLines.AsNoTracking()
                      join a in db.Accounts.AsNoTracking() on l.AccountId equals a.Id
                      where l.JournalEntryId == entryId && (a.Code == "7660" || a.Code == "6660")
                      select l.BaseDebit + l.BaseCredit).FirstOrDefaultAsync();
    }

    private async Task<string> ReceivableStatusOf(Guid id)
    {
        await using var s = Scope();
        var db = s.ServiceProvider.GetRequiredService<NexoraDbContext>();
        return await db.Receivables.AsNoTracking().Where(r => r.Id == id).Select(r => r.Status).FirstAsync();
    }

    private async Task<decimal> ReceivableRemaining(Guid id)
    {
        await using var s = Scope();
        var db = s.ServiceProvider.GetRequiredService<NexoraDbContext>();
        return await db.Receivables.AsNoTracking().Where(r => r.Id == id)
            .Select(r => r.OriginalAmount - r.PaidAmount).FirstAsync();
    }

    private async Task<string> PayableStatusOf(Guid id)
    {
        await using var s = Scope();
        var db = s.ServiceProvider.GetRequiredService<NexoraDbContext>();
        return await db.Payables.AsNoTracking().Where(p => p.Id == id).Select(p => p.Status).FirstAsync();
    }
}
