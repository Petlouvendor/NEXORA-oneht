# NEXORA — Phase 18 — Architecture API .NET / ASP.NET Core : état & validation

Référence : [`phase-18-architecture-api-dotnet-aspnetcore.md`](phase-18-architecture-api-dotnet-aspnetcore.md)
· [`phase-3-architecture-securite.md`](phase-3-architecture-securite.md) (socle) ·
[`phase-16-implementation.md`](phase-16-implementation.md) (architecture).

L'API **existe déjà** : **24 contrôleurs**, **~180 endpoints** sur `/api/v1`, DTO dans
`Nexora.Contracts`, `Result`/`Error` → RFC 7807, `[RequirePermission]` deny-by-default,
`[Idempotent]`, correlation ID, rate limiting, Swagger. Cette phase valide la conformité au
document de référence, dresse l'inventaire des endpoints, et **améliore le filtre d'idempotence**
pour qu'il rejoue réellement la réponse (§12).

## Structure des projets (§4)

| Projet du modèle | Réel |
|---|---|
| `Nexora.Api` | ✅ contrôleurs + middleware + `Program.cs` |
| `Nexora.Application` | ✅ use cases + interfaces (`I*Service`) |
| `Nexora.Domain` | ✅ règles métier (`Result`/`Error`, `FinancialEvent`, `Money`, entités) |
| `Nexora.Infrastructure` | ✅ EF Core database-first + services SQL + RLS + scheduler |
| `Nexora.Contracts` | ✅ DTO publics (`record` immuables), séparés des entités |
| `Nexora.Worker` | ⚙️ **absent** : les tâches asynchrones tournent dans `Nexora.Api` via `BackgroundScheduler` (Phase 16). Un host `Worker` dédié est un choix de **déploiement** (monolithe modulaire — Phase 16 §5), pas une nécessité V1. |
| `Nexora.Tests` | ✅ `Nexora.{UnitTests, IntegrationTests, ApiTests}` |

## Correspondance spec → implémentation

| § | Exigence | État |
|---|---|---|
| 2 | Cas d'utilisation métier, DTO ≠ entités, autz serveur, validation, transactions, idempotence, erreurs structurées, correlation ID, pagination, zéro logique financière au front | ✅ tous en place ; les contrôleurs délèguent aux services (`FromResult(await service...)`) et **ne portent aucune règle financière** |
| 3, 5 | Architecture en couches, modules par domaine | ✅ un dossier par domaine dans chaque couche ; les contrôleurs sont regroupés par domaine (24) |
| 6 | Versionnement `/api/v1` | ✅ `Asp.Versioning` (`ApiVersion(1,0)`, `AssumeDefaultVersionWhenUnspecified`, `ReportApiVersions`), route `api/v{version:apiVersion}/…` |
| 7 | AuthN | ✅ `POST /api/v1/auth/login` (JWT bearer HS256, bcrypt) ; step-up (`/security/step-up`) ; révocation + déconnexion globale (`security_epoch`, `/security/sessions/revoke-all`) ; `user_session` + `sync_device`. Refresh token : non implémenté (jeton court ≤ 60 min). |
| 8 | Contexte d'accès `USER→ROLES→PERMISSIONS→SCOPE→COMPANY→RESOURCE` ; jamais `company_id` client comme preuve | ✅ `TenantResolutionMiddleware` lit les claims JWT (`nx_uid`/`nx_companies`/`nx_groups`), l'en-tête `X-Company-Id` ne fait que **sélectionner** dans l'ensemble autorisé ; RLS PostgreSQL en défense de fond ; `PermissionAuthorizationHandler` vérifie le code dans les permissions effectives de l'entreprise active |
| 9 | Middleware : correlation, authn, authz, exceptions, logs, rate limit, tenant, idempotence, audit | ✅ `CorrelationIdMiddleware`, JWT, `FallbackPolicy` deny-by-default, `ExceptionHandlingMiddleware`, `UseSerilogRequestLogging`, `UseRateLimiter` (policy `auth` 10/min/IP), `TenantResolutionMiddleware`, `[Idempotent]`, contexte d'audit via triggers DB + `tenant.UserId` |
| 10 | Validation multi-niveaux | ✅ syntaxique (`[ApiController]` + DTO + FluentValidation) ; métier / permissions / période / stock / montant / devise / statut / idempotence dans les services → `Error.Validation/Conflict/Forbidden/NotFound` |
| 11 | Erreurs `{code, message, details, correlationId}` | ✅ `ExceptionHandlingMiddleware` → **RFC 7807** `ProblemDetails` (`title` = code stable, `detail` = message sûr, `correlationId`, `status` HTTP adapté) ; `409 Conflict` distinct (`ErrorKind.Conflict`) ; détails techniques **en logs uniquement** |
| 12, 16 | Idempotence : même clé + même charge → **même réponse**, aucun double effet | ✅ **amélioré cette phase** : `IdempotencyFilter` capture le corps de réponse 2xx + le hash des arguments d'action ; rejeu → renvoie **le corps exact** + `Idempotency-Replay: true` ; clé réutilisée avec une charge différente → **422** ; un échec n'est jamais mémorisé. Sécurité de fond : `operation_uuid` UNIQUE en base (payment / stock_movement / sync_operation / payment_intent). Endpoints : `pos/checkout`, `pos/returns`, `finance/*`, `inventory/{counts.../validate, losses}`, `purchasing/*`. |
| 13-36 | Endpoints métier | ✅ voir inventaire ci-dessous ; les routes réelles regroupent parfois différemment (ex. création de vente = `POST /pos/checkout`, création d'entreprise = `POST /group/companies`) |
| 37-38 | Pagination, filtres, recherche par permissions | ⚙️ pagination/filtres/tri **dans les endpoints de reporting** (`ReportsController`, `SecurityController.audit`, `IntegrationsController`) ; recherche globale = palette front sur la navigation (Phase 13). `companyId` n'est **jamais** un bypass de scope (RLS). Un standard `page/pageSize/sort/filter` généralisé reste à formaliser. |
| 39 | Unit of Work contrôlé par l'application ; appels externes hors transaction longue | ✅ `UnitOfWork.ExecuteInTransactionAsync` (ré-entrant) ; `SalesService.CheckoutAsync` = une transaction (stock + caisse + AR + compta) ; les appels HTTP (webhooks sortants) sont faits **après** commit |
| 40-41 | Outbox + worker + consommation idempotente + retry + dead-letter | ⚙️ **partiel** : `outbound_webhook_delivery` EST un outbox de fait (statut pending/failed/exhausted, back-off, `event_id` unique) ; `automation_run` / `workflow_run` historisent. Un **outbox transactionnel générique** (`outbox_event` écrit dans la même transaction que la donnée métier) reste à ajouter pour la fiabilité « exactly-once » des notifications/intégrations. |
| 42 | Performance | ✅ traitements lourds asynchrones (scheduler), pagination reporting, requêtes SQL dédiées (`SqlKpiService`, raw SQL — pas d'ORM lazy → pas de N+1), `CancellationToken` propagé partout, timeouts `HttpClient` |
| 43 | Rate limiting | ⚙️ policy `auth` (10/min/IP) sur le login ; rate limiting par utilisateur / par endpoint / plus strict sur webhooks publics **à étendre** |
| 44 | Sécurité API | ✅ TLS (`UseHsts`/`UseHttpsRedirection` hors dev), secrets hors code (`appsettings` + `credentials_ref`), validation, authz deny-by-default, CORS restreint (`Cors:Origins`), requêtes 100 % paramétrées, `SecurityHeadersMiddleware`, `audit_log` immuable, logs Serilog sans secrets |
| 45 | OpenAPI | ⚙️ `AddSwaggerGen` + `AddApiExplorer` **en dev** ; schémas DTO auto-générés. Descriptions / exemples / codes d'erreur documentés / `[ProducesResponseType]` systématiques **à enrichir** |
| 46 | Tests API | ✅ **125 tests** : endpoint + authz + multi-tenant (isolation testée dès la Phase 3) + validation + **idempotence** (`IdempotencyFilterTests` ×4) + transactions (`FinancialEngineTests`) + webhook (`IntegrationsEcosystemTests`) + offline (`OfflineSyncTests`). Concurrence / performance / E2E navigateur : Phase 15. |
| 47 | Observabilité API | ✅ `Serilog` JSON + `UseSerilogRequestLogging` (méthode, chemin, statut, durée) + correlation ID + `X-Correlation-Id` renvoyé + health `/health` `/health/ready`. Métriques par module (OpenTelemetry) : à câbler (noté Phase 16). |
| 48 | Doc développeur | ⚙️ conventions dans `docs/phase-3/16/18` ; guide intégrateur dédié à produire |

## Inventaire des endpoints (24 contrôleurs, ~180 routes)

| Contrôleur | Base `/api/v1/…` | Endpoints clés |
|---|---|---|
| `AuthController` | `auth` | `POST login` |
| `MeController` | `me` | `GET` (sécurité + préférences + branding) |
| `CompaniesController` | `companies` | `GET` (entreprises autorisées) |
| `GroupController` | `groups/{groupId}` | plan groupe, mappings, `intercompany` (+ `confirm`, `reconcile`, `unreconciled`), `control-center`, `tasks`, **`POST companies`** (provisioning), `companies/{id}/archive` |
| `ConsolidationController` | `groups/{groupId}/consolidation` | `runs` (+ `execute`, `validate`, `publish`, `reopen`, `statements`) |
| `CatalogController` | `catalog` | `POST products` (+ `suppliers`, `prices`, `barcodes`, `reorder-rule`) |
| `InventoryController` | `inventory` | `balance`, `transfers` (+ `receive`), `counts` (+ `validate`), `losses`, `reorder-suggestions` |
| `PosController` | `pos` | `checkout`, `park`, `sales/{id}/void`, `returns`, `sessions/{open,cash,close}` |
| `PurchasingController` | `purchasing` | `orders`, `receipts`, `invoices`, `returns` |
| `FinanceController` | `finance` | `cash-sales`, `credit-sales`, `supplier-invoices`, `expenses`, `customer-payments`, `supplier-payments`, `transfers`, `bank-charges`, `cash-counts` (**tous `[Idempotent]`**) |
| `AccountingController` | `accounting` | `periods` (+ `close`, `reopen`), `journal-entries/{id}/reverse`, rapports (`trial-balance`, `general-ledger`, `income-statement`, `balance-sheet`, `aged-*`, `cash-flow-forecast`) |
| `ReportsController` / `DashboardController` | `reports` / `dashboard` | journaux de ventes, ventilations, valorisation stock, aging, budget vs réel, `export` ; Command Center, dashboards par rôle, `alerts`, `drill` |
| `SectorsController` | `sectors` | config activité/modules/terminologie, `custom-fields` (+ valeurs) |
| `RestaurantController` | `restaurant` | zones, tables, commandes, `kitchen` (+ `advance`), `recipes` |
| `SyncController` | `sync` | `batch` (push), `center`, `conflicts/{id}/resolve`, `devices` (+ `revoke`, `heartbeat`), `offline-policy` |
| `IntegrationsController` | `integrations` | connecteurs (+ `test`, `status`, `rotate-secret`), `payments/intents` (+ `callback`, `refund`, `expire`, `reconciliation`), **`webhooks/inbound/{connectorCode}` (anonyme, signé)**, `webhooks/events`, `webhooks/outbound` (+ `deliveries`, `retry`, `deliver-due`), `bank/statements` (+ `auto-match`, `validate`), `center` |
| `SecurityController` | `security` | `audit` (+ `export`), `fraud/{scan,signals,review}`, `approvals` (+ `decide`), `step-up` (+ `verify`), `sessions` (+ `revoke`, `revoke-all`), `policy` |
| `AdministrationController` | `admin` | `settings` (+ `definitions`), `roles` (+ `permissions`, `preview`, `disable`), `workflows` (+ `test`, `activate`, `evaluate`), `automations` (+ `run`, `run-due`, `runs`), `dunning-policy`, `config-versions` (+ `snapshot`, `activate`, `active-at`), `tasks` |
| `PreferencesController` | `me/preferences`, `companies/branding` | GET/PUT |
| `AskController` | `ask` | `POST` (assistant de pilotage) |

Écarts de nommage vs modèle canonique §13-36 : `POST /pos/checkout` (≡ §17 `POST /sales`),
`POST /group/companies` (≡ §13 `POST /companies`), rôles sous `/admin` (≡ §14 `/roles`),
webhooks entrants `POST /integrations/webhooks/inbound/{connectorCode}` (≡ §31
`/webhooks/{provider}/{event}`). Les contrats et sémantiques sont équivalents.

## Ce qui est livré dans cette phase

- `docs/phase-18-architecture-api-dotnet-aspnetcore.md` — spec verbatim.
- **`Nexora.Api/Security/IdempotencyFilter.cs`** — réécrit (§12, §16) :
  - hash SHA-256 des **arguments d'action** (le DTO), pas du flux brut ;
  - mise en cache du **corps de réponse 2xx** + type de contenu + statut (24 h) ;
  - rejeu : renvoie le corps **exact** via `ContentResult` + en-tête `Idempotency-Replay: true` ;
  - même clé + charge différente → **422 `idempotency_key_conflict`** ;
  - clé trop courte/longue → **400 `idempotency_key_invalid`** ;
  - un statut ≥ 300 n'est **jamais** mémorisé (la requête reste rejouable).
  - *Note* : la validation automatique du modèle (`[ApiController]`) s'exécute **avant** les
    filtres d'action ; l'exigence de l'en-tête ne s'applique donc qu'aux requêtes déjà valides.
- **`Nexora.ApiTests/IdempotencyFilterTests.cs`** — 4 tests unitaires du filtre.
- `docs/phase-18-implementation.md` — ce document.

## Vérifications exécutées

**`IdempotencyFilterTests` — 4/4** : clé absente → 400 sans exécuter l'action ; rejeu (même clé +
charge) → **même corps**, action **non ré-exécutée**, en-tête `Idempotency-Replay` ; même clé +
charge différente → 422 ; opération en échec (500) rejouée deux fois → l'action s'exécute à chaque
fois (aucun cache d'échec).

**Total backend : 125 tests verts** (16 unitaires + **6 API** + 103 intégration), build Debug **et**
Release 0 warning. DB inchangée (34 migrations, 161 tables, 149 RLS) ; `smoke_test` + démo OK.
`tsc` strict + `vite build` OK.

## Manques identifiés (Phase 15 / conception détaillée)

| Sujet | Manque |
|---|---|
| Outbox transactionnel (§40-41) | table `outbox_event` écrite dans la transaction métier + worker + dead-letter |
| `Nexora.Worker` (§4) | host de fond dédié (actuellement dans `Nexora.Api`, config-gated) |
| OpenAPI (§45) | `[ProducesResponseType]` systématiques, descriptions, exemples, schéma d'erreur documenté, portail intégrateur |
| Pagination (§37) | standard `page/pageSize/sort/filter` généralisé à toutes les listes |
| Rate limiting (§43) | policies par utilisateur / par endpoint / webhooks publics |
| Observabilité (§47) | métriques par module (OpenTelemetry), tracing distribué |
| Idempotence (§12) | cache **distribué** (aujourd'hui `IMemoryCache` — OK mono-instance) + table de persistance |
| Auth (§7) | refresh tokens (aujourd'hui jeton court sans refresh) |
| Endpoints (§35-36) | Documents (génération/PDF) et Notifications (`GET`/`read`/préférences) : services partiels, endpoints REST à exposer |

## Couverture des critères de sortie (§50)

Architecture / modules / versionnement / auth / scopes / validation / erreurs / idempotence /
endpoints métier / finance / comptabilité / consolidation / offline / webhooks / sécurité /
tests / observabilité : **✅ définis et en production**. OpenAPI : ⚙️ minimal (dev). Outbox :
⚙️ partiel (les webhooks sortants en tiennent lieu).

## Suite — Phase 19

Architecture Frontend React/TypeScript & POS : organisation des features, state management,
routing, design system technique, écrans par rôle, POS tactile, client offline. Le front existe
déjà (`frontend/nexora-web/`, React 19, `shared/ui`, `core/apiClient`, `ProtectedRoute`,
`PreferencesContext`) ; la Phase 19 en produira la spécification de référence.
