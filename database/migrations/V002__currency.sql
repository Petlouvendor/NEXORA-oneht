-- =============================================================================
-- NEXORA — V002 — Devises et taux de change (§7, §18)
-- Référentiels globaux : pas de company_id, pas de RLS.
-- =============================================================================
SET search_path TO nexora, public;

CREATE TABLE nexora.currency (
    id              uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    code            varchar(3)  NOT NULL,                 -- ISO 4217 : HTG, USD…
    name            text        NOT NULL,
    symbol          text        NOT NULL DEFAULT '',
    decimal_places  smallint    NOT NULL DEFAULT 2,
    status          text        NOT NULL DEFAULT 'active',
    created_at      timestamptz NOT NULL DEFAULT now(),
    updated_at      timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT currency_code_key      UNIQUE (code),
    CONSTRAINT currency_status_chk    CHECK (status IN ('active','inactive')),
    CONSTRAINT currency_decimals_chk  CHECK (decimal_places BETWEEN 0 AND 6)
);

-- Taux de change historisé (§18 : « taux de change historique par transaction »).
-- rate = nombre d'unités de quote_currency pour 1 unité de base_currency.
CREATE TABLE nexora.exchange_rate (
    id                 uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    base_currency_id   uuid NOT NULL REFERENCES nexora.currency(id),
    quote_currency_id  uuid NOT NULL REFERENCES nexora.currency(id),
    rate               nexora.exchange_rate_value NOT NULL,
    effective_at       timestamptz NOT NULL,
    source             text        NOT NULL DEFAULT 'manual',
    created_at         timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT exchange_rate_rate_chk       CHECK (rate > 0),
    CONSTRAINT exchange_rate_distinct_chk   CHECK (base_currency_id <> quote_currency_id),
    CONSTRAINT exchange_rate_unique_point   UNIQUE (base_currency_id, quote_currency_id, effective_at, source)
);

CREATE INDEX exchange_rate_lookup_idx
    ON nexora.exchange_rate (base_currency_id, quote_currency_id, effective_at DESC);

SELECT nexora.attach_common_triggers();
