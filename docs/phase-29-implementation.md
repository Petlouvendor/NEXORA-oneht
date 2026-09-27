# Phase 29 — Comptabilité générale — implémentation

**Statut : livré et vérifié.** Migration `V042`. 9 tests d'intégration
(`GeneralAccountingTests`), 2 tests frontend (`accountingApi.test.ts`). **189 tests backend**
verts (16 unit + 7 API + 166 intégration), Debug **et** Release, 0 warning. 27 tests frontend.

Le moteur comptable existe déjà (Phase 4) et est **validé** par cette phase :

- Plan comptable `chart_of_accounts` / `account` (code, libellé, type, parent, `is_postable`, statut) — V013
- `journal` (10 types dont `opening` / `closing`), `accounting_period`, `journal_entry` / `journal_entry_line` — V013
- Le **moteur événementiel centralisé** `PostingRuleSet` / `FinancialEngine` : événement métier → règle → écriture équilibrée postée en une transaction, idempotence par `source_operation_uuid` (§20-24, §37)
- **Gardes SQL** (V019 / V023) : compte postable uniquement, `entry_date` dans la période, période fermée verrouillée (`allow_closed_period`), écriture validée immuable (correction par extourne), `assert_entry_balanced` différé (`base_debit = base_credit`, + devise transaction si mono-devise)
- `ReversalService` (§13-14), `AccountingPeriodService` close / reopen (§15, §27), `IFinancialReports` (balance §39, grand livre §38, résultat §40, bilan §41, âgées, prévision) — Phase 4
- Consolidation, intercompany, éliminations, périmètre, devise groupe — Phase 9 (§33-35)
- Analytique : `journal_entry_line.cost_center_id` + `cost_center` (§30-31) ; budget vs réalisé `SqlBudgetReports` (Phase 7, §40)
- Multi-devises : montant transaction + `base_*` + `exchange_rate_id`, `fx_gain` / `fx_loss` à l'affectation (§36-37)
- Audit : `audit_log` immuable + triggers sur `journal_entry` (§48)

La Phase 29 ajoute le **workflow comptable manuel** — ce que le comptable saisit à la main,
que le moteur événementiel ne couvre pas — plus la **clôture d'exercice** et les **à-nouveaux**.

---

## Correspondance cahier des charges → implémentation

| § | Exigence | Où |
|---|---|---|
| 2 | Écriture équilibrée ; pas de suppression silencieuse ; correction tracée ; source identifiable | Gardes SQL V019/V023 (inchangées) ; `journal_entry.origin` (`V042` : system / manual / opening / closing / reversal / adjustment) rend l'origine explicite ; `SourceType` / `SourceId` conservés |
| 3 | Architecture entreprise → exercice → périodes / journaux / plan → écritures → lignes | `company` → `accounting_period` → `journal` / `account` → `journal_entry` → `journal_entry_line` (V013) |
| 4-6 | Plan comptable (code, libellé, type, classe, parent, auxiliaire, statut, système/perso, date d'effet) + hiérarchie configurable | `account` (V013) + **`GET /api/v1/chart-of-accounts`**, **`GET /api/v1/accounts`** (`IChartQueries`). Personnalisation : `accounting.manage_chart` (Phase 4). Classe / date d'effet fine : le référentiel démo suit un plan SYSCOHADA-like, non imposé (§5) |
| 7 | Comptes auxiliaires (clients, fournisseurs, banques, caisses, tiers) | `journal_entry_line.third_party_type` / `third_party_id` (customer / supplier / employee / other) ; soldes par tiers dans les âgées (Phase 4/7) ; contrôle §47 « tiers cohérent » |
| 8 | Journaux (VT / AC / BQ / CA / OD / stock / à-nouveaux / clôture / intercompany) | `journal` (V013). **`AN` (à-nouveaux) & `CL` (clôture)** ajoutés au jeu de démo. **`GET /api/v1/journals`** |
| 9-10 | Écriture (date, journal, numéro, devise, taux, libellé, source, lignes compte/débit/crédit/analytique) ; équilibre obligatoire à la validation | **`IManualJournalService`** + `CreateJournalEntryRequest` (journal, date, devise, libellé, lignes). Équilibre = contrôle §47 `balance` (débit = crédit, précision 4 déc.) refusé à la validation ; sinon la garde SQL différée `assert_entry_balanced` bloque au commit |
| 11 | Brouillon (création / modification / validation / suppression / auteur) | `CreateDraftAsync` (numéro provisoire `BROUILLON-…`, pas d'exigence d'équilibre), `UpdateDraftAsync` (remplace les lignes), `DeleteDraftAsync` (jamais une écriture validée). `created_by`. **`POST` / `PUT` / `DELETE /api/v1/accounting-entries[/{id}]`**. Tests `Balanced_manual_entry…`, `Draft_can_be_edited_and_deleted` |
| 12 | Écriture validée (numéro définitif, date compta, journal, période, auteur, validateur, source, audit) | `PostAsync` : `next_document_number("journal:CODE")`, `status = posted`, `posted_at` / `posted_by`, période résolue. Immuable ensuite (garde SQL). Test `…_then_posted` |
| 13-14 | Contrepassation / correction (source, motif, date, écriture inverse, référence croisée, audit) | `ReverseAsync` délègue à **`ReversalService`** (Phase 4) : écriture miroir dans `OD`, `reversal_of_entry_id`, `origin = reversal`, source → `reversed`. **`POST /api/v1/accounting-entries/{id}/reverse`**. Test `Posted_entry_can_be_reversed` |
| 15 | Périodes (ouverte / clôture / clôturée / verrouillée / réouverture) | `accounting_period.status` (open / closed / locked) + garde SQL `journal_entry_period_guard`. `AccountingPeriodService` close / reopen (motif exigé). **`POST /api/v1/accounting-periods/{id}/{close,reopen}`**. Test `Entry_in_a_closed_period_is_rejected` |
| 16 | Exercices (dates, statut, exercice courant, à-nouveaux, clôture) | `accounting_period` porte l'exercice (démo : « Exercice 2026 » = année civile). **`GET /api/v1/accounting-periods`**. Périodes mensuelles infra-annuelles : **backlog** |
| 17 | Clôture mensuelle (contrôles, rapprochements, écritures en attente, balance, validation) | La **clôture financière** (Phase 28 `IFinancialCloseService`, `POST /periods/{id}/financial-close`) porte les contrôles opérationnels ; la clôture d'exercice (§18) vérifie en plus qu'elle a été faite (`finance_close` warn) |
| 18 | **Clôture annuelle** (résultat, comptes temporaires, report, à-nouveaux, verrouillage, audit) | **`IFiscalYearService.CloseYearAsync`** : solde chaque compte de résultat vers `current_year_earnings` (nouvelle clé de mapping, démo `1300`) via une écriture `origin = closing` dans le journal `CL`, puis clôture la période. Contrôles bloquants (`period_open`, `draft_entries`) + forçage motivé. `accounting_year_close` (`V042`, audité) fige le résultat + l'instantané des contrôles. **`GET` / `POST /api/v1/accounting-periods/{id}/close-year`**. Test `Year_end_close_moves_the_result_to_equity` |
| 19 | **À-nouveaux** (génération contrôlée, soldes bilan, références exercice précédent, audit) | **`IFiscalYearService.GenerateOpeningBalancesAsync`** : lit les soldes des comptes d'actif / passif / capitaux propres à la clôture de l'exercice source → une écriture équilibrée `origin = opening` dans le journal `AN`, datée au 1er jour de la période cible ; résidu → `retained_earnings`. Garde anti-doublon (`accounting.opening_exists`). **`POST /api/v1/accounting-periods/{id}/opening-balances`**. Test `Opening_balances_are_generated_and_balanced` |
| 20-24 | Comptabilisation ventes / achats / créances / dettes / stock (mapping par catégorie, taxe, paiement, stock) | `PostingRuleSet` (Phase 4-6) : `CashSale` / `PosSale` / `CreditSale` / `SupplierInvoice` / `CashPurchase` / `CustomerPayment` / `SupplierPayment` / `InventoryWriteOff` / `InventoryRevaluation` / `SupplierReturn` / `SaleRefund` / `ReceivableWrittenOff` — via `nexora.account_mapping`. **Inchangé, validé** |
| 25 | Taxes (collectée / récupérable / non récupérable / exonération / période / compte) | `sales_tax_output` / `purchase_tax_input` (mapping), `product_price.tax_included` (Phase 23), journaux de taxes via grand livre. Déclaration paramétrée par pays : **backlog** (§44) |
| 26-29 | Immobilisations / amortissements / CCA-PCA / provisions | **Hors périmètre V1.0** (backlog) — modélisables par écritures manuelles (`OD`) + `origin = adjustment` en attendant les modules dédiés |
| 30-32 | Comptabilité analytique (centre / projet / activité / site / axe, répartition 100 %) | `cost_center_id` sur la ligne (V013) accepté par `IManualJournalService` ; axes multiples & règles de répartition : **backlog** |
| 33-35 | Multi-entreprises / intercompany / consolidation | Phase 9 (`IGroupLedgerService`, `IIntercompanyService`, `IConsolidationService`) — **validé, inchangé**. Isolation stricte : RLS `FORCE` + `company_id` sur chaque table (§55) |
| 36-37 | Multi-devises / écart de change | `PostingRuleSet` + `AddArApReliefWithFx` (Phase 4). `IManualJournalService` convertit chaque ligne au taux de la date (`base_debit` / `base_credit`) ; réévaluation de clôture : **backlog** |
| 38-39 | Grand livre / balance (générale, auxiliaire, comparaison, par entreprise, consolidée) | `IFinancialReports.GeneralLedgerAsync` / `TrialBalanceAsync`. **`GET /api/v1/general-ledger`**, **`GET /api/v1/trial-balance`**. Balance auxiliaire = âgées (Phase 7) ; consolidée = Phase 9 |
| 40-41 | Compte de résultat / bilan | `IncomeStatementAsync` / `BalanceSheetAsync`. **`GET /api/v1/financial-statements`** (les deux). **Le compte de résultat exclut désormais les écritures `origin = closing`** → il montre toujours le résultat opérationnel, exercice soldé ou non ; le bilan les inclut (le résultat est porté en capitaux propres) |
| 42 | Tableau des flux de trésorerie | Prévision & position : Phase 28 (`ITreasuryQueries`). Tableau normé (opérationnel / investissement / financement) : **backlog** |
| 43-44 | États personnalisés / reporting fiscal | `IExportService` + `ReportCatalog` (Phase 7). Journaux de taxes exportables via grand livre. Paramétrage fiscal par pays : **backlog** |
| 45-46 | Rapprochements / comptes de suspense | Banque (Phase 12), finance & suspense (Phase 28 `ISuspenseService`), intercompany (Phase 9). `AccountKeys.Suspense` |
| 47 | **Contrôles comptables** (équilibre, période ouverte, compte actif, devise valide, source, taxe, tiers, intercompany, séquence, doublon) | **`IAccountingControls.EvaluateAsync`** → 10 contrôles typés (`min_lines`, `balance`, `line_amounts`, `journal`, `currency`, `period_open`, `accounts_exist`, `accounts_postable`, `accounts_active`, `third_party`, `no_duplicate`). Renvoyés dans `PostJournalEntryResult` ; tout `fail` refuse la validation (`accounting.controls_failed`, 422). Test `Unbalanced_draft_cannot_be_posted` |
| 48 | Audit (création, modif brouillon, validation, contrepassation, réouverture, clôture, mapping, plan, consolidation, élimination, change) | `audit_log` immuable + `trg_audit_journal_entry` (Phase 4/11) + `trg_audit_accounting_year_close` (`V042`). `accounting_year_close.controls` fige l'instantané ; `forced` + `note` tracent une clôture forcée |
| 49-50 | Permissions & séparation des responsabilités | `accounting.view` (lire) / `accounting.create` (brouillon) / `accounting.post` (valider) / `accounting.reverse` (contrepasser) / `accounting.close_period` / `accounting.reopen_period` / **`accounting.carry_forward`** (`V042` : à-nouveaux & clôture d'exercice) / `accounting.manage_chart`. Préparer ≠ valider ≠ clôturer ≠ reporter |
| 51 | API principales | **`GET /api/v1/{chart-of-accounts,accounts,journals}`**, **`GET/POST /api/v1/accounting-entries`** (+ `/{id}`, `/{id}/post`, `/{id}/reverse`, `PUT`, `DELETE`), **`GET /api/v1/{general-ledger,trial-balance,financial-statements}`**, **`GET/POST /api/v1/accounting-periods/{id}/{close,reopen,close-year,opening-balances}`**, **`GET /api/v1/accounting-periods`**. Endpoints déjà servis sous `/api/v1/accounting/*` (Phase 4, **conservés**) ; `analytical-reports` → `/reports/*` (Phase 7) ; `consolidation` → `/consolidation/*` (Phase 9) |
| 52 | Événements métier | `JournalEntryPosted` / `JournalEntryReversed` = `PostingRuleSet` + `ReversalService` ; `AccountingPeriodClosed/Reopened` = `AccountingPeriodService` ; `JournalEntryDrafted` = `IManualJournalService`. `AssetDepreciated` : backlog. `IntercompanyMatched` / `ConsolidationRun` / `ConsolidationAdjusted` = Phase 9. **Aucune écriture n'échappe au grand livre** |
| 53 | Tests fondamentaux | `GeneralAccountingTests` (9) : brouillon équilibré → validation + contrôles §47, déséquilibrée refusée, période fermée refusée, contrepassation, brouillon modifiable/supprimable, compte non postable refusé, à-nouveaux équilibrés, clôture d'exercice → résultat en capitaux propres + bilan équilibré, plan comptable & journaux lisibles. Ventes/achats/paiements/stock/multi-devise : `FinanceEngineFixture` §35 (Phase 4, 28 tests) + Phases 5-6-26-27 |
| 54 | Tests d'intégration | Vente POS / achat / paiement / retour / stock → journal : couverts par `FinanceEngineFixture` + `SalesOrderTests` / `PurchaseThreeWayMatchTests` / `CustomerCollectionsTests` (Phases 6/26/27). Clôture finance → compta : `TreasuryCockpitTests` + le contrôle `finance_close` de `close-year` |
| 55 | Tests d'isolation | RLS `FORCE` + `company_id` sur chaque table ; `SqlManualJournalService` / `SqlFiscalYearService` filtrent explicitement par `company_id`. Comptable en chef multi-entreprises : `PermissionService` par scope (Phase 22). Consolidation n'écrit pas dans les ledgers sources : Phase 9. Intercompany bilatéral : Phase 9 |
| 56 | Scénario de validation | Vente → créance → paiement → solde ; achat crédit → dette ; stock → coût ; banque + ; tous journaux équilibrés : combinaison Phases 4-6-26-27 + le contrôle d'équilibre à chaque validation (§10, §47) |

## `V042` — contenu

- `journal_entry.origin` — `system` / `manual` / `opening` / `closing` / `reversal` / `adjustment`
  (défaut `system`) + index `(company_id, origin)` ; les extournes existantes réalignées à `reversal`
- `accounting_year_close` — clôture d'exercice : `net_result`, `result_entry_id`, `carry_forward_entry_id`,
  `next_period_id`, `controls` jsonb (instantané), `forced` + `note`, `status` (`completed` / `reopened`),
  index unique `(accounting_period_id)`
- `account_mapping.mapping_key` CHECK += `current_year_earnings` ; démo : `1300 Résultat de l'exercice`
- Journaux `AN` (type `opening`) & `CL` (type `closing`) pour Pharmacie ABC & Restaurant ABC
- RLS `company_isolation` + `trg_audit_accounting_year_close`
- Permission `accounting.carry_forward` (super_administrator / group_administrator / owner_direction / chief_accountant)
- **171 tables (+1), 159 en RLS (+1)**, 42 migrations (V001–V042)

## Compte de résultat & écritures de clôture

`SqlFinancialReports.IncomeStatementAsync` filtre désormais `origin <> 'closing'`
(`PeriodBalancesAsync(excludeClosing: true)`). Conséquence : après une clôture d'exercice,
le compte de résultat de la période affiche toujours **80 000 / 30 000 / 50 000** (et non 0),
tandis que le bilan reste équilibré car le résultat est porté en `current_year_earnings` (capitaux propres).

## Frontend

`src/accounting/` — `accountingApi.ts` + **`LedgerPage.tsx`** : KPI (résultat de l'exercice,
produits, charges, balance équilibrée, brouillons) ; onglets **Écritures** (liste + tiroir
« Nouvelle écriture » : journal, date, lignes compte/débit/crédit, équilibre en direct,
« Enregistrer le brouillon » / « Enregistrer & valider » ; valider / supprimer / contrepasser
en ligne), **Balance** (balance générale au jour J), **Clôture d'exercice** (sélecteur d'exercice
→ checklist des contrôles → « Clôturer l'exercice » / « Générer les à-nouveaux »). Route `/accounting`
(remplace le `ModulePlaceholder`), nav « Comptabilité » (`accounting.view`). Vitest
`accountingApi.test.ts` (2) → 27 tests frontend.

## Manques assumés (backlog)

- Périodes comptables mensuelles infra-annuelles + clôture mensuelle dédiée (§16-17)
- Immobilisations & amortissements (§26-27)
- Charges / produits constatés d'avance (§28)
- Provisions (§29)
- Axes analytiques multiples + règles de répartition 100 % (§30-32)
- Réévaluation de change de clôture (§36)
- Tableau des flux de trésorerie normé (§42)
- Reporting fiscal paramétré par pays + états personnalisés enregistrables (§43-44)
- Aliasing exact de toutes les routes §51 (`/api/v1/accounting/*` reste servi en parallèle)
