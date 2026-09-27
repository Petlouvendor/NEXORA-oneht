-- =============================================================================
-- NEXORA — V033 — Administration, Paramétrage & Automatisation (Phase 14)
-- Configuration sans code, avec périmètre, audit et versionnement :
--   setting(_definition)  : magasin de paramètres résolus par périmètre (§2-3)
--   workflow / _run       : approbations configurables + moteur de règles testable (§14-16)
--   automation / _run     : planificateur + exécuteurs (relances, alertes, rapports) (§17-18)
--   dunning_policy         : relances clients à niveaux (§19)
--   config_version         : historisation des configs sensibles (§39)
-- Réutilise role/role_permission (§10-11), approval_request (Phase 11), alert_rule,
-- account_mapping, document_sequence, tax, custom_field.
-- =============================================================================
SET search_path TO nexora, public;

-- ===================== 1. Catalogue des paramètres (§4) ===================
CREATE TABLE nexora.setting_definition (
    key               text PRIMARY KEY,
    category          text        NOT NULL,
    value_type        text        NOT NULL,                  -- string | number | boolean | json | enum
    default_value     jsonb       NOT NULL DEFAULT 'null'::jsonb,
    allowed_values    jsonb       NOT NULL DEFAULT '[]'::jsonb, -- value_type = enum
    max_scope         text        NOT NULL DEFAULT 'company', -- niveau le plus fin où le paramètre peut être posé
    is_sensitive      boolean     NOT NULL DEFAULT false,     -- contrôle renforcé + jamais exporté en clair
    affects_future    boolean     NOT NULL DEFAULT false,     -- avertir avant modification (§45)
    description       text        NOT NULL DEFAULT '',
    CONSTRAINT setting_definition_type_chk CHECK (value_type IN ('string','number','boolean','json','enum')),
    CONSTRAINT setting_definition_scope_chk CHECK (max_scope IN ('system','group','company','establishment'))
);

CREATE TABLE nexora.setting (
    id           uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    key          text        NOT NULL REFERENCES nexora.setting_definition(key),
    scope        text        NOT NULL,                        -- system | group | company | establishment
    scope_id     uuid,                                        -- NULL pour system
    company_id   uuid REFERENCES nexora.company(id),          -- pour l'isolation RLS ; NULL = global (system/group)
    value        jsonb       NOT NULL,
    note         text,
    updated_by   uuid REFERENCES nexora.app_user(id),
    created_at   timestamptz NOT NULL DEFAULT now(),
    updated_at   timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT setting_scope_chk CHECK (scope IN ('system','group','company','establishment')),
    CONSTRAINT setting_scope_target_chk CHECK (
        (scope = 'system' AND scope_id IS NULL) OR (scope <> 'system' AND scope_id IS NOT NULL)),
    CONSTRAINT setting_uk UNIQUE NULLS NOT DISTINCT (key, scope, scope_id)
);
CREATE INDEX setting_lookup_idx ON nexora.setting (key, scope);

-- ===================== 2. Workflows configurables (§14-16) ================
CREATE TABLE nexora.workflow (
    id              uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    company_id      uuid        NOT NULL REFERENCES nexora.company(id),
    code            text        NOT NULL,
    name            text        NOT NULL,
    trigger_event   text        NOT NULL,                     -- expense_recorded | discount_applied | stock_adjustment | ...
    definition      jsonb       NOT NULL DEFAULT '{}'::jsonb, -- { logic, conditions[], approval, actions[], notifications[] }
    status          text        NOT NULL DEFAULT 'draft',
    is_tested       boolean     NOT NULL DEFAULT false,
    last_tested_at  timestamptz,
    created_by      uuid REFERENCES nexora.app_user(id),
    updated_by      uuid REFERENCES nexora.app_user(id),
    created_at      timestamptz NOT NULL DEFAULT now(),
    updated_at      timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT workflow_status_chk CHECK (status IN ('draft','active','inactive')),
    CONSTRAINT workflow_company_code_uk UNIQUE (company_id, code),
    CONSTRAINT workflow_company_id_uk   UNIQUE (id, company_id)
);

CREATE TABLE nexora.workflow_run (
    id                  uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    company_id          uuid        NOT NULL,
    workflow_id         uuid        NOT NULL,
    mode                text        NOT NULL DEFAULT 'live',  -- live | test
    trigger_ref_type    text,
    trigger_ref_id      uuid,
    context             jsonb       NOT NULL DEFAULT '{}'::jsonb,
    matched             boolean     NOT NULL DEFAULT false,
    decision            jsonb       NOT NULL DEFAULT '{}'::jsonb,  -- conditions évaluées + effets prévus/réalisés
    approval_request_id uuid,
    created_by          uuid REFERENCES nexora.app_user(id),
    created_at          timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT workflow_run_mode_chk CHECK (mode IN ('live','test')),
    CONSTRAINT workflow_run_workflow_fk FOREIGN KEY (workflow_id, company_id)
        REFERENCES nexora.workflow (id, company_id) ON DELETE CASCADE
);
CREATE INDEX workflow_run_wf_idx ON nexora.workflow_run (workflow_id, created_at DESC);

-- ===================== 3. Automatisations & planificateur (§17-18) ========
CREATE TABLE nexora.automation (
    id            uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    company_id    uuid        NOT NULL REFERENCES nexora.company(id),
    code          text        NOT NULL,
    name          text        NOT NULL,
    kind          text        NOT NULL,
    schedule      jsonb       NOT NULL DEFAULT '{}'::jsonb,   -- { frequency, atTime, dayOfWeek, dayOfMonth, startDate, endDate, timezone }
    parameters    jsonb       NOT NULL DEFAULT '{}'::jsonb,
    status        text        NOT NULL DEFAULT 'active',
    last_run_at   timestamptz,
    next_run_at   timestamptz,
    created_by    uuid REFERENCES nexora.app_user(id),
    created_at    timestamptz NOT NULL DEFAULT now(),
    updated_at    timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT automation_kind_chk CHECK (kind IN (
        'dunning','low_stock_alert','expiry_alert','scheduled_report','supplier_reminder',
        'reconcile_assist','archive','scheduled_export')),
    CONSTRAINT automation_status_chk CHECK (status IN ('active','paused')),
    CONSTRAINT automation_company_code_uk UNIQUE (company_id, code),
    CONSTRAINT automation_company_id_uk   UNIQUE (id, company_id)
);
CREATE INDEX automation_due_idx ON nexora.automation (next_run_at) WHERE status = 'active';

CREATE TABLE nexora.automation_run (
    id               uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    company_id       uuid        NOT NULL,
    automation_id    uuid        NOT NULL,
    mode             text        NOT NULL DEFAULT 'live',     -- live | test (prévisualisation)
    status           text        NOT NULL DEFAULT 'running',
    items_processed  integer     NOT NULL DEFAULT 0,
    result           jsonb       NOT NULL DEFAULT '{}'::jsonb,
    error            text,
    attempt          smallint    NOT NULL DEFAULT 1,
    started_at       timestamptz NOT NULL DEFAULT now(),
    finished_at      timestamptz,
    CONSTRAINT automation_run_mode_chk CHECK (mode IN ('live','test')),
    CONSTRAINT automation_run_status_chk CHECK (status IN ('running','success','failed','skipped')),
    CONSTRAINT automation_run_automation_fk FOREIGN KEY (automation_id, company_id)
        REFERENCES nexora.automation (id, company_id) ON DELETE CASCADE
);
CREATE INDEX automation_run_auto_idx ON nexora.automation_run (automation_id, started_at DESC);

-- ===================== 4. Relances clients (§19) =========================
CREATE TABLE nexora.dunning_policy (
    id          uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    company_id  uuid        NOT NULL REFERENCES nexora.company(id),
    name        text        NOT NULL,
    channel     text        NOT NULL DEFAULT 'email',
    levels      jsonb       NOT NULL DEFAULT '[]'::jsonb,     -- [{ level, offsetDays, title, message }]
    status      text        NOT NULL DEFAULT 'active',
    created_by  uuid REFERENCES nexora.app_user(id),
    created_at  timestamptz NOT NULL DEFAULT now(),
    updated_at  timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT dunning_policy_channel_chk CHECK (channel IN ('email','sms','whatsapp','internal')),
    CONSTRAINT dunning_policy_status_chk CHECK (status IN ('active','inactive')),
    CONSTRAINT dunning_policy_company_uk UNIQUE (company_id, name)
);

ALTER TABLE nexora.customer ADD COLUMN IF NOT EXISTS dunning_opt_out boolean NOT NULL DEFAULT false;

-- ===================== 5. Versionnement de configuration (§39) ============
CREATE TABLE nexora.config_version (
    id             uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    company_id     uuid        NOT NULL REFERENCES nexora.company(id),
    domain         text        NOT NULL,                      -- numbering | accounting_defaults | taxes | approvals | pos | stock
    version_number integer     NOT NULL,
    status         text        NOT NULL DEFAULT 'draft',
    effective_from timestamptz,
    effective_to   timestamptz,
    snapshot       jsonb       NOT NULL DEFAULT '{}'::jsonb,
    diff           jsonb       NOT NULL DEFAULT '{}'::jsonb,
    note           text,
    created_by     uuid REFERENCES nexora.app_user(id),
    approved_by    uuid REFERENCES nexora.app_user(id),
    approved_at    timestamptz,
    created_at     timestamptz NOT NULL DEFAULT now(),
    updated_at     timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT config_version_status_chk CHECK (status IN ('draft','active','superseded')),
    CONSTRAINT config_version_period_chk CHECK (effective_to IS NULL OR effective_from IS NULL OR effective_to >= effective_from),
    CONSTRAINT config_version_uk UNIQUE (company_id, domain, version_number)
);
CREATE INDEX config_version_active_idx ON nexora.config_version (company_id, domain, effective_from);

-- ===================== 6. Permissions (§4, §11) ==========================
INSERT INTO nexora.permission (code, name, module, action) VALUES
    ('admin.settings',        'Administration — paramètres',                 'admin', 'settings'),
    ('admin.workflows',       'Administration — workflows d''approbation',    'admin', 'workflows'),
    ('admin.automations',     'Administration — automatisations',             'admin', 'automations'),
    ('admin.config_versions', 'Administration — versions de configuration',   'admin', 'config_versions'),
    ('admin.roles',           'Administration — rôles personnalisés',         'admin', 'roles')
ON CONFLICT (code) DO NOTHING;

CREATE OR REPLACE FUNCTION pg_temp.grant_perms_v33(p_role text, p_patterns text[])
RETURNS void LANGUAGE sql AS $$
    INSERT INTO nexora.role_permission (role_id, permission_id)
    SELECT r.id, p.id FROM nexora.role r
    JOIN nexora.permission p ON p.code LIKE ANY (p_patterns)
    WHERE r.name = p_role AND r.system_role
    ON CONFLICT DO NOTHING;
$$;

SELECT pg_temp.grant_perms_v33('super_administrator', ARRAY['admin.settings','admin.workflows','admin.automations','admin.config_versions','admin.roles']);
SELECT pg_temp.grant_perms_v33('group_administrator', ARRAY['admin.settings','admin.workflows','admin.automations','admin.config_versions','admin.roles']);
SELECT pg_temp.grant_perms_v33('owner_direction',     ARRAY['admin.settings','admin.workflows','admin.automations','admin.config_versions','admin.roles']);
SELECT pg_temp.grant_perms_v33('manager',             ARRAY['admin.workflows','admin.automations']);
SELECT pg_temp.grant_perms_v33('chief_accountant',    ARRAY['admin.settings','admin.workflows','admin.config_versions']);

-- ===================== 7. RLS — rattrapage entreprise ====================
DO $$
DECLARE
    r record;
    excluded text[] := ARRAY['audit_log','notification','schema_migration','sector_module_preset','fraud_rule','setting_definition'];
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

-- setting : company_id nullable (rows globales system/group visibles à tous) → politique explicite.
ALTER TABLE nexora.setting ENABLE ROW LEVEL SECURITY;
ALTER TABLE nexora.setting FORCE ROW LEVEL SECURITY;
DROP POLICY IF EXISTS company_isolation ON nexora.setting;
CREATE POLICY company_isolation ON nexora.setting
    USING (nexora.rls_allows_company(company_id))
    WITH CHECK (nexora.rls_allows_company(company_id));

-- Audit des changements de configuration (§38).
DO $$
DECLARE t text;
BEGIN
    FOREACH t IN ARRAY ARRAY['setting','workflow','automation','dunning_policy','config_version'] LOOP
        EXECUTE format('DROP TRIGGER IF EXISTS trg_audit_%1$s ON nexora.%1$I', t);
        EXECUTE format(
            'CREATE TRIGGER trg_audit_%1$s AFTER INSERT OR UPDATE OR DELETE ON nexora.%1$I
               FOR EACH ROW EXECUTE FUNCTION nexora.audit_row()', t);
    END LOOP;
END;
$$;

SELECT nexora.attach_common_triggers();

-- ===================== 8. Catalogue de paramètres seedé ==================
INSERT INTO nexora.setting_definition (key, category, value_type, default_value, allowed_values, max_scope, is_sensitive, affects_future, description) VALUES
    ('pos.max_discount_percent',        'pos',        'number',  '15',            '[]', 'establishment', false, false, 'Remise maximale sans approbation (%)'),
    ('pos.allow_credit_sale',           'pos',        'boolean', 'true',          '[]', 'establishment', false, false, 'Autoriser la vente à crédit au POS'),
    ('pos.require_customer_for_credit', 'pos',        'boolean', 'true',          '[]', 'company',       false, false, 'Exiger un client pour une vente à crédit'),
    ('stock.valuation_method',          'stock',      'enum',    '"weighted_average"', '["weighted_average","fifo","standard"]', 'company', true, true, 'Méthode de valorisation du stock'),
    ('stock.enforce_fefo',              'stock',      'boolean', 'false',         '[]', 'establishment', false, false, 'Imposer la sortie FEFO (péremption la plus proche)'),
    ('stock.negative_stock_allowed',    'stock',      'boolean', 'false',         '[]', 'company',       false, true,  'Autoriser le stock négatif'),
    ('finance.rounding_mode',           'finance',    'enum',    '"half_up"',     '["half_up","half_even","down"]', 'company', false, true, 'Mode d''arrondi monétaire'),
    ('finance.manual_fx_rate_allowed',  'finance',    'boolean', 'false',         '[]', 'company',       true,  false, 'Autoriser la saisie manuelle d''un taux de change'),
    ('accounting.lock_closed_period',   'accounting', 'boolean', 'true',          '[]', 'company',       true,  true,  'Bloquer toute écriture sur une période clôturée'),
    ('security.session_minutes',        'security',   'number',  '60',            '[]', 'company',       true,  false, 'Durée maximale d''une session (minutes)'),
    ('backup.enabled',                  'backup',     'boolean', 'false',         '[]', 'company',       false, false, 'Sauvegarde automatique configurée'),
    ('documents.default_language',      'documents',  'enum',    '"fr"',          '["fr","en"]', 'company', false, false, 'Langue par défaut des documents')
ON CONFLICT (key) DO NOTHING;
