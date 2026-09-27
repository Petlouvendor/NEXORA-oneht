-- =============================================================================
-- NEXORA — V005 — Référentiels par entreprise (§7)
-- Taxes, moyens de paiement, unités, catégories, marques, groupes clients,
-- conditions de règlement, catégories de dépense.
-- =============================================================================
SET search_path TO nexora, public;

-- --- Taxes ------------------------------------------------------------
CREATE TABLE nexora.tax (
    id           uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    company_id   uuid        NOT NULL REFERENCES nexora.company(id),
    name         text        NOT NULL,
    code         text        NOT NULL,
    rate         nexora.percentage NOT NULL DEFAULT 0,
    type         text        NOT NULL DEFAULT 'vat',
    is_inclusive boolean     NOT NULL DEFAULT false,
    status       text        NOT NULL DEFAULT 'active',
    created_at   timestamptz NOT NULL DEFAULT now(),
    updated_at   timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT tax_status_chk CHECK (status IN ('active','inactive')),
    CONSTRAINT tax_rate_chk   CHECK (rate >= 0 AND rate <= 100),
    CONSTRAINT tax_type_chk   CHECK (type IN ('vat','sales_tax','excise','withholding','none','other')),
    CONSTRAINT tax_company_code_uk UNIQUE (company_id, code),
    CONSTRAINT tax_company_id_uk   UNIQUE (id, company_id)
);
CREATE INDEX tax_company_idx ON nexora.tax (company_id);

-- --- Moyens de paiement (§18) --------------------------------------
CREATE TABLE nexora.payment_method (
    id                 uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    company_id         uuid        NOT NULL REFERENCES nexora.company(id),
    name               text        NOT NULL,
    type               text        NOT NULL,
    currency_id        uuid        REFERENCES nexora.currency(id),
    requires_reference boolean     NOT NULL DEFAULT false,
    status             text        NOT NULL DEFAULT 'active',
    settings           jsonb       NOT NULL DEFAULT '{}'::jsonb,
    created_at         timestamptz NOT NULL DEFAULT now(),
    updated_at         timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT payment_method_status_chk CHECK (status IN ('active','inactive')),
    CONSTRAINT payment_method_type_chk CHECK (type IN (
        'cash','card','bank_transfer','check','mobile_money','credit','store_credit','voucher','other')),
    CONSTRAINT payment_method_company_id_uk UNIQUE (id, company_id)
);
CREATE INDEX payment_method_company_idx ON nexora.payment_method (company_id);

-- --- Unités de mesure --------------------------------------------
CREATE TABLE nexora.unit (
    id           uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    company_id   uuid        NOT NULL REFERENCES nexora.company(id),
    name         text        NOT NULL,
    code         text        NOT NULL,
    precision    smallint    NOT NULL DEFAULT 0,
    is_weight    boolean     NOT NULL DEFAULT false,   -- §13 produits au poids
    created_at   timestamptz NOT NULL DEFAULT now(),
    updated_at   timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT unit_precision_chk  CHECK (precision BETWEEN 0 AND 6),
    CONSTRAINT unit_company_code_uk UNIQUE (company_id, code),
    CONSTRAINT unit_company_id_uk   UNIQUE (id, company_id)
);
CREATE INDEX unit_company_idx ON nexora.unit (company_id);

-- --- Catégories / marques produit --------------------------------
CREATE TABLE nexora.product_category (
    id           uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    company_id   uuid        NOT NULL REFERENCES nexora.company(id),
    parent_id    uuid        REFERENCES nexora.product_category(id),
    name         text        NOT NULL,
    code         text,
    status       text        NOT NULL DEFAULT 'active',
    created_at   timestamptz NOT NULL DEFAULT now(),
    updated_at   timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT product_category_status_chk CHECK (status IN ('active','inactive','archived')),
    CONSTRAINT product_category_company_id_uk UNIQUE (id, company_id)
);
CREATE INDEX product_category_company_idx ON nexora.product_category (company_id);
CREATE INDEX product_category_parent_idx  ON nexora.product_category (parent_id);

CREATE TABLE nexora.brand (
    id           uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    company_id   uuid        NOT NULL REFERENCES nexora.company(id),
    name         text        NOT NULL,
    status       text        NOT NULL DEFAULT 'active',
    created_at   timestamptz NOT NULL DEFAULT now(),
    updated_at   timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT brand_status_chk CHECK (status IN ('active','inactive','archived')),
    CONSTRAINT brand_company_name_uk UNIQUE (company_id, name),
    CONSTRAINT brand_company_id_uk   UNIQUE (id, company_id)
);
CREATE INDEX brand_company_idx ON nexora.brand (company_id);

-- --- Groupes de clients (tarification différenciée §8, §13) ------
CREATE TABLE nexora.customer_group (
    id           uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    company_id   uuid        NOT NULL REFERENCES nexora.company(id),
    name         text        NOT NULL,
    description  text,
    status       text        NOT NULL DEFAULT 'active',
    created_at   timestamptz NOT NULL DEFAULT now(),
    updated_at   timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT customer_group_status_chk CHECK (status IN ('active','inactive')),
    CONSTRAINT customer_group_company_name_uk UNIQUE (company_id, name),
    CONSTRAINT customer_group_company_id_uk   UNIQUE (id, company_id)
);
CREATE INDEX customer_group_company_idx ON nexora.customer_group (company_id);

-- --- Conditions de règlement client / fournisseur (§9, §10, §15) -
CREATE TABLE nexora.customer_payment_terms (
    id           uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    company_id   uuid        NOT NULL REFERENCES nexora.company(id),
    name         text        NOT NULL,
    days         integer     NOT NULL DEFAULT 0,
    description  text,
    created_at   timestamptz NOT NULL DEFAULT now(),
    updated_at   timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT customer_payment_terms_days_chk CHECK (days >= 0),
    CONSTRAINT customer_payment_terms_company_name_uk UNIQUE (company_id, name),
    CONSTRAINT customer_payment_terms_company_id_uk   UNIQUE (id, company_id)
);

CREATE TABLE nexora.supplier_payment_terms (
    id           uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    company_id   uuid        NOT NULL REFERENCES nexora.company(id),
    name         text        NOT NULL,
    days         integer     NOT NULL DEFAULT 0,        -- 30/60/90 ou personnalisé (§15)
    early_payment_discount_rate  nexora.percentage NOT NULL DEFAULT 0,
    early_payment_discount_days  integer NOT NULL DEFAULT 0,
    late_penalty_rate            nexora.percentage NOT NULL DEFAULT 0,
    description  text,
    created_at   timestamptz NOT NULL DEFAULT now(),
    updated_at   timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT supplier_payment_terms_days_chk CHECK (days >= 0),
    CONSTRAINT supplier_payment_terms_company_name_uk UNIQUE (company_id, name),
    CONSTRAINT supplier_payment_terms_company_id_uk   UNIQUE (id, company_id)
);

-- --- Catégories de dépense (§18 CDC) ----------------------------
CREATE TABLE nexora.expense_category (
    id           uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    company_id   uuid        NOT NULL REFERENCES nexora.company(id),
    parent_id    uuid        REFERENCES nexora.expense_category(id),
    name         text        NOT NULL,
    code         text,
    status       text        NOT NULL DEFAULT 'active',
    created_at   timestamptz NOT NULL DEFAULT now(),
    updated_at   timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT expense_category_status_chk CHECK (status IN ('active','inactive','archived')),
    CONSTRAINT expense_category_company_id_uk UNIQUE (id, company_id)
);
CREATE INDEX expense_category_company_idx ON nexora.expense_category (company_id);

SELECT nexora.attach_common_triggers();
