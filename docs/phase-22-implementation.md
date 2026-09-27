# NEXORA — Phase 22 — Identity, rôles, permissions & multi-entreprises : état & validation

Référence : [`phase-22-identity-roles-permissions-multi-entreprises.md`](phase-22-identity-roles-permissions-multi-entreprises.md)
· [`phase-3-architecture-securite.md`](phase-3-architecture-securite.md) (socle) ·
[`phase-11-securite-audit-conformite-antifraude.md`](phase-11-securite-audit-conformite-antifraude.md) (sessions, step-up).

Le socle d'identité **existe déjà** : JWT + bcrypt, `app_user` / `role` / `permission` /
`role_permission` / `access_scope` / `user_role`, `TenantResolutionMiddleware` +
`PermissionAuthorizationHandler` (deny-by-default), RLS PostgreSQL FORCE, `user_session` +
`security_epoch` (déconnexion globale), step-up, moteur d'approbation, rôles personnalisés
(Phase 14). Cette phase valide la conformité, dresse l'inventaire, et **livre l'accès temporaire
& révocable** (§9, §18, §32-33) + les endpoints `/me/scopes` & `/me/permissions` + une API
d'administration des affectations (§40).

## Correspondance spec → implémentation

| § | Exigence | État |
|---|---|---|
| 2 | `IDENTITÉ → RÔLES → PERMISSIONS → AFFECTATIONS/SCOPES → RESSOURCES` ; autz **toujours serveur** | ✅ `PermissionService.LoadProfileAsync` reconstitue le profil (entreprises via scopes GROUP/COMPANY, permissions via rôles) ; `[RequirePermission]` + `FallbackPolicy` deny-by-default ; `X-Company-Id` **sélectionne** dans l'ensemble autorisé, jamais preuve |
| 3, 13-14 | Hiérarchie `GROUPE → ENTREPRISE → ÉTABLISSEMENT → CAISSE` ; affectation = rôle × périmètre ; **date de début/fin** ; rôle différent par entreprise | ✅ `access_scope` (PLATFORM/GROUP/COMPANY/ESTABLISHMENT/STORE/WAREHOUSE/POS_TERMINAL) + `user_role` ; **`valid_from` / `valid_until` ajoutés (V035)** ; conflits résolus par union des permissions applicables (politique explicite : le plus permissif au sein du périmètre) |
| 4, 21-27 | Rôles types (caissier / comptable / comptable en chef / direction / admin entreprise / super admin / auditeur) | ✅ 12 rôles système seedés (V021) + rôles personnalisés d'entreprise (Phase 14 `ICustomRoleService`) |
| 5-6 | Identité + statuts (`invité/actif/suspendu/bloqué/désactivé/en attente`) ; désactivation ≠ suppression de l'historique | ✅ `app_user` (`status` CHECK `active/suspended/locked/archived`, `mfa_enabled`, `last_login_at`) ; `audit_log` immuable survit à la désactivation ; `SetStatusAsync` livré |
| 7-8 | AuthN + MFA + réinit + expiration + révocation + verrouillage + réauth ; sessions | ✅ login JWT + step-up (`/security/step-up`) + `user_session` + révocation individuelle + **déconnexion globale (`security_epoch`)** ; MFA : `mfa_enabled` + politique, second facteur TOTP non implémenté |
| 9 | Devices | ✅ `sync_device` (Phase 10) — nom, type, entreprise, statut, révocation, `pos_terminal_id` |
| 11 | `DOMAIN.ACTION` granulaire | ✅ ~120 permissions seedées (V021 + par phase) ; NEXORA nomme `pos.sell`, `accounting.post`, `finance.pay`… (équivalent au modèle `sales.create` etc.) |
| 12 | Rôles personnalisés (créer / copier / permissions / scope / désactiver / historiser / **interdire élévation**) | ✅ `ICustomRoleService` (Phase 14) + audité (V011/V030) ; **garde d'élévation ajoutée** : `GrantAsync` refuse d'accorder une permission que l'appelant ne détient pas |
| 15-16 | Contexte courant + sélecteur d'entreprise (accessibles, groupe, **lecture seule**) | ✅ **`GET /me/scopes`** (arbre groupes + entreprises avec `roles` et `readOnly`) + **`GET /me/permissions`** livrés ; front : `AuthContext.setActiveCompany` + `AppLayout` sélecteur |
| 17, 29 | Isolation : aucune confiance dans `company_id` client ; vérif scope + ressource ; tests d'isolation ; logs refus | ✅ **RLS PostgreSQL FORCE sur 149/161 tables** + `RlsConnectionInterceptor` + FK composites `(id, company_id)` ; tests d'isolation depuis la Phase 3 ; refus → `403` RFC 7807 loggé |
| 18 | Moindre privilège ; **admin entreprise ≠ admin groupe** ; accès temporaire avec expiration | ✅ `GrantAsync` : rôles `super_administrator` / `group_administrator` refusés par ce canal, périmètre borné à celui de l'appelant, permissions ⊆ celles de l'appelant ; **`valid_until` → expiration automatique** |
| 19-20 | Séparation des responsabilités + approbations (seuil / nombre / rôle / expiration / délégation) | ✅ Phase 11 : `approval_policy` + `approval_request` (demandeur ≠ approbateur, double approbation) ; Phase 14 : workflows configurables (`workflow` + moteur de règles) |
| 28 | `Authorize(Permission, Scope)` → identité + permission + company scope + statut | ✅ `PermissionAuthorizationHandler` (permission) + `TenantResolutionMiddleware` (identité + scope + **`status`/`security_epoch` → 401**) |
| 30 | Permissions financières sensibles dédiées + audit | ✅ `finance.pay`, `accounting.close_period`, `accounting.reopen_period`, `security.*`, `consolidation.publish`, `group.mappings`… + `audit_row()` sur les tables concernées |
| 31 | Accès lecture seule (entreprise / compta / reporting / audit / consolidation ; **POS interdit**) | ✅ `/me/scopes` calcule `readOnly` (aucune permission d'écriture effective) ; le POS exige `pos.sell` |
| 32-33 | Accès temporaire (début / fin / motif / créateur / révocation / audit) ; délégation | ✅ **livré** — `user_role.valid_from/valid_until/reason/revoked_at/revoked_by` (V035), `GrantAsync` / `RevokeAsync` (motif obligatoire), audité |
| 34-35 | Sécurité des comptes + récupération | ✅ Phase 11 (`security_policy`, sessions, step-up) ; réinit par token à usage unique : `step_up_challenge` couvre la réauth, la réinit de mot de passe complète est à câbler |
| 36-38 | Onboarding utilisateur / entreprise / admin initial | ⚙️ `GroupController.POST companies` (provisioning, Phase 9) + checklist d'onboarding (Phase 13) ; assistant pas-à-pas complet à assembler |
| 39 | API Identity (`/auth/*`, `/me`, `/me/scopes`, `/me/permissions`) | ✅ `login` + `/me` (Phase 3/11) + **`/me/scopes` & `/me/permissions` livrés** ; `refresh` / `mfa/verify` / `password/reset` non implémentés (jeton court ≤ 60 min) |
| 40 | API Users (`GET/POST`, suspend/activate, assignments) | ✅ **`UsersController` livré** : `GET /users`, `GET/POST /users/{id}/assignments`, `POST /users/assignments/{id}/revoke`, `POST /users/{id}/status` (permission `admin.manage_users`). Création d'utilisateur (`POST /users`) + `PATCH` : à ajouter (aujourd'hui via provisioning). |
| 41 | API Roles | ✅ `AdministrationController` `/admin/roles` (+ `permissions`, `preview`, `disable`) — Phase 14 |
| 42 | API Organisations | ✅ `CompaniesController`, `GroupController` (`companies`, `establishments`) — Phases 3/9 |
| 43 | API contexte (`/context`, `/context/select`, `/context/available`) | ⚙️ `X-Company-Id` + `/me/scopes` couvrent le besoin ; endpoints `/context/*` dédiés non exposés (commodité) |
| 44, 48 | Audit identité (login, rôle, scope, suspension, **accès refusé**, **élévation**, permission) + journal protégé | ✅ `audit_row()` sur `user_role` / `access_scope` / `role` / `role_permission` / `app_user` (via Phase 11) ; **`audit_log` immuable** ; correlation ID |
| 45-46 | Tests critiques + non-régression | ✅ voir ci-dessous ; isolation multi-tenant testée en continu depuis la Phase 3 |
| 47 | Performance : cache permissions, index assignments, ne pas charger les entreprises non accessibles | ✅ `LoadProfileAsync` ne charge que les entreprises des scopes de l'utilisateur ; **`user_role_active_idx` ajouté (V035)** ; le profil est reconstruit par requête (cache distribué à ajouter) |

## Ce qui est livré dans cette phase

- `docs/phase-22-identity-roles-permissions-multi-entreprises.md` — spec verbatim.
- **`V035__temporary_role_assignments.sql`** — `user_role` += `valid_from`, `valid_until`,
  `reason`, `revoked_at`, `revoked_by` ; CHECK `valid_until > valid_from` ; index
  `user_role_active_idx (user_id) WHERE revoked_at IS NULL`.
- **`Domain/Identity/UserRole`** — nouvelles propriétés + `IsActiveAt(at)` ; EF mapping.
- **`PermissionService.LoadProfileAsync`** — ne retient que les affectations **actives à l'instant
  présent** (`revoked_at IS NULL`, `valid_from ≤ now`, `valid_until > now`) ; injecte
  `IDateTimeProvider`.
- **`IUserAssignmentService`** (`SqlUserAssignmentService`) :
  - `ListUsersAsync` — utilisateurs du périmètre de l'appelant ;
  - `ListAssignmentsAsync(userId)` — avec `Active` calculé ;
  - `GrantAsync` — crée `access_scope` (find-or-create) + `user_role` (bornable) ;
    **refuse** : rôles protégés (`super_administrator` / `group_administrator`), périmètre hors
    de celui de l'appelant, **élévation de privilège** (permission absente chez l'appelant) ;
  - `RevokeAsync` — **motif obligatoire** ;
  - `SetStatusAsync` — `active` / `suspended` / `locked` ; toute suspension **avance
    `security_epoch`** (invalide les sessions).
- **`IScopeQueryService`** (`SqlScopeQueryService`) — `MyScopesAsync` (arbre + `readOnly`),
  `MyPermissionsAsync` (liste plate pour l'entreprise active). Les lectures d'identité tournent
  en portée système (§3 §12).
- **`UsersController`** (`/api/v1/users`) + **`MeController`** += `GET /me/scopes` & `GET /me/permissions`.
- `docs/phase-22-implementation.md` — ce document.

## Vérifications exécutées

**Tests d'intégration `IdentityAccessTests` — 6/6** :

| Scénario (§9, §18, §32, §45) | Vérifié |
|---|---|
| Affectation temporaire | `auditor` avec `validUntil` **passé** → aucune entreprise dans le profil ; avec `validUntil` **futur** → Pharmacie accessible + `security.audit_view` accordée |
| Révocation | `accountant` accordé → visible ; `RevokeAsync` **sans motif → refusé**, **avec motif → OK** ; profil vidé ; l'affectation reste listée en `Active = false` |
| Anti-élévation | un porteur de `cashier` ne peut accorder `chief_accountant` → `identity.escalation` ; `super_administrator` → `identity.role_protected` |
| Sélecteur / périmètre | `/me/scopes` (caissier) : 1 entreprise, rôles `[cashier]`, `readOnly = false` ; `/me/permissions` contient `pos.sell` ; direction → voit Pharmacie **+ Restaurant** ; `ListUsersAsync` borné au périmètre |
| `company_id` forcé | un caissier « choisissant » l'entreprise Restaurant → `PermissionsFor(Restaurant)` **vide**, `HasPermission("pos.sell", Restaurant)` **faux** |
| Suspension | `SetStatusAsync("suspended")` → `app_user.status = 'suspended'` **et `security_epoch` avancé** |

**Smoke API (démo)** : `/me/permissions` (211 permissions pour la direction) ; `/me/scopes`
(3 entreprises du groupe pour d1, 1 pour le caissier avec rôle `cashier`) ; `GET /users` par le
caissier → **403** ; `GET /users` par d1 → 200 (3 utilisateurs) ; `POST /users/{id}/assignments`
(`auditor` temporaire) → 200 ; `super_administrator` → **403 `identity.role_protected`**.

**Total backend : 131 tests verts** (16 unitaires + 6 API + 109 intégration), build Debug **et**
Release 0 warning. DB : **35 migrations (V001–V035)**, 161 tables, 149 RLS. Rebuild + `smoke_test`
+ démo (rejouée 2×) OK. Frontend `tsc` + `vite build` + Vitest OK.

## Manques identifiés (Phase 15 / conception détaillée)

| Sujet | Manque |
|---|---|
| AuthN (§7, §39) | refresh tokens, second facteur TOTP, `POST /auth/password/reset` complet |
| API Users (§40) | `POST /users` (création) + `PATCH /users/{id}` (profil) |
| Autorisation par ressource (§29) | enforcement au niveau caisse/établissement pour les opérations POS (aujourd'hui : entreprise) |
| Contexte (§43) | endpoints `/context/*` dédiés si besoin d'une commodité serveur |
| Performance (§47) | cache distribué du profil de sécurité + invalidation sur changement de rôle |
| Délégation (§33) | délégation d'approbation temporaire (le modèle `valid_until` sur `user_role` la permet déjà pour les rôles) |
| Onboarding (§36-38) | assistant pas-à-pas complet utilisateur / entreprise |

## Couverture des critères de sortie (§49)

Identity / authentification / users / roles / permissions / **assignments (dont temporaires)** /
scopes groupe-entreprise-site-caisse / **sélecteur de contexte (`/me/scopes`)** / isolation testée /
caissier / comptable / comptable en chef / direction testés / audit activé / approbations définies /
**accès temporaire défini et livré** / API : **✅**. OpenAPI enrichie + refresh tokens : ⚙️.

## Suite — Phase 23

Catalogue produits, services, prix, taxes, unités et codes-barres — base commerciale commune à
tous les secteurs. Le module Catalog existe déjà (`Nexora.{Application,Infrastructure}/Catalog`,
`product` / `product_price` / `product_barcode` / `unit` / `tax` / `custom_field`) ; la Phase 23
en produira la spécification de référence.
