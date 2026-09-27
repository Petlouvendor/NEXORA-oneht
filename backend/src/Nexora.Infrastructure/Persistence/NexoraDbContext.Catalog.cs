using Microsoft.EntityFrameworkCore;
using Nexora.Domain.Catalog;
using Nexora.Domain.Inventory;
using Nexora.Domain.Purchasing;

namespace Nexora.Infrastructure.Persistence;

public sealed partial class NexoraDbContext
{
    partial void ConfigureCatalog(ModelBuilder b)
    {
        b.Entity<Product>(e =>
        {
            e.ToTable("product");
            e.HasKey(x => x.Id);
            Cols(e, ("Id", "id"), ("CompanyId", "company_id"), ("Sku", "sku"), ("Barcode", "barcode"),
                ("Name", "name"), ("Description", "description"), ("CategoryId", "category_id"),
                ("BrandId", "brand_id"), ("UnitId", "unit_id"), ("PurchaseUnitId", "purchase_unit_id"),
                ("DefaultTaxId", "default_tax_id"), ("ParentProductId", "parent_product_id"),
                ("ProductType", "product_type"), ("CostMethod", "cost_method"),
                ("ValuationMethod", "valuation_method"), ("Status", "status"),
                ("TrackLots", "track_lots"), ("IsWeighed", "is_weighed"),
                ("IsSellable", "is_sellable"), ("IsPurchasable", "is_purchasable"),
                ("ShortName", "short_name"), ("PluCode", "plu_code"), ("ManufacturerRef", "manufacturer_ref"),
                ("InternalRef", "internal_ref"), ("SearchAliases", "search_aliases"),
                ("ImageUrl", "image_url"), ("Notes", "notes"));
            e.Property(x => x.VariantAttributes).HasColumnName("variant_attributes").HasColumnType("jsonb");
            Timestamps(e);
        });

        b.Entity<ProductPrice>(e =>
        {
            e.ToTable("product_price");
            e.HasKey(x => x.Id);
            Cols(e, ("Id", "id"), ("ProductId", "product_id"), ("CompanyId", "company_id"),
                ("PriceType", "price_type"), ("CustomerGroupId", "customer_group_id"),
                ("PriceListId", "price_list_id"), ("EstablishmentId", "establishment_id"),
                ("QuantityMin", "quantity_min"), ("CurrencyId", "currency_id"), ("Amount", "amount"),
                ("TaxIncluded", "tax_included"), ("MinPrice", "min_price"),
                ("ValidFrom", "valid_from"), ("ValidTo", "valid_to"));
            Timestamps(e);
        });

        b.Entity<PriceList>(e =>
        {
            e.ToTable("price_list");
            e.HasKey(x => x.Id);
            Cols(e, ("Id", "id"), ("CompanyId", "company_id"), ("Code", "code"), ("Name", "name"),
                ("ListType", "list_type"), ("CurrencyId", "currency_id"),
                ("CustomerGroupId", "customer_group_id"), ("Priority", "priority"), ("Status", "status"));
            Timestamps(e);
        });

        b.Entity<ProductSupplier>(e =>
        {
            e.ToTable("product_supplier");
            e.HasKey(x => x.Id);
            Cols(e, ("Id", "id"), ("ProductId", "product_id"), ("CompanyId", "company_id"),
                ("SupplierId", "supplier_id"), ("SupplierSku", "supplier_sku"),
                ("PurchasePrice", "purchase_price"), ("CurrencyId", "currency_id"),
                ("LeadTimeDays", "lead_time_days"), ("IsPreferred", "is_preferred"));
            Timestamps(e);
        });

        b.Entity<ProductBarcode>(e =>
        {
            e.ToTable("product_barcode");
            e.HasKey(x => x.Id);
            Cols(e, ("Id", "id"), ("ProductId", "product_id"), ("CompanyId", "company_id"),
                ("Barcode", "barcode"), ("BarcodeType", "barcode_type"), ("IsPrimary", "is_primary"),
                ("Status", "status"), ("PackSize", "pack_size"), ("IsWeightEmbedded", "is_weight_embedded"),
                ("ActivatedAt", "activated_at"));
            Timestamps(e);
        });

        b.Entity<UnitConversion>(e =>
        {
            e.ToTable("unit_conversion");
            e.HasKey(x => x.Id);
            Cols(e, ("Id", "id"), ("CompanyId", "company_id"), ("ProductId", "product_id"),
                ("FromUnitId", "from_unit_id"), ("ToUnitId", "to_unit_id"), ("Factor", "factor"));
            Timestamps(e);
        });

        b.Entity<ProductReorderRule>(e =>
        {
            e.ToTable("product_reorder_rule");
            e.HasKey(x => x.Id);
            Cols(e, ("Id", "id"), ("CompanyId", "company_id"), ("ProductId", "product_id"),
                ("InventoryLocationId", "inventory_location_id"), ("MinQuantity", "min_quantity"),
                ("MaxQuantity", "max_quantity"), ("ReorderPoint", "reorder_point"),
                ("ReorderQuantity", "reorder_quantity"), ("LeadTimeDays", "lead_time_days"),
                ("AutoOrder", "auto_order"), ("Status", "status"));
            Timestamps(e);
        });

        b.Entity<ProductCostHistory>(e =>
        {
            e.ToTable("product_cost_history");
            e.HasKey(x => x.Id);
            Cols(e, ("Id", "id"), ("CompanyId", "company_id"), ("ProductId", "product_id"),
                ("SourceType", "source_type"), ("SourceId", "source_id"), ("UnitCost", "unit_cost"),
                ("CurrencyId", "currency_id"), ("AverageCostAfter", "average_cost_after"),
                ("Method", "method"), ("EffectiveAt", "effective_at"));
            e.Property(x => x.CreatedAt).HasColumnName("created_at").ValueGeneratedOnAdd();
        });

        b.Entity<InventoryLocation>(e =>
        {
            e.ToTable("inventory_location");
            e.HasKey(x => x.Id);
            Cols(e, ("Id", "id"), ("CompanyId", "company_id"), ("EstablishmentId", "establishment_id"),
                ("WarehouseId", "warehouse_id"), ("StoreId", "store_id"), ("Name", "name"),
                ("LocationType", "location_type"), ("Status", "status"), ("Code", "code"),
                ("Zone", "zone"), ("Aisle", "aisle"), ("Shelf", "shelf"), ("Bin", "bin"),
                ("Capacity", "capacity"), ("ManagerUserId", "manager_user_id"),
                ("PreferredProductId", "preferred_product_id"));
            Timestamps(e);
        });

        b.Entity<StockBalance>(e =>
        {
            e.ToTable("stock_balance");
            e.HasKey(x => x.Id);
            Cols(e, ("Id", "id"), ("ProductId", "product_id"), ("InventoryLocationId", "inventory_location_id"),
                ("CompanyId", "company_id"), ("QuantityOnHand", "quantity_on_hand"),
                ("QuantityReserved", "quantity_reserved"), ("AverageCost", "average_cost"));
            e.Property(x => x.QuantityAvailable).HasColumnName("quantity_available").ValueGeneratedOnAddOrUpdate();
            e.Property(x => x.QuantityAvailable).Metadata.SetAfterSaveBehavior(Microsoft.EntityFrameworkCore.Metadata.PropertySaveBehavior.Ignore);
            Timestamps(e);
        });

        b.Entity<StockMovement>(e =>
        {
            e.ToTable("stock_movement");
            e.HasKey(x => x.Id);
            Cols(e, ("Id", "id"), ("CompanyId", "company_id"), ("ProductId", "product_id"),
                ("InventoryLocationId", "inventory_location_id"), ("LotId", "lot_id"),
                ("Quantity", "quantity"), ("UnitCost", "unit_cost"), ("ReferenceType", "reference_type"),
                ("ReferenceId", "reference_id"), ("OccurredAt", "occurred_at"), ("UserId", "user_id"),
                ("SourceOperationUuid", "source_operation_uuid"));
            e.Property(x => x.MovementTypeValue).HasColumnName("movement_type");
            e.Property(x => x.CreatedAt).HasColumnName("created_at").ValueGeneratedOnAdd();
        });

        b.Entity<Lot>(e =>
        {
            e.ToTable("lot");
            e.HasKey(x => x.Id);
            Cols(e, ("Id", "id"), ("CompanyId", "company_id"), ("ProductId", "product_id"),
                ("LotNumber", "lot_number"), ("ExpirationDate", "expiration_date"),
                ("ManufactureDate", "manufacture_date"), ("Status", "status"),
                ("SupplierId", "supplier_id"), ("ReceivedAt", "received_at"),
                ("BlockedAt", "blocked_at"), ("BlockedReason", "blocked_reason"),
                ("BlockedBy", "blocked_by"), ("RecallReference", "recall_reference"));
            Timestamps(e);
        });

        b.Entity<LotStock>(e =>
        {
            e.ToTable("lot_stock");
            e.HasKey(x => x.Id);
            Cols(e, ("Id", "id"), ("LotId", "lot_id"), ("InventoryLocationId", "inventory_location_id"),
                ("CompanyId", "company_id"), ("QuantityOnHand", "quantity_on_hand"),
                ("QuantityReserved", "quantity_reserved"));
            Timestamps(e);
        });

        b.Entity<StockTransfer>(e =>
        {
            e.ToTable("stock_transfer");
            e.HasKey(x => x.Id);
            Cols(e, ("Id", "id"), ("CompanyId", "company_id"), ("DocumentNumber", "document_number"),
                ("SourceLocationId", "source_location_id"), ("DestLocationId", "dest_location_id"),
                ("Status", "status"), ("ShippedAt", "shipped_at"), ("ReceivedAt", "received_at"),
                ("Notes", "notes"), ("CreatedBy", "created_by"));
            Timestamps(e);
            e.HasMany(x => x.Lines).WithOne().HasForeignKey(x => x.StockTransferId);
        });
        b.Entity<StockTransferLine>(e =>
        {
            e.ToTable("stock_transfer_line");
            e.HasKey(x => x.Id);
            Cols(e, ("Id", "id"), ("StockTransferId", "stock_transfer_id"), ("CompanyId", "company_id"),
                ("ProductId", "product_id"), ("LotId", "lot_id"), ("Quantity", "quantity"),
                ("QuantityShipped", "quantity_shipped"), ("QuantityReceived", "quantity_received"),
                ("UnitCost", "unit_cost"));
            Timestamps(e);
        });

        b.Entity<InventoryCount>(e =>
        {
            e.ToTable("inventory_count");
            e.HasKey(x => x.Id);
            Cols(e, ("Id", "id"), ("CompanyId", "company_id"), ("InventoryLocationId", "inventory_location_id"),
                ("DocumentNumber", "document_number"), ("CountType", "count_type"), ("Status", "status"),
                ("StartedAt", "started_at"), ("ValidatedAt", "validated_at"), ("ValidatedBy", "validated_by"),
                ("Notes", "notes"));
            Timestamps(e);
            e.HasMany(x => x.Lines).WithOne().HasForeignKey(x => x.InventoryCountId);
        });
        b.Entity<InventoryCountLine>(e =>
        {
            e.ToTable("inventory_count_line");
            e.HasKey(x => x.Id);
            Cols(e, ("Id", "id"), ("InventoryCountId", "inventory_count_id"), ("CompanyId", "company_id"),
                ("ProductId", "product_id"), ("LotId", "lot_id"), ("SystemQuantity", "system_quantity"),
                ("CountedQuantity", "counted_quantity"), ("UnitCost", "unit_cost"));
            e.Property(x => x.Variance).HasColumnName("variance").ValueGeneratedOnAddOrUpdate();
            e.Property(x => x.Variance).Metadata.SetAfterSaveBehavior(Microsoft.EntityFrameworkCore.Metadata.PropertySaveBehavior.Ignore);
            Timestamps(e);
        });

        b.Entity<StockAdjustment>(e =>
        {
            e.ToTable("stock_adjustment");
            e.HasKey(x => x.Id);
            Cols(e, ("Id", "id"), ("CompanyId", "company_id"), ("InventoryLocationId", "inventory_location_id"),
                ("DocumentNumber", "document_number"), ("Status", "status"), ("Reason", "reason"),
                ("OccurredAt", "occurred_at"), ("SourceType", "source_type"), ("SourceId", "source_id"),
                ("ApprovedBy", "approved_by"), ("JournalEntryId", "journal_entry_id"), ("CreatedBy", "created_by"));
            e.Property(x => x.AdjustmentTypeValue).HasColumnName("adjustment_type");
            Timestamps(e);
            e.HasMany(x => x.Lines).WithOne().HasForeignKey(x => x.StockAdjustmentId);
        });
        b.Entity<StockAdjustmentLine>(e =>
        {
            e.ToTable("stock_adjustment_line");
            e.HasKey(x => x.Id);
            Cols(e, ("Id", "id"), ("StockAdjustmentId", "stock_adjustment_id"), ("CompanyId", "company_id"),
                ("ProductId", "product_id"), ("LotId", "lot_id"), ("QuantityDelta", "quantity_delta"),
                ("UnitCost", "unit_cost"));
            Timestamps(e);
        });

        b.Entity<PurchaseRequest>(e =>
        {
            e.ToTable("purchase_request");
            e.HasKey(x => x.Id);
            Cols(e, ("Id", "id"), ("CompanyId", "company_id"), ("EstablishmentId", "establishment_id"),
                ("DocumentNumber", "document_number"), ("Status", "status"), ("Priority", "priority"),
                ("NeededBy", "needed_by"), ("Reason", "reason"), ("RequestedBy", "requested_by"));
            Timestamps(e);
            e.HasMany(x => x.Lines).WithOne().HasForeignKey(x => x.PurchaseRequestId);
        });
        b.Entity<PurchaseRequestLine>(e =>
        {
            e.ToTable("purchase_request_line");
            e.HasKey(x => x.Id);
            Cols(e, ("Id", "id"), ("PurchaseRequestId", "purchase_request_id"), ("CompanyId", "company_id"),
                ("ProductId", "product_id"), ("Quantity", "quantity"), ("EstimatedPrice", "estimated_price"),
                ("Notes", "notes"));
            Timestamps(e);
        });

        b.Entity<PurchaseOrder>(e =>
        {
            e.ToTable("purchase_order");
            e.HasKey(x => x.Id);
            Cols(e, ("Id", "id"), ("CompanyId", "company_id"), ("SupplierId", "supplier_id"),
                ("EstablishmentId", "establishment_id"), ("WarehouseId", "warehouse_id"),
                ("DocumentNumber", "document_number"), ("Status", "status"), ("CurrencyId", "currency_id"),
                ("OrderDate", "order_date"), ("ExpectedDate", "expected_date"), ("Subtotal", "subtotal"),
                ("DiscountTotal", "discount_total"), ("TaxTotal", "tax_total"), ("Total", "total"),
                ("Notes", "notes"), ("CreatedBy", "created_by"));
            Timestamps(e);
            e.HasMany(x => x.Lines).WithOne().HasForeignKey(x => x.PurchaseOrderId);
        });
        b.Entity<PurchaseOrderLine>(e =>
        {
            e.ToTable("purchase_order_line");
            e.HasKey(x => x.Id);
            Cols(e, ("Id", "id"), ("PurchaseOrderId", "purchase_order_id"), ("CompanyId", "company_id"),
                ("ProductId", "product_id"), ("QuantityOrdered", "quantity_ordered"),
                ("QuantityReceived", "quantity_received"), ("UnitPrice", "unit_price"), ("TaxId", "tax_id"),
                ("Discount", "discount"), ("LineTotal", "line_total"));
            Timestamps(e);
        });

        b.Entity<GoodsReceipt>(e =>
        {
            e.ToTable("goods_receipt");
            e.HasKey(x => x.Id);
            Cols(e, ("Id", "id"), ("CompanyId", "company_id"), ("SupplierId", "supplier_id"),
                ("WarehouseId", "warehouse_id"), ("InventoryLocationId", "inventory_location_id"),
                ("PurchaseOrderId", "purchase_order_id"), ("DocumentNumber", "document_number"),
                ("Status", "status"), ("ReceivedAt", "received_at"), ("Notes", "notes"), ("CreatedBy", "created_by"));
            Timestamps(e);
            e.HasMany(x => x.Lines).WithOne().HasForeignKey(x => x.GoodsReceiptId);
        });
        b.Entity<GoodsReceiptLine>(e =>
        {
            e.ToTable("goods_receipt_line");
            e.HasKey(x => x.Id);
            Cols(e, ("Id", "id"), ("GoodsReceiptId", "goods_receipt_id"), ("CompanyId", "company_id"),
                ("PurchaseOrderLineId", "purchase_order_line_id"), ("ProductId", "product_id"),
                ("QuantityReceived", "quantity_received"), ("UnitCost", "unit_cost"), ("LotId", "lot_id"));
            Timestamps(e);
        });

        b.Entity<SupplierReturn>(e =>
        {
            e.ToTable("supplier_return");
            e.HasKey(x => x.Id);
            Cols(e, ("Id", "id"), ("CompanyId", "company_id"), ("SupplierId", "supplier_id"),
                ("GoodsReceiptId", "goods_receipt_id"), ("SupplierInvoiceId", "supplier_invoice_id"),
                ("WarehouseLocationId", "warehouse_location_id"), ("DocumentNumber", "document_number"),
                ("Status", "status"), ("Reason", "reason"), ("CurrencyId", "currency_id"),
                ("Subtotal", "subtotal"), ("TaxTotal", "tax_total"), ("Total", "total"),
                ("ReturnedAt", "returned_at"), ("CreatedBy", "created_by"));
            Timestamps(e);
            e.HasMany(x => x.Lines).WithOne().HasForeignKey(x => x.SupplierReturnId);
        });
        b.Entity<SupplierReturnLine>(e =>
        {
            e.ToTable("supplier_return_line");
            e.HasKey(x => x.Id);
            Cols(e, ("Id", "id"), ("SupplierReturnId", "supplier_return_id"), ("CompanyId", "company_id"),
                ("ProductId", "product_id"), ("LotId", "lot_id"), ("Quantity", "quantity"),
                ("UnitPrice", "unit_price"), ("TaxId", "tax_id"), ("LineTotal", "line_total"));
            Timestamps(e);
        });
    }

    private static void Cols<T>(Microsoft.EntityFrameworkCore.Metadata.Builders.EntityTypeBuilder<T> e,
        params (string Prop, string Col)[] map) where T : class
    {
        foreach (var (prop, col) in map)
            e.Property(prop).HasColumnName(col);
    }

    private static void Timestamps<T>(Microsoft.EntityFrameworkCore.Metadata.Builders.EntityTypeBuilder<T> e) where T : class
    {
        e.Property("CreatedAt").HasColumnName("created_at").ValueGeneratedOnAddOrUpdate();
        e.Property("UpdatedAt").HasColumnName("updated_at").ValueGeneratedOnAddOrUpdate();
    }
}
