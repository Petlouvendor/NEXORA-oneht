using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Nexora.Application.Catalog;
using Nexora.Application.Credit;
using Nexora.Application.Finance;
using Nexora.Application.Finance.Engine;
using Nexora.Application.Finance.Events;
using Nexora.Application.Identity;
using Nexora.Application.Intelligence;
using Nexora.Application.Inventory;
using Nexora.Application.Parties;
using Nexora.Application.Purchasing;
using Nexora.Application.Reporting;
using Nexora.Application.Sales;
using Nexora.Contracts.Catalog;
using Nexora.Contracts.Credit;
using Nexora.Contracts.Documents;
using Nexora.Contracts.Finance;
using Nexora.Contracts.Identity;
using Nexora.Contracts.Parties;
using Nexora.Contracts.Purchasing;
using Nexora.Contracts.Reporting;
using Nexora.Contracts.Sales;
using Nexora.Domain.Common;
using Nexora.Domain.Purchasing;
using Nexora.Domain.Sales;
using Nexora.Infrastructure.Persistence;
using Nexora.Infrastructure.Tenancy;
using Npgsql;

namespace Nexora.RecetteSeed;

internal sealed class SeedException(string message) : Exception(message);

/// <summary>Données de travail d'une entreprise pendant la génération.</summary>
internal sealed class CompanyRun(Co co)
{
    public Co Co { get; } = co;
    public List<Guid> Suppliers { get; } = [];
    public Dictionary<string, Guid> Categories { get; } = [];
    public Dictionary<string, Guid> Customers { get; } = [];
    public List<(ProductSpec Spec, Guid Id)> Products { get; } = [];
    public Dictionary<Guid, decimal> OnHand { get; } = [];
    public List<Guid> RecentSales { get; } = [];
    public List<PlannedPayment> Payments { get; } = [];
    public int Sales, CreditSales, Receipts, Invoices, Expenses;
}

internal sealed record PlannedPayment(int Day, Guid CustomerId, Guid ReceivableId, decimal Amount);

/// <summary>
/// Génère le jeu de recette (Phase 55 §5, §28, §29) sur ~100 jours d'activité, pour la Pharmacie
/// et le Supermarché. Chaque opération passe par le service métier réel ; tout refus d'un service
/// arrête la génération (jamais de donnée incohérente silencieuse).
/// </summary>
internal sealed class RecetteSeeder(IServiceProvider root, string connectionString)
{
    private static readonly Guid Direction = Co.G("0000-0000000000d1");
    private const string Password = "Recette2026!";
    private const int HistoryDays = 100;

    private readonly Random _rng = new(55);
    private readonly DateTimeOffset _today = new(DateTime.UtcNow.Date, TimeSpan.Zero);

    private DateTimeOffset Day(int offset, int hour = 10) =>
        _today.AddDays(offset).AddHours(hour).AddMinutes(_rng.Next(0, 55));

    // ------------------------------------------------------------------ infrastructure

    private async Task<T> In<T>(Co co, Func<IServiceProvider, Task<T>> action, Guid? user = null)
    {
        await using var scope = root.CreateAsyncScope();
        var tenant = scope.ServiceProvider.GetRequiredService<TenantContext>();
        tenant.SetUser(user ?? Direction);
        tenant.SetScope([co.Id], [], co.Id);
        return await action(scope.ServiceProvider);
    }

    private Task In(Co co, Func<IServiceProvider, Task> action) =>
        In<int>(co, async sp => { await action(sp); return 0; });

    private static T Must<T>(Result<T> r, string what) =>
        r.IsSuccess ? r.Value! : throw new SeedException($"{what} : {r.Error!.Code} — {r.Error.Message}");

    private static void Must(Result r, string what)
    {
        if (!r.IsSuccess) throw new SeedException($"{what} : {r.Error!.Code} — {r.Error.Message}");
    }

    private async Task SqlAsync(string sql, params (string Name, object? Value)[] args)
    {
        await using var c = new NpgsqlConnection(connectionString);
        await c.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, c);
        foreach (var (n, v) in args) cmd.Parameters.AddWithValue(n, v ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task<T?> ScalarAsync<T>(string sql)
    {
        await using var c = new NpgsqlConnection(connectionString);
        await c.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, c);
        var v = await cmd.ExecuteScalarAsync();
        return v is null or DBNull ? default : (T)v;
    }

    private static void Step(string text) => Console.WriteLine($"  · {text}");

    // ------------------------------------------------------------------ orchestration

    public async Task RunAsync()
    {
        if (await ScalarAsync<long>("SELECT COUNT(*) FROM nexora.product WHERE sku = 'SM-RIZ5'") > 0)
            throw new SeedException("la base contient déjà le jeu de recette — relancez database/scripts/recette.sh pour la reconstruire.");

        foreach (var co in new[] { Co.Pharmacy, Co.Supermarket })
        {
            Console.WriteLine($"== {co.Name}");
            var run = new CompanyRun(co);
            await SuppliersAsync(run);
            await CatalogAsync(run);
            await CustomersAsync(run);
            await UsersAsync(run);
            await HistoryAsync(run);
            await TodayAsync(run);
            await AnalyticsAsync(run);
            Step($"{run.Products.Count} produits, {run.Customers.Count} clients, {run.Suppliers.Count + (co == Co.Pharmacy ? 1 : 0)} fournisseurs, " +
                 $"{run.Sales} ventes dont {run.CreditSales} à crédit, {run.Receipts} réceptions, {run.Invoices} factures fournisseur, {run.Expenses} dépenses");
        }
    }

    // ------------------------------------------------------------------ tiers

    private async Task SuppliersAsync(CompanyRun run)
    {
        if (run.Co == Co.Pharmacy)
        {
            run.Suppliers.Add(Co.G("0000-00000000b101"));   // Distrimed (démo)
            run.Suppliers.Add(Co.G("0000-00000000b102"));   // PharmaPlus (démo)
            await In(run.Co, async sp => Must(await sp.GetRequiredService<ISupplierService>().CreateAsync(
                new CreateSupplierRequest("Laboratoire du Sud (nouveau)", Email: "contact@labo-sud.demo")), "fournisseur sans historique"));
            Step("fournisseurs : 2 de la démo + 1 sans historique");
            return;
        }
        foreach (var (name, email, tax) in Catalog.SuperSuppliers)
        {
            var s = await In(run.Co, async sp => Must(await sp.GetRequiredService<ISupplierService>().CreateAsync(
                new CreateSupplierRequest(name, Email: email, TaxIdentifier: tax, Phone: "+509 2940 0000")), $"fournisseur {name}"));
            run.Suppliers.Add(s.Id);
        }
        Step($"fournisseurs : {run.Suppliers.Count}");
    }

    private async Task CustomersAsync(CompanyRun run)
    {
        (string Key, CreateCustomerRequest Req)[] list = run.Co == Co.Pharmacy
            ?
            [
                ("comptant", new("Mme Claudette Pierre", Phone: "+509 3711 2233")),
                ("bon_payeur", new("Clinique Bon Samaritain", "business")),   // placeholder, remplacé par la fiche démo
                ("retard", new("Dispensaire Communautaire de Carrefour", "business", Phone: "+509 2813 4455", TaxIdentifier: "NIF-DISP-221", CreditLimit: 100_000m)),
                ("limite", new("Centre Médical Delmas 60", "business", Email: "compta@cmd60.demo", CreditLimit: 40_000m)),
                ("inactif", new("M. Robert Étienne", Phone: "+509 3456 7788")),
                ("sans_historique", new("Mlle Nadège Jean (nouvelle cliente)")),
            ]
            :
            [
                ("comptant", new("Marie Joseph", Phone: "+509 3802 1122")),
                ("bon_payeur", new("Restaurant Le Bon Goût", "business", Email: "achats@bongout.demo", TaxIdentifier: "NIF-BG-778", CreditLimit: 150_000m)),
                ("retard", new("Épicerie Ti Marché", "business", Phone: "+509 2944 5566", CreditLimit: 80_000m)),
                ("limite", new("Hôtel Montagne", "business", Email: "eco@hotelmontagne.demo", CreditLimit: 60_000m)),
                ("bloque", new("Boutique Fermée SARL", "business", CreditLimit: 30_000m)),
                ("inactif", new("Jean-Pierre Louis", Phone: "+509 3777 8899")),
                ("gros_compte", new("École Saint-Joseph", "business", Email: "intendance@stjoseph.demo", TaxIdentifier: "NIF-ESJ-015", CreditLimit: 200_000m)),
                ("sans_historique", new("Nouveau client — sans historique")),
            ];

        foreach (var (key, req) in list)
        {
            if (run.Co == Co.Pharmacy && key == "bon_payeur")
            {
                run.Customers[key] = Co.G("0000-00000000b001");   // Clinique Bon Samaritain (démo, limite 500 000)
                continue;
            }
            var c = await In(run.Co, async sp => Must(await sp.GetRequiredService<ICustomerService>().CreateAsync(req), $"client {req.Name}"));
            run.Customers[key] = c.Id;
        }
        Step($"clients : {string.Join(", ", run.Customers.Keys)}");
    }

    private async Task UsersAsync(CompanyRun run)
    {
        (string Email, string Name, string Role)[] users = run.Co == Co.Pharmacy
            ?
            [
                ("caisse.pharma@recette.demo", "Caissière Pharmacie (recette)", "cashier"),
                ("stock.pharma@recette.demo", "Gestionnaire de stock Pharmacie", "stock_manager"),
                ("compta.pharma@recette.demo", "Comptable Pharmacie", "accountant"),
                ("gerant.pharma@recette.demo", "Gérant Pharmacie", "manager"),
            ]
            :
            [
                ("caisse1.super@recette.demo", "Caissier 1 Supermarché", "cashier"),
                ("caisse2.super@recette.demo", "Caissière 2 Supermarché", "cashier"),
                ("vendeur.super@recette.demo", "Vendeur Supermarché", "salesperson"),
                ("stock.super@recette.demo", "Gestionnaire de stock Supermarché", "stock_manager"),
                ("achats.super@recette.demo", "Acheteur Supermarché", "purchasing_manager"),
                ("compta.super@recette.demo", "Comptable Supermarché", "accountant"),
                ("auditeur.super@recette.demo", "Auditeur (lecture seule)", "auditor"),
                ("gerant.super@recette.demo", "Gérant Supermarché", "manager"),
                ("ancien.employe@recette.demo", "Ancien employé (compte suspendu)", "cashier"),
            ];

        foreach (var (email, name, role) in users)
        {
            var id = await In(run.Co, async sp => Must(await sp.GetRequiredService<IUserAssignmentService>().CreateUserAsync(
                new CreateUserRequest(email, name, Password,
                    new GrantAssignmentRequest(role, "COMPANY", run.Co.Id, null, null, null, "Jeu de recette Phase 55"))), $"utilisateur {email}"));
            if (email.StartsWith("ancien.", StringComparison.Ordinal))
                await In(run.Co, async sp => Must(await sp.GetRequiredService<IUserAssignmentService>()
                    .SetStatusAsync(id, new SetUserStatusRequest("suspended")), "suspension"));
        }
        Step($"utilisateurs : {users.Length} (mot de passe commun : {Password})");
    }

    // ------------------------------------------------------------------ catalogue

    private async Task CatalogAsync(CompanyRun run)
    {
        var co = run.Co;
        var specs = co == Co.Pharmacy ? Catalog.Pharmacy : Catalog.Supermarket;
        var categoryNames = co == Co.Pharmacy ? Catalog.PharmaCategories : Catalog.SuperCategories;

        if (co == Co.Pharmacy)
        {
            run.Categories["Antalgiques"] = Co.G("0000-00000000a031");
            run.Categories["Antibiotiques"] = Co.G("0000-00000000a032");
        }
        var n = 0;
        foreach (var name in categoryNames)
            run.Categories[name] = await In(co, async sp => Must(await sp.GetRequiredService<CatalogService>()
                .CreateCategoryAsync(new CreateCategoryRequest(name, $"{co.Prefix}-C{++n:D2}", null)), $"catégorie {name}"));

        var kgUnit = Co.G("0003-000000000012");
        var index = 0;
        foreach (var spec in specs)
        {
            index++;
            var barcode = $"20{(co == Co.Pharmacy ? 1 : 3)}{index:D10}";
            var id = await In(co, async sp => Must(await sp.GetRequiredService<CatalogService>().CreateProductAsync(new CreateProductCommand(
                spec.Sku, spec.Name, null, spec.Kg ? kgUnit : co.DefaultUnit, "stockable", spec.Kg ? null : barcode,
                run.Categories[spec.Category], null, spec.Vat ? co.TaxVat : co.TaxExempt, spec.Lots, spec.Kg,
                ShortName: spec.Name.Length > 22 ? spec.Name[..22] : null, PluCode: spec.Plu)), $"produit {spec.Sku}"));
            run.Products.Add((spec, id));
            run.OnHand[id] = 0m;

            await In(co, async sp =>
            {
                var catalog = sp.GetRequiredService<CatalogService>();
                Must(await catalog.SetPriceAsync(new SetProductPriceCommand(id, "sale", spec.Price, "HTG", 0m, null,
                    _today.AddDays(-HistoryDays - 5), MinPrice: spec.MinPrice)), $"prix {spec.Sku}");
                if (spec.UsdPrice is { } usd)
                    Must(await catalog.SetPriceAsync(new SetProductPriceCommand(id, "sale", usd, "USD", 0m, null, _today.AddDays(-HistoryDays - 5))), $"prix USD {spec.Sku}");
                if (spec.Profile != Profile.NoSupplier)
                    Must(await catalog.AddSupplierAsync(new AddProductSupplierCommand(id, run.Suppliers[spec.Supplier], $"{spec.Sku}-F",
                        spec.Cost, "HTG", spec.LeadTimeDays, true)), $"fournisseur de {spec.Sku}");
                if (spec.DailyDemand > 0 && spec.Profile is not (Profile.Overstock or Profile.Dormant))
                {
                    var point = Math.Ceiling((decimal)spec.DailyDemand * (spec.LeadTimeDays + 3));
                    Must(await catalog.SetReorderRuleAsync(new SetReorderRuleCommand(id, co.Location, point / 2, point * 4, point,
                        Math.Ceiling((decimal)spec.DailyDemand * 30), spec.LeadTimeDays, false)), $"règle de réassort {spec.Sku}");
                }
            });
        }

        // Palier de quantité (§13 prix) : l'eau par pack de 12
        if (co == Co.Supermarket)
        {
            var eau = run.Products.First(p => p.Spec.Sku == "SM-EAU15").Id;
            await In(co, async sp => Must(await sp.GetRequiredService<CatalogService>().SetPriceAsync(
                new SetProductPriceCommand(eau, "sale", 65m, "HTG", 12m, null, _today.AddDays(-HistoryDays - 5))), "palier eau"));
        }

        // Les deux produits de la démo pharmacie entrent aussi dans l'activité.
        if (co == Co.Pharmacy)
        {
            run.Products.Add((new ProductSpec("PARA500", "Paracétamol 500 mg — 20 cp", "Antalgiques", 75m, 45m, 4.0, Profile.FastMover, Lots: true), Co.G("0000-00000000a041")));
            run.Products.Add((new ProductSpec("AMOX1G", "Amoxicilline 1 g — 12 cp", "Antibiotiques", 250m, 160m, 1.0, Profile.Prescription, Lots: true, Supplier: 1), Co.G("0000-00000000a042")));
            run.OnHand[Co.G("0000-00000000a041")] = 0m;
            run.OnHand[Co.G("0000-00000000a042")] = 0m;
            // Les prix de la démo datent du chargement : on les fait couvrir tout l'historique.
            await SqlAsync("""
                UPDATE nexora.product_price SET valid_from = @f
                WHERE product_id IN ('dce00000-0000-0000-0000-00000000a041', 'dce00000-0000-0000-0000-00000000a042')
                """, ("f", _today.AddDays(-HistoryDays - 5)));
            // Ils n'ont pas de fournisseur référencé dans la démo.
            await In(co, async sp =>
            {
                var catalog = sp.GetRequiredService<CatalogService>();
                Must(await catalog.AddSupplierAsync(new AddProductSupplierCommand(Co.G("0000-00000000a041"), run.Suppliers[0], "PARA500-F", 45m, "HTG", 5, true)), "fournisseur PARA500");
                Must(await catalog.AddSupplierAsync(new AddProductSupplierCommand(Co.G("0000-00000000a042"), run.Suppliers[1], "AMOX1G-F", 160m, "HTG", 5, true)), "fournisseur AMOX1G");
            });
        }

        // Promotions (aucune API de création : référentiel posé en SQL, lu par le moteur de prix réel).
        if (co == Co.Supermarket)
        {
            await SqlAsync("""
                WITH p AS (INSERT INTO nexora.promotion (company_id, name, promo_type, value, priority, starts_at, ends_at, status)
                           VALUES (@c, 'Semaine des boissons −10 %', 'percentage', 10, 5, @s, @e, 'active') RETURNING id)
                INSERT INTO nexora.promotion_target (promotion_id, company_id, target_type, category_id) SELECT id, @c, 'category', @cat FROM p
                """, ("c", co.Id), ("s", _today.AddDays(-12)), ("e", _today.AddDays(9)), ("cat", run.Categories["Boissons"]));
            await SqlAsync("""
                WITH p AS (INSERT INTO nexora.promotion (company_id, name, promo_type, value, priority, starts_at, ends_at, status)
                           VALUES (@c, 'Opération riz (terminée)', 'amount', 25, 3, @s, @e, 'expired') RETURNING id)
                INSERT INTO nexora.promotion_target (promotion_id, company_id, target_type, product_id) SELECT id, @c, 'product', @p FROM p
                """, ("c", co.Id), ("s", _today.AddDays(-60)), ("e", _today.AddDays(-45)), ("p", run.Products.First(p => p.Spec.Sku == "SM-RIZ1").Id));
        }
        Step($"catalogue : {run.Products.Count} produits, {run.Categories.Count} catégories, prix, fournisseurs, règles de réassort");
    }

    // ------------------------------------------------------------------ historique daté

    private decimal InitialQuantity(ProductSpec s) => s.Profile switch
    {
        // Les tickets consomment ~1,25 × la demande nominale (lignes multiples, quantités 1 à 3).
        Profile.LowStock => Math.Ceiling((decimal)s.DailyDemand * HistoryDays * 1.2m),
        Profile.Overstock => s.Price > 1000m ? 300m : 150m,
        Profile.Dormant => 40m,
        Profile.ExpiredLot => 30m,
        Profile.NearExpiry => Math.Ceiling((decimal)s.DailyDemand * HistoryDays * 1.8m) + 40m,
        _ => Math.Ceiling((decimal)s.DailyDemand * 50m) + 10m,
    };

    private static bool Replenished(ProductSpec s) =>
        s.Profile is Profile.Regular or Profile.FastMover or Profile.PriceIncrease or Profile.Weighed or Profile.NoSupplier or Profile.Floor or Profile.Prescription;

    private async Task HistoryAsync(CompanyRun run)
    {
        var co = run.Co;
        await PurchaseRoundAsync(run, -HistoryDays, p => InitialQuantity(p.Spec), payNow: true, holdLastSupplier: true);
        Step("achats initiaux (J-100) : commandes → réception datée → facture → rapprochement → paiement (dernier fournisseur laissé impayé)");

        for (var d = -HistoryDays + 1; d <= -1; d++)
        {
            if (d == -55) await PurchaseRoundAsync(run, d, p => Replenished(p.Spec) ? Math.Ceiling((decimal)p.Spec.DailyDemand * 45m) : 0m, payNow: true, holdLastSupplier: false);
            // Commandes intermédiaires des seuls produits « hausse fournisseur » : la règle exige au moins
            // deux commandes avant et deux après la hausse (+25 % à partir de J-30).
            if (d is -75 or -30) await PurchaseRoundAsync(run, d, p => p.Spec.Profile == Profile.PriceIncrease ? Math.Ceiling((decimal)p.Spec.DailyDemand * 20m) : 0m, payNow: true, holdLastSupplier: false);
            if (d == -15) await PurchaseRoundAsync(run, d, p => Replenished(p.Spec) ? Math.Ceiling((decimal)p.Spec.DailyDemand * 40m) : 0m, payNow: false, holdLastSupplier: false, lateRound: true);

            await DailySalesAsync(run, d);
            await CreditScheduleAsync(run, d);
            await PaymentsDueAsync(run, d);
            await ExpensesAsync(run, d);
            if (d == -40) await LossAsync(run, d);
        }
        Step($"historique : 99 jours de ventes datées ({(co == Co.Supermarket ? "tendance +20 % puis +60 % sur 14 jours" : "tendance −30 % puis −55 % sur 14 jours")}), crédits, règlements, dépenses, casse");
    }

    private async Task PurchaseRoundAsync(CompanyRun run, int day, Func<(ProductSpec Spec, Guid Id), decimal> quantity,
        bool payNow, bool holdLastSupplier, bool lateRound = false)
    {
        var co = run.Co;
        var date = Day(day, 8);
        var bySupplier = run.Products.Where(p => quantity(p) > 0m).GroupBy(p => p.Spec.Profile == Profile.NoSupplier ? 0 : p.Spec.Supplier);
        foreach (var group in bySupplier)
        {
            var supplierIndex = group.Key;
            var supplier = run.Suppliers[supplierIndex];
            // Hausse de prix fournisseur sur le dernier réassort (recommandation « supplier_price_increase »).
            decimal Cost((ProductSpec Spec, Guid Id) p) =>
                day >= -30 && p.Spec.Profile == Profile.PriceIncrease ? Math.Round(p.Spec.Cost * 1.25m, 2) : p.Spec.Cost;

            var lines = group.Select(p => new CreatePurchaseOrderLine(p.Id, quantity(p), Cost(p), null, 0m)).ToList();
            var orderId = await In(co, async sp => Must(await sp.GetRequiredService<PurchasingService>().CreateOrderAsync(
                new CreatePurchaseOrderCommand(supplier, null, "HTG", DateOnly.FromDateTime(date.AddDays(3).DateTime), lines, $"Recette J{day}")), "commande"));
            var poLines = await In(co, async sp =>
            {
                var po = sp.GetRequiredService<PurchasingService>();
                Must(await po.ApproveOrderAsync(orderId), "approbation commande");
                Must(await po.SendOrderAsync(orderId), "envoi commande");
                return await sp.GetRequiredService<NexoraDbContext>().Set<PurchaseOrderLine>().AsNoTracking()
                    .Where(l => l.PurchaseOrderId == orderId).ToDictionaryAsync(l => l.ProductId, l => l.Id);
            });
            // Seule date non antidatable par le service : la date de commande (utilisée par la règle
            // « hausse de prix fournisseur » et l'historique d'achats) est ramenée au jour simulé.
            await SqlAsync("UPDATE nexora.purchase_order SET order_date = @d WHERE id = @id",
                ("d", DateOnly.FromDateTime(date.DateTime)), ("id", orderId));

            // Réception partielle volontaire sur le dernier réassort du 1er fournisseur (ACH-004).
            var partial = lateRound && supplierIndex == 0;
            var receiptLines = group.Select(p =>
            {
                var q = quantity(p);
                if (partial) q = Math.Max(1m, Math.Floor(q * 0.6m));
                var (lot, exp) = LotFor(p.Spec, day);
                return new ReceiveLine(p.Id, poLines[p.Id], q, Cost(p), lot, exp);
            }).ToList();
            await In(co, async sp => Must(await sp.GetRequiredService<PurchasingService>().ReceiveAsync(
                new ReceiveGoodsCommand(Guid.NewGuid(), supplier, co.Location, orderId, receiptLines, date, null)), "réception"));
            foreach (var l in receiptLines) run.OnHand[l.ProductId] += l.Quantity;
            run.Receipts++;

            // Facture à crédit. Écart de prix volontaire (+8 %) sur le 2ᵉ fournisseur du dernier réassort
            // → paiement bloqué par le rapprochement 3-way (ACH-006 / ACH-008).
            var priceVariance = lateRound && supplierIndex == 1;
            var invoiceLines = receiptLines.Select(l => new InvoiceLine(l.ProductId, run.Products.First(p => p.Id == l.ProductId).Spec.Name,
                l.Quantity, priceVariance ? Math.Round(l.UnitCost * 1.08m, 2) : l.UnitCost, null, 0m, 0m)).ToList();
            var invoiceDate = DateOnly.FromDateTime(date.DateTime);
            var invoice = await In(co, async sp => Must(await sp.GetRequiredService<PurchasingService>().RecordInvoiceAsync(
                new RecordPurchaseInvoiceCommand(Guid.NewGuid(), supplier, orderId, "HTG", invoiceDate, invoiceDate.AddDays(30),
                    OnCredit: true, null, invoiceLines, true, SupplierReference: $"{co.Prefix}-F{supplierIndex}-{-day:D3}")), "facture fournisseur"));
            run.Invoices++;
            await In(co, async sp => Must(await sp.GetRequiredService<PurchasingService>().MatchInvoiceAsync(invoice.InvoiceId,
                new MatchInvoiceRequest(2m, 5m)), "rapprochement"));

            var hold = holdLastSupplier && supplierIndex == run.Suppliers.Count - 1;
            if (payNow && !hold && invoice.PayableId is { } payable)
            {
                var total = invoiceLines.Sum(l => l.Quantity * l.UnitPrice);
                await In(co, async sp => Must(await sp.GetRequiredService<PurchasingService>().PayPayableAsync(payable,
                    new PayPayableRequest(Guid.NewGuid(), Math.Round(total, 2), co == Co.Pharmacy ? co.Cash : co.Bank, $"VIR-{-day}-{supplierIndex}")),
                    "paiement fournisseur"));
            }
        }
    }

    private (string? Lot, DateOnly? Expiration) LotFor(ProductSpec s, int day)
    {
        if (!s.Lots) return (null, null);
        var today = DateOnly.FromDateTime(_today.DateTime);
        var exp = s.Profile switch
        {
            Profile.NearExpiry => today.AddDays(20),     // alerte péremption
            Profile.ExpiredLot => today.AddDays(-5),     // lot déjà expiré, encore en stock
            _ => today.AddDays(200 + _rng.Next(0, 300)),
        };
        return ($"L{-day:D3}-{s.Sku.Split('-')[^1]}", exp);
    }

    // ------------------------------------------------------------------ ventes

    // Tendance récente en deux marches, pour qu'elle se voie à 7 jours comme à 14 jours.
    private double Trend(CompanyRun run, int day) => (run.Co == Co.Supermarket, day) switch
    {
        (true, > -8) => 1.6,
        (true, > -15) => 1.2,
        (false, > -8) => 0.45,
        (false, > -15) => 0.7,
        _ => 1.0,
    };

    private async Task DailySalesAsync(CompanyRun run, int day)
    {
        var sellable = run.Products.Where(p => p.Spec.DailyDemand > 0).ToList();
        var totalDemand = sellable.Sum(p => p.Spec.DailyDemand) * Trend(run, day);
        var tickets = Math.Max(1, (int)Math.Round(totalDemand / 4.0 + _rng.NextDouble() * 3 - 1.5));

        for (var t = 0; t < tickets; t++)
        {
            var lines = new List<(ProductSpec Spec, Guid Id, decimal Qty, decimal Discount)>();
            var lineCount = _rng.Next(1, 5);
            for (var i = 0; i < lineCount; i++)
            {
                var p = Pick(sellable);
                if (lines.Any(l => l.Id == p.Id)) continue;
                var qty = p.Spec.Kg ? Math.Round((decimal)(0.3 + _rng.NextDouble() * 2.2), 3) : _rng.Next(1, 4);
                if (run.OnHand[p.Id] < qty) continue;
                var discount = p.Spec.MinPrice is null && _rng.NextDouble() < 0.05 ?Math.Round(p.Spec.Price * qty * 0.05m, 2) : 0m;
                lines.Add((p.Spec, p.Id, qty, discount));
            }
            if (lines.Count == 0) continue;

            // Clients rattachés : habitué comptant, et client « inactif » seulement en début d'historique.
            Guid? customer = null;
            if (day <= -70 && _rng.NextDouble() < 0.15) customer = run.Customers["inactif"];
            else if (_rng.NextDouble() < 0.15) customer = run.Customers["comptant"];

            await SellAsync(run, day, lines, customer, credit: false);
        }

        // Ventes en dollars (FIN-010) — pharmacie, compte bancaire USD.
        if (run.Co == Co.Pharmacy && day is -30 or -12)
        {
            var vitc = run.Products.First(p => p.Spec.Sku == "PH-VITC");
            if (run.OnHand[vitc.Id] >= 2)
                await SellAsync(run, day, [(vitc.Spec, vitc.Id, 2m, 0m)], null, credit: false, currency: "USD");
        }

        // Très grand panier (§29) : un ticket de 25 lignes au supermarché.
        if (run.Co == Co.Supermarket && day == -3)
        {
            var big = run.Products.Where(p => p.Spec.DailyDemand > 0 && run.OnHand[p.Id] >= 1m).Take(25)
                .Select(p => (p.Spec, p.Id, p.Spec.Kg ? 0.5m : 1m, 0m)).ToList();
            await SellAsync(run, day, big, run.Customers["comptant"], credit: false);
        }
    }

    private (ProductSpec Spec, Guid Id) Pick(List<(ProductSpec Spec, Guid Id)> items)
    {
        var total = items.Sum(i => i.Spec.DailyDemand);
        var r = _rng.NextDouble() * total;
        foreach (var i in items)
        {
            r -= i.Spec.DailyDemand;
            if (r <= 0) return i;
        }
        return items[^1];
    }

    /// <summary>Encaisse une vente par le service réel. Les espèces sont toujours le dernier moyen
    /// (le rendu monnaie s'y impute) ; carte et mobile money sont payés au centime près grâce à
    /// l'aperçu de tarification du serveur.</summary>
    private async Task<CheckoutResult> SellAsync(CompanyRun run, int day, List<(ProductSpec Spec, Guid Id, decimal Qty, decimal Discount)> lines,
        Guid? customer, bool credit, decimal deposit = 0m, string currency = "HTG", Guid? session = null, int? dueInDays = null, DateTimeOffset? at = null)
    {
        var co = run.Co;
        var soldAt = at ?? Day(day, 9 + _rng.Next(0, 10));
        var checkoutLines = lines.Select(l => new CheckoutLine(l.Id, l.Qty, null, l.Discount, null)).ToList();

        var total = await In(co, async sp =>
        {
            var priced = await sp.GetRequiredService<ISalesPricing>().PriceCartAsync(co.Location, customer, currency, soldAt, checkoutLines, null);
            decimal sub = 0, disc = 0, tax = 0;
            foreach (var p in priced)
            {
                var gross = Math.Round(p.UnitPrice * p.Quantity, 2, MidpointRounding.ToEven);
                var d = Math.Round(p.ManualDiscount + p.PromotionDiscount, 2, MidpointRounding.ToEven);
                sub += gross; disc += d;
                tax += Math.Round((gross - d) * p.TaxRate / 100m, 2, MidpointRounding.ToEven);
            }
            return Math.Round(sub - disc + tax, 2, MidpointRounding.ToEven);
        });

        List<TenderInput> tenders;
        if (credit)
            tenders = deposit > 0 ? [new TenderInput(co.Cash, co.CashMethod, deposit, "Acompte")] : [];
        else if (session is not null)   // vente de session : espèces, pour un écart de caisse maîtrisé
            tenders = [new TenderInput(co.Cash, co.CashMethod, Math.Ceiling(total / 50) * 50, null)];
        else if (currency == "USD")
            tenders = [new TenderInput(co.Bank, null, total, "USD")];
        else
        {
            var roll = _rng.NextDouble();
            tenders = roll switch
            {
                < 0.15 => [new TenderInput(co.Bank, co.CardMethod, total, $"CB-{_rng.Next(1000, 9999)}")],
                < 0.25 => [new TenderInput(co.Momo, null, total, $"MC-{_rng.Next(100000, 999999)}")],
                < 0.30 when total > 200 => [new TenderInput(co.Bank, co.CardMethod, Math.Round(total / 2, 2), "CB-mixte"),
                                             new TenderInput(co.Cash, co.CashMethod, Math.Ceiling(total / 2 / 50) * 50, null)],
                _ => [new TenderInput(co.Cash, co.CashMethod, Math.Ceiling(total / 50) * 50, null)],
            };
        }
        var tendered = tenders.Sum(t => t.Amount);
        DateOnly? due = credit ? DateOnly.FromDateTime(soldAt.AddDays(dueInDays ?? 30).DateTime) : null;

        var result = await In(co, async sp => Must(await sp.GetRequiredService<SalesService>().CheckoutAsync(new CheckoutCommand(
            Guid.NewGuid(), co.Location, null, null, session, customer, currency, checkoutLines, tenders, tendered, due, null, null, soldAt)),
            $"vente J{day}"));
        foreach (var l in lines) run.OnHand[l.Id] -= l.Qty;
        run.Sales++;
        if (credit) run.CreditSales++;
        if (day >= -2) run.RecentSales.Add(result.SaleId);
        return result;
    }

    // ------------------------------------------------------------------ crédit clients

    private async Task CreditScheduleAsync(CompanyRun run, int day)
    {
        var super = run.Co == Co.Supermarket;
        (string Customer, bool Today, int Units, int PayAfter, decimal PayShare)[] plan =
        [
            // bon payeur : achat hebdomadaire, réglé en totalité 20 jours plus tard (CLI-004/005)
            ("bon_payeur", day % 7 == 0, super ? 18 : 12, 20, 1m),
            // retard : trois achats anciens jamais réglés → créances échues (CLI-006, relance)
            ("retard", day is -80 or -66 or -52, super ? 30 : 20, 0, 0m),
            // proche de la limite : achats répétés sans règlement (CLI-007/008)
            //   (pharmacie : hors des 14 derniers jours, pour ne pas masquer la baisse des ventes)
            ("limite", super ? day is -40 or -25 or -10 : day is -60 or -45 or -35, 0, 0, 0m),
        ];
        if (super)
            plan = [.. plan,
                ("bloque", day == -75, 20, 0, 0m),
                // gros compte : achat très élevé puis règlement partiel (§29, CLI-004)
                ("gros_compte", day == -20, 0, 15, 0.4m)];
        else
            plan = [.. plan, ("bon_payeur", day == -33, 25, 25, 0.5m)];   // Clinique : règlement partiel

        foreach (var (key, today, units, payAfter, share) in plan)
        {
            if (!today) continue;
            var customer = run.Customers[key];
            // « limite » : trois achats de ~27 % du plafond → encours ≈ 85 % de la limite de crédit.
            var lines = key switch
            {
                "gros_compte" => BuildBasketWorth(run, 55_000m),
                "limite" => BuildBasketWorth(run, (super ? 60_000m : 40_000m) * 0.27m),
                _ => BuildBasket(run, units),
            };
            if (lines.Count == 0) continue;
            var sale = await SellAsync(run, day, lines, customer, credit: true, deposit: key == "gros_compte" ? 10_000m : 0m,
                dueInDays: key == "retard" ? 21 : 30);
            if (share > 0 && sale.ReceivableId is { } receivable && day + payAfter <= -1)
                run.Payments.Add(new PlannedPayment(day + payAfter, customer, receivable, Math.Round(sale.CreditAmount * share, 2)));
        }
    }

    private List<(ProductSpec Spec, Guid Id, decimal Qty, decimal Discount)> BuildBasket(CompanyRun run, int units)
    {
        var lines = new List<(ProductSpec, Guid, decimal, decimal)>();
        foreach (var p in run.Products.Where(p => p.Spec.DailyDemand >= 1.0 && !p.Spec.Kg).OrderBy(_ => _rng.Next()).Take(4))
        {
            var q = Math.Max(1, units / 4);
            if (run.OnHand[p.Id] >= q) lines.Add((p.Spec, p.Id, q, 0m));
        }
        return lines;
    }

    /// <summary>Panier d'une valeur HT visée, puisé dans les articles les plus chers en stock
    /// (en laissant quelques unités en rayon).</summary>
    private List<(ProductSpec Spec, Guid Id, decimal Qty, decimal Discount)> BuildBasketWorth(CompanyRun run, decimal target)
    {
        var lines = new List<(ProductSpec, Guid, decimal, decimal)>();
        var remaining = target;
        foreach (var p in run.Products.Where(p => p.Spec.DailyDemand >= 0.5 && !p.Spec.Kg).OrderByDescending(p => p.Spec.Price))
        {
            if (remaining <= 0) break;
            var qty = Math.Min(Math.Floor(run.OnHand[p.Id] / 2), Math.Ceiling(remaining / p.Spec.Price));
            if (qty < 1) continue;
            lines.Add((p.Spec, p.Id, qty, 0m));
            remaining -= qty * p.Spec.Price;
        }
        return lines;
    }

    private async Task PaymentsDueAsync(CompanyRun run, int day)
    {
        foreach (var pay in run.Payments.Where(p => p.Day == day).ToList())
        {
            await In(run.Co, async sp => Must(await sp.GetRequiredService<IFinanceOperations>().RecordCustomerPaymentAsync(new RecordPaymentRequest(
                Guid.NewGuid(), Day(day, 15), pay.CustomerId, pay.Amount, "HTG", run.Co.Cash,
                [new AllocationInput(pay.ReceivableId, pay.Amount, true)], $"REG-{-day}")), $"règlement client J{day}"));
        }
    }

    // ------------------------------------------------------------------ dépenses, pertes

    private async Task ExpensesAsync(CompanyRun run, int day)
    {
        var super = run.Co == Co.Supermarket;
        var account = super ? run.Co.Bank : run.Co.Cash;   // le compte bancaire démo de la pharmacie est en USD
        var list = new List<decimal>();
        if (day is -90 or -60 or -30) list.Add(super ? 45_000m : 25_000m);              // loyer
        if (day % 7 == 0) list.Add((super ? 3_500m : 1_800m) + _rng.Next(0, 400));     // électricité
        if (day == -3) list.Add(super ? 38_000m : 18_000m);                              // dépense inhabituelle (ANL-011, INT-007)
        // Même chemin que POST /finance/expenses : pièce de dépense + écriture (BUG-016 corrigé).
        foreach (var amount in list)
        {
            await In(run.Co, async sp => Must(await sp.GetRequiredService<IFinanceOperations>().RecordExpenseAsync(new RecordExpenseRequest(
                Guid.NewGuid(), Guid.NewGuid(), Day(day, 16), amount, 0m, "HTG", account, "6800")), $"dépense J{day}"));
            run.Expenses++;
        }
    }

    private async Task LossAsync(CompanyRun run, int day)
    {
        var p = run.Products.Where(x => !x.Spec.Lots && !x.Spec.Kg && run.OnHand[x.Id] > 10).OrderByDescending(x => x.Spec.DailyDemand).First();
        await In(run.Co, async sp => Must(await sp.GetRequiredService<InventoryOperationsService>().RecordLossAsync(new RecordLossCommand(
            Guid.NewGuid(), run.Co.Location, "breakage", "Casse en rayon (recette)", [new LossLineInput(p.Id, 3m, null)], Day(day, 18))), "casse"));
        run.OnHand[p.Id] -= 3m;
    }

    // ------------------------------------------------------------------ aujourd'hui

    private async Task TodayAsync(CompanyRun run)
    {
        var co = run.Co;
        var super = co == Co.Supermarket;

        // Session de caisse du jour (POS-001/017/018) : fermée avec écart au supermarché, laissée
        // ouverte à la pharmacie (contrôle « session ouverte » de la clôture financière).
        var session = await In(co, async sp => Must(await sp.GetRequiredService<CashSessionService>()
            .OpenAsync(new OpenSessionCommand(co.Register, null, 5_000m)), "ouverture de caisse"));
        decimal cash = 0m;
        for (var i = 0; i < 6; i++)
        {
            var p = run.Products.Where(x => x.Spec.DailyDemand >= 1 && !x.Spec.Kg && run.OnHand[x.Id] >= 2).OrderBy(_ => _rng.Next()).First();
            var r = await SellCashInSessionAsync(run, p, session);
            cash += r;
        }
        if (super)
        {
            var close = await In(co, async sp => Must(await sp.GetRequiredService<CashSessionService>()
                .CloseAsync(new CloseSessionCommand(session, 5_000m + cash - 150m, "Écart de caisse −150 (recette)")), "clôture de caisse"));
            Step($"caisse du jour : 6 ventes, clôture attendue {close.ExpectedBalance:0.##}, écart {close.Difference:0.##}");
        }
        else Step("caisse du jour : 6 ventes, session laissée OUVERTE");

        // Retours (POS-015/016) sur des ventes récentes. Une vente validée ne s'annule pas (§24 :
        // correction par retour total) ; seule une vente en attente s'annule (POS-012/014).
        if (run.RecentSales.Count >= 2)
        {
            // Retour partiel sur une vente d'au moins 2 unités, sinon il serait de fait total.
            var sizes = await In(co, async sp => await sp.GetRequiredService<NexoraDbContext>().SaleLines.AsNoTracking()
                .Where(l => run.RecentSales.Contains(l.SaleId)).GroupBy(l => l.SaleId)
                .Select(g => new { SaleId = g.Key, Units = g.Sum(l => l.Quantity) }).ToListAsync());
            var partialSale = sizes.Where(x => x.Units >= 2).Select(x => x.SaleId).FirstOrDefault(run.RecentSales[0]);
            await ReturnAsync(run, partialSale, partial: true);
            await ReturnAsync(run, run.RecentSales.First(x => x != partialSale), partial: false);
        }
        var cart = run.Products.Where(x => x.Spec.DailyDemand >= 1 && !x.Spec.Kg).Take(2)
            .Select(x => new CheckoutLine(x.Id, 1m, null, 0m, null)).ToList();
        var parked = await In(co, async sp => Must(await sp.GetRequiredService<SalesService>().ParkAsync(
            new ParkCartCommand(co.Location, null, "HTG", "Panier abandonné (recette)", cart)), "mise en attente"));
        await In(co, async sp => Must(await sp.GetRequiredService<SalesService>().VoidAsync(parked, "Client parti sans payer (recette)"), "annulation de vente en attente"));
        await In(co, async sp => Must(await sp.GetRequiredService<SalesService>().ParkAsync(
            new ParkCartCommand(co.Location, run.Customers["comptant"], "HTG", "Client revient après le distributeur (recette)", cart)), "mise en attente"));
        Step("retour partiel, retour total, panier en attente annulé, panier en attente conservé");

        // Inventaire avec écarts (STOCK-007/008).
        var counted = run.Products.Where(x => !x.Spec.Lots && !x.Spec.Kg && run.OnHand[x.Id] > 5).Take(2).ToList();
        var countId = await In(co, async sp => Must(await sp.GetRequiredService<InventoryOperationsService>()
            .StartCountAsync(co.Location, "partial", counted.Select(c => c.Id).ToList()), "début d'inventaire"));
        await In(co, async sp => Must(await sp.GetRequiredService<InventoryOperationsService>().ValidateCountAsync(countId,
            [new CountLineInput(counted[0].Id, null, run.OnHand[counted[0].Id] - 2m), new CountLineInput(counted[1].Id, null, run.OnHand[counted[1].Id] + 1m)],
            Guid.NewGuid()), "validation d'inventaire"));
        Step($"inventaire : {counted[0].Spec.Sku} −2, {counted[1].Spec.Sku} +1");

        // Devis envoyé et commande réservée (ventes B2B).
        var b2b = run.Customers[super ? "gros_compte" : "limite"];
        var quoteLines = run.Products.Where(x => x.Spec.DailyDemand >= 1 && !x.Spec.Kg).Take(3)
            .Select(x => new QuoteLineInput(x.Id, 10m, null, 0m, null)).ToList();
        var quote = await In(co, async sp => Must(await sp.GetRequiredService<QuoteService>().CreateAsync(new CreateQuoteRequest(
            b2b, "HTG", co.Location, DateOnly.FromDateTime(_today.AddDays(15).DateTime), "Paiement à 30 jours", "Devis de recette", quoteLines)), "devis"));
        await In(co, async sp => Must(await sp.GetRequiredService<QuoteService>().SendAsync(quote), "envoi du devis"));

        var orderLines = run.Products.Where(x => x.Spec.DailyDemand >= 1 && !x.Spec.Kg && run.OnHand[x.Id] >= 5).Take(2)
            .Select(x => new CreateOrderLineInput(x.Id, 3m, null, 0m, null)).ToList();
        var order = await In(co, async sp => Must(await sp.GetRequiredService<OrderService>().CreateOrderAsync(new CreateOrderRequest(
            run.Customers["comptant"], "HTG", SalesOrderType.Pickup, co.Location, false, _today.AddDays(2), null, null, "Commande de recette", orderLines)), "commande client"));
        await In(co, async sp =>
        {
            var orders = sp.GetRequiredService<OrderService>();
            Must(await orders.ConfirmOrderAsync(order), "confirmation commande");
            Must(await orders.ReserveOrderAsync(order), "réservation commande");
        });
        Step("devis envoyé, commande confirmée et réservée");

        // Blocage d'un client (CLI-008) et objectif de CA (REP).
        if (super)
            await In(co, async sp => Must(await sp.GetRequiredService<CollectionsService>().BlockCustomerAsync(
                new BlockCustomerRequest(run.Customers["bloque"], "credit", "Impayés de plus de 60 jours (recette)")), "blocage client"));
        await In(co, async sp => Must(await sp.GetRequiredService<IObjectiveService>().SetAsync(
            new SetObjectiveRequest(null, null, "revenue", "month", super ? 900_000m : 250_000m)), "objectif de CA"));
    }

    private async Task<decimal> SellCashInSessionAsync(CompanyRun run, (ProductSpec Spec, Guid Id) p, Guid session)
    {
        var r = await SellAsync(run, 0, [(p.Spec, p.Id, 1m, 0m)], null, credit: false, session: session, at: DateTimeOffset.UtcNow);
        return r.Total;
    }

    private async Task ReturnAsync(CompanyRun run, Guid saleId, bool partial)
    {
        var lines = await In(run.Co, async sp => await sp.GetRequiredService<NexoraDbContext>().SaleLines.AsNoTracking()
            .Where(l => l.SaleId == saleId).Select(l => new { l.Id, l.Quantity }).ToListAsync());
        var input = partial
            ? [new ReturnLineInput(lines[0].Id, lines[0].Quantity > 1 ? Math.Floor(lines[0].Quantity / 2) : lines[0].Quantity, true)]
            : lines.Select(l => new ReturnLineInput(l.Id, l.Quantity, true)).ToList();
        await In(run.Co, async sp => Must(await sp.GetRequiredService<SalesReturnService>().ReturnAsync(new ReturnCommand(
            Guid.NewGuid(), saleId, run.Co.Location, null, partial ? "Article défectueux (recette)" : "Client insatisfait — retour total (recette)",
            "cash", run.Co.Cash, input)), partial ? "retour partiel" : "retour total"));
    }

    // ------------------------------------------------------------------ analytique

    private async Task AnalyticsAsync(CompanyRun run)
    {
        var co = run.Co;
        var recommendations = await In(co, async sp => await sp.GetRequiredService<IRecommendationService>().GenerateAsync());
        var insights = await In(co, async sp => await sp.GetRequiredService<IInsightService>().ScanAsync());
        var scores = await In(co, async sp => await sp.GetRequiredService<IScoringService>().ComputeAsync());
        await In(co, async sp =>
        {
            var f = sp.GetRequiredService<IForecastService>();
            Must(await f.GenerateAsync(new GenerateForecastRequest("revenue", 7)), "prévision 7 j");
            Must(await f.GenerateAsync(new GenerateForecastRequest("revenue", 30)), "prévision 30 j");
            Must(await f.GenerateAsync(new GenerateForecastRequest("cash", 30)), "prévision trésorerie");
        });
        Step($"analytique : {recommendations} recommandations, {insights} anomalies, {scores} scores, 3 prévisions");
    }
}
