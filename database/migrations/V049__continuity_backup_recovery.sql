-- =============================================================================
-- NEXORA ONE — V049 — Continuité, sauvegarde & reprise : registre applicatif (Phase 36)
--
-- La sauvegarde physique (pg_dump / WAL / snapshots), la réplication et le failover sont des
-- opérations d'infrastructure (cf. `database/scripts/backup.sh`). Cette migration ajoute le
-- **registre applicatif** qui les pilote et les surveille (§4-7, §21, §36-38, §43-46) :
--   backup_policy       : objectifs RPO/RTO, fréquence, rétention, destination, chiffrement (§4-7)
--   backup              : catalogue des sauvegardes réalisées + vérification (§6, §14, §21)
--   restore_run         : restaurations & tests de restauration, en sandbox ou en production (§18-21)
--   continuity_incident : registre d'incident + runbook §43 + post-mortem §45
--
-- Le socle voisin reste VALIDÉ : moteur de synchro offline + curseurs + reprise (Phase 10, §29-30),
-- file de remise fiable retry/dead-letter (`notification_outbox`, Phase 34, §28), audit immuable
-- (`audit_log`, Phase 11, §38), `/health` + `/health/ready` (Phase 3, §46).
-- =============================================================================
SET search_path TO nexora, public;

-- ===================== 1. Politique de continuité (§4-7) ==================
CREATE TABLE nexora.backup_policy (
    id                  uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    company_id          uuid        NOT NULL REFERENCES nexora.company(id),
    name                text        NOT NULL DEFAULT 'Politique par défaut',
    kinds               jsonb       NOT NULL DEFAULT '["full","config","documents"]'::jsonb,
    frequency           text        NOT NULL DEFAULT 'daily',
    retention_days      integer     NOT NULL DEFAULT 30,
    destination         text        NOT NULL DEFAULT 'offsite',
    encrypted           boolean     NOT NULL DEFAULT true,
    immutable           boolean     NOT NULL DEFAULT false,   -- verrou de rétention si le stockage le permet (§13)
    rpo_target_minutes  integer     NOT NULL DEFAULT 1440,
    rto_target_minutes  integer     NOT NULL DEFAULT 240,
    status              text        NOT NULL DEFAULT 'active',
    updated_by          uuid REFERENCES nexora.app_user(id),
    created_at          timestamptz NOT NULL DEFAULT now(),
    updated_at          timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT backup_policy_freq_chk   CHECK (frequency IN ('hourly','daily','weekly','monthly')),
    CONSTRAINT backup_policy_dest_chk   CHECK (destination IN ('local','nas','cloud_object','offsite')),
    CONSTRAINT backup_policy_status_chk CHECK (status IN ('active','paused')),
    CONSTRAINT backup_policy_retention_chk CHECK (retention_days BETWEEN 1 AND 3650),
    CONSTRAINT backup_policy_rpo_chk    CHECK (rpo_target_minutes BETWEEN 1 AND 10080),
    CONSTRAINT backup_policy_rto_chk    CHECK (rto_target_minutes BETWEEN 1 AND 10080),
    CONSTRAINT backup_policy_company_uk UNIQUE (company_id)
);

-- ===================== 2. Catalogue des sauvegardes (§6, §14, §21) ========
CREATE TABLE nexora.backup (
    id                  uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    company_id          uuid        NOT NULL REFERENCES nexora.company(id),
    kind                text        NOT NULL,
    status              text        NOT NULL DEFAULT 'completed',
    started_at          timestamptz NOT NULL DEFAULT now(),
    finished_at         timestamptz,
    size_bytes          bigint,
    checksum            text,                                 -- SHA-256 calculé par l'agent
    object_count        bigint,                               -- nb d'objets/lignes déclaré
    covers_up_to        timestamptz NOT NULL DEFAULT now(),   -- données saines jusqu'à cet instant (point RPO)
    location_ref        text,                                 -- pointeur opaque — jamais un secret (§6, §44)
    encrypted           boolean     NOT NULL DEFAULT true,
    immutable_until     timestamptz,
    verified_at         timestamptz,
    verification_detail jsonb,
    expires_at          timestamptz,                          -- dérivé de la rétention de la politique
    error               text,
    created_by          uuid REFERENCES nexora.app_user(id),
    created_at          timestamptz NOT NULL DEFAULT now(),
    updated_at          timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT backup_kind_chk   CHECK (kind IN ('full','incremental','config','documents','snapshot')),
    CONSTRAINT backup_status_chk CHECK (status IN ('running','completed','failed','verified','expired'))
);
CREATE INDEX backup_company_point_idx ON nexora.backup (company_id, covers_up_to DESC);
CREATE INDEX backup_expiry_idx        ON nexora.backup (expires_at) WHERE status <> 'expired';
ALTER TABLE nexora.backup ADD CONSTRAINT backup_company_id_uk UNIQUE (id, company_id);

-- ===================== 3. Restaurations & tests (§18-21) ==================
CREATE TABLE nexora.restore_run (
    id                    uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    company_id            uuid        NOT NULL REFERENCES nexora.company(id),
    backup_id             uuid        NOT NULL,
    mode                  text        NOT NULL DEFAULT 'test',   -- test (sandbox) | production
    target                text,
    status                text        NOT NULL DEFAULT 'running',
    checksum_ok           boolean,
    object_count_expected bigint,
    object_count_actual   bigint,
    report                jsonb       NOT NULL DEFAULT '{}'::jsonb,
    started_at            timestamptz NOT NULL DEFAULT now(),
    finished_at           timestamptz,
    requested_by          uuid REFERENCES nexora.app_user(id),
    approved_by           uuid REFERENCES nexora.app_user(id),  -- restauration en production : validation par un tiers (§39)
    created_at            timestamptz NOT NULL DEFAULT now(),
    updated_at            timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT restore_run_mode_chk   CHECK (mode IN ('test','production')),
    CONSTRAINT restore_run_status_chk CHECK (status IN ('pending_approval','running','passed','failed','cancelled')),
    CONSTRAINT restore_run_backup_fk  FOREIGN KEY (backup_id, company_id)
        REFERENCES nexora.backup (id, company_id) ON DELETE CASCADE
);
CREATE INDEX restore_run_backup_idx ON nexora.restore_run (backup_id, started_at DESC);

-- ===================== 4. Registre d'incident (§43-45) ===================
CREATE TABLE nexora.continuity_incident (
    id                 uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    company_id         uuid        NOT NULL REFERENCES nexora.company(id),
    severity           text        NOT NULL DEFAULT 'p2',
    category           text        NOT NULL,
    title              text        NOT NULL,
    detected_via       text        NOT NULL DEFAULT 'monitoring',
    status             text        NOT NULL DEFAULT 'open',
    runbook_step       smallint    NOT NULL DEFAULT 1,          -- 1..9 (§43 : détecter → améliorer)
    affected_scope     jsonb       NOT NULL DEFAULT '{}'::jsonb,
    rpo_actual_minutes integer,
    rto_actual_minutes integer,
    post_mortem        jsonb,                                   -- §45 : cause / impact / durée / actions / prévention / responsable / échéance
    opened_at          timestamptz NOT NULL DEFAULT now(),
    resolved_at        timestamptz,
    opened_by          uuid REFERENCES nexora.app_user(id),
    resolved_by        uuid REFERENCES nexora.app_user(id),
    created_at         timestamptz NOT NULL DEFAULT now(),
    updated_at         timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT continuity_incident_sev_chk CHECK (severity IN ('p1','p2','p3','p4')),
    CONSTRAINT continuity_incident_status_chk CHECK (status IN ('open','mitigating','restoring','verifying','resolved')),
    CONSTRAINT continuity_incident_step_chk CHECK (runbook_step BETWEEN 1 AND 9),
    CONSTRAINT continuity_incident_detected_chk CHECK (detected_via IN ('monitoring','alert','user_report','scheduled_test','deploy')),
    CONSTRAINT continuity_incident_category_chk CHECK (category IN (
        'server_failure','disk_failure','db_corruption','human_error','accidental_deletion','ransomware',
        'network_loss','cloud_provider','power','site_disaster','deploy_error','other'))
);
CREATE INDEX continuity_incident_open_idx ON nexora.continuity_incident (company_id, opened_at DESC)
    WHERE status <> 'resolved';

-- ===================== 5. RLS (§39, §42) & audit (§38) ===================
DO $$
DECLARE t text;
BEGIN
    FOREACH t IN ARRAY ARRAY['backup_policy','backup','restore_run','continuity_incident'] LOOP
        PERFORM nexora.enable_company_rls(format('nexora.%I', t)::regclass);
        EXECUTE format('DROP TRIGGER IF EXISTS trg_audit_%1$s ON nexora.%1$I', t);
        EXECUTE format(
            'CREATE TRIGGER trg_audit_%1$s AFTER INSERT OR UPDATE OR DELETE ON nexora.%1$I
               FOR EACH ROW EXECUTE FUNCTION nexora.audit_row()', t);
    END LOOP;
END;
$$;

-- ===================== 6. Permissions (§39) ==============================
INSERT INTO nexora.permission (code, name, module, action) VALUES
    ('continuity.view',    'Continuité — consulter sauvegardes, DR & incidents', 'continuity', 'view'),
    ('continuity.manage',  'Continuité — politiques, enregistrer & vérifier les sauvegardes, tests', 'continuity', 'manage'),
    ('continuity.restore', 'Continuité — exécuter une restauration en production', 'continuity', 'restore')
ON CONFLICT (code) DO NOTHING;

CREATE OR REPLACE FUNCTION pg_temp.grant_perms_v49(p_role text, p_patterns text[])
RETURNS void LANGUAGE sql AS $$
    INSERT INTO nexora.role_permission (role_id, permission_id)
    SELECT r.id, p.id FROM nexora.role r
    JOIN nexora.permission p ON p.code LIKE ANY (p_patterns)
    WHERE r.name = p_role AND r.system_role
    ON CONFLICT DO NOTHING;
$$;
SELECT pg_temp.grant_perms_v49('super_administrator', ARRAY['continuity.%']);
SELECT pg_temp.grant_perms_v49('group_administrator', ARRAY['continuity.%']);
-- La direction d'entreprise autorise une restauration de SES données ; la règle des deux
-- personnes (§39) reste appliquée à l'exécution (`ApproveProductionRestoreAsync`).
SELECT pg_temp.grant_perms_v49('owner_direction',     ARRAY['continuity.%']);

SELECT nexora.attach_common_triggers();
