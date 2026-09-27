-- =============================================================================
-- NEXORA ONE — V052 — Migration, importation & onboarding avancé (Phase 39)
--
-- Permet à une entreprise de démarrer sans repartir de zéro : importer, profiler,
-- mapper, valider, simuler puis intégrer des données CSV / JSON / anciens systèmes,
-- avec prévisualisation, erreurs localisées ligne par ligne, rapprochement et rollback (§2).
--
-- Réutilisé :
--   Phase 5  : `customer` / `supplier` / `product` / `product_category` / `unit` / `product_price`
--   Phase 5  : `IStockLedger` (écrivain unique des stocks) + type de mouvement `opening` (V024)
--   Phase 11 : `audit_log` immuable (trigger) — sert l'audit migration (§36)
--   Phase 32 : `sector_profile` + `IConfigGuardrails` (onboarding automatique §31)
--   Phase 34 : `INotificationService` (fin d'import, anomalies §46)
--
-- Le pipeline est **générique** : `import_job` porte le fichier, le profil, le mapping, la
-- simulation et le rapprochement ; `import_row` porte l'état ligne par ligne ; `import_record`
-- trace les objets créés pour permettre un rollback avant finalisation (§37).
-- =============================================================================
SET search_path TO nexora, public;

-- ===================== 1. Job d'import (§2, §5, §36) ====================

CREATE TABLE nexora.import_job (
    id               uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    company_id       uuid        NOT NULL REFERENCES nexora.company(id),
    kind             text        NOT NULL,                    -- customers | suppliers | products | opening_stock (§4)
    source_type      text        NOT NULL DEFAULT 'csv',      -- csv | json | xlsx | api | manual (§3, §44)
    file_name        text,
    content          text,                                     -- fichier original conservé si la politique le permet (§2, §42)
    content_hash     text,                                      -- SHA-256 du contenu (traçabilité §22, §36)
    status           text        NOT NULL DEFAULT 'created',
    mode             text        NOT NULL DEFAULT 'upsert',    -- create_only | update_only | upsert | skip_duplicates | reject_duplicates (§29)
    batch_size       integer     NOT NULL DEFAULT 500,         -- §27
    total_rows       integer     NOT NULL DEFAULT 0,
    processed_rows   integer     NOT NULL DEFAULT 0,
    ok_rows          integer     NOT NULL DEFAULT 0,
    warning_rows     integer     NOT NULL DEFAULT 0,
    error_rows       integer     NOT NULL DEFAULT 0,
    duplicate_rows   integer     NOT NULL DEFAULT 0,
    created_count    integer     NOT NULL DEFAULT 0,
    updated_count    integer     NOT NULL DEFAULT 0,
    skipped_count    integer     NOT NULL DEFAULT 0,
    rejected_count   integer     NOT NULL DEFAULT 0,
    profile          jsonb,                                     -- §6 profilage
    mapping          jsonb,                                     -- §7 colonne source → champ + transformations
    simulation       jsonb,                                     -- §24 prévisualisation figée
    reconciliation   jsonb,                                     -- §35 rapprochement post-import
    error_message    text,
    paused_at        timestamptz,
    finalized_at     timestamptz,                               -- §36 finalisation (rollback impossible ensuite)
    created_by       uuid REFERENCES nexora.app_user(id),
    created_at       timestamptz NOT NULL DEFAULT now(),
    updated_at       timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT import_job_kind_chk   CHECK (kind IN ('customers','suppliers','products','opening_stock')),
    CONSTRAINT import_job_source_chk CHECK (source_type IN ('csv','json','xlsx','api','manual')),
    CONSTRAINT import_job_status_chk CHECK (status IN
        ('created','profiled','mapped','validated','simulated','executing','paused','completed','failed','canceled')),
    CONSTRAINT import_job_mode_chk   CHECK (mode IN
        ('create_only','update_only','upsert','skip_duplicates','reject_duplicates')),
    CONSTRAINT import_job_batch_chk  CHECK (batch_size BETWEEN 1 AND 10000),
    CONSTRAINT import_job_company_id_uk UNIQUE (id, company_id)
);
CREATE INDEX import_job_company_idx ON nexora.import_job (company_id, created_at DESC);
CREATE INDEX import_job_running_idx ON nexora.import_job (status) WHERE status IN ('executing','paused');

-- ===================== 2. Ligne d'import (§2 « ligne par ligne », §26) ==

CREATE TABLE nexora.import_row (
    id             uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    import_job_id  uuid        NOT NULL,
    company_id     uuid        NOT NULL,
    row_number     integer     NOT NULL,                      -- numéro dans le fichier source (1-based)
    source         jsonb       NOT NULL DEFAULT '{}'::jsonb,  -- ligne brute (clé = colonne source)
    dedup_key      text,                                       -- clé de correspondance (identifiant externe / code / e-mail §9, §28)
    match_type     text        NOT NULL DEFAULT 'none',       -- exact | probable | none (§9)
    status         text        NOT NULL DEFAULT 'pending',    -- pending | valid | warning | error | created | updated | skipped | rejected
    target_id      uuid,                                       -- entité NEXORA créée / rapprochée
    messages       jsonb       NOT NULL DEFAULT '[]'::jsonb,  -- [{ level, field, message }] (§26)
    created_at     timestamptz NOT NULL DEFAULT now(),
    updated_at     timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT import_row_status_chk CHECK (status IN
        ('pending','valid','warning','error','created','updated','skipped','rejected')),
    CONSTRAINT import_row_match_chk  CHECK (match_type IN ('exact','probable','none')),
    CONSTRAINT import_row_job_fk FOREIGN KEY (import_job_id, company_id)
        REFERENCES nexora.import_job (id, company_id) ON DELETE CASCADE,
    CONSTRAINT import_row_job_number_uk UNIQUE (import_job_id, row_number)
);
CREATE INDEX import_row_job_status_idx ON nexora.import_row (import_job_id, status);
CREATE INDEX import_row_dedup_idx      ON nexora.import_row (import_job_id, dedup_key);

-- ===================== 3. Trace des objets créés → rollback (§37) ======

CREATE TABLE nexora.import_record (
    id             uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    import_job_id  uuid        NOT NULL,
    company_id     uuid        NOT NULL,
    entity_type    text        NOT NULL,                      -- customer | supplier | product | product_category | unit | stock_movement | product_price
    entity_id      uuid        NOT NULL,
    action         text        NOT NULL,                      -- created | updated
    reverted_at    timestamptz,
    created_at     timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT import_record_action_chk CHECK (action IN ('created','updated')),
    CONSTRAINT import_record_job_fk FOREIGN KEY (import_job_id, company_id)
        REFERENCES nexora.import_job (id, company_id) ON DELETE CASCADE
);
CREATE INDEX import_record_job_idx    ON nexora.import_record (import_job_id) WHERE reverted_at IS NULL;
CREATE INDEX import_record_entity_idx ON nexora.import_record (entity_type, entity_id);

-- ===================== 4. Mappings sauvegardés (§7) ====================

CREATE TABLE nexora.import_mapping_template (
    id           uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    company_id   uuid        NOT NULL REFERENCES nexora.company(id),
    kind         text        NOT NULL,
    name         text        NOT NULL,
    mapping      jsonb       NOT NULL DEFAULT '{}'::jsonb,
    created_by   uuid REFERENCES nexora.app_user(id),
    created_at   timestamptz NOT NULL DEFAULT now(),
    updated_at   timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT import_mapping_template_uk UNIQUE (company_id, kind, name)
);

-- ===================== 5. Journal d'événements (§46) ==================

CREATE TABLE nexora.import_event (
    id            uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    import_job_id uuid        NOT NULL,
    company_id    uuid        NOT NULL,
    event_type    text        NOT NULL,                       -- ImportCreated | ImportProfiled | … | MigrationReconciled (§46)
    payload       jsonb       NOT NULL DEFAULT '{}'::jsonb,
    actor_user_id uuid REFERENCES nexora.app_user(id),
    occurred_at   timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT import_event_job_fk FOREIGN KEY (import_job_id, company_id)
        REFERENCES nexora.import_job (id, company_id) ON DELETE CASCADE
);
CREATE INDEX import_event_job_idx ON nexora.import_event (import_job_id, occurred_at);

-- ===================== 6. Checklist d'onboarding (§31-32) =============
-- La plupart des items sont dérivés en direct de l'état des données ; cette table
-- porte les acquittements manuels et les items « à cocher » (formation, tests…).

CREATE TABLE nexora.onboarding_task (
    id            uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    company_id    uuid        NOT NULL REFERENCES nexora.company(id),
    task_key      text        NOT NULL,                        -- company | logo | currency | taxes | establishments | … | tests (§32)
    status        text        NOT NULL DEFAULT 'pending',     -- pending | done | skipped
    note          text,
    completed_by  uuid REFERENCES nexora.app_user(id),
    completed_at  timestamptz,
    created_at    timestamptz NOT NULL DEFAULT now(),
    updated_at    timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT onboarding_task_status_chk CHECK (status IN ('pending','done','skipped')),
    CONSTRAINT onboarding_task_uk UNIQUE (company_id, task_key)
);

-- ===================== 7. RLS (§42 scope entreprise) & audit (§36) ====

SELECT nexora.enable_company_rls('nexora.import_job'::regclass);
SELECT nexora.enable_company_rls('nexora.import_row'::regclass);
SELECT nexora.enable_company_rls('nexora.import_record'::regclass);
SELECT nexora.enable_company_rls('nexora.import_mapping_template'::regclass);
SELECT nexora.enable_company_rls('nexora.import_event'::regclass);
SELECT nexora.enable_company_rls('nexora.onboarding_task'::regclass);

DO $$
DECLARE t text;
BEGIN
    FOREACH t IN ARRAY ARRAY['import_job','import_row','import_record','import_mapping_template','onboarding_task'] LOOP
        EXECUTE format('DROP TRIGGER IF EXISTS trg_audit_%1$s ON nexora.%1$I', t);
        EXECUTE format(
            'CREATE TRIGGER trg_audit_%1$s AFTER INSERT OR UPDATE OR DELETE ON nexora.%1$I
               FOR EACH ROW EXECUTE FUNCTION nexora.audit_row()', t);
    END LOOP;
END;
$$;

-- ===================== 8. Permissions (§42-43) ========================

INSERT INTO nexora.permission (code, name, module, action) VALUES
    ('migration.view',   'Migration — consulter les imports et l''onboarding',        'migration', 'view'),
    ('migration.manage', 'Migration — créer, mapper, simuler, exécuter et annuler un import', 'migration', 'manage')
ON CONFLICT (code) DO NOTHING;

CREATE OR REPLACE FUNCTION pg_temp.grant_perms_v52(p_role text, p_patterns text[])
RETURNS void LANGUAGE sql AS $$
    INSERT INTO nexora.role_permission (role_id, permission_id)
    SELECT r.id, p.id FROM nexora.role r
    JOIN nexora.permission p ON p.code LIKE ANY (p_patterns)
    WHERE r.name = p_role AND r.system_role
    ON CONFLICT DO NOTHING;
$$;
SELECT pg_temp.grant_perms_v52('super_administrator', ARRAY['migration.%']);
SELECT pg_temp.grant_perms_v52('group_administrator', ARRAY['migration.%']);
SELECT pg_temp.grant_perms_v52('owner_direction',     ARRAY['migration.%']);
SELECT pg_temp.grant_perms_v52('manager',             ARRAY['migration.view']);
SELECT pg_temp.grant_perms_v52('chief_accountant',    ARRAY['migration.view']);

SELECT nexora.attach_common_triggers();
