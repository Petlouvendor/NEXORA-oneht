-- =============================================================================
-- NEXORA — V019 — Intégrité financière et comptable (§20, §28 CDC ; §31, §36)
-- Règles non exprimables par une simple contrainte : triggers.
--   • Débit total = crédit total (écritures validées)
--   • Comptes postables uniquement
--   • Périodes clôturées protégées
--   • Écritures validées immuables (correction par extourne)
--   • Affectation de paiement ≤ montant disponible / solde ; soldes ≥ 0
--   • Maintien stock_balance ↔ stock_movement
--   • Pas de suppression physique d'une transaction validée
--   • Journal d'audit des opérations sensibles
-- =============================================================================
SET search_path TO nexora, public;

-- --- Helpers de contexte ------------------------------------------------
CREATE OR REPLACE FUNCTION nexora.current_user_id()
RETURNS uuid LANGUAGE plpgsql STABLE AS $$
BEGIN
    RETURN nullif(current_setting('nexora.user_id', true), '')::uuid;
EXCEPTION WHEN others THEN RETURN NULL;
END;
$$;

CREATE OR REPLACE FUNCTION nexora.flag(p_name text)
RETURNS boolean LANGUAGE sql STABLE AS $$
    SELECT coalesce(current_setting('nexora.' || p_name, true), 'off') = 'on';
$$;

-- =====================================================================
-- 1. Comptes postables uniquement (§20)
-- =====================================================================
CREATE OR REPLACE FUNCTION nexora.jel_account_must_be_postable()
RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE v_postable boolean;
BEGIN
    SELECT is_postable INTO v_postable FROM nexora.account WHERE id = NEW.account_id;
    IF v_postable IS DISTINCT FROM true THEN
        RAISE EXCEPTION 'Compte % non postable : écriture interdite', NEW.account_id
            USING ERRCODE = 'check_violation';
    END IF;
    RETURN NEW;
END;
$$;

CREATE TRIGGER trg_jel_account_postable
    BEFORE INSERT OR UPDATE OF account_id ON nexora.journal_entry_line
    FOR EACH ROW EXECUTE FUNCTION nexora.jel_account_must_be_postable();

-- =====================================================================
-- 2. Protection des périodes clôturées + cohérence entry_date (§9, §20, §31)
-- =====================================================================
CREATE OR REPLACE FUNCTION nexora.journal_entry_period_guard()
RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE
    v_status     text;
    v_start      date;
    v_end        date;
    v_period_id  uuid;
    v_date       date;
BEGIN
    IF TG_OP = 'DELETE' THEN
        v_period_id := OLD.accounting_period_id;
        v_date      := OLD.entry_date;
    ELSE
        v_period_id := NEW.accounting_period_id;
        v_date      := NEW.entry_date;
    END IF;

    SELECT status, start_date, end_date INTO v_status, v_start, v_end
    FROM nexora.accounting_period WHERE id = v_period_id;

    IF NOT FOUND THEN
        RETURN coalesce(NEW, OLD);
    END IF;

    IF TG_OP <> 'DELETE' AND (v_date < v_start OR v_date > v_end) THEN
        RAISE EXCEPTION 'entry_date % hors de la période comptable [% ; %]', v_date, v_start, v_end
            USING ERRCODE = 'check_violation';
    END IF;

    IF v_status IN ('closed','locked') AND NOT nexora.flag('allow_closed_period') THEN
        RAISE EXCEPTION 'Période comptable % : opération interdite (statut %)', v_period_id, v_status
            USING ERRCODE = 'check_violation',
                  HINT = 'SET LOCAL nexora.allow_closed_period = ''on'' pour un ajustement autorisé';
    END IF;

    RETURN coalesce(NEW, OLD);
END;
$$;

CREATE TRIGGER trg_journal_entry_period_guard
    BEFORE INSERT OR UPDATE OR DELETE ON nexora.journal_entry
    FOR EACH ROW EXECUTE FUNCTION nexora.journal_entry_period_guard();

-- =====================================================================
-- 3. Écriture validée : immuable, correction par extourne (§20)
-- =====================================================================
CREATE OR REPLACE FUNCTION nexora.journal_entry_immutability()
RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF TG_OP = 'DELETE' THEN
        IF OLD.status IN ('posted','reversed') AND NOT nexora.flag('allow_hard_delete') THEN
            RAISE EXCEPTION 'Écriture % validée : suppression interdite (extourne requise)', OLD.id
                USING ERRCODE = 'check_violation';
        END IF;
        RETURN OLD;
    END IF;

    IF OLD.status = 'posted' THEN
        -- seules transitions autorisées : posted → reversed / cancelled + métadonnées d'extourne
        IF NEW.status NOT IN ('posted','reversed','cancelled') THEN
            RAISE EXCEPTION 'Transition de statut interdite pour une écriture validée : % → %', OLD.status, NEW.status
                USING ERRCODE = 'check_violation';
        END IF;
        IF NEW.company_id            <> OLD.company_id
        OR NEW.journal_id            <> OLD.journal_id
        OR NEW.accounting_period_id  <> OLD.accounting_period_id
        OR NEW.entry_number          <> OLD.entry_number
        OR NEW.entry_date            <> OLD.entry_date THEN
            RAISE EXCEPTION 'Champs d''une écriture validée non modifiables (extourne requise)'
                USING ERRCODE = 'check_violation';
        END IF;
    END IF;
    RETURN NEW;
END;
$$;

CREATE TRIGGER trg_journal_entry_immutability
    BEFORE UPDATE OR DELETE ON nexora.journal_entry
    FOR EACH ROW EXECUTE FUNCTION nexora.journal_entry_immutability();

CREATE OR REPLACE FUNCTION nexora.journal_entry_line_immutability()
RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE v_status text;
BEGIN
    SELECT status INTO v_status FROM nexora.journal_entry
    WHERE id = coalesce(NEW.journal_entry_id, OLD.journal_entry_id);

    IF v_status = 'posted' AND NOT nexora.flag('allow_hard_delete') THEN
        RAISE EXCEPTION 'Lignes d''une écriture validée non modifiables (extourne requise)'
            USING ERRCODE = 'check_violation';
    END IF;
    RETURN coalesce(NEW, OLD);
END;
$$;

CREATE TRIGGER trg_journal_entry_line_immutability
    BEFORE UPDATE OR DELETE ON nexora.journal_entry_line
    FOR EACH ROW EXECUTE FUNCTION nexora.journal_entry_line_immutability();

-- =====================================================================
-- 4. Débit total = crédit total pour toute écriture validée (§20, §31)
--    Contrainte différée : vérifiée en fin de transaction.
-- =====================================================================
CREATE OR REPLACE FUNCTION nexora.assert_entry_balanced(p_entry uuid)
RETURNS void LANGUAGE plpgsql AS $$
DECLARE
    v_status  text;
    v_debit   nexora.money_amount;
    v_credit  nexora.money_amount;
    v_bdebit  nexora.money_amount;
    v_bcredit nexora.money_amount;
    v_lines   integer;
BEGIN
    SELECT status INTO v_status FROM nexora.journal_entry WHERE id = p_entry;
    IF NOT FOUND OR v_status <> 'posted' THEN
        RETURN;
    END IF;

    SELECT coalesce(sum(debit),0), coalesce(sum(credit),0),
           coalesce(sum(base_debit),0), coalesce(sum(base_credit),0), count(*)
    INTO v_debit, v_credit, v_bdebit, v_bcredit, v_lines
    FROM nexora.journal_entry_line WHERE journal_entry_id = p_entry;

    IF v_lines < 2 THEN
        RAISE EXCEPTION 'Écriture % : au moins 2 lignes requises', p_entry
            USING ERRCODE = 'check_violation';
    END IF;
    IF v_debit <> v_credit THEN
        RAISE EXCEPTION 'Écriture % non équilibrée : débit % <> crédit %', p_entry, v_debit, v_credit
            USING ERRCODE = 'check_violation';
    END IF;
    IF v_bdebit <> v_bcredit THEN
        RAISE EXCEPTION 'Écriture % non équilibrée en devise de référence : % <> %', p_entry, v_bdebit, v_bcredit
            USING ERRCODE = 'check_violation';
    END IF;
END;
$$;

CREATE OR REPLACE FUNCTION nexora.trg_entry_balanced_from_line()
RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    PERFORM nexora.assert_entry_balanced(coalesce(NEW.journal_entry_id, OLD.journal_entry_id));
    RETURN NULL;
END;
$$;

CREATE OR REPLACE FUNCTION nexora.trg_entry_balanced_from_header()
RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    PERFORM nexora.assert_entry_balanced(NEW.id);
    RETURN NULL;
END;
$$;

CREATE CONSTRAINT TRIGGER trg_je_balanced_line
    AFTER INSERT OR UPDATE OR DELETE ON nexora.journal_entry_line
    DEFERRABLE INITIALLY DEFERRED
    FOR EACH ROW EXECUTE FUNCTION nexora.trg_entry_balanced_from_line();

CREATE CONSTRAINT TRIGGER trg_je_balanced_header
    AFTER INSERT OR UPDATE OF status ON nexora.journal_entry
    DEFERRABLE INITIALLY DEFERRED
    FOR EACH ROW WHEN (NEW.status = 'posted')
    EXECUTE FUNCTION nexora.trg_entry_balanced_from_header();

-- =====================================================================
-- 5. Affectation des paiements ↔ créances / dettes (§13, §31)
-- =====================================================================
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
    v_orig          nexora.money_amount;
BEGIN
    -- cohérence de devise
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

    -- total affecté du paiement ≤ montant du paiement
    SELECT coalesce(sum(allocated_amount),0) INTO v_alloc_total
    FROM nexora.payment_allocation WHERE payment_id = v_payment_id;

    SELECT amount INTO v_pay_amount FROM nexora.payment WHERE id = v_payment_id;
    IF v_alloc_total > v_pay_amount THEN
        RAISE EXCEPTION 'Affectations (%) > montant du paiement (%)', v_alloc_total, v_pay_amount
            USING ERRCODE = 'check_violation';
    END IF;
    UPDATE nexora.payment SET allocated_amount = v_alloc_total, updated_at = now()
    WHERE id = v_payment_id;

    -- recalcul de la créance
    IF v_receivable_id IS NOT NULL THEN
        SELECT coalesce(sum(allocated_amount),0) INTO v_alloc_total
        FROM nexora.payment_allocation WHERE receivable_id = v_receivable_id;
        SELECT original_amount INTO v_orig FROM nexora.customer_receivable WHERE id = v_receivable_id;
        IF v_alloc_total > v_orig THEN
            RAISE EXCEPTION 'Règlements (%) > montant de la créance (%)', v_alloc_total, v_orig
                USING ERRCODE = 'check_violation';
        END IF;
        UPDATE nexora.customer_receivable
           SET paid_amount = v_alloc_total,
               status = CASE
                   WHEN v_alloc_total = 0        THEN 'OPEN'
                   WHEN v_alloc_total < v_orig   THEN 'PARTIALLY_PAID'
                   ELSE 'PAID' END,
               updated_at = now()
         WHERE id = v_receivable_id AND status <> 'CANCELLED';
    END IF;

    -- recalcul de la dette
    IF v_payable_id IS NOT NULL THEN
        SELECT coalesce(sum(allocated_amount),0) INTO v_alloc_total
        FROM nexora.payment_allocation WHERE payable_id = v_payable_id;
        SELECT original_amount INTO v_orig FROM nexora.supplier_payable WHERE id = v_payable_id;
        IF v_alloc_total > v_orig THEN
            RAISE EXCEPTION 'Règlements (%) > montant de la dette (%)', v_alloc_total, v_orig
                USING ERRCODE = 'check_violation';
        END IF;
        UPDATE nexora.supplier_payable
           SET paid_amount = v_alloc_total,
               status = CASE
                   WHEN v_alloc_total = 0        THEN 'OPEN'
                   WHEN v_alloc_total < v_orig   THEN 'PARTIALLY_PAID'
                   ELSE 'PAID' END,
               updated_at = now()
         WHERE id = v_payable_id AND status <> 'CANCELLED';
    END IF;

    RETURN coalesce(NEW, OLD);
END;
$$;

CREATE TRIGGER trg_payment_allocation_apply
    AFTER INSERT OR UPDATE OR DELETE ON nexora.payment_allocation
    FOR EACH ROW EXECUTE FUNCTION nexora.payment_allocation_apply();

-- --- Vente : amount_paid maintenu depuis sale_payment ---------------
CREATE OR REPLACE FUNCTION nexora.sale_payment_apply()
RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE
    v_sale_id uuid := coalesce(NEW.sale_id, OLD.sale_id);
    v_paid    nexora.money_amount;
BEGIN
    SELECT coalesce(sum(amount),0) INTO v_paid FROM nexora.sale_payment WHERE sale_id = v_sale_id;
    UPDATE nexora.sale SET amount_paid = v_paid, updated_at = now() WHERE id = v_sale_id;
    RETURN coalesce(NEW, OLD);
END;
$$;

CREATE TRIGGER trg_sale_payment_apply
    AFTER INSERT OR UPDATE OR DELETE ON nexora.sale_payment
    FOR EACH ROW EXECUTE FUNCTION nexora.sale_payment_apply();

-- =====================================================================
-- 6. Maintien du stock : stock_balance ↔ stock_movement (§36)
-- =====================================================================
CREATE OR REPLACE FUNCTION nexora.stock_movement_apply()
RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE
    v_old_qty  nexora.quantity;
    v_old_cost nexora.money_amount;
    v_new_qty  nexora.quantity;
    v_new_cost nexora.money_amount;
BEGIN
    SELECT quantity_on_hand, average_cost INTO v_old_qty, v_old_cost
    FROM nexora.stock_balance
    WHERE product_id = NEW.product_id AND inventory_location_id = NEW.inventory_location_id
    FOR UPDATE;

    IF NOT FOUND THEN
        v_old_qty := 0; v_old_cost := 0;
    END IF;

    v_new_qty := v_old_qty + NEW.quantity;

    IF NEW.quantity > 0 AND v_new_qty > 0 THEN
        v_new_cost := ((v_old_qty * v_old_cost) + (NEW.quantity * NEW.unit_cost)) / v_new_qty;
    ELSE
        v_new_cost := v_old_cost;
    END IF;

    INSERT INTO nexora.stock_balance AS sb
        (product_id, inventory_location_id, company_id, quantity_on_hand, average_cost)
    VALUES (NEW.product_id, NEW.inventory_location_id, NEW.company_id, v_new_qty, v_new_cost)
    ON CONFLICT (product_id, inventory_location_id) DO UPDATE
        SET quantity_on_hand = EXCLUDED.quantity_on_hand,
            average_cost     = EXCLUDED.average_cost,
            updated_at       = now();

    IF NEW.lot_id IS NOT NULL THEN
        INSERT INTO nexora.lot_stock AS ls
            (lot_id, inventory_location_id, company_id, quantity_on_hand)
        VALUES (NEW.lot_id, NEW.inventory_location_id, NEW.company_id, GREATEST(NEW.quantity, 0))
        ON CONFLICT (lot_id, inventory_location_id) DO UPDATE
            SET quantity_on_hand = ls.quantity_on_hand + NEW.quantity,
                updated_at       = now();
    END IF;

    RETURN NEW;
END;
$$;

CREATE TRIGGER trg_stock_movement_apply
    AFTER INSERT ON nexora.stock_movement
    FOR EACH ROW EXECUTE FUNCTION nexora.stock_movement_apply();

-- =====================================================================
-- 7. Interdiction de suppression physique d'une transaction validée (§28, §29)
-- =====================================================================
CREATE OR REPLACE FUNCTION nexora.forbid_hard_delete_validated()
RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE
    v_locked boolean := false;
BEGIN
    IF nexora.flag('allow_hard_delete') THEN
        RETURN OLD;
    END IF;

    v_locked := CASE TG_TABLE_NAME
        WHEN 'sale'             THEN OLD.status IN ('completed','partially_refunded','refunded')
        WHEN 'payment'          THEN OLD.status = 'completed'
        WHEN 'supplier_invoice' THEN OLD.status IN ('validated','partially_paid','paid')
        WHEN 'goods_receipt'    THEN OLD.status = 'posted'
        WHEN 'customer_receivable' THEN true
        WHEN 'supplier_payable'    THEN true
        WHEN 'financial_transaction' THEN OLD.status = 'cleared'
        WHEN 'stock_movement'   THEN true
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

CREATE TRIGGER trg_forbid_delete_sale
    BEFORE DELETE ON nexora.sale
    FOR EACH ROW EXECUTE FUNCTION nexora.forbid_hard_delete_validated();
CREATE TRIGGER trg_forbid_delete_payment
    BEFORE DELETE ON nexora.payment
    FOR EACH ROW EXECUTE FUNCTION nexora.forbid_hard_delete_validated();
CREATE TRIGGER trg_forbid_delete_supplier_invoice
    BEFORE DELETE ON nexora.supplier_invoice
    FOR EACH ROW EXECUTE FUNCTION nexora.forbid_hard_delete_validated();
CREATE TRIGGER trg_forbid_delete_goods_receipt
    BEFORE DELETE ON nexora.goods_receipt
    FOR EACH ROW EXECUTE FUNCTION nexora.forbid_hard_delete_validated();
CREATE TRIGGER trg_forbid_delete_receivable
    BEFORE DELETE ON nexora.customer_receivable
    FOR EACH ROW EXECUTE FUNCTION nexora.forbid_hard_delete_validated();
CREATE TRIGGER trg_forbid_delete_payable
    BEFORE DELETE ON nexora.supplier_payable
    FOR EACH ROW EXECUTE FUNCTION nexora.forbid_hard_delete_validated();
CREATE TRIGGER trg_forbid_delete_fin_txn
    BEFORE DELETE ON nexora.financial_transaction
    FOR EACH ROW EXECUTE FUNCTION nexora.forbid_hard_delete_validated();
CREATE TRIGGER trg_forbid_delete_stock_movement
    BEFORE DELETE ON nexora.stock_movement
    FOR EACH ROW EXECUTE FUNCTION nexora.forbid_hard_delete_validated();

-- =====================================================================
-- 8. Journal d'audit des opérations sensibles (§23, §25)
-- =====================================================================
CREATE OR REPLACE FUNCTION nexora.audit_row()
RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE
    v_company uuid;
    v_old     jsonb;
    v_new     jsonb;
BEGIN
    IF TG_OP = 'INSERT' THEN
        v_new := to_jsonb(NEW); v_company := (v_new ->> 'company_id')::uuid;
    ELSIF TG_OP = 'UPDATE' THEN
        v_old := to_jsonb(OLD); v_new := to_jsonb(NEW);
        v_company := coalesce((v_new ->> 'company_id')::uuid, (v_old ->> 'company_id')::uuid);
    ELSE
        v_old := to_jsonb(OLD); v_company := (v_old ->> 'company_id')::uuid;
    END IF;

    INSERT INTO nexora.audit_log
        (company_id, user_id, action, entity_type, entity_id, old_values, new_values, context)
    VALUES
        (v_company, nexora.current_user_id(), TG_OP, TG_TABLE_NAME,
         coalesce((v_new ->> 'id')::uuid, (v_old ->> 'id')::uuid),
         v_old, v_new,
         jsonb_build_object('txid', txid_current(), 'at', now()));

    RETURN coalesce(NEW, OLD);
END;
$$;

DO $$
DECLARE
    t text;
    sensitive text[] := ARRAY[
        'journal_entry','journal_entry_line','payment','payment_allocation',
        'customer_receivable','supplier_payable','sale','supplier_invoice',
        'expense','accounting_period','goods_receipt','financial_transaction',
        'product_price','role_permission','user_role'];
BEGIN
    FOREACH t IN ARRAY sensitive LOOP
        EXECUTE format(
            'CREATE TRIGGER trg_audit_%1$s AFTER INSERT OR UPDATE OR DELETE ON nexora.%1$I
               FOR EACH ROW EXECUTE FUNCTION nexora.audit_row()', t);
    END LOOP;
END;
$$;

SELECT nexora.attach_common_triggers();
