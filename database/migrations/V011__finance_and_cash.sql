-- =============================================================================
-- NEXORA — V011 — Trésorerie : comptes financiers, caisses, dépenses (§16, §17, §18 CDC)
-- financial_account = caisse / banque / Mobile Money / autre.
-- =============================================================================
SET search_path TO nexora, public;

-- --- Compte financier ---------------------------------------------
CREATE TABLE nexora.financial_account (
    id                    uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    company_id            uuid        NOT NULL REFERENCES nexora.company(id),
    account_type          text        NOT NULL,
    name                  text        NOT NULL,
    account_number_masked text,
    currency_id           uuid        NOT NULL REFERENCES nexora.currency(id),
    opening_balance       nexora.money_amount NOT NULL DEFAULT 0,
    current_balance       nexora.money_amount NOT NULL DEFAULT 0,
    ledger_account_id     uuid,                       -- lien vers account (FK ajoutée en V013)
    status                text        NOT NULL DEFAULT 'active',
    settings              jsonb       NOT NULL DEFAULT '{}'::jsonb,
    created_at            timestamptz NOT NULL DEFAULT now(),
    updated_at           timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT financial_account_type_chk CHECK (account_type IN (
        'cash','bank','mobile_money','card_processor','wallet','other')),
    CONSTRAINT financial_account_status_chk CHECK (status IN ('active','inactive','closed')),
    CONSTRAINT financial_account_company_name_uk UNIQUE (company_id, name),
    CONSTRAINT financial_account_company_id_uk   UNIQUE (id, company_id)
);
CREATE INDEX financial_account_company_idx ON nexora.financial_account (company_id);

-- --- Transaction financière (mouvement de trésorerie) -----------
CREATE TABLE nexora.financial_transaction (
    id                     uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    company_id             uuid        NOT NULL,
    financial_account_id   uuid        NOT NULL,
    transaction_type       text        NOT NULL,
    amount                 nexora.money_amount NOT NULL,   -- signé : + entrée, - sortie
    currency_id            uuid        NOT NULL REFERENCES nexora.currency(id),
    exchange_rate_id       uuid        REFERENCES nexora.exchange_rate(id),
    counterparty_account_id uuid,                          -- transferts internes
    reference_type         text,
    reference_id           uuid,
    description            text,
    occurred_at            timestamptz NOT NULL DEFAULT now(),
    status                 text        NOT NULL DEFAULT 'cleared',
    source_operation_uuid  uuid,
    created_by             uuid REFERENCES nexora.app_user(id),
    created_at             timestamptz NOT NULL DEFAULT now(),
    updated_at             timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT financial_transaction_type_chk CHECK (transaction_type IN (
        'deposit','withdrawal','transfer_in','transfer_out','fee','interest','adjustment','opening')),
    CONSTRAINT financial_transaction_status_chk CHECK (status IN ('pending','cleared','void')),
    CONSTRAINT financial_transaction_amount_chk CHECK (amount <> 0),
    CONSTRAINT financial_transaction_account_fk FOREIGN KEY (financial_account_id, company_id)
        REFERENCES nexora.financial_account (id, company_id),
    CONSTRAINT financial_transaction_counterparty_fk FOREIGN KEY (counterparty_account_id, company_id)
        REFERENCES nexora.financial_account (id, company_id)
);
CREATE INDEX financial_transaction_account_time_idx
    ON nexora.financial_transaction (financial_account_id, occurred_at DESC);
CREATE INDEX financial_transaction_reference_idx ON nexora.financial_transaction (reference_type, reference_id);
CREATE UNIQUE INDEX financial_transaction_source_op_uk
    ON nexora.financial_transaction (source_operation_uuid) WHERE source_operation_uuid IS NOT NULL;

-- --- Caisse (§16) ---------------------------------------------
CREATE TABLE nexora.cash_register (
    id                   uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    company_id           uuid        NOT NULL REFERENCES nexora.company(id),
    store_id             uuid,
    financial_account_id uuid,
    name                 text        NOT NULL,
    code                 text        NOT NULL,
    currency_id          uuid        NOT NULL REFERENCES nexora.currency(id),
    status               text        NOT NULL DEFAULT 'active',
    created_at           timestamptz NOT NULL DEFAULT now(),
    updated_at           timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT cash_register_status_chk CHECK (status IN ('active','inactive','archived')),
    CONSTRAINT cash_register_company_code_uk UNIQUE (company_id, code),
    CONSTRAINT cash_register_company_id_uk   UNIQUE (id, company_id),
    CONSTRAINT cash_register_store_fk FOREIGN KEY (store_id, company_id)
        REFERENCES nexora.store (id, company_id),
    CONSTRAINT cash_register_account_fk FOREIGN KEY (financial_account_id, company_id)
        REFERENCES nexora.financial_account (id, company_id)
);
CREATE INDEX cash_register_company_idx ON nexora.cash_register (company_id);

CREATE TABLE nexora.cash_session (
    id                uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    company_id        uuid        NOT NULL,
    cash_register_id  uuid        NOT NULL,
    pos_terminal_id   uuid,
    user_id           uuid        NOT NULL REFERENCES nexora.app_user(id),
    opened_at         timestamptz NOT NULL DEFAULT now(),
    closed_at         timestamptz,
    opening_balance   nexora.money_amount NOT NULL DEFAULT 0,
    expected_balance  nexora.money_amount,
    counted_balance   nexora.money_amount,
    difference        nexora.money_amount GENERATED ALWAYS AS (counted_balance - expected_balance) STORED,
    status            text        NOT NULL DEFAULT 'open',
    notes             text,
    created_at        timestamptz NOT NULL DEFAULT now(),
    updated_at        timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT cash_session_status_chk CHECK (status IN ('open','closing','closed','reconciled')),
    CONSTRAINT cash_session_close_chk CHECK (
        (status IN ('open','closing')) OR (closed_at IS NOT NULL)),
    CONSTRAINT cash_session_company_id_uk UNIQUE (id, company_id),
    CONSTRAINT cash_session_register_fk FOREIGN KEY (cash_register_id, company_id)
        REFERENCES nexora.cash_register (id, company_id),
    CONSTRAINT cash_session_pos_terminal_fk FOREIGN KEY (pos_terminal_id, company_id)
        REFERENCES nexora.pos_terminal (id, company_id)
);
CREATE UNIQUE INDEX cash_session_one_open_uk
    ON nexora.cash_session (cash_register_id) WHERE status IN ('open','closing');
CREATE INDEX cash_session_company_idx ON nexora.cash_session (company_id, opened_at DESC);
CREATE INDEX cash_session_user_idx    ON nexora.cash_session (user_id, opened_at DESC);

CREATE TABLE nexora.cash_movement (
    id                    uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    company_id            uuid        NOT NULL,
    cash_session_id       uuid        NOT NULL,
    movement_type         text        NOT NULL,
    amount                nexora.money_amount NOT NULL,   -- signé
    currency_id           uuid        NOT NULL REFERENCES nexora.currency(id),
    reference_type        text,
    reference_id          uuid,
    note                  text,
    occurred_at           timestamptz NOT NULL DEFAULT now(),
    user_id               uuid REFERENCES nexora.app_user(id),
    source_operation_uuid uuid,
    created_at            timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT cash_movement_type_chk CHECK (movement_type IN (
        'opening_float','sale_payment','sale_refund','payout','payin','pickup','adjustment','closing')),
    CONSTRAINT cash_movement_amount_chk CHECK (amount <> 0),
    CONSTRAINT cash_movement_session_fk FOREIGN KEY (cash_session_id, company_id)
        REFERENCES nexora.cash_session (id, company_id) ON DELETE CASCADE
);
CREATE INDEX cash_movement_session_idx   ON nexora.cash_movement (cash_session_id, occurred_at);
CREATE INDEX cash_movement_reference_idx ON nexora.cash_movement (reference_type, reference_id);
CREATE UNIQUE INDEX cash_movement_source_op_uk
    ON nexora.cash_movement (source_operation_uuid) WHERE source_operation_uuid IS NOT NULL;

-- --- Dépenses (§18 CDC) --------------------------------------
CREATE TABLE nexora.expense (
    id                   uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    company_id           uuid        NOT NULL REFERENCES nexora.company(id),
    establishment_id     uuid,
    financial_account_id uuid,
    category_id          uuid,
    supplier_id          uuid,
    document_number      text        NOT NULL,
    description          text        NOT NULL DEFAULT '',
    amount               nexora.money_amount NOT NULL,
    tax_amount           nexora.money_amount NOT NULL DEFAULT 0,
    currency_id          uuid        NOT NULL REFERENCES nexora.currency(id),
    exchange_rate_id     uuid        REFERENCES nexora.exchange_rate(id),
    expense_date         date        NOT NULL DEFAULT current_date,
    status               text        NOT NULL DEFAULT 'draft',
    approval_request_id  uuid,
    approved_by          uuid REFERENCES nexora.app_user(id),
    paid_at              timestamptz,
    created_by           uuid REFERENCES nexora.app_user(id),
    created_at           timestamptz NOT NULL DEFAULT now(),
    updated_at           timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT expense_status_chk CHECK (status IN (
        'draft','submitted','approved','paid','rejected','cancelled')),
    CONSTRAINT expense_amount_chk CHECK (amount >= 0 AND tax_amount >= 0),
    CONSTRAINT expense_company_number_uk UNIQUE (company_id, document_number),
    CONSTRAINT expense_company_id_uk     UNIQUE (id, company_id),
    CONSTRAINT expense_establishment_fk FOREIGN KEY (establishment_id, company_id)
        REFERENCES nexora.establishment (id, company_id),
    CONSTRAINT expense_category_fk FOREIGN KEY (category_id, company_id)
        REFERENCES nexora.expense_category (id, company_id),
    CONSTRAINT expense_supplier_fk FOREIGN KEY (supplier_id, company_id)
        REFERENCES nexora.supplier (id, company_id),
    CONSTRAINT expense_account_fk FOREIGN KEY (financial_account_id, company_id)
        REFERENCES nexora.financial_account (id, company_id)
);
CREATE INDEX expense_company_date_idx ON nexora.expense (company_id, expense_date DESC);
CREATE INDEX expense_category_idx     ON nexora.expense (category_id);

-- --- Budgets et prévisions (§17) ----------------------------
CREATE TABLE nexora.budget (
    id           uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    company_id   uuid        NOT NULL REFERENCES nexora.company(id),
    name         text        NOT NULL,
    fiscal_year  integer     NOT NULL,
    currency_id  uuid        NOT NULL REFERENCES nexora.currency(id),
    status       text        NOT NULL DEFAULT 'draft',
    created_at   timestamptz NOT NULL DEFAULT now(),
    updated_at   timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT budget_status_chk CHECK (status IN ('draft','active','closed')),
    CONSTRAINT budget_company_name_year_uk UNIQUE (company_id, name, fiscal_year),
    CONSTRAINT budget_company_id_uk        UNIQUE (id, company_id)
);

CREATE TABLE nexora.budget_line (
    id             uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    budget_id      uuid        NOT NULL,
    company_id     uuid        NOT NULL,
    account_id     uuid,
    cost_center_id uuid,
    establishment_id uuid,
    period_month   smallint,                          -- NULL = montant annuel
    amount         nexora.money_amount NOT NULL DEFAULT 0,
    created_at     timestamptz NOT NULL DEFAULT now(),
    updated_at     timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT budget_line_month_chk CHECK (period_month IS NULL OR period_month BETWEEN 1 AND 12),
    CONSTRAINT budget_line_budget_fk FOREIGN KEY (budget_id, company_id)
        REFERENCES nexora.budget (id, company_id) ON DELETE CASCADE
);
CREATE INDEX budget_line_budget_idx ON nexora.budget_line (budget_id);

-- --- Rapprochement bancaire (§17) --------------------------
CREATE TABLE nexora.bank_statement (
    id                   uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    company_id           uuid        NOT NULL,
    financial_account_id uuid        NOT NULL,
    statement_number     text,
    statement_date       date        NOT NULL,
    opening_balance      nexora.money_amount NOT NULL DEFAULT 0,
    closing_balance      nexora.money_amount NOT NULL DEFAULT 0,
    status               text        NOT NULL DEFAULT 'imported',
    created_at           timestamptz NOT NULL DEFAULT now(),
    updated_at           timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT bank_statement_status_chk CHECK (status IN ('imported','reconciling','reconciled')),
    CONSTRAINT bank_statement_company_id_uk UNIQUE (id, company_id),
    CONSTRAINT bank_statement_account_fk FOREIGN KEY (financial_account_id, company_id)
        REFERENCES nexora.financial_account (id, company_id)
);
CREATE INDEX bank_statement_account_idx ON nexora.bank_statement (financial_account_id, statement_date DESC);

CREATE TABLE nexora.bank_statement_line (
    id                    uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    bank_statement_id     uuid        NOT NULL,
    company_id            uuid        NOT NULL,
    value_date            date        NOT NULL,
    amount                nexora.money_amount NOT NULL,
    description           text,
    reference             text,
    matched_transaction_id uuid       REFERENCES nexora.financial_transaction(id),
    status                text        NOT NULL DEFAULT 'unmatched',
    created_at            timestamptz NOT NULL DEFAULT now(),
    updated_at           timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT bank_statement_line_status_chk CHECK (status IN ('unmatched','matched','ignored')),
    CONSTRAINT bank_statement_line_stmt_fk FOREIGN KEY (bank_statement_id, company_id)
        REFERENCES nexora.bank_statement (id, company_id) ON DELETE CASCADE
);
CREATE INDEX bank_statement_line_stmt_idx ON nexora.bank_statement_line (bank_statement_id);

SELECT nexora.attach_common_triggers();
