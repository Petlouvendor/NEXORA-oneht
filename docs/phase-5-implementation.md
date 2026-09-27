# NEXORA — Phase 5 — État de l'implémentation

Référence : [`phase-5-produits-stocks-achats.md`](phase-5-produits-stocks-achats.md).

## Ce qui est livré

### Base de données — `V024`

| Objet | Rôle |
|---|---|
| `product` (+ colonnes) | variantes (`parent_product_id`, `variant_attributes`), unité d'achat, taxe par défaut, `valuation_method`, produit au poids |
| `unit_conversion` | conversions explicites (§6 : « 1 CARTON = 12 UNITÉS ») |
| `product_reorder_rule` | seuils min/max, point de commande, quantité de réappro, délai (§12, §19) |
| `product_cost_history` | historique du coût unitaire par source (§8, §29) |
| `purchase_request(_line)` | demande d'achat (§22) |
| `stock_transfer(_line)` | transferts inter-emplacements (§14) |
| `inventory_count(_line)` | inventaires, `variance` générée (§15) |
| `stock_adjustment(_line)` | ajustements / pertes / casse / expiration (§16) |
| `supplier_return(_line)` | retours fournisseur (§28) |
| `stock_movement` | types étendus (`loss`, `breakage`, `expiry`, `found`) |
| `account_mapping` | clés `inventory_adjustment`, `inventory_loss`, `goods_in_transit`, `purchase_price_variance` |
| permissions | `catalog.*`, `inventory.*`, `purchasing.*` + rattachement aux rôles |

Démo `demo_abc_group.sql` : 2 fournisseurs, 2 emplacements (Officine + Réserve/entrepôt),
mappings de stock, plan comptable complet.

### Backend — modules `Catalog` / `Inventory` / `Purchasing`

```
Nexora.Domain/{Catalog,Inventory,Purchasing}/   entités du catalogue, du stock, du cycle d'achat

Nexora.Application/
  Inventory/IStockLedger        ÉCRIVAIN UNIQUE des mouvements ; solde + coût moyen + lot maintenus
                                par les triggers PostgreSQL (Phase 2 §36). FEFO, réservations.
  Catalog/CatalogService        produit, prix historisés, fournisseurs (préféré exclusif), code-barres, seuils
  Purchasing/PurchasingService  commande, réception (partielle, lots/expiration → stock + cost history),
                                facture → moteur financier (crédit / comptant), retour fournisseur
  Inventory/InventoryOperationsService   transfert (A↓ → transit → B↑), inventaire (écart → ajustement
                                → réévaluation), perte/casse (write-off → écriture)
  Inventory/IReorderService     suggestions de réapprovisionnement (§19, §35)

Nexora.Infrastructure/Inventory/   EfStockLedger + stores + SqlReorderService + mappings EF
Nexora.Api/Controllers/         CatalogController, PurchasingController, InventoryController
```

### Intégration avec le moteur financier (Phase 4)

Aucune règle financière dupliquée (§2, §38) : le module appelle `IFinanceOperations` /
`IFinancialEngine`.

| Opération Phase 5 | Événement financier |
|---|---|
| Facture fournisseur à crédit | `SupplierInvoiceRecorded` → dette + écriture |
| Achat comptant | `CashPurchaseRecorded` → trésorerie + écriture |
| Écart d'inventaire | `InventoryRevaluationRecorded` → stock ↕ / compte d'ajustement |
| Perte / casse / expiration | `InventoryWriteOffRecorded` → charge / stock |
| Retour fournisseur | `SupplierReturnRecorded` → dette ↓ / stock ↓ / TVA |

Toutes ces opérations sont **atomiques** (transaction unique, partagée avec le moteur : la
réception + les mouvements de stock + l'écriture comptable commettent ou échouent ensemble) et
**idempotentes** via l'identifiant d'opération.

## Vérifications exécutées

**Tests d'intégration — 11/11** (base PostgreSQL réelle), scénarios §39 :

| Scénario | Vérifié |
|---|---|
| Créer un produit (SKU + code-barres) | créé ; SKU dupliqué rejeté |
| Associer deux fournisseurs | 1 seul préféré (exclusivité) |
| Réception → stock + coût moyen pondéré | 200 u., coût 45 = (100×40 + 100×50)/200 |
| Réception partielle sur commande | commande `partially_received`, ligne reçue = 60/100 |
| Lots + FEFO | sortie priorise le lot expirant en premier |
| Transfert entre entrepôts | Officine 100 → 60 ; Réserve 0 → 40 |
| Inventaire → écart validé | ajustement −5, écriture de réévaluation équilibrée (60 = 60) |
| Perte / casse | stock 100 → 90, écriture équilibrée (150 = 150) |
| Achat à crédit / comptant | dette créée ; trésorerie −150 |
| Retour fournisseur | stock 50 → 38, écriture équilibrée |
| Alerte de réappro | produit sous le point de commande listé, fournisseur préféré résolu |

**Total backend : 39 tests verts** (16 unitaires + 2 API + 21 intégration), build Release 0 warning.

Smoke API : `POST /api/v1/catalog/products` → produit ; `POST /api/v1/purchasing/receipts` →
réception ; `GET /api/v1/inventory/balance` → `{ onHand: 100, available: 100, averageCost: 40 }`.

## Couverture des critères de sortie (§40)

| Critère | État |
|---|---|
| Catalogue produit défini | ✅ `CatalogService` + `product`/variantes/unités |
| Prix et coûts historisés | ✅ `product_price` (clôture + nouvelle ligne), `product_cost_history` |
| Stock multi-emplacements | ✅ `inventory_location` + `stock_balance` par emplacement |
| Mouvements de stock traçables | ✅ `IStockLedger` (écrivain unique) → `stock_movement` immuable |
| Inventaires et ajustements | ✅ `inventory_count` → `stock_adjustment` + réévaluation |
| Lots / expiration | ✅ `lot` / `lot_stock` + FEFO testé |
| Réapprovisionnement | ✅ `product_reorder_rule` + `IReorderService` |
| Cycle fournisseur | ✅ commande → réception (partielle) → facture |
| Achats comptants et à crédit | ✅ via moteur financier, testés |
| Retours fournisseur | ✅ `PurchasingService.RecordReturnAsync` + écriture |
| Intégration finance/comptabilité | ✅ 5 événements financiers, écritures équilibrées |
| Permissions et audit | ✅ `[RequirePermission]` + triggers d'audit (Phase 2) |
| Scénarios critiques testables | ✅ 11 scénarios §39 automatisés |
| Secteurs spécialisés sur le noyau commun | ✅ `pharmacy_product`/`recipe`/`promotion` reliés au produit sans le dupliquer |

## Suite — Phase 6

Ventes & POS : le POS exploitera le catalogue, `IStockLedger` et `IFinanceOperations` pour
qu'une vente soit enregistrée **une seule fois** puis propagée vers stock, caisse, créances,
finance et comptabilité.
