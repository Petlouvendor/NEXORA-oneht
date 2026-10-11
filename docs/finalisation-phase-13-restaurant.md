# Finalisation produit — Phase 13 : Restaurant

> Plan maître « NEXORA ONE — Finalisation complète du produit », phase 13 (§35 Module restaurant).
> Branche `feature/restaurant`. Date : 2026-10-04.

Cycle visé : **table → commande → cuisine → service → paiement → table libérée**, sans logique inventée : la commande est
une vente en attente du moteur de vente existant, et son règlement passe par le même encaissement que la caisse (prix,
taxes, promotions, stock, recettes, caisse, comptabilité).

## 1. Travail effectué

Avant cette phase, les 10 routes `/restaurant/*` existaient sans aucun écran. Leur inspection a montré plusieurs défauts
qui auraient produit de fausses données dès la première utilisation (voir §5) ; ils sont corrigés avant de construire
l'interface.

| Onglet | Contenu |
|---|---|
| **Salle** (serveur, `restaurant.serve`) | tables groupées par salle : libre / occupée, couverts, total en cours, état cuisine et âge ; table libre → **ouvrir une commande** (couverts, note cuisine, recherche d'articles comme en caisse, quantités, **précisions** « sans oignon, bien cuit ») ; table occupée → **fiche de commande** : lignes et précisions, **ajouter des articles**, faire avancer la cuisine (si autorisé), **encaisser**, **annuler** avec motif |
| **Encaisser** | montant exact calculé par le serveur (taxes, promotions), moyens de paiement de la caisse (multi-paiement, rendu sur espèces seulement — mêmes outils que le POS), session de caisse ouverte exigée ; **pourboire** noté « pour mémoire — non encaissé par NEXORA » |
| **Cuisine** (KDS, `restaurant.kitchen`) | tickets en cours avec âge (couleur à 10 et 20 min), note, précisions, **articles ajoutés après l'envoi marqués « nouveau »** ; boutons Commencer → Prêt → Servi ; actualisation toutes les 10 s |
| **Recettes** (`recipes.manage`) | liste des recettes avec ingrédients et coût estimé ; création / modification : plat (non stocké), ingrédients (stockés), quantité **dans l'unité de stock de l'ingrédient**, perte %, poste |
| **Configuration** (`restaurant.manage`) | établissement, salles (code unique), tables (salle, code, places 1–99), état des tables |

Bandeau si le module n'est pas activé pour l'entreprise (lien vers Secteurs & modules). Entrée « Restaurant » dans le menu.
Aucun `prompt()` / `alert()` / `confirm()`.

## 2. Fichiers créés

- `frontend/nexora-web/src/restaurant/RestaurantPage.tsx`, `RestaurantSections.tsx`, `restaurantApi.ts`,
  `restaurantApi.test.ts`

## 3. Fichiers modifiés

- Backend : `Infrastructure/Restaurant/RestaurantServices.cs` (service restaurant et service de recettes réécrits),
  `Application/Restaurant/Abstractions.cs`, `Contracts/Restaurant/RestaurantContracts.cs`,
  `Api/Controllers/RestaurantController.cs`, `Application/Sales/SalesService.cs` (`AddToParkedAsync`,
  `ParkedLineIdsAsync`), `Application/Sales/Abstractions.cs` + `Infrastructure/Sales/SalesInfrastructure.cs`
  (`AddSaleLinesAsync`), tests `SectorModulesTests.cs`
- Frontend : `app/nav.ts`, `app/router.tsx`, `shared/icons.tsx` (icône couverts), `app/AppLayout.tsx`,
  `ai/NexoraAiPanel.tsx`, `shared/theme.css` (barre du haut sur téléphone)

## 4. APIs

Nouvelles : `GET /restaurant/setup` (établissements avec emplacement de stock, module actif ou non),
`GET /restaurant/areas`, `POST /restaurant/orders/items` (ajout d'articles à une commande ouverte),
`POST /restaurant/orders/cancel` (annulation motivée), `GET /restaurant/recipes`, `GET /restaurant/recipes/options`.
Étendues : tables (salle, couverts, état cuisine, heure d'ouverture, total en cours), commande (lignes avec identifiant,
précisions, heure d'ajout ; note, heures cuisine, emplacement, devise, serveur, statut), recettes (unité, coût).
Réutilisées par l'écran : `/pos/products/search`, `/pos/cart/preview`, `/pos/tender-options`, `/pos/sessions/current`.
Pas de migration : le schéma (V027) contenait déjà tout ce qu'il fallait, y compris `sale_line_modifier`.

## 5. Fonctionnalités connectées — défauts corrigés

1. **Règlement non idempotent** : chaque essai créait une nouvelle opération ; une coupure après encaissement suivie d'un
   nouvel essai pouvait produire **deux ventes**. L'identifiant d'opération est désormais dérivé de la commande, et
   l'écran envoie une clé d'idempotence fixée à l'ouverture du dialogue.
2. **Précisions de cuisine ignorées** : acceptées par l'API, jamais enregistrées. Elles sont conservées
   (`sale_line_modifier`) et affichées en salle et en cuisine.
3. **Impossible d'ajouter des articles** à une commande ouverte (dessert, deuxième tournée) ; le ticket revient « à
   préparer » s'il était prêt ou servi, et la cuisine voit la nouvelle vague.
4. **Impossible d'annuler** une commande : la table restait occupée pour toujours. Annulation motivée (auteur ou droit
   d'annulation de vente), table libérée, ticket retiré de la cuisine, motif conservé et audité.
5. **Deux serveurs sur la même table** au même instant : les deux commandes passaient. L'occupation est maintenant
   atomique.
6. **Recette sur un plat stocké** : acceptée mais **jamais appliquée** (le moteur ne déroule la recette que des produits
   non stockés). Refusée avec explication.
7. **Unité d'ingrédient ignorée** : une recette en grammes sur un ingrédient stocké en kilos sortait 1000 fois trop de
   stock (aucune conversion n'existe à la sortie). L'unité doit être celle du stock de l'ingrédient.
8. Recette : plat composant de lui-même, ingrédient en double, quantité nulle, perte ≥ 100 % acceptés.
9. **Cuisine modifiable sur une commande réglée ou annulée** ; contrôle du module absent sur la cuisine et le règlement.
10. **Codes de salle / table en double** : erreur brute de base de données ; places à 0 acceptées ; salle d'un autre
    établissement acceptée.
11. **Collision de référence** de commande (table + heure) lorsqu'une table est rouverte à la même seconde un autre jour :
    erreur 23505 révélée par les tests.
12. Pourboire négatif accepté ; il est maintenant contrôlé et présenté honnêtement comme non encaissé.

## 6. Tests ajoutés

- Intégration (`SectorModulesTests`, +2) : cycle complet (code en double, places, précisions, table occupée, total,
  nouvelle vague renvoyée en cuisine, règlement rejoué sans seconde vente, cuisine figée après règlement, annulation
  motivée qui libère la table) ; règles de recette (plat stocké, plat composant de lui-même, perte, unité ≠ unité de
  stock, coût estimé, options).
- Frontend (+5) : clé d'idempotence identique à chaque essai de règlement, routes d'ajout / annulation, précisions,
  repérage de la nouvelle vague, âge des tickets.

## 7. Tests exécutés

| Suite | Résultat |
|---|---|
| Unitaires backend | 50 / 50 |
| Intégration backend | 466 / 466 |
| API backend | 186 / 186 |
| Frontend Vitest | 249 / 249 (53 fichiers) |
| Scénario API (gérant, caissier) | 50 / 50 |
| Navigateur (gérant, caissier) : salle et table créées, recette consultée, commande avec précisions, ajout d'articles, cuisine Commencer → Prêt → Servi, paiement avec pourboire contrôlé, table libérée, nouvelle commande annulée avec motif ; salle sur **téléphone (390 px)** | réussi, 0 erreur, 0 requête en échec, 0 boîte native |

## 8. Résultats

Le restaurant est utilisable de bout en bout depuis l'interface : un serveur ouvre une table, envoie la commande avec ses
précisions, la cuisine la voit et l'avance, des articles s'ajoutent en cours de repas, l'encaissement passe par le
moteur de vente (taxes, promotions, caisse, comptabilité, **sortie des ingrédients selon la recette** — vérifiée :
3 plats × 2 kg sortis du stock), et la table se libère. Un double clic ou une coupure pendant le paiement ne crée
jamais une seconde vente. Chaque rôle ne voit que ses onglets : le caissier-serveur n'a que la Salle, sans accès cuisine
ni recettes.

## 9. Problèmes rencontrés

- **Barre du haut débordant sur téléphone** (toutes les pages, terrain compris, non vu en phase 12 qui ne contrôlait
  que les fenêtres) : 167 px de trop à 390 px de large. Corrigé dans la feuille commune : sur écran étroit, libellés
  secondaires masqués (« En ligne », « Ctrl K », « NEXORA AI »), icônes conservées, retour à la ligne.
- Validation native du navigateur (`min=1`) qui court-circuitait les messages de l'application : formulaires du
  restaurant en `noValidate`. Boutons de création désactivés tant que les établissements ne sont pas chargés (un clic
  trop rapide affichait « Choisissez l'établissement » alors que la liste allait se remplir).
- Premier passage navigateur lent (48 s pour la page de connexion) pendant que les suites backend tournaient : rejoué
  après, sans changement de code.

- **Restaurant ABC** (données de recette) n'a ni produits, ni emplacement de stock, ni caisse : il ne peut pas servir à
  la recette restaurant. La QA s'est faite en activant le module sur Supermarché ABC (base `nexora_qa`).
- Lecture des tables quand le module est désactivé : renvoie la liste (vide) plutôt qu'un refus ; toutes les écritures
  sont refusées (`restaurant.module_disabled`) et l'écran affiche un bandeau. Choix conservé.

## 10. Dette technique restante

- **Pourboire** : enregistré pour mémoire, ni encaissé ni comptabilisé (pas de compte de pourboires dans le moteur).
- **Précisions** sans incidence sur le prix (`price_delta` = 0) : pas de suppléments payants.
- Pas de transfert de table, de fusion ou de partage d'addition (non supportés par le backend).
- Pas de conversion d'unité dans les recettes (l'unité est forcée à celle du stock).
- Marque « nouveau » en cuisine : seulement pour les articles ajoutés après « Commencer » (avant, tout est encore à
  préparer).
- Cuisine actualisée par interrogation (10 s), pas en temps réel ; pas d'impression de bon cuisine.
- Pas de réservations (statut `reserved` présent au schéma, sans flux).

## 11. Prochaine phase recommandée

Phase 14 — Groupe & consolidation.
