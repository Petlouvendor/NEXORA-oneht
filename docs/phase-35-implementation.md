# Phase 35 — Intégrations & Écosystème — implémentation

**Statut : livré et vérifié.** Migration `V048`. 6 tests d'intégration (`ApiKeyPlatformTests`),
4 tests API HTTP (`ApiKeyAuthenticationTests`), 3 tests frontend (`apiKeysApi.test.ts`).
**235 tests backend** verts (18 unit + 11 API + 206 intégration), Debug **et** Release, 0 warning.
48 tests frontend.

Le socle d'intégration existe déjà et est **validé** par cette phase :

- **Phase 12** (`V031`) — `integration_connector` : **couche connecteurs remplaçable** (§2-5),
  états, santé, config non secrète, `credentials_ref` (pointeur vers coffre — jamais le secret §44).
  `payment_intent` + **adaptateur simulé → paiement réel** (§7-9). `webhook_event` : webhooks
  **entrants** HMAC + timestamp + **protection replay** + idempotence (§16). `outbound_webhook` /
  `outbound_webhook_delivery` : webhooks **sortants** signés + **retry + backoff + dead-letter**
  (§17, §46). Rapprochement bancaire + **import de relevé** comme secours (§6, §47). Centre
  d'intégration + observabilité transverse (§4, §42, §53)
- **Phase 10** (`V017`/`V029`) — moteur de synchronisation, curseurs, reprise après erreur,
  file locale, mode hybride (§18, §49)
- **Phase 34** (`V047`) — `notification_outbox` + distributeur : retry / backoff / dead-letter,
  `INotificationChannel` remplaçable pour email/SMS/webhook (§31-32, §46)
- **Phase 7** — `CsvExportService` + `export_log` : export CSV filtré & journalisé (§22)
- **Phase 3/18** — API **versionnée** `/api/v1`, `IdempotencyFilter` (§45), rate limiter,
  RFC 7807, `TenantResolutionMiddleware` (le `X-Company-Id` ne fait que *sélectionner* §13)
- **Phase 33** (`V046`) — MFA / sessions / verrouillage (auth **humaine**)

La Phase 35 ajoute la brique qui manquait à l'API publique (§13-14, §43-44, §52) :
**l'authentification machine-à-machine par clé d'API**. Les humains s'authentifient par
JWT + MFA ; une intégration serveur s'authentifie par une clé à **périmètre borné**, expirable,
rotable, révocable, avec **quota par minute**.

---

## Correspondance cahier des charges → implémentation

| § | Exigence | Où |
|---|---|---|
| 1-2 | Cœur métier indépendant ; connecteurs remplaçables, sécurisés, traçables, par entreprise ; une erreur externe ne corrompt pas les données ; chaque échange a une référence & un statut | `integration_connector` (adaptateur par connecteur, `credentials_ref`, RLS entreprise), `payment_intent` / `webhook_event` (référence + statut), transactions atomiques côté cœur (Phase 4) — **Phase 12, inchangé** |
| 3 | API REST, webhooks, CSV/Excel, SFTP, import/export manuel, SDK, connecteurs locaux/cloud | REST `/api/v1` ; webhooks in/out (Phase 12) ; CSV export (Phase 7) ; import de relevé bancaire (Phase 12). SFTP / SDK / Excel : **backlog** |
| 4-5 | Connecteurs : nom, type, fournisseur, version, entreprise, environnement, statut, dernière synchro, **santé**, config, permissions ; états non-configuré → révoqué | `integration_connector` + `IIntegrationCenter` (Phase 12) : `connector_type`, `provider`, `environment` (test/production), `status`, `last_sync_at`, `last_error`, santé calculée. États intermédiaires (dégradé/suspendu) : partiellement backlog |
| 6-9 | Banques (import relevé + secours), Mobile Money, cartes, **passerelle de paiement** (vérif serveur, signature webhook, doublons) | `IBankReconciliationService` + import de relevé ; `IPaymentGateway` + `payment_intent` + adaptateur simulé → paiement réel + `IWebhookIngestService` (HMAC, replay, idempotence) — **Phase 12**. Connecteurs MM / carte réels : **backlog** (l'abstraction existe) |
| 10-12, 37-38 | E-commerce / marketplace / boutique : produits, prix, stock, clients, commandes, paiements, expéditions, retours, avoirs ; stock centralisé, réservation, décrémentation | Le cœur expose déjà `OrderService` (réservation §25 Phase 25), `IStockLedger` (Phase 5), catalogue (Phase 23), documents (Phase 30). L'**API publique** (avec clé + scopes) rend ces services consommables par un connecteur e-commerce. Connecteur Shopify/WooCommerce dédié + mapping : **backlog** |
| 13 | **API publique** : versionnée, OAuth 2.0/OIDC, **clés API pour intégrations serveur**, **scopes**, **rate limits**, quotas, journalisation | **`api_key`** (`V048`) — clé par entreprise, `scopes` jsonb (codes de permission), `rate_limit_per_minute`, `owner_user_id` (identité d'exécution §35), `environment` test/live. **`ApiKeyAuthenticationMiddleware`** : `X-Api-Key` (ou `Authorization: Bearer nxk_…`) → identité machine « ApiKey » + claim `nx_api_scopes`. `TenantResolutionMiddleware` borne l'entreprise active à celle de la clé. `PermissionAuthorizationHandler` : accès = permission du **propriétaire** ∩ **scopes de la clé**. Quota : fenêtre glissante en mémoire par clé → **429** + `Retry-After`. OAuth 2.0/OIDC (client credentials) : **backlog** |
| 14 | **API Keys** : création, nom, scope, expiration, rotation, révocation, dernière utilisation, **jamais afficher le secret après création** | **`IApiKeyService`** : `CreateAsync` (secret `nxk_{env}_{prefix}_{random}` renvoyé **une seule fois** ; en base, seul le **SHA-256** est stocké), `RotateAsync` (révoque l'ancienne, émet une nouvelle avec le même périmètre, `rotated_from`), `RevokeAsync`, `ListAsync` (jamais de champ secret). `expires_at`, `last_used_at` / `last_used_ip` horodatés à chaque résolution. `GET/POST /api/v1/api-keys` + `/{id}/rotate` + `/{id}/revoke` (`integrations.manage_api_keys`) |
| 15 | OAuth : client ID, secret protégé, scopes, consentement, tokens courts, refresh, révocation | **Backlog** — la clé d'API couvre le cas « intégration serveur de confiance » ; OAuth client-credentials + code d'autorisation restent à faire |
| 16-17 | Webhooks entrants (signature, timestamp, replay, event ID, idempotence, validation, IP allowlist, journal) & sortants (retry, backoff, timeout, dead-letter, historique) | **Phase 12** (`webhook_event`, `outbound_webhook(_delivery)`, `IWebhookIngestService`, `IOutboundWebhookService`) — inchangé, validé. IP allowlist : backlog |
| 18-20 | Synchronisation (temps réel / périodique / manuelle, curseur, reprise) ; **mapping** externe → NEXORA ; **gestion des conflits** (source prioritaire, date, version, file, résolution manuelle, aucun écrasement silencieux) | Moteur de synchro + curseurs + reprise : **Phase 10**. Tables `external_mapping` + `sync_job` + `sync_conflict` + `/api/v1/sync/{jobs,conflicts}` : **backlog** (le socle de synchro offline existe, la synchro *source externe* avec mapping/conflits reste à formaliser) |
| 21-22 | Import fichiers (CSV/Excel, validation colonnes, prévisualisation, mapping, erreurs ligne par ligne, rapport, rollback) ; export (CSV/Excel/PDF/JSON, comptable/financier/stock/clients) | Export CSV + `export_log` (Phase 7) ; rendu PDF documentaire (Phase 30). Import CSV générique avec prévisualisation & erreurs par ligne : **backlog** |
| 23-24 | Comptabilité externe (export écritures, mapping plan comptable, journaux, tiers, taxes, période, statut, référence, rapprochement) ; fiscalité (exports, formats, déclarations, contrôles, historique) | Balance / grand livre / journaux exportables (Phase 29) ; `automation` kind `accounting_export` (Phase 14, exécuteur stub). Connecteur comptable tiers (Sage, QuickBooks) + connecteur fiscal officiel : **backlog** |
| 25-29 | Imprimantes (thermique / A4 / réseau / USB, file, état, réimpression) ; scanners (code-barres / QR / HID / caméra) ; balances ; **terminaux POS** (ID, entreprise, établissement, caisse, version, dernière connexion, **clé/identité appareil**, révocation) ; tiroir-caisse | `pos_terminal` + registre d'appareils + révocation : **Phases 12/22/28** ; scan POS (`unaccent`/`pg_trgm`) : **Phase 23** ; produits au poids (`quantity numeric(20,6)`) : **Phase 2**. Rendu ticket / A4 : **Phase 30**. Pilotes matériel (impression directe, HID, balance série) : **backlog** (côté client / agent local) |
| 30 | Livraison (expédition, transporteur, tracking, statut, retour, frais, référence externe) | `delivery_note` (Phase 30) ; connecteur transporteur + tracking : **backlog** |
| 31-33 | SMS / Email / WhatsApp (provider configurable, templates, statut, retry, quota, opt-out) ; stockage documentaire (local / objet cloud, métadonnées, hash, version, URL temporaire) | **Phase 34** : `message_template`, `notification_outbox` + distributeur (retry / dead-letter), `INotificationChannel` remplaçable. `issued_document` + hash SHA-256 + snapshot (Phase 30). Stockage objet cloud + URL signée temporaire : **backlog** |
| 34 | **SSO** : OIDC / SAML, mapping utilisateur & groupes, provisioning / déprovisioning, MFA selon IdP | `app_user.external_identity` prévu (Phase 4) ; connecteur OIDC / SAML + SCIM : **backlog** |
| 35-36 | Comptabilité / ERP tiers ; CRM (contacts, leads, clients, historique, segments, interactions) | Via l'API publique + clés d'API. Connecteurs dédiés : **backlog** |
| 39-41 | SDK (TS / .NET, exemples, versionnement) ; portail développeurs (doc, clés, apps, webhooks, logs, sandbox, quotas) ; sandbox (env test, données fictives, secrets séparés, reset) | `api_key.environment` = `test` / `live` (secrets séparés §41) ; OpenAPI généré (`/swagger`). SDK packagés + portail développeurs + sandbox à données fictives : **backlog** |
| 42 | Observabilité (latence, taux succès/erreur, retries, webhooks en attente, synchro retardée, dernière activité, alertes) | `IIntegrationCenter` (Phase 12) : connecteurs en erreur, webhooks en échec, livraisons dues, exceptions bancaires ; `api_key.last_used_at`. Métriques de latence / taux : **backlog** |
| 43-44 | **Sécurité** : secrets en coffre, rotation, scopes minimaux, signature, TLS, validation certificats, replay, rate limiting, audit, **isolation tenant**. **Secrets** : clés API / OAuth / certificats / webhook / tokens ; **aucun secret dans les logs ni les exports** | `api_key.key_hash` = SHA-256 (jamais le secret) ; comparaison à **temps constant** (`CryptographicOperations.FixedTimeEquals`) ; RLS `enable_company_rls` sur `api_key` ; `trg_audit_api_key` ; rotation `RotateAsync` ; quota par minute ; le DTO de liste n'expose jamais le secret ; `integration_connector.credentials_ref` (Phase 12) pointe vers un coffre |
| 45 | **Idempotence** (idempotency key → check → replay / exécuter) | `IdempotencyFilter` (Phase 18) — inchangé |
| 46 | **Retry** (temporaire, backoff exponentiel, limite, dead-letter, rejeu manuel, pas de retry pour erreur métier permanente) | `outbound_webhook_delivery` (Phase 12) + `notification_outbox` (Phase 34) : backoff `2^n`, `max_attempts`, dead-letter, rejeu |
| 47 | Réconciliation externe (montant, référence, date, statut, frais, devise, source, destination, écart, validation) | `IBankReconciliationService` + `IPaymentReconciliationService` (Phase 12) |
| 48 | **Multi-entreprises** : connecteur par entreprise, secrets séparés, scopes séparés, données externes isolées, admin centralisée selon permission | `api_key.company_id` (RLS) ; une clé n'authentifie que **son** entreprise (l'entreprise active y est forcée) ; scopes propres à chaque clé ; `integration_connector` par entreprise (Phase 12) |
| 49 | Local / cloud / hybride (connecteurs, proxy, queue locale, reprise réseau, mode dégradé) | Phase 10 (sync offline, file locale) ; les clés d'API fonctionnent à l'identique quel que soit le mode |
| 50-51 | Marketplace de connecteurs (catalogue, version, compatibilité, permissions demandées, certification, install/update/uninstall) ; certification (tests sécurité / API / idempotence / isolation / erreurs, signature du package) | **Backlog** — modèle proche de `sector_profile` (Phase 32) : un `connector_package` versionné, à permissions déclarées, installable |
| 52 | API principales | **`GET/POST /api/v1/api-keys`**, **`POST /api-keys/{id}/rotate`**, **`POST /api-keys/{id}/revoke`**. `/integrations/*` + `/webhooks/*` : Phase 12. `/sync/jobs`, `/sync/conflicts`, `/integration-logs` : **écart de nommage / backlog assumé** |
| 53 | Événements | Journalisés via `webhook_event` / `outbound_webhook_delivery` / `integration_connector.last_error` + `audit_log` ; `trg_audit_api_key`. Bus d'évènements dédié : backlog commun aux phases 16-35 |
| 54 | Tests | `ApiKeyPlatformTests` (6) + `ApiKeyAuthenticationTests` (4) — cf. ci-dessous. Webhook signé / doublon / retry / dead-letter / rapprochement : `IntegrationEcosystemTests` (Phase 12) |
| 55 | Scénario e-commerce complet (commande → réservation → paiement externe → webhook → facture → compta → livraison) | Chaque maillon existe (`OrderService`, `IStockLedger`, `IPaymentGateway`, `IWebhookIngestService`, `IDocumentService`, moteur comptable) ; le connecteur e-commerce qui les orchestre via l'API publique reste à écrire |
| 56 | Critères de sortie | API publique + **clés d'API** (création / scope borné / expiration / rotation / révocation / quota / dernière utilisation / secret jamais ré-affiché) : **livré**. Couche connecteurs, webhooks, idempotence, retry, réconciliation, multi-entreprises, observabilité : **Phase 12, validé**. OAuth, SSO, sync externe + mapping + conflits, import CSV, connecteurs métier (e-commerce / comptable / transporteur), SDK, portail dev, marketplace de connecteurs : **backlog explicite** |

## `V048` — contenu

- **`api_key`** — `company_id` (RLS), `name`, `key_prefix` (visible, unique), `key_hash`
  (SHA-256, unique), `environment` (`test`/`live`), `scopes` jsonb, `rate_limit_per_minute`
  (`CHECK 1..100000`), `owner_user_id` (identité d'exécution), `expires_at`, `last_used_at` /
  `last_used_ip`, `revoked_at` / `revoked_by`, `rotated_from` (traçabilité de rotation).
  Index partiel `api_key_active_idx (key_prefix) WHERE revoked_at IS NULL`.
- **RLS** `enable_company_rls` + `trg_audit_api_key`.
- Permission `integrations.manage_api_keys` (super_administrator / group_administrator /
  owner_direction).
- **183 tables (+1), 171 en RLS (+1)**, 48 migrations (V001–V048).

## Backend

| Couche | Fichier |
|---|---|
| Contracts | `Nexora.Contracts/Integrations/ApiKeyContracts.cs` — `ApiKeyDto`, `CreateApiKeyRequest`, `ApiKeyCreatedDto` |
| Application | `Nexora.Application/Integrations/IApiKeys.cs` — `IApiKeyService`, `IApiKeyAuthenticator` (+ `ApiKeyResolution` / `ApiKeyOutcome`) ; `Abstractions/ITenantContext.cs` += `ApiScopes` / `SetApiScopes` |
| Infrastructure | `Nexora.Infrastructure/Integrations/ApiKeyService.cs` — `SqlApiKeyService` (create / rotate / revoke / list, borne du périmètre aux permissions du propriétaire), `SqlApiKeyAuthenticator` (résolution par préfixe + comparaison de hash à temps constant + horodatage d'usage) ; `Tenancy/TenantContext.cs` impl ; DI dans `Infrastructure/DependencyInjection.cs` |
| API | `Middleware/ApiKeyAuthenticationMiddleware.cs` (identité machine + quota/minute → 429), `Middleware/TenantResolutionMiddleware.cs` (entreprise active forcée + lecture du scope), `Security/PermissionAuthorization.cs` (filtre : permission ∩ scope de la clé), `Controllers/ApiKeysController.cs`, `PermissionCatalog.Integrations.ManageApiKeys`, `Program.cs` (middleware inséré après `UseAuthentication`) |

### Points de conception

- **Format du secret** : `nxk_{env}_{prefix8}_{random}` — le préfixe (`nxk_live_ab12cd34`) est
  stocké en clair et sert de clé de recherche ; le secret complet n'est jamais persisté, seul son
  SHA-256 l'est. Comparaison **à temps constant**.
- **Périmètre borné à deux niveaux** : une clé ne peut être créée qu'avec des scopes que son
  créateur détient (`api_key.scope_excess`) ; à chaque requête, `PermissionAuthorizationHandler`
  exige `permission ∈ (permissions du propriétaire) ∩ (scopes de la clé)`.
- **Quota** : fenêtre `yyyyMMddHHmm` en `IMemoryCache`, par `key_id` → 429 + `Retry-After: 60`.
- **Entreprise active** : une clé n'autorise qu'une entreprise → `TenantResolutionMiddleware` la
  force comme entreprise active (aucun `X-Company-Id` attendu).

## Tests

**`ApiKeyPlatformTests` (6, `[Collection("finance")]`)** — service :

| Test | § | Vérifie |
|---|---|---|
| `Create_returns_the_secret_once_and_the_list_never_does` | 14 | secret `nxk_live_…` renvoyé une fois, `key_hash` en base ≠ secret et fait 64 hex |
| `A_scope_outside_the_owner_permissions_is_refused` | 13 | scope inconnu → `api_key.scope_excess` |
| `The_authenticator_resolves_a_valid_key_and_stamps_last_used` | 13 | `ResolveAsync` → `Ok` + company/owner/scopes/quota ; `last_used_at` posé ; secret bidon → `Invalid` |
| `A_revoked_key_no_longer_resolves` | 14 | après `RevokeAsync` → `Revoked` ; double révocation → `not_found` |
| `An_expired_key_no_longer_resolves` | 14 | `expires_at` dans le passé → `Expired` |
| `Rotation_kills_the_old_secret_and_issues_a_new_one` | 14 | ancien secret → `Revoked`, nouveau → `Ok`, même périmètre, `rotated_from` renseigné |

**`ApiKeyAuthenticationTests` (4, `WebApplicationFactory`)** — pipeline HTTP :

| Test | Vérifie |
|---|---|
| `Unknown_api_key_is_rejected_with_401` | `X-Api-Key` inconnu → 401 `api_key_invalid` |
| `Revoked_api_key_is_rejected_with_401` | clé révoquée → 401 `api_key_revoked` |
| `A_key_whose_scope_covers_the_endpoint_is_authorized` | scope `integrations.view` → `GET /integrations/connectors` → **200** |
| `A_key_whose_scope_does_not_cover_the_endpoint_is_forbidden` | scope `catalog.view` seul, propriétaire a `integrations.view` → **403** |

*Note fixture* : `FinanceEngineFixture` réessaie `CREATE DATABASE … TEMPLATE nexora` sur
`55006 object_in_use` — les tests API touchent désormais la base `nexora` en parallèle.

## Frontend

`src/integrations/` — `integrationsApi.ts` += `listApiKeys` / `createApiKey` / `rotateApiKey` /
`revokeApiKey` ; section **« Clés d'API »** ajoutée à `IntegrationsCenterPage.tsx` (route
`/integrations`, gate `integrations.manage_api_keys`) : table (préfixe, env, scopes, quota,
dernier usage, statut) avec **Pivoter / Révoquer**, formulaire de création (nom + scopes +
expiration), **encart de secret affiché une seule fois**. Vitest `apiKeysApi.test.ts` (3) →
**48 tests frontend**.

## Smoke API (port 5080, `direction@abc-group.demo`)

- `POST /api-keys {scopes:["integrations.view"]}` → préfixe `nxk_live_…`, secret 42 c., `active` ;
  `GET /api-keys` → aucun champ secret.
- `GET /integrations/connectors` avec `X-Api-Key` → **200** ; `GET /catalog/products` avec la même
  clé → **403** (hors scope).
- `POST /api-keys {scopes:["totally.made.up"]}` → **403 `api_key.scope_excess`**.
- `POST /api-keys/{id}/revoke` → 204 ; réutilisation de la clé → **401**.
- Caissier sur `GET /api-keys` → **403**.

## Manques assumés (backlog)

- OAuth 2.0 / OIDC (client-credentials & authorization-code) au-delà de la clé d'API (§15)
- SSO entrant OIDC / SAML + SCIM (§34)
- Synchronisation *source externe* : `external_mapping`, `sync_job`, `sync_conflict` +
  `/api/v1/sync/{jobs,conflicts}` (le socle de synchro offline existe déjà — Phase 10) (§18-20)
- Import CSV générique (validation de colonnes, prévisualisation, erreurs par ligne, rapport) (§21)
- Connecteurs métier concrets : e-commerce (Shopify/WooCommerce), comptable (Sage/QuickBooks),
  transporteur, Mobile Money, carte (§6-12, §30, §35-36)
- SDK TS/.NET packagés, portail développeurs, sandbox à données fictives (§39-41)
- Marketplace de connecteurs + certification (§50-51)
- Pilotes matériel (impression directe, HID, balance série) — côté agent local (§25-27)
- Métriques d'observabilité (latence, taux) + `integration-logs` unifiés (§42, §52)
