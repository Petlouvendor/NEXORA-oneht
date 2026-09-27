# NEXORA — Phase 8 — État de l'implémentation

Référence : [`phase-8-modules-sectoriels.md`](phase-8-modules-sectoriels.md).

## Principe directeur

Le noyau (produit, stock, vente, paiement, finance, comptabilité) reste **commun et
inchangé**. Les modules sectoriels ajoutent des processus ; ils ne créent aucun système
de vente, de paiement ou de comptabilité parallèle (§22). La configuration s'active et
se désactive sans jamais supprimer de données existantes (§29).

## Ce qui est livré

### Base de données — `V027`

| Objet | Rôle |
|---|---|
| `company_activity` | activités d'une entreprise — une principale, plusieurs secondaires (§27) ; l'activité principale se reflète dans `company.main_activity_type` |
| `company_module` | registre des modules activés/désactivés + `enabled_at` / `disabled_at` (§3, §29) |
| `sector_module_preset` | modules recommandés par activité — donnée de référence |
| `terminology_override` | libellés métier adaptables sans changer le sens des transactions (§18) |
| `custom_field` + `custom_field_value` | moteur de champs personnalisés typés (text/number/date/boolean/list/reference/file), obligatoires ou non, avec permission d'accès (§16) |
| `restaurant_area` | zones / salles (§6) |
| `restaurant_table` (+ `area_id`, `current_sale_id`) | rattachement à une zone + commande ouverte |
| `sale_service_context` (+ `notes`, `priority`, `fired_at`, `ready_at`, `served_at`) | contexte service / horodatage KDS |
| `sale_line_modifier` | modificateurs d'une ligne (« sans oignon ») (§6) |
| `recipe_ingredient` (+ `kitchen_station`) | poste de cuisine |
| permissions | `sectors.{configure,custom_fields,terminology}`, `restaurant.{manage,serve,kitchen}`, `recipes.manage` + rattachement aux rôles |
| RLS | **rattrapage** : la politique d'isolation entreprise est ré-appliquée à toutes les tables à `company_id NOT NULL` ajoutées après `V020` (V024–V027) — 120/130 tables protégées (§21) |

Réutilise l'acquis de `V018` : `pharmacy_product`, `prescription*`, `recipe`, `restaurant_table`,
`promotion`, `coupon`, `product_barcode` (poids), `loyalty_*`, `delivery*`.

### Backend

```
Nexora.Contracts/Sectors/        SectorConfig, ModuleState, CustomFieldDto, DefineCustomFieldRequest, …
Nexora.Contracts/Restaurant/     RestaurantTableDto, PlaceOrderRequest, RestaurantOrderDto, DefineRecipeRequest, …

Nexora.Application/Sectors/
  ISectorConfigService             GetAsync, SetActivitiesAsync, SetModuleAsync, IsModuleEnabledAsync, SetTerminologyAsync
  ICustomFieldService              DefineAsync, ListAsync, SetValuesAsync (validation obligatoire + type), GetValuesAsync
Nexora.Application/Restaurant/
  IRestaurantService               zones, tables, PlaceOrder, KDS (AdvanceKitchen), KitchenBoard, CloseOrder
  IRecipeService                   DefineAsync, GetAsync (coût de revient)
Nexora.Application/Sales/
  IRecipeExpansion                 « plat → ingrédients » au moment de la vente (implémenté en Infrastructure)

Nexora.Infrastructure/Sectors/    SqlSectorConfigService, SqlCustomFieldService
Nexora.Infrastructure/Restaurant/ EfRestaurantService, SqlRecipeService, SqlRecipeExpansion
```

**Intégration au noyau (§22).** La commande restaurant est une **vente en attente** (Phase 6
§34-35). Sa clôture appelle `SalesService.CheckoutAsync` — même stock, même paiement, même
comptabilité. Un produit `composite` (plat / kit) déclenche `IRecipeExpansion` :
`SalesService` remplace le coût unitaire par le coût de recette et ajoute des mouvements de
sortie pour chaque ingrédient (avec perte configurable), appliqués par `IStockLedger`.
Un seul mouvement de stock porte l'UUID d'opération (marqueur d'idempotence offline).

**Modules & activités.** `SetActivitiesAsync` synchronise `company_activity`,
`company.main_activity_type` (activité principale = première de la liste) et active les
modules du preset — sans jamais désactiver. `EfRestaurantService` refuse toute création
si le module `restaurant` n'est pas activé (`403 restaurant.module_disabled`).

### API — `v1`

| Route | Permission |
|---|---|
| `GET /sectors/config` | `sectors.configure` |
| `PUT /sectors/activities` · `PUT /sectors/modules` | `sectors.configure` |
| `PUT /sectors/terminology` | `sectors.terminology` |
| `POST /sectors/custom-fields` | `sectors.custom_fields` |
| `GET /sectors/custom-fields/{entityType}` · `PUT /sectors/custom-fields/values` · `GET /sectors/custom-fields/values/{entityType}/{id}` | `sectors.configure` |
| `POST /restaurant/areas` · `POST /restaurant/tables` | `restaurant.manage` |
| `GET /restaurant/tables` · `POST /restaurant/orders` · `GET /restaurant/orders/{saleId}` · `POST /restaurant/orders/close` | `restaurant.serve` |
| `GET /restaurant/kitchen` · `POST /restaurant/kitchen/advance` | `restaurant.kitchen` |
| `POST /restaurant/recipes` · `GET /restaurant/recipes/{productId}` | `recipes.manage` |

### Frontend

`frontend/nexora-web/src/sectors/SectorSettingsPage.tsx` (route `/sectors`) : bascule des
activités (la première devient principale), cases à cocher des modules, table des champs
personnalisés par entité + formulaire d'ajout. `npm run typecheck` + `build` OK.

### Démo

`demo_abc_group.sql` : activité principale + modules du preset pour les 3 entreprises
(Pharmacie → pharmacy/retail/loyalty ; Restaurant → restaurant/delivery ; Supermarché →
retail/loyalty/delivery), établissement + salle + 2 tables pour Restaurant ABC. Rejouable.

## Vérifications exécutées

**Tests d'intégration `Nexora.IntegrationTests/SectorModulesTests` — 6/6** :

| Scénario (§31) | Vérifié |
|---|---|
| Activer deux secteurs dans une même entreprise | `[pharmacy, restaurant]` → primaire pharmacy, `main_activity_type='pharmacy'`, modules pharmacy+restaurant actifs |
| Restaurant avec tables + KDS, réglé par le noyau | zone + table → commande (2 plats) → KDS pending→preparing→ready (retour arrière refusé) → clôture cash 1000 → **écriture équilibrée**, coût de ligne = coût recette (70), ingrédients consommés (−6 / −4), table libérée, commande d'origine `void` traçable |
| Entreprise de services sans stock | produit `service` vendu → **aucun mouvement de stock**, écriture équilibrée, produit constaté |
| Ajouter un champ personnalisé | champ `number` obligatoire : manquant → refus, type incorrect → refus, valeur `3` → stockée et typée |
| Modifier la configuration sans supprimer l'historique | module `services` désactivé (`disabled_at` posé) → la vente historique reste intacte |
| Données sectorielles hors périmètre | champ défini pour l'entreprise A absent du périmètre de l'entreprise B |

Smoke API (démo) : `GET /sectors/config`, `PUT /sectors/modules`, `POST /sectors/custom-fields`,
`GET /sectors/custom-fields/product`, `GET /restaurant/tables` (c2), `POST /restaurant/areas` (c2),
`PUT /sectors/activities` → **2xx** ; activité inconnue → **422** ; caissier sur `GET /sectors/config`
→ **403** (deny-by-default).

**Total backend : 64 tests verts** (16 unitaires + 2 API + 46 intégration), build Release 0 warning.
DB : 27 migrations (V001–V027), **130 tables**. Rebuild + smoke + démo (rejouée) OK.

## Écarts et décisions

- **Recette → ingrédients** : consommation à la vente pour les produits `composite` ;
  conversion d'unités entre ingrédient et recette non gérée (unités supposées homogènes) —
  raffinement.
- **KDS** : statut au niveau de la commande (`pending/preparing/ready/served`) ; le statut
  par article et les postes de cuisine (`kitchen_station`) sont stockés mais pas encore
  exploités par un écran par poste.
- **Commande restaurant** : passée en une fois (`PlaceOrder`) ; l'ajout incrémental
  d'articles, la fusion/séparation de tables et le transfert de commande sont définis mais
  non implémentés.
- **Workflows sectoriels (§17)** : le schéma d'approbation existant (`V016`) sert de base ;
  aucun moteur de workflow configurable dédié n'a été ajouté.
- **Champs personnalisés** : valeurs typées et validées ; les règles de visibilité
  (`visibility` jsonb) et le rendu conditionnel sont stockés mais évalués côté client.
- **Modules Hôtel / E-commerce / Agriculture / Production** : identifiés, `company_module`
  les porte, entités dédiées reportées (§32 : « définis / identifiés comme extensions »).

## Couverture des critères de sortie (§32)

| Critère | État |
|---|---|
| Architecture Core/Sectoriel | ✅ modules branchés sur le noyau, aucun système parallèle (test §22) |
| Pharmacie | ✅ `V018` (lots/FEFO Phase 5, `pharmacy_product`, `prescription`) + module activable |
| Restaurant | ✅ zones/tables/commande/KDS/recette → consommation stock, réglé par le noyau |
| Supermarché / Retail | ✅ poids (`product.is_weighed`, `product_barcode`), promotions, multi-caisses (Phase 6) |
| Grossiste / Distribution | ✅ tarifs par quantité/groupe + crédit (Phase 6), `delivery*` (`V018`) |
| Boutique | ✅ module `boutique` + retail + fidélité |
| Services | ✅ `product_type='service'` vendu sans stock, comptabilisé (test) |
| Production (extension) | ✅ recettes/nomenclatures partagées ; ordres de production reportés |
| Hôtel / e-commerce | ✅ identifiés, portés par `company_module` |
| Mode générique | ✅ activité `other` → module `generic` + champs personnalisés |
| Champs personnalisés | ✅ `ICustomFieldService`, typés, obligatoires, scoping entreprise |
| Workflows sectoriels | ⚙️ défini (approbations `V016`) ; moteur dédié reporté |
| Permissions | ✅ `sectors.*`, `restaurant.*`, `recipes.*` + héritage de périmètre |
| Données sensibles | ✅ RLS rattrapée sur toutes les tables métier ; `custom_field.view_permission` |
| Multi-activité | ✅ `company_activity` + test |
| Intégration Core/sectoriel | ✅ clôture de commande = `SalesService.CheckoutAsync` |
| Scénarios testables | ✅ 6 scénarios §31 automatisés |

## Suite — Phase 9

Multi-entreprises, gestion de groupe & consolidation : gouvernance d'un groupe, accès du
comptable en chef, vues consolidées, transactions intercompany, séparation des données.
