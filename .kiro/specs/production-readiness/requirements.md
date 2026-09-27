# NEXORA — Production Readiness : Exigences

## Vue d'ensemble

NEXORA est un ERP multi-tenant complet avec moteur comptable, POS, paie, GED et intelligence métier. Le code fonctionnel est solide mais six problèmes identifiés bloquent un déploiement production fiable :

1. **Scheduler non distribué** — double exécution sur plusieurs instances
2. **Révocation de session sans cache** — query DB sur chaque requête authentifiée
3. **Pas de refresh token** — reconnexion forcée toutes les 60 minutes
4. **Rate limiting incomplet** — seulement `/auth`, pas les endpoints lourds
5. **Health check insuffisant** — ne valide pas le schéma ni les droits DB
6. **OpenTelemetry absent** — pas de traces distribuées pour le diagnostic production

Ce spec couvre l'implémentation de ces six points plus la documentation opérationnelle nécessaire au premier déploiement.

---

## Exigences fonctionnelles

### REQ-01 : Scheduler distribué (Advisory Lock PostgreSQL)

**Contexte** : `SchedulerRunner.RunOnceAsync` s'exécute dans chaque instance de l'API sans coordination. Sur N instances, toutes les tâches planifiées (dispatch notifications, webhooks, alertes crédit, paie…) s'exécutent N fois simultanément.

- **REQ-01.1** : Avant chaque passage du planificateur, l'instance doit acquérir un advisory lock PostgreSQL (`pg_try_advisory_lock`) avec une clé fixe dérivée du nom d'application.
- **REQ-01.2** : Si le lock n'est pas disponible (une autre instance l'a acquis), l'instance courante doit ignorer ce passage silencieusement (pas d'erreur, pas de log warning).
- **REQ-01.3** : Le lock doit être libéré après chaque passage, que le passage réussisse ou échoue.
- **REQ-01.4** : La durée maximale d'un passage doit être configurable (`Scheduler:MaxRunSeconds`, défaut 120) — au-delà, le lock est considéré perdu et le passage est interrompu proprement.
- **REQ-01.5** : Le nombre d'instances ayant tenté d'acquérir le lock doit être visible dans les logs structurés (pour diagnostiquer une configuration multi-instance).

### REQ-02 : Cache de révocation de session

**Contexte** : `TenantResolutionMiddleware` exécute une query PostgreSQL sur chaque requête authentifiée pour vérifier que la session n'est pas révoquée. À 100 req/s, c'est 100 queries/s supplémentaires uniquement pour cette vérification.

- **REQ-02.1** : Les résultats de vérification de session `(jti → valid/revoked/epoch)` doivent être mis en cache en mémoire (`IMemoryCache` déjà disponible) avec une TTL de 30 secondes.
- **REQ-02.2** : Lors d'une révocation explicite de session (`POST /auth/sessions/{id}/revoke` ou déconnexion globale), l'entrée de cache correspondante doit être invalidée immédiatement.
- **REQ-02.3** : Le cache doit être limité en taille (max 10 000 entrées) pour éviter une croissance mémoire illimitée sous charge.
- **REQ-02.4** : Une fenêtre de 30 secondes est acceptable : un token révoqué peut encore être utilisé pendant 30 secondes maximum après révocation. Ce comportement doit être documenté.
- **REQ-02.5** : Les métriques de hit/miss du cache doivent être disponibles dans les logs structurés au niveau Debug.

### REQ-03 : Refresh Token

**Contexte** : Les JWT expirent en 60 minutes (`AccessTokenMinutes`). Il n'existe pas de mécanisme de rafraîchissement — les utilisateurs (caissiers, comptables, superviseurs) doivent se reconnecter toutes les heures, y compris en pleine saisie.

- **REQ-03.1** : À la connexion réussie, un refresh token opaque (256 bits aléatoires) doit être émis en complément du JWT.
- **REQ-03.2** : Le refresh token doit être retourné dans la réponse JSON de login (champ `refreshToken`) ET stocké en base de données (`user_session`) haché (SHA-256), avec expiration configurable (`Jwt:RefreshTokenDays`, défaut 30 jours).
- **REQ-03.3** : Un endpoint `POST /api/v1/auth/refresh` doit accepter le refresh token, valider qu'il est connu, non expiré et non révoqué, puis émettre un nouveau JWT + un nouveau refresh token (rotation).
- **REQ-03.4** : La rotation doit être atomique : l'ancien refresh token est révoqué dans la même transaction que l'émission du nouveau.
- **REQ-03.5** : La détection de réutilisation (refresh token déjà utilisé) doit révoquer **toutes** les sessions actives de l'utilisateur (indicateur de vol de token).
- **REQ-03.6** : Le endpoint `/auth/refresh` doit être soumis au rate limiting existant (policy `auth`).
- **REQ-03.7** : Le frontend doit intercepter les 401 et tenter automatiquement un refresh avant de rediriger vers `/login`.

### REQ-04 : Rate Limiting étendu

**Contexte** : Seul l'endpoint `auth` est limité à 10 req/min/IP. Les endpoints lourds (checkout POS, calcul de paie, génération PDF, import) peuvent saturer la base ou le CPU.

- **REQ-04.1** : Les endpoints suivants doivent avoir leur propre policy de rate limiting :

  | Policy | Endpoints | Limite | Fenêtre |
  |---|---|---|---|
  | `pos-checkout` | `POST /api/v1/pos/checkout` | 60 req/min | par IP |
  | `heavy-compute` | `POST /api/v1/hr/payroll/*/calculate`, `POST /api/v1/hr/payroll/*/validate` | 10 req/min | par IP |
  | `export` | `POST /api/v1/reports/*`, `GET /api/v1/*/export`, `GET /api/v1/*/print` | 20 req/min | par IP |
  | `import` | `POST /api/v1/migration/*/execute`, `POST /api/v1/migration/*/simulate` | 5 req/min | par IP |
  | `setup` | `POST /api/v1/setup/*` | 5 req/min | par IP |

- **REQ-04.2** : Toutes les policies doivent retourner HTTP 429 avec `Retry-After` et un corps JSON `{title: "rate_limit_exceeded", detail: "...", retryAfterSeconds: N}`.
- **REQ-04.3** : Les limites doivent être configurables dans `appsettings.json` (section `RateLimiting`) sans recompilation.
- **REQ-04.4** : Les controllers cibles doivent être décorés avec `[EnableRateLimiting("policy-name")]`.

### REQ-05 : Health Check amélioré

**Contexte** : `DatabaseHealthCheck` vérifie uniquement que la connexion TCP s'ouvre. Il ne valide pas le schéma (migrations appliquées), les droits du rôle applicatif, ni la santé fonctionnelle.

- **REQ-05.1** : Le health check `database` doit exécuter `SELECT count(*) FROM nexora.app_user LIMIT 1` — valide que le schéma existe ET que `nexora_app` a les droits SELECT.
- **REQ-05.2** : Un nouveau health check `schema-version` doit lire la version de migration la plus récente appliquée depuis `__ef_migrations_history_unused` (ou une table de tracking Flyway) et l'inclure dans la réponse.
- **REQ-05.3** : Un health check `scheduler` doit signaler si le scheduler est activé et, si oui, combien de temps s'est écoulé depuis le dernier passage réussi (Unhealthy si > 5 × l'intervalle).
- **REQ-05.4** : Les health checks doivent retourner des données structurées JSON avec `{status, duration, data: {...}}` lisibles par les outils de monitoring (Prometheus, Kubernetes liveness/readiness probes).

### REQ-06 : OpenTelemetry (traces + métriques)

**Contexte** : L'application génère des logs Serilog structurés mais aucune trace distribuée ni métrique d'application. En production, diagnostiquer une lenteur nécessite de corréler manuellement des logs.

- **REQ-06.1** : Le package `OpenTelemetry.Extensions.Hosting` et ses instrumentations ASP.NET Core, EF Core et HttpClient doivent être ajoutés.
- **REQ-06.2** : Les traces doivent être exportées vers OTLP (configurable via `OpenTelemetry:Endpoint` dans appsettings). En l'absence de configuration, l'export doit être désactivé (pas d'erreur au démarrage).
- **REQ-06.3** : Les métriques suivantes doivent être instrumentées manuellement :
  - `nexora.checkout.total` — compteur de ventes POS (avec tag `status: success|error`)
  - `nexora.payroll.calculation.duration` — histogramme de durée du calcul de paie
  - `nexora.scheduler.tick.duration` — histogramme de durée d'un passage planificateur
  - `nexora.notification.dispatched` — compteur par canal (`email|sms|push|webhook`)
- **REQ-06.4** : Le `CorrelationId` existant doit être propagé comme `trace.id` dans les spans OpenTelemetry.
- **REQ-06.5** : Les métriques doivent être exposées sur `/metrics` au format Prometheus (scrappable par Grafana/Prometheus).

---

## Exigences non-fonctionnelles

### REQ-NF-01 : Compatibilité
- Toutes les modifications doivent rester compatibles avec le schéma de base existant (migrations V001–V077). Toute migration SQL nécessaire doit être numérotée V078+.
- Le comportement actuel sans configuration supplémentaire (mono-instance dev) doit rester identique.

### REQ-NF-02 : Performance
- REQ-02 (cache révocation) : réduction attendue ≥ 80% des queries de vérification de session.
- REQ-01 (advisory lock) : overhead ≤ 5ms par passage planificateur.
- REQ-06 (OTel) : overhead de tracing ≤ 2% sur le p99 des endpoints.

### REQ-NF-03 : Résilience
- REQ-01 : si PostgreSQL est injoignable au moment d'acquérir le lock, le scheduler doit ignorer ce passage (pas de crash).
- REQ-03 : si la table de refresh tokens est corrompue, l'authentification par mot de passe doit continuer à fonctionner.
- REQ-06 : si l'endpoint OTLP est injoignable, l'API doit continuer à fonctionner normalement (export en mode fire-and-forget avec buffer limité).

### REQ-NF-04 : Tests
- Chaque nouvelle fonctionnalité doit avoir des tests d'intégration dans `Nexora.IntegrationTests` qui passent dans le pipeline CI existant.
- Les tests existants ne doivent pas être cassés.

### REQ-NF-05 : Documentation opérationnelle
- Un `RUNBOOK.md` doit être créé à la racine décrivant :
  - Variables d'environnement obligatoires et optionnelles
  - Procédure de premier déploiement
  - Procédure de mise à jour (migration + déploiement)
  - Comment activer chaque fonctionnalité optionnelle (scheduler, OTel, canaux notif)

---

## Hors périmètre

Les éléments suivants sont identifiés mais hors de ce spec :

- **RS256 (JWT asymétrique)** : nécessite un changement de provider d'identité — travail distinct.
- **Redis distribué** : le cache REQ-02 utilise `IMemoryCache` (in-process) — suffisant pour la majorité des déploiements. Redis peut remplacer sans changer le code applicatif.
- **Portail développeur API** : couvert par la Phase 46 existante.
- **RGPD — export données utilisateur** : périmètre légal séparé.
