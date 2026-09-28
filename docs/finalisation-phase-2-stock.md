# Finalisation produit — Phase 2 : Stock, inventaires, transferts, pertes et ajustements

> Plan maître « NEXORA ONE — Finalisation complète du produit », phase 2.
> Branche `feature/inventory`. Date : 2026-09-28.

## 1. Travail effectué

La page **Stock** devient un centre opérationnel complet (8 onglets) :

| Onglet | Avant | Après |
|---|---|---|
| État du stock | inexistant (la fonction existait, aucun écran) | tableau article × emplacement : en stock, réservé, disponible, min, point de commande, max, valeur, dernier mouvement, péremption ; état **calculé par le serveur** (rupture, faible, normal, surstock) ; filtres emplacement / catégorie / état / recherche ; pagination ; historique des mouvements d'un article |
| Inventaires | aucune interface | ouvrir (complet = tous les articles de l'emplacement, ou partiel/tournant), compter (saisie enregistrée sans impact), revue des écarts avec justification par ligne, validation (commentaire obligatoire si écart), annulation |
| Transferts | aucune interface | expédier, réceptionner (total ou partiel), **clôturer avec écart** (perte en transit comptabilisée), annuler (retour à la source) |
| Pertes & ajustements | aucune interface | perte, casse, expiration, **vol** ; correction ± ; stock retrouvé ; motif obligatoire, justification par ligne, document consultable |
| Lots & péremptions | actions via `window.prompt` | modales avec motif et référence de rappel ; sortie des lots expirés confirmée |
| Alertes, Mouvements | lecture | inchangé + pagination des mouvements |
| Emplacements | aucune interface | création d'entrepôts et d'emplacements (zone, allée, étagère, case) |

## 2. Défauts corrigés

1. **Sur-réception d'un transfert** : recevoir plus que la quantité expédiée était accepté et créait du stock à partir de rien.
2. **Devise codée en dur (« HTG »)** dans la valorisation des écarts d'inventaire et des pertes : faux pour une entreprise tenue dans une autre devise. La devise fonctionnelle de l'entreprise est désormais utilisée.
3. **« Stock retrouvé » enregistré en sortie** (mouvement négatif) ; aucun ajustement positif n'était possible.
4. **Erreurs 500** sur les refus métier (stock insuffisant, contrainte violée) pendant un transfert, une perte ou un inventaire : ils deviennent des erreurs explicites (409 / 422).
5. **Quantités comptées jamais enregistrées** : la saisie n'était possible qu'au moment de la validation, en une fois.
6. Transfert partiellement reçu **impossible à clôturer** (resté ouvert indéfiniment).
7. Ajustements sans lien vers leur écriture comptable (`journal_entry_id` jamais renseigné).
8. **Statut périmé dans une même unité de travail** : les statuts (transfert, inventaire) changés par mise à
   jour directe en base laissaient l'entité suivie par EF à l'ancien statut — une seconde opération dans le
   même traitement pouvait recevoir un transfert déjà annulé. L'entité suivie est désormais réalignée.
9. Quantités affichées au format français figé dans les nouveaux écrans (« 45,087 kg » lu comme 45 mille à
   côté de montants au format de l'utilisateur) : formateurs partagés de l'application utilisés partout.
10. Test BI rouge chaque lundi (vente datée d'hier hors de la semaine en cours) — test corrigé, règle métier inchangée.

## 3. Base de données — V080

- Colonne `note` (justification) sur `inventory_count_line` et `stock_adjustment_line`.
- Type d'ajustement `theft` (vol).
- Audit (`nexora.audit_row`) étendu à : `inventory_count`, `inventory_count_line`, `stock_adjustment_line`, `stock_transfer`, `stock_transfer_line`, `warehouse`, `inventory_location`.

## 4. APIs

Nouvelles : `GET /stock/overview` · `GET /inventory/transfers`, `GET /inventory/transfers/{id}`,
`POST /inventory/transfers/{id}/close`, `POST /inventory/transfers/{id}/cancel` · `GET /inventory/counts`,
`GET /inventory/counts/{id}`, `PUT /inventory/counts/{id}/lines`, `POST /inventory/counts/{id}/cancel` ·
`GET /inventory/adjustments`, `GET /inventory/adjustments/{id}`, `POST /inventory/adjustments` · `GET /warehouses/sites`.

Existantes désormais branchées : `POST /inventory/transfers`, `POST …/receive`, `POST /inventory/counts`,
`POST …/validate` (justification exigée), `POST /inventory/losses`, `POST /warehouses`, `POST /warehouses/locations`.

## 5. Règles garanties par le serveur

- Aucune quantité ne change sans mouvement de stock tracé ; toute variation de valeur est comptabilisée (écart d'inventaire ou perte).
- Coûts et valeurs de stock visibles seulement avec `inventory.view_valuation` (masqués par le serveur).
- Inventaire : quantité système figée à l'ouverture ; l'écart s'applique en delta à la validation, donc les ventes intervenues pendant le comptage restent justes.

## 6. Tests

| Suite | Résultat |
|---|---|
| Unitaires backend | 50 / 50 |
| Intégration backend (dont 9 nouveaux `StockOperationsTests`) | 429 / 429 |
| API backend | 185 / 185 |
| Frontend Vitest (dont 6 nouveaux) | 197 / 197 |
| Scénario API bout en bout (clone `nexora_qa`) | 20 / 20 |
| Parcours navigateur (inventaire complet par l'interface, 8 onglets) | réussi, 0 erreur console |

## 7. Dette technique restante

- Inventaire et transfert au niveau de l'article, pas du lot (les lots sont suivis par les réceptions et les ventes).
- Pas de « brouillon » de transfert : la création expédie immédiatement (état `draft` prévu en base, non utilisé).
- Les conversions d'unités ne sont pas encore proposées dans ces écrans (quantités en unité de base).
