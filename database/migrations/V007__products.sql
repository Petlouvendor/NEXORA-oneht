-- =============================================================================
-- NEXORA — V007 — Produits, prix et approvisionnement fournisseur (§8, §13)
-- Prix historisables : une révision n'écrase jamais l'historique des transactions.
-- =============================================================================
SET search_path TO nexora, public;

CREATE TABLE nexora.product (
    id                 uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    company_id         uuid        NOT NULL REFERENCES nexora.company(id),
    sku                text        NOT NULL,
    barcode            text,
    name               text        NOT NULL,
    description        text,
    category_id        uuid,
    brand_id           uuid,
    unit_id            uuid        NOT NULL,
    product_type       text        NOT NULL DEFAULT 'stockable',
    cost_method        text        NOT NULL DEFAULT 'average',
    status             text        NOT NULL DEFAULT 'active',
    tax_configuration  jsonb       NOT NULL DEFAULT '{}'::jsonb,
    track_lots         boolean     NOT NULL DEFAULT false,   -- §13 lots / FEFO
    is_sellable        boolean     NOT NULL DEFAULT true,
    is_purchasable     boolean     NOT NULL DEFAULT true,
    created_at         timestamptz NOT NULL DEFAULT now(),
    updated_at         timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT product_type_chk CHECK (product_type IN (
        'stockable','service','composite','weight','non_stock')),
    CONSTRAINT product_cost_method_chk CHECK (cost_method IN (
        'average','fifo','standard','manual')),
    CONSTRAINT product_status_chk CHECK (status IN ('active','inactive','archived')),
    CONSTRAINT product_company_sku_uk UNIQUE (company_id, sku),
    CONSTRAINT product_company_id_uk  UNIQUE (id, company_id),
    CONSTRAINT product_category_fk FOREIGN KEY (category_id, company_id)
        REFERENCES nexora.product_category (id, company_id),
    CONSTRAINT product_brand_fk FOREIGN KEY (brand_id, company_id)
        REFERENCES nexora.brand (id, company_id),
    CONSTRAINT product_unit_fk FOREIGN KEY (unit_id, company_id)
        REFERENCES nexora.unit (id, company_id)
);
CREATE INDEX product_company_idx  ON nexora.product (company_id);
CREATE INDEX product_category_idx ON nexora.product (category_id);
CREATE INDEX product_name_idx     ON nexora.product (company_id, lower(name));
CREATE UNIQUE INDEX product_company_barcode_uk
    ON nexora.product (company_id, barcode) WHERE barcode IS NOT NULL;

-- --- Prix (vente / achat / promo), historisés -------------------------
CREATE TABLE nexora.product_price (
    id                uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    product_id        uuid        NOT NULL,
    company_id        uuid        NOT NULL,
    price_type        text        NOT NULL DEFAULT 'sale',
    customer_group_id uuid,
    quantity_min      nexora.quantity NOT NULL DEFAULT 0,
    currency_id       uuid        NOT NULL REFERENCES nexora.currency(id),
    amount            nexora.money_amount NOT NULL,
    valid_from        timestamptz NOT NULL DEFAULT now(),
    valid_to          timestamptz,
    created_at        timestamptz NOT NULL DEFAULT now(),
    updated_at        timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT product_price_type_chk CHECK (price_type IN ('sale','purchase','promo')),
    CONSTRAINT product_price_amount_chk CHECK (amount >= 0),
    CONSTRAINT product_price_qty_chk CHECK (quantity_min >= 0),
    CONSTRAINT product_price_period_chk CHECK (valid_to IS NULL OR valid_to > valid_from),
    CONSTRAINT product_price_product_fk FOREIGN KEY (product_id, company_id)
        REFERENCES nexora.product (id, company_id),
    CONSTRAINT product_price_group_fk FOREIGN KEY (customer_group_id, company_id)
        REFERENCES nexora.customer_group (id, company_id)
);
CREATE INDEX product_price_lookup_idx
    ON nexora.product_price (product_id, price_type, currency_id, valid_from DESC);

-- --- Produit ↔ fournisseur (§14 historique des prix d'achat) ---------
CREATE TABLE nexora.product_supplier (
    id             uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    product_id     uuid        NOT NULL,
    company_id     uuid        NOT NULL,
    supplier_id    uuid        NOT NULL,
    supplier_sku   text,
    purchase_price nexora.money_amount NOT NULL DEFAULT 0,
    currency_id    uuid        NOT NULL REFERENCES nexora.currency(id),
    lead_time_days integer     NOT NULL DEFAULT 0,
    is_preferred   boolean     NOT NULL DEFAULT false,
    created_at     timestamptz NOT NULL DEFAULT now(),
    updated_at     timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT product_supplier_price_chk CHECK (purchase_price >= 0),
    CONSTRAINT product_supplier_lead_chk  CHECK (lead_time_days >= 0),
    CONSTRAINT product_supplier_uk UNIQUE (product_id, supplier_id),
    CONSTRAINT product_supplier_product_fk FOREIGN KEY (product_id, company_id)
        REFERENCES nexora.product (id, company_id),
    CONSTRAINT product_supplier_supplier_fk FOREIGN KEY (supplier_id, company_id)
        REFERENCES nexora.supplier (id, company_id)
);
CREATE UNIQUE INDEX product_supplier_preferred_uk
    ON nexora.product_supplier (product_id) WHERE is_preferred;
CREATE INDEX product_supplier_supplier_idx ON nexora.product_supplier (supplier_id);

SELECT nexora.attach_common_triggers();
