-- =============================================================================
-- NEXORA — V012 — Paiements, créances clients et dettes fournisseurs (§12–§16)
-- PAYMENT N ─ N RECEIVABLE/PAYABLE via PAYMENT_ALLOCATION.
-- Soldes jamais négatifs (§31) : garantis par colonnes générées + CHECK.
-- =============================================================================
SET search_path TO nexora, public;

-- --- Paiement (objet financier générique §13) -------------------
CREATE TABLE nexora.payment (
    id                    uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    company_id            uuid        NOT NULL REFERENCES nexora.company(id),
    payment_number        text        NOT NULL,
    direction             text        NOT NULL,          -- inbound = encaissement, outbound = décaissement
    party_type            text,
    customer_id           uuid,
    supplier_id           uuid,
    payment_method_id     uuid,
    financial_account_id  uuid,
    cash_session_id       uuid,
    currency_id           uuid        NOT NULL REFERENCES nexora.currency(id),
    exchange_rate_id      uuid        REFERENCES nexora.exchange_rate(id),
    amount                nexora.money_amount NOT NULL,
    allocated_amount      nexora.money_amount NOT NULL DEFAULT 0,
    unallocated_amount    nexora.money_amount GENERATED ALWAYS AS (amount - allocated_amount) STORED,
    reference             text,
    status                text        NOT NULL DEFAULT 'completed',
    received_at           timestamptz NOT NULL DEFAULT now(),
    source_operation_uuid uuid,
    created_by            uuid REFERENCES nexora.app_user(id),
    created_at            timestamptz NOT NULL DEFAULT now(),
    updated_at            timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT payment_direction_chk CHECK (direction IN ('inbound','outbound')),
    CONSTRAINT payment_party_type_chk CHECK (party_type IS NULL OR party_type IN ('customer','supplier','other')),
    CONSTRAINT payment_status_chk CHECK (status IN ('draft','completed','void')),
    CONSTRAINT payment_amount_chk CHECK (amount > 0),
    CONSTRAINT payment_allocated_chk CHECK (allocated_amount >= 0 AND allocated_amount <= amount),
    CONSTRAINT payment_company_number_uk UNIQUE (company_id, payment_number),
    CONSTRAINT payment_company_id_uk     UNIQUE (id, company_id),
    CONSTRAINT payment_customer_fk FOREIGN KEY (customer_id, company_id)
        REFERENCES nexora.customer (id, company_id),
    CONSTRAINT payment_supplier_fk FOREIGN KEY (supplier_id, company_id)
        REFERENCES nexora.supplier (id, company_id),
    CONSTRAINT payment_method_fk FOREIGN KEY (payment_method_id, company_id)
        REFERENCES nexora.payment_method (id, company_id),
    CONSTRAINT payment_account_fk FOREIGN KEY (financial_account_id, company_id)
        REFERENCES nexora.financial_account (id, company_id),
    CONSTRAINT payment_cash_session_fk FOREIGN KEY (cash_session_id, company_id)
        REFERENCES nexora.cash_session (id, company_id)
);
CREATE INDEX payment_company_time_idx ON nexora.payment (company_id, received_at DESC);
CREATE INDEX payment_customer_idx     ON nexora.payment (customer_id);
CREATE INDEX payment_supplier_idx     ON nexora.payment (supplier_id);
CREATE INDEX payment_open_idx         ON nexora.payment (company_id) WHERE status = 'completed' AND allocated_amount < amount;
CREATE UNIQUE INDEX payment_source_op_uk
    ON nexora.payment (source_operation_uuid) WHERE source_operation_uuid IS NOT NULL;

-- --- Lien vente ↔ paiement (§14) ------------------------------
CREATE TABLE nexora.sale_payment (
    id          uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    sale_id     uuid        NOT NULL,
    company_id  uuid        NOT NULL,
    payment_id  uuid        NOT NULL,
    amount      nexora.money_amount NOT NULL,
    currency_id uuid        NOT NULL REFERENCES nexora.currency(id),
    created_at  timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT sale_payment_amount_chk CHECK (amount > 0),
    CONSTRAINT sale_payment_uk UNIQUE (sale_id, payment_id),
    CONSTRAINT sale_payment_sale_fk FOREIGN KEY (sale_id, company_id)
        REFERENCES nexora.sale (id, company_id) ON DELETE CASCADE,
    CONSTRAINT sale_payment_payment_fk FOREIGN KEY (payment_id, company_id)
        REFERENCES nexora.payment (id, company_id)
);
CREATE INDEX sale_payment_payment_idx ON nexora.sale_payment (payment_id);

-- --- Créance client (§15) -----------------------------------
CREATE TABLE nexora.customer_receivable (
    id               uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    company_id       uuid        NOT NULL REFERENCES nexora.company(id),
    sale_id          uuid,
    customer_id      uuid        NOT NULL,
    currency_id      uuid        NOT NULL REFERENCES nexora.currency(id),
    original_amount  nexora.money_amount NOT NULL,
    paid_amount      nexora.money_amount NOT NULL DEFAULT 0,
    remaining_amount nexora.money_amount GENERATED ALWAYS AS (original_amount - paid_amount) STORED,
    issue_date       date        NOT NULL DEFAULT current_date,
    due_date         date,
    status           text        NOT NULL DEFAULT 'OPEN',
    created_at       timestamptz NOT NULL DEFAULT now(),
    updated_at       timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT customer_receivable_status_chk CHECK (status IN (
        'OPEN','PARTIALLY_PAID','PAID','OVERDUE','CANCELLED')),
    CONSTRAINT customer_receivable_original_chk CHECK (original_amount >= 0),
    CONSTRAINT customer_receivable_paid_chk CHECK (paid_amount >= 0 AND paid_amount <= original_amount),
    CONSTRAINT customer_receivable_company_id_uk UNIQUE (id, company_id),
    CONSTRAINT customer_receivable_sale_fk FOREIGN KEY (sale_id, company_id)
        REFERENCES nexora.sale (id, company_id),
    CONSTRAINT customer_receivable_customer_fk FOREIGN KEY (customer_id, company_id)
        REFERENCES nexora.customer (id, company_id)
);
CREATE UNIQUE INDEX customer_receivable_sale_uk
    ON nexora.customer_receivable (sale_id) WHERE sale_id IS NOT NULL;
CREATE INDEX customer_receivable_due_idx
    ON nexora.customer_receivable (company_id, due_date) WHERE status IN ('OPEN','PARTIALLY_PAID','OVERDUE');
CREATE INDEX customer_receivable_customer_idx ON nexora.customer_receivable (customer_id);

-- --- Dette fournisseur (§13) -------------------------------
CREATE TABLE nexora.supplier_payable (
    id                 uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    company_id         uuid        NOT NULL REFERENCES nexora.company(id),
    supplier_invoice_id uuid,
    supplier_id        uuid        NOT NULL,
    currency_id        uuid        NOT NULL REFERENCES nexora.currency(id),
    original_amount    nexora.money_amount NOT NULL,
    paid_amount        nexora.money_amount NOT NULL DEFAULT 0,
    remaining_amount   nexora.money_amount GENERATED ALWAYS AS (original_amount - paid_amount) STORED,
    issue_date         date        NOT NULL DEFAULT current_date,
    due_date           date,
    status             text        NOT NULL DEFAULT 'OPEN',
    created_at         timestamptz NOT NULL DEFAULT now(),
    updated_at         timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT supplier_payable_status_chk CHECK (status IN (
        'OPEN','PARTIALLY_PAID','PAID','OVERDUE','CANCELLED')),
    CONSTRAINT supplier_payable_original_chk CHECK (original_amount >= 0),
    CONSTRAINT supplier_payable_paid_chk CHECK (paid_amount >= 0 AND paid_amount <= original_amount),
    CONSTRAINT supplier_payable_company_id_uk UNIQUE (id, company_id),
    CONSTRAINT supplier_payable_invoice_fk FOREIGN KEY (supplier_invoice_id, company_id)
        REFERENCES nexora.supplier_invoice (id, company_id),
    CONSTRAINT supplier_payable_supplier_fk FOREIGN KEY (supplier_id, company_id)
        REFERENCES nexora.supplier (id, company_id)
);
CREATE UNIQUE INDEX supplier_payable_invoice_uk
    ON nexora.supplier_payable (supplier_invoice_id) WHERE supplier_invoice_id IS NOT NULL;
CREATE INDEX supplier_payable_due_idx
    ON nexora.supplier_payable (company_id, due_date) WHERE status IN ('OPEN','PARTIALLY_PAID','OVERDUE');
CREATE INDEX supplier_payable_supplier_idx ON nexora.supplier_payable (supplier_id);

-- --- Affectation d'un paiement à une créance / dette (§13) --
CREATE TABLE nexora.payment_allocation (
    id               uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    company_id       uuid        NOT NULL,
    payment_id       uuid        NOT NULL,
    receivable_id    uuid,
    payable_id       uuid,
    allocated_amount nexora.money_amount NOT NULL,
    currency_id      uuid        NOT NULL REFERENCES nexora.currency(id),
    allocated_at     timestamptz NOT NULL DEFAULT now(),
    created_by       uuid REFERENCES nexora.app_user(id),
    created_at       timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT payment_allocation_amount_chk CHECK (allocated_amount > 0),
    CONSTRAINT payment_allocation_target_chk CHECK (
        (receivable_id IS NOT NULL AND payable_id IS NULL)
     OR (receivable_id IS NULL AND payable_id IS NOT NULL)),
    CONSTRAINT payment_allocation_payment_fk FOREIGN KEY (payment_id, company_id)
        REFERENCES nexora.payment (id, company_id) ON DELETE CASCADE,
    CONSTRAINT payment_allocation_receivable_fk FOREIGN KEY (receivable_id, company_id)
        REFERENCES nexora.customer_receivable (id, company_id),
    CONSTRAINT payment_allocation_payable_fk FOREIGN KEY (payable_id, company_id)
        REFERENCES nexora.supplier_payable (id, company_id)
);
CREATE INDEX payment_allocation_payment_idx    ON nexora.payment_allocation (payment_id);
CREATE INDEX payment_allocation_receivable_idx ON nexora.payment_allocation (receivable_id);
CREATE INDEX payment_allocation_payable_idx    ON nexora.payment_allocation (payable_id);

SELECT nexora.attach_common_triggers();
