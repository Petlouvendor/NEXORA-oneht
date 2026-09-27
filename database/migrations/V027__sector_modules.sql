-- =============================================================================
-- NEXORA — V027 — Modules sectoriels (Phase 8)
-- Le noyau (produit, stock, vente, paiement, finance, comptabilité) reste commun.
-- Cette migration ajoute : configuration sectorielle (activités + modules +
-- terminologie), moteur de champs personnalisés, et les entités restaurant
-- manquantes. Aucun système de vente / paiement / comptabilité parallèle (§22).
-- =============================================================================
SET search_path TO nexora, public;

-- ===================== 1. Configuration sectorielle (§3, §4, §27, §29) =========

-- Activités d'une entreprise — une principale, plusieurs secondaires possibles (§27).
CREATE TABLE nexora.company_activity (
    id            uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    company_id    uuid        NOT NULL REFERENCES nexora.company(id),
    activity_type text        NOT NULL,
    is_primary    boolean     NOT NULL DEFAULT false,
    created_at    timestamptz NOT NULL DEFAULT now(),
    updated_at    timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT company_activity_type_chk CHECK (activity_type IN (
        'supermarket','grocery','pharmacy','restaurant','boutique','hotel',
        'hardware_store','wholesaler','distributor','services','production',
        'ecommerce','agriculture','other')),
    CONSTRAINT company_activity_uk UNIQUE (company_id, activity_type)
);
CREATE UNIQUE INDEX company_activity_primary_uk
    ON nexora.company_activity (company_id) WHERE is_primary;
CREATE INDEX company_activity_company_idx ON nexora.company_activity (company_id);

-- Registre des modules activés / désactivés (§3). La désactivation ne supprime rien (§29).
CREATE TABLE nexora.company_module (
    id          uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    company_id  uuid        NOT NULL REFERENCES nexora.company(id),
    module_key  text        NOT NULL,
    enabled     boolean     NOT NULL DEFAULT true,
    config      jsonb       NOT NULL DEFAULT '{}'::jsonb,
    enabled_at  timestamptz NOT NULL DEFAULT now(),
    disabled_at timestamptz,
    created_at  timestamptz NOT NULL DEFAULT now(),
    updated_at  timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT company_module_key_chk CHECK (module_key IN (
        'pharmacy','restaurant','retail','wholesale','services','production',
        'boutique','hotel','ecommerce','agriculture','loyalty','delivery',
        'appointments','custom_fields','generic')),
    CONSTRAINT company_module_uk UNIQUE (company_id, module_key)
);
CREATE INDEX company_module_company_idx ON nexora.company_module (company_id) WHERE enabled;

-- Modules recommandés par activité — donnée de référence (pas de company_id).
CREATE TABLE nexora.sector_module_preset (
    activity_type text NOT NULL,
    module_key    text NOT NULL,
    PRIMARY KEY (activity_type, module_key)
);
INSERT INTO nexora.sector_module_preset (activity_type, module_key) VALUES
    ('pharmacy','pharmacy'), ('pharmacy','retail'), ('pharmacy','loyalty'),
    ('restaurant','restaurant'), ('restaurant','delivery'),
    ('supermarket','retail'), ('supermarket','loyalty'), ('supermarket','delivery'),
    ('grocery','retail'), ('grocery','loyalty'),
    ('boutique','boutique'), ('boutique','retail'), ('boutique','loyalty'),
    ('hotel','hotel'), ('hotel','restaurant'),
    ('hardware_store','retail'), ('hardware_store','wholesale'),
    ('wholesaler','wholesale'), ('wholesaler','delivery'),
    ('distributor','wholesale'), ('distributor','delivery'),
    ('services','services'), ('services','appointments'),
    ('production','production'), ('production','wholesale'),
    ('ecommerce','ecommerce'), ('ecommerce','retail'), ('ecommerce','delivery'),
    ('agriculture','agriculture'), ('agriculture','wholesale'),
    ('other','generic')
ON CONFLICT DO NOTHING;

-- Terminologie affichée, adaptable sans changer le sens des transactions (§18).
CREATE TABLE nexora.terminology_override (
    id         uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    company_id uuid        NOT NULL REFERENCES nexora.company(id),
    term_key   text        NOT NULL,
    locale     text        NOT NULL DEFAULT 'fr',
    label      text        NOT NULL,
    created_at timestamptz NOT NULL DEFAULT now(),
    updated_at timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT terminology_override_uk UNIQUE (company_id, term_key, locale)
);
CREATE INDEX terminology_override_company_idx ON nexora.terminology_override (company_id, locale);

-- ===================== 2. Moteur de champs personnalisés (§16) ================

CREATE TABLE nexora.custom_field (
    id               uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    company_id       uuid        NOT NULL REFERENCES nexora.company(id),
    entity_type      text        NOT NULL,
    field_key        text        NOT NULL,
    label            text        NOT NULL,
    data_type        text        NOT NULL,
    is_required      boolean     NOT NULL DEFAULT false,
    options          jsonb       NOT NULL DEFAULT '[]'::jsonb,   -- data_type = list
    reference_entity text,                                       -- data_type = reference
    visibility       jsonb       NOT NULL DEFAULT '{}'::jsonb,   -- règles de visibilité (§16)
    view_permission  text,                                       -- accès contrôlé (§16, §20)
    position         smallint    NOT NULL DEFAULT 0,
    status           text        NOT NULL DEFAULT 'active',
    created_at       timestamptz NOT NULL DEFAULT now(),
    updated_at       timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT custom_field_entity_chk CHECK (entity_type IN (
        'product','customer','supplier','sale','purchase_order','establishment',
        'employee','service','recipe','generic')),
    CONSTRAINT custom_field_type_chk CHECK (data_type IN (
        'text','number','date','boolean','list','reference','file')),
    CONSTRAINT custom_field_status_chk CHECK (status IN ('active','inactive')),
    CONSTRAINT custom_field_uk           UNIQUE (company_id, entity_type, field_key),
    CONSTRAINT custom_field_company_id_uk UNIQUE (id, company_id)
);
CREATE INDEX custom_field_entity_idx ON nexora.custom_field (company_id, entity_type) WHERE status = 'active';

CREATE TABLE nexora.custom_field_value (
    id              uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    company_id      uuid        NOT NULL,
    custom_field_id uuid        NOT NULL,
    entity_type     text        NOT NULL,
    entity_id       uuid        NOT NULL,
    value_text      text,
    value_number    numeric(20,6),
    value_date      date,
    value_boolean   boolean,
    value_reference uuid,
    value_json      jsonb,
    created_at      timestamptz NOT NULL DEFAULT now(),
    updated_at      timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT custom_field_value_uk UNIQUE (custom_field_id, entity_id),
    CONSTRAINT custom_field_value_field_fk FOREIGN KEY (custom_field_id, company_id)
        REFERENCES nexora.custom_field (id, company_id) ON DELETE CASCADE
);
CREATE INDEX custom_field_value_entity_idx ON nexora.custom_field_value (company_id, entity_type, entity_id);

-- ===================== 3. Restaurant — entités manquantes (§6) ================

-- Zones / salles.
CREATE TABLE nexora.restaurant_area (
    id               uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    company_id       uuid        NOT NULL REFERENCES nexora.company(id),
    establishment_id uuid        NOT NULL,
    name             text        NOT NULL,
    code             text        NOT NULL,
    status           text        NOT NULL DEFAULT 'active',
    created_at       timestamptz NOT NULL DEFAULT now(),
    updated_at       timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT restaurant_area_status_chk CHECK (status IN ('active','inactive')),
    CONSTRAINT restaurant_area_company_code_uk UNIQUE (company_id, code),
    CONSTRAINT restaurant_area_company_id_uk   UNIQUE (id, company_id),
    CONSTRAINT restaurant_area_establishment_fk FOREIGN KEY (establishment_id, company_id)
        REFERENCES nexora.establishment (id, company_id)
);
CREATE INDEX restaurant_area_company_idx ON nexora.restaurant_area (company_id);

ALTER TABLE nexora.restaurant_table
    ADD COLUMN area_id         uuid,
    ADD COLUMN current_sale_id uuid,
    ADD CONSTRAINT restaurant_table_area_fk FOREIGN KEY (area_id, company_id)
        REFERENCES nexora.restaurant_area (id, company_id),
    ADD CONSTRAINT restaurant_table_current_sale_fk FOREIGN KEY (current_sale_id, company_id)
        REFERENCES nexora.sale (id, company_id);

-- Contexte service : commande, priorité, notes, horodatage KDS (§6, §24).
ALTER TABLE nexora.sale_service_context
    ADD COLUMN notes      text,
    ADD COLUMN priority   smallint    NOT NULL DEFAULT 0,
    ADD COLUMN fired_at   timestamptz,
    ADD COLUMN ready_at   timestamptz,
    ADD COLUMN served_at  timestamptz;

-- Modificateurs / options d'une ligne (« sans oignon », « bien cuit ») (§6).
CREATE TABLE nexora.sale_line_modifier (
    id           uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    company_id   uuid        NOT NULL,
    sale_id      uuid        NOT NULL,
    sale_line_id uuid        NOT NULL,
    name         text        NOT NULL,
    price_delta  nexora.money_amount NOT NULL DEFAULT 0,
    created_at   timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT sale_line_modifier_sale_fk FOREIGN KEY (sale_id, company_id)
        REFERENCES nexora.sale (id, company_id) ON DELETE CASCADE
);
CREATE INDEX sale_line_modifier_line_idx ON nexora.sale_line_modifier (sale_line_id);

-- Poste de cuisine de préparation d'un ingrédient / plat (§6).
ALTER TABLE nexora.recipe_ingredient ADD COLUMN kitchen_station text;

-- ===================== 4. Permissions sectorielles (§20) =====================
INSERT INTO nexora.permission (code, name, module, action) VALUES
    ('sectors.configure',     'Secteurs — activités et modules',        'sectors',    'configure'),
    ('sectors.custom_fields', 'Secteurs — champs personnalisés',        'sectors',    'custom_fields'),
    ('sectors.terminology',   'Secteurs — terminologie affichée',       'sectors',    'terminology'),
    ('restaurant.manage',     'Restaurant — zones, tables, recettes',   'restaurant', 'manage'),
    ('restaurant.serve',      'Restaurant — prise de commande / service','restaurant','serve'),
    ('restaurant.kitchen',    'Restaurant — écran cuisine (KDS)',       'restaurant', 'kitchen'),
    ('recipes.manage',        'Recettes / nomenclatures — gérer',       'recipes',    'manage')
ON CONFLICT (code) DO NOTHING;

CREATE OR REPLACE FUNCTION pg_temp.grant_perms_v27(p_role text, p_patterns text[])
RETURNS void LANGUAGE sql AS $$
    INSERT INTO nexora.role_permission (role_id, permission_id)
    SELECT r.id, p.id FROM nexora.role r
    JOIN nexora.permission p ON p.code LIKE ANY (p_patterns)
    WHERE r.name = p_role AND r.system_role
    ON CONFLICT DO NOTHING;
$$;

SELECT pg_temp.grant_perms_v27('super_administrator', ARRAY['sectors.%','restaurant.%','recipes.%']);
SELECT pg_temp.grant_perms_v27('group_administrator', ARRAY['sectors.%','restaurant.%','recipes.%']);
SELECT pg_temp.grant_perms_v27('owner_direction',     ARRAY['sectors.%','restaurant.%','recipes.%']);
SELECT pg_temp.grant_perms_v27('manager',             ARRAY['sectors.%','restaurant.%','recipes.%']);
SELECT pg_temp.grant_perms_v27('stock_manager',       ARRAY['recipes.manage','restaurant.manage']);
SELECT pg_temp.grant_perms_v27('cashier',             ARRAY['restaurant.serve']);
SELECT pg_temp.grant_perms_v27('salesperson',         ARRAY['restaurant.serve']);

-- ===================== 5. RLS — rattrapage des tables post-V020 (§21) =========
-- Les migrations V024..V027 ajoutent des tables métier ; on ré-applique la
-- politique d'isolation entreprise à toute table à company_id NOT NULL qui ne
-- l'a pas encore.
DO $$
DECLARE
    r record;
    excluded text[] := ARRAY['audit_log','notification','schema_migration','sector_module_preset'];
BEGIN
    FOR r IN
        SELECT c.table_name
        FROM information_schema.columns c
        JOIN information_schema.tables t
          ON t.table_schema = c.table_schema AND t.table_name = c.table_name
        LEFT JOIN pg_tables pt
          ON pt.schemaname = 'nexora' AND pt.tablename = c.table_name
        WHERE c.table_schema = 'nexora'
          AND c.column_name  = 'company_id'
          AND c.is_nullable  = 'NO'
          AND t.table_type   = 'BASE TABLE'
          AND c.table_name <> ALL (excluded)
          AND (pt.rowsecurity IS NOT TRUE)
    LOOP
        PERFORM nexora.enable_company_rls(format('nexora.%I', r.table_name)::regclass);
    END LOOP;
END;
$$;

-- ===================== 6. Activité principale des entreprises existantes ======
-- Toute entreprise déjà présente reçoit son activité principale + les modules
-- recommandés par le preset (idempotent ; le jeu de démo complète ensuite).
INSERT INTO nexora.company_activity (company_id, activity_type, is_primary)
SELECT id, main_activity_type, true FROM nexora.company
ON CONFLICT (company_id, activity_type) DO NOTHING;

INSERT INTO nexora.company_module (company_id, module_key)
SELECT c.id, p.module_key
FROM nexora.company c
JOIN nexora.sector_module_preset p ON p.activity_type = c.main_activity_type
ON CONFLICT (company_id, module_key) DO NOTHING;

SELECT nexora.attach_common_triggers();
