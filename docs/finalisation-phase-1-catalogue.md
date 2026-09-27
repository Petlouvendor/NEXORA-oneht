# Finalisation produit — Phase 1 : Produits, catégories, unités, prix et promotions

> Plan maître « NEXORA ONE — Finalisation complète du produit », phase 1.
> Branche `feature/products`. Date : 2026-09-27.

## 1. Travail effectué

Le catalogue devient gérable **entièrement depuis l'interface**, sans import ni script SQL :

| Besoin | Avant | Après |
|---|---|---|
| Créer un produit | impossible (import seulement) | formulaire complet : SKU, nom, type, unité, catégorie, marque, taxe, code-barres, PLU, lots, poids, statut, prix de vente/achat, seuils |
| Modifier un produit | nom, nom court, PLU, description | + catégorie, marque, taxe par défaut |
| Statut (activer, suspendre, archiver) | boutons bruts | libellés clairs + confirmation pour retirer de la vente / archiver |
| Image produit | champ inexistant côté écran | envoi PNG/JPEG/WebP (2 Mo), affichage sur la fiche |
| Prix | lecture seule | ajout de prix : vente/achat, devise, palier de quantité, liste tarifaire, groupe de clients, TTC, prix plancher, date d'effet ; marge indicative |
| Codes-barres | lecture seule | ajout, colisage (ex. carton de 24), principal |
| Fournisseurs du produit | lecture seule | association : fournisseur, référence, prix d'achat, délai, principal |
| Stock min / max / point de commande | aucune interface | formulaire de seuils (validé côté serveur) |
| Catégories | aucune interface | création, modification, sous-catégories, statut, anti-cycle |
| Marques | aucune route | liste + création |
| Unités & conversions | table sans route ni écran | unités, conversions générales et propres au produit, testeur de conversion |
| Listes de prix | lecture seule | création |
| Groupes de clients | aucune route | liste + création ; choix du groupe sur la fiche client |
| Promotions | lecture seule, 2 calculs divergents | création/modification/activation, cibles (produit, catégorie, marque, tout), période, priorité, quantité minimale, **2 pour 1 / X+Y offerts**, **lots à prix fixe** |
| Coupons | aucune interface ; limite jamais appliquée | création, activation, limite d'utilisation réellement consommée |

## 2. Fichiers créés

- `backend/src/Nexora.Application/Catalog/PromotionCalculator.cs` — calcul unique des promotions.
- `backend/src/Nexora.Infrastructure/Catalog/CatalogReferenceService.cs` — unités, conversions, catégories, marques, groupes, devises, image ; `SqlUnitConverter`.
- `backend/src/Nexora.Infrastructure/Catalog/PromotionResolver.cs`, `PromotionService.cs`.
- `database/migrations/V079__catalog_audit.sql` — audit de 13 tables du référentiel.
- `frontend/nexora-web/src/shared/useAsync.ts` — chargement partagé.
- `frontend/nexora-web/src/catalog/catalogShared.tsx`, `ProductForms.tsx`, `CatalogReferences.tsx`, `PromotionsPanel.tsx`, `PriceSimulator.tsx`.
- Tests : `CatalogReferenceTests.cs`, `CatalogPromotionTests.cs`, `PromotionCalculatorTests.cs`, `catalogProducts.test.ts`.

## 3. Fichiers modifiés

`CatalogController.cs`, `CatalogService.cs`, `CatalogAbstractions.cs`, `CatalogContracts.cs`, `CatalogQueries.cs`,
`CatalogPricingEngine.cs`, `SalesInfrastructure.cs`, `SalesService.cs`, `Sales/Abstractions.cs`,
`CatalogInventoryStores.cs`, `DependencyInjection.cs` ; frontend `catalogApi.ts`, `CataloguePage.tsx`,
`customersApi.ts`, `CustomerAccountPage.tsx`.

## 4. APIs

Existantes, désormais branchées : `POST /catalog/products`, `PUT /catalog/products/{id}`, `POST …/prices`,
`POST …/barcodes`, `POST …/suppliers`, `PUT …/reorder-rule`, `POST /catalog/categories`, `POST /catalog/price-lists`,
`PATCH /customers/{id}` (groupe).

Nouvelles : `PUT /catalog/categories/{id}` · `GET|POST /catalog/units` · `GET|POST /catalog/unit-conversions` ·
`DELETE /catalog/unit-conversions/{id}` · `GET /catalog/units/convert` · `POST|GET /catalog/products/{id}/image` ·
`GET|POST /catalog/brands` · `GET|POST /catalog/customer-groups` · `GET /catalog/currencies` ·
`POST|PUT /catalog/promotions`, `POST /catalog/promotions/{id}/status` · `GET|POST /catalog/coupons`,
`POST /catalog/coupons/{id}/status`. Toutes sous permission (`catalog.view|create|update|manage_price|manage_promotion`).

## 5. Règles métier garanties par le serveur

- **Conversions déterministes** : même unité → conversion propre au produit (direct puis inverse) → conversion générale ; jamais de chaînage implicite ; arrondi 6 décimales ; une conversion et son inverse ne peuvent coexister.
- **Promotions** : une seule règle de calcul pour la caisse et le simulateur ; remise bornée au montant de la ligne ; seules des unités complètes déclenchent un lot ou une offre.
- **Prix historisés** : un nouveau prix clôt l'ancien de même portée, rien n'est effacé.
- **Audit** : chaque écriture du référentiel est journalisée (ancienne/nouvelle valeur, utilisateur, date) dans le journal immuable.

## 6. Défauts corrigés en chemin

1. **La caisse appliquait les prix des listes tarifaires à tout client** (ex. prix « Grossiste » dès 10 unités) — filtre manquant.
2. **Coupon accepté hors période de validité** en caisse.
3. **Limite d'utilisation des coupons inopérante** : le compteur n'était jamais incrémenté ; il est maintenant consommé dans la transaction de vente (coupon épuisé entre-temps → vente annulée proprement).
4. Code coupon sensible à la casse.
5. Simulateur et caisse calculaient les promotions différemment (le simulateur gérait « prix imposé », pas la caisse).
6. Création de produit acceptant un nom vide, un type ou une unité inconnus, une taxe d'une autre entreprise ; seuils de stock incohérents acceptés ; lien vers un fournisseur inconnu → erreur 500.
7. Prix de groupe inutilisables : impossible de rattacher un client à un groupe depuis l'écran.

## 7. Tests

| Suite | Résultat |
|---|---|
| Intégration backend (dont +18 nouveaux) | 421 / 421 |
| Unitaires backend (+5) | 50 / 50 |
| Tests API backend | 185 / 185 |
| Frontend Vitest (+8) | 191 / 191 |
| Scénario API bout en bout (base clone `nexora_qa`) | 30 / 30 |
| Parcours navigateur (création, doublon, liste, onglets, catégorie, promotion) | réussi, 0 erreur console |

## 8. Dette technique restante

- Six écrans gardent une copie locale de `useAsync` (à remplacer par `shared/useAsync.ts`).
- **Journalisation** : tout refus métier (409/422) est journalisé comme « 500 » avec pile d'appels (ordre des middlewares) → à corriger en phase 18.
- Sans en-tête `X-Company-Id`, l'API répond 500 au lieu de 400 → phase 18.
- Les conversions d'unités ne sont pas encore utilisées par les réceptions d'achat → phase 3.
- Pas de suppression de catégorie (archivage seulement), ni de marque/groupe (création seule).
- Coupon : `per_customer_limit` existe en base mais n'est pas appliqué.

## 9. Prochaine phase

Phase 2 — Stock : tableau des quantités, inventaire, transferts, pertes et ajustements (routes de liste à ajouter).
