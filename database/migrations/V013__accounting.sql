-- =============================================================================
-- NEXORA — V013 — Comptabilité en partie double (§9, §19, §20, §21)
-- Comptabilité indépendante par entreprise. SUM(debit) = SUM(credit) : trigger V019.
-- =============================================================================
SET search_path TO nexora, public;

-- --- Plan comptable ---------------------------------------------
CREATE TABLE nexora.chart_of_accounts (
    id           uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    company_id   uuid        NOT NULL REFERENCES nexora.company(id),
    name         text        NOT NULL,
    version      text        NOT NULL DEFAULT '1',
    status       text        NOT NULL DEFAULT 'draft',
    created_at   timestamptz NOT NULL DEFAULT now(),
    updated_at   timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT chart_of_accounts_status_chk CHECK (status IN ('draft','active','archived')),
    CONSTRAINT chart_of_accounts_company_id_uk UNIQUE (id, company_id)
);
CREATE UNIQUE INDEX chart_of_accounts_active_uk
    ON nexora.chart_of_accounts (company_id) WHERE status = 'active';

-- --- Compte -----------------------------------------------------
CREATE TABLE nexora.account (
    id                   uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    chart_of_accounts_id uuid        NOT NULL,
    company_id           uuid        NOT NULL,
    code                 text        NOT NULL,
    name                 text        NOT NULL,
    account_type         text        NOT NULL,
    parent_account_id    uuid        REFERENCES nexora.account(id),
    is_postable          boolean     NOT NULL DEFAULT true,
    currency_id          uuid        REFERENCES nexora.currency(id),
    status               text        NOT NULL DEFAULT 'active',
    created_at           timestamptz NOT NULL DEFAULT now(),
    updated_at           timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT account_type_chk CHECK (account_type IN ('asset','liability','equity','revenue','expense')),
    CONSTRAINT account_status_chk CHECK (status IN ('active','inactive')),
    CONSTRAINT account_chart_code_uk UNIQUE (chart_of_accounts_id, code),
    CONSTRAINT account_company_id_uk UNIQUE (id, company_id),
    CONSTRAINT account_chart_fk FOREIGN KEY (chart_of_accounts_id, company_id)
        REFERENCES nexora.chart_of_accounts (id, company_id)
);
CREATE INDEX account_company_idx ON nexora.account (company_id);
CREATE INDEX account_parent_idx  ON nexora.account (parent_account_id);

-- --- Période comptable (§9 : périodes et clôtures) ------------
CREATE TABLE nexora.accounting_period (
    id           uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    company_id   uuid        NOT NULL REFERENCES nexora.company(id),
    name         text        NOT NULL,
    start_date   date        NOT NULL,
    end_date     date        NOT NULL,
    status       text        NOT NULL DEFAULT 'open',
    closed_at    timestamptz,
    closed_by    uuid        REFERENCES nexora.app_user(id),
    created_at   timestamptz NOT NULL DEFAULT now(),
    updated_at   timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT accounting_period_status_chk CHECK (status IN ('open','closed','locked')),
    CONSTRAINT accounting_period_range_chk  CHECK (end_date >= start_date),
    CONSTRAINT accounting_period_company_id_uk UNIQUE (id, company_id),
    CONSTRAINT accounting_period_no_overlap EXCLUDE USING gist (
        company_id WITH =,
        daterange(start_date, end_date, '[]') WITH &&)
);
CREATE INDEX accounting_period_company_idx ON nexora.accounting_period (company_id, start_date);

-- --- Journal --------------------------------------------------
CREATE TABLE nexora.journal (
    id           uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    company_id   uuid        NOT NULL REFERENCES nexora.company(id),
    code         text        NOT NULL,
    name         text        NOT NULL,
    journal_type text        NOT NULL DEFAULT 'general',
    status       text        NOT NULL DEFAULT 'active',
    created_at   timestamptz NOT NULL DEFAULT now(),
    updated_at   timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT journal_type_chk CHECK (journal_type IN (
        'sales','purchases','cash','bank','general','opening','closing','inventory','payroll')),
    CONSTRAINT journal_status_chk CHECK (status IN ('active','inactive')),
    CONSTRAINT journal_company_code_uk UNIQUE (company_id, code),
    CONSTRAINT journal_company_id_uk   UNIQUE (id, company_id)
);

-- --- Centre de coûts (§21) ----------------------------------
CREATE TABLE nexora.cost_center (
    id           uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    company_id   uuid        NOT NULL REFERENCES nexora.company(id),
    code         text        NOT NULL,
    name         text        NOT NULL,
    parent_id    uuid        REFERENCES nexora.cost_center(id),
    status       text        NOT NULL DEFAULT 'active',
    created_at   timestamptz NOT NULL DEFAULT now(),
    updated_at   timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT cost_center_status_chk CHECK (status IN ('active','inactive','archived')),
    CONSTRAINT cost_center_company_code_uk UNIQUE (company_id, code),
    CONSTRAINT cost_center_company_id_uk   UNIQUE (id, company_id)
);
CREATE INDEX cost_center_company_idx ON nexora.cost_center (company_id);

-- --- Écriture comptable -----------------------------------
CREATE TABLE nexora.journal_entry (
    id                   uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    company_id           uuid        NOT NULL REFERENCES nexora.company(id),
    journal_id           uuid        NOT NULL,
    accounting_period_id uuid        NOT NULL,
    entry_number         text        NOT NULL,
    entry_date           date        NOT NULL DEFAULT current_date,
    source_type          text,                          -- §20 : source métier identifiable
    source_id            uuid,
    description          text,
    status               text        NOT NULL DEFAULT 'draft',
    posted_at            timestamptz,
    posted_by            uuid        REFERENCES nexora.app_user(id),
    reversal_of_entry_id uuid        REFERENCES nexora.journal_entry(id),
    is_intercompany      boolean     NOT NULL DEFAULT false,
    created_by           uuid        REFERENCES nexora.app_user(id),
    created_at           timestamptz NOT NULL DEFAULT now(),
    updated_at           timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT journal_entry_status_chk CHECK (status IN ('draft','posted','reversed','cancelled')),
    CONSTRAINT journal_entry_number_uk UNIQUE (company_id, journal_id, entry_number),
    CONSTRAINT journal_entry_company_id_uk UNIQUE (id, company_id),
    CONSTRAINT journal_entry_journal_fk FOREIGN KEY (journal_id, company_id)
        REFERENCES nexora.journal (id, company_id),
    CONSTRAINT journal_entry_period_fk FOREIGN KEY (accounting_period_id, company_id)
        REFERENCES nexora.accounting_period (id, company_id)
);
CREATE INDEX journal_entry_company_date_idx ON nexora.journal_entry (company_id, entry_date DESC);
CREATE INDEX journal_entry_period_idx       ON nexora.journal_entry (accounting_period_id);
CREATE INDEX journal_entry_source_idx       ON nexora.journal_entry (source_type, source_id);

CREATE TABLE nexora.journal_entry_line (
    id               uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    journal_entry_id uuid        NOT NULL,
    company_id       uuid        NOT NULL,
    account_id       uuid        NOT NULL,
    line_number      integer     NOT NULL DEFAULT 1,
    description      text,
    debit            nexora.money_amount NOT NULL DEFAULT 0,
    credit           nexora.money_amount NOT NULL DEFAULT 0,
    currency_id      uuid        NOT NULL REFERENCES nexora.currency(id),
    exchange_rate_id uuid        REFERENCES nexora.exchange_rate(id),
    base_debit       nexora.money_amount NOT NULL DEFAULT 0,   -- converti en devise entreprise
    base_credit      nexora.money_amount NOT NULL DEFAULT 0,
    third_party_type text,
    third_party_id   uuid,
    cost_center_id   uuid,
    created_at       timestamptz NOT NULL DEFAULT now(),
    updated_at       timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT journal_entry_line_sign_chk CHECK (debit >= 0 AND credit >= 0),
    CONSTRAINT journal_entry_line_xor_chk  CHECK ((debit = 0) <> (credit = 0)),
    CONSTRAINT journal_entry_line_company_id_uk UNIQUE (id, company_id),
    CONSTRAINT journal_entry_line_entry_fk FOREIGN KEY (journal_entry_id, company_id)
        REFERENCES nexora.journal_entry (id, company_id) ON DELETE CASCADE,
    CONSTRAINT journal_entry_line_account_fk FOREIGN KEY (account_id, company_id)
        REFERENCES nexora.account (id, company_id),
    CONSTRAINT journal_entry_line_cost_center_fk FOREIGN KEY (cost_center_id, company_id)
        REFERENCES nexora.cost_center (id, company_id)
);
CREATE INDEX journal_entry_line_entry_idx   ON nexora.journal_entry_line (journal_entry_id);
CREATE INDEX journal_entry_line_account_idx ON nexora.journal_entry_line (account_id);
CREATE INDEX journal_entry_line_third_party_idx ON nexora.journal_entry_line (third_party_type, third_party_id);

-- --- Répartition analytique (§21) -------------------------
CREATE TABLE nexora.entry_analytic_allocation (
    id                    uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    company_id            uuid        NOT NULL,
    journal_entry_line_id uuid        NOT NULL,
    cost_center_id        uuid        NOT NULL,
    percentage            nexora.percentage NOT NULL DEFAULT 0,
    amount                nexora.money_amount NOT NULL DEFAULT 0,
    created_at            timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT entry_analytic_allocation_pct_chk CHECK (percentage >= 0 AND percentage <= 100),
    CONSTRAINT entry_analytic_allocation_line_fk FOREIGN KEY (journal_entry_line_id, company_id)
        REFERENCES nexora.journal_entry_line (id, company_id) ON DELETE CASCADE,
    CONSTRAINT entry_analytic_allocation_cc_fk FOREIGN KEY (cost_center_id, company_id)
        REFERENCES nexora.cost_center (id, company_id)
);
CREATE INDEX entry_analytic_allocation_line_idx ON nexora.entry_analytic_allocation (journal_entry_line_id);

-- --- Liens différés vers la comptabilité -----------------
ALTER TABLE nexora.financial_account
    ADD CONSTRAINT financial_account_ledger_fk FOREIGN KEY (ledger_account_id, company_id)
        REFERENCES nexora.account (id, company_id);

ALTER TABLE nexora.budget_line
    ADD CONSTRAINT budget_line_account_fk FOREIGN KEY (account_id, company_id)
        REFERENCES nexora.account (id, company_id),
    ADD CONSTRAINT budget_line_cost_center_fk FOREIGN KEY (cost_center_id, company_id)
        REFERENCES nexora.cost_center (id, company_id),
    ADD CONSTRAINT budget_line_establishment_fk FOREIGN KEY (establishment_id, company_id)
        REFERENCES nexora.establishment (id, company_id);

SELECT nexora.attach_common_triggers();
