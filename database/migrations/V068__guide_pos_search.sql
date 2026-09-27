-- =============================================================================
-- NEXORA — V068 — Guide fonctionnel développeur, étape 5 : recherche produit au POS
-- (docs/guide-fonctionnel-conformite.md, écart GF-10).
--
-- SAISIE « EA » → API → RÉSULTATS PERTINENTS LIMITÉS → SÉLECTION → PANIER (§9, §24) : jamais le
-- catalogue complet ; seuls les produits actifs et vendables ; ni coût, ni fournisseur, ni quantité.
-- =============================================================================
SET search_path TO nexora, public;

INSERT INTO nexora.setting_definition (key, category, value_type, default_value, allowed_values, max_scope, is_sensitive, affects_future, description) VALUES
    ('pos.search_min_chars',   'pos', 'number', '2', '[]', 'company', false, false,
     'Nombre minimal de caractères avant de lancer la recherche produit au POS'),
    ('pos.search_max_results', 'pos', 'number', '8', '[]', 'company', false, false,
     'Nombre maximal de produits proposés par la recherche POS (borné entre 1 et 20)')
ON CONFLICT (key) DO NOTHING;
