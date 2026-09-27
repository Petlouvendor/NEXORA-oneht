# Phase 26 — Achats, Fournisseurs, Crédit & Dettes — implémentation

**Statut : livré et vérifié.** Migration `V039`. 7 tests d'intégration
(`PurchaseThreeWayMatchTests`), 2 tests frontend (`purchasingApi.test.ts`).
165 tests backend verts (Debug + Release, 0 warning).

Le cycle d'achat était déjà construit en **Phase 5** (`PurchasingService` : commande →
réception partielle avec lots/expiration + historique des coûts, facture → moteur financier,
retour fournisseur → sortie stock + impact dette), **Phase 4** (`SupplierInvoiceRecorded`,
`SupplierPaymentMade`, `supplier_payable`), **Phase 12** (paiements & rapprochement bancaire),
**Phase 24** (réception pharmacie : lots, quarantaine, blocage). La Phase 26 **valide** cet
ensemble et ajoute le maillon défini par le cahier des charges : le **rapprochement 3-way**
et le contrôle des dettes.

---

## Correspondance cahier des charges → implémentation

| § | Exigence | Où |
|---|---|---|
| 2-3 | Cycle besoin → demande → commande → réception → facture → dette ; « une commande n'est pas une dette » | `purchase_request` (V024) → `PurchasingService.CreateOrderAsync` → `ReceiveAsync` (stock) → `RecordInvoiceAsync` (finance) → `supplier_payable` (créé par `SupplierInvoiceRecorded`, uniquement à crédit) |
| 4-5 | Fournisseur (identité, conditions, devise, compte) ; multi-entreprises | `supplier` (V006) porté par `company_id` ; `supplier_account` (conditions par entreprise) ; RLS isole l'historique |
| 6 | Catalogue fournisseur (réf. fournisseur, coût, MOQ, délai, principal) | `product_supplier` (V007) — `supplier_sku`, `purchase_price`, `lead_time_days`, `is_preferred` (index unique partiel) |
| 7-8 | Demande d'achat + workflow d'approbation | `purchase_request(_line)` (V024) : statut `draft/submitted/approved/rejected/ordered/cancelled`. Moteur d'approbation configurable = `approval_request` (Phase 11/14) |
| 9-10 | Commande fournisseur + statuts (brouillon → soumise → approuvée → **envoyée** → reçue → clôturée) | `PurchaseOrderStatus.CanTransition` + **`PurchasingService.{Submit,Approve,Send,Cancel}OrderAsync`**. `V039` ajoute l'état `sent`. `POST /purchasing/orders/{id}/{submit,approve,send,cancel}` |
| 11 | Commande partielle (commandé / reçu / restant, backorder) | `purchase_order_line.quantity_received` bumpé par `ReceiveAsync` ; `RefreshOrderStatusAsync` → `partially_received` / `received` |
| 12-18 | Réception (complète/partielle, lot, expiration, emplacement, contrôle) | `PurchasingService.ReceiveAsync` (Phase 5) + lots/quarantaine/blocage (Phase 24, [[nexora-stock-lots]]) |
| 19 | Facture fournisseur : n°, dates, **lignes**, réf. commande, réf. réception | **`RecordInvoiceAsync` persiste désormais `supplier_invoice_line`** + `supplier_reference` + `goods_receipt_id`. Renvoie `RecordPurchaseInvoiceResult(InvoiceId, PayableId, …)` |
| 20 | **Rapprochement 3-way : COMMANDE ↕ RÉCEPTION ↕ FACTURE** | **`PurchasingService.MatchInvoiceAsync(invoiceId, MatchInvoiceRequest{QuantityTolerancePercent, PriceTolerancePercent})`** → `ThreeWayMatchReport` : par produit, compare quantité facturée vs **reçue** et prix facturé vs **commandé** ; écart hors tolérance → `match_status = 'variance'` + `payment_blocked = true`. `POST /purchasing/invoices/{id}/match`. Détail stocké dans `supplier_invoice.match_detail` (jsonb). Tests `Three_way_match_within_tolerance_passes`, `Price_variance_blocks_the_payment`, `Quantity_variance_is_flagged` |
| 21 | Facture sans commande | `RecordInvoiceAsync` avec `PurchaseOrderId = null` — `MatchInvoiceAsync` renvoie alors `purchasing.no_order` (rapprochement N/A) |
| 22-23 | Achat comptant / à crédit | `RecordInvoiceAsync` : `OnCredit=false` → `RecordCashPurchaseAsync` (règlement immédiat) ; `OnCredit=true` → `RecordSupplierInvoiceAsync` → dette |
| 24-25 | Dette fournisseur (montant, payé, solde, retard, statut) | `supplier_payable` (V012 : `remaining_amount` généré, statut `OPEN/PARTIALLY_PAID/PAID/OVERDUE/CANCELLED`). `IPurchasingQueries.ListPayablesAsync` calcule `daysOverdue` |
| 26-27 | Paiement fournisseur (multiple, partiel, référence, compte) | **`PurchasingService.PayPayableAsync(payableId, PayPayableRequest)`** → contrôle `payment_blocked` (§40) → `IFinanceOperations.RecordSupplierPaymentAsync` avec allocation à la dette. `POST /payables/{id}/payments` (`[Idempotent]`). Test `Full_purchase_to_debt_settlement` (50 000 → 20 000 → 30 000 → 0, statut `PAID`) |
| 28 | Acompte fournisseur | Réutilise `RecordSupplierPaymentAsync` non alloué (avance) — même mécanisme que l'acompte client (Phase 25). Imputation automatique à la facture : **backlog** |
| 29-30 | Crédit / avoir / retour fournisseur | `PurchasingService.RecordReturnAsync` (Phase 5) → sortie stock + `SupplierReturnRecorded` (contre dette ou compte d'attente). Avoir consommable : **backlog** |
| 31-32 | Frais d'achat / coût d'acquisition (répartition sur articles) | Colonne `supplier_invoice.landed_cost_total` **non ajoutée cette phase** — documenté comme manque ; le coût de réception = `unit_cost` de la ligne |
| 33-34 | Taxes achats, devise, écart de change | `tax` récupérable/non (Phase 5) ; conversion fonctionnelle + écart de change dans `PostingRuleSet` (Phase 4 `fx_gain`/`fx_loss`) |
| 35-36 | Échéances & calendrier fournisseurs (à payer 7j/30j/en retard) | `supplier_payable.due_date` + `ListPayablesAsync` trié par échéance ; **`IPurchasingQueries.SupplierStatementAsync`** (relevé : factures + paiements + retours, solde progressif, total en retard). `GET /suppliers/{id}/statement`. Test `Supplier_statement_lists_invoices_and_payments` |
| 37-38 | Budget achat & engagement | **Hors périmètre V1.0** — documenté comme manque (le rattachement `approval_request` couvre l'approbation par seuil §8) |
| 39 | Workflow paiement (rapprochement → approbation → programmation → paiement) | `match` → `payment_blocked` → `PayPayableAsync`. Programmation / lots de paiement : backlog |
| 40 | **Blocage paiement** (écart 3-way, litige, fournisseur bloqué, permission) | `supplier_invoice.payment_blocked` + `PayPayableAsync` refuse `purchasing.payment_blocked` ; `supplier.status = 'blocked'` (V006) ; permission `purchasing.pay`. Test `Price_variance_blocks_the_payment` |
| 41 | Fournisseur en litige | `match_status = 'variance'` gèle le paiement ; résolution = re-`match` avec tolérance ajustée ou `waived` (état prévu au `CHECK`). Entité litige dédiée : backlog |
| 42-43 | Performance / comparaison fournisseur | **Hors périmètre V1.0** (données présentes : `product_supplier`, `goods_receipt`, `supplier_return`) |
| 44 | Réapprovisionnement → suggestion de commande | `IReorderService.SuggestionsAsync` (Phase 5/24) — inchangé |
| 45 | Achats multi-entreprises | `purchase_order` / `supplier_invoice` / `supplier_payable` portés par `company_id` + RLS |
| 46 | API | `GET /purchasing/orders` (+ `{id}`, `{id}/{submit,approve,send,cancel}`), `POST /purchasing/receipts`, `POST /purchasing/invoices` (+ `{id}/match`), `POST /purchasing/returns`, `GET /payables` + `POST /payables/{id}/payments`, `GET /suppliers` + `GET /suppliers/{id}/statement`. **Écart de nommage** vs §46 : préfixe `/purchasing/` pour les commandes / factures / réceptions |
| 47 | Événements métier | `SupplierInvoiceRecorded`, `SupplierPaymentMade`, `SupplierReturnRecorded` → `PostingRuleSet` (Phases 4-5). `PurchaseInvoiceMatched` : le rapprochement met à jour `supplier_invoice` (audité `trg_audit_supplier_invoice`) |
| 48-50 | Intégration stock / finance / comptabilité | Réception → `IStockLedger` ; facture crédit → dette ; paiement → réduit dette ; écritures via `PostingRuleSet` (journal `AC` achats, `BQ`/`CA` banque/caisse), période ouverte contrôlée |
| 51 | Permissions | `V039` : `purchasing.approve_order`, `purchasing.match`, `purchasing.approve_invoice`, `purchasing.pay`, `purchasing.view_payables` (+ existantes `order`/`receive`/`invoice`/`return`/`view`) |
| 52 | Audit | `V039` : `trg_audit_{purchase_order,supplier_invoice,goods_receipt,supplier_return}` sur toute écriture |
| 53-54 | **Anti-fraude & anti-doublon** (n° facture déjà utilisé) | `PurchasingService.RecordInvoiceAsync` rejette `purchasing.duplicate_invoice` si `(company, supplier, lower(supplier_reference))` existe déjà (statut ≠ cancelled) — index unique partiel `supplier_invoice_supplier_ref_uk`. Test `Duplicate_supplier_invoice_reference_is_rejected`. Moteur de fraude à signaux (Phase 11) pour les autres motifs |
| 55-56 | Tests + scénario complet §56 | `PurchaseThreeWayMatchTests` (7) — le scénario §56 est `Full_purchase_to_debt_settlement` |

## `V039` — contenu

- `purchase_order.status` CHECK += `sent` (aucune nouvelle table — **164 tables, 152 RLS**)
- `supplier_invoice` += `match_status` (`unmatched/matched/variance/blocked/waived`), `matched_at/by`,
  `match_detail` (jsonb), `payment_blocked` (+ `payment_block_reason`), `goods_receipt_id` (FK)
- index unique partiel `supplier_invoice_supplier_ref_uk (company_id, supplier_id, lower(supplier_reference))`
- `trg_audit_{purchase_order,supplier_invoice,goods_receipt,supplier_return}`
- 5 permissions Phase 26

## Le rapprochement 3-way (`MatchInvoiceAsync`)

```
FACTURE (lignes) ─┐
                  ├─ par produit ─→ écart quantité = |facturé − reçu| / reçu
COMMANDE (lignes) ┤                 écart prix     = |facturé − commandé| / commandé
RÉCEPTIONS (Σ)   ─┘
   ↓
tout ≤ tolérance ?  →  matched, paiement autorisé
sinon               →  variance, payment_blocked = true  (PayPayableAsync refuse)
```

Le détail par ligne (`MatchLineDto`) est renvoyé **et** persisté en `match_detail`. Le
rapprochement est ré-exécutable (tolérance ajustée) ; l'état `waived` permet une validation
manuelle contrôlée (transition non exposée cette phase — backlog).

## Frontend

`src/purchasing/` — `purchasingApi.ts` + **`PurchasingPage.tsx`** (KPIs dettes/retard + onglets
Dettes / Commandes / Fournisseurs + carte « Rapprochement 3-way » affichant le rapport ligne à
ligne + bouton Payer bloqué quand `paymentBlocked`). Route `/purchasing` (auparavant un
placeholder). Vitest `purchasingApi.test.ts` (2) → 21 tests frontend.

## Durcissement transverse

Le jeu de démonstration (`demo_abc_group.sql`) ne nettoyait pas les tables achats / commandes
clients / documents de stock : re-jouer le seed après une opération d'achat échouait sur une
violation de clé étrangère (`supplier` référencé par `purchase_order`). La liste de purge a été
complétée (`supplier_return`, `supplier_invoice`, `goods_receipt`, `purchase_order`,
`sales_order`, `stock_transfer`, `inventory_count`, `stock_adjustment`, `stock_movement`,
`lot`, `stock_balance`) — le seed redevient idempotent quel que soit l'historique.

## Manques assumés (backlog)

- Frais d'achat / coût d'acquisition avec répartition sur articles — §31-32
- Budgets d'achat & engagement — §37-38
- Avoir fournisseur consommable, acompte imputé automatiquement — §28-29
- Entité « litige fournisseur » + transition `waived` exposée — §41
- Performance & comparaison fournisseur — §42-43
- Programmation / lots de paiement — §39
