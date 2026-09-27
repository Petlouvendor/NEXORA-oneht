-- =============================================================================
-- NEXORA ONE — V050 — Qualité & préparation production : registre de mise en service (Phase 37)
--
-- Le socle qualité / observabilité existe déjà et reste VALIDÉ par cette phase :
--   Tests : 243 tests backend (unit + intégration + API) + 53 frontend, Debug & Release, 0 warning
--   Phase 3  : `/health` + `/health/ready` (`DatabaseHealthCheck` tag `ready`), Serilog structuré
--              + `CorrelationIdMiddleware` (`X-Correlation-Id` → `LogContext`), RFC 7807, rate limiter
--   Phase 11 : `audit_log` immuable (trigger), `security_policy`
--   Phase 16 : `SchedulerRunner` idempotent (traitements asynchrones)
--   Phase 31 : `IDataQualityService` — 10 contrôles + score 0-100 + note A-E (§46)
--   Phase 32 : `IConfigGuardrails` — cohérence de configuration avant publication
--   Phase 33 : MFA, verrouillage, journal d'évènements de sécurité (§15, §51)
--   Phase 36 : `IContinuityService` — sauvegardes vérifiées, tests de restauration, DR status,
--              runbook d'incident (§40 « backup vérifié / restauration testée », §50)
--
-- Cette migration ajoute le **registre de mise en service** (§34, §38-40, §54, §56) :
--   deployment      : journal des déploiements / releases + rollback (§34, §38-39)
--   golive_signoff  : validation Go-Live d'une entreprise, avec instantané de la checklist (§40, §54)
-- La checklist Go-Live et l'état système sont **dérivés en direct** — aucune table de résultat.
-- =============================================================================
SET search_path TO nexora, public;

-- ===================== 1. Journal des déploiements (§34, §38-39, §56) =====
-- Table plateforme (pas de company_id — comme schema_migration).
CREATE TABLE nexora.deployment (
    id               uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    version          text        NOT NULL,
    environment      text        NOT NULL DEFAULT 'production',
    status           text        NOT NULL DEFAULT 'succeeded',
    schema_version   text,                                  -- max(schema_migration.version) au déploiement
    notes            text,
    rolled_back_from uuid REFERENCES nexora.deployment(id), -- déploiement de retour arrière → l'origine
    rollback_reason  text,
    started_at       timestamptz NOT NULL DEFAULT now(),
    finished_at      timestamptz,
    created_by       uuid REFERENCES nexora.app_user(id),
    created_at       timestamptz NOT NULL DEFAULT now(),
    updated_at       timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT deployment_env_chk    CHECK (environment IN ('local','test','staging','production')),
    CONSTRAINT deployment_status_chk CHECK (status IN ('running','succeeded','failed','rolled_back'))
);
CREATE INDEX deployment_time_idx ON nexora.deployment (environment, started_at DESC);

-- ===================== 2. Validation Go-Live (§40, §54) ==================
CREATE TABLE nexora.golive_signoff (
    id                 uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    company_id         uuid        NOT NULL REFERENCES nexora.company(id),
    environment        text        NOT NULL DEFAULT 'production',
    verdict            text        NOT NULL,
    checklist_snapshot jsonb       NOT NULL DEFAULT '{}'::jsonb,   -- copie figée de la checklist au moment de la signature
    can_go_live        boolean     NOT NULL,
    forced             boolean     NOT NULL DEFAULT false,
    force_reason       text,
    notes              text,
    signed_by          uuid REFERENCES nexora.app_user(id),
    signed_at          timestamptz NOT NULL DEFAULT now(),
    created_at         timestamptz NOT NULL DEFAULT now(),
    updated_at         timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT golive_signoff_verdict_chk CHECK (verdict IN ('go','go_with_reservations','no_go')),
    CONSTRAINT golive_signoff_forced_chk  CHECK (NOT forced OR force_reason IS NOT NULL)
);
CREATE INDEX golive_signoff_company_idx ON nexora.golive_signoff (company_id, signed_at DESC);

-- ===================== 3. RLS (§8 isolation) & audit (§25-26) ============
SELECT nexora.enable_company_rls('nexora.golive_signoff'::regclass);

DO $$
DECLARE t text;
BEGIN
    FOREACH t IN ARRAY ARRAY['deployment','golive_signoff'] LOOP
        EXECUTE format('DROP TRIGGER IF EXISTS trg_audit_%1$s ON nexora.%1$I', t);
        EXECUTE format(
            'CREATE TRIGGER trg_audit_%1$s AFTER INSERT OR UPDATE OR DELETE ON nexora.%1$I
               FOR EACH ROW EXECUTE FUNCTION nexora.audit_row()', t);
    END LOOP;
END;
$$;

-- ===================== 4. Permissions (§51) =============================
INSERT INTO nexora.permission (code, name, module, action) VALUES
    ('operations.view',   'Exploitation — état système, checklist Go-Live, déploiements', 'operations', 'view'),
    ('operations.manage', 'Exploitation — enregistrer un déploiement / rollback, signer le Go-Live', 'operations', 'manage')
ON CONFLICT (code) DO NOTHING;

CREATE OR REPLACE FUNCTION pg_temp.grant_perms_v50(p_role text, p_patterns text[])
RETURNS void LANGUAGE sql AS $$
    INSERT INTO nexora.role_permission (role_id, permission_id)
    SELECT r.id, p.id FROM nexora.role r
    JOIN nexora.permission p ON p.code LIKE ANY (p_patterns)
    WHERE r.name = p_role AND r.system_role
    ON CONFLICT DO NOTHING;
$$;
SELECT pg_temp.grant_perms_v50('super_administrator', ARRAY['operations.%']);
SELECT pg_temp.grant_perms_v50('group_administrator', ARRAY['operations.%']);
SELECT pg_temp.grant_perms_v50('owner_direction',     ARRAY['operations.%']);
SELECT pg_temp.grant_perms_v50('manager',             ARRAY['operations.view']);

SELECT nexora.attach_common_triggers();
