-- =============================================================================
-- NEXORA — V009 — Achats (§14)
-- Commande → Réception → Stock → Facture → Dette/Paiement → Comptabilité
-- =============================================================================
SET search_path TO nexora, public;

-- --- Commande fournisseur ------------------------------------------
CREATE TABLE nexora.purchase_order (
    id               uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    company_id       uuid        NOT NULL REFERENCES nexora.company(id),
    supplier_id      uuid        NOT NULL,
    establishment_id uuid,
    warehouse_id     uuid,
    document_number  text        NOT NULL,
    status           text        NOT NULL DEFAULT 'draft',
    currency_id      uuid        NOT NULL REFERENCES nexora.currency(id),
    order_date       date        NOT NULL DEFAULT current_date,
    expected_date    date,
    subtotal         nexora.money_amount NOT NULL DEFAULT 0,
    discount_total   nexora.money_amount NOT NULL DEFAULT 0,
    tax_total        nexora.money_amount NOT NULL DEFAULT 0,
    total            nexora.money_amount NOT NULL DEFAULT 0,
    notes            text,
    created_by       uuid REFERENCES nexora.app_user(id),
    created_at       timestamptz NOT NULL DEFAULT now(),
    updated_at       timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT purchase_order_status_chk CHECK (status IN (
        'draft','submitted','approved','partially_received','received','invoiced','closed','cancelled')),
    CONSTRAINT purchase_order_totals_chk CHECK (subtotal >= 0 AND tax_total >= 0 AND total >= 0),
    CONSTRAINT purchase_order_company_number_uk UNIQUE (company_id, document_number),
    CONSTRAINT purchase_order_company_id_uk     UNIQUE (id, company_id),
    CONSTRAINT purchase_order_supplier_fk FOREIGN KEY (supplier_id, company_id)
        REFERENCES nexora.supplier (id, company_id),
    CONSTRAINT purchase_order_establishment_fk FOREIGN KEY (establishment_id, company_id)
        REFERENCES nexora.establishment (id, company_id),
    CONSTRAINT purchase_order_warehouse_fk FOREIGN KEY (warehouse_id, company_id)
        REFERENCES nexora.warehouse (id, company_id)
);
CREATE INDEX purchase_order_company_status_idx ON nexora.purchase_order (company_id, status, order_date DESC);
CREATE INDEX purchase_order_supplier_idx       ON nexora.purchase_order (supplier_id);

CREATE TABLE nexora.purchase_order_line (
    id                uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    purchase_order_id uuid        NOT NULL,
    company_id        uuid        NOT NULL,
    product_id        uuid        NOT NULL,
    quantity_ordered  nexora.quantity NOT NULL,
    quantity_received nexora.quantity NOT NULL DEFAULT 0,
    unit_price        nexora.money_amount NOT NULL DEFAULT 0,
    tax_id            uuid,
    discount          nexora.money_amount NOT NULL DEFAULT 0,
    line_total        nexora.money_amount NOT NULL DEFAULT 0,
    created_at        timestamptz NOT NULL DEFAULT now(),
    updated_at        timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT purchase_order_line_qty_chk CHECK (quantity_ordered > 0 AND quantity_received >= 0),
    CONSTRAINT purchase_order_line_price_chk CHECK (unit_price >= 0 AND discount >= 0),
    CONSTRAINT purchase_order_line_po_fk FOREIGN KEY (purchase_order_id, company_id)
        REFERENCES nexora.purchase_order (id, company_id) ON DELETE CASCADE,
    CONSTRAINT purchase_order_line_product_fk FOREIGN KEY (product_id, company_id)
        REFERENCES nexora.product (id, company_id),
    CONSTRAINT purchase_order_line_tax_fk FOREIGN KEY (tax_id, company_id)
        REFERENCES nexora.tax (id, company_id)
);
CREATE INDEX purchase_order_line_po_idx      ON nexora.purchase_order_line (purchase_order_id);
CREATE INDEX purchase_order_line_product_idx ON nexora.purchase_order_line (product_id);

-- --- Réception de marchandises -----------------------------------
CREATE TABLE nexora.goods_receipt (
    id                    uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    company_id            uuid        NOT NULL REFERENCES nexora.company(id),
    supplier_id           uuid        NOT NULL,
    warehouse_id          uuid,
    inventory_location_id uuid,
    purchase_order_id     uuid,
    document_number       text        NOT NULL,
    status                text        NOT NULL DEFAULT 'draft',
    received_at           timestamptz NOT NULL DEFAULT now(),
    notes                 text,
    created_by            uuid REFERENCES nexora.app_user(id),
    created_at            timestamptz NOT NULL DEFAULT now(),
    updated_at            timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT goods_receipt_status_chk CHECK (status IN ('draft','posted','cancelled')),
    CONSTRAINT goods_receipt_company_number_uk UNIQUE (company_id, document_number),
    CONSTRAINT goods_receipt_company_id_uk     UNIQUE (id, company_id),
    CONSTRAINT goods_receipt_supplier_fk FOREIGN KEY (supplier_id, company_id)
        REFERENCES nexora.supplier (id, company_id),
    CONSTRAINT goods_receipt_warehouse_fk FOREIGN KEY (warehouse_id, company_id)
        REFERENCES nexora.warehouse (id, company_id),
    CONSTRAINT goods_receipt_location_fk FOREIGN KEY (inventory_location_id, company_id)
        REFERENCES nexora.inventory_location (id, company_id),
    CONSTRAINT goods_receipt_po_fk FOREIGN KEY (purchase_order_id, company_id)
        REFERENCES nexora.purchase_order (id, company_id)
);
CREATE INDEX goods_receipt_company_idx  ON nexora.goods_receipt (company_id, received_at DESC);
CREATE INDEX goods_receipt_supplier_idx ON nexora.goods_receipt (supplier_id);
CREATE INDEX goods_receipt_po_idx       ON nexora.goods_receipt (purchase_order_id);

CREATE TABLE nexora.goods_receipt_line (
    id                     uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    goods_receipt_id       uuid        NOT NULL,
    company_id             uuid        NOT NULL,
    purchase_order_line_id uuid,
    product_id             uuid        NOT NULL,
    quantity_received      nexora.quantity NOT NULL,
    unit_cost              nexora.money_amount NOT NULL DEFAULT 0,
    lot_id                 uuid,
    created_at             timestamptz NOT NULL DEFAULT now(),
    updated_at             timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT goods_receipt_line_qty_chk  CHECK (quantity_received > 0),
    CONSTRAINT goods_receipt_line_cost_chk CHECK (unit_cost >= 0),
    CONSTRAINT goods_receipt_line_gr_fk FOREIGN KEY (goods_receipt_id, company_id)
        REFERENCES nexora.goods_receipt (id, company_id) ON DELETE CASCADE,
    CONSTRAINT goods_receipt_line_product_fk FOREIGN KEY (product_id, company_id)
        REFERENCES nexora.product (id, company_id),
    CONSTRAINT goods_receipt_line_lot_fk FOREIGN KEY (lot_id, company_id)
        REFERENCES nexora.lot (id, company_id)
);
CREATE INDEX goods_receipt_line_gr_idx      ON nexora.goods_receipt_line (goods_receipt_id);
CREATE INDEX goods_receipt_line_product_idx ON nexora.goods_receipt_line (product_id);

-- --- Facture fournisseur ----------------------------------------
CREATE TABLE nexora.supplier_invoice (
    id                 uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    company_id         uuid        NOT NULL REFERENCES nexora.company(id),
    supplier_id        uuid        NOT NULL,
    purchase_order_id  uuid,
    document_number    text        NOT NULL,
    supplier_reference text,
    invoice_date       date        NOT NULL DEFAULT current_date,
    due_date           date,
    currency_id        uuid        NOT NULL REFERENCES nexora.currency(id),
    exchange_rate_id   uuid        REFERENCES nexora.exchange_rate(id),
    subtotal           nexora.money_amount NOT NULL DEFAULT 0,
    discount_total     nexora.money_amount NOT NULL DEFAULT 0,
    tax_total          nexora.money_amount NOT NULL DEFAULT 0,
    total              nexora.money_amount NOT NULL DEFAULT 0,
    status             text        NOT NULL DEFAULT 'draft',
    created_by         uuid REFERENCES nexora.app_user(id),
    created_at         timestamptz NOT NULL DEFAULT now(),
    updated_at         timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT supplier_invoice_status_chk CHECK (status IN (
        'draft','validated','partially_paid','paid','cancelled')),
    CONSTRAINT supplier_invoice_totals_chk CHECK (subtotal >= 0 AND tax_total >= 0 AND total >= 0),
    CONSTRAINT supplier_invoice_company_number_uk UNIQUE (company_id, document_number),
    CONSTRAINT supplier_invoice_company_id_uk     UNIQUE (id, company_id),
    CONSTRAINT supplier_invoice_supplier_fk FOREIGN KEY (supplier_id, company_id)
        REFERENCES nexora.supplier (id, company_id),
    CONSTRAINT supplier_invoice_po_fk FOREIGN KEY (purchase_order_id, company_id)
        REFERENCES nexora.purchase_order (id, company_id)
);
CREATE INDEX supplier_invoice_company_status_idx ON nexora.supplier_invoice (company_id, status, invoice_date DESC);
CREATE INDEX supplier_invoice_supplier_idx       ON nexora.supplier_invoice (supplier_id);
CREATE INDEX supplier_invoice_due_idx            ON nexora.supplier_invoice (company_id, due_date) WHERE status IN ('validated','partially_paid');

CREATE TABLE nexora.supplier_invoice_line (
    id                 uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    supplier_invoice_id uuid       NOT NULL,
    company_id         uuid        NOT NULL,
    product_id         uuid,
    description        text        NOT NULL DEFAULT '',
    quantity           nexora.quantity NOT NULL DEFAULT 1,
    unit_price         nexora.money_amount NOT NULL DEFAULT 0,
    tax_id             uuid,
    discount           nexora.money_amount NOT NULL DEFAULT 0,
    line_total         nexora.money_amount NOT NULL DEFAULT 0,
    created_at         timestamptz NOT NULL DEFAULT now(),
    updated_at         timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT supplier_invoice_line_qty_chk   CHECK (quantity > 0),
    CONSTRAINT supplier_invoice_line_price_chk CHECK (unit_price >= 0 AND discount >= 0),
    CONSTRAINT supplier_invoice_line_inv_fk FOREIGN KEY (supplier_invoice_id, company_id)
        REFERENCES nexora.supplier_invoice (id, company_id) ON DELETE CASCADE,
    CONSTRAINT supplier_invoice_line_product_fk FOREIGN KEY (product_id, company_id)
        REFERENCES nexora.product (id, company_id),
    CONSTRAINT supplier_invoice_line_tax_fk FOREIGN KEY (tax_id, company_id)
        REFERENCES nexora.tax (id, company_id)
);
CREATE INDEX supplier_invoice_line_inv_idx ON nexora.supplier_invoice_line (supplier_invoice_id);

SELECT nexora.attach_common_triggers();
