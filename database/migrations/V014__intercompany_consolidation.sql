-- =============================================================================
-- NEXORA — V014 — Intercompany et consolidation (§10 CDC, §22, §23)
-- La consolidation part des comptabilités individuelles sans les altérer.
-- =============================================================================
SET search_path TO nexora, public;

-- --- Transaction inter-entreprises -----------------------------
CREATE TABLE nexora.intercompany_transaction (
    id                     uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    group_id               uuid        NOT NULL REFERENCES nexora.business_group(id),
    source_company_id      uuid        NOT NULL,
    target_company_id      uuid        NOT NULL,
    transaction_type       text        NOT NULL,
    source_reference_type  text,
    source_reference_id    uuid,
    target_reference_type  text,
    target_reference_id    uuid,
    amount                 nexora.money_amount NOT NULL,
    currency_id            uuid        NOT NULL REFERENCES nexora.currency(id),
    status                 text        NOT NULL DEFAULT 'draft',
    occurred_at            timestamptz NOT NULL DEFAULT now(),
    created_at             timestamptz NOT NULL DEFAULT now(),
    updated_at             timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT intercompany_transaction_type_chk CHECK (transaction_type IN (
        'sale_purchase','loan','transfer','cost_allocation','dividend','other')),
    CONSTRAINT intercompany_transaction_status_chk CHECK (status IN (
        'draft','confirmed','reconciled','eliminated')),
    CONSTRAINT intercompany_transaction_distinct_chk CHECK (source_company_id <> target_company_id),
    CONSTRAINT intercompany_transaction_amount_chk CHECK (amount >= 0),
    CONSTRAINT intercompany_transaction_source_fk FOREIGN KEY (source_company_id, group_id)
        REFERENCES nexora.company (id, group_id),
    CONSTRAINT intercompany_transaction_target_fk FOREIGN KEY (target_company_id, group_id)
        REFERENCES nexora.company (id, group_id)
);
CREATE INDEX intercompany_transaction_group_idx  ON nexora.intercompany_transaction (group_id, occurred_at DESC);
CREATE INDEX intercompany_transaction_source_idx ON nexora.intercompany_transaction (source_company_id);
CREATE INDEX intercompany_transaction_target_idx ON nexora.intercompany_transaction (target_company_id);
CREATE INDEX intercompany_transaction_src_ref_idx ON nexora.intercompany_transaction (source_reference_type, source_reference_id);

-- --- Profil de consolidation ---------------------------------
CREATE TABLE nexora.consolidation_profile (
    id           uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    group_id     uuid        NOT NULL REFERENCES nexora.business_group(id),
    name         text        NOT NULL,
    currency_id  uuid        NOT NULL REFERENCES nexora.currency(id),
    status       text        NOT NULL DEFAULT 'draft',
    created_at   timestamptz NOT NULL DEFAULT now(),
    updated_at   timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT consolidation_profile_status_chk CHECK (status IN ('draft','active','archived')),
    CONSTRAINT consolidation_profile_group_name_uk UNIQUE (group_id, name),
    CONSTRAINT consolidation_profile_group_id_uk   UNIQUE (id, group_id)
);

CREATE TABLE nexora.consolidation_scope (
    id                      uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    consolidation_profile_id uuid       NOT NULL,
    group_id                uuid        NOT NULL,
    company_id              uuid        NOT NULL,
    ownership_percentage    nexora.percentage NOT NULL DEFAULT 100,
    method                  text        NOT NULL DEFAULT 'full',
    effective_from          date        NOT NULL DEFAULT current_date,
    effective_to            date,
    created_at              timestamptz NOT NULL DEFAULT now(),
    updated_at              timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT consolidation_scope_method_chk CHECK (method IN ('full','equity','proportional')),
    CONSTRAINT consolidation_scope_pct_chk CHECK (ownership_percentage >= 0 AND ownership_percentage <= 100),
    CONSTRAINT consolidation_scope_period_chk CHECK (effective_to IS NULL OR effective_to >= effective_from),
    CONSTRAINT consolidation_scope_uk UNIQUE (consolidation_profile_id, company_id, effective_from),
    CONSTRAINT consolidation_scope_profile_fk FOREIGN KEY (consolidation_profile_id, group_id)
        REFERENCES nexora.consolidation_profile (id, group_id) ON DELETE CASCADE,
    CONSTRAINT consolidation_scope_company_fk FOREIGN KEY (company_id, group_id)
        REFERENCES nexora.company (id, group_id)
);
CREATE INDEX consolidation_scope_profile_idx ON nexora.consolidation_scope (consolidation_profile_id);

-- --- Ajustement / élimination de consolidation --------------
CREATE TABLE nexora.consolidation_adjustment (
    id                       uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    consolidation_profile_id uuid       NOT NULL,
    group_id                 uuid        NOT NULL,
    company_id               uuid,                        -- NULL = ajustement au niveau groupe
    account_id               uuid        REFERENCES nexora.account(id),
    account_code             text,
    adjustment_type          text        NOT NULL,
    amount                   nexora.money_amount NOT NULL,
    currency_id              uuid        NOT NULL REFERENCES nexora.currency(id),
    period_label             text,
    reason                   text        NOT NULL,
    created_by               uuid        REFERENCES nexora.app_user(id),
    created_at               timestamptz NOT NULL DEFAULT now(),
    updated_at               timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT consolidation_adjustment_type_chk CHECK (adjustment_type IN (
        'elimination','reclassification','fx_translation','minority_interest','goodwill','other')),
    CONSTRAINT consolidation_adjustment_profile_fk FOREIGN KEY (consolidation_profile_id, group_id)
        REFERENCES nexora.consolidation_profile (id, group_id) ON DELETE CASCADE
);
CREATE INDEX consolidation_adjustment_profile_idx ON nexora.consolidation_adjustment (consolidation_profile_id);

SELECT nexora.attach_common_triggers();
