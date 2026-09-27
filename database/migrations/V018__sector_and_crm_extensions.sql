-- =============================================================================
-- NEXORA — V018 — Extensions sectorielles et CRM (§19, §20 CDC ; §35 modèle)
-- Tables métier dédiées reliées au produit / à la transaction : le noyau ventes,
-- stock, paiement, comptabilité reste inchangé.
-- =============================================================================
SET search_path TO nexora, public;

-- ===================== PHARMACIE (§19) =====================
CREATE TABLE nexora.pharmacy_product (
    product_id            uuid PRIMARY KEY,
    company_id            uuid        NOT NULL,
    active_ingredient     text,
    dosage                text,
    form                  text,
    requires_prescription boolean     NOT NULL DEFAULT false,
    controlled_schedule   text,
    atc_code              text,
    storage_conditions    text,
    created_at            timestamptz NOT NULL DEFAULT now(),
    updated_at            timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT pharmacy_product_product_fk FOREIGN KEY (product_id, company_id)
        REFERENCES nexora.product (id, company_id) ON DELETE CASCADE
);

CREATE TABLE nexora.prescription (
    id               uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    company_id       uuid        NOT NULL REFERENCES nexora.company(id),
    customer_id      uuid,
    prescriber_name  text        NOT NULL,
    prescriber_license text,
    issue_date       date        NOT NULL DEFAULT current_date,
    expiry_date      date,
    notes            text,
    status           text        NOT NULL DEFAULT 'active',
    created_at       timestamptz NOT NULL DEFAULT now(),
    updated_at       timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT prescription_status_chk CHECK (status IN (
        'active','partially_filled','filled','expired','cancelled')),
    CONSTRAINT prescription_company_id_uk UNIQUE (id, company_id),
    CONSTRAINT prescription_customer_fk FOREIGN KEY (customer_id, company_id)
        REFERENCES nexora.customer (id, company_id)
);
CREATE INDEX prescription_company_idx ON nexora.prescription (company_id, issue_date DESC);

CREATE TABLE nexora.prescription_line (
    id                  uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    prescription_id     uuid        NOT NULL,
    company_id          uuid        NOT NULL,
    product_id          uuid        NOT NULL,
    quantity_prescribed nexora.quantity NOT NULL,
    quantity_dispensed  nexora.quantity NOT NULL DEFAULT 0,
    dosage_instructions text,
    created_at          timestamptz NOT NULL DEFAULT now(),
    updated_at          timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT prescription_line_qty_chk CHECK (quantity_prescribed > 0 AND quantity_dispensed >= 0),
    CONSTRAINT prescription_line_prescription_fk FOREIGN KEY (prescription_id, company_id)
        REFERENCES nexora.prescription (id, company_id) ON DELETE CASCADE,
    CONSTRAINT prescription_line_product_fk FOREIGN KEY (product_id, company_id)
        REFERENCES nexora.product (id, company_id)
);
CREATE INDEX prescription_line_prescription_idx ON nexora.prescription_line (prescription_id);

CREATE TABLE nexora.sale_prescription (
    sale_id         uuid        NOT NULL,
    prescription_id uuid        NOT NULL,
    company_id      uuid        NOT NULL,
    created_at      timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY (sale_id, prescription_id),
    CONSTRAINT sale_prescription_sale_fk FOREIGN KEY (sale_id, company_id)
        REFERENCES nexora.sale (id, company_id) ON DELETE CASCADE,
    CONSTRAINT sale_prescription_prescription_fk FOREIGN KEY (prescription_id, company_id)
        REFERENCES nexora.prescription (id, company_id)
);

-- ===================== RESTAURANT (§19) =====================
CREATE TABLE nexora.restaurant_table (
    id               uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    company_id       uuid        NOT NULL,
    establishment_id uuid        NOT NULL,
    store_id         uuid,
    name             text        NOT NULL,
    code             text        NOT NULL,
    seats            smallint    NOT NULL DEFAULT 2,
    area             text,
    status           text        NOT NULL DEFAULT 'available',
    created_at       timestamptz NOT NULL DEFAULT now(),
    updated_at       timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT restaurant_table_status_chk CHECK (status IN (
        'available','occupied','reserved','inactive')),
    CONSTRAINT restaurant_table_company_code_uk UNIQUE (company_id, code),
    CONSTRAINT restaurant_table_company_id_uk   UNIQUE (id, company_id),
    CONSTRAINT restaurant_table_establishment_fk FOREIGN KEY (establishment_id, company_id)
        REFERENCES nexora.establishment (id, company_id)
);
CREATE INDEX restaurant_table_company_idx ON nexora.restaurant_table (company_id);

CREATE TABLE nexora.recipe (
    id             uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    company_id     uuid        NOT NULL,
    product_id     uuid        NOT NULL,
    name           text        NOT NULL,
    yield_quantity nexora.quantity NOT NULL DEFAULT 1,
    yield_unit_id  uuid        NOT NULL,
    instructions   text,
    status         text        NOT NULL DEFAULT 'active',
    created_at     timestamptz NOT NULL DEFAULT now(),
    updated_at     timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT recipe_status_chk CHECK (status IN ('active','inactive','archived')),
    CONSTRAINT recipe_yield_chk  CHECK (yield_quantity > 0),
    CONSTRAINT recipe_company_id_uk UNIQUE (id, company_id),
    CONSTRAINT recipe_product_uk    UNIQUE (product_id),
    CONSTRAINT recipe_product_fk FOREIGN KEY (product_id, company_id)
        REFERENCES nexora.product (id, company_id) ON DELETE CASCADE,
    CONSTRAINT recipe_unit_fk FOREIGN KEY (yield_unit_id, company_id)
        REFERENCES nexora.unit (id, company_id)
);

CREATE TABLE nexora.recipe_ingredient (
    id                  uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    recipe_id           uuid        NOT NULL,
    company_id          uuid        NOT NULL,
    component_product_id uuid       NOT NULL,
    quantity            nexora.quantity NOT NULL,
    unit_id             uuid        NOT NULL,
    wastage_percentage  nexora.percentage NOT NULL DEFAULT 0,
    created_at          timestamptz NOT NULL DEFAULT now(),
    updated_at          timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT recipe_ingredient_qty_chk CHECK (quantity > 0),
    CONSTRAINT recipe_ingredient_wastage_chk CHECK (wastage_percentage >= 0 AND wastage_percentage < 100),
    CONSTRAINT recipe_ingredient_recipe_fk FOREIGN KEY (recipe_id, company_id)
        REFERENCES nexora.recipe (id, company_id) ON DELETE CASCADE,
    CONSTRAINT recipe_ingredient_product_fk FOREIGN KEY (component_product_id, company_id)
        REFERENCES nexora.product (id, company_id),
    CONSTRAINT recipe_ingredient_unit_fk FOREIGN KEY (unit_id, company_id)
        REFERENCES nexora.unit (id, company_id)
);
CREATE INDEX recipe_ingredient_recipe_idx ON nexora.recipe_ingredient (recipe_id);

CREATE TABLE nexora.sale_service_context (
    sale_id             uuid PRIMARY KEY,
    company_id          uuid        NOT NULL,
    restaurant_table_id uuid,
    covers              smallint,
    waiter_user_id      uuid        REFERENCES nexora.app_user(id),
    kitchen_status      text        NOT NULL DEFAULT 'pending',
    tip_amount          nexora.money_amount NOT NULL DEFAULT 0,
    opened_at           timestamptz NOT NULL DEFAULT now(),
    closed_at           timestamptz,
    created_at          timestamptz NOT NULL DEFAULT now(),
    updated_at          timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT sale_service_context_kitchen_chk CHECK (kitchen_status IN (
        'pending','preparing','ready','served','cancelled')),
    CONSTRAINT sale_service_context_tip_chk CHECK (tip_amount >= 0),
    CONSTRAINT sale_service_context_sale_fk FOREIGN KEY (sale_id, company_id)
        REFERENCES nexora.sale (id, company_id) ON DELETE CASCADE,
    CONSTRAINT sale_service_context_table_fk FOREIGN KEY (restaurant_table_id, company_id)
        REFERENCES nexora.restaurant_table (id, company_id)
);
CREATE INDEX sale_service_context_table_idx ON nexora.sale_service_context (restaurant_table_id);

-- ===================== RETAIL (§19) =====================
CREATE TABLE nexora.product_barcode (
    id                    uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    product_id            uuid        NOT NULL,
    company_id            uuid        NOT NULL,
    barcode               text        NOT NULL,
    pack_size             nexora.quantity NOT NULL DEFAULT 1,
    is_weight_embedded    boolean     NOT NULL DEFAULT false,
    created_at            timestamptz NOT NULL DEFAULT now(),
    updated_at            timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT product_barcode_company_barcode_uk UNIQUE (company_id, barcode),
    CONSTRAINT product_barcode_product_fk FOREIGN KEY (product_id, company_id)
        REFERENCES nexora.product (id, company_id) ON DELETE CASCADE
);
CREATE INDEX product_barcode_product_idx ON nexora.product_barcode (product_id);

CREATE TABLE nexora.promotion (
    id           uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    company_id   uuid        NOT NULL REFERENCES nexora.company(id),
    name         text        NOT NULL,
    promo_type   text        NOT NULL,
    value        numeric(20,6) NOT NULL DEFAULT 0,
    priority     smallint    NOT NULL DEFAULT 0,
    starts_at    timestamptz,
    ends_at      timestamptz,
    conditions   jsonb       NOT NULL DEFAULT '{}'::jsonb,
    status       text        NOT NULL DEFAULT 'active',
    created_at   timestamptz NOT NULL DEFAULT now(),
    updated_at   timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT promotion_type_chk CHECK (promo_type IN (
        'percentage','amount','buy_x_get_y','bundle','price_override')),
    CONSTRAINT promotion_status_chk CHECK (status IN ('active','inactive','scheduled','expired')),
    CONSTRAINT promotion_period_chk CHECK (ends_at IS NULL OR starts_at IS NULL OR ends_at > starts_at),
    CONSTRAINT promotion_company_id_uk UNIQUE (id, company_id)
);
CREATE INDEX promotion_company_idx ON nexora.promotion (company_id, status);

CREATE TABLE nexora.promotion_target (
    id           uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    promotion_id uuid        NOT NULL,
    company_id   uuid        NOT NULL,
    target_type  text        NOT NULL,
    product_id   uuid,
    category_id  uuid,
    brand_id     uuid,
    created_at   timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT promotion_target_type_chk CHECK (target_type IN ('product','category','brand','all')),
    CONSTRAINT promotion_target_promotion_fk FOREIGN KEY (promotion_id, company_id)
        REFERENCES nexora.promotion (id, company_id) ON DELETE CASCADE,
    CONSTRAINT promotion_target_product_fk FOREIGN KEY (product_id, company_id)
        REFERENCES nexora.product (id, company_id),
    CONSTRAINT promotion_target_category_fk FOREIGN KEY (category_id, company_id)
        REFERENCES nexora.product_category (id, company_id),
    CONSTRAINT promotion_target_brand_fk FOREIGN KEY (brand_id, company_id)
        REFERENCES nexora.brand (id, company_id)
);
CREATE INDEX promotion_target_promotion_idx ON nexora.promotion_target (promotion_id);

CREATE TABLE nexora.coupon (
    id                 uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    company_id         uuid        NOT NULL REFERENCES nexora.company(id),
    code               text        NOT NULL,
    promotion_id       uuid,
    discount_type      text        NOT NULL DEFAULT 'percentage',
    discount_value     numeric(20,6) NOT NULL DEFAULT 0,
    usage_limit        integer,
    used_count         integer     NOT NULL DEFAULT 0,
    per_customer_limit integer,
    valid_from         timestamptz,
    valid_to           timestamptz,
    status             text        NOT NULL DEFAULT 'active',
    created_at         timestamptz NOT NULL DEFAULT now(),
    updated_at         timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT coupon_discount_type_chk CHECK (discount_type IN ('percentage','amount')),
    CONSTRAINT coupon_status_chk CHECK (status IN ('active','inactive','expired','exhausted')),
    CONSTRAINT coupon_company_code_uk UNIQUE (company_id, code),
    CONSTRAINT coupon_promotion_fk FOREIGN KEY (promotion_id, company_id)
        REFERENCES nexora.promotion (id, company_id)
);

-- ===================== CRM / FIDÉLITÉ / LIVRAISON (§20) =====================
CREATE TABLE nexora.loyalty_program (
    id           uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    company_id   uuid        NOT NULL REFERENCES nexora.company(id),
    name         text        NOT NULL,
    earn_rate    numeric(20,6) NOT NULL DEFAULT 1,   -- points par unité de devise
    redeem_rate  numeric(20,6) NOT NULL DEFAULT 1,   -- valeur devise par point
    status       text        NOT NULL DEFAULT 'active',
    created_at   timestamptz NOT NULL DEFAULT now(),
    updated_at   timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT loyalty_program_status_chk CHECK (status IN ('active','inactive')),
    CONSTRAINT loyalty_program_company_id_uk UNIQUE (id, company_id)
);

CREATE TABLE nexora.loyalty_account (
    id             uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    company_id     uuid        NOT NULL,
    customer_id    uuid        NOT NULL,
    program_id     uuid        NOT NULL,
    points_balance numeric(20,6) NOT NULL DEFAULT 0,
    tier           text,
    status         text        NOT NULL DEFAULT 'active',
    created_at     timestamptz NOT NULL DEFAULT now(),
    updated_at     timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT loyalty_account_status_chk CHECK (status IN ('active','inactive','suspended')),
    CONSTRAINT loyalty_account_balance_chk CHECK (points_balance >= 0),
    CONSTRAINT loyalty_account_uk UNIQUE (customer_id, program_id),
    CONSTRAINT loyalty_account_company_id_uk UNIQUE (id, company_id),
    CONSTRAINT loyalty_account_customer_fk FOREIGN KEY (customer_id, company_id)
        REFERENCES nexora.customer (id, company_id),
    CONSTRAINT loyalty_account_program_fk FOREIGN KEY (program_id, company_id)
        REFERENCES nexora.loyalty_program (id, company_id)
);

CREATE TABLE nexora.loyalty_transaction (
    id                uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    company_id        uuid        NOT NULL,
    loyalty_account_id uuid       NOT NULL,
    type              text        NOT NULL,
    points            numeric(20,6) NOT NULL,
    reference_type    text,
    reference_id      uuid,
    occurred_at       timestamptz NOT NULL DEFAULT now(),
    created_at        timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT loyalty_transaction_type_chk CHECK (type IN ('earn','redeem','adjust','expire')),
    CONSTRAINT loyalty_transaction_account_fk FOREIGN KEY (loyalty_account_id, company_id)
        REFERENCES nexora.loyalty_account (id, company_id) ON DELETE CASCADE
);
CREATE INDEX loyalty_transaction_account_idx ON nexora.loyalty_transaction (loyalty_account_id, occurred_at DESC);

CREATE TABLE nexora.delivery_zone (
    id           uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    company_id   uuid        NOT NULL REFERENCES nexora.company(id),
    name         text        NOT NULL,
    fee          nexora.money_amount NOT NULL DEFAULT 0,
    config       jsonb       NOT NULL DEFAULT '{}'::jsonb,
    status       text        NOT NULL DEFAULT 'active',
    created_at   timestamptz NOT NULL DEFAULT now(),
    updated_at   timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT delivery_zone_status_chk CHECK (status IN ('active','inactive')),
    CONSTRAINT delivery_zone_fee_chk CHECK (fee >= 0),
    CONSTRAINT delivery_zone_company_id_uk UNIQUE (id, company_id)
);

CREATE TABLE nexora.delivery (
    id              uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    company_id      uuid        NOT NULL REFERENCES nexora.company(id),
    sale_id         uuid,
    order_reference text,
    customer_id     uuid,
    zone_id         uuid,
    driver_user_id  uuid        REFERENCES nexora.app_user(id),
    address         text,
    status          text        NOT NULL DEFAULT 'pending',
    scheduled_at    timestamptz,
    dispatched_at   timestamptz,
    delivered_at    timestamptz,
    proof_reference text,
    cod_amount      nexora.money_amount NOT NULL DEFAULT 0,
    cod_collected   boolean     NOT NULL DEFAULT false,
    created_at      timestamptz NOT NULL DEFAULT now(),
    updated_at      timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT delivery_status_chk CHECK (status IN (
        'pending','assigned','in_transit','delivered','failed','returned','cancelled')),
    CONSTRAINT delivery_cod_chk CHECK (cod_amount >= 0),
    CONSTRAINT delivery_sale_fk FOREIGN KEY (sale_id, company_id)
        REFERENCES nexora.sale (id, company_id),
    CONSTRAINT delivery_customer_fk FOREIGN KEY (customer_id, company_id)
        REFERENCES nexora.customer (id, company_id),
    CONSTRAINT delivery_zone_fk FOREIGN KEY (zone_id, company_id)
        REFERENCES nexora.delivery_zone (id, company_id)
);
CREATE INDEX delivery_company_status_idx ON nexora.delivery (company_id, status);
CREATE INDEX delivery_driver_idx         ON nexora.delivery (driver_user_id, status);

SELECT nexora.attach_common_triggers();
