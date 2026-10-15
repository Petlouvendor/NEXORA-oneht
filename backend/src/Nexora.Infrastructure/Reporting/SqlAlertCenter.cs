using Microsoft.EntityFrameworkCore;
using Nexora.Application.Abstractions;
using Nexora.Application.Reporting;
using Nexora.Contracts.Reporting;
using Nexora.Infrastructure.Persistence;
using Nexora.Domain.Common;

namespace Nexora.Infrastructure.Reporting;

/// <summary>Centre d'alertes (Phase 7 §28) — évalué à la volée sur les données validées.</summary>
public sealed class SqlAlertCenter(
    NexoraDbContext db, ITenantContext tenant, IDateTimeProvider clock, IPermissionService permissions) : IAlertCenter
{
    /// <summary>Alertes porteuses de montants financiers (guide §6, §17) : réservées à
    /// <c>reporting.view_financial</c>. Les alertes de stock et de ventes restent visibles.</summary>
    private static readonly HashSet<string> FinancialAlerts =
        ["overdue_receivable", "overdue_payable", "cash_variance", "low_cash"];

    private Guid Company => tenant.ActiveCompanyId
        ?? throw TenantErrors.ActiveCompanyRequired();

    private sealed record Row(int Count, decimal Amount);

    public async Task<AlertCenterResponse> EvaluateAsync(CancellationToken ct = default)
    {
        var today = await ReportingClock.TodayAsync(db, ct);
        var alerts = new List<AlertSummary>();

        var lowStock = await One($"""
            SELECT COUNT(*)::int AS "Count", 0::numeric AS "Amount"
            FROM nexora.product_reorder_rule r
            JOIN nexora.stock_balance sb ON sb.product_id = r.product_id
            WHERE r.company_id = {Company} AND r.status = 'active'
              AND (sb.quantity_on_hand - sb.quantity_reserved) <= r.reorder_point
            """, ct);
        if (lowStock.Count > 0)
            alerts.Add(new("low_stock", "warning", "Stock faible",
                $"{lowStock.Count} produit(s) au point de commande ou en dessous.", "inventory/reorder-suggestions", lowStock.Count));

        var expiring = await One($"""
            SELECT COUNT(*)::int AS "Count", 0::numeric AS "Amount"
            FROM nexora.lot l JOIN nexora.lot_stock ls ON ls.lot_id = l.id
            WHERE l.company_id = {Company} AND l.expiration_date IS NOT NULL
              AND l.expiration_date <= {today.AddDays(60)} AND ls.quantity_on_hand > 0
            """, ct);
        if (expiring.Count > 0)
            alerts.Add(new("expiring_lot", "warning", "Lots proches de l'expiration",
                $"{expiring.Count} lot(s) expirent sous 60 jours.", "reports/stock/expiry", expiring.Count));

        var overdueAr = await One($"""
            SELECT COUNT(*)::int AS "Count", COALESCE(SUM(remaining_amount),0) AS "Amount"
            FROM nexora.customer_receivable
            WHERE company_id = {Company} AND status IN ('OPEN','PARTIALLY_PAID','OVERDUE')
              AND due_date IS NOT NULL AND due_date < {today}
            """, ct);
        if (overdueAr.Count > 0)
            alerts.Add(new("overdue_receivable", "critical", "Créances échues",
                $"{overdueAr.Count} créance(s) échue(s) — {overdueAr.Amount:0.##}.", "reports/aging/receivables", overdueAr.Count));

        var overdueAp = await One($"""
            SELECT COUNT(*)::int AS "Count", COALESCE(SUM(remaining_amount),0) AS "Amount"
            FROM nexora.supplier_payable
            WHERE company_id = {Company} AND status IN ('OPEN','PARTIALLY_PAID','OVERDUE')
              AND due_date IS NOT NULL AND due_date < {today}
            """, ct);
        if (overdueAp.Count > 0)
            alerts.Add(new("overdue_payable", "warning", "Dettes échues",
                $"{overdueAp.Count} dette(s) échue(s) — {overdueAp.Amount:0.##}.", "reports/aging/payables", overdueAp.Count));

        var cashVariance = await One($"""
            SELECT COUNT(*)::int AS "Count", COALESCE(SUM(ABS(difference)),0) AS "Amount"
            FROM nexora.cash_session
            WHERE company_id = {Company} AND status IN ('closed','reconciled')
              AND difference IS NOT NULL AND ABS(difference) > 0
              AND closed_at::date >= {today.AddDays(-7)}
            """, ct);
        if (cashVariance.Count > 0)
            alerts.Add(new("cash_variance", "warning", "Écarts de caisse (7 j)",
                $"{cashVariance.Count} session(s) avec écart — total {cashVariance.Amount:0.##}.", "reports/cash/sessions", cashVariance.Count));

        var lowCash = await One($"""
            SELECT 0::int AS "Count", COALESCE(SUM(current_balance),0) AS "Amount"
            FROM nexora.financial_account
            WHERE company_id = {Company} AND status = 'active' AND account_type IN ('cash','bank','mobile_money')
            """, ct);
        if (lowCash.Amount < 0m)
            alerts.Add(new("low_cash", "critical", "Trésorerie négative",
                $"Solde de trésorerie {lowCash.Amount:0.##}.", "reports/finance/cash-flow", 1));

        // Baisse forte des ventes : 7 derniers jours vs 7 précédents (§28)
        var recent = await Scalar($"""
            SELECT COALESCE(SUM(subtotal - discount_total),0) AS "Value" FROM nexora.sale
            WHERE company_id = {Company} AND status IN ('completed','partially_refunded','refunded')
              AND sold_at::date >= {today.AddDays(-7)}
            """, ct);
        var previous = await Scalar($"""
            SELECT COALESCE(SUM(subtotal - discount_total),0) AS "Value" FROM nexora.sale
            WHERE company_id = {Company} AND status IN ('completed','partially_refunded','refunded')
              AND sold_at::date >= {today.AddDays(-14)} AND sold_at::date < {today.AddDays(-7)}
            """, ct);
        if (previous > 0m && recent < previous * 0.7m)
            alerts.Add(new("sales_drop", "warning", "Baisse des ventes",
                $"−{Math.Round((previous - recent) / previous * 100m, 0)} % sur 7 jours.", "reports/sales/journal", 1));

        var granted = await permissions.CurrentPermissionsAsync(tenant, ct);
        if (!granted.Contains(Nexora.Application.Security.Permissions.Reporting.ViewFinancial))
            alerts.RemoveAll(a => FinancialAlerts.Contains(a.Key));
        // « Baisse des ventes » révèle l'évolution du chiffre d'affaires (guide §18).
        if (!granted.Contains(Nexora.Application.Security.Permissions.Reporting.ViewSales))
            alerts.RemoveAll(a => a.Key == "sales_drop");
        return new AlertCenterResponse(clock.UtcNow, alerts);
    }

    private sealed record ScalarRow(decimal Value);
    private async Task<Row> One(FormattableString sql, CancellationToken ct)
        => (await db.Database.SqlQuery<Row>(sql).ToListAsync(ct)).FirstOrDefault() ?? new Row(0, 0m);
    private async Task<decimal> Scalar(FormattableString sql, CancellationToken ct)
        => (await db.Database.SqlQuery<ScalarRow>(sql).ToListAsync(ct)).FirstOrDefault()?.Value ?? 0m;
}
