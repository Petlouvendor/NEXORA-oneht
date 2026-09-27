# Phase 27 — Clients, Créances & Recouvrement — implémentation

**Statut : livré et vérifié.** Migration `V040`. 7 tests d'intégration
(`CustomerCollectionsTests`), 2 tests frontend (`creditApi.test.ts`). 172 tests backend
verts (Debug + Release, 0 warning).

Le socle crédit client existait déjà : `customer` / `customer_account` (V006), `credit_limit`,
`customer_receivable` (V012, `remaining_amount` généré, allocations de paiement Phase 4/12),
`ICreditControl.EvaluateAsync` (limite, encours projeté, retards, autorisation exceptionnelle —
Phase 6), `dunning_policy` + exécuteur de relances (V033 / Phase 14), aging (Phase 7). La
Phase 27 **valide** cet ensemble et ajoute le **workflow de recouvrement** : promesses de
paiement, litiges, blocages et radiation contrôlée.

---

## Correspondance cahier des charges → implémentation

| § | Exigence | Où |
|---|---|---|
| 2-6 | Cycle client, fiche, compte client | `customer` (V006), `customer_account` (solde + conditions par entreprise + devise) |
| 4-5 | Particulier / entreprise ; client multi-entreprises | `customer.type` ; `customer` porté par `company_id` + RLS (solde & historique par entreprise) |
| 7-8 | Conditions de paiement ; profil crédit (limite, risque, révision) | `customer_payment_terms` (V005), `customer.credit_limit` / `customer_account.credit_limit` + `payment_terms_id`. Perm `credit.set_limit`. Score/révision : **backlog** |
| 9-10 | Éligibilité crédit, crédit disponible = limite − dû − engagements | `ICreditControl.EvaluateAsync` → `CreditDecision(Allowed, RequiresOverride, AvailableCredit, ProjectedExposure, Message)`. Phase 27 : **refuse aussi un client bloqué** (statut `blocked` ou `customer_block` actif de type credit/sales/account, §26) |
| 11-12 | Vente à crédit → créance liée à une source, montant original conservé | `PosSaleRecorded` / `CreditSaleRecorded` → `customer_receivable` (Phase 6). Le montant original n'est jamais réécrit — la radiation met à jour `paid_amount` / `status` (§37) |
| 13 | Statuts créance (à venir, échue, partielle, payée, retard, **litige**, **radiée**) | `V040` : `customer_receivable.status` CHECK += `IN_DISPUTE`, `WRITTEN_OFF` |
| 14-15 | Échéancier & aging (courant / 1-30 / 31-60 / 61-90 / 90+) | **`ICreditPortfolioQueries.AgingSummaryAsync`** (`FILTER (WHERE due_date BETWEEN …)` par tranche) + par client dans `CollectionsAsync`. `GET /credit/aging`. Test `Collections_and_aging_reflect_overdue_receivables` |
| 16-18 | Paiements clients, partiel, **affectation** | `IFinanceOperations.RecordCustomerPaymentAsync` (allocations à N créances, montant non affecté → acompte/suspense) — Phases 4/12 ; `POST /finance/customer-payments`. Réaffectation auditée : backlog |
| 19-20 | Acomptes / avoirs client | Acompte non alloué (Phase 25 `OrderService.RecordDepositAsync`) ; avoir = `sale_return.refund_method = 'credit_note'` → compte `store_credit` (Phase 6). Avoir consommable : **backlog** |
| 21-22 | Relances multi-niveaux, scénarios (avant échéance, 1-7 j, 8-30 j…) | `dunning_policy.levels` (jsonb) + exécuteur `RunDunningAsync` (Phase 14) → notifications, exécutable par le planificateur (Phase 16) |
| 23-24 | **Recouvrement & promesse de paiement** | **`CollectionsService`** : `CreatePromiseAsync` (fige `snapshot_remaining` = solde des créances visées, ≥ 1 créance obligatoire §24), `SettlePromiseAsync` (compare le solde actuel au snapshot → `kept` / `partially_kept` / `broken`). `customer_promise` (`V040`, audité). `POST /credit/promises` + `/{id}/settle`. Tests `Promise_is_kept…`, `Promise_is_broken…` |
| 25 | **Litiges** (montant contesté, motif, statut, résolution, impact solde) | `CollectionsService.OpenDisputeAsync` (→ créance liée passe à `IN_DISPUTE`), `ResolveDisputeAsync` (`resolved_customer` / `resolved_company` / `cancelled` → créance rétablie à `OPEN`). `customer_dispute` (`V040`). `POST /credit/disputes` + `/{id}/resolve`. Test `Dispute_flags_the_receivable…` |
| 26-27 | **Blocage / déblocage client** (crédit / vente / livraison / compte) | `CollectionsService.BlockCustomerAsync` (motif obligatoire, index unique « un blocage actif par type », met `customer.status = 'blocked'` pour credit/sales/account), `ReleaseBlockAsync` (motif obligatoire ; réactive le client si plus aucun blocage). `customer_block` (`V040`, `is_active` généré). `POST /credit/blocks` + `/{id}/release`. Test `Blocked_customer_cannot_buy_on_credit` |
| 28 | Risque client / score | Signaux disponibles (retards, dû, litiges, promesses non tenues) ; score interne : **backlog** (« un score n'est pas une décision automatique irrévocable ») |
| 29-30 | Analyse portefeuille & DSO | `AgingSummaryAsync` : total / courant / tranches / échu + **DSO** ≈ créances / (ventes 90 j / 90) quand les ventes sont suffisantes (méthode explicite §30). `GET /credit/collections` (par client). Écran Crédit & recouvrement |
| 31 | Recouvrement multi-entreprises | Toutes les tables portées par `company_id` + RLS ; le comptable en chef voit plusieurs entreprises selon son scope (Phase 22) sans mélange des soldes |
| 32-33 | Commercial / commissions | Le portefeuille est lisible par `salesperson` (grant `credit.collect` / `view_portfolio`). Commissions : **hors périmètre V1.0** |
| 34-35 | Documents & **relevé client** | **`CustomerStatementAsync`** : ventes à crédit + encaissements + radiations, solde progressif, total en retard (symétrique du relevé fournisseur Phase 26). `GET /credit/statements/{customerId}`. Test `Customer_statement_lists_charges_and_payments` |
| 36 | Rapprochement paiement | `IPaymentReconciliationService` / `IBankReconciliationService` (Phase 12) — inchangé |
| 37 | **Radiation de créance** (motif, approbation, compte, audit, pas de suppression) | **`CollectionsService.WriteOffReceivableAsync`** : montant = solde exact restant, événement **`ReceivableWrittenOff`** → écriture `Dr bad_debt / Cr accounts_receivable` (clé de mapping `bad_debt` ajoutée `V040`), `customer_receivable` → `paid_amount = original`, `status = WRITTEN_OFF` (montant original conservé), ligne `receivable_write_off` avec `journal_entry_id`. `POST /credit/receivables/{id}/write-off` (`[Idempotent]`). Test `Write_off_posts_a_balanced_bad_debt_entry…` |
| 38-39 | Rééchelonnement & plans de paiement | **Backlog** (`customer_receivable` peut être scindé ; hors périmètre de cette phase) |
| 40 | Paiement en devise / écart de change | `PostingRuleSet` : conversion fonctionnelle + `fx_gain` / `fx_loss` sur l'affectation (Phase 4) |
| 41-43 | Intégration POS / finance / comptabilité | `SalesService.CheckoutAsync` → créance ; `RecordCustomerPaymentAsync` → réduit la créance + augmente la trésorerie ; radiation → charge sur créances irrécouvrables. Écritures équilibrées, mapping, période ouverte (Phases 4-6) |
| 44 | API | `GET /credit/{collections,aging,promises,disputes,blocks}`, `GET /credit/statements/{customerId}`, `POST /credit/{promises,disputes,blocks}` (+ `/{id}/{settle,resolve,release}`), `POST /credit/receivables/{id}/write-off`. **Écart de nommage** vs §44 : endpoints regroupés sous `/credit/` |
| 45 | Événements métier | `ReceivableWrittenOff` (nouveau, → `PostingRuleSet`). `customer_promise` / `customer_dispute` / `customer_block` audités par `trg_audit_*` (§48) |
| 46-47 | Permissions & séparation des responsabilités | `V040` : `credit.{set_limit,collect,dispute,block,write_off,view_portfolio}`. Un caissier encaisse (`finance.pay`) sans pouvoir modifier la limite ni bloquer ; `credit.write_off` réservé aux rôles comptables / direction |
| 48 | Audit | `trg_audit_{customer_promise,customer_dispute,customer_block,receivable_write_off}` (`V040`) |
| 49-50 | Anti-fraude & doublons clients | Moteur de fraude à signaux (Phase 11) ; unicité `customer_company_number_uk` + `email`/`phone`. Détection de doublons avant création + fusion contrôlée : **backlog** (§50-51) |
| 52 | Notifications | `notification` + exécuteurs de relances / alertes (Phase 14) |
| 53-54 | Tests + scénario complet §54 | `CustomerCollectionsTests` (7). Le scénario §54 (limite 500 000 → vente 300 000 crédit → créance → paiements → relance → promesse → solde 0) est couvert par la combinaison `Blocked_customer…` + `Promise_is_kept…` + `Write_off…` |

## `V040` — contenu

- `customer_promise` (`snapshot_remaining`, `receivable_ids uuid[]`, statut `open/kept/partially_kept/broken/cancelled`)
- `customer_dispute` (`disputed_amount`, `reason`, statut, `resolution`)
- `customer_block` (`block_type`, `reason`, `is_active` généré, index unique « un actif par type »)
- `receivable_write_off` (`amount`, `reason`, `journal_entry_id`, `approved_by`)
- `customer_receivable.status` CHECK += `IN_DISPUTE`, `WRITTEN_OFF`
- `account_mapping.mapping_key` CHECK += `bad_debt` ; démo : `bad_debt → 6800`
- RLS `company_isolation` sur les 4 tables ; `trg_audit_*` sur les 4 tables
- 6 permissions `credit.*`
- **168 tables (+4), 156 en RLS (+4)**

## Événement `ReceivableWrittenOff`

```
Dr  bad_debt (charge sur créances irrécouvrables)     montant du solde restant
Cr  accounts_receivable (tiers = client)              idem
```

`FinancialEngine` ne touche pas le sous-ledger pour cet événement — `CollectionsService`
met à jour `customer_receivable` dans la même transaction (statut `WRITTEN_OFF`, montant
original conservé §37).

## Frontend

`src/credit/` — `creditApi.ts` + **`RecoveryPage.tsx`** (KPIs total / échu / 90+ / DSO / litiges ;
onglets Portefeuille (aging par client) / Promesses / Litiges / Blocages ; actions bloquer /
débloquer / solder une promesse / trancher un litige ; carte aging global). Route `/credit`,
nav « Crédit & recouvrement » (`credit.view_portfolio`). Vitest `creditApi.test.ts` (2) →
23 tests frontend.

## Manques assumés (backlog)

- Score de risque client explicable (§28)
- Rééchelonnement & plans de paiement (§38-39)
- Avoir client consommable comme moyen de règlement (§20)
- Détection de doublons clients + fusion contrôlée (§50-51)
- Réaffectation de paiement auditée (§18)
- Commissions commerciales (§32-33)
