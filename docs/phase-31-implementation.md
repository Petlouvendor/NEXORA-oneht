# Phase 31 — Reporting & Analytics — implémentation

**Statut : livré et vérifié.** Migration `V044`. 6 tests d'intégration (`ReportingTrustTests`),
2 tests frontend (`analyticsApi.test.ts`). **202 tests backend** verts (16 unit + 7 API +
179 intégration), Debug **et** Release, 0 warning. 31 tests frontend.

Le socle reporting existe déjà (Phase 7) et est **validé** par cette phase :

- `SqlKpiService` — Command Center (§4), dashboards caissier / manager / groupe (§5-7), catalogue
  documenté des KPI avec source & formule (§34), tendance & comparaison de période (§36)
- `SqlSalesReports` — journal des ventes, ventilation par dimension (§11-14, §18-19), top produits (§15)
- `SqlStockReports` — valorisation, ruptures, produits dormants (§10, §25-26)
- `SqlAgingReports` — aging configurable créances / dettes (§20-21)
- `SqlBudgetReports` — budget vs réalisé + variance (§23)
- `SqlDrillDown` — CA → jour → vente → ligne ; créance → client → factures (§35)
- `SqlAlertCenter` — alertes évaluées à la volée sur données validées (§38)
- `CsvExportService` + `export_log` — export CSV filtré, journalisé (§42, §52)
- `RuleBasedAskNexora` — questions en langage naturel, cite ses sources (§34)
- Consolidation / intercompany / comparaison entreprises — Phase 9 (§5, §36) ; états financiers
  §24 — Phase 4/29 ; trésorerie §22 — Phase 28 ; recouvrement §20 — Phase 27

La Phase 31 ajoute la **couche de confiance** décrite aux §2, §44-46 : prouver que les chiffres
sont réconciliables, contrôler la qualité des données, figer des photos reproductibles.

---

## Correspondance cahier des charges → implémentation

| § | Exigence | Où |
|---|---|---|
| 2 | Indicateur à définition claire ; périmètre d'accès ; **chiffres réconciliables** ; périodes explicites ; rapports historiques reproductibles | `KpiCatalog` (source + formule par KPI, Phase 7) + **`IKpiReconciliation`** (§46) + **`IAnalyticsSnapshotService`** (§44). Périmètre : RLS serveur + filtre `company_id` sur chaque requête |
| 3 | Périmètres (groupe / entreprise / établissement / entrepôt / caisse / produit / commercial / client / …) | `ReportFilter` (Phase 7) : dimensions & filtres ; RLS borne l'entreprise / le groupe |
| 4-10 | Dashboards direction / groupe / manager / caissier / comptable / finance / stock | `IKpiService.CommandCenterAsync` / `ManagerDashboardAsync` / `CashierDashboardAsync` / `GroupDashboardAsync` (Phase 7) ; dashboard comptable = `AccountingController` + `IFinancialCloseService`/`IFiscalYearService` (Phases 28-29) ; dashboard finance = `ITreasuryQueries` (Phase 28) ; dashboard stock = `IStockReports` + `IStockQueries` (Phases 7/24) |
| 11-19 | Ventes / CA / panier / marge / analyses produits-clients-fournisseurs-caisses-employés | `SqlSalesReports` (journal, `BreakdownAsync(dimension)`), `SqlKpiService` (panier = CA net / nb transactions §13, marge = ventes nettes − COGS §14). `KpiCatalog` affiche chaque définition (§12, §26) |
| 20-24 | Crédit client / dettes fournisseurs / trésorerie / budget / comptabilité | `SqlAgingReports` + `ICreditPortfolioQueries` (DSO, aging, promesses — Phase 27) ; `IPurchasingQueries` (aging dettes — Phase 26) ; `ITreasuryQueries` (position, liquidité, prévision — Phase 28) ; `SqlBudgetReports` ; `IFinancialReports` (balance, GL, bilan, résultat — Phase 4/29) |
| 25-32 | KPI stock, opérationnels, sectoriels (restaurant / pharmacie / supermarché / grossiste / services) | `SqlStockReports` (rotation, jours de stock, casse, expiration) ; les KPI sectoriels réutilisent `BreakdownAsync` par catégorie / rayon / table + les hooks sectoriels (Phase 8). Food cost, temps de préparation, taux d'occupation : **backlog** |
| 33 | Rapports planifiés (quotidien → annuel, destinataires, format, filtres) | Permission `reporting.schedule` + `SchedulerRunner` (Phase 16) ; table `report_schedule` dédiée : **backlog** |
| 34 | Rapports personnalisés (métriques, dimensions, filtres, groupements, colonnes, tri, sauvegarde, partage) | `KpiCatalog` + `ReportFilter` + `IExportService` couvrent le socle ; constructeur de rapport sauvegardable (`saved_report`) : **backlog** |
| 35 | **Drill-down** KPI → entreprise → établissement → caisse → jour → transaction → ligne | `SqlDrillDown.ExpandAsync(path)` (Phase 7 : `revenue/{jour}/{vente}` + `receivable/{client}`). Chaque niveau respecte la RLS. Niveaux établissement / caisse intermédiaires : **backlog** |
| 36-37 | Comparaisons (jour / semaine / mois / année, budget vs réel, A vs B, moyenne) & tendances | `CommandCenterResponse` porte tendance + période précédente (Phase 7) ; `SqlBudgetReports` (budget vs réel) ; A vs B via `GroupDashboardAsync` |
| 38-39 | Alertes & alertes intelligentes (seuil, variation %, historique, priorité, acquittement) | `SqlAlertCenter.EvaluateAsync` (Phase 7) : rupture, surstock, expiration, CA en baisse 7 j, trésorerie négative, écarts de caisse, créances échues. **`GET /api/v1/alerts`** (alias canonique). Acquittement persistant : **backlog** (`alert_rule` existe) |
| 40-41 | Objectifs / scorecards (objectif → réel → écart → statut) | Permission `reporting.manage_targets` ; table `kpi_target` + scorecards : **backlog** |
| 42 | Exports (PDF / CSV / Excel, horodatage, utilisateur) | `CsvExportService` + `export_log` (Phase 7) ; PDF / Excel : **backlog** (le rendu HTML documentaire de la Phase 30 est réutilisable) |
| 43 | Partage (lien sécurisé, expiration, révocation) | **Backlog** — modèle proche du partage de documents (Phase 30) |
| 44 | **Snapshots** (photo KPI à une date, périmètre, version de calcul, reproductibilité) | **`analytics_snapshot`** (`V044`) + **`IAnalyticsSnapshotService`** : `CreateAsync(kind, label, from, to)` exécute la réconciliation ou le contrôle qualité, **fige le résultat en jsonb** (sérialisation camelCase, `calculation_version = 'v1'`) ; `GetAsync` / `ListAsync` rejouent à l'identique. **`GET/POST /api/v1/analytics/snapshots`**, `GET /analytics/snapshots/{id}`. Tests `Reconciliation_snapshot_is_reproducible`, `Data_quality_snapshot_carries_a_score` |
| 45 | **Data quality** (données manquantes, doublons, non rapproché, non validé, produits sans coût, clients sans conditions, stocks négatifs, erreurs de mapping, score) | **`IDataQualityService.EvaluateAsync`** → `DataQualityReportDto` : 10 contrôles catégorisés (`unposted` / `unreconciled` / `missing` / `integrity` / `mapping`) avec sévérité (`info` / `warn` / `error`), compte, `sampleIds` (jusqu'à 5 pour le drill), **score 0-100 + note A-E** (pénalité par contrôle en échec selon sévérité). **`GET /api/v1/analytics/data-quality`**. Tests `Data_quality_flags_a_draft_entry_and_produces_a_score`, `Data_quality_detects_a_missing_account_mapping`, `Negative_stock_is_flagged` |
| 46 | **Réconciliation KPI** (CA reporting = ventes, encaissement = paiements confirmés, stock = ledger, résultat = compta, créances = comptes clients, dettes = comptes fournisseurs) | **`IKpiReconciliation.ReconcileAsync(from, to)`** → `ReconciliationReportDto` : 5 lignes `{ operationalValue, accountingValue, difference, reconciled, definition }` — **CA net HT** (table `sale` − retours vs comptes `sales_revenue`/`sales_returns`), **encaissements** (`payment` inbound confirmés vs `financial_transaction` liés), **créances** (sous-livre vs compte collectif 4111), **dettes** (sous-livre vs 4011), **valeur du stock** (ledger vs compte de stock). Tolérance 0,01. **`GET /api/v1/analytics/reconciliation`**. Test `Reconciliation_matches_a_credit_sale_on_a_private_period` |
| 47 | Performance (pagination, agrégations optimisées, index, asynchrone) | Requêtes agrégées en SQL brut sur index existants (`sale (company_id, sold_at)`, `journal_entry (company_id, entry_date)`, `stock_balance`) ; `SchedulerRunner` pour les traitements lourds (Phase 16) |
| 48 | Temps réel (ventes POS récentes, caisse, stock critique, alertes, position trésorerie) | `IKpiService` + `IAlertCenter` évaluent à la volée ; fréquence de rafraîchissement pilotée côté écran |
| 49 | Sécurité (scope par requête, aucune donnée d'une autre entreprise, exports protégés, audit) | RLS serveur (`nexora_app` sans BYPASSRLS) + filtre `company_id` explicite ; `export_log` (§52) ; `analytics_snapshot` RLS + `trg_audit_analytics_snapshot` |
| 50 | API principales | **`GET /api/v1/analytics/{reconciliation,data-quality}`**, **`GET/POST /api/v1/analytics/snapshots`** (+ `/{id}`), **`GET /api/v1/kpis`** & **`GET /api/v1/alerts`** (alias canoniques). Servis par Phase 7 : `/dashboard/*`, `/reports/*` (journal, ventilation, aging, budget-vs-actual, export), `/dashboard/drill`. `/reports` CRUD + `/schedule`, `/analytics/{sales,finance,stock,accounting,customers,suppliers}` détaillés : **écart de nommage assumé** (données servies par `/reports/*` + `/dashboard/*` + les endpoints métier des Phases 27-29) |
| 51 | Permissions | `V044` : `reporting.data_quality` (réconciliation + qualité + snapshots) ; le reste via `reporting.{view, view_financial, view_group, export, export_financial, schedule, manage_targets, ask}` (Phase 7). Un caissier n'accède pas à `/analytics/*` (403 vérifié) |
| 52 | Audit | `export_log` (Phase 7) ; `trg_audit_analytics_snapshot` (`V044`) |
| 53 | Tests | `ReportingTrustTests` (6) : réconciliation CA sur période privée, contrôle qualité (brouillon + score + grade), mapping manquant, stock négatif, snapshot réconciliation reproductible, snapshot qualité avec score |

## `V044` — contenu

- `analytics_snapshot` — `kind` (`reconciliation` / `data_quality` / `kpi`), `label`, `as_of_date`,
  `period_from` / `period_to`, `scope` jsonb, `calculation_version`, `score`, `result` jsonb,
  `created_by`. RLS `company_isolation` + `trg_audit_analytics_snapshot`.
- permission `reporting.data_quality` (super_administrator / group_administrator / owner_direction /
  manager / chief_accountant / senior_accountant)
- **175 tables (+1), 163 en RLS (+1)**, 44 migrations (V001–V044)

## Frontend

`src/reports/` — `analyticsApi.ts` + **`AnalyticsPage.tsx`** (remplace le `ModulePlaceholder` de
`/reports`) : KPI (réconciliation N/M, score qualité + note, contrôles en échec, snapshots) ;
onglets **Réconciliation KPI** (opérationnel vs comptable + écart + définitions §2 affichées),
**Qualité des données** (10 contrôles catégorisés + score/note), **Snapshots** (liste + « figer »).
Route `/reports`, nav « Rapports » (`reporting.view`). Vitest `analyticsApi.test.ts` (2) → 31 tests frontend.

## Manques assumés (backlog)

- Rapports planifiés (`report_schedule` : cron, destinataires, format) (§33)
- Constructeur de rapports personnalisés sauvegardables (`saved_report`) (§34)
- Objectifs / scorecards (`kpi_target` : objectif → réel → écart → statut) (§40-41)
- Acquittement persistant des alertes (§39) ; alertes par anomalie statistique (§39)
- Exports PDF / Excel (§42) ; partage de rapport par lien sécurisé révocable (§43)
- Drill-down avec niveaux établissement / caisse intermédiaires (§35)
- KPI sectoriels avancés — food cost, temps de préparation, taux d'occupation (§27-32)
