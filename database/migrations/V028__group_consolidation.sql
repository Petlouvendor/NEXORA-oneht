-- =============================================================================
-- NEXORA — V028 — Multi-entreprises, Groupe & Consolidation (Phase 9)
-- La consolidation est un PROCESSUS DISTINCT : elle agrège des copies converties
-- et mappées des balances individuelles et n'écrit JAMAIS dans les livres sources
-- (§2, §13, §16). Réutilise business_group / company.group_id (V003),
-- intercompany_transaction / consolidation_profile / consolidation_scope (V014).
-- =============================================================================
SET search_path TO nexora, public;

-- ===================== 1. Plan comptable groupe (§12) ========================
CREATE TABLE nexora.group_account (
    id            uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    group_id      uuid        NOT NULL REFERENCES nexora.business_group(id),
    code          text        NOT NULL,
    name          text        NOT NULL,
    account_type  text        NOT NULL,
    parent_id     uuid        REFERENCES nexora.group_account(id),
    is_eliminable boolean     NOT NULL DEFAULT false,   -- comptes intercompany éliminables (§16)
    sort_order    integer     NOT NULL DEFAULT 0,
    status        text        NOT NULL DEFAULT 'active',
    created_at    timestamptz NOT NULL DEFAULT now(),
    updated_at    timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT group_account_type_chk CHECK (account_type IN ('asset','liability','equity','revenue','expense')),
    CONSTRAINT group_account_status_chk CHECK (status IN ('active','inactive')),
    CONSTRAINT group_account_group_code_uk UNIQUE (group_id, code),
    CONSTRAINT group_account_group_id_uk   UNIQUE (id, group_id)
);
CREATE INDEX group_account_group_idx ON nexora.group_account (group_id, sort_order);

-- Mapping compte local → compte groupe, versionné et daté (§12).
CREATE TABLE nexora.account_group_mapping (
    id               uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    group_id         uuid        NOT NULL REFERENCES nexora.business_group(id),
    company_id       uuid        NOT NULL,
    account_id       uuid        NOT NULL,
    group_account_id uuid        NOT NULL,
    effective_from   date        NOT NULL DEFAULT current_date,
    effective_to     date,
    status           text        NOT NULL DEFAULT 'active',
    created_by       uuid        REFERENCES nexora.app_user(id),
    created_at       timestamptz NOT NULL DEFAULT now(),
    updated_at       timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT account_group_mapping_status_chk CHECK (status IN ('active','superseded')),
    CONSTRAINT account_group_mapping_account_fk FOREIGN KEY (account_id, company_id)
        REFERENCES nexora.account (id, company_id),
    CONSTRAINT account_group_mapping_group_account_fk FOREIGN KEY (group_account_id, group_id)
        REFERENCES nexora.group_account (id, group_id),
    CONSTRAINT account_group_mapping_company_group_fk FOREIGN KEY (company_id, group_id)
        REFERENCES nexora.company (id, group_id)
);
CREATE UNIQUE INDEX account_group_mapping_active_uk
    ON nexora.account_group_mapping (account_id) WHERE status = 'active';
CREATE INDEX account_group_mapping_group_idx ON nexora.account_group_mapping (group_id, company_id);

-- ===================== 2. Politiques groupe (§26) ============================
CREATE TABLE nexora.group_policy (
    id                        uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    group_id                  uuid        NOT NULL UNIQUE REFERENCES nexora.business_group(id),
    consolidation_currency_id uuid        NOT NULL REFERENCES nexora.currency(id),
    fx_method                 text        NOT NULL DEFAULT 'closing_rate',
    intercompany_tolerance    nexora.money_amount NOT NULL DEFAULT 0,
    config                    jsonb       NOT NULL DEFAULT '{}'::jsonb,
    created_at                timestamptz NOT NULL DEFAULT now(),
    updated_at                timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT group_policy_fx_method_chk CHECK (fx_method IN ('closing_rate','average_rate','historical')),
    CONSTRAINT group_policy_tolerance_chk CHECK (intercompany_tolerance >= 0)
);

-- ===================== 3. Rapprochement intercompany (§15) ==================
ALTER TABLE nexora.intercompany_transaction
    ADD CONSTRAINT intercompany_transaction_group_id_uk UNIQUE (id, group_id);

CREATE TABLE nexora.intercompany_match (
    id                     uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    group_id               uuid        NOT NULL REFERENCES nexora.business_group(id),
    source_transaction_id  uuid        NOT NULL,
    counterpart_transaction_id uuid,
    status                 text        NOT NULL DEFAULT 'unmatched',
    amount_difference      nexora.money_amount NOT NULL DEFAULT 0,
    currency_match         boolean     NOT NULL DEFAULT true,
    date_difference_days   integer     NOT NULL DEFAULT 0,
    notes                  text,
    matched_by             uuid        REFERENCES nexora.app_user(id),
    matched_at             timestamptz,
    created_at             timestamptz NOT NULL DEFAULT now(),
    updated_at             timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT intercompany_match_status_chk CHECK (status IN (
        'unmatched','matched','asymmetric','missing_counterpart')),
    CONSTRAINT intercompany_match_source_fk FOREIGN KEY (source_transaction_id, group_id)
        REFERENCES nexora.intercompany_transaction (id, group_id) ON DELETE CASCADE,
    CONSTRAINT intercompany_match_counterpart_fk FOREIGN KEY (counterpart_transaction_id, group_id)
        REFERENCES nexora.intercompany_transaction (id, group_id),
    CONSTRAINT intercompany_match_source_uk UNIQUE (source_transaction_id)
);
CREATE INDEX intercompany_match_group_idx ON nexora.intercompany_match (group_id, status);

-- ===================== 4. Processus de consolidation (§17-19) ================
CREATE TABLE nexora.consolidation_run (
    id                       uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    group_id                 uuid        NOT NULL REFERENCES nexora.business_group(id),
    consolidation_profile_id uuid,
    period_label             text        NOT NULL,
    from_date                date        NOT NULL,
    to_date                  date        NOT NULL,
    currency_id              uuid        NOT NULL REFERENCES nexora.currency(id),
    status                   text        NOT NULL DEFAULT 'draft',
    coherence_ok             boolean     NOT NULL DEFAULT false,
    total_debit              nexora.money_amount NOT NULL DEFAULT 0,
    total_credit             nexora.money_amount NOT NULL DEFAULT 0,
    elimination_total        nexora.money_amount NOT NULL DEFAULT 0,
    diagnostics              jsonb       NOT NULL DEFAULT '[]'::jsonb,
    generated_at             timestamptz,
    validated_by             uuid        REFERENCES nexora.app_user(id),
    validated_at             timestamptz,
    published_by             uuid        REFERENCES nexora.app_user(id),
    published_at             timestamptz,
    reopen_reason            text,
    notes                    text,
    created_by               uuid        REFERENCES nexora.app_user(id),
    created_at               timestamptz NOT NULL DEFAULT now(),
    updated_at               timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT consolidation_run_status_chk CHECK (status IN (
        'draft','preparing','reconciling','ready','validated','published','reopened')),
    CONSTRAINT consolidation_run_range_chk CHECK (to_date >= from_date),
    CONSTRAINT consolidation_run_group_id_uk UNIQUE (id, group_id),
    CONSTRAINT consolidation_run_profile_fk FOREIGN KEY (consolidation_profile_id, group_id)
        REFERENCES nexora.consolidation_profile (id, group_id)
);
CREATE INDEX consolidation_run_group_idx ON nexora.consolidation_run (group_id, to_date DESC);

CREATE TABLE nexora.consolidation_run_company (
    id                   uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    consolidation_run_id uuid        NOT NULL,
    group_id             uuid        NOT NULL,
    company_id           uuid        NOT NULL,
    included             boolean     NOT NULL DEFAULT true,
    functional_currency_id uuid,
    fx_rate              numeric(20,8) NOT NULL DEFAULT 1,
    period_closed        boolean     NOT NULL DEFAULT false,
    mapping_complete     boolean     NOT NULL DEFAULT false,
    contributed_debit    nexora.money_amount NOT NULL DEFAULT 0,
    contributed_credit   nexora.money_amount NOT NULL DEFAULT 0,
    created_at           timestamptz NOT NULL DEFAULT now(),
    updated_at           timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT consolidation_run_company_uk UNIQUE (consolidation_run_id, company_id),
    CONSTRAINT consolidation_run_company_run_fk FOREIGN KEY (consolidation_run_id, group_id)
        REFERENCES nexora.consolidation_run (id, group_id) ON DELETE CASCADE,
    CONSTRAINT consolidation_run_company_company_fk FOREIGN KEY (company_id, group_id)
        REFERENCES nexora.company (id, group_id)
);
CREATE INDEX consolidation_run_company_run_idx ON nexora.consolidation_run_company (consolidation_run_id);

CREATE TABLE nexora.consolidation_run_line (
    id                   uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    consolidation_run_id uuid        NOT NULL,
    group_id             uuid        NOT NULL,
    group_account_id     uuid        NOT NULL,
    account_type         text        NOT NULL,
    aggregated_debit     nexora.money_amount NOT NULL DEFAULT 0,
    aggregated_credit    nexora.money_amount NOT NULL DEFAULT 0,
    elimination_debit    nexora.money_amount NOT NULL DEFAULT 0,
    elimination_credit   nexora.money_amount NOT NULL DEFAULT 0,
    consolidated_debit   nexora.money_amount NOT NULL DEFAULT 0,
    consolidated_credit  nexora.money_amount NOT NULL DEFAULT 0,
    created_at           timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT consolidation_run_line_uk UNIQUE (consolidation_run_id, group_account_id),
    CONSTRAINT consolidation_run_line_run_fk FOREIGN KEY (consolidation_run_id, group_id)
        REFERENCES nexora.consolidation_run (id, group_id) ON DELETE CASCADE,
    CONSTRAINT consolidation_run_line_account_fk FOREIGN KEY (group_account_id, group_id)
        REFERENCES nexora.group_account (id, group_id)
);
CREATE INDEX consolidation_run_line_run_idx ON nexora.consolidation_run_line (consolidation_run_id);

-- ===================== 5. Tâches groupe (§25) ================================
CREATE TABLE nexora.group_task (
    id               uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    group_id         uuid        NOT NULL REFERENCES nexora.business_group(id),
    company_id       uuid,
    title            text        NOT NULL,
    description      text,
    assignee_user_id uuid        REFERENCES nexora.app_user(id),
    priority         text        NOT NULL DEFAULT 'normal',
    due_date         date,
    status           text        NOT NULL DEFAULT 'open',
    notes            text,
    created_by       uuid        REFERENCES nexora.app_user(id),
    created_at       timestamptz NOT NULL DEFAULT now(),
    updated_at       timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT group_task_priority_chk CHECK (priority IN ('low','normal','high','urgent')),
    CONSTRAINT group_task_status_chk CHECK (status IN ('open','in_progress','blocked','done','cancelled')),
    CONSTRAINT group_task_company_group_fk FOREIGN KEY (company_id, group_id)
        REFERENCES nexora.company (id, group_id)
);
CREATE INDEX group_task_group_idx ON nexora.group_task (group_id, status);

-- ===================== 6. Archivage d'entreprise (§29) ======================
ALTER TABLE nexora.company
    ADD COLUMN archived_at timestamptz,
    ADD COLUMN archived_by uuid REFERENCES nexora.app_user(id);

-- ===================== 7. Permissions groupe (§21) ==========================
INSERT INTO nexora.permission (code, name, module, action) VALUES
    ('group.view',              'Groupe — vue et KPI groupe',              'group',         'view'),
    ('group.manage',            'Groupe — paramètres et politiques',       'group',         'manage'),
    ('group.mappings',          'Groupe — plan comptable et mappings',     'group',         'mappings'),
    ('group.intercompany',      'Groupe — transactions intercompany',      'group',         'intercompany'),
    ('group.tasks',             'Groupe — tâches de contrôle',             'group',         'tasks'),
    ('group.companies',         'Groupe — créer / archiver une entreprise','group',         'companies'),
    ('consolidation.run',       'Consolidation — lancer',                  'consolidation', 'run'),
    ('consolidation.validate',  'Consolidation — valider',                 'consolidation', 'validate'),
    ('consolidation.publish',   'Consolidation — publier',                 'consolidation', 'publish')
ON CONFLICT (code) DO NOTHING;

CREATE OR REPLACE FUNCTION pg_temp.grant_perms_v28(p_role text, p_patterns text[])
RETURNS void LANGUAGE sql AS $$
    INSERT INTO nexora.role_permission (role_id, permission_id)
    SELECT r.id, p.id FROM nexora.role r
    JOIN nexora.permission p ON p.code LIKE ANY (p_patterns)
    WHERE r.name = p_role AND r.system_role
    ON CONFLICT DO NOTHING;
$$;

SELECT pg_temp.grant_perms_v28('super_administrator', ARRAY['group.%','consolidation.%']);
SELECT pg_temp.grant_perms_v28('group_administrator', ARRAY['group.%','consolidation.%']);
SELECT pg_temp.grant_perms_v28('owner_direction',     ARRAY['group.%','consolidation.%']);
SELECT pg_temp.grant_perms_v28('chief_accountant',    ARRAY[
    'group.view','group.manage','group.mappings','group.intercompany','group.tasks',
    'consolidation.run','consolidation.validate','consolidation.publish']);
SELECT pg_temp.grant_perms_v28('senior_accountant',   ARRAY['group.view','group.intercompany','group.tasks','consolidation.run']);
SELECT pg_temp.grant_perms_v28('accountant',          ARRAY['group.view']);
SELECT pg_temp.grant_perms_v28('auditor',             ARRAY['group.view']);
SELECT pg_temp.grant_perms_v28('manager',             ARRAY['group.view']);

-- ===================== 8. RLS ==============================================
-- Tables portées par le groupe → isolation par périmètre groupe (X-Group-Scope).
SELECT nexora.enable_group_rls('nexora.group_account'::regclass);
SELECT nexora.enable_group_rls('nexora.group_policy'::regclass);
SELECT nexora.enable_group_rls('nexora.intercompany_match'::regclass);
SELECT nexora.enable_group_rls('nexora.consolidation_run'::regclass);
SELECT nexora.enable_group_rls('nexora.consolidation_run_company'::regclass);
SELECT nexora.enable_group_rls('nexora.consolidation_run_line'::regclass);
SELECT nexora.enable_group_rls('nexora.group_task'::regclass);

-- Rattrapage : toute table métier à company_id NOT NULL ajoutée après V020
-- qui n'a pas encore la politique d'isolation entreprise (§22).
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

-- ===================== 9. Politique groupe par défaut =======================
INSERT INTO nexora.group_policy (group_id, consolidation_currency_id)
SELECT id, default_currency_id FROM nexora.business_group
ON CONFLICT (group_id) DO NOTHING;

SELECT nexora.attach_common_triggers();
