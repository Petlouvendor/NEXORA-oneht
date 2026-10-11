using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Nexora.Contracts.Printing;
using K = Nexora.Contracts.Printing.PrintKinds;

namespace Nexora.Infrastructure.Printing;

/// <summary>Documents de vente (phase 4) : devis, commande client et document commercial émis (facture,
/// avoir, bon de livraison, proforma…) — ce dernier reconstruit depuis son contenu figé, jamais depuis la
/// source vivante (un document émis ne change plus).</summary>
public sealed partial class SqlReceiptService
{
    // ================================================================== Devis

    private sealed record QuoteHeadRow(
        string Number, string Status, DateOnly QuoteDate, DateOnly? ValidUntil, decimal Subtotal, decimal DiscountTotal,
        decimal TaxTotal, decimal Total, string? Terms, string? Notes, string Currency, Guid? CustomerId,
        Guid? EstablishmentId, Guid? CreatedBy, string? CreatedByName);

    private sealed record SalesLineRow(string Label, decimal Quantity, decimal UnitPrice, decimal Discount, decimal TaxAmount, decimal LineTotal);

    private async Task<Built?> QuoteAsync(Guid id, string level, CancellationToken ct)
    {
        var q = await db.Database.SqlQuery<QuoteHeadRow>($"""
            SELECT q.document_number AS "Number", q.status AS "Status", q.quote_date AS "QuoteDate", q.valid_until AS "ValidUntil",
                   q.subtotal AS "Subtotal", q.discount_total AS "DiscountTotal", q.tax_total AS "TaxTotal", q.total AS "Total",
                   q.terms AS "Terms", q.notes AS "Notes", cur.code AS "Currency", q.customer_id AS "CustomerId",
                   q.establishment_id AS "EstablishmentId", q.created_by AS "CreatedBy", u.full_name AS "CreatedByName"
            FROM nexora.quote q
            JOIN nexora.currency cur ON cur.id = q.currency_id
            LEFT JOIN nexora.app_user u ON u.id = q.created_by
            WHERE q.id = {id} AND q.company_id = {Company}
            """).FirstOrDefaultAsync(ct);
        if (q is null || (level == "own" && q.CreatedBy != tenant.UserId)) return null;

        var lines = await db.Database.SqlQuery<SalesLineRow>($"""
            SELECT l.description AS "Label", l.quantity AS "Quantity", l.unit_price AS "UnitPrice",
                   l.discount AS "Discount", l.tax_amount AS "TaxAmount", l.line_total AS "LineTotal"
            FROM nexora.quote_line l WHERE l.quote_id = {id} ORDER BY l.line_number
            """).ToListAsync(ct);

        var notes = new List<string>();
        if (!string.IsNullOrWhiteSpace(q.Terms)) notes.Add(q.Terms!);
        if (!string.IsNullOrWhiteSpace(q.Notes)) notes.Add(q.Notes!);
        var doc = new PrintDocument(
            Title: "Devis",
            Reference: q.Number,
            Parties: q.CustomerId is { } cid && await CustomerPartyAsync(cid, ct) is { } party ? [party] : null,
            Details: Details(
                ("Date", q.QuoteDate, K.Date),
                ("Valable jusqu'au", q.ValidUntil, K.Date),
                ("Statut", QuoteStatusLabel(q.Status), K.Text),
                ("Établi par", q.CreatedByName, K.Text)),
            Tables: [SalesLinesTable("Articles proposés", lines, q.Currency)],
            Summary: Totals(q.Subtotal, q.DiscountTotal, q.TaxTotal, q.Total, q.Currency, "Total du devis"),
            Notes: notes.Count == 0 ? null : notes,
            Signatures: ["Pour l'entreprise", "Le client (bon pour accord)"],
            Stamp: q.Status switch
            {
                "cancelled" => "DEVIS ANNULÉ",
                "rejected" => "DEVIS REFUSÉ",
                "expired" => "DEVIS EXPIRÉ",
                "draft" => "BROUILLON — non envoyé",
                _ => null,
            });
        return new Built(doc, $"devis-{q.Number}", "normal", q.EstablishmentId);
    }

    // ================================================================== Commande client

    private sealed record SalesOrderRow(
        string Number, string Status, string OrderType, DateTimeOffset CreatedAt, DateTimeOffset? ExpectedAt, DateOnly? DueDate,
        decimal Subtotal, decimal DiscountTotal, decimal TaxTotal, decimal Total, decimal DepositAmount, string? DeliveryAddress,
        string? Notes, string Currency, Guid? CustomerId, Guid? EstablishmentId, string? Location, string? CreatedByName);

    private sealed record OrderLinePrintRow(string Label, decimal Quantity, decimal QuantityFulfilled, decimal UnitPrice,
        decimal Discount, decimal LineTotal);

    private async Task<Built?> SalesOrderAsync(Guid id, CancellationToken ct)
    {
        var o = await db.Database.SqlQuery<SalesOrderRow>($"""
            SELECT o.document_number AS "Number", o.status AS "Status", o.order_type AS "OrderType", o.created_at AS "CreatedAt",
                   o.expected_at AS "ExpectedAt", o.due_date AS "DueDate", o.subtotal AS "Subtotal", o.discount_total AS "DiscountTotal",
                   o.tax_total AS "TaxTotal", o.total AS "Total", o.deposit_amount AS "DepositAmount",
                   o.delivery_address AS "DeliveryAddress", o.notes AS "Notes", cur.code AS "Currency", o.customer_id AS "CustomerId",
                   o.establishment_id AS "EstablishmentId", loc.name AS "Location", u.full_name AS "CreatedByName"
            FROM nexora.sales_order o
            JOIN nexora.currency cur ON cur.id = o.currency_id
            LEFT JOIN nexora.inventory_location loc ON loc.id = o.inventory_location_id
            LEFT JOIN nexora.app_user u ON u.id = o.created_by
            WHERE o.id = {id} AND o.company_id = {Company}
            """).FirstOrDefaultAsync(ct);
        if (o is null) return null;

        var lines = await db.Database.SqlQuery<OrderLinePrintRow>($"""
            SELECT l.description AS "Label", l.quantity AS "Quantity", l.quantity_fulfilled AS "QuantityFulfilled",
                   l.unit_price AS "UnitPrice", l.discount AS "Discount", l.line_total AS "LineTotal"
            FROM nexora.sales_order_line l WHERE l.sales_order_id = {id} AND l.company_id = {Company}
            ORDER BY l.description
            """).ToListAsync(ct);

        var parties = new List<PrintParty>();
        if (o.CustomerId is { } cid && await CustomerPartyAsync(cid, ct) is { } customer) parties.Add(customer);
        if (!string.IsNullOrWhiteSpace(o.DeliveryAddress)) parties.Add(new PrintParty("Livraison", [o.DeliveryAddress!]));

        var summary = Totals(o.Subtotal, o.DiscountTotal, o.TaxTotal, o.Total, o.Currency, "Total de la commande");
        if (o.DepositAmount > 0)
        {
            summary.Add(new("Acompte versé", o.DepositAmount, K.Money, o.Currency));
            summary.Add(new("Reste à régler", Math.Max(0m, o.Total - o.DepositAmount), K.Money, o.Currency, Emphasis: true));
        }
        var doc = new PrintDocument(
            Title: "Bon de commande client",
            Reference: o.Number,
            Parties: parties,
            Details: Details(
                ("Date", o.CreatedAt, K.DateTime),
                ("Type", OrderTypeLabel(o.OrderType), K.Text),
                ("Livraison / retrait prévu", o.ExpectedAt, K.DateTime),
                ("Échéance", o.DueDate, K.Date),
                ("Préparé depuis", o.Location, K.Text),
                ("Statut", SalesOrderStatusLabel(o.Status), K.Text),
                ("Saisie par", o.CreatedByName, K.Text)),
            Tables:
            [
                new PrintTable("Articles",
                    [new("Article"), new("Qté", K.Number), new("Livré", K.Number), new("P.U.", K.Money, o.Currency),
                     new("Remise", K.Money, o.Currency), new("Montant", K.Money, o.Currency)],
                    lines.Select(l => (IReadOnlyList<object?>)
                        [l.Label, l.Quantity, l.QuantityFulfilled, l.UnitPrice, l.Discount == 0 ? null : l.Discount, l.LineTotal]).ToList()),
            ],
            Summary: summary,
            Notes: string.IsNullOrWhiteSpace(o.Notes) ? null : [o.Notes!],
            Signatures: ["Pour l'entreprise", "Le client"],
            Stamp: o.Status switch { "cancelled" => "COMMANDE ANNULÉE", "draft" => "BROUILLON — non confirmé", _ => null });
        return new Built(doc, $"commande-{o.Number}", "normal", o.EstablishmentId);
    }

    // ================================================================== Document émis (contenu figé)

    private sealed record IssuedRow(
        string DocumentType, string DocumentNumber, string Status, DateOnly IssueDate, string Content,
        string? VerificationCode, string? CancelReason, Guid? EstablishmentId, Guid? CreatedBy, Guid? IssuedBy);

    private async Task<Built?> IssuedDocumentAsync(Guid id, string level, CancellationToken ct)
    {
        var d = await db.Database.SqlQuery<IssuedRow>($"""
            SELECT document_type AS "DocumentType", document_number AS "DocumentNumber", status AS "Status",
                   issue_date AS "IssueDate", content::text AS "Content", verification_code AS "VerificationCode",
                   cancel_reason AS "CancelReason", establishment_id AS "EstablishmentId", created_by AS "CreatedBy",
                   issued_by AS "IssuedBy"
            FROM nexora.issued_document WHERE id = {id} AND company_id = {Company}
            """).FirstOrDefaultAsync(ct);
        if (d is null) return null;
        if (level == "own" && d.CreatedBy != tenant.UserId && d.IssuedBy != tenant.UserId) return null;

        using var json = JsonDocument.Parse(d.Content);
        var root = json.RootElement;
        var currency = Str(root, "currency") ?? "";
        var party = Prop(root, "party");
        var source = Prop(root, "source");
        var totals = Prop(root, "totals");

        var rows = new List<IReadOnlyList<object?>>();
        var hasLines = false;
        if (Prop(root, "lines") is { ValueKind: JsonValueKind.Array } lines)
            foreach (var l in lines.EnumerateArray())
            {
                hasLines = true;
                var discount = Num(l, "discount");
                rows.Add([Str(l, "description") ?? "", Num(l, "quantity"), Num(l, "unitPrice"),
                          discount is 0m ? null : discount, Num(l, "taxAmount"), Num(l, "lineTotal") ?? Num(l, "amount")]);
            }

        var parties = new List<PrintParty>();
        if (party is { } p && Str(p, "id") is { } partyId && Guid.TryParse(partyId, out var pid)
            && Str(p, "type") == "customer" && await CustomerPartyAsync(pid, ct) is { } customer)
            parties.Add(customer);
        else if (party is { } p2 && Str(p2, "name") is { } name)
            parties.Add(new PrintParty("Client", [name]));

        var summary = new List<PrintFigure>();
        if (totals is { } t)
        {
            summary.Add(new("Total hors taxes", Num(t, "net") ?? 0m, K.Money, currency));
            if ((Num(t, "tax") ?? 0m) != 0m) summary.Add(new("Taxes", Num(t, "tax"), K.Money, currency));
            summary.Add(new(d.DocumentType == "credit_note" ? "Montant de l'avoir" : "Total", Num(t, "total") ?? 0m, K.Money, currency, Emphasis: true));
        }

        var notes = new List<string>();
        if (d.VerificationCode is { } code) notes.Add($"Code de vérification : {code}");
        if (Prop(root, "issuer") is { } issuer && Str(issuer, "footer") is { } footer) notes.Add(footer);
        if (d.Status is "cancelled" or "replaced" && !string.IsNullOrWhiteSpace(d.CancelReason)) notes.Add($"Motif d'annulation : {d.CancelReason}");

        var doc = new PrintDocument(
            Title: DocumentTypeLabel(d.DocumentType),
            Reference: d.DocumentNumber,
            Parties: parties,
            Details: Details(
                ("Date d'émission", d.IssueDate, K.Date),
                ("Valable jusqu'au", Str(root, "validUntil") is { } vu && DateOnly.TryParse(vu, CultureInfo.InvariantCulture, out var until) ? until : null, K.Date),
                ("Pièce d'origine", source is { } s ? Str(s, "number") : null, K.Text),
                ("Devise", currency, K.Text)),
            Tables: hasLines
                ?
                [
                    new PrintTable("Détail",
                        [new("Désignation"), new("Qté", K.Number), new("P.U.", K.Money, currency), new("Remise", K.Money, currency),
                         new("Taxe", K.Money, currency), new("Montant", K.Money, currency)],
                        rows),
                ]
                : null,
            Summary: summary,
            Notes: notes.Count == 0 ? null : notes,
            Stamp: d.Status switch { "cancelled" => "DOCUMENT ANNULÉ", "replaced" => "DOCUMENT REMPLACÉ", _ => null });
        return new Built(doc, $"{d.DocumentType.Replace('_', '-')}-{d.DocumentNumber}", "financial", d.EstablishmentId);
    }

    // ================================================================== Aides

    private static PrintTable SalesLinesTable(string title, IReadOnlyList<SalesLineRow> lines, string currency) =>
        new(title,
            [new("Désignation"), new("Qté", K.Number), new("P.U.", K.Money, currency), new("Remise", K.Money, currency),
             new("Taxe", K.Money, currency), new("Montant", K.Money, currency)],
            lines.Select(l => (IReadOnlyList<object?>)
                [l.Label, l.Quantity, l.UnitPrice, l.Discount == 0 ? null : l.Discount, l.TaxAmount == 0 ? null : l.TaxAmount, l.LineTotal]).ToList());

    private static List<PrintFigure> Totals(decimal subtotal, decimal discount, decimal tax, decimal total, string currency, string totalLabel)
    {
        var summary = new List<PrintFigure> { new("Sous-total", subtotal, K.Money, currency) };
        if (discount > 0) summary.Add(new("Remises", -discount, K.Money, currency));
        if (tax > 0) summary.Add(new("Taxes", tax, K.Money, currency));
        summary.Add(new(totalLabel, total, K.Money, currency, Emphasis: true));
        return summary;
    }

    private static JsonElement? Prop(JsonElement e, string name)
    {
        if (e.ValueKind != JsonValueKind.Object) return null;
        // Contenus figés avant la phase 4 : propriétés parfois en PascalCase.
        foreach (var p in e.EnumerateObject())
            if (string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase))
                return p.Value.ValueKind == JsonValueKind.Null ? null : p.Value;
        return null;
    }

    private static string? Str(JsonElement e, string name) => Prop(e, name) is { } v
        ? v.ValueKind == JsonValueKind.String ? v.GetString() : v.ToString()
        : null;

    private static decimal? Num(JsonElement e, string name) => Prop(e, name) is { } v && v.ValueKind == JsonValueKind.Number && v.TryGetDecimal(out var d) ? d : null;

    private static string DocumentTypeLabel(string type) => type switch
    {
        "invoice" => "Facture",
        "deposit_invoice" => "Facture d'acompte",
        "final_invoice" => "Facture de solde",
        "proforma" => "Facture proforma",
        "credit_note" => "Avoir",
        "receipt" => "Reçu",
        "delivery_note" => "Bon de livraison",
        _ => "Document",
    };

    private static string QuoteStatusLabel(string status) => status switch
    {
        "draft" => "Brouillon",
        "sent" => "Envoyé",
        "accepted" => "Accepté",
        "rejected" => "Refusé",
        "expired" => "Expiré",
        "converted" => "Converti en commande",
        "cancelled" => "Annulé",
        _ => status,
    };

    private static string SalesOrderStatusLabel(string status) => status switch
    {
        "draft" => "Brouillon",
        "confirmed" => "Confirmée",
        "reserved" => "Stock réservé",
        "partially_fulfilled" => "Partiellement livrée",
        "fulfilled" => "Livrée",
        "cancelled" => "Annulée",
        _ => status,
    };

    private static string OrderTypeLabel(string type) => type switch
    {
        "pickup" => "Retrait en magasin",
        "delivery" => "Livraison",
        "wholesale" => "Vente en gros",
        "reservation" => "Réservation",
        _ => type,
    };
}
