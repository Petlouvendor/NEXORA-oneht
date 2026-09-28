-- =============================================================================
-- NEXORA — V081 — Achats : unités d'achat, contrôle à réception, conditions fournisseur
-- (finalisation produit, phase 3)
--
-- 1. Unité d'achat : une commande ou une réception peut être saisie en cartons alors que le
--    stock est tenu en bouteilles. La quantité en unité de BASE reste la seule qui pilote le
--    stock et le rapprochement (quantity_ordered / quantity_received, inchangées) ; l'unité et
--    la quantité saisies sont conservées pour la traçabilité (« 10 CRT » = 240 BTL).
--    La conversion est faite par le serveur (IUnitConverter, phase 1).
-- 2. Taxe par ligne de commande : jusqu'ici tax_total restait à 0 même avec une taxe choisie.
-- 3. Contrôle à réception : quantité refusée (casse, non conforme) et motif — refusée = jamais
--    entrée en stock, visible sur le bon de réception.
-- 4. Fournisseur : conditions de paiement (réutilise supplier_payment_terms, V005, jusqu'ici
--    sans aucun usage), contact et notes. La limite de crédit existe déjà (supplier.credit_limit).
--    supplier_account (V006) reste inutilisé : son solde n'est alimenté par aucun traitement.
-- 5. Audit des tables d'achat qui ne l'étaient pas.
-- Idempotente.
-- =============================================================================
ALTER TABLE nexora.purchase_order_line
    ADD COLUMN IF NOT EXISTS purchase_unit_id  uuid,
    ADD COLUMN IF NOT EXISTS purchase_quantity nexora.quantity,
    ADD COLUMN IF NOT EXISTS tax_amount        nexora.money_amount NOT NULL DEFAULT 0;

ALTER TABLE nexora.goods_receipt_line
    ADD COLUMN IF NOT EXISTS purchase_unit_id  uuid,
    ADD COLUMN IF NOT EXISTS purchase_quantity nexora.quantity,
    ADD COLUMN IF NOT EXISTS quantity_rejected nexora.quantity NOT NULL DEFAULT 0,
    ADD COLUMN IF NOT EXISTS rejection_reason  text;

ALTER TABLE nexora.supplier
    ADD COLUMN IF NOT EXISTS payment_terms_id uuid,
    ADD COLUMN IF NOT EXISTS contact_name     text,
    ADD COLUMN IF NOT EXISTS notes            text;

DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'purchase_order_line_unit_fk') THEN
        ALTER TABLE nexora.purchase_order_line ADD CONSTRAINT purchase_order_line_unit_fk
            FOREIGN KEY (purchase_unit_id, company_id) REFERENCES nexora.unit (id, company_id);
    END IF;
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'goods_receipt_line_unit_fk') THEN
        ALTER TABLE nexora.goods_receipt_line ADD CONSTRAINT goods_receipt_line_unit_fk
            FOREIGN KEY (purchase_unit_id, company_id) REFERENCES nexora.unit (id, company_id);
    END IF;
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'supplier_payment_terms_fk') THEN
        ALTER TABLE nexora.supplier ADD CONSTRAINT supplier_payment_terms_fk
            FOREIGN KEY (payment_terms_id, company_id) REFERENCES nexora.supplier_payment_terms (id, company_id);
    END IF;
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'goods_receipt_line_rejected_chk') THEN
        ALTER TABLE nexora.goods_receipt_line ADD CONSTRAINT goods_receipt_line_rejected_chk
            CHECK (quantity_rejected >= 0 AND (quantity_rejected = 0 OR rejection_reason IS NOT NULL));
    END IF;
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'purchase_order_line_tax_chk') THEN
        ALTER TABLE nexora.purchase_order_line ADD CONSTRAINT purchase_order_line_tax_chk CHECK (tax_amount >= 0);
    END IF;
END;
$$;

COMMENT ON COLUMN nexora.purchase_order_line.purchase_quantity IS 'Quantité saisie dans l''unité d''achat (purchase_unit_id) ; quantity_ordered = équivalent en unité de base.';
COMMENT ON COLUMN nexora.goods_receipt_line.quantity_rejected  IS 'Quantité refusée au contrôle (jamais entrée en stock), en unité de base.';

DO $$
DECLARE
    t text;
BEGIN
    FOREACH t IN ARRAY ARRAY['purchase_order_line','goods_receipt_line','supplier_invoice_line',
                             'supplier_return_line','supplier_payment_terms'] LOOP
        EXECUTE format('DROP TRIGGER IF EXISTS trg_audit_%1$s ON nexora.%1$I', t);
        EXECUTE format(
            'CREATE TRIGGER trg_audit_%1$s AFTER INSERT OR UPDATE OR DELETE ON nexora.%1$I
               FOR EACH ROW EXECUTE FUNCTION nexora.audit_row()', t);
    END LOOP;
END;
$$;
