# Phase 42 — Business Intelligence, Reporting & Pilotage — implémentation

**Statut : livré et vérifié.** Migration `V055` (3 tables neuves + 4 tables V011/V026 activées
+ 1 correction de schéma + 3 permissions). 7 tests d'intégration (`BiReportingTests`),
3 tests API (`BiEndpointsTests`), 5 tests frontend (`biApi.test.ts`).
**313 tests backend** verts (18 unit + 29 API + 266 intégration), Debug **et** Release, 0 warning.
76 tests frontend. `tsc` + `vite build` verts.

Le socle décisionnel existait déjà et reste **validé** : Command Center + dashboards par rôle,
rapports ventes/stock/aging/budget-vs-réel, centre d'alertes, drill-down, export CSV, Ask Nexora
(Phase 7) ; consolidation groupe complète (Phase 9) ; trésorerie & prévision de trésorerie
(Phase 28) ; comptabilité générale (Phase 29) ; couche de confiance — réconciliation KPI, qualité
des données, snapshots (Phase 31). L'amélioration ciblée de cette phase **active quatre tables
posées en Phase 7 (V026) mais jamais consommées par aucun service** (`kpi_target`, `saved_report`,
`report_schedule`, `report_execution` — confirmé : zéro référence dans le code avant cette phase),
**active le CRUD budgets** (`budget`/`budget_line` de la Phase 7 n'avaient qu'une lecture
vs-réel), et ajoute les trois concepts qui n'existaient nulle part : un **catalogue KPI gouverné**
persisté, des **insights** (anomalies expliquées et actionnables), et des **prévisions** présentées
explicitement comme des estimations.

### Socle réutilisé

- **Phase 7** — `IKpiService`/`ISalesReports`/`IStockReports`/`IAgingReports`/`IBudgetReports`/
  `IAlertCenter`/`IDrillDown`/`IExportService`/`IAskNexora`, `KpiCatalog` (catalogue KPI statique,
  source du nouveau catalogue persisté)
- **Phase 9** — `IConsolidationService` (aliasé, non reconstruit)
- **Phase 11** — `audit_log` immuable → historique de `kpi_definition`/`budget` (triggers)
- **Phase 16/41** — `SchedulerRunner`/`IJobRegistry` → 2 nouveaux jobs nommés
- **Phase 28** — `ITreasuryQueries.MaturityForecastAsync` → réutilisée pour la prévision de
  trésorerie plutôt que dupliquée une troisième fois (une deuxième implémentation parallèle du
  cashflow forecast existait déjà en Phase 28/`IFinancialReports` — non touchée, documentée)
- **Phase 31** — `IDataQualityService`/`IKpiReconciliation`/`IAnalyticsSnapshotService` (alias
  `/reporting/data-quality`)
- **Phase 34** — `INotificationService.NotifyAsync` → livraison des rapports planifiés & assignation
  d'insight (`source` doit être l'une des 6 valeurs déjà en base : `system/automation/workflow/
  alert/security/manual` — jamais une clé libre)
- **Phase 40** — `IDashboardLayoutService.SaveAsync` → alias `POST /dashboards`

---

## Correspondance cahier des charges → implémentation

| § | Exigence | Où |
|---|---|---|
| 2 | Un KPI a une définition/source/périmètre ; chiffres traçables ; consolidation respecte les droits | `kpi_definition` gouverné (§48/50) ; RLS + filtre explicite sur toutes les nouvelles tables entreprise ; consolidation Phase 9 déjà conforme |
| 4-8 | Dashboards par rôle (caissier/manager/comptable/direction/groupe) | **Déjà livrés** (Phase 7 : `CashierDashboardAsync`/`ManagerDashboardAsync`/`CommandCenterAsync`/`GroupDashboardAsync`) — validés, non reconstruits. Dashboard comptable (§6) — couvert par l'écran Comptabilité (Phase 29) + Finance (Phase 28), pas un endpoint dédié séparé |
| 9 | Contexte analytique (entreprise/groupe/établissement/…/devise) | `ReportFilter` (Phase 7) déjà porteur de la plupart des dimensions ; RLS = garde-fou serveur systématique |
| 10-16 | KPI ventes/marge/stock/clients/fournisseurs/finance/comptabilité | Calculs **déjà livrés** dans les modules dédiés (Phases 7/24/26/27/28/29) ; **gouvernance documentée** de 14 KPI transverses dans `kpi_definition` (§50) |
| 17 | KPI RH optionnels, données sensibles limitées aux rôles autorisés | Hors périmètre applicatif de cette phase — aucune donnée de présence/heures travaillées n'existe encore dans le modèle ; **backlog explicite** |
| 18-19 | Périodes (jour→exercice, comparaison N/N-1) ; comparaisons (entreprise/établissement/période/objectif/budget/prévision/produit) | `ReportFilter`/`FilterPeriod` (Phase 7) déjà couvrent jour/semaine/mois/année ; **`IObjectiveService.ScoreAsync`** calcule `PeriodRange` pour day/week/month/**quarter**/year (nouveau) ; comparaison objectif vs réel (§39-40) et budget vs réel (§34, déjà Phase 7) livrées ; prévision vs réel — le forecast est daté et horodaté pour permettre la comparaison a posteriori, le rapprochement automatique reste backlog |
| 20 | Drill-down groupe→…→écriture, permissions à chaque niveau | **Déjà livré** (`IDrillDown`, Phase 7) — chaque insight référence un `drillPath` réutilisable (§38) |
| 21-22 | Graphiques (courbe/barres/aire/donut/heatmap/funnel), lisibilité, unité/période visibles | **Backlog explicite** — aucune librairie de graphiques dans le frontend (confirmé : `package.json` n'a que `react`/`react-dom`/`react-router-dom`) ; le seul graphique existant est une barre CSS faite main dans `DashboardPage` (Phase 7). Ajouter une vraie librairie ou des composants SVG dédiés dépasse le périmètre de cette phase — tableaux + `KpiCard`/`Badge` couvrent la lisibilité en attendant |
| 23-24 | Filtres ; filtres sauvegardés (nom/périmètre/partage/favoris/par rôle/utilisateur) | `ReportFilter` (Phase 7) ; filtres sauvegardés **génériques déjà livrés en Phase 40** (`saved_filter`) — non redistribués ici ; **`saved_report`** (§48-49 ci-dessous) est le cas spécifique « rapport + son propre filtre » |
| 25 | Rapports standards (ventes/achats/stock/inventaire/clients/fournisseurs/finance/trésorerie/comptabilité/taxes/créances/dettes/caisse/paiements/rentabilité/budgets) | **Catalogue documenté** (`IReportCatalogService.StandardReports()`, §48) recense les 16 rapports déjà livrés à travers les Phases 7/28/29, avec leur point d'entrée canonique |
| 26-28 | Rapports pharmacie/restaurant/commerce | **Déjà couverts** par les modules sectoriels (Phase 8) et le cycle de lots (Phase 24, expiration/rotation) — non redistribués |
| 29 | Rapports groupe (résultat consolidé, CA/marge/trésorerie consolidés, comparaison sociétés, contribution) | **Déjà livrés** (Phase 9, `GetStatementsAsync`) — catalogué (`consolidated_statements`) |
| 30-32 | Consolidation (mapping/devise/périodes/intercompany/élimination/périmètre/version) ; intercompany ; multi-devises (taux historique/moyen/clôture, gain/perte change) | **Déjà livrés** (Phase 9) — validés. Alias `GET/POST ~/api/v1/consolidations{,/{id}/run}` ajoutés (§55) sur `ConsolidationController`, résolvant le groupe depuis le périmètre appelant (`tenant.GroupIds`) plutôt que de dupliquer la logique. Méthode de taux « moyenne » (`GroupPolicyDto.FxMethod`) toujours non implémentée (seul le taux de clôture point-in-time l'est) — **limite déjà documentée en Phase 9, non résolue ici** |
| 33-34 | Budgets (annuel/mensuel/entreprise/département/catégorie/compte/version/approbation/révision) ; budget vs réel (écart montant/%/seuil/alerte/commentaire/responsable) | Budget vs réel **déjà livré** (Phase 7, `IBudgetReports`). **CRUD budgets activé** : `IBudgetService` — cycle `draft → pending_approval → active → closed`, **approbateur ≠ soumissionnaire** (comme Phase 11), **révision** = nouveau budget `draft` lié par `revised_from`, lignes recopiées. Alerte de seuil sur écart — backlog (le rapport §34 existant expose déjà l'écart, l'alerte automatique reste à brancher sur `IAlertCenter`) |
| 35-36 | Prévisions (ventes/trésorerie/stock/créances/dépenses, scénarios base/optimiste/prudent/personnalisé, hypothèses, date de calcul, version) — **toujours présentées comme des estimations** | **`IForecastService`** — `revenue`/`expenses`/`receivables` : moyenne mobile sur 60 j d'historique projetée à plat + ajustement de scénario (±10 % ou personnalisé) ; `stock` : valeur actuelle projetée à plat (aucune série historique de valorisation disponible) ; `cash` : **délègue** à `ITreasuryQueries.MaturityForecastAsync` (Phase 28) plutôt que de dupliquer un troisième calcul de trésorerie. `confidenceLevel` dérivé de la profondeur d'historique (low/medium/high) ; `assumptions` toujours renseignées en clair — jamais présenté comme une certitude |
| 37-38, 58 | Détection d'anomalies (vente/remise/écart caisse/marge/stock négatif/dépenses/CA/paiement) ; insights (données→anomalie→insight→explication→action suggérée, ignorable, tâche assignable, aucune action financière automatique) | **`IInsightService.ScanAsync`** — 5 règles autonomes (`revenue_drop`, `margin_drop`, `stock_anomaly`, `expense_spike`, `cash_gap`) écrites en SQL borné, dédupliquées par `(entreprise, catégorie, chemin, période)` ; chaque insight porte `evidence` (les chiffres qui le justifient), `drillPath`, `suggestedAction` ; **`DismissAsync`** (ignorer, motif) et **`AssignAsync`** (§38 « créer une tâche » — assignation + échéance + notification, jamais d'action financière déclenchée). Les autres catégories d'anomalies (remise anormale, écart caisse, paiement inhabituel) sont déjà couvertes par le moteur anti-fraude (Phase 11, `IFraudDetectionService`) — architecture proche (règles + signaux scorés) mais volontairement **non fusionnée** avec les insights BI dans cette phase (domaines distincts : fraude = sécurité, insight = pilotage) |
| 39-40 | Objectifs (CA/marge/stock/encaissement/recouvrement/budget, par entreprise/établissement/individuel) ; score de performance (configurable, KPI pondérés, période, périmètre, comparaison, explication) | **`kpi_target`** activé (`weight` ajouté) + **`IObjectiveService`** — `SetAsync` (upsert), `ScoreAsync` = moyenne pondérée de `min(ratio réel/cible, 150 %)` par objectif actif, note A-E (même barème que Phases 31/36/37) ; `direction=min` (cible à atteindre) vs `max` (plafond) toujours interprétés pour que ≥100 % = objectif tenu |
| 41 | Rapports planifiés (quotidien/hebdo/mensuel, destinataires, canal, format, filtre, période, fuseau) | **`report_schedule`/`report_execution` activés** — `IReportScheduleService.RunDueAsync` (nouveau job planificateur `reports.deliver_scheduled`, par entreprise) exécute les rapports dus via le dispatcher générique (§48) et notifie chaque destinataire résolu en utilisateur NEXORA (`INotificationService`) ; fuseau horaire — le calcul `NextRunAt` est strictement en UTC (§ Points de conception) ; canal — notification interne systématique, email/SMS = stubs Phase 34 |
| 42 | Exports (PDF/Excel/CSV/JSON/graphique/données sources) | CSV **déjà livré** (Phase 7, `IExportService`) — historique exposé (`GET /reporting/exports`, §55). PDF/Excel/export graphique — **backlog explicite** |
| 43 | Mode présentation (plein écran, actualisation, rotation, date/périmètre visibles, masquage sensible) | **Backlog explicite** — aucun mode kiosque/présentation dans le frontend actuel |
| 44-45 | Reporting financier (résultat/bilan/flux/balance/GL/budget/créances/dettes/marge/rentabilité) ; reporting comptable (journal/GL/balances/taxes/écritures/périodes) | **Déjà livrés** (Phases 28/29) — catalogués (§48), non redistribués |
| 46 | Reporting audit (actions sensibles, corrections, remboursements, annulations, accès, exports, élévations) | **Déjà livré** (`audit_log` immuable + `IAuditQueryService`, Phase 11) — non redistribué |
| 47 | Reporting opérationnel (caisses, temps traitement, ruptures, commandes, livraisons, retours, SLA) | Partiellement couvert par les modules dédiés (POS Phase 6, achats Phase 26, exploitation Phase 37) — un tableau opérationnel unifié reste **backlog** |
| 48-49 | Catalogue de rapports (catégorie/nom/description/source/rôles/filtres/version/propriétaire/statut) ; rapports personnalisés (constructeur, métriques, dimensions, filtres, groupements, calculs, graphiques, enregistrement, partage) | **`IReportCatalogService`** — `StandardReports()` documente les 16 rapports déjà livrés (catégorie/nom/description/source/rôles/point d'entrée) ; **`saved_report` activé** pour les rapports personnels/partagés **fondés sur un filtre** (7 clés génériques réexécutables via `RunAsync`/`RunByKeyAsync`) — un constructeur drag-and-drop de métriques/dimensions/calculs arbitraires est **backlog explicite**, la personnalisation actuelle se limite à choisir un rapport existant + son filtre |
| 50 | Gouvernance KPI (définition unique, source officielle, formule documentée, propriétaire, version, historique) | **`kpi_definition`** — table plateforme (une seule définition pour tout NEXORA ONE, comme `job_definition`), amorcée depuis `KpiCatalog` (Phase 7), lecture seule dans cette phase (édition = migration + `audit_log`, §50 « historique ») |
| 51 | Qualité données BI (fraîcheur/complétude/exactitude/doublons/écart source/dernière synchro/statut) | **Déjà livré** (`IDataQualityService`, Phase 31) — non redistribué, alias `/reporting/data-quality` |
| 52 | Performance BI (agrégats/cache/index/pré-calcul/pagination/asynchronisme/limites export/requêtes lourdes en arrière-plan) | Pagination déjà présente sur les rapports Phase 7 ; rapports planifiés = asynchronisme via le planificateur (§41) ; pré-calcul/cache dédié BI — **backlog** |
| 53 | Sécurité BI (RLS, scope groupe/entreprise/établissement, masquage, exports contrôlés, audit, aucune fuite inter-tenant) | RLS sur `insight`/`forecast` (`enable_company_rls`) ; `kpi_target`/`saved_report`/`report_schedule`/`report_execution`/`budget`/`budget_line` déjà RLS depuis leur création (V011/V026, vérifié) ; tous les nouveaux services **filtrent explicitement** par entreprise (défense en profondeur, comme Phases 40-41) |
| 54 | Offline/local (rapports locaux, cache contrôlé, rapports différés, synchro, fraîcheur) | Hérite du mode offline/local Phase 10 — aucun cache BI dédié ajouté ; **backlog** |
| 55 | API principales | Voir § Backend/API ci-dessous — chaque route de la liste est servie, `/kpis` existant (alias Phase 31 vers le Command Center) conservé sans changement pour ne pas casser le frontend déjà branché — le catalogue KPI gouverné est exposé à `/kpi-definitions` (+ alias `/kpis/{key}/definition`, sans collision) |
| 56 | Événements (`ReportGenerated`…`DataQualityIssueDetected`) | Portés par les colonnes de statut (`report_execution.status`, `insight.status`, `budget.status`) + `audit_log` (trigger) — cohérent avec le style déjà en place (Phases 30/34/39/41 : table de statut + audit, pas de bus d'évènements séparé) |
| 57 | Tests | `BiReportingTests` (7) + `BiEndpointsTests` (3) — cf. ci-dessous |
| 58 | Scénario direction groupe (CA -8 % → drill-down → entreprise -18 % → produits en baisse → insight → tâche manager → suivi) | Le mécanisme (insight avec `evidence` + `drillPath` + `AssignAsync` → notification) est livré ; le scénario **multi-niveaux complet** (agrégation groupe → détail entreprise → détail produit en une seule chaîne automatique) reste un exemple d'usage combinant les briques existantes (`GroupDashboardAsync` Phase 7 + `IDrillDown` + `IInsightService`), pas un nouveau moteur dédié |
| 59 | Critères de sortie | Dashboards/KPI/rapports/consolidation/intercompany/multi-devises (déjà livrés, validés) + budgets CRUD, prévisions, insights, objectifs/score, rapports planifiés, catalogue de rapports/rapports enregistrés, gouvernance KPI (**livrés cette phase**) ; graphiques, constructeur de rapports personnalisés, mode présentation, exports PDF/Excel, reporting opérationnel unifié, performance BI dédiée (cache/pré-calcul), offline BI : **backlog explicite** |
| 60 | Étape suivante — Phase 43 (intelligence métier & recommandations) | hors périmètre |

## `V055` — contenu (3 tables neuves, 4 activées, 1 correction, 3 permissions)

- **`kpi_definition`** (NEW, plateforme, pas de RLS) — 14 définitions amorcées depuis `KpiCatalog`.
- **`kpi_target`** (V026, activée) — `+ weight numeric(5,2) CHECK > 0`.
- **`budget`** (V011, activé) — `status` += `pending_approval` ; `+ revised_from`/`submitted_by`/
  `approved_by`/`approved_at`.
- **`insight`** (NEW, RLS entreprise) — `UNIQUE NULLS NOT DISTINCT (company_id, category,
  drill_path, period_start)` — dédup des scans.
- **`forecast`** (NEW, RLS entreprise) — `points` jsonb `[{date,value}]`, `confidence_level`
  CHECK low/medium/high.
- **Correction** : `report_execution_schedule_fk` était une FK **composite**
  `(report_schedule_id, company_id) REFERENCES report_schedule(id, company_id) ON DELETE SET NULL`
  — supprimer un `report_schedule` mettait **aussi `company_id` à `NULL`** sur ses exécutions,
  violant sa contrainte `NOT NULL` (jamais constaté avant cette phase : zéro service ne consommait
  encore ces tables). Remplacée par une FK simple sur `report_schedule_id` seul.
- `trg_audit_*` sur `kpi_definition`/`insight`/`forecast`/`budget`. Permissions
  **`reporting.manage_budgets`**, **`reporting.forecast`**, **`reporting.manage_insights`**
  (super/group admin, owner, chief_accountant ; `forecast`/`manage_insights` aussi manager) —
  réutilise **`reporting.schedule`** et **`reporting.manage_targets`** (Phase 7, déjà accordées,
  jusque-là sans fonctionnalité derrière).
- **225 tables (+3), 190 RLS FORCE (+2)**, 55 migrations (V001–V055).

## Backend

| Couche | Fichier |
|---|---|
| Contracts | `Nexora.Contracts/Reporting/BiContracts.cs` (les stubs morts `ReportScheduleDto`/`CreateReportScheduleRequest` de `ReportingContracts.cs` sont supprimés — remplacés par la version branchée) |
| Application | `Nexora.Application/Reporting/IBi.cs` — `IKpiGovernanceService`, `IObjectiveService`, `IReportCatalogService`, `IReportScheduleService`, `IForecastService`, `IInsightService`, `IBudgetService` ; `IExportService += HistoryAsync` ; `PermissionCatalog.Reporting += ManageBudgets/Forecast/ManageInsights` |
| Infrastructure | `Reporting/{KpiGovernanceService,ObjectiveService,ReportCatalogService,ReportScheduleService,ForecastService,InsightService,BudgetService}.cs` ; `CsvExportService += HistoryAsync` ; `SchedulerRunner` += 2 jobs (`reports.deliver_scheduled`, `insights.scan`) |
| API | `Nexora.Api/Controllers/BiController.cs` (`/api/v1/{kpi-definitions,objectives,reports,forecasts,insights,budgets,dashboards}`) + alias sur `ConsolidationController` (`~/consolidations`), `AnalyticsController` (`~/reporting/data-quality`), `ReportsController` (`~/reporting/exports`) |

### Points de conception

- **`SchedulerRunner` : deux jobs de plus, même moule que Phase 41** — `reports.deliver_scheduled`
  et `insights.scan` sont des jobs **par entreprise** (`PerCompanyAsync`), exactement comme
  `automations.due` — chaque service opère dans le périmètre déjà posé par la boucle, aucune
  logique système/cross-entreprise à gérer dans les nouveaux services.
- **Toujours en UTC, jamais `DateTimeOffset.Date`** — `NextRunAt` (rapports planifiés) calcule sur
  `from.UtcDateTime.Date` puis reconstruit un `DateTimeOffset` avec `TimeSpan.Zero` explicite :
  `DateTimeOffset.Date` renvoie un `DateTime` « Unspecified » dont la conversion implicite adopte
  le **fuseau local de la machine**, pas UTC — même piège que `DateTimeOffset.Parse` sans « Z »
  déjà rencontré en Phase 41, découvert ici sur un serveur en `UTC-4`.
- **Ne jamais dupliquer un calcul de trésorerie qui existe déjà** — `IForecastService` pour
  `metric=cash` délègue à `ITreasuryQueries.MaturityForecastAsync` (Phase 28) plutôt que de
  recalculer un troisième cashflow forecast (un deuxième existait déjà en parallèle côté
  `IFinancialReports` — documenté, non touché).
- **`source` de notification = liste fermée en base** — `notification_source_chk` limite `source`
  à `system/automation/workflow/alert/security/manual` ; les nouveaux appels (rapports planifiés,
  assignation d'insight) utilisent `automation`, jamais une clé métier libre comme
  `"insights.scan"` (détecté par un test d'intégration, pas en relecture de code).
- **FK composite + `ON DELETE SET NULL` = piège** — une FK sur `(a, b) REFERENCES t(a, b) ON
  DELETE SET NULL` met **les deux colonnes** à `NULL`, pas seulement celle qu'on croit cibler ;
  si l'une des deux est `NOT NULL` ailleurs, la suppression échoue. Corrigé dans `V055` en
  ramenant la FK à la seule colonne réellement optionnelle.
- **Catalogue KPI = table plateforme, pas une définition par entreprise** — `kpi_definition`
  n'a pas de `company_id` : gouvernance signifie *une* définition partagée par tout NEXORA ONE
  (comme `job_definition`), pas une redéfinition libre par client — cohérent avec §50 « définition
  unique ».
- **Alias `/kpis` existant conservé tel quel** — `AnalyticsController` exposait déjà
  `GET ~/api/v1/kpis` comme alias du Command Center (Phase 31), **consommé par le frontend**
  (`analyticsApi.getKpis`). Plutôt que de le réécrire (rupture), le nouveau catalogue KPI gouverné
  prend un chemin distinct (`/kpi-definitions` + alias `/kpis/{key}/definition`, qui ne collisionne
  pas puisque c'est un gabarit de route différent).

## Tests d'intégration — `BiReportingTests` (7, `[Collection("finance")]`, entreprise c2)

| Test | § | Vérifie |
|---|---|---|
| `Kpi_governance_lists_seeded_definitions_and_looks_up_one` | 48, 50 | catalogue contient les KPI clés, lookup par clé, clé inconnue → 404 |
| `Objective_set_list_delete_and_score_reflects_actual_vs_target` | 39-40 | upsert d'objectif, score reflète l'actuel (vente insérée) vs la cible, suppression |
| `Report_schedule_save_list_run_due_delivers_and_logs_execution` | 41 | planification, exécution due livrée + notifiée, historique `succeeded`, prochaine échéance recalculée |
| `Report_catalog_lists_standard_reports_and_saved_report_round_trips_and_runs` | 48-49 | catalogue standard non vide, clé non générique refusée, sauvegarde + exécution + suppression d'un rapport personnel |
| `Forecast_generate_for_revenue_and_cash_and_rejects_invalid_input` | 35-36 | projection revenue (méthode/points/confiance), délégation trésorerie pour cash, rejets (métrique/horizon/ajustement manquant) |
| `Insight_scan_detects_anomalies_then_dismiss_assign_and_dedup` | 37-38, 58 | détection baisse de CA + stock négatif, déduplication au second scan, ignorer, assigner (notifie) |
| `Budget_lifecycle_requires_four_eyes_and_revision_preserves_lines` | 33-34 | cycle complet, approbateur ≠ soumissionnaire, révision recopie les lignes |

## Tests API — `BiEndpointsTests` (3)

| Test | Vérifie |
|---|---|
| `Bi_routes_require_authentication` | 6 routes `/api/v1/*` sans jeton → 401 |
| `Read_only_bi_endpoints_are_wired_over_http` | 13 endpoints de lecture → 200 |
| `Saved_report_round_trips_over_http_and_cleans_up_after_itself` | création + exécution + suppression, aucune trace laissée |

## Frontend

`src/reports/biApi.ts` (types + fonctions) + `src/reports/BiSections.tsx` (6 nouvelles sections)
branchées en onglets dans `AnalyticsPage.tsx` (route `/reports`, déjà dans la nav « Rapports ») :

- le **portail de la page passe de `reporting.data_quality` à `reporting.view`** (plus large — la
  page ne doit plus être invisible à un simple lecteur de rapports) ; les 3 onglets de la couche de
  confiance (réconciliation/qualité/snapshots) restent gardés individuellement par
  `reporting.data_quality` avec un message explicite si absent ;
- **Catalogue KPI** — table des définitions gouvernées ;
- **Objectifs & score** — score global + détail pondéré, création/suppression d'objectif ;
- **Rapports** — catalogue standard, mes rapports enregistrés (créer/exécuter/supprimer, résultat
  affiché en JSON), rapports planifiés + journal d'exécution (gardés par `reporting.schedule`) ;
- **Prévisions** — génération (métrique/horizon/scénario) + liste avec niveau de confiance ;
- **Insights** — liste avec sévérité, assigner/ignorer (gardés par `reporting.manage_insights`) ;
- **Budgets** — liste, création, lignes, actions de cycle de vie selon le statut courant.

Vitest `biApi.test.ts` (5) → **76 tests frontend**.

## Manques assumés (backlog)

- Vraie librairie de graphiques (courbe/aire/donut/heatmap/funnel, §21-22) — le frontend n'a
  aujourd'hui qu'une barre CSS faite main (Phase 7) ; tableaux/`KpiCard`/`Badge` en attendant
- Constructeur de rapports personnalisés par glisser-déposer (métriques/dimensions/calculs
  arbitraires, §49) — la personnalisation actuelle se limite aux 7 rapports génériques + leur filtre
- Mode présentation plein écran (§43)
- Exports PDF/Excel/graphique (§42, seul CSV est livré)
- KPI RH (§17) — aucune donnée de présence/heures dans le modèle actuel
- Cache/pré-calcul BI dédié (§52) — au-delà de la pagination déjà en place
- Alerte automatique de seuil sur écart budgétaire (§34) — le rapport existe, le déclenchement
  d'alerte reste à brancher sur `IAlertCenter`
- Méthode de taux de change « moyenne » pour la consolidation (§32) — limite déjà documentée en
  Phase 9, seul le taux de clôture point-in-time est implémenté
- Reporting opérationnel unifié (§47) — dispersé dans les modules dédiés (POS, achats, exploitation)
- Rapprochement automatique prévision vs réel (§19) — le forecast est horodaté pour permettre une
  comparaison manuelle, pas encore calculée automatiquement
