-- =============================================================================
-- NEXORA — V034 — Cohérence du modèle de données (Phase 17 §51, §58)
-- Rend explicites au niveau base deux règles de cohérence critiques déjà tenues
-- par le domaine, en défense en profondeur :
--   §58 — « Un mouvement de stock doit avoir une cause identifiable »
--   §58 — « Un paiement doit avoir une source ou un contexte financier valide »
-- Toutes les écritures existantes du domaine et des jeux de test/démo les respectent.
-- =============================================================================
SET search_path TO nexora, public;

-- --- §16, §58 : tout mouvement de stock porte un reference_type ---------------
ALTER TABLE nexora.stock_movement
    ADD CONSTRAINT stock_movement_cause_chk CHECK (reference_type IS NOT NULL);

-- --- §30, §58 : un paiement est rattaché à un tiers ou à un compte financier --
ALTER TABLE nexora.payment
    ADD CONSTRAINT payment_context_chk CHECK (
        financial_account_id IS NOT NULL
        OR customer_id IS NOT NULL
        OR supplier_id IS NOT NULL
        OR cash_session_id IS NOT NULL);

-- --- §52 : correction du garde-fou de suppression physique -------------------
-- Les tables « toujours verrouillées » (stock_movement, receivable, payable) n'ont pas de
-- colonne `status` : l'accès direct OLD.status faisait échouer la fonction sur un message
-- technique au lieu du message métier. Lecture via to_jsonb(OLD) → robuste quel que soit le type.
CREATE OR REPLACE FUNCTION nexora.forbid_hard_delete_validated()
RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE
    v_status text := to_jsonb(OLD)->>'status';
    v_locked boolean := false;
BEGIN
    IF nexora.flag('allow_hard_delete') THEN
        RETURN OLD;
    END IF;

    v_locked := CASE TG_TABLE_NAME
        WHEN 'sale'                 THEN v_status IN ('completed','partially_refunded','refunded')
        WHEN 'payment'              THEN v_status = 'completed'
        WHEN 'supplier_invoice'     THEN v_status IN ('validated','partially_paid','paid')
        WHEN 'goods_receipt'        THEN v_status = 'posted'
        WHEN 'customer_receivable'  THEN true
        WHEN 'supplier_payable'     THEN true
        WHEN 'financial_transaction' THEN v_status = 'cleared'
        WHEN 'stock_movement'       THEN true
        ELSE false
    END;

    IF v_locked THEN
        RAISE EXCEPTION '% % validé(e) : suppression physique interdite (annulation/extourne requise)',
            TG_TABLE_NAME, OLD.id
            USING ERRCODE = 'check_violation';
    END IF;
    RETURN OLD;
END;
$$;
