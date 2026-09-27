using Microsoft.EntityFrameworkCore;
using Nexora.Domain.Sales;

namespace Nexora.Infrastructure.Persistence;

public sealed partial class NexoraDbContext
{
    partial void ConfigureDocuments(ModelBuilder b)
    {
        b.Entity<Quote>(e =>
        {
            e.ToTable("quote");
            e.HasKey(x => x.Id);
            Cols(e, ("Id", "id"), ("CompanyId", "company_id"), ("EstablishmentId", "establishment_id"),
                ("CustomerId", "customer_id"), ("DocumentNumber", "document_number"), ("CurrencyId", "currency_id"),
                ("Status", "status"), ("Version", "version"), ("QuoteDate", "quote_date"), ("ValidUntil", "valid_until"),
                ("Subtotal", "subtotal"), ("DiscountTotal", "discount_total"), ("TaxTotal", "tax_total"),
                ("Total", "total"), ("Terms", "terms"), ("Notes", "notes"), ("SentAt", "sent_at"),
                ("DecidedAt", "decided_at"), ("ConvertedOrderId", "converted_order_id"), ("CreatedBy", "created_by"));
            Timestamps(e);
            e.HasMany(x => x.Lines).WithOne().HasForeignKey(x => x.QuoteId);
        });

        b.Entity<QuoteLine>(e =>
        {
            e.ToTable("quote_line");
            e.HasKey(x => x.Id);
            Cols(e, ("Id", "id"), ("QuoteId", "quote_id"), ("CompanyId", "company_id"), ("ProductId", "product_id"),
                ("Description", "description"), ("Quantity", "quantity"), ("UnitPrice", "unit_price"),
                ("Discount", "discount"), ("TaxId", "tax_id"), ("TaxAmount", "tax_amount"),
                ("LineTotal", "line_total"), ("LineNumber", "line_number"));
            Timestamps(e);
        });
    }
}
