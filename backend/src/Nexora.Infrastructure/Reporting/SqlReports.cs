using Microsoft.EntityFrameworkCore;
using Nexora.Application.Abstractions;
using Nexora.Application.Reporting;
using Nexora.Contracts.Reporting;
using Nexora.Infrastructure.Persistence;

namespace Nexora.Infrastructure.Reporting;

public sealed class SqlSalesReports(NexoraDbContext db, ITenantContext tenant) : ISalesReports
{
    private Guid Company => tenant.ActiveCompanyId ?? throw new InvalidOperationException("Entreprise active requise.");

    public async Task<SalesJournalResponse> JournalAsync(ReportFilter filter, CancellationToken ct = default)
    {
        var (from, to) = FilterPeriod.Resolve(filter, await ReportingClock.TodayAsync(db, ct));
        var (page, size) = (Math.Max(1, filter.Page), Math.Clamp(filter.PageSize, 1, 500));

        var rows = await db.Database.SqlQuery<SalesJournalRow>($"""
            SELECT s.sold_at::date AS "Date", s.document_number AS "DocumentNumber",
                   cu.name AS "Customer", u.full_name AS "Salesperson", s.sale_type AS "SaleType",
                   s.subtotal AS "Subtotal", s.discount_total AS "Discount", s.tax_total AS "Tax",
                   s.total AS "Total", s.amount_paid AS "Paid"
            FROM nexora.sale s
            LEFT JOIN nexora.customer cu ON cu.id = s.customer_id
            LEFT JOIN nexora.app_user u ON u.id = s.salesperson_user_id
            WHERE s.company_id = {Company} AND s.status IN ('completed','partially_refunded','refunded')
              AND s.sold_at::date BETWEEN {from} AND {to}
              AND ({filter.CustomerId}::uuid IS NULL OR s.customer_id = {filter.CustomerId})
              AND ({filter.SalespersonUserId}::uuid IS NULL OR s.salesperson_user_id = {filter.SalespersonUserId})
              AND ({filter.EstablishmentId}::uuid IS NULL OR s.establishment_id = {filter.EstablishmentId})
            ORDER BY s.sold_at
            OFFSET {(page - 1) * size} LIMIT {size}
            """).ToListAsync(ct);

        var totals = await db.Database.SqlQuery<JournalTotals>($"""
            SELECT COUNT(*)::bigint AS "Count", COALESCE(SUM(subtotal - discount_total),0) AS "Revenue",
                   COALESCE(SUM(tax_total),0) AS "Tax"
            FROM nexora.sale
            WHERE company_id = {Company} AND status IN ('completed','partially_refunded','refunded')
              AND sold_at::date BETWEEN {from} AND {to}
            """).FirstAsync(ct);

        var currency = await Currency(ct);
        return new SalesJournalResponse(from, to, currency, rows, R(totals.Revenue), R(totals.Tax), totals.Count);
    }

    public async Task<SalesBreakdownResponse> BreakdownAsync(string dimension, ReportFilter filter, CancellationToken ct = default)
    {
        var (from, to) = FilterPeriod.Resolve(filter, await ReportingClock.TodayAsync(db, ct));
        var currency = await Currency(ct);

        // labelExpr / groupExpr sont des fragments contrôlés (pas d'entrée utilisateur) ; la dimension
        // inconnue retombe sur « jour ».
        var (labelExpr, groupExpr, joinExpr) = dimension switch
        {
            "product" => ("p.name", "p.name", "JOIN nexora.product p ON p.id = sl.product_id"),
            "category" => ("COALESCE(cat.name,'(sans catégorie)')", "COALESCE(cat.name,'(sans catégorie)')",
                           "JOIN nexora.product p ON p.id = sl.product_id LEFT JOIN nexora.product_category cat ON cat.id = p.category_id"),
            "customer" => ("COALESCE(cu.name,'Anonyme')", "COALESCE(cu.name,'Anonyme')",
                           "LEFT JOIN nexora.customer cu ON cu.id = s.customer_id"),
            "salesperson" => ("COALESCE(u.full_name,'(inconnu)')", "COALESCE(u.full_name,'(inconnu)')",
                              "LEFT JOIN nexora.app_user u ON u.id = s.salesperson_user_id"),
            "payment_method" => ("COALESCE(pm.name,'Espèces')", "COALESCE(pm.name,'Espèces')",
                                 "JOIN nexora.sale_payment sp2 ON sp2.sale_id = s.id LEFT JOIN nexora.payment pay ON pay.id = sp2.payment_id LEFT JOIN nexora.payment_method pm ON pm.id = pay.payment_method_id"),
            "day" or _ => ("s.sold_at::date::text", "s.sold_at::date::text", ""),
        };

        var sql =
            $"SELECT {labelExpr} AS \"Label\", " +
            "SUM(sl.line_total - sl.tax_amount) AS \"Revenue\", " +
            "SUM(sl.line_total - sl.tax_amount - sl.unit_cost * sl.quantity) AS \"Margin\", " +
            "SUM(sl.quantity) AS \"Quantity\", COUNT(DISTINCT s.id)::int AS \"Transactions\" " +
            "FROM nexora.sale_line sl JOIN nexora.sale s ON s.id = sl.sale_id " +
            joinExpr + " " +
            "WHERE s.company_id = {0} AND s.status IN ('completed','partially_refunded','refunded') " +
            "AND s.sold_at::date BETWEEN {1} AND {2} " +
            $"GROUP BY {groupExpr} ORDER BY 2 DESC LIMIT 200";

        var raw = await db.Database.SqlQueryRaw<BreakdownRaw>(sql, Company, from, to).ToListAsync(ct);
        var rows = raw.Select(r => new SalesBreakdownRow(dimension, r.Label, R(r.Revenue), R(r.Margin),
            r.Revenue == 0 ? 0m : Math.Round(r.Margin / r.Revenue * 100m, 1), R(r.Quantity), r.Transactions)).ToList();
        return new SalesBreakdownResponse(dimension, from, to, currency, rows);
    }

    public async Task<IReadOnlyList<ProductRankRow>> TopProductsAsync(ReportFilter filter, int take, CancellationToken ct = default)
    {
        var (from, to) = FilterPeriod.Resolve(filter, await ReportingClock.TodayAsync(db, ct));
        return await db.Database.SqlQueryRaw<ProductRankRow>(
            "SELECT p.id AS \"ProductId\", p.sku AS \"Sku\", p.name AS \"Name\", SUM(sl.quantity) AS \"Quantity\", " +
            "SUM(sl.line_total - sl.tax_amount) AS \"Revenue\", " +
            "SUM(sl.line_total - sl.tax_amount - sl.unit_cost*sl.quantity) AS \"Margin\" " +
            "FROM nexora.sale_line sl JOIN nexora.sale s ON s.id=sl.sale_id JOIN nexora.product p ON p.id=sl.product_id " +
            "WHERE s.company_id={0} AND s.status IN ('completed','partially_refunded','refunded') AND s.sold_at::date BETWEEN {1} AND {2} " +
            "GROUP BY p.id,p.sku,p.name ORDER BY 5 DESC LIMIT {3}", Company, from, to, take).ToListAsync(ct);
    }

    private sealed record JournalTotals(long Count, decimal Revenue, decimal Tax);
    private sealed record BreakdownRaw(string Label, decimal Revenue, decimal Margin, decimal Quantity, int Transactions);
    private static decimal R(decimal v) => Math.Round(v, 2, MidpointRounding.ToEven);
    private Task<string> Currency(CancellationToken ct) => (from c in db.Companies.AsNoTracking()
        join cur in db.Currencies.AsNoTracking() on c.DefaultCurrencyId equals cur.Id
        where c.Id == Company select cur.Code).FirstAsync(ct);
}

public sealed class SqlStockReports(NexoraDbContext db, ITenantContext tenant) : IStockReports
{
    private Guid Company => tenant.ActiveCompanyId ?? throw new InvalidOperationException("Entreprise active requise.");

    public async Task<StockValuationResponse> ValuationAsync(ReportFilter filter, CancellationToken ct = default)
    {
        var currency = await (from c in db.Companies.AsNoTracking()
            join cur in db.Currencies.AsNoTracking() on c.DefaultCurrencyId equals cur.Id
            where c.Id == Company select cur.Code).FirstAsync(ct);
        var rows = await db.Database.SqlQuery<StockValuationRow>($"""
            SELECT p.id AS "ProductId", p.sku AS "Sku", p.name AS "Name",
                   SUM(sb.quantity_on_hand) AS "OnHand",
                   AVG(sb.average_cost) AS "AverageCost",
                   SUM(sb.quantity_on_hand * sb.average_cost) AS "Value"
            FROM nexora.stock_balance sb JOIN nexora.product p ON p.id = sb.product_id
            WHERE sb.company_id = {Company}
              AND ({filter.ProductId}::uuid IS NULL OR p.id = {filter.ProductId})
              AND ({filter.CategoryId}::uuid IS NULL OR p.category_id = {filter.CategoryId})
            GROUP BY p.id, p.sku, p.name HAVING SUM(sb.quantity_on_hand) <> 0
            ORDER BY 6 DESC
            """).ToListAsync(ct);
        return new StockValuationResponse(await ReportingClock.TodayAsync(db, ct), currency, rows,
            Math.Round(rows.Sum(r => r.Value), 2));
    }

    public async Task<IReadOnlyList<StockAlertRow>> RupturesAsync(ReportFilter filter, CancellationToken ct = default)
        => await db.Database.SqlQuery<StockAlertRow>($"""
            SELECT p.id AS "ProductId", p.sku AS "Sku", p.name AS "Name", sb.inventory_location_id AS "LocationId",
                   sb.quantity_on_hand AS "OnHand", COALESCE(r.reorder_point, 0) AS "ReorderPoint"
            FROM nexora.stock_balance sb JOIN nexora.product p ON p.id = sb.product_id
            LEFT JOIN nexora.product_reorder_rule r ON r.product_id = p.id
            WHERE sb.company_id = {Company} AND (sb.quantity_on_hand - sb.quantity_reserved) <= 0
            ORDER BY sb.quantity_on_hand ASC
            """).ToListAsync(ct);

    public async Task<IReadOnlyList<ProductRankRow>> DormantAsync(ReportFilter filter, int days, CancellationToken ct = default)
        => await db.Database.SqlQuery<ProductRankRow>($"""
            SELECT p.id AS "ProductId", p.sku AS "Sku", p.name AS "Name",
                   COALESCE(SUM(sb.quantity_on_hand),0) AS "Quantity",
                   0::numeric AS "Revenue", 0::numeric AS "Margin"
            FROM nexora.product p
            JOIN nexora.stock_balance sb ON sb.product_id = p.id
            WHERE p.company_id = {Company} AND sb.quantity_on_hand > 0
              AND NOT EXISTS (
                  SELECT 1 FROM nexora.stock_movement m
                  WHERE m.product_id = p.id AND m.movement_type = 'sale'
                    AND m.occurred_at >= now() - make_interval(days => {days}))
            GROUP BY p.id, p.sku, p.name
            ORDER BY 4 DESC
            """).ToListAsync(ct);
}
