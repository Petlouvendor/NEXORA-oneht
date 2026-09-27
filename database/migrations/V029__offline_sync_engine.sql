-- =============================================================================
-- NEXORA — V029 — Moteur offline & synchronisation (Phase 10)
-- Le mode de déploiement ne change pas les règles métier (§2). Cette migration
-- ajoute la politique offline configurable, les lots de synchronisation et enrichit
-- le modèle de conflits déjà posé en V017. L'idempotence reste portée par
-- operation_uuid (unique global) + source_operation_uuid sur les entités (Phase 6).
-- =============================================================================
SET search_path TO nexora, public;

-- ===================== 1. Politique offline par entreprise (§10, §17) ========
CREATE TABLE nexora.offline_policy (
    id                      uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    company_id              uuid        NOT NULL UNIQUE REFERENCES nexora.company(id),
    allow_offline_sales     boolean     NOT NULL DEFAULT true,
    allow_offline_credit    boolean     NOT NULL DEFAULT false,
    offline_credit_limit    nexora.money_amount NOT NULL DEFAULT 0,
    allow_offline_discount  boolean     NOT NULL DEFAULT true,
    allow_offline_refund    boolean     NOT NULL DEFAULT false,
    max_offline_minutes     integer     NOT NULL DEFAULT 720,
    pending_alert_threshold integer     NOT NULL DEFAULT 50,
    allowed_payment_methods jsonb       NOT NULL DEFAULT '["cash"]'::jsonb,
    auto_sync               boolean     NOT NULL DEFAULT true,
    config                  jsonb       NOT NULL DEFAULT '{}'::jsonb,
    created_at              timestamptz NOT NULL DEFAULT now(),
    updated_at              timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT offline_policy_credit_limit_chk CHECK (offline_credit_limit >= 0),
    CONSTRAINT offline_policy_minutes_chk      CHECK (max_offline_minutes >= 0),
    CONSTRAINT offline_policy_threshold_chk    CHECK (pending_alert_threshold >= 0)
);

-- ===================== 2. Lot de synchronisation (§14, §30) ==================
CREATE TABLE nexora.sync_batch (
    id               uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    company_id       uuid        NOT NULL,
    device_id        uuid        NOT NULL,
    operation_count  integer     NOT NULL DEFAULT 0,
    confirmed_count  integer     NOT NULL DEFAULT 0,
    conflict_count   integer     NOT NULL DEFAULT 0,
    failed_count     integer     NOT NULL DEFAULT 0,
    received_at      timestamptz NOT NULL DEFAULT now(),
    created_at       timestamptz NOT NULL DEFAULT now(),
    updated_at       timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT sync_batch_company_id_uk UNIQUE (id, company_id),
    CONSTRAINT sync_batch_device_fk FOREIGN KEY (device_id, company_id)
        REFERENCES nexora.sync_device (id, company_id)
);
CREATE INDEX sync_batch_company_idx ON nexora.sync_batch (company_id, received_at DESC);

-- ===================== 3. Enrichissement du modèle V017 ======================
ALTER TABLE nexora.sync_operation
    ADD COLUMN priority         smallint    NOT NULL DEFAULT 5,   -- 0 = critique, 9 = faible (§14)
    ADD COLUMN result_entity_id uuid,
    ADD COLUMN result_summary   jsonb,
    ADD COLUMN last_attempt_at  timestamptz,
    ADD COLUMN sync_batch_id    uuid;
ALTER TABLE nexora.sync_operation
    ADD CONSTRAINT sync_operation_batch_fk FOREIGN KEY (sync_batch_id, company_id)
        REFERENCES nexora.sync_batch (id, company_id) ON DELETE SET NULL;
CREATE INDEX sync_operation_priority_idx ON nexora.sync_operation (company_id, status, priority);

ALTER TABLE nexora.sync_conflict
    ADD COLUMN conflict_type      text NOT NULL DEFAULT 'business',
    ADD COLUMN detail             text,
    ADD COLUMN recommended_action text,
    ADD COLUMN resolution_note    text;
ALTER TABLE nexora.sync_conflict
    ADD CONSTRAINT sync_conflict_type_chk CHECK (conflict_type IN (
        'stock','credit','duplicate','validation','period','business'));

-- ===================== 4. Permissions (§30, §31) ============================
INSERT INTO nexora.permission (code, name, module, action) VALUES
    ('sync.view',         'Synchronisation — centre de synchronisation',   'sync',    'view'),
    ('sync.manage',       'Synchronisation — forcer / résoudre les conflits','sync',  'manage'),
    ('sync.devices',      'Synchronisation — enregistrer / révoquer les appareils', 'sync', 'devices'),
    ('offline.configure', 'Offline — politique de fonctionnement hors connexion',    'offline', 'configure')
ON CONFLICT (code) DO NOTHING;

CREATE OR REPLACE FUNCTION pg_temp.grant_perms_v29(p_role text, p_patterns text[])
RETURNS void LANGUAGE sql AS $$
    INSERT INTO nexora.role_permission (role_id, permission_id)
    SELECT r.id, p.id FROM nexora.role r
    JOIN nexora.permission p ON p.code LIKE ANY (p_patterns)
    WHERE r.name = p_role AND r.system_role
    ON CONFLICT DO NOTHING;
$$;

SELECT pg_temp.grant_perms_v29('super_administrator', ARRAY['sync.%','offline.%']);
SELECT pg_temp.grant_perms_v29('group_administrator', ARRAY['sync.%','offline.%']);
SELECT pg_temp.grant_perms_v29('owner_direction',     ARRAY['sync.%','offline.%']);
SELECT pg_temp.grant_perms_v29('manager',             ARRAY['sync.view','sync.manage','sync.devices','offline.configure']);
SELECT pg_temp.grant_perms_v29('cashier',             ARRAY['sync.view']);
SELECT pg_temp.grant_perms_v29('salesperson',         ARRAY['sync.view']);
SELECT pg_temp.grant_perms_v29('stock_manager',       ARRAY['sync.view','sync.manage']);
SELECT pg_temp.grant_perms_v29('auditor',             ARRAY['sync.view']);

-- ===================== 5. RLS — rattrapage post-V020 (§19, §31) =============
DO $$
DECLARE
    r record;
    excluded text[] := ARRAY['audit_log','notification','schema_migration','sector_module_preset'];
BEGIN
    FOR r IN
        SELECT c.table_name
        FROM information_schema.columns c
        JOIN information_schema.tables t
          ON t.table_schema = c.table_schema AND t.table_name = c.table_name
        LEFT JOIN pg_tables pt
          ON pt.schemaname = 'nexora' AND pt.tablename = c.table_name
        WHERE c.table_schema = 'nexora'
          AND c.column_name  = 'company_id'
          AND c.is_nullable  = 'NO'
          AND t.table_type   = 'BASE TABLE'
          AND c.table_name <> ALL (excluded)
          AND (pt.rowsecurity IS NOT TRUE)
    LOOP
        PERFORM nexora.enable_company_rls(format('nexora.%I', r.table_name)::regclass);
    END LOOP;
END;
$$;

-- ===================== 6. Politique offline par défaut ======================
-- Conservatrice : ventes comptant offline autorisées, crédit refusé (§17).
INSERT INTO nexora.offline_policy (company_id)
SELECT id FROM nexora.company
ON CONFLICT (company_id) DO NOTHING;

SELECT nexora.attach_common_triggers();
