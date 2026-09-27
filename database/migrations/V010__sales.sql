-- =============================================================================
-- NEXORA — V010 — Ventes et POS (§11, §14)
-- Le coût au moment de la vente (cost_snapshot) est figé pour la marge historique.
-- =============================================================================
SET search_path TO nexora, public;

CREATE TABLE nexora.sale (
    id                    uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    company_id            uuid        NOT NULL REFERENCES nexora.company(id),
    establishment_id      uuid,
    store_id              uuid,
    pos_terminal_id       uuid,
    customer_id           uuid,
    document_number       text        NOT NULL,
    status                text        NOT NULL DEFAULT 'draft',
    sale_type             text        NOT NULL DEFAULT 'cash',
    currency_id           uuid        NOT NULL REFERENCES nexora.currency(id),
    exchange_rate_id      uuid        REFERENCES nexora.exchange_rate(id),
    sold_at               timestamptz NOT NULL DEFAULT now(),
    subtotal              nexora.money_amount NOT NULL DEFAULT 0,
    discount_total        nexora.money_amount NOT NULL DEFAULT 0,
    tax_total             nexora.money_amount NOT NULL DEFAULT 0,
    total                 nexora.money_amount NOT NULL DEFAULT 0,
    amount_paid           nexora.money_amount NOT NULL DEFAULT 0,
    amount_due            nexora.money_amount GENERATED ALWAYS AS (total - amount_paid) STORED,
    salesperson_user_id   uuid REFERENCES nexora.app_user(id),
    source_operation_uuid uuid,                       -- §28 idempotence offline
    created_by            uuid REFERENCES nexora.app_user(id),
    created_at            timestamptz NOT NULL DEFAULT now(),
    updated_at            timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT sale_status_chk CHECK (status IN (
        'draft','parked','completed','partially_refunded','refunded','void')),
    CONSTRAINT sale_type_chk CHECK (sale_type IN ('cash','credit','mixed','quote','layaway')),
    CONSTRAINT sale_totals_chk CHECK (subtotal >= 0 AND tax_total >= 0 AND total >= 0 AND amount_paid >= 0),
    CONSTRAINT sale_company_number_uk UNIQUE (company_id, document_number),
    CONSTRAINT sale_company_id_uk     UNIQUE (id, company_id),
    CONSTRAINT sale_customer_fk FOREIGN KEY (customer_id, company_id)
        REFERENCES nexora.customer (id, company_id),
    CONSTRAINT sale_establishment_fk FOREIGN KEY (establishment_id, company_id)
        REFERENCES nexora.establishment (id, company_id),
    CONSTRAINT sale_store_fk FOREIGN KEY (store_id, company_id)
        REFERENCES nexora.store (id, company_id),
    CONSTRAINT sale_pos_terminal_fk FOREIGN KEY (pos_terminal_id, company_id)
        REFERENCES nexora.pos_terminal (id, company_id)
);
CREATE INDEX sale_company_time_idx   ON nexora.sale (company_id, sold_at DESC);
CREATE INDEX sale_company_status_idx ON nexora.sale (company_id, status, sold_at DESC);
CREATE INDEX sale_customer_idx       ON nexora.sale (customer_id);
CREATE INDEX sale_store_idx          ON nexora.sale (store_id, sold_at DESC);
CREATE INDEX sale_salesperson_idx    ON nexora.sale (salesperson_user_id, sold_at DESC);
CREATE UNIQUE INDEX sale_source_op_uk
    ON nexora.sale (source_operation_uuid) WHERE source_operation_uuid IS NOT NULL;

CREATE TABLE nexora.sale_line (
    id                    uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    sale_id               uuid        NOT NULL,
    company_id            uuid        NOT NULL,
    product_id            uuid        NOT NULL,
    description           text        NOT NULL DEFAULT '',
    quantity              nexora.quantity NOT NULL,
    unit_price            nexora.money_amount NOT NULL DEFAULT 0,
    discount              nexora.money_amount NOT NULL DEFAULT 0,
    tax_id                uuid,
    tax_amount            nexora.money_amount NOT NULL DEFAULT 0,
    line_total            nexora.money_amount NOT NULL DEFAULT 0,
    cost_snapshot         nexora.money_amount NOT NULL DEFAULT 0,   -- §14
    lot_id                uuid,
    inventory_location_id uuid,
    created_at            timestamptz NOT NULL DEFAULT now(),
    updated_at            timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT sale_line_qty_chk   CHECK (quantity > 0),
    CONSTRAINT sale_line_price_chk CHECK (unit_price >= 0 AND discount >= 0),
    CONSTRAINT sale_line_sale_fk FOREIGN KEY (sale_id, company_id)
        REFERENCES nexora.sale (id, company_id) ON DELETE CASCADE,
    CONSTRAINT sale_line_product_fk FOREIGN KEY (product_id, company_id)
        REFERENCES nexora.product (id, company_id),
    CONSTRAINT sale_line_tax_fk FOREIGN KEY (tax_id, company_id)
        REFERENCES nexora.tax (id, company_id),
    CONSTRAINT sale_line_lot_fk FOREIGN KEY (lot_id, company_id)
        REFERENCES nexora.lot (id, company_id),
    CONSTRAINT sale_line_location_fk FOREIGN KEY (inventory_location_id, company_id)
        REFERENCES nexora.inventory_location (id, company_id)
);
CREATE INDEX sale_line_sale_idx    ON nexora.sale_line (sale_id);
CREATE INDEX sale_line_product_idx ON nexora.sale_line (product_id);

-- --- Retours / remboursements (§11) -------------------------------
CREATE TABLE nexora.sale_return (
    id               uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    company_id       uuid        NOT NULL REFERENCES nexora.company(id),
    sale_id          uuid        NOT NULL,
    document_number  text        NOT NULL,
    status           text        NOT NULL DEFAULT 'draft',
    currency_id      uuid        NOT NULL REFERENCES nexora.currency(id),
    reason           text,
    refund_total     nexora.money_amount NOT NULL DEFAULT 0,
    returned_at      timestamptz NOT NULL DEFAULT now(),
    created_by       uuid REFERENCES nexora.app_user(id),
    created_at       timestamptz NOT NULL DEFAULT now(),
    updated_at       timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT sale_return_status_chk CHECK (status IN ('draft','completed','cancelled')),
    CONSTRAINT sale_return_total_chk  CHECK (refund_total >= 0),
    CONSTRAINT sale_return_company_number_uk UNIQUE (company_id, document_number),
    CONSTRAINT sale_return_company_id_uk     UNIQUE (id, company_id),
    CONSTRAINT sale_return_sale_fk FOREIGN KEY (sale_id, company_id)
        REFERENCES nexora.sale (id, company_id)
);
CREATE INDEX sale_return_sale_idx    ON nexora.sale_return (sale_id);
CREATE INDEX sale_return_company_idx ON nexora.sale_return (company_id, returned_at DESC);

CREATE TABLE nexora.sale_return_line (
    id             uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    sale_return_id uuid        NOT NULL,
    company_id     uuid        NOT NULL,
    sale_line_id   uuid,
    product_id     uuid        NOT NULL,
    quantity       nexora.quantity NOT NULL,
    unit_price     nexora.money_amount NOT NULL DEFAULT 0,
    line_total     nexora.money_amount NOT NULL DEFAULT 0,
    restock        boolean     NOT NULL DEFAULT true,
    created_at     timestamptz NOT NULL DEFAULT now(),
    updated_at     timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT sale_return_line_qty_chk CHECK (quantity > 0),
    CONSTRAINT sale_return_line_return_fk FOREIGN KEY (sale_return_id, company_id)
        REFERENCES nexora.sale_return (id, company_id) ON DELETE CASCADE,
    CONSTRAINT sale_return_line_product_fk FOREIGN KEY (product_id, company_id)
        REFERENCES nexora.product (id, company_id)
);
CREATE INDEX sale_return_line_return_idx ON nexora.sale_return_line (sale_return_id);

SELECT nexora.attach_common_triggers();
