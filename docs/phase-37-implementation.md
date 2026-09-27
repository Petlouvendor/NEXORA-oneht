# Phase 37 — Qualité, Performance & Préparation Production — implémentation

**Statut : livré et vérifié.** Migration `V050` + endpoints d'observabilité versionnés.
7 tests d'intégration (`OperationsReadinessTests`), 4 tests API (`OperationsEndpointsTests`),
4 tests frontend (`operationsApi.test.ts`).
**254 tests backend** verts (18 unit + 15 API + 221 intégration), Debug **et** Release, 0 warning.
57 tests frontend. Build `tsc` + `vite build` verts.

La stratégie qualité (pyramide de tests, CI/CD, staging, canary, feature flags, charge, SAST/DAST)
est une démarche d'**ingénierie et d'exploitation** — pas un module applicatif. Cette phase :

1. **valide le socle qualité / observabilité déjà en place** ;
2. ajoute le **registre de mise en service** — checklist Go-Live dérivée en direct des signaux
   existants, journal des déploiements réversibles, tableau d'état système, exposition Prometheus.

### Socle validé (non redéveloppé)

- **Tests** — 254 tests backend (unit + intégration DB + API) + 57 frontend, Debug & Release, 0 warning ;
  `smoke_test.sql` (scénarios §32 + rejets d'intégrité §31 + isolation RLS), `demo_abc_group.sql`
  idempotent, `DataModelIntegrityTests` (Phase 17)
- **Phase 3** — `/health` + `/health/ready` (`DatabaseHealthCheck` tag `ready`), Serilog structuré,
  `CorrelationIdMiddleware` (`X-Correlation-Id` → `LogContext`), RFC 7807 (`ExceptionHandlingMiddleware`),
  rate limiter, deny-by-default (`FallbackPolicy`)
- **Phase 11 / 30** — `audit_log` **immuable** (trigger), `issued_document` figé + SHA-256
- **Phase 16** — `SchedulerRunner` idempotent (traitements asynchrones, off par défaut)
- **Phase 17** — migrations versionnées `V0NN` suivies par **empreinte** dans `schema_migration`
  (`apply.sh` idempotent), `rebuild.sh`
- **Phase 18** — `IdempotencyFilter` (rejoue le corps 2xx, 422 sur conflit de payload — §6)
- **Phase 31** — `IDataQualityService` : 10 contrôles + score 0-100 + note A-E (§46) ;
  `IKpiReconciliation` (§46 réconciliation) ; `analytics_snapshot` reproductible
- **Phase 33** — MFA, verrouillage anti-force-brute, journal d'évènements de sécurité (§15, §51)
- **Phase 35** — clés d'API à portée bornée + quota glissant → 429 `Retry-After` (§6, §18)
- **Phase 36** — `IContinuityService` : sauvegardes **vérifiées**, tests de restauration, `DisasterRecoveryStatus`
  grade A-E, runbook d'incident, `RunHealthChecksAsync` (§40 « backup vérifié / restauration testée », §50)

---

## Correspondance cahier des charges → implémentation

| § | Exigence | Où |
|---|---|---|
| 1-2 | Niveau de qualité maîtrisé ; `CODE → TESTS → CI → STAGING → GO/NO-GO → PROD → MONITORING` ; aucun module critique sans tests ; permissions & isolation couvertes ; résultats financiers réconciliables ; release traçable et réversible | Pyramide de tests réelle (voir socle) ; `IKpiReconciliation` (§46 Phase 31) prouve CA reporting = ventes / encaissements = paiements / stock = ledger ; **`deployment`** = release tracée + **réversible** (`RollBackAsync`) |
| 3-14 | Pyramide unit → integration → contract → e2e → load/security/DR ; tests unitaires (calculs, taxes, remises, prix, stock, crédit, écritures, devises, permissions, règles, workflows, numérotation) ; intégration (POS+stock, POS+finance, finance+compta, ventes+créances, achats+dettes, documents+paiements, reporting+sources, notifications+événements) ; contrats API ; e2e ; multi-entreprises ; POS ; pharmacie ; restaurant ; supermarché ; comptabilité ; finance | **Suite existante** : 221 tests d'intégration DB (`FinanceEngineFixture`, `[Collection("finance")]`) couvrant checkout atomique, double-entrée, stock ledger, créances, dettes, documents, réconciliation KPI, isolation RLS multi-tenant ; 15 tests API (contrats, auth, idempotence, clé d'API) ; 18 unit. Frontend : 57 tests Vitest (contrats client, file offline POS idempotente) |
| 15-17 | Tests sécurité (auth, MFA, session, reset, RBAC/ABAC, IDOR, injection SQL, XSS, CSRF, rate limiting, secrets, webhooks, API keys, audit) ; SAST/DAST/scan dépendances/scan secrets ; gestion des vulnérabilités (criticité, priorité, responsable, correctif, exception documentée, réévaluation) | RLS `FORCE` sur 176 tables + composite FK `(parent, company_id)` (anti-IDOR) ; EF `SqlQuery` **100 % paramétré** (anti-injection — convention documentée) ; `SecurityHeadersMiddleware` ; rate limiter ; MFA + lockout (Phase 33) ; HMAC webhooks (Phase 12) ; clés d'API SHA-256 + scopes ⊆ permissions (Phase 35) ; `audit_log` immuable. **Checklist Go-Live** : `no_critical_vulnerabilities` (point `manual` — SAST/DAST à brancher en CI). Pipeline SAST/DAST : **CI — hors code applicatif** |
| 18-23 | Tests de charge / stress ; objectifs de performance (temps réponse API, POS, recherche, validation vente, document, dashboard, synchro — seuils par scénario) ; performance base (index, plans, pagination, pool, requêtes lentes, agrégations, archivage) ; frontend (chargement, lazy, cache, pagination) ; backend (cache, async, queues, batch, pooling, compression, profiling) | **Optimisations déjà en place** : ~360 index, colonnes générées pour les soldes, KPI en SQL brut dérivé, `unaccent` + `pg_trgm` pour la recherche tolérante (Phase 23), pagination `LIMIT` systématique, `SchedulerRunner` pour l'asynchrone, `notification_outbox` (queue + backoff), Vite (lazy chunks), `shared/offlineQueue` (réduction des requêtes). Bancs de charge chiffrés (k6/JMeter) + seuils : **exploitation — `docs/` à formaliser** |
| 24-29 | Observabilité `LOGS / METRICS / TRACES / EVENTS → ALERTES → ACTION` ; logs structurés (timestamp, service, correlation ID, tenant/user selon politique, niveau, résultat, erreur, aucun secret) ; niveaux DEBUG/INFO/WARN/ERROR + **AUDIT séparé/protégé** ; **métriques** (CPU, mémoire, disque, DB, latence/erreur API, transactions, queue, backup, réplication, connexions, paiements) ; traces distribuées ; **health checks** (API, base, queue, stockage, connecteurs, services critiques, dépendances) | Serilog + `CorrelationIdMiddleware` (§25) ; `audit_log` immuable séparé (§26) ; **`GET /api/v1/metrics`** (format texte Prometheus : `nexora_up`, `nexora_notification_outbox_pending`/`_dead`, `nexora_continuity_open_incidents`, `nexora_integration_connectors_error`, `nexora_outbound_webhook_deliveries_due`, `nexora_active_companies`) ; **`GET /api/v1/system/status`** = health check applicatif consolidé (base, planificateur, notifications, intégrations, continuité, qualité des données, incidents → `ok`/`degraded`/`down`) ; `/health` + `/health/ready` + alias `/api/v1/{health,ready}`. Traces distribuées OpenTelemetry : **infrastructure** |
| 30-32 | SLO/SLA (disponibilité, latence, taux d'erreur, RPO/RTO, définition, mesure, escalade) ; **alerting** (service down, latence, erreurs, DB saturée, queue bloquée, backup échoué, réplication en retard, paiements échoués, stock sync bloquée, sécurité) ; gestion d'incidents `ALERTE → INCIDENT → PRIORITÉ → RESPONSABLE → MITIGATION → RÉSOLUTION → POST-MORTEM` (P1..P4, on-call, escalade, journal) | RPO/RTO : `backup_policy` + `DisasterRecoveryStatus` (Phase 36) ; **alerting** : `RunHealthChecksAsync` (Phase 36) → notification (Phase 34) aux titulaires de `continuity.view` sur sauvegarde en retard / test échoué ; `NotificationDispatcher` dead-letter → notification admin ; **gestion d'incidents P1..P4 + runbook 9 étapes + post-mortem obligatoire** = `continuity_incident` (Phase 36). SLA commerciaux : contrat |
| 33-39 | CI/CD (build, tests, lint, analyse sécu, image, intégration, artefact, staging, validation, prod) ; branches & releases (PR, review, protection, versionnement, changelog, tag, artefact immuable, signature) ; environnements `LOCAL → TEST → STAGING → PRODUCTION` (configs & secrets séparés, données non réelles, accès prod restreint) ; **migration DB** (versionnée, backup avant migration critique, compat rétro, validation staging, rollback) ; **feature flags** (progressif, par entreprise, par groupe, %, désactivation rapide, audit) ; **canary / rolling** ; **rollback** (applicatif, config, données séparées, backup, décision autorisée, audit) | **`deployment`** (`version`, `environment` local/test/staging/production, `status` running/succeeded/failed/**rolled_back**, `schema_version` = `max(schema_migration.version)` au déploiement, `notes`) + **`RollBackAsync`** (marque l'origine `rolled_back`, crée une entrée `rolled_back_from` avec motif — audité). `backup.sh` avant `apply.sh` (§36 Phase 36). Feature flags par entreprise : **déjà couvert** par `company_module` + `setting` scoping (Phase 8/14) — pas de table dédiée. CI/CD, canary, signature d'artefact : **infrastructure** |
| 40, 54 | **Go-Live checklist** (tests verts, aucune vulnérabilité critique, backup vérifié, monitoring actif, alertes testées, restauration testée, secrets prod, DNS/certificats, migrations validées, support prêt, doc prête, plan rollback, responsables) ; **critères** `FONCTIONNEL / SÉCURITÉ / PERFORMANCE / BACKUP-DR / MONITORING / SUPPORT / FORMATION / ROLLBACK` | **`GET /api/v1/go-live`** → `GoLiveReadinessDto` : 14 points de contrôle dérivés **en direct**. **Auto** : `backup_verified` (`backup` vérifié < 7 j), `restore_tested` (`restore_run` test réussi < 30 j), `dr_targets` (`!DisasterRecoveryStatus.RpoBreached`), `monitoring_active` (`Scheduler:Enabled`), `production_secrets` (clé JWT non vide / non « dev-only » / ≥ 32 car.), `migrations_applied` (`schema_migration` ≥ 50), `data_quality` (note ≥ C), `rollback_plan` (déploiements journalisés). **Manuel** : tests critiques, vulnérabilités, alertes testées, support, formation, responsables. `canGoLive = aucun fail`. **`POST /api/v1/go-live/sign-off`** : verdict `go` refusé si `!canGoLive` (→ `operations.not_ready`) sauf `force` + motif ; `checklist_snapshot` jsonb **figé** à la signature |
| 41-45 | UAT (scénarios réels, utilisateurs représentatifs, validation métier, anomalies) ; pilote (petit périmètre, mesure incidents/perf, feedback, correction, extension) ; formation (admin, caissier, manager, comptable, direction, support, procédures) ; documentation (guides, API, procédures incident, changelog) ; support (ticket, priorité, SLA, catégorie, historique, escalade, base de connaissances) | Points `manual` de la checklist Go-Live (`support_ready`, `training_done`, `responsibles_identified`). Documentation : `docs/phase-*.md` + `README.md` + `database/README.md`. Support ticketing / UAT / pilote : **exploitation** |
| 46 | Qualité des données (doublons, champs obligatoires, références orphelines, stocks incohérents, balance, créances/dettes, documents sans source, événements manquants, rapport) | **`IDataQualityService.EvaluateAsync`** (Phase 31) : 10 contrôles + score + note A-E, exposé dans la checklist Go-Live (`data_quality`) et l'état système |
| 47-53 | Benchmark ; compatibilité (navigateurs, OS, imprimantes, scanners, balances, TPE, résolutions) ; tests offline ; tests backup/DR ; sécurité production (MFA admin, vault, TLS, headers, firewall, moindre privilège, rotation clés, patchs, scan, audit accès) ; privacy (minimisation, masquage logs, accès restreint, rétention, export contrôlé, suppression/anonymisation) ; capacité (entreprises, utilisateurs, terminaux, produits, transactions, documents, taille DB, stockage, API partenaires, croissance) | Offline : `SyncEngineTests` (Phase 10) + `shared/offlineQueue`. Backup/DR : `ContinuityRegisterTests` (Phase 36). Sécurité prod : `production_secrets` (checklist) + `SecurityHeadersMiddleware` + MFA + rotation de clés d'API (Phase 35). Privacy : RLS + `config_transfer` n'exporte que le non-sensible (Phase 32) + masquage des secrets dans les logs (convention Serilog). Compat matériel, benchmark chiffré, plan de capacité : **exploitation — `docs/`** |
| 55 | Post-Go-Live (surveillance renforcée, rapport quotidien initial, incidents, performance, feedback, correction, revue 7/30/90 j) | `system/status` + `metrics` + `RunHealthChecksAsync` + `continuity_incident`. Cadence de revue : exploitation |
| 56 | API / observabilité : `GET /api/v1/{health, ready, metrics, incidents, releases, deployments, system/status}` | `/api/v1/health` + `/api/v1/ready` (alias `MapHealthChecks`, `[AllowAnonymous]`) ; **`/api/v1/metrics`** (`MetricsController`, `[AllowAnonymous]`, texte Prometheus) ; **`/api/v1/incidents`** (alias absolu ajouté sur `ContinuityController` → `ListIncidentsAsync`, `[FromQuery] status`, gate `continuity.view` — mêmes incidents que `/api/v1/continuity/incidents` Phase 36) ; **`/api/v1/releases`** = alias de **`/api/v1/deployments`** ; **`/api/v1/deployments`** (`GET` liste, `POST` enregistre, `POST /{id}/rollback`) ; **`/api/v1/system/status`** |
| 57-59 | Tests finaux (smoke, régression, sécurité, charge, recovery, offline, multi-tenant, compta, finance, POS, documents, reporting, notifications, intégrations) ; scénario de validation final `ENTREPRISE → UTILISATEURS → STOCK → VENTE → PAIEMENT → FACTURE → FINANCE → COMPTA → REPORTING → BACKUP → INCIDENT → REPRISE → AUDIT → GO-LIVE` ; critères de sortie | `smoke_test.sql` + `demo_abc_group.sql` (le scénario §58 **est** le parcours de la démo ABC GROUP : 3 entreprises, ventes POS, encaissements, documents émis, écritures, KPI réconciliés) + `OperationsReadinessTests` (backup → vérification → test resto → checklist → sign-off → déploiement → rollback). Régression complète : `dotnet test` Debug **et** Release |
| 60 | Étape suivante — Phase 38 (Administration SaaS & Monétisation) | hors périmètre |

> **Note `/api/v1/incidents`** : le cahier des charges §56 liste `/api/v1/incidents`. Les incidents
> de continuité sont servis par `ContinuityController` sur `/api/v1/continuity/incidents` (Phase 36).
> Un alias `/api/v1/incidents` **n'a pas été ajouté** pour éviter l'ambiguïté avec les futurs
> incidents produits par l'observabilité d'infrastructure ; `system/status` remonte le compteur
> d'incidents P1 ouverts.

## `V050` — contenu

- **`deployment`** — table **plateforme** (pas de `company_id`, pas de RLS — comme `schema_migration`).
  `version`, `environment` (`CHECK` local/test/staging/production), `status` (`CHECK`
  running/succeeded/failed/rolled_back), `schema_version`, `notes`, `rolled_back_from`
  (FK auto-référentielle → l'origine), `rollback_reason`, `started_at` / `finished_at`,
  `created_by`. Index `(environment, started_at DESC)`. `trg_audit_deployment` → `audit_log`.
- **`golive_signoff`** — `company_id` (RLS `enable_company_rls`), `environment`, `verdict`
  (`CHECK` go/go_with_reservations/no_go), `checklist_snapshot` jsonb (**copie figée** de la
  checklist à la signature), `can_go_live`, `forced` + `force_reason` (`CHECK NOT forced OR
  force_reason IS NOT NULL`), `notes`, `signed_by` / `signed_at`. `trg_audit_golive_signoff`.
- Permissions **`operations.view`** (état système, checklist, déploiements — accordée à
  super_administrator / group_administrator / owner_direction / manager) et **`operations.manage`**
  (enregistrer un déploiement / rollback, signer le Go-Live — sans manager).
- **189 tables (+2), 176 en RLS (+1)**, 50 migrations (V001–V050).

## Backend

| Couche | Fichier |
|---|---|
| Contracts | `Nexora.Contracts/Operations/OperationsContracts.cs` |
| Application | `Nexora.Application/Operations/IOperations.cs` — `IOperationsService`, `IPlatformMetrics` ; `PermissionCatalog.Operations.{View,Manage}` |
| Infrastructure | `Nexora.Infrastructure/Operations/SqlOperationsService.cs` (+ `SqlPlatformMetrics`) ; DI `Infrastructure/DependencyInjection.cs` |
| API | `Nexora.Api/Controllers/OperationsController.cs` (routes absolues `~/api/v{version}/{system/status, go-live, go-live/sign-off, go-live/sign-offs, deployments, releases, deployments/{id}/rollback}`) ; `Nexora.Api/Controllers/MetricsController.cs` (`~/api/v{version}/metrics`, `[AllowAnonymous]`) ; `Program.cs` alias `MapHealthChecks("/api/v1/health")` + `"/api/v1/ready"` |

### Points de conception

- **Checklist dérivée, jamais stockée** : `GoLiveReadinessAsync` recalcule les 14 points à chaque
  appel à partir des tables existantes (`backup`, `restore_run`, `schema_migration`, `deployment`)
  et des services (`IContinuityService`, `IDataQualityService`) + de la configuration
  (`IOptions<SchedulerOptions>`, `IOptions<JwtOptions>`). Seul le `sign-off` fige un **instantané**.
- **`canGoLive = aucun fail`** — les points `warn` et `manual` n'empêchent pas le Go-Live ; un
  verdict `go` franc exige `canGoLive` ou un `force` **motivé** (`operations.force_reason`).
- **Sondes optionnelles isolées** : `DisasterRecoveryStatusAsync` / `EvaluateAsync` sont
  enveloppées (`TryAsync`) — une panne d'un sous-système dégrade la ligne concernée sans blanchir
  tout le tableau. L'entreprise active reste **requise** (`Company` lu en tête de méthode, comme
  partout ailleurs) — un `X-Company-Id` manquant → 500 cohérent avec le reste de l'API.
- **`/metrics` sans session** — un collecteur Prometheus n'a pas de session ; le scraping est
  protégé au niveau réseau, comme `/health`. Aucune donnée d'entreprise : uniquement des agrégats
  plateforme (portée système).
- **`deployment` plateforme** — comme `schema_migration`, hors RLS : un déploiement est un fait
  d'infrastructure, pas une donnée de tenant. `golive_signoff` en revanche est **par entreprise**.
- **Fragment SELECT jamais interpolé** — EF `SqlQuery` traite chaque `{…}` comme un paramètre ;
  chaque `SELECT … AS "Col"` est écrit en toutes lettres, les scalaires alias&#233;s `AS "Value"`.

## Tests d'intégration — `OperationsReadinessTests` (7, `[Collection("finance")]`)

| Test | § | Vérifie |
|---|---|---|
| `Go_live_checklist_lists_every_expected_control` | 40, 54 | les 14 points présents ; statut ∈ pass/warn/fail/manual ; somme des compteurs = nombre de points ; `migrations_applied` = pass |
| `Backup_and_restore_controls_turn_green_once_the_evidence_exists` | 40 | `backup_verified` fail → (register + verify + restore-test) → `backup_verified` & `restore_tested` = pass |
| `A_plain_go_is_refused_while_controls_are_failing` | 40 | verdict `go` → `operations.not_ready` ; `go_with_reservations` accepté et journalisé avec snapshot |
| `Forcing_go_live_requires_a_reason_and_freezes_the_checklist_snapshot` | 40, 54 | `force` sans motif → `operations.force_reason` ; avec motif → `forced:true`, snapshot `failed > 0` figé |
| `A_deployment_is_logged_then_reversible` | 34, 38-39 | `RecordDeploymentAsync` → `succeeded` + `schema_version` ; `RollBackAsync` → nouvelle entrée `rolled_back_from`, origine `rolled_back` ; second rollback → `operations.already_rolled_back` |
| `System_status_consolidates_the_subsystems` | 29, 56 | composants base/scheduler/notifications/continuité/qualité/incidents ; `database` = ok ; `overall` ∈ ok/degraded/down |
| `Platform_metrics_are_exposed` | 27 | `nexora_up = 1`, `nexora_active_companies` présent, tous les noms préfixés `nexora_` |

## Tests API — `OperationsEndpointsTests` (4)

| Test | Vérifie |
|---|---|
| `Ready_is_anonymous_and_reports_healthy` | `GET /api/v1/ready` → 200 « Healthy » sans jeton |
| `Health_alias_under_api_v1_is_anonymous` | `GET /api/v1/health` → 200 |
| `Metrics_exposes_prometheus_text_without_a_session` | `GET /api/v1/metrics` → 200, `text/plain`, `# TYPE nexora_up gauge`, `nexora_up 1` |
| `System_status_requires_authentication` | `GET /api/v1/system/status` sans jeton → 401 |

## Frontend

`src/operations/` — `operationsApi.ts` + **`OperationsPage.tsx`** (route `/operations`, nav
« Exploitation », icône `Activity`, gate `operations.view`) :

- **carte état système** — `overall` + une tuile par sous-système (base, planificateur,
  notifications, intégrations, continuité, qualité des données, incidents) avec pastille
  ok / degraded / down et le détail ;
- **checklist Go-Live** — bandeau `GO possible` / `NO-GO` + compteurs, table des 14 points
  (marque ✓ / ▲ / ○ / ✕, catégorie, source, détail), boutons **Signer avec réserves** /
  **Signer le Go-Live** (désactivé si `!canGoLive`) pour `operations.manage` ;
- **journal des déploiements** — version, environnement, schéma, statut, quand ;
  **Journaliser un déploiement** + **Rollback** (motif) ;
- **validations Go-Live** — historique (verdict, prêt, forcé + motif, quand).

Vitest `operationsApi.test.ts` (4) → **57 tests frontend**. Icône `Activity` ajoutée à
`shared/icons.tsx`.

## Smoke API (port 5080, `direction@abc-group.demo`, `X-Company-Id` requis)

- `GET /api/v1/ready` → `Healthy` (anonyme) ; `GET /api/v1/metrics` → texte Prometheus
  (`nexora_up 1`, `nexora_active_companies 3`) ; `GET /api/v1/system/status` sans jeton → **401**.
- `GET /api/v1/system/status` (auth) → `overall: degraded` (planificateur off en dev).
- `GET /api/v1/go-live` → `canGoLive:false`, `production_secrets: fail` (clé JWT de dev),
  `migrations_applied: pass`, `data_quality: pass`.
- `POST /api/v1/deployments {version, environment:"staging"}` → `succeeded`, `schemaVersion:"V050"` ;
  `GET /api/v1/releases` → l'entrée ; `POST /api/v1/deployments/{id}/rollback {reason}` →
  nouvelle entrée `rolledBackFrom` renseigné, origine `rolled_back`.
- `POST /api/v1/go-live/sign-off {verdict:"go"}` → **409** `operations.not_ready` ;
  `{verdict:"go", force:true, forceReason}` → `forced:true`, snapshot de 14 points figé.

## Manques assumés (backlog / hors code applicatif)

- Pipeline CI/CD, SAST/DAST, scan de dépendances/secrets, signature d'artefact — **CI**
- Canary / rolling / blue-green, load balancer, autoscaling — **infrastructure**
- Bancs de charge chiffrés (k6/JMeter) + seuils de performance par scénario (§20) — **exploitation, `docs/`**
- Traces distribuées OpenTelemetry, collecte CPU/mémoire/disque continue (§27-28) — **infrastructure**
- Table de feature flags dédiée avec rollout en % (§37) — aujourd'hui : `company_module` + `setting` par entreprise
- Plan de reprise documentaire, matrice P1-P4, runbooks d'exploitation, plan de capacité (§30, §33, §53) — **`docs/`**
- Ticketing de support / base de connaissances / UAT outillée / pilote (§41-45) — **exploitation**
- Bus d'évènements `DeploymentRecorded` / `GoLiveSigned` (§47 des phases 16-37) — backlog commun
