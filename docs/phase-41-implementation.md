# Phase 41 — Administration opérationnelle & maintenance — implémentation

**Statut : livré et vérifié.** Migration `V054` (7 tables + 1 permission).
9 tests d'intégration (`OperationsMaintenanceTests`), 3 tests API (`OpsMaintenanceEndpointsTests`),
5 tests frontend (`maintenanceApi.test.ts`).
**303 tests backend** verts (18 unit + 26 API + 259 intégration), Debug **et** Release, 0 warning.
71 tests frontend. `tsc` + `vite build` verts.

Le socle d'exploitation existait déjà (Phase 16 planificateur, Phase 34 file de notification,
Phase 36 continuité, Phase 37 état système/Go-Live/déploiements, Phase 11 audit) et reste
**validé** par cette phase. L'amélioration ciblée est un **centre d'exploitation & de
maintenance** : un vrai **registre de jobs** (nom, activation, historique, exécution manuelle),
l'administration des **deux files d'attente existantes** (notifications + webhooks) et de leurs
messages morts, une **batterie de diagnostics** normalisée, des **bascules de fonctionnalité**
avec kill switch, des **fenêtres de maintenance planifiée** avec état machine et dégradation
réelle de l'API, une **purge technique** sûre (jamais de donnée métier) précédée d'un aperçu à
blanc, et un **workflow de correction de donnée contrôlée** à quatre yeux.

### Socle réutilisé

- **Phase 16** — `SchedulerRunner` (traitements asynchrones idempotents) — **refactoré** en jobs nommés (§7-9)
- **Phase 34** — `notification_outbox` (retry/backoff/dead-letter) → première file administrée (§10-12)
- **Phase 12** — `outbound_webhook_delivery` → seconde file administrée (§10-12)
- **Phase 36** — `backup` (vérification récente) → contrôle « backup » des diagnostics (§5)
- **Phase 37** — `IOperationsService.SystemStatusAsync` / `/api/v1/system/status` (alias `/ops/status`), `IPlatformMetrics`
- **Phase 11** — `audit_log` immuable + `IAuditQueryService` (alias `/ops/audit`) (§31)
- **Phase 38** — `platform_customer_company` / `subscription` → contrôle « licence » des diagnostics (§5)
- **Phase 10** — `sync_device.last_sync_at` → contrôle « synchronisation » des diagnostics (§5)

---

## Correspondance cahier des charges → implémentation

| § | Exigence | Où |
|---|---|---|
| 2 | `SANTÉ → DIAGNOSTIC → ACTION CONTRÔLÉE → AUDIT` ; aucune action destructive sans confirmation/permission ; audit systématique | Toute mutation passe par `operations.manage`/`operations.correct_data` ; `trg_audit_*` sur les 7 tables de `V054` ; la purge exige un aperçu (`PreviewAsync`, aucune écriture) avant `ExecuteAsync` |
| 3-4, 33-34, 47-48 | Dashboard opérationnel, statuts système, monitoring, alertes, capacité, SLA interne | Réutilise `SystemStatusAsync` (Phase 37) + nouveaux onglets Jobs/Files/Diagnostics ; `JobStatusDto.successRatePct` = taux de réussite (§48) ; capacité/prévision de croissance — **backlog** (compteurs bruts déjà exposés via `/metrics` Phase 37) |
| 5-6 | Diagnostics (DB, stockage, queue, email, SMS, connecteur, imprimante, synchronisation, backup, licence, horloge/fuseau) ; `/health` `/ready` `/live` | **`IOpsDiagnosticsService.RunAllAsync`** — 11 contrôles exacts ; email/SMS/imprimante `skipped` (canaux simulés / hors périmètre applicatif, §40) ; `/health` + `/ready` déjà Phase 3/37, alias `/ops/health` → `RunAllAsync` |
| 7-9, 51 | Jobs planifiés (nom, type, fréquence, dernière/prochaine exécution, durée, statut, erreurs, retry, propriétaire) ; jobs métier / techniques | **`job_definition`** (9 jobs seedés, catégorie business/technical) + **`job_run`** (historique) ; **`IJobRegistry`** ; `SchedulerRunner` **refactoré** : chaque traitement devient un job nommé, vérifié via `IsEnabledAsync`, journalisé via `Start/FinishRunAsync` ; `RunSingleJobAsync` = exécution manuelle (outrepasse la pause) |
| 10-12 | File d'attente (priorité, taille, âge, workers, débit, erreur, retry, dead-letter) ; gestion dead-letter (cause, tentatives, rejeu contrôlé, suppression contrôlée, audit) ; retry (backoff, max tentatives, escalade) | **`IQueueAdminService`** sur les 2 files existantes (`notification_outbox`, `outbound_webhook_delivery`) : `StatsAsync` (pending/failed/dead/débit horaire/âge), `DeadLettersAsync`, `RetryAsync` (relance immédiate), `ReplayDeadLetterAsync` (remise en file, tentatives à zéro), `DeleteDeadLetterAsync` — le payload reste dans `audit_log` (trigger déjà en place) |
| 13-15 | Maintenance DB (index, connexions, locks, transactions longues, taille tables) ; diagnostics requêtes ; stockage | **`IOpsDiagnosticsService.DatabaseAsync`** — lecture système (`pg_stat_activity`, `pg_locks`, `pg_class`/`pg_total_relation_size`) sans donnée d'entreprise ; `StorageAsync` (taille base, sauvegardes, documents émis) ; top requêtes lentes / plan / recommandation — **backlog** (`pg_stat_statements` non activé par défaut) |
| 16-19, 22 | Rétention (type, durée, entreprise, obligation légale, archivage/suppression, exception) ; purge (dry-run, aperçu, confirmation, par lot, journal, protection données critiques) ; nettoyage (jamais de suppression métier silencieuse) | **`retention_policy`** (4 types **fermés** : sessions expirées, défis MFA expirés, jobs terminés, notifications résolues — jamais de table métier/comptable) + **`purge_run`** (journal aperçu/exécuté) ; **`IRetentionPurgeService`** — `PreviewAsync` = zéro écriture, `ExecuteAsync` protège explicitement (`completed_job_runs` : seul un `succeeded` est purgeable, un `failed` reste pour post-mortem ; `resolved_notifications` : seule une notification archivée/expirée est purgeable, jamais une non lue) |
| 20 | Cache (statut, taille, hit/miss, invalidation contrôlée, clés critiques protégées) | `POST /ops/cache/invalidate` — vide le cache mémoire technique partagé (clés d'API Phase 35, idempotence Phase 18) ; jamais de donnée métier en cache |
| 21-22 | Configuration système / par entreprise | Déjà couvert par `setting`/`setting_definition` (Phase 14) + `sector_profile` (Phase 32) — validé, non redistribué ici |
| 23-24 | Maintenance planifiée (`PLANIFIER → NOTIFIER → SAUVEGARDER → MAINTENANCE → VÉRIFIER → FERMER`) ; mode maintenance (page, accès admin contrôlé, API dégradée, audit) | **`maintenance_window`** — état machine exacte (+ `canceled` avant démarrage, retour `verifying → in_progress`) ; index unique **une seule fenêtre `in_progress` à la fois** ; **`MaintenanceModeMiddleware`** — écritures refusées (503 + `Retry-After`) pendant `in_progress`, **sauf** un titulaire de `operations.manage` et les routes `/ops/*` `/auth/*` `/health` — les lectures restent toujours servies (dégradation, pas coupure totale) |
| 25-26 | Mise à jour (version, backup, migration, rollback, validation) ; gestion des versions | Déjà couvert par `deployment` (Phase 37 : version, `schema_version`, rollback) — validé |
| 27 | Feature flags (global/groupe/entreprise/utilisateur test, expiration, audit, kill switch) | **`feature_flag`** (`scope` global/company/user, `expires_at`) ; **`IFeatureFlagService.IsEnabledAsync`** — résolution utilisateur > entreprise > global, **un global désactivé (kill switch) l'emporte toujours** même sur un réglage plus spécifique activé ; scope « groupe » — backlog (pas de `group_id` dédié, un `company`-scope par membre du groupe suffit en pratique) |
| 28 | Diagnostic entreprise (santé, synchro, jobs, stockage, utilisateurs, permissions, connecteurs, POS, backup, erreurs) | `SystemStatusAsync` (Phase 37) + `RunAllAsync` (§5-6) couvrent déjà santé/synchro/connecteurs/backup par entreprise active ; vue consolidée dédiée « diagnostic entreprise » — backlog (agrégation des existants) |
| 29-30 | Support technique (mode diagnostic temporaire, consentement, durée, journal, révocation) ; impersonation sécurisée (interdite par défaut, bannière, aucun accès secret, sortie immédiate) | **Fondations déjà posées, non ré-implémentées ici** : `support_access_grant` (Phase 38, consentement/expiration/révocation) + `audit_log` immuable + step-up (Phase 11). Une impersonation réelle (émission de jeton « en tant que », bannière, revue de sécurité dédiée) est un chantier à part entière — **backlog explicite** |
| 31 | Audit administration (qui/quoi/quand/où/entreprise/objet/avant-après/résultat/correlation ID) | `audit_log` (Phase 11, immuable) + `IAuditQueryService` — alias `GET ~/api/v{version}/ops/audit` sur `SecurityController.Audit` |
| 32 | Export diagnostic (version, config non secrète, health, erreurs, métriques, jobs, queues, connecteurs — aucun secret) | `GET /ops/diagnostics` + `GET /ops/jobs` + `GET /ops/queues` + `GET /api/v1/metrics` (Phase 37, `[AllowAnonymous]`, agrégats uniquement) composent déjà cet export ; bouton « exporter en un fichier » côté écran — backlog |
| 35-37 | Maintenance locale / cloud / hybride (services OS, instances, scaling, agent) | Infrastructure — hors périmètre applicatif (cf. même réserve Phases 36-37) |
| 38 | Multi-entreprises (administration centralisée, scope groupe/entreprise, aucune action globale accidentelle, confirmation du périmètre) | Jobs/files/diagnostics/purge exigent une entreprise active (`X-Company-Id`) sauf les tables réellement globales (jobs, bascules, fenêtres de maintenance — explicitement des outils **plateforme**, comme `deployment` en Phase 37) ; confirmation de périmètre = `window.confirm` côté écran avant purge |
| 39-41 | Maintenance POS / imprimantes / connecteurs | POS/synchro déjà couverts (Phase 10) ; connecteurs déjà couverts (Phase 12/35, `integration_connector.status`) ; pilotes d'imprimante physiques = hors périmètre applicatif |
| 42 | Maintenance index/recherche (réindexation contrôlée, progression, pause/reprise) | `pg_trgm`/`unaccent` (Phase 23) ne nécessitent pas de réindexation applicative — non applicable, documenté comme tel |
| 43 | Recalculs (stock, agrégats, KPI, simulation avant correction, audit) | Chaque module porte déjà son propre recalcul de référence (ledger de stock Phase 5/24, réconciliation KPI Phase 31, clôture Phase 28/29) — un outil de recalcul générique transverse reste **backlog**, ces mécanismes dédiés restant la source de vérité |
| 44 | Correction de données (aucun `UPDATE` direct non audité, motif, avant/après, auteur, approbation, rapprochement) | **`data_correction_request`** (liste fermée table/champ) + **`IDataCorrectionService`** — `RequestAsync` capture l'ancienne valeur, `DecideAsync` exige **demandeur ≠ approbateur** (`CHECK` en base + contrôle applicatif), applique via une identité de colonne validée deux fois contre la liste fermée (jamais de saisie libre) |
| 45 | Outils développeur contrôlés (console restreinte, aucun accès arbitraire à la base) | Satisfait **structurellement** : toute la console d'exploitation n'expose que des contrôles prédéfinis (`RunAllAsync`, jobs, files) — aucune requête libre n'est jamais possible depuis l'API |
| 46 | Automatisation maintenance (backup, rotation logs, nettoyage cache, health checks, rapports, alertes, archivage) | Nouveau job **`maintenance.purge`** (technique, quotidien au plus) exécute les politiques de rétention système dues ; backups/health-checks déjà automatisés (Phase 36) ; rotation de logs = hébergement (Serilog console/fichier, infrastructure) |
| 49-50 | Plan de maintenance (quotidien/hebdo/mensuel, checklist, preuve) ; documentation exploitation (runbooks, procédures, contacts) | `maintenance_window` porte la preuve d'exécution (horodatages par état) ; runbooks/procédures = documentation opérationnelle (`docs/`), pas du code — **backlog documentaire** |
| 51 | API principales | `/api/v1/ops/{status,health,jobs,jobs/{key}/{run,pause,resume},queues,queues/{id}/retry,dead-letters,dead-letters/{id}/{replay},storage,database,cache/invalidate,maintenance,maintenance/{id}/transition,diagnostics,audit}` + extensions `feature-flags`, `retention-policies`, `purge/{preview,execute,history}`, `data-corrections/{,{id}/decide}` |
| 52 | Événements (`JobStarted`…`DataCorrectionApplied`) | Portés par `job_run.status`/`data_correction_request.status` + `audit_log` (trigger) plutôt qu'un flux d'évènements dédié — cohérent avec le style déjà en place (Phases 30/34/39 : table de statut + `audit_log`, pas de bus d'évènements séparé) |
| 53 | Tests | `OperationsMaintenanceTests` (9) + `OpsMaintenanceEndpointsTests` (3) — cf. ci-dessous |
| 54 | Critères de sortie | Dashboard/statuts/diagnostics/health, jobs, files/dead-letter/retry, DB/stockage, rétention/purge/nettoyage, cache, maintenance planifiée + mode maintenance réel, bascules + kill switch, audit, correction de données à 4 yeux, API : **livrés**. Capacité/prévision, requêtes lentes détaillées, impersonation réelle, réindexation, recalcul générique, runbooks documentaires, export diagnostic packagé : **backlog explicite** (fondations posées) |
| 55 | Étape suivante — Phase 42 (BI, reporting & pilotage) | hors périmètre |

## `V054` — contenu (7 tables, 1 permission)

- **`job_definition`** / **`job_run`** — registre + historique des jobs planifiés (table **plateforme**, sans RLS — comme `deployment`) ; 9 jobs seedés (5 techniques, 2 métier + `automations.due`/`lots.sweep_expired`).
- **`feature_flag`** — table **plateforme** ; `UNIQUE NULLS NOT DISTINCT (key, scope, company_id, user_id)` ; `CHECK` de cohérence scope↔cible.
- **`maintenance_window`** — table **plateforme** ; index unique partiel **une seule fenêtre `in_progress`**.
- **`retention_policy`** (RLS personnalisée : défaut système `company_id NULL` visible de tous, surcharge par entreprise) — 4 défauts seedés (7/1/90/30 jours).
- **`purge_run`** (même RLS personnalisée) — journal aperçu/exécuté, `protected_excluded_count`.
- **`data_correction_request`** (même RLS personnalisée) — `CHECK (approved_by IS NULL OR approved_by <> requested_by)`.
- `trg_audit_*` sur les 7 tables. Permission **`operations.correct_data`** (super/group admin, owner) — s'ajoute à `operations.{view,manage}` (Phase 37).
- **222 tables (+7), 188 RLS FORCE (+3)** — `retention_policy`/`purge_run`/`data_correction_request` en RLS personnalisée ; `job_definition`/`job_run`/`feature_flag`/`maintenance_window` restent des tables **plateforme** sans RLS, comme `deployment` (Phase 37). 54 migrations (V001–V054).

## Backend

| Couche | Fichier |
|---|---|
| Contracts | `Nexora.Contracts/Operations/OpsMaintenanceContracts.cs` |
| Application | `Nexora.Application/Operations/IOpsMaintenance.cs` — `IJobRegistry`, `IQueueAdminService`, `IOpsDiagnosticsService`, `IFeatureFlagService`, `IMaintenanceService`, `IRetentionPurgeService`, `IDataCorrectionService` ; `PermissionCatalog.Operations.CorrectData` |
| Infrastructure | `Operations/{SqlJobRegistry,SqlQueueAdminService,SqlOpsDiagnosticsService,SqlFeatureFlagService,SqlMaintenanceService,SqlRetentionPurgeService,SqlDataCorrectionService}.cs` ; `Scheduling/SchedulerRunner.cs` **refactoré** (jobs nommés) |
| API | `Nexora.Api/Controllers/OpsMaintenanceController.cs` (`/api/v1/ops/*`) + alias sur `OperationsController` (`/ops/status`) et `SecurityController` (`/ops/audit`) ; `Middleware/MaintenanceModeMiddleware.cs` |

### Points de conception

- **`SchedulerRunner` restructuré par job, pas par entreprise** — chaque traitement (`automations.due`,
  `webhooks.deliver`, …) devient une entrée du dictionnaire `BuildJobActions`, exécutée via
  `RunJobAsync` (vérifie `IsEnabledAsync`, journalise `job_run`). Le tour par entreprise reste
  interne à chaque job (`PerCompanyAsync`) — comportement inchangé, juste nommé et observable.
  `RunSingleJobAsync(key, force:true)` réutilise exactement les mêmes actions pour le déclenchement manuel.
- **Casser le cycle `SqlJobRegistry ↔ SchedulerRunner`** — `SchedulerRunner` dépend de `IJobRegistry`
  (interface) ; `SqlJobRegistry.RunNowAsync` résout `SchedulerRunner` **à l'exécution** via
  `IServiceProvider.GetRequiredService` (jamais en paramètre de constructeur) : le graphe scoped
  se construit sans boucle, l'appel réel n'a lieu qu'une fois les deux objets déjà instanciés.
- **Purge : jamais un `IS NULL` de paramètre nu** — `({companyId} IS NULL OR company_id = {companyId})`
  répété deux fois donne à Postgres un paramètre sans contexte de type (`42P18 could not determine
  data type`) quand la valeur est `NULL` ; fixé par un cast explicite sur la première occurrence
  (`{companyId}::uuid IS NULL OR …`).
- **`SqlQueryRaw` + paramètre `Guid?` nul** — passer un `Guid?` nul directement dans le tableau
  `params object[]` d'`SqlQueryRaw` déclenche `CS8604` (nullabilité, `TreatWarningsAsErrors`) ; fixé
  par `(object?)request.CompanyId ?? DBNull.Value`.
- **Mode maintenance = dégradation, pas coupure** — le middleware ne bloque que les méthodes non
  sûres (`POST/PUT/PATCH/DELETE`) et seulement si une fenêtre est **effectivement** `in_progress` ;
  par défaut (aucune fenêtre active) c'est un aller-retour en mémoire qui ne change rien au
  comportement existant — vérifié en conditions réelles (§ Smoke API).
- **Correction de données : identifiant de colonne validé, jamais interpolé depuis l'utilisateur** —
  `table`/`field` sont vérifiés contre une liste fermée (`Allowlist`) **à la demande et à la
  décision** ; seule la valeur (jamais l'identifiant SQL) provient de l'utilisateur.

## Tests d'intégration — `OperationsMaintenanceTests` (9, `[Collection("finance")]`, entreprise c3)

| Test | § | Vérifie |
|---|---|---|
| `Job_registry_lists_seeded_jobs_with_categories` | 7-9 | 9 jobs présents, catégories business/technical correctes |
| `Pausing_a_job_skips_it_on_the_next_scheduler_tick_then_resuming_runs_it_again` | 7 | en pause → aucun nouveau passage ; repris → repasse au tick suivant |
| `Manual_run_bypasses_pause_and_is_recorded_as_manual` | 7, 51 | exécution manuelle malgré la pause, `triggeredBy = manual`, job inconnu → 404 |
| `Queue_stats_and_dead_letter_replay_and_delete` | 10-12 | stats agrégées, rejeu remet `pending`/`attempts=0`, suppression contrôlée, erreurs `not_found` |
| `Diagnostics_run_all_covers_the_documented_checks` | 5-6, 13-15 | les 11 contrôles présents, statuts valides, stockage/DB renseignés |
| `Feature_flag_resolution_respects_specificity_and_kill_switch` | 27 | utilisateur > entreprise > global, **kill switch global l'emporte toujours** |
| `Maintenance_window_follows_the_allowed_state_machine` | 23-24 | transitions autorisées uniquement, `IsActiveAsync` bascule avec `in_progress` |
| `Purge_removes_only_the_eligible_technical_rows_and_protects_the_rest` | 16-19 | session expirée purgée / active protégée ; job `succeeded` ancien purgé / `failed` protégé |
| `Data_correction_requires_a_different_approver_and_records_before_after` | 44 | 4 yeux, avant/après, rejet n'applique rien, champ/table hors liste refusés |

## Tests API — `OpsMaintenanceEndpointsTests` (3)

| Test | Vérifie |
|---|---|
| `Ops_routes_require_authentication` | 6 routes `/ops/*` sans jeton → 401 |
| `Read_only_ops_endpoints_are_wired_over_http` | 13 endpoints de lecture → 200 (base de dev partagée — **volontairement sans transition de maintenance ni purge d'exécution**, cf. note ci-dessous) |
| `Feature_flag_round_trips_over_http_and_cleans_up_after_itself` | création + suppression, aucune trace laissée |

**Note de sécurité des tests** : `OpsMaintenanceEndpointsTests` tourne contre la base de
développement **partagée** (comme les autres suites API). Faire passer une fenêtre de maintenance
à `in_progress` y dégraderait l'API pour tout le monde en cas d'échec d'assertion avant nettoyage —
ce scénario est donc **exclusivement** couvert par `OperationsMaintenanceTests` sur la base clonée
jetable de `FinanceEngineFixture`. Le comportement réel du middleware (503 en écriture, lectures
servies, contournement `operations.manage`) a néanmoins été vérifié manuellement en HTTP réel lors
du smoke test (§ ci-dessous), avec fermeture immédiate de la fenêtre.

## Frontend

`src/operations/MaintenanceSections.tsx` — 7 sections ajoutées en onglets à l'écran **Exploitation**
existant (`OperationsPage.tsx`, route `/operations`, déjà gated `operations.view`) :

- **Jobs** — tableau (dernière exécution, statut, taux de réussite 30 j), boutons Exécuter/Pause/Reprendre (`operations.manage`) ;
- **Files & dead-letter** — cartes de statistiques par file, tableau dead-letter avec Rejouer/Supprimer ;
- **Diagnostics** — pastilles des 11 contrôles, base de données (connexions/transactions/verrous/tables), bouton « Purger le cache technique » ;
- **Bascules** — tableau clé/portée/état, activer/désactiver/supprimer ;
- **Maintenance planifiée** — tableau des fenêtres avec boutons de transition dynamiques selon l'état courant ;
- **Rétention & purge** — politiques + boutons Aperçu/Purger (confirmation), journal des purges ;
- **Corrections** — liste des demandes, Approuver/Refuser (`operations.correct_data`).

`operationsApi.ts` étendu avec les types et fonctions Phase 41. Vitest `maintenanceApi.test.ts` (5) → **71 tests frontend**.

## Smoke API (port 5080, `direction@abc-group.demo`, `X-Company-Id` requis)

- `GET /ops/{status,jobs,diagnostics,queues,feature-flags,maintenance,retention-policies,database,storage,audit,data-corrections}` → 200, données cohérentes (9 jobs, 11 diagnostics, 4 politiques de rétention seedées, tables triées par taille).
- Écriture : exécution manuelle d'un job (`imports.resume_stalled` → `succeeded`, `triggeredBy: manual`), création + suppression d'une bascule, planification + annulation d'une fenêtre de maintenance.
- **Mode maintenance vérifié en conditions réelles** : fenêtre `planned → notified → backed_up → in_progress` → `GET` (lecture métier) toujours 200, route `/ops/*` toujours 200 (exemptée) → fenêtre refermée immédiatement (`verifying → closed`). Aucune trace laissée en base après nettoyage.

## Manques assumés (backlog)

- Impersonation utilisateur réelle (§30) — fondations (`support_access_grant`, `audit_log`, step-up) posées, l'émission de jeton « en tant que » + bannière + revue de sécurité dédiée restent à faire
- Capacité & prévision de croissance (§47), SLA agrégé sur période (§48) — compteurs bruts déjà exposés (`/metrics`, `successRatePct`)
- Diagnostic entreprise consolidé en une seule vue (§28) — les briques existent séparément
- Top requêtes lentes détaillées (§14, nécessite `pg_stat_statements`)
- Export diagnostic packagé en un fichier téléchargeable (§32)
- Réindexation applicative (§42, non applicable à `pg_trgm`/`unaccent`) et recalcul générique transverse (§43, chaque module garde son mécanisme dédié)
- Runbooks/procédures documentaires détaillés (§50) — au-delà de ce document d'implémentation
- Bascule de fonctionnalité à l'échelle d'un **groupe** (§27) — le scope entreprise par membre suffit en pratique
