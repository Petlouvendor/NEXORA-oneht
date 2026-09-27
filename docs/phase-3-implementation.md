# NEXORA — Phase 3 — État de l'implémentation

Référence : [`phase-3-architecture-securite.md`](phase-3-architecture-securite.md).
Ce document trace la correspondance entre les critères de sortie (§31) et le code livré.

## Arborescence livrée

```
backend/
├── Nexora.sln · Directory.Build.props · Directory.Packages.props · Dockerfile
├── src/
│   ├── Nexora.Domain/          entités (Organization, Identity), Result/Error
│   ├── Nexora.Contracts/       DTO Auth / Organization / pagination
│   ├── Nexora.Application/     ITenantContext, IUnitOfWork, IPermissionService,
│   │                           LoginHandler, ListCompaniesHandler, PermissionCatalog
│   ├── Nexora.Infrastructure/  NexoraDbContext (base d'abord), RlsConnectionInterceptor,
│   │                           TenantContext, UnitOfWork, repositories, Bcrypt, JwtTokenService,
│   │                           PermissionService
│   └── Nexora.Api/             Program.cs, middleware (correlation, exceptions, tenant, headers),
│                               sécurité (permissions dynamiques, idempotence), contrôleurs, health
└── tests/  UnitTests (6) · ApiTests (2) · IntegrationTests (Phase 4)

frontend/nexora-web/            Vite + React 19 + Router 7 — structure §7, auth, apiClient, thème
infrastructure/docker-compose.yml   PostgreSQL 18 + service API
.github/workflows/ci.yml        DB (migrations + smoke test) · backend (build/test/scan) · frontend
```

## Couverture des critères de sortie (§31)

| Critère | État | Où |
|---|---|---|
| Architecture backend validée | ✅ | 6 projets, build 0 warning, `dotnet run` opérationnel |
| Architecture frontend validée | ✅ | `npm run build` OK, structure §7, auth + apiClient câblés |
| Modules et responsabilités définis | ✅ | `phase-3-architecture-securite.md` §6 ; catalogue de permissions aligné (migration V021 étendue) |
| API versionnée et sécurisée | ✅ | `/api/v1/*`, `Asp.Versioning`, HTTPS/HSTS hors dev, headers, CORS, rate limiting |
| Authentification / autorisation | ✅ | JWT + bcrypt ; `[RequirePermission]` + deny-by-default ; testé (caissier 403, direction 200) |
| Multi-tenant | ✅ | `TenantResolutionMiddleware` + `RlsConnectionInterceptor` + RLS PostgreSQL ; testé (isolation Pharmacie/Restaurant/Supermarché) |
| Transactions et intégrité | ✅ (fondations) | `IUnitOfWork.ExecuteInTransactionAsync` + triggers financiers Phase 2 ; moteur métier détaillé en Phase 4 |
| Audit / observabilité | ✅ | Serilog + correlation ID ; `audit_log` alimenté par triggers (Phase 2) |
| Sauvegarde / restauration | ✅ (défini) | §20 + `database/README.md` ; procédures ops à instancier au déploiement |
| Local / cloud / hybride / offline | ✅ (défini) | §21-23 ; `deployment_mode` en base ; tables `sync_*` prêtes (Phase 2) |
| CI/CD et tests de sécurité | ✅ | `.github/workflows/ci.yml` : migrations + smoke test + build + `dotnet list package --vulnerable` |

## Vérifications exécutées

```
GET  /health                                  -> 200 Healthy
GET  /health/ready                             -> 200 (PostgreSQL joignable via nexora_app)
POST /api/v1/auth/login  (direction)           -> 200 + JWT + 3 entreprises (périmètre groupe)
GET  /api/v1/me          (caissier)            -> périmètre = Pharmacie, 13 permissions POS/ventes
GET  /api/v1/companies   (caissier)            -> 403  (permission organizations.view absente)
GET  /api/v1/companies   (direction, X-Company-Id=Pharmacie)   -> 1 entreprise  (RLS)
GET  /api/v1/companies   (direction, sans header)             -> 3 entreprises
GET  /api/v1/companies   (direction, X-Company-Id=Supermarché autre user) -> 403 company_not_authorized
GET  /api/v1/companies   (sans jeton)          -> 401
dotnet test (unit + api)                        -> 8/8 vert
```

## Décisions d'implémentation

- **EF Core en mode base d'abord.** Le schéma reste la propriété des migrations SQL
  (`database/migrations/`) — triggers, RLS, colonnes générées ne se prêtent pas aux migrations EF.
- **RLS = frontière d'isolation.** Les requêtes applicatives ne portent pas de filtre `company_id` ;
  l'interception de connexion pose le contexte de session, PostgreSQL applique la politique.
  Le pool Npgsql réinitialise (`RESET ALL`) au retour → aucune fuite entre requêtes.
- **Permissions dynamiques.** Pas de politiques statiques : `perm:<code>` est résolue à la volée
  par `PermissionPolicyProvider`, vérifiée dans le périmètre de l'entreprise active.
- **Contexte actif ≠ autorisation** (§12). `X-Company-Id` restreint le périmètre RLS mais n'accorde
  jamais l'accès : la valeur doit déjà figurer dans le jeton.
- **Rôle runtime `nexora_app`** sans `BYPASSRLS`, séparé du rôle de migration (`postgres`).

## Suite — Phase 4

Moteur financier et comptable : génération centralisée des écritures, événements financiers,
affectations de paiements, trésorerie, devises, rapprochements, consolidation — sur les fondations
transactionnelles posées ici et le schéma de la Phase 2.
