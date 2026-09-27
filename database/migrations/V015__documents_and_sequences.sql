-- =============================================================================
-- NEXORA — V015 — Documents et numérotation (§24, §25 CDC)
-- Numérotation configurable, sans collision entre entreprises / établissements / types.
-- =============================================================================
SET search_path TO nexora, public;

CREATE TABLE nexora.document_sequence (
    id               uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    company_id       uuid        NOT NULL REFERENCES nexora.company(id),
    document_type    text        NOT NULL,
    establishment_id uuid,
    year             integer,
    prefix           text        NOT NULL DEFAULT '',
    suffix           text        NOT NULL DEFAULT '',
    separator        text        NOT NULL DEFAULT '-',
    include_year     boolean     NOT NULL DEFAULT true,
    padding          smallint    NOT NULL DEFAULT 6,
    increment_by     integer     NOT NULL DEFAULT 1,
    current_number   bigint      NOT NULL DEFAULT 0,
    format           text,                              -- gabarit avancé optionnel
    status           text        NOT NULL DEFAULT 'active',
    created_at       timestamptz NOT NULL DEFAULT now(),
    updated_at       timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT document_sequence_status_chk CHECK (status IN ('active','inactive')),
    CONSTRAINT document_sequence_padding_chk CHECK (padding BETWEEN 0 AND 20),
    CONSTRAINT document_sequence_increment_chk CHECK (increment_by >= 1),
    CONSTRAINT document_sequence_uk
        UNIQUE NULLS NOT DISTINCT (company_id, document_type, establishment_id, year),
    CONSTRAINT document_sequence_establishment_fk FOREIGN KEY (establishment_id, company_id)
        REFERENCES nexora.establishment (id, company_id)
);
CREATE INDEX document_sequence_company_idx ON nexora.document_sequence (company_id, document_type);

-- Attribution atomique du prochain numéro (§31 : unicité dans le périmètre).
CREATE OR REPLACE FUNCTION nexora.next_document_number(
    p_company_id        uuid,
    p_document_type     text,
    p_establishment_id  uuid DEFAULT NULL,
    p_year              integer DEFAULT NULL
) RETURNS text
LANGUAGE plpgsql
AS $$
DECLARE
    v_year   integer := coalesce(p_year, extract(year FROM current_date)::integer);
    v_row    nexora.document_sequence;
    v_number bigint;
BEGIN
    INSERT INTO nexora.document_sequence (company_id, document_type, establishment_id, year)
    VALUES (p_company_id, p_document_type, p_establishment_id, v_year)
    ON CONFLICT ON CONSTRAINT document_sequence_uk DO NOTHING;

    SELECT * INTO v_row
    FROM nexora.document_sequence
    WHERE company_id = p_company_id
      AND document_type = p_document_type
      AND establishment_id IS NOT DISTINCT FROM p_establishment_id
      AND year IS NOT DISTINCT FROM v_year
    FOR UPDATE;

    v_number := v_row.current_number + v_row.increment_by;

    UPDATE nexora.document_sequence
       SET current_number = v_number, updated_at = now()
     WHERE id = v_row.id;

    RETURN v_row.prefix
        || CASE WHEN v_row.include_year
                THEN v_year::text || v_row.separator
                ELSE '' END
        || lpad(v_number::text, v_row.padding, '0')
        || v_row.suffix;
END;
$$;

-- --- Documents émis sans table dédiée (devis, pro forma, relevés…) -------
CREATE TABLE nexora.issued_document (
    id              uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    company_id      uuid        NOT NULL REFERENCES nexora.company(id),
    establishment_id uuid,
    document_type   text        NOT NULL,
    document_number text        NOT NULL,
    reference_type  text,
    reference_id    uuid,
    party_type      text,
    party_id        uuid,
    issue_date      date        NOT NULL DEFAULT current_date,
    valid_until     date,
    currency_id     uuid        REFERENCES nexora.currency(id),
    total           nexora.money_amount NOT NULL DEFAULT 0,
    status          text        NOT NULL DEFAULT 'draft',
    file_reference  text,
    payload         jsonb       NOT NULL DEFAULT '{}'::jsonb,
    created_by      uuid        REFERENCES nexora.app_user(id),
    created_at      timestamptz NOT NULL DEFAULT now(),
    updated_at      timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT issued_document_type_chk CHECK (document_type IN (
        'quote','proforma','invoice','delivery_note','purchase_order',
        'credit_note','debit_note','customer_statement','supplier_statement','receipt','other')),
    CONSTRAINT issued_document_status_chk CHECK (status IN (
        'draft','issued','sent','accepted','rejected','expired','cancelled','converted')),
    CONSTRAINT issued_document_party_chk CHECK (party_type IS NULL OR party_type IN ('customer','supplier')),
    CONSTRAINT issued_document_company_number_uk UNIQUE (company_id, document_type, document_number),
    CONSTRAINT issued_document_establishment_fk FOREIGN KEY (establishment_id, company_id)
        REFERENCES nexora.establishment (id, company_id)
);
CREATE INDEX issued_document_company_idx   ON nexora.issued_document (company_id, document_type, issue_date DESC);
CREATE INDEX issued_document_reference_idx ON nexora.issued_document (reference_type, reference_id);

SELECT nexora.attach_common_triggers();
