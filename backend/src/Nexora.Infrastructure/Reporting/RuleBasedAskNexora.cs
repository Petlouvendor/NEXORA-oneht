using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Nexora.Application.Abstractions;
using Nexora.Application.Customers;
using Nexora.Application.Inventory;
using Nexora.Application.Payroll;
using Nexora.Application.Reporting;
using Nexora.Application.Security;
using Nexora.Contracts.Reporting;
using Nexora.Infrastructure.Persistence;
using Nexora.Domain.Common;
using Nexora.Domain.Accounting;

namespace Nexora.Infrastructure.Reporting;

/// <summary>
/// Ask Nexora (Phase 7 §33 ; résumé fonctionnel §14) — appariement d'intentions déterministe, sans LLM.
/// L'assistant ne répond qu'avec les données auxquelles l'utilisateur a droit, ne calcule rien
/// lui-même hors des services métier (KPI, balance âgée, réapprovisionnement, grand livre) et dit
/// explicitement quand il n'y a pas de données — il n'invente jamais un chiffre.
/// Questions types : « Pourquoi mes bénéfices ont diminué ? », « Quels produits dois-je commander ? »,
/// « Quel produit risque de tomber en rupture ? », « Quels clients me doivent le plus ? »,
/// « Quelles dépenses ont augmenté ? », « Combien ce client me doit-il aujourd'hui ? » (module clients §27 :
/// le solde vient du compte client calculé par le serveur ; l'assistant ne fait que l'expliquer),
/// « Combien reste-t-il d'avances à récupérer ? », « Quelle est la masse salariale de ce mois ? » (module paie §33 :
/// réservé aux droits paie, chiffres lus dans les avances et les lignes de paie calculées par le serveur).
/// NEXORA ONE §8 (NEXORA AI) : « Quels produits se vendent le mieux ? » (classement + tendance vs période
/// précédente), « Pourquoi mon stock diminue ? » (décomposition des mouvements de stock réels, 30 j vs 30 j),
/// « Quels clients ont des paiements en retard ? » (créances échues nominatives).
/// </summary>
public sealed class RuleBasedAskNexora(
    IKpiService kpi, IAgingReports aging, IReorderService reorder, NexoraDbContext db,
    IPermissionService permissions, ITenantContext tenant, ICustomerAccountQueries customerAccounts,
    IPayrollReports payroll, ISalesReports salesReports) : IAskNexora
{
    private static readonly CultureInfo Fr = CultureInfo.GetCultureInfo("fr-FR");
    private Guid Company => tenant.ActiveCompanyId ?? throw TenantErrors.ActiveCompanyRequired();

    private sealed record ExpenseRow(string Code, string Name, decimal Current, decimal Previous);
    private sealed record CoverRow(string Sku, string Name, decimal Available, decimal Daily, decimal? DaysCover);
    private sealed record MovementRow(string MovementType, decimal Quantity, decimal Value, decimal PreviousQuantity, decimal PreviousValue);
    private sealed record ProductDropRow(string Sku, string Name, decimal NetQuantity, decimal SoldQuantity);

    public async Task<AskResponse> AskAsync(AskRequest request, CancellationToken ct = default)
    {
        var q = (request.Question ?? "").ToLowerInvariant();
        var filter = request.Filter ?? new ReportFilter { Preset = "month" };

        var granted = await permissions.CurrentPermissionsAsync(tenant, ct);
        var canSales = granted.Contains(Permissions.Reporting.ViewSales);
        var canFinancial = granted.Contains(Permissions.Reporting.ViewFinancial);
        var canStock = granted.Contains(Permissions.Inventory.View) || granted.Contains(Permissions.Purchasing.View);

        // Du plus spécifique au plus général.
        var canPayroll = granted.Contains(Permissions.Payroll.Reports) || granted.Contains(Permissions.Payroll.ViewSalaries);
        if (Contains(q, "avance", "acompte", "prêt au personnel", "pret au personnel", "prêts au personnel", "prets au personnel")
            && Contains(q, "reste", "récupérer", "recuperer", "combien", "solde", "encours", "dû", "du personnel"))
            return canPayroll ? await OutstandingAdvancesAsync(ct) : Denied();
        if (Contains(q, "masse salariale", "salaires", "paie du mois", "coût de la paie", "cout de la paie"))
            return canPayroll ? await PayrollMassAsync(q, ct) : Denied();
        if (Contains(q, "bénéfice", "benefice", "profit"))
            return canFinancial ? await ProfitAsync(filter, ct) : Denied();
        if (Contains(q, "dépense", "depense", "charges"))
            return canFinancial ? await ExpensesAsync(filter, ct) : Denied();
        // NEXORA ONE §8 — avant « commander » / « rupture » / « ventes » qui partagent des mots.
        if (Contains(q, "se vendent le mieux", "se vend le mieux", "vendent le plus", "vend le plus", "meilleures ventes",
                     "meilleurs produits", "meilleur produit", "top produit", "plus vendu", "best-seller", "best seller"))
            return canSales ? await TopProductsAsync(filter, ct) : Denied();
        if (Contains(q, "stock") && Contains(q, "diminu", "baisse", "descend", "fond", "réduit", "reduit", "chute"))
            return canStock ? await StockDecreaseAsync(ct) : Denied();
        if (Contains(q, "retard", "en souffrance", "pas payé", "pas paye"))
        {
            var canCredit = canFinancial || granted.Contains(Permissions.Credit.ViewPortfolio);
            if (!canCredit) return Denied();
            return await OverdueAsync(ct);
        }
        if (Contains(q, "commander", "réapprovision", "reapprovision", "acheter", "recommander"))
            return canStock ? await ReorderAsync(ct) : Denied();
        if (Contains(q, "rupture", "stock faible", "manque", "épuis", "epuis"))
            return canStock ? await StockoutRiskAsync(ct) : Denied();
        if (request.CustomerId is not null
            || Contains(q, "ce client", "me doit", "nous doit", "doit-il", "doit il", "doit-elle", "doit elle", "solde du client"))
        {
            var canCredit = canFinancial || granted.Contains(Permissions.Credit.ViewPortfolio);
            if (!canCredit) return Denied();
            return await CustomerBalanceAsync(request.CustomerId, request.Question ?? "", ct);
        }
        if (Contains(q, "doivent", "débiteur", "debiteur", "créance", "creance", "impayé", "impaye", "échu", "echu"))
        {
            var canCredit = canFinancial || granted.Contains(Permissions.Credit.ViewPortfolio);
            if (!canCredit) return Denied();
            return Contains(q, "plus", "top", "principaux", "gros") ? await TopDebtorsAsync(ct) : await OverdueAsync(ct);
        }
        if (Contains(q, "marge", "rentab"))
            return canFinancial ? await MarginAsync(filter, ct) : Denied();
        if (Contains(q, "trésorerie", "tresorerie", "cash", "liquidit"))
            return canFinancial ? await CashAsync(filter, ct) : Denied();
        if (Contains(q, "vendu", "chiffre", "ventes", "ca "))
            return canSales ? await SalesAsync(filter, ct) : Denied();

        return new AskResponse("unknown",
            "Question non reconnue. Essayez : « Pourquoi mes bénéfices ont diminué ? », « Quels produits dois-je commander ? », " +
            "« Quel produit risque de tomber en rupture ? », « Quels clients me doivent le plus ? », « Quelles dépenses ont augmenté ? », " +
            "« Quels produits se vendent le mieux ? », « Pourquoi mon stock diminue ? », « Quels clients ont des paiements en retard ? ».",
            [], [], false);
    }

    // ------------------------------------------------------------------ Bénéfice : causes de l'évolution

    private async Task<AskResponse> ProfitAsync(ReportFilter filter, CancellationToken ct)
    {
        var (from, to, prevFrom, prevTo) = await PeriodsAsync(filter, ct);
        var cur = await kpi.CommandCenterAsync(new ReportFilter { From = from, To = to }, ct);
        var prev = await kpi.CommandCenterAsync(new ReportFilter { From = prevFrom, To = prevTo }, ct);
        var currency = cur.Currency;

        decimal V(CommandCenterResponse cc, string key) => cc.Kpis.FirstOrDefault(k => k.Key == key)?.Value ?? 0m;
        var (p1, p0) = (V(cur, KpiCatalog.Profit), V(prev, KpiCatalog.Profit));
        var (r1, r0) = (V(cur, KpiCatalog.Revenue), V(prev, KpiCatalog.Revenue));
        var (m1, m0) = (V(cur, KpiCatalog.GrossMargin), V(prev, KpiCatalog.GrossMargin));
        var (e1, e0) = (V(cur, KpiCatalog.Expenses), V(prev, KpiCatalog.Expenses));
        var rate0 = r0 == 0 ? 0m : m0 / r0;
        var rate1 = r1 == 0 ? 0m : m1 / r1;

        if (r1 == 0 && r0 == 0 && e1 == 0 && e0 == 0)
            return new AskResponse("profit_change",
                $"Aucune vente ni dépense enregistrée du {D(prevFrom)} au {D(to)} : impossible d'expliquer une évolution du bénéfice.",
                [], ["dashboard/command-center"], false);

        // Décomposition : effet volume (CA au taux de marge précédent) + effet taux (reste de la variation
        // de marge) − variation des dépenses = variation du bénéfice.
        var volume = Math.Round((r1 - r0) * rate0, 2);
        var rateEffect = Math.Round((m1 - m0) - volume, 2);
        var expenseEffect = -(e1 - e0);
        var causes = new List<(decimal Impact, string Text)>
        {
            (volume, $"chiffre d'affaires {(r1 < r0 ? "en baisse" : "en hausse")} de {M(Math.Abs(r1 - r0), currency)} ({M(r0, currency)} → {M(r1, currency)}) : impact {Signed(volume, currency)}"),
            (rateEffect, $"taux de marge passé de {Pct(rate0)} à {Pct(rate1)} : impact {Signed(rateEffect, currency)}"),
            (expenseEffect, $"dépenses {(e1 > e0 ? "en hausse" : "en baisse")} de {M(Math.Abs(e1 - e0), currency)} ({M(e0, currency)} → {M(e1, currency)}) : impact {Signed(expenseEffect, currency)}"),
        }.Where(c => c.Impact != 0m).OrderBy(c => p1 < p0 ? c.Impact : -c.Impact).ToList();

        var head = p1 < p0
            ? $"Le bénéfice a baissé de {M(p0 - p1, currency)} : {M(p0, currency)} du {D(prevFrom)} au {D(prevTo)}, contre {M(p1, currency)} du {D(from)} au {D(to)}."
            : $"Le bénéfice n'a pas diminué : {M(p0, currency)} du {D(prevFrom)} au {D(prevTo)}, {M(p1, currency)} du {D(from)} au {D(to)} ({Signed(p1 - p0, currency)}).";
        var body = causes.Count == 0
            ? " Aucun écart notable de chiffre d'affaires, de marge ou de dépenses."
            : " Principaux facteurs : " + string.Join(" ; ", causes.Select((c, i) => $"{i + 1}) {c.Text}")) + ".";

        return new AskResponse("profit_change", head + body,
        [
            new(KpiCatalog.Profit, "Bénéfice", p1, currency, KpiValue.Actual),
            new(KpiCatalog.Profit + "_previous", "Bénéfice — période précédente", p0, currency, KpiValue.Actual),
            new(KpiCatalog.Revenue, "Chiffre d'affaires", r1, currency, KpiValue.Actual),
            new(KpiCatalog.Revenue + "_previous", "Chiffre d'affaires — période précédente", r0, currency, KpiValue.Actual),
            new(KpiCatalog.MarginRate, "Taux de marge", Math.Round(rate1 * 100m, 1), "%", KpiValue.Actual),
            new(KpiCatalog.MarginRate + "_previous", "Taux de marge — période précédente", Math.Round(rate0 * 100m, 1), "%", KpiValue.Actual),
            new(KpiCatalog.Expenses, "Dépenses", e1, currency, KpiValue.Actual),
            new(KpiCatalog.Expenses + "_previous", "Dépenses — période précédente", e0, currency, KpiValue.Actual),
        ], ["dashboard/command-center", "reports/sales/breakdown/category"], false);
    }

    // ------------------------------------------------------------------ Dépenses en hausse (grand livre)

    private async Task<AskResponse> ExpensesAsync(ReportFilter filter, CancellationToken ct)
    {
        var (from, to, prevFrom, prevTo) = await PeriodsAsync(filter, ct);
        // Charges d'exploitation = comptes de charge hors coût des ventes et achats de marchandises.
        var rows = await db.Database.SqlQuery<ExpenseRow>($"""
            SELECT a.code AS "Code", a.name AS "Name",
                   COALESCE(SUM(CASE WHEN je.entry_date BETWEEN {from} AND {to} THEN l.base_debit - l.base_credit END), 0) AS "Current",
                   COALESCE(SUM(CASE WHEN je.entry_date BETWEEN {prevFrom} AND {prevTo} THEN l.base_debit - l.base_credit END), 0) AS "Previous"
            FROM nexora.journal_entry_line l
            JOIN nexora.journal_entry je ON je.id = l.journal_entry_id AND je.status = ANY({LedgerStatuses.InLedger})
            JOIN nexora.account a ON a.id = l.account_id AND a.account_type = 'expense'
            WHERE l.company_id = {Company} AND je.entry_date BETWEEN {prevFrom} AND {to}
              AND a.id NOT IN (SELECT m.account_id FROM nexora.account_mapping m
                               WHERE m.company_id = {Company} AND m.mapping_key IN ('cogs', 'purchases'))
            GROUP BY a.code, a.name
            """).ToListAsync(ct);
        var currency = await CurrencyAsync(ct);
        var total1 = rows.Sum(r => r.Current);
        var total0 = rows.Sum(r => r.Previous);
        if (total1 == 0 && total0 == 0)
            return new AskResponse("expense_increase",
                $"Aucune dépense d'exploitation comptabilisée du {D(prevFrom)} au {D(to)}.", [], ["accounting/reports/general-ledger"], false);

        var up = rows.Where(r => r.Current > r.Previous).OrderByDescending(r => r.Current - r.Previous).Take(5).ToList();
        var head = $"Dépenses d'exploitation : {M(total1, currency)} du {D(from)} au {D(to)}, contre {M(total0, currency)} du {D(prevFrom)} au {D(prevTo)} ({Signed(total1 - total0, currency)}).";
        var body = up.Count == 0
            ? " Aucun poste de dépense n'a augmenté."
            : " En hausse : " + string.Join(" ; ", up.Select(r =>
                $"{r.Code} {r.Name} {Signed(r.Current - r.Previous, currency)} ({M(r.Previous, currency)} → {M(r.Current, currency)})")) + ".";
        return new AskResponse("expense_increase", head + body,
            up.Select(r => new KpiValue(r.Code, r.Name, r.Current - r.Previous, currency, KpiValue.Actual)).ToList(),
            ["accounting/reports/general-ledger"], false);
    }

    // ------------------------------------------------------------------ Réapprovisionnement

    private async Task<AskResponse> ReorderAsync(CancellationToken ct)
    {
        var suggestions = (await reorder.SuggestionsAsync(ct))
            .OrderByDescending(s => s.ReorderPoint - s.Available).Take(10).ToList();
        if (suggestions.Count == 0)
            return new AskResponse("reorder", "Aucun produit n'est sous son point de commande : rien à commander pour l'instant.",
                [], ["inventory/reorder-suggestions"], false);
        return new AskResponse("reorder",
            $"{suggestions.Count} produit(s) sous le point de commande. À commander en priorité : " +
            string.Join(" ; ", suggestions.Select(s =>
                $"{s.ProductName} — {N(s.SuggestedQuantity)} (disponible {N(s.Available)}, point de commande {N(s.ReorderPoint)})")) + ".",
            suggestions.Select(s => new KpiValue(s.Sku, s.ProductName, s.SuggestedQuantity, "qté", KpiValue.Actual)).ToList(),
            ["inventory/reorder-suggestions"], false);
    }

    // ------------------------------------------------------------------ Risque de rupture (couverture)

    private async Task<AskResponse> StockoutRiskAsync(CancellationToken ct)
    {
        // Couverture = stock disponible / ventes moyennes par jour sur 30 jours : une estimation, signalée comme telle.
        var rows = await db.Database.SqlQuery<CoverRow>($"""
            WITH sold AS (
                SELECT sl.product_id, SUM(sl.quantity) / 30.0 AS daily
                FROM nexora.sale_line sl JOIN nexora.sale s ON s.id = sl.sale_id
                WHERE s.company_id = {Company} AND s.status IN ('completed', 'partially_refunded', 'refunded')
                  AND s.sold_at >= now() - interval '30 days'
                GROUP BY sl.product_id),
            stock AS (
                SELECT product_id, SUM(quantity_on_hand - quantity_reserved) AS available
                FROM nexora.stock_balance WHERE company_id = {Company} GROUP BY product_id)
            SELECT p.sku AS "Sku", p.name AS "Name", COALESCE(st.available, 0) AS "Available", so.daily AS "Daily",
                   COALESCE(st.available, 0) / so.daily AS "DaysCover"
            FROM sold so
            JOIN nexora.product p ON p.id = so.product_id
            LEFT JOIN stock st ON st.product_id = p.id
            WHERE p.status = 'active' AND p.product_type IN ('stockable', 'weight') AND so.daily > 0
            ORDER BY COALESCE(st.available, 0) / so.daily
            LIMIT 5
            """).ToListAsync(ct);
        if (rows.Count == 0)
            return new AskResponse("stockout_risk",
                "Pas de ventes de produits stockés sur les 30 derniers jours : pas de base pour estimer un risque de rupture.",
                [], ["reports/stock/ruptures"], false);
        return new AskResponse("stockout_risk",
            "Estimation d'après les ventes des 30 derniers jours — couverture la plus courte : " +
            string.Join(" ; ", rows.Select(r =>
                $"{r.Name} — {(r.DaysCover is { } d && d <= 0 ? "déjà en rupture" : $"{N(r.DaysCover ?? 0, 1)} jour(s)")} (disponible {N(r.Available)}, ~{N(r.Daily, 1)}/jour)")) + ".",
            rows.Select(r => new KpiValue(r.Sku, r.Name, Math.Round(r.DaysCover ?? 0, 1), "jours", KpiValue.Forecast)).ToList(),
            ["reports/stock/ruptures", "inventory/reorder-suggestions"], ContainsForecast: true);
    }

    // ------------------------------------------------------------------ Meilleures ventes (NEXORA ONE §8)

    private async Task<AskResponse> TopProductsAsync(ReportFilter filter, CancellationToken ct)
    {
        var (from, to, prevFrom, prevTo) = await PeriodsAsync(filter, ct);
        var current = await salesReports.TopProductsAsync(new ReportFilter { From = from, To = to }, 5, ct);
        var currency = await CurrencyAsync(ct);
        if (current.Count == 0)
            return new AskResponse("top_products", $"Aucune vente du {D(from)} au {D(to)} : pas de classement possible.",
                [], ["reports/sales/top-products"], false);
        // Tendance = même produit, période précédente de même durée (classement large pour le retrouver).
        var previous = (await salesReports.TopProductsAsync(new ReportFilter { From = prevFrom, To = prevTo }, 200, ct))
            .ToDictionary(p => p.ProductId, p => p.Revenue);
        string Trend(ProductRankRow r) => previous.TryGetValue(r.ProductId, out var before) && before > 0
            ? $", tendance {(r.Revenue >= before ? "+" : "−")}{Math.Abs(Math.Round((r.Revenue - before) / before * 100m, 1)).ToString("0.#", Fr)} %"
            : ", nouveau sur la période";
        var total = current.Sum(r => r.Revenue);
        return new AskResponse("top_products",
            $"Produits qui se vendent le mieux du {D(from)} au {D(to)} (chiffre d'affaires HT) : " +
            string.Join(" ; ", current.Select((r, i) =>
                $"{i + 1}) {r.Name} — {M(r.Revenue, currency)} ({N(r.Quantity)} vendus, marge {M(r.Margin, currency)}{Trend(r)})")) +
            $". Ces {current.Count} produits totalisent {M(total, currency)}.",
            current.Select(r => new KpiValue(r.Sku, r.Name, r.Revenue, currency, KpiValue.Actual)).ToList(),
            ["reports/sales/top-products", "reports/sales/breakdown/product"], false);
    }

    // ------------------------------------------------------------------ Stock en baisse (NEXORA ONE §8)

    private static readonly Dictionary<string, string> MovementLabels = new()
    {
        ["sale"] = "sorties par ventes", ["sale_return"] = "retours clients", ["receipt"] = "réceptions fournisseurs",
        ["purchase_return"] = "retours fournisseurs", ["transfer_in"] = "transferts entrants", ["transfer_out"] = "transferts sortants",
        ["adjustment"] = "ajustements", ["loss"] = "pertes", ["breakage"] = "casse", ["expiry"] = "péremptions",
        ["found"] = "stock retrouvé", ["production_input"] = "consommations de production",
        ["production_output"] = "production", ["count"] = "écarts d'inventaire", ["opening"] = "stock d'ouverture",
    };

    private async Task<AskResponse> StockDecreaseAsync(CancellationToken ct)
    {
        // Mouvements réels du stock physique (réservations exclues : elles ne sortent rien), 30 j vs 30 j précédents.
        var rows = await db.Database.SqlQuery<MovementRow>($"""
            SELECT movement_type AS "MovementType",
                   COALESCE(SUM(quantity) FILTER (WHERE occurred_at >= now() - interval '30 days'), 0) AS "Quantity",
                   COALESCE(SUM(quantity * unit_cost) FILTER (WHERE occurred_at >= now() - interval '30 days'), 0) AS "Value",
                   COALESCE(SUM(quantity) FILTER (WHERE occurred_at < now() - interval '30 days'), 0) AS "PreviousQuantity",
                   COALESCE(SUM(quantity * unit_cost) FILTER (WHERE occurred_at < now() - interval '30 days'), 0) AS "PreviousValue"
            FROM nexora.stock_movement
            WHERE company_id = {Company} AND occurred_at >= now() - interval '60 days'
              AND movement_type NOT IN ('reservation', 'release')
            GROUP BY movement_type
            """).ToListAsync(ct);
        var currency = await CurrencyAsync(ct);
        if (rows.All(r => r.Quantity == 0))
            return new AskResponse("stock_decrease", "Aucun mouvement de stock sur les 30 derniers jours : le stock n'a pas bougé.",
                [], ["inventory"], false);

        var net = rows.Sum(r => r.Value);
        var outflows = rows.Where(r => r.Value < 0).OrderBy(r => r.Value).ToList();
        var inflows = rows.Where(r => r.Value > 0).OrderByDescending(r => r.Value).ToList();
        var outTotal = -outflows.Sum(r => r.Value);
        var inTotal = inflows.Sum(r => r.Value);
        string Label(string t) => MovementLabels.GetValueOrDefault(t, t);

        var head = net < 0
            ? $"Sur les 30 derniers jours, la valeur du stock a baissé de {M(-net, currency)} : {M(outTotal, currency)} sont sortis pour {M(inTotal, currency)} entrés."
            : $"Le stock ne diminue pas en valeur sur 30 jours : {M(inTotal, currency)} entrés pour {M(outTotal, currency)} sortis ({Signed(net, currency)}).";
        var causes = outflows.Take(4).Select(r =>
        {
            var prev = -r.PreviousValue;
            var change = prev > 0
                ? $", {(-r.Value >= prev ? "+" : "−")}{Math.Abs(Math.Round((-r.Value - prev) / prev * 100m, 1)).ToString("0.#", Fr)} % vs les 30 jours précédents"
                : "";
            return $"{Label(r.MovementType)} {M(-r.Value, currency)}{change}";
        }).ToList();
        var body = causes.Count == 0 ? "" : " Principales sorties : " + string.Join(" ; ", causes) + ".";
        var receipts = rows.FirstOrDefault(r => r.MovementType == "receipt")?.Value ?? 0m;
        if (net < 0 && receipts < outTotal / 2)
            body += " Les réceptions fournisseurs ne compensent pas les sorties : le réapprovisionnement est en retard sur la demande.";
        var shrink = outflows.Where(r => r.MovementType is "loss" or "breakage" or "expiry" or "adjustment" or "count").Sum(r => -r.Value);
        if (shrink > 0 && outTotal > 0 && shrink / outTotal >= 0.1m)
            body += $" Attention : pertes, casse, péremptions et ajustements représentent {Pct(shrink / outTotal)} des sorties.";

        // Produits dont le stock a le plus baissé (valeur nette) — pour savoir où agir.
        var products = await db.Database.SqlQuery<ProductDropRow>($"""
            SELECT p.sku AS "Sku", p.name AS "Name", SUM(m.quantity) AS "NetQuantity",
                   COALESCE(-SUM(m.quantity) FILTER (WHERE m.movement_type = 'sale'), 0) AS "SoldQuantity"
            FROM nexora.stock_movement m JOIN nexora.product p ON p.id = m.product_id
            WHERE m.company_id = {Company} AND m.occurred_at >= now() - interval '30 days'
              AND m.movement_type NOT IN ('reservation', 'release')
            GROUP BY p.sku, p.name
            HAVING SUM(m.quantity) < 0
            ORDER BY SUM(m.quantity * m.unit_cost) ASC
            LIMIT 5
            """).ToListAsync(ct);
        if (products.Count > 0)
            body += " Plus fortes baisses : " + string.Join(" ; ", products.Select(p =>
                $"{p.Name} {N(p.NetQuantity)}{(p.SoldQuantity > 0 ? $" (dont {N(p.SoldQuantity)} vendus)" : "")}")) + ".";

        var facts = outflows.Concat(inflows)
            .Select(r => new KpiValue("stock_" + r.MovementType, Label(r.MovementType), Math.Round(r.Value, 2), currency, KpiValue.Actual))
            .ToList();
        return new AskResponse("stock_decrease", head + body, facts,
            ["inventory", "reports/stock/valuation", "inventory/reorder-suggestions"], false);
    }

    // ------------------------------------------------------------------ Créances

    private async Task<AskResponse> TopDebtorsAsync(CancellationToken ct)
    {
        var ar = await aging.ReceivablesAsync(ct: ct);
        var top = ar.Rows.Where(r => r.Total > 0).OrderByDescending(r => r.Total).Take(5).ToList();
        if (top.Count == 0)
            return new AskResponse("top_debtors", "Aucun client ne vous doit d'argent.", [], ["reports/aging/receivables"], false);
        return new AskResponse("top_debtors",
            $"Clients qui vous doivent le plus (total des créances {M(ar.GrandTotal, ar.Currency)}) : " +
            string.Join(" ; ", top.Select(r =>
            {
                var overdue = r.Buckets.Skip(1).Sum();
                return $"{r.Party} — {M(r.Total, ar.Currency)}{(overdue > 0 ? $" dont {M(overdue, ar.Currency)} échus" : "")}";
            })) + ".",
            top.Select(r => new KpiValue("customer", r.Party, r.Total, ar.Currency, KpiValue.Actual)).ToList(),
            ["reports/aging/receivables"], false);
    }

    // ------------------------------------------------------------------ Solde d'un client (module clients §27)

    private sealed record CustomerHit(Guid Id, string Name);

    private async Task<AskResponse> CustomerBalanceAsync(Guid? customerId, string question, CancellationToken ct)
    {
        // Le client vient du contexte (fiche ouverte) ou d'un code / nom cité dans la question.
        if (customerId is null)
        {
            var hit = await db.Database.SqlQuery<CustomerHit>($"""
                SELECT id AS "Id", name AS "Name" FROM nexora.customer
                WHERE company_id = {Company}
                  AND (upper({question}) LIKE '%' || upper(customer_number) || '%'
                       OR (length(name) >= 4 AND nexora.search_norm({question}) LIKE '%' || nexora.search_norm(name) || '%'))
                ORDER BY length(name) DESC
                LIMIT 1
                """).FirstOrDefaultAsync(ct);
            customerId = hit?.Id;
        }
        if (customerId is null)
            return new AskResponse("customer_balance",
                "Précisez le client (code client ou nom), ou posez la question depuis sa fiche.", [], ["customers"], false);

        var a = await customerAccounts.SummaryAsync(customerId.Value, null, null, ct);
        if (a is null)
            return new AskResponse("customer_balance", "Client introuvable dans l'entreprise active.", [], ["customers"], false);

        var head = a.NetBalance > 0m
            ? $"{a.Name} vous doit aujourd'hui {M(a.NetBalance, a.Currency)}."
            : a.NetBalance < 0m
                ? $"{a.Name} ne vous doit rien : vous lui devez {M(-a.NetBalance, a.Currency)} d'avoir disponible."
                : $"{a.Name} ne vous doit rien aujourd'hui : son compte est soldé.";
        var detail = $" Calcul : crédits restant dus {M(a.BalanceDue, a.Currency)}"
                     + (a.OverdueAmount > 0m ? $" (dont {M(a.OverdueAmount, a.Currency)} échus, le plus ancien depuis {a.OldestOverdueDays} jour(s))" : "")
                     + (a.AvailableStoreCredit > 0m ? $" − avoir disponible {M(a.AvailableStoreCredit, a.Currency)}" : "")
                     + "."
                     + (a.LastPaymentAt is { } last ? $" Dernier versement : {M(a.LastPaymentAmount ?? 0m, a.Currency)} le {last.ToString("dd/MM/yyyy", Fr)}." : "")
                     + (a.LedgerConsistent ? " Le journal du compte client concorde." : " Attention : le journal du compte client ne concorde pas, vérifiez le compte.");
        return new AskResponse("customer_balance", head + detail,
        [
            new("customer_net_balance", "Solde net", a.NetBalance, a.Currency, KpiValue.Actual),
            new("customer_balance_due", "Crédits restant dus", a.BalanceDue, a.Currency, KpiValue.Actual),
            new("customer_overdue", "Dont échu", a.OverdueAmount, a.Currency, KpiValue.Actual),
            new("customer_store_credit", "Avoir disponible", a.AvailableStoreCredit, a.Currency, KpiValue.Actual),
        ], [$"customers/{a.CustomerId}/account", $"customers/{a.CustomerId}/ledger"], false);
    }

    private async Task<AskResponse> OverdueAsync(CancellationToken ct)
    {
        var ar = await aging.ReceivablesAsync(ct: ct);
        var overdue = ar.Rows.Where(r => r.Buckets.Skip(1).Sum() > 0m)
            .OrderByDescending(r => r.Buckets.Skip(1).Sum()).ToList();
        if (overdue.Count == 0)
            return new AskResponse("overdue_receivables", "Aucune créance échue : tous les clients sont à jour de leurs paiements.",
                [], ["reports/aging/receivables"], false);
        // NEXORA ONE §8 — réponse nominative : qui, combien d'échu, sur quel total.
        return new AskResponse("overdue_receivables",
            $"{overdue.Count} client(s) avec des paiements en retard, total échu {M(overdue.Sum(r => r.Buckets.Skip(1).Sum()), ar.Currency)}. " +
            "Les plus importants : " + string.Join(" ; ", overdue.Take(5).Select(r =>
                $"{r.Party} — {M(r.Buckets.Skip(1).Sum(), ar.Currency)} échus sur {M(r.Total, ar.Currency)}")) + ".",
            overdue.Take(5).Select(r => new KpiValue("customer", r.Party, r.Buckets.Skip(1).Sum(), ar.Currency, KpiValue.Actual)).ToList(),
            ["reports/aging/receivables", "credit"], false);
    }

    // ------------------------------------------------------------------ Paie (module paie §33)

    private async Task<AskResponse> OutstandingAdvancesAsync(CancellationToken ct)
    {
        var rows = await payroll.OutstandingAdvancesAsync(ct);
        if (rows.Count == 0)
            return new AskResponse("payroll_outstanding_advances", "Aucune avance ni aucun prêt au personnel ne reste à récupérer.", [],
                ["hr?tab=advances"], false);
        var text = string.Join(" ; ", rows.Select(r =>
            $"{M(r.Advances + r.Loans, r.Currency)} à récupérer sur {r.Count} dossier(s) (avances et acomptes {M(r.Advances, r.Currency)}, " +
            $"prêts {M(r.Loans, r.Currency)})"));
        return new AskResponse("payroll_outstanding_advances", $"Il reste {text}. Montants issus des soldes d'avances tenus par le serveur.",
            rows.SelectMany(r => new[]
            {
                new KpiValue("payroll_advances_outstanding", "Avances et acomptes à récupérer", r.Advances, r.Currency, KpiValue.Actual),
                new KpiValue("payroll_loans_outstanding", "Prêts au personnel à récupérer", r.Loans, r.Currency, KpiValue.Actual),
            }).ToList(), ["hr?tab=advances", "hr/reports/staff_debt"], false);
    }

    private async Task<AskResponse> PayrollMassAsync(string q, CancellationToken ct)
    {
        var today = await Reporting.ReportingClock.TodayAsync(db, ct);
        var month = new DateOnly(today.Year, today.Month, 1);
        if (Contains(q, "mois dernier", "mois précédent", "mois precedent")) month = month.AddMonths(-1);
        var label = month.ToString("MMMM yyyy", Fr);
        var totals = await payroll.PayrollMassAsync(month, ct);
        if (totals.Count == 0)
            return new AskResponse("payroll_mass", $"Aucune paie calculée pour {label} : la masse salariale n'est pas encore connue.", [],
                ["hr?tab=payroll"], false);
        var text = string.Join(" ; ", totals.Select(t =>
            $"brut {M(t.Gross, t.Currency)}, net {M(t.Net, t.Currency)}, coût employeur {M(t.EmployerCost, t.Currency)}" +
            (t.Unpaid > 0m ? $" (reste à payer {M(t.Unpaid, t.Currency)})" : "")));
        return new AskResponse("payroll_mass", $"Masse salariale de {label} : {text}.",
            totals.SelectMany(t => new[]
            {
                new KpiValue("payroll_gross", "Masse salariale brute", t.Gross, t.Currency, KpiValue.Actual),
                new KpiValue("payroll_net", "Net à payer", t.Net, t.Currency, KpiValue.Actual),
                new KpiValue("payroll_employer_cost", "Coût employeur", t.EmployerCost, t.Currency, KpiValue.Actual),
            }).ToList(), ["hr?tab=payroll", "hr/reports/payroll_mass"], false);
    }

    // ------------------------------------------------------------------ Indicateurs simples

    private async Task<AskResponse> SalesAsync(ReportFilter filter, CancellationToken ct)
    {
        var cc = await kpi.CommandCenterAsync(filter, ct);
        var rev = cc.Kpis.First(k => k.Key == KpiCatalog.Revenue);
        return new AskResponse("sales_total",
            $"Chiffre d'affaires du {D(cc.From)} au {D(cc.To)} : {M(rev.Value, cc.Currency)}.",
            [rev, cc.Kpis.First(k => k.Key == KpiCatalog.Transactions), cc.Kpis.First(k => k.Key == KpiCatalog.AverageBasket)],
            ["reports/sales/journal"], false);
    }

    private async Task<AskResponse> MarginAsync(ReportFilter filter, CancellationToken ct)
    {
        var cc = await kpi.CommandCenterAsync(filter, ct);
        var margin = cc.Kpis.First(k => k.Key == KpiCatalog.GrossMargin);
        var rate = cc.Kpis.First(k => k.Key == KpiCatalog.MarginRate);
        return new AskResponse("margin", $"Marge brute : {M(margin.Value, cc.Currency)} — taux {rate.Value.ToString("0.#", Fr)} %.",
            [margin, rate], ["reports/sales/breakdown/category"], false);
    }

    private async Task<AskResponse> CashAsync(ReportFilter filter, CancellationToken ct)
    {
        var cc = await kpi.CommandCenterAsync(filter, ct);
        var cash = cc.Kpis.First(k => k.Key == KpiCatalog.Cash);
        var inflows = cc.Kpis.First(k => k.Key == KpiCatalog.CashInflows);
        var outflows = cc.Kpis.First(k => k.Key == KpiCatalog.CashOutflows);
        return new AskResponse("cash",
            $"Trésorerie actuelle {M(cash.Value, cc.Currency)}. Sur la période : encaissements {M(inflows.Value, cc.Currency)}, décaissements {M(outflows.Value, cc.Currency)}.",
            [cash, inflows, outflows], ["reports/finance/cash-flow"], false);
    }

    // ------------------------------------------------------------------ Aides

    /// <summary>Période demandée et période précédente de même durée (comparaison N / N-1).</summary>
    private async Task<(DateOnly From, DateOnly To, DateOnly PrevFrom, DateOnly PrevTo)> PeriodsAsync(ReportFilter filter, CancellationToken ct)
    {
        var today = await ReportingClock.TodayAsync(db, ct);
        var (from, to) = FilterPeriod.Resolve(filter, today);
        var span = to.DayNumber - from.DayNumber;
        var prevTo = from.AddDays(-1);
        return (from, to, prevTo.AddDays(-span), prevTo);
    }

    private async Task<string> CurrencyAsync(CancellationToken ct)
        => await db.Database.SqlQuery<string>($"""
            SELECT cur.code AS "Value" FROM nexora.company c JOIN nexora.currency cur ON cur.id = c.default_currency_id
            WHERE c.id = {Company}
            """).FirstOrDefaultAsync(ct) ?? "HTG";

    private static bool Contains(string q, params string[] terms) => terms.Any(q.Contains);
    private static string D(DateOnly d) => d.ToString("dd/MM", Fr);
    private static string N(decimal v, int decimals = 2) => Math.Round(v, decimals).ToString("#,0.##", Fr);
    private static string M(decimal v, string currency) => $"{v.ToString("#,0.00", Fr)} {currency}";
    private static string Signed(decimal v, string currency) => $"{(v > 0 ? "+" : v < 0 ? "−" : "")}{Math.Abs(v).ToString("#,0.00", Fr)} {currency}";
    private static string Pct(decimal ratio) => $"{(ratio * 100m).ToString("0.#", Fr)} %";

    /// <summary>Refus explicite, sans aucun chiffre : la réponse ne révèle rien de la donnée demandée.</summary>
    private static AskResponse Denied() => new("forbidden",
        "Cette information n'est pas accessible avec vos permissions.", [], [], false);
}
