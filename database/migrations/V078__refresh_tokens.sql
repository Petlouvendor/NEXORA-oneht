-- =============================================================================
-- NEXORA — V078 — Refresh Tokens (Production Readiness — REQ-03)
--
-- Étend nexora.user_session avec les colonnes nécessaires aux refresh tokens :
--   refresh_token_hash  — SHA-256 hex du token opaque émis au client
--   refresh_expires_at  — expiration indépendante du JWT (Jwt:RefreshTokenDays, défaut 30 j)
--   refresh_used_at     — horodatage de rotation ; NOT NULL = token déjà consommé
--                         (détection de réutilisation → révocation globale des sessions)
--
-- Aucune nouvelle table : on étend user_session (V030, complétée en V063).
-- Idempotente : ADD COLUMN IF NOT EXISTS, CREATE INDEX IF NOT EXISTS.
-- =============================================================================
SET search_path TO nexora, public;

-- ===================== 1. Colonnes refresh token ============================
ALTER TABLE nexora.user_session
    ADD COLUMN IF NOT EXISTS refresh_token_hash  text,
    ADD COLUMN IF NOT EXISTS refresh_expires_at  timestamptz,
    ADD COLUMN IF NOT EXISTS refresh_used_at     timestamptz;

-- ===================== 2. Contraintes & index ================================
-- Unicité du hash — un hash ne peut être associé qu'à une session.
CREATE UNIQUE INDEX IF NOT EXISTS user_session_refresh_hash_uk
    ON nexora.user_session(refresh_token_hash)
    WHERE refresh_token_hash IS NOT NULL;

-- Accès rapide à la session par hash (endpoint POST /auth/refresh).
CREATE INDEX IF NOT EXISTS user_session_refresh_lookup_idx
    ON nexora.user_session(refresh_token_hash)
    WHERE refresh_token_hash IS NOT NULL AND refresh_used_at IS NULL;

-- ===================== 3. Commentaires ======================================
COMMENT ON COLUMN nexora.user_session.refresh_token_hash IS
    'SHA-256 hex du refresh token opaque remis au client lors de la connexion. '
    'NULL pour les sessions legacy (avant V078) et les sessions clé d''API.';

COMMENT ON COLUMN nexora.user_session.refresh_expires_at IS
    'Expiration du refresh token, calculée au moment de l''émission '
    '(Jwt:RefreshTokenDays jours après issued_at). NULL = session sans refresh.';

COMMENT ON COLUMN nexora.user_session.refresh_used_at IS
    'Horodatage de la dernière rotation. NOT NULL signifie que ce token a déjà '
    'servi à émettre un nouveau couple (JWT + refresh). Une tentative de réutilisation '
    'd''un token consommé déclenche la révocation de TOUTES les sessions de l''utilisateur '
    '(indicateur de vol de token — REQ-03.5).';
