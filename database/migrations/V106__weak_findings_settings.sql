-- =============================================================================
-- NEXORA — V106 — Réglages issus des constats faibles de l'audit (audit 2026-10 éd. 2, lot 7)
--
-- * accounting.manual_entry_four_eyes : une écriture manuelle (OD) doit être validée par une autre personne que son
--   auteur. Désactivé par défaut (décision du propriétaire du 2026-10-17) : les structures à un seul comptable restent
--   possibles ; une entreprise qui le souhaite l'active.
-- Idempotente.
-- =============================================================================

INSERT INTO nexora.setting_definition (key, category, value_type, default_value, allowed_values, max_scope, is_sensitive, affects_future, description) VALUES
    ('accounting.manual_entry_four_eyes', 'accounting', 'boolean', 'false', '[]', 'company', true, true,
     'Une écriture manuelle doit être validée par une autre personne que son auteur')
ON CONFLICT (key) DO NOTHING;
