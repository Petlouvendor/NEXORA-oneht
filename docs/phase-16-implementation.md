# NEXORA — Phase 16 — Architecture technique : état & validation

Référence : [`phase-16-architecture-technique.md`](phase-16-architecture-technique.md).

Cette phase **valide l'architecture de référence** contre le code réellement produit aux
Phases 2–14. NEXORA est un **monolithe modulaire ASP.NET Core / PostgreSQL / React** :
l'architecture décrite n'était pas à concevoir *ex nihilo* — elle est **déjà en place et
testée** (113 tests avant cette phase). Ce document en fait l'inventaire, fige les décisions
techniques du §48 et liste les manques.

## Stack figée (§2, §48)

| Décision | Choix retenu |
|---|---|
| Backend | **.NET 10** / ASP.NET Core, C# — `Nexora.sln`, Central Package Management, `TreatWarningsAsErrors` |
| Frontend | **React 19 + TypeScript strict + Vite 6**, React Router 7 |
| Base de données | **PostgreSQL 18** (compatible 14+), schéma `nexora` |
| Accès données | **EF Core 10 database-first** — le schéma appartient à `database/migrations/`, aucune migration EF ; requêtes en SQL brut typé (`SqlQuery<T>` / `ExecuteSqlAsync`) |
| Moteur de migrations | scripts SQL versionnés `V001…V033` + `apply.sh` (idempotent, suivi dans `nexora.schema_migration`) + `rebuild.sh` |
| Authentification | **JWT bearer** (HS256) + bcrypt ; claims `nx_uid`/`nx_companies`/`nx_groups` ; en-têtes `X-Company-Id` / `X-Group-Scope` ; step-up par mot de passe (Phase 11) |
| Cache | `AddMemoryCache` (in-process) — usage minimal, jamais pour les soldes financiers |
| Jobs / background | `BackgroundScheduler` (`IHostedService`) + `SchedulerRunner` — **désactivé par défaut** (`Scheduler:Enabled`) |
| Stockage fichiers | référence en base (`document`, `company_branding.logo_url`) ; store objet externe **non câblé** |
| Observabilité | **Serilog** (JSON console), `CorrelationIdMiddleware`, health `/health` + `/health/ready` ; métriques/traces OpenTelemetry **non câblées** |
| CI/CD | `.github/workflows/ci.yml` (migrations+smoke / build+test+scan vuln / build front) |
| Hébergement cible | `backend/Dockerfile` + `infrastructure/docker-compose.yml` (Postgres + API) ; local, hybride, cloud |
| Offline local | moteur de synchro serveur (Phase 10) : `sync_batch` / `sync_operation` / `operation_uuid` / `sync_conflict` ; **outbox client** à réaliser dans l'app POS |
| Impression / périphériques | abstraction `integration_connector` type `pos_terminal` (Phase 12) ; **pilotes non livrés** |

## Correspondance spec → implémentation

| § | Exigence | État |
|---|---|---|
| 3-6 | Architecture générale, monolithe modulaire, couches API→Application→Domain→Infrastructure | ✅ `Nexora.{Api,Application,Domain,Infrastructure,Contracts}` — règles financières dans `Nexora.Application/Finance` (moteur d'événements), **jamais** dans les contrôleurs (`FromResult` + `DomainException`) |
| 4, 7 | Domaines métier | ✅ un dossier par domaine dans chaque couche : Identity, Organization, Catalog, Inventory, Sales, Purchasing, Finance, Accounting, Group/Consolidation, Reporting, Sectors, Sync, Security, Integrations, Preferences, Administration |
| 8 | Multi-tenant : entreprise = périmètre de sécurité | ✅ **PostgreSQL RLS `FORCE`** sur 149/161 tables + `RlsConnectionInterceptor` (`SET nexora.company_ids/group_ids/user_id`) + `TenantResolutionMiddleware` ; `nexora_app` sans `BYPASSRLS` ; jamais d'accès sur ID client seul ; **tests d'isolation** automatisés (dès la Phase 3) |
| 9 | Hiérarchie `GROUP → COMPANY → ESTABLISHMENT → LOCATION → TERMINAL` | ✅ `business_group`/`company`/`establishment`/`store`/`warehouse`/`pos_terminal` ; entreprise indépendante = `group_id NULL` ; affectations multiples via `access_scope`/`user_role` |
| 10-11 | Modèle de données, montants financiers déterministes | ✅ PK uuid stables, numéros métier séparés (`document_sequence`) ; `timestamptz` UTC ; domaine `money_amount numeric(19,4)` (**jamais float**), `quantity numeric(20,6)`, `exchange_rate_value numeric(24,12)` ; FK composites `(parent_id, company_id)` ; `account_mapping` centralise les comptes ; taux de change par transaction ; `finance.rounding_mode` paramétrable (Phase 14) |
| 12 | Comptabilité : transaction métier → événement → écriture débit/crédit équilibrée | ✅ `IFinancialEngine.HandleAsync(FinancialEvent)` → `PostingRuleSet` → `journal_entry` + `journal_entry_line` (`base_debit`/`base_credit`) ; contrainte d'équilibre **différée** (validée au COMMIT) ; source métier tracée (`reference_type`/`reference_id`) ; extourne tracée |
| 13 | Stock : mouvement → solde | ✅ `IStockLedger` **seul rédacteur** de `stock_movement` ; `stock_balance` maintenu ; lots (`lot`) + emplacements (`inventory_location`) ; concurrence contrôlée par transaction ; inventaire réconciliable |
| 14 | POS : panier local → validation serveur | ✅ `SalesService.CheckoutAsync` = **une vente atomique** (stock + caisse + créance + comptabilité) ; `operation_uuid` ; écran POS React (Phase 13 §13-14) |
| 15-16 | Offline : outbox/queue → sync engine → inbox/idempotence | ✅ côté serveur : `SyncService.PushBatchAsync` rejoue via `SalesService`, idempotence par `operation_uuid`, `sync_conflict` (jamais d'écrasement), accusé par opération |
| 16 | Idempotence : clé → contrainte unique → effet unique | ✅ `stock_movement_source_op_uk`, `payment_source_op_uk`, `payment_intent_idempotency_uk`, `webhook_event` unique `(connector_id, provider_event_id)`, `Idempotency-Key` filter API |
| 17 | Concurrence | ✅ `UnitOfWork.ExecuteInTransactionAsync` (ré-entrant), triggers de solde différés, numérotation via séquence en base, `payment_source_op_uk` empêche le double paiement |
| 18-19 | API REST : `/api/v1`, DTO ≠ entités, erreurs standardisées, correlation ID | ✅ `Nexora.Contracts` (DTO) ≠ `Nexora.Domain` ; `Result`/`Error` → `ExceptionHandlingMiddleware` → **RFC 7807** ; `2xx`/`4xx`/`409 Conflict`/`5xx` ; `X-Correlation-Id` ; pagination/filtres/tri dans les services de reporting |
| 20 | AuthN/Z : RBAC, scopes, MFA, révocation, permissions serveur | ✅ `[RequirePermission("module.action")]` → policy dynamique `perm:<code>` → `PermissionAuthorizationHandler`, **deny-by-default** `FallbackPolicy` ; scopes entreprise/site/caisse (`access_scope`) ; step-up + `security_epoch` (déconnexion globale) ; audit des actions sensibles |
| 21 | Cache | ⚙️ `AddMemoryCache` présent, usage minimal ; aucune donnée financière servie depuis un cache ; **stratégie d'invalidation formelle à définir** |
| 22 | Jobs asynchrones idempotents, observables, rejouables | ✅ `SchedulerRunner.RunOnceAsync` (par entreprise, en portée système) enchaîne `automations.RunDueAsync` + `outbound.DeliverDueAsync` + `payments.ExpireStaleAsync` — tous idempotents ; `BackgroundScheduler` (`Scheduler:Enabled`) ; rapports/exports lourds : **file dédiée à ajouter** |
| 23 | Workflow & automatisation : EVENT→RULES→CONDITION→ACTION→AUDIT | ✅ Phase 14 : `workflow` (moteur de règles `IF/THEN`), **dry-run testable** avant activation, `workflow_run` historisé, versionnement via `config_version`, `automation`/`automation_run` avec retry ; garde-fou anti-boucle : les actions au-delà de l'approbation sont **enregistrées, non ré-exécutées** |
| 24 | Intégrations : interface commune, adapter, secrets hors code, retry, webhook signé, idempotence | ✅ Phase 12 : `IIntegrationRegistry`, `integration_connector` (`credentials_ref` = pointeur coffre, **jamais** le secret), webhooks entrants **HMAC-SHA256 + dédup**, sortants signés + back-off 1/5/15/60/180 min ; **circuit breaker à ajouter** |
| 25 | Documents : génération serveur, templates versionnés, PDF, lien source, scope | ⚙️ `document` / `document_sequence` / `company_branding` (en-tête, pied, logo, format) en base ; **rendu PDF non câblé** |
| 26 | Notifications : canaux, templates, queue, retry, historique | ⚙️ `notification` (interne) livré + relances (`dunning_policy`) ; email/SMS/messagerie = connecteurs Phase 12 **définis, envoi non câblé** |
| 27 | Audit : COMMAND→AUTHZ→ACTION→AUDIT EVENT, avant/après, journal protégé | ✅ `audit_row()` sur ~30 tables sensibles, avant/après (`old_values`/`new_values`), acteur/entreprise/ressource/contexte ; **`audit_log` immuable** (trigger bloque UPDATE/DELETE, Phase 11) ; `IAuditQueryService` borné au périmètre |
| 28 | Observabilité : logs structurés, metrics, traces, health, dashboards | ⚙️ Serilog JSON + correlation ID + `/health` + `/health/ready` **livrés** ; **dashboards fonctionnels** livrés (Command Center, Centre des intégrations, Centre de synchronisation, Centre de tâches admin) ; métriques/traces OpenTelemetry + dashboard technique **à câbler** |
| 29 | Sécurité applicative | ✅ validation d'entrée (FluentValidation + DTO), requêtes paramétrées (0 concaténation SQL), TLS en prod (`UseHsts`/`UseHttpsRedirection`), `SecurityHeadersMiddleware`, `UseRateLimiter` (auth 10/min/IP), CORS restreint, erreurs RFC 7807 sans fuite |
| 30-31 | PostgreSQL : normalisé, index, contraintes, transactions, migrations versionnées, rollback, vérif post-migration | ✅ 161 tables, ~300 FK, ~370 index, ~120 triggers, RLS FORCE ; `apply.sh` incrémental + `rebuild.sh` ; `smoke_test.sql` (transaction annulée) = vérification post-migration ; **backup/réplication = exploitation** |
| 32 | Fichiers | ⚙️ références en base ; store objet séparé + hash d'intégrité **à ajouter si volume** |
| 33 | Configuration : par environnement/groupe/entreprise, secrets hors Git, versionnement des paramètres sensibles | ✅ `appsettings.{env}.json` + `setting`/`setting_definition` (périmètre system/group/company/establishment) ; `.gitignore` couvre les secrets ; `config_version` (Phase 14 §39) ; changements audités |
| 34-36 | Déploiement local / cloud / hybride | ✅ **défini** : `Dockerfile`, `docker-compose.yml` (local/cloud), moteur de synchro (hybride) ; `deployment_mode` par entreprise (`LOCAL`/`HYBRID`/`CLOUD`) ; scripts d'install et service auto-démarrable **à produire pour le poste local** |
| 37 | Arborescence projet | ✅ conforme : `backend/src/Nexora.*`, `frontend/nexora-web/src/{admin,pos,dashboard,shared,…}`, `docs/`, `database/` ; front **feature-based** (Phase 13) |
| 38-40 | Frontend : feature-based, design system, client API centralisé, permissions, routing protégé, i18n, a11y | ✅ Phase 13 : `shared/theme.css` (tokens, clair/sombre/densité), `shared/ui` (bibliothèque de composants + états), `core/apiClient` centralisé, `ProtectedRoute` + `hasPermission`, `shared/i18n` (FR + architecture prête), a11y (focus-visible, `prefers-reduced-motion`, `aria-live`) ; **Storybook + tests visuels à ajouter** |
| 39 | State management : serveur ≠ UI, panier POS local, contexte entreprise, file offline | ✅ `AuthContext` (session + entreprises), `PreferencesContext`, panier POS en état local du composant, pas de store global lourd |
| 41 | Tests techniques | ✅ **114 tests** : unitaires backend (16), API (2), intégration (96 dont isolation multi-tenant, financiers, comptables, stock, POS, offline, sécurité, intégrations, admin, planificateur) ; front `tsc` strict + `vite build` ; **E2E navigateur / charge / contract tests / tests visuels : Phase 15** |
| 42-44 | CI/CD, gestion des versions, environnements | ✅ pipeline `ci.yml` (build reproductible, tests, scan vuln) ; Git + migrations liées aux versions `V0NN` ; environnements Development/Testing/Staging/Pilot/Production **définis**, secrets/bases séparés |
| 45-46 | Scalabilité, résilience | ✅ API **stateless** (JWT), jobs découplés (scheduler), index DB, pagination ; timeouts (`HttpClient` 8 s), retries bornés (webhooks), idempotence, health checks, graceful shutdown (`BackgroundService`) ; **circuit breaker + plan de reprise formel à ajouter** |
| 47 | Architecture décisionnelle | ✅ monolithe modulaire assumé, une seule base transactionnelle, pas de microservices « par mode », priorité à la fiabilité transactionnelle et au mode local |

## Ce qui est livré dans cette phase

- `docs/phase-16-architecture-technique.md` — spec verbatim.
- **`Nexora.Infrastructure/Scheduling/SchedulerRunner.cs`** — cœur du planificateur (§22) :
  pour chaque entreprise active, en portée système, exécute les traitements asynchrones
  idempotents dus. Renvoie un `SchedulerTickResult` observable et journalise un résumé structuré.
- **`Nexora.Api/Scheduling/BackgroundScheduler.cs`** — `IHostedService` avec `PeriodicTimer`,
  **inerte tant que `Scheduler:Enabled = false`** (tests et poste de dev non impactés).
- `SqlAutomationService` : les notifications d'automatisation sont adressées au `created_by` de
  l'automatisation (et non plus au `tenant.UserId`, nul dans un contexte de fond).
- `appsettings.json` : section `Scheduler` (désactivée).
- `docs/phase-16-implementation.md` — ce document.

## Vérifications exécutées

**Test `SchedulerTests.Scheduler_tick_runs_due_automations_across_active_companies`** :
créance en retard + automatisation `dunning` échue → `RunOnceAsync` : `Companies ≥ 1`,
`AutomationsRun ≥ 1`, `Failures = 0`, notification `dunning` créée ; **second passage immédiat →
`AutomationsRun = 0`** (idempotent, l'automatisation a été replanifiée).

**Total backend : 114 tests verts** (16 unitaires + 2 API + 96 intégration), build Debug **et**
Release 0 warning. DB inchangée : 33 migrations (V001–V033), 161 tables, 149 RLS. Rebuild +
smoke + démo (rejouée 2×) OK. `tsc` strict + `vite build` OK.

## Manques identifiés (Phase 15 / conception détaillée)

| Sujet | Manque |
|---|---|
| Observabilité (§28) | métriques + traces distribuées (OpenTelemetry), dashboard technique, alertes infra |
| Documents (§25, §32) | rendu PDF serveur, templates versionnés, store objet + hash d'intégrité |
| Notifications (§26) | envoi réel email/SMS/messagerie (connecteurs définis), templates, désabonnement |
| Jobs (§22) | file dédiée aux rapports/exports/imports lourds ; worker de fond en production |
| Résilience (§24, §46) | circuit breaker sur intégrations, plan de reprise (RTO/RPO) formalisé, tests de restauration |
| Cache (§21) | stratégie d'invalidation documentée par ressource |
| Offline client (§15) | outbox/local store dans l'app POS (le serveur est prêt) |
| Périphériques (§48) | pilotes imprimante/scanner/balance/tiroir/TPE (abstraction prête) |
| Front (§40-41) | Storybook + tests visuels, E2E navigateur, tests de charge |
| Exploitation (§30, §34) | backup/réplication PostgreSQL, installeur poste local + service auto-démarrable |

## Suite — Phase 17

Modèle de données PostgreSQL : détail des tables, relations, clés, contraintes, index,
périmètres multi-entreprises, données financières / comptables / stock / POS / utilisateurs /
audit / synchronisation / consolidation. Le schéma physique existe déjà (`database/migrations/`
V001–V033) ; la Phase 17 en produira la spécification de référence.
