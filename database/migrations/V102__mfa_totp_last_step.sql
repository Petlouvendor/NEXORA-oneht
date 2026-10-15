-- =============================================================================
-- NEXORA — V102 — Anti-rejeu des codes TOTP (audit 2026-10, A-011)
--
-- Un code TOTP restait accepté pendant toute sa fenêtre de validité (pas courant ± 1, jusqu'à 90 s) : un code
-- observé (épaule, enregistrement d'écran, hameçonnage en temps réel) pouvait servir une seconde fois. On retient le
-- dernier pas de temps accepté par utilisateur ; un code n'est accepté que si son pas le suit.
-- NULL : aucun code accepté depuis l'activation (ou MFA réinitialisée).
-- Idempotente.
-- =============================================================================

ALTER TABLE nexora.app_user ADD COLUMN IF NOT EXISTS mfa_last_step bigint;

COMMENT ON COLUMN nexora.app_user.mfa_last_step IS
    'Dernier pas de temps TOTP (compteur RFC 6238) accepté : un code de ce pas ou d''un pas antérieur est refusé.';
