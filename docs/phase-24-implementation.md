# Phase 24 — Stock, Entrepôts, Lots & Inventaires — implémentation

**Statut : livré et vérifié.** Migration `V037`. 9 tests d'intégration (`StockLifecycleTests`),
1 test API (`IdempotencyFilterTests` — jeton d'annulation), 3 tests frontend
(`inventoryApi.test.ts`). 150 tests backend verts (Debug + Release, 0 warning).

Le moteur de stock était déjà construit en **Phase 5** : `inventory_location`, `stock_balance`
(trigger de coût moyen pondéré), `stock_movement` immuable, `lot` / `lot_stock`,
`stock_transfer(_line)`, `inventory_count(_line)`, `stock_adjustment(_line)`, `IStockLedger`
(écrivain unique), FEFO, réservations, `product_reorder_rule` / `IReorderService`,
`InventoryOperationsService` (transferts, inventaires, pertes → finance). La Phase 24
**valide** cet existant et ajoute une couche concrète : **cycle de vie des lots**
(blocage / rappel / balayage des expirés), **politique de stock négatif configurable**,
**détail des entrepôts / emplacements**, et les **lectures manquantes** (§41).

---

## Correspondance cahier des charges → implémentation

| § | Exigence | Où |
|---|---|---|
| 2 | Le mouvement est la trace ; corrections = mouvements | `stock_movement` immuable, `IStockLedger` seul écrivain (Phase 5) |
| 3-4 | COMPANY → ESTABLISHMENT → WAREHOUSE → LOCATION ; entrepôt (responsable, adresse, capacité) | `warehouse` + `V037` : `manager_user_id`, `address`, `capacity`. `SqlWarehouseService.CreateWarehouseAsync` + `POST /warehouses` |
| 5 | Types d'emplacements (magasin, réserve, chambre froide, pharmacie, cuisine, transit, retour, quarantaine, virtuel…) | `inventory_location.location_type` étendu (`V037`) : 12 types. `InventoryLocation.Types` / `NonPhysicalTypes` |
| 6 | Emplacement : code, zone, rayon, étagère, bin, capacité, produit préféré | `inventory_location` + `V037` : `code` (unique partiel), `zone`, `aisle`, `shelf`, `bin`, `capacity`, `preferred_product_id`, `manager_user_id`. `SqlWarehouseService.CreateLocationAsync` (`inventory.location_target` si physique sans rattachement) + `POST /warehouses/locations` |
| 7 | Stock séparé par emplacement | `stock_balance` `UNIQUE (product_id, inventory_location_id)` (Phase 5) |
| 8 | `AVAILABLE = ON_HAND − RESERVED − BLOCKED` ; en transit / quarantaine / expiré | `stock_balance.quantity_available` (colonne générée). Le stock des lots bloqués / expirés / rappelés est exclu de la vente via le filtre FEFO `status = 'active'` |
| 9, 26 | Réservations | `IStockLedger.ReserveAsync` / `ReleaseAsync` (delta sur `stock_balance.quantity_reserved`) — Phase 5. Entité de réservation avec échéance : **manque documenté** |
| 10-11 | Types de mouvement, source polymorphe | `MovementType` (16 types), `stock_movement.reference_type`/`reference_id`. `RecordLossAsync` : le type de mouvement suit le type de perte (`loss`/`breakage`/`expiry`/`found`) |
| 12 | Lot : fournisseur, date réception, statut, document source | `lot` + `V037` : `supplier_id` (FK), `received_at`. `PurchasingService.ReceiveAsync` renseigne désormais fournisseur + date de réception sur le lot créé |
| 13 | Expiration : jours d'alerte, statut normal/proche/expiré, blocage vente, rapport, notifications | `Lot.ExpiryStatus(today, alertDays)` → `normal`/`near`/`expired`/`none`. Paramètres `stock.expiry_alert_days` (30), `stock.block_expired_sales` (`V037`). `ILotService.ExpiringAsync` (rapport §40) + `GET /stock/lots/expiring`. Alerte `near_expiry`/`expired` dans `GET /stock/alerts` |
| 13-14 | Balayage automatique des expirations | **`ILotService.SweepExpiredAsync`** : passe les lots actifs dépassés à `expired`, sort le stock (`expiry`), comptabilise la perte (`InventoryWriteOffRecorded`). Idempotent. Exécuté par le **planificateur** (Phase 16, `SchedulerRunner` → `SchedulerTickResult.LotsExpired`) + `POST /stock/expire-lots`. Test `Expired_lot_sweep_writes_off_and_removes_it_from_sale` |
| 14 | FEFO, override contrôlé, compatibilité réservation | `IStockLedger.PickFefoAsync` : tri `expiration_date`, ne retient que `lot.status = 'active'` et le disponible net (`on_hand − reserved`) — Phase 5. Override FEFO avec audit : **manque documenté** |
| 15 | Pharmacie : quarantaine, blocage lot, rappel, traçabilité | **`ILotService`** : `BlockAsync` (→ `quarantine`/`blocked` + `blocked_at/reason/by`), `UnblockAsync`, `RecallAsync` (→ `recalled` + `recall_reference`, référence obligatoire). Motif obligatoire. `POST /stock/lots/{id}/{block,unblock,recall}`. Audit (`trg_audit_lot`, `V037`). Tests `Blocked_lot_is_excluded_from_fefo`, `Recall_freezes_the_lot_and_records_the_reference` |
| 16-19 | Restaurant / supermarché / grossiste / production | Recettes → ingrédients (Phase 8, `IRecipeExpansion` dans `SalesService`) ; PLU / pesé / code-barres (Phase 23) ; conversions carton/palette (`unit_conversion`, Phase 5). Ordres de fabrication : **hors périmètre V1.0** |
| 20-23 | Inventaire (préparation → comptage → écart → validation → ajustement → audit), tournant | `InventoryOperationsService.StartCountAsync` / `ValidateCountAsync` (fige la quantité système, écart → `stock_adjustment` + `InventoryRevaluationRecorded`) — Phase 5. Inventaire tournant planifié : s'appuiera sur le planificateur (backlog) |
| 24-25, 36 | Transfert A → transit → B ; inter-établissements ; stock en transit | `InventoryOperationsService.CreateAndShipTransferAsync` (sortie `transfer_out` à la source, statut `in_transit`), `ReceiveTransferAsync` (`transfer_in`, partiel OK) — Phase 5. Vérifié bout-en-bout par `Full_supply_chain_scenario_reconciles` (§48) |
| 27-28 | Valorisation : coût moyen exact, FIFO/standard/lot ; changement historisé | `stock_balance.average_cost` (trigger WAC), `product.valuation_method`, `product_cost_history` (Phase 5). Paramètre `stock.valuation_method` (Phase 14) |
| 29 | Coût → comptabilité (réception, COGS) | Événements `PosSaleRecorded` (COGS), `GoodsReceiptPosted`, `InventoryWriteOffRecorded`, `InventoryRevaluationRecorded` → `PostingRuleSet` (Phases 4-6) |
| 30 | Seuils min/max, point de commande, alertes rupture / surstock | `product_reorder_rule`. **`IStockQueries.AlertsAsync`** classe `out_of_stock` / `low_stock` / `overstock` + `near_expiry` / `expired`. `GET /stock/alerts`. Test `Stock_alerts_flag_low_stock_and_near_expiry` |
| 31 | Réapprovisionnement : suggestions, fournisseur préféré, MOQ, délai | `IReorderService.SuggestionsAsync` + `GET /inventory/reorder-suggestions` (Phase 5) |
| 32 | **Stock négatif** : interdit par défaut, autorisation configurable, alerte, audit | **`IStockAvailabilityPolicy.AllowNegativeAsync`** lit `stock.negative_stock_allowed` (paramètre entreprise, `is_sensitive`). `SalesService` : si insuffisant et politique active → vente autorisée + `LogWarning` (§32) ; sinon `pos.insufficient_stock`. Le changement de paramètre est audité (Phase 14). Test `Negative_stock_is_refused_by_default_and_allowed_by_setting` |
| 33 | Pertes et casse : type, motif, lot, valeur, approbation | `InventoryOperationsService.RecordLossAsync` → `stock_adjustment` + mouvements + `InventoryWriteOffRecorded` (Phase 5) |
| 34-35 | Retours clients / fournisseurs | `SalesReturnService` (Phase 6), `PurchasingService.RecordReturnAsync` (Phase 5) |
| 37-38 | Scanner ; recherche stock (SKU, code-barres, lot, entrepôt, expiration, statut) | `GET /catalog/scan` (Phase 23). `IStockQueries` : `MovementsAsync`, `LotsAsync` (filtres produit / emplacement / statut / `expiringWithinDays`), `BalancesAsync` |
| 39-40 | Dashboard & rapports stock | `IStockQueries.AlertsAsync` / `BalancesAsync` / `MovementsAsync` ; `ILotService.ExpiringAsync`. Écran **`StockCenterPage`** (KPIs + onglets Alertes / Péremptions / Lots / Mouvements). Rapports détaillés Phase 7 (`IStockReports`) |
| 41 | API | `GET/POST /warehouses`, `GET/POST /warehouses/locations`, `GET /stock/balances`, `GET /stock/movements` (pagination `limit`/`offset`), `GET /stock/lots`, `GET /stock/lots/expiring`, `GET /stock/alerts`, `POST /stock/lots/{id}/{block,unblock,recall}`, `POST /stock/expire-lots`. **Écart de nommage** vs §41 : préfixe `/stock/` (cohérence modules) — pas `/stock-movements`, `/lots`, `/stock-alerts` à la racine |
| 42 | Permissions | `inventory.manage_lot` (blocage/rappel), `inventory.view_valuation`, `inventory.manage_warehouse` (`V037`) ; + existantes `view`/`adjust`/`transfer`/`count`/`validate_adjustment`/`write_off` |
| 43 | Audit : blocage/déblocage lot, override FEFO, stock négatif autorisé | `trg_audit_lot` (`V037`) sur toute écriture de `lot`. Changement de `stock.negative_stock_allowed` audité (Phase 14). Override FEFO : manque |
| 44 | Concurrence : ventes simultanées, verrou de ligne, transactions atomiques | `IStockLedger.LockAvailableAsync` (`SELECT … FOR UPDATE`), `UnitOfWork.ExecuteInTransactionAsync` (Phase 5-6) |
| 45 | Offline : réservation/décrément local, réconciliation serveur, pas de double effet | Moteur de synchronisation (Phase 10) : rejeu via `SalesService`, idempotence par `operation_uuid`, conflits de stock — inchangé |
| 46 | Performance : index produit/warehouse/location, index lots expiration, pagination | Index `stock_movement_product_loc_idx`, `lot_expiration_idx` (Phase 5) + `lot_company_expiry_idx`, `inventory_location_code_uk` (`V037`). `MovementsAsync` paginé (`limit` ≤ 500, `offset`). Traitements lourds → planificateur |
| 47-48 | Tests + scénario complet | `StockLifecycleTests` (9) — le scénario §48 (achat 100 → vente 10 → retour 2 → transfert 20 → réception : source 72 / dest 20) est couvert par `Full_supply_chain_scenario_reconciles` |

## `V037` — contenu

- `lot` += `supplier_id` (FK), `received_at`, `blocked_at`, `blocked_reason`, `blocked_by`, `recall_reference` ; `status` CHECK += `blocked` ; index `lot_company_expiry_idx` ; trigger `trg_audit_lot`
- `inventory_location` += `code` (unique partiel), `zone`, `aisle`, `shelf`, `bin`, `capacity`, `manager_user_id`, `preferred_product_id` ; `location_type` CHECK → 12 types ; `target_chk` assoupli pour `return`/`quarantine`
- `warehouse` += `manager_user_id`, `address`, `capacity`
- `setting_definition` : `stock.expiry_alert_days` (30), `stock.block_expired_sales` (true)
- `permission` : `inventory.manage_lot`, `inventory.view_valuation`, `inventory.manage_warehouse`
- **162 tables** (aucune nouvelle), **150 en RLS**

## Correction transverse

`IdempotencyFilter.HashArguments` sérialisait *tous* les arguments d'action, y compris le
`CancellationToken` injecté par MVC → `NotSupportedException` (`System.IntPtr`) sur tout endpoint
`[Idempotent]` sans corps (`POST /stock/expire-lots`). Corrigé : le jeton d'annulation est
désormais exclu du hachage. Test `Cancellation_token_argument_does_not_break_hashing`.

## Manques assumés (backlog)

- Entité de réservation avec échéance et libération automatique (§9, §26) — aujourd'hui un simple compteur
- Override FEFO tracé (§14, §43)
- Inventaire tournant planifié (§23) et inventaire massif asynchrone (§46)
- Ordres de fabrication / production (§19) — hors périmètre V1.0
- Stock en quarantaine / endommagé comme *buckets* distincts de `stock_balance` (§8) — géré aujourd'hui par le statut du lot
