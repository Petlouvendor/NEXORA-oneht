-- =============================================================================
-- NEXORA — V094 — Double authentification imposée pour les actions critiques (finalisation produit, phase 17)
--
-- La liste mfa_required_roles restait indicative (choix de la phase 47). Ce réglage, désactivé par défaut,
-- permet à l'entreprise de la rendre bloquante : un rôle concerné sans double authentification ne peut
-- plus confirmer d'action critique (paiement, annulation, validation comptable, droits, suppression,
-- données sensibles). Si la double authentification est active, son code est toujours exigé.
-- Idempotente.
-- =============================================================================
ALTER TABLE nexora.security_policy
    ADD COLUMN IF NOT EXISTS enforce_mfa_for_critical boolean NOT NULL DEFAULT false;
