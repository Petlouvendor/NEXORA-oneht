-- =============================================================================
-- NEXORA — V036 — Moteur de prix du catalogue (Phase 23 §6, §18, §22, §25, §37)
-- Le catalogue universel (produit, variantes, unités, conversions, catégories,
-- marques, taxes, prix historisés, promotions, coupons, recettes) est déjà en place
-- (V005, V007, V018, V024, V027). Cette migration ajoute la couche « prix explicable » :
--   price_list                : listes tarifaires nommées (COST/RETAIL/WHOLESALE/…) §18
--   product_price.tax_included : un prix peut être saisi TTC ou HT §25
--   product_price.price_list_id / establishment_id : portée d'un prix §18, §33
--   product_barcode : type, principal, statut, activation §6
--   product : nom court POS, code PLU §4-5
--   product.status : workflow brouillon → à valider → actif → suspendu → archivé §45
-- Aucune règle de prix cachée : le moteur restitue chaque étape appliquée (§22).
-- =============================================================================
SET search_path TO nexora, public;

-- Recherche tolérante (§37-38) : trigrammes (ILIKE accéléré + similarity) et
-- insensibilité aux accents (« paracet » trouve « Paracétamol »).
CREATE EXTENSION IF NOT EXISTS pg_trgm   SCHEMA public;
CREATE EXTENSION IF NOT EXISTS unaccent  SCHEMA public;

-- Wrapper IMMUTABLE : autorise l'indexation d'expressions unaccent().
CREATE OR REPLACE FUNCTION nexora.search_norm(t text)
RETURNS text LANGUAGE sql IMMUTABLE PARALLEL SAFE AS $$
    SELECT lower(public.unaccent('public.unaccent'::regdictionary, coalesce(t, '')))
$$;

-- ===================== 1. Listes tarifaires (§18) ========================
CREATE TABLE nexora.price_list (
    id           uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    company_id   uuid        NOT NULL REFERENCES nexora.company(id),
    code         text        NOT NULL,
    name         text        NOT NULL,
    list_type    text        NOT NULL DEFAULT 'custom',
    currency_id  uuid        REFERENCES nexora.currency(id),   -- NULL = toutes devises
    customer_group_id uuid,
    priority     smallint    NOT NULL DEFAULT 0,               -- la plus haute gagne
    status       text        NOT NULL DEFAULT 'active',
    created_at   timestamptz NOT NULL DEFAULT now(),
    updated_at   timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT price_list_type_chk CHECK (list_type IN (
        'cost','retail','wholesale','member','promotional','custom')),
    CONSTRAINT price_list_status_chk CHECK (status IN ('active','inactive','archived')),
    CONSTRAINT price_list_company_code_uk UNIQUE (company_id, code),
    CONSTRAINT price_list_company_id_uk   UNIQUE (id, company_id),
    CONSTRAINT price_list_group_fk FOREIGN KEY (customer_group_id, company_id)
        REFERENCES nexora.customer_group (id, company_id)
);
CREATE INDEX price_list_company_idx ON nexora.price_list (company_id, status);

-- ===================== 2. Portée & base d'un prix (§18, §25, §33) ========
ALTER TABLE nexora.product_price
    ADD COLUMN IF NOT EXISTS tax_included     boolean NOT NULL DEFAULT false,
    ADD COLUMN IF NOT EXISTS price_list_id    uuid,
    ADD COLUMN IF NOT EXISTS establishment_id uuid,
    ADD COLUMN IF NOT EXISTS min_price        nexora.money_amount;   -- plancher (remise max) §46

ALTER TABLE nexora.product_price DROP CONSTRAINT IF EXISTS product_price_list_fk;
ALTER TABLE nexora.product_price
    ADD CONSTRAINT product_price_list_fk FOREIGN KEY (price_list_id, company_id)
        REFERENCES nexora.price_list (id, company_id);
ALTER TABLE nexora.product_price DROP CONSTRAINT IF EXISTS product_price_establishment_fk;
ALTER TABLE nexora.product_price
    ADD CONSTRAINT product_price_establishment_fk FOREIGN KEY (establishment_id, company_id)
        REFERENCES nexora.establishment (id, company_id);

CREATE INDEX IF NOT EXISTS product_price_list_idx
    ON nexora.product_price (product_id, price_list_id, currency_id, valid_from DESC);

-- ===================== 3. Codes-barres : type / principal / statut (§6) ==
ALTER TABLE nexora.product_barcode
    ADD COLUMN IF NOT EXISTS barcode_type text NOT NULL DEFAULT 'ean13',
    ADD COLUMN IF NOT EXISTS is_primary   boolean NOT NULL DEFAULT false,
    ADD COLUMN IF NOT EXISTS status       text NOT NULL DEFAULT 'active',
    ADD COLUMN IF NOT EXISTS activated_at timestamptz NOT NULL DEFAULT now();

ALTER TABLE nexora.product_barcode DROP CONSTRAINT IF EXISTS product_barcode_type_chk;
ALTER TABLE nexora.product_barcode
    ADD CONSTRAINT product_barcode_type_chk CHECK (barcode_type IN (
        'ean13','ean8','upca','upce','code128','code39','itf14','qr','plu','internal','other')),
    ADD CONSTRAINT product_barcode_status_chk CHECK (status IN ('active','inactive'));

-- Un seul code-barres principal actif par produit.
CREATE UNIQUE INDEX IF NOT EXISTS product_barcode_primary_uk
    ON nexora.product_barcode (product_id) WHERE is_primary;

-- ===================== 4. Fiche produit : POS + PLU + workflow (§4-5, §45) =
ALTER TABLE nexora.product
    ADD COLUMN IF NOT EXISTS short_name       text,
    ADD COLUMN IF NOT EXISTS plu_code         text,
    ADD COLUMN IF NOT EXISTS manufacturer_ref text,
    ADD COLUMN IF NOT EXISTS internal_ref     text,
    ADD COLUMN IF NOT EXISTS search_aliases   text,
    ADD COLUMN IF NOT EXISTS image_url        text,
    ADD COLUMN IF NOT EXISTS notes            text;

CREATE UNIQUE INDEX IF NOT EXISTS product_company_plu_uk
    ON nexora.product (company_id, plu_code) WHERE plu_code IS NOT NULL;

-- Workflow produit (§45) : brouillon → à valider → actif → suspendu → archivé.
ALTER TABLE nexora.product DROP CONSTRAINT IF EXISTS product_status_chk;
ALTER TABLE nexora.product
    ADD CONSTRAINT product_status_chk CHECK (status IN (
        'draft','review','active','suspended','inactive','archived'));

-- Recherche tolérante (§37-38) : index trigramme sur nom / sku / alias / PLU normalisés.
CREATE INDEX IF NOT EXISTS product_search_trgm_idx ON nexora.product
    USING gin (nexora.search_norm(
        name || ' ' || sku || ' ' || coalesce(short_name,'') || ' ' ||
        coalesce(search_aliases,'') || ' ' || coalesce(plu_code,'')) gin_trgm_ops);

-- ===================== 5. RLS (§34) =====================================
SELECT nexora.enable_company_rls('nexora.price_list'::regclass);

-- ===================== 6. Permissions Phase 23 (§46) ====================
INSERT INTO nexora.permission (code, name, module, action) VALUES
    ('catalog.manage_promotion', 'Catalogue — gérer les promotions',   'catalog', 'manage_promotion'),
    ('catalog.manage_tax',       'Catalogue — gérer les taxes',        'catalog', 'manage_tax'),
    ('catalog.import',           'Catalogue — importer / exporter',    'catalog', 'import')
ON CONFLICT (code) DO NOTHING;

CREATE OR REPLACE FUNCTION pg_temp.grant_perms_v36(p_role text, p_patterns text[])
RETURNS void LANGUAGE sql AS $$
    INSERT INTO nexora.role_permission (role_id, permission_id)
    SELECT r.id, p.id FROM nexora.role r
    JOIN nexora.permission p ON p.code LIKE ANY (p_patterns)
    WHERE r.name = p_role AND r.system_role
    ON CONFLICT DO NOTHING;
$$;

SELECT pg_temp.grant_perms_v36('super_administrator', ARRAY['catalog.%']);
SELECT pg_temp.grant_perms_v36('group_administrator', ARRAY['catalog.%']);
SELECT pg_temp.grant_perms_v36('owner_direction',     ARRAY['catalog.%']);
SELECT pg_temp.grant_perms_v36('manager',             ARRAY['catalog.%']);
SELECT pg_temp.grant_perms_v36('purchasing_manager',  ARRAY['catalog.import','catalog.manage_price']);
-- Recherche / scan POS (§38) : les rôles de vente doivent consulter le catalogue.
SELECT pg_temp.grant_perms_v36('cashier',             ARRAY['catalog.view']);
SELECT pg_temp.grant_perms_v36('salesperson',         ARRAY['catalog.view']);

SELECT nexora.attach_common_triggers();
