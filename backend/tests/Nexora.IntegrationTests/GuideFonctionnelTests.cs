using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Nexora.Application.Abstractions;
using Nexora.Application.Catalog;
using Nexora.Application.Identity;
using Nexora.Application.Inventory;
using Nexora.Application.Purchasing;
using Nexora.Application.Reporting;
using Nexora.Application.Sales;
using Nexora.Contracts.Identity;
using Nexora.Contracts.Reporting;
using Nexora.Contracts.Sales;
using Nexora.Infrastructure.Persistence;

namespace Nexora.IntegrationTests;

/// <summary>
/// Guide fonctionnel développeur (docs/guide-fonctionnel-conformite.md) — règles de rôle et de
/// validation Manager prouvées au niveau des services, avec de vrais utilisateurs affectés à des
/// rôles système (pas l'utilisateur direction du contexte par défaut).
/// </summary>
[Collection("finance")]
public sealed class GuideFonctionnelTests(FinanceEngineFixture fx)
{
    private static readonly Guid Company = Guid.Parse("dce00000-0000-0000-0000-0000000000c1");
    private static readonly Guid Direction = Guid.Parse("dce00000-0000-0000-0000-0000000000d1");
    private static readonly Guid Officine = Guid.Parse("dce00000-0000-0000-0000-00000000a003");
    private static readonly Guid Supplier1 = Guid.Parse("dce00000-0000-0000-0000-00000000b101");
    private static readonly Guid BoxUnit = Guid.Parse("dce00000-0000-0000-0000-00000000a011");
    private static readonly Guid CashAccount = Guid.Parse("dce00000-0000-0000-0000-00000000a051");
    private static readonly Guid CashMethod = Guid.Parse("dce00000-0000-0000-0000-00000000a081");

    private AsyncServiceScope Scope() => fx.Provider.CreateAsyncScope();

    /// <summary>Crée un utilisateur et lui affecte un rôle système sur la Pharmacie (via le moteur
    /// d'affectation réel, anti-élévation compris — la direction détient toutes les permissions).</summary>
    private async Task<Guid> UserWithRole(string role)
    {
        var id = Guid.NewGuid();
        await using (var c = new Npgsql.NpgsqlConnection(fx.TestConnectionString))
        {
            await c.OpenAsync();
            await using var cmd = new Npgsql.NpgsqlCommand($"""
                INSERT INTO nexora.app_user (id, email, password_hash, full_name, status)
                VALUES ('{id}', 'GF-{id:N}@test.demo', crypt('x', gen_salt('bf', 4)), '{role} test', 'active')
                """, c);
            await cmd.ExecuteNonQueryAsync();
        }
        await using var s = Scope();
        As(s.ServiceProvider, Direction);
        var granted = await s.ServiceProvider.GetRequiredService<IUserAssignmentService>()
            .GrantAsync(id, new GrantAssignmentRequest(role, "COMPANY", Company, null, null, null, null));
        granted.IsSuccess.Should().BeTrue(granted.Error?.Message);
        return id;
    }

    private static void As(IServiceProvider sp, Guid userId)
    {
        var tc = (TestTenantContext)sp.GetRequiredService<ITenantContext>();
        tc.SetUser(userId);
        tc.SetScope([Company], [], Company);
    }

    // ---- Étape 1 — GF-16 : pas de KPI financier sans reporting.view_financial (§6, §17) ----
    [Fact]
    public async Task Stock_manager_command_center_hides_financial_kpis_and_alerts()
    {
        var stock = await UserWithRole("stock_manager");
        await using var s = Scope();
        As(s.ServiceProvider, stock);

        var cc = await s.ServiceProvider.GetRequiredService<IKpiService>().CommandCenterAsync(new ReportFilter { Preset = "month" });
        // Étape 7 (guide §18) : ni KPI financier, ni chiffre d'affaires pour le responsable stock.
        cc.Kpis.Select(k => k.Key).Should().NotContain(KpiCatalog.Financial).And.NotContain(KpiCatalog.Sales);
        cc.RevenueTrend.Should().BeEmpty();
        cc.Comparison.Current.Should().Be(0m);

        var alerts = await s.ServiceProvider.GetRequiredService<IAlertCenter>().EvaluateAsync();
        alerts.Alerts.Select(a => a.Key).Should().NotContain(["overdue_receivable", "overdue_payable", "cash_variance", "low_cash", "sales_drop"]);
    }

    // ---- Étape 7 — GF-19..22 : le chiffre d'affaires ne fuit par aucun autre chemin ----
    [Fact]
    public async Task Saved_reports_never_bypass_the_permissions_of_the_report_itself()
    {
        var stock = await UserWithRole("stock_manager");   // reporting.view, sans view_financial ni view_sales
        await using var s = Scope();
        As(s.ServiceProvider, stock);
        var catalog = s.ServiceProvider.GetRequiredService<IReportCatalogService>();
        var empty = System.Text.Json.JsonDocument.Parse("{}").RootElement;
        (await catalog.SaveAsync(new SaveSavedReportRequest(null, "Dettes", "aging_payables", empty, false)))
            .Error!.Code.Should().Be("report.forbidden");
        (await catalog.SaveAsync(new SaveSavedReportRequest(null, "Ventes", "sales_journal", empty, false)))
            .Error!.Code.Should().Be("report.forbidden");
        (await catalog.SaveAsync(new SaveSavedReportRequest(null, "Ruptures", "stock_ruptures", empty, false)))
            .IsSuccess.Should().BeTrue("un rapport de stock reste accessible au responsable stock");
    }

    [Fact]
    public async Task Cashier_sees_only_his_own_documents_and_never_the_customers_receivables()
    {
        var cashier = await UserWithRole("cashier");
        var (sale, _, _, _) = await CompletedSale("DOC", 1m);
        Guid invoice;
        await using (var s = Scope())
        {
            As(s.ServiceProvider, Direction);
            var issued = await s.ServiceProvider.GetRequiredService<Nexora.Application.Documents.IDocumentService>()
                .IssueAsync(new Nexora.Contracts.Documents.IssueDocumentRequest("invoice", "sale", sale));
            issued.IsSuccess.Should().BeTrue(issued.Error?.Message);
            invoice = issued.Value!.Id;
            var all = await s.ServiceProvider.GetRequiredService<Nexora.Application.Documents.IDocumentQueries>()
                .SearchAsync(new Nexora.Contracts.Documents.DocumentSearchRequest(null, null, null, null, null, null, 500));
            all.Should().Contain(d => d.Id == invoice);
        }
        await using (var s = Scope())
        {
            As(s.ServiceProvider, cashier);
            var queries = s.ServiceProvider.GetRequiredService<Nexora.Application.Documents.IDocumentQueries>();
            (await queries.SearchAsync(new Nexora.Contracts.Documents.DocumentSearchRequest(null, null, null, null, null, null, 500)))
                .Should().BeEmpty("la liste des factures de l'entreprise révélerait le chiffre d'affaires");
            (await queries.GetAsync(invoice)).Error!.Code.Should().Be("document.not_found");
            (await s.ServiceProvider.GetRequiredService<Nexora.Application.Documents.IDocumentService>().RenderAsync(invoice, null))
                .Error!.Code.Should().Be("document.not_found");

            var customers = s.ServiceProvider.GetRequiredService<Nexora.Application.Parties.ICustomerService>();
            var list = await customers.ListAsync(null, null);
            list.Should().NotBeEmpty().And.OnlyContain(c => c.OpenReceivables == null && c.CreditLimit == null);
            var one = await customers.GetAsync(list[0].Id);
            one!.OpenReceivables.Should().NotBeNull("au POS, le solde d'UN client sert à décider une vente à crédit");
        }
    }

    [Fact]
    public async Task Direction_command_center_keeps_every_kpi()
    {
        await using var s = Scope();
        As(s.ServiceProvider, Direction);
        var cc = await s.ServiceProvider.GetRequiredService<IKpiService>().CommandCenterAsync(new ReportFilter { Preset = "month" });
        cc.Kpis.Select(k => k.Key).Should().Contain(KpiCatalog.Financial);
    }

    // ---- Étape 1 — GF-01/GF-05/GF-14 : périmètre POS strict du caissier, caissier senior ----
    [Fact]
    public async Task Cashier_and_senior_cashier_hold_only_pos_permissions()
    {
        var cashier = await UserWithRole("cashier");
        var senior = await UserWithRole("senior_cashier");
        await using var s = Scope();
        var perms = s.ServiceProvider.GetRequiredService<IPermissionService>();

        foreach (var user in new[] { cashier, senior })
        {
            var granted = (await perms.LoadProfileAsync(user)).PermissionsFor(Company);
            granted.Should().Contain(["pos.sell", "pos.return", "pos.discount", "pos.open_session", "pos.close_session"]);
            granted.Should().NotContain(["reporting.view", "reporting.view_financial", "finance.pay", "finance.manage_cash",
                "finance.view", "integrations.payments", "pos.refund", "pos.approve_discount", "pos.manage_sessions",
                "pos.change_price", "pos.credit_override", "catalog.view", "catalog.view_cost", "inventory.view",
                "inventory.adjust", "admin.manage_users", "admin.manage_roles"]);
        }
    }

    // =====================================================================
    // Étape 2 — retours avec validation Manager (§10, §22, §26)
    // =====================================================================

    /// <summary>Produit stocké + vente encaissée (par la direction) → (vente, ligne de vente, produit, ticket).</summary>
    private async Task<(Guid Sale, Guid Line, Guid Product, string Number)> CompletedSale(string suffix, decimal qty, decimal stock = 20m)
    {
        await using var s = Scope();
        As(s.ServiceProvider, Direction);
        var catalog = s.ServiceProvider.GetRequiredService<CatalogService>();
        var product = (await catalog.CreateProductAsync(new CreateProductCommand(
            $"GF-{suffix}-{Guid.NewGuid():N}"[..20], $"Article GF {suffix}", null, BoxUnit, "stockable", null, null, null, null, false, false))).Value;
        await catalog.SetPriceAsync(new SetProductPriceCommand(product, "sale", 100m, "HTG", 0m, null, null));
        await s.ServiceProvider.GetRequiredService<PurchasingService>().ReceiveAsync(new ReceiveGoodsCommand(
            Guid.NewGuid(), Supplier1, Officine, null, [new ReceiveLine(product, null, stock, 40m, null, null)], null, null));
        var sale = await s.ServiceProvider.GetRequiredService<SalesService>().CheckoutAsync(new CheckoutCommand(
            Guid.NewGuid(), Officine, null, null, null, null, "HTG", [new CheckoutLine(product, qty, null, 0m, null)],
            [new TenderInput(CashAccount, CashMethod, 100m * qty, null)], 0m, null, null, null, null));
        sale.IsSuccess.Should().BeTrue(sale.Error?.Message);
        var db = s.ServiceProvider.GetRequiredService<NexoraDbContext>();
        var line = await db.SaleLines.AsNoTracking().Where(l => l.SaleId == sale.Value!.SaleId).Select(l => l.Id).FirstAsync();
        return (sale.Value!.SaleId, line, product, sale.Value.DocumentNumber);
    }

    private static CreateReturnRequest ReturnRequestFor(Guid sale, Guid line, decimal qty, string reason = "Article défectueux")
        => new(Guid.NewGuid(), sale, Officine, null, reason, "cash", CashAccount, [new ReturnRequestLineInput(line, qty)]);

    private async Task<decimal> OnHand(Guid product)
    {
        await using var s = Scope();
        return await s.ServiceProvider.GetRequiredService<IStockLedger>().OnHandAsync(product, Officine);
    }

    [Fact]
    public async Task Cashier_return_is_only_a_request_until_a_manager_approves_it()
    {
        var cashier = await UserWithRole("cashier");
        var manager = await UserWithRole("manager");
        var (sale, line, product, number) = await CompletedSale("RET1", 3m);
        var stockAfterSale = await OnHand(product);

        // Le caissier retrouve la vente par son ticket, puis demande le retour de 2 unités.
        Guid requestId;
        await using (var s = Scope())
        {
            As(s.ServiceProvider, cashier);
            var svc = s.ServiceProvider.GetRequiredService<IReturnRequestService>();
            var found = await svc.FindSaleForReturnAsync(number);
            found!.Lines.Should().ContainSingle().Which.QuantityReturnable.Should().Be(3m);

            var req = await svc.RequestAsync(ReturnRequestFor(sale, line, 2m));
            req.IsSuccess.Should().BeTrue(req.Error?.Message);
            req.Value!.Status.Should().Be("pending_approval");
            req.Value.EstimatedRefund.Should().Be(200m, "le montant est calculé par le serveur");
            requestId = req.Value.Id;

            // Un caissier ne peut pas valider (pas de pos.refund).
            (await svc.ApproveAsync(requestId, new DecideRequest(null))).Error!.Code.Should().Be("pos.refund_forbidden");
        }
        (await OnHand(product)).Should().Be(stockAfterSale, "une demande ne touche pas au stock");

        // Le Manager est notifié, avec un lien qui ouvre la demande (§20).
        await using (var s = Scope())
        {
            var db = s.ServiceProvider.GetRequiredService<NexoraDbContext>();
            var notified = await db.Database.SqlQuery<string>($"""
                SELECT action_url AS "Value" FROM nexora.notification
                WHERE user_id = {manager} AND reference_type = 'sale_return_request' AND reference_id = {requestId}
                """).ToListAsync();
            notified.Should().ContainSingle().Which.Should().Contain(requestId.ToString());
        }

        // Le Manager approuve : le retour est exécuté (stock, statut de vente), la demande est liée au retour.
        await using (var s = Scope())
        {
            As(s.ServiceProvider, manager);
            var approved = await s.ServiceProvider.GetRequiredService<IReturnRequestService>()
                .ApproveAsync(requestId, new DecideRequest("Produit vérifié"));
            approved.IsSuccess.Should().BeTrue(approved.Error?.Message);
            approved.Value!.Status.Should().Be("executed");
            approved.Value.SaleReturnId.Should().NotBeNull();
            approved.Value.DecidedBy.Should().Be(manager);

            var db = s.ServiceProvider.GetRequiredService<NexoraDbContext>();
            (await db.Sales.AsNoTracking().Where(x => x.Id == sale).Select(x => x.Status).FirstAsync()).Should().Be("partially_refunded");
            (await db.SaleReturns.AsNoTracking().Where(r => r.Id == approved.Value.SaleReturnId).Select(r => r.RefundTotal).FirstAsync())
                .Should().Be(200m);

            // §22 : la demande et le retour laissent une trace d'audit.
            var returnId = approved.Value.SaleReturnId!.Value;
            var audited = await db.Database.SqlQuery<string>($"""
                SELECT DISTINCT entity_type AS "Value" FROM nexora.audit_log
                WHERE entity_id = {requestId} OR entity_id = {returnId}
                """).ToListAsync();
            audited.Should().Contain(["sale_return_request", "sale_return"]);
        }
        (await OnHand(product)).Should().Be(stockAfterSale + 2m);
    }

    [Fact]
    public async Task Requester_can_never_approve_his_own_return_request()
    {
        var manager = await UserWithRole("manager");
        var (sale, line, _, _) = await CompletedSale("RET2", 1m);
        await using var s = Scope();
        As(s.ServiceProvider, manager);
        var svc = s.ServiceProvider.GetRequiredService<IReturnRequestService>();
        var req = await svc.RequestAsync(ReturnRequestFor(sale, line, 1m));
        req.IsSuccess.Should().BeTrue(req.Error?.Message);
        (await svc.ApproveAsync(req.Value!.Id, new DecideRequest(null))).Error!.Code.Should().Be("pos.self_approval");
    }

    [Fact]
    public async Task Rejected_request_stays_in_history_and_cannot_be_approved_afterwards()
    {
        var cashier = await UserWithRole("cashier");
        var manager = await UserWithRole("manager");
        var (sale, line, product, _) = await CompletedSale("RET3", 2m);
        var stock = await OnHand(product);

        Guid id;
        await using (var s = Scope())
        {
            As(s.ServiceProvider, cashier);
            id = (await s.ServiceProvider.GetRequiredService<IReturnRequestService>().RequestAsync(ReturnRequestFor(sale, line, 1m))).Value!.Id;
        }
        await using (var s = Scope())
        {
            As(s.ServiceProvider, manager);
            var svc = s.ServiceProvider.GetRequiredService<IReturnRequestService>();
            (await svc.RejectAsync(id, new DecideRequest(" "))).Error!.Code.Should().Be("pos.decision_note_required");
            var rejected = await svc.RejectAsync(id, new DecideRequest("Article utilisé, hors politique de retour"));
            rejected.IsSuccess.Should().BeTrue(rejected.Error?.Message);
            rejected.Value!.Status.Should().Be("rejected");
            (await svc.ApproveAsync(id, new DecideRequest(null))).Error!.Code.Should().Be("pos.request_closed");
        }
        await using (var s = Scope())
        {
            As(s.ServiceProvider, cashier);
            var mine = await s.ServiceProvider.GetRequiredService<IReturnRequestService>().ListAsync("rejected");
            mine.Should().ContainSingle(r => r.Id == id).Which.DecisionNote.Should().Contain("hors politique");
        }
        (await OnHand(product)).Should().Be(stock, "un refus n'a aucun impact stock");
    }

    [Fact]
    public async Task Returned_quantity_can_never_exceed_what_was_bought_across_several_returns()
    {
        var cashier = await UserWithRole("cashier");
        var (sale, line, _, _) = await CompletedSale("RET4", 2m);

        await using (var s = Scope())
        {
            As(s.ServiceProvider, Direction);
            var returns = s.ServiceProvider.GetRequiredService<SalesReturnService>();
            (await returns.ReturnAsync(new ReturnCommand(Guid.NewGuid(), sale, Officine, null, "Retour partiel", "cash", CashAccount,
                [new ReturnLineInput(line, 1m, true)]))).IsSuccess.Should().BeTrue();

            // Avant correction : 2 ≤ 2 vendues passait — 3 unités remboursées sur 2.
            var over = await returns.ReturnAsync(new ReturnCommand(Guid.NewGuid(), sale, Officine, null, "Deuxième retour", "cash",
                CashAccount, [new ReturnLineInput(line, 2m, true)]));
            over.Error!.Code.Should().Be("pos.return_exceeds_sold");
        }

        await using (var s = Scope())
        {
            As(s.ServiceProvider, cashier);
            var svc = s.ServiceProvider.GetRequiredService<IReturnRequestService>();
            (await svc.RequestAsync(ReturnRequestFor(sale, line, 2m))).Error!.Code.Should().Be("pos.return_exceeds_sold");
            // La dernière unité se demande une fois ; une seconde demande est bloquée tant que la première attend.
            (await svc.RequestAsync(ReturnRequestFor(sale, line, 1m))).IsSuccess.Should().BeTrue();
            (await svc.RequestAsync(ReturnRequestFor(sale, line, 1m))).Error!.Code.Should().Be("pos.return_exceeds_sold");
        }
    }

    [Fact]
    public async Task Full_return_across_two_returns_marks_the_sale_refunded()
    {
        var (sale, line, _, _) = await CompletedSale("RET5", 2m);
        await using var s = Scope();
        As(s.ServiceProvider, Direction);
        var returns = s.ServiceProvider.GetRequiredService<SalesReturnService>();
        for (var i = 0; i < 2; i++)
            (await returns.ReturnAsync(new ReturnCommand(Guid.NewGuid(), sale, Officine, null, "Retour", "cash", CashAccount,
                [new ReturnLineInput(line, 1m, true)]))).IsSuccess.Should().BeTrue();
        var db = s.ServiceProvider.GetRequiredService<NexoraDbContext>();
        (await db.Sales.AsNoTracking().Where(x => x.Id == sale).Select(x => x.Status).FirstAsync()).Should().Be("refunded");
    }

    // =====================================================================
    // Étape 3 — encaissement : produit vendable, crédit, remises par rôle (§12, §16, §25)
    // =====================================================================

    private async Task<Guid> StockedProduct(string suffix, decimal stock = 50m, string? status = null)
    {
        await using var s = Scope();
        As(s.ServiceProvider, Direction);
        var catalog = s.ServiceProvider.GetRequiredService<CatalogService>();
        var product = (await catalog.CreateProductAsync(new CreateProductCommand(
            $"GF-{suffix}-{Guid.NewGuid():N}"[..20], $"Article GF {suffix}", null, BoxUnit, "stockable", null, null, null, null, false, false))).Value;
        await catalog.SetPriceAsync(new SetProductPriceCommand(product, "sale", 100m, "HTG", 0m, null, null));
        await s.ServiceProvider.GetRequiredService<PurchasingService>().ReceiveAsync(new ReceiveGoodsCommand(
            Guid.NewGuid(), Supplier1, Officine, null, [new ReceiveLine(product, null, stock, 40m, null, null)], null, null));
        if (status is not null)
            await s.ServiceProvider.GetRequiredService<NexoraDbContext>().Database
                .ExecuteSqlAsync($"UPDATE nexora.product SET status = {status} WHERE id = {product}");
        return product;
    }

    private static CheckoutCommand Sale(Guid product, decimal qty, decimal discount = 0m, Guid? operation = null,
        Guid? customer = null, decimal? paid = null, string? overrideReason = null, Guid? session = null)
        => new(operation ?? Guid.NewGuid(), Officine, null, null, session, customer, "HTG",
            [new CheckoutLine(product, qty, null, discount, null)],
            [new TenderInput(CashAccount, CashMethod, paid ?? 100m * qty - discount, null)], 0m, null, null, overrideReason, null);

    private async Task<Guid> NewCustomer(decimal creditLimit)
    {
        var id = Guid.NewGuid();
        await using var c = new Npgsql.NpgsqlConnection(fx.TestConnectionString);
        await c.OpenAsync();
        await using var cmd = new Npgsql.NpgsqlCommand($"""
            INSERT INTO nexora.customer (id, company_id, customer_number, name, credit_limit, default_currency_id)
            VALUES ('{id}', '{Company}', 'GF-{id:N}', 'Client GF', {creditLimit.ToString(System.Globalization.CultureInfo.InvariantCulture)},
                    (SELECT id FROM nexora.currency WHERE code='HTG'))
            """, c);
        await cmd.ExecuteNonQueryAsync();
        return id;
    }

    [Fact]
    public async Task Inactive_or_non_sellable_products_cannot_be_sold()
    {
        var inactive = await StockedProduct("INA", status: "inactive");
        var notSellable = await StockedProduct("NSE");
        await using var s = Scope();
        As(s.ServiceProvider, Direction);
        await s.ServiceProvider.GetRequiredService<NexoraDbContext>().Database
            .ExecuteSqlAsync($"UPDATE nexora.product SET is_sellable = false WHERE id = {notSellable}");
        var sales = s.ServiceProvider.GetRequiredService<SalesService>();

        (await sales.CheckoutAsync(Sale(inactive, 1m))).Error!.Code.Should().Be("sale.product_inactive");
        (await sales.CheckoutAsync(Sale(notSellable, 1m))).Error!.Code.Should().Be("sale.product_not_sellable");
        (await sales.CheckoutAsync(Sale(Guid.NewGuid(), 1m))).Error!.Code.Should().Be("sale.product_not_found");
    }

    [Fact]
    public async Task Discount_limits_depend_on_the_role()
    {
        var cashier = await UserWithRole("cashier");
        var senior = await UserWithRole("senior_cashier");
        var product = await StockedProduct("LIM");

        await using (var s = Scope())
        {
            As(s.ServiceProvider, cashier);
            var limits = await s.ServiceProvider.GetRequiredService<IDiscountRequestService>().MyLimitsAsync();
            limits.DiscountLimitPercent.Should().Be(5m);
            limits.CanApproveDiscount.Should().BeFalse();
            limits.CanRefund.Should().BeFalse();
            var sales = s.ServiceProvider.GetRequiredService<SalesService>();
            (await sales.CheckoutAsync(Sale(product, 1m, discount: 8m))).Error!.Code.Should().Be("sale.discount_over_limit");
        }
        await using (var s = Scope())
        {
            As(s.ServiceProvider, senior);
            (await s.ServiceProvider.GetRequiredService<IDiscountRequestService>().MyLimitsAsync()).DiscountLimitPercent.Should().Be(10m);
            (await s.ServiceProvider.GetRequiredService<SalesService>().CheckoutAsync(Sale(product, 1m, discount: 8m)))
                .IsSuccess.Should().BeTrue("8 % est dans la limite du caissier senior");
        }
    }

    [Fact]
    public async Task Exceptional_discount_needs_a_manager_approval_bound_to_the_cart_and_used_once()
    {
        var cashier = await UserWithRole("cashier");
        var manager = await UserWithRole("manager");
        var product = await StockedProduct("EXC");
        var cart = Guid.NewGuid();

        Guid requestId;
        await using (var s = Scope())
        {
            As(s.ServiceProvider, cashier);
            var svc = s.ServiceProvider.GetRequiredService<IDiscountRequestService>();
            (await svc.RequestAsync(new CreateDiscountRequest(cart, product, 2m, 6m, "HTG", Officine, null, "Client fidèle")))
                .Error!.Code.Should().Be("pos.discount_within_limit", "3 % n'a pas besoin de validation");
            var req = await svc.RequestAsync(new CreateDiscountRequest(cart, product, 2m, 40m, "HTG", Officine, null, "Emballage abîmé"));
            req.IsSuccess.Should().BeTrue(req.Error?.Message);
            req.Value!.RequestedPercent.Should().Be(20m, "40 sur 200 recalculés par le serveur");
            req.Value.Status.Should().Be("pending_approval");
            requestId = req.Value.Id;

            (await s.ServiceProvider.GetRequiredService<SalesService>().CheckoutAsync(Sale(product, 2m, 40m, cart)))
                .Error!.Code.Should().Be("sale.discount_over_limit", "tant que la demande n'est pas approuvée");
        }

        await using (var s = Scope())
        {
            As(s.ServiceProvider, manager);
            var approved = await s.ServiceProvider.GetRequiredService<IDiscountRequestService>().ApproveAsync(requestId, new DecideRequest("OK"));
            approved.IsSuccess.Should().BeTrue(approved.Error?.Message);
            approved.Value!.Status.Should().Be("approved");
        }

        await using (var s = Scope())
        {
            As(s.ServiceProvider, cashier);
            var sales = s.ServiceProvider.GetRequiredService<SalesService>();
            // L'approbation ne vaut que pour CE panier.
            (await sales.CheckoutAsync(Sale(product, 2m, 40m))).Error!.Code.Should().Be("sale.discount_over_limit");
            var sold = await sales.CheckoutAsync(Sale(product, 2m, 40m, cart));
            sold.IsSuccess.Should().BeTrue(sold.Error?.Message);
            sold.Value!.DiscountTotal.Should().Be(40m);

            var consumed = await s.ServiceProvider.GetRequiredService<IDiscountRequestService>().GetAsync(requestId);
            consumed.Value!.Status.Should().Be("consumed");
            consumed.Value.ConsumedSaleId.Should().Be(sold.Value.SaleId);
        }
    }

    [Fact]
    public async Task Manager_approves_exceptional_discounts_only_within_his_own_limit_and_never_his_own()
    {
        var cashier = await UserWithRole("cashier");
        var manager = await UserWithRole("manager");
        var product = await StockedProduct("MGR");

        Guid tooHigh;
        await using (var s = Scope())
        {
            As(s.ServiceProvider, cashier);
            tooHigh = (await s.ServiceProvider.GetRequiredService<IDiscountRequestService>().RequestAsync(
                new CreateDiscountRequest(Guid.NewGuid(), product, 1m, 30m, "HTG", Officine, null, "Geste commercial"))).Value!.Id;
        }
        await using (var s = Scope())
        {
            As(s.ServiceProvider, manager);
            var svc = s.ServiceProvider.GetRequiredService<IDiscountRequestService>();
            (await svc.ApproveAsync(tooHigh, new DecideRequest(null))).Error!.Code
                .Should().Be("pos.discount_beyond_approver_limit", "30 % > limite Manager de 25 %");

            var own = await svc.RequestAsync(new CreateDiscountRequest(Guid.NewGuid(), product, 1m, 20m, "HTG", Officine, null, "Test"));
            own.IsSuccess.Should().BeTrue(own.Error?.Message);
            (await svc.ApproveAsync(own.Value!.Id, new DecideRequest(null))).Error!.Code.Should().Be("pos.self_approval");
        }
        await using (var s = Scope())
        {
            As(s.ServiceProvider, Direction);   // limite d'approbation 100 %
            (await s.ServiceProvider.GetRequiredService<IDiscountRequestService>().ApproveAsync(tooHigh, new DecideRequest("Accordé")))
                .IsSuccess.Should().BeTrue();
        }
    }

    [Fact]
    public async Task Credit_limit_override_requires_permission_not_just_a_reason()
    {
        var cashier = await UserWithRole("cashier");
        var manager = await UserWithRole("manager");
        var product = await StockedProduct("CRD");
        var customer = await NewCustomer(creditLimit: 50m);

        await using (var s = Scope())
        {
            As(s.ServiceProvider, cashier);
            var r = await s.ServiceProvider.GetRequiredService<SalesService>().CheckoutAsync(
                Sale(product, 1m, customer: customer, paid: 0m, overrideReason: "Client connu"));
            r.Error!.Code.Should().Be("pos.credit_override_forbidden");
        }
        await using (var s = Scope())
        {
            As(s.ServiceProvider, manager);
            var r = await s.ServiceProvider.GetRequiredService<SalesService>().CheckoutAsync(
                Sale(product, 1m, customer: customer, paid: 0m, overrideReason: "Client connu"));
            r.IsSuccess.Should().BeTrue(r.Error?.Message);
            r.Value!.CreditOverrideApplied.Should().BeTrue();
        }
    }

    [Fact]
    public async Task A_sale_is_only_attached_to_the_sellers_own_open_session()
    {
        var cashierA = await UserWithRole("cashier");
        var cashierB = await UserWithRole("cashier");
        var product = await StockedProduct("SES");
        var register = Guid.NewGuid();
        await using (var s = Scope())
        {
            await s.ServiceProvider.GetRequiredService<NexoraDbContext>().Database.ExecuteSqlAsync($"""
                INSERT INTO nexora.cash_register (id, company_id, financial_account_id, name, code, currency_id)
                VALUES ({register}, {Company}, {CashAccount}, 'Caisse GF', {"GF" + register.ToString("N")[..6]},
                        (SELECT id FROM nexora.currency WHERE code = 'HTG'))
                """);
        }

        Guid session;
        await using (var s = Scope())
        {
            As(s.ServiceProvider, cashierA);
            session = (await s.ServiceProvider.GetRequiredService<CashSessionService>().OpenAsync(new OpenSessionCommand(register, null, 1000m))).Value;
            (await s.ServiceProvider.GetRequiredService<SalesService>().CheckoutAsync(Sale(product, 1m, session: session)))
                .IsSuccess.Should().BeTrue();
        }
        await using (var s = Scope())
        {
            As(s.ServiceProvider, cashierB);
            (await s.ServiceProvider.GetRequiredService<SalesService>().CheckoutAsync(Sale(product, 1m, session: session)))
                .Error!.Code.Should().Be("pos.session_not_owned");
        }
        await using (var s = Scope())
        {
            As(s.ServiceProvider, cashierA);
            (await s.ServiceProvider.GetRequiredService<CashSessionService>().CloseAsync(new CloseSessionCommand(session, 1100m, null)))
                .IsSuccess.Should().BeTrue();
            (await s.ServiceProvider.GetRequiredService<SalesService>().CheckoutAsync(Sale(product, 1m, session: session)))
                .Error!.Code.Should().Be("pos.session_closed");
        }
    }

    // =====================================================================
    // Étape 4 — sessions de caisse : propriété, écart validé par un Manager, clôture figée (§13)
    // =====================================================================

    private async Task<Guid> NewRegister()
    {
        var register = Guid.NewGuid();
        await using var s = Scope();
        await s.ServiceProvider.GetRequiredService<NexoraDbContext>().Database.ExecuteSqlAsync($"""
            INSERT INTO nexora.cash_register (id, company_id, financial_account_id, name, code, currency_id)
            VALUES ({register}, {Company}, {CashAccount}, 'Caisse GF', {"GF" + register.ToString("N")[..6]},
                    (SELECT id FROM nexora.currency WHERE code = 'HTG'))
            """);
        return register;
    }

    private async Task<Guid> OpenSession(Guid user, decimal opening = 1000m)
    {
        var register = await NewRegister();
        await using var s = Scope();
        As(s.ServiceProvider, user);
        var open = await s.ServiceProvider.GetRequiredService<CashSessionService>().OpenAsync(new OpenSessionCommand(register, null, opening));
        open.IsSuccess.Should().BeTrue(open.Error?.Message);
        return open.Value;
    }

    [Fact]
    public async Task A_cashier_cannot_operate_or_see_a_colleagues_session()
    {
        var cashierA = await UserWithRole("cashier");
        var cashierB = await UserWithRole("cashier");
        var session = await OpenSession(cashierA);

        await using var s = Scope();
        As(s.ServiceProvider, cashierB);
        var sessions = s.ServiceProvider.GetRequiredService<CashSessionService>();
        (await sessions.CashInOutAsync(new CashInOutCommand(session, "payout", 500m, "Retrait"))).Error!.Code.Should().Be("pos.session_not_owned");
        (await sessions.CloseAsync(new CloseSessionCommand(session, 0m, null))).Error!.Code.Should().Be("pos.session_not_owned");
        var dashboard = () => s.ServiceProvider.GetRequiredService<IKpiService>().CashierDashboardAsync(session);
        (await dashboard.Should().ThrowAsync<Nexora.Domain.Common.DomainException>()).Which.Error.Code.Should().Be("pos.session_not_owned");
    }

    /// <summary>Recette 2026-09-23 : caisse ouverte sans fond de caisse, aucune vente → la clôture
    /// échouait (mouvement « closing » de 0, refusé par cash_movement_amount_chk).</summary>
    [Fact]
    public async Task A_session_opened_without_float_and_without_sales_closes_cleanly()
    {
        var cashier = await UserWithRole("cashier");
        var empty = await OpenSession(cashier, opening: 0m);
        var withCash = await OpenSession(cashier, opening: 0m);

        await using var s = Scope();
        As(s.ServiceProvider, cashier);
        var sessions = s.ServiceProvider.GetRequiredService<CashSessionService>();

        var closed = await sessions.CloseAsync(new CloseSessionCommand(empty, 0m, null));
        closed.IsSuccess.Should().BeTrue(closed.Error?.Message);
        closed.Value!.Should().Match<SessionSummary>(c => c.Status == "closed" && c.ExpectedBalance == 0m && c.Difference == 0m);

        // Attendu nul mais 100 trouvés dans le tiroir : écart sous le seuil, clôture et écriture d'écart.
        var surplus = await sessions.CloseAsync(new CloseSessionCommand(withCash, 100m, "Monnaie oubliée"));
        surplus.IsSuccess.Should().BeTrue(surplus.Error?.Message);
        surplus.Value!.Should().Match<SessionSummary>(c => c.Status == "closed" && c.Difference == 100m);

        var db = s.ServiceProvider.GetRequiredService<NexoraDbContext>();
        (await db.Database.SqlQuery<int>($"""
            SELECT COUNT(*)::int AS "Value" FROM nexora.cash_movement WHERE cash_session_id IN ({empty}, {withCash})
            """).FirstAsync()).Should().Be(0, "aucun mouvement de caisse nul n'est écrit");
    }

    [Fact]
    public async Task A_zero_cash_in_or_out_is_refused_clearly()
    {
        var cashier = await UserWithRole("cashier");
        var session = await OpenSession(cashier);
        await using var s = Scope();
        As(s.ServiceProvider, cashier);
        (await s.ServiceProvider.GetRequiredService<CashSessionService>()
            .CashInOutAsync(new CashInOutCommand(session, "payout", 0m, "Rien")))
            .Error!.Code.Should().Be("pos.movement_amount");
    }

    [Fact]
    public async Task Large_cash_variance_waits_for_a_distinct_manager_then_the_closing_is_frozen()
    {
        var cashier = await UserWithRole("cashier");
        var manager = await UserWithRole("manager");
        var product = await StockedProduct("CLO");
        var session = await OpenSession(cashier, opening: 1000m);

        await using (var s = Scope())
        {
            As(s.ServiceProvider, cashier);
            (await s.ServiceProvider.GetRequiredService<SalesService>().CheckoutAsync(Sale(product, 2m, session: session)))
                .IsSuccess.Should().BeTrue();
            // Attendu 1 200, compté 400 : écart de −800 > seuil de 500 → validation Manager.
            var closing = await s.ServiceProvider.GetRequiredService<CashSessionService>()
                .CloseAsync(new CloseSessionCommand(session, 400m, "Billets manquants ?"));
            closing.IsSuccess.Should().BeTrue(closing.Error?.Message);
            closing.Value!.Status.Should().Be("closing");
            closing.Value.Difference.Should().Be(-800m);

            (await s.ServiceProvider.GetRequiredService<SalesService>().CheckoutAsync(Sale(product, 1m, session: session)))
                .Error!.Code.Should().Be("pos.session_closed", "plus aucune vente sur une caisse comptée");
            (await s.ServiceProvider.GetRequiredService<CashSessionService>().ValidateCloseAsync(new ValidateCloseCommand(session, "OK")))
                .Error!.Code.Should().Be("pos.manage_sessions_forbidden");
        }

        await using (var s = Scope())
        {
            As(s.ServiceProvider, manager);
            (await s.ServiceProvider.GetRequiredService<ICashSessionQueries>().PendingClosingsAsync())
                .Should().Contain(p => p.SessionId == session && p.Difference == -800m);
            var sessions = s.ServiceProvider.GetRequiredService<CashSessionService>();
            (await sessions.ValidateCloseAsync(new ValidateCloseCommand(session, " "))).Error!.Code.Should().Be("pos.decision_note_required");
            var closed = await sessions.ValidateCloseAsync(new ValidateCloseCommand(session, "Recompté avec le caissier, écart confirmé"));
            closed.IsSuccess.Should().BeTrue(closed.Error?.Message);
            closed.Value!.Status.Should().Be("closed");

            var db = s.ServiceProvider.GetRequiredService<NexoraDbContext>();
            var validatedBy = await db.Database.SqlQuery<Guid?>($"""
                SELECT close_validated_by AS "Value" FROM nexora.cash_session WHERE id = {session}
                """).FirstAsync();
            validatedBy.Should().Be(manager);
            (await db.Database.SqlQuery<int>($"""
                SELECT COUNT(*)::int AS "Value" FROM nexora.audit_log WHERE entity_type = 'cash_session' AND entity_id = {session}
                """).FirstAsync()).Should().BeGreaterThanOrEqualTo(3, "ouverture, comptage et validation sont audités");

            // §13 : une clôture finalisée n'est pas modifiable librement, même en SQL.
            var tamper = () => db.Database.ExecuteSqlAsync($"UPDATE nexora.cash_session SET counted_balance = 1200 WHERE id = {session}");
            await tamper.Should().ThrowAsync<Npgsql.PostgresException>();
        }
    }

    [Fact]
    public async Task A_register_left_open_is_shown_to_cashiers_and_closed_by_a_supervisor()
    {
        var owner = await UserWithRole("cashier");
        var colleague = await UserWithRole("cashier");
        var manager = await UserWithRole("manager");
        var session = await OpenSession(owner);
        Guid register;
        await using (var s = Scope())
            register = await s.ServiceProvider.GetRequiredService<NexoraDbContext>().CashSessions.AsNoTracking()
                .Where(x => x.Id == session).Select(x => x.CashRegisterId).FirstAsync();

        // Le collègue voit que la caisse est occupée, et par qui ; il ne voit pas les sessions des autres.
        await using (var s = Scope())
        {
            As(s.ServiceProvider, colleague);
            var queries = s.ServiceProvider.GetRequiredService<ICashSessionQueries>();
            var reg = (await queries.RegistersAsync()).Single(r => r.Id == register);
            reg.HasOpenSession.Should().BeTrue();
            reg.OpenedBy.Should().Be("cashier test");
            (await queries.OpenSessionsAsync()).Should().BeEmpty("réservé à pos.manage_sessions");
            (await s.ServiceProvider.GetRequiredService<CashSessionService>().OpenAsync(new OpenSessionCommand(register, null, 0m)))
                .Error!.Code.Should().Be("pos.session_already_open");
        }

        // Le responsable la voit et la clôture ; la caisse redevient disponible.
        await using (var s = Scope())
        {
            As(s.ServiceProvider, manager);
            (await s.ServiceProvider.GetRequiredService<ICashSessionQueries>().OpenSessionsAsync())
                .Should().Contain(o => o.SessionId == session && o.CashierId == owner);
            var closed = await s.ServiceProvider.GetRequiredService<CashSessionService>()
                .CloseAsync(new CloseSessionCommand(session, 1000m, "Caisse oubliée ouverte"));
            closed.IsSuccess.Should().BeTrue(closed.Error?.Message);
            closed.Value!.Status.Should().Be("closed");
        }
        await using (var s = Scope())
        {
            As(s.ServiceProvider, colleague);
            (await s.ServiceProvider.GetRequiredService<CashSessionService>().OpenAsync(new OpenSessionCommand(register, null, 500m)))
                .IsSuccess.Should().BeTrue();
        }
    }

    // =====================================================================
    // Étape 9 — validation sur place : le responsable saisit SES identifiants au poste du caissier
    // =====================================================================

    private static string EmailOf(Guid user) => $"GF-{user:N}@test.demo";   // mot de passe des comptes de test : « x »

    private static ManagerOverrideRequest Override(string action, Guid target, Guid manager, string password = "x", string? note = null)
        => new(action, target, EmailOf(manager), password, null, note);

    [Fact]
    public async Task Manager_validates_a_return_on_the_cashiers_terminal_with_his_own_password()
    {
        var cashier = await UserWithRole("cashier");
        var manager = await UserWithRole("manager");
        var (sale, line, _, _) = await CompletedSale("OVR1", 2m);

        await using var s = Scope();
        As(s.ServiceProvider, cashier);
        var request = (await s.ServiceProvider.GetRequiredService<IReturnRequestService>().RequestAsync(ReturnRequestFor(sale, line, 1m))).Value!;
        var overrides = s.ServiceProvider.GetRequiredService<IManagerOverrideService>();

        (await overrides.ExecuteAsync(Override("approve_return", request.Id, manager, password: "faux"), "127.0.0.1"))
            .Error!.Code.Should().Be("pos.override_invalid_credentials");
        (await s.ServiceProvider.GetRequiredService<IReturnRequestService>().GetAsync(request.Id)).Value!.Status.Should().Be("pending_approval");

        var ok = await overrides.ExecuteAsync(Override("approve_return", request.Id, manager, note: "Vérifié en caisse"), "127.0.0.1");
        ok.IsSuccess.Should().BeTrue(ok.Error?.Message);
        ok.Value!.Status.Should().Be("executed");
        ok.Value.ApprovedBy.Should().Be(manager);

        // Le caissier reste le caissier : l'identité du poste est restaurée, sans droit supplémentaire.
        s.ServiceProvider.GetRequiredService<ITenantContext>().UserId.Should().Be(cashier);
        var after = (await s.ServiceProvider.GetRequiredService<IReturnRequestService>().GetAsync(request.Id)).Value!;
        after.DecidedBy.Should().Be(manager, "la décision est enregistrée au nom du responsable");

        // L'audit de la décision porte le responsable, pas le caissier.
        var db = s.ServiceProvider.GetRequiredService<NexoraDbContext>();
        var auditUsers = await db.Database.SqlQuery<Guid?>($"""
            SELECT user_id AS "Value" FROM nexora.audit_log
            WHERE entity_type = 'sale_return_request' AND entity_id = {request.Id} AND action = 'UPDATE'
            """).ToListAsync();
        auditUsers.Should().Contain(manager);
        (await db.Database.SqlQuery<int>($"""
            SELECT COUNT(*)::int AS "Value" FROM nexora.audit_log
            WHERE entity_type = 'security_event' AND user_id = {manager} AND action = 'pos.override_succeeded'
            """).FirstAsync()).Should().Be(1);
    }

    [Fact]
    public async Task On_site_validation_requires_the_right_permission_and_another_person()
    {
        var cashier = await UserWithRole("cashier");
        var stock = await UserWithRole("stock_manager");   // pas de pos.refund
        var (sale, line, _, _) = await CompletedSale("OVR2", 1m);

        await using var s = Scope();
        As(s.ServiceProvider, cashier);
        var request = (await s.ServiceProvider.GetRequiredService<IReturnRequestService>().RequestAsync(ReturnRequestFor(sale, line, 1m))).Value!;
        var overrides = s.ServiceProvider.GetRequiredService<IManagerOverrideService>();
        (await overrides.ExecuteAsync(Override("approve_return", request.Id, stock), null)).Error!.Code.Should().Be("pos.override_forbidden");
        (await overrides.ExecuteAsync(Override("approve_return", request.Id, cashier), null)).Error!.Code.Should().Be("pos.self_approval");
    }

    [Fact]
    public async Task Repeated_wrong_manager_passwords_lock_the_manager_account()
    {
        var cashier = await UserWithRole("cashier");
        var manager = await UserWithRole("manager");
        await using var s = Scope();
        As(s.ServiceProvider, cashier);
        var overrides = s.ServiceProvider.GetRequiredService<IManagerOverrideService>();
        for (var i = 0; i < 5; i++)
            await overrides.ExecuteAsync(Override("approve_return", Guid.NewGuid(), manager, password: "faux"), null);
        (await overrides.ExecuteAsync(Override("approve_return", Guid.NewGuid(), manager), null))
            .Error!.Code.Should().Be("pos.override_locked", "même le bon mot de passe est refusé pendant le verrouillage");
    }

    [Fact]
    public async Task Manager_validates_an_exceptional_discount_and_a_cash_variance_on_site()
    {
        var cashier = await UserWithRole("cashier");
        var manager = await UserWithRole("manager");
        var product = await StockedProduct("OVR3");
        var cart = Guid.NewGuid();
        var session = await OpenSession(cashier, opening: 1000m);

        await using var s = Scope();
        As(s.ServiceProvider, cashier);
        var discount = (await s.ServiceProvider.GetRequiredService<IDiscountRequestService>().RequestAsync(
            new CreateDiscountRequest(cart, product, 1m, 20m, "HTG", Officine, null, "Client fidèle"))).Value!;
        var overrides = s.ServiceProvider.GetRequiredService<IManagerOverrideService>();
        (await overrides.ExecuteAsync(Override("approve_discount", discount.Id, manager), null)).Value!.Status.Should().Be("approved");
        (await s.ServiceProvider.GetRequiredService<SalesService>().CheckoutAsync(Sale(product, 1m, 20m, cart, session: session)))
            .IsSuccess.Should().BeTrue("la remise validée sur place débloque l'encaissement");

        var closing = await s.ServiceProvider.GetRequiredService<CashSessionService>().CloseAsync(new CloseSessionCommand(session, 100m, null));
        closing.Value!.Status.Should().Be("closing");
        (await overrides.ExecuteAsync(Override("validate_close", session, manager), null))
            .Error!.Code.Should().Be("pos.decision_note_required");
        (await overrides.ExecuteAsync(Override("validate_close", session, manager, note: "Écart recompté ensemble"), null))
            .Value!.Status.Should().Be("closed");
    }

    // =====================================================================
    // Résumé fonctionnel — RF-02 : BESOIN → COMMANDE → APPROBATION → RÉCEPTION (§9)
    // =====================================================================

    [Fact]
    public async Task Goods_can_only_be_received_against_an_approved_order_and_never_beyond_what_was_ordered()
    {
        var product = await StockedProduct("RCV", stock: 1m);
        await using var s = Scope();
        As(s.ServiceProvider, Direction);
        var purchasing = s.ServiceProvider.GetRequiredService<PurchasingService>();
        var db = s.ServiceProvider.GetRequiredService<NexoraDbContext>();

        var order = (await purchasing.CreateOrderAsync(new CreatePurchaseOrderCommand(
            Supplier1, null, "HTG", null, [new CreatePurchaseOrderLine(product, 10m, 40m, null, 0m)], null))).Value;
        var line = await db.Set<Nexora.Domain.Purchasing.PurchaseOrderLine>().AsNoTracking()
            .Where(l => l.PurchaseOrderId == order).Select(l => l.Id).FirstAsync();
        ReceiveGoodsCommand Receive(decimal qty) => new(Guid.NewGuid(), Supplier1, Officine, order,
            [new ReceiveLine(product, line, qty, 40m, null, null)], null, null);

        (await purchasing.ReceiveAsync(Receive(5m))).Error!.Code.Should().Be("purchasing.po_not_receivable", "commande non approuvée");

        (await purchasing.ApproveOrderAsync(order)).IsSuccess.Should().BeTrue();
        (await purchasing.ReceiveAsync(Receive(11m))).Error!.Code.Should().Be("purchasing.over_receipt");
        (await purchasing.ReceiveAsync(Receive(6m))).IsSuccess.Should().BeTrue("réception partielle");
        (await purchasing.ReceiveAsync(Receive(5m))).Error!.Code.Should().Be("purchasing.over_receipt", "il n'en reste que 4");
        (await purchasing.ReceiveAsync(Receive(4m))).IsSuccess.Should().BeTrue();

        var cancelled = (await purchasing.CreateOrderAsync(new CreatePurchaseOrderCommand(
            Supplier1, null, "HTG", null, [new CreatePurchaseOrderLine(product, 3m, 40m, null, 0m)], null))).Value;
        (await purchasing.CancelOrderAsync(cancelled)).IsSuccess.Should().BeTrue();
        (await purchasing.ReceiveAsync(new ReceiveGoodsCommand(Guid.NewGuid(), Supplier1, Officine, cancelled,
            [new ReceiveLine(product, null, 3m, 40m, null, null)], null, null))).Error!.Code.Should().Be("purchasing.po_not_receivable");
    }

    // =====================================================================
    // Résumé fonctionnel — RF-03 / BUG-016 : dépenses réelles et assistant fondé sur les données (§11, §14)
    // =====================================================================

    [Fact]
    public async Task A_recorded_expense_creates_its_document_and_reaches_the_expense_and_profit_kpis()
    {
        await using var s = Scope();
        As(s.ServiceProvider, Direction);
        var kpi = s.ServiceProvider.GetRequiredService<IKpiService>();
        decimal Kpi(CommandCenterResponse cc, string key) => cc.Kpis.First(k => k.Key == key).Value;
        var before = await kpi.CommandCenterAsync(new ReportFilter { Preset = "month" });

        var expenseId = Guid.NewGuid();
        var operation = Guid.NewGuid();
        var request = new Nexora.Contracts.Finance.RecordExpenseRequest(operation, expenseId, DateTimeOffset.UtcNow,
            12_345m, 0m, "HTG", CashAccount, "6270", "Frais bancaires GF");
        var finance = s.ServiceProvider.GetRequiredService<Nexora.Application.Finance.IFinanceOperations>();
        (await finance.RecordExpenseAsync(request)).IsSuccess.Should().BeTrue();
        (await finance.RecordExpenseAsync(request)).IsSuccess.Should().BeTrue("rejeu idempotent");

        var db = s.ServiceProvider.GetRequiredService<NexoraDbContext>();
        (await db.Database.SqlQuery<int>($"""
            SELECT COUNT(*)::int AS "Value" FROM nexora.expense WHERE id = {expenseId} AND status = 'paid'
            """).FirstAsync()).Should().Be(1, "la pièce de dépense existe désormais (BUG-016)");

        var after = await kpi.CommandCenterAsync(new ReportFilter { Preset = "month" });
        (Kpi(after, KpiCatalog.Expenses) - Kpi(before, KpiCatalog.Expenses)).Should().Be(12_345m);
        (Kpi(before, KpiCatalog.Profit) - Kpi(after, KpiCatalog.Profit)).Should().Be(12_345m);
    }

    [Fact]
    public async Task The_assistant_answers_the_five_reference_questions_from_real_data()
    {
        var product = await StockedProduct("ASK", stock: 3m);
        await using var s = Scope();
        As(s.ServiceProvider, Direction);
        await s.ServiceProvider.GetRequiredService<NexoraDbContext>().Database.ExecuteSqlAsync($"""
            INSERT INTO nexora.product_reorder_rule (company_id, product_id, inventory_location_id, min_quantity, reorder_point, reorder_quantity, status)
            VALUES ({Company}, {product}, {Officine}, 2, 10, 20, 'active')
            """);
        (await s.ServiceProvider.GetRequiredService<SalesService>().CheckoutAsync(Sale(product, 2m))).IsSuccess.Should().BeTrue();
        (await s.ServiceProvider.GetRequiredService<Nexora.Application.Finance.IFinanceOperations>().RecordExpenseAsync(
            new Nexora.Contracts.Finance.RecordExpenseRequest(Guid.NewGuid(), Guid.NewGuid(), DateTimeOffset.UtcNow,
                987_654m, 0m, "HTG", CashAccount, "6270", "Frais exceptionnels"))).IsSuccess.Should().BeTrue();

        var ask = s.ServiceProvider.GetRequiredService<IAskNexora>();
        async Task<AskResponse> Ask(string q) => await ask.AskAsync(new AskRequest(q, null));

        var profit = await Ask("Pourquoi mes bénéfices ont diminué ?");
        profit.Intent.Should().Be("profit_change");
        profit.Facts.Select(f => f.Key).Should().Contain(["profit", "profit_previous", "expenses", "expenses_previous"]);
        profit.Answer.Should().Contain("dépenses en hausse");

        var reorderAnswer = await Ask("Quels produits dois-je commander ?");
        reorderAnswer.Intent.Should().Be("reorder");
        reorderAnswer.Answer.Should().Contain("Article GF ASK");

        var stockout = await Ask("Quel produit risque de tomber en rupture ?");
        stockout.Intent.Should().Be("stockout_risk");
        stockout.ContainsForecast.Should().BeTrue("une couverture en jours est une estimation");
        stockout.Facts.Should().NotBeEmpty();

        (await Ask("Quels clients me doivent le plus ?")).Intent.Should().Be("top_debtors");

        var expenses = await Ask("Quelles dépenses ont augmenté ?");
        expenses.Intent.Should().Be("expense_increase");
        expenses.Facts.Should().Contain(f => f.Key == "6270" && f.Value >= 987_654m);
    }

    [Fact]
    public async Task The_assistant_only_answers_with_data_the_user_may_see()
    {
        var cashier = await UserWithRole("cashier");
        var stock = await UserWithRole("stock_manager");
        await using (var s = Scope())
        {
            As(s.ServiceProvider, cashier);
            var ask = s.ServiceProvider.GetRequiredService<IAskNexora>();
            foreach (var q in new[] { "Pourquoi mes bénéfices ont diminué ?", "Quels produits dois-je commander ?",
                                      "Quel produit risque de tomber en rupture ?", "Quels clients me doivent le plus ?",
                                      "Quelles dépenses ont augmenté ?" })
                (await ask.AskAsync(new AskRequest(q, null))).Intent.Should().Be("forbidden", q);
        }
        await using (var s = Scope())
        {
            As(s.ServiceProvider, stock);
            var ask = s.ServiceProvider.GetRequiredService<IAskNexora>();
            (await ask.AskAsync(new AskRequest("Quels produits dois-je commander ?", null))).Intent.Should().Be("reorder");
            (await ask.AskAsync(new AskRequest("Pourquoi mes bénéfices ont diminué ?", null))).Intent.Should().Be("forbidden");
        }
    }

    // ---- RF-04 : New → Viewed → Accepted / Modified / Ignored → Executed / Expired (§17) ----
    [Fact]
    public async Task Recommendation_lifecycle_goes_through_viewed_and_is_executed_only_after_a_decision()
    {
        var customer = await NewCustomer(0m);
        await using var s = Scope();
        As(s.ServiceProvider, Direction);
        await s.ServiceProvider.GetRequiredService<NexoraDbContext>().Database.ExecuteSqlAsync($"""
            INSERT INTO nexora.customer_receivable (id, company_id, customer_id, currency_id, original_amount, issue_date, due_date, status)
            VALUES ({Guid.NewGuid()}, {Company}, {customer}, (SELECT id FROM nexora.currency WHERE code = 'HTG'),
                    800, current_date - 70, current_date - 40, 'OVERDUE')
            """);
        var recs = s.ServiceProvider.GetRequiredService<Nexora.Application.Intelligence.IRecommendationService>();
        await recs.GenerateAsync();
        var rec = (await recs.ListAsync("open", "receivable_priority")).Single(r => r.DrillPath == $"receivables/customer/{customer}");
        rec.Status.Should().Be("new");

        var outcome = new Nexora.Contracts.Intelligence.ResolveRecommendationRequest("positive", "fait");
        (await recs.ResolveAsync(rec.Id, outcome)).IsSuccess.Should().BeFalse("exécuter sans décision préalable est interdit");

        var viewed = await recs.MarkViewedAsync(rec.Id);
        viewed.Value!.Status.Should().Be("viewed");
        viewed.Value.ViewedAt.Should().NotBeNull();
        (await recs.ListAsync("open", "receivable_priority")).Should().Contain(r => r.Id == rec.Id, "une recommandation vue reste à décider");

        (await recs.AcceptAsync(rec.Id, new Nexora.Contracts.Intelligence.AcceptRecommendationRequest())).Value!.Status.Should().Be("accepted");
        (await recs.MarkViewedAsync(rec.Id)).Value!.Status.Should().Be("accepted", "consulter une recommandation décidée ne change pas son état");
        (await recs.ResolveAsync(rec.Id, outcome)).Value!.Status.Should().Be("resolved");
    }

    // =====================================================================
    // Résumé fonctionnel — RF-05 : UTILISATEUR → ENTREPRISE → SUCCURSALE (§4)
    // =====================================================================

    private static readonly Guid Delmas = Guid.Parse("dce00000-0000-0000-0000-0000000000f1");   // succursale de l'Officine

    /// <summary>Seconde succursale de la Pharmacie : établissement, point de vente, emplacement, caisse.</summary>
    private async Task<(Guid Establishment, Guid Location, Guid Register)> SecondBranch()
    {
        var (est, store, loc, reg) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var code = est.ToString("N")[..6];
        await using var s = Scope();
        As(s.ServiceProvider, Direction);
        await s.ServiceProvider.GetRequiredService<NexoraDbContext>().Database.ExecuteSqlAsync($"""
            INSERT INTO nexora.establishment (id, company_id, name, code) VALUES ({est}, {Company}, 'Pharmacie ABC — Carrefour', {"CAR" + code});
            INSERT INTO nexora.store (id, company_id, establishment_id, name, code) VALUES ({store}, {Company}, {est}, 'Comptoir Carrefour', {"SC" + code});
            INSERT INTO nexora.inventory_location (id, company_id, establishment_id, store_id, name, location_type, code)
                VALUES ({loc}, {Company}, {est}, {store}, 'Officine Carrefour', 'store', {"LC" + code});
            INSERT INTO nexora.cash_register (id, company_id, store_id, financial_account_id, name, code, currency_id)
                VALUES ({reg}, {Company}, {store}, {CashAccount}, 'Caisse Carrefour', {"RC" + code}, (SELECT id FROM nexora.currency WHERE code = 'HTG'));
            """);
        return (est, loc, reg);
    }

    [Fact]
    public async Task A_user_assigned_to_a_branch_only_sees_and_operates_that_branch()
    {
        var (carrefour, carrefourLocation, carrefourRegister) = await SecondBranch();
        var product = await StockedProduct("BR");   // stock à l'Officine (Delmas)
        Guid branchManager, saleDelmas, saleCarrefour;
        await using (var s = Scope())
        {
            As(s.ServiceProvider, Direction);
            await s.ServiceProvider.GetRequiredService<PurchasingService>().ReceiveAsync(new ReceiveGoodsCommand(
                Guid.NewGuid(), Supplier1, carrefourLocation, null, [new ReceiveLine(product, null, 20m, 40m, null, null)], null, null));
            var sales = s.ServiceProvider.GetRequiredService<SalesService>();
            saleDelmas = (await sales.CheckoutAsync(Sale(product, 1m))).Value!.SaleId;
            var atCarrefour = await sales.CheckoutAsync(new CheckoutCommand(Guid.NewGuid(), carrefourLocation, null, null, null, null, "HTG",
                [new CheckoutLine(product, 2m, null, 0m, null)], [new TenderInput(CashAccount, CashMethod, 200m, null)], 0m, null, null, null, null));
            atCarrefour.IsSuccess.Should().BeTrue(atCarrefour.Error?.Message);
            saleCarrefour = atCarrefour.Value!.SaleId;

            // Chaque vente appartient à la succursale de son emplacement de stock.
            var db = s.ServiceProvider.GetRequiredService<NexoraDbContext>();
            (await db.Sales.AsNoTracking().Where(x => x.Id == saleCarrefour).Select(x => x.EstablishmentId).FirstAsync()).Should().Be(carrefour);
            (await db.Sales.AsNoTracking().Where(x => x.Id == saleDelmas).Select(x => x.EstablishmentId).FirstAsync()).Should().Be(Delmas);
        }

        // Gérant affecté à la seule succursale de Delmas.
        branchManager = Guid.NewGuid();
        await using (var c = new Npgsql.NpgsqlConnection(fx.TestConnectionString))
        {
            await c.OpenAsync();
            await using var cmd = new Npgsql.NpgsqlCommand($"""
                INSERT INTO nexora.app_user (id, email, password_hash, full_name, status)
                VALUES ('{branchManager}', 'GF-{branchManager:N}@test.demo', crypt('x', gen_salt('bf', 4)), 'Gérant Delmas', 'active')
                """, c);
            await cmd.ExecuteNonQueryAsync();
        }
        await using (var s = Scope())
        {
            As(s.ServiceProvider, Direction);
            (await s.ServiceProvider.GetRequiredService<IUserAssignmentService>().GrantAsync(branchManager,
                new GrantAssignmentRequest("manager", "ESTABLISHMENT", Company, Delmas, null, null, null))).IsSuccess.Should().BeTrue();
            var resolver = s.ServiceProvider.GetRequiredService<IBranchScopeResolver>();
            (await resolver.ResolveAsync(branchManager, [Company])).Should().BeEquivalentTo([Delmas]);
            (await resolver.ResolveAsync(Direction, [Company])).Should().BeNull("la direction est affectée au groupe");
        }

        // Rôle applicatif réel (nexora_app) : la barrière est portée par PostgreSQL (RLS), pas par le code.
        await using var appRole = fx.CreateAppRoleProvider();
        await using (var s = appRole.CreateAsyncScope())
        {
            As(s.ServiceProvider, branchManager);
            ((TestTenantContext)s.ServiceProvider.GetRequiredService<ITenantContext>()).SetEstablishments([Delmas]);
            var queries = s.ServiceProvider.GetRequiredService<ISalesQueries>();
            (await queries.GetSaleAsync(saleDelmas)).Should().NotBeNull();
            (await queries.GetSaleAsync(saleCarrefour)).Should().BeNull("vente d'une autre succursale");

            (await s.ServiceProvider.GetRequiredService<ICashSessionQueries>().RegistersAsync())
                .Should().NotContain(r => r.Id == carrefourRegister).And.Contain(r => r.Name == "Caisse officine");
            (await s.ServiceProvider.GetRequiredService<IStockLedger>().OnHandAsync(product, carrefourLocation))
                .Should().Be(0m, "le stock d'une autre succursale n'est pas visible");

            // Il ne peut pas vendre sur le stock d'une autre succursale.
            var foreign = await s.ServiceProvider.GetRequiredService<SalesService>().CheckoutAsync(new CheckoutCommand(
                Guid.NewGuid(), carrefourLocation, null, null, null, null, "HTG", [new CheckoutLine(product, 1m, null, 0m, null)],
                [new TenderInput(CashAccount, CashMethod, 100m, null)], 0m, null, null, null, null));
            foreign.IsSuccess.Should().BeFalse();
        }

        await using (var s = appRole.CreateAsyncScope())
        {
            As(s.ServiceProvider, Direction);   // sans restriction : les deux succursales
            (await s.ServiceProvider.GetRequiredService<ISalesQueries>().GetSaleAsync(saleCarrefour)).Should().NotBeNull();
        }
    }

    [Fact]
    public async Task Small_cash_variance_closes_immediately()
    {
        var cashier = await UserWithRole("cashier");
        var session = await OpenSession(cashier, opening: 1000m);
        await using var s = Scope();
        As(s.ServiceProvider, cashier);
        var closed = await s.ServiceProvider.GetRequiredService<CashSessionService>().CloseAsync(new CloseSessionCommand(session, 980m, null));
        closed.IsSuccess.Should().BeTrue(closed.Error?.Message);
        closed.Value!.Status.Should().Be("closed");
        closed.Value.Difference.Should().Be(-20m);
    }

    // =====================================================================
    // Étape 5 — recherche POS bornée, sans coût ; masquage du prix d'achat (§7.2, §9, §24, §27)
    // =====================================================================

    [Fact]
    public async Task Pos_search_returns_a_short_relevant_list_of_sellable_products_only()
    {
        var cashier = await UserWithRole("cashier");
        var tag = $"Zq{Guid.NewGuid():N}"[..8];   // mot unique pour isoler ce test
        var products = new List<Guid>();
        for (var i = 0; i < 12; i++)
            products.Add(await NamedProduct($"{tag} article {i:00}"));
        var inactive = await NamedProduct($"{tag} inactif", status: "inactive");

        await using var s = Scope();
        As(s.ServiceProvider, cashier);
        var search = s.ServiceProvider.GetRequiredService<IPosCatalogQueries>();

        var tooShort = await search.SearchAsync("Z", Officine);
        tooShort.Items.Should().BeEmpty("rien n'est chargé avant le minimum de caractères");
        tooShort.MinChars.Should().Be(2);

        var hits = await search.SearchAsync(tag.ToLowerInvariant(), Officine);
        hits.Items.Should().HaveCount(8, "liste courte : 8 résultats par défaut, jamais le catalogue complet");
        hits.Items.Should().NotContain(h => h.ProductId == inactive);
        hits.Items.Should().OnlyContain(h => h.SalePrice == 100m && h.Availability == "in_stock");

        var sku = await s.ServiceProvider.GetRequiredService<NexoraDbContext>().Products.AsNoTracking()
            .Where(p => p.Id == products[5]).Select(p => p.Sku).FirstAsync();
        (await search.SearchAsync(sku, Officine)).Items[0].ProductId.Should().Be(products[5], "la correspondance exacte passe en tête");
        (await search.ScanAsync(sku, Officine))!.ProductId.Should().Be(products[5]);
        var inactiveSku = await s.ServiceProvider.GetRequiredService<NexoraDbContext>().Products.AsNoTracking()
            .Where(p => p.Id == inactive).Select(p => p.Sku).FirstAsync();
        (await search.ScanAsync(inactiveSku, Officine)).Should().BeNull("un produit inactif n'est pas proposé au POS");
    }

    private async Task<Guid> NamedProduct(string name, string? status = null)
    {
        await using var s = Scope();
        As(s.ServiceProvider, Direction);
        var catalog = s.ServiceProvider.GetRequiredService<CatalogService>();
        var product = (await catalog.CreateProductAsync(new CreateProductCommand(
            $"GS-{Guid.NewGuid():N}"[..18], name, null, BoxUnit, "stockable", null, null, null, null, false, false))).Value;
        await catalog.SetPriceAsync(new SetProductPriceCommand(product, "sale", 100m, "HTG", 0m, null, null));
        await s.ServiceProvider.GetRequiredService<PurchasingService>().ReceiveAsync(new ReceiveGoodsCommand(
            Guid.NewGuid(), Supplier1, Officine, null, [new ReceiveLine(product, null, 30m, 40m, null, null)], null, null));
        if (status is not null)
            await s.ServiceProvider.GetRequiredService<NexoraDbContext>().Database
                .ExecuteSqlAsync($"UPDATE nexora.product SET status = {status} WHERE id = {product}");
        return product;
    }

    [Fact]
    public async Task Purchase_cost_is_never_sent_without_catalog_view_cost()
    {
        var salesperson = await UserWithRole("salesperson");   // catalog.view, sans catalog.view_cost
        var cashier = await UserWithRole("cashier");
        var (sale, _, product, _) = await CompletedSale("CST", 1m);
        await using (var s = Scope())
        {
            As(s.ServiceProvider, Direction);
            (await s.ServiceProvider.GetRequiredService<CatalogService>().AddSupplierAsync(
                new AddProductSupplierCommand(product, Supplier1, "FRN-REF", 40m, "HTG", 3, true))).IsSuccess.Should().BeTrue();
            var full = await s.ServiceProvider.GetRequiredService<ICatalogQueries>().GetProductAsync(product);
            full!.Suppliers.Should().ContainSingle().Which.PurchasePrice.Should().Be(40m);
            (await s.ServiceProvider.GetRequiredService<ISalesQueries>().GetSaleAsync(sale))!.Lines[0].UnitCost.Should().Be(40m);
        }
        await using (var s = Scope())
        {
            As(s.ServiceProvider, salesperson);
            var masked = await s.ServiceProvider.GetRequiredService<ICatalogQueries>().GetProductAsync(product);
            masked!.Suppliers.Should().BeEmpty();
            masked.Prices.Should().NotContain(p => p.PriceType == "purchase");
        }
        await using (var s = Scope())
        {
            As(s.ServiceProvider, cashier);
            (await s.ServiceProvider.GetRequiredService<ISalesQueries>().GetSaleAsync(sale))!.Lines[0].UnitCost.Should().BeNull();
        }
    }

    [Fact]
    public async Task A_cashier_voids_his_own_parked_cart_but_not_a_colleagues()
    {
        var cashierA = await UserWithRole("cashier");
        var cashierB = await UserWithRole("cashier");
        var product = await StockedProduct("VOI");
        Guid parked;
        await using (var s = Scope())
        {
            As(s.ServiceProvider, cashierA);
            parked = (await s.ServiceProvider.GetRequiredService<SalesService>().ParkAsync(new ParkCartCommand(
                Officine, null, "HTG", "Client au téléphone", [new CheckoutLine(product, 1m, null, 0m, null)]))).Value;
        }
        await using (var s = Scope())
        {
            As(s.ServiceProvider, cashierB);
            (await s.ServiceProvider.GetRequiredService<SalesService>().VoidAsync(parked, "Erreur")).Error!.Code.Should().Be("pos.void_forbidden");
        }
        await using (var s = Scope())
        {
            As(s.ServiceProvider, cashierA);
            (await s.ServiceProvider.GetRequiredService<SalesService>().VoidAsync(parked, "Client parti")).IsSuccess.Should().BeTrue();
        }
    }
}
