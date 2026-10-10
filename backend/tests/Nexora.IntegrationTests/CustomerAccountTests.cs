using System.Globalization;
using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Nexora.Application.Abstractions;
using Nexora.Application.Administration;
using Nexora.Application.Catalog;
using Nexora.Application.Customers;
using Nexora.Application.Identity;
using Nexora.Application.Parties;
using Nexora.Application.Purchasing;
using Nexora.Application.Sales;
using Nexora.Application.Sync;
using Nexora.Contracts.Administration;
using Nexora.Contracts.Customers;
using Nexora.Contracts.Identity;
using Nexora.Contracts.Parties;
using Nexora.Contracts.Sales;
using Nexora.Contracts.Sync;
using Nexora.Infrastructure.Persistence;

namespace Nexora.IntegrationTests;

/// <summary>
/// Module « Gestion clients & comptes clients » — grille d'acceptation §31 et exemple complet §28
/// (docs/module-clients-comptes-clients.md), prouvés au niveau des services avec de vrais utilisateurs.
/// Chaque scénario vérifie aussi la cohérence journal client ↔ crédits/avoirs ↔ grand livre.
/// </summary>
[Collection("finance")]
public sealed class CustomerAccountTests(FinanceEngineFixture fx)
{
    private static readonly Guid Company = Guid.Parse("dce00000-0000-0000-0000-0000000000c1");
    private static readonly Guid Restaurant = Guid.Parse("dce00000-0000-0000-0000-0000000000c2");
    private static readonly Guid Direction = Guid.Parse("dce00000-0000-0000-0000-0000000000d1");
    private static readonly Guid Officine = Guid.Parse("dce00000-0000-0000-0000-00000000a003");
    private static readonly Guid Supplier1 = Guid.Parse("dce00000-0000-0000-0000-00000000b101");
    private static readonly Guid BoxUnit = Guid.Parse("dce00000-0000-0000-0000-00000000a011");
    private static readonly Guid CashAccount = Guid.Parse("dce00000-0000-0000-0000-00000000a051");
    private static readonly Guid CashMethod = Guid.Parse("dce00000-0000-0000-0000-00000000a081");
    private static DateOnly Today => TestDates.Today;

    private AsyncServiceScope Scope() => fx.Provider.CreateAsyncScope();

    private static void As(IServiceProvider sp, Guid userId, Guid? company = null)
    {
        var tc = (TestTenantContext)sp.GetRequiredService<ITenantContext>();
        tc.SetUser(userId);
        tc.SetScope([company ?? Company], [], company ?? Company);
    }

    private async Task<Guid> UserWithRole(string role)
    {
        var id = Guid.NewGuid();
        await Exec($"""
            INSERT INTO nexora.app_user (id, email, password_hash, full_name, status)
            VALUES ('{id}', 'CA-{id:N}@test.demo', crypt('x', gen_salt('bf', 4)), '{role} compte client', 'active')
            """);
        await using var s = Scope();
        As(s.ServiceProvider, Direction);
        var granted = await s.ServiceProvider.GetRequiredService<IUserAssignmentService>()
            .GrantAsync(id, new GrantAssignmentRequest(role, "COMPANY", Company, null, null, null, null));
        granted.IsSuccess.Should().BeTrue(granted.Error?.Message);
        return id;
    }

    private async Task Exec(string sql)
    {
        await using var c = new Npgsql.NpgsqlConnection(fx.TestConnectionString);
        await c.OpenAsync();
        await using var cmd = new Npgsql.NpgsqlCommand(sql, c);
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task<T> Scalar<T>(string sql)
    {
        await using var c = new Npgsql.NpgsqlConnection(fx.TestConnectionString);
        await c.OpenAsync();
        await using var cmd = new Npgsql.NpgsqlCommand(sql, c);
        var v = await cmd.ExecuteScalarAsync();
        return v is null or DBNull ? default! : (T)Convert.ChangeType(v, typeof(T), CultureInfo.InvariantCulture);
    }

    private async Task<CustomerDto> NewCustomer(decimal limit, int? creditDays = null)
    {
        await using var s = Scope();
        As(s.ServiceProvider, Direction);
        var r = await s.ServiceProvider.GetRequiredService<ICustomerService>().CreateAsync(new CreateCustomerRequest(
            $"Client compte {Guid.NewGuid():N}"[..28], Phone: "+509 3712 4455", CreditLimit: limit, FirstName: "Jean",
            City: "Pétion-Ville", Country: "HT", Segment: "Grossiste", CreditDays: creditDays, CreditTerms: "Paiement à 30 jours"));
        r.IsSuccess.Should().BeTrue(r.Error?.Message);
        return r.Value!;
    }

    private async Task<Guid> Product(decimal price, decimal qty = 200m)
    {
        await using var s = Scope();
        As(s.ServiceProvider, Direction);
        var catalog = s.ServiceProvider.GetRequiredService<CatalogService>();
        var id = (await catalog.CreateProductAsync(new CreateProductCommand(
            $"CA-{Guid.NewGuid():N}"[..20], "Article compte client", null, BoxUnit, "stockable", null, null, null, null, false, false))).Value;
        await catalog.SetPriceAsync(new SetProductPriceCommand(id, "sale", price, "HTG", 0m, null, null));
        await s.ServiceProvider.GetRequiredService<PurchasingService>().ReceiveAsync(new ReceiveGoodsCommand(
            Guid.NewGuid(), Supplier1, Officine, null, [new ReceiveLine(id, null, qty, price / 2m, null, null)], null, null));
        return id;
    }

    private async Task<CheckoutResult> CreditSale(Guid customer, Guid product, decimal qty, DateOnly? due = null,
        IReadOnlyList<TenderInput>? tenders = null, string? overrideReason = null)
    {
        await using var s = Scope();
        As(s.ServiceProvider, Direction);
        var r = await s.ServiceProvider.GetRequiredService<SalesService>().CheckoutAsync(new CheckoutCommand(
            Guid.NewGuid(), Officine, null, null, null, customer, "HTG", [new CheckoutLine(product, qty, null, 0m, null)],
            tenders ?? [], 0m, due, null, overrideReason, null));
        r.IsSuccess.Should().BeTrue(r.Error?.Message);
        return r.Value!;
    }

    private async Task<CustomerAccountSummaryDto> Summary(Guid customer)
    {
        await using var s = Scope();
        As(s.ServiceProvider, Direction);
        return (await s.ServiceProvider.GetRequiredService<ICustomerAccountQueries>().SummaryAsync(customer, null, null))!;
    }

    /// <summary>Solde du compte client dans le grand livre (compte collectif clients 4111, tiers = ce client).</summary>
    private Task<decimal> GeneralLedgerBalance(Guid customer, string accountCode = "4111") => Scalar<decimal>($"""
        SELECT COALESCE(SUM(l.base_debit - l.base_credit), 0) FROM nexora.journal_entry_line l
        JOIN nexora.account a ON a.id = l.account_id
        WHERE l.third_party_id = '{customer}' AND a.code = '{accountCode}'
        """);

    private static JsonElement J(string json) => JsonDocument.Parse(json).RootElement.Clone();

    // ---- §28 + §31 tests 1, 2, 3, 6, 7 : l'exemple complet du cahier, de bout en bout ----
    [Fact]
    public async Task Complete_example_credit_payments_expense_and_final_payment_settle_the_account()
    {
        var manager = await UserWithRole("manager");
        var customer = await NewCustomer(100_000m);
        (await Summary(customer.Id)).Should().Match<CustomerAccountSummaryDto>(a =>
            a.BalanceDue == 0m && a.LedgerBalance == 0m && a.LedgerConsistent && a.AvailableCredit == 100_000m);

        // Vente à crédit 50 000 HTG → la dette augmente ; le crédit reçoit un numéro unique et une échéance par défaut.
        var product = await Product(5_000m);
        var sale = await CreditSale(customer.Id, product, 10m);
        var creditId = sale.ReceivableId!.Value;
        await using (var s = Scope())
        {
            As(s.ServiceProvider, Direction);
            var detail = (await s.ServiceProvider.GetRequiredService<ICustomerAccountQueries>().CreditAsync(creditId))!;
            detail.Credit.CreditNumber.Should().StartWith("CRD-");
            detail.Credit.OriginalAmount.Should().Be(50_000m);
            detail.Credit.SourceReference.Should().Be(sale.DocumentNumber);
            detail.Credit.DueDate.Should().Be(Today.AddDays(30), "échéance par défaut credit.default_due_days");
        }
        (await Summary(customer.Id)).BalanceDue.Should().Be(50_000m);

        // Versements de 10 000 puis 15 000 : chacun est une opération distincte, rien n'est écrasé.
        await using (var s = Scope())
        {
            As(s.ServiceProvider, manager);
            var svc = s.ServiceProvider.GetRequiredService<ICustomerAccountService>();
            (await svc.RecordCreditPaymentAsync(creditId, new RecordCreditPaymentRequest(Guid.NewGuid(), 10_000m, CashAccount,
                CashMethod, null, "REC-1", "Versement 1"))).IsSuccess.Should().BeTrue();
            (await svc.RecordCreditPaymentAsync(creditId, new RecordCreditPaymentRequest(Guid.NewGuid(), 15_000m, CashAccount,
                CashMethod, null, "REC-2", "Versement 2"))).IsSuccess.Should().BeTrue();
        }
        (await Summary(customer.Id)).BalanceDue.Should().Be(25_000m);

        // Dépense de livraison 2 000 validée par un autre utilisateur → la dette augmente.
        Guid expenseId;
        await using (var s = Scope())
        {
            As(s.ServiceProvider, Direction);
            var created = await s.ServiceProvider.GetRequiredService<ICustomerExpenseService>().CreateAsync(customer.Id,
                new CreateCustomerExpenseRequest(Guid.NewGuid(), "delivery", "Livraison spéciale", 2_000m));
            created.IsSuccess.Should().BeTrue(created.Error?.Message);
            created.Value!.Status.Should().Be("pending");
            created.Value.ExpenseNumber.Should().StartWith("EXP-CLI-");
            expenseId = created.Value.Id;
            (await s.ServiceProvider.GetRequiredService<ICustomerExpenseService>().ApproveAsync(expenseId, new CustomerDecisionRequest(null)))
                .Error!.Code.Should().Be("customer_expense.self_approval", "l'auteur ne valide pas sa propre dépense");
        }
        (await Summary(customer.Id)).BalanceDue.Should().Be(25_000m, "une dépense en attente ne touche pas le compte");
        await using (var s = Scope())
        {
            As(s.ServiceProvider, manager);
            var approved = await s.ServiceProvider.GetRequiredService<ICustomerExpenseService>().ApproveAsync(expenseId, new CustomerDecisionRequest("OK"));
            approved.IsSuccess.Should().BeTrue(approved.Error?.Message);
            approved.Value!.Status.Should().Be("validated");
            approved.Value.CreditNumber.Should().StartWith("CRD-");
        }
        (await Summary(customer.Id)).BalanceDue.Should().Be(27_000m, "§28 : 50 000 − 10 000 − 15 000 + 2 000");

        // Paiement final de 27 000 sur le compte : réparti sur les deux crédits, qui sont soldés.
        await using (var s = Scope())
        {
            As(s.ServiceProvider, manager);
            var final = await s.ServiceProvider.GetRequiredService<ICustomerAccountService>().RecordPaymentAsync(customer.Id,
                new RecordCustomerPaymentRequest(Guid.NewGuid(), 27_000m, CashAccount, CashMethod, Note: "Solde de tout compte"));
            final.IsSuccess.Should().BeTrue(final.Error?.Message);
            final.Value!.Allocations.Should().HaveCount(2).And.OnlyContain(a => !a.Voided);
            final.Value.ReceivedByName.Should().Be("manager compte client");
            final.Value.PaymentMethod.Should().NotBeNullOrEmpty();
        }

        var end = await Summary(customer.Id);
        end.BalanceDue.Should().Be(0m);
        end.LedgerBalance.Should().Be(0m);
        end.LedgerConsistent.Should().BeTrue();
        end.ExpensesBilled.Should().Be(2_000m);
        (await GeneralLedgerBalance(customer.Id)).Should().Be(0m, "le grand livre clients concorde avec le compte client");

        await using (var s = Scope())
        {
            As(s.ServiceProvider, Direction);
            var q = s.ServiceProvider.GetRequiredService<ICustomerAccountQueries>();
            var credits = await q.CreditsAsync(customer.Id, null);
            credits.Should().HaveCount(2).And.OnlyContain(c => c.DisplayStatus == "paid");
            // §6/§31-6 : chaque versement reste visible avec son moyen, son auteur et sa note.
            var detail = (await q.CreditAsync(creditId))!;
            detail.Settlements.Where(x => x.Kind == "payment").Select(x => x.Amount).Should().Equal(10_000m, 15_000m, 25_000m);
            detail.Settlements.Should().OnlyContain(x => x.ReceivedByName != null && x.Method != null);
            var ledger = (await q.LedgerAsync(customer.Id, null, null))!;
            ledger.Entries.Select(e => e.EntryType).Should().Equal(
                "credit_sale", "payment", "payment", "customer_expense", "payment", "payment");
            ledger.ClosingBalance.Should().Be(0m);
            var timeline = await q.TimelineAsync(customer.Id);
            timeline.Should().Contain(e => e.Category == "profile" && e.Title == "Fiche client créée");
            timeline.Should().Contain(e => e.Category == "purchase" && e.Reference == sale.DocumentNumber);
            timeline.Should().Contain(e => e.Category == "expense" && e.Title == "Dépense validée");
        }
    }

    // ---- §31 test 4 : versement supérieur au solde selon la règle configurée ; §7 avoir imputé ----
    [Fact]
    public async Task Overpayment_is_refused_by_default_and_becomes_a_usable_credit_memo_when_configured()
    {
        var customer = await NewCustomer(50_000m);
        var product = await Product(100m);
        var sale = await CreditSale(customer.Id, product, 10m);

        await using (var s = Scope())
        {
            As(s.ServiceProvider, Direction);
            var refused = await s.ServiceProvider.GetRequiredService<ICustomerAccountService>().RecordPaymentAsync(customer.Id,
                new RecordCustomerPaymentRequest(Guid.NewGuid(), 1_500m, CashAccount, CashMethod));
            refused.Error!.Code.Should().Be("credit.overpayment");
        }
        (await Scalar<long>($"SELECT count(*) FROM nexora.payment WHERE customer_id = '{customer.Id}'")).Should().Be(0);

        await using (var s = Scope())
        {
            As(s.ServiceProvider, Direction);
            var settings = s.ServiceProvider.GetRequiredService<ISettingsService>();
            (await settings.SetAsync(new SetSettingRequest("credit.overpayment_policy", "company", null, J("\"credit_memo\""), "Test avoir")))
                .IsSuccess.Should().BeTrue();
            try
            {
                var paid = await s.ServiceProvider.GetRequiredService<ICustomerAccountService>().RecordPaymentAsync(customer.Id,
                    new RecordCustomerPaymentRequest(Guid.NewGuid(), 1_500m, CashAccount, CashMethod));
                paid.IsSuccess.Should().BeTrue(paid.Error?.Message);
                paid.Value!.AllocatedAmount.Should().Be(1_000m);
                paid.Value.CreditMemoNumber.Should().StartWith("AVC-");
            }
            finally
            {
                await settings.SetAsync(new SetSettingRequest("credit.overpayment_policy", "company", null, J("\"refuse\""), "Remise en état"));
            }
        }
        var after = await Summary(customer.Id);
        after.BalanceDue.Should().Be(0m);
        after.AvailableStoreCredit.Should().Be(500m);
        after.NetBalance.Should().Be(-500m);
        after.LedgerConsistent.Should().BeTrue();

        // L'avoir disponible solde une nouvelle dette (Dr compte d'attente / Cr clients).
        var second = await CreditSale(customer.Id, product, 3m);
        await using (var s = Scope())
        {
            As(s.ServiceProvider, Direction);
            var memo = (await s.ServiceProvider.GetRequiredService<ICustomerAccountQueries>().CreditMemosAsync(customer.Id, true)).Single();
            var applied = await s.ServiceProvider.GetRequiredService<ICustomerAccountService>().ApplyCreditMemoAsync(memo.Id,
                new ApplyCreditMemoRequest(Guid.NewGuid()));
            applied.IsSuccess.Should().BeTrue(applied.Error?.Message);
            applied.Value!.AppliedAmount.Should().Be(300m);
            applied.Value.RemainingAmount.Should().Be(200m);
            applied.Value.Applications.Should().ContainSingle(a => a.ReceivableId == second.ReceivableId);
        }
        var final = await Summary(customer.Id);
        final.BalanceDue.Should().Be(0m);
        final.AvailableStoreCredit.Should().Be(200m);
        final.LedgerConsistent.Should().BeTrue();
        (await GeneralLedgerBalance(customer.Id)).Should().Be(0m);
    }

    // ---- §31 test 5 : la même requête envoyée deux fois ne crée qu'un versement ----
    [Fact]
    public async Task Retrying_the_same_payment_request_creates_a_single_payment()
    {
        var customer = await NewCustomer(50_000m);
        var sale = await CreditSale(customer.Id, await Product(100m), 10m);
        var op = Guid.NewGuid();
        Guid first;
        await using (var s = Scope())
        {
            As(s.ServiceProvider, Direction);
            var svc = s.ServiceProvider.GetRequiredService<ICustomerAccountService>();
            first = (await svc.RecordCreditPaymentAsync(sale.ReceivableId!.Value,
                new RecordCreditPaymentRequest(op, 400m, CashAccount, CashMethod))).Value!.Id;
        }
        await using (var s = Scope())
        {
            As(s.ServiceProvider, Direction);
            var again = await s.ServiceProvider.GetRequiredService<ICustomerAccountService>().RecordCreditPaymentAsync(
                sale.ReceivableId!.Value, new RecordCreditPaymentRequest(op, 400m, CashAccount, CashMethod));
            again.IsSuccess.Should().BeTrue();
            again.Value!.Id.Should().Be(first);
        }
        (await Scalar<long>($"SELECT count(*) FROM nexora.payment WHERE source_operation_uuid = '{op}'")).Should().Be(1);
        (await Summary(customer.Id)).BalanceDue.Should().Be(600m);
    }

    // ---- §31 test 8 : annulation autorisée → contre-écriture et audit, rien n'est supprimé ----
    [Fact]
    public async Task Voiding_a_payment_restores_the_debt_with_counter_entries_and_keeps_the_original()
    {
        var manager = await UserWithRole("manager");
        var customer = await NewCustomer(50_000m);
        var sale = await CreditSale(customer.Id, await Product(100m), 10m);
        Guid paymentId;
        await using (var s = Scope())
        {
            As(s.ServiceProvider, Direction);
            paymentId = (await s.ServiceProvider.GetRequiredService<ICustomerAccountService>().RecordCreditPaymentAsync(
                sale.ReceivableId!.Value, new RecordCreditPaymentRequest(Guid.NewGuid(), 400m, CashAccount, CashMethod))).Value!.Id;
        }
        var cashBefore = await Scalar<decimal>($"SELECT current_balance FROM nexora.financial_account WHERE id = '{CashAccount}'");

        await using (var s = Scope())
        {
            As(s.ServiceProvider, manager);
            var svc = s.ServiceProvider.GetRequiredService<ICustomerAccountService>();
            (await svc.VoidPaymentAsync(paymentId, new VoidPaymentRequest(""))).Error!.Code.Should().Be("credit.void_reason");
            var voided = await svc.VoidPaymentAsync(paymentId, new VoidPaymentRequest("Chèque sans provision"));
            voided.IsSuccess.Should().BeTrue(voided.Error?.Message);
            voided.Value!.Status.Should().Be("void");
            voided.Value.VoidReason.Should().Be("Chèque sans provision");
            voided.Value.Allocations.Should().OnlyContain(a => a.Voided);
            (await svc.VoidPaymentAsync(paymentId, new VoidPaymentRequest("Deuxième fois"))).Error!.Code.Should().Be("credit.payment_already_void");
        }

        var summary = await Summary(customer.Id);
        summary.BalanceDue.Should().Be(1_000m, "la dette est rétablie");
        summary.LedgerConsistent.Should().BeTrue();
        (await Scalar<long>($"SELECT count(*) FROM nexora.payment_allocation WHERE payment_id = '{paymentId}'")).Should().Be(1, "l'affectation est conservée");
        (await Scalar<long>($"""
            SELECT count(*) FROM nexora.journal_entry r JOIN nexora.journal_entry o ON o.id = r.reversal_of_entry_id
            JOIN nexora.payment p ON p.source_operation_uuid = o.source_operation_uuid WHERE p.id = '{paymentId}'
            """)).Should().Be(1, "l'écriture d'encaissement est extournée");
        (await Scalar<decimal>($"SELECT current_balance FROM nexora.financial_account WHERE id = '{CashAccount}'"))
            .Should().Be(cashBefore - 400m, "la caisse ressort le montant");
        (await Scalar<long>($"""
            SELECT count(*) FROM nexora.audit_log WHERE entity_type = 'payment' AND entity_id = '{paymentId}' AND action = 'UPDATE'
              AND new_values ->> 'void_reason' = 'Chèque sans provision' AND user_id = '{manager}'
            """)).Should().BeGreaterThan(0, "§30 : qui, quand, motif");
        (await GeneralLedgerBalance(customer.Id)).Should().Be(1_000m);

        await using (var s = Scope())
        {
            As(s.ServiceProvider, Direction);
            var ledger = (await s.ServiceProvider.GetRequiredService<ICustomerAccountQueries>().LedgerAsync(customer.Id, null, null))!;
            ledger.Entries.Select(e => e.EntryType).Should().Equal("credit_sale", "payment", "payment_void");
        }

        // Le journal client est immuable (§33) : ni modification ni suppression.
        var update = () => Exec($"UPDATE nexora.customer_ledger_entry SET credit = 0 WHERE customer_id = '{customer.Id}'");
        await update.Should().ThrowAsync<Npgsql.PostgresException>();
        var delete = () => Exec($"DELETE FROM nexora.customer_ledger_entry WHERE customer_id = '{customer.Id}'");
        await delete.Should().ThrowAsync<Npgsql.PostgresException>();
    }

    // ---- §31 test 9 / §12 : limite, dépassement autorisé, échéance impayée, client inactif ----
    [Fact]
    public async Task Credit_control_enforces_limit_overdue_debts_and_inactive_customers()
    {
        var product = await Product(100m);
        var small = await NewCustomer(1_000m);
        await using (var s = Scope())
        {
            As(s.ServiceProvider, Direction);
            var sales = s.ServiceProvider.GetRequiredService<SalesService>();
            var over = await sales.CheckoutAsync(new CheckoutCommand(Guid.NewGuid(), Officine, null, null, null, small.Id, "HTG",
                [new CheckoutLine(product, 15m, null, 0m, null)], [], 0m, null, null, null, null));
            over.Error!.Code.Should().Be("pos.credit_limit");
        }
        (await CreditSale(small.Id, product, 15m, overrideReason: "Client stratégique — accord direction")).CreditOverrideApplied.Should().BeTrue();

        // Une échéance dépassée bloque tout nouveau crédit (le statut OVERDUE n'est jamais stocké : lu sur la date).
        var late = await NewCustomer(10_000m);
        await CreditSale(late.Id, product, 5m, due: Today.AddDays(-3));
        await using (var s = Scope())
        {
            As(s.ServiceProvider, Direction);
            var decision = await s.ServiceProvider.GetRequiredService<ICreditControl>().EvaluateAsync(late.Id, "HTG", 100m);
            decision.Allowed.Should().BeFalse();
            decision.Message.Should().Contain("échue");
        }

        // Fiche inactive : refus sans dérogation possible ; archivage refusé tant que le compte n'est pas soldé.
        var idle = await NewCustomer(10_000m);
        await using (var s = Scope())
        {
            As(s.ServiceProvider, Direction);
            var customers = s.ServiceProvider.GetRequiredService<ICustomerService>();
            (await customers.ChangeStatusAsync(idle.Id, new ChangeCustomerStatusRequest("inactive", "Client parti à l'étranger")))
                .Value!.Status.Should().Be("inactive");
            var decision = await s.ServiceProvider.GetRequiredService<ICreditControl>().EvaluateAsync(idle.Id, "HTG", 100m);
            decision.Allowed.Should().BeFalse();
            decision.RequiresOverride.Should().BeFalse();
            (await customers.ChangeStatusAsync(late.Id, new ChangeCustomerStatusRequest("archived", "Fin de relation")))
                .Error!.Code.Should().Be("customer.not_settled");
        }
    }

    // ---- §31 test 10 / §13 : échéances et aging ----
    [Fact]
    public async Task Aging_splits_not_yet_due_and_overdue_by_bucket()
    {
        var customer = await NewCustomer(1_000_000m);
        foreach (var (amount, offset) in new[] { (100m, 5), (200m, -10), (300m, -45), (400m, -75), (500m, -100) })
            await Exec($"""
                INSERT INTO nexora.customer_receivable (company_id, customer_id, currency_id, original_amount, issue_date, due_date, status)
                VALUES ('{Company}', '{customer.Id}', (SELECT id FROM nexora.currency WHERE code='HTG'),
                        {amount.ToString(CultureInfo.InvariantCulture)}, '{Today.AddDays(-120):yyyy-MM-dd}',
                        '{Today.AddDays(offset):yyyy-MM-dd}', 'OPEN')
                """);

        await using var s = Scope();
        As(s.ServiceProvider, Direction);
        var credits = await s.ServiceProvider.GetRequiredService<ICustomerAccountQueries>().CreditsAsync(customer.Id, null);
        credits.Should().HaveCount(5);
        credits.Single(c => c.OriginalAmount == 100m).DisplayStatus.Should().Be("open");
        credits.Single(c => c.OriginalAmount == 500m).Should().Match<CustomerCreditDto>(c => c.DisplayStatus == "overdue" && c.DaysOverdue == 100);

        var aging = await s.ServiceProvider.GetRequiredService<ICustomerReports>().RunAsync("aging", null, null);
        aging.IsSuccess.Should().BeTrue();
        var row = aging.Value!.Rows.Single(r => r[0] == customer.CustomerNumber);
        row.Skip(3).Take(6).Should().Equal("100.00", "200.00", "300.00", "400.00", "500.00", "1500.00");

        var summary = await Summary(customer.Id);
        summary.OverdueAmount.Should().Be(1_400m);
        summary.OverdueCredits.Should().Be(4);
        summary.OldestOverdueDays.Should().Be(100);
        summary.Signals.Should().Contain(x => x.Code == "late_payer" && x.Explanation.StartsWith("Règle"));
        summary.LedgerConsistent.Should().BeTrue();
    }

    // ---- §31 test 11 / §11 : retour validé par un Manager → dette réduite, reliquat en avoir, remboursement ----
    [Fact]
    public async Task Manager_validated_return_reduces_the_debt_and_the_rest_becomes_a_refundable_credit_memo()
    {
        var cashier = await UserWithRole("cashier");
        var manager = await UserWithRole("manager");
        var customer = await NewCustomer(50_000m);
        var sale = await CreditSale(customer.Id, await Product(100m), 10m);   // 1 000 à crédit
        await using (var s = Scope())
        {
            As(s.ServiceProvider, Direction);
            (await s.ServiceProvider.GetRequiredService<ICustomerAccountService>().RecordCreditPaymentAsync(sale.ReceivableId!.Value,
                new RecordCreditPaymentRequest(Guid.NewGuid(), 700m, CashAccount, CashMethod))).IsSuccess.Should().BeTrue();
        }

        Guid requestId;
        await using (var s = Scope())
        {
            As(s.ServiceProvider, cashier);
            var db = s.ServiceProvider.GetRequiredService<NexoraDbContext>();
            var line = await db.SaleLines.AsNoTracking().Where(l => l.SaleId == sale.SaleId).Select(l => l.Id).FirstAsync();
            var request = await s.ServiceProvider.GetRequiredService<IReturnRequestService>().RequestAsync(new CreateReturnRequest(
                Guid.NewGuid(), sale.SaleId, Officine, null, "Produits abîmés", "original", CashAccount,
                [new ReturnRequestLineInput(line, 5m)]));
            request.IsSuccess.Should().BeTrue(request.Error?.Message);
            requestId = request.Value!.Id;
        }
        (await Summary(customer.Id)).BalanceDue.Should().Be(300m, "une demande de retour ne touche pas le compte");
        await using (var s = Scope())
        {
            As(s.ServiceProvider, manager);
            var approved = await s.ServiceProvider.GetRequiredService<IReturnRequestService>().ApproveAsync(requestId, new DecideRequest("Constaté"));
            approved.IsSuccess.Should().BeTrue(approved.Error?.Message);
        }

        var afterReturn = await Summary(customer.Id);
        afterReturn.BalanceDue.Should().Be(0m, "le retour (500) solde d'abord le reste dû (300)");
        afterReturn.AvailableStoreCredit.Should().Be(200m, "le reliquat devient un avoir disponible");
        afterReturn.ReturnsAmount.Should().Be(500m);
        afterReturn.LedgerConsistent.Should().BeTrue();
        (await GeneralLedgerBalance(customer.Id)).Should().Be(0m);
        (await GeneralLedgerBalance(customer.Id, "4471")).Should().Be(-200m, "l'avoir est une dette envers le client");

        Guid memoId, refundId;
        await using (var s = Scope())
        {
            As(s.ServiceProvider, manager);
            var memo = (await s.ServiceProvider.GetRequiredService<ICustomerAccountQueries>().CreditMemosAsync(customer.Id, true)).Single();
            memo.Should().Match<CreditMemoDto>(m => m.Amount == 500m && m.UsedAmount == 300m && m.Status == "partially_used"
                                                    && m.MemoNumber.StartsWith("AVC-"));
            memoId = memo.Id;
            var refunds = s.ServiceProvider.GetRequiredService<ICustomerRefundService>();
            (await refunds.RequestAsync(new RequestCustomerRefundRequest(Guid.NewGuid(), memoId, 250m, "cash", "Trop")))
                .Error!.Code.Should().Be("credit.refund_exceeds");
            var requested = await refunds.RequestAsync(new RequestCustomerRefundRequest(Guid.NewGuid(), memoId, 200m, "cash", "Le client préfère être remboursé"));
            requested.IsSuccess.Should().BeTrue(requested.Error?.Message);
            refundId = requested.Value!.Id;
            (await refunds.ApproveAsync(refundId, new CustomerDecisionRequest(null))).Error!.Code.Should().Be("credit.self_approval");
            (await refunds.ExecuteAsync(refundId, new ExecuteCustomerRefundRequest(CashAccount))).Error!.Code.Should().Be("credit.refund_not_approved");
        }
        await using (var s = Scope())
        {
            As(s.ServiceProvider, Direction);
            (await s.ServiceProvider.GetRequiredService<ICustomerRefundService>().ApproveAsync(refundId, new CustomerDecisionRequest("Accordé")))
                .Value!.Status.Should().Be("approved");
        }
        await using (var s = Scope())
        {
            As(s.ServiceProvider, manager);
            var executed = await s.ServiceProvider.GetRequiredService<ICustomerRefundService>().ExecuteAsync(refundId, new ExecuteCustomerRefundRequest(CashAccount));
            executed.IsSuccess.Should().BeTrue(executed.Error?.Message);
            executed.Value!.Status.Should().Be("executed");
        }

        var end = await Summary(customer.Id);
        end.AvailableStoreCredit.Should().Be(0m);
        end.NetBalance.Should().Be(0m);
        end.LedgerConsistent.Should().BeTrue();
        (await GeneralLedgerBalance(customer.Id, "4471")).Should().Be(0m);
        await using (var s = Scope())
        {
            As(s.ServiceProvider, Direction);
            var ledger = (await s.ServiceProvider.GetRequiredService<ICustomerAccountQueries>().LedgerAsync(customer.Id, null, null))!;
            ledger.Entries.Select(e => e.EntryType).Should().Equal("credit_sale", "payment", "sale_return", "refund");
            ledger.ClosingBalance.Should().Be(0m);
        }
    }

    // ---- §10 : dépense annulée après validation → contre-écriture, crédit annulé, rien supprimé ----
    [Fact]
    public async Task Cancelling_a_validated_expense_reverses_it_and_cancels_its_credit()
    {
        var manager = await UserWithRole("manager");
        var customer = await NewCustomer(10_000m);
        Guid expense;
        await using (var s = Scope())
        {
            As(s.ServiceProvider, Direction);
            var svc = s.ServiceProvider.GetRequiredService<ICustomerExpenseService>();
            (await svc.CreateAsync(customer.Id, new CreateCustomerExpenseRequest(Guid.NewGuid(), "pizza", "x", 10m)))
                .Error!.Code.Should().Be("customer_expense.category");
            expense = (await svc.CreateAsync(customer.Id, new CreateCustomerExpenseRequest(Guid.NewGuid(), "transport", "Transport chantier", 1_200m))).Value!.Id;
        }
        await using (var s = Scope())
        {
            As(s.ServiceProvider, manager);
            var svc = s.ServiceProvider.GetRequiredService<ICustomerExpenseService>();
            (await svc.ApproveAsync(expense, new CustomerDecisionRequest(null))).IsSuccess.Should().BeTrue();
            (await Summary(customer.Id)).BalanceDue.Should().Be(1_200m);
            (await svc.CancelAsync(expense, new CustomerCancelRequest("x"))).Error!.Code.Should().Be("customer_expense.cancel_reason");
            var cancelled = await svc.CancelAsync(expense, new CustomerCancelRequest("Transport finalement offert"));
            cancelled.IsSuccess.Should().BeTrue(cancelled.Error?.Message);
            cancelled.Value!.Status.Should().Be("cancelled");
        }
        var summary = await Summary(customer.Id);
        summary.BalanceDue.Should().Be(0m);
        summary.LedgerConsistent.Should().BeTrue();
        (await GeneralLedgerBalance(customer.Id)).Should().Be(0m);
        var delete = () => Exec($"DELETE FROM nexora.customer_expense WHERE id = '{expense}'");
        await delete.Should().ThrowAsync<Npgsql.PostgresException>("une dépense validée ne se supprime jamais");
    }

    // ---- §31 tests 13-14 : deux entreprises, identifiants d'une autre entreprise ----
    [Fact]
    public async Task Another_company_cannot_read_or_move_this_customers_account()
    {
        var customer = await NewCustomer(50_000m);
        var sale = await CreditSale(customer.Id, await Product(100m), 10m);
        Guid payment;
        await using (var s = Scope())
        {
            As(s.ServiceProvider, Direction);
            payment = (await s.ServiceProvider.GetRequiredService<ICustomerAccountService>().RecordCreditPaymentAsync(
                sale.ReceivableId!.Value, new RecordCreditPaymentRequest(Guid.NewGuid(), 100m, CashAccount, CashMethod))).Value!.Id;
        }

        await using (var s = Scope())
        {
            As(s.ServiceProvider, Direction, Restaurant);
            var sp = s.ServiceProvider;
            (await sp.GetRequiredService<ICustomerService>().GetAsync(customer.Id)).Should().BeNull();
            (await sp.GetRequiredService<ICustomerAccountQueries>().SummaryAsync(customer.Id, null, null)).Should().BeNull();
            (await sp.GetRequiredService<ICustomerAccountQueries>().CreditAsync(sale.ReceivableId!.Value)).Should().BeNull();
            (await sp.GetRequiredService<ICustomerAccountQueries>().PaymentAsync(payment)).Should().BeNull();
            var svc = sp.GetRequiredService<ICustomerAccountService>();
            (await svc.RecordCreditPaymentAsync(sale.ReceivableId!.Value, new RecordCreditPaymentRequest(Guid.NewGuid(), 50m, CashAccount, CashMethod)))
                .Error!.Code.Should().Be("credit.not_found");
            (await svc.VoidPaymentAsync(payment, new VoidPaymentRequest("Tentative hors périmètre"))).Error!.Code.Should().Be("credit.payment_not_found");
            (await sp.GetRequiredService<ICustomerExpenseService>().CreateAsync(customer.Id,
                new CreateCustomerExpenseRequest(Guid.NewGuid(), "delivery", "Hors périmètre", 10m))).Error!.Code.Should().Be("customer.not_found");
        }

        // Barrière base de données (rôle applicatif réel, RLS) : le journal d'un client n'existe pas pour l'autre entreprise.
        await using var appRole = fx.CreateAppRoleProvider();
        await using (var s = appRole.CreateAsyncScope())
        {
            As(s.ServiceProvider, Direction, Restaurant);
            var db = s.ServiceProvider.GetRequiredService<NexoraDbContext>();
            var visible = await db.Database.SqlQuery<int>($"""
                SELECT count(*)::int AS "Value" FROM nexora.customer_ledger_entry WHERE customer_id = {customer.Id}
                """).FirstAsync();
            visible.Should().Be(0);
        }
    }

    // ---- §31 test 15 / §24 : vente à crédit synchronisée hors ligne, rejouée → un seul crédit ----
    [Fact]
    public async Task Offline_credit_sale_replayed_twice_creates_one_credit_and_one_ledger_entry()
    {
        var customer = await NewCustomer(500_000m);
        var product = await Product(100m);
        await using var s = Scope();
        As(s.ServiceProvider, Direction);
        var sync = s.ServiceProvider.GetRequiredService<ISyncService>();
        var policy = s.ServiceProvider.GetRequiredService<IOfflinePolicyService>();
        var deviceCode = $"CA-{Guid.NewGuid():N}"[..12];
        var device = (await s.ServiceProvider.GetRequiredService<IDeviceRegistryService>()
            .RegisterAsync(new RegisterDeviceRequest(deviceCode, deviceCode, "pos", null, null))).Value;
        var op = Guid.NewGuid();
        var cmd = new CheckoutCommand(op, Officine, null, null, null, customer.Id, "HTG",
            [new CheckoutLine(product, 4m, 100m, 0m, null)], [], 0m, null, null, null, null);
        var input = new SyncOperationInput(op, "sale", "create", JsonSerializer.SerializeToElement(cmd));

        (await policy.SetAsync(new SetOfflinePolicyRequest(null, true, 1_000_000m, null, null, null, null, null, null))).IsSuccess.Should().BeTrue();
        try
        {
            (await sync.PushBatchAsync(new PushBatchRequest(device, [input]))).Value!.Operations[0].Status.Should().Be("SYNCED");
            (await sync.PushBatchAsync(new PushBatchRequest(device, [input]))).IsSuccess.Should().BeTrue();
        }
        finally
        {
            await policy.SetAsync(new SetOfflinePolicyRequest(null, false, 0m, null, null, null, null, null, null));
        }
        (await Scalar<long>($"SELECT count(*) FROM nexora.customer_receivable WHERE customer_id = '{customer.Id}'")).Should().Be(1);
        (await Scalar<long>($"SELECT count(*) FROM nexora.customer_ledger_entry WHERE customer_id = '{customer.Id}'")).Should().Be(1);
        (await Summary(customer.Id)).BalanceDue.Should().Be(400m);
    }

    // ---- §31 test 16 / §26 : rapports ; §17 recherche ; §6 fiche enrichie ----
    [Fact]
    public async Task Reports_run_lookup_finds_by_phone_and_credit_number_and_the_profile_keeps_its_fields()
    {
        var customer = await NewCustomer(20_000m, creditDays: 15);
        customer.City.Should().Be("Pétion-Ville");
        customer.CreditDays.Should().Be(15);
        var sale = await CreditSale(customer.Id, await Product(100m), 2m);

        await using var s = Scope();
        As(s.ServiceProvider, Direction);
        var sp = s.ServiceProvider;
        var credit = (await sp.GetRequiredService<ICustomerAccountQueries>().CreditAsync(sale.ReceivableId!.Value))!.Credit;
        credit.DueDate.Should().Be(Today.AddDays(15), "la durée de crédit de la fiche prime sur le réglage");

        var lookup = sp.GetRequiredService<ICustomerAccountQueries>();
        (await lookup.LookupAsync("4455")).Should().Contain(c => c.Id == customer.Id && c.PhoneHint == "4455");
        (await lookup.LookupAsync(credit.CreditNumber)).Should().ContainSingle(c => c.Id == customer.Id);
        (await lookup.LookupAsync("x")).Should().BeEmpty("2 caractères minimum");

        // PUT d'un écran qui ne connaît pas les champs du module : ils sont conservés.
        var customers = sp.GetRequiredService<ICustomerService>();
        var updated = await customers.UpdateAsync(customer.Id, new UpdateCustomerRequest(customer.Name, "individual", Phone: "+509 3712 4455"));
        updated.Value!.City.Should().Be("Pétion-Ville");
        updated.Value.Segment.Should().Be("Grossiste");
        var patched = await customers.PatchAsync(customer.Id, new PatchCustomerRequest(City: "Jacmel"));
        patched.Value!.City.Should().Be("Jacmel");
        patched.Value.Phone.Should().Be("+509 3712 4455");

        var reports = sp.GetRequiredService<ICustomerReports>();
        foreach (var def in reports.Catalog())
        {
            var run = await reports.RunAsync(def.Key, Today.AddDays(-7), Today);
            run.IsSuccess.Should().BeTrue($"{def.Key} : {run.Error?.Message}");
            run.Value!.Columns.Should().NotBeEmpty();
            run.Value.Rows.Where(r => r.Count != run.Value.Columns.Count).Should().BeEmpty(def.Key);
        }
        (await reports.RunAsync("balances", null, null)).Value!.Rows.Should().Contain(r => r[0] == customer.CustomerNumber);
        (await reports.RunAsync("inexistant", null, null)).Error!.Code.Should().Be("customer_report.unknown");

        var timeline = await sp.GetRequiredService<ICustomerAccountQueries>().TimelineAsync(customer.Id);
        timeline.Should().Contain(e => e.Category == "profile" && e.Title == "Fiche modifiée" && e.Detail!.Contains("ville"));
    }

    // ---- §27 : « Combien ce client me doit-il aujourd'hui ? » — réponse issue du compte client, calcul affiché ----
    [Fact]
    public async Task Assistant_answers_how_much_a_customer_owes_from_the_server_side_account()
    {
        var customer = await NewCustomer(50_000m);
        await CreditSale(customer.Id, await Product(100m), 12m);
        await using var s = Scope();
        As(s.ServiceProvider, Direction);
        var ask = s.ServiceProvider.GetRequiredService<Nexora.Application.Reporting.IAskNexora>();

        var inContext = await ask.AskAsync(new Nexora.Contracts.Reporting.AskRequest("Combien ce client me doit-il aujourd'hui ?", null, customer.Id));
        inContext.Intent.Should().Be("customer_balance");
        inContext.Answer.Should().Contain("vous doit aujourd'hui").And.Contain("200,00 HTG").And.Contain("Calcul");
        inContext.Facts.Should().Contain(f => f.Key == "customer_net_balance" && f.Value == 1_200m);

        var byNumber = await ask.AskAsync(new Nexora.Contracts.Reporting.AskRequest($"Combien {customer.CustomerNumber} me doit-il ?", null));
        byNumber.Facts.Should().Contain(f => f.Key == "customer_net_balance" && f.Value == 1_200m);
        (await ask.AskAsync(new Nexora.Contracts.Reporting.AskRequest("Combien ce client me doit-il ?", null)))
            .Answer.Should().StartWith("Précisez le client");
    }

    // ---- §13 : actions de recouvrement journalisées ----
    [Fact]
    public async Task Collection_actions_are_logged_and_listed_when_due()
    {
        var customer = await NewCustomer(10_000m);
        await using var s = Scope();
        As(s.ServiceProvider, Direction);
        var svc = s.ServiceProvider.GetRequiredService<ICollectionActionService>();
        (await svc.LogAsync(customer.Id, new CreateCollectionActionRequest("pigeon", "x"))).Error!.Code.Should().Be("collection.action_type");
        var logged = await svc.LogAsync(customer.Id, new CreateCollectionActionRequest("call", "Client injoignable, rappeler", "no_answer",
            NextActionDate: Today));
        logged.IsSuccess.Should().BeTrue(logged.Error?.Message);
        (await svc.ListAsync(null, onlyDue: true)).Should().Contain(a => a.Id == logged.Value!.Id);
        (await svc.ListAsync(customer.Id, onlyDue: false)).Should().ContainSingle()
            .Which.Should().Match<CollectionActionDto>(a => a.Outcome == "no_answer" && a.CreatedByName != null);
    }

    // ---- Numérotation automatique : des créations simultanées (plusieurs caisses) n'entrent jamais en collision ----
    [Fact]
    public async Task Customers_and_suppliers_created_at_the_same_time_get_distinct_numbers()
    {
        async Task<string> Customer(int i)
        {
            await using var s = Scope();
            As(s.ServiceProvider, Direction);
            var r = await s.ServiceProvider.GetRequiredService<ICustomerService>().CreateAsync(new CreateCustomerRequest($"Client simultané {i} {Guid.NewGuid():N}"[..30]));
            r.IsSuccess.Should().BeTrue(r.Error?.Message);
            return r.Value!.CustomerNumber;
        }
        async Task<string> Supplier(int i)
        {
            await using var s = Scope();
            As(s.ServiceProvider, Direction);
            var r = await s.ServiceProvider.GetRequiredService<ISupplierService>().CreateAsync(new CreateSupplierRequest($"Fournisseur simultané {i} {Guid.NewGuid():N}"[..34]));
            r.IsSuccess.Should().BeTrue(r.Error?.Message);
            return r.Value!.SupplierNumber;
        }

        var customers = await Task.WhenAll(Enumerable.Range(1, 12).Select(Customer));
        var suppliers = await Task.WhenAll(Enumerable.Range(1, 12).Select(Supplier));
        customers.Should().OnlyHaveUniqueItems().And.OnlyContain(n => n.StartsWith("CLI-"));
        suppliers.Should().OnlyHaveUniqueItems().And.OnlyContain(n => n.StartsWith("FRN-"));

        // Un numéro saisi à la main déjà pris reste refusé proprement (pas d'exception).
        await using var scope = Scope();
        As(scope.ServiceProvider, Direction);
        var dup = await scope.ServiceProvider.GetRequiredService<ICustomerService>().CreateAsync(new CreateCustomerRequest("Doublon", CustomerNumber: customers[0]));
        dup.Error!.Code.Should().Be("customer.number_exists");
    }
}
