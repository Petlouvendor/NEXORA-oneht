using Microsoft.EntityFrameworkCore;
using Nexora.Application.Printing;
using Nexora.Contracts.Printing;
using Nexora.Domain.Common;
using K = Nexora.Contracts.Printing.PrintKinds;

namespace Nexora.Infrastructure.Printing;

/// <summary>Ticket de caisse et documents fournisseurs (paiement, facture, commande, réception, relevé).</summary>
public sealed partial class SqlReceiptService
{
    // ================================================================== Ticket de caisse

    private sealed record SaleRow(
        string Number, string Status, string SaleType, DateTimeOffset SoldAt, decimal Subtotal, decimal DiscountTotal,
        decimal TaxTotal, decimal Total, decimal AmountTendered, decimal ChangeGiven, string Currency,
        Guid? EstablishmentId, Guid? CustomerId, string? Cashier, string? VoidReason);

    private sealed record SaleLineRow(string Label, decimal Quantity, decimal UnitPrice, decimal Discount, decimal LineTotal);

    private sealed record LabelAmount(string Label, decimal Amount);

    private sealed record SaleCreditRow(Guid ReceivableId, string CreditNumber, decimal Original, decimal StoreCredit, DateTimeOffset? Mark);

    private async Task<Built?> SaleAsync(Guid id, CancellationToken ct)
    {
        var s = await db.Database.SqlQuery<SaleRow>($"""
            SELECT s.document_number AS "Number", s.status AS "Status", s.sale_type AS "SaleType", s.sold_at AS "SoldAt",
                   s.subtotal AS "Subtotal", s.discount_total AS "DiscountTotal", s.tax_total AS "TaxTotal", s.total AS "Total",
                   coalesce(s.amount_tendered, 0) AS "AmountTendered", coalesce(s.change_given, 0) AS "ChangeGiven",
                   cur.code AS "Currency", s.establishment_id AS "EstablishmentId", s.customer_id AS "CustomerId",
                   u.full_name AS "Cashier", s.void_reason AS "VoidReason"
            FROM nexora.sale s
            JOIN nexora.currency cur ON cur.id = s.currency_id
            LEFT JOIN nexora.app_user u ON u.id = s.created_by
            WHERE s.id = {id} AND s.company_id = {Company}
            """).FirstOrDefaultAsync(ct);
        if (s is null) return null;

        var lines = await db.Database.SqlQuery<SaleLineRow>($"""
            SELECT coalesce(nullif(btrim(l.description), ''), p.name, 'Article') AS "Label", l.quantity AS "Quantity",
                   l.unit_price AS "UnitPrice", l.discount AS "Discount", l.line_total AS "LineTotal"
            FROM nexora.sale_line l
            LEFT JOIN nexora.product p ON p.id = l.product_id
            WHERE l.sale_id = {id} AND l.company_id = {Company}
            ORDER BY l.created_at, l.id
            """).ToListAsync(ct);
        var tenders = await db.Database.SqlQuery<LabelAmount>($"""
            SELECT coalesce(pm.name, 'Paiement') AS "Label", sp.amount AS "Amount"
            FROM nexora.sale_payment sp
            JOIN nexora.payment p ON p.id = sp.payment_id
            LEFT JOIN nexora.payment_method pm ON pm.id = p.payment_method_id
            WHERE sp.sale_id = {id} AND sp.company_id = {Company}
            ORDER BY sp.created_at
            """).ToListAsync(ct);
        // Part à crédit : la créance née de la vente ; l'avoir utilisé à la caisse est imputé dans la même
        // transaction (même horodatage de transaction que la créance).
        var credit = await db.Database.SqlQuery<SaleCreditRow>($"""
            SELECT r.id AS "ReceivableId", r.credit_number AS "CreditNumber", r.original_amount AS "Original",
                   coalesce((SELECT sum(x.amount) FROM nexora.customer_credit_application x
                             WHERE x.receivable_id = r.id AND x.created_at = r.created_at AND x.voided_at IS NULL), 0) AS "StoreCredit",
                   (SELECT max(l.created_at) FROM nexora.customer_ledger_entry l
                    WHERE l.receivable_id = r.id AND l.entry_type = 'credit_sale') AS "Mark"
            FROM nexora.customer_receivable r
            WHERE r.sale_id = {id} AND r.company_id = {Company}
            ORDER BY r.created_at
            LIMIT 1
            """).FirstOrDefaultAsync(ct);

        PrintParty? party = s.CustomerId is { } cid ? await CustomerPartyAsync(cid, ct) : null;
        var summary = new List<PrintFigure>
        {
            new("Sous-total", s.Subtotal + s.DiscountTotal, K.Money, s.Currency),
        };
        if (s.DiscountTotal > 0) summary.Add(new("Remises", -s.DiscountTotal, K.Money, s.Currency));
        if (s.TaxTotal > 0) summary.Add(new("Taxes", s.TaxTotal, K.Money, s.Currency));
        summary.Add(new("Total", s.Total, K.Money, s.Currency, Emphasis: true));
        summary.AddRange(tenders.Select(t => new PrintFigure(t.Label, t.Amount, K.Money, s.Currency)));
        if (s.AmountTendered > 0 && s.ChangeGiven > 0) summary.Add(new("Montant reçu", s.AmountTendered, K.Money, s.Currency));
        if (s.ChangeGiven > 0) summary.Add(new("Rendu", s.ChangeGiven, K.Money, s.Currency));
        if (credit is not null)
        {
            if (credit.StoreCredit > 0) summary.Add(new("Payé par avoir", credit.StoreCredit, K.Money, s.Currency));
            summary.Add(new($"À crédit ({credit.CreditNumber})", credit.Original - credit.StoreCredit, K.Money, s.Currency));
            if (s.CustomerId is { } customerId && credit.Mark is { } mark)
            {
                var pos = await PositionAsync(customerId, s.Currency, mark, ct);
                summary.Add(pos.Balance >= 0
                    ? new("Reste à verser (compte client)", pos.Balance, K.Money, s.Currency, Emphasis: true)
                    : new("Avoir du client", -pos.Balance, K.Money, s.Currency, Emphasis: true));
            }
        }

        var doc = new PrintDocument(
            Title: "Ticket de caisse",
            Reference: s.Number,
            Parties: party is null ? null : [party],
            Details: Details(("Date", s.SoldAt, K.DateTime), ("Caissier", s.Cashier, K.Text)),
            Tables:
            [
                new PrintTable(null,
                    [new("Article"), new("Qté", K.Number), new("P.U.", K.Money), new("Montant", K.Money)],
                    lines.Select(l => (IReadOnlyList<object?>)
                        [l.Discount > 0 ? $"{l.Label} (remise {PrintValues.Money(l.Discount)})" : l.Label, l.Quantity, l.UnitPrice, l.LineTotal]).ToList()),
            ],
            Summary: summary,
            Notes: s.Status == "void" ? [$"Vente annulée — motif : {s.VoidReason}."] : null,
            Signatures: credit is not null ? ["Signature du client"] : null,
            Stamp: s.Status switch
            {
                "void" => "VENTE ANNULÉE",
                "refunded" => "VENTE REMBOURSÉE",
                "partially_refunded" => "VENTE PARTIELLEMENT REMBOURSÉE",
                "parked" or "draft" => "VENTE NON ENCAISSÉE",
                _ => null,
            });
        return new Built(doc, $"ticket-{s.Number}", EstablishmentId: s.EstablishmentId);
    }

    // ================================================================== Paiement fournisseur

    private sealed record SupplierPaymentRow(
        string Number, decimal Amount, string Status, DateTimeOffset PaidAt, string? Reference, string? Note,
        DateTimeOffset? VoidedAt, string? VoidReason, string Currency, Guid CurrencyId, Guid SupplierId,
        string? Account, string? Method, string? PaidBy, DateTimeOffset CreatedAt);

    private sealed record PayableAllocationRow(
        Guid PayableId, string Invoice, string? SupplierReference, decimal Original, decimal Amount, bool Voided, DateTimeOffset AllocatedAt);

    private sealed record SupplierPosition(decimal Debts, decimal Paid);

    private async Task<Built?> SupplierPaymentAsync(Guid id, CancellationToken ct)
    {
        var p = await db.Database.SqlQuery<SupplierPaymentRow>($"""
            SELECT p.payment_number AS "Number", p.amount AS "Amount", p.status AS "Status", p.received_at AS "PaidAt",
                   p.reference AS "Reference", p.note AS "Note", p.voided_at AS "VoidedAt", p.void_reason AS "VoidReason",
                   cur.code AS "Currency", p.currency_id AS "CurrencyId", p.supplier_id AS "SupplierId",
                   fa.name AS "Account", pm.name AS "Method", u.full_name AS "PaidBy", p.created_at AS "CreatedAt"
            FROM nexora.payment p
            JOIN nexora.currency cur ON cur.id = p.currency_id
            LEFT JOIN nexora.financial_account fa ON fa.id = p.financial_account_id
            LEFT JOIN nexora.payment_method pm ON pm.id = p.payment_method_id
            LEFT JOIN nexora.app_user u ON u.id = p.created_by
            WHERE p.id = {id} AND p.company_id = {Company} AND p.direction = 'outbound' AND p.supplier_id IS NOT NULL
            """).FirstOrDefaultAsync(ct);
        if (p is null) return null;

        var allocations = await db.Database.SqlQuery<PayableAllocationRow>($"""
            SELECT a.payable_id AS "PayableId", coalesce(si.document_number, 'Dette') AS "Invoice",
                   si.supplier_reference AS "SupplierReference", sp.original_amount AS "Original",
                   a.allocated_amount AS "Amount", (a.voided_at IS NOT NULL) AS "Voided", a.allocated_at AS "AllocatedAt"
            FROM nexora.payment_allocation a
            JOIN nexora.supplier_payable sp ON sp.id = a.payable_id
            LEFT JOIN nexora.supplier_invoice si ON si.id = sp.supplier_invoice_id
            WHERE a.payment_id = {id} AND a.company_id = {Company}
            ORDER BY a.allocated_at, si.document_number
            """).ToListAsync(ct);
        var at = allocations.Count > 0 ? allocations.Max(a => a.AllocatedAt) : p.CreatedAt;

        var payableIds = allocations.Select(a => a.PayableId).Distinct().ToArray();
        var remaining = payableIds.Length == 0 ? [] : (await db.Database.SqlQuery<AmountRow>($"""
            SELECT sp.id AS "Id",
                   sp.original_amount - coalesce((SELECT sum(a.allocated_amount) FROM nexora.payment_allocation a
                                                  WHERE a.payable_id = sp.id AND a.allocated_at <= {at}
                                                    AND (a.voided_at IS NULL OR a.voided_at > {at})), 0) AS "Amount"
            FROM nexora.supplier_payable sp
            WHERE sp.company_id = {Company} AND sp.id = ANY({payableIds})
            """).ToListAsync(ct)).ToDictionary(r => r.Id, r => r.Amount);

        var pos = await db.Database.SqlQuery<SupplierPosition>($"""
            SELECT coalesce((SELECT sum(sp.original_amount) FROM nexora.supplier_payable sp
                             WHERE sp.company_id = {Company} AND sp.supplier_id = {p.SupplierId} AND sp.currency_id = {p.CurrencyId}
                               AND sp.status <> 'CANCELLED' AND sp.created_at <= {at}), 0) AS "Debts",
                   coalesce((SELECT sum(a.allocated_amount) FROM nexora.payment_allocation a
                             JOIN nexora.supplier_payable sp ON sp.id = a.payable_id
                             WHERE sp.company_id = {Company} AND sp.supplier_id = {p.SupplierId} AND sp.currency_id = {p.CurrencyId}
                               AND a.allocated_at <= {at} AND (a.voided_at IS NULL OR a.voided_at > {at})), 0) AS "Paid"
            """).FirstAsync(ct);

        var party = await SupplierPartyAsync(p.SupplierId, ct);
        var doc = new PrintDocument(
            Title: "Reçu de paiement fournisseur",
            Reference: p.Number,
            Parties: party is null ? null : [party],
            Details: Details(
                ("Date", p.PaidAt, K.DateTime),
                ("Mode de paiement", p.Method, K.Text),
                ("Payé depuis", p.Account, K.Text),
                ("Référence", p.Reference, K.Text),
                ("Payé par", p.PaidBy, K.Text),
                ("Note", p.Note, K.Text),
                ("Annulé le", p.VoidedAt, K.DateTime)),
            Tables: allocations.Count == 0 ? null :
            [
                new PrintTable("Factures réglées",
                    [new("Facture"), new("Réf. fournisseur"), new("Montant", K.Money, p.Currency), new("Payé ici", K.Money, p.Currency), new("Reste dû", K.Money, p.Currency)],
                    allocations.Select(a => (IReadOnlyList<object?>)
                        [a.Voided ? $"{a.Invoice} (annulé)" : a.Invoice, a.SupplierReference, a.Original, a.Amount, remaining.GetValueOrDefault(a.PayableId)]).ToList()),
            ],
            Summary:
            [
                new("Montant de ce paiement", p.Amount, K.Money, p.Currency),
                new("Total facturé par le fournisseur", pos.Debts, K.Money, p.Currency),
                new("Total payé", pos.Paid, K.Money, p.Currency),
                new("Reste à payer au fournisseur", pos.Debts - pos.Paid, K.Money, p.Currency, Emphasis: true),
            ],
            Notes: p.Status == "void"
                ? [$"Paiement annulé — motif : {p.VoidReason}.", "Situation fournisseur arrêtée au moment de ce paiement."]
                : ["Situation fournisseur arrêtée au moment de ce paiement."],
            Signatures: ["Le fournisseur (reçu la somme)", "Payé par"],
            Stamp: p.Status == "void" ? "PAIEMENT ANNULÉ" : null);
        return new Built(doc, $"paiement-fournisseur-{p.Number}");
    }

    // ================================================================== Facture fournisseur

    private sealed record SupplierInvoiceRow(
        string Number, string? SupplierReference, DateOnly InvoiceDate, DateOnly? DueDate, decimal Subtotal,
        decimal DiscountTotal, decimal TaxTotal, decimal Total, string Status, string Currency, Guid SupplierId,
        string? OrderNumber, Guid? PayableId, decimal? Paid, decimal? Remaining, string? CreatedBy);

    private sealed record DocLineRow(string Label, string? Sku, decimal Quantity, decimal? Quantity2, decimal UnitPrice, decimal Discount, decimal LineTotal);

    private sealed record PaymentLineRow(string Number, DateTimeOffset PaidAt, decimal Amount, bool Voided);

    private async Task<Built?> SupplierInvoiceAsync(Guid id, CancellationToken ct)
    {
        var i = await db.Database.SqlQuery<SupplierInvoiceRow>($"""
            SELECT si.document_number AS "Number", si.supplier_reference AS "SupplierReference", si.invoice_date AS "InvoiceDate",
                   si.due_date AS "DueDate", si.subtotal AS "Subtotal", si.discount_total AS "DiscountTotal",
                   si.tax_total AS "TaxTotal", si.total AS "Total", si.status AS "Status", cur.code AS "Currency",
                   si.supplier_id AS "SupplierId", po.document_number AS "OrderNumber", sp.id AS "PayableId",
                   sp.paid_amount AS "Paid", sp.remaining_amount AS "Remaining", u.full_name AS "CreatedBy"
            FROM nexora.supplier_invoice si
            JOIN nexora.currency cur ON cur.id = si.currency_id
            LEFT JOIN nexora.purchase_order po ON po.id = si.purchase_order_id
            LEFT JOIN nexora.supplier_payable sp ON sp.supplier_invoice_id = si.id
            LEFT JOIN nexora.app_user u ON u.id = si.created_by
            WHERE si.id = {id} AND si.company_id = {Company}
            """).FirstOrDefaultAsync(ct);
        if (i is null) return null;

        var lines = await db.Database.SqlQuery<DocLineRow>($"""
            SELECT coalesce(nullif(btrim(l.description), ''), p.name, 'Article') AS "Label", p.sku AS "Sku",
                   l.quantity AS "Quantity", NULL::numeric AS "Quantity2", l.unit_price AS "UnitPrice",
                   l.discount AS "Discount", l.line_total AS "LineTotal"
            FROM nexora.supplier_invoice_line l
            LEFT JOIN nexora.product p ON p.id = l.product_id
            WHERE l.supplier_invoice_id = {id} AND l.company_id = {Company}
            ORDER BY l.created_at, l.id
            """).ToListAsync(ct);
        var payments = i.PayableId is { } payableId
            ? await db.Database.SqlQuery<PaymentLineRow>($"""
                SELECT p.payment_number AS "Number", p.received_at AS "PaidAt", a.allocated_amount AS "Amount",
                       (a.voided_at IS NOT NULL) AS "Voided"
                FROM nexora.payment_allocation a
                JOIN nexora.payment p ON p.id = a.payment_id
                WHERE a.payable_id = {payableId} AND a.company_id = {Company}
                ORDER BY a.allocated_at
                """).ToListAsync(ct)
            : [];

        var party = await SupplierPartyAsync(i.SupplierId, ct);
        var tables = new List<PrintTable>
        {
            new("Lignes",
                [new("Article"), new("Réf."), new("Qté", K.Number), new("P.U.", K.Money, i.Currency), new("Remise", K.Money, i.Currency), new("Montant", K.Money, i.Currency)],
                lines.Select(l => (IReadOnlyList<object?>)[l.Label, l.Sku, l.Quantity, l.UnitPrice, l.Discount == 0 ? null : l.Discount, l.LineTotal]).ToList()),
        };
        if (payments.Count > 0)
            tables.Add(new PrintTable("Paiements",
                [new("Paiement"), new("Date", K.DateTime), new("Montant", K.Money, i.Currency), new("État")],
                payments.Select(p => (IReadOnlyList<object?>)[p.Number, p.PaidAt, p.Amount, p.Voided ? "Annulé" : "Actif"]).ToList()));

        var summary = new List<PrintFigure> { new("Sous-total", i.Subtotal, K.Money, i.Currency) };
        if (i.DiscountTotal > 0) summary.Add(new("Remises", -i.DiscountTotal, K.Money, i.Currency));
        if (i.TaxTotal > 0) summary.Add(new("Taxes", i.TaxTotal, K.Money, i.Currency));
        summary.Add(new("Total de la facture", i.Total, K.Money, i.Currency));
        summary.Add(new("Total payé", i.Paid ?? 0m, K.Money, i.Currency));
        summary.Add(new("Reste à payer", i.Remaining ?? i.Total, K.Money, i.Currency, Emphasis: true));

        var doc = new PrintDocument(
            Title: "Facture fournisseur",
            Reference: i.Number,
            Parties: party is null ? null : [party],
            Details: Details(
                ("Référence fournisseur", i.SupplierReference, K.Text),
                ("Date de facture", i.InvoiceDate, K.Date),
                ("Échéance", i.DueDate, K.Date),
                ("Bon de commande", i.OrderNumber, K.Text),
                ("Statut", InvoiceStatusLabel(i.Status), K.Text),
                ("Saisie par", i.CreatedBy, K.Text)),
            Tables: tables,
            Summary: summary,
            Stamp: i.Status switch { "cancelled" => "FACTURE ANNULÉE", "draft" => "BROUILLON — non validée", "paid" => "PAYÉE", _ => null });
        return new Built(doc, $"facture-fournisseur-{i.Number}");
    }

    // ================================================================== Bon de commande

    private sealed record PurchaseOrderRow(
        string Number, string Status, DateOnly OrderDate, DateOnly? ExpectedDate, decimal Subtotal, decimal DiscountTotal,
        decimal TaxTotal, decimal Total, string? Notes, string Currency, Guid SupplierId, string? Warehouse,
        string? WarehouseAddress, string? Establishment, Guid? EstablishmentId, string? CreatedBy);

    private async Task<Built?> PurchaseOrderAsync(Guid id, CancellationToken ct)
    {
        var o = await db.Database.SqlQuery<PurchaseOrderRow>($"""
            SELECT po.document_number AS "Number", po.status AS "Status", po.order_date AS "OrderDate",
                   po.expected_date AS "ExpectedDate", po.subtotal AS "Subtotal", po.discount_total AS "DiscountTotal",
                   po.tax_total AS "TaxTotal", po.total AS "Total", po.notes AS "Notes", cur.code AS "Currency",
                   po.supplier_id AS "SupplierId", w.name AS "Warehouse", w.address AS "WarehouseAddress",
                   e.name AS "Establishment", coalesce(po.establishment_id, w.establishment_id) AS "EstablishmentId",
                   u.full_name AS "CreatedBy"
            FROM nexora.purchase_order po
            JOIN nexora.currency cur ON cur.id = po.currency_id
            LEFT JOIN nexora.warehouse w ON w.id = po.warehouse_id
            LEFT JOIN nexora.establishment e ON e.id = coalesce(po.establishment_id, w.establishment_id)
            LEFT JOIN nexora.app_user u ON u.id = po.created_by
            WHERE po.id = {id} AND po.company_id = {Company}
            """).FirstOrDefaultAsync(ct);
        if (o is null) return null;

        var lines = await db.Database.SqlQuery<DocLineRow>($"""
            SELECT p.name AS "Label", p.sku AS "Sku", l.quantity_ordered AS "Quantity", l.quantity_received AS "Quantity2",
                   l.unit_price AS "UnitPrice", l.discount AS "Discount", l.line_total AS "LineTotal"
            FROM nexora.purchase_order_line l
            JOIN nexora.product p ON p.id = l.product_id
            WHERE l.purchase_order_id = {id} AND l.company_id = {Company}
            ORDER BY l.created_at, l.id
            """).ToListAsync(ct);

        var parties = new List<PrintParty>();
        if (await SupplierPartyAsync(o.SupplierId, ct) is { } supplier) parties.Add(supplier);
        var delivery = new[] { o.Warehouse, o.WarehouseAddress, o.Establishment }.Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x!).ToList();
        if (delivery.Count > 0) parties.Add(new PrintParty("Livrer à", delivery));

        var summary = new List<PrintFigure> { new("Sous-total", o.Subtotal, K.Money, o.Currency) };
        if (o.DiscountTotal > 0) summary.Add(new("Remises", -o.DiscountTotal, K.Money, o.Currency));
        if (o.TaxTotal > 0) summary.Add(new("Taxes", o.TaxTotal, K.Money, o.Currency));
        summary.Add(new("Total de la commande", o.Total, K.Money, o.Currency, Emphasis: true));

        var doc = new PrintDocument(
            Title: "Bon de commande",
            Reference: o.Number,
            Parties: parties,
            Details: Details(
                ("Date de commande", o.OrderDate, K.Date),
                ("Livraison attendue", o.ExpectedDate, K.Date),
                ("Statut", OrderStatusLabel(o.Status), K.Text),
                ("Émis par", o.CreatedBy, K.Text)),
            Tables:
            [
                new PrintTable("Articles commandés",
                    [new("Article"), new("Réf."), new("Qté commandée", K.Number), new("Qté reçue", K.Number),
                     new("P.U.", K.Money, o.Currency), new("Remise", K.Money, o.Currency), new("Montant", K.Money, o.Currency)],
                    lines.Select(l => (IReadOnlyList<object?>)
                        [l.Label, l.Sku, l.Quantity, l.Quantity2, l.UnitPrice, l.Discount == 0 ? null : l.Discount, l.LineTotal]).ToList()),
            ],
            Summary: summary,
            Notes: string.IsNullOrWhiteSpace(o.Notes) ? null : [o.Notes!],
            Signatures: ["Émis par", "Approuvé par", "Le fournisseur (bon pour accord)"],
            Stamp: o.Status switch { "cancelled" => "COMMANDE ANNULÉE", "draft" => "BROUILLON — non validé", _ => null });
        return new Built(doc, $"bon-de-commande-{o.Number}", "normal", o.EstablishmentId);
    }

    // ================================================================== Bon de réception

    private sealed record GoodsReceiptRow(
        string Number, string Status, DateTimeOffset ReceivedAt, string? Notes, Guid SupplierId, string? OrderNumber,
        string? Warehouse, Guid? EstablishmentId, string? ReceivedBy, string Currency);

    private sealed record ReceiptLineRow(string Label, string? Sku, decimal Quantity, decimal UnitCost, string? LotNumber, DateOnly? Expiry);

    private async Task<Built?> GoodsReceiptAsync(Guid id, CancellationToken ct)
    {
        var g = await db.Database.SqlQuery<GoodsReceiptRow>($"""
            SELECT gr.document_number AS "Number", gr.status AS "Status", gr.received_at AS "ReceivedAt", gr.notes AS "Notes",
                   gr.supplier_id AS "SupplierId", po.document_number AS "OrderNumber", w.name AS "Warehouse",
                   w.establishment_id AS "EstablishmentId", u.full_name AS "ReceivedBy",
                   coalesce(pc.code, dc.code) AS "Currency"
            FROM nexora.goods_receipt gr
            JOIN nexora.company co ON co.id = gr.company_id
            JOIN nexora.currency dc ON dc.id = co.default_currency_id
            LEFT JOIN nexora.purchase_order po ON po.id = gr.purchase_order_id
            LEFT JOIN nexora.currency pc ON pc.id = po.currency_id
            LEFT JOIN nexora.warehouse w ON w.id = gr.warehouse_id
            LEFT JOIN nexora.app_user u ON u.id = gr.created_by
            WHERE gr.id = {id} AND gr.company_id = {Company}
            """).FirstOrDefaultAsync(ct);
        if (g is null) return null;

        var lines = await db.Database.SqlQuery<ReceiptLineRow>($"""
            SELECT p.name AS "Label", p.sku AS "Sku", l.quantity_received AS "Quantity", l.unit_cost AS "UnitCost",
                   lt.lot_number AS "LotNumber", lt.expiration_date AS "Expiry"
            FROM nexora.goods_receipt_line l
            JOIN nexora.product p ON p.id = l.product_id
            LEFT JOIN nexora.lot lt ON lt.id = l.lot_id
            WHERE l.goods_receipt_id = {id} AND l.company_id = {Company}
            ORDER BY l.created_at, l.id
            """).ToListAsync(ct);

        var party = await SupplierPartyAsync(g.SupplierId, ct);
        var total = lines.Sum(l => l.Quantity * l.UnitCost);
        var doc = new PrintDocument(
            Title: "Bon de réception",
            Reference: g.Number,
            Parties: party is null ? null : [party],
            Details: Details(
                ("Reçu le", g.ReceivedAt, K.DateTime),
                ("Bon de commande", g.OrderNumber, K.Text),
                ("Entrepôt", g.Warehouse, K.Text),
                ("Réceptionné par", g.ReceivedBy, K.Text)),
            Tables:
            [
                new PrintTable("Articles reçus",
                    [new("Article"), new("Réf."), new("Lot"), new("Péremption", K.Date), new("Qté reçue", K.Number),
                     new("Coût unitaire", K.Money, g.Currency), new("Valeur", K.Money, g.Currency)],
                    lines.Select(l => (IReadOnlyList<object?>)
                        [l.Label, l.Sku, l.LotNumber, l.Expiry, l.Quantity, l.UnitCost, l.Quantity * l.UnitCost]).ToList(),
                    [null, null, null, "Total", lines.Sum(l => l.Quantity), null, total]),
            ],
            Summary: [new("Valeur de la réception", total, K.Money, g.Currency, Emphasis: true)],
            Notes: string.IsNullOrWhiteSpace(g.Notes) ? null : [g.Notes!],
            Signatures: ["Réceptionné par", "Le livreur / fournisseur"],
            Stamp: g.Status switch { "cancelled" => "RÉCEPTION ANNULÉE", "draft" => "BROUILLON — non comptabilisé", _ => null });
        return new Built(doc, $"bon-de-reception-{g.Number}", "normal", g.EstablishmentId);
    }

    // ================================================================== Relevé fournisseur

    public async Task<Result<RenderedFile>> SupplierStatementAsync(
        Guid supplierId, DateOnly? from, DateOnly? to, string format, CancellationToken ct = default)
    {
        var f = PrintFormats.Normalize(format);
        if (!PrintFormats.IsValid(f)) return Error.Validation("print.format", "Format inconnu : pdf, ticket, xlsx ou csv.");
        if (await LevelAsync("supplier-statement", ct) == "none")
            return Error.Forbidden("print.forbidden", "Le relevé fournisseur requiert purchasing.view_payables.");

        var st = await purchasing.SupplierStatementAsync(supplierId, from, to, ct);
        if (st is null) return Error.NotFound("print.not_found", "Fournisseur introuvable.");
        var party = await SupplierPartyAsync(supplierId, ct);

        var rows = new List<IReadOnlyList<object?>>();
        if (from is not null) rows.Add([from, "Solde d'ouverture", null, null, null, null, st.OpeningBalance]);
        rows.AddRange(st.Lines.Select(l => (IReadOnlyList<object?>)
            [l.Date, SupplierDocLabel(l.DocumentType), l.DocumentNumber, l.Reference,
             l.Charge == 0 ? null : l.Charge, l.Payment == 0 ? null : l.Payment, l.RunningBalance]));
        var charges = st.Lines.Sum(l => l.Charge);
        var paid = st.Lines.Sum(l => l.Payment);

        var doc = new PrintDocument(
            Title: "Relevé fournisseur",
            Subtitle: Period(from, to),
            Parties: party is null ? null : [party],
            Details: Details(("Devise", st.Currency, K.Text)),
            Tables:
            [
                new PrintTable("Mouvements",
                    [new("Date", K.Date), new("Document"), new("N°"), new("Référence"), new("Facturé", K.Money, st.Currency),
                     new("Payé", K.Money, st.Currency), new("Solde", K.Money, st.Currency)],
                    rows, [null, "Totaux", null, null, charges, paid, st.ClosingBalance]),
            ],
            Summary:
            [
                new("Solde d'ouverture", st.OpeningBalance, K.Money, st.Currency),
                new("Total facturé", charges, K.Money, st.Currency),
                new("Total payé", paid, K.Money, st.Currency),
                new("Dont échu", st.TotalOverdue, K.Money, st.Currency),
                new("Reste à payer", st.ClosingBalance, K.Money, st.Currency, Emphasis: true),
            ]);
        return Result.Success(await print.RenderAsync(doc, f, "statement:supplier", $"releve-fournisseur-{st.SupplierName}", "financial", supplierId, ct: ct));
    }

    public async Task<IReadOnlyList<PrintableItemDto>> SupplierDocumentsAsync(Guid supplierId, CancellationToken ct = default)
    {
        var allowed = new List<string>();
        foreach (var kind in new[] { PrintableKinds.SupplierPayment, PrintableKinds.GoodsReceipt, PrintableKinds.SupplierInvoice, PrintableKinds.PurchaseOrder })
            if (await LevelAsync(kind, ct) != "none") allowed.Add(kind);
        if (allowed.Count == 0) return [];
        var kinds = allowed.ToArray();
        return await db.Database.SqlQuery<PrintableItemDto>($"""
            SELECT * FROM (
                SELECT 'supplier-payment' AS "Kind", p.id AS "Id", p.payment_number AS "Number", p.received_at AS "Date",
                       p.amount AS "Amount", c.code AS "Currency", p.status AS "Status", p.reference AS "Detail"
                FROM nexora.payment p JOIN nexora.currency c ON c.id = p.currency_id
                WHERE p.company_id = {Company} AND p.supplier_id = {supplierId} AND p.direction = 'outbound'
                UNION ALL
                SELECT 'goods-receipt', gr.id, gr.document_number, gr.received_at,
                       (SELECT sum(l.quantity_received * l.unit_cost) FROM nexora.goods_receipt_line l WHERE l.goods_receipt_id = gr.id),
                       NULL, gr.status, po.document_number
                FROM nexora.goods_receipt gr LEFT JOIN nexora.purchase_order po ON po.id = gr.purchase_order_id
                WHERE gr.company_id = {Company} AND gr.supplier_id = {supplierId}
                UNION ALL
                SELECT 'supplier-invoice', si.id, si.document_number, si.invoice_date::timestamptz, si.total, c.code, si.status,
                       si.supplier_reference
                FROM nexora.supplier_invoice si JOIN nexora.currency c ON c.id = si.currency_id
                WHERE si.company_id = {Company} AND si.supplier_id = {supplierId}
                UNION ALL
                SELECT 'purchase-order', po.id, po.document_number, po.order_date::timestamptz, po.total, c.code, po.status, NULL
                FROM nexora.purchase_order po JOIN nexora.currency c ON c.id = po.currency_id
                WHERE po.company_id = {Company} AND po.supplier_id = {supplierId}
            ) d
            WHERE d."Kind" = ANY({kinds})
            ORDER BY d."Date" DESC
            LIMIT 300
            """).ToListAsync(ct);
    }

    private async Task<PrintParty?> SupplierPartyAsync(Guid id, CancellationToken ct)
    {
        var s = await db.Database.SqlQuery<PartyRow>($"""
            SELECT supplier_number AS "Number", name AS "Name", phone AS "Phone", email AS "Email",
                   nullif(btrim(address), '') AS "Address", tax_identifier AS "TaxId"
            FROM nexora.supplier WHERE id = {id} AND company_id = {Company}
            """).FirstOrDefaultAsync(ct);
        return s is null ? null : Party("Fournisseur", s);
    }

    private static string InvoiceStatusLabel(string status) => status switch
    {
        "draft" => "Brouillon",
        "validated" => "Validée",
        "partially_paid" => "Partiellement payée",
        "paid" => "Payée",
        "cancelled" => "Annulée",
        _ => status,
    };

    private static string OrderStatusLabel(string status) => status switch
    {
        "draft" => "Brouillon",
        "submitted" => "Soumise",
        "approved" => "Approuvée",
        "partially_received" => "Partiellement reçue",
        "received" => "Reçue",
        "invoiced" => "Facturée",
        "closed" => "Clôturée",
        "cancelled" => "Annulée",
        _ => status,
    };

    private static string SupplierDocLabel(string type) => type switch
    {
        "invoice" or "supplier_invoice" => "Facture",
        "payment" => "Paiement",
        "credit_note" or "return" => "Avoir / retour",
        _ => type,
    };
}
