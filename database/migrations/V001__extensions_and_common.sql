-- =============================================================================
-- NEXORA — V001 — Extensions, schéma, domaines et fonctions communes
-- Cible : PostgreSQL 14+ (validé sur PostgreSQL 18)
-- Dérivé de : docs/phase-2-modele-de-donnees.md
-- =============================================================================

-- --- Extensions (installées dans public, partagées) -------------------------
CREATE EXTENSION IF NOT EXISTS pgcrypto   SCHEMA public;  -- gen_random_uuid(), crypt()
CREATE EXTENSION IF NOT EXISTS citext     SCHEMA public;  -- identifiants insensibles à la casse
CREATE EXTENSION IF NOT EXISTS btree_gist SCHEMA public;  -- contraintes EXCLUDE (périodes comptables)

-- --- Schéma applicatif -----------------------------------------------------
CREATE SCHEMA IF NOT EXISTS nexora;

SET search_path TO nexora, public;

-- --- Suivi des migrations -------------------------------------------------
CREATE TABLE IF NOT EXISTS nexora.schema_migration (
    version     text        PRIMARY KEY,
    filename    text        NOT NULL,
    checksum    text,
    applied_at  timestamptz NOT NULL DEFAULT now(),
    applied_by  text        NOT NULL DEFAULT current_user
);

-- --- Domaines financiers (§3 : NUMERIC, jamais FLOAT) ----------------------
-- money_amount : montant signé, 4 décimales (suffisant HTG/USD/EUR/CAD ; §18).
CREATE DOMAIN nexora.money_amount   AS numeric(19,4);
-- quantity : quantités, y compris produits au poids (§13).
CREATE DOMAIN nexora.quantity       AS numeric(20,6);
-- exchange_rate_value : taux de change, forte précision (§18, §28 règles fin.).
CREATE DOMAIN nexora.exchange_rate_value AS numeric(24,12);
-- percentage : 0..100 (remises, TVA, répartitions analytiques).
CREATE DOMAIN nexora.percentage     AS numeric(9,6);

-- --- Fonction : maintien de updated_at -----------------------------------
CREATE OR REPLACE FUNCTION nexora.set_updated_at()
RETURNS trigger
LANGUAGE plpgsql
AS $$
BEGIN
    NEW.updated_at := now();
    RETURN NEW;
END;
$$;

-- Attache trg_set_updated_at à toute table du schéma nexora possédant updated_at.
-- Appelée en fin de chaque migration créant des tables.
CREATE OR REPLACE FUNCTION nexora.attach_common_triggers()
RETURNS void
LANGUAGE plpgsql
SET client_min_messages TO warning
AS $$
DECLARE
    r record;
BEGIN
    FOR r IN
        SELECT c.table_name
        FROM information_schema.columns c
        JOIN information_schema.tables t
          ON t.table_schema = c.table_schema
         AND t.table_name  = c.table_name
        WHERE c.table_schema = 'nexora'
          AND c.column_name  = 'updated_at'
          AND t.table_type   = 'BASE TABLE'
    LOOP
        EXECUTE format(
            'DROP TRIGGER IF EXISTS trg_set_updated_at ON nexora.%I', r.table_name);
        EXECUTE format(
            'CREATE TRIGGER trg_set_updated_at BEFORE UPDATE ON nexora.%I
               FOR EACH ROW EXECUTE FUNCTION nexora.set_updated_at()', r.table_name);
    END LOOP;
END;
$$;

-- =============================================================================
-- Contexte de session pour Row-Level Security (§34)
-- L'API applicative positionne ces variables par transaction :
--   SET LOCAL nexora.company_ids = '<uuid>,<uuid>';   -- périmètre entreprise
--   SET LOCAL nexora.group_ids   = '<uuid>,<uuid>';   -- périmètre groupe
--   SET LOCAL nexora.bypass_rls  = 'on';              -- tâches système / migrations
-- =============================================================================

CREATE OR REPLACE FUNCTION nexora.current_company_ids()
RETURNS uuid[]
LANGUAGE plpgsql
STABLE
AS $$
DECLARE
    raw text := current_setting('nexora.company_ids', true);
BEGIN
    IF raw IS NULL OR btrim(raw) = '' THEN
        RETURN ARRAY[]::uuid[];
    END IF;
    RETURN string_to_array(btrim(raw), ',')::uuid[];
EXCEPTION WHEN others THEN
    RETURN ARRAY[]::uuid[];
END;
$$;

CREATE OR REPLACE FUNCTION nexora.current_group_ids()
RETURNS uuid[]
LANGUAGE plpgsql
STABLE
AS $$
DECLARE
    raw text := current_setting('nexora.group_ids', true);
BEGIN
    IF raw IS NULL OR btrim(raw) = '' THEN
        RETURN ARRAY[]::uuid[];
    END IF;
    RETURN string_to_array(btrim(raw), ',')::uuid[];
EXCEPTION WHEN others THEN
    RETURN ARRAY[]::uuid[];
END;
$$;

CREATE OR REPLACE FUNCTION nexora.rls_bypass()
RETURNS boolean
LANGUAGE sql
STABLE
AS $$
    SELECT coalesce(current_setting('nexora.bypass_rls', true), 'off') = 'on';
$$;

-- Autorise l'accès à une ligne rattachée à company_id (NULL = donnée globale).
CREATE OR REPLACE FUNCTION nexora.rls_allows_company(target uuid)
RETURNS boolean
LANGUAGE sql
STABLE
AS $$
    SELECT nexora.rls_bypass()
        OR target IS NULL
        OR target = ANY (nexora.current_company_ids());
$$;

CREATE OR REPLACE FUNCTION nexora.rls_allows_group(target uuid)
RETURNS boolean
LANGUAGE sql
STABLE
AS $$
    SELECT nexora.rls_bypass()
        OR target IS NULL
        OR target = ANY (nexora.current_group_ids());
$$;

-- Applique RLS + politique d'isolation entreprise à une table portant company_id.
CREATE OR REPLACE FUNCTION nexora.enable_company_rls(p_table regclass)
RETURNS void
LANGUAGE plpgsql
AS $$
BEGIN
    EXECUTE format('ALTER TABLE %s ENABLE ROW LEVEL SECURITY', p_table);
    EXECUTE format('ALTER TABLE %s FORCE ROW LEVEL SECURITY', p_table);
    EXECUTE format('DROP POLICY IF EXISTS company_isolation ON %s', p_table);
    EXECUTE format(
        'CREATE POLICY company_isolation ON %s
           USING (nexora.rls_allows_company(company_id))
           WITH CHECK (nexora.rls_allows_company(company_id))', p_table);
END;
$$;

CREATE OR REPLACE FUNCTION nexora.enable_group_rls(p_table regclass)
RETURNS void
LANGUAGE plpgsql
AS $$
BEGIN
    EXECUTE format('ALTER TABLE %s ENABLE ROW LEVEL SECURITY', p_table);
    EXECUTE format('ALTER TABLE %s FORCE ROW LEVEL SECURITY', p_table);
    EXECUTE format('DROP POLICY IF EXISTS group_isolation ON %s', p_table);
    EXECUTE format(
        'CREATE POLICY group_isolation ON %s
           USING (nexora.rls_allows_group(group_id))
           WITH CHECK (nexora.rls_allows_group(group_id))', p_table);
END;
$$;
