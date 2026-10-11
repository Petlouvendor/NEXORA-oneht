using Microsoft.EntityFrameworkCore;
using Nexora.Application.Abstractions;
using Nexora.Application.Purchasing;
using Nexora.Contracts.Purchasing;
using Nexora.Infrastructure.Persistence;
using Nexora.Domain.Common;

namespace Nexora.Infrastructure.Purchasing;

/// <summary>Lectures achats (Phase 26 §24, §36, §46).</summary>
public sealed class SqlPurchasingQueries(NexoraDbContext db, ITenantContext tenant, IBusinessCalendar calendar) : IPurchasingQueries
{
    private Guid Company => tenant.ActiveCompanyId
        ?? throw TenantErrors.ActiveCompanyRequired();

    private sealed record PayableRow(
        Guid Id, Guid? SupplierInvoiceId, string? InvoiceNumber, string? SupplierReference,
        Guid SupplierId, string SupplierName, string Currency,
        decimal OriginalAmount, decimal PaidAmount, decimal RemainingAmount,
        DateOnly IssueDate, DateOnly? DueDate, string Status, bool PaymentBlocked, string? BlockReason);

    public async Task<IReadOnlyList<PayableDto>> ListPayablesAsync(Guid? supplierId, bool onlyOpen, int limit, CancellationToken ct = default)
    {
        limit = Math.Clamp(limit, 1, 500);
        var today = calendar.Today;
        var rows = await db.Database.SqlQuery<PayableRow>($"""
            SELECT p.id AS "Id", p.supplier_invoice_id AS "SupplierInvoiceId",
                   si.document_number AS "InvoiceNumber", si.supplier_reference AS "SupplierReference",
                   p.supplier_id AS "SupplierId", s.name AS "SupplierName", cur.code AS "Currency",
                   p.original_amount AS "OriginalAmount", p.paid_amount AS "PaidAmount",
                   p.remaining_amount AS "RemainingAmount", p.issue_date AS "IssueDate",
                   p.due_date AS "DueDate", p.status AS "Status",
                   COALESCE(si.payment_blocked, false) AS "PaymentBlocked", si.payment_block_reason AS "BlockReason"
            FROM nexora.supplier_payable p
            JOIN nexora.supplier s ON s.id = p.supplier_id
            JOIN nexora.currency cur ON cur.id = p.currency_id
            LEFT JOIN nexora.supplier_invoice si ON si.id = p.supplier_invoice_id
            WHERE p.company_id = {Company}
              AND ({supplierId}::uuid IS NULL OR p.supplier_id = {supplierId})
              AND (NOT {onlyOpen} OR p.status IN ('OPEN','PARTIALLY_PAID','OVERDUE'))
            ORDER BY p.due_date NULLS LAST, p.issue_date
            LIMIT {limit}
            """).ToListAsync(ct);

        return rows.Select(r => new PayableDto(
            r.Id, r.SupplierInvoiceId, r.InvoiceNumber, r.SupplierReference, r.SupplierId, r.SupplierName, r.Currency,
            r.OriginalAmount, r.PaidAmount, r.RemainingAmount, r.IssueDate, r.DueDate,
            r.DueDate is { } d && d < today && r.Status != "PAID" ? today.DayNumber - d.DayNumber : 0,
            r.Status, r.PaymentBlocked, r.BlockReason)).ToList();
    }

    private sealed record StmtRow(DateOnly Date, string DocumentType, string DocumentNumber, string? Reference, decimal Charge, decimal Payment);

    public async Task<SupplierStatementDto?> SupplierStatementAsync(Guid supplierId, DateOnly? from, DateOnly? to, CancellationToken ct = default)
    {
        var sup = await db.Database.SqlQuery<string>($"""
            SELECT name AS "Value" FROM nexora.supplier WHERE id = {supplierId} AND company_id = {Company}
            """).FirstOrDefaultAsync(ct);
        if (sup is null) return null;
        var currency = await db.Database.SqlQuery<string>($"""
            SELECT cur.code AS "Value" FROM nexora.supplier s JOIN nexora.currency cur ON cur.id = s.default_currency_id
            WHERE s.id = {supplierId}
            """).FirstAsync(ct);
        var today = calendar.Today;

        var rows = await db.Database.SqlQuery<StmtRow>($"""
            SELECT si.invoice_date AS "Date", 'invoice' AS "DocumentType", si.document_number AS "DocumentNumber",
                   si.supplier_reference AS "Reference", si.total AS "Charge", 0::numeric AS "Payment"
            FROM nexora.supplier_invoice si
            WHERE si.company_id = {Company} AND si.supplier_id = {supplierId} AND si.status <> 'cancelled'
            UNION ALL
            SELECT pay.received_at::date AS "Date", 'payment' AS "DocumentType", pay.payment_number AS "DocumentNumber",
                   pay.reference AS "Reference", 0::numeric AS "Charge", pay.amount AS "Payment"
            FROM nexora.payment pay
            WHERE pay.company_id = {Company} AND pay.supplier_id = {supplierId} AND pay.direction = 'outbound'
            UNION ALL
            SELECT sr.returned_at::date AS "Date", 'return' AS "DocumentType", sr.document_number AS "DocumentNumber",
                   sr.reason AS "Reference", 0::numeric AS "Charge", sr.total AS "Payment"
            FROM nexora.supplier_return sr
            WHERE sr.company_id = {Company} AND sr.supplier_id = {supplierId} AND sr.status = 'posted'
            ORDER BY "Date"
            """).ToListAsync(ct);

        decimal opening = 0m, running = 0m;
        var lines = new List<SupplierStatementLineDto>();
        foreach (var r in rows.OrderBy(x => x.Date))
        {
            if (from is { } f && r.Date < f) { opening += r.Charge - r.Payment; running = opening; continue; }
            if (to is { } t && r.Date > t) continue;
            running += r.Charge - r.Payment;
            lines.Add(new SupplierStatementLineDto(r.Date, r.DocumentType, r.DocumentNumber, r.Reference,
                r.Charge, r.Payment, Math.Round(running, 2)));
        }

        var overdue = await db.Database.SqlQuery<decimal>($"""
            SELECT COALESCE(SUM(remaining_amount), 0) AS "Value" FROM nexora.supplier_payable
            WHERE company_id = {Company} AND supplier_id = {supplierId}
              AND status IN ('OPEN','PARTIALLY_PAID','OVERDUE') AND due_date < {today}
            """).FirstAsync(ct);

        return new SupplierStatementDto(supplierId, sup, currency, Math.Round(opening, 2), Math.Round(running, 2),
            Math.Round(overdue, 2), lines);
    }

    public async Task<IReadOnlyList<SupplierSummaryDto>> ListSuppliersAsync(CancellationToken ct = default)
        => await db.Database.SqlQuery<SupplierSummaryDto>($"""
            SELECT s.id AS "Id", s.supplier_number AS "SupplierNumber", s.name AS "Name", s.status AS "Status",
                   cur.code AS "Currency",
                   COALESCE((SELECT SUM(p.remaining_amount) FROM nexora.supplier_payable p
                             WHERE p.supplier_id = s.id AND p.status IN ('OPEN','PARTIALLY_PAID','OVERDUE')), 0) AS "OpenPayables"
            FROM nexora.supplier s
            JOIN nexora.currency cur ON cur.id = s.default_currency_id
            WHERE s.company_id = {Company}
            ORDER BY s.name
            """).ToListAsync(ct);

    public async Task<IReadOnlyList<PurchaseOrderSummaryDto>> ListPurchaseOrdersAsync(string? status, int limit, CancellationToken ct = default)
    {
        limit = Math.Clamp(limit, 1, 200);
        return await db.Database.SqlQuery<PurchaseOrderSummaryDto>($"""
            SELECT o.id AS "Id", o.document_number AS "DocumentNumber", o.status AS "Status",
                   o.supplier_id AS "SupplierId", s.name AS "SupplierName", cur.code AS "Currency",
                   o.order_date AS "OrderDate", o.total AS "Total",
                   COALESCE((SELECT CASE WHEN SUM(l.quantity_ordered) = 0 THEN 0
                                         ELSE ROUND(SUM(l.quantity_received) / SUM(l.quantity_ordered), 4) END
                             FROM nexora.purchase_order_line l WHERE l.purchase_order_id = o.id), 0) AS "ReceivedRatio"
            FROM nexora.purchase_order o
            JOIN nexora.supplier s ON s.id = o.supplier_id
            JOIN nexora.currency cur ON cur.id = o.currency_id
            WHERE o.company_id = {Company} AND ({status}::text IS NULL OR o.status = {status})
            ORDER BY o.order_date DESC, o.document_number DESC
            LIMIT {limit}
            """).ToListAsync(ct);
    }

    private sealed record PoLineRow(Guid Id, Guid ProductId, string Description, decimal QuantityOrdered,
        decimal QuantityReceived, decimal UnitPrice, decimal Discount, decimal LineTotal);

    public async Task<PurchaseOrderDto?> GetPurchaseOrderAsync(Guid orderId, CancellationToken ct = default)
    {
        var o = await db.PurchaseOrders.AsNoTracking().FirstOrDefaultAsync(x => x.Id == orderId && x.CompanyId == Company, ct);
        if (o is null) return null;
        var supplierName = await db.Database.SqlQuery<string>($"""
            SELECT name AS "Value" FROM nexora.supplier WHERE id = {o.SupplierId}
            """).FirstAsync(ct);
        var currency = await db.Database.SqlQuery<string>($"""
            SELECT code AS "Value" FROM nexora.currency WHERE id = {o.CurrencyId}
            """).FirstAsync(ct);
        var lines = await db.Database.SqlQuery<PoLineRow>($"""
            SELECT l.id AS "Id", l.product_id AS "ProductId", p.name AS "Description",
                   l.quantity_ordered AS "QuantityOrdered", l.quantity_received AS "QuantityReceived",
                   l.unit_price AS "UnitPrice", l.discount AS "Discount", l.line_total AS "LineTotal"
            FROM nexora.purchase_order_line l
            JOIN nexora.product p ON p.id = l.product_id
            WHERE l.purchase_order_id = {orderId}
            ORDER BY p.name
            """).ToListAsync(ct);

        return new PurchaseOrderDto(
            o.Id, o.DocumentNumber, o.Status, o.SupplierId, supplierName, currency, o.OrderDate, o.ExpectedDate,
            o.Subtotal, o.TaxTotal, o.Total,
            lines.Select(l => new PurchaseOrderLineDto(l.Id, l.ProductId, l.Description, l.QuantityOrdered,
                l.QuantityReceived, l.UnitPrice, l.Discount, l.LineTotal)).ToList());
    }
}
