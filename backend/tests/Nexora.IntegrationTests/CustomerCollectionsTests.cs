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

        // Finalisation phase 9 : avant l'échéance, le client a encore le temps — la promesse n'est pas « non tenue ».
        (await col.SettlePromiseAsync(promise)).Error!.Code.Should().Be("credit.promise_not_due");
        var db = s.ServiceProvider.GetRequiredService<NexoraDbContext>();
        await db.Database.ExecuteSqlAsync($"UPDATE nexora.customer_promise SET promised_date = current_date - 1 WHERE id = {promise}");
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

        (await col.WriteOffReceivableAsync(rid, new WriteOffReceivableRequest(Guid.NewGuid(), 40000m, "Client disparu")))
            .Error!.Code.Should().Be("credit.writeoff_amount");   // doit porter sur le solde exact (30 000)

        var wo = await col.WriteOffReceivableAsync(rid, new WriteOffReceivableRequest(Guid.NewGuid(), 30000m, "Client insolvable"));
        wo.IsSuccess.Should().BeTrue(wo.Error?.Message);

        var r = await db.Receivables.AsNoTracking().FirstAsync(x => x.Id == rid);
        r.Status.Should().Be("WRITTEN_OFF");
        r.OriginalAmount.Should().Be(40000m);   // montant original conservé (§37)

        var lines = await db.JournalEntryLines.AsNoTracking().Where(l => l.JournalEntryId == wo.Value!.JournalEntryId).ToListAsync();
        lines.Sum(l => l.BaseDebit).Should().Be(30000m);
        lines.Sum(l => l.BaseDebit).Should().Be(lines.Sum(l => l.BaseCredit));

        (await col.WriteOffReceivableAsync(rid, new WriteOffReceivableRequest(Guid.NewGuid(), 30000m, "Client disparu")))
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

    // ---- Finalisation phase 9 : cycle créance → promesse → échéance → tenue / non tenue ----
    [Fact]
    public async Task Promise_rules_and_due_date_evaluation()
    {
        await using var s = Scope();
        var col = s.ServiceProvider.GetRequiredService<CollectionsService>();
        var db = s.ServiceProvider.GetRequiredService<NexoraDbContext>();
        var cust = await NewCustomer();
        var other = await NewCustomer();
        var rid = await NewReceivable(cust, 20000m, -10);
        var foreign = await NewReceivable(other, 5000m, -10);
        var today = DateOnly.FromDateTime(DateTime.UtcNow);

        (await col.CreatePromiseAsync(new CreatePromiseRequest(cust, 5000m, today.AddDays(-1), "phone", [rid], null)))
            .Error!.Code.Should().Be("credit.promise_date");
        (await col.CreatePromiseAsync(new CreatePromiseRequest(cust, 25000m, today.AddDays(5), "phone", [rid], null)))
            .Error!.Code.Should().Be("credit.promise_amount", "on ne promet pas plus que le reste dû");
        (await col.CreatePromiseAsync(new CreatePromiseRequest(cust, 5000m, today.AddDays(5), "pigeon", [rid], null)))
            .Error!.Code.Should().Be("credit.promise_channel");
        (await col.CreatePromiseAsync(new CreatePromiseRequest(cust, 5000m, today.AddDays(5), "phone", [foreign], null)))
            .Error!.Code.Should().Be("credit.promise_receivable_customer");

        var p = await col.CreatePromiseAsync(new CreatePromiseRequest(cust, 8000m, today.AddDays(5), "phone", [rid, rid], "Paie le 15"));
        p.IsSuccess.Should().BeTrue(p.Error?.Message);
        (await col.CreatePromiseAsync(new CreatePromiseRequest(cust, 1000m, today.AddDays(6), "sms", [rid], null)))
            .Error!.Code.Should().Be("credit.promise_overlap");

        // Payée en avance : le planificateur la déclare tenue sans attendre l'échéance.
        await PayReceivable(rid, 8000m);
        (await col.SettleDuePromisesAsync()).Should().BeGreaterThanOrEqualTo(1);
        (await db.Database.SqlQuery<string>($"SELECT status AS \"Value\" FROM nexora.customer_promise WHERE id = {p.Value}").FirstAsync())
            .Should().Be("kept");

        // Deuxième promesse, échue sans paiement : non tenue, et apparaît dans l'agenda.
        var p2 = (await col.CreatePromiseAsync(new CreatePromiseRequest(cust, 4000m, today.AddDays(2), "visit", [rid], null))).Value;
        var q = s.ServiceProvider.GetRequiredService<ICreditPortfolioQueries>();
        await col.SettleDuePromisesAsync();
        (await q.PromisesAsync(cust, null)).Single(x => x.Id == p2).Status.Should().Be("open", "pas encore échue, rien n'est payé : on attend");
        await db.Database.ExecuteSqlAsync($"UPDATE nexora.customer_promise SET promised_date = current_date - 1 WHERE id = {p2}");
        await col.SettleDuePromisesAsync();
        (await q.PromisesAsync(cust, null)).Single(x => x.Id == p2).Status.Should().Be("broken");
        (await q.FollowUpsAsync()).Should().Contain(f => f.Kind == "promise_broken" && f.ReferenceId == p2);

        // Annulation : motif obligatoire, puis plus d'action possible.
        var p3 = (await col.CreatePromiseAsync(new CreatePromiseRequest(cust, 1000m, today.AddDays(3), "phone", [rid], null))).Value;
        (await col.CancelPromiseAsync(p3, new CancelPromiseRequest(""))).Error!.Code.Should().Be("credit.promise_cancel_reason");
        (await col.CancelPromiseAsync(p3, new CancelPromiseRequest("Client a changé d'avis"))).IsSuccess.Should().BeTrue();
        (await col.SettlePromiseAsync(p3)).Error!.Code.Should().Be("credit.promise_closed");
        (await q.PromisesAsync(cust, "cancelled")).Single().CancelReason.Should().Be("Client a changé d'avis");
    }

    [Fact]
    public async Task Dispute_rules_keep_the_receivable_status_and_block_write_off()
    {
        await using var s = Scope();
        var col = s.ServiceProvider.GetRequiredService<CollectionsService>();
        var db = s.ServiceProvider.GetRequiredService<NexoraDbContext>();
        var cust = await NewCustomer();
        var other = await NewCustomer();
        var rid = await NewReceivable(cust, 30000m, -20);
        await PayReceivable(rid, 10000m);   // reste 20 000, partiellement payée

        (await col.OpenDisputeAsync(new OpenDisputeRequest(other, rid, 1000m, "Erreur de facture")))
            .Error!.Code.Should().Be("credit.dispute_receivable");
        (await col.OpenDisputeAsync(new OpenDisputeRequest(cust, rid, 25000m, "Erreur de facture")))
            .Error!.Code.Should().Be("credit.dispute_amount");
        var d = await col.OpenDisputeAsync(new OpenDisputeRequest(cust, rid, 5000m, "Livraison incomplète"));
        d.IsSuccess.Should().BeTrue(d.Error?.Message);
        (await col.OpenDisputeAsync(new OpenDisputeRequest(cust, rid, 1000m, "Autre motif"))).Error!.Code.Should().Be("credit.dispute_exists");
        (await col.WriteOffReceivableAsync(rid, new WriteOffReceivableRequest(Guid.NewGuid(), 20000m, "Client disparu")))
            .Error!.Code.Should().Be("credit.receivable_in_dispute");
        (await col.CreatePromiseAsync(new CreatePromiseRequest(cust, 1000m, DateOnly.FromDateTime(DateTime.UtcNow).AddDays(3), "phone", [rid], null)))
            .Error!.Code.Should().Be("credit.promise_disputed");

        (await col.ResolveDisputeAsync(d.Value, new ResolveDisputeRequest("resolved_company", ""))).Error!.Code.Should().Be("credit.dispute_resolution");
        (await col.ResolveDisputeAsync(d.Value, new ResolveDisputeRequest("resolved_company", "Bon de livraison signé"))).IsSuccess.Should().BeTrue();
        (await db.Receivables.AsNoTracking().Where(r => r.Id == rid).Select(r => r.Status).FirstAsync())
            .Should().Be("PARTIALLY_PAID", "la sortie de litige ne doit pas effacer les règlements déjà reçus");

        var q = s.ServiceProvider.GetRequiredService<ICreditPortfolioQueries>();
        var rec = (await q.ReceivablesAsync(cust, true)).Single(r => r.Id == rid);
        rec.RemainingAmount.Should().Be(20000m);
        // Retard calculé par le service sur sa propre horloge (UTC) : le test ne suppose pas la date locale de PostgreSQL
        // (entre 20 h et minuit à Haïti, UTC est déjà au lendemain).
        rec.DaysOverdue.Should().Be(DateOnly.FromDateTime(DateTime.UtcNow).DayNumber - rec.DueDate!.Value.DayNumber);
        rec.DaysOverdue.Should().BeInRange(20, 21);
        rec.OpenDisputeId.Should().BeNull();
        (await q.DisputesAsync(cust, null)).Single().Status.Should().Be("resolved_company");
    }

    [Fact]
    public async Task Follow_up_agenda_keeps_only_the_latest_action_and_release_does_not_reactivate_archived()
    {
        await using var s = Scope();
        var actions = s.ServiceProvider.GetRequiredService<Nexora.Application.Customers.ICollectionActionService>();
        var q = s.ServiceProvider.GetRequiredService<ICreditPortfolioQueries>();
        var col = s.ServiceProvider.GetRequiredService<CollectionsService>();
        var db = s.ServiceProvider.GetRequiredService<NexoraDbContext>();
        var cust = await NewCustomer();
        await NewReceivable(cust, 10000m, -5);

        var first = await actions.LogAsync(cust, new Nexora.Contracts.Customers.CreateCollectionActionRequest("call", "Pas de réponse", "no_answer", null,
            DateOnly.FromDateTime(DateTime.UtcNow)));
        first.IsSuccess.Should().BeTrue(first.Error?.Message);
        (await q.FollowUpsAsync()).Should().Contain(f => f.Kind == "action" && f.CustomerId == cust);
        await actions.LogAsync(cust, new Nexora.Contracts.Customers.CreateCollectionActionRequest("call", "Rappel fait, paiera vendredi", "reached"));
        (await q.FollowUpsAsync()).Should().NotContain(f => f.Kind == "action" && f.CustomerId == cust,
            "la relance prévue a été faite : elle ne doit plus rester à traiter");

        var blk = (await col.BlockCustomerAsync(new BlockCustomerRequest(cust, "credit", "Retards"))).Value;
        await db.Database.ExecuteSqlAsync($"UPDATE nexora.customer SET status = 'archived' WHERE id = {cust}");
        (await col.ReleaseBlockAsync(blk, new ReleaseBlockRequest("Régularisé"))).IsSuccess.Should().BeTrue();
        (await db.Database.SqlQuery<string>($"SELECT status AS \"Value\" FROM nexora.customer WHERE id = {cust}").FirstAsync())
            .Should().Be("archived");
    }
}
