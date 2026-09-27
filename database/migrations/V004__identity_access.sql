-- =============================================================================
-- NEXORA — V004 — Utilisateurs, rôles, permissions et périmètres (§6, §8 CDC)
-- Modèle : Utilisateur + Rôle + Permission + Périmètre.
-- =============================================================================
SET search_path TO nexora, public;

-- --- Utilisateur (plateforme ; rattaché aux entreprises via access_scope) --
CREATE TABLE nexora.app_user (
    id                uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    email             citext      NOT NULL,
    username          citext,
    password_hash     text,                       -- §34 : jamais en clair
    external_identity text,                       -- SSO / IdP (§5 ENTERPRISE)
    full_name         text,
    status            text        NOT NULL DEFAULT 'active',
    mfa_enabled       boolean     NOT NULL DEFAULT false,
    mfa_secret        text,                       -- chiffré côté application
    last_login_at     timestamptz,
    created_at        timestamptz NOT NULL DEFAULT now(),
    updated_at        timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT app_user_email_uk    UNIQUE (email),
    CONSTRAINT app_user_username_uk UNIQUE (username),
    CONSTRAINT app_user_status_chk  CHECK (status IN ('active','suspended','locked','archived')),
    CONSTRAINT app_user_identity_chk CHECK (password_hash IS NOT NULL OR external_identity IS NOT NULL)
);

-- --- Rôle (global si company_id NULL, sinon propre à l'entreprise) --------
CREATE TABLE nexora.role (
    id           uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    company_id   uuid        REFERENCES nexora.company(id),
    name         text        NOT NULL,
    description  text,
    system_role  boolean     NOT NULL DEFAULT false,
    status       text        NOT NULL DEFAULT 'active',
    created_at   timestamptz NOT NULL DEFAULT now(),
    updated_at   timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT role_status_chk CHECK (status IN ('active','inactive'))
);

CREATE UNIQUE INDEX role_global_name_uk  ON nexora.role (name) WHERE company_id IS NULL;
CREATE UNIQUE INDEX role_company_name_uk ON nexora.role (company_id, name) WHERE company_id IS NOT NULL;
CREATE INDEX role_company_idx ON nexora.role (company_id);

-- --- Permission (catalogue global : module + action) --------------------
CREATE TABLE nexora.permission (
    id           uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    code         text        NOT NULL,
    name         text        NOT NULL,
    description  text,
    module       text        NOT NULL,
    action       text        NOT NULL,
    created_at   timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT permission_code_uk UNIQUE (code)
);

CREATE TABLE nexora.role_permission (
    role_id       uuid NOT NULL REFERENCES nexora.role(id) ON DELETE CASCADE,
    permission_id uuid NOT NULL REFERENCES nexora.permission(id) ON DELETE CASCADE,
    created_at    timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY (role_id, permission_id)
);

-- --- Périmètre d'accès -------------------------------------------------
CREATE TABLE nexora.access_scope (
    id               uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    scope_type       text NOT NULL,
    group_id         uuid REFERENCES nexora.business_group(id),
    company_id       uuid REFERENCES nexora.company(id),
    establishment_id uuid REFERENCES nexora.establishment(id),
    store_id         uuid REFERENCES nexora.store(id),
    warehouse_id     uuid REFERENCES nexora.warehouse(id),
    pos_terminal_id  uuid REFERENCES nexora.pos_terminal(id),
    created_at       timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT access_scope_type_chk CHECK (scope_type IN (
        'PLATFORM','GROUP','COMPANY','ESTABLISHMENT','STORE','WAREHOUSE','POS_TERMINAL')),
    CONSTRAINT access_scope_target_chk CHECK (
        (scope_type = 'PLATFORM'      AND group_id IS NULL AND company_id IS NULL AND establishment_id IS NULL AND store_id IS NULL AND warehouse_id IS NULL AND pos_terminal_id IS NULL)
     OR (scope_type = 'GROUP'         AND group_id IS NOT NULL)
     OR (scope_type = 'COMPANY'       AND company_id IS NOT NULL)
     OR (scope_type = 'ESTABLISHMENT' AND establishment_id IS NOT NULL)
     OR (scope_type = 'STORE'         AND store_id IS NOT NULL)
     OR (scope_type = 'WAREHOUSE'     AND warehouse_id IS NOT NULL)
     OR (scope_type = 'POS_TERMINAL'  AND pos_terminal_id IS NOT NULL))
);

CREATE INDEX access_scope_group_idx   ON nexora.access_scope (group_id);
CREATE INDEX access_scope_company_idx ON nexora.access_scope (company_id);

-- --- Affectation Utilisateur → Rôle → Périmètre -----------------------
CREATE TABLE nexora.user_role (
    id          uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    user_id     uuid NOT NULL REFERENCES nexora.app_user(id) ON DELETE CASCADE,
    role_id     uuid NOT NULL REFERENCES nexora.role(id),
    scope_id    uuid NOT NULL REFERENCES nexora.access_scope(id),
    granted_by  uuid REFERENCES nexora.app_user(id),
    granted_at  timestamptz NOT NULL DEFAULT now(),
    created_at  timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT user_role_uk UNIQUE (user_id, role_id, scope_id)
);

CREATE INDEX user_role_user_idx ON nexora.user_role (user_id);
CREATE INDEX user_role_role_idx ON nexora.user_role (role_id);

SELECT nexora.attach_common_triggers();
