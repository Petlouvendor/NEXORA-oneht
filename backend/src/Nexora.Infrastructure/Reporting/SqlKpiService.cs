using Microsoft.EntityFrameworkCore;
using Nexora.Application.Abstractions;
using Nexora.Application.Reporting;
using Nexora.Contracts.Reporting;
using Nexora.Domain.Common;
using Nexora.Infrastructure.Persistence;

namespace Nexora.Infrastructure.Reporting;

public sealed class SqlKpiService(
    NexoraDbContext db, ITenantContext tenant, IDateTimeProvider clock, IAlertCenter alerts,
    IPermissionService permissions) : IKpiService
{
    private Guid Company => tenant.ActiveCompanyId
        ?? throw TenantErrors.ActiveCompanyRequired();

    private sealed record Scalar(decimal Value);
    private sealed record CountScalar(long Value);
    private sealed record TrendRow(DateOnly D, decimal V);

    public async Task<CommandCenterResponse> CommandCenterAsync(ReportFilter filter, CancellationToken ct = default)
    {
        var today = await ReportingClock.TodayAsync(db, ct);
        var (from, to) = FilterPeriod.Resolve(filter, today);
        var currency = await FunctionalCurrency(ct);

        var revenue = await Money($"""
            SELECT COALESCE(SUM(subtotal - discount_total),0) AS "Value" FROM nexora.sale
            WHERE company_id = {Company} AND status IN ('completed','partially_refunded','refunded')
              AND sold_at::date BETWEEN {from} AND {to}
            """, ct);
        var returns = await Money($"""
            SELECT COALESCE(SUM(sr.refund_total - COALESCE(l.tax,0)),0) AS "Value"
            FROM nexora.sale_return sr
            LEFT JOIN LATERAL (SELECT SUM(tax_amount) tax FROM nexora.sale_return_line WHERE sale_return_id = sr.id) l ON true
            WHERE sr.company_id = {Company} AND sr.status = 'completed' AND sr.returned_at::date BETWEEN {from} AND {to}
            """, ct);
        revenue -= returns;

        var cogs = await Money($"""
            SELECT COALESCE(SUM(sl.unit_cost * sl.quantity),0) AS "Value"
            FROM nexora.sale_line sl JOIN nexora.sale s ON s.id = sl.sale_id
            WHERE s.company_id = {Company} AND s.status IN ('completed','partially_refunded','refunded')
              AND s.sold_at::date BETWEEN {from} AND {to}
            """, ct);
        var expenses = await Money($"""
            SELECT COALESCE(SUM(amount),0) AS "Value" FROM nexora.expense
            WHERE company_id = {Company} AND status IN ('approved','paid') AND expense_date BETWEEN {from} AND {to}
            """, ct);
        var count = await Count($"""
            SELECT COUNT(*)::bigint AS "Value" FROM nexora.sale
            WHERE company_id = {Company} AND status IN ('completed','partially_refunded','refunded')
              AND sold_at::date BETWEEN {from} AND {to}
            """, ct);
        var cash = await Money($"""
            SELECT COALESCE(SUM(current_balance),0) AS "Value" FROM nexora.financial_account
            WHERE company_id = {Company} AND status = 'active'
              AND account_type IN ('cash','bank','mobile_money')
            """, ct);
        var receivables = await Money($"""
            SELECT COALESCE(SUM(remaining_amount),0) AS "Value" FROM nexora.customer_receivable
            WHERE company_id = {Company} AND status IN ('OPEN','PARTIALLY_PAID','OVERDUE')
            """, ct);
        var payables = await Money($"""
            SELECT COALESCE(SUM(remaining_amount),0) AS "Value" FROM nexora.supplier_payable
            WHERE company_id = {Company} AND status IN ('OPEN','PARTIALLY_PAID','OVERDUE')
            """, ct);
        var stockValue = await Money($"""
            SELECT COALESCE(SUM(quantity_on_hand * average_cost),0) AS "Value" FROM nexora.stock_balance
            WHERE company_id = {Company}
            """, ct);
        var todayRevenue = await Money($"""
            SELECT COALESCE(SUM(subtotal - discount_total),0) AS "Value" FROM nexora.sale
            WHERE company_id = {Company} AND status IN ('completed','partially_refunded','refunded')
              AND sold_at::date = {today}
            """, ct);
        var inflows = await Money($"""
            SELECT COALESCE(SUM(amount),0) AS "Value" FROM nexora.payment
            WHERE company_id = {Company} AND direction = 'inbound' AND status = 'completed'
              AND received_at::date BETWEEN {from} AND {to}
            """, ct);
        var outflows = await Money($"""
            SELECT COALESCE(SUM(amount),0) AS "Value" FROM nexora.payment
            WHERE company_id = {Company} AND direction = 'outbound' AND status = 'completed'
              AND received_at::date BETWEEN {from} AND {to}
            """, ct);

        var margin = revenue - cogs;
        var kpis = new List<KpiValue>
        {
            new(KpiCatalog.Revenue, "Chiffre d'affaires", Round(revenue), currency, KpiValue.Actual),
            new(KpiCatalog.GrossMargin, "Marge brute", Round(margin), currency, KpiValue.Actual),
            new(KpiCatalog.MarginRate, "Taux de marge", revenue == 0 ? 0m : Math.Round(margin / revenue * 100m, 1), "%", KpiValue.Actual),
            new(KpiCatalog.Profit, "Bénéfice", Round(margin - expenses), currency, KpiValue.Actual),
            new(KpiCatalog.Expenses, "Dépenses", Round(expenses), currency, KpiValue.Actual),
            new(KpiCatalog.Cash, "Trésorerie", Round(cash), currency, KpiValue.Actual),
            new(KpiCatalog.OpenReceivables, "Créances ouvertes", Round(receivables), currency, KpiValue.Actual),
            new(KpiCatalog.OpenPayables, "Dettes ouvertes", Round(payables), currency, KpiValue.Actual),
            new(KpiCatalog.StockValue, "Stock valorisé", Round(stockValue), currency, KpiValue.Actual),
            new(KpiCatalog.Transactions, "Nombre de ventes", count, "n", KpiValue.Actual),
            new(KpiCatalog.AverageBasket, "Panier moyen", count == 0 ? 0m : Round(revenue / count), currency, KpiValue.Actual),
            new(KpiCatalog.TodayRevenue, "Ventes du jour", Round(todayRevenue), currency, KpiValue.Actual),
            new(KpiCatalog.CashInflows, "Encaissements", Round(inflows), currency, KpiValue.Actual),
            new(KpiCatalog.CashOutflows, "Décaissements", Round(outflows), currency, KpiValue.Actual),
        };
        // Guide §6, §17, §18 : KPI financiers sans reporting.view_financial, chiffre d'affaires sans
        // reporting.view_sales — retirés côté serveur, pas seulement à l'affichage.
        var granted = await permissions.CurrentPermissionsAsync(tenant, ct);
        kpis.RemoveAll(k => !KpiCatalog.IsVisible(k.Key, granted));
        var showSales = granted.Contains(Nexora.Application.Security.Permissions.Reporting.ViewSales);

        var trend = (await db.Database.SqlQuery<TrendRow>($"""
            SELECT d::date AS "D",
                   COALESCE((SELECT SUM(subtotal - discount_total) FROM nexora.sale
                             WHERE company_id = {Company} AND status IN ('completed','partially_refunded','refunded')
                               AND sold_at::date = d::date), 0) AS "V"
            FROM generate_series({from}::date, {to}::date, interval '1 day') d
            """).ToListAsync(ct)).Select(r => new TrendPoint(r.D, Round(r.V))).ToList();

        // Comparaison période N vs N-1 de même durée (§26)
        var span = to.DayNumber - from.DayNumber;
        var prevFrom = from.AddDays(-(span + 1));
        var prevTo = from.AddDays(-1);
        var prevRevenue = await Money($"""
            SELECT COALESCE(SUM(subtotal - discount_total),0) AS "Value" FROM nexora.sale
            WHERE company_id = {Company} AND status IN ('completed','partially_refunded','refunded')
              AND sold_at::date BETWEEN {prevFrom} AND {prevTo}
            """, ct);
        var comparison = new ComparisonBlock("Période précédente", Round(revenue), Round(prevRevenue),
            Round(revenue - prevRevenue), prevRevenue == 0 ? 0m : Math.Round((revenue - prevRevenue) / prevRevenue * 100m, 1));
        if (!showSales)
        {
            // La tendance et la comparaison sont du chiffre d'affaires : rien n'en sort.
            trend = [];
            comparison = new ComparisonBlock("Période précédente", 0m, 0m, 0m, 0m);
        }

        var alertResp = await alerts.EvaluateAsync(ct);
        var critical = alertResp.Alerts.Where(a => a.Severity is "critical" or "warning").Take(6).ToList();

        return new CommandCenterResponse(from, to, currency, kpis, trend, comparison, critical, clock.UtcNow);
    }

    public async Task<CashierDashboardResponse> CashierDashboardAsync(Guid sessionId, CancellationToken ct = default)
    {
        var currency = await FunctionalCurrency(ct);
        var session = await db.CashSessions.AsNoTracking().FirstOrDefaultAsync(s => s.Id == sessionId, ct)
            ?? throw new InvalidOperationException("Session introuvable ou hors périmètre.");
        // Guide §13 : les données des autres caisses ne sont pas visibles par défaut.
        if (session.UserId != tenant.UserId
            && !(await permissions.CurrentPermissionsAsync(tenant, ct)).Contains(Nexora.Application.Security.Permissions.Pos.ManageSessions))
            throw new DomainException(Error.Forbidden("pos.session_not_owned", "Cette session de caisse n'est pas la vôtre."));

        var saleCount = await Count($"""
            SELECT COUNT(*)::bigint AS "Value" FROM nexora.sale WHERE cash_session_id = {sessionId} AND status <> 'void'
            """, ct);
        var collected = await Money($"""
            SELECT COALESCE(SUM(amount),0) AS "Value" FROM nexora.cash_movement
            WHERE cash_session_id = {sessionId} AND movement_type = 'sale_payment'
            """, ct);
        var breakdown = await db.Database.SqlQuery<BreakdownRow>($"""
            SELECT COALESCE(pm.name,'Espèces') AS "Label", SUM(sp.amount) AS "Amount"
            FROM nexora.sale s
            JOIN nexora.sale_payment sp ON sp.sale_id = s.id
            LEFT JOIN nexora.payment p ON p.id = sp.payment_id
            LEFT JOIN nexora.payment_method pm ON pm.id = p.payment_method_id
            WHERE s.cash_session_id = {sessionId} AND s.status <> 'void'
            GROUP BY COALESCE(pm.name,'Espèces')
            """).ToListAsync(ct);

        var cashIn = await Money($"""
            SELECT COALESCE(SUM(amount),0) AS "Value" FROM nexora.cash_movement
            WHERE cash_session_id = {sessionId} AND movement_type NOT IN ('opening_float','closing')
            """, ct);
        var expected = session.OpeningBalance + cashIn;

        return new CashierDashboardResponse(
            sessionId, session.Status, (int)saleCount, Round(collected),
            saleCount == 0 ? 0m : Round(collected / saleCount),
            breakdown.Select(b => new KpiValue("tender", b.Label, Round(b.Amount), currency, KpiValue.Actual)).ToList(),
            session.OpeningBalance, Round(expected),
            session.CountedBalance is { } cb ? Round(cb - expected) : null);
    }

    public async Task<ManagerDashboardResponse> ManagerDashboardAsync(ReportFilter filter, CancellationToken ct = default)
    {
        var today = await ReportingClock.TodayAsync(db, ct);
        var (from, to) = FilterPeriod.Resolve(filter, today);
        var currency = await FunctionalCurrency(ct);
        var estFilter = filter.EstablishmentId;

        var revenue = await Money($"""
            SELECT COALESCE(SUM(s.subtotal - s.discount_total),0) AS "Value" FROM nexora.sale s
            WHERE s.company_id = {Company} AND s.status IN ('completed','partially_refunded','refunded')
              AND s.sold_at::date BETWEEN {from} AND {to}
              AND ({estFilter}::uuid IS NULL OR s.establishment_id = {estFilter})
            """, ct);
        var cogs = await Money($"""
            SELECT COALESCE(SUM(sl.unit_cost * sl.quantity),0) AS "Value"
            FROM nexora.sale_line sl JOIN nexora.sale s ON s.id = sl.sale_id
            WHERE s.company_id = {Company} AND s.status IN ('completed','partially_refunded','refunded')
              AND s.sold_at::date BETWEEN {from} AND {to}
              AND ({estFilter}::uuid IS NULL OR s.establishment_id = {estFilter})
            """, ct);

        var byRegister = (await db.Database.SqlQuery<MgrRow>($"""
            SELECT COALESCE(cr.name,'(sans caisse)') AS "Label",
                   SUM(s.subtotal - s.discount_total) AS "Revenue", 0::numeric AS "Margin", COUNT(*)::int AS "Transactions"
            FROM nexora.sale s
            LEFT JOIN nexora.cash_session cs ON cs.id = s.cash_session_id
            LEFT JOIN nexora.cash_register cr ON cr.id = cs.cash_register_id
            WHERE s.company_id = {Company} AND s.status IN ('completed','partially_refunded','refunded')
              AND s.sold_at::date BETWEEN {from} AND {to}
            GROUP BY COALESCE(cr.name,'(sans caisse)') ORDER BY 2 DESC
            """).ToListAsync(ct)).Select(r => new ManagerRow(r.Label, Round(r.Revenue), Round(r.Margin), r.Transactions)).ToList();

        var bySalesperson = (await db.Database.SqlQuery<MgrRow>($"""
            SELECT COALESCE(u.full_name, u.email::text, '(inconnu)') AS "Label",
                   SUM(s.subtotal - s.discount_total) AS "Revenue", 0::numeric AS "Margin", COUNT(*)::int AS "Transactions"
            FROM nexora.sale s LEFT JOIN nexora.app_user u ON u.id = s.salesperson_user_id
            WHERE s.company_id = {Company} AND s.status IN ('completed','partially_refunded','refunded')
              AND s.sold_at::date BETWEEN {from} AND {to}
            GROUP BY 1 ORDER BY 2 DESC
            """).ToListAsync(ct)).Select(r => new ManagerRow(r.Label, Round(r.Revenue), Round(r.Margin), r.Transactions)).ToList();

        var top = await TopProducts(from, to, 5, true, ct);
        var slow = await TopProducts(from, to, 5, false, ct);
        var critical = await db.Database.SqlQuery<StockAlertRow>($"""
            SELECT p.id AS "ProductId", p.sku AS "Sku", p.name AS "Name", sb.inventory_location_id AS "LocationId",
                   sb.quantity_on_hand AS "OnHand", r.reorder_point AS "ReorderPoint"
            FROM nexora.product_reorder_rule r
            JOIN nexora.product p ON p.id = r.product_id
            JOIN nexora.stock_balance sb ON sb.product_id = r.product_id
            WHERE r.company_id = {Company} AND (sb.quantity_on_hand - sb.quantity_reserved) <= r.reorder_point
            ORDER BY (sb.quantity_on_hand - sb.quantity_reserved) ASC LIMIT 10
            """).ToListAsync(ct);

        var openReceivables = await Money($"""
            SELECT COALESCE(SUM(remaining_amount),0) AS "Value" FROM nexora.customer_receivable
            WHERE company_id = {Company} AND status IN ('OPEN','PARTIALLY_PAID','OVERDUE')
            """, ct);
        var expenses = await Money($"""
            SELECT COALESCE(SUM(amount),0) AS "Value" FROM nexora.expense
            WHERE company_id = {Company} AND status IN ('approved','paid') AND expense_date BETWEEN {from} AND {to}
              AND ({estFilter}::uuid IS NULL OR establishment_id = {estFilter})
            """, ct);

        var margin = revenue - cogs;
        var kpis = new List<KpiValue>
        {
            new(KpiCatalog.Revenue, "CA établissement", Round(revenue), currency, KpiValue.Actual),
            new(KpiCatalog.GrossMargin, "Marge brute", Round(margin), currency, KpiValue.Actual),
            new(KpiCatalog.MarginRate, "Taux de marge", revenue == 0 ? 0m : Math.Round(margin / revenue * 100m, 1), "%", KpiValue.Actual),
        };
        return new ManagerDashboardResponse(from, to, currency, kpis, byRegister, bySalesperson, top, slow, critical, Round(openReceivables), Round(expenses));
    }

    public async Task<GroupDashboardResponse> GroupDashboardAsync(Guid groupId, ReportFilter filter, CancellationToken ct = default)
    {
        if (!tenant.BypassRls && !tenant.GroupIds.Contains(groupId))
            throw new DomainException(Error.Forbidden("reporting.group_scope",
                "Groupe hors du périmètre autorisé. Activez la vue groupe (X-Group-Scope) avec un périmètre habilité (§37)."));

        var today = await ReportingClock.TodayAsync(db, ct);
        var (from, to) = FilterPeriod.Resolve(filter, today);

        // Périmètre autorisé du demandeur — capturé AVANT le scope système,
        // sinon la consolidation verrait toutes les entreprises (§37).
        var callerUnrestricted = tenant.BypassRls;
        var callerCompanies = tenant.CompanyIds.ToHashSet();

        // La vue consolidée ne dépend d'aucune entreprise active : devise = celle du groupe.
        using (tenant.BeginSystemScope())
        {
            var currency = await (from g in db.BusinessGroups.AsNoTracking()
                join cur in db.Currencies.AsNoTracking() on g.DefaultCurrencyId equals cur.Id
                where g.Id == groupId select cur.Code).FirstOrDefaultAsync(ct) ?? "HTG";

            var companies = await db.Companies.AsNoTracking()
                .Where(c => c.GroupId == groupId && c.Status != "archived")   // §29 : hors vues opérationnelles
                .Select(c => new { c.Id, c.Name, c.MainActivityType })
                .ToListAsync(ct);

            var authorizedIds = callerUnrestricted
                ? companies.Select(c => c.Id).ToHashSet()
                : callerCompanies;

            var rows = new List<CompanyKpiRow>();
            decimal consRevenue = 0, consResult = 0, groupCash = 0, groupRecv = 0, groupPay = 0;
            var excluded = new List<string>();

            foreach (var c in companies)
            {
                var authorized = authorizedIds.Contains(c.Id);
                if (!authorized) { excluded.Add(c.Name); continue; }   // §37 : exclusion stricte

                var rev = await Money($"""
                    SELECT COALESCE(SUM(subtotal - discount_total),0) AS "Value" FROM nexora.sale
                    WHERE company_id = {c.Id} AND status IN ('completed','partially_refunded','refunded')
                      AND sold_at::date BETWEEN {from} AND {to}
                    """, ct);
                var cogs = await Money($"""
                    SELECT COALESCE(SUM(sl.unit_cost * sl.quantity),0) AS "Value"
                    FROM nexora.sale_line sl JOIN nexora.sale s ON s.id = sl.sale_id
                    WHERE s.company_id = {c.Id} AND s.status IN ('completed','partially_refunded','refunded')
                      AND s.sold_at::date BETWEEN {from} AND {to}
                    """, ct);
                var exp = await Money($"""
                    SELECT COALESCE(SUM(amount),0) AS "Value" FROM nexora.expense
                    WHERE company_id = {c.Id} AND status IN ('approved','paid') AND expense_date BETWEEN {from} AND {to}
                    """, ct);
                var cash = await Money($"""
                    SELECT COALESCE(SUM(current_balance),0) AS "Value" FROM nexora.financial_account
                    WHERE company_id = {c.Id} AND status = 'active' AND account_type IN ('cash','bank','mobile_money')
                    """, ct);
                var recv = await Money($"""
                    SELECT COALESCE(SUM(remaining_amount),0) AS "Value" FROM nexora.customer_receivable
                    WHERE company_id = {c.Id} AND status IN ('OPEN','PARTIALLY_PAID','OVERDUE')
                    """, ct);
                var pay = await Money($"""
                    SELECT COALESCE(SUM(remaining_amount),0) AS "Value" FROM nexora.supplier_payable
                    WHERE company_id = {c.Id} AND status IN ('OPEN','PARTIALLY_PAID','OVERDUE')
                    """, ct);

                var result = rev - cogs - exp;
                rows.Add(new CompanyKpiRow(c.Id, c.Name, c.MainActivityType, Round(rev), Round(result), Round(cash), Round(recv), Round(pay), true));
                consRevenue += rev; consResult += result; groupCash += cash; groupRecv += recv; groupPay += pay;
            }

            var intercompany = await Money($"""
                SELECT COALESCE(SUM(amount),0) AS "Value" FROM nexora.intercompany_transaction
                WHERE group_id = {groupId} AND status IN ('draft','confirmed')
                """, ct);

            return new GroupDashboardResponse(groupId, from, to, currency, rows,
                Round(consRevenue), Round(consResult), Round(groupCash), Round(groupRecv), Round(groupPay),
                Round(intercompany), excluded);
        }
    }

    // --- helpers -----------------------------------------------------

    private sealed record BreakdownRow(string Label, decimal Amount);
    private sealed record MgrRow(string Label, decimal Revenue, decimal Margin, int Transactions);

    private async Task<IReadOnlyList<ProductRankRow>> TopProducts(DateOnly from, DateOnly to, int take, bool best, CancellationToken ct)
    {
        var direction = best ? "DESC" : "ASC";   // booléen contrôlé, jamais d'entrée utilisateur
        var sql =
            "SELECT p.id AS \"ProductId\", p.sku AS \"Sku\", p.name AS \"Name\", " +
            "SUM(sl.quantity) AS \"Quantity\", " +
            "SUM(sl.line_total - sl.tax_amount) AS \"Revenue\", " +
            "SUM(sl.line_total - sl.tax_amount - sl.unit_cost * sl.quantity) AS \"Margin\" " +
            "FROM nexora.sale_line sl JOIN nexora.sale s ON s.id = sl.sale_id " +
            "JOIN nexora.product p ON p.id = sl.product_id " +
            "WHERE s.company_id = {0} AND s.status IN ('completed','partially_refunded','refunded') " +
            "AND s.sold_at::date BETWEEN {1} AND {2} " +
            "GROUP BY p.id, p.sku, p.name " +
            $"ORDER BY SUM(sl.line_total - sl.tax_amount) {direction} LIMIT {{3}}";
        return await db.Database.SqlQueryRaw<ProductRankRow>(sql, Company, from, to, take).ToListAsync(ct);
    }

    private async Task<string> FunctionalCurrency(CancellationToken ct)
        => await (from c in db.Companies.AsNoTracking()
                  join cur in db.Currencies.AsNoTracking() on c.DefaultCurrencyId equals cur.Id
                  where c.Id == Company select cur.Code).FirstOrDefaultAsync(ct) ?? "HTG";

    private async Task<decimal> Money(FormattableString sql, CancellationToken ct)
        => (await db.Database.SqlQuery<Scalar>(sql).ToListAsync(ct)).FirstOrDefault()?.Value ?? 0m;

    private async Task<long> Count(FormattableString sql, CancellationToken ct)
        => (await db.Database.SqlQuery<CountScalar>(sql).ToListAsync(ct)).FirstOrDefault()?.Value ?? 0L;

    private static decimal Round(decimal v) => Math.Round(v, 2, MidpointRounding.ToEven);
}
