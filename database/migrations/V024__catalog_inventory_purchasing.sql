-- =============================================================================
-- NEXORA — V024 — Catalogue, stocks & achats (Phase 5)
-- Variantes, conversions, seuils, historique des coûts, demandes d'achat,
-- transferts, inventaires, ajustements/pertes, retours fournisseur.
-- =============================================================================
SET search_path TO nexora, public;

-- --- Catalogue : variantes et unité d'achat (§5, §6) ---------------------
ALTER TABLE nexora.product
    ADD COLUMN parent_product_id  uuid REFERENCES nexora.product(id),
    ADD COLUMN variant_attributes jsonb NOT NULL DEFAULT '{}'::jsonb,
    ADD COLUMN purchase_unit_id   uuid,
    ADD COLUMN default_tax_id     uuid,
    ADD COLUMN is_weighed         boolean NOT NULL DEFAULT false,
    ADD COLUMN valuation_method   text NOT NULL DEFAULT 'average';

ALTER TABLE nexora.product
    ADD CONSTRAINT product_valuation_chk CHECK (valuation_method IN ('average','fifo','standard')),
    ADD CONSTRAINT product_purchase_unit_fk FOREIGN KEY (purchase_unit_id, company_id)
        REFERENCES nexora.unit (id, company_id),
    ADD CONSTRAINT product_default_tax_fk FOREIGN KEY (default_tax_id, company_id)
        REFERENCES nexora.tax (id, company_id);
CREATE INDEX product_parent_idx ON nexora.product (parent_product_id);

-- --- Conversions d'unités (§6 : « 1 CARTON = 12 UNITÉS ») ----------------
CREATE TABLE nexora.unit_conversion (
    id            uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    company_id    uuid        NOT NULL REFERENCES nexora.company(id),
    product_id    uuid,                              -- NULL = conversion générique
    from_unit_id  uuid        NOT NULL,
    to_unit_id    uuid        NOT NULL,
    factor        numeric(20,8) NOT NULL,            -- 1 from_unit = factor to_unit
    created_at    timestamptz NOT NULL DEFAULT now(),
    updated_at    timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT unit_conversion_factor_chk CHECK (factor > 0),
    CONSTRAINT unit_conversion_distinct_chk CHECK (from_unit_id <> to_unit_id),
    CONSTRAINT unit_conversion_uk UNIQUE NULLS NOT DISTINCT (company_id, product_id, from_unit_id, to_unit_id),
    CONSTRAINT unit_conversion_from_fk FOREIGN KEY (from_unit_id, company_id)
        REFERENCES nexora.unit (id, company_id),
    CONSTRAINT unit_conversion_to_fk FOREIGN KEY (to_unit_id, company_id)
        REFERENCES nexora.unit (id, company_id),
    CONSTRAINT unit_conversion_product_fk FOREIGN KEY (product_id, company_id)
        REFERENCES nexora.product (id, company_id)
);
CREATE INDEX unit_conversion_company_idx ON nexora.unit_conversion (company_id);

-- --- Règles de réapprovisionnement / seuils (§12, §19) ------------------
CREATE TABLE nexora.product_reorder_rule (
    id                    uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    company_id            uuid        NOT NULL REFERENCES nexora.company(id),
    product_id            uuid        NOT NULL,
    inventory_location_id uuid,                       -- NULL = règle entreprise
    min_quantity          nexora.quantity NOT NULL DEFAULT 0,
    max_quantity          nexora.quantity NOT NULL DEFAULT 0,
    reorder_point         nexora.quantity NOT NULL DEFAULT 0,
    reorder_quantity      nexora.quantity NOT NULL DEFAULT 0,
    lead_time_days        integer     NOT NULL DEFAULT 0,
    auto_order            boolean     NOT NULL DEFAULT false,
    status                text        NOT NULL DEFAULT 'active',
    created_at            timestamptz NOT NULL DEFAULT now(),
    updated_at            timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT product_reorder_rule_status_chk CHECK (status IN ('active','inactive')),
    CONSTRAINT product_reorder_rule_qty_chk CHECK (
        min_quantity >= 0 AND max_quantity >= 0 AND reorder_point >= 0 AND reorder_quantity >= 0),
    CONSTRAINT product_reorder_rule_uk UNIQUE NULLS NOT DISTINCT (company_id, product_id, inventory_location_id),
    CONSTRAINT product_reorder_rule_product_fk FOREIGN KEY (product_id, company_id)
        REFERENCES nexora.product (id, company_id),
    CONSTRAINT product_reorder_rule_location_fk FOREIGN KEY (inventory_location_id, company_id)
        REFERENCES nexora.inventory_location (id, company_id)
);
CREATE INDEX product_reorder_rule_company_idx ON nexora.product_reorder_rule (company_id, product_id);

-- --- Historique des coûts (§8, §29) -----------------------------------
CREATE TABLE nexora.product_cost_history (
    id            uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    company_id    uuid        NOT NULL REFERENCES nexora.company(id),
    product_id    uuid        NOT NULL,
    source_type   text        NOT NULL,               -- goods_receipt, supplier_invoice, adjustment…
    source_id     uuid,
    unit_cost     nexora.money_amount NOT NULL,
    currency_id   uuid        NOT NULL REFERENCES nexora.currency(id),
    average_cost_after nexora.money_amount,
    method        text        NOT NULL DEFAULT 'average',
    effective_at  timestamptz NOT NULL DEFAULT now(),
    created_at    timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT product_cost_history_product_fk FOREIGN KEY (product_id, company_id)
        REFERENCES nexora.product (id, company_id)
);
CREATE INDEX product_cost_history_idx ON nexora.product_cost_history (product_id, effective_at DESC);

-- --- Demande d'achat (§21, §22) --------------------------------------
CREATE TABLE nexora.purchase_request (
    id               uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    company_id       uuid        NOT NULL REFERENCES nexora.company(id),
    establishment_id uuid,
    document_number  text        NOT NULL,
    status           text        NOT NULL DEFAULT 'draft',
    priority         text        NOT NULL DEFAULT 'normal',
    needed_by        date,
    reason           text,
    requested_by     uuid REFERENCES nexora.app_user(id),
    approval_request_id uuid,
    created_at       timestamptz NOT NULL DEFAULT now(),
    updated_at       timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT purchase_request_status_chk CHECK (status IN (
        'draft','submitted','approved','rejected','ordered','cancelled')),
    CONSTRAINT purchase_request_priority_chk CHECK (priority IN ('low','normal','high','urgent')),
    CONSTRAINT purchase_request_company_number_uk UNIQUE (company_id, document_number),
    CONSTRAINT purchase_request_company_id_uk     UNIQUE (id, company_id),
    CONSTRAINT purchase_request_establishment_fk FOREIGN KEY (establishment_id, company_id)
        REFERENCES nexora.establishment (id, company_id)
);
CREATE INDEX purchase_request_company_status_idx ON nexora.purchase_request (company_id, status);

CREATE TABLE nexora.purchase_request_line (
    id                  uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    purchase_request_id uuid        NOT NULL,
    company_id          uuid        NOT NULL,
    product_id          uuid        NOT NULL,
    quantity            nexora.quantity NOT NULL,
    estimated_price     nexora.money_amount NOT NULL DEFAULT 0,
    notes               text,
    created_at          timestamptz NOT NULL DEFAULT now(),
    updated_at          timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT purchase_request_line_qty_chk CHECK (quantity > 0),
    CONSTRAINT purchase_request_line_pr_fk FOREIGN KEY (purchase_request_id, company_id)
        REFERENCES nexora.purchase_request (id, company_id) ON DELETE CASCADE,
    CONSTRAINT purchase_request_line_product_fk FOREIGN KEY (product_id, company_id)
        REFERENCES nexora.product (id, company_id)
);
CREATE INDEX purchase_request_line_pr_idx ON nexora.purchase_request_line (purchase_request_id);

-- --- Transferts de stock (§14) --------------------------------------
CREATE TABLE nexora.stock_transfer (
    id                 uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    company_id         uuid        NOT NULL REFERENCES nexora.company(id),
    document_number    text        NOT NULL,
    source_location_id uuid        NOT NULL,
    dest_location_id   uuid        NOT NULL,
    status             text        NOT NULL DEFAULT 'draft',
    shipped_at         timestamptz,
    received_at        timestamptz,
    notes              text,
    created_by         uuid REFERENCES nexora.app_user(id),
    created_at         timestamptz NOT NULL DEFAULT now(),
    updated_at         timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT stock_transfer_status_chk CHECK (status IN (
        'draft','in_transit','partially_received','completed','cancelled')),
    CONSTRAINT stock_transfer_distinct_chk CHECK (source_location_id <> dest_location_id),
    CONSTRAINT stock_transfer_company_number_uk UNIQUE (company_id, document_number),
    CONSTRAINT stock_transfer_company_id_uk     UNIQUE (id, company_id),
    CONSTRAINT stock_transfer_source_fk FOREIGN KEY (source_location_id, company_id)
        REFERENCES nexora.inventory_location (id, company_id),
    CONSTRAINT stock_transfer_dest_fk FOREIGN KEY (dest_location_id, company_id)
        REFERENCES nexora.inventory_location (id, company_id)
);
CREATE INDEX stock_transfer_company_status_idx ON nexora.stock_transfer (company_id, status);

CREATE TABLE nexora.stock_transfer_line (
    id                uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    stock_transfer_id uuid        NOT NULL,
    company_id        uuid        NOT NULL,
    product_id        uuid        NOT NULL,
    lot_id            uuid,
    quantity          nexora.quantity NOT NULL,
    quantity_shipped  nexora.quantity NOT NULL DEFAULT 0,
    quantity_received nexora.quantity NOT NULL DEFAULT 0,
    unit_cost         nexora.money_amount NOT NULL DEFAULT 0,
    created_at        timestamptz NOT NULL DEFAULT now(),
    updated_at        timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT stock_transfer_line_qty_chk CHECK (quantity > 0 AND quantity_shipped >= 0 AND quantity_received >= 0),
    CONSTRAINT stock_transfer_line_transfer_fk FOREIGN KEY (stock_transfer_id, company_id)
        REFERENCES nexora.stock_transfer (id, company_id) ON DELETE CASCADE,
    CONSTRAINT stock_transfer_line_product_fk FOREIGN KEY (product_id, company_id)
        REFERENCES nexora.product (id, company_id),
    CONSTRAINT stock_transfer_line_lot_fk FOREIGN KEY (lot_id, company_id)
        REFERENCES nexora.lot (id, company_id)
);
CREATE INDEX stock_transfer_line_transfer_idx ON nexora.stock_transfer_line (stock_transfer_id);

-- --- Inventaires (§15) ---------------------------------------------
CREATE TABLE nexora.inventory_count (
    id                    uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    company_id            uuid        NOT NULL REFERENCES nexora.company(id),
    inventory_location_id uuid        NOT NULL,
    document_number       text        NOT NULL,
    count_type            text        NOT NULL DEFAULT 'full',
    status                text        NOT NULL DEFAULT 'draft',
    started_at            timestamptz,
    validated_at          timestamptz,
    validated_by          uuid REFERENCES nexora.app_user(id),
    notes                 text,
    created_at            timestamptz NOT NULL DEFAULT now(),
    updated_at            timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT inventory_count_type_chk CHECK (count_type IN ('full','partial','cycle')),
    CONSTRAINT inventory_count_status_chk CHECK (status IN ('draft','counting','review','validated','cancelled')),
    CONSTRAINT inventory_count_company_number_uk UNIQUE (company_id, document_number),
    CONSTRAINT inventory_count_company_id_uk     UNIQUE (id, company_id),
    CONSTRAINT inventory_count_location_fk FOREIGN KEY (inventory_location_id, company_id)
        REFERENCES nexora.inventory_location (id, company_id)
);
CREATE INDEX inventory_count_company_status_idx ON nexora.inventory_count (company_id, status);

CREATE TABLE nexora.inventory_count_line (
    id                 uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    inventory_count_id uuid        NOT NULL,
    company_id         uuid        NOT NULL,
    product_id         uuid        NOT NULL,
    lot_id             uuid,
    system_quantity    nexora.quantity NOT NULL DEFAULT 0,
    counted_quantity   nexora.quantity,
    variance           nexora.quantity GENERATED ALWAYS AS (coalesce(counted_quantity,0) - system_quantity) STORED,
    unit_cost          nexora.money_amount NOT NULL DEFAULT 0,
    created_at         timestamptz NOT NULL DEFAULT now(),
    updated_at         timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT inventory_count_line_count_fk FOREIGN KEY (inventory_count_id, company_id)
        REFERENCES nexora.inventory_count (id, company_id) ON DELETE CASCADE,
    CONSTRAINT inventory_count_line_product_fk FOREIGN KEY (product_id, company_id)
        REFERENCES nexora.product (id, company_id),
    CONSTRAINT inventory_count_line_lot_fk FOREIGN KEY (lot_id, company_id)
        REFERENCES nexora.lot (id, company_id)
);
CREATE INDEX inventory_count_line_count_idx ON nexora.inventory_count_line (inventory_count_id);

-- --- Ajustements / pertes / casse / expiration (§16) --------------
CREATE TABLE nexora.stock_adjustment (
    id                    uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    company_id            uuid        NOT NULL REFERENCES nexora.company(id),
    inventory_location_id uuid        NOT NULL,
    document_number       text        NOT NULL,
    adjustment_type       text        NOT NULL,
    status                text        NOT NULL DEFAULT 'draft',
    reason                text,
    occurred_at           timestamptz NOT NULL DEFAULT now(),
    source_type           text,
    source_id             uuid,
    approval_request_id   uuid,
    approved_by           uuid REFERENCES nexora.app_user(id),
    journal_entry_id      uuid,
    created_by            uuid REFERENCES nexora.app_user(id),
    created_at            timestamptz NOT NULL DEFAULT now(),
    updated_at            timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT stock_adjustment_type_chk CHECK (adjustment_type IN (
        'count','loss','breakage','expiry','correction','found','opening')),
    CONSTRAINT stock_adjustment_status_chk CHECK (status IN ('draft','posted','cancelled')),
    CONSTRAINT stock_adjustment_company_number_uk UNIQUE (company_id, document_number),
    CONSTRAINT stock_adjustment_company_id_uk     UNIQUE (id, company_id),
    CONSTRAINT stock_adjustment_location_fk FOREIGN KEY (inventory_location_id, company_id)
        REFERENCES nexora.inventory_location (id, company_id)
);
CREATE INDEX stock_adjustment_company_idx ON nexora.stock_adjustment (company_id, occurred_at DESC);

CREATE TABLE nexora.stock_adjustment_line (
    id                  uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    stock_adjustment_id uuid        NOT NULL,
    company_id          uuid        NOT NULL,
    product_id          uuid        NOT NULL,
    lot_id              uuid,
    quantity_delta      nexora.quantity NOT NULL,     -- signé : + trouvé, - perte
    unit_cost           nexora.money_amount NOT NULL DEFAULT 0,
    created_at          timestamptz NOT NULL DEFAULT now(),
    updated_at          timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT stock_adjustment_line_delta_chk CHECK (quantity_delta <> 0),
    CONSTRAINT stock_adjustment_line_adj_fk FOREIGN KEY (stock_adjustment_id, company_id)
        REFERENCES nexora.stock_adjustment (id, company_id) ON DELETE CASCADE,
    CONSTRAINT stock_adjustment_line_product_fk FOREIGN KEY (product_id, company_id)
        REFERENCES nexora.product (id, company_id),
    CONSTRAINT stock_adjustment_line_lot_fk FOREIGN KEY (lot_id, company_id)
        REFERENCES nexora.lot (id, company_id)
);
CREATE INDEX stock_adjustment_line_adj_idx ON nexora.stock_adjustment_line (stock_adjustment_id);

-- --- Retours fournisseur (§28) -----------------------------------
CREATE TABLE nexora.supplier_return (
    id                  uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    company_id          uuid        NOT NULL REFERENCES nexora.company(id),
    supplier_id         uuid        NOT NULL,
    goods_receipt_id    uuid,
    supplier_invoice_id uuid,
    warehouse_location_id uuid,
    document_number     text        NOT NULL,
    status              text        NOT NULL DEFAULT 'draft',
    reason              text,
    currency_id         uuid        NOT NULL REFERENCES nexora.currency(id),
    subtotal            nexora.money_amount NOT NULL DEFAULT 0,
    tax_total           nexora.money_amount NOT NULL DEFAULT 0,
    total               nexora.money_amount NOT NULL DEFAULT 0,
    returned_at         timestamptz NOT NULL DEFAULT now(),
    created_by          uuid REFERENCES nexora.app_user(id),
    created_at          timestamptz NOT NULL DEFAULT now(),
    updated_at          timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT supplier_return_status_chk CHECK (status IN ('draft','posted','cancelled')),
    CONSTRAINT supplier_return_company_number_uk UNIQUE (company_id, document_number),
    CONSTRAINT supplier_return_company_id_uk     UNIQUE (id, company_id),
    CONSTRAINT supplier_return_supplier_fk FOREIGN KEY (supplier_id, company_id)
        REFERENCES nexora.supplier (id, company_id),
    CONSTRAINT supplier_return_gr_fk FOREIGN KEY (goods_receipt_id, company_id)
        REFERENCES nexora.goods_receipt (id, company_id),
    CONSTRAINT supplier_return_invoice_fk FOREIGN KEY (supplier_invoice_id, company_id)
        REFERENCES nexora.supplier_invoice (id, company_id),
    CONSTRAINT supplier_return_location_fk FOREIGN KEY (warehouse_location_id, company_id)
        REFERENCES nexora.inventory_location (id, company_id)
);
CREATE INDEX supplier_return_company_idx ON nexora.supplier_return (company_id, returned_at DESC);

CREATE TABLE nexora.supplier_return_line (
    id                uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    supplier_return_id uuid       NOT NULL,
    company_id        uuid        NOT NULL,
    product_id        uuid        NOT NULL,
    lot_id            uuid,
    quantity          nexora.quantity NOT NULL,
    unit_price        nexora.money_amount NOT NULL DEFAULT 0,
    tax_id            uuid,
    line_total        nexora.money_amount NOT NULL DEFAULT 0,
    created_at        timestamptz NOT NULL DEFAULT now(),
    updated_at        timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT supplier_return_line_qty_chk CHECK (quantity > 0),
    CONSTRAINT supplier_return_line_return_fk FOREIGN KEY (supplier_return_id, company_id)
        REFERENCES nexora.supplier_return (id, company_id) ON DELETE CASCADE,
    CONSTRAINT supplier_return_line_product_fk FOREIGN KEY (product_id, company_id)
        REFERENCES nexora.product (id, company_id),
    CONSTRAINT supplier_return_line_tax_fk FOREIGN KEY (tax_id, company_id)
        REFERENCES nexora.tax (id, company_id)
);
CREATE INDEX supplier_return_line_return_idx ON nexora.supplier_return_line (supplier_return_id);

-- --- stock_movement : types étendus (§13, §16) ------------------
ALTER TABLE nexora.stock_movement DROP CONSTRAINT stock_movement_type_chk;
ALTER TABLE nexora.stock_movement ADD CONSTRAINT stock_movement_type_chk CHECK (movement_type IN (
    'receipt','sale','sale_return','purchase_return','transfer_in','transfer_out',
    'adjustment','loss','breakage','expiry','found','production_input','production_output',
    'count','reservation','release','opening'));

-- --- account_mapping : clés Phase 5 -----------------------------
ALTER TABLE nexora.account_mapping DROP CONSTRAINT account_mapping_key_chk;
ALTER TABLE nexora.account_mapping ADD CONSTRAINT account_mapping_key_chk CHECK (mapping_key IN (
    'sales_revenue','sales_tax_output','accounts_receivable','accounts_payable',
    'purchases','inventory','cogs','purchase_tax_input','expense_default',
    'cash_short_over','fx_gain','fx_loss','rounding','bank_charges','suspense',
    'cash','bank','mobile_money','retained_earnings',
    'inventory_adjustment','inventory_loss','goods_in_transit','purchase_price_variance'));

-- --- Permissions Phase 5 (§36) --------------------------------
INSERT INTO nexora.permission (code, name, module, action) VALUES
    ('catalog.manage_price',       'Catalogue — modifier les prix',         'catalog',    'manage_price'),
    ('catalog.view_cost',          'Catalogue — voir les coûts',            'catalog',    'view_cost'),
    ('inventory.count',            'Stock — réaliser un inventaire',        'inventory',  'count'),
    ('inventory.validate_adjustment','Stock — valider un ajustement',       'inventory',  'validate_adjustment'),
    ('inventory.write_off',        'Stock — perte / casse',                 'inventory',  'write_off'),
    ('purchasing.request',        'Achats — créer une demande',            'purchasing', 'request'),
    ('purchasing.approve',        'Achats — approuver',                    'purchasing', 'approve'),
    ('purchasing.order',          'Achats — passer commande',              'purchasing', 'order'),
    ('purchasing.invoice',        'Achats — enregistrer une facture',      'purchasing', 'invoice'),
    ('purchasing.return',         'Achats — retour fournisseur',           'purchasing', 'return'),
    ('suppliers.manage',          'Fournisseurs — gérer',                  'suppliers',  'manage')
ON CONFLICT (code) DO NOTHING;

CREATE OR REPLACE FUNCTION pg_temp.grant_perms_v24(p_role text, p_patterns text[])
RETURNS void LANGUAGE sql AS $$
    INSERT INTO nexora.role_permission (role_id, permission_id)
    SELECT r.id, p.id FROM nexora.role r
    JOIN nexora.permission p ON p.code LIKE ANY (p_patterns)
    WHERE r.name = p_role AND r.system_role
    ON CONFLICT DO NOTHING;
$$;

SELECT pg_temp.grant_perms_v24('super_administrator', ARRAY['catalog.%','inventory.%','purchasing.%','suppliers.%']);
SELECT pg_temp.grant_perms_v24('group_administrator', ARRAY['catalog.%','inventory.%','purchasing.%','suppliers.%']);
SELECT pg_temp.grant_perms_v24('owner_direction',     ARRAY['catalog.%','inventory.%','purchasing.%','suppliers.%']);
SELECT pg_temp.grant_perms_v24('purchasing_manager',  ARRAY['purchasing.%','suppliers.%','catalog.view','catalog.view_cost','inventory.view']);
SELECT pg_temp.grant_perms_v24('stock_manager',       ARRAY['inventory.%','catalog.view','catalog.update','purchasing.request','purchasing.view','purchasing.receive']);
SELECT pg_temp.grant_perms_v24('manager',             ARRAY['catalog.%','inventory.%','purchasing.%','suppliers.%']);

SELECT nexora.attach_common_triggers();
