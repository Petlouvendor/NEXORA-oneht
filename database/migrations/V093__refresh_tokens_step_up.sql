-- =============================================================================
-- NEXORA — V093 — Jetons de rafraîchissement et réauthentification forte (finalisation produit, phase 17)
--
--  * auth_refresh_token : un jeton opaque par rotation, rattaché à sa session. Chaque rotation consomme
--    l'ancien jeton (used_at) et en crée un nouveau (replaced_by). Présenter un jeton déjà consommé
--    trahit un vol : toutes les sessions de l'utilisateur sont révoquées. Seul le SHA-256 est stocké.
--  * Les colonnes ajoutées par V078 à user_session (refresh_token_hash, refresh_used_at) n'ont jamais été
--    utilisées et ne permettaient pas de détecter une réutilisation après rotation : elles sont retirées.
--    refresh_expires_at reste l'horizon de rafraîchissement de la session.
--  * Réauthentification forte par catégorie d'action critique (paiement, annulation, validation comptable,
--    changement de permission, suppression, données sensibles), liée à la session, avec suivi des échecs.
-- Idempotente.
-- =============================================================================
CREATE TABLE IF NOT EXISTS nexora.auth_refresh_token (
    id           uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    session_id   uuid NOT NULL REFERENCES nexora.user_session(id) ON DELETE CASCADE,
    user_id      uuid NOT NULL REFERENCES nexora.app_user(id),
    token_hash   text NOT NULL,
    issued_at    timestamptz NOT NULL DEFAULT now(),
    expires_at   timestamptz NOT NULL,
    used_at      timestamptz,
    replaced_by  uuid REFERENCES nexora.auth_refresh_token(id),
    revoked_at   timestamptz,
    created_ip   inet
);
CREATE UNIQUE INDEX IF NOT EXISTS auth_refresh_token_hash_uk ON nexora.auth_refresh_token (token_hash);
CREATE INDEX IF NOT EXISTS auth_refresh_token_session_ix ON nexora.auth_refresh_token (session_id);
CREATE INDEX IF NOT EXISTS auth_refresh_token_user_ix ON nexora.auth_refresh_token (user_id) WHERE revoked_at IS NULL;

DROP INDEX IF EXISTS nexora.user_session_refresh_hash_uk;
DROP INDEX IF EXISTS nexora.user_session_refresh_lookup_idx;
ALTER TABLE nexora.user_session DROP COLUMN IF EXISTS refresh_token_hash;
ALTER TABLE nexora.user_session DROP COLUMN IF EXISTS refresh_used_at;
COMMENT ON COLUMN nexora.user_session.refresh_expires_at IS
    'Horizon de rafraîchissement de la session (durée maximale) ; les jetons sont dans auth_refresh_token.';

ALTER TABLE nexora.step_up_challenge DROP CONSTRAINT IF EXISTS step_up_challenge_purpose_chk;
ALTER TABLE nexora.step_up_challenge ADD CONSTRAINT step_up_challenge_purpose_chk CHECK (purpose IN (
    'payment','cancellation','accounting_validation','permission_change','deletion','sensitive_data',
    'period_close','period_reopen','financial_exceptional','consolidation_publish','device_revoke','sensitive_export','other'));
ALTER TABLE nexora.step_up_challenge
    ADD COLUMN IF NOT EXISTS session_id uuid REFERENCES nexora.user_session(id) ON DELETE CASCADE,
    ADD COLUMN IF NOT EXISTS method text;

CREATE TABLE IF NOT EXISTS nexora.step_up_failure (
    id          uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    user_id     uuid NOT NULL REFERENCES nexora.app_user(id),
    purpose     text NOT NULL,
    reason      text NOT NULL,
    ip_address  inet,
    created_at  timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX IF NOT EXISTS step_up_failure_user_ix ON nexora.step_up_failure (user_id, created_at);

DO $$
DECLARE
    t text;
BEGIN
    FOREACH t IN ARRAY ARRAY['step_up_challenge'] LOOP
        EXECUTE format('DROP TRIGGER IF EXISTS trg_audit_%1$s ON nexora.%1$I', t);
        EXECUTE format(
            'CREATE TRIGGER trg_audit_%1$s AFTER INSERT OR UPDATE OR DELETE ON nexora.%1$I
               FOR EACH ROW EXECUTE FUNCTION nexora.audit_row()', t);
    END LOOP;
END;
$$;
