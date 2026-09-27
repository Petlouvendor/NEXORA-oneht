-- =============================================================================
-- NEXORA — V031 — Intégrations, Paiements & Écosystème (Phase 12)
-- Le cœur métier reste indépendant du fournisseur : chaque intégration passe par
-- un connecteur (contrat commun), possède son état, ses logs et ses références,
-- et ses webhooks entrants sont authentifiés et idempotents (§2, §12-13, §38).
-- Réutilise financial_account / financial_transaction / payment / bank_statement.
-- =============================================================================
SET search_path TO nexora, public;

-- ===================== 1. Registre des connecteurs (§5-6) ===================
CREATE TABLE nexora.integration_connector (
    id                uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    company_id        uuid        NOT NULL REFERENCES nexora.company(id),
    code              text        NOT NULL,
    name              text        NOT NULL,
    connector_type    text        NOT NULL,
    provider          text        NOT NULL,
    environment       text        NOT NULL DEFAULT 'test',
    status            text        NOT NULL DEFAULT 'inactive',
    config            jsonb       NOT NULL DEFAULT '{}'::jsonb,   -- paramètres non secrets
    webhook_secret    text,                                       -- secret de signature des webhooks entrants
    credentials_ref   text,                                       -- pointeur vers un coffre — jamais le secret en clair (§38)
    connector_version text        NOT NULL DEFAULT '1',
    last_sync_at      timestamptz,
    last_error        text,
    last_error_at     timestamptz,
    configured_by     uuid        REFERENCES nexora.app_user(id),
    created_at        timestamptz NOT NULL DEFAULT now(),
    updated_at        timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT integration_connector_type_chk CHECK (connector_type IN (
        'payment','bank','ecommerce','marketplace','messaging','email_sms',
        'document_storage','accounting_export','pos_terminal','other')),
    CONSTRAINT integration_connector_env_chk CHECK (environment IN ('test','production')),
    CONSTRAINT integration_connector_status_chk CHECK (status IN ('inactive','active','error')),
    CONSTRAINT integration_connector_company_code_uk UNIQUE (company_id, code),
    CONSTRAINT integration_connector_company_id_uk   UNIQUE (id, company_id)
);
CREATE INDEX integration_connector_company_idx ON nexora.integration_connector (company_id, connector_type);

-- ===================== 2. Abstraction des paiements (§7-11) =================
CREATE TABLE nexora.payment_intent (
    id                   uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    company_id           uuid        NOT NULL REFERENCES nexora.company(id),
    connector_id         uuid        NOT NULL,
    idempotency_key      text        NOT NULL,
    provider_reference   text,
    amount               nexora.money_amount NOT NULL,
    currency_id          uuid        NOT NULL REFERENCES nexora.currency(id),
    method               text        NOT NULL DEFAULT 'card',
    purpose              text        NOT NULL DEFAULT 'link',       -- sale | receivable | link
    sale_id              uuid,
    customer_id          uuid,
    financial_account_id uuid,
    payment_id           uuid,                                       -- paiement réel créé à la confirmation
    status               text        NOT NULL DEFAULT 'pending',
    provider_response    jsonb       NOT NULL DEFAULT '{}'::jsonb,
    failure_reason       text,
    refunded_amount      nexora.money_amount NOT NULL DEFAULT 0,      -- suivi côté prestataire (§9) ; l'avoir comptable relève de la Phase 6
    expires_at           timestamptz,
    confirmed_at         timestamptz,
    requested_by         uuid        REFERENCES nexora.app_user(id),
    created_at           timestamptz NOT NULL DEFAULT now(),
    updated_at           timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT payment_intent_amount_chk CHECK (amount > 0),
    CONSTRAINT payment_intent_refund_chk CHECK (refunded_amount >= 0 AND refunded_amount <= amount),
    CONSTRAINT payment_intent_purpose_chk CHECK (purpose IN ('sale','receivable','link')),
    CONSTRAINT payment_intent_status_chk CHECK (status IN (
        'pending','authorized','confirmed','failed','cancelled','expired','amount_mismatch',
        'partially_refunded','refunded')),
    CONSTRAINT payment_intent_idempotency_uk UNIQUE (idempotency_key),
    CONSTRAINT payment_intent_connector_fk FOREIGN KEY (connector_id, company_id)
        REFERENCES nexora.integration_connector (id, company_id),
    CONSTRAINT payment_intent_sale_fk FOREIGN KEY (sale_id, company_id)
        REFERENCES nexora.sale (id, company_id),
    CONSTRAINT payment_intent_customer_fk FOREIGN KEY (customer_id, company_id)
        REFERENCES nexora.customer (id, company_id),
    CONSTRAINT payment_intent_account_fk FOREIGN KEY (financial_account_id, company_id)
        REFERENCES nexora.financial_account (id, company_id),
    CONSTRAINT payment_intent_payment_fk FOREIGN KEY (payment_id, company_id)
        REFERENCES nexora.payment (id, company_id)
);
CREATE INDEX payment_intent_company_status_idx ON nexora.payment_intent (company_id, status, created_at DESC);
CREATE INDEX payment_intent_provider_ref_idx   ON nexora.payment_intent (connector_id, provider_reference);

-- ===================== 3. Webhooks entrants (§12-13) =======================
CREATE TABLE nexora.webhook_event (
    id                uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    company_id        uuid        NOT NULL,
    connector_id      uuid        NOT NULL,
    provider_event_id text        NOT NULL,
    event_type        text        NOT NULL DEFAULT 'unknown',
    signature_valid   boolean     NOT NULL DEFAULT false,
    payload           jsonb       NOT NULL DEFAULT '{}'::jsonb,
    status            text        NOT NULL DEFAULT 'received',
    error             text,
    received_at       timestamptz NOT NULL DEFAULT now(),
    processed_at      timestamptz,
    CONSTRAINT webhook_event_status_chk CHECK (status IN ('received','processed','failed','duplicate','ignored')),
    CONSTRAINT webhook_event_dedup_uk UNIQUE (connector_id, provider_event_id),
    CONSTRAINT webhook_event_connector_fk FOREIGN KEY (connector_id, company_id)
        REFERENCES nexora.integration_connector (id, company_id) ON DELETE CASCADE
);
CREATE INDEX webhook_event_company_status_idx ON nexora.webhook_event (company_id, status, received_at DESC);

-- ===================== 4. Webhooks sortants (§32) =========================
CREATE TABLE nexora.outbound_webhook (
    id                   uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    company_id           uuid        NOT NULL REFERENCES nexora.company(id),
    name                 text        NOT NULL,
    target_url           text        NOT NULL,
    secret               text        NOT NULL,
    subscribed_events    jsonb       NOT NULL DEFAULT '[]'::jsonb,
    status               text        NOT NULL DEFAULT 'active',
    consecutive_failures integer     NOT NULL DEFAULT 0,
    last_delivery_at     timestamptz,
    created_by           uuid        REFERENCES nexora.app_user(id),
    created_at           timestamptz NOT NULL DEFAULT now(),
    updated_at           timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT outbound_webhook_status_chk CHECK (status IN ('active','paused')),
    CONSTRAINT outbound_webhook_company_id_uk UNIQUE (id, company_id)
);
CREATE INDEX outbound_webhook_company_idx ON nexora.outbound_webhook (company_id) WHERE status = 'active';

CREATE TABLE nexora.outbound_webhook_delivery (
    id                  uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    company_id          uuid        NOT NULL,
    outbound_webhook_id uuid        NOT NULL,
    event_type          text        NOT NULL,
    event_id            uuid        NOT NULL,
    payload             jsonb       NOT NULL DEFAULT '{}'::jsonb,
    attempts            integer     NOT NULL DEFAULT 0,
    max_attempts        integer     NOT NULL DEFAULT 5,
    status              text        NOT NULL DEFAULT 'pending',
    response_code       integer,
    next_attempt_at     timestamptz NOT NULL DEFAULT now(),
    last_error          text,
    delivered_at        timestamptz,
    created_at          timestamptz NOT NULL DEFAULT now(),
    updated_at          timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT outbound_webhook_delivery_status_chk CHECK (status IN ('pending','delivered','failed','exhausted')),
    CONSTRAINT outbound_webhook_delivery_webhook_fk FOREIGN KEY (outbound_webhook_id, company_id)
        REFERENCES nexora.outbound_webhook (id, company_id) ON DELETE CASCADE,
    CONSTRAINT outbound_webhook_delivery_dedup_uk UNIQUE (outbound_webhook_id, event_id)
);
CREATE INDEX outbound_webhook_delivery_due_idx ON nexora.outbound_webhook_delivery (next_attempt_at)
    WHERE status IN ('pending','failed');

-- ===================== 5. Permissions (§38) ==============================
INSERT INTO nexora.permission (code, name, module, action) VALUES
    ('integrations.configure', 'Intégrations — connecteurs et secrets',       'integrations', 'configure'),
    ('integrations.payments',  'Intégrations — paiements et rapprochement',   'integrations', 'payments'),
    ('integrations.webhooks',  'Intégrations — webhooks entrants et sortants', 'integrations', 'webhooks'),
    ('integrations.bank',      'Intégrations — relevés et rapprochement bancaire', 'integrations', 'bank')
ON CONFLICT (code) DO NOTHING;

CREATE OR REPLACE FUNCTION pg_temp.grant_perms_v31(p_role text, p_patterns text[])
RETURNS void LANGUAGE sql AS $$
    INSERT INTO nexora.role_permission (role_id, permission_id)
    SELECT r.id, p.id FROM nexora.role r
    JOIN nexora.permission p ON p.code LIKE ANY (p_patterns)
    WHERE r.name = p_role AND r.system_role
    ON CONFLICT DO NOTHING;
$$;

SELECT pg_temp.grant_perms_v31('super_administrator', ARRAY['integrations.%']);
SELECT pg_temp.grant_perms_v31('group_administrator', ARRAY['integrations.%']);
SELECT pg_temp.grant_perms_v31('owner_direction',     ARRAY['integrations.%']);
SELECT pg_temp.grant_perms_v31('manager',             ARRAY['integrations.view','integrations.payments','integrations.webhooks']);
SELECT pg_temp.grant_perms_v31('chief_accountant',    ARRAY['integrations.view','integrations.payments','integrations.bank']);
SELECT pg_temp.grant_perms_v31('senior_accountant',   ARRAY['integrations.view','integrations.payments','integrations.bank']);
SELECT pg_temp.grant_perms_v31('accountant',          ARRAY['integrations.view','integrations.bank']);
SELECT pg_temp.grant_perms_v31('cashier',             ARRAY['integrations.payments']);
SELECT pg_temp.grant_perms_v31('auditor',             ARRAY['integrations.view']);

-- ===================== 6. RLS — rattrapage post-V020 =====================
DO $$
DECLARE
    r record;
    excluded text[] := ARRAY['audit_log','notification','schema_migration','sector_module_preset','fraud_rule'];
BEGIN
    FOR r IN
        SELECT c.table_name
        FROM information_schema.columns c
        JOIN information_schema.tables t
          ON t.table_schema = c.table_schema AND t.table_name = c.table_name
        LEFT JOIN pg_tables pt ON pt.schemaname = 'nexora' AND pt.tablename = c.table_name
        WHERE c.table_schema = 'nexora' AND c.column_name = 'company_id' AND c.is_nullable = 'NO'
          AND t.table_type = 'BASE TABLE' AND c.table_name <> ALL (excluded) AND (pt.rowsecurity IS NOT TRUE)
    LOOP
        PERFORM nexora.enable_company_rls(format('nexora.%I', r.table_name)::regclass);
    END LOOP;
END;
$$;

-- Audit des connecteurs et de leurs secrets (§38).
DO $$
DECLARE t text;
BEGIN
    FOREACH t IN ARRAY ARRAY['integration_connector','outbound_webhook'] LOOP
        EXECUTE format('DROP TRIGGER IF EXISTS trg_audit_%1$s ON nexora.%1$I', t);
        EXECUTE format(
            'CREATE TRIGGER trg_audit_%1$s AFTER INSERT OR UPDATE OR DELETE ON nexora.%1$I
               FOR EACH ROW EXECUTE FUNCTION nexora.audit_row()', t);
    END LOOP;
END;
$$;

SELECT nexora.attach_common_triggers();
