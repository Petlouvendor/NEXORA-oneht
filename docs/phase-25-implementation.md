# Phase 25 — Ventes, POS, Commandes & Paiements — implémentation

**Statut : livré et vérifié.** Migration `V038`. 8 tests d'intégration (`SalesOrderTests`),
2 tests frontend (`ordersApi.test.ts`). 158 tests backend verts (Debug + Release, 0 warning).

Le moteur de vente POS était déjà construit en **Phase 6** : `SalesService.CheckoutAsync`
(saisie unique → stock + caisse + créance + finance + comptabilité, atomique et idempotente),
paiements multiples & partiels, rendu monnaie, contrôle du crédit client + autorisation
exceptionnelle, `SalesReturnService` (retour partiel/total, réintégration stock, avoir/
remboursement), `CashSessionService` (ouverture / entrées-sorties / clôture avec écart),
promotions & coupons (Phase 8/23), snapshots transactionnels sur `sale_line`. La Phase 12
a fourni l'abstraction des paiements externes (`payment_intent`, webhooks signés). La Phase 24
a ajouté la politique de stock négatif. **La Phase 25 valide cet ensemble et ajoute le maillon
manquant : la commande client** (§18-20, §25, §43).

---

## Correspondance cahier des charges → implémentation

| § | Exigence | Où |
|---|---|---|
| 2, 4 | Backend autorité ; vente validée non modifiable ; cycle d'états défini côté serveur | `SalesService` (Phase 6). Commande : `SalesOrderStatus.CanTransition` (`draft → confirmed → reserved → partially_fulfilled → fulfilled` / `cancelled`) |
| 3 | Types de ventes (immédiate, crédit, commande retrait/livraison, gros, service, offline) | `SaleType` (Phase 6) + **`sales_order.order_type`** : `pickup`, `delivery`, `wholesale`, `reservation` |
| 5-8 | Écran POS, recherche, panier, remises, taxes | `PosPage` (Phase 6/13), recherche/scan `GET /catalog/{search,scan}` (Phase 23), politique stock négatif (Phase 24 §32) |
| 9 | Snapshots transactionnels (description, prix, taxe, remise, devise, taux) | `sale_line` fige `unit_price` / `discount` / `tax_amount` / `cost_snapshot` / `original_unit_price` (Phase 6). **Commande** : `sales_order_line` fige le prix à la création — l'exécution passe `unit_price` en `UnitPriceOverride` de la caisse. Test `Order_line_price_is_frozen_at_order_time` |
| 10-12 | Moteur de prix explicable, remises, taxes HT/TTC | `CatalogPricingEngine` (Phase 23 §22) ; `ISalesPricing.PriceCartAsync` (POS & commandes) ; `product_price.tax_included` (Phase 23 §25) |
| 13-15 | Paiements multiples / partiels / espèces, rendu monnaie | `SalesService.CheckoutAsync` : plusieurs `TenderInput`, somme contrôlée, rendu monnaie sur le dernier moyen, `SalePayment` par allocation (Phase 6) |
| 16 | Paiement externe : pending → confirmation fournisseur → webhook, idempotency key | `payment_intent` + `IPaymentGateway` + webhooks HMAC entrants/sortants (Phase 12). « Une réponse positive du frontend ne suffit jamais » — inchangé |
| 17 | Vente à crédit : client obligatoire, limite, blocage, autorisation exceptionnelle | `ICreditControl.EvaluateAsync` + `CheckoutCommand.CreditOverrideReason` → `sale.credit_override_by/reason` (Phase 6) |
| 18 | **Commande** : création, client, lignes, réservation stock, acompte, statut, exécution | **`OrderService`** : `CreateOrderAsync` (lignes tarifées, totaux), `ConfirmOrderAsync`, `ReserveOrderAsync` (`IStockLedger.ReserveAsync` par ligne, échec si disponible insuffisant), `RecordDepositAsync`, `CompleteOrderAsync` (lève la réservation → `SalesService.CheckoutAsync` pour la quantité livrée → `sale.sales_order_id`), `CancelOrderAsync`. `V038` : `sales_order` / `sales_order_line` |
| 19 | Commande à crédit : acompte, solde, échéance, limite | `sales_order.on_credit` / `due_date` / `deposit_amount`. Exécution sans tenders (ou partiels) ⇒ `SalesService` crée la créance. Test `Credit_order_completion_creates_a_receivable` |
| 20 | Livraison : adresse, statut, impact stock, document | `sales_order.delivery_address` + `order_type = 'delivery'`. Créneau/responsable/preuve : **backlog** |
| 21 | Vente de service (aucun mouvement stock) | `product_type = 'service'` → `RequiresStock = false` (Phase 6/23) ; les lignes de commande services ne sont pas réservées (`ProductRequiresStockAsync`) |
| 22-25 | Restaurant / supermarché / pharmacie / grossiste | Recettes → ingrédients (Phase 8), PLU/pesé (Phase 23), lots/FEFO/blocage (Phase 24), tarifs quantité (Phase 23 §20), commande + réservation + crédit (Phase 25) |
| 26-27 | Retours & remboursements (partiel/total, motif, lot, quarantaine, moyen d'origine) | `SalesReturnService.ReturnAsync` : quantité bornée, restock optionnel, `refund_method` (`cash`/`card`/`store_credit`/`credit_note`/`original`) → `SaleRefundRecorded` (Phase 6) |
| 28 | Avoirs (solde, réutilisable, expiration, utilisation partielle) | `sale_return.refund_method = 'credit_note'` + compte `StoreCredit`. **Cycle de vie complet de l'avoir (solde consommable, expiration) : backlog** |
| 29 | Annulation : avant validation selon état ; après = retour/contrepassation | `SalesService.VoidAsync` (parked → void ; validée → « utiliser un retour total »). **Commande** : `CancelOrderAsync` (motif obligatoire, libère les réservations, interdit si `fulfilled`) |
| 30 | Ventes suspendues (panier, restauration, expiration) | `SalesService.ParkAsync` → `sale.status = 'parked'` + `parked_reference` (Phase 6) |
| 31 | Tickets, factures, avoirs, bon de commande / livraison, numéro unique | Numérotation `document_sequence` par type (`sale`, `sale_return`, **`sales_order`**). Rendu PDF/template : **backlog** |
| 32 | Impression (par caisse, réimpression selon permission) | `pos.reprint` seedée. Pilotes d'impression : hors périmètre backend |
| 33-36 | Clôture & sessions de caisse, gestion espèces, multi-utilisateur | `CashSessionService` : ouverture / `CashInOut` / clôture (attendu vs compté → `CashCountRecorded`), `cash_session` (un ouvert par caisse), traçabilité `user_id` (Phase 6) |
| 37 | Offline POS (outbox local, idempotence serveur, conflit explicite) | `shared/offlineQueue.ts` (Phase 19) + moteur de synchronisation (Phase 10) : rejeu via `SalesService`, idempotence par `operation_uuid`, conflits de stock/crédit |
| 38 | Stock à la vente : disponibilité, réservation, décrément, lot, FEFO | `SalesService` (verrou `LockAvailableAsync`, FEFO) ; **commande** : réservation via `IStockLedger` puis libération avant la vente (sinon l'auto-réservation bloquerait sa propre vente) |
| 39-40 | Finance & comptabilité à la vente | Événements `PosSaleRecorded` / `CreditSaleRecorded` / `SaleRefundRecorded` / `CustomerPaymentReceived` → `PostingRuleSet` (Phases 4-6). Acompte commande → `CustomerPaymentReceived` non alloué (Dr caisse / Cr compte d'attente « Acompte client ») |
| 41 | Anti-fraude (remises anormales, annulations/retours fréquents, écarts caisse) | Moteur de fraude à signaux scorés (Phase 11), audit `audit_log` immuable |
| 42 | Permissions POS | `pos.{sell,sell_credit,discount,change_price,return,refund,void_sale,open_session,close_session,cash_in_out,reprint}` + **`pos.manage_order`** (`V038`) |
| 43 | API | **`OrdersController`** (`/api/v1/orders` : `POST`, `GET`, `GET /{id}`, `POST /{id}/{confirm,reserve,deposit,complete,cancel}`) ; **`SalesController`** (`/api/v1/sales` : `POST` = checkout, `GET /{id}` détail, `POST /{id}/payments` = règlement d'une créance, `POST /{id}/returns`, `POST /{id}/cancel`). La caisse tactile garde `/pos/*` |
| 44 | Événements métier via outbox, consommateurs idempotents | Webhooks sortants signés + file de relance (Phase 12). Outbox transactionnel dédié : **backlog** (déjà noté Phase 18) |
| 45-49 | Tests | `SalesOrderTests` (8) ; les scénarios ventes/paiements/retours/caisse de Phase 6 (`PosSalesTests`, 10) et Phase 24 (`StockLifecycleTests`) restent verts |

## `V038` — contenu

- `sales_order` (RLS `company_isolation`, audit `trg_audit_sales_order`) + `sales_order_line`
  (`quantity_fulfilled ≤ quantity`) — **164 tables, 152 RLS**
- `sale.sales_order_id` (FK) + index partiel — la vente issue d'une commande référence celle-ci
- `permission` : `pos.manage_order` (créer & exécuter des commandes)
- `document_sequence` : type `sales_order` (numérotation `CMD…`)

## Le service de commande (`OrderService`)

```
CreateOrder → (Confirm) → (Reserve : IStockLedger.ReserveAsync par ligne)
            → (Deposit : CustomerPaymentReceived non alloué)
            → Complete : release réservation → SalesService.CheckoutAsync(lignes reliquat,
                         UnitPriceOverride = prix commande) → sale.sales_order_id
                       → quantity_fulfilled ; statut fulfilled / partially_fulfilled
```

- L'exécution s'appuie **entièrement** sur `SalesService.CheckoutAsync` : aucune règle de
  vente, stock, finance ou comptabilité n'est dupliquée (§2).
- Réservation puis libération : la disponibilité du POS = `on_hand − reserved` ; il faut donc
  lever la réservation **dans la même transaction** avant la vente, sinon elle bloquerait sa
  propre exécution (§38).
- Le prix de la ligne de commande est figé à la création et rejoué tel quel (§9).

## Manques assumés (backlog)

- Cycle de vie complet de l'avoir (solde consommable comme moyen de paiement, expiration, utilisation partielle) — §28
- Suivi de livraison (créneau, responsable, preuve, statut) — §20
- Rendu des documents (ticket / facture / bon de commande / bon de livraison en PDF) — §31
- Application automatique de l'acompte à la facture d'exécution (aujourd'hui : avance sur compte client, allocation manuelle) — §18-19
- Outbox transactionnel dédié pour les événements métier §44 (déjà noté Phase 18)
- Pourboire / partage d'addition restaurant — §22
