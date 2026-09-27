-- =============================================================================
-- NEXORA — V071 — Résumé fonctionnel §4 : UTILISATEUR → ENTREPRISE → SUCCURSALE → MODULE → ACTION
-- (docs/guide-fonctionnel-conformite.md, écart RF-05).
--
-- Une affectation au niveau « établissement » (succursale) donnait accès à TOUTE l'entreprise. On
-- ajoute une seconde barrière RLS, RESTRICTIVE (elle s'ajoute à l'isolation entreprise) :
--   • variable de session nexora.establishment_ids : succursales autorisées dans l'entreprise active ;
--     vide = aucune restriction (utilisateur affecté à l'entreprise, au groupe ou à la plateforme) ;
--   • données opérationnelles (ventes, caisses, stock, achats, dépenses, documents) filtrées par
--     succursale ; une ligne sans succursale n'est pas visible d'un utilisateur restreint.
-- Les utilisateurs existants (affectés à l'entreprise ou au groupe) ne voient aucun changement.
-- =============================================================================
SET search_path TO nexora, public;

-- --- Contexte de session -----------------------------------------------------
CREATE OR REPLACE FUNCTION nexora.current_establishment_ids()
RETURNS uuid[] LANGUAGE plpgsql STABLE AS $$
DECLARE
    raw text := current_setting('nexora.establishment_ids', true);
BEGIN
    IF raw IS NULL OR btrim(raw) = '' THEN
        RETURN ARRAY[]::uuid[];
    END IF;
    RETURN string_to_array(btrim(raw), ',')::uuid[];
EXCEPTION WHEN others THEN
    RETURN ARRAY[]::uuid[];
END;
$$;

-- Vrai seulement pour un utilisateur restreint à des succursales (jamais en portée système).
CREATE OR REPLACE FUNCTION nexora.branch_restricted()
RETURNS boolean LANGUAGE sql STABLE COST 1 AS $$
    SELECT NOT nexora.rls_bypass()
       AND coalesce(btrim(current_setting('nexora.establishment_ids', true)), '') <> '';
$$;

-- --- Rattachement d'une ligne à sa succursale (lecture hors RLS, clé primaire) ---------
CREATE OR REPLACE FUNCTION nexora.location_establishment(p uuid)
RETURNS uuid LANGUAGE sql STABLE SECURITY DEFINER SET search_path = nexora, pg_temp COST 100 AS $$
    SELECT establishment_id FROM nexora.inventory_location WHERE id = p;
$$;
CREATE OR REPLACE FUNCTION nexora.store_establishment(p uuid)
RETURNS uuid LANGUAGE sql STABLE SECURITY DEFINER SET search_path = nexora, pg_temp COST 100 AS $$
    SELECT establishment_id FROM nexora.store WHERE id = p;
$$;
CREATE OR REPLACE FUNCTION nexora.register_establishment(p uuid)
RETURNS uuid LANGUAGE sql STABLE SECURITY DEFINER SET search_path = nexora, pg_temp COST 100 AS $$
    SELECT s.establishment_id FROM nexora.cash_register r JOIN nexora.store s ON s.id = r.store_id WHERE r.id = p;
$$;
CREATE OR REPLACE FUNCTION nexora.session_establishment(p uuid)
RETURNS uuid LANGUAGE sql STABLE SECURITY DEFINER SET search_path = nexora, pg_temp COST 100 AS $$
    SELECT s.establishment_id FROM nexora.cash_session cs
    JOIN nexora.cash_register r ON r.id = cs.cash_register_id JOIN nexora.store s ON s.id = r.store_id
    WHERE cs.id = p;
$$;
CREATE OR REPLACE FUNCTION nexora.sale_establishment(p uuid)
RETURNS uuid LANGUAGE sql STABLE SECURITY DEFINER SET search_path = nexora, pg_temp COST 100 AS $$
    SELECT establishment_id FROM nexora.sale WHERE id = p;
$$;
GRANT EXECUTE ON FUNCTION nexora.current_establishment_ids(), nexora.branch_restricted(),
    nexora.location_establishment(uuid), nexora.store_establishment(uuid), nexora.register_establishment(uuid),
    nexora.session_establishment(uuid), nexora.sale_establishment(uuid) TO nexora_app;

-- --- Rattachement des données existantes -------------------------------------
-- Vente : succursale du point de vente, sinon de l'emplacement de stock de ses lignes.
UPDATE nexora.sale s
   SET establishment_id = COALESCE(nexora.store_establishment(s.store_id),
       (SELECT nexora.location_establishment(sl.inventory_location_id) FROM nexora.sale_line sl
        WHERE sl.sale_id = s.id AND sl.inventory_location_id IS NOT NULL LIMIT 1))
 WHERE s.establishment_id IS NULL;
UPDATE nexora.sales_order o SET establishment_id = nexora.location_establishment(o.inventory_location_id)
 WHERE o.establishment_id IS NULL AND o.inventory_location_id IS NOT NULL;
UPDATE nexora.issued_document d SET establishment_id = nexora.sale_establishment(d.reference_id)
 WHERE d.establishment_id IS NULL AND d.reference_type = 'sale';

-- --- Rattachement automatique des nouvelles lignes -----------------------------
-- Dérivé de la source quand elle existe ; à défaut, pour un utilisateur restreint à UNE succursale, celle-ci.
CREATE OR REPLACE FUNCTION nexora.default_establishment_from_session(current_value uuid)
RETURNS uuid LANGUAGE sql STABLE AS $$
    SELECT COALESCE(current_value,
        CASE WHEN nexora.branch_restricted() AND cardinality(nexora.current_establishment_ids()) = 1
             THEN (nexora.current_establishment_ids())[1] END);
$$;

CREATE OR REPLACE FUNCTION nexora.trg_sale_establishment() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    NEW.establishment_id := nexora.default_establishment_from_session(
        COALESCE(NEW.establishment_id, nexora.store_establishment(NEW.store_id)));
    RETURN NEW;
END;
$$;
CREATE OR REPLACE FUNCTION nexora.trg_sales_order_establishment() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    NEW.establishment_id := nexora.default_establishment_from_session(
        COALESCE(NEW.establishment_id, nexora.location_establishment(NEW.inventory_location_id)));
    RETURN NEW;
END;
$$;
CREATE OR REPLACE FUNCTION nexora.trg_issued_document_establishment() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF NEW.establishment_id IS NULL AND NEW.reference_type = 'sale' THEN
        NEW.establishment_id := nexora.sale_establishment(NEW.reference_id);
    END IF;
    NEW.establishment_id := nexora.default_establishment_from_session(NEW.establishment_id);
    RETURN NEW;
END;
$$;
CREATE OR REPLACE FUNCTION nexora.trg_default_establishment() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    NEW.establishment_id := nexora.default_establishment_from_session(NEW.establishment_id);
    RETURN NEW;
END;
$$;

DROP TRIGGER IF EXISTS trg_branch_default ON nexora.sale;
CREATE TRIGGER trg_branch_default BEFORE INSERT ON nexora.sale
    FOR EACH ROW EXECUTE FUNCTION nexora.trg_sale_establishment();
DROP TRIGGER IF EXISTS trg_branch_default ON nexora.sales_order;
CREATE TRIGGER trg_branch_default BEFORE INSERT ON nexora.sales_order
    FOR EACH ROW EXECUTE FUNCTION nexora.trg_sales_order_establishment();
DROP TRIGGER IF EXISTS trg_branch_default ON nexora.issued_document;
CREATE TRIGGER trg_branch_default BEFORE INSERT ON nexora.issued_document
    FOR EACH ROW EXECUTE FUNCTION nexora.trg_issued_document_establishment();
DO $$
DECLARE t text;
BEGIN
    FOREACH t IN ARRAY ARRAY['purchase_order', 'expense', 'quote'] LOOP
        EXECUTE format('DROP TRIGGER IF EXISTS trg_branch_default ON nexora.%I', t);
        EXECUTE format('CREATE TRIGGER trg_branch_default BEFORE INSERT ON nexora.%I
                          FOR EACH ROW EXECUTE FUNCTION nexora.trg_default_establishment()', t);
    END LOOP;
END;
$$;

-- --- Politiques RESTRICTIVES « branch_isolation » --------------------------------
-- Sans restriction (NOT branch_restricted()), la politique est neutre ; l'expression coûteuse
-- (fonction de rattachement) n'est évaluée que pour un utilisateur restreint.
DO $$
DECLARE
    r record;
BEGIN
    FOR r IN SELECT * FROM (VALUES
        ('establishment',        'id'),
        ('store',                'establishment_id'),
        ('warehouse',            'establishment_id'),
        ('inventory_location',   'establishment_id'),
        ('pos_terminal',         'nexora.store_establishment(store_id)'),
        ('cash_register',        'nexora.store_establishment(store_id)'),
        ('cash_session',         'nexora.register_establishment(cash_register_id)'),
        ('cash_movement',        'nexora.session_establishment(cash_session_id)'),
        ('sale',                 'establishment_id'),
        ('sale_line',            'nexora.sale_establishment(sale_id)'),
        ('sale_return',          'nexora.sale_establishment(sale_id)'),
        ('sale_return_request',  'nexora.sale_establishment(sale_id)'),
        ('stock_balance',        'nexora.location_establishment(inventory_location_id)'),
        ('stock_movement',       'nexora.location_establishment(inventory_location_id)'),
        ('lot_stock',            'nexora.location_establishment(inventory_location_id)'),
        ('stock_adjustment',     'nexora.location_establishment(inventory_location_id)'),
        ('inventory_count',      'nexora.location_establishment(inventory_location_id)'),
        ('goods_receipt',        'nexora.location_establishment(inventory_location_id)'),
        ('purchase_order',       'establishment_id'),
        ('sales_order',          'establishment_id'),
        ('expense',              'establishment_id'),
        ('issued_document',      'establishment_id'),
        ('quote',                'establishment_id')
    ) AS v(tbl, expr)
    LOOP
        EXECUTE format('DROP POLICY IF EXISTS branch_isolation ON nexora.%I', r.tbl);
        EXECUTE format(
            'CREATE POLICY branch_isolation ON nexora.%I AS RESTRICTIVE
               USING (NOT nexora.branch_restricted() OR (%s) = ANY (nexora.current_establishment_ids()))
               WITH CHECK (NOT nexora.branch_restricted() OR (%s) = ANY (nexora.current_establishment_ids()))',
            r.tbl, r.expr, r.expr);
    END LOOP;
END;
$$;

-- Règles de réapprovisionnement : une règle sans emplacement vaut pour toute l'entreprise (visible).
DROP POLICY IF EXISTS branch_isolation ON nexora.product_reorder_rule;
CREATE POLICY branch_isolation ON nexora.product_reorder_rule AS RESTRICTIVE
    USING (NOT nexora.branch_restricted() OR inventory_location_id IS NULL
           OR nexora.location_establishment(inventory_location_id) = ANY (nexora.current_establishment_ids()))
    WITH CHECK (NOT nexora.branch_restricted() OR inventory_location_id IS NULL
           OR nexora.location_establishment(inventory_location_id) = ANY (nexora.current_establishment_ids()));
