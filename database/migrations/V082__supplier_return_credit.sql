-- =============================================================================
-- NEXORA — V082 — Retour fournisseur imputé sur la dette (finalisation produit, phase 3)
--
-- Défaut corrigé : un retour « sur dette » débitait le compte Fournisseurs en comptabilité
-- (règle SupplierReturnRecorded, AgainstPayable) sans diminuer la dette elle-même : le grand
-- livre et la balance fournisseurs divergeaient, et la dette pouvait être payée en totalité
-- (surpaiement de la marchandise retournée).
--
-- 1. supplier_return.payable_id : la dette sur laquelle le retour est imputé.
-- 2. supplier_payable.credited_amount : total des retours comptabilisés imputés sur la dette.
--    remaining_amount = original − payé − avoirs (colonne générée recréée).
-- 3. nexora.recompute_supplier_payable(id) : seule écriture de paid_amount / credited_amount /
--    status ; appelée par les affectations de paiement ET par les retours.
-- Idempotente.
-- =============================================================================
ALTER TABLE nexora.supplier_return ADD COLUMN IF NOT EXISTS payable_id uuid;
ALTER TABLE nexora.supplier_payable ADD COLUMN IF NOT EXISTS credited_amount nexora.money_amount NOT NULL DEFAULT 0;

DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'supplier_return_payable_fk') THEN
        ALTER TABLE nexora.supplier_return ADD CONSTRAINT supplier_return_payable_fk
            FOREIGN KEY (payable_id, company_id) REFERENCES nexora.supplier_payable (id, company_id);
    END IF;

    -- Le reste à payer tient compte des avoirs : la colonne générée est recréée si besoin.
    IF NOT EXISTS (
        SELECT 1 FROM information_schema.columns
         WHERE table_schema = 'nexora' AND table_name = 'supplier_payable' AND column_name = 'remaining_amount'
           AND generation_expression LIKE '%credited_amount%') THEN
        ALTER TABLE nexora.supplier_payable DROP COLUMN IF EXISTS remaining_amount;
        ALTER TABLE nexora.supplier_payable ADD COLUMN remaining_amount nexora.money_amount
            GENERATED ALWAYS AS (original_amount::numeric - paid_amount::numeric - credited_amount::numeric) STORED;
    END IF;

    IF EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'supplier_payable_paid_chk') THEN
        ALTER TABLE nexora.supplier_payable DROP CONSTRAINT supplier_payable_paid_chk;
    END IF;
    ALTER TABLE nexora.supplier_payable ADD CONSTRAINT supplier_payable_paid_chk
        CHECK (paid_amount::numeric >= 0 AND credited_amount::numeric >= 0
               AND paid_amount::numeric + credited_amount::numeric <= original_amount::numeric);
END;
$$;

CREATE INDEX IF NOT EXISTS supplier_return_payable_idx ON nexora.supplier_return (payable_id) WHERE payable_id IS NOT NULL;

COMMENT ON COLUMN nexora.supplier_payable.credited_amount IS 'Total des retours fournisseur comptabilisés imputés sur cette dette (avoirs).';

CREATE OR REPLACE FUNCTION nexora.recompute_supplier_payable(p_payable_id uuid)
RETURNS void LANGUAGE plpgsql AS $$
DECLARE
    v_paid     numeric;
    v_credited numeric;
    v_orig     numeric;
BEGIN
    SELECT coalesce(sum(allocated_amount), 0) INTO v_paid
      FROM nexora.payment_allocation WHERE payable_id = p_payable_id AND voided_at IS NULL;
    SELECT coalesce(sum(total), 0) INTO v_credited
      FROM nexora.supplier_return WHERE payable_id = p_payable_id AND status = 'posted';
    SELECT original_amount INTO v_orig FROM nexora.supplier_payable WHERE id = p_payable_id;
    IF v_paid + v_credited > v_orig THEN
        RAISE EXCEPTION 'Règlements (%) + avoirs (%) > montant de la dette (%)', v_paid, v_credited, v_orig
            USING ERRCODE = 'check_violation';
    END IF;
    UPDATE nexora.supplier_payable
       SET paid_amount = v_paid,
           credited_amount = v_credited,
           status = CASE
               WHEN v_paid + v_credited = 0     THEN 'OPEN'
               WHEN v_paid + v_credited < v_orig THEN 'PARTIALLY_PAID'
               ELSE 'PAID' END,
           updated_at = now()
     WHERE id = p_payable_id AND status <> 'CANCELLED';
END;
$$;

-- Remplace V073 : la branche « dette » délègue au recalcul commun (paiements + avoirs).
CREATE OR REPLACE FUNCTION nexora.payment_allocation_apply()
RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE
    v_payment_id    uuid := coalesce(NEW.payment_id, OLD.payment_id);
    v_receivable_id uuid := coalesce(NEW.receivable_id, OLD.receivable_id);
    v_payable_id    uuid := coalesce(NEW.payable_id, OLD.payable_id);
    v_alloc_total   nexora.money_amount;
    v_pay_amount    nexora.money_amount;
    v_pay_ccy       uuid;
    v_target_ccy    uuid;
BEGIN
    IF TG_OP <> 'DELETE' THEN
        SELECT amount, currency_id INTO v_pay_amount, v_pay_ccy FROM nexora.payment WHERE id = NEW.payment_id;
        IF NEW.currency_id <> v_pay_ccy THEN
            RAISE EXCEPTION 'Devise d''affectation <> devise du paiement' USING ERRCODE = 'check_violation';
        END IF;
        IF v_receivable_id IS NOT NULL THEN
            SELECT currency_id INTO v_target_ccy FROM nexora.customer_receivable WHERE id = v_receivable_id;
        ELSE
            SELECT currency_id INTO v_target_ccy FROM nexora.supplier_payable WHERE id = v_payable_id;
        END IF;
        IF NEW.currency_id <> v_target_ccy THEN
            RAISE EXCEPTION 'Devise d''affectation <> devise de la créance/dette' USING ERRCODE = 'check_violation';
        END IF;
    END IF;

    SELECT coalesce(sum(allocated_amount), 0) INTO v_alloc_total
    FROM nexora.payment_allocation WHERE payment_id = v_payment_id AND voided_at IS NULL;
    SELECT amount INTO v_pay_amount FROM nexora.payment WHERE id = v_payment_id;
    IF v_alloc_total > v_pay_amount THEN
        RAISE EXCEPTION 'Affectations (%) > montant du paiement (%)', v_alloc_total, v_pay_amount
            USING ERRCODE = 'check_violation';
    END IF;
    UPDATE nexora.payment SET allocated_amount = v_alloc_total, updated_at = now() WHERE id = v_payment_id;

    IF v_receivable_id IS NOT NULL THEN
        PERFORM nexora.recompute_customer_receivable(v_receivable_id);
    END IF;
    IF v_payable_id IS NOT NULL THEN
        PERFORM nexora.recompute_supplier_payable(v_payable_id);
    END IF;

    RETURN coalesce(NEW, OLD);
END;
$$;

-- Un retour comptabilisé (ou annulé) sur une dette recalcule cette dette.
CREATE OR REPLACE FUNCTION nexora.supplier_return_apply_credit()
RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF TG_OP = 'UPDATE' AND OLD.payable_id IS NOT NULL AND OLD.payable_id IS DISTINCT FROM NEW.payable_id THEN
        PERFORM nexora.recompute_supplier_payable(OLD.payable_id);
    END IF;
    IF NEW.payable_id IS NOT NULL THEN
        PERFORM nexora.recompute_supplier_payable(NEW.payable_id);
    END IF;
    RETURN NEW;
END;
$$;

DROP TRIGGER IF EXISTS trg_supplier_return_credit ON nexora.supplier_return;
CREATE TRIGGER trg_supplier_return_credit
    AFTER INSERT OR UPDATE OF payable_id, status, total ON nexora.supplier_return
    FOR EACH ROW EXECUTE FUNCTION nexora.supplier_return_apply_credit();
