# NEXORA — Phase 6 — État de l'implémentation

Référence : [`phase-6-ventes-pos.md`](phase-6-ventes-pos.md).

## Ce qui est livré

### Base de données — `V025`

| Objet | Rôle |
|---|---|
| `sale` (+ colonnes) | `cash_session_id`, `amount_tendered`, `change_given`, `parked_reference`/`parked_at`, `credit_override_by`/`_reason`, `voided_at`/`void_reason` |
| `sale_line` (+ colonnes) | `original_unit_price` (avant modification manuelle), `promotion_id`, `unit_cost` |
| `sale_promotion` | résultat de promotion **figé** à la transaction (§10 : l'historique ne change pas) |
| `sale_return` (+ colonnes) | `refund_method`, `refund_payment_id`, `restocked`, `source_operation_uuid`, `journal_entry_id`, `cash_session_id` |
| `sale_return_line` (+ colonnes) | `lot_id`, `unit_cost`, `tax_amount` |
| `coupon` | `UNIQUE (id, company_id)` (FK composite) |
| `account_mapping` | clés `sales_returns`, `store_credit`, `tips_payable` |
| permissions | `pos.*` (§41) + rattachement aux rôles |

Démo : 2 moyens de paiement (Espèces / Carte), compte `7019 Retours et remises sur ventes`,
caisse rattachée au compte financier, mappings.

### Backend — module `Sales`

```
Nexora.Domain/Sales/            Sale, SaleLine, SalePayment, SalePromotion, SaleReturn,
                                CashRegister, CashSession, CashMovement

Nexora.Application/Sales/
  ISalesPricing (SqlSalesPricing)   recalcul serveur déterministe : prix (product_price par
                                    quantité / groupe client / devise) + promotions + coupon + taxe
  ICreditControl (SqlCreditControl) limite globale, encours projeté, retards, autorisation (§17)
  SalesService.CheckoutAsync        LE parcours : recompute → contrôle crédit → verrou stock
                                    (SELECT … FOR UPDATE) → transaction atomique :
                                    sale + lignes + mouvements stock (FEFO) + payment + sale_payment
                                    + cash_movement + sale_promotion + moteur financier
  SalesService.ParkAsync            vente en attente (§34-35), jamais considérée validée
  SalesReturnService.ReturnAsync    retour partiel/total → réintégration stock + écriture de correction
  CashSessionService                ouverture / retrait-dépôt / clôture + écart → moteur financier
```

### Intégration financière (Phase 4)

Le POS déclenche les services ; il ne crée pas d'écriture (§39). Deux événements ajoutés :

| Opération | Événement | Écriture |
|---|---|---|
| Vente POS (comptant / crédit / mixte) | `PosSaleRecorded` | D moyens + D clients (crédit) / C ventes + C TVA + COGS |
| Retour / remboursement | `SaleRefundRecorded` | D retours + D TVA / C moyen ou C clients ou C avoir + réintégration COGS |

`somme(moyens) + montant_crédit = net + taxe`. Le brouillon est vérifié équilibré avant validation.

### Frontend

`frontend/nexora-web/src/pos/PosPage.tsx` — écran POS : recherche/scan, panier, totaux
permanents, montant reçu + rendu, bouton **Payer** appelant `/api/v1/pos/checkout`
avec `Idempotency-Key`. Route `/pos`. `npm run build` OK.

## Vérifications exécutées

**Tests d'intégration `Nexora.IntegrationTests/PosSalesTests` — 10/10**, scénarios §42 :

| Scénario | Vérifié |
|---|---|
| Vente comptant | stock −3, caisse +300, écriture équilibrée |
| Vente avec taxe | taxe 20 (200 net × 10 %), total 220 |
| Vente avec remise ligne | remise 50, total 450 |
| Paiement multiple + rendu monnaie | carte 600 + espèces 500, total 1000, **rendu 100**, écriture équilibrée |
| Vente à crédit / mixte | créance 1000 (OPEN) ; puis mixte : payé 400, crédit 600 |
| Dépassement de limite | refus `pos.credit_limit`, puis **autorisation** avec motif |
| Doublon de synchronisation (offline) | même `operationUuid` → une seule vente, un seul mouvement |
| Retour partiel | réintégration stock (+4), écriture de correction équilibrée, statut `partially_refunded` |
| Ouverture / clôture de caisse + écart | théorique 5300 (5000 + 300), **écart −20**, 1 vente |
| Deux caisses, même stock | verrou `FOR UPDATE` → **exactement une** des deux ventes passe, pas de sur-vente |

Smoke API : `POST /api/v1/catalog/products` → `POST /api/v1/purchasing/receipts` →
`POST /api/v1/pos/checkout` → vente `2026-000001` ;
`GET /api/v1/accounting/reports/trial-balance` → balance équilibrée
(Caisse D 160 / Ventes C 160 / COGS D 90 / Stocks C 90 — D 250 = C 250).

**Total backend : 49 tests verts** (16 unitaires + 2 API + 31 intégration), build Release 0 warning.
DB : 26 migrations (V001–V025), 116 tables.

## Couverture des critères de sortie (§43)

| Critère | État |
|---|---|
| Parcours de vente complet | ✅ `SalesService.CheckoutAsync` (comptant / crédit / mixte, multi-lignes, FEFO) |
| Paiements multiples | ✅ `TenderSplit[]` + `sale_payment` par moyen, testé |
| Crédit client | ✅ `ICreditControl` + autorisation tracée |
| Retours et remboursements | ✅ `SalesReturnService` + écriture de correction |
| Caisse et sessions | ✅ `CashSessionService` (théorique = ouverture + entrées − sorties) |
| Multi-caisses | ✅ session par caisse ; verrou de stock concurrent testé |
| Offline | ✅ `sale.source_operation_uuid` — idempotence testée |
| Périphériques POS | défini (§6, §21) ; intégration matérielle en Phase intégrations |
| Fonctions sectorielles | ✅ noyau commun (lots/FEFO pharmacie, poids retail, tables/recettes restaurant reliées) |
| Sécurité et anti-fraude | ✅ `[RequirePermission("pos.*")]` ; override crédit tracé ; triggers d'audit |
| Intégration Stock/Finance/Comptabilité | ✅ transaction atomique unique, écritures équilibrées |
| Scénarios critiques testables | ✅ 10 scénarios §42 automatisés |

## Suite — Phase 7

Dashboard, Rapports & Pilotage : transformer les données validées en indicateurs pour
caissier / manager / comptable / direction / groupe.
