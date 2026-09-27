# NEXORA — Backend (ASP.NET Core)

Monolithe modulaire, Clean Architecture. Cible **.NET 10**. Voir
[`docs/phase-3-architecture-securite.md`](../docs/phase-3-architecture-securite.md).

## Projets (Phase 3 §5)

| Projet | Rôle | Dépend de |
|---|---|---|
| `Nexora.Domain` | Entités, invariants, `Result`/`Error` | — |
| `Nexora.Contracts` | DTO d'API (requêtes / réponses) | — |
| `Nexora.Application` | Cas d'usage, abstractions (`ITenantContext`, `IUnitOfWork`, `IPermissionService`…) | Domain, Contracts |
| `Nexora.Infrastructure` | EF Core / PostgreSQL, RLS, JWT, hashing, repositories | Application, Domain, Contracts |
| `Nexora.Api` | Endpoints, middleware, sécurité, composition | Application, Infrastructure, Contracts |
| `tests/Nexora.UnitTests` | Domaine + application (xUnit, NSubstitute) | Domain, Application, Infrastructure |
| `tests/Nexora.IntegrationTests` | Base de données (Testcontainers) — Phase 4 | Infrastructure |
| `tests/Nexora.ApiTests` | Endpoints (`WebApplicationFactory`) | Api |

## Démarrer en local

Prérequis : PostgreSQL avec la base `nexora` migrée (voir [`database/`](../database/)) et le
rôle applicatif configuré :

```sql
ALTER ROLE nexora_app WITH PASSWORD 'nexora_app_dev';
```

```bash
cd backend
dotnet restore Nexora.sln
dotnet build   Nexora.sln
dotnet run --project src/Nexora.Api        # http://localhost:5080
```

Endpoints de contrôle :

```bash
curl http://localhost:5080/health           # liveness
curl http://localhost:5080/health/ready     # readiness (inclut PostgreSQL)
# Swagger : http://localhost:5080/swagger  (Development)
```

Connexion de démo (`database/seeds/demo_abc_group.sql`, mot de passe `Nexora!2026`) :

```bash
curl -X POST http://localhost:5080/api/v1/auth/login \
  -H 'Content-Type: application/json' \
  -d '{"email":"direction@abc-group.demo","password":"Nexora!2026"}'

curl http://localhost:5080/api/v1/companies \
  -H "Authorization: Bearer <token>" \
  -H "X-Company-Id: <company-uuid>"
```

## Sécurité — comment c'est câblé (Phase 3 §9-15)

| Préoccupation | Implémentation |
|---|---|
| Authentification | JWT Bearer ; mots de passe **bcrypt** (`BcryptPasswordHasher`) ; claims `nx_uid`, `nx_companies`, `nx_groups` |
| Autorisation | `[RequirePermission("module.action")]` → politique dynamique → `PermissionAuthorizationHandler` ; **deny-by-default** via `FallbackPolicy` |
| Multi-tenant | `TenantResolutionMiddleware` lit le jeton + `X-Company-Id` / `X-Group-Scope` ; le `company_id` du client ne fait que *sélectionner* dans le périmètre du jeton |
| Isolation données | `RlsConnectionInterceptor` pose `SET nexora.company_ids / group_ids / user_id` à chaque ouverture de connexion → **RLS PostgreSQL** applique le filtre |
| Portée système | `ITenantContext.BeginSystemScope()` → `SET nexora.bypass_rls='on'` pour la résolution d'identité (IAM) uniquement |
| Transactions | `IUnitOfWork.ExecuteInTransactionAsync` — rollback complet, stratégie de retry Npgsql (§15) |
| Idempotence | `[Idempotent]` + en-tête `Idempotency-Key` (§8) |
| Erreurs | `ExceptionHandlingMiddleware` → `ProblemDetails` RFC 7807 + `correlationId` |
| Traçabilité | `CorrelationIdMiddleware` (`X-Correlation-Id`), Serilog enrichi |
| En-têtes | `SecurityHeadersMiddleware` (nosniff, DENY, Referrer-Policy…) |
| Débit | Rate limiting `auth` (10 req/min/IP) |
| Secrets | `Jwt:SigningKey`, `Database:ConnectionString` via env / user-secrets — **jamais** dans Git (§18) |

## Accès aux données

EF Core en **mode base d'abord** : le schéma appartient à `database/migrations/` (SQL).
Aucune migration EF n'est générée. `NexoraDbContext` mappe les tables existantes du schéma
`nexora`. Les requêtes métier ne filtrent pas sur `company_id` — la RLS s'en charge.

## Tests

```bash
dotnet test Nexora.sln --filter "FullyQualifiedName!~IntegrationTests"
```

Les tests d'intégration base (RLS, triggers financiers, scénarios §32) arrivent en Phase 4
avec Testcontainers.
