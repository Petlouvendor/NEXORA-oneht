# Phase 33 — Identité, Sécurité & Gestion des accès — implémentation

**Statut : livré et vérifié.** Migration `V046`. 7 tests d'intégration (`MfaAuthenticationTests`),
3 tests unitaires ajoutés (`LoginHandlerTests`), 5 tests frontend (`securityApi.test.ts`).
**218 tests backend** verts (18 unit + 7 API + 193 intégration), Debug **et** Release, 0 warning.
40 tests frontend.

Le socle IAM / sécurité existe déjà et reste **validé** par cette phase :

- **Phase 3** — JWT + RLS multi-tenant, `TenantResolutionMiddleware` (le `X-Company-Id` du client
  ne fait que *sélectionner* parmi les entreprises du jeton — §11-12, §20), politique
  deny-by-default (`[RequirePermission]`, `FallbackPolicy` refus — §2, §28), `security_epoch`
  (déconnexion globale — §6, §29)
- **Phase 4** (`V004`) — `app_user`, `role` (système / entreprise), `permission`, `role_permission`,
  `access_scope` (7 types §17), `user_role` (RBAC — §12-14)
- **Phase 11** (`V030`) — `user_session` (registre + révocation + « déconnexion globale » §10),
  `step_up_challenge` (ré-authentification renforcée §6, §23), `security_policy` (MFA obligatoire
  par rôle §37, TTL de session, rétention d'audit §34), `audit_log` **immuable** (trigger
  `BEFORE UPDATE/DELETE` §34, §50), moteur anti-fraude (`fraud_rule` / `fraud_signal` — §35),
  workflow d'approbation (demandeur ≠ approbateur, double approbation §24)
- **Phase 14** (`V033`) — rôles personnalisés (`ICustomRoleService` §12), config par périmètre,
  `config_version` (§37 politiques entreprise versionnées)
- **Phase 22** (`V035`) — affectations de rôle **temporaires & révocables** (`user_role.valid_from
  / valid_until / revoked_at` — §23), `PermissionService` ne retient que les affectations actives
  à l'instant présent, garde anti-élévation, `GET /me/scopes` & `/me/permissions` (§21),
  `UsersController` (affectations §44)
- **Phase 9** — `ICompanyProvisioningService` (onboarding §5) ; **Phase 32** (`V045`) —
  `security.reset_mfa` s'ajoute au catalogue de permissions

La Phase 33 rend **opérationnelle de bout en bout** la brique manquante la plus centrale du §8
et durcit le chemin de connexion (§7, §4, §48) : **MFA TOTP** (les colonnes `app_user.mfa_enabled
/ mfa_secret` existaient depuis `V004` mais n'étaient jamais utilisées) + **verrouillage
temporaire après essais répétés** + **journal d'évènements de sécurité explicite** (§33, §46).

---

## Correspondance cahier des charges → implémentation

| § | Exigence | Où |
|---|---|---|
| 2 | L'authentification n'ouvre pas l'accès ; autorisation serveur ; scope vérifié hors interface ; moindre privilège | Chaîne inchangée (Phase 3/22) : `TenantResolutionMiddleware` → RLS PostgreSQL + `[RequirePermission]`. La MFA ajoute un facteur **avant** l'émission du jeton |
| 3 | Identité (id, nom, email, statut, langue, fuseau, préférences, dernière activité, métadonnées de sécurité) | `app_user` (Phase 4) + `user_preference` (Phase 13) ; `app_user.last_login_at`, **`mfa_enrolled_at`**, **`failed_login_count`**, **`locked_until`** (§4) |
| 4 | Comptes : invité / actif / suspendu / désactivé / **verrouillé temporairement** / suppression logique / réactivation | `app_user.status` (`active`/`suspended`/`locked`/`archived`) + **`locked_until`** : verrouillage *temporaire* automatique, distinct du `status='locked'` administratif ; `IUserAssignmentService.SetStatusAsync` (Phase 22) |
| 5 | Création : invitation → vérification → activation → rôle → scope ; expiration ; pas de compte partagé | `ICompanyProvisioningService` (Phase 9) + `IUserAssignmentService` (Phase 22). Flux d'invitation à jeton dédié (`/users/invitations`) : **backlog** |
| 6 | Auth : email + mot de passe ; SSO/OIDC ; auth locale ; session sécurisée ; **ré-authentification pour actions critiques** | `LoginHandler` (email + mot de passe, `BcryptPasswordHasher`) ; `IStepUpAuthService` (Phase 11, §23) ; SSO/OIDC : `app_user.external_identity` prévu, connecteur : **backlog** |
| 7 | Mot de passe : hash robuste ; jamais en clair ; **protection contre essais répétés** ; réinitialisation sécurisée ; expiration selon politique | BCrypt (coût 12) ; **`IUserRepository.RegisterFailedLoginAsync`** : 5 échecs → `locked_until = now + 15 min` + compteur remis à zéro ; `ResetFailedLoginsAsync` à la réussite. `/auth/password/reset` : **backlog** (`step_up_challenge` fournit la primitive de jeton court) |
| 8 | **MFA** : TOTP ; Passkey/WebAuthn ; **code de récupération** ; obligatoire pour rôles sensibles ; obligatoire configurable par entreprise ; **enrôlement contrôlé** | **`IMfaService`** + **`Totp`** (RFC 6238, HMAC-SHA1, 6 chiffres, pas 30 s, fenêtre ±1 ; Base32 RFC 4648 ; aucune dépendance). Enrôlement : `BeginEnrollmentAsync` (secret → `mfa_pending_secret` + URI `otpauth://`) → `ConfirmEnrollmentAsync` (1er code valide → `mfa_enabled`, `mfa_secret`, **10 codes de récupération** hachés SHA-256 dans `mfa_recovery_code`, renvoyés **une seule fois**). Connexion : `LoginHandler` émet un `mfa_challenge` (5 min) au lieu du jeton ; `/auth/mfa/verify` valide le code **TOTP ou de récupération** puis émet le jeton (`MfaVerifyHandler`). WebAuthn : **backlog** |
| 9 | Récupération : demande sécurisée ; vérification d'identité ; jeton à durée limitée ; réinitialisation ; **révocation des sessions** ; journalisation | Code de récupération à **usage unique** (`mfa_recovery_code.used_at`) ; **`IMfaService.ResetForUserAsync`** (permission `security.reset_mfa`, cible dans le périmètre de l'appelant §39) efface secret + codes + défis en cours et journalise `mfa.reset_by_admin` ; révocation de sessions via `ISessionSecurityService` (Phase 11) |
| 10 | Sessions : unique/multiple ; expiration ; inactivité ; révocation ; liste appareils ; dernière activité ; réauth | `user_session` + `ISessionSecurityService` (Phase 11) : `GET/POST /security/sessions*`, `revoke-all` = déconnexion globale (`security_epoch`) |
| 11 | Appareils : reconnu ; nom ; dernière connexion ; plateforme ; révocation ; **confiance à durée limitée, jamais permanente implicite** | `user_session.device_label` / `user_agent` / `ip_address` ; registre d'appareils de confiance dédié (`/devices`) : **backlog** — le principe « aucune confiance permanente » est déjà tenu (chaque session expire, `mfa_challenge` à 5 min, codes de récupération à usage unique) |
| 12-13 | Rôles système / personnalisés ; permissions granulaires ; par entreprise / établissement ; **plusieurs rôles selon contexte** | `role` + `role_permission` + `user_role` → `access_scope` (Phase 4) ; `ICustomRoleService` (Phase 14). Exemples caissier / comptable / comptable en chef / admin groupe = données de `role` + `access_scope` |
| 14-15 | Permissions (voir…configurer) ; permissions **sensibles** (limite crédit, radiation, réouverture période, plan comptable, consolidation, gestion sécurité…) | `PermissionCatalog` (catalogue typé) ; les opérations sensibles passent par `[RequirePermission]` + `IStepUpAuthService` + `IApprovalWorkflowService` (Phases 11/27-29). **`security.reset_mfa`** ajoutée |
| 16 | **RBAC + ABAC** : rôle + permission + entreprise + établissement + objet + contexte + règle | RBAC : `PermissionService.LoadProfileAsync` (permissions par entreprise). ABAC : `IWorkflowService` moteur de règles (Phase 14) + gardes métier (période close, client bloqué, limite crédit…). Politique MFA = règle ABAC sur le rôle (`security_policy.mfa_required_roles`) |
| 17-18 | Scopes (groupe/entreprise/établissement/…) ; lecture seule ou écriture ; utilisateur multi-entreprises ; **changement de contexte explicite** ; audit de l'entreprise active | `access_scope` (7 types) ; `X-Company-Id` explicite (Phase 3) ; `GET /me/scopes` (Phase 22) ; l'entreprise active est journalisée dans `audit_log.company_id` |
| 19 | Comptable en chef : plusieurs entreprises ; compta multi-entreprises ; consolidation si autorisée ; **pas d'accès implicite aux données opérationnelles** | Rôle `chief_accountant` en scope `GROUP` → `PermissionService` n'accorde que les permissions du rôle (compta), jamais les ventes ; consolidation via permission dédiée (Phase 9) |
| 20 | **Séparation stricte** ; tests automatisés d'isolation ; aucun filtre uniquement frontend | RLS `FORCE` sur 167 tables ; `IdentityAccessTests` + `smoke_test.sql` §7 (isolation prouvée en base). `mfa_recovery_code` / `mfa_challenge` : RLS `user_id = current_user_id()` |
| 21 | Contexte actif affiché ; **confirmation avant action sensible dans un autre scope** | Sélecteur d'entreprise (Phase 13) ; `IStepUpAuthService` (Phase 11) |
| 22 | **Impersonation support** : consentement ; durée limitée ; bannière ; audit complet ; jamais silencieux | **Backlog** — `impersonation_request` + bannière + audit `impersonation.started/ended` restent à faire ; la primitive de jeton court (`mfa_challenge` / `step_up_challenge`) et le journal explicite (`ISecurityEventLog`) sont en place |
| 23 | **Élévation temporaire** : motif ; durée ; approbateur ; expiration automatique ; audit | **`user_role.valid_from/valid_until/reason`** + `IUserAssignmentService.GrantAsync` (Phase 22) : une affectation bornée expire d'elle-même. Endpoint `/elevations` dédié : **backlog** |
| 24 | **Double approbation** : préparation A ; validation B ; pas d'auto-validation ; seuil configurable | `IApprovalWorkflowService` (Phase 11) : `security.self_approval` / `security.duplicate_approver`, `approval_policy.required_levels` |
| 25 | Sécurité paiement : réauth ; MFA selon risque ; 2ᵉ personne selon montant ; bénéficiaire contrôlé ; idempotence ; audit | `IStepUpAuthService` + `IApprovalWorkflowService` + `IdempotencyFilter` (Phase 18) + 3-way match (Phase 26) ; MFA disponible comme facteur |
| 26-27 | Sécurité caisse / POS : session caisse ; PIN ; verrouillage ; changement opérateur ; terminal identifié ; **aucune confiance dans les montants du client** | `cash_session` + `CashSessionService` (Phase 6) ; `SalesService.CheckoutAsync` recalcule tout côté serveur (Phase 6 §42) ; PIN caissier : **backlog** |
| 28 | Sécurité API : auth forte ; autorisation serveur ; validation ; rate limiting ; **idempotency keys** ; **protection replay** ; logs sécurité ; **tokens courts** ; rotation secrets | Rate limiter (`Program.cs`, politique `auth`) ; `IdempotencyFilter` (Phase 18) ; **`mfa_challenge` à usage unique** (`consumed_at`) = anti-rejeu prouvé (`MfaAuthenticationTests`) ; access token court (`Jwt:AccessTokenMinutes`) ; `ISecurityEventLog` |
| 29 | Tokens : access court ; **refresh protégé + rotation + révocation + détection de réutilisation** ; stockage client sécurisé | Access token court + `security_epoch` (révocation globale). **Refresh token rotatif** : **backlog** (aujourd'hui : re-login) |
| 30 | CSRF / XSS / injection : CSP ; encodage sortie ; **requêtes paramétrées** ; validation fichiers | Auth par `Authorization: Bearer` (pas de cookie → surface CSRF nulle) ; toutes les requêtes SQL sont **paramétrées** (`FormattableString` EF) ; CSP `SecurityHeadersMiddleware` |
| 31-32 | Sécurité données ; données personnelles : chiffrement en transit ; secrets séparés ; **données sensibles masquées** ; export selon droits ; journalisation des accès sensibles | TLS ; `mfa_secret` / `mfa_pending_secret` non exposés par l'API ; codes de récupération **hachés** ; `setting_definition.is_sensitive` (Phase 32) ; `export_log` (Phase 7) |
| 33-34 | **Audit sécurité** : connexion réussie / échec ; MFA ; réinitialisation ; modif rôle / scope ; élévation ; révocation ; action sensible ; export ; impersonation. Journal : timestamp, user, ip/device, action, ressource, avant/après, résultat, corrélation ; **immuable** ; rétention ; recherche ; export | **`ISecurityEventLog.RecordAsync`** → insert dans `audit_log` (immuable, en portée système). Actions : `login.succeeded` / `login.failed` / `login.locked` / `login.blocked` / `login.company_denied` / `login.mfa_challenged` / `login.mfa_succeeded` / `mfa.enrolled` / `mfa.disabled` / `mfa.disable_failed` / `mfa.challenge_failed` / `mfa.challenge_locked` / `mfa.reset_by_admin`. Recherche + export : `IAuditQueryService` (Phase 11) |
| 35 | **Détection d'anomalies** : échecs répétés ; connexion inhabituelle ; multiples appareils ; élévation fréquente ; exports massifs ; accès hors scope | Le verrouillage `locked_until` traite les échecs répétés ; le reste est couvert par le moteur `fraud_rule` (Phase 11) qui lit désormais aussi les évènements `login.*` de l'`audit_log`. Règles dédiées « connexion inhabituelle » / « exports massifs » : **backlog** |
| 36 | Alertes sécurité : compte verrouillé ; **MFA désactivé** ; rôle critique modifié ; nouvel appareil ; export sensible ; tentative interdite ; modif coordonnées bancaires ; réouverture comptable | Les évènements sont journalisés ; `IAlertCenter` (Phase 7/31) + `notification` (Phase 16) restent à câbler sur `login.locked` / `mfa.disabled` : **backlog partiel** |
| 37 | **Politique par entreprise** : MFA obligatoire ; durée session ; seuil approbation ; appareils de confiance ; politique mot de passe ; export | `security_policy` (Phase 11) — `mfa_required_roles` désormais **exploité** : `IMfaService.PolicyRequiresMfaAsync` → `LoginHandler` renvoie `mfaEnrollmentRequired` ; l'écran Sécurité affiche l'alerte |
| 38-39 | Administration groupe / entreprise : gestion utilisateurs, rôles, politiques, audit — **sans accès automatique aux données métier** ; l'admin entreprise ne sort pas de son périmètre sauf permission groupe | `IUserAssignmentService` (Phase 22) borné au périmètre de l'appelant ; `ResetForUserAsync` vérifie `caller.CompanyIds ∩ target.CompanyIds` (ou groupe partagé) |
| 40-43 | Local / cloud / hybride / **permissions offline** : comptes locaux ; IAM centralisé ; cache minimal ; révocation prioritaire ; actions à haut risque interdites offline | Mode local/hybride/cloud + `offline_policy` (Phase 10) ; la file POS ne rejoue que des opérations pré-autorisées (Phase 10/19). Cache de permissions offline explicitement borné : partiellement backlog |
| 44 | Cycle utilisateur : invitation → activation → rôle → scope → utilisation → audit → suspension/révocation | `IUserAssignmentService` + `ISecurityEventLog` + `audit_log` |
| 45 | API principales | **`POST /api/v1/auth/login`** (renvoie `mfaRequired` + `mfaToken` si MFA active), **`POST /auth/mfa/verify`** ; **`GET /auth/mfa`**, **`POST /auth/mfa/enroll`**, **`POST /auth/mfa/enroll/confirm`**, **`POST /auth/mfa/disable`** ; **`POST /users/{id}/mfa/reset`**. Servis par les phases antérieures : `/users`, `/users/{id}/assignments*`, `/me/{scopes,permissions}` (Phase 22), `/security/{sessions,audit,approvals,step-up,policy}` (Phase 11). `/auth/refresh`, `/auth/logout`, `/auth/password/reset`, `/roles` CRUD, `/permissions`, `/access-policies`, `/devices`, `/elevations`, `/impersonation-requests` : **écart de nommage / backlog assumé** |
| 46 | Évènements (`LoginSucceeded`, `LoginFailed`, `MfaEnabled`, `MfaDisabled`, `SessionRevoked`, `RoleChanged`, `ScopeChanged`, `PrivilegeElevated`, `ImpersonationStarted/Ended`, `AccessDenied`…) | Journalisés dans `audit_log` via `ISecurityEventLog` (login/MFA) + triggers d'audit des tables `user_role` / `role` / `role_permission` (rôle & scope). Bus d'évènements dédié : **backlog commun aux phases 16-33** |
| 47 | Tests d'autorisation (caissier A → A seulement, comptable en chef → A+B, admin entreprise → autre refusée…) | `IdentityAccessTests` (Phase 22) — inchangés, toujours verts |
| 48 | **Tests sécurité** : brute force ; mot de passe faible ; reset token expiré ; **replay token** ; session révoquée ; **MFA invalide** ; CSRF ; XSS ; SQL injection ; IDOR ; scope manipulé ; export non autorisé ; élévation expirée | `MfaAuthenticationTests` (7) : enrôlement/confirmation, **défi puis TOTP → jeton**, **rejeu du défi refusé**, **code de récupération à usage unique**, **5 codes faux épuisent le défi**, **5 mots de passe faux verrouillent le compte**, réinitialisation admin, politique. `LoginHandlerTests` (+3) : verrou sans vérif du mot de passe, échec enregistré, défi MFA. Scope manipulé / IDOR / SQL injection : couverts par Phases 3/18/22 (RLS + params + `TenantResolutionMiddleware`) |
| 49 | Tests multi-entreprises (rôles différents, changement d'entreprise active, mauvais tenant, rapport groupe, offline, audit contexte) | `IdentityAccessTests` + `GroupConsolidationTests` (Phases 22/9) |
| 50 | Tests audit (chaque action critique enregistrée ; avant/après ; auteur ; scope ; résultat ; corrélation ; **impossible de masquer par le frontend**) | `SecurityAuditTests` (Phase 11) : trigger d'immuabilité prouvé. `MfaAuthenticationTests` vérifie la persistance des `used_at` / `consumed_at` |
| 51 | Critères de sortie | MFA + verrouillage + évènements de sécurité **livrés** ; RBAC / ABAC / scopes / multi-entreprises / comptable en chef / double approbation / élévation temporaire / sécurité caisse-POS / audit / anti-fraude / politiques entreprise **validés** (Phases 3-32) ; impersonation contrôlée / refresh token rotatif / registre d'appareils de confiance / flux d'invitation à jeton : **backlog explicite** |

## `V046` — contenu

- **`app_user`** += `mfa_pending_secret` (secret en cours d'enrôlement), `mfa_enrolled_at`,
  `failed_login_count` (`CHECK >= 0`), `locked_until` (verrouillage temporaire §4).
- **`mfa_recovery_code`** — `user_id` FK `ON DELETE CASCADE`, `code_hash` (SHA-256),
  `used_at` (usage unique), `UNIQUE (user_id, code_hash)`, index partiel `WHERE used_at IS NULL`.
- **`mfa_challenge`** — `user_id`, `company_id`, `token_hash`, `expires_at`, `consumed_at`,
  `attempts` (plafonné à 5 côté service).
- **RLS** `ENABLE + FORCE` sur les deux tables : `USING/WITH CHECK (rls_bypass() OR user_id =
  current_user_id())` (même schéma que `user_session` / `step_up_challenge`).
- **Permission** `security.reset_mfa` (super_administrator / group_administrator / owner_direction).
- **179 tables (+2), 167 en RLS (+2)**, 46 migrations (V001–V046).

## Backend

| Couche | Fichier |
|---|---|
| Domain | `Nexora.Domain/Identity/AppUser.cs` — `MfaPendingSecret`, `MfaEnrolledAt`, `FailedLoginCount`, `LockedUntil` |
| Contracts | `Nexora.Contracts/Auth/AuthContracts.cs` — `AuthTokenResponse` += `MfaRequired` / `MfaToken` / `MfaExpiresAt` / `MfaEnrollmentRequired` ; `MfaEnrollmentDto`, `MfaConfirmRequest`, `MfaRecoveryCodesDto`, `MfaDisableRequest`, `MfaVerifyLoginRequest`, `MfaStatusDto` |
| Application | `Identity/IMfaService.cs` (+ `MfaLoginTicket`), `Security/ISecurityEventLog.cs`, `Abstractions/Repositories.cs` (`IUserRepository` += `RegisterFailedLoginAsync` / `ResetFailedLoginsAsync`), `UseCases/Auth/{LoginHandler (réécrit), MfaVerifyHandler (neuf), AccessTokenIssuer (neuf)}` |
| Infrastructure | `Identity/Totp.cs` (RFC 6238 + Base32), `Identity/MfaService.cs` (`SqlMfaService`), `Security/SecurityEventLog.cs` (`SqlSecurityEventLog`), `Persistence/Repositories.cs` (verrouillage), `Persistence/NexoraDbContext.cs` (mapping colonnes), DI `Infrastructure/DependencyInjection.cs` + `Application/DependencyInjection.cs` |
| API | `Controllers/AuthController.cs` (réécrit : `login` + `mfa/verify`, `[AllowAnonymous]`), `Controllers/AuthMfaController.cs` (neuf : `GET /auth/mfa`, `enroll`, `enroll/confirm`, `disable` — authentifié), `Controllers/UsersController.cs` (`POST /users/{id}/mfa/reset`), `PermissionCatalog.Security.ResetMfa` |

### Points de conception

- **TOTP maison** (`Totp`, ~110 lignes) : RFC 6238 (HMAC-SHA1, 6 chiffres, pas 30 s, tolérance
  ±1 fenêtre, comparaison à temps constant) + Base32 RFC 4648. Compatible Google Authenticator,
  Aegis, 1Password. Aucune dépendance NuGet ajoutée.
- **Flux de connexion** : `LoginHandler` (1) refuse si `locked_until > now` **sans vérifier le
  mot de passe** ; (2) sur échec de mot de passe → `RegisterFailedLoginAsync` (5 → verrou 15 min) ;
  (3) si `mfa_enabled` → `mfa_challenge` + `AuthTokenResponse { MfaRequired = true, MfaToken }`
  (aucune permission divulguée avant le 2ᵉ facteur) ; (4) sinon jeton + `mfaEnrollmentRequired`
  si la politique l'impose. `MfaVerifyHandler` consomme le défi, revérifie le périmètre entreprise,
  émet le jeton via `AccessTokenIssuer` (partagé avec `LoginHandler`).
- **Pré-authentification** : `SqlMfaService` enveloppe toute opération de défi dans
  `tenant.BeginSystemScope()` (le contexte tenant n'existe pas encore au login), comme
  `UserRepository`.
- **Codes de récupération** : 10 codes `XXXXX-XXXXX` (alphabet sans ambiguïté), **stockés
  hachés**, à usage unique (`UPDATE … WHERE used_at IS NULL RETURNING count`), renvoyés en clair
  **une seule fois** à la confirmation.

## Tests d'intégration — `MfaAuthenticationTests` (7, `[Collection("finance")]`)

| Test | § | Vérifie |
|---|---|---|
| `Enroll_then_confirm_activates_mfa_and_returns_recovery_codes` | 8 | `BeginEnrollmentAsync` → URI `otpauth://`, `ConfirmEnrollmentAsync(code)` → `mfa_enabled = true`, `mfa_pending_secret` effacé, 10 codes de récupération |
| `Login_with_mfa_returns_a_challenge_then_totp_verify_issues_a_token` | 8, 28 | login → `MfaRequired`, `AccessToken` vide ; `/auth/mfa/verify(TOTP)` → jeton ; **rejeu du même défi refusé** |
| `A_recovery_code_authenticates_once_then_is_spent` | 8-9 | un code de récupération authentifie une fois puis `used_at` est posé et il est refusé |
| `Five_wrong_codes_exhaust_the_challenge` | 48 | 5 codes faux → 6ᵉ tentative → `auth.mfa_too_many_attempts` |
| `Five_bad_passwords_lock_the_account_temporarily` | 4, 7, 48 | 5 mots de passe faux → `locked_until` non nul ; le bon mot de passe est ensuite refusé (`auth.account_locked`) |
| `An_administrator_can_reset_a_users_mfa` | 9, 39 | `ResetForUserAsync` (owner_direction) efface MFA + codes de la cible ; `security.self_reset` sur soi-même |
| `Company_policy_flags_mfa_as_required_for_a_configured_role` | 37 | `security_policy.mfa_required_roles = ["cashier"]` → `PolicyRequiresMfaAsync(caissier) = true`, `(direction) = false` |

## Frontend

`src/security/` — `securityApi.ts` (`getMfaStatus`, `beginMfaEnrollment`, `confirmMfaEnrollment`,
`disableMfa`, `resetUserMfa`) + onglet **« Mon authentification »** ajouté à `SecurityPage.tsx`
(route `/security`) : statut MFA, activation guidée (secret + URI `otpauth` → code → **codes de
récupération affichés une fois**), désactivation, alerte si la politique impose la MFA.

`src/auth/` — `AuthContext` gère l'état `mfaPending` : `login()` détecte `mfaRequired` et ne pose
pas le jeton ; `verifyMfa(code)` appelle `/auth/mfa/verify` ; `cancelMfa()`. `LoginPage.tsx`
bascule en saisie de code à 6 chiffres (ou code de récupération) quand `mfaPending`.

Vitest `securityApi.test.ts` (5) → **40 tests frontend**.

## Smoke API (port 5080, `direction@abc-group.demo`)

- `GET /auth/mfa` → `{ enabled:false, policyRequires:false }` ; `POST /auth/mfa/enroll` → secret +
  `otpauth://totp/NEXORA:…` ; `POST /auth/mfa/enroll/confirm {code:<TOTP calculé>}` → 10 codes ;
  `GET /auth/mfa` → `enabled:true`.
- `POST /auth/login` avec MFA active → `{ mfaRequired:true, mfaToken:… , accessToken:"" }` ;
  `POST /auth/mfa/verify {mfaToken, code}` → jeton d'accès.
- `POST /auth/mfa/verify` rejoué → 401 ; code faux → 401 `auth.mfa_invalid`.
- Caissier sur `POST /users/{id}/mfa/reset` → **403** (permission `security.reset_mfa` absente).
- 5 `POST /auth/login` avec mot de passe erroné → `auth.invalid_credentials` puis 6ᵉ →
  `auth.account_locked` ; re-seed pour rétablir.

## Manques assumés (backlog)

- Impersonation support avec consentement + bannière + audit `impersonation.*` (§22)
- Refresh token rotatif + détection de réutilisation (§29) — aujourd'hui : access token court + re-login
- Flux d'invitation utilisateur à jeton expirable (`/users/invitations`) (§5)
- `/auth/password/reset` (jeton court sécurisé) & politique d'expiration de mot de passe (§7)
- Registre d'appareils de confiance à durée limitée (`/devices`) (§11)
- WebAuthn / Passkey (§8) ; SSO / OIDC (§6)
- Endpoint `/elevations` dédié au-dessus des affectations bornées de la Phase 22 (§23)
- Alertes temps réel sur `login.locked` / `mfa.disabled` / rôle critique modifié (§36)
- Règles anti-fraude « connexion inhabituelle » / « exports massifs » (§35)
- Bus d'évènements de sécurité (§46) — backlog commun aux phases 16-33
