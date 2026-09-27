-- =============================================================================
-- NEXORA — V003 — Structure organisationnelle (§4, §5, §30)
-- PLATFORM → GROUP → COMPANY → ESTABLISHMENT → STORE/WAREHOUSE → POS
-- =============================================================================
SET search_path TO nexora, public;

-- --- Groupe (« group » est réservé en SQL → business_group) ----------------
CREATE TABLE nexora.business_group (
    id                  uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    name                text        NOT NULL,
    legal_name          text,
    tax_identifier      text,
    status              text        NOT NULL DEFAULT 'active',
    default_currency_id uuid        NOT NULL REFERENCES nexora.currency(id),
    settings            jsonb       NOT NULL DEFAULT '{}'::jsonb,
    created_at          timestamptz NOT NULL DEFAULT now(),
    updated_at          timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT business_group_status_chk CHECK (status IN ('active','inactive','archived'))
);

-- --- Entreprise -----------------------------------------------------------
CREATE TABLE nexora.company (
    id                  uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    group_id            uuid        REFERENCES nexora.business_group(id),  -- §4 : nullable = entreprise indépendante
    name                text        NOT NULL,
    legal_name          text,
    tax_identifier      text,
    status              text        NOT NULL DEFAULT 'active',
    main_activity_type  text        NOT NULL DEFAULT 'other',
    complexity_level    text        NOT NULL DEFAULT 'SIMPLE',    -- §5
    deployment_mode     text        NOT NULL DEFAULT 'LOCAL',     -- §6
    default_currency_id uuid        NOT NULL REFERENCES nexora.currency(id),
    timezone            text        NOT NULL DEFAULT 'America/Port-au-Prince',
    settings            jsonb       NOT NULL DEFAULT '{}'::jsonb,
    created_at          timestamptz NOT NULL DEFAULT now(),
    updated_at          timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT company_status_chk CHECK (status IN ('active','inactive','archived')),
    CONSTRAINT company_activity_chk CHECK (main_activity_type IN (
        'supermarket','grocery','pharmacy','restaurant','boutique','hotel',
        'hardware_store','wholesaler','distributor','services','production',
        'ecommerce','other')),
    CONSTRAINT company_complexity_chk CHECK (complexity_level IN (
        'SIMPLE','STANDARD','ADVANCED','ENTERPRISE')),
    CONSTRAINT company_deployment_chk CHECK (deployment_mode IN (
        'LOCAL','HYBRID','CLOUD')),
    CONSTRAINT company_group_id_uk UNIQUE (id, group_id)
);

CREATE INDEX company_group_idx ON nexora.company (group_id);

-- --- Établissement ------------------------------------------------------
CREATE TABLE nexora.establishment (
    id           uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    company_id   uuid        NOT NULL REFERENCES nexora.company(id),
    name         text        NOT NULL,
    code         text        NOT NULL,
    address      text,
    phone        text,
    status       text        NOT NULL DEFAULT 'active',
    settings     jsonb       NOT NULL DEFAULT '{}'::jsonb,
    created_at   timestamptz NOT NULL DEFAULT now(),
    updated_at   timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT establishment_status_chk CHECK (status IN ('active','inactive','archived')),
    CONSTRAINT establishment_company_code_uk UNIQUE (company_id, code),
    CONSTRAINT establishment_company_id_uk   UNIQUE (id, company_id)  -- support FK composite
);

CREATE INDEX establishment_company_idx ON nexora.establishment (company_id);

-- --- Magasin -----------------------------------------------------------
CREATE TABLE nexora.store (
    id               uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    establishment_id uuid        NOT NULL,
    company_id       uuid        NOT NULL,
    name             text        NOT NULL,
    code             text        NOT NULL,
    status           text        NOT NULL DEFAULT 'active',
    settings         jsonb       NOT NULL DEFAULT '{}'::jsonb,
    created_at       timestamptz NOT NULL DEFAULT now(),
    updated_at       timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT store_status_chk CHECK (status IN ('active','inactive','archived')),
    CONSTRAINT store_company_code_uk UNIQUE (company_id, code),
    CONSTRAINT store_company_id_uk   UNIQUE (id, company_id),
    CONSTRAINT store_establishment_fk FOREIGN KEY (establishment_id, company_id)
        REFERENCES nexora.establishment (id, company_id)
);

CREATE INDEX store_establishment_idx ON nexora.store (establishment_id);
CREATE INDEX store_company_idx       ON nexora.store (company_id);

-- --- Entrepôt --------------------------------------------------------
CREATE TABLE nexora.warehouse (
    id               uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    establishment_id uuid        NOT NULL,
    company_id       uuid        NOT NULL,
    name             text        NOT NULL,
    code             text        NOT NULL,
    type             text        NOT NULL DEFAULT 'general',
    status           text        NOT NULL DEFAULT 'active',
    settings         jsonb       NOT NULL DEFAULT '{}'::jsonb,
    created_at       timestamptz NOT NULL DEFAULT now(),
    updated_at       timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT warehouse_status_chk CHECK (status IN ('active','inactive','archived')),
    CONSTRAINT warehouse_type_chk   CHECK (type IN ('general','cold','bonded','transit','quarantine')),
    CONSTRAINT warehouse_company_code_uk UNIQUE (company_id, code),
    CONSTRAINT warehouse_company_id_uk   UNIQUE (id, company_id),
    CONSTRAINT warehouse_establishment_fk FOREIGN KEY (establishment_id, company_id)
        REFERENCES nexora.establishment (id, company_id)
);

CREATE INDEX warehouse_establishment_idx ON nexora.warehouse (establishment_id);
CREATE INDEX warehouse_company_idx       ON nexora.warehouse (company_id);

-- --- Terminal POS ---------------------------------------------------
CREATE TABLE nexora.pos_terminal (
    id                uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    store_id          uuid        NOT NULL,
    company_id        uuid        NOT NULL,
    name              text        NOT NULL,
    code              text        NOT NULL,
    device_identifier text,
    status            text        NOT NULL DEFAULT 'active',
    configuration     jsonb       NOT NULL DEFAULT '{}'::jsonb,
    created_at        timestamptz NOT NULL DEFAULT now(),
    updated_at        timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT pos_terminal_status_chk CHECK (status IN ('active','inactive','archived')),
    CONSTRAINT pos_terminal_company_code_uk   UNIQUE (company_id, code),
    CONSTRAINT pos_terminal_company_id_uk     UNIQUE (id, company_id),
    CONSTRAINT pos_terminal_device_uk         UNIQUE (device_identifier),
    CONSTRAINT pos_terminal_store_fk FOREIGN KEY (store_id, company_id)
        REFERENCES nexora.store (id, company_id)
);

CREATE INDEX pos_terminal_store_idx   ON nexora.pos_terminal (store_id);
CREATE INDEX pos_terminal_company_idx ON nexora.pos_terminal (company_id);

SELECT nexora.attach_common_triggers();
