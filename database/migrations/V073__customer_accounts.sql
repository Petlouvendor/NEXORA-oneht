-- =============================================================================
-- NEXORA — V073 — Module « Gestion clients & comptes clients »
-- (docs/module-clients-comptes-clients.md ; mise en œuvre : docs/module-clients-comptes-clients-implementation.md)
--
-- Le socle existe et reste la seule source de vérité — rien n'est dupliqué :
--   customer (V006) = fiche ; customer_receivable (V012) = « crédit » (§8) ; payment +
--   payment_allocation (V012) = versements multiples affectés à une ou plusieurs dettes (§9) ;
--   customer_promise / customer_dispute / customer_block / receivable_write_off (V040) = recouvrement ;
--   sale_return (+ demandes V065) = retours validés par un Manager (§11).
-- Ajouts :
--   1. fiche client enrichie (§6) ;
--   2. numéro de crédit unique + origine (§8) ;
--   3. annulation contrôlée d'un versement : l'affectation est neutralisée, jamais supprimée (§4, §22) ;
--   4. dépenses imputées au client, distinctes des dépenses générales (§10) ;
--   5. avoirs utilisables + remboursements (§11, §29) ;
--   6. actions de recouvrement (§13) ;
--   7. journal financier client append-only (§7, §20), alimenté par triggers depuis les tables
--      sources : aucun chemin d'écriture (POS, commandes, sync hors ligne, API finance) ne peut l'oublier.
-- Solde client = Σ débit − Σ crédit du journal = Σ créances restantes − Σ avoirs disponibles.
-- =============================================================================
SET search_path TO nexora, public;

-- =====================================================================
-- 1. Fiche client (§6)
-- =====================================================================
ALTER TABLE nexora.customer
    ADD COLUMN first_name              text,
    ADD COLUMN city                    text,
    ADD COLUMN country                 text,
    ADD COLUMN secondary_contact_name  text,
    ADD COLUMN secondary_contact_phone text,
    ADD COLUMN segment                 text,
    ADD COLUMN account_manager_id      uuid REFERENCES nexora.app_user(id),
    ADD COLUMN credit_days             integer,          -- échéance par défaut d'un crédit (jours)
    ADD COLUMN credit_terms            text,             -- conditions de crédit (texte libre)
    ADD COLUMN establishment_id        uuid,             -- succursale de rattachement
    ADD COLUMN notes                   text,
    ADD COLUMN created_by              uuid REFERENCES nexora.app_user(id),
    ADD COLUMN updated_by              uuid REFERENCES nexora.app_user(id),
    ADD CONSTRAINT customer_credit_days_chk CHECK (credit_days IS NULL OR credit_days BETWEEN 0 AND 3650),
    ADD CONSTRAINT customer_establishment_fk FOREIGN KEY (establishment_id, company_id)
        REFERENCES nexora.establishment (id, company_id);

CREATE INDEX customer_status_idx ON nexora.customer (company_id, status);
CREATE INDEX customer_phone_idx  ON nexora.customer (company_id, phone) WHERE phone IS NOT NULL;
-- Recherche progressive (§17) : même normalisation que le catalogue (V036).
CREATE INDEX customer_search_trgm_idx ON nexora.customer
    USING gin (nexora.search_norm(coalesce(first_name, '') || ' ' || name) gin_trgm_ops);

-- =====================================================================
-- 2. Crédit = créance client (§8) : numéro unique, origine, auteur, succursale, annulation
-- =====================================================================
ALTER TABLE nexora.customer_receivable
    ADD COLUMN credit_number    text,
    ADD COLUMN source_type      text,
    ADD COLUMN source_id        uuid,
    ADD COLUMN establishment_id uuid,
    ADD COLUMN created_by       uuid REFERENCES nexora.app_user(id),
    ADD COLUMN cancelled_at     timestamptz,
    ADD COLUMN cancel_reason    text;

UPDATE nexora.customer_receivable r
   SET source_type = CASE WHEN r.sale_id IS NOT NULL THEN 'sale' ELSE 'manual' END,
       source_id = r.sale_id,
       establishment_id = s.establishment_id,
       created_by = s.created_by
  FROM nexora.customer_receivable r0
  LEFT JOIN nexora.sale s ON s.id = r0.sale_id
 WHERE r0.id = r.id;

WITH n AS (
    SELECT id, extract(year FROM issue_date)::int AS y,
           row_number() OVER (PARTITION BY company_id, extract(year FROM issue_date)
                              ORDER BY issue_date, created_at, id) AS rn
    FROM nexora.customer_receivable)
UPDATE nexora.customer_receivable r
   SET credit_number = 'CRD-' || n.y || '-' || lpad(n.rn::text, 6, '0')
  FROM n WHERE n.id = r.id;

INSERT INTO nexora.document_sequence (company_id, document_type, year, current_number)
SELECT company_id, 'customer_credit', extract(year FROM issue_date)::int, count(*)
FROM nexora.customer_receivable
GROUP BY company_id, extract(year FROM issue_date)
ON CONFLICT ON CONSTRAINT document_sequence_uk
DO UPDATE SET current_number = GREATEST(nexora.document_sequence.current_number, EXCLUDED.current_number);

ALTER TABLE nexora.customer_receivable
    ALTER COLUMN credit_number SET NOT NULL,
    ALTER COLUMN source_type SET NOT NULL,
    ADD CONSTRAINT customer_receivable_credit_number_uk UNIQUE (company_id, credit_number),
    ADD CONSTRAINT customer_receivable_source_chk
        CHECK (source_type IN ('sale', 'customer_expense', 'opening', 'manual')),
    ADD CONSTRAINT customer_receivable_cancel_chk
        CHECK (status <> 'CANCELLED' OR cancelled_at IS NULL OR length(btrim(coalesce(cancel_reason, ''))) > 0),
    ADD CONSTRAINT customer_receivable_establishment_fk FOREIGN KEY (establishment_id, company_id)
        REFERENCES nexora.establishment (id, company_id);
CREATE INDEX customer_receivable_customer_status_idx ON nexora.customer_receivable (company_id, customer_id, status);

-- Valeurs par défaut posées par la base : aucun chemin d'insertion (moteur financier, tests, imports)
-- ne peut produire un crédit sans numéro ni origine.
CREATE OR REPLACE FUNCTION nexora.customer_receivable_defaults()
RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    NEW.source_type := coalesce(NEW.source_type, CASE WHEN NEW.sale_id IS NOT NULL THEN 'sale' ELSE 'manual' END);
    IF NEW.source_type = 'sale' THEN
        NEW.source_id := coalesce(NEW.source_id, NEW.sale_id);
    END IF;
    IF NEW.credit_number IS NULL THEN
        NEW.credit_number := 'CRD-' || nexora.next_document_number(
            NEW.company_id, 'customer_credit', NULL, extract(year FROM NEW.issue_date)::int);
    END IF;
    NEW.created_by := coalesce(NEW.created_by, nexora.current_user_id());
    IF NEW.establishment_id IS NULL AND NEW.sale_id IS NOT NULL THEN
        NEW.establishment_id := nexora.sale_establishment(NEW.sale_id);
    END IF;
    RETURN NEW;
END;
$$;
DROP TRIGGER IF EXISTS trg_customer_receivable_defaults ON nexora.customer_receivable;
CREATE TRIGGER trg_customer_receivable_defaults BEFORE INSERT ON nexora.customer_receivable
    FOR EACH ROW EXECUTE FUNCTION nexora.customer_receivable_defaults();

-- =====================================================================
-- 3. Versements : note, annulation contrôlée (§9, §22 « POST /payments/{id}/void »)
-- Annuler = neutraliser les affectations (voided_at) + extourne comptable + sortie de trésorerie.
-- Le versement et ses affectations restent visibles ; rien n'est supprimé (§4, §33).
-- =====================================================================
ALTER TABLE nexora.payment
    ADD COLUMN note                  text,
    ADD COLUMN voided_at             timestamptz,
    ADD COLUMN voided_by             uuid REFERENCES nexora.app_user(id),
    ADD COLUMN void_reason           text,
    ADD COLUMN void_journal_entry_id uuid,
    ADD CONSTRAINT payment_void_chk CHECK (
        status <> 'void' OR (voided_at IS NOT NULL AND length(btrim(coalesce(void_reason, ''))) > 0));

ALTER TABLE nexora.payment_allocation
    ADD COLUMN voided_at timestamptz,
    ADD COLUMN voided_by uuid REFERENCES nexora.app_user(id);

-- --- Avoirs (§11, §29) — déclarés avant le recalcul de créance qui les lit -------------------
CREATE TABLE nexora.customer_credit_memo (
    id              uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    company_id      uuid NOT NULL REFERENCES nexora.company(id),
    memo_number     text NOT NULL,
    customer_id     uuid NOT NULL,
    source_type     text NOT NULL,              -- sale_return | overpayment
    sale_return_id  uuid,
    payment_id      uuid,
    currency_id     uuid NOT NULL REFERENCES nexora.currency(id),
    amount          nexora.money_amount NOT NULL,
    used_amount     nexora.money_amount NOT NULL DEFAULT 0,
    remaining_amount nexora.money_amount GENERATED ALWAYS AS (amount - used_amount) STORED,
    -- Compte où l'avoir disponible est porté en comptabilité : l'utiliser débite ce compte.
    gl_account_key  text NOT NULL,
    status          text NOT NULL DEFAULT 'available',
    reason          text,
    establishment_id uuid,
    created_by      uuid REFERENCES nexora.app_user(id),
    cancelled_at    timestamptz,
    cancelled_by    uuid REFERENCES nexora.app_user(id),
    cancel_reason   text,
    created_at      timestamptz NOT NULL DEFAULT now(),
    updated_at      timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT customer_credit_memo_company_id_uk UNIQUE (id, company_id),
    CONSTRAINT customer_credit_memo_number_uk UNIQUE (company_id, memo_number),
    CONSTRAINT customer_credit_memo_customer_fk FOREIGN KEY (customer_id, company_id)
        REFERENCES nexora.customer (id, company_id),
    CONSTRAINT customer_credit_memo_return_fk FOREIGN KEY (sale_return_id, company_id)
        REFERENCES nexora.sale_return (id, company_id),
    CONSTRAINT customer_credit_memo_payment_fk FOREIGN KEY (payment_id, company_id)
        REFERENCES nexora.payment (id, company_id),
    CONSTRAINT customer_credit_memo_source_chk CHECK (source_type IN ('sale_return', 'overpayment')),
    CONSTRAINT customer_credit_memo_origin_chk CHECK (
        (source_type = 'sale_return' AND sale_return_id IS NOT NULL)
     OR (source_type = 'overpayment' AND payment_id IS NOT NULL)),
    CONSTRAINT customer_credit_memo_amount_chk CHECK (amount > 0),
    CONSTRAINT customer_credit_memo_used_chk CHECK (used_amount >= 0 AND used_amount <= amount),
    CONSTRAINT customer_credit_memo_gl_chk CHECK (gl_account_key IN ('store_credit', 'suspense')),
    CONSTRAINT customer_credit_memo_status_chk
        CHECK (status IN ('available', 'partially_used', 'used', 'cancelled')),
    CONSTRAINT customer_credit_memo_cancel_chk CHECK (
        status <> 'cancelled' OR (cancelled_at IS NOT NULL AND length(btrim(coalesce(cancel_reason, ''))) > 0))
);
CREATE INDEX customer_credit_memo_customer_idx ON nexora.customer_credit_memo (company_id, customer_id, status);
CREATE UNIQUE INDEX customer_credit_memo_return_uk ON nexora.customer_credit_memo (sale_return_id) WHERE sale_return_id IS NOT NULL;
CREATE UNIQUE INDEX customer_credit_memo_payment_uk ON nexora.customer_credit_memo (payment_id) WHERE payment_id IS NOT NULL;

-- --- Remboursements d'avoir (§7 « diminue un avoir et génère un décaissement », §29) ---------
CREATE TABLE nexora.customer_refund (
    id                   uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    company_id           uuid NOT NULL REFERENCES nexora.company(id),
    refund_number        text NOT NULL,
    customer_id          uuid NOT NULL,
    credit_memo_id       uuid NOT NULL,
    currency_id          uuid NOT NULL REFERENCES nexora.currency(id),
    amount               nexora.money_amount NOT NULL,
    refund_method        text NOT NULL DEFAULT 'cash',
    financial_account_id uuid REFERENCES nexora.financial_account(id),
    cash_session_id      uuid,
    reason               text NOT NULL,
    status               text NOT NULL DEFAULT 'requested',
    operation_uuid       uuid NOT NULL,
    requested_by         uuid NOT NULL REFERENCES nexora.app_user(id),
    requested_at         timestamptz NOT NULL DEFAULT now(),
    decided_by           uuid REFERENCES nexora.app_user(id),
    decided_at           timestamptz,
    decision_note        text,
    executed_by          uuid REFERENCES nexora.app_user(id),
    executed_at          timestamptz,
    journal_entry_id     uuid,
    created_at           timestamptz NOT NULL DEFAULT now(),
    updated_at           timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT customer_refund_company_id_uk UNIQUE (id, company_id),
    CONSTRAINT customer_refund_number_uk UNIQUE (company_id, refund_number),
    CONSTRAINT customer_refund_operation_uk UNIQUE (operation_uuid),
    CONSTRAINT customer_refund_customer_fk FOREIGN KEY (customer_id, company_id)
        REFERENCES nexora.customer (id, company_id),
    CONSTRAINT customer_refund_memo_fk FOREIGN KEY (credit_memo_id, company_id)
        REFERENCES nexora.customer_credit_memo (id, company_id),
    CONSTRAINT customer_refund_session_fk FOREIGN KEY (cash_session_id, company_id)
        REFERENCES nexora.cash_session (id, company_id),
    CONSTRAINT customer_refund_amount_chk CHECK (amount > 0),
    CONSTRAINT customer_refund_method_chk
        CHECK (refund_method IN ('cash', 'card', 'bank_transfer', 'mobile_money', 'check', 'other')),
    CONSTRAINT customer_refund_status_chk
        CHECK (status IN ('requested', 'approved', 'executed', 'rejected', 'cancelled')),
    CONSTRAINT customer_refund_reason_chk CHECK (length(btrim(reason)) > 0),
    -- Séparation des responsabilités (§19) : le demandeur n'approuve jamais sa propre demande.
    CONSTRAINT customer_refund_four_eyes_chk CHECK (decided_by IS NULL OR decided_by <> requested_by),
    CONSTRAINT customer_refund_decided_chk CHECK (
        status NOT IN ('approved', 'executed', 'rejected') OR (decided_by IS NOT NULL AND decided_at IS NOT NULL)),
    CONSTRAINT customer_refund_rejected_note_chk CHECK (
        status <> 'rejected' OR length(btrim(coalesce(decision_note, ''))) > 0),
    CONSTRAINT customer_refund_executed_chk CHECK (
        status <> 'executed' OR (executed_at IS NOT NULL AND financial_account_id IS NOT NULL))
);
CREATE INDEX customer_refund_customer_idx ON nexora.customer_refund (company_id, customer_id, status);
CREATE INDEX customer_refund_status_idx ON nexora.customer_refund (company_id, status, requested_at DESC);

-- --- Utilisation d'un avoir : sur une créance, ou par un remboursement --------------------
CREATE TABLE nexora.customer_credit_application (
    id               uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    company_id       uuid NOT NULL REFERENCES nexora.company(id),
    credit_memo_id   uuid NOT NULL,
    receivable_id    uuid,
    refund_id        uuid,
    currency_id      uuid NOT NULL REFERENCES nexora.currency(id),
    amount           nexora.money_amount NOT NULL,
    -- Écriture Dr <compte de l'avoir> / Cr clients ; NULL quand le retour a déjà crédité le compte client.
    journal_entry_id uuid,
    applied_at       timestamptz NOT NULL DEFAULT now(),
    created_by       uuid REFERENCES nexora.app_user(id),
    voided_at        timestamptz,
    voided_by        uuid REFERENCES nexora.app_user(id),
    created_at       timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT customer_credit_application_memo_fk FOREIGN KEY (credit_memo_id, company_id)
        REFERENCES nexora.customer_credit_memo (id, company_id),
    CONSTRAINT customer_credit_application_receivable_fk FOREIGN KEY (receivable_id, company_id)
        REFERENCES nexora.customer_receivable (id, company_id),
    CONSTRAINT customer_credit_application_refund_fk FOREIGN KEY (refund_id, company_id)
        REFERENCES nexora.customer_refund (id, company_id),
    CONSTRAINT customer_credit_application_amount_chk CHECK (amount > 0),
    CONSTRAINT customer_credit_application_target_chk CHECK (
        (receivable_id IS NOT NULL AND refund_id IS NULL) OR (receivable_id IS NULL AND refund_id IS NOT NULL))
);
CREATE INDEX customer_credit_application_memo_idx ON nexora.customer_credit_application (credit_memo_id);
CREATE INDEX customer_credit_application_receivable_idx ON nexora.customer_credit_application (receivable_id);

-- --- Recalcul d'une créance depuis ses règlements actifs --------------------------------
-- réglé = versements affectés (non annulés) + avoirs imputés (non annulés) + radiations.
-- Un litige reste « en litige » tant que la créance n'est pas soldée ; radiée / annulée : figée.
CREATE OR REPLACE FUNCTION nexora.recompute_customer_receivable(p_receivable_id uuid)
RETURNS void LANGUAGE plpgsql AS $$
DECLARE
    v_orig    nexora.money_amount;
    v_settled nexora.money_amount;
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

    UPDATE nexora.customer_receivable
       SET paid_amount = v_settled,
           status = CASE
               WHEN status IN ('CANCELLED', 'WRITTEN_OFF') THEN status
               WHEN v_settled >= v_orig THEN 'PAID'
               WHEN status = 'IN_DISPUTE' THEN 'IN_DISPUTE'
               WHEN v_settled = 0 THEN 'OPEN'
               ELSE 'PARTIALLY_PAID' END,
           updated_at = now()
     WHERE id = p_receivable_id;
END;
$$;

-- Remplace V019 : seules les affectations actives comptent (versement annulé → affectation neutralisée).
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
        SELECT coalesce(sum(allocated_amount), 0) INTO v_alloc_total
        FROM nexora.payment_allocation WHERE payable_id = v_payable_id AND voided_at IS NULL;
        SELECT original_amount INTO v_orig FROM nexora.supplier_payable WHERE id = v_payable_id;
        IF v_alloc_total > v_orig THEN
            RAISE EXCEPTION 'Règlements (%) > montant de la dette (%)', v_alloc_total, v_orig
                USING ERRCODE = 'check_violation';
        END IF;
        UPDATE nexora.supplier_payable
           SET paid_amount = v_alloc_total,
               status = CASE
                   WHEN v_alloc_total = 0      THEN 'OPEN'
                   WHEN v_alloc_total < v_orig THEN 'PARTIALLY_PAID'
                   ELSE 'PAID' END,
               updated_at = now()
         WHERE id = v_payable_id AND status <> 'CANCELLED';
    END IF;

    RETURN coalesce(NEW, OLD);
END;
$$;

-- Une affectation annulée ne se réactive pas, et une affectation n'est jamais supprimée
-- une fois son versement validé (le versement lui-même est protégé depuis V019).
CREATE OR REPLACE FUNCTION nexora.payment_allocation_guard()
RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF TG_OP = 'UPDATE' THEN
        IF OLD.voided_at IS NOT NULL AND NEW.voided_at IS DISTINCT FROM OLD.voided_at THEN
            RAISE EXCEPTION 'Affectation annulée : elle ne peut pas être réactivée' USING ERRCODE = 'check_violation';
        END IF;
        IF NEW.allocated_amount <> OLD.allocated_amount OR NEW.receivable_id IS DISTINCT FROM OLD.receivable_id
           OR NEW.payable_id IS DISTINCT FROM OLD.payable_id OR NEW.payment_id <> OLD.payment_id THEN
            RAISE EXCEPTION 'Une affectation ne se modifie pas : annulez le versement' USING ERRCODE = 'check_violation';
        END IF;
    END IF;
    RETURN NEW;
END;
$$;
DROP TRIGGER IF EXISTS trg_payment_allocation_guard ON nexora.payment_allocation;
CREATE TRIGGER trg_payment_allocation_guard BEFORE UPDATE ON nexora.payment_allocation
    FOR EACH ROW EXECUTE FUNCTION nexora.payment_allocation_guard();

-- Utilisation d'un avoir → « utilisé » de l'avoir + recalcul de la créance visée.
CREATE OR REPLACE FUNCTION nexora.customer_credit_application_apply()
RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE
    v_memo_id    uuid := coalesce(NEW.credit_memo_id, OLD.credit_memo_id);
    v_receivable uuid := coalesce(NEW.receivable_id, OLD.receivable_id);
    v_used       nexora.money_amount;
    v_memo_ccy   uuid;
    v_target_ccy uuid;
BEGIN
    IF TG_OP = 'INSERT' THEN
        SELECT currency_id INTO v_memo_ccy FROM nexora.customer_credit_memo WHERE id = NEW.credit_memo_id;
        IF NEW.currency_id <> v_memo_ccy THEN
            RAISE EXCEPTION 'Devise d''utilisation <> devise de l''avoir' USING ERRCODE = 'check_violation';
        END IF;
        IF NEW.receivable_id IS NOT NULL THEN
            SELECT currency_id INTO v_target_ccy FROM nexora.customer_receivable WHERE id = NEW.receivable_id;
            IF NEW.currency_id <> v_target_ccy THEN
                RAISE EXCEPTION 'Devise de l''avoir <> devise de la créance' USING ERRCODE = 'check_violation';
            END IF;
        END IF;
    END IF;

    SELECT coalesce(sum(amount), 0) INTO v_used
    FROM nexora.customer_credit_application WHERE credit_memo_id = v_memo_id AND voided_at IS NULL;
    UPDATE nexora.customer_credit_memo
       SET used_amount = v_used,
           status = CASE WHEN status = 'cancelled' THEN status
                         WHEN v_used = 0 THEN 'available'
                         WHEN v_used < amount THEN 'partially_used'
                         ELSE 'used' END,
           updated_at = now()
     WHERE id = v_memo_id;

    IF v_receivable IS NOT NULL THEN
        PERFORM nexora.recompute_customer_receivable(v_receivable);
    END IF;
    RETURN NULL;
END;
$$;
CREATE TRIGGER trg_customer_credit_application_apply
    AFTER INSERT OR UPDATE ON nexora.customer_credit_application
    FOR EACH ROW EXECUTE FUNCTION nexora.customer_credit_application_apply();

-- =====================================================================
-- 4. Dépenses imputées au client (§10) — distinctes de nexora.expense (dépenses générales)
-- =====================================================================
CREATE TABLE nexora.customer_expense (
    id                      uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    company_id              uuid NOT NULL REFERENCES nexora.company(id),
    expense_number          text NOT NULL,
    customer_id             uuid NOT NULL,
    category                text NOT NULL,
    description             text NOT NULL,
    amount                  nexora.money_amount NOT NULL,
    currency_id             uuid NOT NULL REFERENCES nexora.currency(id),
    expense_date            date NOT NULL DEFAULT current_date,
    -- Refacturée : la validation crée un crédit client (la dette augmente, §7). Sinon, suivie
    -- pour l'analyse de rentabilité du client sans toucher à son solde.
    billable                boolean NOT NULL DEFAULT true,
    due_date                date,
    status                  text NOT NULL DEFAULT 'draft',
    document_id             uuid,                        -- justificatif (GED, Phase 45)
    receivable_id           uuid,                        -- crédit généré à la validation
    journal_entry_id        uuid,
    establishment_id        uuid,
    created_by              uuid NOT NULL REFERENCES nexora.app_user(id),
    submitted_at            timestamptz,
    decided_by              uuid REFERENCES nexora.app_user(id),
    decided_at              timestamptz,
    decision_note           text,
    cancelled_by            uuid REFERENCES nexora.app_user(id),
    cancelled_at            timestamptz,
    cancel_reason           text,
    cancel_journal_entry_id uuid,
    operation_uuid          uuid NOT NULL,
    created_at              timestamptz NOT NULL DEFAULT now(),
    updated_at              timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT customer_expense_company_id_uk UNIQUE (id, company_id),
    CONSTRAINT customer_expense_number_uk UNIQUE (company_id, expense_number),
    CONSTRAINT customer_expense_operation_uk UNIQUE (operation_uuid),
    CONSTRAINT customer_expense_customer_fk FOREIGN KEY (customer_id, company_id)
        REFERENCES nexora.customer (id, company_id),
    CONSTRAINT customer_expense_receivable_fk FOREIGN KEY (receivable_id, company_id)
        REFERENCES nexora.customer_receivable (id, company_id),
    CONSTRAINT customer_expense_document_fk FOREIGN KEY (document_id, company_id)
        REFERENCES nexora.document (id, company_id),
    CONSTRAINT customer_expense_establishment_fk FOREIGN KEY (establishment_id, company_id)
        REFERENCES nexora.establishment (id, company_id),
    CONSTRAINT customer_expense_amount_chk CHECK (amount > 0),
    CONSTRAINT customer_expense_category_chk CHECK (category IN (
        'delivery', 'transport', 'installation', 'handling', 'repair', 'file_fee', 'penalty', 'other')),
    CONSTRAINT customer_expense_status_chk
        CHECK (status IN ('draft', 'pending', 'validated', 'rejected', 'cancelled')),
    CONSTRAINT customer_expense_description_chk CHECK (length(btrim(description)) > 0),
    -- Séparation des responsabilités (§18-19) : celui qui saisit ne valide pas.
    CONSTRAINT customer_expense_four_eyes_chk CHECK (decided_by IS NULL OR decided_by <> created_by),
    CONSTRAINT customer_expense_decided_chk CHECK (
        status NOT IN ('validated', 'rejected') OR (decided_by IS NOT NULL AND decided_at IS NOT NULL)),
    CONSTRAINT customer_expense_rejected_note_chk CHECK (
        status <> 'rejected' OR length(btrim(coalesce(decision_note, ''))) > 0),
    CONSTRAINT customer_expense_billed_chk CHECK (
        NOT (status = 'validated' AND billable) OR receivable_id IS NOT NULL),
    CONSTRAINT customer_expense_cancel_chk CHECK (
        status <> 'cancelled' OR (cancelled_at IS NOT NULL AND length(btrim(coalesce(cancel_reason, ''))) > 0))
);
CREATE INDEX customer_expense_customer_idx ON nexora.customer_expense (company_id, customer_id, expense_date DESC);
CREATE INDEX customer_expense_status_idx ON nexora.customer_expense (company_id, status);

-- Une dépense validée qui a touché le compte client ne se supprime jamais (§10).
CREATE OR REPLACE FUNCTION nexora.customer_expense_no_delete()
RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF OLD.status IN ('validated', 'cancelled', 'rejected') AND NOT nexora.flag('allow_hard_delete') THEN
        RAISE EXCEPTION 'Dépense client % : suppression interdite (annuler par contre-écriture)', OLD.expense_number
            USING ERRCODE = 'insufficient_privilege';
    END IF;
    RETURN OLD;
END;
$$;
CREATE TRIGGER trg_customer_expense_no_delete BEFORE DELETE ON nexora.customer_expense
    FOR EACH ROW EXECUTE FUNCTION nexora.customer_expense_no_delete();

-- =====================================================================
-- 5. Actions de recouvrement (§13) — journal append-only
-- =====================================================================
CREATE TABLE nexora.collection_action (
    id               uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    company_id       uuid NOT NULL REFERENCES nexora.company(id),
    customer_id      uuid NOT NULL,
    receivable_id    uuid,
    action_type      text NOT NULL,
    outcome          text,
    note             text NOT NULL,
    next_action_date date,
    promise_id       uuid,
    created_by       uuid NOT NULL REFERENCES nexora.app_user(id),
    created_at       timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT collection_action_customer_fk FOREIGN KEY (customer_id, company_id)
        REFERENCES nexora.customer (id, company_id),
    CONSTRAINT collection_action_receivable_fk FOREIGN KEY (receivable_id, company_id)
        REFERENCES nexora.customer_receivable (id, company_id),
    CONSTRAINT collection_action_type_chk CHECK (action_type IN (
        'call', 'sms', 'email', 'whatsapp', 'visit', 'letter', 'reminder', 'note', 'other')),
    CONSTRAINT collection_action_outcome_chk CHECK (outcome IS NULL OR outcome IN (
        'reached', 'no_answer', 'promise', 'refused', 'dispute', 'paid', 'other')),
    CONSTRAINT collection_action_note_chk CHECK (length(btrim(note)) > 0)
);
CREATE INDEX collection_action_customer_idx ON nexora.collection_action (company_id, customer_id, created_at DESC);
CREATE INDEX collection_action_next_idx ON nexora.collection_action (company_id, next_action_date)
    WHERE next_action_date IS NOT NULL;

-- =====================================================================
-- 6. Journal financier client (§7, §20 customer_ledger_entries) — append-only
-- =====================================================================
CREATE TABLE nexora.customer_ledger_entry (
    id               uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    company_id       uuid NOT NULL REFERENCES nexora.company(id),
    customer_id      uuid NOT NULL,
    occurred_at      timestamptz NOT NULL,
    entry_type       text NOT NULL,
    debit            nexora.money_amount NOT NULL DEFAULT 0,   -- augmente ce que le client doit
    credit           nexora.money_amount NOT NULL DEFAULT 0,   -- le diminue (ou crée un avoir)
    currency_id      uuid NOT NULL REFERENCES nexora.currency(id),
    receivable_id    uuid,
    payment_id       uuid,
    credit_memo_id   uuid,
    source_type      text NOT NULL,
    source_id        uuid,
    reference        text,
    description      text,
    establishment_id uuid,
    created_by       uuid,                                     -- pas de FK : l'historique survit au compte
    created_at       timestamptz NOT NULL DEFAULT clock_timestamp(),
    CONSTRAINT customer_ledger_entry_customer_fk FOREIGN KEY (customer_id, company_id)
        REFERENCES nexora.customer (id, company_id),
    CONSTRAINT customer_ledger_entry_type_chk CHECK (entry_type IN (
        'credit_sale', 'customer_expense', 'opening', 'manual_debit', 'payment', 'payment_void',
        'sale_return', 'overpayment', 'write_off', 'refund', 'credit_cancelled', 'credit_memo_cancelled')),
    CONSTRAINT customer_ledger_entry_side_chk CHECK (
        debit >= 0 AND credit >= 0 AND ((debit > 0 AND credit = 0) OR (credit > 0 AND debit = 0)))
);
CREATE INDEX customer_ledger_entry_customer_idx ON nexora.customer_ledger_entry (company_id, customer_id, occurred_at, created_at);
CREATE INDEX customer_ledger_entry_type_idx ON nexora.customer_ledger_entry (company_id, entry_type, occurred_at);
CREATE INDEX customer_ledger_entry_receivable_idx ON nexora.customer_ledger_entry (receivable_id) WHERE receivable_id IS NOT NULL;

CREATE OR REPLACE FUNCTION nexora.customer_ledger_immutable()
RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    RAISE EXCEPTION 'Le journal financier client est immuable (§4, §33) : % interdit — passer une opération compensatoire', TG_OP
        USING ERRCODE = 'insufficient_privilege';
END;
$$;
CREATE TRIGGER trg_customer_ledger_immutable BEFORE UPDATE OR DELETE ON nexora.customer_ledger_entry
    FOR EACH ROW EXECUTE FUNCTION nexora.customer_ledger_immutable();

CREATE OR REPLACE FUNCTION nexora.customer_ledger_write(
    p_company uuid, p_customer uuid, p_type text, p_debit numeric, p_credit numeric, p_currency uuid,
    p_receivable uuid, p_payment uuid, p_memo uuid, p_source_type text, p_source_id uuid,
    p_reference text, p_description text, p_establishment uuid, p_occurred timestamptz)
RETURNS void LANGUAGE sql AS $$
    INSERT INTO nexora.customer_ledger_entry
        (company_id, customer_id, occurred_at, entry_type, debit, credit, currency_id, receivable_id, payment_id,
         credit_memo_id, source_type, source_id, reference, description, establishment_id, created_by)
    VALUES (p_company, p_customer, coalesce(p_occurred, clock_timestamp()), p_type, p_debit, p_credit, p_currency,
            p_receivable, p_payment, p_memo, p_source_type, p_source_id, p_reference, p_description, p_establishment,
            nexora.current_user_id());
$$;

-- Crédit créé (vente à crédit, dépense refacturée, reprise) → dette +.
CREATE OR REPLACE FUNCTION nexora.trg_ledger_receivable()
RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE
    v_doc text;
BEGIN
    IF TG_OP = 'INSERT' THEN
        IF NEW.original_amount > 0 THEN
            IF NEW.sale_id IS NOT NULL THEN
                SELECT document_number INTO v_doc FROM nexora.sale WHERE id = NEW.sale_id;
            END IF;
            PERFORM nexora.customer_ledger_write(NEW.company_id, NEW.customer_id,
                CASE NEW.source_type WHEN 'sale' THEN 'credit_sale' WHEN 'customer_expense' THEN 'customer_expense'
                                     WHEN 'opening' THEN 'opening' ELSE 'manual_debit' END,
                NEW.original_amount, 0, NEW.currency_id, NEW.id, NULL, NULL, NEW.source_type, NEW.source_id,
                NEW.credit_number,
                CASE NEW.source_type WHEN 'sale' THEN 'Vente à crédit ' || coalesce(v_doc, '')
                                     WHEN 'customer_expense' THEN 'Dépense imputée'
                                     WHEN 'opening' THEN 'Reprise de solde' ELSE 'Crédit' END,
                NEW.establishment_id, NULL);
        END IF;
    ELSIF NEW.status = 'CANCELLED' AND OLD.status <> 'CANCELLED' AND OLD.remaining_amount > 0 THEN
        PERFORM nexora.customer_ledger_write(NEW.company_id, NEW.customer_id, 'credit_cancelled',
            0, OLD.remaining_amount, NEW.currency_id, NEW.id, NULL, NULL, NEW.source_type, NEW.source_id,
            NEW.credit_number, 'Annulation du crédit — ' || coalesce(NEW.cancel_reason, ''), NEW.establishment_id, NULL);
    END IF;
    RETURN NULL;
END;
$$;
CREATE TRIGGER trg_ledger_receivable AFTER INSERT OR UPDATE OF status ON nexora.customer_receivable
    FOR EACH ROW EXECUTE FUNCTION nexora.trg_ledger_receivable();

-- Versement affecté → dette − ; annulation → dette + (contre-écriture, l'original reste).
CREATE OR REPLACE FUNCTION nexora.trg_ledger_payment_allocation()
RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE
    v_customer uuid;
    v_credit   text;
    v_estab    uuid;
    v_number   text;
    v_session  uuid;
BEGIN
    IF NEW.receivable_id IS NULL THEN RETURN NULL; END IF;
    SELECT customer_id, credit_number, establishment_id INTO v_customer, v_credit, v_estab
    FROM nexora.customer_receivable WHERE id = NEW.receivable_id;
    SELECT payment_number, cash_session_id INTO v_number, v_session FROM nexora.payment WHERE id = NEW.payment_id;
    IF v_session IS NOT NULL THEN
        v_estab := coalesce(nexora.session_establishment(v_session), v_estab);
    END IF;

    IF TG_OP = 'INSERT' AND NEW.voided_at IS NULL THEN
        PERFORM nexora.customer_ledger_write(NEW.company_id, v_customer, 'payment', 0, NEW.allocated_amount,
            NEW.currency_id, NEW.receivable_id, NEW.payment_id, NULL, 'payment', NEW.payment_id, v_number,
            'Versement sur ' || v_credit, v_estab, NEW.allocated_at);
    ELSIF TG_OP = 'UPDATE' AND OLD.voided_at IS NULL AND NEW.voided_at IS NOT NULL THEN
        PERFORM nexora.customer_ledger_write(NEW.company_id, v_customer, 'payment_void', NEW.allocated_amount, 0,
            NEW.currency_id, NEW.receivable_id, NEW.payment_id, NULL, 'payment', NEW.payment_id, v_number,
            'Annulation du versement ' || v_number || ' sur ' || v_credit, v_estab, NEW.voided_at);
    END IF;
    RETURN NULL;
END;
$$;
CREATE TRIGGER trg_ledger_payment_allocation AFTER INSERT OR UPDATE OF voided_at ON nexora.payment_allocation
    FOR EACH ROW EXECUTE FUNCTION nexora.trg_ledger_payment_allocation();

-- Radiation (Phase 27) → dette −.
CREATE OR REPLACE FUNCTION nexora.trg_ledger_write_off()
RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE
    v_ccy    uuid;
    v_credit text;
    v_estab  uuid;
BEGIN
    SELECT currency_id, credit_number, establishment_id INTO v_ccy, v_credit, v_estab
    FROM nexora.customer_receivable WHERE id = NEW.receivable_id;
    PERFORM nexora.customer_ledger_write(NEW.company_id, NEW.customer_id, 'write_off', 0, NEW.amount, v_ccy,
        NEW.receivable_id, NULL, NULL, 'receivable_write_off', NEW.id, v_credit,
        'Radiation — ' || NEW.reason, v_estab, NEW.written_off_at);
    RETURN NULL;
END;
$$;
CREATE TRIGGER trg_ledger_write_off AFTER INSERT ON nexora.receivable_write_off
    FOR EACH ROW EXECUTE FUNCTION nexora.trg_ledger_write_off();

-- Avoir émis (retour, trop-perçu) → dette − / crédit disponible + ; annulé → reliquat repris.
CREATE OR REPLACE FUNCTION nexora.trg_ledger_credit_memo()
RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF TG_OP = 'INSERT' THEN
        PERFORM nexora.customer_ledger_write(NEW.company_id, NEW.customer_id, NEW.source_type, 0, NEW.amount,
            NEW.currency_id, NULL, NEW.payment_id, NEW.id, NEW.source_type,
            coalesce(NEW.sale_return_id, NEW.payment_id), NEW.memo_number,
            CASE NEW.source_type WHEN 'sale_return' THEN 'Avoir sur retour' ELSE 'Trop-perçu converti en avoir' END
                || coalesce(' — ' || NEW.reason, ''),
            NEW.establishment_id, NULL);
    ELSIF NEW.status = 'cancelled' AND OLD.status <> 'cancelled' AND NEW.amount - NEW.used_amount > 0 THEN
        PERFORM nexora.customer_ledger_write(NEW.company_id, NEW.customer_id, 'credit_memo_cancelled',
            NEW.amount - NEW.used_amount, 0, NEW.currency_id, NULL, NEW.payment_id, NEW.id, NEW.source_type,
            coalesce(NEW.sale_return_id, NEW.payment_id), NEW.memo_number,
            'Annulation de l''avoir — ' || coalesce(NEW.cancel_reason, ''), NEW.establishment_id, NEW.cancelled_at);
    END IF;
    RETURN NULL;
END;
$$;
CREATE TRIGGER trg_ledger_credit_memo AFTER INSERT OR UPDATE OF status ON nexora.customer_credit_memo
    FOR EACH ROW EXECUTE FUNCTION nexora.trg_ledger_credit_memo();

-- Remboursement exécuté → crédit disponible − (le client est payé).
CREATE OR REPLACE FUNCTION nexora.trg_ledger_refund()
RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF NEW.status = 'executed' AND OLD.status <> 'executed' THEN
        PERFORM nexora.customer_ledger_write(NEW.company_id, NEW.customer_id, 'refund', NEW.amount, 0,
            NEW.currency_id, NULL, NULL, NEW.credit_memo_id, 'customer_refund', NEW.id, NEW.refund_number,
            'Remboursement d''avoir — ' || NEW.reason,
            CASE WHEN NEW.cash_session_id IS NOT NULL THEN nexora.session_establishment(NEW.cash_session_id) END,
            NEW.executed_at);
    END IF;
    RETURN NULL;
END;
$$;
CREATE TRIGGER trg_ledger_refund AFTER UPDATE OF status ON nexora.customer_refund
    FOR EACH ROW EXECUTE FUNCTION nexora.trg_ledger_refund();

-- --- Reprise de l'historique existant (ordre chronologique d'origine) --------------------------
INSERT INTO nexora.customer_ledger_entry
    (company_id, customer_id, occurred_at, entry_type, debit, credit, currency_id, receivable_id,
     source_type, source_id, reference, description, establishment_id, created_by, created_at)
SELECT r.company_id, r.customer_id, r.created_at,
       CASE r.source_type WHEN 'sale' THEN 'credit_sale' ELSE 'manual_debit' END,
       r.original_amount, 0, r.currency_id, r.id, r.source_type, r.source_id, r.credit_number,
       CASE WHEN s.document_number IS NOT NULL THEN 'Vente à crédit ' || s.document_number ELSE 'Crédit' END,
       r.establishment_id, r.created_by, r.created_at
FROM nexora.customer_receivable r
LEFT JOIN nexora.sale s ON s.id = r.sale_id
WHERE r.original_amount > 0;

INSERT INTO nexora.customer_ledger_entry
    (company_id, customer_id, occurred_at, entry_type, debit, credit, currency_id, receivable_id, payment_id,
     source_type, source_id, reference, description, establishment_id, created_by, created_at)
SELECT a.company_id, r.customer_id, a.allocated_at, 'payment', 0, a.allocated_amount, a.currency_id,
       a.receivable_id, a.payment_id, 'payment', a.payment_id, p.payment_number,
       'Versement sur ' || r.credit_number, r.establishment_id, a.created_by, a.created_at
FROM nexora.payment_allocation a
JOIN nexora.customer_receivable r ON r.id = a.receivable_id
JOIN nexora.payment p ON p.id = a.payment_id;

INSERT INTO nexora.customer_ledger_entry
    (company_id, customer_id, occurred_at, entry_type, debit, credit, currency_id, receivable_id,
     source_type, source_id, reference, description, establishment_id, created_by, created_at)
SELECT w.company_id, w.customer_id, w.written_off_at, 'write_off', 0, w.amount, r.currency_id, w.receivable_id,
       'receivable_write_off', w.id, r.credit_number, 'Radiation — ' || w.reason, r.establishment_id,
       w.created_by, w.created_at
FROM nexora.receivable_write_off w
JOIN nexora.customer_receivable r ON r.id = w.receivable_id;

-- =====================================================================
-- 7. Sécurité : isolation entreprise + audit (§23, §30)
-- =====================================================================
SELECT nexora.enable_company_rls('nexora.customer_credit_memo'::regclass);
SELECT nexora.enable_company_rls('nexora.customer_refund'::regclass);
SELECT nexora.enable_company_rls('nexora.customer_credit_application'::regclass);
SELECT nexora.enable_company_rls('nexora.customer_expense'::regclass);
SELECT nexora.enable_company_rls('nexora.collection_action'::regclass);
SELECT nexora.enable_company_rls('nexora.customer_ledger_entry'::regclass);

-- Le journal client est lui-même la trace immuable : pas d'audit ligne à ligne en double.
DO $$
DECLARE t text;
BEGIN
    FOREACH t IN ARRAY ARRAY['customer_credit_memo', 'customer_refund', 'customer_credit_application',
                             'customer_expense', 'collection_action'] LOOP
        EXECUTE format('DROP TRIGGER IF EXISTS trg_audit_%1$s ON nexora.%1$I', t);
        EXECUTE format(
            'CREATE TRIGGER trg_audit_%1$s AFTER INSERT OR UPDATE OR DELETE ON nexora.%1$I
               FOR EACH ROW EXECUTE FUNCTION nexora.audit_row()', t);
    END LOOP;
END;
$$;

-- =====================================================================
-- 8. Permissions (§18) — une action sensible = une permission
-- =====================================================================
INSERT INTO nexora.permission (code, name, module, action) VALUES
    ('credit.receive_payment',     'Crédit — enregistrer un versement client',              'credit',    'receive_payment'),
    ('credit.void_payment',        'Crédit — annuler un versement (contre-écriture)',       'credit',    'void_payment'),
    ('credit.refund',              'Crédit — approuver / exécuter un remboursement d''avoir', 'credit',  'refund'),
    ('customers.create_expense',   'Clients — saisir une dépense imputée',                  'customers', 'create_expense'),
    ('customers.approve_expense',  'Clients — valider / annuler une dépense imputée',       'customers', 'approve_expense')
ON CONFLICT (code) DO NOTHING;

-- Caissier : aucune de ces permissions par défaut (§18 « si autorisé » / « selon permission ») —
-- l'entreprise peut les accorder à un rôle personnalisé.
INSERT INTO nexora.role_permission (role_id, permission_id)
SELECT r.id, p.id
FROM nexora.role r
JOIN nexora.permission p ON
     (p.code = 'credit.receive_payment' AND r.name IN ('senior_cashier', 'salesperson', 'manager', 'accountant',
        'senior_accountant', 'chief_accountant', 'owner_direction', 'super_administrator', 'group_administrator'))
  OR (p.code = 'credit.void_payment' AND r.name IN ('manager', 'chief_accountant', 'owner_direction',
        'super_administrator', 'group_administrator'))
  OR (p.code = 'credit.refund' AND r.name IN ('manager', 'chief_accountant', 'owner_direction',
        'super_administrator', 'group_administrator'))
  OR (p.code = 'customers.create_expense' AND r.name IN ('senior_cashier', 'salesperson', 'manager', 'accountant',
        'senior_accountant', 'chief_accountant', 'owner_direction', 'super_administrator', 'group_administrator'))
  OR (p.code = 'customers.approve_expense' AND r.name IN ('manager', 'senior_accountant', 'chief_accountant',
        'owner_direction', 'super_administrator', 'group_administrator'))
WHERE r.company_id IS NULL
ON CONFLICT DO NOTHING;

-- =====================================================================
-- 9. Paramètres crédit (§12, §31 test 4 « selon la règle configurée »)
-- =====================================================================
INSERT INTO nexora.setting_definition (key, category, value_type, default_value, allowed_values, max_scope, is_sensitive, affects_future, description) VALUES
    ('credit.overpayment_policy', 'credit', 'enum', '"refuse"', '["refuse","credit_memo"]', 'company', false, true,
     'Versement supérieur au solde dû : refuser (défaut) ou convertir l''excédent en avoir disponible'),
    ('credit.default_due_days',   'credit', 'number', '30', '[]', 'company', false, true,
     'Échéance par défaut d''un crédit (jours) quand ni la vente ni la fiche client n''en fixent'),
    ('credit.near_limit_percent', 'credit', 'number', '90', '[]', 'company', false, false,
     'Seuil d''alerte « crédit proche de la limite » (% de la limite consommée)')
ON CONFLICT (key) DO NOTHING;

-- =====================================================================
-- 10. Notifications (§25) et job d'alertes crédit
-- =====================================================================
INSERT INTO nexora.message_template (company_id, key, channel, locale, subject, body, description, variables) VALUES
(NULL, 'credit.alert', 'in_app', 'fr',
 '{{alert.title}} — {{customer.name}}',
 '{{alert.detail}}',
 'Module clients §25 — alerte crédit (échéance dépassée, limite proche/dépassée, promesse échue)',
 '["alert.title","customer.name","alert.detail"]'::jsonb),
(NULL, 'customer.payment_event', 'in_app', 'fr',
 '{{event.title}} — {{customer.name}}',
 '{{event.detail}}',
 'Module clients §25 — versement reçu / versement annulé',
 '["event.title","customer.name","event.detail"]'::jsonb)
ON CONFLICT ON CONSTRAINT message_template_key_uk DO NOTHING;

INSERT INTO nexora.job_definition (key, name, category, description, owner) VALUES
    ('credit.scan_alerts', 'Alertes crédit client', 'business',
     'Créances échues, crédits proches ou au-delà de la limite, promesses de paiement échues (module clients §25).',
     'scheduler')
ON CONFLICT (key) DO NOTHING;

SELECT nexora.attach_common_triggers();
