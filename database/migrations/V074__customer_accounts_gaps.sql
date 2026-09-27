-- =============================================================================
-- NEXORA — V074 — Module « Gestion clients & comptes clients » : correction des écarts de V073
-- (docs/module-clients-comptes-clients-implementation.md §6, demande du 2026-09-23 : « les écarts
-- doivent être corrigés, suivre la consigne du document du module »).
--
--   1. Échéancier d'un crédit (§8 « échéance ou calendrier », §20 credit_installments).
--   2. Création d'un crédit hors vente — reprise de solde (§22 POST /customers/{id}/credits).
--   3. Avoir utilisable comme moyen de paiement à la caisse (§7, §15 « crédit utilisable ») :
--      aucune table — l'avoir s'impute sur le crédit né de la vente (customer_credit_application).
--   4. Dépassement de limite par le workflow d'approbation configuré (§12) : approval_request
--      (V016, type credit_sale, niveaux selon approval_policy) porte le contexte et sa consommation.
--   5. Devise explicite (§21) : conversion au dernier taux connu, jamais un mélange de devises.
--   6. Versement hors ligne (§24) : autorisé ou restreint par la politique hors ligne.
--   7. Retours antérieurs à V073 sur des ventes client : avoirs et imputations reconstitués.
-- =============================================================================
SET search_path TO nexora, public;

-- =====================================================================
-- 1. Échéancier
-- =====================================================================
-- Un échéancier répartit le reste dû au moment où il est posé (paid_before = déjà réglé alors) ;
-- un nouvel échéancier remplace le précédent sans l'effacer (versionné, audité).
CREATE TABLE nexora.credit_schedule (
    id            uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    company_id    uuid NOT NULL REFERENCES nexora.company(id),
    receivable_id uuid NOT NULL,
    version       integer NOT NULL,
    paid_before   nexora.money_amount NOT NULL,
    total_amount  nexora.money_amount NOT NULL,
    reason        text NOT NULL,
    status        text NOT NULL DEFAULT 'active',
    created_by    uuid NOT NULL REFERENCES nexora.app_user(id),
    created_at    timestamptz NOT NULL DEFAULT now(),
    replaced_at   timestamptz,
    replaced_by   uuid REFERENCES nexora.app_user(id),
    CONSTRAINT credit_schedule_company_id_uk UNIQUE (id, company_id),
    CONSTRAINT credit_schedule_version_uk UNIQUE (receivable_id, version),
    CONSTRAINT credit_schedule_receivable_fk FOREIGN KEY (receivable_id, company_id)
        REFERENCES nexora.customer_receivable (id, company_id),
    CONSTRAINT credit_schedule_status_chk CHECK (status IN ('active', 'replaced')),
    CONSTRAINT credit_schedule_amount_chk CHECK (total_amount > 0 AND paid_before >= 0),
    CONSTRAINT credit_schedule_reason_chk CHECK (length(btrim(reason)) > 0)
);
CREATE UNIQUE INDEX credit_schedule_active_uk ON nexora.credit_schedule (receivable_id) WHERE status = 'active';

CREATE TABLE nexora.credit_installment (
    id                 uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    company_id         uuid NOT NULL REFERENCES nexora.company(id),
    schedule_id        uuid NOT NULL,
    receivable_id      uuid NOT NULL,
    installment_number integer NOT NULL,
    due_date           date NOT NULL,
    amount             nexora.money_amount NOT NULL,
    created_at         timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT credit_installment_number_uk UNIQUE (schedule_id, installment_number),
    CONSTRAINT credit_installment_schedule_fk FOREIGN KEY (schedule_id, company_id)
        REFERENCES nexora.credit_schedule (id, company_id),
    CONSTRAINT credit_installment_receivable_fk FOREIGN KEY (receivable_id, company_id)
        REFERENCES nexora.customer_receivable (id, company_id),
    CONSTRAINT credit_installment_amount_chk CHECK (amount > 0),
    CONSTRAINT credit_installment_number_chk CHECK (installment_number > 0)
);
CREATE INDEX credit_installment_receivable_idx ON nexora.credit_installment (receivable_id, due_date);

-- Les lignes d'un échéancier ne se modifient pas : on en pose un nouveau.
CREATE OR REPLACE FUNCTION nexora.credit_installment_immutable()
RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    RAISE EXCEPTION 'Une échéance ne se modifie pas : posez un nouvel échéancier (%)', TG_OP
        USING ERRCODE = 'insufficient_privilege';
END;
$$;
CREATE TRIGGER trg_credit_installment_immutable BEFORE UPDATE OR DELETE ON nexora.credit_installment
    FOR EACH ROW EXECUTE FUNCTION nexora.credit_installment_immutable();

-- Recalcul d'un crédit (remplace V073) : en plus du réglé et du statut, l'échéance devient celle de
-- la PREMIÈRE échéance non couverte de l'échéancier actif — balance âgée, relances, contrôle de
-- crédit et alertes lisent donc la prochaine échéance réellement due, sans code supplémentaire.
CREATE OR REPLACE FUNCTION nexora.recompute_customer_receivable(p_receivable_id uuid)
RETURNS void LANGUAGE plpgsql AS $$
DECLARE
    v_orig     nexora.money_amount;
    v_settled  nexora.money_amount;
    v_schedule uuid;
    v_before   nexora.money_amount;
    v_next     date;
BEGIN
    SELECT original_amount INTO v_orig FROM nexora.customer_receivable WHERE id = p_receivable_id;
    IF NOT FOUND THEN RETURN; END IF;

    v_settled := coalesce((SELECT sum(allocated_amount) FROM nexora.payment_allocation
                           WHERE receivable_id = p_receivable_id AND voided_at IS NULL), 0)
               + coalesce((SELECT sum(amount) FROM nexora.customer_credit_application
                           WHERE receivable_id = p_receivable_id AND voided_at IS NULL), 0)
               + coalesce((SELECT sum(amount) FROM nexora.receivable_write_off
                           WHERE receivable_id = p_receivable_id), 0);
    IF v_settled > v_orig THEN
        RAISE EXCEPTION 'Règlements (%) > montant de la créance (%)', v_settled, v_orig
            USING ERRCODE = 'check_violation';
    END IF;

    SELECT id, paid_before INTO v_schedule, v_before
    FROM nexora.credit_schedule WHERE receivable_id = p_receivable_id AND status = 'active';
    IF v_schedule IS NOT NULL THEN
        SELECT due_date INTO v_next FROM (
            SELECT due_date, installment_number,
                   sum(amount) OVER (ORDER BY installment_number) AS cum
            FROM nexora.credit_installment WHERE schedule_id = v_schedule) s
        WHERE v_before + s.cum > v_settled
        ORDER BY installment_number LIMIT 1;
        IF v_next IS NULL THEN
            SELECT max(due_date) INTO v_next FROM nexora.credit_installment WHERE schedule_id = v_schedule;
        END IF;
    END IF;

    UPDATE nexora.customer_receivable
       SET paid_amount = v_settled,
           status = CASE
               WHEN status IN ('CANCELLED', 'WRITTEN_OFF') THEN status
               WHEN v_settled >= v_orig THEN 'PAID'
               WHEN status = 'IN_DISPUTE' THEN 'IN_DISPUTE'
               WHEN v_settled = 0 THEN 'OPEN'
               ELSE 'PARTIALLY_PAID' END,
           due_date = coalesce(v_next, due_date),
           updated_at = now()
     WHERE id = p_receivable_id;
END;
$$;

-- =====================================================================
-- 2. Création d'un crédit hors vente (reprise de solde) — permission dédiée
-- =====================================================================
INSERT INTO nexora.permission (code, name, module, action) VALUES
    ('credit.create', 'Crédit — créer un crédit hors vente (reprise de solde)', 'credit', 'create')
ON CONFLICT (code) DO NOTHING;
INSERT INTO nexora.role_permission (role_id, permission_id)
SELECT r.id, p.id FROM nexora.role r JOIN nexora.permission p ON p.code = 'credit.create'
WHERE r.company_id IS NULL AND r.name IN ('chief_accountant', 'owner_direction', 'super_administrator', 'group_administrator')
ON CONFLICT DO NOTHING;

-- =====================================================================
-- 3. Avoir utilisé en paiement : l'utilisation porte l'opération qui l'a produite (idempotence),
--    y compris quand aucune écriture n'est nécessaire (avoir déjà porté par le compte clients).
-- =====================================================================
ALTER TABLE nexora.customer_credit_application ADD COLUMN operation_uuid uuid;
CREATE INDEX customer_credit_application_operation_idx ON nexora.customer_credit_application (operation_uuid)
    WHERE operation_uuid IS NOT NULL;

-- =====================================================================
-- 4. Dépassement de crédit par le workflow d'approbation configuré (V016 / Phase 11)
-- =====================================================================
ALTER TABLE nexora.approval_request
    ADD COLUMN context          jsonb NOT NULL DEFAULT '{}'::jsonb,
    ADD COLUMN consumed_at      timestamptz,
    ADD COLUMN consumed_by_id   uuid;
CREATE INDEX approval_request_override_idx ON nexora.approval_request (company_id, reference_id)
    WHERE reference_type = 'pos_credit_override';

-- =====================================================================
-- 5. Devise explicite : conversion au dernier taux connu (même règle que le moteur financier)
-- =====================================================================
-- NULL quand aucun taux n'existe : l'appelant doit refuser, jamais additionner deux devises.
CREATE OR REPLACE FUNCTION nexora.convert_amount(p_amount numeric, p_from uuid, p_to uuid, p_at timestamptz DEFAULT now())
RETURNS numeric LANGUAGE plpgsql STABLE AS $$
DECLARE
    v_rate numeric;
BEGIN
    IF p_amount IS NULL THEN RETURN NULL; END IF;
    IF p_from = p_to THEN RETURN p_amount; END IF;
    SELECT rate INTO v_rate FROM nexora.exchange_rate
    WHERE base_currency_id = p_from AND quote_currency_id = p_to AND effective_at <= p_at
    ORDER BY effective_at DESC LIMIT 1;
    IF FOUND THEN RETURN p_amount * v_rate; END IF;
    SELECT rate INTO v_rate FROM nexora.exchange_rate
    WHERE base_currency_id = p_to AND quote_currency_id = p_from AND effective_at <= p_at
    ORDER BY effective_at DESC LIMIT 1;
    IF FOUND AND v_rate > 0 THEN RETURN p_amount / v_rate; END IF;
    RETURN NULL;
END;
$$;

-- =====================================================================
-- 6. Versement client hors ligne (§24)
-- =====================================================================
ALTER TABLE nexora.offline_policy
    ADD COLUMN allow_offline_customer_payment boolean NOT NULL DEFAULT true;

-- =====================================================================
-- 7. Retours antérieurs à V073 : avoirs et imputations reconstitués
-- =====================================================================
-- Un avoir peut désormais être porté par le compte clients lui-même : c'est le cas des anciens
-- retours « original / credit_note », dont l'écriture a crédité le compte clients (4111).
ALTER TABLE nexora.customer_credit_memo DROP CONSTRAINT customer_credit_memo_gl_chk;
ALTER TABLE nexora.customer_credit_memo ADD CONSTRAINT customer_credit_memo_gl_chk
    CHECK (gl_account_key IN ('store_credit', 'suspense', 'accounts_receivable'));

WITH old AS (
    SELECT sr.id AS return_id, sr.company_id, s.customer_id, sr.currency_id, sr.refund_total,
           sr.refund_method, sr.document_number, sr.reason, s.establishment_id, sr.created_by,
           CASE WHEN sr.refund_method = 'store_credit' THEN 'store_credit' ELSE 'accounts_receivable' END AS gl_key
    FROM nexora.sale_return sr
    JOIN nexora.sale s ON s.id = sr.sale_id
    WHERE s.customer_id IS NOT NULL AND sr.refund_total > 0 AND sr.status = 'completed'
      AND NOT EXISTS (SELECT 1 FROM nexora.customer_credit_memo m WHERE m.sale_return_id = sr.id)
      AND (sr.refund_method IN ('store_credit', 'credit_note')
           OR (sr.refund_method = 'original'
               AND EXISTS (SELECT 1 FROM nexora.customer_receivable r WHERE r.sale_id = s.id))))
INSERT INTO nexora.customer_credit_memo
    (company_id, memo_number, customer_id, source_type, sale_return_id, currency_id, amount, gl_account_key,
     reason, establishment_id, created_by)
SELECT company_id, 'AVC-REPRISE-' || document_number, customer_id, 'sale_return', return_id, currency_id, refund_total,
       gl_key, 'Reprise V074 — ' || coalesce(reason, 'retour'), establishment_id, created_by
FROM old;

-- Imputation sur la créance de la vente (retours « original / credit_note »), dans l'ordre des retours.
DO $$
DECLARE
    m record;
    v_remaining numeric;
    v_part numeric;
BEGIN
    FOR m IN
        SELECT cm.id, cm.company_id, cm.amount, cm.currency_id, r.id AS receivable_id
        FROM nexora.customer_credit_memo cm
        JOIN nexora.sale_return sr ON sr.id = cm.sale_return_id
        JOIN nexora.customer_receivable r ON r.sale_id = sr.sale_id
        WHERE cm.memo_number LIKE 'AVC-REPRISE-%' AND cm.gl_account_key = 'accounts_receivable'
          AND r.status IN ('OPEN', 'PARTIALLY_PAID', 'OVERDUE', 'IN_DISPUTE')
          AND NOT EXISTS (SELECT 1 FROM nexora.customer_credit_application a WHERE a.credit_memo_id = cm.id)
        ORDER BY sr.returned_at
    LOOP
        SELECT remaining_amount INTO v_remaining FROM nexora.customer_receivable WHERE id = m.receivable_id;
        v_part := least(m.amount, v_remaining);
        IF v_part > 0 THEN
            INSERT INTO nexora.customer_credit_application (company_id, credit_memo_id, receivable_id, currency_id, amount)
            VALUES (m.company_id, m.id, m.receivable_id, m.currency_id, v_part);
        END IF;
    END LOOP;
END;
$$;

-- =====================================================================
-- Sécurité : isolation entreprise + audit
-- =====================================================================
SELECT nexora.enable_company_rls('nexora.credit_schedule'::regclass);
SELECT nexora.enable_company_rls('nexora.credit_installment'::regclass);
DO $$
DECLARE t text;
BEGIN
    FOREACH t IN ARRAY ARRAY['credit_schedule', 'credit_installment'] LOOP
        EXECUTE format('DROP TRIGGER IF EXISTS trg_audit_%1$s ON nexora.%1$I', t);
        EXECUTE format(
            'CREATE TRIGGER trg_audit_%1$s AFTER INSERT OR UPDATE OR DELETE ON nexora.%1$I
               FOR EACH ROW EXECUTE FUNCTION nexora.audit_row()', t);
    END LOOP;
END;
$$;

SELECT nexora.attach_common_triggers();
