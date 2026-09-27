# NEXORA — Production Readiness : Tâches d'implémentation

## Ordre d'exécution

Les tâches sont ordonnées du moins au plus risqué, avec les dépendances explicites. Chaque tâche est indépendante et testable isolément. Commencer par la fondation (migration + DI), puis les fonctionnalités critiques.

---

## GROUPE 1 — Fondation (base de données + DI)

### Tâche 1 — Migration SQL V078 : Refresh Tokens
**Fichier** : `database/migrations/V078__refresh_tokens.sql`

Créer la migration qui étend `nexora.user_session` avec les colonnes refresh token :
- `refresh_token_hash TEXT` — SHA-256 hex du token opaque
- `refresh_expires_at TIMESTAMPTZ` — expiration du refresh token
- `refresh_used_at TIMESTAMPTZ` — horodatage de rotation (non-null = déjà utilisé)
- Index unique sur `refresh_token_hash WHERE NOT NULL`
- Commentaires de colonnes complets
- La migration doit être idempotente (`ADD COLUMN IF NOT EXISTS`, `CREATE INDEX IF NOT EXISTS`)

**Critère de validation** : `psql -f V078__refresh_tokens.sql` passe deux fois sans erreur. `\d nexora.user_session` montre les 3 nouvelles colonnes.

---

### Tâche 2 — Interface ISessionCacheInvalidator
**Fichier** : `backend/src/Nexora.Application/Abstractions/ISessionCacheInvalidator.cs`

Créer l'interface :
```csharp
public interface ISessionCacheInvalidator
{
    void Invalidate(Guid tokenId);       // révocation d'une session spécifique
    void InvalidateUser(Guid userId);   // déconnexion globale — invalide toutes les sessions de l'utilisateur
}
```

**Critère de validation** : fichier compile, interface dans le namespace `Nexora.Application.Abstractions`.

---

### Tâche 3 — Implémentation SessionCacheInvalidator
**Fichier** : `backend/src/Nexora.Infrastructure/Identity/SessionCacheInvalidator.cs`

Implémenter `ISessionCacheInvalidator` avec `IMemoryCache` :
- `Invalidate(tokenId)` : retire l'entrée `"session:{tokenId}"` du cache
- `InvalidateUser(userId)` : stocke une entrée `"user-revoked:{userId}"` avec TTL de 60 secondes (utilisée par le middleware pour court-circuiter le cache de session de cet utilisateur)
- Enregistrer en Singleton dans `DependencyInjection.cs`

**Critère de validation** : test unitaire vérifie que `Invalidate` appelle `cache.Remove` avec la bonne clé.

---

## GROUPE 2 — REQ-01 : Scheduler distribué

### Tâche 4 — PostgresAdvisoryLock
**Fichier** : `backend/src/Nexora.Infrastructure/Scheduling/PostgresAdvisoryLock.cs`

Créer la classe avec une connexion Npgsql dédiée (pas le DbContext EF) :

```csharp
public sealed class PostgresAdvisoryLock(NexoraDbOptions dbOptions)
{
    private const long SchedulerLockKey = unchecked((long)0xBE57_3C48_A1D2_4F90UL);

    // Tente d'acquérir le lock, exécute action(), libère dans finally.
    // Retourne true si exécuté, false si lock non disponible.
    public async Task<bool> TryExecuteAsync(
        Func<CancellationToken, Task> action, CancellationToken ct);
}
```

Logique interne :
- Ouvre une connexion Npgsql (chaîne depuis `NexoraDbOptions.ConnectionString`)
- Exécute `SELECT pg_try_advisory_lock($1)` avec `SchedulerLockKey`
- Si `false` → ferme la connexion et retourne `false`
- Si `true` → exécute `action(ct)` dans un try/finally
- Finally : `SELECT pg_advisory_unlock($1)`, ferme la connexion

Gérer le cas où PostgreSQL est injoignable : catch + log Warning + retourner `false` (ne pas crasher).

Enregistrer en Singleton dans `DependencyInjection.cs`.

**Critère de validation** : test d'intégration avec deux appels concurrents — vérifier qu'un seul s'exécute.

---

### Tâche 5 — Modification BackgroundScheduler
**Fichier** : `backend/src/Nexora.Api/Scheduling/BackgroundScheduler.cs`

Injecter `PostgresAdvisoryLock` et envelopper `runner.RunOnceAsync` :

```csharp
while (!stoppingToken.IsCancellationRequested && await timer.WaitForNextTickAsync(stoppingToken))
{
    try
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var runner = scope.ServiceProvider.GetRequiredService<SchedulerRunner>();
        var advisoryLock = scope.ServiceProvider.GetRequiredService<PostgresAdvisoryLock>();
        var sw = Stopwatch.StartNew();
        var executed = await advisoryLock.TryExecuteAsync(runner.RunOnceAsync, stoppingToken);
        if (!executed)
            logger.LogDebug("Planificateur : lock non disponible (autre instance en cours) — passage ignoré.");
        else
            NexoraMetrics.SchedulerTickDuration.Record(sw.ElapsedMilliseconds);
    }
    // ... gestion d'erreurs existante
}
```

**Critère de validation** : test d'intégration `SchedulerTests.cs` — vérifier que deux instances simulées n'exécutent pas le runner deux fois.

---

## GROUPE 3 — REQ-02 : Cache de révocation de session

### Tâche 6 — Cache dans TenantResolutionMiddleware
**Fichier** : `backend/src/Nexora.Api/Middleware/TenantResolutionMiddleware.cs`

Modifier le check de révocation de session pour passer par le cache :

```csharp
// Avant la query SQL, vérifier le cache :
var cacheKey = $"session:{tokenId}";
if (cache.TryGetValue<SessionValidity>(cacheKey, out var cached))
{
    // utiliser cached.IsValid, cached.Epoch
}
else
{
    // query PostgreSQL existante
    // stocker le résultat dans le cache avec options de taille et TTL
    cache.Set(cacheKey, new SessionValidity(isValid, epoch), new MemoryCacheEntryOptions
    {
        AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(cacheTtl),
        Size = 1,
    });
}
```

Ajouter `ISessionCacheInvalidator` et `IMemoryCache` aux dépendances du middleware.

Configurer `IMemoryCache` avec `SizeLimit = 10_000` dans `Program.cs` :
```csharp
builder.Services.AddMemoryCache(o => o.SizeLimit = 10_000);
```

**Critère de validation** : test de performance dans `IdentityAccessTests.cs` — 100 requêtes consécutives avec le même token génèrent ≤ 2 queries SQL (première + une de refresh à 30s).

---

### Tâche 7 — Appel d'invalidation dans SqlAccountService et SqlMfaService

Dans les méthodes de révocation existantes, appeler `ISessionCacheInvalidator` :
- `SqlAccountService.ChangePasswordAsync` → après révocation des sessions, appeler `invalidator.InvalidateUser(userId)`
- `SqlAccountService.ResetPasswordAsync` → idem
- Les services de révocation de session existants → appeler `invalidator.Invalidate(tokenId)`

**Critère de validation** : test dans `RecetteCorrectionsTests.cs` — changer le mot de passe puis immédiatement appeler un endpoint protégé avec l'ancien token → 401.

---

## GROUPE 4 — REQ-03 : Refresh Token

### Tâche 8 — Modification des contrats Auth
**Fichier** : `backend/src/Nexora.Contracts/Auth/AuthContracts.cs`

Ajouter à `AuthTokenResponse` :
```csharp
public string? RefreshToken { get; init; }
public int RefreshExpiresInDays { get; init; }
```

Ajouter le record de requête :
```csharp
public sealed record RefreshRequest(string RefreshToken);
```

**Critère de validation** : compile sans warning.

---

### Tâche 9 — RefreshHandler
**Fichier** : `backend/src/Nexora.Application/UseCases/Auth/RefreshHandler.cs`

Créer le handler avec l'interface `IUserRepository` étendue. Le handler doit :
1. Calculer le hash SHA-256 du refresh token reçu
2. Appeler `users.FindSessionByRefreshHashAsync(hash)` → retourne `(UserId, TokenId, ExpiresAt, UsedAt, UserStatus, Epoch)`
3. Si not found → `Error.Unauthorized("auth.invalid_refresh_token", ...)`
4. Si `UsedAt IS NOT NULL` → appeler `users.RevokeAllSessionsAsync(userId)` → `Error.Unauthorized("auth.refresh_token_reuse", ...)`
5. Si `ExpiresAt < now()` → `Error.Unauthorized("auth.refresh_token_expired", ...)`
6. Si `UserStatus != "active"` → `Error.Unauthorized("auth.account_inactive", ...)`
7. Générer nouveau refresh token (`RandomNumberGenerator.GetBytes(32)`)
8. Appeler `users.RotateRefreshTokenAsync(oldTokenId, newHash, newExpires)` — atomique
9. Charger le profil, émettre nouveau JWT via `AccessTokenIssuer`
10. Retourner `Result<AuthTokenResponse>` avec les deux tokens

Enregistrer dans `Application/DependencyInjection.cs`.

**Critère de validation** : test unitaire couvre les 5 cas d'erreur + le cas nominal.

---

### Tâche 10 — Méthodes repository pour refresh tokens
**Fichier** : `backend/src/Nexora.Application/Identity/IUserRepository.cs`

Ajouter les signatures :
```csharp
Task<RefreshSessionRow?> FindSessionByRefreshHashAsync(string hash, CancellationToken ct);
Task RotateRefreshTokenAsync(Guid oldTokenId, string newHash, DateTimeOffset newExpires, CancellationToken ct);
Task<int> RevokeAllSessionsAsync(Guid userId, CancellationToken ct);
```

Implémenter dans `UserRepository.cs` (Infrastructure) :
- `FindSession` : `SELECT ... FROM nexora.user_session WHERE refresh_token_hash = $1 FOR UPDATE`
- `Rotate` : UPDATE ancien + INSERT nouveau dans la même transaction via `UnitOfWork`
- `RevokeAll` : UPDATE toutes les sessions de l'utilisateur

**Critère de validation** : test d'intégration `MfaAuthenticationTests.cs` étendu — login → refresh → refresh avec ancien token → 401 + toutes les sessions révoquées.

---

### Tâche 11 — Modification LoginHandler pour émettre le refresh token
**Fichier** : `backend/src/Nexora.Application/UseCases/Auth/LoginHandler.cs`

Après l'émission du JWT (ligne existante `var response = await issuer.IssueAsync(...)`), générer et stocker un refresh token :

```csharp
var refresh = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
var refreshExpires = clock.UtcNow.AddDays(_opts.RefreshTokenDays);
await users.StoreRefreshTokenAsync(user.Id, response.User.UserId, Hash(refresh), refreshExpires, ct);
response = response with { RefreshToken = refresh, RefreshExpiresInDays = _opts.RefreshTokenDays };
```

**Critère de validation** : après login, la table `user_session` contient `refresh_token_hash` non null.

---

### Tâche 12 — Endpoint POST /auth/refresh
**Fichier** : `backend/src/Nexora.Api/Controllers/AuthController.cs`

Ajouter :
```csharp
[HttpPost("refresh")]
[AllowAnonymous]
[EnableRateLimiting("auth")]
public async Task<IActionResult> Refresh([FromBody] RefreshRequest request, ...)
{
    var result = await refreshHandler.HandleAsync(request, GetIpAddress(), ct);
    return result.IsSuccess ? Ok(result.Value) : Problem(result);
}
```

**Critère de validation** : appel `POST /api/v1/auth/refresh` avec token valide → 200 avec nouveau JWT. Appel avec token invalide → 401.

---

### Tâche 13 — Frontend : intercepteur refresh dans apiClient.ts
**Fichier** : `frontend/nexora-web/src/core/apiClient.ts`

Modifier `send()` pour intercepter les 401 :
- Si 401 reçu ET refresh token disponible ET pas déjà en train de rafraîchir → appeler `POST /auth/refresh`
- Si refresh réussit → mettre à jour le token en mémoire + rejouer la requête originale
- Si refresh échoue → appeler `onUnauthorized()` (déconnexion)

Éviter les boucles infinies : flag `isRefreshing` + file d'attente des requêtes en cours.

**Critère de validation** : test E2E manuel — laisser le JWT expirer, faire une requête → le refresh se fait automatiquement sans déconnexion visible.

---

### Tâche 14 — Frontend : stockage refresh token dans AuthContext.tsx
**Fichier** : `frontend/nexora-web/src/auth/AuthContext.tsx`

- Stocker le `refreshToken` retourné par login dans `tokenVault` (à côté du JWT)
- Exposer `getRefreshToken()` via `configureApiClient`
- À la déconnexion (`logout()`), appeler `DELETE /auth/sessions/current` avec le refresh token pour révoquer côté serveur avant de supprimer localement

**Critère de validation** : après login, `localStorage` (ou secure store) contient les deux tokens. Après logout, le refresh token est révoqué en base.

---

## GROUPE 5 — REQ-04 : Rate Limiting étendu

### Tâche 15 — Configuration RateLimiting dans appsettings.json
**Fichier** : `backend/src/Nexora.Api/appsettings.json`

Ajouter la section :
```json
"RateLimiting": {
  "PosCheckout":  { "PermitLimit": 60, "WindowMinutes": 1 },
  "HeavyCompute": { "PermitLimit": 10, "WindowMinutes": 1 },
  "Export":       { "PermitLimit": 20, "WindowMinutes": 1 },
  "Import":       { "PermitLimit": 5,  "WindowMinutes": 1 },
  "Setup":        { "PermitLimit": 5,  "WindowMinutes": 1 }
}
```

**Critère de validation** : les valeurs sont lisibles via `IConfiguration` dans le test.

---

### Tâche 16 — Policies SlidingWindow dans Program.cs
**Fichier** : `backend/src/Nexora.Api/Program.cs`

Lire la config et créer 5 policies `SlidingWindow`. Clé de partition : `IP:UserId`. Réponse 429 normalisée JSON avec `Retry-After`. Remplacer la réponse 429 par défaut de la policy `auth` existante par le même format JSON.

**Critère de validation** : build sans warning. Test manuel `curl -X POST /api/v1/pos/checkout` × 61 → le 61e retourne 429 avec `{"title":"rate_limit_exceeded",...}`.

---

### Tâche 17 — Décoration des controllers avec [EnableRateLimiting]

Décorer les endpoints dans :
- `PosController.cs` : `Checkout` → `[EnableRateLimiting("pos-checkout")]`
- `PayrollController.cs` : `Calculate`, `Validate` → `[EnableRateLimiting("heavy-compute")]`
- `BiController.cs` et controllers d'export → `[EnableRateLimiting("export")]`
- `MigrationController.cs` : `Execute`, `Simulate` → `[EnableRateLimiting("import")]`
- `SetupController.cs` → `[EnableRateLimiting("setup")]` sur les POST

**Critère de validation** : build sans erreur. `dotnet test` passe (les tests existants ne doivent pas être affectés par le rate limiting car ils ne passent pas par le middleware HTTP).

---

## GROUPE 6 — REQ-05 : Health Checks améliorés

### Tâche 18 — Améliorer DatabaseHealthCheck
**Fichier** : `backend/src/Nexora.Api/HealthChecks/DatabaseHealthCheck.cs`

Remplacer `CanConnectAsync` par une query fonctionnelle :
```csharp
await db.Database.ExecuteSqlRawAsync("SELECT count(*) FROM nexora.app_user LIMIT 1", ct);
// Retourner Healthy avec data: { "message": "Schéma et droits validés" }
```

Garder le catch sur `Exception` qui retourne `Unhealthy`.

**Critère de validation** : `GET /health/ready` avec une DB sans le schéma `nexora` → `Unhealthy` au lieu de `Healthy`.

---

### Tâche 19 — SchemaVersionHealthCheck
**Fichier** : `backend/src/Nexora.Api/HealthChecks/SchemaVersionHealthCheck.cs`

Créer un health check qui lit la migration la plus récente :
```csharp
// Tente de lire depuis une table de tracking des migrations
// Retourne: { version: "V077", appliedAt: "..." }
// Si la table n'existe pas → Degraded (pas Unhealthy — c'est encore démarrable)
```

Enregistrer dans `Program.cs` avec tag `["ready"]`.

**Critère de validation** : après les migrations, `GET /health/ready` inclut `"schema-version": { "status": "Healthy", "data": { "version": "V077" } }`.

---

### Tâche 20 — SchedulerHealthCheck
**Fichier** : `backend/src/Nexora.Api/HealthChecks/SchedulerHealthCheck.cs`

```csharp
// Si Scheduler:Enabled = false → Healthy avec { enabled: false }
// Si Scheduler:Enabled = true :
//   Lire le dernier job_run (table nexora.job_run, col finished_at)
//   Si finished_at > now() - (5 * IntervalSeconds) → Healthy avec { secondsAgo: N }
//   Sinon → Unhealthy avec { message: "Planificateur silencieux depuis Ns" }
```

**Critère de validation** : avec scheduler désactivé → `{ enabled: false, status: Healthy }`. Avec scheduler activé et dernier run > 5 × interval → `Unhealthy`.

---

### Tâche 21 — Réponse JSON enrichie pour tous les health checks
**Fichier** : `backend/src/Nexora.Api/Program.cs`

Configurer un `ResponseWriter` personnalisé sur les endpoints `/health/ready` et `/health` qui retourne le JSON structuré décrit dans le design :

```csharp
app.MapHealthChecks("/health/ready", new HealthCheckOptions
{
    Predicate = c => c.Tags.Contains("ready"),
    ResponseWriter = HealthCheckResponseWriter.WriteAsync,
});
```

**Critère de validation** : `curl /health/ready` retourne un JSON avec `entries` contenant tous les checks avec leurs données.

---

## GROUPE 7 — REQ-06 : OpenTelemetry

### Tâche 22 — Packages OTel dans Directory.Packages.props et Nexora.Api.csproj
**Fichiers** : `backend/Directory.Packages.props`, `backend/src/Nexora.Api/Nexora.Api.csproj`

Ajouter les 6 packages OTel avec leurs versions exactes (voir design). Référencer dans le `.csproj` de l'API.

**Critère de validation** : `dotnet restore` réussit sans conflit de version.

---

### Tâche 23 — NexoraMetrics.cs
**Fichier** : `backend/src/Nexora.Infrastructure/Telemetry/NexoraMetrics.cs`

Créer les 4 instruments métriques (Counter checkout, Histogram payroll, Histogram scheduler, Counter notifications) avec noms, unités et descriptions corrects. Singleton statique — pas de DI.

**Critère de validation** : compile. Les noms respectent la convention OTel (`nexora.<domain>.<metric>`).

---

### Tâche 24 — Configuration OTel dans Program.cs
**Fichier** : `backend/src/Nexora.Api/Program.cs`

Configurer `AddOpenTelemetry()` avec tracing ASP.NET Core + EF Core + HttpClient + OTLP exporter conditionnel. Configurer metrics avec Prometheus scraping sur `/metrics`. Ajouter la section `OpenTelemetry` dans `appsettings.json`.

L'export OTLP doit être conditionnel : si `OpenTelemetry:Endpoint` est vide → pas d'exporter (pas d'erreur).

**Critère de validation** : démarrage sans erreur avec `OpenTelemetry:Endpoint` vide. `GET /metrics` retourne des métriques Prometheus. Avec un endpoint OTLP configuré → les traces arrivent dans Jaeger/Grafana.

---

### Tâche 25 — Instrumentation des points métier critiques

Modifier 4 fichiers pour appeler `NexoraMetrics.*` :

1. **`SalesService.cs`** : dans `CheckoutCoreAsync`, après le `return Result.Success(...)`, ajouter :
   ```csharp
   NexoraMetrics.CheckoutTotal.Add(1, new TagList { { "status", "success" } });
   ```
   Et dans les chemins d'erreur :
   ```csharp
   NexoraMetrics.CheckoutTotal.Add(1, new TagList { { "status", "error" } });
   ```

2. **`SqlPayrollService.Calculation.cs`** : entourer `ComputeAsync` d'un `Stopwatch` et enregistrer dans `NexoraMetrics.PayrollDuration`.

3. **`NotificationDispatcher.cs`** : après chaque envoi réussi, `NexoraMetrics.NotificationDispatched.Add(1, new TagList { { "channel", row.Channel } })`.

4. **`BackgroundScheduler.cs`** : enregistrer la durée du tick (voir Tâche 5).

**Critère de validation** : après un checkout POS, `GET /metrics` contient `nexora_checkout_total{status="success"} 1`.

---

## GROUPE 8 — Documentation opérationnelle

### Tâche 26 — RUNBOOK.md
**Fichier** : `RUNBOOK.md` (racine du dépôt)

Créer le runbook opérationnel avec les sections :
1. **Pré-requis** : versions minimales (Docker, PostgreSQL 16+, .NET 10, Node 22)
2. **Variables d'environnement** : tableau complet avec description, obligatoire/optionnel, exemple
3. **Premier déploiement** : étapes numérotées (DB → migrations → API → frontend)
4. **Mise à jour** : procédure zero-downtime (migration → déploiement rolling)
5. **Activation du scheduler** : quand l'activer, combien d'instances, verrou distribué
6. **Canaux de notification** : configuration SMTP/Twilio/FCM avec exemples
7. **OpenTelemetry** : connecter Grafana/Jaeger, dashboards recommandés
8. **Diagnostic production** : commandes utiles (`/health/ready`, `/metrics`, advisory locks actifs)
9. **Rollback** : procédure si déploiement échoue

**Critère de validation** : document complet, toutes les variables d'environnement référencées dans le code sont listées.

---

### Tâche 27 — Tests d'intégration pour les nouvelles fonctionnalités

Ajouter dans les fichiers de tests existants :

**`MfaAuthenticationTests.cs`** (ou nouveau `RefreshTokenTests.cs`) :
- Login → obtenir refresh token
- Refresh → nouveau JWT valide
- Refresh avec ancien token → 401 + toutes sessions révoquées
- Refresh avec token expiré → 401

**`SchedulerTests.cs`** :
- Deux appels concurrents à `SchedulerRunner` via `PostgresAdvisoryLock` → un seul s'exécute

**`IdentityAccessTests.cs`** :
- Révocation de session → vérifier que le cache est invalidé et que le token est refusé

**`OperationsReadinessTests.cs`** :
- `GET /health/ready` → JSON structuré avec tous les checks

**Critère de validation** : `dotnet test` passe à 100% dans le pipeline CI.

---

## Séquence d'implémentation recommandée

```
Tâche 1  (migration V078)
Tâche 2  (ISessionCacheInvalidator interface)
Tâche 3  (implémentation + DI)
    ↓
Tâche 4  (PostgresAdvisoryLock)
Tâche 5  (BackgroundScheduler)
    ↓
Tâche 6  (cache TenantResolutionMiddleware)
Tâche 7  (invalidation dans services)
    ↓
Tâche 8  (contrats Auth)
Tâche 9  (RefreshHandler)
Tâche 10 (repository méthodes)
Tâche 11 (LoginHandler)
Tâche 12 (AuthController)
Tâche 13 (frontend apiClient)
Tâche 14 (frontend AuthContext)
    ↓
Tâche 15 (config RateLimiting)
Tâche 16 (policies Program.cs)
Tâche 17 (decoration controllers)
    ↓
Tâche 18 (DatabaseHealthCheck)
Tâche 19 (SchemaVersionHealthCheck)
Tâche 20 (SchedulerHealthCheck)
Tâche 21 (response writer)
    ↓
Tâche 22 (packages OTel)
Tâche 23 (NexoraMetrics)
Tâche 24 (OTel Program.cs)
Tâche 25 (instrumentation)
    ↓
Tâche 26 (RUNBOOK.md)
Tâche 27 (tests)
```

**Total** : 27 tâches couvrant 6 exigences. Effort estimé : 3–5 jours de développement pour une équipe maîtrisant la codebase.
