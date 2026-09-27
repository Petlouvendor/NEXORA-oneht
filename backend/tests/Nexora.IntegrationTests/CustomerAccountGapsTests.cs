using System.Globalization;
using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Nexora.Application.Abstractions;
using Nexora.Application.Catalog;
using Nexora.Application.Customers;
using Nexora.Application.Identity;
using Nexora.Application.Parties;
using Nexora.Application.Purchasing;
using Nexora.Application.Sales;
using Nexora.Application.Sync;
using Nexora.Contracts.Customers;
using Nexora.Contracts.Identity;
using Nexora.Contracts.Parties;
using Nexora.Contracts.Sync;
using Nexora.Infrastructure.Persistence;

namespace Nexora.IntegrationTests;

/// <summary>
/// Module clients — correction des écarts de V073 (V074) : échéancier (§8), crédit hors vente (§22),
/// paiement par avoir (§7, §15), dépassement par le workflow d'approbation configuré (§12), devise
/// explicite (§21), versement hors ligne (§24), reprise des retours antérieurs.
/// </summary>
[Collection("finance")]
public sealed class CustomerAccountGapsTests(FinanceEngineFixture fx)
{
    private static readonly Guid Company = Guid.Parse("dce00000-0000-0000-0000-0000000000c1");
    private static readonly Guid Direction = Guid.Parse("dce00000-0000-0000-0000-0000000000d1");
    private static readonly Guid Officine = Guid.Parse("dce00000-0000-0000-0000-00000000a003");
    private static readonly Guid Supplier1 = Guid.Parse("dce00000-0000-0000-0000-00000000b101");
    private static readonly Guid BoxUnit = Guid.Parse("dce00000-0000-0000-0000-00000000a011");
    private static readonly Guid CashAccount = Guid.Parse("dce00000-0000-0000-0000-00000000a051");
    private static readonly Guid CashMethod = Guid.Parse("dce00000-0000-0000-0000-00000000a081");
    private static DateOnly Today => DateOnly.FromDateTime(DateTime.UtcNow);

    private AsyncServiceScope Scope() => fx.Provider.CreateAsyncScope();

    private static void As(IServiceProvider sp, Guid userId)
    {
        var tc = (TestTenantContext)sp.GetRequiredService<ITenantContext>();
        tc.SetUser(userId);
        tc.SetScope([Company], [], Company);
    }

    private async Task<Guid> UserWithRole(string role)
    {
        var id = Guid.NewGuid();
        await Exec($"""
            INSERT INTO nexora.app_user (id, email, password_hash, full_name, status)
            VALUES ('{id}', 'GAP-{id:N}@test.demo', crypt('x', gen_salt('bf', 4)), '{role} écarts', 'active')
            """);
        await using var s = Scope();
        As(s.ServiceProvider, Direction);
        (await s.ServiceProvider.GetRequiredService<IUserAssignmentService>()
            .GrantAsync(id, new GrantAssignmentRequest(role, "COMPANY", Company, null, null, null, null))).IsSuccess.Should().BeTrue();
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

    private async Task<CustomerDto> NewCustomer(decimal limit)
    {
        await using var s = Scope();
        As(s.ServiceProvider, Direction);
        return (await s.ServiceProvider.GetRequiredService<ICustomerService>().CreateAsync(new CreateCustomerRequest(
            $"Client écart {Guid.NewGuid():N}"[..26], CreditLimit: limit))).Value!;
    }

    private async Task<Guid> Product(decimal price)
    {
        await using var s = Scope();
        As(s.ServiceProvider, Direction);
        var catalog = s.ServiceProvider.GetRequiredService<CatalogService>();
        var id = (await catalog.CreateProductAsync(new CreateProductCommand(
            $"GAP-{Guid.NewGuid():N}"[..20], "Article écart", null, BoxUnit, "stockable", null, null, null, null, false, false))).Value;
        await catalog.SetPriceAsync(new SetProductPriceCommand(id, "sale", price, "HTG", 0m, null, null));
        await s.ServiceProvider.GetRequiredService<PurchasingService>().ReceiveAsync(new ReceiveGoodsCommand(
            Guid.NewGuid(), Supplier1, Officine, null, [new ReceiveLine(id, null, 500m, price / 2m, null, null)], null, null));
        return id;
    }

    private static CheckoutCommand Sale(Guid customer, Guid product, decimal qty, IReadOnlyList<TenderInput>? tenders = null,
        decimal storeCredit = 0m, Guid? op = null)
        => new(op ?? Guid.NewGuid(), Officine, null, null, null, customer, "HTG", [new CheckoutLine(product, qty, null, 0m, null)],
            tenders ?? [], 0m, null, null, null, null, storeCredit);

    private async Task<CheckoutResult> Checkout(CheckoutCommand cmd, Guid? user = null)
    {
        await using var s = Scope();
        As(s.ServiceProvider, user ?? Direction);
        var r = await s.ServiceProvider.GetRequiredService<SalesService>().CheckoutAsync(cmd);
        r.IsSuccess.Should().BeTrue(r.Error?.Message);
        return r.Value!;
    }

    private async Task<CustomerAccountSummaryDto> Summary(Guid customer)
    {
        await using var s = Scope();
        As(s.ServiceProvider, Direction);
        return (await s.ServiceProvider.GetRequiredService<ICustomerAccountQueries>().SummaryAsync(customer, null, null))!;
    }

    private Task<decimal> GeneralLedgerBalance(Guid customer, string accountCode) => Scalar<decimal>($"""
        SELECT COALESCE(SUM(l.base_debit - l.base_credit), 0) FROM nexora.journal_entry_line l
        JOIN nexora.account a ON a.id = l.account_id
        WHERE l.third_party_id = '{customer}' AND a.code = '{accountCode}'
        """);

    // ---- §8 : échéancier — la prochaine échéance devient l'échéance du crédit ----
    [Fact]
    public async Task Installment_schedule_drives_the_next_due_date_and_follows_payments()
    {
        var customer = await NewCustomer(50_000m);
        var sale = await Checkout(Sale(customer.Id, await Product(100m), 9m));   // 900 à crédit
        var credit = sale.ReceivableId!.Value;

        await using var s = Scope();
        As(s.ServiceProvider, Direction);
        var accounts = s.ServiceProvider.GetRequiredService<ICustomerAccountService>();
        (await accounts.ScheduleAsync(credit, new ScheduleInstallmentsRequest("Plan de paiement",
            [new InstallmentInput(Today.AddDays(10), 400m), new InstallmentInput(Today.AddDays(40), 400m)])))
            .Error!.Code.Should().Be("credit.schedule_total", "400 + 400 ≠ 900");

        var planned = await accounts.ScheduleAsync(credit, new ScheduleInstallmentsRequest("Plan en 3 mois", Count: 3,
            FirstDueDate: Today.AddDays(15)));
        planned.IsSuccess.Should().BeTrue(planned.Error?.Message);
        planned.Value!.Schedule!.Installments.Select(i => i.Amount).Should().Equal(300m, 300m, 300m);
        planned.Value.Credit.DueDate.Should().Be(Today.AddDays(15));
        planned.Value.Credit.HasSchedule.Should().BeTrue();

        (await accounts.RecordCreditPaymentAsync(credit, new RecordCreditPaymentRequest(Guid.NewGuid(), 350m, CashAccount, CashMethod)))
            .IsSuccess.Should().BeTrue();
        var detail = (await s.ServiceProvider.GetRequiredService<ICustomerAccountQueries>().CreditAsync(credit))!;
        detail.Schedule!.Installments.Select(i => i.Status).Should().Equal("paid", "partial", "upcoming");
        detail.Schedule.Installments[1].Covered.Should().Be(50m);
        detail.Credit.DueDate.Should().Be(Today.AddDays(15).AddMonths(1), "la prochaine échéance non couverte est la 2e");

        // Un nouvel échéancier remplace l'actif sans l'effacer.
        var replanned = await accounts.ScheduleAsync(credit, new ScheduleInstallmentsRequest("Report accordé",
            [new InstallmentInput(Today.AddDays(60), 550m)]));
        replanned.IsSuccess.Should().BeTrue(replanned.Error?.Message);
        replanned.Value!.Schedule!.Version.Should().Be(2);
        replanned.Value.Credit.DueDate.Should().Be(Today.AddDays(60));
        (await Scalar<long>($"SELECT count(*) FROM nexora.credit_schedule WHERE receivable_id = '{credit}' AND status = 'replaced'")).Should().Be(1);
        var update = () => Exec($"UPDATE nexora.credit_installment SET amount = 1 WHERE receivable_id = '{credit}'");
        await update.Should().ThrowAsync<Npgsql.PostgresException>("une échéance ne se modifie pas");
    }

    // ---- §22 : crédit hors vente (reprise de solde), idempotent, contrepartie en attente ----
    [Fact]
    public async Task Opening_credit_creates_a_traced_debt_once_with_a_suspense_counterpart()
    {
        var customer = await NewCustomer(0m);
        var op = Guid.NewGuid();
        await using var s = Scope();
        As(s.ServiceProvider, Direction);
        var accounts = s.ServiceProvider.GetRequiredService<ICustomerAccountService>();
        (await accounts.OpenCreditAsync(customer.Id, new OpenCustomerCreditRequest(op, 5_000m, "x"))).Error!.Code.Should().Be("credit.reason");

        var opened = await accounts.OpenCreditAsync(customer.Id, new OpenCustomerCreditRequest(op, 5_000m,
            "Reprise du solde de l'ancien logiciel au 31/08", Today.AddDays(-20)));
        opened.IsSuccess.Should().BeTrue(opened.Error?.Message);
        opened.Value!.Credit.SourceType.Should().Be("opening");
        opened.Value.Credit.DisplayStatus.Should().Be("overdue", "une reprise peut porter une échéance déjà dépassée");
        (await accounts.OpenCreditAsync(customer.Id, new OpenCustomerCreditRequest(op, 5_000m, "Rejeu du même envoi")))
            .Value!.Credit.Id.Should().Be(opened.Value.Credit.Id);

        (await Scalar<long>($"SELECT count(*) FROM nexora.customer_receivable WHERE customer_id = '{customer.Id}'")).Should().Be(1);
        (await Scalar<string>($"SELECT entry_type FROM nexora.customer_ledger_entry WHERE customer_id = '{customer.Id}'")).Should().Be("opening");
        (await GeneralLedgerBalance(customer.Id, "4111")).Should().Be(5_000m);
        (await Summary(customer.Id)).LedgerConsistent.Should().BeTrue();
    }

    // ---- §7, §15 : l'avoir disponible paie une vente à la caisse ----
    [Fact]
    public async Task Store_credit_pays_a_sale_at_checkout_and_is_consumed_oldest_first()
    {
        var customer = await NewCustomer(0m);   // aucun crédit : l'avoir n'en est pas un
        var product = await Product(100m);
        // Avoir de 500 : vente comptant puis retour « en avoir ».
        var cash = await Checkout(Sale(customer.Id, product, 5m, [new TenderInput(CashAccount, CashMethod, 500m, null)]));
        await using (var s = Scope())
        {
            As(s.ServiceProvider, Direction);
            var line = await s.ServiceProvider.GetRequiredService<NexoraDbContext>().SaleLines.AsNoTracking()
                .Where(l => l.SaleId == cash.SaleId).Select(l => l.Id).FirstAsync();
            (await s.ServiceProvider.GetRequiredService<SalesReturnService>().ReturnAsync(new ReturnCommand(
                Guid.NewGuid(), cash.SaleId, Officine, null, "Échange", "store_credit", null, [new ReturnLineInput(line, 5m, true)])))
                .IsSuccess.Should().BeTrue();
        }
        (await Summary(customer.Id)).AvailableStoreCredit.Should().Be(500m);

        await using (var s = Scope())
        {
            As(s.ServiceProvider, Direction);
            var refused = await s.ServiceProvider.GetRequiredService<SalesService>().CheckoutAsync(Sale(customer.Id, product, 7m, storeCredit: 700m));
            refused.Error!.Code.Should().Be("pos.store_credit_insufficient");
        }

        // Vente de 300 : 200 d'avoir + 100 en espèces.
        var paid = await Checkout(Sale(customer.Id, product, 3m, [new TenderInput(CashAccount, CashMethod, 100m, null)], storeCredit: 200m));
        paid.StoreCreditUsed.Should().Be(200m);
        paid.CreditAmount.Should().Be(0m, "l'avoir n'est pas un crédit : aucune limite n'est requise");

        var after = await Summary(customer.Id);
        after.AvailableStoreCredit.Should().Be(300m);
        after.BalanceDue.Should().Be(0m);
        after.LedgerConsistent.Should().BeTrue();
        (await GeneralLedgerBalance(customer.Id, "4111")).Should().Be(0m);
        (await GeneralLedgerBalance(customer.Id, "4471")).Should().Be(-300m);
    }

    // ---- §12 : dépassement par le workflow d'approbation configuré (niveaux, 4-yeux, usage unique) ----
    [Fact]
    public async Task Credit_overrun_goes_through_the_configured_approval_workflow_once()
    {
        var cashier = await UserWithRole("cashier");
        var manager = await UserWithRole("manager");
        var customer = await NewCustomer(1_000m);
        var product = await Product(100m);
        var cart = Guid.NewGuid();

        await using (var s = Scope())
        {
            As(s.ServiceProvider, cashier);
            (await s.ServiceProvider.GetRequiredService<SalesService>().CheckoutAsync(Sale(customer.Id, product, 15m, op: cart)))
                .Error!.Code.Should().Be("pos.credit_limit");
        }

        Guid requestId;
        await using (var s = Scope())
        {
            As(s.ServiceProvider, cashier);
            var overrides = s.ServiceProvider.GetRequiredService<ICreditOverrideService>();
            var requested = await overrides.RequestAsync(new CreditOverrideRequestInput(cart, customer.Id, 1_500m, "HTG", "Commande urgente d'un client fidèle"));
            requested.IsSuccess.Should().BeTrue(requested.Error?.Message);
            requested.Value!.Status.Should().Be("pending");
            requestId = requested.Value.Id;
            (await overrides.ApproveAsync(requestId, null)).Error!.Code.Should().Be("security.self_approval");
        }
        await using (var s = Scope())
        {
            As(s.ServiceProvider, cashier);
            (await s.ServiceProvider.GetRequiredService<SalesService>().CheckoutAsync(Sale(customer.Id, product, 15m, op: cart)))
                .Error!.Code.Should().Be("pos.credit_limit", "tant que la demande n'est pas approuvée");
        }
        await using (var s = Scope())
        {
            As(s.ServiceProvider, manager);
            (await s.ServiceProvider.GetRequiredService<ICreditOverrideService>().ApproveAsync(requestId, "Client connu"))
                .Value!.Status.Should().Be("approved");
        }

        var sold = await Checkout(Sale(customer.Id, product, 15m, op: cart), cashier);
        sold.CreditOverrideApplied.Should().BeTrue();
        (await Scalar<string>($"SELECT credit_override_by::text FROM nexora.sale WHERE id = '{sold.SaleId}'")).Should().Be(manager.ToString());
        (await Scalar<bool>($"SELECT consumed_at IS NOT NULL FROM nexora.approval_request WHERE id = '{requestId}'")).Should().BeTrue();

        // L'approbation ne vaut que pour CE panier : un autre panier est refusé.
        await using (var s = Scope())
        {
            As(s.ServiceProvider, cashier);
            (await s.ServiceProvider.GetRequiredService<SalesService>().CheckoutAsync(Sale(customer.Id, product, 2m)))
                .Error!.Code.Should().Be("pos.credit_limit");
        }
    }

    [Fact]
    public async Task Double_approval_policy_requires_two_distinct_approvers()
    {
        var cashier = await UserWithRole("cashier");
        var first = await UserWithRole("manager");
        var second = await UserWithRole("manager");
        var customer = await NewCustomer(100m);
        var cart = Guid.NewGuid();
        var policy = Guid.NewGuid();
        await Exec($"""
            INSERT INTO nexora.approval_policy (id, company_id, request_type, min_amount, required_levels, status)
            VALUES ('{policy}', '{Company}', 'credit_sale', 1000, 2, 'active')
            """);
        try
        {
            Guid id;
            await using (var s = Scope())
            {
                As(s.ServiceProvider, cashier);
                var r = await s.ServiceProvider.GetRequiredService<ICreditOverrideService>().RequestAsync(
                    new CreditOverrideRequestInput(cart, customer.Id, 5_000m, "HTG", "Grosse commande exceptionnelle"));
                r.Value!.RequiredLevels.Should().Be(2);
                id = r.Value.Id;
            }
            await using (var s = Scope())
            {
                As(s.ServiceProvider, first);
                var o = s.ServiceProvider.GetRequiredService<ICreditOverrideService>();
                (await o.ApproveAsync(id, "Niveau 1")).Value!.Status.Should().Be("pending");
                (await o.ApproveAsync(id, "Encore moi")).Error!.Code.Should().Be("security.duplicate_approver");
            }
            await using (var s = Scope())
            {
                As(s.ServiceProvider, second);
                (await s.ServiceProvider.GetRequiredService<ICreditOverrideService>().ApproveAsync(id, "Niveau 2"))
                    .Value!.Status.Should().Be("approved");
            }
        }
        finally
        {
            await Exec($"UPDATE nexora.approval_policy SET status = 'inactive' WHERE id = '{policy}'");
        }
    }

    // ---- §21 : devise explicite — encours converti, soldes et journal par devise ----
    [Fact]
    public async Task Receivables_in_another_currency_are_converted_for_the_limit_and_never_summed_raw()
    {
        var customer = await NewCustomer(20_000m);
        await Checkout(Sale(customer.Id, await Product(100m), 10m));    // 1 000 HTG
        await Exec($"""
            INSERT INTO nexora.customer_receivable (company_id, customer_id, currency_id, original_amount, issue_date, due_date, status)
            VALUES ('{Company}', '{customer.Id}', (SELECT id FROM nexora.currency WHERE code = 'USD'), 100,
                    '{Today:yyyy-MM-dd}', '{Today.AddDays(30):yyyy-MM-dd}', 'OPEN')
            """);
        var usdInHtg = await Scalar<decimal>("""
            SELECT nexora.convert_amount(100, (SELECT id FROM nexora.currency WHERE code='USD'), (SELECT id FROM nexora.currency WHERE code='HTG'))
            """);

        var summary = await Summary(customer.Id);
        summary.BalanceDue.Should().Be(1_000m, "le solde principal est dans la devise du client");
        summary.Balances.Select(b => b.Currency).Should().Equal("HTG", "USD");
        summary.Balances.Single(b => b.Currency == "USD").BalanceDue.Should().Be(100m);
        summary.TotalExposure.Should().Be(1_000m + usdInHtg);
        summary.AvailableCredit.Should().Be(20_000m - 1_000m - usdInHtg);
        summary.LedgerConsistent.Should().BeTrue();

        await using var s = Scope();
        As(s.ServiceProvider, Direction);
        var decision = await s.ServiceProvider.GetRequiredService<ICreditControl>().EvaluateAsync(customer.Id, "HTG", 20_000m - 1_000m - usdInHtg + 1m);
        decision.Allowed.Should().BeFalse("l'encours en dollars compte, converti, dans la limite");
        var usdLedger = (await s.ServiceProvider.GetRequiredService<ICustomerAccountQueries>().LedgerAsync(customer.Id, null, null, "USD"))!;
        usdLedger.Entries.Should().ContainSingle().Which.Debit.Should().Be(100m);
        usdLedger.Currency.Should().Be("USD");
    }

    // ---- §24 : versement hors ligne — synchronisé une fois, conflit si le crédit est déjà soldé ----
    [Fact]
    public async Task Offline_customer_payment_syncs_once_and_conflicts_when_nothing_is_due()
    {
        var customer = await NewCustomer(50_000m);
        await Checkout(Sale(customer.Id, await Product(100m), 4m));   // 400 dus
        await using var s = Scope();
        As(s.ServiceProvider, Direction);
        var sync = s.ServiceProvider.GetRequiredService<ISyncService>();
        var code = $"PAY-{Guid.NewGuid():N}"[..12];
        var device = (await s.ServiceProvider.GetRequiredService<IDeviceRegistryService>()
            .RegisterAsync(new RegisterDeviceRequest(code, code, "pos", null, null))).Value;

        SyncOperationInput Payment(Guid op, decimal amount) => new(op, "customer_payment", "create", JsonSerializer.SerializeToElement(new
        {
            customerId = customer.Id, amount, financialAccountId = CashAccount, paymentMethodId = CashMethod, note = "Versement hors ligne",
        }));

        var op = Guid.NewGuid();
        (await sync.PushBatchAsync(new PushBatchRequest(device, [Payment(op, 400m)]))).Value!.Operations[0].Status.Should().Be("SYNCED");
        (await sync.PushBatchAsync(new PushBatchRequest(device, [Payment(op, 400m)]))).IsSuccess.Should().BeTrue();
        (await Scalar<long>($"SELECT count(*) FROM nexora.payment WHERE source_operation_uuid = '{op}'")).Should().Be(1);
        (await Summary(customer.Id)).BalanceDue.Should().Be(0m);

        var late = await sync.PushBatchAsync(new PushBatchRequest(device, [Payment(Guid.NewGuid(), 100m)]));
        late.Value!.Operations[0].Status.Should().Be("CONFLICT", "le crédit a déjà été soldé : jamais un versement en trop silencieux");
    }

    // ---- Reprise V074 : un ancien retour « avoir » sur une vente à crédit réduit enfin la créance ----
    [Fact]
    public async Task Legacy_return_on_a_credit_sale_is_rebuilt_as_a_credit_memo_applied_to_the_receivable()
    {
        var customer = await NewCustomer(50_000m);
        var sale = await Checkout(Sale(customer.Id, await Product(100m), 10m));   // 1 000 à crédit
        // Retour enregistré comme avant V073 : écriture au compte clients, créance intacte, aucun avoir.
        await Exec($"""
            INSERT INTO nexora.sale_return (id, company_id, sale_id, document_number, status, currency_id, reason, refund_total,
                                            returned_at, refund_method, restocked)
            VALUES (gen_random_uuid(), '{Company}', '{sale.SaleId}', 'RET-LEGACY-{Guid.NewGuid():N}', 'completed',
                    (SELECT id FROM nexora.currency WHERE code = 'HTG'), 'Retour ancien', 200, now(), 'credit_note', true)
            """);
        (await Summary(customer.Id)).BalanceDue.Should().Be(1_000m, "situation héritée : la créance n'avait pas baissé");

        // Rejoue la section « 7. Retours antérieurs » de la migration, telle qu'elle est livrée.
        var migration = await File.ReadAllTextAsync(Path.Combine(RepoRoot(), "database", "migrations", "V074__customer_accounts_gaps.sql"));
        var start = migration.IndexOf("WITH old AS (", StringComparison.Ordinal);
        var end = migration.IndexOf("-- =====================================================================\n-- Sécurité", start, StringComparison.Ordinal);
        if (end < 0) end = migration.IndexOf("-- Sécurité", start, StringComparison.Ordinal);
        await Exec("SET search_path TO nexora, public;\n" + migration[start..end]);

        var summary = await Summary(customer.Id);
        summary.BalanceDue.Should().Be(800m);
        summary.LedgerConsistent.Should().BeTrue();
        (await Scalar<string>($"""
            SELECT gl_account_key FROM nexora.customer_credit_memo m JOIN nexora.sale_return sr ON sr.id = m.sale_return_id
            WHERE sr.sale_id = '{sale.SaleId}'
            """)).Should().Be("accounts_receivable");
        (await GeneralLedgerBalance(customer.Id, "4111")).Should().Be(1_000m, "le grand livre de l'époque n'est pas réécrit par la reprise");
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "database", "migrations"))) dir = dir.Parent;
        return dir?.FullName ?? throw new DirectoryNotFoundException("Racine du dépôt introuvable.");
    }
}
