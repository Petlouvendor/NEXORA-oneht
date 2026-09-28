using Microsoft.EntityFrameworkCore;
using Nexora.Application.Abstractions;
using Nexora.Application.Purchasing;
using Nexora.Contracts.Purchasing;
using Nexora.Domain.Common;
using Nexora.Infrastructure.Persistence;

namespace Nexora.Infrastructure.Purchasing;

/// <summary>Lectures du cycle d'achat (finalisation produit, phase 3). Tous les montants viennent de la base.</summary>
public sealed class SqlPurchasingDocumentQueries(NexoraDbContext db, ITenantContext tenant) : IPurchasingDocumentQueries
{
    private Guid Company => tenant.ActiveCompanyId ?? throw new InvalidOperationException("Aucune entreprise active.");

    private sealed record OrderHeader(
        Guid Id, string DocumentNumber, string Status, Guid SupplierId, string SupplierName, Guid? WarehouseId,
        string Currency, DateOnly OrderDate, DateOnly? ExpectedDate, string? Notes,
        decimal Subtotal, decimal TaxTotal, decimal Total, string? CreatedByName);

    public async Task<PurchaseOrderDetailDto?> OrderAsync(Guid id, CancellationToken ct = default)
    {
        var h = await db.Database.SqlQuery<OrderHeader>($"""
            SELECT o.id AS "Id", o.document_number AS "DocumentNumber", o.status AS "Status", o.supplier_id AS "SupplierId",
                   s.name AS "SupplierName", o.warehouse_id AS "WarehouseId", cur.code AS "Currency",
                   o.order_date AS "OrderDate", o.expected_date AS "ExpectedDate", o.notes AS "Notes",
                   o.subtotal AS "Subtotal", o.tax_total AS "TaxTotal", o.total AS "Total",
                   COALESCE(u.full_name, u.email::text) AS "CreatedByName"
            FROM nexora.purchase_order o
            JOIN nexora.supplier s ON s.id = o.supplier_id
            JOIN nexora.currency cur ON cur.id = o.currency_id
            LEFT JOIN nexora.app_user u ON u.id = o.created_by
            WHERE o.id = {id} AND o.company_id = {Company}
            """).FirstOrDefaultAsync(ct);
        if (h is null) return null;
        var lines = await db.Database.SqlQuery<PurchaseOrderDetailLineDto>($"""
            SELECT l.id AS "Id", l.product_id AS "ProductId", p.sku AS "Sku", p.name AS "ProductName", bu.code AS "BaseUnitCode",
                   l.quantity_ordered AS "QuantityOrdered", l.quantity_received AS "QuantityReceived",
                   GREATEST(l.quantity_ordered - l.quantity_received, 0) AS "Remaining", l.unit_price AS "UnitPrice",
                   l.tax_id AS "TaxId", l.discount AS "Discount", l.line_total AS "LineTotal", l.tax_amount AS "TaxAmount",
                   l.purchase_unit_id AS "PurchaseUnitId", pu.code AS "PurchaseUnitCode", l.purchase_quantity AS "PurchaseQuantity"
            FROM nexora.purchase_order_line l
            JOIN nexora.product p ON p.id = l.product_id
            LEFT JOIN nexora.unit bu ON bu.id = p.unit_id
            LEFT JOIN nexora.unit pu ON pu.id = l.purchase_unit_id
            WHERE l.purchase_order_id = {id}
            ORDER BY p.name
            """).ToListAsync(ct);
        return new PurchaseOrderDetailDto(h.Id, h.DocumentNumber, h.Status, h.SupplierId, h.SupplierName, h.WarehouseId,
            h.Currency, h.OrderDate, h.ExpectedDate, h.Notes, h.Subtotal, h.TaxTotal, h.Total, h.CreatedByName,
            lines, await ReceiptRows(null, null, id, 100, ct), await InvoiceRows(null, null, null, id, 100, ct));
    }

    // ------------------------------------------------------------------ Réceptions

    public async Task<IReadOnlyList<GoodsReceiptSummaryDto>> ReceiptsAsync(Guid? supplierId, Guid? purchaseOrderId, int limit, CancellationToken ct = default)
        => await ReceiptRows(null, supplierId, purchaseOrderId, Math.Clamp(limit, 1, 500), ct);

    private async Task<List<GoodsReceiptSummaryDto>> ReceiptRows(Guid? id, Guid? supplierId, Guid? poId, int limit, CancellationToken ct)
        => await db.Database.SqlQuery<GoodsReceiptSummaryDto>($"""
            SELECT r.id AS "Id", r.document_number AS "DocumentNumber", r.supplier_id AS "SupplierId", s.name AS "SupplierName",
                   r.purchase_order_id AS "PurchaseOrderId", po.document_number AS "PurchaseOrderNumber",
                   r.inventory_location_id AS "LocationId", il.name AS "LocationName", r.received_at AS "ReceivedAt",
                   (SELECT COUNT(*)::int FROM nexora.goods_receipt_line l WHERE l.goods_receipt_id = r.id) AS "LineCount",
                   COALESCE((SELECT SUM(l.quantity_received) FROM nexora.goods_receipt_line l WHERE l.goods_receipt_id = r.id), 0) AS "QuantityAccepted",
                   COALESCE((SELECT SUM(l.quantity_rejected) FROM nexora.goods_receipt_line l WHERE l.goods_receipt_id = r.id), 0) AS "QuantityRejected",
                   COALESCE((SELECT ROUND(SUM(l.quantity_received * l.unit_cost), 2) FROM nexora.goods_receipt_line l WHERE l.goods_receipt_id = r.id), 0) AS "Value",
                   r.status AS "Status", r.notes AS "Notes", COALESCE(u.full_name, u.email::text) AS "CreatedByName"
            FROM nexora.goods_receipt r
            JOIN nexora.supplier s ON s.id = r.supplier_id
            LEFT JOIN nexora.purchase_order po ON po.id = r.purchase_order_id
            LEFT JOIN nexora.inventory_location il ON il.id = r.inventory_location_id
            LEFT JOIN nexora.app_user u ON u.id = r.created_by
            WHERE r.company_id = {Company}
              AND ({id}::uuid IS NULL OR r.id = {id})
              AND ({supplierId}::uuid IS NULL OR r.supplier_id = {supplierId})
              AND ({poId}::uuid IS NULL OR r.purchase_order_id = {poId})
            ORDER BY r.received_at DESC, r.document_number DESC
            LIMIT {limit}
            """).ToListAsync(ct);

    public async Task<GoodsReceiptDetailDto?> ReceiptAsync(Guid id, CancellationToken ct = default)
    {
        var h = (await ReceiptRows(id, null, null, 1, ct)).FirstOrDefault();
        if (h is null) return null;
        var lines = await db.Database.SqlQuery<GoodsReceiptLineDto>($"""
            SELECT l.id AS "Id", l.product_id AS "ProductId", p.sku AS "Sku", p.name AS "ProductName", u.code AS "UnitCode",
                   l.quantity_received AS "QuantityReceived", l.quantity_rejected AS "QuantityRejected",
                   l.rejection_reason AS "RejectionReason", l.unit_cost AS "UnitCost",
                   lt.lot_number AS "LotNumber", lt.expiration_date AS "ExpirationDate",
                   pu.code AS "PurchaseUnitCode", l.purchase_quantity AS "PurchaseQuantity",
                   l.purchase_order_line_id AS "PurchaseOrderLineId"
            FROM nexora.goods_receipt_line l
            JOIN nexora.product p ON p.id = l.product_id
            LEFT JOIN nexora.unit u ON u.id = p.unit_id
            LEFT JOIN nexora.unit pu ON pu.id = l.purchase_unit_id
            LEFT JOIN nexora.lot lt ON lt.id = l.lot_id
            WHERE l.goods_receipt_id = {id}
            ORDER BY p.name
            """).ToListAsync(ct);
        return new GoodsReceiptDetailDto(h, lines);
    }

    // ------------------------------------------------------------------ Factures

    public async Task<IReadOnlyList<SupplierInvoiceSummaryDto>> InvoicesAsync(Guid? supplierId, string? status, int limit, CancellationToken ct = default)
        => await InvoiceRows(null, supplierId, status, null, Math.Clamp(limit, 1, 500), ct);

    private async Task<List<SupplierInvoiceSummaryDto>> InvoiceRows(Guid? id, Guid? supplierId, string? status, Guid? poId, int limit, CancellationToken ct)
        => await db.Database.SqlQuery<SupplierInvoiceSummaryDto>($"""
            SELECT i.id AS "Id", i.document_number AS "DocumentNumber", i.supplier_reference AS "SupplierReference",
                   i.supplier_id AS "SupplierId", s.name AS "SupplierName", i.purchase_order_id AS "PurchaseOrderId",
                   po.document_number AS "PurchaseOrderNumber", i.goods_receipt_id AS "GoodsReceiptId",
                   i.invoice_date AS "InvoiceDate", i.due_date AS "DueDate", cur.code AS "Currency",
                   i.subtotal AS "Subtotal", i.tax_total AS "TaxTotal", i.total AS "Total", i.status AS "Status",
                   i.match_status AS "MatchStatus", i.payment_blocked AS "PaymentBlocked",
                   (SELECT SUM(p.remaining_amount) FROM nexora.supplier_payable p WHERE p.supplier_invoice_id = i.id) AS "RemainingAmount"
            FROM nexora.supplier_invoice i
            JOIN nexora.supplier s ON s.id = i.supplier_id
            JOIN nexora.currency cur ON cur.id = i.currency_id
            LEFT JOIN nexora.purchase_order po ON po.id = i.purchase_order_id
            WHERE i.company_id = {Company}
              AND ({id}::uuid IS NULL OR i.id = {id})
              AND ({supplierId}::uuid IS NULL OR i.supplier_id = {supplierId})
              AND ({status}::text IS NULL OR i.status = {status})
              AND ({poId}::uuid IS NULL OR i.purchase_order_id = {poId})
            ORDER BY i.invoice_date DESC, i.document_number DESC
            LIMIT {limit}
            """).ToListAsync(ct);

    public async Task<SupplierInvoiceDetailDto?> InvoiceAsync(Guid id, CancellationToken ct = default)
    {
        var h = (await InvoiceRows(id, null, null, null, 1, ct)).FirstOrDefault();
        if (h is null) return null;
        var lines = await db.Database.SqlQuery<SupplierInvoiceLineDto>($"""
            SELECT l.id AS "Id", l.product_id AS "ProductId", p.sku AS "Sku", l.description AS "Description",
                   l.quantity AS "Quantity", l.unit_price AS "UnitPrice", l.discount AS "Discount",
                   l.line_total AS "LineTotal", l.tax_id AS "TaxId"
            FROM nexora.supplier_invoice_line l
            LEFT JOIN nexora.product p ON p.id = l.product_id
            WHERE l.supplier_invoice_id = {id}
            ORDER BY l.created_at, l.id
            """).ToListAsync(ct);
        return new SupplierInvoiceDetailDto(h, lines);
    }

    // ------------------------------------------------------------------ Retours

    public async Task<IReadOnlyList<SupplierReturnSummaryDto>> ReturnsAsync(Guid? supplierId, int limit, CancellationToken ct = default)
        => await db.Database.SqlQuery<SupplierReturnSummaryDto>($"""
            SELECT r.id AS "Id", r.document_number AS "DocumentNumber", r.supplier_id AS "SupplierId", s.name AS "SupplierName",
                   il.name AS "LocationName", r.reason AS "Reason", r.returned_at AS "ReturnedAt", cur.code AS "Currency",
                   r.total AS "Total", r.status AS "Status",
                   (SELECT COUNT(*)::int FROM nexora.supplier_return_line l WHERE l.supplier_return_id = r.id) AS "LineCount"
            FROM nexora.supplier_return r
            JOIN nexora.supplier s ON s.id = r.supplier_id
            JOIN nexora.currency cur ON cur.id = r.currency_id
            LEFT JOIN nexora.inventory_location il ON il.id = r.warehouse_location_id
            WHERE r.company_id = {Company} AND ({supplierId}::uuid IS NULL OR r.supplier_id = {supplierId})
            ORDER BY r.returned_at DESC
            LIMIT {Math.Clamp(limit, 1, 500)}
            """).ToListAsync(ct);

    // ------------------------------------------------------------------ Fournisseur

    private sealed record SupplierRow(
        Guid SupplierId, string Name, string? ContactName, string? Notes, decimal CreditLimit,
        Guid? PaymentTermsId, string? PaymentTermsName, int? PaymentTermsDays, string Currency,
        decimal PurchasedLast12Months, decimal OpenPayables, decimal OverduePayables, int OpenOrders, DateTimeOffset? LastReceiptAt);

    public async Task<SupplierOverviewDto?> SupplierOverviewAsync(Guid supplierId, CancellationToken ct = default)
    {
        var s = await db.Database.SqlQuery<SupplierRow>($"""
            SELECT s.id AS "SupplierId", s.name AS "Name", s.contact_name AS "ContactName", s.notes AS "Notes",
                   s.credit_limit AS "CreditLimit", s.payment_terms_id AS "PaymentTermsId", t.name AS "PaymentTermsName",
                   t.days AS "PaymentTermsDays", cur.code AS "Currency",
                   COALESCE((SELECT SUM(i.total) FROM nexora.supplier_invoice i
                             WHERE i.supplier_id = s.id AND i.status <> 'cancelled' AND i.invoice_date >= current_date - 365), 0) AS "PurchasedLast12Months",
                   COALESCE((SELECT SUM(p.remaining_amount) FROM nexora.supplier_payable p
                             WHERE p.supplier_id = s.id AND p.status IN ('OPEN','PARTIALLY_PAID','OVERDUE')), 0) AS "OpenPayables",
                   COALESCE((SELECT SUM(p.remaining_amount) FROM nexora.supplier_payable p
                             WHERE p.supplier_id = s.id AND p.status IN ('OPEN','PARTIALLY_PAID','OVERDUE')
                               AND p.due_date < current_date), 0) AS "OverduePayables",
                   (SELECT COUNT(*)::int FROM nexora.purchase_order o
                     WHERE o.supplier_id = s.id AND o.status IN ('draft','submitted','approved','sent','partially_received')) AS "OpenOrders",
                   (SELECT max(r.received_at) FROM nexora.goods_receipt r WHERE r.supplier_id = s.id) AS "LastReceiptAt"
            FROM nexora.supplier s
            JOIN nexora.currency cur ON cur.id = s.default_currency_id
            LEFT JOIN nexora.supplier_payment_terms t ON t.id = s.payment_terms_id
            WHERE s.id = {supplierId} AND s.company_id = {Company}
            """).FirstOrDefaultAsync(ct);
        if (s is null) return null;
        var prices = await db.Database.SqlQuery<SupplierPriceHistoryDto>($"""
            SELECT h.product_id AS "ProductId", p.sku AS "Sku", p.name AS "ProductName", h.effective_at AS "EffectiveAt",
                   h.unit_cost AS "UnitCost", cur.code AS "Currency", r.document_number AS "DocumentNumber"
            FROM nexora.product_cost_history h
            JOIN nexora.goods_receipt r ON r.id = h.source_id AND h.source_type = 'goods_receipt'
            JOIN nexora.product p ON p.id = h.product_id
            JOIN nexora.currency cur ON cur.id = h.currency_id
            WHERE r.supplier_id = {supplierId} AND h.company_id = {Company}
            ORDER BY h.effective_at DESC
            LIMIT 100
            """).ToListAsync(ct);
        return new SupplierOverviewDto(s.SupplierId, s.Name, s.ContactName, s.Notes, s.CreditLimit, s.PaymentTermsId,
            s.PaymentTermsName, s.PaymentTermsDays, s.Currency, s.PurchasedLast12Months, s.OpenPayables, s.OverduePayables,
            s.OpenOrders, s.LastReceiptAt, prices);
    }

    public async Task<IReadOnlyList<SupplierPaymentTermsDto>> PaymentTermsAsync(CancellationToken ct = default)
        => await db.Database.SqlQuery<SupplierPaymentTermsDto>($"""
            SELECT id AS "Id", name AS "Name", days AS "Days", early_payment_discount_rate AS "EarlyPaymentDiscountRate",
                   early_payment_discount_days AS "EarlyPaymentDiscountDays", late_penalty_rate AS "LatePenaltyRate",
                   description AS "Description"
            FROM nexora.supplier_payment_terms WHERE company_id = {Company} ORDER BY days, name
            """).ToListAsync(ct);
}

/// <summary>Conditions de paiement (table supplier_payment_terms, V005) et conditions commerciales du fournisseur (V081).</summary>
public sealed class SqlSupplierTermsService(NexoraDbContext db, ITenantContext tenant, IUnitOfWork uow) : ISupplierTermsService
{
    private Guid Company => tenant.ActiveCompanyId ?? throw new InvalidOperationException("Aucune entreprise active.");

    public async Task<Result<Guid>> CreatePaymentTermsAsync(SavePaymentTermsRequest r, CancellationToken ct = default)
    {
        var name = (r.Name ?? "").Trim();
        if (name.Length == 0) return Error.Validation("purchasing.terms_name", "Le nom des conditions est obligatoire.");
        if (r.Days < 0 || r.EarlyPaymentDiscountDays < 0)
            return Error.Validation("purchasing.terms_days", "Les délais ne peuvent être négatifs.");
        if (r.EarlyPaymentDiscountRate is < 0 or > 100 || r.LatePenaltyRate is < 0 or > 100)
            return Error.Validation("purchasing.terms_rate", "Les taux doivent être compris entre 0 et 100 %.");
        if (r.EarlyPaymentDiscountDays > r.Days)
            return Error.Validation("purchasing.terms_days", "Le délai d'escompte ne peut dépasser le délai de paiement.");
        var exists = await db.Database.SqlQuery<bool>($"""
            SELECT EXISTS(SELECT 1 FROM nexora.supplier_payment_terms WHERE company_id = {Company} AND lower(name) = lower({name})) AS "Value"
            """).FirstAsync(ct);
        if (exists) return Error.Conflict("purchasing.terms_exists", $"Les conditions « {name} » existent déjà.");
        var id = Guid.NewGuid();
        await db.Database.ExecuteSqlAsync($"""
            INSERT INTO nexora.supplier_payment_terms
              (id, company_id, name, days, early_payment_discount_rate, early_payment_discount_days, late_penalty_rate, description)
            VALUES ({id}, {Company}, {name}, {r.Days}, {r.EarlyPaymentDiscountRate}, {r.EarlyPaymentDiscountDays},
                    {r.LatePenaltyRate}, {(string.IsNullOrWhiteSpace(r.Description) ? null : r.Description.Trim())})
            """, ct);
        await uow.SaveChangesAsync(ct);
        return Result.Success(id);
    }

    public async Task<Result> SetSupplierTermsAsync(Guid supplierId, SupplierTermsRequest r, CancellationToken ct = default)
    {
        if (r.CreditLimit < 0) return Error.Validation("purchasing.credit_limit", "La limite de crédit ne peut être négative.");
        if (r.PaymentTermsId is { } tid)
        {
            var ok = await db.Database.SqlQuery<bool>($"""
                SELECT EXISTS(SELECT 1 FROM nexora.supplier_payment_terms WHERE id = {tid} AND company_id = {Company}) AS "Value"
                """).FirstAsync(ct);
            if (!ok) return Error.Validation("purchasing.terms_unknown", "Conditions de paiement inconnues.");
        }
        var n = await db.Database.ExecuteSqlAsync($"""
            UPDATE nexora.supplier
               SET credit_limit = {r.CreditLimit}, payment_terms_id = {r.PaymentTermsId},
                   contact_name = {(string.IsNullOrWhiteSpace(r.ContactName) ? null : r.ContactName.Trim())},
                   notes = {(string.IsNullOrWhiteSpace(r.Notes) ? null : r.Notes.Trim())}, updated_at = now()
             WHERE id = {supplierId} AND company_id = {Company}
            """, ct);
        if (n == 0) return Error.NotFound("purchasing.supplier_not_found", "Fournisseur introuvable.");
        await uow.SaveChangesAsync(ct);
        return Result.Success();
    }
}
