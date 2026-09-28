using Microsoft.EntityFrameworkCore;
using Nexora.Application.Abstractions;
using Nexora.Application.Sales;
using Nexora.Contracts.Sales;
using Nexora.Domain.Sales;
using Nexora.Infrastructure.Persistence;

namespace Nexora.Infrastructure.Sales;

public sealed class EfOrderStore(NexoraDbContext db, ITenantContext tenant) : IOrderStore
{
    public Task<Guid> ResolveCurrencyIdAsync(string code, CancellationToken ct = default)
        => db.Currencies.AsNoTracking().Where(c => c.Code == code).Select(c => c.Id).FirstAsync(ct);

    public Task<string> CurrencyCodeAsync(Guid currencyId, CancellationToken ct = default)
        => db.Currencies.AsNoTracking().Where(c => c.Id == currencyId).Select(c => c.Code).FirstAsync(ct);

    public Task<string> NextNumberAsync(string documentType, CancellationToken ct = default)
        => SalesHelpers.NextNumber(db, tenant, documentType, ct);

    public async Task AddOrderAsync(SalesOrder order, CancellationToken ct = default)
        => await db.SalesOrders.AddAsync(order, ct);

    public Task<SalesOrder?> GetOrderAsync(Guid id, CancellationToken ct = default)
        => db.SalesOrders.Include(o => o.Lines).FirstOrDefaultAsync(o => o.Id == id, ct);

    public async Task<IReadOnlyList<Guid>> SaleIdsForOrderAsync(Guid orderId, CancellationToken ct = default)
        => await db.Sales.AsNoTracking().Where(s => s.SalesOrderId == orderId).Select(s => s.Id).ToListAsync(ct);

    public async Task LinkSaleToOrderAsync(Guid saleId, Guid orderId, CancellationToken ct = default)
        => await db.Sales.Where(s => s.Id == saleId)
            .ExecuteUpdateAsync(u => u.SetProperty(s => s.SalesOrderId, orderId), ct);

    public async Task<bool> ProductRequiresStockAsync(Guid productId, CancellationToken ct = default)
        => await db.Products.AsNoTracking().Where(p => p.Id == productId)
            .Select(p => p.ProductType == "stockable" || p.ProductType == "weight").FirstOrDefaultAsync(ct);

    private Guid Company => tenant.ActiveCompanyId ?? throw new InvalidOperationException("Aucune entreprise active.");

    public async Task<Guid?> CurrencyIdOrNullAsync(string code, CancellationToken ct = default)
        => await db.Currencies.AsNoTracking().Where(c => c.Code == code).Select(c => (Guid?)c.Id).FirstOrDefaultAsync(ct);

    public Task<bool> LocationExistsAsync(Guid locationId, CancellationToken ct = default)
        => db.Database.SqlQuery<bool>($"""
            SELECT EXISTS(SELECT 1 FROM nexora.inventory_location
                          WHERE id = {locationId} AND company_id = {Company} AND status = 'active') AS "Value"
            """).FirstAsync(ct);

    public Task<string?> CustomerStatusAsync(Guid customerId, CancellationToken ct = default)
        => db.Database.SqlQuery<string?>($"""
            SELECT status AS "Value" FROM nexora.customer WHERE id = {customerId} AND company_id = {Company}
            """).FirstOrDefaultAsync(ct);

    public async Task SyncEstablishmentAsync(Guid orderId, CancellationToken ct = default)
        => await db.Database.ExecuteSqlAsync($"""
            UPDATE nexora.sales_order SET establishment_id = COALESCE(nexora.location_establishment(inventory_location_id), establishment_id)
            WHERE id = {orderId} AND company_id = {Company}
            """, ct);

    public Task RemoveLinesAsync(SalesOrder order, CancellationToken ct = default)
    {
        db.Set<SalesOrderLine>().RemoveRange(order.Lines);
        order.Lines.Clear();
        return Task.CompletedTask;
    }

    public Task AddLinesAsync(IEnumerable<SalesOrderLine> lines, CancellationToken ct = default)
    {
        db.Set<SalesOrderLine>().AddRange(lines);
        return Task.CompletedTask;
    }
}

internal sealed record OrderExtraRow(string? CustomerName, string? LocationName, decimal DepositAvailable);

/// <summary>Lectures ventes & commandes (Phase 25 §9, §43).</summary>
public sealed class SqlSalesQueries(NexoraDbContext db, ITenantContext tenant, IPermissionService permissions) : ISalesQueries
{
    private Guid Company => tenant.ActiveCompanyId
        ?? throw new InvalidOperationException("Aucune entreprise active.");

    public async Task<SaleDto?> GetSaleAsync(Guid saleId, CancellationToken ct = default)
    {
        var sale = await db.Sales.AsNoTracking().Include(s => s.Lines)
            .FirstOrDefaultAsync(s => s.Id == saleId && s.CompanyId == Company, ct);
        if (sale is null) return null;

        var currency = await db.Currencies.AsNoTracking().Where(c => c.Id == sale.CurrencyId).Select(c => c.Code).FirstAsync(ct);
        var lotNumbers = await db.Lots.AsNoTracking()
            .Where(l => sale.Lines.Select(x => x.LotId).Contains(l.Id))
            .ToDictionaryAsync(l => l.Id, l => l.LotNumber, ct);

        var payments = await db.Database.SqlQuery<SalePaymentDto>($"""
            SELECT p.id AS "PaymentId", COALESCE(pm.type, 'other') AS "Method",
                   sp.amount AS "Amount", p.received_at AS "ReceivedAt"
            FROM nexora.sale_payment sp
            JOIN nexora.payment p ON p.id = sp.payment_id
            LEFT JOIN nexora.payment_method pm ON pm.id = p.payment_method_id
            WHERE sp.sale_id = {saleId}
            ORDER BY p.received_at
            """).ToListAsync(ct);

        var showCost = (await permissions.CurrentPermissionsAsync(tenant, ct)).Contains(Nexora.Application.Security.Permissions.Catalog.ViewCost);
        var lines = sale.Lines.Select(l => new SaleLineDto(
            l.Id, l.ProductId, l.Description, l.Quantity, l.UnitPrice, l.Discount, l.TaxAmount, l.LineTotal,
            showCost ? l.UnitCost : null, l.LotId is { } lid && lotNumbers.TryGetValue(lid, out var ln) ? ln : null)).ToList();

        return new SaleDto(
            sale.Id, sale.DocumentNumber, sale.Status, sale.SaleTypeValue, sale.CustomerId, currency, sale.SoldAt,
            sale.Subtotal, sale.DiscountTotal, sale.TaxTotal, sale.Total, sale.AmountPaid,
            Math.Max(0m, sale.Total - sale.AmountPaid), sale.ChangeGiven, sale.SalesOrderId, sale.CashSessionId,
            lines, payments);
    }

    public async Task<(Guid? CustomerId, Guid? ReceivableId, string Currency)> SaleSettlementTargetAsync(Guid saleId, CancellationToken ct = default)
    {
        var sale = await db.Sales.AsNoTracking().Where(s => s.Id == saleId && s.CompanyId == Company)
            .Select(s => new { s.CustomerId, s.CurrencyId }).FirstOrDefaultAsync(ct);
        if (sale is null) return (null, null, "");
        var currency = await db.Currencies.AsNoTracking().Where(c => c.Id == sale.CurrencyId).Select(c => c.Code).FirstAsync(ct);
        var receivable = await db.Receivables.AsNoTracking()
            .Where(r => r.SaleId == saleId && (r.Status == "OPEN" || r.Status == "PARTIALLY_PAID" || r.Status == "OVERDUE"))
            .Select(r => (Guid?)r.Id).FirstOrDefaultAsync(ct);
        return (sale.CustomerId, receivable, currency);
    }

    public async Task<IReadOnlyList<OrderSummaryDto>> ListOrdersAsync(string? status, int limit, CancellationToken ct = default)
    {
        limit = Math.Clamp(limit, 1, 200);
        return await db.Database.SqlQuery<OrderSummaryDto>($"""
            SELECT o.id AS "Id", o.document_number AS "DocumentNumber", o.order_type AS "OrderType",
                   o.status AS "Status", c.name AS "CustomerName", cur.code AS "Currency",
                   o.total AS "Total", o.deposit_amount AS "DepositAmount",
                   GREATEST(o.total - o.deposit_amount, 0) AS "RemainingBalance",
                   o.expected_at AS "ExpectedAt", o.created_at AS "CreatedAt"
            FROM nexora.sales_order o
            JOIN nexora.currency cur ON cur.id = o.currency_id
            LEFT JOIN nexora.customer c ON c.id = o.customer_id
            WHERE o.company_id = {Company} AND ({status}::text IS NULL OR o.status = {status})
            ORDER BY o.created_at DESC
            LIMIT {limit}
            """).ToListAsync(ct);
    }

    public async Task<OrderDto?> GetOrderAsync(Guid orderId, CancellationToken ct = default)
    {
        var order = await db.SalesOrders.AsNoTracking().Include(o => o.Lines)
            .FirstOrDefaultAsync(o => o.Id == orderId && o.CompanyId == Company, ct);
        if (order is null) return null;

        var currency = await db.Currencies.AsNoTracking().Where(c => c.Id == order.CurrencyId).Select(c => c.Code).FirstAsync(ct);
        var saleIds = await db.Sales.AsNoTracking().Where(s => s.SalesOrderId == orderId).Select(s => s.Id).ToListAsync(ct);
        var extra = await db.Database.SqlQuery<OrderExtraRow>($"""
            SELECT c.name AS "CustomerName", loc.name AS "LocationName",
                   COALESCE((SELECT SUM(m.remaining_amount) FROM nexora.customer_credit_memo m
                             WHERE m.sales_order_id = o.id AND m.source_type = 'order_deposit'
                               AND m.status IN ('available', 'partially_used')), 0) AS "DepositAvailable"
            FROM nexora.sales_order o
            LEFT JOIN nexora.customer c ON c.id = o.customer_id
            LEFT JOIN nexora.inventory_location loc ON loc.id = o.inventory_location_id
            WHERE o.id = {orderId} AND o.company_id = {Company}
            """).FirstAsync(ct);
        var sales = await db.Database.SqlQuery<OrderSaleRefDto>($"""
            SELECT s.id AS "Id", s.document_number AS "DocumentNumber", s.sold_at AS "SoldAt", s.total AS "Total",
                   COALESCE((SELECT SUM(r.remaining_amount) FROM nexora.customer_receivable r
                             WHERE r.sale_id = s.id AND r.status IN ('OPEN','PARTIALLY_PAID','OVERDUE')), 0) AS "AmountDue",
                   s.status AS "Status"
            FROM nexora.sale s WHERE s.sales_order_id = {orderId} AND s.company_id = {Company}
            ORDER BY s.sold_at
            """).ToListAsync(ct);

        return new OrderDto(
            order.Id, order.DocumentNumber, order.OrderType, order.Status, order.CustomerId, currency,
            order.Subtotal, order.DiscountTotal, order.TaxTotal, order.Total, order.DepositAmount,
            order.RemainingBalance, order.OnCredit, order.ExpectedAt, order.DueDate, order.DeliveryAddress, order.Notes,
            order.ReservedAt, order.FulfilledAt,
            order.Lines.OrderBy(l => l.Description).Select(l => new OrderLineDto(
                l.Id, l.ProductId, l.Description, l.Quantity, l.QuantityFulfilled, l.UnitPrice, l.Discount,
                l.TaxAmount, l.LineTotal)).ToList(),
            saleIds, extra.CustomerName, order.InventoryLocationId, extra.LocationName, extra.DepositAvailable, sales,
            order.CancelledReason, order.CreatedAt);
    }
}
