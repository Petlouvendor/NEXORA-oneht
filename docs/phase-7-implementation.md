# NEXORA — Phase 7 — État de l'implémentation

Référence : [`phase-7-dashboard-rapports-pilotage.md`](phase-7-dashboard-rapports-pilotage.md).

## Principe directeur

Les rapports **dérivent** des données validées (ventes, stock, écritures, paiements) et
sont **réconciliables** avec la comptabilité (§2, §34). Aucun chiffre métier n'est stocké
en double : `V026` ne crée que des objectifs, des configurations d'affichage, des
planifications et des journaux.

## Ce qui est livré

### Base de données — `V026`

| Objet | Rôle |
|---|---|
| `kpi_target` | objectifs KPI par entreprise/établissement, période, sens `min`/`max`, dates d'effet (§27) |
| `aging_bucket_set` | tranches d'antériorité configurables (`buckets` jsonb), une par défaut par entreprise/type (§18) |
| `saved_report` | rapports favoris / enregistrés (filtre jsonb, partage) |
| `report_schedule` | rapports planifiés — fréquence `daily`/`weekly`/`monthly`/`on_close`, format `csv`/`xlsx`/`pdf`, destinataires (§31) |
| `report_execution` | journal d'exécution des rapports planifiés (FK composite `(id, company_id)`) |
| `export_log` | journal des exports, `sensitivity` `normal`/`financial`/`restricted` (§30, §36) |
| permissions | `reporting.view`, `reporting.export`, `reporting.view_financial`, `reporting.export_financial`, `reporting.view_group`, `reporting.schedule`, `reporting.manage_targets`, `reporting.ask` + rattachement aux rôles |

Seed : un jeu de tranches « Standard » (Non échu / 1-30 / 31-60 / 61-90 / > 90 j) par
entreprise et par type (receivable, payable).

### Backend — `Reporting`

```
Nexora.Contracts/Reporting/        ReportFilter (filtre universel §6), KpiValue (+ nature actual/forecast/estimated),
                                   CommandCenterResponse, Cashier/Manager/GroupDashboardResponse,
                                   Sales{Journal,Breakdown}Response, StockValuation/Alert, AgingResponse,
                                   BudgetVsActualResponse, AlertCenterResponse, DrillResponse, Ask*, Export*

Nexora.Application/Reporting/
  FilterPeriod.Resolve             presets today / week / month / year + période personnalisée
  KpiCatalog                       définition documentée de 14 KPI : clé, libellé, source, formule (§34)
  ReportCatalog                    sensibilité d'un rapport exportable (financial ⇒ permission dédiée)
  IKpiService / ISalesReports / IStockReports / IAgingReports / IBudgetReports
  IAlertCenter / IDrillDown / IExportService / IAskNexora

Nexora.Infrastructure/Reporting/
  ReportingClock.TodayAsync        « aujourd'hui » = current_date de la base (fuseau serveur),
                                   cohérent avec sold_at::date / expense_date / due_date — jamais l'UTC du process
  SqlKpiService                    Command Center (CA net des retours, marge, COGS, dépenses, trésorerie,
                                   créances, dettes, stock, panier moyen, encaissements/décaissements),
                                   tendance (generate_series), comparaison N vs N-1, dashboards caissier /
                                   manager / groupe. Vue groupe : périmètre autorisé capturé AVANT le scope
                                   système ; entreprises non autorisées listées dans ExcludedCompanies (§37)
  SqlAlertCenter                   stock faible, lots proches péremption, créances/dettes échues,
                                   écarts de caisse, trésorerie négative, chute des ventes 7 j (§28)
  SqlSalesReports                  journal des ventes paginé, ventilation par produit/catégorie/client/
                                   vendeur/moyen/jour, top produits
  SqlStockReports                  valorisation, ruptures, produits dormants
  SqlAgingReports                  balance âgée clients/fournisseurs, tranches lues depuis aging_bucket_set
  SqlBudgetReports                 budget vs réalisé par compte (LATERAL sur budget_line et journal_entry_line)
  SqlDrillDown                     CA → jour → vente → ligne ; créance → client → factures (§29)
  CsvExportService                 export CSV conforme aux filtres, journalisé dans export_log (§30, §36)
  RuleBasedAskNexora               appariement d'intentions déterministe (sans LLM) : CA, marge, créances
                                   échues, trésorerie, ruptures ; cite les sources ; ContainsForecast explicite (§33)
```

### API — `v1`

| Route | Permission | Objet |
|---|---|---|
| `GET /dashboard/command-center` | `reporting.view` | Command Center (§4-5) |
| `GET /dashboard/cashier/{sessionId}` | `reporting.view` | tableau de bord caissier (§7) |
| `GET /dashboard/manager` | `reporting.view_financial` | tableau de bord responsable (§8) |
| `GET /dashboard/groups/{groupId}` | `reporting.view_group` | vue consolidée — requiert l'en-tête `X-Group-Scope: true` (§12) |
| `GET /dashboard/alerts` | `reporting.view` | centre d'alertes (§28) |
| `GET /dashboard/drill?path=` | `reporting.view_financial` | drill-down (§29) |
| `GET /dashboard/kpi-catalog` | `reporting.view` | définitions documentées des KPI (§34) |
| `GET /reports/sales/journal` · `sales/breakdown/{dimension}` · `sales/top-products` | `reporting.view` | ventes (§13-14) |
| `GET /reports/stock/{valuation,ruptures,dormant}` | `view_financial` / `view` | stock (§15) |
| `GET /reports/aging/{receivables,payables}` | `reporting.view_financial` | antériorité des soldes (§17-19) |
| `GET /reports/budget-vs-actual?fiscalYear=` | `reporting.view_financial` | budget vs réalisé (§24) |
| `POST /reports/export` | `reporting.export` (+ `export_financial` si rapport financier) | export CSV, `File(...)` (§30) |
| `POST /ask` | `reporting.ask` | Ask Nexora (§33) |

Le contrôle de périmètre reste **serveur** : `X-Company-Id` ne fait que *sélectionner*
parmi les entreprises du jeton ; la vue groupe exige `X-Group-Scope: true` et un
groupe habilité, sinon `403 reporting.group_scope`.

### Frontend

`frontend/nexora-web/src/dashboard/DashboardPage.tsx` — Command Center : sélecteur de
période (jour / semaine / mois / année), grille de 14 tuiles KPI (badge « prévision » /
« estimation » si `kind ≠ actual`), sparkline du CA, bloc de comparaison N vs N-1,
liste des alertes. `dashboardApi.ts` typé. Écran masqué proprement sans `reporting.view`.
`npm run build` + `npm run typecheck` OK.

## Vérifications exécutées

**Tests d'intégration `Nexora.IntegrationTests/DashboardReportingTests` — 9/9** :

| Scénario (§37) | Vérifié |
|---|---|
| CA du Command Center réconcilié avec le journal des ventes | Δ CA = Δ total journal = 900 ; tous les KPI `kind = actual` |
| Ventilation des ventes sur toutes les dimensions | product / category / customer / salesperson / payment_method / day — aucune erreur SQL, taux de marge ≤ 100 % |
| Balance âgée réconciliée avec le sous-livre des créances | `GrandTotal` = Σ `original_amount − paid_amount` ; créance échue depuis 15 j → tranche « 1-30 j » ; chaque ligne = Σ de ses tranches |
| Dashboard groupe exclut une entreprise non autorisée | Supermarché ABC non habilité → `ExcludedCompanies` ; CA consolidé = Σ des seules entreprises autorisées |
| Groupe hors périmètre | `DomainException` / `ErrorKind.Forbidden` |
| Drill-down d'un KPI vers ses sources | `revenue` → jour → vente (id retrouvé) → lignes (Σ = 400) |
| Ask Nexora | intent `sales_total`, sources non vides, `ContainsForecast = false`, faits `kind = actual` ; question inconnue → `unknown` |
| Export du journal des ventes | CSV `text/csv`, sensibilité `financial`, en-tête attendu, ligne écrite dans `export_log` |
| Centre d'alertes | ne renvoie que des sévérités connues sur données validées |

Smoke API (démo ABC GROUP) : login `direction@abc-group.demo` →
`command-center`, `alerts`, `kpi-catalog` (14), `sales/journal`, les 6 dimensions de
`sales/breakdown`, `top-products`, `stock/{valuation,ruptures,dormant}`,
`aging/{receivables,payables}`, `budget-vs-actual`, `drill?path=revenue`, `manager`,
`ask`, `export` → **200**. `groups/{id}` sans `X-Group-Scope` → **403** ; avec →
**200** (vue consolidée des 3 entreprises). Caissier : `command-center` **200**,
`manager` **403** (deny-by-default).

**Total backend : 58 tests verts** (16 unitaires + 2 API + 40 intégration),
build Release 0 warning. DB : 26 migrations (V001–V026), 122 tables.

## Écarts et décisions

- **Fuseau horaire** : « aujourd'hui » pour le pilotage = `current_date` de la base
  (fuseau serveur), lu via `ReportingClock`. L'UTC du process bascule un jour trop tôt en
  soirée et désaligne les fenêtres `sold_at::date`.
- **Rapports planifiés** : tables `report_schedule` / `report_execution` + contrat DTO
  livrés ; l'exécuteur (cron, envoi e-mail) est reporté à la phase intégrations.
- **Exports** : CSV livré ; `xlsx` / `pdf` et l'expiration des téléchargements temporaires
  restent à câbler.
- **Ask Nexora** : appariement d'intentions déterministe, sans LLM — garde-fous du §33
  respectés (périmètre, sources, distinction prévision/réel).
- **Prévisions (§25)** : `KpiValue.Kind` (`forecast` / `estimated`) et le drapeau
  `ContainsForecast` sont en place ; les modèles de projection eux-mêmes ne sont pas encore
  calculés.

## Couverture des critères de sortie (§38)

| Critère | État |
|---|---|
| Dashboards par rôle | ✅ caissier / manager / groupe implémentés ; comptable & direction couverts par Command Center + rapports financiers |
| Command Center | ✅ `SqlKpiService.CommandCenterAsync` + écran |
| KPI définis et documentés | ✅ `KpiCatalog` (14) + `GET /dashboard/kpi-catalog` |
| Filtres et périmètres | ✅ `ReportFilter` + `FilterPeriod` ; périmètre borné serveur (RLS + `X-Group-Scope`) |
| Rapports ventes / stock / achats | ✅ ventes & stock livrés ; achats via journaux comptables (Phase 4) |
| Créances / dettes | ✅ balance âgée configurable, réconciliée au sous-livre |
| Finance et comptabilité | ✅ réutilise les rapports Phase 4 (balance, résultat, bilan) ; budget vs réalisé ajouté |
| Consolidation | ✅ `GroupDashboardAsync` — exclusion stricte des entreprises non autorisées |
| Drill-down | ✅ `SqlDrillDown` chemin CA → ligne, créance → facture |
| Exports | ✅ CSV journalisé, permission financière distincte |
| Rapports planifiés | ✅ schéma + contrat ; exécuteur en phase intégrations |
| Alertes | ✅ `SqlAlertCenter` (7 règles) + objectifs `kpi_target` |
| Ask Nexora avec garde-fous | ✅ `RuleBasedAskNexora` |
| Performance et sécurité | ✅ pagination, filtres indexés, agrégations SQL ; contrôle de périmètre serveur, audit des exports |

## Suite — Phase 8

Modules sectoriels : adapter NEXORA à la pharmacie, au restaurant, au supermarché, au
grossiste et aux services en conservant le même noyau.
