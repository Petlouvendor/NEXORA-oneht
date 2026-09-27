-- =============================================================================
-- NEXORA — V039 — Rapprochement 3-way & contrôle des dettes fournisseurs (Phase 26)
-- Le cycle d'achat est déjà en place (V009, V024) : commande → réception (partielle,
-- lots) → facture → dette (V012) → paiement (V012, Phase 12). Cette migration ajoute :
--   purchase_order.status  : état « sent » (commande envoyée au fournisseur) §10
--   supplier_invoice       : lignes persistées + rapprochement 3-way §20 :
--     match_status, matched_at/by, match_detail (jsonb), payment_blocked (+ motif),
--     goods_receipt_id (lien réception)
--   contrôle anti-doublon  : (entreprise, fournisseur, référence facture) §53-54
--   audit                  : purchase_order & supplier_invoice §52
-- =============================================================================
SET search_path TO nexora, public;

-- ===================== 1. Commande : état « envoyée » (§10) =============
ALTER TABLE nexora.purchase_order DROP CONSTRAINT IF EXISTS purchase_order_status_chk;
ALTER TABLE nexora.purchase_order
    ADD CONSTRAINT purchase_order_status_chk CHECK (status IN (
        'draft','submitted','approved','sent','partially_received','received',
        'invoiced','closed','cancelled'));

-- ===================== 2. Facture : rapprochement 3-way (§20, §40) ======
ALTER TABLE nexora.supplier_invoice
    ADD COLUMN IF NOT EXISTS match_status        text    NOT NULL DEFAULT 'unmatched',
    ADD COLUMN IF NOT EXISTS matched_at          timestamptz,
    ADD COLUMN IF NOT EXISTS matched_by          uuid REFERENCES nexora.app_user(id),
    ADD COLUMN IF NOT EXISTS match_detail        jsonb   NOT NULL DEFAULT '[]'::jsonb,
    ADD COLUMN IF NOT EXISTS payment_blocked     boolean NOT NULL DEFAULT false,
    ADD COLUMN IF NOT EXISTS payment_block_reason text,
    ADD COLUMN IF NOT EXISTS goods_receipt_id    uuid;

ALTER TABLE nexora.supplier_invoice DROP CONSTRAINT IF EXISTS supplier_invoice_match_chk;
ALTER TABLE nexora.supplier_invoice
    ADD CONSTRAINT supplier_invoice_match_chk CHECK (match_status IN (
        'unmatched','matched','variance','blocked','waived'));

ALTER TABLE nexora.supplier_invoice DROP CONSTRAINT IF EXISTS supplier_invoice_gr_fk;
ALTER TABLE nexora.supplier_invoice
    ADD CONSTRAINT supplier_invoice_gr_fk FOREIGN KEY (goods_receipt_id, company_id)
        REFERENCES nexora.goods_receipt (id, company_id);

-- ===================== 3. Anti-doublon facture (§53-54) ================
CREATE UNIQUE INDEX IF NOT EXISTS supplier_invoice_supplier_ref_uk
    ON nexora.supplier_invoice (company_id, supplier_id, lower(supplier_reference))
    WHERE supplier_reference IS NOT NULL AND status <> 'cancelled';

-- ===================== 4. Audit (§52) =================================
DO $$
DECLARE t text;
BEGIN
    FOREACH t IN ARRAY ARRAY['purchase_order','supplier_invoice','goods_receipt','supplier_return'] LOOP
        EXECUTE format('DROP TRIGGER IF EXISTS trg_audit_%1$s ON nexora.%1$I', t);
        EXECUTE format(
            'CREATE TRIGGER trg_audit_%1$s AFTER INSERT OR UPDATE OR DELETE ON nexora.%1$I
               FOR EACH ROW EXECUTE FUNCTION nexora.audit_row()', t);
    END LOOP;
END;
$$;

-- ===================== 5. Permissions (§51) ===========================
INSERT INTO nexora.permission (code, name, module, action) VALUES
    ('purchasing.approve_order',   'Achats — approuver une commande',        'purchasing', 'approve_order'),
    ('purchasing.match',           'Achats — rapprocher une facture (3-way)', 'purchasing', 'match'),
    ('purchasing.approve_invoice', 'Achats — approuver une facture',         'purchasing', 'approve_invoice'),
    ('purchasing.pay',             'Achats — payer un fournisseur',          'purchasing', 'pay'),
    ('purchasing.view_payables',   'Achats — voir les dettes fournisseurs',  'purchasing', 'view_payables')
ON CONFLICT (code) DO NOTHING;

CREATE OR REPLACE FUNCTION pg_temp.grant_perms_v39(p_role text, p_patterns text[])
RETURNS void LANGUAGE sql AS $$
    INSERT INTO nexora.role_permission (role_id, permission_id)
    SELECT r.id, p.id FROM nexora.role r
    JOIN nexora.permission p ON p.code LIKE ANY (p_patterns)
    WHERE r.name = p_role AND r.system_role
    ON CONFLICT DO NOTHING;
$$;

SELECT pg_temp.grant_perms_v39('super_administrator', ARRAY['purchasing.%']);
SELECT pg_temp.grant_perms_v39('group_administrator', ARRAY['purchasing.%']);
SELECT pg_temp.grant_perms_v39('owner_direction',     ARRAY['purchasing.%']);
SELECT pg_temp.grant_perms_v39('manager',             ARRAY['purchasing.%']);
SELECT pg_temp.grant_perms_v39('purchasing_manager',  ARRAY['purchasing.%']);
SELECT pg_temp.grant_perms_v39('chief_accountant',    ARRAY['purchasing.match','purchasing.approve_invoice','purchasing.pay','purchasing.view_payables']);
SELECT pg_temp.grant_perms_v39('accountant',          ARRAY['purchasing.view_payables','purchasing.match']);

SELECT nexora.attach_common_triggers();
