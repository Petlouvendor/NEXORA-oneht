using Microsoft.EntityFrameworkCore;
using Nexora.Application.Abstractions;
using Nexora.Application.Catalog;
using Nexora.Application.Inventory;
using Nexora.Application.Purchasing;
using Nexora.Domain.Catalog;
using Nexora.Domain.Inventory;
using Nexora.Domain.Purchasing;
using Nexora.Infrastructure.Persistence;
using Nexora.Domain.Common;

namespace Nexora.Infrastructure.Inventory;

internal static class StoreHelpers
{
    public static Guid Company(ITenantContext t) => t.ActiveCompanyId
        ?? throw TenantErrors.ActiveCompanyRequired();

    public static async Task<Guid> CurrencyId(NexoraDbContext db, string code, CancellationToken ct)
        => await db.Currencies.AsNoTracking().Where(c => c.Code == code).Select(c => (Guid?)c.Id).FirstOrDefaultAsync(ct)
           ?? throw new InvalidOperationException($"Devise {code} inconnue.");

    public static async Task<string> NextNumber(NexoraDbContext db, ITenantContext t, string type, CancellationToken ct)
    {
        // Année locale de l'entreprise : next_document_number prend current_date (session au fuseau de l'entreprise).
        int? year = null;
        var rows = await db.Database
            .SqlQuery<string>($"""SELECT nexora.next_document_number({Company(t)}, {type}, {(Guid?)null}, {year}) AS "Value" """)
            .ToListAsync(ct);
        return rows[0];
    }
}

public sealed class EfCatalogStore(NexoraDbContext db, ITenantContext tenant) : ICatalogStore
{
    public Task<bool> SkuExistsAsync(Guid companyId, string sku, CancellationToken ct = default)
        => db.Products.AsNoTracking().AnyAsync(p => p.CompanyId == companyId && p.Sku == sku, ct);

    public Task<Guid> ResolveCurrencyIdAsync(string code, CancellationToken ct = default)
        => StoreHelpers.CurrencyId(db, code, ct);

    public Task<Product?> GetProductAsync(Guid id, CancellationToken ct = default)
        => db.Products.AsNoTracking().FirstOrDefaultAsync(p => p.Id == id, ct);

    public Task UpdateProductAsync(Product product, CancellationToken ct = default)
    {
        // L'entité lue par GetProductAsync est non suivie. Si le contexte suit déjà ce produit
        // (créé ou lu plus tôt dans la même requête), on recopie les valeurs sur l'instance suivie
        // plutôt que d'en rattacher une seconde — EF refuserait deux instances pour la même clé.
        var tracked = db.Products.Local.FirstOrDefault(p => p.Id == product.Id);
        if (tracked is null) db.Products.Update(product);
        else db.Entry(tracked).CurrentValues.SetValues(product);
        return Task.CompletedTask;
    }

    public async Task AddProductAsync(Product product, CancellationToken ct = default)
        => await db.Products.AddAsync(product, ct);

    public Task<bool> SupplierLinkExistsAsync(Guid productId, Guid supplierId, CancellationToken ct = default)
        => db.ProductSuppliers.AsNoTracking().AnyAsync(x => x.ProductId == productId && x.SupplierId == supplierId, ct);

    public async Task AddSupplierLinkAsync(ProductSupplier link, bool clearOtherPreferred, CancellationToken ct = default)
    {
        if (clearOtherPreferred)
            await db.ProductSuppliers.Where(x => x.ProductId == link.ProductId && x.IsPreferred)
                .ExecuteUpdateAsync(u => u.SetProperty(x => x.IsPreferred, false), ct);
        await db.ProductSuppliers.AddAsync(link, ct);
    }

    public async Task ClosePreviousPriceAsync(Guid productId, string priceType, Guid? customerGroupId, Guid? priceListId, decimal quantityMin, Guid currencyId, DateTimeOffset at, CancellationToken ct = default)
        => await db.ProductPrices
            .Where(p => p.ProductId == productId && p.PriceType == priceType
                        && p.CustomerGroupId == customerGroupId && p.PriceListId == priceListId
                        && p.QuantityMin == quantityMin && p.CurrencyId == currencyId && p.ValidTo == null)
            .ExecuteUpdateAsync(u => u.SetProperty(p => p.ValidTo, at), ct);

    public async Task AddPriceAsync(ProductPrice price, CancellationToken ct = default)
        => await db.ProductPrices.AddAsync(price, ct);

    public async Task AddBarcodeAsync(ProductBarcode barcode, bool clearOtherPrimary, CancellationToken ct = default)
    {
        if (clearOtherPrimary)
            await db.ProductBarcodes.Where(x => x.ProductId == barcode.ProductId && x.IsPrimary)
                .ExecuteUpdateAsync(u => u.SetProperty(x => x.IsPrimary, false), ct);
        await db.ProductBarcodes.AddAsync(barcode, ct);
    }

    public async Task<bool> BarcodeExistsAsync(Guid companyId, string barcode, CancellationToken ct = default)
        => await db.Products.AsNoTracking().AnyAsync(p => p.CompanyId == companyId && p.Barcode == barcode, ct)
           || await db.ProductBarcodes.AsNoTracking().AnyAsync(x => x.CompanyId == companyId && x.Barcode == barcode, ct);

    public Task<bool> PluExistsAsync(Guid companyId, string plu, CancellationToken ct = default)
        => db.Products.AsNoTracking().AnyAsync(p => p.CompanyId == companyId && p.PluCode == plu, ct);

    public Task<bool> CategoryCodeExistsAsync(Guid companyId, string code, CancellationToken ct = default)
        => db.Database.SqlQuery<bool>($"""
            SELECT EXISTS(SELECT 1 FROM nexora.product_category
                          WHERE company_id = {companyId} AND code = {code}) AS "Value"
            """).FirstAsync(ct);

    public Task<bool> CategoryExistsAsync(Guid companyId, Guid categoryId, CancellationToken ct = default)
        => db.Database.SqlQuery<bool>($"""
            SELECT EXISTS(SELECT 1 FROM nexora.product_category
                          WHERE company_id = {companyId} AND id = {categoryId}) AS "Value"
            """).FirstAsync(ct);

    public Task<bool> UnitExistsAsync(Guid companyId, Guid unitId, CancellationToken ct = default)
        => db.Database.SqlQuery<bool>($"""
            SELECT EXISTS(SELECT 1 FROM nexora.unit WHERE company_id = {companyId} AND id = {unitId}) AS "Value"
            """).FirstAsync(ct);

    public Task<bool> TaxExistsAsync(Guid companyId, Guid taxId, CancellationToken ct = default)
        => db.Database.SqlQuery<bool>($"""
            SELECT EXISTS(SELECT 1 FROM nexora.tax WHERE company_id = {companyId} AND id = {taxId}) AS "Value"
            """).FirstAsync(ct);

    public Task<bool> SupplierExistsAsync(Guid companyId, Guid supplierId, CancellationToken ct = default)
        => db.Database.SqlQuery<bool>($"""
            SELECT EXISTS(SELECT 1 FROM nexora.supplier WHERE company_id = {companyId} AND id = {supplierId}) AS "Value"
            """).FirstAsync(ct);

    public async Task CreateCategoryAsync(Guid id, Guid companyId, string name, string? code, Guid? parentId, CancellationToken ct = default)
        => await db.Database.ExecuteSqlAsync($"""
            INSERT INTO nexora.product_category (id, company_id, parent_id, name, code, status)
            VALUES ({id}, {companyId}, {parentId}, {name}, {code}, 'active')
            """, ct);

    public Task<bool> PriceListCodeExistsAsync(Guid companyId, string code, CancellationToken ct = default)
        => db.PriceLists.AsNoTracking().AnyAsync(l => l.CompanyId == companyId && l.Code == code, ct);

    public async Task AddPriceListAsync(PriceList list, CancellationToken ct = default)
        => await db.PriceLists.AddAsync(list, ct);

    public Task<string?> ProductStatusAsync(Guid productId, CancellationToken ct = default)
        => db.Products.AsNoTracking().Where(p => p.Id == productId).Select(p => (string?)p.Status).FirstOrDefaultAsync(ct);

    public async Task SetProductStatusAsync(Guid productId, string status, CancellationToken ct = default)
        => await db.Products.Where(p => p.Id == productId)
            .ExecuteUpdateAsync(u => u.SetProperty(p => p.Status, status), ct);

    public async Task UpsertReorderRuleAsync(ProductReorderRule rule, CancellationToken ct = default)
    {
        var existing = await db.ReorderRules.FirstOrDefaultAsync(
            r => r.ProductId == rule.ProductId && r.InventoryLocationId == rule.InventoryLocationId, ct);
        if (existing is null) { await db.ReorderRules.AddAsync(rule, ct); return; }
        existing.MinQuantity = rule.MinQuantity;
        existing.MaxQuantity = rule.MaxQuantity;
        existing.ReorderPoint = rule.ReorderPoint;
        existing.ReorderQuantity = rule.ReorderQuantity;
        existing.LeadTimeDays = rule.LeadTimeDays;
        existing.AutoOrder = rule.AutoOrder;
    }

    private Guid Company => StoreHelpers.Company(tenant);
}

public sealed class EfPurchasingStore(NexoraDbContext db, ITenantContext tenant) : IPurchasingStore
{
    private Guid Company => StoreHelpers.Company(tenant);

    public Task<Guid> ResolveCurrencyIdAsync(string code, CancellationToken ct = default) => StoreHelpers.CurrencyId(db, code, ct);
    public Task<string> NextNumberAsync(string documentType, CancellationToken ct = default) => StoreHelpers.NextNumber(db, tenant, documentType, ct);

    public async Task AddOrderAsync(PurchaseOrder order, CancellationToken ct = default) => await db.PurchaseOrders.AddAsync(order, ct);
    public async Task<Guid> CompanyCurrencyIdAsync(CancellationToken ct = default)
        => await db.Database.SqlQuery<Guid>($"""
            SELECT default_currency_id AS "Value" FROM nexora.company WHERE id = {tenant.ActiveCompanyId}
            """).FirstAsync(ct);

    public Task<PurchaseOrder?> GetOrderAsync(Guid id, CancellationToken ct = default)
        // Sans suivi : le statut et les quantités reçues sont mis à jour par ExecuteUpdate — une instance
        // déjà suivie dans le contexte serait périmée (lecture de contrôle avant réception).
        => db.PurchaseOrders.AsNoTracking().Include(o => o.Lines).FirstOrDefaultAsync(o => o.Id == id, ct);

    public Task<string?> OrderStatusAsync(Guid orderId, CancellationToken ct = default)
        => db.PurchaseOrders.AsNoTracking().Where(o => o.Id == orderId).Select(o => (string?)o.Status).FirstOrDefaultAsync(ct);

    public async Task SetOrderStatusAsync(Guid orderId, string status, CancellationToken ct = default)
        => await db.PurchaseOrders.Where(o => o.Id == orderId)
            .ExecuteUpdateAsync(u => u.SetProperty(o => o.Status, status), ct);

    public Task<bool> SupplierRefExistsAsync(Guid supplierId, string reference, CancellationToken ct = default)
        => db.Database.SqlQuery<bool>($"""
            SELECT EXISTS(SELECT 1 FROM nexora.supplier_invoice
              WHERE company_id = {Company} AND supplier_id = {supplierId}
                AND lower(supplier_reference) = lower({reference}) AND status <> 'cancelled') AS "Value"
            """).FirstAsync(ct);

    private sealed record InvLineRow(Guid? ProductId, string Description, decimal Quantity, decimal UnitPrice);
    private sealed record OrdLineRow(Guid ProductId, decimal QuantityOrdered, decimal UnitPrice);
    private sealed record RcvRow(Guid ProductId, decimal Received);
    private sealed record InvHeaderRow(string DocumentNumber, Guid? PurchaseOrderId, string MatchStatus);

    public async Task<InvoiceMatchData?> GetInvoiceMatchDataAsync(Guid invoiceId, CancellationToken ct = default)
    {
        var h = await db.Database.SqlQuery<InvHeaderRow>($"""
            SELECT document_number AS "DocumentNumber", purchase_order_id AS "PurchaseOrderId",
                   match_status AS "MatchStatus"
            FROM nexora.supplier_invoice WHERE id = {invoiceId} AND company_id = {Company}
            """).FirstOrDefaultAsync(ct);
        if (h is null) return null;

        var invLines = await db.Database.SqlQuery<InvLineRow>($"""
            SELECT product_id AS "ProductId", description AS "Description",
                   quantity AS "Quantity", unit_price AS "UnitPrice"
            FROM nexora.supplier_invoice_line WHERE supplier_invoice_id = {invoiceId}
            """).ToListAsync(ct);

        var ordLines = h.PurchaseOrderId is null ? new List<OrdLineRow>() : await db.Database.SqlQuery<OrdLineRow>($"""
            SELECT product_id AS "ProductId", quantity_ordered AS "QuantityOrdered", unit_price AS "UnitPrice"
            FROM nexora.purchase_order_line WHERE purchase_order_id = {h.PurchaseOrderId}
            """).ToListAsync(ct);

        var received = h.PurchaseOrderId is null ? new List<RcvRow>() : await db.Database.SqlQuery<RcvRow>($"""
            SELECT grl.product_id AS "ProductId", COALESCE(SUM(grl.quantity_received), 0) AS "Received"
            FROM nexora.goods_receipt_line grl
            JOIN nexora.goods_receipt gr ON gr.id = grl.goods_receipt_id
            WHERE gr.purchase_order_id = {h.PurchaseOrderId} AND gr.status = 'posted'
            GROUP BY grl.product_id
            """).ToListAsync(ct);

        return new InvoiceMatchData(
            invoiceId, h.DocumentNumber, h.PurchaseOrderId, h.MatchStatus,
            invLines.Select(l => new InvoiceMatchLine(l.ProductId, l.Description, l.Quantity, l.UnitPrice)).ToList(),
            ordLines.Select(l => new OrderMatchLine(l.ProductId, l.QuantityOrdered, l.UnitPrice)).ToList(),
            received.ToDictionary(r => r.ProductId, r => r.Received));
    }

    public async Task SetInvoiceMatchAsync(Guid invoiceId, string matchStatus, string detailJson,
        bool blocked, string? blockReason, Guid? matchedBy, CancellationToken ct = default)
        => await db.Database.ExecuteSqlAsync($"""
            UPDATE nexora.supplier_invoice
               SET match_status = {matchStatus}, match_detail = {detailJson}::jsonb,
                   matched_at = now(), matched_by = {matchedBy},
                   payment_blocked = {blocked}, payment_block_reason = {blockReason}
             WHERE id = {invoiceId} AND company_id = {Company}
            """, ct);

    public async Task<PayablePaymentInfo?> GetPayablePaymentInfoAsync(Guid payableId, CancellationToken ct = default)
        => await db.Database.SqlQuery<PayablePaymentInfo>($"""
            SELECT p.id AS "PayableId", p.supplier_id AS "SupplierId", cur.code AS "Currency",
                   p.remaining_amount AS "RemainingAmount", p.status AS "Status",
                   COALESCE(si.payment_blocked, false) AS "InvoicePaymentBlocked",
                   si.payment_block_reason AS "BlockReason"
            FROM nexora.supplier_payable p
            JOIN nexora.currency cur ON cur.id = p.currency_id
            LEFT JOIN nexora.supplier_invoice si ON si.id = p.supplier_invoice_id
            WHERE p.id = {payableId} AND p.company_id = {Company}
            """).FirstOrDefaultAsync(ct);

    public async Task<(Guid SupplierId, IReadOnlyDictionary<Guid, decimal> Returnable)?> ReceiptReturnableAsync(Guid receiptId, CancellationToken ct = default)
    {
        var supplier = await db.GoodsReceipts.AsNoTracking().Where(r => r.Id == receiptId && r.CompanyId == Company)
            .Select(r => (Guid?)r.SupplierId).FirstOrDefaultAsync(ct);
        if (supplier is null) return null;
        var rows = await db.Database.SqlQuery<ReturnableRow>($"""
            SELECT grl.product_id AS "ProductId",
                   SUM(grl.quantity_received)
                   - COALESCE((SELECT SUM(srl.quantity) FROM nexora.supplier_return_line srl
                                 JOIN nexora.supplier_return sr ON sr.id = srl.supplier_return_id
                                WHERE sr.goods_receipt_id = {receiptId} AND sr.status = 'posted'
                                  AND srl.product_id = grl.product_id), 0) AS "Quantity"
            FROM nexora.goods_receipt_line grl
            WHERE grl.goods_receipt_id = {receiptId} AND grl.company_id = {Company}
            GROUP BY grl.product_id
            """).ToListAsync(ct);
        return (supplier.Value, rows.ToDictionary(r => r.ProductId, r => r.Quantity));
    }

    private sealed record ReturnableRow(Guid ProductId, decimal Quantity);

    public async Task<string> CurrencyCodeAsync(Guid currencyId, CancellationToken ct = default)
        => await db.Database.SqlQuery<string>($"""
            SELECT code AS "Value" FROM nexora.currency WHERE id = {currencyId}
            """).FirstAsync(ct);

    public async Task<IReadOnlySet<Guid>> StockedProductsAsync(IReadOnlyCollection<Guid> productIds, CancellationToken ct = default)
    {
        if (productIds.Count == 0) return new HashSet<Guid>();
        var ids = productIds.Distinct().ToArray();
        var rows = await db.Database.SqlQuery<Guid>($"""
            SELECT id AS "Value" FROM nexora.product
            WHERE company_id = {Company} AND id = ANY({ids}) AND product_type NOT IN ('service', 'non_stock')
            """).ToListAsync(ct);
        return rows.ToHashSet();
    }

    public async Task<IReadOnlyDictionary<Guid, InvoiceStockBasis>> InvoiceStockBasisAsync(
        Guid? goodsReceiptId, Guid? purchaseOrderId, IReadOnlyCollection<Guid> productIds, CancellationToken ct = default)
    {
        if (productIds.Count == 0 || (goodsReceiptId is null && purchaseOrderId is null))
            return new Dictionary<Guid, InvoiceStockBasis>();
        var ids = productIds.Distinct().ToArray();
        // Réception « comptabilisée » = une écriture de réception (source goods_receipt) existe : depuis le 4081.
        var rows = await db.Database.SqlQuery<BasisRow>($"""
            SELECT l.product_id AS "ProductId",
                   SUM(l.quantity_received * l.unit_cost) FILTER (WHERE a.accrued)
                     / NULLIF(SUM(l.quantity_received) FILTER (WHERE a.accrued), 0) AS "AccruedUnitCost",
                   COALESCE(bool_or(NOT a.accrued), false) AS "HasLegacy"
            FROM nexora.goods_receipt_line l
            JOIN nexora.goods_receipt r ON r.id = l.goods_receipt_id
            CROSS JOIN LATERAL (SELECT EXISTS (
                SELECT 1 FROM nexora.journal_entry je
                WHERE je.company_id = r.company_id AND je.source_type = 'goods_receipt' AND je.source_id = r.id
                  AND je.status IN ('posted', 'reversed')) AS accrued) a
            WHERE r.company_id = {Company} AND r.status = 'posted' AND l.product_id = ANY({ids})
              AND (r.id = {goodsReceiptId} OR ({goodsReceiptId}::uuid IS NULL AND r.purchase_order_id = {purchaseOrderId}))
            GROUP BY l.product_id
            """).ToListAsync(ct);
        return rows.ToDictionary(r => r.ProductId, r => new InvoiceStockBasis(r.AccruedUnitCost, r.HasLegacy));
    }

    private sealed record BasisRow(Guid ProductId, decimal? AccruedUnitCost, bool HasLegacy);

    public async Task<(bool Accrued, bool Invoiced)> ReceiptAccountingStateAsync(Guid receiptId, CancellationToken ct = default)
    {
        var row = await db.Database.SqlQuery<ReceiptStateRow>($"""
            SELECT EXISTS (SELECT 1 FROM nexora.journal_entry je
                           WHERE je.company_id = r.company_id AND je.source_type = 'goods_receipt' AND je.source_id = r.id
                             AND je.status IN ('posted', 'reversed')) AS "Accrued",
                   EXISTS (SELECT 1 FROM nexora.supplier_invoice i
                           WHERE i.company_id = r.company_id AND i.status <> 'cancelled'
                             AND (i.goods_receipt_id = r.id
                                  OR (r.purchase_order_id IS NOT NULL AND i.purchase_order_id = r.purchase_order_id))) AS "Invoiced"
            FROM nexora.goods_receipt r
            WHERE r.id = {receiptId} AND r.company_id = {Company}
            """).FirstOrDefaultAsync(ct);
        return row is null ? (false, false) : (row.Accrued, row.Invoiced);
    }

    private sealed record ReceiptStateRow(bool Accrued, bool Invoiced);

    public async Task AddReceiptAsync(GoodsReceipt receipt, CancellationToken ct = default) => await db.GoodsReceipts.AddAsync(receipt, ct);

    public Task<Guid?> FindReceiptByOperationAsync(Guid operationUuid, CancellationToken ct = default)
        => db.StockMovements.AsNoTracking()
            .Where(m => m.SourceOperationUuid == operationUuid && m.ReferenceType == "goods_receipt")
            .Select(m => (Guid?)m.ReferenceId).FirstOrDefaultAsync(ct);

    public async Task BumpOrderLineReceivedAsync(Guid orderLineId, decimal quantity, CancellationToken ct = default)
        => await db.Set<PurchaseOrderLine>().Where(l => l.Id == orderLineId)
            .ExecuteUpdateAsync(u => u.SetProperty(l => l.QuantityReceived, l => l.QuantityReceived + quantity), ct);

    public async Task RefreshOrderStatusAsync(Guid orderId, CancellationToken ct = default)
    {
        var lines = await db.Set<PurchaseOrderLine>().AsNoTracking()
            .Where(l => l.PurchaseOrderId == orderId)
            .Select(l => new { l.QuantityOrdered, l.QuantityReceived }).ToListAsync(ct);
        var status = lines.All(l => l.QuantityReceived >= l.QuantityOrdered) ? "received"
            : lines.Any(l => l.QuantityReceived > 0) ? "partially_received" : null;
        // Rien d'imputé sur les lignes : le statut courant (ex. « sent ») est conservé, jamais rétrogradé.
        if (status is null) return;
        await db.PurchaseOrders.Where(o => o.Id == orderId)
            .ExecuteUpdateAsync(u => u.SetProperty(o => o.Status, status), ct);
    }

    public async Task<Guid> EnsureLotAsync(Guid productId, string lotNumber, DateOnly? expiration,
        Guid? supplierId, DateOnly? receivedAt, CancellationToken ct = default)
    {
        var existing = await db.Lots.AsNoTracking()
            .Where(l => l.ProductId == productId && l.LotNumber == lotNumber)
            .Select(l => (Guid?)l.Id).FirstOrDefaultAsync(ct);
        if (existing is { } id) return id;
        var lot = new Lot
        {
            Id = Guid.NewGuid(), CompanyId = Company, ProductId = productId,
            LotNumber = lotNumber, ExpirationDate = expiration, Status = "active",
            SupplierId = supplierId, ReceivedAt = receivedAt,
        };
        await db.Lots.AddAsync(lot, ct);
        return lot.Id;
    }

    public async Task<Guid> CreateSupplierInvoiceAsync(SupplierInvoiceRow r, IReadOnlyList<SupplierInvoiceLineRow> lines, CancellationToken ct = default)
    {
        await db.Database.ExecuteSqlAsync($"""
            INSERT INTO nexora.supplier_invoice
              (id, company_id, supplier_id, purchase_order_id, goods_receipt_id, document_number,
               supplier_reference, invoice_date, due_date, currency_id, subtotal, tax_total, total, status)
            VALUES ({r.Id}, {r.CompanyId}, {r.SupplierId}, {r.PurchaseOrderId}, {r.GoodsReceiptId}, {r.DocumentNumber},
                    {r.SupplierReference}, {r.InvoiceDate}, {r.DueDate}, {r.CurrencyId},
                    {r.Subtotal}, {r.TaxTotal}, {r.Total}, {r.Status})
            """, ct);
        foreach (var l in lines)
            await db.Database.ExecuteSqlAsync($"""
                INSERT INTO nexora.supplier_invoice_line
                  (id, supplier_invoice_id, company_id, product_id, description, quantity, unit_price, tax_id, discount, line_total)
                VALUES ({Guid.NewGuid()}, {r.Id}, {r.CompanyId}, {l.ProductId}, {l.Description},
                        {l.Quantity}, {l.UnitPrice}, {l.TaxId}, {l.Discount}, {l.LineTotal})
                """, ct);
        return r.Id;
    }

    public async Task AddCostHistoryAsync(ProductCostHistory history, CancellationToken ct = default)
        => await db.CostHistory.AddAsync(history, ct);

    public async Task AddSupplierReturnAsync(SupplierReturn ret, CancellationToken ct = default)
        => await db.SupplierReturns.AddAsync(ret, ct);

    public Task<Product?> GetProductAsync(Guid id, CancellationToken ct = default)
        => db.Products.AsNoTracking().FirstOrDefaultAsync(p => p.Id == id, ct);

    // --- Finalisation produit, phase 3 -------------------------------------------------

    public Task<Guid?> CurrencyIdOrNullAsync(string code, CancellationToken ct = default)
        => db.Currencies.AsNoTracking().Where(c => c.Code == code).Select(c => (Guid?)c.Id).FirstOrDefaultAsync(ct);

    public Task<SupplierPurchasingInfo?> SupplierInfoAsync(Guid supplierId, CancellationToken ct = default)
        => db.Database.SqlQuery<SupplierPurchasingInfo>($"""
            SELECT s.id AS "SupplierId", s.status AS "Status", t.days AS "PaymentTermsDays", s.credit_limit AS "CreditLimit"
            FROM nexora.supplier s
            LEFT JOIN nexora.supplier_payment_terms t ON t.id = s.payment_terms_id
            WHERE s.id = {supplierId} AND s.company_id = {Company}
            """).FirstOrDefaultAsync(ct);

    public Task<decimal?> TaxRateAsync(Guid taxId, CancellationToken ct = default)
        => db.Database.SqlQuery<decimal?>($"""
            SELECT rate AS "Value" FROM nexora.tax WHERE id = {taxId} AND company_id = {Company}
            """).FirstOrDefaultAsync(ct);

    public Task<bool> LocationExistsAsync(Guid locationId, CancellationToken ct = default)
        => db.Database.SqlQuery<bool>($"""
            SELECT EXISTS(SELECT 1 FROM nexora.inventory_location WHERE id = {locationId} AND company_id = {Company}) AS "Value"
            """).FirstAsync(ct);

    public async Task<Guid?> FindInvoiceByOperationAsync(Guid operationUuid, CancellationToken ct = default)
        => await db.Database.SqlQuery<Guid?>($"""
            SELECT je.source_id AS "Value" FROM nexora.journal_entry je
            WHERE je.source_operation_uuid = {operationUuid} AND je.company_id = {Company}
            LIMIT 1
            """).FirstOrDefaultAsync(ct);

    public async Task<PurchaseOrder?> GetOrderForUpdateAsync(Guid id, CancellationToken ct = default)
    {
        var order = await db.PurchaseOrders.Include(o => o.Lines).FirstOrDefaultAsync(o => o.Id == id && o.CompanyId == Company, ct);
        // Les transitions de statut passent par ExecuteUpdate : une instance déjà suivie peut porter un statut périmé.
        if (order is not null) await db.Entry(order).ReloadAsync(ct);
        return order;
    }

    public Task ReplaceOrderLinesAsync(PurchaseOrder order, IReadOnlyList<PurchaseOrderLine> lines, CancellationToken ct = default)
    {
        db.Set<PurchaseOrderLine>().RemoveRange(order.Lines);
        order.Lines.Clear();
        // Clé déjà fixée côté application : sans Add explicite, EF traiterait ces lignes comme « modifiées ».
        // L'ajout raccroche déjà la ligne à la commande suivie (fixup) : ne pas l'ajouter deux fois.
        db.Set<PurchaseOrderLine>().AddRange(lines);
        foreach (var l in lines) if (!order.Lines.Contains(l)) order.Lines.Add(l);
        return Task.CompletedTask;
    }

    public async Task SetOrderStatusIfAsync(Guid orderId, string fromStatus, string toStatus, CancellationToken ct = default)
        => await db.PurchaseOrders.Where(o => o.Id == orderId && o.Status == fromStatus)
            .ExecuteUpdateAsync(u => u.SetProperty(o => o.Status, toStatus), ct);
}

public sealed class EfInventoryOpsStore(NexoraDbContext db, ITenantContext tenant) : IInventoryOpsStore
{
    public Task<string> NextNumberAsync(string documentType, CancellationToken ct = default) => StoreHelpers.NextNumber(db, tenant, documentType, ct);

    public async Task AddTransferAsync(StockTransfer transfer, CancellationToken ct = default) => await db.StockTransfers.AddAsync(transfer, ct);
    public Task<StockTransfer?> GetTransferAsync(Guid id, CancellationToken ct = default)
        => db.StockTransfers.Include(t => t.Lines).FirstOrDefaultAsync(t => t.Id == id, ct);

    public async Task AddCountAsync(InventoryCount count, CancellationToken ct = default) => await db.InventoryCounts.AddAsync(count, ct);
    public Task<InventoryCount?> GetCountAsync(Guid id, CancellationToken ct = default)
        => db.InventoryCounts.Include(c => c.Lines).FirstOrDefaultAsync(c => c.Id == id, ct);

    public async Task<decimal> SystemQuantityAsync(Guid productId, Guid locationId, CancellationToken ct = default)
        => await db.StockBalances.AsNoTracking()
            .Where(s => s.ProductId == productId && s.InventoryLocationId == locationId)
            .Select(s => (decimal?)s.QuantityOnHand).FirstOrDefaultAsync(ct) ?? 0m;

    public async Task<decimal> MovementsBetweenAsync(Guid productId, Guid locationId, Guid? lotId, DateTimeOffset after,
        DateTimeOffset upTo, CancellationToken ct = default)
        => await db.Database.SqlQuery<decimal>($"""
            SELECT COALESCE(SUM(quantity), 0) AS "Value" FROM nexora.stock_movement
            WHERE company_id = {Company} AND product_id = {productId} AND inventory_location_id = {locationId}
              AND ({lotId}::uuid IS NULL OR lot_id = {lotId}::uuid)
              AND movement_type NOT IN ('reservation', 'release')
              AND created_at > {after} AND created_at <= {upTo}
            """).FirstAsync(ct);

    private sealed record LotQuantityRow(Guid LotId, decimal Quantity);

    public async Task<IReadOnlyList<(Guid LotId, decimal Quantity)>> LotQuantitiesAsync(Guid productId, Guid locationId,
        CancellationToken ct = default)
        => (await db.Database.SqlQuery<LotQuantityRow>($"""
                SELECT ls.lot_id AS "LotId", ls.quantity_on_hand AS "Quantity"
                FROM nexora.lot_stock ls
                JOIN nexora.lot l ON l.id = ls.lot_id
                JOIN nexora.product p ON p.id = l.product_id AND p.track_lots
                WHERE ls.company_id = {Company} AND l.product_id = {productId} AND ls.inventory_location_id = {locationId}
                  AND ls.quantity_on_hand <> 0
                ORDER BY l.expiration_date NULLS LAST, l.lot_number
                """).ToListAsync(ct))
            .Select(r => (r.LotId, r.Quantity)).ToList();

    public async Task<decimal> AverageCostAsync(Guid productId, Guid locationId, CancellationToken ct = default)
        => await db.StockBalances.AsNoTracking()
            .Where(s => s.ProductId == productId && s.InventoryLocationId == locationId)
            .Select(s => (decimal?)s.AverageCost).FirstOrDefaultAsync(ct) ?? 0m;

    public async Task AddAdjustmentAsync(StockAdjustment adjustment, CancellationToken ct = default)
        => await db.StockAdjustments.AddAsync(adjustment, ct);

    public async Task SetCountValidatedAsync(Guid countId, Guid? userId, CancellationToken ct = default)
    {
        var at = DateTimeOffset.UtcNow;
        await db.InventoryCounts.Where(c => c.Id == countId)
            .ExecuteUpdateAsync(u => u
                .SetProperty(c => c.Status, "validated")
                .SetProperty(c => c.ValidatedAt, at)
                .SetProperty(c => c.ValidatedBy, userId), ct);
        SyncTracked(db.InventoryCounts.Local.FirstOrDefault(c => c.Id == countId), e =>
        {
            Set(e.Property(x => x.Status), "validated");
            Set(e.Property(x => x.ValidatedAt), (DateTimeOffset?)at);
            Set(e.Property(x => x.ValidatedBy), userId);
        });
    }

    public async Task SetTransferStatusAsync(Guid transferId, string status, DateTimeOffset? shipped, DateTimeOffset? received, CancellationToken ct = default)
    {
        await db.StockTransfers.Where(t => t.Id == transferId)
            .ExecuteUpdateAsync(u => u
                .SetProperty(t => t.Status, status)
                .SetProperty(t => t.ReceivedAt, t => received ?? t.ReceivedAt), ct);
        SyncTracked(db.StockTransfers.Local.FirstOrDefault(t => t.Id == transferId), e =>
        {
            Set(e.Property(x => x.Status), status);
            if (received is not null) Set(e.Property(x => x.ReceivedAt), received);
        });
    }

    private Guid Company => StoreHelpers.Company(tenant);

    public Task<string> FunctionalCurrencyAsync(CancellationToken ct = default)
        => db.Database.SqlQuery<string>($"""
            SELECT cur.code AS "Value" FROM nexora.company co JOIN nexora.currency cur ON cur.id = co.default_currency_id
            WHERE co.id = {Company}
            """).FirstAsync(ct);

    public Task<bool> LocationExistsAsync(Guid locationId, CancellationToken ct = default)
        => db.Database.SqlQuery<bool>($"""
            SELECT EXISTS(SELECT 1 FROM nexora.inventory_location WHERE id = {locationId} AND company_id = {Company}) AS "Value"
            """).FirstAsync(ct);

    public Task<bool> ProductExistsAsync(Guid productId, CancellationToken ct = default)
        => db.Database.SqlQuery<bool>($"""
            SELECT EXISTS(SELECT 1 FROM nexora.product WHERE id = {productId} AND company_id = {Company}) AS "Value"
            """).FirstAsync(ct);

    public async Task<IReadOnlyList<Guid>> ProductsForFullCountAsync(Guid locationId, CancellationToken ct = default)
        => await db.Database.SqlQuery<Guid>($"""
            SELECT DISTINCT p.id AS "Value"
            FROM nexora.product p
            WHERE p.company_id = {Company}
              AND (EXISTS (SELECT 1 FROM nexora.stock_balance sb
                           WHERE sb.product_id = p.id AND sb.inventory_location_id = {locationId} AND sb.quantity_on_hand <> 0)
                   OR (p.status = 'active' AND p.product_type IN ('stockable','weight')
                       AND EXISTS (SELECT 1 FROM nexora.product_reorder_rule r
                                   WHERE r.product_id = p.id AND r.status = 'active'
                                     AND (r.inventory_location_id IS NULL OR r.inventory_location_id = {locationId}))))
            """).ToListAsync(ct);

    public async Task SetCountStatusAsync(Guid countId, string status, string? notes, CancellationToken ct = default)
    {
        await db.InventoryCounts.Where(c => c.Id == countId)
            .ExecuteUpdateAsync(u => u
                .SetProperty(c => c.Status, status)
                .SetProperty(c => c.Notes, c => notes ?? c.Notes), ct);
        SyncTracked(db.InventoryCounts.Local.FirstOrDefault(c => c.Id == countId), e =>
        {
            Set(e.Property(x => x.Status), status);
            if (notes is not null) Set(e.Property(x => x.Notes), notes);
        });
    }

    /// <summary>
    /// Les changements de statut passent par <c>ExecuteUpdate</c> (directement en base) : l'entité déjà suivie
    /// par le contexte garderait l'ancien statut et une seconde opération dans la même unité de travail la
    /// relirait périmée (ex. recevoir un transfert déjà annulé). On aligne l'entité suivie — valeur courante
    /// ET originale, pour qu'EF ne réémette aucune mise à jour.
    /// </summary>
    private void SyncTracked<T>(T? entity, Action<Microsoft.EntityFrameworkCore.ChangeTracking.EntityEntry<T>> apply) where T : class
    {
        if (entity is null) return;
        apply(db.Entry(entity));
    }

    private static void Set<TP>(Microsoft.EntityFrameworkCore.ChangeTracking.PropertyEntry<InventoryCount, TP> p, TP value)
    {
        p.CurrentValue = value;
        p.OriginalValue = value;
    }

    private static void Set<TP>(Microsoft.EntityFrameworkCore.ChangeTracking.PropertyEntry<StockTransfer, TP> p, TP value)
    {
        p.CurrentValue = value;
        p.OriginalValue = value;
    }

    public async Task SetAdjustmentJournalAsync(Guid adjustmentId, Guid journalEntryId, CancellationToken ct = default)
    {
        // L'ajustement vient d'être ajouté au contexte : on le persiste avant d'y rattacher l'écriture.
        await db.SaveChangesAsync(ct);
        await db.StockAdjustments.Where(a => a.Id == adjustmentId)
            .ExecuteUpdateAsync(u => u.SetProperty(a => a.JournalEntryId, journalEntryId), ct);
    }
}

public sealed class SqlReorderService(NexoraDbContext db, ITenantContext tenant) : IReorderService
{
    private sealed record Row(Guid ProductId, string Sku, string ProductName, Guid LocationId,
        decimal OnHand, decimal Available, decimal ReorderPoint, decimal ReorderQuantity, Guid? PreferredSupplierId);

    public async Task<IReadOnlyList<ReorderSuggestion>> SuggestionsAsync(CancellationToken ct = default)
    {
        var company = StoreHelpers.Company(tenant);
        var rows = await db.Database.SqlQuery<Row>($"""
            SELECT p.id AS "ProductId", p.sku AS "Sku", p.name AS "ProductName",
                   sb.inventory_location_id AS "LocationId",
                   sb.quantity_on_hand AS "OnHand",
                   (sb.quantity_on_hand - sb.quantity_reserved) AS "Available",
                   r.reorder_point AS "ReorderPoint",
                   GREATEST(r.reorder_quantity, r.max_quantity - sb.quantity_on_hand) AS "ReorderQuantity",
                   (SELECT ps.supplier_id FROM nexora.product_supplier ps
                     WHERE ps.product_id = p.id ORDER BY ps.is_preferred DESC LIMIT 1) AS "PreferredSupplierId"
            FROM nexora.product_reorder_rule r
            JOIN nexora.product p ON p.id = r.product_id
            JOIN nexora.stock_balance sb ON sb.product_id = r.product_id
                 AND (r.inventory_location_id IS NULL OR r.inventory_location_id = sb.inventory_location_id)
            WHERE r.company_id = {company} AND r.status = 'active'
              AND (sb.quantity_on_hand - sb.quantity_reserved) <= r.reorder_point
            """).ToListAsync(ct);

        return rows.Select(x => new ReorderSuggestion(
            x.ProductId, x.Sku, x.ProductName, x.LocationId, x.OnHand, x.Available, x.ReorderPoint,
            x.ReorderQuantity <= 0 ? x.ReorderPoint : x.ReorderQuantity, x.PreferredSupplierId)).ToList();
    }
}
