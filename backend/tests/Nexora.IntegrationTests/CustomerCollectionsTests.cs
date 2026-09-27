using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Nexora.Application.Credit;
using Nexora.Application.Sales;
using Nexora.Contracts.Credit;
using Nexora.Infrastructure.Persistence;

namespace Nexora.IntegrationTests;

/// <summary>Clients, créances & recouvrement — scénarios de validation Phase 27 §53-54.</summary>
[Collection("finance")]
public sealed class CustomerCollectionsTests(FinanceEngineFixture fx)
{
    private static readonly Guid Company = Guid.Parse("dce00000-0000-0000-0000-0000000000c1");

    private AsyncServiceScope Scope() => fx.Provider.CreateAsyncScope();

    private async Task<Guid> NewCustomer(decimal creditLimit = 500000m)
    {
        var id = Guid.NewGuid();
        await using var c = new Npgsql.NpgsqlConnection(fx.TestConnectionString);
        await c.OpenAsync();
        await using var cmd = new Npgsql.NpgsqlCommand($"""
            INSERT INTO nexora.customer (id, company_id, customer_number, name, credit_limit, default_currency_id)
            VALUES ('{id}', '{Company}', 'CO-{id:N}', 'Client recouvrement',
                    {creditLimit.ToString(System.Globalization.CultureInfo.InvariantCulture)},
                    (SELECT id FROM nexora.currency WHERE code='HTG'))
            """, c);
        await cmd.ExecuteNonQueryAsync();
        return id;
    }

    private async Task<Guid> NewReceivable(Guid customerId, decimal amount, int dueOffsetDays)
    {
        var id = Guid.NewGuid();
        await using var c = new Npgsql.NpgsqlConnection(fx.TestConnectionString);
        await c.OpenAsync();
        await using var cmd = new Npgsql.NpgsqlCommand($"""
            INSERT INTO nexora.customer_receivable (id, company_id, customer_id, currency_id, original_amount, issue_date, due_date, status)
            VALUES ('{id}', '{Company}', '{customerId}', (SELECT id FROM nexora.currency WHERE code='HTG'),
                    {amount.ToString(System.Globalization.CultureInfo.InvariantCulture)},
                    current_date - 5, current_date + ({dueOffsetDays}), 'OPEN')
            """, c);
        await cmd.ExecuteNonQueryAsync();
        return id;
    }

    private async Task PayReceivable(Guid receivableId, decimal amount)
    {
        await using var c = new Npgsql.NpgsqlConnection(fx.TestConnectionString);
        await c.OpenAsync();
        await using var cmd = new Npgsql.NpgsqlCommand($"""
            UPDATE nexora.customer_receivable
               SET paid_amount = paid_amount + {amount.ToString(System.Globalization.CultureInfo.InvariantCulture)},
                   status = CASE WHEN paid_amount + {amount.ToString(System.Globalization.CultureInfo.InvariantCulture)} >= original_amount
                                 THEN 'PAID' ELSE 'PARTIALLY_PAID' END
             WHERE id = '{receivableId}'
            """, c);
        await cmd.ExecuteNonQueryAsync();
    }

    // ---- §26-27 : un client bloqué ne peut acheter à crédit ; le déblocage rétablit ----
    [Fact]
    public async Task Blocked_customer_cannot_buy_on_credit()
    {
        await using var s = Scope();
        var col = s.ServiceProvider.GetRequiredService<CollectionsService>();
        var credit = s.ServiceProvider.GetRequiredService<ICreditControl>();
        var cust = await NewCustomer();

        (await credit.EvaluateAsync(cust, "HTG", 100000m)).Allowed.Should().BeTrue();

        var blk = await col.BlockCustomerAsync(new BlockCustomerRequest(cust, "credit", "Retards répétés"));
        blk.IsSuccess.Should().BeTrue(blk.Error?.Message);

        var decision = await credit.EvaluateAsync(cust, "HTG", 100000m);
        decision.Allowed.Should().BeFalse();
        decision.Message.Should().Contain("bloqué");

        (await col.BlockCustomerAsync(new BlockCustomerRequest(cust, "credit", "x"))).Error!.Code.Should().Be("credit.already_blocked");

        (await col.ReleaseBlockAsync(blk.Value, new ReleaseBlockRequest(""))).IsSuccess.Should().BeFalse();
        (await col.ReleaseBlockAsync(blk.Value, new ReleaseBlockRequest("Situation régularisée"))).IsSuccess.Should().BeTrue();
        (await credit.EvaluateAsync(cust, "HTG", 100000m)).Allowed.Should().BeTrue();
    }

    // ---- §24 : promesse tenue quand les créances visées sont payées ----
    [Fact]
    public async Task Promise_is_kept_when_the_targeted_receivables_are_paid()
    {
        await using var s = Scope();
        var col = s.ServiceProvider.GetRequiredService<CollectionsService>();
        var cust = await NewCustomer();
        var rid = await NewReceivable(cust, 100000m, dueOffsetDays: -10);

        var promise = await col.CreatePromiseAsync(new CreatePromiseRequest(
            cust, 100000m, DateOnly.FromDateTime(DateTime.UtcNow).AddDays(7), "phone", [rid], "Appel du 30"));
        promise.IsSuccess.Should().BeTrue(promise.Error?.Message);

        await PayReceivable(rid, 100000m);
        (await col.SettlePromiseAsync(promise.Value)).Value.Should().Be("kept");

        (await col.SettlePromiseAsync(promise.Value)).Error!.Code.Should().Be("credit.promise_closed");
    }

    // ---- §24 : promesse rompue si rien n'est payé ----
    [Fact]
    public async Task Promise_is_broken_when_nothing_is_paid()
    {
        await using var s = Scope();
        var col = s.ServiceProvider.GetRequiredService<CollectionsService>();
        var cust = await NewCustomer();
        var rid = await NewReceivable(cust, 50000m, -3);
        var promise = (await col.CreatePromiseAsync(new CreatePromiseRequest(
            cust, 50000m, DateOnly.FromDateTime(DateTime.UtcNow).AddDays(3), "email", [rid], null))).Value;

        (await col.SettlePromiseAsync(promise)).Value.Should().Be("broken");
    }

    // ---- §25 : un litige met la créance « en litige », la résolution la rétablit ----
    [Fact]
    public async Task Dispute_flags_the_receivable_then_resolution_restores_it()
    {
        await using var s = Scope();
        var col = s.ServiceProvider.GetRequiredService<CollectionsService>();
        var db = s.ServiceProvider.GetRequiredService<NexoraDbContext>();
        var cust = await NewCustomer();
        var rid = await NewReceivable(cust, 80000m, 15);

        var d = await col.OpenDisputeAsync(new OpenDisputeRequest(cust, rid, 30000m, "Produit non conforme"));
        d.IsSuccess.Should().BeTrue(d.Error?.Message);
        (await db.Receivables.AsNoTracking().Where(r => r.Id == rid).Select(r => r.Status).FirstAsync()).Should().Be("IN_DISPUTE");

        (await col.ResolveDisputeAsync(d.Value, new ResolveDisputeRequest("resolved_company", "Contrôle qualité : conforme"))).IsSuccess.Should().BeTrue();
        (await db.Receivables.AsNoTracking().Where(r => r.Id == rid).Select(r => r.Status).FirstAsync()).Should().Be("OPEN");
    }

    // ---- §37 : radiation → écriture Dr créances irrécouvrables / Cr clients, créance radiée ----
    [Fact]
    public async Task Write_off_posts_a_balanced_bad_debt_entry_and_closes_the_receivable()
    {
        await using var s = Scope();
        var col = s.ServiceProvider.GetRequiredService<CollectionsService>();
        var db = s.ServiceProvider.GetRequiredService<NexoraDbContext>();
        var cust = await NewCustomer();
        var rid = await NewReceivable(cust, 40000m, -60);
        await PayReceivable(rid, 10000m);   // reste 30 000

        (await col.WriteOffReceivableAsync(rid, new WriteOffReceivableRequest(Guid.NewGuid(), 40000m, "x")))
            .Error!.Code.Should().Be("credit.writeoff_amount");   // doit porter sur le solde exact (30 000)

        var wo = await col.WriteOffReceivableAsync(rid, new WriteOffReceivableRequest(Guid.NewGuid(), 30000m, "Client insolvable"));
        wo.IsSuccess.Should().BeTrue(wo.Error?.Message);

        var r = await db.Receivables.AsNoTracking().FirstAsync(x => x.Id == rid);
        r.Status.Should().Be("WRITTEN_OFF");
        r.OriginalAmount.Should().Be(40000m);   // montant original conservé (§37)

        var lines = await db.JournalEntryLines.AsNoTracking().Where(l => l.JournalEntryId == wo.Value!.JournalEntryId).ToListAsync();
        lines.Sum(l => l.BaseDebit).Should().Be(30000m);
        lines.Sum(l => l.BaseDebit).Should().Be(lines.Sum(l => l.BaseCredit));

        (await col.WriteOffReceivableAsync(rid, new WriteOffReceivableRequest(Guid.NewGuid(), 30000m, "x")))
            .Error!.Code.Should().Be("credit.receivable_closed");
    }

    // ---- §15, §29 : le portefeuille & l'aging reflètent les créances échues ----
    [Fact]
    public async Task Collections_and_aging_reflect_overdue_receivables()
    {
        await using var s = Scope();
        var col = s.ServiceProvider.GetRequiredService<CollectionsService>();
        var q = s.ServiceProvider.GetRequiredService<ICreditPortfolioQueries>();
        var cust = await NewCustomer();
        var rid = await NewReceivable(cust, 120000m, dueOffsetDays: -45);   // 31–60 j
        await col.CreatePromiseAsync(new CreatePromiseRequest(cust, 120000m, DateOnly.FromDateTime(DateTime.UtcNow).AddDays(5), "phone", [rid], null));

        var collections = await q.CollectionsAsync(onlyOverdue: true);
        var row = collections.Should().Contain(x => x.CustomerId == cust).Which;
        row.Bucket31To60.Should().Be(120000m);
        row.OpenPromises.Should().Be(1);
        row.OldestOverdueDays.Should().BeGreaterThanOrEqualTo(45);

        var aging = await q.AgingSummaryAsync();
        aging.TotalOverdue.Should().BeGreaterThanOrEqualTo(120000m);
        aging.Bucket31To60.Should().BeGreaterThanOrEqualTo(120000m);
    }

    // ---- §35 : relevé client — ventes crédit, paiements, radiations, solde progressif ----
    [Fact]
    public async Task Customer_statement_lists_charges_and_payments()
    {
        await using var s = Scope();
        var col = s.ServiceProvider.GetRequiredService<CollectionsService>();
        var q = s.ServiceProvider.GetRequiredService<ICreditPortfolioQueries>();
        var cust = await NewCustomer();
        var rid = await NewReceivable(cust, 25000m, -30);
        var wo = await col.WriteOffReceivableAsync(rid, new WriteOffReceivableRequest(Guid.NewGuid(), 25000m, "Radiation test"));
        wo.IsSuccess.Should().BeTrue(wo.Error?.Message);

        var stmt = await q.CustomerStatementAsync(cust, null, null);
        stmt.Should().NotBeNull();
        stmt!.Lines.Should().Contain(l => l.DocumentType == "write_off" && l.Payment == 25000m);
    }
}
