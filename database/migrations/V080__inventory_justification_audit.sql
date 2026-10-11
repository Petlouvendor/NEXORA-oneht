-- =============================================================================
-- NEXORA — V080 — Justification des écarts & audit des opérations de stock
-- (finalisation produit, phase 2)
--
-- 1. Justification ligne à ligne : un écart d'inventaire ou un ajustement manuel doit
--    pouvoir être expliqué au niveau de l'article (« casse constatée », « erreur de saisie
--    réception »…). Colonne note sur inventory_count_line et stock_adjustment_line ;
--    la justification globale reste dans inventory_count.notes / stock_adjustment.reason.
-- 2. Audit : inventaires, transferts, lignes d'ajustement, entrepôts et emplacements
--    deviennent modifiables depuis l'interface → trigger nexora.audit_row() (V019).
--    stock_adjustment et lot l'étaient déjà ; stock_movement est un registre en ajout seul.
-- Idempotente : ADD COLUMN IF NOT EXISTS, DROP … IF EXISTS puis CREATE.
-- =============================================================================
-- 3. Type d'ajustement « theft » (vol) : distinct de la perte dans les rapports, même
--    traitement comptable (sortie de stock en charge). Le mouvement de stock reste « loss ».
ALTER TABLE nexora.stock_adjustment DROP CONSTRAINT IF EXISTS stock_adjustment_type_chk;
ALTER TABLE nexora.stock_adjustment ADD CONSTRAINT stock_adjustment_type_chk CHECK (adjustment_type IN (
    'count','loss','breakage','expiry','theft','correction','found','opening'));

ALTER TABLE nexora.inventory_count_line  ADD COLUMN IF NOT EXISTS note text;
ALTER TABLE nexora.stock_adjustment_line ADD COLUMN IF NOT EXISTS note text;

COMMENT ON COLUMN nexora.inventory_count_line.note  IS 'Justification de l''écart constaté sur la ligne (saisie au comptage ou à la revue).';
COMMENT ON COLUMN nexora.stock_adjustment_line.note IS 'Justification de la ligne d''ajustement (reprise de la ligne d''inventaire ou saisie manuelle).';

DO $$
DECLARE
    t text;
BEGIN
    FOREACH t IN ARRAY ARRAY['inventory_count','inventory_count_line','stock_adjustment_line',
                             'stock_transfer','stock_transfer_line','warehouse','inventory_location'] LOOP
        EXECUTE format('DROP TRIGGER IF EXISTS trg_audit_%1$s ON nexora.%1$I', t);
        EXECUTE format(
            'CREATE TRIGGER trg_audit_%1$s AFTER INSERT OR UPDATE OR DELETE ON nexora.%1$I
               FOR EACH ROW EXECUTE FUNCTION nexora.audit_row()', t);
    END LOOP;
END;
$$;
