-- =============================================================================
-- NEXORA — V063 — Recette (Phase 55) : déconnexion réelle & réinitialisation de mot de passe
--
-- La grille de recette a révélé deux écarts d'authentification :
--   AUTH-004 — la déconnexion ne faisait que vider le navigateur ; la session serveur restait
--              active et le jeton restait utilisable jusqu'à son expiration. Plus largement,
--              révoquer UNE session (écran Sessions, Phase 11) ne coupait pas son jeton : seul
--              le « tout déconnecter » (security_epoch) le faisait.
--   AUTH-007/008 — aucun changement ni réinitialisation de mot de passe n'existait.
--
-- Correctif : chaque jeton porte désormais un identifiant (claim `jti`) enregistré sur la
-- session ; le middleware refuse un jeton dont la session est révoquée. Les demandes de
-- réinitialisation sont stockées hachées, à usage unique et à durée courte.
-- =============================================================================
SET search_path TO nexora, public;

-- ===================== 1. Lien jeton ↔ session =================================================
ALTER TABLE nexora.user_session ADD COLUMN token_id uuid;
CREATE UNIQUE INDEX user_session_token_uk ON nexora.user_session (token_id) WHERE token_id IS NOT NULL;

-- ===================== 2. Réinitialisation de mot de passe (AUTH-008) =========================
-- Le jeton n'est jamais stocké en clair (SHA-256, comme step_up_challenge / mfa_challenge).
CREATE TABLE nexora.password_reset (
    id           uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    user_id      uuid        NOT NULL REFERENCES nexora.app_user(id) ON DELETE CASCADE,
    token_hash   text        NOT NULL,
    expires_at   timestamptz NOT NULL,
    consumed_at  timestamptz,
    requested_ip inet,
    created_at   timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT password_reset_token_uk UNIQUE (token_hash)
);
CREATE INDEX password_reset_user_idx ON nexora.password_reset (user_id, created_at DESC);

-- Isolation par utilisateur, comme user_session / mfa_challenge. Le parcours « mot de passe
-- oublié » est anonyme : le service passe explicitement en portée système pour ces seules lignes.
ALTER TABLE nexora.password_reset ENABLE ROW LEVEL SECURITY;
ALTER TABLE nexora.password_reset FORCE ROW LEVEL SECURITY;
CREATE POLICY password_reset_isolation ON nexora.password_reset
    USING (nexora.rls_bypass() OR user_id = nexora.current_user_id())
    WITH CHECK (nexora.rls_bypass() OR user_id = nexora.current_user_id());

-- Aucune nouvelle permission : changer SON mot de passe et se déconnecter sont des droits de
-- tout utilisateur authentifié ; créer un utilisateur réutilise `admin.manage_users`, créer un
-- client / fournisseur réutilise `customers.*` / `suppliers.*` (déjà amorcées, jamais branchées).
