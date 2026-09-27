# Phase 23 — Catalogue, Produits, Services, Prix & Taxes — implémentation

**Statut : livré et vérifié.** Migration `V036`. 9 tests d'intégration (`CatalogPricingTests`),
3 tests frontend (`catalogApi.test.ts`). 140 tests backend verts (Debug + Release).

Le moteur produit était déjà largement construit en **Phase 5** (produits, prix historisés,
fournisseurs, unités, conversions, seuils, coûts), **Phase 8** (recettes / ingrédients,
champs personnalisés, code-barres, promotions, coupons) et **Phase 18** (contrats API).
La Phase 23 **valide** cet existant contre le cahier des charges et ajoute une couche
concrète : le **moteur de prix explicable** (§22), le **HT/TTC déterministe** (§25), la
**recherche tolérante / scan POS** (§37-38), les **listes tarifaires nommées** (§18) et le
**workflow produit** (§45).

---

## Correspondance cahier des charges → implémentation

| § | Exigence | Où |
|---|---|---|
| 2-3 | ITEM → PRICING → TAX → SALES/INVENTORY/FINANCE | Chaîne `product` → `product_price` / `price_list` → `tax` → `SalesService` / `IStockLedger` / `IFinancialEngine` (Phases 5-6) |
| 3 | Types d'articles | `product.product_type` : `stockable`, `service`, `composite`, `weight`, `non_stock` (`ProductType`) |
| 4-5 | Fiche produit, identité commerciale | `product` + `V036` : `short_name`, `plu_code`, `manufacturer_ref`, `internal_ref`, `search_aliases`, `image_url`, `notes` |
| 6 | Codes-barres multiples, type, principal, statut, unicité | `product_barcode` + `V036` : `barcode_type` (11 types), `is_primary` (index unique partiel), `status`, `activated_at`. `CatalogService.AddBarcodeAsync` valide l'unicité (`catalog.barcode_exists`) |
| 7-8 | Unités & conversions | `unit`, `unit_conversion` (`factor`, `product_id` NULL = générique) — Phase 5 |
| 9 | Catégories arborescentes | `product_category` (`parent_id`). `CatalogService.CreateCategoryAsync` + `GET/POST /catalog/categories` |
| 10 | Marques | `brand` — Phase 5 |
| 11 | Variantes | `product.parent_product_id` + `variant_attributes` jsonb — Phase 5 |
| 12-14 | Attributs pharmacie / restaurant / supermarché | `pharmacy_product` (Phase 8), `recipe` / `recipe_ingredient` (Phase 8), `product.plu_code` + `product_barcode.is_weight_embedded` |
| 15 | Services | `product_type = 'service'` → `RequiresStock = false`, aucun mouvement de stock |
| 16-17 | Composés / recettes versionnées | `recipe` (`version`, `status`), `recipe_ingredient` (`wastage_percentage`, `yield`), `IRecipeExpansion` consommé par `SalesService` (Phase 8) |
| 18 | Listes tarifaires (COST/RETAIL/WHOLESALE/MEMBER/PROMOTIONAL/CUSTOM) | **`V036` `price_list`** (`code`, `list_type`, `currency_id`, `customer_group_id`, `priority`, `status`). `product_price.price_list_id` / `establishment_id`. `CatalogService.CreatePriceListAsync` + `GET/POST /catalog/price-lists` |
| 19 | Historique des prix reconstituable | `product_price` jamais réécrit — `valid_from` / `valid_to`. `CatalogService.SetPriceAsync` **ferme** la version courante avant d'insérer la nouvelle. `sale_line` fige `unit_price` / `cost_snapshot`. Test `Historical_price_is_preserved_after_a_revision` |
| 20 | Prix par quantité (paliers) | `product_price.quantity_min`. Résolution : `quantity_min <= qty` `ORDER BY quantity_min DESC`. Test `Base_price_and_quantity_tier_are_explained` |
| 21 | Promotions | `promotion` (`percentage`, `amount`, `buy_x_get_y`, `bundle`, `price_override`), `promotion_target` (`product`/`category`/`brand`/`all`), `coupon` (Phase 8). Moteur : pourcentage / montant / price_override + coupon avec `usage_limit` |
| 22 | **Moteur de prix — résultat explicable, aucune règle cachée** | **`CatalogPricingEngine.QuoteAsync`** → `PriceQuoteDto` avec la suite ordonnée `PriceStepDto[]` : `base_price` → `price_list` / `customer_price` / `quantity_tier` → `tax_extraction` → `promotion` → `coupon` → `manual_discount` → `tax` → `final`. `POST /catalog/pricing/quote`. Écran « Simulateur de prix » |
| 23-24 | Taxes, taxe par produit, historisation | `tax` (`rate`, `type`, `is_inclusive`, `status`), `product.default_tax_id`, `sale_line.tax_id` |
| 25 | **Prix TTC / HT déterministe** | **`product_price.tax_included`**. Si le prix est TTC : `net = round(montant / (1 + taux/100), 4)`. Appliqué **dans le moteur de devis** (`tax_extraction`) **et dans `SqlSalesPricing`** (le total TTC de la vente reste cohérent). Test `Tax_inclusive_price_is_split_deterministically` |
| 26 | Arrondis | `Rounding.Money` (2 déc., `MidpointRounding.ToEven`) ligne / facture ; devis : unité 4 déc., ligne 2 déc. |
| 27 | Devises | `product_price.currency_id` — le devis résout le prix **par devise de transaction**. Recherche : prix affiché = devise fonctionnelle de l'entreprise. Test `Quote_uses_the_currency_specific_price` |
| 28 | Coût | `product_cost_history`, `stock_balance.average_cost`, `product.cost_method` / `valuation_method` (Phase 5) |
| 29 | Fournisseurs liés | `product_supplier` (`supplier_sku`, `purchase_price`, `lead_time_days`, `is_preferred` — index unique partiel) |
| 30 | Stock lié au produit | `product_reorder_rule` (min/max, point de commande, `auto_order`), `product.track_lots`, FEFO (`IStockLedger.PickFefoAsync`) — Phase 5 |
| 31 / 40 | Paramètres & fiche POS | `product.short_name`, `is_weighed`, `default_tax_id`. `GET /catalog/scan` / `search` accessibles aux rôles `cashier` / `salesperson` (`V036` accorde `catalog.view`) |
| 32 / 45 | Disponibilité & workflow produit | **`product.status`** étendu : `draft` → `review` → `active` → `suspended` / `inactive` → `archived`. `ProductStatus.CanTransition` + `CatalogService.SetProductStatusAsync` (`catalog.status_transition`). `POST /catalog/products/{id}/status`. Test `Product_workflow_enforces_allowed_transitions` |
| 33 | Catalogue multi-entreprises | `product` porté par `company_id` + RLS (`V020`+). Prix / taxe / stock / activation locaux via `product_price.establishment_id`, `price_list`, `product.status` |
| 34-36 | Import / export catalogue | Non livré cette phase — permission `catalog.import` seedée ; l'import massif asynchrone s'appuiera sur le planificateur (Phase 16). Documenté comme manque. |
| 37-38 | Recherche tolérante & scan POS | **`CatalogQueries.SearchAsync`** : `nexora.search_norm()` (minuscule + `unaccent`) sur nom / sku / short_name / alias, `=` sur PLU / code-barres, `pg_trgm` (`similarity`, index GIN). **`ScanAsync`** : correspondance exacte code-barres → 0/1 résultat, `ExactBarcode` marqué. Test `Search_is_tolerant_and_scan_returns_one_exact_match` |
| 39 | Fiche produit — écran gestion | `CatalogQueries.GetProductAsync` → `ProductDetailDto` (prix + codes-barres + fournisseurs). Écran `CataloguePage` + tiroir de détail |
| 41 | Comptabilité produit | `account_mapping` (`sales_revenue`, `sales_tax_output`, `cogs`, `inventory`…) — Phases 4-5 ; taxe → `tax` |
| 42-43 | Champs personnalisés & attributs sectoriels | `custom_field` / `custom_field_value` (`entity_type = 'product'` / `recipe` / …), moteur Phase 8 |
| 44 | Numérotation SKU / réf. | SKU manuel (unicité `product_company_sku_uk`), `plu_code` unique partiel (`V036`), `document_sequence` pour les numéros de documents |
| 46 | Sécurité | `catalog.manage_price` / `view_cost` / `manage_promotion` / `manage_tax` / `import` (`V036`). Écriture de prix ⇒ `catalog.manage_price` ; `product_price.min_price` = plancher de remise (moteur : `manual_discount` plafonné). Test `Manual_discount_is_capped_at_the_floor_price` |
| 47 | API | `GET /catalog/search`, `/catalog/scan`, `/catalog/products`, `/catalog/products/{id}`, `POST /catalog/products/{id}/status`, `GET/POST /catalog/categories`, `GET/POST /catalog/price-lists`, `GET /catalog/taxes`, `GET /catalog/promotions`, `POST /catalog/pricing/quote` (+ endpoints Phase 5 : `POST .../suppliers`, `.../prices`, `.../barcodes`, `PUT .../reorder-rule`). **Écart de nommage** vs §47 : préfixe `/catalog/` conservé (cohérence Phase 5/18), pas `/products` à la racine |
| 48 | Tests | `CatalogPricingTests` (9) : prix de base + palier, devise, TTC, promotion, plancher de remise, historique, recherche/scan, workflow, catégorie/liste. Produits simple/service/composé/recette/pondéré/pharma/resto/supermarché déjà couverts par les tests Phases 5/6/8 |

## `V036` — contenu

- `price_list` (RLS `company_isolation`) — **table 162**, RLS **150/162**
- `product_price` : `tax_included`, `price_list_id`, `establishment_id`, `min_price`
- `product_barcode` : `barcode_type`, `is_primary`, `status`, `activated_at` + index unique partiel « un principal »
- `product` : `short_name`, `plu_code` (index unique partiel), `manufacturer_ref`, `internal_ref`, `search_aliases`, `image_url`, `notes`
- `product.status` : `draft` / `review` / `suspended` ajoutés au `CHECK`
- `pg_trgm` + `unaccent` + `nexora.search_norm(text)` (IMMUTABLE) + index GIN trigramme sur `product`
- Permissions `catalog.manage_promotion` / `catalog.manage_tax` / `catalog.import` ; `catalog.view` accordé à `cashier` / `salesperson`

## Le moteur de prix (`CatalogPricingEngine`)

Résolution déterministe, une ligne de prix « gagnante » choisie par :
`price_list_id` explicite > `customer_group_id` (du client) > portée par défaut, puis
palier `quantity_min` le plus élevé `<= qty`, puis `valid_from` le plus récent.

Chaque `PriceStepDto(Rule, Label, Delta, RunningUnit, Reference)` est renvoyé tel quel —
le POS peut afficher exactement pourquoi le prix est ce qu'il est (§22). Les remises
successives (promotion → coupon → remise manuelle) s'appliquent sur le **net HT** ;
la taxe est ajoutée en dernier ; un prix saisi TTC est ramené au HT en tout début de
chaîne (`tax_extraction`). La remise manuelle est plafonnée par `product_price.min_price`.

## Manques assumés (backlog)

- Import / export CSV-Excel (§34-36) — permission seedée, exécuteur asynchrone à écrire
- Promotions `buy_x_get_y` / `bundle` : schéma en place, calcul non implémenté dans le moteur
- Établissement : `product_price.establishment_id` stocké mais non arbitré par le moteur (résolution « NULL = tous »)
- Écran gestion : création/édition de produit se fait via l'API ; l'UI se limite à la recherche, la fiche, le workflow et le simulateur
