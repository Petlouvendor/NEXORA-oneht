-- =============================================================================
-- NEXORA — V079 — Audit du référentiel catalogue (finalisation produit, phase 1)
--
-- Jusqu'ici seul product_price était audité (V019). Le catalogue devient modifiable
-- depuis l'interface (création/modification produit, catégories, unités, conversions,
-- codes-barres, fournisseurs, seuils, listes de prix, promotions, coupons, marques, groupes
-- de clients) : chaque écriture doit
-- laisser une trace ancienne valeur / nouvelle valeur / utilisateur / date.
--
-- Aucune nouvelle table ni fonction : réutilise nexora.audit_row() (V019) et le journal
-- immuable nexora.audit_log (V030). La table attachment n'est volontairement PAS auditée
-- ligne à ligne : to_jsonb() recopierait les octets de l'image dans le journal ; le
-- rattachement de l'image est tracé par la mise à jour de product.image_url.
-- Idempotente : DROP TRIGGER IF EXISTS puis CREATE TRIGGER.
-- =============================================================================
DO $$
DECLARE
    t text;
BEGIN
    FOREACH t IN ARRAY ARRAY['product','product_category','product_barcode','product_supplier',
                             'product_reorder_rule','unit','unit_conversion','price_list',
                             'promotion','promotion_target','coupon','brand','customer_group'] LOOP
        EXECUTE format('DROP TRIGGER IF EXISTS trg_audit_%1$s ON nexora.%1$I', t);
        EXECUTE format(
            'CREATE TRIGGER trg_audit_%1$s AFTER INSERT OR UPDATE OR DELETE ON nexora.%1$I
               FOR EACH ROW EXECUTE FUNCTION nexora.audit_row()', t);
    END LOOP;
END;
$$;
