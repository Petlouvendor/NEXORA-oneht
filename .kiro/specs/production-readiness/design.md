# NEXORA — Production Readiness : Design

## Architecture générale des changements

Tous les changements sont **addifs** : aucune interface existante n'est modifiée, aucune couche n'est réorganisée. Le principe est de brancher les nouvelles fonctionnalités sur les points d'extension déjà prévus dans l'architecture.

```
┌─────────────────────────────────────────────────────────┐
│  Nexora.Api                                             │
│  ├── Program.cs          ← OTel, RateLimiting étendu   │
│  ├── Middleware/          ← SessionCache dans Tenant    │
│  ├── HealthChecks/        ← 3 nouveaux checks           │
│  └── Scheduling/          ← DistributedScheduler wrap   │
│                                                         │
│  Nexora.Infrastructure                                  │
│  ├── Identity/            ← RefreshToken, SessionCache  │
│  ├── Scheduling/          ← AdvisoryLockScheduler       │
│  └── Telemetry/           ← Métriques métier OTel       │
│                                                         │
│  database/migrations/                                   │
│  └── V078__refresh_tokens.sql                           │
└─────────────────────────────────────────────────────────┘
```

---

## REQ-01 : Scheduler distribué

### Approche : PostgreSQL Advisory Lock

PostgreSQL fournit des advisory locks applicatifs (`pg_try_advisory_lock(bigint)`) qui sont :
- Non bloquants (`pg_try_advisory_lock` retourne `false` immédiatement si le lock est pris)
- Automatiquement libérés à la fin de la session (filet de sécurité contre les crashs)
- Visibles dans `pg_locks` pour le diagnostic

La clé du lock est un `bigint` dérivé du nom de l'application :

```csharp
// Clé stable = hash déterministe de "nexora-scheduler"
private const long SchedulerLockKey = unchecked((long)0xBE57_3C48_A1D2_4F90UL);
```

### Modification de `BackgroundScheduler.cs`

Le service de fond ne change pas structurellement. Il délègue à `SchedulerRunner` via une nouvelle abstraction `IDistributedLock` :

```
BackgroundScheduler.ExecuteAsync()
  └── PostgresAdvisoryLock.TryAcquireAsync(lockKey)
       ├── acquired → SchedulerRunner.RunOnceAsync()
       │              └── lock libéré dans finally
       └── not acquired → log Debug, skip
```

### Nouveau fichier : `Infrastructure/Scheduling/PostgresAdvisoryLock.cs`

```csharp
// Acquiert le lock, exécute l'action, libère dans finally.
// Retourne false si le lock était pris (aucune exception).
public Task<bool> TryExecuteAsync(long key, Func<CancellationToken, Task> action, CancellationToken ct)
```

Utilise une connexion dédiée (pas le DbContext partagé) pour garantir que la libération du lock n'interfère pas avec une transaction en cours.

---

## REQ-02 : Cache de révocation de session

### Approche : IMemoryCache avec invalidation ciblée

`IMemoryCache` est déjà enregistré dans `Program.cs` (`builder.Services.AddMemoryCache()`). Aucune dépendance supplémentaire.

### Clé de cache et structure

```
Cache key : "session:{jti}"
Cache value : SessionValidity { IsValid: bool, EpochUtc: DateTimeOffset }
TTL : 30 secondes (configurable : Security:SessionCacheTtlSeconds)
Size limit : 10 000 entrées (IMemoryCache size-limited)
```

### Modification de `TenantResolutionMiddleware.cs`

```
TenantResolutionMiddleware.InvokeAsync()
  ├── cache.TryGetValue("session:{jti}") → hit → utilise la valeur cachée
  └── miss → query PostgreSQL → set cache → utilise la valeur
```

### Invalidation

Quand `SqlAccountService.RevokeSessionAsync` (ou `ChangePasswordAsync`) est appelé, il doit invalider les entrées de cache. Comme le service Infrastructure n'a pas accès à `IMemoryCache` directement (injection circulaire potentielle), on utilise un `ISessionCacheInvalidator` simple enregistré en Singleton :

```csharp
// Application/Abstractions/ISessionCacheInvalidator.cs
public interface ISessionCacheInvalidator
{
    void Invalidate(Guid tokenId);
    void InvalidateUser(Guid userId);  // déconnexion globale
}
```

---

## REQ-03 : Refresh Token

### Schéma de données

Migration `V078__refresh_tokens.sql` :

```sql
-- Colonne ajoutée sur user_session (déjà existante en V030/V063)
ALTER TABLE nexora.user_session
    ADD COLUMN IF NOT EXISTS refresh_token_hash  text,          -- SHA-256 hex du token
    ADD COLUMN IF NOT EXISTS refresh_expires_at  timestamptz,
    ADD COLUMN IF NOT EXISTS refresh_used_at     timestamptz;   -- rotation : null = non utilisé

CREATE UNIQUE INDEX IF NOT EXISTS user_session_refresh_hash_uk
    ON nexora.user_session(refresh_token_hash)
    WHERE refresh_token_hash IS NOT NULL;
```

### Flow complet

```
POST /auth/login
  └── LoginHandler.HandleAsync()
       ├── Vérifie credentials (existant)
       ├── Émet JWT (existant)
       ├── Génère refresh token (32 bytes → 64 hex chars)
       ├── Stocke hash(refresh) + expires dans user_session
       └── Retourne { accessToken, refreshToken, expiresIn }

POST /auth/refresh
  └── RefreshHandler.HandleAsync(refreshToken)
       ├── Cherche user_session par hash(refreshToken)
       ├── Si not found → 401 "invalid_refresh_token"
       ├── Si refresh_used_at IS NOT NULL → réutilisation détectée
       │   └── REVOKE TOUTES les sessions → 401 "refresh_token_reuse"
       ├── Si refresh_expires_at < now() → 401 "refresh_token_expired"
       ├── Émet nouveau JWT
       ├── Génère nouveau refresh token
       ├── UPDATE session : refresh_used_at = now() (atomic avec INSERT nouveau)
       └── Retourne { accessToken, refreshToken, expiresIn }
```

### Nouveau handler : `Application/UseCases/Auth/RefreshHandler.cs`

Suit le même pattern que `LoginHandler` — pure logique, pas de dépendance infrastructure.

### Nouveau contrôleur : ajout d'endpoint dans `AuthController.cs`

```
POST /api/v1/auth/refresh
Body: { refreshToken: "string" }
Response: AuthTokenResponse (existant, + refreshToken)
```

### Modification frontend : `auth/AuthContext.tsx`

```typescript
// Intercepteur sur apiClient.ts : sur 401, tente refresh avant de déconnecter
// Si refresh réussit → rejoue la requête originale
// Si refresh échoue → redirige vers /login
```

---

## REQ-04 : Rate Limiting étendu

### Approche : policies nommées dans `Program.cs`

ASP.NET Core Rate Limiting (System.Threading.RateLimiting) est déjà configuré. On ajoute 5 nouvelles policies en `SlidingWindow` (plus juste que `FixedWindow` pour les bursts) :

```csharp
options.AddSlidingWindowLimiter("pos-checkout", o => {
    o.PermitLimit = config.GetValue("RateLimiting:PosCheckout:Limit", 60);
    o.Window = TimeSpan.FromMinutes(1);
    o.SegmentsPerWindow = 6;  // fenêtre de 10s
});
// ... idem pour heavy-compute, export, import, setup
```

La clé de partition est l'IP + UserId (si authentifié) pour éviter qu'un utilisateur saturé ne bloque toute l'IP partagée (NAT d'entreprise) :

```csharp
ctx.Connection.RemoteIpAddress?.ToString() + ":" +
    (ctx.User.FindFirstValue(NexoraClaims.UserId) ?? "anon")
```

### Configuration centralisée

```json
// appsettings.json
"RateLimiting": {
  "PosCheckout":   { "PermitLimit": 60,  "WindowMinutes": 1 },
  "HeavyCompute":  { "PermitLimit": 10,  "WindowMinutes": 1 },
  "Export":        { "PermitLimit": 20,  "WindowMinutes": 1 },
  "Import":        { "PermitLimit": 5,   "WindowMinutes": 1 },
  "Setup":         { "PermitLimit": 5,   "WindowMinutes": 1 }
}
```

### Decoration des controllers

```csharp
// PosController.cs
[HttpPost("checkout")]
[EnableRateLimiting("pos-checkout")]
public Task<IActionResult> Checkout(...)

// PayrollController.cs
[HttpPost("{periodId}/calculate")]
[EnableRateLimiting("heavy-compute")]
public Task<IActionResult> Calculate(...)
```

### Réponse normalisée 429

```csharp
options.OnRejected = async (ctx, ct) => {
    ctx.HttpContext.Response.StatusCode = 429;
    ctx.HttpContext.Response.Headers.RetryAfter = "60";
    await ctx.HttpContext.Response.WriteAsJsonAsync(new {
        title = "rate_limit_exceeded",
        detail = $"Trop de requêtes. Réessayez dans {retryAfter} secondes.",
        retryAfterSeconds = retryAfter,
        correlationId = ctx.HttpContext.TraceIdentifier,
    }, ct);
};
```

---

## REQ-05 : Health Checks améliorés

### Structure des 3 nouveaux checks

```
/health/ready
  ├── database        : SELECT count(*) FROM nexora.app_user LIMIT 1
  ├── schema-version  : lit la migration la plus récente
  └── scheduler       : last_run_at dans job_run, compare à now()
```

### `SchemaVersionHealthCheck`

```csharp
// Lit depuis nexora.job_run ou une table dédiée la version de migration
// Retourne: { version: "V077", appliedAt: "2024-..." }
// Unhealthy si: la table nexora.app_user n'existe pas (schéma non initialisé)
```

### `SchedulerHealthCheck`

```csharp
// Si Scheduler:Enabled = false → Healthy avec data: { enabled: false }
// Si Scheduler:Enabled = true → cherche le dernier job_run
//   last < now() - (5 * interval) → Unhealthy
//   sinon → Healthy avec data: { lastRun, secondsAgo }
```

### Réponse JSON enrichie

ASP.NET Core health checks retournent du JSON via `ResponseWriter` personnalisé :

```json
{
  "status": "Healthy",
  "totalDuration": "00:00:00.042",
  "entries": {
    "database": { "status": "Healthy", "duration": "00:00:00.035", "data": { "rowCount": 3 } },
    "schema-version": { "status": "Healthy", "data": { "version": "V077" } },
    "scheduler": { "status": "Healthy", "data": { "enabled": true, "secondsAgo": 58 } }
  }
}
```

---

## REQ-06 : OpenTelemetry

### Packages à ajouter (`Directory.Packages.props`)

```xml
<PackageVersion Include="OpenTelemetry.Extensions.Hosting" Version="1.12.0" />
<PackageVersion Include="OpenTelemetry.Instrumentation.AspNetCore" Version="1.12.0" />
<PackageVersion Include="OpenTelemetry.Instrumentation.EntityFrameworkCore" Version="1.12.0" />
<PackageVersion Include="OpenTelemetry.Instrumentation.Http" Version="1.12.0" />
<PackageVersion Include="OpenTelemetry.Exporter.OpenTelemetryProtocol" Version="1.12.0" />
<PackageVersion Include="OpenTelemetry.Exporter.Prometheus.AspNetCore" Version="1.12.0-rc.2" />
```

### Configuration dans `Program.cs`

```csharp
builder.Services.AddOpenTelemetry()
    .WithTracing(tracing => tracing
        .AddAspNetCoreInstrumentation()
        .AddEntityFrameworkCoreInstrumentation()
        .AddHttpClientInstrumentation()
        .AddSource("Nexora.*")
        .AddOtlpExporter(o => {
            var ep = builder.Configuration["OpenTelemetry:Endpoint"];
            if (!string.IsNullOrWhiteSpace(ep)) o.Endpoint = new Uri(ep);
        }))
    .WithMetrics(metrics => metrics
        .AddAspNetCoreInstrumentation()
        .AddRuntimeInstrumentation()
        .AddMeter("Nexora.Api")
        .AddPrometheusExporter());

app.MapPrometheusScrapingEndpoint("/metrics").AllowAnonymous();
```

### `Infrastructure/Telemetry/NexoraMetrics.cs`

```csharp
public static class NexoraMetrics
{
    private static readonly Meter Meter = new("Nexora.Api", "1.0");

    public static readonly Counter<long> CheckoutTotal =
        Meter.CreateCounter<long>("nexora.checkout.total", "sale", "Ventes POS");

    public static readonly Histogram<double> PayrollDuration =
        Meter.CreateHistogram<double>("nexora.payroll.calculation.duration", "ms");

    public static readonly Histogram<double> SchedulerTickDuration =
        Meter.CreateHistogram<double>("nexora.scheduler.tick.duration", "ms");

    public static readonly Counter<long> NotificationDispatched =
        Meter.CreateCounter<long>("nexora.notification.dispatched", "notification", "{canal}");
}
```

### Points d'instrumentation

- `SalesService.CheckoutAsync` → `NexoraMetrics.CheckoutTotal.Add(1, tag status)`
- `SqlPayrollService.CalculateAsync` → `NexoraMetrics.PayrollDuration.Record(ms)`
- `BackgroundScheduler.ExecuteAsync` → `NexoraMetrics.SchedulerTickDuration.Record(ms)`
- `NotificationDispatcher.DispatchDueAsync` → `NexoraMetrics.NotificationDispatched.Add(handled, tag channel)`

### Configuration `appsettings.json`

```json
"OpenTelemetry": {
  "Endpoint": "",
  "ServiceName": "nexora-api",
  "ServiceVersion": "1.0.0"
}
```

---

## Migration SQL : V078__refresh_tokens.sql

```sql
-- Refresh tokens sur la table user_session existante (V030/V063)
-- Aucune nouvelle table — on étend user_session.

ALTER TABLE nexora.user_session
    ADD COLUMN IF NOT EXISTS refresh_token_hash  text,
    ADD COLUMN IF NOT EXISTS refresh_expires_at  timestamptz,
    ADD COLUMN IF NOT EXISTS refresh_used_at     timestamptz;

CREATE UNIQUE INDEX IF NOT EXISTS user_session_refresh_hash_uk
    ON nexora.user_session(refresh_token_hash)
    WHERE refresh_token_hash IS NOT NULL;

COMMENT ON COLUMN nexora.user_session.refresh_token_hash IS
    'SHA-256 hex du refresh token. NULL = session sans refresh (API Key, sessions legacy).';
COMMENT ON COLUMN nexora.user_session.refresh_expires_at IS
    'Expiration du refresh token (Jwt:RefreshTokenDays à partir de l''émission).';
COMMENT ON COLUMN nexora.user_session.refresh_used_at IS
    'Horodatage de la rotation. NOT NULL = token déjà utilisé (détection de réutilisation).';
```

---

## Fichiers modifiés / créés — récapitulatif

| Fichier | Action | REQ |
|---|---|---|
| `database/migrations/V078__refresh_tokens.sql` | Créer | REQ-03 |
| `backend/Directory.Packages.props` | Modifier — OTel packages | REQ-06 |
| `backend/src/Nexora.Api/Program.cs` | Modifier — OTel, RateLimiting | REQ-04, REQ-06 |
| `backend/src/Nexora.Api/appsettings.json` | Modifier — sections RateLimiting, OTel | REQ-04, REQ-06 |
| `backend/src/Nexora.Api/Scheduling/BackgroundScheduler.cs` | Modifier — advisory lock | REQ-01 |
| `backend/src/Nexora.Api/HealthChecks/DatabaseHealthCheck.cs` | Modifier — query fonctionnelle | REQ-05 |
| `backend/src/Nexora.Api/HealthChecks/SchemaVersionHealthCheck.cs` | Créer | REQ-05 |
| `backend/src/Nexora.Api/HealthChecks/SchedulerHealthCheck.cs` | Créer | REQ-05 |
| `backend/src/Nexora.Api/Controllers/AuthController.cs` | Modifier — endpoint /refresh | REQ-03 |
| `backend/src/Nexora.Api/Controllers/PosController.cs` | Modifier — [EnableRateLimiting] | REQ-04 |
| `backend/src/Nexora.Api/Controllers/PayrollController.cs` | Modifier — [EnableRateLimiting] | REQ-04 |
| `backend/src/Nexora.Api/Controllers/BiController.cs` | Modifier — [EnableRateLimiting] | REQ-04 |
| `backend/src/Nexora.Api/Controllers/MigrationController.cs` | Modifier — [EnableRateLimiting] | REQ-04 |
| `backend/src/Nexora.Api/Controllers/SetupController.cs` | Modifier — [EnableRateLimiting] | REQ-04 |
| `backend/src/Nexora.Api/Middleware/TenantResolutionMiddleware.cs` | Modifier — cache hit/miss | REQ-02 |
| `backend/src/Nexora.Application/Abstractions/ISessionCacheInvalidator.cs` | Créer | REQ-02 |
| `backend/src/Nexora.Application/UseCases/Auth/RefreshHandler.cs` | Créer | REQ-03 |
| `backend/src/Nexora.Infrastructure/Identity/SessionCacheInvalidator.cs` | Créer | REQ-02 |
| `backend/src/Nexora.Infrastructure/Scheduling/PostgresAdvisoryLock.cs` | Créer | REQ-01 |
| `backend/src/Nexora.Infrastructure/Telemetry/NexoraMetrics.cs` | Créer | REQ-06 |
| `backend/src/Nexora.Application/Sales/SalesService.cs` | Modifier — métriques | REQ-06 |
| `backend/src/Nexora.Infrastructure/Payroll/SqlPayrollService.Calculation.cs` | Modifier — métriques | REQ-06 |
| `backend/src/Nexora.Infrastructure/Notifications/NotificationDispatcher.cs` | Modifier — métriques | REQ-06 |
| `backend/src/Nexora.Infrastructure/DependencyInjection.cs` | Modifier — ISessionCacheInvalidator | REQ-02 |
| `frontend/nexora-web/src/core/apiClient.ts` | Modifier — intercepteur refresh | REQ-03 |
| `frontend/nexora-web/src/auth/AuthContext.tsx` | Modifier — refresh token storage | REQ-03 |
| `frontend/nexora-web/src/auth/accountApi.ts` | Modifier — POST /auth/refresh | REQ-03 |
| `RUNBOOK.md` | Créer | REQ-NF-05 |
