-- =============================================================================
-- NEXORA — V037 — Cycle de vie des lots & détail des emplacements (Phase 24)
-- Le moteur de stock (emplacements, soldes, mouvements immuables, lots, lot_stock,
-- transferts, inventaires, ajustements, FEFO, réservations, réappro) est déjà en place
-- (V003, V008, V024). Cette migration ajoute :
--   lot                : blocage / rappel / traçabilité fournisseur & réception (§12-15)
--   inventory_location : types §5 + code/zone/rayon/étagère/bin/capacité/responsable (§6)
--   warehouse          : responsable, adresse, capacité (§4)
--   settings           : jours d'alerte d'expiration, blocage vente des lots expirés (§13)
--   permissions        : gérer les lots, voir la valorisation, gérer les entrepôts (§42)
-- =============================================================================
SET search_path TO nexora, public;

-- ===================== 1. Lot : blocage, rappel, traçabilité (§12-15) =====
ALTER TABLE nexora.lot
    ADD COLUMN IF NOT EXISTS supplier_id     uuid,
    ADD COLUMN IF NOT EXISTS received_at     date,
    ADD COLUMN IF NOT EXISTS blocked_at      timestamptz,
    ADD COLUMN IF NOT EXISTS blocked_reason  text,
    ADD COLUMN IF NOT EXISTS blocked_by      uuid REFERENCES nexora.app_user(id),
    ADD COLUMN IF NOT EXISTS recall_reference text;

ALTER TABLE nexora.lot DROP CONSTRAINT IF EXISTS lot_supplier_fk;
ALTER TABLE nexora.lot
    ADD CONSTRAINT lot_supplier_fk FOREIGN KEY (supplier_id, company_id)
        REFERENCES nexora.supplier (id, company_id);

-- lot.status : 'blocked' rejoint quarantine/expired/recalled/depleted.
ALTER TABLE nexora.lot DROP CONSTRAINT IF EXISTS lot_status_chk;
ALTER TABLE nexora.lot
    ADD CONSTRAINT lot_status_chk CHECK (status IN (
        'active','quarantine','blocked','expired','recalled','depleted'));

-- Requêtes « lots proches de la péremption » (§13, §40).
CREATE INDEX IF NOT EXISTS lot_company_expiry_idx
    ON nexora.lot (company_id, expiration_date) WHERE expiration_date IS NOT NULL AND status = 'active';

-- ===================== 2. Emplacements : types & détail (§5-6) ===========
ALTER TABLE nexora.inventory_location
    ADD COLUMN IF NOT EXISTS code            text,
    ADD COLUMN IF NOT EXISTS zone            text,
    ADD COLUMN IF NOT EXISTS aisle           text,
    ADD COLUMN IF NOT EXISTS shelf           text,
    ADD COLUMN IF NOT EXISTS bin             text,
    ADD COLUMN IF NOT EXISTS capacity        numeric(20,6),
    ADD COLUMN IF NOT EXISTS manager_user_id uuid REFERENCES nexora.app_user(id),
    ADD COLUMN IF NOT EXISTS preferred_product_id uuid;

ALTER TABLE nexora.inventory_location DROP CONSTRAINT IF EXISTS inventory_location_type_chk;
ALTER TABLE nexora.inventory_location
    ADD CONSTRAINT inventory_location_type_chk CHECK (location_type IN (
        'store','warehouse','reserve','cold_room','pharmacy','secure',
        'kitchen','production','transit','return','quarantine','virtual'));

-- Les types non physiques ne réclament pas de warehouse/store.
ALTER TABLE nexora.inventory_location DROP CONSTRAINT IF EXISTS inventory_location_target_chk;
ALTER TABLE nexora.inventory_location
    ADD CONSTRAINT inventory_location_target_chk CHECK (
        warehouse_id IS NOT NULL OR store_id IS NOT NULL
        OR location_type IN ('virtual','transit','production','return','quarantine'));

ALTER TABLE nexora.inventory_location DROP CONSTRAINT IF EXISTS inventory_location_code_uk;
CREATE UNIQUE INDEX IF NOT EXISTS inventory_location_code_uk
    ON nexora.inventory_location (company_id, code) WHERE code IS NOT NULL;

-- ===================== 3. Entrepôts : responsable, adresse, capacité (§4) =
ALTER TABLE nexora.warehouse
    ADD COLUMN IF NOT EXISTS manager_user_id uuid REFERENCES nexora.app_user(id),
    ADD COLUMN IF NOT EXISTS address         text,
    ADD COLUMN IF NOT EXISTS capacity        numeric(20,6);

-- ===================== 4. Paramètres d'expiration (§13) ==================
INSERT INTO nexora.setting_definition (key, category, value_type, default_value, allowed_values, max_scope, is_sensitive, affects_future, description) VALUES
    ('stock.expiry_alert_days',   'stock', 'number',  '30',   '[]', 'establishment', false, false, 'Jours avant expiration déclenchant l''alerte'),
    ('stock.block_expired_sales', 'stock', 'boolean', 'true', '[]', 'establishment', false, true,  'Empêcher la vente d''un lot expiré')
ON CONFLICT (key) DO NOTHING;

-- ===================== 5. Permissions Phase 24 (§42-43) =================
INSERT INTO nexora.permission (code, name, module, action) VALUES
    ('inventory.manage_lot',       'Stock — bloquer / débloquer / rappeler un lot', 'inventory', 'manage_lot'),
    ('inventory.view_valuation',   'Stock — voir la valorisation',                  'inventory', 'view_valuation'),
    ('inventory.manage_warehouse', 'Stock — gérer entrepôts & emplacements',        'inventory', 'manage_warehouse')
ON CONFLICT (code) DO NOTHING;

CREATE OR REPLACE FUNCTION pg_temp.grant_perms_v37(p_role text, p_patterns text[])
RETURNS void LANGUAGE sql AS $$
    INSERT INTO nexora.role_permission (role_id, permission_id)
    SELECT r.id, p.id FROM nexora.role r
    JOIN nexora.permission p ON p.code LIKE ANY (p_patterns)
    WHERE r.name = p_role AND r.system_role
    ON CONFLICT DO NOTHING;
$$;

SELECT pg_temp.grant_perms_v37('super_administrator', ARRAY['inventory.%']);
SELECT pg_temp.grant_perms_v37('group_administrator', ARRAY['inventory.%']);
SELECT pg_temp.grant_perms_v37('owner_direction',     ARRAY['inventory.%']);
SELECT pg_temp.grant_perms_v37('manager',             ARRAY['inventory.%']);
SELECT pg_temp.grant_perms_v37('stock_manager',       ARRAY['inventory.manage_lot','inventory.view_valuation','inventory.manage_warehouse']);
SELECT pg_temp.grant_perms_v37('purchasing_manager',  ARRAY['inventory.view_valuation']);

-- ===================== 6. Audit du cycle de vie des lots (§43) ==========
DROP TRIGGER IF EXISTS trg_audit_lot ON nexora.lot;
CREATE TRIGGER trg_audit_lot AFTER INSERT OR UPDATE OR DELETE ON nexora.lot
    FOR EACH ROW EXECUTE FUNCTION nexora.audit_row();

SELECT nexora.attach_common_triggers();
