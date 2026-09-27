-- =============================================================================
-- NEXORA — V026 — Dashboard, Rapports & Pilotage (Phase 7)
-- Les rapports sont dérivés des données sources (§2) : ces tables ne stockent
-- que des objectifs, des configurations d'affichage, des planifications et des journaux.
-- =============================================================================
SET search_path TO nexora, public;

-- --- Objectifs KPI (§27) -----------------------------------------------
CREATE TABLE nexora.kpi_target (
    id               uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    company_id       uuid        NOT NULL REFERENCES nexora.company(id),
    establishment_id uuid,
    kpi_key          text        NOT NULL,
    period_type      text        NOT NULL DEFAULT 'month',
    target_value     nexora.money_amount NOT NULL,
    direction        text        NOT NULL DEFAULT 'min',   -- min = à atteindre, max = à ne pas dépasser
    effective_from   date        NOT NULL DEFAULT current_date,
    effective_to     date,
    status           text        NOT NULL DEFAULT 'active',
    created_at       timestamptz NOT NULL DEFAULT now(),
    updated_at       timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT kpi_target_period_chk CHECK (period_type IN ('day','week','month','quarter','year')),
    CONSTRAINT kpi_target_direction_chk CHECK (direction IN ('min','max')),
    CONSTRAINT kpi_target_status_chk CHECK (status IN ('active','inactive')),
    CONSTRAINT kpi_target_uk UNIQUE NULLS NOT DISTINCT (company_id, establishment_id, kpi_key, period_type, effective_from),
    CONSTRAINT kpi_target_establishment_fk FOREIGN KEY (establishment_id, company_id)
        REFERENCES nexora.establishment (id, company_id)
);
CREATE INDEX kpi_target_company_idx ON nexora.kpi_target (company_id, kpi_key);

-- --- Tranches d'antériorité configurables (§18) ----------------------
CREATE TABLE nexora.aging_bucket_set (
    id          uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    company_id  uuid        NOT NULL REFERENCES nexora.company(id),
    name        text        NOT NULL,
    kind        text        NOT NULL,                      -- receivable | payable
    buckets     jsonb       NOT NULL,                      -- [{label, from_days, to_days|null}]
    is_default  boolean     NOT NULL DEFAULT false,
    created_at  timestamptz NOT NULL DEFAULT now(),
    updated_at  timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT aging_bucket_set_kind_chk CHECK (kind IN ('receivable','payable')),
    CONSTRAINT aging_bucket_set_company_name_uk UNIQUE (company_id, name)
);
CREATE UNIQUE INDEX aging_bucket_set_default_uk
    ON nexora.aging_bucket_set (company_id, kind) WHERE is_default;

-- --- Rapports enregistrés / favoris --------------------------------
CREATE TABLE nexora.saved_report (
    id          uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    company_id  uuid        NOT NULL REFERENCES nexora.company(id),
    user_id     uuid        REFERENCES nexora.app_user(id),
    name        text        NOT NULL,
    report_key  text        NOT NULL,
    filter      jsonb       NOT NULL DEFAULT '{}'::jsonb,
    is_shared   boolean     NOT NULL DEFAULT false,
    created_at  timestamptz NOT NULL DEFAULT now(),
    updated_at  timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX saved_report_company_idx ON nexora.saved_report (company_id, report_key);

-- --- Rapports planifiés (§31) ------------------------------------
CREATE TABLE nexora.report_schedule (
    id           uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    company_id   uuid        NOT NULL REFERENCES nexora.company(id),
    name         text        NOT NULL,
    report_key   text        NOT NULL,
    frequency    text        NOT NULL DEFAULT 'daily',
    filter       jsonb       NOT NULL DEFAULT '{}'::jsonb,   -- périmètre enregistré
    format       text        NOT NULL DEFAULT 'csv',
    recipients   jsonb       NOT NULL DEFAULT '[]'::jsonb,
    status       text        NOT NULL DEFAULT 'active',
    last_run_at  timestamptz,
    next_run_at  timestamptz,
    created_by   uuid        REFERENCES nexora.app_user(id),
    created_at   timestamptz NOT NULL DEFAULT now(),
    updated_at   timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT report_schedule_freq_chk CHECK (frequency IN ('daily','weekly','monthly','on_close')),
    CONSTRAINT report_schedule_format_chk CHECK (format IN ('csv','xlsx','pdf')),
    CONSTRAINT report_schedule_status_chk CHECK (status IN ('active','paused')),
    CONSTRAINT report_schedule_company_id_uk UNIQUE (id, company_id)
);
CREATE INDEX report_schedule_due_idx ON nexora.report_schedule (next_run_at) WHERE status = 'active';

-- --- Journal d'exécution des rapports (§31) --------------------
CREATE TABLE nexora.report_execution (
    id                 uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    company_id         uuid        NOT NULL,
    report_schedule_id uuid,
    report_key         text        NOT NULL,
    filter             jsonb       NOT NULL DEFAULT '{}'::jsonb,
    format             text        NOT NULL DEFAULT 'csv',
    status             text        NOT NULL DEFAULT 'running',
    row_count          integer,
    file_reference     text,
    started_at         timestamptz NOT NULL DEFAULT now(),
    finished_at        timestamptz,
    error              text,
    run_by             uuid,
    CONSTRAINT report_execution_status_chk CHECK (status IN ('running','succeeded','failed')),
    CONSTRAINT report_execution_schedule_fk FOREIGN KEY (report_schedule_id, company_id)
        REFERENCES nexora.report_schedule (id, company_id) ON DELETE SET NULL
);
CREATE INDEX report_execution_company_idx ON nexora.report_execution (company_id, started_at DESC);

-- --- Journal des exports sensibles (§30, §36) -----------------
CREATE TABLE nexora.export_log (
    id           uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    company_id   uuid,
    user_id      uuid,
    report_key   text        NOT NULL,
    filter       jsonb       NOT NULL DEFAULT '{}'::jsonb,
    format       text        NOT NULL DEFAULT 'csv',
    row_count    integer,
    sensitivity  text        NOT NULL DEFAULT 'normal',
    ip_address   inet,
    created_at   timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT export_log_sensitivity_chk CHECK (sensitivity IN ('normal','financial','restricted'))
);
CREATE INDEX export_log_company_idx ON nexora.export_log (company_id, created_at DESC);
CREATE INDEX export_log_user_idx    ON nexora.export_log (user_id, created_at DESC);

-- --- Permissions Pilotage (§36) -----------------------------
INSERT INTO nexora.permission (code, name, module, action) VALUES
    ('reporting.view',             'Pilotage — tableaux de bord et rapports opérationnels', 'reporting', 'view'),
    ('reporting.export',           'Pilotage — exporter les rapports opérationnels',        'reporting', 'export'),
    ('reporting.view_financial',   'Pilotage — voir les rapports financiers',   'reporting', 'view_financial'),
    ('reporting.export_financial', 'Pilotage — exporter les rapports financiers','reporting', 'export_financial'),
    ('reporting.view_group',       'Pilotage — vue groupe / consolidée',        'reporting', 'view_group'),
    ('reporting.schedule',         'Pilotage — planifier des rapports',         'reporting', 'schedule'),
    ('reporting.manage_targets',   'Pilotage — gérer les objectifs KPI',        'reporting', 'manage_targets'),
    ('reporting.ask',              'Pilotage — Ask Nexora',                     'reporting', 'ask')
ON CONFLICT (code) DO NOTHING;

CREATE OR REPLACE FUNCTION pg_temp.grant_perms_v26(p_role text, p_patterns text[])
RETURNS void LANGUAGE sql AS $$
    INSERT INTO nexora.role_permission (role_id, permission_id)
    SELECT r.id, p.id FROM nexora.role r
    JOIN nexora.permission p ON p.code LIKE ANY (p_patterns)
    WHERE r.name = p_role AND r.system_role
    ON CONFLICT DO NOTHING;
$$;

SELECT pg_temp.grant_perms_v26('super_administrator', ARRAY['reporting.%']);
SELECT pg_temp.grant_perms_v26('group_administrator', ARRAY['reporting.%']);
SELECT pg_temp.grant_perms_v26('owner_direction',     ARRAY['reporting.%']);
SELECT pg_temp.grant_perms_v26('chief_accountant',    ARRAY['reporting.%']);
SELECT pg_temp.grant_perms_v26('senior_accountant',   ARRAY[
    'reporting.view','reporting.view_financial','reporting.export','reporting.export_financial','reporting.ask']);
SELECT pg_temp.grant_perms_v26('accountant',          ARRAY['reporting.view','reporting.view_financial','reporting.export','reporting.ask']);
SELECT pg_temp.grant_perms_v26('manager',             ARRAY[
    'reporting.view','reporting.view_financial','reporting.export','reporting.schedule','reporting.manage_targets','reporting.ask']);
SELECT pg_temp.grant_perms_v26('auditor',             ARRAY['reporting.view','reporting.view_financial','reporting.view_group','reporting.export']);
SELECT pg_temp.grant_perms_v26('cashier',             ARRAY['reporting.view']);
SELECT pg_temp.grant_perms_v26('stock_manager',       ARRAY['reporting.view','reporting.export']);
SELECT pg_temp.grant_perms_v26('purchasing_manager',  ARRAY['reporting.view','reporting.export']);

-- --- Jeu de tranches d'antériorité par défaut par entreprise (§18) ---
INSERT INTO nexora.aging_bucket_set (company_id, name, kind, buckets, is_default)
SELECT c.id, 'Standard', k.kind,
       '[{"label":"Non échu","from_days":-100000,"to_days":0},
         {"label":"1-30 j","from_days":1,"to_days":30},
         {"label":"31-60 j","from_days":31,"to_days":60},
         {"label":"61-90 j","from_days":61,"to_days":90},
         {"label":"> 90 j","from_days":91,"to_days":null}]'::jsonb,
       true
FROM nexora.company c CROSS JOIN (VALUES ('receivable'), ('payable')) AS k(kind)
ON CONFLICT DO NOTHING;

SELECT nexora.attach_common_triggers();
