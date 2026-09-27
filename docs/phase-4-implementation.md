# NEXORA — Phase 4 — État de l'implémentation

Référence : [`phase-4-moteur-financier-comptable.md`](phase-4-moteur-financier-comptable.md).

## Ce qui est livré

### Base de données

| Migration | Contenu |
|---|---|
| `V022` | `account_mapping` (rôle comptable → compte, par entreprise) ; `journal_entry.source_operation_uuid` (idempotence §33) ; `payment_allocation.fx_difference` ; permissions financières (§31) + rattachement aux rôles |
| `V023` | Équilibre des écritures **multi-devises** : la règle §4 porte sur `base_debit`/`base_credit` ; l'équilibre en devise de transaction n'est exigé qu'en mono-devise |

`database/seeds/demo_abc_group.sql` étendu : plan comptable complet Pharmacie ABC (TVA, COGS,
écart de caisse, gains/pertes de change…), `account_mapping` (18 clés), `ledger_account_id` des
comptes financiers, taux USD→HTG (132 au 01/2026, 135 au 06/2026), 1 client + 1 fournisseur.

### Backend — module `Finance` / `Accounting`

```
Nexora.Domain/
  Common/Money.cs              décimal + devise, opérations exactes (§32)
  Common/Currency.cs
  Accounting/Account.cs         AccountType, AccountMapping, AccountKeys
  Accounting/JournalEntry.cs    Journal, AccountingPeriod, JournalEntry(Line)
  Accounting/JournalEntryDraft.cs   brouillon équilibré (débit=crédit garanti avant validation)
  Finance/Payment.cs            Payment, PaymentAllocation, CustomerReceivable, SupplierPayable
  Finance/FinancialAccount.cs   FinancialAccount, FinancialTransaction, ExchangeRate

Nexora.Application/Finance/
  Events/FinancialEvents.cs     CashSale, CreditSale, SupplierInvoice, CashPurchase,
                                CustomerPayment, SupplierPayment, Expense, CashCount,
                                FinancialTransfer, BankCharge
  Posting/PostingRuleSet.cs     règle de génération par événement (§8-19)
  Posting/PostingContext.cs     résolution comptes + conversion devise historique
  Engine/FinancialEngine.cs     événement → règle → transaction atomique + idempotence (§3, §33)
  Payments/… Periods/… Reversals/…   clôture/réouverture (§27), extourne (§28)
  Reports/IFinancialReports.cs  balance, grand livre, résultat, bilan, âgées, prévision trésorerie
  FinanceOperations.cs          façade appelée par l'API (§16 : point d'entrée unique)

Nexora.Infrastructure/Finance/  mappings EF + stores + services (SQL des rapports)
Nexora.Api/Controllers/         FinanceController, AccountingController + permissions
```

### Flux atomique (§3, §15, §33)

`FinancialEngine.HandleAsync(evt)` :
1. **Idempotence** — si `journal_entry` porte déjà `source_operation_uuid` → renvoie l'écriture existante (`WasReplay`).
2. **Période** — résout la période de la date ; refuse si clôturée (`accounting.period_closed`).
3. **Règle** — construit un `JournalEntryDraft` équilibré (conversion au taux historique de la date).
4. **Transaction unique** (`IUnitOfWork.ExecuteInTransactionAsync`) :
   écriture `posted` + créance/dette + `payment` + `payment_allocation` + `financial_transaction`
   + solde du compte financier. Rollback complet en cas d'échec.
5. Les triggers PostgreSQL (Phase 2 + V023) restent le filet : débit=crédit, comptes postables,
   période clôturée, affectation ≤ solde, soldes ≥ 0, pas de suppression physique.

### Multi-devises (§22)

Chaque ligne conserve montant transaction (`debit`/`credit` + `currency_id`) **et** montant
converti en devise fonctionnelle (`base_debit`/`base_credit` + `exchange_rate_id`). Une écriture
de règlement solde la créance/dette **au taux d'émission** et impute la différence à
`fx_gain` / `fx_loss` (au taux du paiement). Aucun recalcul rétroactif.

## Vérifications exécutées

**Tests d'intégration `Nexora.IntegrationTests` — 10/10** (base PostgreSQL réelle, clone de la
base migrée), scénarios §35 :

| # | Scénario | Vérifié |
|---|---|---|
| 1 | Vente comptant | écriture équilibrée (base 1100 = 1100), caisse +1100 |
| 2 | Vente à crédit + règlement partiel + final | créance 5000 → 3000 → 0 ; OPEN → PARTIALLY_PAID → PAID |
| 3 | Achat à crédit + règlements | dette 4000 → 0 ; PARTIALLY_PAID → PAID |
| 4 | Dépense | caisse −800, écriture équilibrée |
| 5 | Écart de caisse (manquant 100) | variance imputée, écriture équilibrée |
| 6 | Vente en USD (devise fonctionnelle HTG) | conversion 100 × 132 = 13 200 HTG |
| 7 | Différence de change au règlement | créance soldée au taux d'émission, gain de change 300 HTG |
| 8 | Extourne d'une écriture validée | écriture miroir (débit↔crédit), source `reversed`, lien conservé |
| 9 | Idempotence (rejeu du même `operation_uuid`) | aucune double écriture |
| 10 | Clôture puis tentative de saisie | rejet `accounting.period_closed` |

**Tests unitaires** (`Money`, `JournalEntryDraft`, `PostingRuleSet`) — 16/16.
**API** — `POST /api/v1/finance/cash-sales` → écriture `2026-000001` ;
`GET /api/v1/accounting/reports/trial-balance` → balance équilibrée (D 1100 = C 1100).

Total backend : **28 tests verts**, build Release 0 warning.

## API (extraits)

```
POST /api/v1/finance/cash-sales                                   [sales.create] [Idempotent]
POST /api/v1/finance/credit-sales                                 [sales.credit]
POST /api/v1/finance/supplier-invoices                            [purchasing.create]
POST /api/v1/finance/expenses                                     [finance.pay]
POST /api/v1/finance/customer-payments                            [finance.pay]
POST /api/v1/finance/supplier-payments                            [finance.pay]
POST /api/v1/finance/transfers | bank-charges | cash-counts
GET  /api/v1/accounting/periods
POST /api/v1/accounting/periods/{id}/close                        [accounting.close_period]
POST /api/v1/accounting/periods/{id}/reopen                       [accounting.reopen_period]
POST /api/v1/accounting/journal-entries/{id}/reverse              [accounting.reverse]
GET  /api/v1/accounting/reports/{trial-balance|general-ledger|income-statement|balance-sheet}
GET  /api/v1/accounting/reports/{aged-receivables|aged-payables|cash-flow-forecast}
```

## Couverture des critères de sortie (§36)

| Critère | État |
|---|---|
| Toutes les opérations financières critiques ont une règle | ✅ `PostingRuleSet` + engine (10 types d'événements) |
| Créances/dettes avec paiements partiels | ✅ testé (§35 #2, #3) |
| Écritures équilibrées | ✅ `JournalEntryDraft.EnsureBalanced` + trigger `assert_entry_balanced` |
| Périodes contrôlées | ✅ `AccountingPeriodService` + trigger `journal_entry_period_guard` |
| Devises et taux historisés | ✅ `base_*` + `exchange_rate_id` par ligne ; pas de recalcul |
| Rapprochements | ⏳ définis (§29) ; rapport rapprochement bancaire en Phase ultérieure |
| Intercompany / consolidation | ⏳ tables prêtes (Phase 2) ; moteur d'élimination à venir |
| Permissions financières | ✅ V022 + `[RequirePermission]` sur chaque endpoint |
| Scénarios critiques testables | ✅ 10 scénarios §35 automatisés |
| Utilisable par POS / Achats sans logique dupliquée | ✅ `IFinanceOperations` = point d'entrée unique |

## Suite — Phase 5

Produits, stocks et achats : le module consommera `IFinanceOperations` pour transformer
commandes / réceptions / factures / achats comptants et à crédit en mouvements de stock,
dettes, paiements et écritures — sans réimplémenter de règle financière.
