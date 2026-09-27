-- =============================================================================
-- NEXORA — V044 — Reporting & analytics : couche de confiance (Phase 31)
--
-- Le socle reporting existe déjà (Phase 7) : `SqlKpiService` (Command Center, dashboards
-- caissier / manager / groupe), `SqlSalesReports` (journal, ventilation, top produits),
-- `SqlStockReports` (valorisation, ruptures, dormants), `SqlAgingReports` (aging configurable),
-- `SqlBudgetReports` (budget vs réel), `SqlDrillDown` (§35), `SqlAlertCenter` (§38-39),
-- `CsvExportService` + `export_log` (§42), `RuleBasedAskNexora` (langage naturel).
--
-- Cette migration ajoute la **couche de confiance** décrite aux §2, §44-46 :
--   analytics_snapshot : photo reproductible d'une réconciliation / d'un contrôle qualité (§44)
--   permission reporting.data_quality
-- La réconciliation KPI (§46) et le contrôle qualité des données (§45) sont des lectures
-- dérivées — aucune table de résultat permanente hors snapshot.
-- =============================================================================
SET search_path TO nexora, public;

-- ===================== 1. Snapshot analytique (§44) ==================
CREATE TABLE nexora.analytics_snapshot (
    id                  uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    company_id          uuid        NOT NULL REFERENCES nexora.company(id),
    kind                text        NOT NULL,
    label               text,
    as_of_date          date        NOT NULL,
    period_from         date,
    period_to          date,
    scope               jsonb       NOT NULL DEFAULT '{}'::jsonb,
    calculation_version text        NOT NULL DEFAULT 'v1',
    score               numeric(6,2),
    result              jsonb       NOT NULL DEFAULT '{}'::jsonb,
    created_by          uuid REFERENCES nexora.app_user(id),
    created_at          timestamptz NOT NULL DEFAULT now(),
    updated_at         timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT analytics_snapshot_kind_chk CHECK (kind IN ('reconciliation','data_quality','kpi')),
    CONSTRAINT analytics_snapshot_company_id_uk UNIQUE (id, company_id)
);
CREATE INDEX analytics_snapshot_company_idx
    ON nexora.analytics_snapshot (company_id, kind, as_of_date DESC);

-- ===================== 2. RLS (§49) & audit (§52) ====================
SELECT nexora.enable_company_rls('nexora.analytics_snapshot'::regclass);
DROP TRIGGER IF EXISTS trg_audit_analytics_snapshot ON nexora.analytics_snapshot;
CREATE TRIGGER trg_audit_analytics_snapshot
    AFTER INSERT OR UPDATE OR DELETE ON nexora.analytics_snapshot
    FOR EACH ROW EXECUTE FUNCTION nexora.audit_row();

-- ===================== 3. Permission (§51) ===========================
INSERT INTO nexora.permission (code, name, module, action) VALUES
    ('reporting.data_quality', 'Reporting — réconciliation KPI & qualité des données', 'reporting', 'data_quality')
ON CONFLICT (code) DO NOTHING;

CREATE OR REPLACE FUNCTION pg_temp.grant_perms_v44(p_role text, p_patterns text[])
RETURNS void LANGUAGE sql AS $$
    INSERT INTO nexora.role_permission (role_id, permission_id)
    SELECT r.id, p.id FROM nexora.role r
    JOIN nexora.permission p ON p.code LIKE ANY (p_patterns)
    WHERE r.name = p_role AND r.system_role
    ON CONFLICT DO NOTHING;
$$;

SELECT pg_temp.grant_perms_v44('super_administrator', ARRAY['reporting.data_quality']);
SELECT pg_temp.grant_perms_v44('group_administrator', ARRAY['reporting.data_quality']);
SELECT pg_temp.grant_perms_v44('owner_direction',     ARRAY['reporting.data_quality']);
SELECT pg_temp.grant_perms_v44('manager',             ARRAY['reporting.data_quality']);
SELECT pg_temp.grant_perms_v44('chief_accountant',    ARRAY['reporting.data_quality']);
SELECT pg_temp.grant_perms_v44('senior_accountant',   ARRAY['reporting.data_quality']);

SELECT nexora.attach_common_triggers();
