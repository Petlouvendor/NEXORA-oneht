-- =============================================================================
-- NEXORA — V070 — Résumé fonctionnel §13, §17 : cycle de vie des recommandations
-- New → Viewed → Accepted / Modified / Ignored → Executed / Expired.
-- Correspondance : Ignored = « dismissed », Executed = « resolved » (résultat constaté après action).
-- Ajout de l'état « viewed » : qui a consulté la recommandation, et quand.
-- =============================================================================
SET search_path TO nexora, public;

ALTER TABLE nexora.recommendation ADD COLUMN IF NOT EXISTS viewed_at timestamptz;
ALTER TABLE nexora.recommendation ADD COLUMN IF NOT EXISTS viewed_by uuid REFERENCES nexora.app_user(id);

ALTER TABLE nexora.recommendation DROP CONSTRAINT IF EXISTS recommendation_status_chk;
ALTER TABLE nexora.recommendation ADD CONSTRAINT recommendation_status_chk
    CHECK (status IN ('new', 'viewed', 'accepted', 'dismissed', 'snoozed', 'resolved', 'modified', 'expired'));
