using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Nexora.Domain.Sales;

namespace Nexora.Infrastructure.Persistence;

public sealed partial class NexoraDbContext
{
    partial void ConfigureSales(ModelBuilder b)
    {
        b.Entity<Sale>(e =>
        {
            e.ToTable("sale");
            e.HasKey(x => x.Id);
            Cols(e, ("Id", "id"), ("CompanyId", "company_id"), ("EstablishmentId", "establishment_id"),
                ("StoreId", "store_id"), ("PosTerminalId", "pos_terminal_id"), ("CustomerId", "customer_id"),
                ("CashSessionId", "cash_session_id"), ("DocumentNumber", "document_number"), ("Status", "status"),
                ("CurrencyId", "currency_id"), ("ExchangeRateId", "exchange_rate_id"), ("SoldAt", "sold_at"),
                ("Subtotal", "subtotal"), ("DiscountTotal", "discount_total"), ("TaxTotal", "tax_total"),
                ("Total", "total"), ("AmountPaid", "amount_paid"), ("AmountTendered", "amount_tendered"),
                ("ChangeGiven", "change_given"), ("SalespersonUserId", "salesperson_user_id"),
                ("SourceOperationUuid", "source_operation_uuid"), ("ParkedReference", "parked_reference"),
                ("ParkedAt", "parked_at"), ("CreditOverrideBy", "credit_override_by"),
                ("CreditOverrideReason", "credit_override_reason"), ("VoidedAt", "voided_at"),
                ("VoidReason", "void_reason"), ("ReversalJournalEntryId", "reversal_journal_entry_id"),
                ("SalesOrderId", "sales_order_id"), ("CreatedBy", "created_by"));
            e.Property(x => x.SaleTypeValue).HasColumnName("sale_type");
            Timestamps(e);
            e.HasMany(x => x.Lines).WithOne().HasForeignKey(x => x.SaleId);
        });

        b.Entity<SalesOrder>(e =>
        {
            e.ToTable("sales_order");
            e.HasKey(x => x.Id);
            Cols(e, ("Id", "id"), ("CompanyId", "company_id"), ("EstablishmentId", "establishment_id"),
                ("CustomerId", "customer_id"), ("DocumentNumber", "document_number"), ("OrderType", "order_type"),
                ("Status", "status"), ("CurrencyId", "currency_id"), ("InventoryLocationId", "inventory_location_id"),
                ("Subtotal", "subtotal"), ("DiscountTotal", "discount_total"), ("TaxTotal", "tax_total"),
                ("Total", "total"), ("DepositAmount", "deposit_amount"), ("DepositPaymentId", "deposit_payment_id"),
                ("OnCredit", "on_credit"), ("ExpectedAt", "expected_at"), ("DueDate", "due_date"),
                ("DeliveryAddress", "delivery_address"), ("Notes", "notes"), ("ReservedAt", "reserved_at"),
                ("FulfilledAt", "fulfilled_at"), ("CancelledReason", "cancelled_reason"), ("CreatedBy", "created_by"));
            Timestamps(e);
            e.HasMany(x => x.Lines).WithOne().HasForeignKey(x => x.SalesOrderId);
        });

        b.Entity<SalesOrderLine>(e =>
        {
            e.ToTable("sales_order_line");
            e.HasKey(x => x.Id);
            Cols(e, ("Id", "id"), ("SalesOrderId", "sales_order_id"), ("CompanyId", "company_id"),
                ("ProductId", "product_id"), ("Description", "description"), ("Quantity", "quantity"),
                ("QuantityFulfilled", "quantity_fulfilled"), ("UnitPrice", "unit_price"), ("Discount", "discount"),
                ("TaxId", "tax_id"), ("TaxAmount", "tax_amount"), ("LineTotal", "line_total"), ("UnitCost", "unit_cost"));
            Timestamps(e);
        });

        b.Entity<SaleLine>(e =>
        {
            e.ToTable("sale_line");
            e.HasKey(x => x.Id);
            Cols(e, ("Id", "id"), ("SaleId", "sale_id"), ("CompanyId", "company_id"), ("ProductId", "product_id"),
                ("Description", "description"), ("Quantity", "quantity"), ("UnitPrice", "unit_price"),
                ("OriginalUnitPrice", "original_unit_price"), ("Discount", "discount"), ("TaxId", "tax_id"),
                ("TaxAmount", "tax_amount"), ("LineTotal", "line_total"), ("CostSnapshot", "cost_snapshot"),
                ("UnitCost", "unit_cost"), ("LotId", "lot_id"), ("InventoryLocationId", "inventory_location_id"),
                ("PromotionId", "promotion_id"));
            Timestamps(e);
        });

        b.Entity<SalePayment>(e =>
        {
            e.ToTable("sale_payment");
            e.HasKey(x => x.Id);
            Cols(e, ("Id", "id"), ("SaleId", "sale_id"), ("CompanyId", "company_id"), ("PaymentId", "payment_id"),
                ("Amount", "amount"), ("CurrencyId", "currency_id"));
            e.Property(x => x.CreatedAt).HasColumnName("created_at").ValueGeneratedOnAdd();
        });

        b.Entity<SalePromotion>(e =>
        {
            e.ToTable("sale_promotion");
            e.HasKey(x => x.Id);
            Cols(e, ("Id", "id"), ("CompanyId", "company_id"), ("SaleId", "sale_id"), ("SaleLineId", "sale_line_id"),
                ("PromotionId", "promotion_id"), ("CouponId", "coupon_id"), ("PromoType", "promo_type"),
                ("Label", "label"), ("Value", "value"), ("DiscountAmount", "discount_amount"));
            e.Property(x => x.CreatedAt).HasColumnName("created_at").ValueGeneratedOnAdd();
        });

        b.Entity<SaleReturn>(e =>
        {
            e.ToTable("sale_return");
            e.HasKey(x => x.Id);
            Cols(e, ("Id", "id"), ("CompanyId", "company_id"), ("SaleId", "sale_id"),
                ("DocumentNumber", "document_number"), ("Status", "status"), ("CurrencyId", "currency_id"),
                ("Reason", "reason"), ("RefundTotal", "refund_total"), ("RefundMethod", "refund_method"),
                ("RefundPaymentId", "refund_payment_id"), ("Restocked", "restocked"),
                ("SourceOperationUuid", "source_operation_uuid"), ("JournalEntryId", "journal_entry_id"),
                ("CashSessionId", "cash_session_id"), ("ReturnedAt", "returned_at"), ("CreatedBy", "created_by"));
            Timestamps(e);
            e.HasMany(x => x.Lines).WithOne().HasForeignKey(x => x.SaleReturnId);
        });

        b.Entity<SaleReturnLine>(e =>
        {
            e.ToTable("sale_return_line");
            e.HasKey(x => x.Id);
            Cols(e, ("Id", "id"), ("SaleReturnId", "sale_return_id"), ("CompanyId", "company_id"),
                ("SaleLineId", "sale_line_id"), ("ProductId", "product_id"), ("Quantity", "quantity"),
                ("UnitPrice", "unit_price"), ("LineTotal", "line_total"), ("TaxAmount", "tax_amount"),
                ("UnitCost", "unit_cost"), ("LotId", "lot_id"), ("Restock", "restock"));
            Timestamps(e);
        });

        b.Entity<CashRegister>(e =>
        {
            e.ToTable("cash_register");
            e.HasKey(x => x.Id);
            Cols(e, ("Id", "id"), ("CompanyId", "company_id"), ("StoreId", "store_id"),
                ("FinancialAccountId", "financial_account_id"), ("Name", "name"), ("Code", "code"),
                ("CurrencyId", "currency_id"), ("Status", "status"));
            Timestamps(e);
        });

        b.Entity<CashSession>(e =>
        {
            e.ToTable("cash_session");
            e.HasKey(x => x.Id);
            Cols(e, ("Id", "id"), ("CompanyId", "company_id"), ("CashRegisterId", "cash_register_id"),
                ("PosTerminalId", "pos_terminal_id"), ("UserId", "user_id"), ("OpenedAt", "opened_at"),
                ("ClosedAt", "closed_at"), ("OpeningBalance", "opening_balance"),
                ("ExpectedBalance", "expected_balance"), ("CountedBalance", "counted_balance"),
                ("Status", "status"), ("Notes", "notes"));
            e.Property(x => x.Difference).HasColumnName("difference").ValueGeneratedOnAddOrUpdate();
            e.Property(x => x.Difference).Metadata.SetAfterSaveBehavior(PropertySaveBehavior.Ignore);
            e.Property(x => x.Difference).Metadata.SetBeforeSaveBehavior(PropertySaveBehavior.Ignore);
            Timestamps(e);
        });

        b.Entity<CashMovement>(e =>
        {
            e.ToTable("cash_movement");
            e.HasKey(x => x.Id);
            Cols(e, ("Id", "id"), ("CompanyId", "company_id"), ("CashSessionId", "cash_session_id"),
                ("MovementType", "movement_type"), ("Amount", "amount"), ("CurrencyId", "currency_id"),
                ("ReferenceType", "reference_type"), ("ReferenceId", "reference_id"), ("Note", "note"),
                ("OccurredAt", "occurred_at"), ("UserId", "user_id"), ("SourceOperationUuid", "source_operation_uuid"));
            e.Property(x => x.CreatedAt).HasColumnName("created_at").ValueGeneratedOnAdd();
        });
    }
}
