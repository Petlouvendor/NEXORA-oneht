-- =============================================================================
-- NEXORA — V046 — Identité & sécurité : MFA (TOTP) + durcissement de la connexion (Phase 33)
--
-- Le socle IAM / sécurité existe déjà et reste VALIDÉ par cette phase :
--   Phase 3  : JWT + RLS multi-tenant, `TenantResolutionMiddleware` (périmètre serveur §11-12),
--              `security_epoch` (déconnexion globale), politique deny-by-default (§28)
--   Phase 4  : `app_user`, `role`, `permission`, `role_permission`, `access_scope`, `user_role`
--   Phase 11 (V030) : `user_session` (registre + révocation §10), `step_up_challenge`
--              (ré-authentification §6), `security_policy` (MFA obligatoire par rôle §37,
--              durée de session, rétention d'audit §34), `audit_log` immuable (trigger),
--              moteur anti-fraude (`fraud_rule` / `fraud_signal` §35), workflow d'approbation
--              (demandeur ≠ approbateur, double approbation §24)
--   Phase 14 (V033) : rôles personnalisés (`ICustomRoleService`), config par périmètre
--   Phase 22 (V035) : affectations de rôle temporaires & révocables (`user_role.valid_from/
--              valid_until/revoked_at`), garde anti-élévation, `GET /me/scopes`, `/me/permissions`
--
-- Les colonnes `app_user.mfa_enabled` / `mfa_secret` existaient (V004) mais n'étaient jamais
-- utilisées. Cette migration rend la **MFA TOTP** opérationnelle de bout en bout (§8) et
-- ajoute la **protection contre les essais répétés** (§7, §4 « verrouillé temporairement », §48) :
--   app_user            += mfa_pending_secret / mfa_enrolled_at / failed_login_count / locked_until
--   mfa_recovery_code   : codes de récupération à usage unique, hachés (§8, §9)
--   mfa_challenge       : défi court entre « mot de passe OK » et émission du jeton (§8)
--   permission security.reset_mfa : un administrateur réinitialise la MFA d'un utilisateur (§9, §39)
-- =============================================================================
SET search_path TO nexora, public;

-- ===================== 1. Cycle de vie MFA + verrouillage (§4, §7, §8) ======
ALTER TABLE nexora.app_user
    ADD COLUMN IF NOT EXISTS mfa_pending_secret  text,          -- secret en cours d'enrôlement, avant 1re vérif
    ADD COLUMN IF NOT EXISTS mfa_enrolled_at     timestamptz,
    ADD COLUMN IF NOT EXISTS failed_login_count  integer     NOT NULL DEFAULT 0,
    ADD COLUMN IF NOT EXISTS locked_until        timestamptz;   -- verrouillage temporaire (§4)

ALTER TABLE nexora.app_user DROP CONSTRAINT IF EXISTS app_user_failed_login_chk;
ALTER TABLE nexora.app_user ADD  CONSTRAINT app_user_failed_login_chk CHECK (failed_login_count >= 0);

-- ===================== 2. Codes de récupération (§8, §9) ====================
CREATE TABLE nexora.mfa_recovery_code (
    id         uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    user_id    uuid        NOT NULL REFERENCES nexora.app_user(id) ON DELETE CASCADE,
    code_hash  text        NOT NULL,                 -- SHA-256, jamais en clair (§7, §31)
    used_at    timestamptz,
    created_at timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT mfa_recovery_code_uk UNIQUE (user_id, code_hash)
);
CREATE INDEX mfa_recovery_code_open_idx ON nexora.mfa_recovery_code (user_id) WHERE used_at IS NULL;

-- ===================== 3. Défi de connexion MFA (§8) =======================
CREATE TABLE nexora.mfa_challenge (
    id          uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    user_id     uuid        NOT NULL REFERENCES nexora.app_user(id) ON DELETE CASCADE,
    company_id  uuid REFERENCES nexora.company(id),  -- entreprise demandée au login (facultatif)
    token_hash  text        NOT NULL,
    issued_at   timestamptz NOT NULL DEFAULT now(),
    expires_at  timestamptz NOT NULL,
    consumed_at timestamptz,
    attempts    smallint    NOT NULL DEFAULT 0
);
CREATE INDEX mfa_challenge_lookup_idx ON nexora.mfa_challenge (user_id, expires_at DESC);

-- ===================== 4. RLS — tables propres à l'utilisateur (§17, §20) ===
DO $$
DECLARE t text;
BEGIN
    FOREACH t IN ARRAY ARRAY['mfa_recovery_code','mfa_challenge'] LOOP
        EXECUTE format('ALTER TABLE nexora.%I ENABLE ROW LEVEL SECURITY', t);
        EXECUTE format('ALTER TABLE nexora.%I FORCE ROW LEVEL SECURITY', t);
        EXECUTE format('DROP POLICY IF EXISTS %I_isolation ON nexora.%I', t, t);
        EXECUTE format(
            'CREATE POLICY %1$I_isolation ON nexora.%1$I
               USING (nexora.rls_bypass() OR user_id = nexora.current_user_id())
               WITH CHECK (nexora.rls_bypass() OR user_id = nexora.current_user_id())', t);
    END LOOP;
END;
$$;

-- ===================== 5. Permission — réinitialisation MFA par un admin (§9, §39) ==
INSERT INTO nexora.permission (code, name, module, action) VALUES
    ('security.reset_mfa', 'Sécurité — réinitialiser la MFA d''un utilisateur', 'security', 'reset_mfa')
ON CONFLICT (code) DO NOTHING;

CREATE OR REPLACE FUNCTION pg_temp.grant_perms_v46(p_role text, p_patterns text[])
RETURNS void LANGUAGE sql AS $$
    INSERT INTO nexora.role_permission (role_id, permission_id)
    SELECT r.id, p.id FROM nexora.role r
    JOIN nexora.permission p ON p.code LIKE ANY (p_patterns)
    WHERE r.name = p_role AND r.system_role
    ON CONFLICT DO NOTHING;
$$;

SELECT pg_temp.grant_perms_v46('super_administrator', ARRAY['security.reset_mfa']);
SELECT pg_temp.grant_perms_v46('group_administrator', ARRAY['security.reset_mfa']);
SELECT pg_temp.grant_perms_v46('owner_direction',     ARRAY['security.reset_mfa']);

SELECT nexora.attach_common_triggers();
