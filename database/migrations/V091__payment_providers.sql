-- =============================================================================
-- NEXORA — V091 — Paiements réels / connecteurs (finalisation produit, phase 15)
--
--  * Fournisseur de paiement explicite : « simulated » (aucun service externe), « moncash »,
--    « card ». Les connecteurs de démonstration nommés « *-sim » sont reclassés en simulation.
--  * Identifiants fournisseur chiffrés au repos (AES-256-GCM, jamais relus par l'API).
--  * Résultat du dernier test de connexion : CONNECTED, AUTHENTICATION_FAILED, TIMEOUT, UNAVAILABLE,
--    NOT_CONFIGURED — le test répondait toujours « OK ».
--  * Mode de chaque intention de paiement : « simulation » ou « real ». Toutes les intentions
--    existantes venaient de l'adaptateur simulé : elles sont marquées « simulation ».
--  * Lien de paiement et numéro de commande transmis au fournisseur (MonCash : orderId).
-- Idempotente.
-- =============================================================================
ALTER TABLE nexora.integration_connector
    ADD COLUMN IF NOT EXISTS credentials_encrypted text,
    ADD COLUMN IF NOT EXISTS credentials_hint text,
    ADD COLUMN IF NOT EXISTS last_test_status text,
    ADD COLUMN IF NOT EXISTS last_test_message text,
    ADD COLUMN IF NOT EXISTS last_tested_at timestamptz;

DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'integration_connector_test_status_ck') THEN
        ALTER TABLE nexora.integration_connector ADD CONSTRAINT integration_connector_test_status_ck
            CHECK (last_test_status IS NULL OR last_test_status IN
                   ('CONNECTED','AUTHENTICATION_FAILED','TIMEOUT','UNAVAILABLE','NOT_CONFIGURED'));
    END IF;
END;
$$;

UPDATE nexora.integration_connector
SET provider = 'simulated'
WHERE connector_type = 'payment' AND provider <> 'simulated'
  AND (provider ILIKE '%sim%' OR code ILIKE '%-sim');

ALTER TABLE nexora.payment_intent
    ADD COLUMN IF NOT EXISTS mode text NOT NULL DEFAULT 'simulation',
    ADD COLUMN IF NOT EXISTS redirect_url text,
    ADD COLUMN IF NOT EXISTS provider_order_id text;

DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'payment_intent_mode_ck') THEN
        ALTER TABLE nexora.payment_intent ADD CONSTRAINT payment_intent_mode_ck CHECK (mode IN ('simulation','real'));
    END IF;
END;
$$;

-- Le défaut ne vaut que pour l'historique : chaque nouvelle intention déclare son mode.
ALTER TABLE nexora.payment_intent ALTER COLUMN mode DROP DEFAULT;

CREATE UNIQUE INDEX IF NOT EXISTS payment_intent_provider_order_uk
    ON nexora.payment_intent (connector_id, provider_order_id) WHERE provider_order_id IS NOT NULL;
