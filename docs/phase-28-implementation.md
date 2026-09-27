# Phase 28 — Finance & Trésorerie — implémentation

**Statut : livré et vérifié.** Migration `V041`. 8 tests d'intégration
(`TreasuryCockpitTests`), 2 tests frontend (`financeApi.test.ts`). **180 tests backend**
verts (16 unit + 7 API + 157 intégration), Debug **et** Release, 0 warning. 25 tests frontend.

Le socle de trésorerie existait déjà :

- `financial_account` / `financial_transaction` (V011) — caisse / banque / Mobile Money /
  card_processor / wallet / other, solde système, compte GL associé
- `cash_register` / `cash_session` / `cash_movement` (V011) + `CashSessionService` (Phase 6) —
  ouverture, fond, entrées/sorties, comptage, **écart → `CashCountRecorded` → écriture `OD`**
- `bank_statement` / `bank_statement_line` (V011) + **`SqlBankReconciliationService`** (Phase 12) —
  import, dédoublonnage, auto-match (montant + fenêtre ±3 j), match manuel, validation
- `expense` / `budget` / `budget_line` (V011) + `SqlBudgetReports` (Phase 7) — dépenses avec
  approbation, budget vs réalisé
- `exchange_rate` (V010) + `EfExchangeRateProvider` — taux historisés, pas de recalcul rétroactif
- Le **moteur financier centralisé** (Phase 4, `PostingRuleSet` / `FinancialEngine`) — un seul
  point de génération comptable pour ventes, achats, encaissements, décaissements, dépenses,
  transferts, frais bancaires, écarts de caisse, radiations
- `CashFlowForecastAsync` (Phase 4 `IFinancialReports`) — prévision jour par jour
- Transferts inter-entreprises + écritures intercompany (Phase 9)

La Phase 28 **valide** cet ensemble et ajoute la **couche de contrôle & de pilotage** : position
de trésorerie, liquidité & gap, prévision par maturité, comptes de suspense et clôture financière,
exposés aux endpoints canoniques du §50.

---

## Correspondance cahier des charges → implémentation

| § | Exigence | Où |
|---|---|---|
| 2 | La finance suit les fonds, la comptabilité reste la référence ; chaque mouvement a source + compte + statut ; soldes réconciliables | `financial_transaction` (source_operation_uuid, reference_type/id, status), `PostingRuleSet` produit les écritures ; `SqlTreasuryQueries` ne fait que lire |
| 3-4 | Comptes financiers (caisse / banque / Mobile Money / wallet / transit / suspense / dépôt) + fiche | `financial_account` (V011 : cash / bank / mobile_money / card_processor / wallet / other). **`GET /api/v1/financial-accounts`** → nom, type, devise, solde système **+ solde converti** en devise fonctionnelle. Transit/dépôt : modélisés par `financial_transaction.status = 'pending'` (« en transit ») ; suspense : voir §32 |
| 5-9 | Caisses, sessions, fond, mouvements, écarts | `cash_register` / `cash_session` / `cash_movement` (V011), `CashSessionService` (Phase 6) — inchangé. L'écart de session part déjà au moteur (`RecordCashCountAsync`) |
| 10-12 | Banques, relevés, rapprochement (auto / manuel / suspense) | `SqlBankReconciliationService` (Phase 12) — inchangé. La Phase 28 **remonte** les lignes non rapprochées dans le contrôle de clôture (§38) |
| 13 | Mobile Money — couche d'intégration remplaçable | `financial_account.account_type = 'mobile_money'` ; connecteurs `integration_connector` + `payment_intent` (Phase 12) |
| 14-15 | Paiements externes ; frais financiers | `payment_intent` + webhooks HMAC idempotents (Phase 12) ; `BankChargeRecorded` → compte `bank_charges` |
| 16-17 | Dépenses ; petite caisse | `expense` (V011, catégorie, bénéficiaire, taxe, budget, approbation) → `ExpenseRecorded`. Les dépenses approuvées non payées entrent dans **`ShortTermCommitments`** de la liquidité (§28) et la tranche « Aujourd'hui » de la prévision |
| 18-19 | Transferts financiers ; inter-entreprises | `FinancialTransferRecorded` → écriture `OD`, `financial_transaction.counterparty_account_id` ; intercompany (Phase 9) |
| 20-21 | Créances / dettes → impact trésorerie | `customer_receivable` / `supplier_payable` agrégés dans **position** (flux proches), **liquidité** et **prévision** |
| 22-23 | **Prévision par maturité** (aujourd'hui / 7 / 30 / 60 / 90 j / au-delà) | **`ITreasuryQueries.MaturityForecastAsync`** → `CashflowForecastDto` : solde d'ouverture (comptes actifs) + 6 tranches `{ inflow, outflow, net, projectedBalance }`. **`GET /api/v1/cashflow/forecast`**. Test `Maturity_forecast_places_flows_in_the_right_buckets` |
| 24-25 | Budgets ; contrôle budget (engagé → réalisé → variance) | `budget` / `budget_line` (V011) + `SqlBudgetReports.BudgetVsActualAsync` (Phase 7), `GET /reports/budget-vs-actual`. « Engagé » distinct de « réalisé » : **backlog** (`budget_line` ne porte pas d'engagements) |
| 26 | Analyse flux de trésorerie (entrées / sorties / net par dimension) | `MaturityForecastAsync` (net par tranche) ; par compte / catégorie : `GET /reports/*` (Phase 7) |
| 27 | **Position de trésorerie** (disponible, banques, caisses, Mobile Money, transits, suspens, créances/dettes proches) | **`ITreasuryQueries.PositionAsync`** → `TreasuryPositionDto` : `available` + ventilation `cash / bank / mobileMoney / other`, `pending` (en transit), `suspenseExposure` (Σ\|suspens ouverts\|), `nearReceivables` / `nearPayables` (≤ horizon), `netPosition`. **`GET /api/v1/treasury/position`**. Test `Position_aggregates_cash_across_account_types` |
| 28 | **Liquidité & gap** (liquidité immédiate, engagements court terme, créances/dettes attendues, gap, alertes) | **`ITreasuryQueries.LiquidityAsync`** → `LiquidityDto` : `immediateLiquidity` (comptes actifs, base), `expectedReceivables` (≤ horizon), `shortTermCommitments` (dettes ≤ horizon + dépenses approuvées non payées), `projectedGap`, **`alerts[]`** (gap < 0, seuil `treasury.min_cash_alert`, dettes échues, suspens anciens §48, compte à découvert). **`GET /api/v1/treasury/liquidity?horizonDays=`**. Test `Liquidity_flags_a_gap_when_commitments_exceed_available_cash` |
| 29-30 | Multi-devises ; conversion (taux historique / clôture) | `financial_account.currency_id` + conversion en devise fonctionnelle via **dernier `exchange_rate`** (`base=devise compte`, `quote=devise entreprise`). `PostingRuleSet` : montant transaction + `base_*` + `fx_gain`/`fx_loss` (Phase 4). Réévaluation de clôture : **backlog** |
| 31-32 | **Rapprochement financier ; comptes de suspense** (transaction non identifiée, paiement sans facture, frais non affectés, écart bancaire / Mobile Money, responsable, date limite, résolution auditée) | **`treasury_suspense_item`** (`V041`, RLS, `trg_audit_*`) + **`ISuspenseService`** : `OpenAsync` (kind ∈ 7 natures, montant signé, motif obligatoire, responsable & échéance optionnels), `ListAsync(status, onlyOverdue)`, `ResolveAsync` (`resolved` / `written_off` / `cancelled`, note obligatoire). **`GET/POST /api/v1/treasury/suspense`**, `POST /api/v1/treasury/suspense/{id}/resolve`. Tests `Suspense_item_opens_lists_and_resolves`, `Overdue_suspense_items_are_flagged` |
| 33 | Calendrier de trésorerie | Couvert par `MaturityForecastAsync` (tranches datées) + aging `GET /reports/aging/*` |
| 34 | Scénarios (retard clients, paiement anticipé…) | `LiquidityAsync` prend un `horizonDays` variable ; scénarios prudent/central/optimiste paramétrés : **backlog** |
| 35-36 | Financements (prêts) ; actifs financiers simples | **Hors périmètre V1.0** (backlog — `financial_account` type `other` peut porter un dépôt/garantie) |
| 37 | Remboursements (client / fournisseur / paiement externe) | `SaleRefundRecorded` (Phase 6), avoirs `store_credit`, `supplier_return` (Phase 5/26) — inchangé |
| 38 | **Clôture financière** (période, contrôles, comptes rapprochés, écarts, suspens, créances/dettes, validation, rapport, audit) | **`financial_close`** (`V041`, RLS, audit) + **`IFinancialCloseService`** : `PreviewAsync` exécute 7 contrôles horodatés (`cash_sessions_open`, `bank_unreconciled`, `suspense_overdue` — **bloquants** ; `suspense_open`, `unposted_entries`, `receivables_overdue`, `payables_overdue` — informatifs), `CompleteAsync` refuse si un contrôle bloquant échoue (`finance.close_blocked`) sauf `force` + motif (`finance.force_reason`), enregistre l'instantané des contrôles, non rejouable le même jour (`finance.already_closed`). **`GET`/`POST /api/v1/periods/{id}/financial-close`**. **N'écrit aucune écriture** — distincte de la clôture comptable (Phase 4 §27). Tests `Financial_close_control_detects_an_open_cash_session`, `Financial_close_can_be_forced_with_a_reason_and_is_recorded`, `Financial_close_control_detects_unreconciled_bank_lines` |
| 39-43 | Intégrations ventes / achats / crédit / dettes / comptabilité | `SalesService.CheckoutAsync` → tenders → `financial_account` + écriture ; `PayPayableAsync` / `RecordCustomerPaymentAsync` (Phases 4-6-26-27) — inchangé |
| 44 | Dashboard finance | **Écran « Finance & trésorerie »** (`/finance`, `finance.view`) : KPI (disponible, banques, caisses, Mobile Money, gap, suspens), alertes de liquidité, onglets Position / Prévision / Suspens / Clôture. Le Command Center (Phase 7) porte déjà les alertes trésorerie négative & écarts de caisse |
| 45 | Dashboard direction groupe | Trésorerie consolidée : `IGroupControlCenter` (Phase 9), `X-Group-Scope` |
| 46 | Permissions | `V041` : **`finance.close_period`**, **`finance.manage_suspense`** ; lecture position/liquidité/prévision = `finance.view` ; suspens (liste) = `finance.reconcile` |
| 47 | Séparation des responsabilités | Ouvrir un suspens (`finance.manage_suspense`) ≠ le rapprochement bancaire (`finance.reconcile`) ≠ clôturer (`finance.close_period`) ≠ payer (`finance.pay`). Workflow d'approbation des dépenses/paiements : Phase 11/14 |
| 48 | Anti-fraude | Les **suspens de plus de `treasury.suspense_max_age_days` jours** lèvent une alerte de liquidité et un contrôle de clôture ; le moteur de fraude à signaux (Phase 11) couvre paiements fractionnés / nouveau bénéficiaire / hors horaires |
| 49 | Audit | `trg_audit_{treasury_suspense_item,financial_close}` (`V041`) ; `financial_close.controls` fige l'instantané des contrôles ; `financial_close.forced` + `note` tracent une clôture forcée |
| 50 | API principales | **`GET /api/v1/financial-accounts`**, **`GET /api/v1/treasury/position`**, **`GET /api/v1/treasury/liquidity`**, **`GET /api/v1/cashflow/forecast`**, **`GET/POST /api/v1/treasury/suspense`** (+ `/{id}/resolve`), **`GET`/`POST /api/v1/periods/{id}/financial-close`**, **`GET /api/v1/periods`**. Endpoints déjà servis sous d'autres routes (**écart de nommage assumé**) : `/cash-sessions` → `POST /pos/sessions/*` ; `/cash-movements` → `POST /pos/sessions/cash` ; `/bank-imports` + `/bank-reconciliations` + `/bank-transactions` → `/integrations/bank/*` ; `/financial-transactions` + `/financial-transfers` → `POST /finance/transfers` + `financial_transaction` ; `/expenses` → `POST /finance/expenses` ; `/budgets` → `/reports/budget-vs-actual` |
| 51 | Événements métier | Déjà couverts par le moteur (Phase 4) : `CashCountRecorded` (≈ CashOpened/CashClosed), `FinancialTransferRecorded`, `ExpenseRecorded`, `CustomerPaymentReceived`, `SupplierPaymentMade`. `BankTransactionReconciled` = `SqlBankReconciliationService`. `FinancialPeriodClosed` = `financial_close` (§38). Aucun nouvel événement comptable — la Phase 28 est une couche de lecture/contrôle |
| 52 | Offline | Caisse offline + outbox idempotent par UUID d'opération (Phase 10 / Phase 19) — inchangé |
| 53 | Performance | Index `treasury_suspense_item (company_id, status, resolution_due_date)` ; `financial_close (company_id, as_of_date)` ; agrégats de position calculés en une passe (comptes chargés une fois, ventilés en mémoire) ; `financial_transaction (financial_account_id, occurred_at)` (V011). Imports & rapprochements massifs asynchrones : `SchedulerRunner` (Phase 16) |
| 54 | Tests | `TreasuryCockpitTests` (8) : position multi-comptes/multi-devises, gap de liquidité, tranches de maturité, cycle de vie d'un suspens + suspens en retard, clôture bloquée par session ouverte / relevé non rapproché, clôture forcée avec motif + non rejouable |
| 55-56 | Scénario complet & critères de sortie | Le scénario §55 (ventes + achats + dépenses + paiements + transferts → position → rapprochement → comptabilité) est couvert par la combinaison Phases 4-6-26-27 + la position/prévision/clôture de la Phase 28 |

## `V041` — contenu

- `treasury_suspense_item` — `kind` CHECK (`unidentified` / `payment_without_invoice` / `unallocated_fee` /
  `bank_discrepancy` / `mobile_money_discrepancy` / `cash_variance` / `other`), `amount` signé (≠ 0),
  `reference`, `reason`, `source_type` / `source_id`, `responsible_user_id`, `resolution_due_date`,
  `status` (`open` / `resolved` / `written_off` / `cancelled`), `resolution` / `resolved_at` / `resolved_by`
- `financial_close` — `accounting_period_id` (FK composite), `as_of_date`, `status` (`completed` / `reopened`),
  `controls` jsonb (instantané), `forced` bool, `note`, `closed_at` / `closed_by` ;
  index unique `(accounting_period_id, as_of_date) WHERE status = 'completed'`
- 3 `setting_definition` : `treasury.liquidity_horizon_days` (30), `treasury.suspense_max_age_days` (30),
  `treasury.min_cash_alert` (0)
- RLS `company_isolation` + `trg_audit_*` sur les 2 tables
- Permissions `finance.close_period`, `finance.manage_suspense` (super_administrator / group_administrator /
  owner_direction / chief_accountant ; `manage_suspense` aussi senior_accountant / accountant)
- **170 tables (+2), 158 en RLS (+2)**, 41 migrations (V001–V041)

## Correctif incident (surfacé par les tests)

`CatalogQueries.SearchAsync` calculait `ExactBarcode = (p.barcode = terme OR EXISTS(...))` :
quand `p.barcode IS NULL` et l'`EXISTS` faux, SQL renvoie **`NULL`** (et non `false`), ce qui
faisait échouer le mapping `bool` d'EF (`Column 'ExactBarcode' is null`) dès qu'une recherche
par nom remontait un produit sans code-barres. Corrigé en `COALESCE(p.barcode = terme, false) OR EXISTS(...)`.
Bug latent (ordre des tests indépendant) — même famille que les correctifs `::uuid` de la Phase 23.

## Conversion de devise — limite assumée

La position et la liquidité convertissent **le solde des comptes financiers** en devise
fonctionnelle (dernier taux `exchange_rate`). Les **créances et dettes** sont sommées à leur
valeur nominale (comme les rapports d'aging Phase 4/7) : la quasi-totalité est déjà en devise
fonctionnelle ; une conversion ligne à ligne des sous-livres est du backlog.

## Frontend

`src/finance/` — `financeApi.ts` + **`TreasuryCockpitPage.tsx`** : KPI (trésorerie disponible,
banques, caisses, Mobile Money, gap à l'horizon, exposition suspens) ; encart « Alertes de
liquidité » ; onglets **Position** (comptes + solde système + solde converti), **Prévision**
(6 tranches de maturité, solde projeté), **Suspens** (liste, ouvrir, résoudre — `finance.manage_suspense`),
**Clôture** (sélecteur de période → checklist des 7 contrôles → clôturer / forcer avec motif —
`finance.close_period`). Route `/finance` (remplace le `ModulePlaceholder`), nav « Finance »
(`finance.view`). Vitest `financeApi.test.ts` (2) → 25 tests frontend.

## Manques assumés (backlog)

- « Engagé » budgétaire distinct du réalisé + blocage optionnel au dépassement (§25)
- Scénarios de trésorerie paramétrés — prudent / central / optimiste (§22, §34)
- Réévaluation de change de clôture (§29-30)
- Gestion des financements / prêts & échéanciers (§35)
- Actifs financiers simples — dépôts / garanties / acomptes suivis (§36)
- Conversion ligne à ligne des créances/dettes multi-devises dans la position
- Aliasing exact de toutes les routes §50 (`/cash-sessions`, `/expenses`, `/budgets`, …)
