using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Nexora.Application.Abstractions;
using Nexora.Application.Documents;
using Nexora.Application.Finance.Abstractions;
using Nexora.Application.Sales;
using Nexora.Contracts.Documents;
using Nexora.Contracts.Sales;
using Nexora.Domain.Common;
using Nexora.Domain.Sales;
using Nexora.Infrastructure.Persistence;

namespace Nexora.Infrastructure.Documents;

// =============================================================================
// Moteur documentaire commercial (Phase 30). issued_document = registre unique ;
// le contenu est figé (snapshot jsonb + hash) à l'émission → document reconstituable (§2).
// =============================================================================

public sealed class EfQuoteStore(
    NexoraDbContext db, ITenantContext tenant, IDocumentNumberGenerator numbers, IPermissionService permissions) : IQuoteStore
{
    /// <summary>Guide §7.2 : sans <c>documents.view_all</c>, on ne lit que ses propres devis.</summary>
    private async Task<bool> SeesAllAsync(CancellationToken ct)
        => (await permissions.CurrentPermissionsAsync(tenant, ct)).Contains(Nexora.Application.Security.Permissions.Documents.ViewAll);

    private Guid Company => tenant.ActiveCompanyId ?? throw TenantErrors.ActiveCompanyRequired();

    public Task<Guid> ResolveCurrencyIdAsync(string code, CancellationToken ct = default)
        => db.Database.SqlQuery<Guid>($"""SELECT id AS "Value" FROM nexora.currency WHERE code = {code}""").FirstAsync(ct);

    public Task<string> CurrencyCodeAsync(Guid currencyId, CancellationToken ct = default)
        => db.Database.SqlQuery<string>($"""SELECT code AS "Value" FROM nexora.currency WHERE id = {currencyId}""").FirstAsync(ct);

    public Task<string> NextNumberAsync(string documentType, CancellationToken ct = default)
        => numbers.NextAsync(documentType, null, ct);

    public async Task AddAsync(Quote quote, CancellationToken ct = default) => await db.Quotes.AddAsync(quote, ct);

    public async Task<Guid?> CurrencyIdOrNullAsync(string code, CancellationToken ct = default)
        => await db.Database.SqlQuery<Guid?>($"""SELECT id AS "Value" FROM nexora.currency WHERE code = {code}""").FirstOrDefaultAsync(ct);

    public Task ReplaceLinesAsync(Quote quote, IReadOnlyList<QuoteLine> lines, CancellationToken ct = default)
    {
        db.Set<QuoteLine>().RemoveRange(quote.Lines);
        quote.Lines.Clear();
        // Clé fixée côté application : ajout explicite (sinon EF croirait à une mise à jour).
        db.Set<QuoteLine>().AddRange(lines);
        foreach (var l in lines) if (!quote.Lines.Contains(l)) quote.Lines.Add(l);
        return Task.CompletedTask;
    }

    public Task<Quote?> GetAsync(Guid id, CancellationToken ct = default)
        => db.Quotes.Include(q => q.Lines).FirstOrDefaultAsync(q => q.Id == id && q.CompanyId == Company, ct);

    public Task<string?> CustomerNameAsync(Guid customerId, CancellationToken ct = default)
        => db.Database.SqlQuery<string?>($"""
            SELECT name AS "Value" FROM nexora.customer WHERE id = {customerId} AND company_id = {Company}
            """).FirstOrDefaultAsync(ct);

    public Task<int> ExpireOverdueAsync(DateOnly today, CancellationToken ct = default)
        => db.Database.ExecuteSqlAsync($"""
            UPDATE nexora.quote SET status = 'expired', updated_at = now()
            WHERE company_id = {Company} AND status = 'sent' AND valid_until IS NOT NULL AND valid_until < {today}
            """);

    private sealed record QuoteHead(
        Guid Id, string DocumentNumber, string Status, int Version, Guid? CustomerId, string? CustomerName,
        string Currency, DateOnly QuoteDate, DateOnly? ValidUntil, decimal Subtotal, decimal DiscountTotal,
        decimal TaxTotal, decimal Total, string? Terms, string? Notes, Guid? ConvertedOrderId,
        DateTimeOffset? SentAt, DateTimeOffset CreatedAt);
    private sealed record QuoteLineRow(
        Guid Id, Guid ProductId, string Description, decimal Quantity, decimal UnitPrice,
        decimal Discount, decimal TaxAmount, decimal LineTotal);

    public async Task<QuoteDto?> GetDtoAsync(Guid id, CancellationToken ct = default)
    {
        var all = await SeesAllAsync(ct);
        var h = await db.Database.SqlQuery<QuoteHead>($"""
            SELECT q.id AS "Id", q.document_number AS "DocumentNumber", q.status AS "Status", q.version AS "Version",
                   q.customer_id AS "CustomerId", c.name AS "CustomerName", cur.code AS "Currency",
                   q.quote_date AS "QuoteDate", q.valid_until AS "ValidUntil", q.subtotal AS "Subtotal",
                   q.discount_total AS "DiscountTotal", q.tax_total AS "TaxTotal", q.total AS "Total",
                   q.terms AS "Terms", q.notes AS "Notes", q.converted_order_id AS "ConvertedOrderId",
                   q.sent_at AS "SentAt", q.created_at AS "CreatedAt"
            FROM nexora.quote q
            JOIN nexora.currency cur ON cur.id = q.currency_id
            LEFT JOIN nexora.customer c ON c.id = q.customer_id
            WHERE q.id = {id} AND q.company_id = {Company} AND ({all} OR q.created_by = {tenant.UserId})
            """).FirstOrDefaultAsync(ct);
        if (h is null) return null;

        var lines = await db.Database.SqlQuery<QuoteLineRow>($"""
            SELECT l.id AS "Id", l.product_id AS "ProductId", l.description AS "Description", l.quantity AS "Quantity",
                   l.unit_price AS "UnitPrice", l.discount AS "Discount", l.tax_amount AS "TaxAmount", l.line_total AS "LineTotal"
            FROM nexora.quote_line l WHERE l.quote_id = {id} ORDER BY l.line_number
            """).ToListAsync(ct);

        return new QuoteDto(h.Id, h.DocumentNumber, h.Status, h.Version, h.CustomerId, h.CustomerName, h.Currency,
            h.QuoteDate, h.ValidUntil, h.Subtotal, h.DiscountTotal, h.TaxTotal, h.Total, h.Terms, h.Notes,
            h.ConvertedOrderId, h.SentAt, h.CreatedAt,
            lines.Select(l => new QuoteLineDto(l.Id, l.ProductId, l.Description, l.Quantity, l.UnitPrice,
                l.Discount, l.TaxAmount, l.LineTotal)).ToList());
    }

    public async Task<IReadOnlyList<QuoteDto>> ListAsync(string? status, Guid? customerId, int limit, CancellationToken ct = default)
    {
        var all = await SeesAllAsync(ct);
        var rows = await db.Database.SqlQuery<QuoteHead>($"""
            SELECT q.id AS "Id", q.document_number AS "DocumentNumber", q.status AS "Status", q.version AS "Version",
                   q.customer_id AS "CustomerId", c.name AS "CustomerName", cur.code AS "Currency",
                   q.quote_date AS "QuoteDate", q.valid_until AS "ValidUntil", q.subtotal AS "Subtotal",
                   q.discount_total AS "DiscountTotal", q.tax_total AS "TaxTotal", q.total AS "Total",
                   q.terms AS "Terms", q.notes AS "Notes", q.converted_order_id AS "ConvertedOrderId",
                   q.sent_at AS "SentAt", q.created_at AS "CreatedAt"
            FROM nexora.quote q
            JOIN nexora.currency cur ON cur.id = q.currency_id
            LEFT JOIN nexora.customer c ON c.id = q.customer_id
            WHERE q.company_id = {Company} AND ({all} OR q.created_by = {tenant.UserId})
              AND ({status}::text IS NULL OR q.status = {status})
              AND ({customerId}::uuid IS NULL OR q.customer_id = {customerId})
            ORDER BY q.quote_date DESC, q.created_at DESC
            LIMIT {limit}
            """).ToListAsync(ct);
        return rows.Select(h => new QuoteDto(h.Id, h.DocumentNumber, h.Status, h.Version, h.CustomerId, h.CustomerName,
            h.Currency, h.QuoteDate, h.ValidUntil, h.Subtotal, h.DiscountTotal, h.TaxTotal, h.Total, h.Terms, h.Notes,
            h.ConvertedOrderId, h.SentAt, h.CreatedAt, [])).ToList();
    }
}

// -----------------------------------------------------------------------------

public sealed class SqlDocumentService(
    NexoraDbContext db, IUnitOfWork uow, ITenantContext tenant,
    IDocumentNumberGenerator numbers, IDateTimeProvider clock, IPermissionService permissions) : IDocumentService
{
    private Guid Company => tenant.ActiveCompanyId ?? throw TenantErrors.ActiveCompanyRequired();

    private static readonly JsonSerializerOptions SnapshotJson = new(JsonSerializerDefaults.Web);

    private static readonly Dictionary<string, string[]> AllowedSources = new()
    {
        ["invoice"] = ["sale"],
        ["deposit_invoice"] = ["sales_order"],
        ["final_invoice"] = ["sale"],
        ["proforma"] = ["sale", "sales_order", "quote"],
        ["credit_note"] = ["sale_return"],
        ["receipt"] = ["payment"],
        ["delivery_note"] = ["sales_order", "sale"],
    };

    private sealed record SourceData(
        string? PartyType, Guid? PartyId, string? PartyName, string Currency, Guid CurrencyId,
        decimal TotalNet, decimal TotalTax, decimal Total, DateOnly IssueDate, DateOnly? ValidUntil,
        Guid? EstablishmentId, object Lines, string SourceNumber, string? Blocked = null);

    public async Task<Result<IssuedDocumentDto>> IssueAsync(IssueDocumentRequest r, CancellationToken ct = default)
    {
        if (!AllowedSources.TryGetValue(r.Type, out var sources))
            return Error.Validation("document.type", $"Type de document « {r.Type} » non émissible.");
        if (!sources.Contains(r.SourceType))
            return Error.Validation("document.source", $"Un « {r.Type} » ne s'émet pas depuis « {r.SourceType} ».");

        var src = await LoadSourceAsync(r.SourceType, r.SourceId, ct);
        if (src is null) return Error.NotFound("document.source_not_found", "Source du document introuvable.");
        if (src.Blocked is { } blocked) return Error.Conflict("document.source_state", blocked);

        var existing = await db.Database.SqlQuery<Guid>($"""
            SELECT id AS "Value" FROM nexora.issued_document
            WHERE company_id = {Company} AND document_type = {r.Type}
              AND reference_type = {r.SourceType} AND reference_id = {r.SourceId}
              AND status NOT IN ('cancelled','replaced')
            """).FirstOrDefaultAsync(ct);
        if (existing != Guid.Empty)
            return Error.Conflict("document.already_issued", "Un document de ce type existe déjà pour cette source (annuler d'abord).");

        var company = await db.Database.SqlQuery<CompanyRow>($"""
            SELECT co.name AS "Name", co.legal_name AS "LegalName", co.tax_identifier AS "TaxId",
                   b.document_footer AS "Footer", b.default_locale AS "Locale"
            FROM nexora.company co
            LEFT JOIN nexora.company_branding b ON b.company_id = co.id
            WHERE co.id = {Company}
            """).FirstAsync(ct);

        var language = r.Language ?? company.Locale ?? "fr-FR";
        var id = Guid.NewGuid();
        var number = await numbers.NextAsync($"document:{r.Type}", null, ct);
        var verification = Convert.ToHexString(RandomNumberGenerator.GetBytes(10));

        var snapshot = new
        {
            document = new { id, type = r.Type, number, language, issuedAt = clock.UtcNow, issuedBy = tenant.UserId },
            issuer = new { name = company.Name, legalName = company.LegalName, taxId = company.TaxId, footer = company.Footer },
            party = new { type = src.PartyType, id = src.PartyId, name = src.PartyName },
            source = new { type = r.SourceType, id = r.SourceId, number = src.SourceNumber },
            currency = src.Currency,
            issueDate = src.IssueDate,
            validUntil = src.ValidUntil,
            totals = new { net = src.TotalNet, tax = src.TotalTax, total = src.Total },
            lines = src.Lines,
        };
        var json = JsonSerializer.Serialize(snapshot, SnapshotJson);
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json)));

        await db.Database.ExecuteSqlAsync($"""
            INSERT INTO nexora.issued_document
              (id, company_id, establishment_id, document_type, document_number, reference_type, reference_id,
               party_type, party_id, issue_date, valid_until, currency_id, total, total_net, total_tax,
               status, content, content_hash, verification_code, language, issued_at, issued_by, created_by)
            VALUES ({id}, {Company}, {src.EstablishmentId}, {r.Type}, {number}, {r.SourceType}, {r.SourceId},
                    {src.PartyType}, {src.PartyId}, {src.IssueDate}, {src.ValidUntil}, {src.CurrencyId},
                    {src.Total}, {src.TotalNet}, {src.TotalTax}, 'issued', {json}::jsonb, {hash}, {verification},
                    {language}, {clock.UtcNow}, {tenant.UserId}, {tenant.UserId})
            """, ct);

        return Result.Success(new IssuedDocumentDto(
            id, r.Type, number, "issued", language, src.PartyType, src.PartyId, src.PartyName,
            r.SourceType, r.SourceId, src.Currency, src.TotalNet, src.TotalTax, src.Total,
            src.IssueDate, src.ValidUntil, hash, verification, null, null, clock.UtcNow, clock.UtcNow));
    }

    public async Task<Result> CancelAsync(Guid documentId, CancelDocumentRequest r, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(r.Reason))
            return Error.Validation("document.cancel_reason", "Un motif d'annulation est obligatoire (§31).");
        var status = await db.Database.SqlQuery<string>($"""
            SELECT status AS "Value" FROM nexora.issued_document WHERE id = {documentId} AND company_id = {Company}
            """).FirstOrDefaultAsync(ct);
        if (status is null) return Error.NotFound("document.not_found", "Document introuvable.");
        if (status is "cancelled" or "replaced") return Error.Conflict("document.closed", "Ce document est déjà annulé / remplacé.");

        var newStatus = r.ReplacedByDocumentId is null ? "cancelled" : "replaced";
        await db.Database.ExecuteSqlAsync($"""
            UPDATE nexora.issued_document
               SET status = {newStatus}, cancel_reason = {r.Reason.Trim()}, cancelled_at = now(),
                   cancelled_by = {tenant.UserId}, replaced_by_document_id = {r.ReplacedByDocumentId}, updated_at = now()
             WHERE id = {documentId} AND company_id = {Company}
            """, ct);
        return Result.Success();
    }

    public async Task<Result<RenderedDocument>> RenderAsync(Guid documentId, string? format, CancellationToken ct = default)
    {
        // Guide §7.2 : sans documents.view_all, seuls les documents émis par l'utilisateur (ses tickets).
        var all = (await permissions.CurrentPermissionsAsync(tenant, ct)).Contains(Nexora.Application.Security.Permissions.Documents.ViewAll);
        var row = await db.Database.SqlQuery<RenderRow>($"""
            SELECT document_type AS "DocumentType", document_number AS "DocumentNumber", status AS "Status",
                   verification_code AS "VerificationCode", content::text AS "Content"
            FROM nexora.issued_document
            WHERE id = {documentId} AND company_id = {Company}
              AND ({all} OR created_by = {tenant.UserId} OR issued_by = {tenant.UserId})
            """).FirstOrDefaultAsync(ct);
        if (row is null) return Error.NotFound("document.not_found", "Document introuvable.");

        var doc = JsonDocument.Parse(row.Content);
        var html = BuildHtml(doc.RootElement, row.DocumentType, row.DocumentNumber, row.Status, row.VerificationCode,
            compact: string.Equals(format, "ticket", StringComparison.OrdinalIgnoreCase));
        return Result.Success(new RenderedDocument("text/html; charset=utf-8", html, $"{row.DocumentNumber}.html"));
    }

    public async Task<Result<Guid>> SendAsync(Guid documentId, SendDocumentRequest r, CancellationToken ct = default)
    {
        var channels = new[] { "email", "link", "print", "sms", "download", "portal" };
        if (!channels.Contains(r.Channel)) return Error.Validation("document.send_channel", "Canal d'envoi inconnu.");
        var exists = await db.Database.SqlQuery<int>($"""
            SELECT COUNT(*)::int AS "Value" FROM nexora.issued_document WHERE id = {documentId} AND company_id = {Company}
            """).FirstAsync(ct);
        if (exists == 0) return Error.NotFound("document.not_found", "Document introuvable.");

        var id = Guid.NewGuid();
        // Envoi réel = connecteur Phase 12 ; ici on journalise (§36). email/sms sans intégration → 'queued'.
        var status = r.Channel is "email" or "sms" ? "queued" : "sent";
        DateTimeOffset? sentAt = status == "sent" ? clock.UtcNow : null;
        await uow.ExecuteInTransactionAsync(async token =>
        {
            await db.Database.ExecuteSqlAsync($"""
                INSERT INTO nexora.document_send
                  (id, company_id, issued_document_id, channel, recipient, subject, message, status, sent_at, sent_by)
                VALUES ({id}, {Company}, {documentId}, {r.Channel}, {r.Recipient}, {r.Subject}, {r.Message}, {status},
                        {sentAt}, {tenant.UserId})
                """, token);
            await db.Database.ExecuteSqlAsync($"""
                UPDATE nexora.issued_document SET status = CASE WHEN status = 'issued' THEN 'sent' ELSE status END, updated_at = now()
                WHERE id = {documentId} AND company_id = {Company}
                """, token);
            return 0;
        }, ct);
        return Result.Success(id);
    }

    public async Task<DocumentVerificationDto> VerifyAsync(string verificationCode, CancellationToken ct = default)
    {
        using var _ = tenant.BeginSystemScope();   // vérification publique — hors périmètre entreprise (§28)
        var row = await db.Database.SqlQuery<VerifyRow>($"""
            SELECT d.document_type AS "DocumentType", d.document_number AS "DocumentNumber", d.status AS "Status",
                   d.issue_date AS "IssueDate", co.name AS "CompanyName", d.total AS "Total", cur.code AS "Currency"
            FROM nexora.issued_document d
            JOIN nexora.company co ON co.id = d.company_id
            LEFT JOIN nexora.currency cur ON cur.id = d.currency_id
            WHERE d.verification_code = {verificationCode}
            """).FirstOrDefaultAsync(ct);

        if (row is null)
            return new DocumentVerificationDto(false, null, null, null, null, null, null, null, "Document introuvable.");

        var msg = row.Status switch
        {
            "issued" or "sent" => "Document valide.",
            "cancelled" => "Document annulé.",
            "replaced" => "Document remplacé par une version corrective.",
            _ => $"Statut : {row.Status}.",
        };
        return new DocumentVerificationDto(true, row.DocumentType, row.DocumentNumber, row.Status,
            row.IssueDate, row.CompanyName, row.Total, row.Currency, msg);
    }

    // --- chargement des sources ------------------------------------------

    private async Task<SourceData?> LoadSourceAsync(string sourceType, Guid sourceId, CancellationToken ct) => sourceType switch
    {
        "sale" => await LoadSaleAsync(sourceId, ct),
        "sale_return" => await LoadReturnAsync(sourceId, ct),
        "payment" => await LoadPaymentAsync(sourceId, ct),
        "sales_order" => await LoadOrderAsync(sourceId, ct),
        "quote" => await LoadQuoteAsync(sourceId, ct),
        _ => null,
    };

    private sealed record SaleRow(Guid? CustomerId, string? CustomerName, string Currency, Guid CurrencyId,
        decimal Subtotal, decimal DiscountTotal, decimal TaxTotal, decimal Total, DateOnly SoldOn,
        Guid? EstablishmentId, string DocumentNumber, string Status);
    private sealed record LineRow(string Description, decimal Quantity, decimal UnitPrice, decimal Discount, decimal TaxAmount, decimal LineTotal);

    private async Task<SourceData?> LoadSaleAsync(Guid id, CancellationToken ct)
    {
        var s = await db.Database.SqlQuery<SaleRow>($"""
            SELECT s.customer_id AS "CustomerId", c.name AS "CustomerName", cur.code AS "Currency", s.currency_id AS "CurrencyId",
                   s.subtotal AS "Subtotal", s.discount_total AS "DiscountTotal", s.tax_total AS "TaxTotal", s.total AS "Total",
                   s.sold_at::date AS "SoldOn", s.establishment_id AS "EstablishmentId", s.document_number AS "DocumentNumber",
                   s.status AS "Status"
            FROM nexora.sale s JOIN nexora.currency cur ON cur.id = s.currency_id
            LEFT JOIN nexora.customer c ON c.id = s.customer_id
            WHERE s.id = {id} AND s.company_id = {Company}
            """).FirstOrDefaultAsync(ct);
        if (s is null) return null;
        var lines = await db.Database.SqlQuery<LineRow>($"""
            SELECT p.name AS "Description", l.quantity AS "Quantity", l.unit_price AS "UnitPrice",
                   l.discount AS "Discount", l.tax_amount AS "TaxAmount", l.line_total AS "LineTotal"
            FROM nexora.sale_line l JOIN nexora.product p ON p.id = l.product_id
            WHERE l.sale_id = {id} ORDER BY p.name
            """).ToListAsync(ct);
        // Une vente annulée, en attente ou non encaissée ne se facture pas.
        var blocked = s.Status switch
        {
            "void" => $"La vente {s.DocumentNumber} est annulée : elle ne se facture pas.",
            "draft" or "parked" => $"La vente {s.DocumentNumber} n'est pas finalisée.",
            _ => null,
        };
        return new SourceData("customer", s.CustomerId, s.CustomerName, s.Currency, s.CurrencyId,
            s.Subtotal - s.DiscountTotal, s.TaxTotal, s.Total, s.SoldOn, null, s.EstablishmentId, lines, s.DocumentNumber, blocked);
    }

    private sealed record ReturnRow(Guid? CustomerId, string? CustomerName, string Currency, Guid CurrencyId,
        decimal RefundTotal, DateOnly ReturnedOn, string DocumentNumber, string? Reason);

    private async Task<SourceData?> LoadReturnAsync(Guid id, CancellationToken ct)
    {
        var rr = await db.Database.SqlQuery<ReturnRow>($"""
            SELECT s.customer_id AS "CustomerId", c.name AS "CustomerName", cur.code AS "Currency", r.currency_id AS "CurrencyId",
                   r.refund_total AS "RefundTotal", r.returned_at::date AS "ReturnedOn", r.document_number AS "DocumentNumber",
                   r.reason AS "Reason"
            FROM nexora.sale_return r
            JOIN nexora.sale s ON s.id = r.sale_id
            JOIN nexora.currency cur ON cur.id = r.currency_id
            LEFT JOIN nexora.customer c ON c.id = s.customer_id
            WHERE r.id = {id} AND r.company_id = {Company}
            """).FirstOrDefaultAsync(ct);
        if (rr is null) return null;
        var lines = await db.Database.SqlQuery<LineRow>($"""
            SELECT p.name AS "Description", l.quantity AS "Quantity", l.unit_price AS "UnitPrice",
                   0::numeric AS "Discount", l.tax_amount AS "TaxAmount", l.line_total AS "LineTotal"
            FROM nexora.sale_return_line l JOIN nexora.product p ON p.id = l.product_id
            WHERE l.sale_return_id = {id} ORDER BY p.name
            """).ToListAsync(ct);
        return new SourceData("customer", rr.CustomerId, rr.CustomerName, rr.Currency, rr.CurrencyId,
            rr.RefundTotal, 0m, rr.RefundTotal, rr.ReturnedOn, null, null, lines, rr.DocumentNumber);
    }

    private sealed record PaymentRow(string? PartyType, Guid? CustomerId, Guid? SupplierId, string? PartyName,
        string Currency, Guid CurrencyId, decimal Amount, DateOnly ReceivedOn, string PaymentNumber, string? Method);

    private async Task<SourceData?> LoadPaymentAsync(Guid id, CancellationToken ct)
    {
        var p = await db.Database.SqlQuery<PaymentRow>($"""
            SELECT pay.party_type AS "PartyType", pay.customer_id AS "CustomerId", pay.supplier_id AS "SupplierId",
                   COALESCE(c.name, su.name) AS "PartyName", cur.code AS "Currency", pay.currency_id AS "CurrencyId",
                   pay.amount AS "Amount", pay.received_at::date AS "ReceivedOn", pay.payment_number AS "PaymentNumber",
                   pm.name AS "Method"
            FROM nexora.payment pay
            JOIN nexora.currency cur ON cur.id = pay.currency_id
            LEFT JOIN nexora.customer c ON c.id = pay.customer_id
            LEFT JOIN nexora.supplier su ON su.id = pay.supplier_id
            LEFT JOIN nexora.payment_method pm ON pm.id = pay.payment_method_id
            WHERE pay.id = {id} AND pay.company_id = {Company}
            """).FirstOrDefaultAsync(ct);
        if (p is null) return null;
        var partyId = p.CustomerId ?? p.SupplierId;
        object lines = new[] { new { description = $"Règlement {p.PaymentNumber}", method = p.Method, amount = p.Amount } };
        return new SourceData(p.PartyType, partyId, p.PartyName, p.Currency, p.CurrencyId,
            p.Amount, 0m, p.Amount, p.ReceivedOn, null, null, lines, p.PaymentNumber);
    }

    private sealed record OrderRow(Guid? CustomerId, string? CustomerName, string Currency, Guid CurrencyId,
        decimal Subtotal, decimal DiscountTotal, decimal TaxTotal, decimal Total, Guid? EstablishmentId, string DocumentNumber);

    private async Task<SourceData?> LoadOrderAsync(Guid id, CancellationToken ct)
    {
        var o = await db.Database.SqlQuery<OrderRow>($"""
            SELECT o.customer_id AS "CustomerId", c.name AS "CustomerName", cur.code AS "Currency", o.currency_id AS "CurrencyId",
                   o.subtotal AS "Subtotal", o.discount_total AS "DiscountTotal", o.tax_total AS "TaxTotal", o.total AS "Total",
                   o.establishment_id AS "EstablishmentId", o.document_number AS "DocumentNumber"
            FROM nexora.sales_order o JOIN nexora.currency cur ON cur.id = o.currency_id
            LEFT JOIN nexora.customer c ON c.id = o.customer_id
            WHERE o.id = {id} AND o.company_id = {Company}
            """).FirstOrDefaultAsync(ct);
        if (o is null) return null;
        var lines = await db.Database.SqlQuery<LineRow>($"""
            SELECT l.description AS "Description", l.quantity AS "Quantity", l.unit_price AS "UnitPrice",
                   l.discount AS "Discount", l.tax_amount AS "TaxAmount", l.line_total AS "LineTotal"
            FROM nexora.sales_order_line l WHERE l.sales_order_id = {id} ORDER BY l.description
            """).ToListAsync(ct);
        return new SourceData("customer", o.CustomerId, o.CustomerName, o.Currency, o.CurrencyId,
            o.Subtotal - o.DiscountTotal, o.TaxTotal, o.Total, DateOnly.FromDateTime(clock.UtcNow.UtcDateTime),
            null, o.EstablishmentId, lines, o.DocumentNumber);
    }

    private async Task<SourceData?> LoadQuoteAsync(Guid id, CancellationToken ct)
    {
        var q = await db.Database.SqlQuery<OrderRow>($"""
            SELECT q.customer_id AS "CustomerId", c.name AS "CustomerName", cur.code AS "Currency", q.currency_id AS "CurrencyId",
                   q.subtotal AS "Subtotal", q.discount_total AS "DiscountTotal", q.tax_total AS "TaxTotal", q.total AS "Total",
                   q.establishment_id AS "EstablishmentId", q.document_number AS "DocumentNumber"
            FROM nexora.quote q JOIN nexora.currency cur ON cur.id = q.currency_id
            LEFT JOIN nexora.customer c ON c.id = q.customer_id
            WHERE q.id = {id} AND q.company_id = {Company}
            """).FirstOrDefaultAsync(ct);
        if (q is null) return null;
        var lines = await db.Database.SqlQuery<LineRow>($"""
            SELECT l.description AS "Description", l.quantity AS "Quantity", l.unit_price AS "UnitPrice",
                   l.discount AS "Discount", l.tax_amount AS "TaxAmount", l.line_total AS "LineTotal"
            FROM nexora.quote_line l WHERE l.quote_id = {id} ORDER BY l.line_number
            """).ToListAsync(ct);
        return new SourceData("customer", q.CustomerId, q.CustomerName, q.Currency, q.CurrencyId,
            q.Subtotal - q.DiscountTotal, q.TaxTotal, q.Total, DateOnly.FromDateTime(clock.UtcNow.UtcDateTime),
            null, q.EstablishmentId, lines, q.DocumentNumber);
    }

    private sealed record CompanyRow(string Name, string? LegalName, string? TaxId, string? Footer, string? Locale);
    private sealed record RenderRow(string DocumentType, string DocumentNumber, string Status, string? VerificationCode, string Content);
    private sealed record VerifyRow(string DocumentType, string DocumentNumber, string Status, DateOnly IssueDate,
        string CompanyName, decimal Total, string? Currency);

    private static string BuildHtml(JsonElement c, string type, string number, string status, string? verification, bool compact)
    {
        string S(JsonElement e, string prop) => e.TryGetProperty(prop, out var v) && v.ValueKind is not JsonValueKind.Null ? v.ToString() : "";
        var issuer = c.GetProperty("issuer");
        var party = c.GetProperty("party");
        var totals = c.GetProperty("totals");
        var currency = S(c, "currency");
        var width = compact ? "300px" : "780px";

        var sb = new StringBuilder();
        sb.Append("<!doctype html><meta charset=\"utf-8\"><style>");
        sb.Append($"body{{font-family:system-ui,Arial,sans-serif;max-width:{width};margin:24px auto;color:#1a1a1a}}");
        sb.Append("h1{font-size:18px;margin:0 0 2px} .muted{color:#666;font-size:12px}");
        sb.Append("table{width:100%;border-collapse:collapse;margin:16px 0;font-size:13px}");
        sb.Append("th,td{text-align:left;padding:6px 4px;border-bottom:1px solid #eee} td.n,th.n{text-align:right}");
        sb.Append(".tot{font-weight:600} .badge{display:inline-block;padding:2px 8px;border-radius:10px;background:#eee;font-size:11px}");
        sb.Append("</style>");
        sb.Append($"<h1>{issuer.GetProperty("name").GetString()}</h1>");
        sb.Append($"<div class=\"muted\">{S(issuer, "legalName")} {(issuer.TryGetProperty("taxId", out var t) && t.ValueKind == JsonValueKind.String ? "· " + t.GetString() : "")}</div>");
        sb.Append($"<h2 style=\"margin-top:18px\">{DocLabel(type)} {number} <span class=\"badge\">{status}</span></h2>");
        sb.Append($"<div class=\"muted\">Date : {S(c, "issueDate")} · Devise : {currency}</div>");
        if (party.TryGetProperty("name", out var pn) && pn.ValueKind == JsonValueKind.String)
            sb.Append($"<div style=\"margin-top:8px\"><strong>{pn.GetString()}</strong></div>");

        sb.Append("<table><thead><tr><th>Désignation</th><th class=\"n\">Qté</th><th class=\"n\">PU</th><th class=\"n\">Total</th></tr></thead><tbody>");
        foreach (var l in c.GetProperty("lines").EnumerateArray())
        {
            sb.Append("<tr>");
            sb.Append($"<td>{S(l, "description")}</td>");
            sb.Append($"<td class=\"n\">{S(l, "quantity")}</td>");
            sb.Append($"<td class=\"n\">{S(l, "unitPrice")}{S(l, "amount")}</td>");
            sb.Append($"<td class=\"n\">{S(l, "lineTotal")}{S(l, "amount")}</td>");
            sb.Append("</tr>");
        }
        sb.Append("</tbody></table>");
        sb.Append($"<div class=\"n\" style=\"text-align:right\">Net HT : {S(totals, "net")} {currency}<br>");
        sb.Append($"Taxes : {S(totals, "tax")} {currency}<br>");
        sb.Append($"<span class=\"tot\">Total : {S(totals, "total")} {currency}</span></div>");

        if (issuer.TryGetProperty("footer", out var f) && f.ValueKind == JsonValueKind.String)
            sb.Append($"<p class=\"muted\" style=\"margin-top:24px\">{f.GetString()}</p>");
        if (verification is not null)
            sb.Append($"<p class=\"muted\">Vérification : /api/v1/documents/verify/{verification}</p>");
        return sb.ToString();
    }

    private static string DocLabel(string type) => type switch
    {
        "invoice" or "final_invoice" => "Facture",
        "deposit_invoice" => "Facture d'acompte",
        "proforma" => "Proforma",
        "credit_note" => "Avoir",
        "receipt" => "Reçu",
        "delivery_note" => "Bon de livraison",
        _ => "Document",
    };
}

// -----------------------------------------------------------------------------

public sealed class SqlDocumentQueries(NexoraDbContext db, ITenantContext tenant, IPermissionService permissions) : IDocumentQueries
{
    private Guid Company => tenant.ActiveCompanyId ?? throw TenantErrors.ActiveCompanyRequired();

    /// <summary>
    /// Guide §7.2 : la liste des factures / reçus de l'entreprise, avec leurs montants, révèle le
    /// chiffre d'affaires. Sans <c>documents.view_all</c> (caissier, vendeur), on ne voit que les
    /// documents qu'on a soi-même créés ou émis — de quoi réimprimer ses tickets, rien de plus.
    /// </summary>
    private async Task<bool> SeesAllAsync(CancellationToken ct)
        => (await permissions.CurrentPermissionsAsync(tenant, ct)).Contains(Nexora.Application.Security.Permissions.Documents.ViewAll);

    private sealed record DocRow(
        Guid Id, string DocumentType, string DocumentNumber, string Status, string Language,
        string? PartyType, Guid? PartyId, string? PartyName, string? ReferenceType, Guid? ReferenceId, string Currency,
        decimal TotalNet, decimal TotalTax, decimal Total, DateOnly IssueDate, DateOnly? ValidUntil,
        string? ContentHash, string? VerificationCode, string? CancelReason, Guid? ReplacedByDocumentId,
        DateTimeOffset? IssuedAt, DateTimeOffset CreatedAt);

    private static IssuedDocumentDto Map(DocRow d) => new(
        d.Id, d.DocumentType, d.DocumentNumber, d.Status, d.Language, d.PartyType, d.PartyId, d.PartyName,
        d.ReferenceType, d.ReferenceId, d.Currency, d.TotalNet, d.TotalTax, d.Total, d.IssueDate, d.ValidUntil,
        d.ContentHash, d.VerificationCode, d.CancelReason, d.ReplacedByDocumentId, d.IssuedAt, d.CreatedAt);

    public async Task<IReadOnlyList<IssuedDocumentDto>> SearchAsync(DocumentSearchRequest r, CancellationToken ct = default)
    {
        var limit = Math.Clamp(r.Limit, 1, 500);
        var all = await SeesAllAsync(ct);
        var rows = await db.Database.SqlQuery<DocRow>($"""
            SELECT d.id AS "Id", d.document_type AS "DocumentType", d.document_number AS "DocumentNumber", d.status AS "Status",
                   d.language AS "Language", d.party_type AS "PartyType", d.party_id AS "PartyId",
                   COALESCE(c.name, su.name) AS "PartyName", d.reference_type AS "ReferenceType", d.reference_id AS "ReferenceId",
                   cur.code AS "Currency", d.total_net AS "TotalNet", d.total_tax AS "TotalTax", d.total AS "Total",
                   d.issue_date AS "IssueDate", d.valid_until AS "ValidUntil", d.content_hash AS "ContentHash",
                   d.verification_code AS "VerificationCode", d.cancel_reason AS "CancelReason",
                   d.replaced_by_document_id AS "ReplacedByDocumentId", d.issued_at AS "IssuedAt", d.created_at AS "CreatedAt"
            FROM nexora.issued_document d
            LEFT JOIN nexora.currency cur ON cur.id = d.currency_id
            LEFT JOIN nexora.customer c ON c.id = d.party_id AND d.party_type = 'customer'
            LEFT JOIN nexora.supplier su ON su.id = d.party_id AND d.party_type = 'supplier'
            WHERE d.company_id = {Company}
              AND ({all} OR d.created_by = {tenant.UserId} OR d.issued_by = {tenant.UserId})
              AND ({r.Type}::text IS NULL OR d.document_type = {r.Type})
              AND ({r.Status}::text IS NULL OR d.status = {r.Status})
              AND ({r.Number}::text IS NULL OR d.document_number ILIKE '%' || {r.Number} || '%')
              AND ({r.PartyId}::uuid IS NULL OR d.party_id = {r.PartyId})
              AND ({r.From}::date IS NULL OR d.issue_date >= {r.From})
              AND ({r.To}::date IS NULL OR d.issue_date <= {r.To})
              AND ({r.SourceType}::text IS NULL OR d.reference_type = {r.SourceType})
              AND ({r.SourceId}::uuid IS NULL OR d.reference_id = {r.SourceId})
            ORDER BY d.issue_date DESC, d.created_at DESC
            LIMIT {limit}
            """).ToListAsync(ct);
        return rows.Select(Map).ToList();
    }

    public async Task<Result<IssuedDocumentDetailDto>> GetAsync(Guid documentId, CancellationToken ct = default)
    {
        var all = await SeesAllAsync(ct);
        var row = await db.Database.SqlQuery<DocRow>($"""
            SELECT d.id AS "Id", d.document_type AS "DocumentType", d.document_number AS "DocumentNumber", d.status AS "Status",
                   d.language AS "Language", d.party_type AS "PartyType", d.party_id AS "PartyId",
                   COALESCE(c.name, su.name) AS "PartyName", d.reference_type AS "ReferenceType", d.reference_id AS "ReferenceId",
                   cur.code AS "Currency", d.total_net AS "TotalNet", d.total_tax AS "TotalTax", d.total AS "Total",
                   d.issue_date AS "IssueDate", d.valid_until AS "ValidUntil", d.content_hash AS "ContentHash",
                   d.verification_code AS "VerificationCode", d.cancel_reason AS "CancelReason",
                   d.replaced_by_document_id AS "ReplacedByDocumentId", d.issued_at AS "IssuedAt", d.created_at AS "CreatedAt"
            FROM nexora.issued_document d
            LEFT JOIN nexora.currency cur ON cur.id = d.currency_id
            LEFT JOIN nexora.customer c ON c.id = d.party_id AND d.party_type = 'customer'
            LEFT JOIN nexora.supplier su ON su.id = d.party_id AND d.party_type = 'supplier'
            WHERE d.company_id = {Company} AND d.id = {documentId}
              AND ({all} OR d.created_by = {tenant.UserId} OR d.issued_by = {tenant.UserId})
            """).FirstOrDefaultAsync(ct);
        if (row is null) return Error.NotFound("document.not_found", "Document introuvable.");
        var content = await db.Database.SqlQuery<string>($"""
            SELECT content::text AS "Value" FROM nexora.issued_document WHERE id = {documentId}
            """).FirstAsync(ct);
        return Result.Success(new IssuedDocumentDetailDto(Map(row), JsonDocument.Parse(content).RootElement.Clone()));
    }

    public async Task<IReadOnlyList<DocumentSendDto>> SendsAsync(Guid documentId, CancellationToken ct = default)
    {
        var all = await SeesAllAsync(ct);
        return await db.Database.SqlQuery<DocumentSendDto>($"""
            SELECT s.id AS "Id", s.channel AS "Channel", s.recipient AS "Recipient", s.subject AS "Subject",
                   s.status AS "Status", s.error AS "Error", s.sent_at AS "SentAt", s.created_at AS "CreatedAt"
            FROM nexora.document_send s
            JOIN nexora.issued_document d ON d.id = s.issued_document_id
            WHERE s.issued_document_id = {documentId} AND s.company_id = {Company}
              AND ({all} OR d.created_by = {tenant.UserId} OR d.issued_by = {tenant.UserId})
            ORDER BY s.created_at DESC
            """).ToListAsync(ct);
    }

    public async Task<IReadOnlyList<DocumentSequenceDto>> SequencesAsync(CancellationToken ct = default)
        => await db.Database.SqlQuery<DocumentSequenceDto>($"""
            SELECT id AS "Id", document_type AS "DocumentType", establishment_id AS "EstablishmentId", year AS "Year",
                   prefix AS "Prefix", include_year AS "IncludeYear", padding AS "Padding",
                   current_number AS "CurrentNumber", status AS "Status"
            FROM nexora.document_sequence WHERE company_id = {Company}
            ORDER BY document_type, year DESC NULLS LAST
            """).ToListAsync(ct);
}
