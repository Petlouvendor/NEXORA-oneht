-- =============================================================================
-- NEXORA ONE — V054 — Administration opérationnelle & maintenance (Phase 41)
--
-- Le socle d'exploitation existe déjà et reste VALIDÉ par cette phase :
--   Phase 16 : `SchedulerRunner` — traitements asynchrones idempotents, désactivé par défaut
--   Phase 34 : `notification_outbox` — file de remise externe, retry/backoff/dead-letter
--   Phase 36 : `backup`/`restore_run`/`continuity_incident` — registre de continuité
--   Phase 37 : `IOperationsService` — état système consolidé, checklist Go-Live, déploiements ;
--              `/api/v1/{system/status,go-live,deployments,metrics,health,ready}`
--   Phase 11 : `audit_log` immuable, `IAuditQueryService` (`GET /security/audit`)
--
-- Cette migration ajoute le **centre d'exploitation & de maintenance** (§3-51) :
--   job_definition   : registre des jobs planifiés (nom, catégorie, activation) — §7-9
--   job_run          : historique d'exécution par job (statut, durée, erreurs) — §7
--   feature_flag     : bascules de fonctionnalité — global / entreprise / utilisateur, expiration — §27
--   maintenance_window : fenêtres de maintenance planifiée avec état (§23-24)
--   retention_policy : durée de rétention par type de donnée technique, défaut système ou par entreprise — §17, §22
--   purge_run        : journal des purges (aperçu à blanc puis exécution), jamais sur des données métier — §16-19
--   data_correction_request : workflow de correction contrôlée (motif, avant/après, approbateur ≠ demandeur) — §44
--
-- Hors périmètre (infrastructure, cf. Phases 36-37 pour la même réserve) : services OS
-- Windows/Linux (§35), instances cloud & auto-scaling (§36), pilotes d'imprimante physiques (§40),
-- réindexation Postgres (§42 — `pg_trgm`/`unaccent` ne le nécessitent pas), impersonation utilisateur
-- réelle (§30 — nécessite une revue de sécurité dédiée ; les briques `support_access_grant` (Phase 38)
-- + `audit_log` (Phase 11) + step-up (Phase 11) existent déjà et sont notées comme fondations).
-- =============================================================================
SET search_path TO nexora, public;

-- ===================== 1. Registre des jobs planifiés (§7-9) ===============
-- Table plateforme (pas de company_id — un job du planificateur couvre toutes les entreprises).

CREATE TABLE nexora.job_definition (
    id          uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    key         text        NOT NULL UNIQUE,
    name        text        NOT NULL,
    category    text        NOT NULL,                     -- business | technical (§8-9)
    description text        NOT NULL DEFAULT '',
    enabled     boolean     NOT NULL DEFAULT true,
    owner       text        NOT NULL DEFAULT 'scheduler',
    created_at  timestamptz NOT NULL DEFAULT now(),
    updated_at  timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT job_definition_category_chk CHECK (category IN ('business','technical'))
);

CREATE TABLE nexora.job_run (
    id                 uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    job_key            text        NOT NULL REFERENCES nexora.job_definition(key) ON DELETE CASCADE,
    started_at         timestamptz NOT NULL DEFAULT now(),
    finished_at        timestamptz,
    status             text        NOT NULL DEFAULT 'running',
    affected_count     integer,
    error              text,
    triggered_by       text        NOT NULL DEFAULT 'scheduler',   -- scheduler | manual (§7, §51)
    triggered_by_user  uuid REFERENCES nexora.app_user(id),
    created_at         timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT job_run_status_chk       CHECK (status IN ('running','succeeded','failed')),
    CONSTRAINT job_run_triggered_by_chk CHECK (triggered_by IN ('scheduler','manual'))
);
CREATE INDEX job_run_key_idx ON nexora.job_run (job_key, started_at DESC);

-- ===================== 2. Bascules de fonctionnalité (§27) =================
-- Table plateforme : une bascule peut viser tout le monde, une entreprise ou un utilisateur de test.

CREATE TABLE nexora.feature_flag (
    id          uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    key         text        NOT NULL,
    scope       text        NOT NULL,                      -- global | company | user
    company_id  uuid REFERENCES nexora.company(id) ON DELETE CASCADE,
    user_id     uuid REFERENCES nexora.app_user(id) ON DELETE CASCADE,
    enabled     boolean     NOT NULL DEFAULT true,
    description text,
    expires_at  timestamptz,
    created_by  uuid REFERENCES nexora.app_user(id),
    created_at  timestamptz NOT NULL DEFAULT now(),
    updated_at  timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT feature_flag_scope_chk CHECK (scope IN ('global','company','user')),
    CONSTRAINT feature_flag_target_chk CHECK (
        (scope = 'global'  AND company_id IS NULL     AND user_id IS NULL)
     OR (scope = 'company' AND company_id IS NOT NULL AND user_id IS NULL)
     OR (scope = 'user'    AND user_id IS NOT NULL)
    ),
    CONSTRAINT feature_flag_uk UNIQUE NULLS NOT DISTINCT (key, scope, company_id, user_id)
);
CREATE INDEX feature_flag_key_idx ON nexora.feature_flag (key);

-- ===================== 3. Fenêtres de maintenance planifiée (§23-24) =======
-- PLANIFIER → NOTIFIER → SAUVEGARDER → MAINTENANCE → VÉRIFIER → FERMER.
-- Table plateforme : une maintenance touche l'application dans son ensemble.

CREATE TABLE nexora.maintenance_window (
    id               uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    scheduled_at     timestamptz NOT NULL,
    duration_minutes integer     NOT NULL,
    impact           text        NOT NULL DEFAULT '',
    services         jsonb       NOT NULL DEFAULT '[]'::jsonb,
    responsible      text,
    rollback_plan    text,
    status           text        NOT NULL DEFAULT 'planned',
    started_at       timestamptz,
    ended_at         timestamptz,
    notes            text,
    created_by       uuid REFERENCES nexora.app_user(id),
    created_at       timestamptz NOT NULL DEFAULT now(),
    updated_at       timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT maintenance_window_duration_chk CHECK (duration_minutes > 0),
    CONSTRAINT maintenance_window_status_chk CHECK (
        status IN ('planned','notified','backed_up','in_progress','verifying','closed','canceled')),
    CONSTRAINT maintenance_window_services_chk CHECK (jsonb_typeof(services) = 'array')
);
CREATE INDEX maintenance_window_status_idx ON nexora.maintenance_window (status, scheduled_at);
-- Une seule fenêtre active à la fois (mode maintenance §24).
CREATE UNIQUE INDEX maintenance_window_active_uk ON nexora.maintenance_window ((true)) WHERE status = 'in_progress';

-- ===================== 4. Rétention & purge technique (§16-19, §22) ========
-- Liste fermée de données strictement TECHNIQUES — jamais de donnée métier ou comptable (§17).

CREATE TABLE nexora.retention_policy (
    id              uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    data_type       text        NOT NULL,
    company_id      uuid REFERENCES nexora.company(id) ON DELETE CASCADE,  -- NULL = défaut système
    duration_days   integer     NOT NULL,
    action          text        NOT NULL DEFAULT 'delete',                 -- archive | delete
    legal_reference text,
    created_by      uuid REFERENCES nexora.app_user(id),
    created_at      timestamptz NOT NULL DEFAULT now(),
    updated_at      timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT retention_policy_type_chk CHECK (
        data_type IN ('expired_sessions','expired_mfa_challenges','completed_job_runs','resolved_notifications')),
    CONSTRAINT retention_policy_action_chk   CHECK (action IN ('archive','delete')),
    CONSTRAINT retention_policy_duration_chk CHECK (duration_days > 0),
    CONSTRAINT retention_policy_uk UNIQUE NULLS NOT DISTINCT (data_type, company_id)
);

CREATE TABLE nexora.purge_run (
    id                        uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    data_type                 text        NOT NULL,
    company_id                uuid REFERENCES nexora.company(id),   -- NULL = purge technique globale
    dry_run                   boolean     NOT NULL DEFAULT true,
    cutoff_date               timestamptz NOT NULL,
    object_count              integer     NOT NULL DEFAULT 0,
    protected_excluded_count  integer     NOT NULL DEFAULT 0,
    status                    text        NOT NULL DEFAULT 'previewed',
    executed_by               uuid REFERENCES nexora.app_user(id),
    executed_at               timestamptz,
    journal                   jsonb       NOT NULL DEFAULT '{}'::jsonb,
    created_at                timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT purge_run_status_chk CHECK (status IN ('previewed','executed','failed'))
);
CREATE INDEX purge_run_type_idx ON nexora.purge_run (data_type, created_at DESC);

-- ===================== 5. Correction de données contrôlée (§44) ============
-- Liste fermée de tables/champs corrigeables ; jamais d'UPDATE libre. Demandeur ≠ approbateur.

CREATE TABLE nexora.data_correction_request (
    id           uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    table_name   text        NOT NULL,
    record_id    uuid        NOT NULL,
    field_name   text        NOT NULL,
    old_value    text,
    new_value    text        NOT NULL,
    reason       text        NOT NULL,
    status       text        NOT NULL DEFAULT 'requested',
    requested_by uuid        NOT NULL REFERENCES nexora.app_user(id),
    approved_by  uuid REFERENCES nexora.app_user(id),
    applied_at   timestamptz,
    notes        text,
    company_id   uuid REFERENCES nexora.company(id),   -- NULL pour app_user (pas d'entreprise porteuse)
    created_at   timestamptz NOT NULL DEFAULT now(),
    updated_at   timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT data_correction_table_chk  CHECK (table_name IN ('app_user','customer','supplier','product')),
    CONSTRAINT data_correction_status_chk CHECK (status IN ('requested','approved','rejected','applied')),
    CONSTRAINT data_correction_4eyes_chk  CHECK (approved_by IS NULL OR approved_by <> requested_by)
);
CREATE INDEX data_correction_status_idx ON nexora.data_correction_request (status, created_at DESC);

-- ===================== 6. RLS (§38 séparation des périmètres) ==============
-- retention_policy / purge_run / data_correction_request : visibles si défaut système (company_id
-- NULL) ou si l'entreprise est dans le périmètre de l'appelant ; job_definition / job_run /
-- feature_flag / maintenance_window restent des tables plateforme sans RLS (comme `deployment`).

DROP POLICY IF EXISTS retention_policy_isolation ON nexora.retention_policy;
ALTER TABLE nexora.retention_policy ENABLE ROW LEVEL SECURITY;
ALTER TABLE nexora.retention_policy FORCE ROW LEVEL SECURITY;
CREATE POLICY retention_policy_isolation ON nexora.retention_policy
    USING (nexora.rls_bypass() OR company_id IS NULL OR nexora.rls_allows_company(company_id))
    WITH CHECK (nexora.rls_bypass() OR nexora.rls_allows_company(company_id));

DROP POLICY IF EXISTS purge_run_isolation ON nexora.purge_run;
ALTER TABLE nexora.purge_run ENABLE ROW LEVEL SECURITY;
ALTER TABLE nexora.purge_run FORCE ROW LEVEL SECURITY;
CREATE POLICY purge_run_isolation ON nexora.purge_run
    USING (nexora.rls_bypass() OR company_id IS NULL OR nexora.rls_allows_company(company_id))
    WITH CHECK (nexora.rls_bypass() OR company_id IS NULL OR nexora.rls_allows_company(company_id));

DROP POLICY IF EXISTS data_correction_request_isolation ON nexora.data_correction_request;
ALTER TABLE nexora.data_correction_request ENABLE ROW LEVEL SECURITY;
ALTER TABLE nexora.data_correction_request FORCE ROW LEVEL SECURITY;
CREATE POLICY data_correction_request_isolation ON nexora.data_correction_request
    USING (nexora.rls_bypass() OR company_id IS NULL OR nexora.rls_allows_company(company_id))
    WITH CHECK (nexora.rls_bypass() OR company_id IS NULL OR nexora.rls_allows_company(company_id));

-- ===================== 7. Audit (§31) =======================================

DO $$
DECLARE t text;
BEGIN
    FOREACH t IN ARRAY ARRAY['job_definition','job_run','feature_flag','maintenance_window',
                              'retention_policy','purge_run','data_correction_request'] LOOP
        EXECUTE format('DROP TRIGGER IF EXISTS trg_audit_%1$s ON nexora.%1$I', t);
        EXECUTE format(
            'CREATE TRIGGER trg_audit_%1$s AFTER INSERT OR UPDATE OR DELETE ON nexora.%1$I
               FOR EACH ROW EXECUTE FUNCTION nexora.audit_row()', t);
    END LOOP;
END;
$$;

-- ===================== 8. Amorçage (§7-9, §17) ==============================

INSERT INTO nexora.job_definition (key, name, category, description, owner) VALUES
    ('automations.due', 'Automatisations & relances dues', 'business',
        'Exécute les règles d''automatisation dont l''échéance est passée (relances clients, alertes).', 'scheduler'),
    ('lots.sweep_expired', 'Balayage des lots expirés', 'business',
        'Bloque automatiquement les lots dont la date de péremption est dépassée.', 'scheduler'),
    ('continuity.health_check', 'Contrôle de santé continuité', 'technical',
        'Vérifie les sauvegardes en retard et les tests de restauration en échec.', 'scheduler'),
    ('webhooks.deliver', 'Livraison des webhooks sortants', 'technical',
        'Remet les webhooks sortants dus, avec retry et backoff.', 'scheduler'),
    ('payments.expire_stale', 'Expiration des intentions de paiement', 'technical',
        'Expire les intentions de paiement dépassant leur délai.', 'scheduler'),
    ('notifications.dispatch', 'Remise des notifications externes', 'technical',
        'Envoie les notifications en file (email/SMS/webhook) avec backoff et dead-letter.', 'scheduler'),
    ('subscriptions.lifecycle', 'Cycle de vie des abonnements SaaS', 'technical',
        'Fait avancer les abonnements plateforme (essai, grâce, suspension) et réconcilie la facturation.', 'scheduler'),
    ('imports.resume_stalled', 'Reprise des imports interrompus', 'technical',
        'Reprend les imports restés bloqués en cours de traitement.', 'scheduler'),
    ('maintenance.purge', 'Purge technique planifiée', 'technical',
        'Purge les données techniques expirées selon les politiques de rétention (sessions, défis MFA, historique de jobs, notifications résolues).', 'scheduler')
ON CONFLICT (key) DO NOTHING;

INSERT INTO nexora.retention_policy (data_type, company_id, duration_days, action) VALUES
    ('expired_sessions',        NULL, 7,  'delete'),
    ('expired_mfa_challenges',  NULL, 1,  'delete'),
    ('completed_job_runs',      NULL, 90, 'delete'),
    ('resolved_notifications',  NULL, 30, 'delete')
ON CONFLICT ON CONSTRAINT retention_policy_uk DO NOTHING;

-- ===================== 9. Permissions (§51) =================================
INSERT INTO nexora.permission (code, name, module, action) VALUES
    ('operations.correct_data', 'Exploitation — approuver/appliquer une correction de donnée contrôlée', 'operations', 'correct_data')
ON CONFLICT (code) DO NOTHING;

CREATE OR REPLACE FUNCTION pg_temp.grant_perms_v54(p_role text, p_patterns text[])
RETURNS void LANGUAGE sql AS $$
    INSERT INTO nexora.role_permission (role_id, permission_id)
    SELECT r.id, p.id FROM nexora.role r
    JOIN nexora.permission p ON p.code LIKE ANY (p_patterns)
    WHERE r.name = p_role AND r.system_role
    ON CONFLICT DO NOTHING;
$$;
SELECT pg_temp.grant_perms_v54('super_administrator', ARRAY['operations.%']);
SELECT pg_temp.grant_perms_v54('group_administrator', ARRAY['operations.%']);
SELECT pg_temp.grant_perms_v54('owner_direction',     ARRAY['operations.%']);

SELECT nexora.attach_common_triggers();
