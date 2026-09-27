-- =============================================================================
-- NEXORA ONE — V056 — Intelligence métier & recommandations (Phase 43)
--
-- Le socle décisionnel existe déjà et reste VALIDÉ par cette phase :
--   Phase 7  : Command Center, alertes, Ask Nexora (`RuleBasedAskNexora`, déterministe,
--              sans LLM) — `IKpiService`/`IAlertCenter`/`IAskNexora`
--   Phase 11 : moteur de fraude à signaux + revue humaine, audit immuable
--   Phase 26 : correspondance à trois voies achats (variance, retards de réception)
--   Phase 27 : portefeuille créances (`ICreditPortfolioQueries` — ancienneté, montants,
--              promesses, litiges par client)
--   Phase 28 : trésorerie — position, liquidité, prévision par échéance
--   Phase 42 : `insight` (anomalies expliquées), `forecast` (projections jamais présentées
--              comme des faits), `budget` (cycle complet), `SchedulerRunner`/`IJobRegistry`
--
-- Cette migration ajoute la couche manquante entre « on a détecté » (Phase 42 `insight`)
-- et « voici quoi faire » (§2 : DONNÉES → ANALYSE → DÉTECTION → EXPLICATION →
-- RECOMMANDATION → VALIDATION → ACTION → MESURE) :
--   recommendation     : proposition actionnable, expliquée, datée, avec boucle d'apprentissage (§5, §33)
--   intelligence_rule  : seuils configurables gouvernant la génération (§34, §39)
--   entity_score       : scoring explicable produit/client/fournisseur (§35)
--   assistant_query     : observabilité des questions posées à l'assistant conversationnel (§38)
--   task               : tâche générique liée à une recommandation/insight — comblait un vrai
--                        manque (aucune table de tâche non spécifique au groupe n'existait, §31)
-- =============================================================================
SET search_path TO nexora, public;

-- ===================== 1. Recommandations (§2, §4, §5, §31, §33, §46) =====

CREATE TABLE nexora.recommendation (
    id                uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    company_id        uuid        NOT NULL REFERENCES nexora.company(id) ON DELETE CASCADE,
    establishment_id  uuid,
    category          text        NOT NULL,   -- stock_reorder | stock_dormant | receivable_priority | cashflow_risk | budget_overrun
    rule_key          text        NOT NULL,
    title             text        NOT NULL,
    why               text        NOT NULL,                       -- §5 « pourquoi ? »
    evidence          jsonb       NOT NULL DEFAULT '{}'::jsonb,    -- §5 « données »
    impact_estimate   jsonb       NOT NULL DEFAULT '{}'::jsonb,    -- §5 « impact estimé » — jamais un fait
    proposed_action   text        NOT NULL,                       -- §5 « action proposée »
    expected_result   text        NOT NULL DEFAULT '',            -- §5 « résultat attendu »
    impact_score      smallint    NOT NULL DEFAULT 3,
    urgency_score     smallint    NOT NULL DEFAULT 3,
    confidence_score  smallint    NOT NULL DEFAULT 2,              -- 0 données insuffisantes,1 faible,2 moyenne,3 élevée
    confidence_level  text        NOT NULL DEFAULT 'medium',
    confidence_reason text        NOT NULL DEFAULT '',             -- §4 « explication du niveau »
    priority_score    smallint    GENERATED ALWAYS AS
                          (impact_score * urgency_score * GREATEST(confidence_score, 1)) STORED,  -- §46
    drill_path        text,
    period_start      date        NOT NULL,
    status            text        NOT NULL DEFAULT 'new',
    snooze_until      timestamptz,
    decision_reason   text,
    decided_by        uuid REFERENCES nexora.app_user(id),
    decided_at        timestamptz,
    outcome           text,                                       -- positive | negative — boucle d'apprentissage §33
    outcome_note      text,
    outcome_at        timestamptz,
    created_at        timestamptz NOT NULL DEFAULT now(),
    updated_at        timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT recommendation_category_chk CHECK (category IN
        ('stock_reorder','stock_dormant','receivable_priority','cashflow_risk','budget_overrun')),
    CONSTRAINT recommendation_confidence_chk CHECK (confidence_level IN ('high','medium','low','insufficient_data')),
    CONSTRAINT recommendation_status_chk CHECK (status IN ('new','accepted','dismissed','snoozed','resolved')),
    CONSTRAINT recommendation_outcome_chk CHECK (outcome IS NULL OR outcome IN ('positive','negative')),
    CONSTRAINT recommendation_scores_chk CHECK (
        impact_score BETWEEN 1 AND 5 AND urgency_score BETWEEN 1 AND 5 AND confidence_score BETWEEN 0 AND 3),
    CONSTRAINT recommendation_dedup_uk UNIQUE NULLS NOT DISTINCT (company_id, category, drill_path, period_start)
);
CREATE INDEX recommendation_company_status_idx
    ON nexora.recommendation (company_id, status, priority_score DESC, created_at DESC);

-- ===================== 2. Règles configurables (§34, §39) ==================
-- company_id NULL = valeur système par défaut (même principe que sector_profile, V045) ;
-- la couche service n'expose jamais de chemin permettant à un appelant tenant de créer une
-- règle globale — seule cette migration amorce les company_id NULL.

CREATE TABLE nexora.intelligence_rule (
    id                  uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    company_id          uuid REFERENCES nexora.company(id),
    key                 text        NOT NULL,
    category            text        NOT NULL,
    label               text        NOT NULL,
    threshold           jsonb       NOT NULL DEFAULT '{}'::jsonb,
    period_days         integer,
    default_priority    text        NOT NULL DEFAULT 'medium',
    recipient_role      text,
    action_label        text        NOT NULL DEFAULT '',
    escalate_after_days integer,
    enabled             boolean     NOT NULL DEFAULT true,
    valid_from          date        NOT NULL DEFAULT CURRENT_DATE,
    expires_at          date,
    owner_user_id       uuid REFERENCES nexora.app_user(id),
    version             integer     NOT NULL DEFAULT 1,
    approved_by         uuid REFERENCES nexora.app_user(id),
    approved_at         timestamptz,
    created_at          timestamptz NOT NULL DEFAULT now(),
    updated_at          timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT intelligence_rule_priority_chk CHECK (default_priority IN ('low','medium','high','critical')),
    CONSTRAINT intelligence_rule_key_uk UNIQUE NULLS NOT DISTINCT (company_id, key)
);
CREATE INDEX intelligence_rule_lookup_idx ON nexora.intelligence_rule (key, enabled);

-- ===================== 3. Scoring explicable (§35) ==========================

CREATE TABLE nexora.entity_score (
    id            uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    company_id    uuid        NOT NULL REFERENCES nexora.company(id) ON DELETE CASCADE,
    entity_type   text        NOT NULL,   -- product | customer | supplier
    entity_id     uuid        NOT NULL,
    score_key     text        NOT NULL,   -- stock_coverage | credit_risk | supplier_reliability
    value         numeric(6,2) NOT NULL,
    level         text        NOT NULL DEFAULT 'medium',
    explanation   jsonb       NOT NULL DEFAULT '{}'::jsonb,   -- §35 « explication des variables »
    computed_at   timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT entity_score_type_chk  CHECK (entity_type IN ('product','customer','supplier')),
    CONSTRAINT entity_score_level_chk CHECK (level IN ('low','medium','high')),
    CONSTRAINT entity_score_uk UNIQUE (company_id, entity_type, entity_id, score_key)
);
CREATE INDEX entity_score_lookup_idx ON nexora.entity_score (company_id, entity_type, score_key);

-- ===================== 4. Observabilité assistant (§38, §29) ===============
-- Journal d'ajout seul (comme report_execution, V026) — jamais mis à jour après coup.

CREATE TABLE nexora.assistant_query (
    id                uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    company_id        uuid        NOT NULL REFERENCES nexora.company(id) ON DELETE CASCADE,
    user_id           uuid        NOT NULL REFERENCES nexora.app_user(id),
    role              text,
    question          text        NOT NULL,
    intent            text        NOT NULL,
    answer            text        NOT NULL,
    sources           jsonb       NOT NULL DEFAULT '[]'::jsonb,
    contains_forecast boolean     NOT NULL DEFAULT false,
    created_at        timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX assistant_query_company_idx ON nexora.assistant_query (company_id, created_at DESC);

-- ===================== 5. Tâche générique (§31) =============================
-- Comble un manque réel : `group_task` (V028) exige un groupe, `onboarding_task` (V052) est
-- une checklist fixe — aucune table ne permettait une tâche libre liée à une recommandation.

CREATE TABLE nexora.task (
    id             uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    company_id     uuid        NOT NULL REFERENCES nexora.company(id) ON DELETE CASCADE,
    title          text        NOT NULL,
    description    text        NOT NULL DEFAULT '',
    reference_type text,                     -- recommendation | insight | forecast
    reference_id   uuid,
    assigned_to    uuid REFERENCES nexora.app_user(id),
    priority       text        NOT NULL DEFAULT 'normal',
    status         text        NOT NULL DEFAULT 'open',
    due_at         timestamptz,
    created_by     uuid REFERENCES nexora.app_user(id),
    created_at     timestamptz NOT NULL DEFAULT now(),
    updated_at     timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT task_priority_chk CHECK (priority IN ('low','normal','high','urgent')),
    CONSTRAINT task_status_chk   CHECK (status IN ('open','in_progress','done','cancelled'))
);
CREATE INDEX task_company_status_idx ON nexora.task (company_id, status, due_at);

-- ===================== 6. RLS + audit (§40) =================================

SELECT nexora.enable_company_rls('nexora.recommendation'::regclass);
SELECT nexora.enable_company_rls('nexora.intelligence_rule'::regclass);
SELECT nexora.enable_company_rls('nexora.entity_score'::regclass);
SELECT nexora.enable_company_rls('nexora.assistant_query'::regclass);
SELECT nexora.enable_company_rls('nexora.task'::regclass);

DO $$
DECLARE t text;
BEGIN
    -- assistant_query n'est pas auditée : journal d'ajout seul, comme report_execution (V026).
    FOREACH t IN ARRAY ARRAY['recommendation','intelligence_rule','entity_score','task'] LOOP
        EXECUTE format('DROP TRIGGER IF EXISTS trg_audit_%1$s ON nexora.%1$I', t);
        EXECUTE format(
            'CREATE TRIGGER trg_audit_%1$s AFTER INSERT OR UPDATE OR DELETE ON nexora.%1$I
               FOR EACH ROW EXECUTE FUNCTION nexora.audit_row()', t);
    END LOOP;
END;
$$;

-- ===================== 7. Amorçage des règles système (§34) ================

INSERT INTO nexora.intelligence_rule (company_id, key, category, label, threshold, period_days, default_priority, action_label) VALUES
    (NULL, 'stock_reorder',       'stock',      'Réapprovisionnement intelligent',
        '{"safetyDays":7,"minHistoryDays":14}'::jsonb,       60, 'high',     'Réapprovisionner'),
    (NULL, 'stock_dormant',       'stock',      'Produit dormant / surstock',
        '{"dormantDays":45,"marginRateMaxPct":15}'::jsonb,   45, 'medium',   'Réduire le réapprovisionnement et réévaluer la promotion'),
    (NULL, 'receivable_priority', 'receivables','Priorisation du recouvrement',
        '{"minOverdueAmount":100,"topN":10}'::jsonb,         NULL, 'high',   'Relancer le client'),
    (NULL, 'cashflow_risk',       'treasury',   'Risque de trésorerie',
        '{"minBalance":0,"horizonDays":30}'::jsonb,          30, 'critical', 'Sécuriser la trésorerie'),
    (NULL, 'budget_overrun',      'budget',     'Dépassement budgétaire probable',
        '{"overrunPct":10}'::jsonb,                          NULL, 'medium', 'Réviser le budget ou les dépenses')
ON CONFLICT ON CONSTRAINT intelligence_rule_key_uk DO NOTHING;

-- ===================== 8. Amorçage du job planifié (§45-46) ================

INSERT INTO nexora.job_definition (key, name, category, description, owner) VALUES
    ('intelligence.generate_recommendations', 'Génération des recommandations', 'business',
        'Applique les règles d''intelligence activées et matérialise des recommandations expliquées et scorées (§2, §34, §46).', 'scheduler')
ON CONFLICT (key) DO NOTHING;

-- ===================== 9. Permissions (§48 périmètre) =======================

INSERT INTO nexora.permission (code, name, module, action) VALUES
    ('intelligence.view',                'Intelligence — consulter recommandations/scores/insights', 'intelligence', 'view'),
    ('intelligence.manage_rules',        'Intelligence — configurer les règles',                      'intelligence', 'manage_rules'),
    ('intelligence.act_on_recommendation','Intelligence — accepter/ignorer/reporter une recommandation','intelligence', 'act_on_recommendation'),
    ('intelligence.use_assistant',       'Intelligence — utiliser l''assistant conversationnel',       'intelligence', 'use_assistant')
ON CONFLICT (code) DO NOTHING;

CREATE OR REPLACE FUNCTION pg_temp.grant_perms_v56(p_role text, p_patterns text[])
RETURNS void LANGUAGE sql AS $$
    INSERT INTO nexora.role_permission (role_id, permission_id)
    SELECT r.id, p.id FROM nexora.role r
    JOIN nexora.permission p ON p.code LIKE ANY (p_patterns)
    WHERE r.name = p_role AND r.system_role
    ON CONFLICT DO NOTHING;
$$;
SELECT pg_temp.grant_perms_v56('super_administrator', ARRAY['intelligence.%']);
SELECT pg_temp.grant_perms_v56('group_administrator', ARRAY['intelligence.%']);
SELECT pg_temp.grant_perms_v56('owner_direction',     ARRAY['intelligence.%']);
SELECT pg_temp.grant_perms_v56('manager',             ARRAY[
    'intelligence.view','intelligence.act_on_recommendation','intelligence.use_assistant']);
SELECT pg_temp.grant_perms_v56('chief_accountant',    ARRAY[
    'intelligence.view','intelligence.act_on_recommendation','intelligence.use_assistant']);
SELECT pg_temp.grant_perms_v56('accountant',          ARRAY['intelligence.view','intelligence.use_assistant']);
SELECT pg_temp.grant_perms_v56('senior_accountant',   ARRAY['intelligence.view','intelligence.use_assistant']);
SELECT pg_temp.grant_perms_v56('stock_manager',       ARRAY['intelligence.view']);
SELECT pg_temp.grant_perms_v56('purchasing_manager',  ARRAY['intelligence.view']);
SELECT pg_temp.grant_perms_v56('cashier',             ARRAY['intelligence.use_assistant']);

SELECT nexora.attach_common_triggers();
