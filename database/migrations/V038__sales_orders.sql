-- =============================================================================
-- NEXORA — V038 — Commandes clients (Phase 25 §18-20, §25, §43)
-- Le moteur de vente POS est déjà en place (V010, V025) : caisse tactile, ventes
-- comptant / crédit / mixte, paiements multiples, promotions, retours & remboursements,
-- sessions de caisse, snapshots transactionnels. Cette migration ajoute la **commande
-- client** : lignes figées au prix du jour, réservation de stock, acompte, puis
-- exécution (« complete ») qui crée la vente réelle pour la quantité livrée.
--   sales_order / sales_order_line : la commande et ses lignes (§18)
--   sale.sales_order_id            : la vente issue d'une commande référence celle-ci
-- =============================================================================
SET search_path TO nexora, public;

CREATE TABLE nexora.sales_order (
    id                  uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    company_id          uuid        NOT NULL REFERENCES nexora.company(id),
    establishment_id    uuid,
    customer_id         uuid,
    document_number     text        NOT NULL,
    order_type          text        NOT NULL DEFAULT 'pickup',
    status              text        NOT NULL DEFAULT 'draft',
    currency_id         uuid        NOT NULL REFERENCES nexora.currency(id),
    inventory_location_id uuid,
    subtotal            nexora.money_amount NOT NULL DEFAULT 0,
    discount_total      nexora.money_amount NOT NULL DEFAULT 0,
    tax_total           nexora.money_amount NOT NULL DEFAULT 0,
    total               nexora.money_amount NOT NULL DEFAULT 0,
    deposit_amount      nexora.money_amount NOT NULL DEFAULT 0,
    deposit_payment_id  uuid,
    on_credit           boolean     NOT NULL DEFAULT false,
    expected_at         timestamptz,
    due_date            date,
    delivery_address    text,
    notes               text,
    reserved_at         timestamptz,
    fulfilled_at        timestamptz,
    cancelled_reason    text,
    created_by          uuid REFERENCES nexora.app_user(id),
    created_at          timestamptz NOT NULL DEFAULT now(),
    updated_at          timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT sales_order_type_chk CHECK (order_type IN ('pickup','delivery','wholesale','reservation')),
    CONSTRAINT sales_order_status_chk CHECK (status IN (
        'draft','confirmed','reserved','partially_fulfilled','fulfilled','cancelled')),
    CONSTRAINT sales_order_totals_chk CHECK (
        subtotal >= 0 AND tax_total >= 0 AND total >= 0 AND deposit_amount >= 0),
    CONSTRAINT sales_order_company_number_uk UNIQUE (company_id, document_number),
    CONSTRAINT sales_order_company_id_uk     UNIQUE (id, company_id),
    CONSTRAINT sales_order_customer_fk FOREIGN KEY (customer_id, company_id)
        REFERENCES nexora.customer (id, company_id),
    CONSTRAINT sales_order_establishment_fk FOREIGN KEY (establishment_id, company_id)
        REFERENCES nexora.establishment (id, company_id),
    CONSTRAINT sales_order_location_fk FOREIGN KEY (inventory_location_id, company_id)
        REFERENCES nexora.inventory_location (id, company_id)
);
CREATE INDEX sales_order_company_status_idx ON nexora.sales_order (company_id, status, created_at DESC);
CREATE INDEX sales_order_customer_idx       ON nexora.sales_order (customer_id);

CREATE TABLE nexora.sales_order_line (
    id                  uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    sales_order_id      uuid        NOT NULL,
    company_id          uuid        NOT NULL,
    product_id          uuid        NOT NULL,
    description         text        NOT NULL DEFAULT '',
    quantity            nexora.quantity NOT NULL,
    quantity_fulfilled  nexora.quantity NOT NULL DEFAULT 0,
    unit_price          nexora.money_amount NOT NULL DEFAULT 0,
    discount            nexora.money_amount NOT NULL DEFAULT 0,
    tax_id              uuid,
    tax_amount          nexora.money_amount NOT NULL DEFAULT 0,
    line_total          nexora.money_amount NOT NULL DEFAULT 0,
    unit_cost           nexora.money_amount NOT NULL DEFAULT 0,
    created_at          timestamptz NOT NULL DEFAULT now(),
    updated_at          timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT sales_order_line_qty_chk CHECK (quantity > 0 AND quantity_fulfilled >= 0 AND quantity_fulfilled <= quantity),
    CONSTRAINT sales_order_line_order_fk FOREIGN KEY (sales_order_id, company_id)
        REFERENCES nexora.sales_order (id, company_id) ON DELETE CASCADE,
    CONSTRAINT sales_order_line_product_fk FOREIGN KEY (product_id, company_id)
        REFERENCES nexora.product (id, company_id),
    CONSTRAINT sales_order_line_tax_fk FOREIGN KEY (tax_id, company_id)
        REFERENCES nexora.tax (id, company_id)
);
CREATE INDEX sales_order_line_order_idx ON nexora.sales_order_line (sales_order_id);

-- La vente issue d'une commande référence celle-ci (§18, §43).
ALTER TABLE nexora.sale ADD COLUMN IF NOT EXISTS sales_order_id uuid;
ALTER TABLE nexora.sale DROP CONSTRAINT IF EXISTS sale_sales_order_fk;
ALTER TABLE nexora.sale
    ADD CONSTRAINT sale_sales_order_fk FOREIGN KEY (sales_order_id, company_id)
        REFERENCES nexora.sales_order (id, company_id);
CREATE INDEX IF NOT EXISTS sale_sales_order_idx ON nexora.sale (sales_order_id) WHERE sales_order_id IS NOT NULL;

-- ===================== RLS (§34) & audit (§43) =========================
SELECT nexora.enable_company_rls('nexora.sales_order'::regclass);
SELECT nexora.enable_company_rls('nexora.sales_order_line'::regclass);

DROP TRIGGER IF EXISTS trg_audit_sales_order ON nexora.sales_order;
CREATE TRIGGER trg_audit_sales_order AFTER INSERT OR UPDATE OR DELETE ON nexora.sales_order
    FOR EACH ROW EXECUTE FUNCTION nexora.audit_row();

-- ===================== Permissions (§42) ==============================
INSERT INTO nexora.permission (code, name, module, action) VALUES
    ('pos.manage_order', 'POS — créer & exécuter des commandes clients', 'pos', 'manage_order')
ON CONFLICT (code) DO NOTHING;

CREATE OR REPLACE FUNCTION pg_temp.grant_perms_v38(p_role text, p_patterns text[])
RETURNS void LANGUAGE sql AS $$
    INSERT INTO nexora.role_permission (role_id, permission_id)
    SELECT r.id, p.id FROM nexora.role r
    JOIN nexora.permission p ON p.code LIKE ANY (p_patterns)
    WHERE r.name = p_role AND r.system_role
    ON CONFLICT DO NOTHING;
$$;

SELECT pg_temp.grant_perms_v38('super_administrator', ARRAY['pos.manage_order']);
SELECT pg_temp.grant_perms_v38('group_administrator', ARRAY['pos.manage_order']);
SELECT pg_temp.grant_perms_v38('owner_direction',     ARRAY['pos.manage_order']);
SELECT pg_temp.grant_perms_v38('manager',             ARRAY['pos.manage_order']);
SELECT pg_temp.grant_perms_v38('salesperson',         ARRAY['pos.manage_order']);

SELECT nexora.attach_common_triggers();
