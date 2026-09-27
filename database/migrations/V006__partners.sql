-- =============================================================================
-- NEXORA — V006 — Clients et fournisseurs (§9, §10)
-- Soldes matérialisés = cache de performance, réconciliables avec les écritures.
-- =============================================================================
SET search_path TO nexora, public;

-- --- Client ---------------------------------------------------------
CREATE TABLE nexora.customer (
    id                  uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    company_id          uuid        NOT NULL REFERENCES nexora.company(id),
    customer_number     text        NOT NULL,
    name                text        NOT NULL,
    type                text        NOT NULL DEFAULT 'individual',
    customer_group_id   uuid,
    phone               text,
    email               citext,
    address             text,
    tax_identifier      text,
    status              text        NOT NULL DEFAULT 'active',
    credit_limit        nexora.money_amount NOT NULL DEFAULT 0,
    default_currency_id uuid        NOT NULL REFERENCES nexora.currency(id),
    created_at          timestamptz NOT NULL DEFAULT now(),
    updated_at          timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT customer_type_chk   CHECK (type IN ('individual','business')),
    CONSTRAINT customer_status_chk CHECK (status IN ('active','inactive','blocked','archived')),
    CONSTRAINT customer_credit_limit_chk CHECK (credit_limit >= 0),
    CONSTRAINT customer_company_number_uk UNIQUE (company_id, customer_number),
    CONSTRAINT customer_company_id_uk     UNIQUE (id, company_id),
    CONSTRAINT customer_group_fk FOREIGN KEY (customer_group_id, company_id)
        REFERENCES nexora.customer_group (id, company_id)
);
CREATE INDEX customer_company_idx ON nexora.customer (company_id);
CREATE INDEX customer_name_idx    ON nexora.customer (company_id, lower(name));

CREATE TABLE nexora.customer_account (
    id               uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    customer_id      uuid        NOT NULL,
    company_id       uuid        NOT NULL,
    currency_id      uuid        NOT NULL REFERENCES nexora.currency(id),
    credit_limit     nexora.money_amount NOT NULL DEFAULT 0,
    payment_terms_id uuid,
    current_balance  nexora.money_amount NOT NULL DEFAULT 0,   -- + = le client nous doit
    status           text        NOT NULL DEFAULT 'active',
    created_at       timestamptz NOT NULL DEFAULT now(),
    updated_at       timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT customer_account_status_chk CHECK (status IN ('active','inactive','on_hold')),
    CONSTRAINT customer_account_credit_limit_chk CHECK (credit_limit >= 0),
    CONSTRAINT customer_account_currency_uk UNIQUE (customer_id, currency_id),
    CONSTRAINT customer_account_company_id_uk UNIQUE (id, company_id),
    CONSTRAINT customer_account_customer_fk FOREIGN KEY (customer_id, company_id)
        REFERENCES nexora.customer (id, company_id),
    CONSTRAINT customer_account_terms_fk FOREIGN KEY (payment_terms_id, company_id)
        REFERENCES nexora.customer_payment_terms (id, company_id)
);
CREATE INDEX customer_account_customer_idx ON nexora.customer_account (customer_id);
CREATE INDEX customer_account_company_idx  ON nexora.customer_account (company_id);

-- --- Fournisseur ---------------------------------------------------
CREATE TABLE nexora.supplier (
    id                  uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    company_id          uuid        NOT NULL REFERENCES nexora.company(id),
    supplier_number     text        NOT NULL,
    name                text        NOT NULL,
    type                text        NOT NULL DEFAULT 'business',
    phone               text,
    email               citext,
    address             text,
    tax_identifier      text,
    status              text        NOT NULL DEFAULT 'active',
    credit_limit        nexora.money_amount NOT NULL DEFAULT 0,
    default_currency_id uuid        NOT NULL REFERENCES nexora.currency(id),
    created_at          timestamptz NOT NULL DEFAULT now(),
    updated_at          timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT supplier_type_chk   CHECK (type IN ('individual','business')),
    CONSTRAINT supplier_status_chk CHECK (status IN ('active','inactive','blocked','archived')),
    CONSTRAINT supplier_credit_limit_chk CHECK (credit_limit >= 0),
    CONSTRAINT supplier_company_number_uk UNIQUE (company_id, supplier_number),
    CONSTRAINT supplier_company_id_uk     UNIQUE (id, company_id)
);
CREATE INDEX supplier_company_idx ON nexora.supplier (company_id);
CREATE INDEX supplier_name_idx    ON nexora.supplier (company_id, lower(name));

CREATE TABLE nexora.supplier_account (
    id               uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    supplier_id      uuid        NOT NULL,
    company_id       uuid        NOT NULL,
    currency_id      uuid        NOT NULL REFERENCES nexora.currency(id),
    credit_limit     nexora.money_amount NOT NULL DEFAULT 0,   -- limite de crédit fournisseur (§15)
    payment_terms_id uuid,
    current_balance  nexora.money_amount NOT NULL DEFAULT 0,   -- + = nous devons au fournisseur
    status           text        NOT NULL DEFAULT 'active',
    created_at       timestamptz NOT NULL DEFAULT now(),
    updated_at       timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT supplier_account_status_chk CHECK (status IN ('active','inactive','on_hold')),
    CONSTRAINT supplier_account_credit_limit_chk CHECK (credit_limit >= 0),
    CONSTRAINT supplier_account_currency_uk UNIQUE (supplier_id, currency_id),
    CONSTRAINT supplier_account_company_id_uk UNIQUE (id, company_id),
    CONSTRAINT supplier_account_supplier_fk FOREIGN KEY (supplier_id, company_id)
        REFERENCES nexora.supplier (id, company_id),
    CONSTRAINT supplier_account_terms_fk FOREIGN KEY (payment_terms_id, company_id)
        REFERENCES nexora.supplier_payment_terms (id, company_id)
);
CREATE INDEX supplier_account_supplier_idx ON nexora.supplier_account (supplier_id);
CREATE INDEX supplier_account_company_idx  ON nexora.supplier_account (company_id);

SELECT nexora.attach_common_triggers();
