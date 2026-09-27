-- =============================================================================
-- NEXORA ONE — V055 — Business Intelligence, Reporting & Pilotage (Phase 42)
--
-- Le socle décisionnel existe déjà et reste VALIDÉ par cette phase :
--   Phase 7  : Command Center + dashboards par rôle (caissier/manager/groupe), rapports
--              ventes/stock/aging/budget-vs-réel, centre d'alertes, drill-down, export CSV,
--              Ask Nexora — `IKpiService`/`ISalesReports`/`IStockReports`/`IAgingReports`/
--              `IBudgetReports`/`IAlertCenter`/`IDrillDown`/`IExportService`/`IAskNexora`
--   Phase 9  : consolidation groupe (agrégation → conversion → élimination → cohérence),
--              mapping de plan comptable versionné, intercompany, multi-devises (taux de
--              clôture point-in-time) — `IConsolidationService`/`IIntercompanyService`
--   Phase 28 : trésorerie — position, liquidité, prévision de trésorerie par échéance
--   Phase 29 : comptabilité générale — balance, grand livre, états financiers
--   Phase 31 : couche de confiance — réconciliation KPI, qualité des données, snapshots
--   Phase 16/41 : `SchedulerRunner` / `IJobRegistry` — traitements de fond nommés
--
-- Cette migration ACTIVE quatre tables déjà créées en V026 mais jamais consommées par
-- aucun service (`kpi_target`, `saved_report`, `report_schedule`, `report_execution`),
-- active le CRUD budgets (`budget`/`budget_line` de V011 n'avaient qu'une lecture
-- vs-réel), et ajoute trois tables neuves pour les concepts qui n'existaient nulle part :
--   kpi_definition   : catalogue KPI gouverné, persisté et versionné (§48, §50)
--   insight          : anomalies détectées + explication + action suggérée (§37-38, §58)
--   forecast         : projections simples présentées comme des ESTIMATIONS, jamais des
--                      certitudes (§35-36 — méthode, hypothèses, niveau de confiance)
-- =============================================================================
SET search_path TO nexora, public;

-- ===================== 1. Catalogue KPI gouverné (§48, §50) ===============
-- Table plateforme (comme job_definition) : une définition unique pour tout NEXORA ONE,
-- pas une redéfinition par entreprise. Lecture seule côté API dans cette phase — l'historique
-- des évolutions futures passera par `audit_log` (trg_audit_kpi_definition).

CREATE TABLE nexora.kpi_definition (
    id          uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    key         text        NOT NULL UNIQUE,
    label       text        NOT NULL,
    category    text        NOT NULL,                     -- sales | margin | stock | finance | accounting
    unit        text        NOT NULL,
    source      text        NOT NULL,
    formula     text        NOT NULL,
    kind        text        NOT NULL DEFAULT 'actual',     -- actual | forecast | estimated
    owner       text        NOT NULL DEFAULT 'NEXORA ONE',
    version     integer     NOT NULL DEFAULT 1,
    created_at  timestamptz NOT NULL DEFAULT now(),
    updated_at  timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT kpi_definition_kind_chk CHECK (kind IN ('actual','forecast','estimated'))
);

-- ===================== 1b. Correction FK report_execution (§41, activation V026) ==
-- La FK composite (report_schedule_id, company_id) ON DELETE SET NULL mettrait
-- AUSSI company_id à NULL à la suppression d'un rapport planifié — impossible,
-- report_execution.company_id est NOT NULL (jamais constaté avant cette phase, aucun
-- service ne consommait encore ces tables). Corrigée en FK simple sur report_schedule_id
-- seul : company_id reste toujours renseigné par l'écrivain (§17 pas de suppression
-- silencieuse — un historique d'exécution reste malgré la suppression du planning).

ALTER TABLE nexora.report_execution DROP CONSTRAINT IF EXISTS report_execution_schedule_fk;
ALTER TABLE nexora.report_execution
    ADD CONSTRAINT report_execution_schedule_fk FOREIGN KEY (report_schedule_id)
        REFERENCES nexora.report_schedule(id) ON DELETE SET NULL;

-- ===================== 2. Objectifs pondérés (§39-40) ======================
-- kpi_target existe depuis V026 sans aucun service ; on ajoute la pondération du score.

ALTER TABLE nexora.kpi_target
    ADD COLUMN IF NOT EXISTS weight numeric(5,2) NOT NULL DEFAULT 1;
ALTER TABLE nexora.kpi_target DROP CONSTRAINT IF EXISTS kpi_target_weight_chk;
ALTER TABLE nexora.kpi_target ADD CONSTRAINT kpi_target_weight_chk CHECK (weight > 0);

-- ===================== 3. Budgets — activation du CRUD (§33-34) ============
-- budget/budget_line existent depuis V011 (lecture vs-réel seule, Phase 7 §24) : on ajoute
-- le cycle brouillon → soumis → approuvé → clos, et la révision (nouvelle version tracée).

ALTER TABLE nexora.budget DROP CONSTRAINT IF EXISTS budget_status_chk;
ALTER TABLE nexora.budget ADD CONSTRAINT budget_status_chk
    CHECK (status IN ('draft','pending_approval','active','closed'));
ALTER TABLE nexora.budget
    ADD COLUMN IF NOT EXISTS revised_from uuid REFERENCES nexora.budget(id),
    ADD COLUMN IF NOT EXISTS submitted_by uuid REFERENCES nexora.app_user(id),
    ADD COLUMN IF NOT EXISTS approved_by  uuid REFERENCES nexora.app_user(id),
    ADD COLUMN IF NOT EXISTS approved_at  timestamptz;

-- ===================== 4. Insights — anomalies expliquées (§37-38, §58) ====

CREATE TABLE nexora.insight (
    id               uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    company_id       uuid        NOT NULL REFERENCES nexora.company(id) ON DELETE CASCADE,
    category         text        NOT NULL,                 -- revenue_drop | margin_drop | stock_anomaly | expense_spike | cash_gap
    severity         text        NOT NULL DEFAULT 'warning',
    title            text        NOT NULL,
    detail           text        NOT NULL,                 -- explication en langage clair
    evidence         jsonb       NOT NULL DEFAULT '{}'::jsonb,  -- les données qui justifient l'insight (§38)
    drill_path       text,                                 -- chemin réutilisable par IDrillDown (Phase 7 §29)
    suggested_action text,
    period_start     date        NOT NULL,
    status           text        NOT NULL DEFAULT 'open',
    assigned_to      uuid REFERENCES nexora.app_user(id),   -- « créer une tâche » (§38)
    due_at           timestamptz,
    dismissed_by     uuid REFERENCES nexora.app_user(id),
    dismissed_at     timestamptz,
    dismissed_reason text,
    created_at       timestamptz NOT NULL DEFAULT now(),
    updated_at       timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT insight_category_chk CHECK (category IN ('revenue_drop','margin_drop','stock_anomaly','expense_spike','cash_gap')),
    CONSTRAINT insight_severity_chk CHECK (severity IN ('info','warning','critical')),
    CONSTRAINT insight_status_chk   CHECK (status IN ('open','dismissed','actioned')),
    CONSTRAINT insight_dedup_uk UNIQUE NULLS NOT DISTINCT (company_id, category, drill_path, period_start)
);
CREATE INDEX insight_company_status_idx ON nexora.insight (company_id, status, created_at DESC);

-- ===================== 5. Prévisions — toujours des estimations (§35-36) ===

CREATE TABLE nexora.forecast (
    id               uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    company_id       uuid        NOT NULL REFERENCES nexora.company(id) ON DELETE CASCADE,
    metric           text        NOT NULL,                 -- revenue | cash | stock | receivables | expenses
    method           text        NOT NULL DEFAULT 'moving_average',
    scenario         text        NOT NULL DEFAULT 'base',  -- base | optimistic | prudent | custom
    adjustment_pct   numeric(6,2) NOT NULL DEFAULT 0,
    horizon_days     integer     NOT NULL,
    confidence_level text        NOT NULL DEFAULT 'medium',
    assumptions      text        NOT NULL DEFAULT '',
    points           jsonb       NOT NULL DEFAULT '[]'::jsonb,   -- [{date, value}]
    currency_id      uuid REFERENCES nexora.currency(id),
    generated_at     timestamptz NOT NULL DEFAULT now(),
    created_by       uuid REFERENCES nexora.app_user(id),
    created_at       timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT forecast_metric_chk     CHECK (metric IN ('revenue','cash','stock','receivables','expenses')),
    CONSTRAINT forecast_method_chk     CHECK (method IN ('moving_average','linear_trend','manual')),
    CONSTRAINT forecast_scenario_chk   CHECK (scenario IN ('base','optimistic','prudent','custom')),
    CONSTRAINT forecast_confidence_chk CHECK (confidence_level IN ('low','medium','high')),
    CONSTRAINT forecast_horizon_chk    CHECK (horizon_days BETWEEN 1 AND 365)
);
CREATE INDEX forecast_company_idx ON nexora.forecast (company_id, metric, generated_at DESC);

-- ===================== 6. RLS (§53) + audit ================================

SELECT nexora.enable_company_rls('nexora.insight'::regclass);
SELECT nexora.enable_company_rls('nexora.forecast'::regclass);

DO $$
DECLARE t text;
BEGIN
    FOREACH t IN ARRAY ARRAY['kpi_definition','insight','forecast','budget'] LOOP
        EXECUTE format('DROP TRIGGER IF EXISTS trg_audit_%1$s ON nexora.%1$I', t);
        EXECUTE format(
            'CREATE TRIGGER trg_audit_%1$s AFTER INSERT OR UPDATE OR DELETE ON nexora.%1$I
               FOR EACH ROW EXECUTE FUNCTION nexora.audit_row()', t);
    END LOOP;
END;
$$;

-- ===================== 7. Amorçage du catalogue KPI (§50) ==================
-- Reprend le catalogue documenté déjà en code (Nexora.Application.Reporting.KpiCatalog, Phase 7 §34).

INSERT INTO nexora.kpi_definition (key, label, category, unit, source, formula, kind) VALUES
    ('revenue',          'Chiffre d''affaires',  'sales',   'HTG', 'sale (completed)',        'Σ (subtotal − discount_total) − retours nets', 'actual'),
    ('gross_margin',     'Marge brute',          'margin',  'HTG', 'sale_line',               'CA − Σ (unit_cost × quantité)', 'actual'),
    ('margin_rate',      'Taux de marge',        'margin',  '%',   'dérivé',                  'marge brute / CA × 100', 'actual'),
    ('profit',           'Bénéfice',             'finance', 'HTG', 'dérivé',                  'marge brute − dépenses de la période', 'actual'),
    ('expenses',         'Dépenses',             'finance', 'HTG', 'expense',                 'Σ amount (période, statut approuvé/payé)', 'actual'),
    ('cash',             'Trésorerie',           'finance', 'HTG', 'financial_account',       'Σ current_balance (comptes actifs caisse/banque/mobile)', 'actual'),
    ('open_receivables', 'Créances ouvertes',    'finance', 'HTG', 'customer_receivable',     'Σ remaining_amount (OPEN/PARTIALLY_PAID/OVERDUE)', 'actual'),
    ('open_payables',    'Dettes ouvertes',      'finance', 'HTG', 'supplier_payable',        'Σ remaining_amount (OPEN/PARTIALLY_PAID/OVERDUE)', 'actual'),
    ('stock_value',      'Stock valorisé',       'stock',   'HTG', 'stock_balance',           'Σ quantity_on_hand × average_cost', 'actual'),
    ('transactions',     'Nombre de ventes',     'sales',   'n',   'sale (completed)',        'count(*)', 'actual'),
    ('average_basket',   'Panier moyen',         'sales',   'HTG', 'dérivé',                  'CA / nombre de ventes', 'actual'),
    ('today_revenue',    'Ventes du jour',       'sales',   'HTG', 'sale',                    'CA où sold_at::date = date du jour', 'actual'),
    ('cash_inflows',     'Encaissements',        'finance', 'HTG', 'payment (entrant)',       'Σ amount (période)', 'actual'),
    ('cash_outflows',    'Décaissements',        'finance', 'HTG', 'payment (sortant)',       'Σ amount (période)', 'actual')
ON CONFLICT (key) DO NOTHING;

-- ===================== 8. Amorçage des jobs planifiés (§41, §46) ==========

INSERT INTO nexora.job_definition (key, name, category, description, owner) VALUES
    ('reports.deliver_scheduled', 'Livraison des rapports planifiés', 'business',
        'Exécute les rapports planifiés dus (quotidien/hebdomadaire/mensuel) et notifie les destinataires.', 'scheduler'),
    ('insights.scan', 'Détection d''anomalies & insights', 'business',
        'Analyse les indicateurs récents et matérialise les anomalies en insights expliqués (§37-38).', 'scheduler')
ON CONFLICT (key) DO NOTHING;

-- ===================== 9. Permissions (§55) =================================
INSERT INTO nexora.permission (code, name, module, action) VALUES
    ('reporting.manage_budgets',  'Reporting — créer/soumettre/approuver un budget',        'reporting', 'manage_budgets'),
    ('reporting.forecast',        'Reporting — générer une prévision',                       'reporting', 'forecast'),
    ('reporting.manage_insights', 'Reporting — assigner/rejeter un insight',                 'reporting', 'manage_insights')
ON CONFLICT (code) DO NOTHING;

CREATE OR REPLACE FUNCTION pg_temp.grant_perms_v55(p_role text, p_patterns text[])
RETURNS void LANGUAGE sql AS $$
    INSERT INTO nexora.role_permission (role_id, permission_id)
    SELECT r.id, p.id FROM nexora.role r
    JOIN nexora.permission p ON p.code LIKE ANY (p_patterns)
    WHERE r.name = p_role AND r.system_role
    ON CONFLICT DO NOTHING;
$$;
SELECT pg_temp.grant_perms_v55('super_administrator', ARRAY['reporting.manage_budgets','reporting.forecast','reporting.manage_insights']);
SELECT pg_temp.grant_perms_v55('group_administrator', ARRAY['reporting.manage_budgets','reporting.forecast','reporting.manage_insights']);
SELECT pg_temp.grant_perms_v55('owner_direction',     ARRAY['reporting.manage_budgets','reporting.forecast','reporting.manage_insights']);
SELECT pg_temp.grant_perms_v55('chief_accountant',    ARRAY['reporting.manage_budgets','reporting.forecast','reporting.manage_insights']);
SELECT pg_temp.grant_perms_v55('manager',             ARRAY['reporting.forecast','reporting.manage_insights']);

SELECT nexora.attach_common_triggers();
