-- =============================================================================
-- NEXORA — V008 — Stocks : emplacements, soldes, mouvements, lots (§11, §13)
-- stock_balance = état courant ; stock_movement = historique immuable.
-- =============================================================================
SET search_path TO nexora, public;

-- --- Emplacement de stock -------------------------------------------
CREATE TABLE nexora.inventory_location (
    id               uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    company_id       uuid        NOT NULL REFERENCES nexora.company(id),
    establishment_id uuid,
    warehouse_id     uuid,
    store_id         uuid,
    name             text        NOT NULL,
    location_type    text        NOT NULL DEFAULT 'warehouse',
    status           text        NOT NULL DEFAULT 'active',
    created_at       timestamptz NOT NULL DEFAULT now(),
    updated_at       timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT inventory_location_type_chk CHECK (location_type IN (
        'store','warehouse','virtual','transit','production')),
    CONSTRAINT inventory_location_status_chk CHECK (status IN ('active','inactive','archived')),
    CONSTRAINT inventory_location_target_chk CHECK (
        warehouse_id IS NOT NULL OR store_id IS NOT NULL
        OR location_type IN ('virtual','transit','production')),
    CONSTRAINT inventory_location_company_id_uk UNIQUE (id, company_id),
    CONSTRAINT inventory_location_establishment_fk FOREIGN KEY (establishment_id, company_id)
        REFERENCES nexora.establishment (id, company_id),
    CONSTRAINT inventory_location_warehouse_fk FOREIGN KEY (warehouse_id, company_id)
        REFERENCES nexora.warehouse (id, company_id),
    CONSTRAINT inventory_location_store_fk FOREIGN KEY (store_id, company_id)
        REFERENCES nexora.store (id, company_id)
);
CREATE INDEX inventory_location_company_idx ON nexora.inventory_location (company_id);

-- --- Solde courant par produit / emplacement ----------------------
CREATE TABLE nexora.stock_balance (
    id                   uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    product_id           uuid NOT NULL,
    inventory_location_id uuid NOT NULL,
    company_id           uuid NOT NULL,
    quantity_on_hand     nexora.quantity NOT NULL DEFAULT 0,
    quantity_reserved    nexora.quantity NOT NULL DEFAULT 0,
    quantity_available   nexora.quantity GENERATED ALWAYS AS (quantity_on_hand - quantity_reserved) STORED,
    average_cost         nexora.money_amount NOT NULL DEFAULT 0,
    created_at           timestamptz NOT NULL DEFAULT now(),
    updated_at           timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT stock_balance_reserved_chk CHECK (quantity_reserved >= 0),
    CONSTRAINT stock_balance_cost_chk     CHECK (average_cost >= 0),
    CONSTRAINT stock_balance_uk UNIQUE (product_id, inventory_location_id),
    CONSTRAINT stock_balance_product_fk FOREIGN KEY (product_id, company_id)
        REFERENCES nexora.product (id, company_id),
    CONSTRAINT stock_balance_location_fk FOREIGN KEY (inventory_location_id, company_id)
        REFERENCES nexora.inventory_location (id, company_id)
);
CREATE INDEX stock_balance_location_idx ON nexora.stock_balance (inventory_location_id);
CREATE INDEX stock_balance_company_idx  ON nexora.stock_balance (company_id);

-- --- Lot (traçabilité optionnelle §13 FEFO) -----------------------
CREATE TABLE nexora.lot (
    id               uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    company_id       uuid        NOT NULL,
    product_id       uuid        NOT NULL,
    lot_number       text        NOT NULL,
    expiration_date  date,
    manufacture_date date,
    status           text        NOT NULL DEFAULT 'active',
    created_at       timestamptz NOT NULL DEFAULT now(),
    updated_at       timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT lot_status_chk CHECK (status IN ('active','quarantine','expired','recalled','depleted')),
    CONSTRAINT lot_product_number_uk UNIQUE (product_id, lot_number),
    CONSTRAINT lot_company_id_uk      UNIQUE (id, company_id),
    CONSTRAINT lot_product_fk FOREIGN KEY (product_id, company_id)
        REFERENCES nexora.product (id, company_id)
);
CREATE INDEX lot_product_idx    ON nexora.lot (product_id);
CREATE INDEX lot_expiration_idx ON nexora.lot (product_id, expiration_date);

CREATE TABLE nexora.lot_stock (
    id                    uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    lot_id                uuid NOT NULL,
    inventory_location_id uuid NOT NULL,
    company_id            uuid NOT NULL,
    quantity_on_hand      nexora.quantity NOT NULL DEFAULT 0,
    quantity_reserved     nexora.quantity NOT NULL DEFAULT 0,
    created_at            timestamptz NOT NULL DEFAULT now(),
    updated_at            timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT lot_stock_reserved_chk CHECK (quantity_reserved >= 0),
    CONSTRAINT lot_stock_onhand_chk   CHECK (quantity_on_hand >= 0),
    CONSTRAINT lot_stock_uk UNIQUE (lot_id, inventory_location_id),
    CONSTRAINT lot_stock_lot_fk FOREIGN KEY (lot_id, company_id)
        REFERENCES nexora.lot (id, company_id),
    CONSTRAINT lot_stock_location_fk FOREIGN KEY (inventory_location_id, company_id)
        REFERENCES nexora.inventory_location (id, company_id)
);
CREATE INDEX lot_stock_location_idx ON nexora.lot_stock (inventory_location_id);

-- --- Mouvement de stock (immuable — pas de updated_at) -----------
CREATE TABLE nexora.stock_movement (
    id                    uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    company_id            uuid NOT NULL,
    product_id            uuid NOT NULL,
    inventory_location_id uuid NOT NULL,
    lot_id                uuid,
    movement_type         text NOT NULL,
    quantity              nexora.quantity NOT NULL,   -- signé : + entrée, - sortie
    unit_cost             nexora.money_amount NOT NULL DEFAULT 0,
    reference_type        text,                        -- polymorphe (sale, goods_receipt, transfer…)
    reference_id          uuid,
    occurred_at           timestamptz NOT NULL DEFAULT now(),
    user_id               uuid REFERENCES nexora.app_user(id),
    source_operation_uuid uuid,                        -- §28 idempotence offline
    created_at            timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT stock_movement_type_chk CHECK (movement_type IN (
        'receipt','sale','sale_return','purchase_return','transfer_in','transfer_out',
        'adjustment','production_input','production_output','count','reservation','release')),
    CONSTRAINT stock_movement_qty_chk  CHECK (quantity <> 0),
    CONSTRAINT stock_movement_cost_chk CHECK (unit_cost >= 0),
    CONSTRAINT stock_movement_product_fk FOREIGN KEY (product_id, company_id)
        REFERENCES nexora.product (id, company_id),
    CONSTRAINT stock_movement_location_fk FOREIGN KEY (inventory_location_id, company_id)
        REFERENCES nexora.inventory_location (id, company_id),
    CONSTRAINT stock_movement_lot_fk FOREIGN KEY (lot_id, company_id)
        REFERENCES nexora.lot (id, company_id)
);
CREATE INDEX stock_movement_product_loc_idx
    ON nexora.stock_movement (product_id, inventory_location_id, occurred_at DESC);
CREATE INDEX stock_movement_company_time_idx ON nexora.stock_movement (company_id, occurred_at DESC);
CREATE INDEX stock_movement_reference_idx    ON nexora.stock_movement (reference_type, reference_id);
CREATE UNIQUE INDEX stock_movement_source_op_uk
    ON nexora.stock_movement (source_operation_uuid) WHERE source_operation_uuid IS NOT NULL;

SELECT nexora.attach_common_triggers();
