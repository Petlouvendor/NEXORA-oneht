-- =============================================================================
-- NEXORA — V035 — Affectations à durée limitée & révocables (Phase 22 §9, §18, §32-33)
-- Une affectation (user_role) porte désormais une fenêtre de validité et une révocation
-- explicite. La résolution du profil de sécurité n'accorde un rôle que si l'affectation est
-- active à l'instant présent — l'accès temporaire expire de lui-même.
-- =============================================================================
SET search_path TO nexora, public;

ALTER TABLE nexora.user_role
    ADD COLUMN IF NOT EXISTS valid_from  timestamptz,
    ADD COLUMN IF NOT EXISTS valid_until timestamptz,
    ADD COLUMN IF NOT EXISTS reason      text,
    ADD COLUMN IF NOT EXISTS revoked_at  timestamptz,
    ADD COLUMN IF NOT EXISTS revoked_by  uuid REFERENCES nexora.app_user(id);

ALTER TABLE nexora.user_role DROP CONSTRAINT IF EXISTS user_role_validity_chk;
ALTER TABLE nexora.user_role
    ADD CONSTRAINT user_role_validity_chk CHECK (
        valid_until IS NULL OR valid_from IS NULL OR valid_until > valid_from);

CREATE INDEX IF NOT EXISTS user_role_active_idx ON nexora.user_role (user_id) WHERE revoked_at IS NULL;

-- Audit des affectations (déjà couvert par le déclencheur générique — rappel de portée §44).
