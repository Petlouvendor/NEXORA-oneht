-- =============================================================================
-- NEXORA — V086 — Recouvrement : cycle de la promesse et du litige (finalisation produit, phase 9)
--
-- Promesse : annulation motivée et traçable (qui a soldé / annulé, pourquoi) ; l'échéance est évaluée par le
-- planificateur (job credit.settle_promises) : tenue, partiellement tenue ou non tenue, sans saisie.
-- Litige : un seul litige ouvert par créance (deux litiges sur la même créance faussaient l'encours contesté).
-- Idempotente.
-- =============================================================================
ALTER TABLE nexora.customer_promise
    ADD COLUMN IF NOT EXISTS cancel_reason text,
    ADD COLUMN IF NOT EXISTS settled_by    uuid REFERENCES nexora.app_user(id);

DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'customer_promise_cancel_reason_chk') THEN
        ALTER TABLE nexora.customer_promise ADD CONSTRAINT customer_promise_cancel_reason_chk
            CHECK (status <> 'cancelled' OR length(btrim(coalesce(cancel_reason, ''))) > 0) NOT VALID;
    END IF;
END;
$$;

CREATE UNIQUE INDEX IF NOT EXISTS customer_dispute_one_open_per_receivable_uk
    ON nexora.customer_dispute (receivable_id)
    WHERE receivable_id IS NOT NULL AND status IN ('open', 'under_review');

CREATE INDEX IF NOT EXISTS customer_promise_open_due_idx
    ON nexora.customer_promise (company_id, promised_date) WHERE status = 'open';

INSERT INTO nexora.job_definition (key, name, category, description, owner) VALUES
    ('credit.settle_promises', 'Échéance des promesses de paiement', 'business',
     'Classe chaque promesse arrivée à échéance (tenue, partiellement tenue, non tenue) d''après les règlements reçus depuis la promesse.',
     'scheduler')
ON CONFLICT (key) DO NOTHING;
