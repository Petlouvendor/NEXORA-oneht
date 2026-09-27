# Phase 46 — API, intégrations & écosystème — implémentation

**Statut : livré et vérifié.** Migration `V059` (3 tables neuves, 2 colonnes sur `api_key`,
2 permissions, 3 apps officielles amorcées). 6 tests d'intégration
(`MarketplaceEcosystemTests`), 4 tests API (`MarketplaceEndpointsTests`), 7 tests frontend
(`marketplaceApi.test.ts`).
**350 tests backend** verts (18 unit + 42 API + 290 intégration), 0 warning, vérifiés après un
rebuild complet de la base (`V001`→`V059`, 245 tables). 96 tests frontend. `tsc` vert.

Le socle d'ouverture existait déjà et reste **validé**, volontairement **non reconstruit** :
Phase 12 (connecteurs, paiements, webhooks entrants/sortants signés, rapprochement bancaire),
Phase 35 (`api_key` — auth machine, scopes ⊆ permissions du propriétaire, rotation, rate limit),
Phase 3 (`/api/v1`, idempotence, erreurs RFC 7807). Cette phase ajoute la couche qui manquait
réellement : un **catalogue marketplace** curé par NEXORA (soumission → revue humaine →
publication), une **installation par entreprise** à permissions explicitement accordées (jamais
héritées de ce que l'app a demandé), le raccordement de `api_key` à une installation (une app
obtient un accès API réel via le moteur existant, jamais un second système d'authentification), et
un **portail développeur** minimal (usage quotidien par clé).

### Socle réutilisé

- **Phase 3** — `/api/v1`, `IdempotencyFilter`, enveloppe d'erreur RFC 7807 → non retouchés
- **Phase 11** — `audit_log` immuable → `marketplace_app`/`app_installation` audités par les mêmes
  triggers génériques (`trg_audit_*`), aucune table d'audit dédiée
- **Phase 12** — `integration_connector`/`payment_intent`/`webhook_event`/`outbound_webhook(_delivery)`
  **validés**, non reconstruits ; `/api/v1/integrations` (bare), `/webhooks` sont des **alias** sur
  ces mêmes services, jamais une seconde logique
- **Phase 35** — `api_key`/`IApiKeyService`/`ApiKeyAuthenticationMiddleware` **étendus**, pas
  dupliqués : une clé émise pour une app installée reste le même objet `api_key`, avec une colonne
  `app_installation_id` en plus et une seconde borne de scope
- **Phase 38** — motif « table plateforme sans RLS entreprise » (`platform_plan`) réutilisé
  identiquement pour `marketplace_app` : un seul catalogue pour tout NEXORA ONE, jamais une vue par
  entreprise
- **Phase 39** — pipeline d'import générique (§50 du cahier des charges) déjà couvert, non reconstruit
- **Phase 45** — leçon retenue sur les FK composites (`document_signature_request_company_id_uk`) :
  `api_key_company_id_uk UNIQUE(id, company_id)` ajoutée explicitement pour permettre
  `app_installation(id, company_id) → api_key(id, company_id)`

---

## Correspondance cahier des charges → implémentation

| § | Exigence | Où |
|---|---|---|
| 4-12 | API publique REST/JSON, versionnement, auth, autorisation, rate limiting, idempotence, pagination, erreurs | **Hérité et validé** (Phase 3, 35) — non reconstruit. Nouveauté cette phase : **pagination par curseur** (§11), première du genre dans la base de code (`CursorPage<T>`, tri stable `created_at DESC, id DESC`, curseur opaque base64) utilisée par `GET /apps` — coexiste délibérément avec `PagedResult<T>` (pagination par page, Phase 3) plutôt que de la remplacer |
| 6-8 | OAuth 2.0, API keys, client credentials, service accounts | **API keys** (Phase 35) restent le seul mécanisme machine-à-machine construit — pas de flux OAuth2 standard (authorization-code/client-credentials avec endpoint `/oauth/token`) séparé ; `CreateApiKeyRequest.AppInstallationId` fait d'une clé un véritable « service account » d'une app (§8), avec propriétaire, scopes minimaux, expiration, rotation, révocation, audit — tous hérités de Phase 35 |
| 13-14 | Webhooks sortants, signature HMAC, retry/backoff/dead-letter | **Hérité et validé** (Phase 12) — `GET/POST /webhooks` sont des alias directs sur `IOutboundWebhookService` |
| 15 | Événements principaux (15 types) | **Partiellement câblés** — seuls `payment.confirmed`/`payment.refunded` sont émis automatiquement (Phase 12, `PaymentGatewayService`) ; les 13 autres (`sale.*`, `invoice.*`, `stock.updated`, `customer.created`, `document.*`, `subscription.updated`, `user.updated`, `sync.completed`) existent comme **valeurs possibles** dans `outbound_webhook.subscribed_events` et via `POST /webhooks/outbound/emit` (déclenchement manuel) mais ne sont **pas** émis automatiquement par les services métier correspondants (Sales, Purchasing, Stock, CRM, GED, SaaS) — **backlog explicite**, un chantier transverse touchant de nombreux modules, délibérément hors du périmètre resserré de cette phase |
| 16 | Sandbox | **Hérité** (Phase 12 `sim.webhook.local` + callback fournisseur simulé ; Phase 35 `environment: test/live` sur `api_key`) — pas de réinitialisation de données de test dédiée, pas de bac à sable marketplace séparé |
| 17-34 | API partenaires, comptabilité, finance, POS, paiements, banques, mobile money, SMS/email, messagerie, e-commerce, livraison, signature, OCR, matériel | **Ce sont les API métier de NEXORA elles-mêmes** (Sales/Finance/Accounting/POS des phases 4-10, signature/OCR de la Phase 45, paiements/banques de la Phase 12) — cette phase ne les reconstruit pas, elle ajoute la couche écosystème (scopes, service accounts, marketplace) par-dessus. Connecteurs réels vers des fournisseurs externes (SMS/email/e-commerce/livraison/matériel) restent **simulés** comme en Phase 12 — backlog assumé dès cette phase-là |
| 35-40 | Connecteurs (config/auth/mapping/test/activation/sync/monitoring), conflits, centre d'intégrations | **Hérité et validé** (Phase 12 `IIntegrationRegistry`, `IIntegrationCenter`) ; nouveauté : alias canoniques §58 `GET/POST /integrations`, `GET /integrations/{id}/health`, `POST /integrations/{id}/test`, `POST /integrations/{id}/sync` sur `IntegrationsController` existant — `sync` et `test` appellent la **même** méthode (`TestConnectionAsync`) car l'architecture n'a pas d'adaptateur réel à synchroniser différemment (documenté explicitement dans le code) |
| 41-42 | Marketplace : catalogue, catégories, recherche, fiche, éditeur, permissions demandées, prix, installation/désinstallation ; officielle/certifiée/communautaire | **`marketplace_app`** — 15 catégories, 3 niveaux de certification, modèle tarifaire (gratuit/payant/abonnement/usage) avec prix optionnel. Recherche : filtre catégorie + statut seulement (pas de recherche texte plein — **backlog**). 3 apps officielles amorcées (mobile money, export comptable, sync e-commerce) |
| 43-44 | Installation (choisir→permissions→autoriser→configurer→tester→activer) ; permissions marketplace (périmètre explicite par domaine) | **`app_installation`** — `InstallAsync` **refuse** tout scope demandé par l'entreprise hors de `marketplace_app.requested_scopes` (`marketplace.scope_excess`) ; réinstaller une app déjà installée **met à jour** la même ligne (`ON CONFLICT`), jamais une seconde ligne. Étape « tester » du flux (§43) — pas d'action dédiée, `POST /integrations/{id}/test` existe pour les connecteurs mais aucune app marketplace de cette phase n'a de test de connexion propre (aucune n'a de credentials externes réels) |
| 45 | App review (identité éditeur/sécurité/scopes/doc/tests/confidentialité/support/version/date) | **`ReviewAsync`** — décision `approve/reject/suspend`, horodatée, notée. Garde à 4 yeux **stricte** : le soumissionnaire (`submitted_by`) ne peut jamais réviser sa propre soumission (`marketplace.self_review`), même s'il détient par ailleurs `platform.manage_marketplace`. Une décision non `pending_review` ne peut pas être rejouée. Champs identité/sécurité/doc/tests eux-mêmes : **déclaratifs** (texte libre dans `description`), aucune vérification automatisée — la revue reste un acte humain, conforme à l'intention du §45 |
| 46 | Secret management (chiffré, jamais dans les logs, rotation, expiration, révocation, accès minimal, audit) | **Hérité intégralement de Phase 35** — aucun second mécanisme de secret pour les apps marketplace. Nouveauté : désinstaller une app **révoque immédiatement** toute clé émise pour son installation (`UninstallAsync` → `UPDATE api_key SET revoked_at = now() WHERE app_installation_id = …`) — aucun accès résiduel |
| 47 | Partner portal (créer app, clés sandbox/prod, webhooks, doc, logs, usage, erreurs, support, certification) | **Partiellement construit et honnêtement limité** : pas de portail self-service séparé avec sa propre identité « partenaire » — `POST /apps` (soumission) et `POST /apps/{id}/review` sont réservés au personnel plateforme (`platform.manage_marketplace`) exactement comme la curation de `platform_plan` (Phase 38). Un éditeur externe n'a aujourd'hui aucun compte NEXORA propre pour soumettre lui-même une app — **backlog explicite**, nécessiterait un système d'identité partenaire distinct des utilisateurs entreprise et du personnel plateforme |
| 47-48 | Developer portal (clés, usage, erreurs) | **`GET/POST /developer/keys`**, `POST /developer/keys/{id}/revoke` (alias sur `IApiKeyService`) + **`GET /developer/usage`** (nouveau, `IDeveloperUsageQuery`) — agrège `api_key_daily_usage` par clé/jour sur une fenêtre glissante (défaut 30 j, max 90). Documentation API (OpenAPI/exemples/SDK/changelog/guides/status) — **backlog**, aucune génération Swagger/OpenAPI ni documentation publiée cette phase |
| 49 | SDK (JS/TS, PHP, Python, mobile) | **Backlog explicite** — aucun SDK généré ni maintenu cette phase |
| 50 | Import/export externe (CSV/XLSX/JSON/API/SFTP) | **Hérité** (Phase 39, `IImportPipeline`) — non reconstruit |
| 51-52 | Sécurité (OAuth/scopes/rate limit/WAF/signature webhook/rotation/audit/isolation tenant/protection SSRF/allowlist) ; sorties réseau (allowlist/timeout/TLS/SSRF) | **Écart assumé et documenté** : `RegisterOutboundWebhookRequest.TargetUrl` (Phase 12, `WebhookServices.cs`) valide uniquement le schéma (`http`/`https`), **aucune protection SSRF** (pas de blocage d'IP privée/loopback/lien-local, pas d'allowlist de domaines) — un attaquant avec accès à `integrations.webhooks` pourrait enregistrer une cible interne. **Backlog de sécurité explicite**, à traiter avant toute exposition à des URLs non fiables |
| 53 | Confidentialité (minimisation, scopes stricts, consentement, révocation, suppression tokens, pas de vente de données) | Scopes stricts et révocation — **couverts** (§44, §46) ; consentement explicite/CGU par app — **backlog**, aucun écran de consentement dédié au-delà de l'affichage des scopes demandés |
| 54 | Disponibilité (circuit breaker, retry, fallback, queue, dead-letter, mode manuel) | **Hérité** (Phase 12 : retry/backoff/dead-letter sur les webhooks sortants) — pas de circuit breaker dédié aux apps marketplace elles-mêmes (aucune n'appelle un service externe réel cette phase) |
| 55 | Facturation intégrations (gratuit/payant/abonnement/usage/commission/facture/renouvellement) | **Affichage seulement** — `marketplace_app.pricing_model`/`price_amount`/`price_currency_code` sont stockés et affichés, **aucun flux de facturation réel** (pas de charge, pas de calcul de commission, pas de facture générée) — backlog explicite, cohérent avec les 3 apps amorcées étant toutes gratuites |
| 56 | Marketplace multi-entreprises (installation groupe/entreprise, scope explicite, aucun accès implicite) | **Installation au niveau entreprise uniquement** — `app_installation.company_id`, RLS stricte, `UNIQUE(company_id, marketplace_app_id)` : aucune fuite entre entreprises, aucune installation groupe transverse cette phase (cohérent avec l'absence de colonne `group_id` sur la table) — **backlog** si un jour une installation groupe-large est nécessaire |
| 57 | Audit (installation/autorisation/scope/clé créée/révoquée/connecteur activé/désinstallation) | **`marketplace_app`/`app_installation`** audités par les triggers génériques existants (`trg_audit_*`, Phase 11) — aucune requête d'audit dédiée construite cette phase (contrairement à `IGedAuditQuery` en Phase 45) ; la table `audit_log` elle-même est déjà interrogeable génériquement |
| 58 | API principales | Toutes les 13 routes servies — voir § Backend/API. Correspondance exacte avec la liste du cahier des charges |
| 59 | Tests | `MarketplaceEcosystemTests` (6) + `MarketplaceEndpointsTests` (4) — cf. ci-dessous |
| 60 | Scénario paiement | **Déjà couvert par Phase 12** (`PaymentGatewayService` : intention→callback→webhook signé→idempotence→confirmation→réconciliation) — non reconstruit |
| 61 | Scénario marketplace (admin→marketplace→app→permissions→installation→config→test→activation→monitoring) | Reproduit par `Install_app_and_issue_a_scoped_developer_key_over_http` (choix app→scopes accordés→installation→clé développeur→révocation→désinstallation) ; étape « monitoring » — pas de tableau de bord dédié par app installée, `GET /developer/usage` couvre l'usage des clés |
| 62 | Critères de sortie | API publique/versionnement/auth/scopes/service accounts/rate limiting/idempotence/pagination/erreurs/webhooks : **hérités et validés**. Marketplace/apps/installation/permissions/app review/secrets : **livrés**. Partner portal self-service, developer portal complet (OpenAPI/SDK), sécurité réseau (SSRF), facturation réelle, multi-entreprises groupe, événements automatiques complets : **backlog explicite**, détaillé ci-dessous |
| 63 | Étape suivante — Phase 47 (Sécurité avancée, IAM, conformité) | hors périmètre |

## `V059` — contenu (3 tables neuves, 2 colonnes, 2 permissions)

- **`marketplace_app`** (NEW, **sans RLS entreprise** — table plateforme comme `platform_plan`,
  Phase 38) — catalogue curé par NEXORA : slug unique, 15 catégories, 3 niveaux de certification,
  cycle `draft→pending_review→approved/rejected/suspended`, scopes demandés (jsonb), modèle
  tarifaire, éditeur/soumissionnaire/réviseur tracés.
- **`app_installation`** (NEW, RLS entreprise) — une ligne par `(company_id, marketplace_app_id)`
  (`UNIQUE`), scopes **accordés** (toujours ⊆ scopes demandés, contrôlé applicativement), config
  jsonb libre, `UNIQUE(id, company_id)` pour servir de cible à la FK composite depuis `api_key`.
- **`api_key_daily_usage`** (NEW, RLS entreprise) — compteur d'appels/refus-de-quota par clé et par
  jour, `UNIQUE(api_key_id, usage_date)`, alimenté par le middleware d'authentification (best-effort,
  ne bloque jamais une requête authentifiée).
- **`api_key`** (Phase 35, ALTER) — `+ app_installation_id` (nullable) `+ api_key_company_id_uk
  UNIQUE(id, company_id)` (cible de la FK composite) `+ api_key_app_installation_fk`.
- `trg_audit_*` sur `marketplace_app`/`app_installation`. Permissions
  **`platform.manage_marketplace`** (curation, réservée `super_administrator`/`platform_admin`) et
  **`integrations.manage_apps`** (installation entreprise, même distribution que `integrations.*`).
- 3 apps officielles amorcées, pré-approuvées : `nexora-mobile-money-connect`,
  `nexora-export-comptable`, `nexora-ecommerce-sync`.
- **245 tables (+3), 209 RLS (+2)**, 59 migrations (V001–V059).

## Backend

| Couche | Fichier |
|---|---|
| Contracts | `Nexora.Contracts/Common/CursorPage.cs` (nouveau — première pagination par curseur) ; `Nexora.Contracts/Marketplace/MarketplaceContracts.cs` ; `Nexora.Contracts/Integrations/ApiKeyContracts.cs` (étendu : `AppInstallationId`) |
| Application | `Nexora.Application/Marketplace/IMarketplace.cs` — `IMarketplaceCatalogService`, `IAppInstallationService`, `IDeveloperUsageQuery` ; `Nexora.Application/Integrations/IApiKeys.cs` (étendu : `IApiKeyUsageRecorder`) ; `PermissionCatalog.Integrations.ManageApps`, `PermissionCatalog.Platform.ManageMarketplace` |
| Infrastructure | `Marketplace/MarketplaceServices.cs` (4 services) ; `Integrations/ApiKeyService.cs` (étendu : double borne de scope, `AppInstallationId` propagé à la rotation) ; `Api/Middleware/ApiKeyAuthenticationMiddleware.cs` (étendu : enregistrement d'usage) |
| API | `Nexora.Api/Controllers/MarketplaceController.cs` (nouveau) — `/apps`, `/apps/{id}`, `/apps/{id}/install`, `/apps/{id}/uninstall`, `/apps/{id}/disable`, `/apps/installed`, `/apps/{id}/review`, `/developer/keys(/{id}/revoke)`, `/developer/usage`, `/webhooks` ; `IntegrationsController.cs` (étendu : alias bare `/integrations`, `/integrations/{id}/health`, `/{id}/test`, `/{id}/sync`) |

### Points de conception

- **Une app installée obtient un accès API réel via le moteur de clés existant, jamais un second
  système d'authentification (§8, §46)** — `CreateApiKeyRequest.AppInstallationId` ajoute une
  **seconde** borne de scope (`scopes ⊆ app_installation.granted_scopes`) **en plus** de la borne
  historique (`scopes ⊆ permissions du propriétaire`, Phase 35, inchangée) ; les deux s'appliquent,
  la plus stricte gagne.
- **`marketplace_app` sans RLS entreprise, par choix, comme `platform_plan`** — un seul catalogue
  pour toute la plateforme ; la visibilité par statut (`approved` seulement pour un appelant sans
  `platform.manage_marketplace`) est un filtre applicatif dans `ListAsync`/`GetAsync`, jamais une
  politique RLS (il n'y a pas d'entreprise à isoler ici, seulement un niveau de confiance).
- **Première pagination par curseur de la base de code (§11)** — `CursorPage<T>` coexiste
  délibérément avec `PagedResult<T>` (Phase 3, offset/page) plutôt que de le remplacer : le
  cahier des charges demande explicitement le curseur en priorité pour les nouvelles API, sans
  exiger de migrer l'existant.
- **Bug détecté et corrigé pendant les tests d'intégration** — `SqlQuery<TScalar>(FormattableString)`
  d'EF Core exige que la colonne unique soit aliasée `AS "Value"` ; deux requêtes scalaires
  (vérification du périmètre accordé dans `ApiKeyService.CreateAsync`, résolution de l'installation
  dans `UninstallAsync`) ne l'étaient pas et échouaient avec `42703: column s.Value does not exist`
  — capturé par les tests avant toute mise en production, jamais en aveugle.
- **Désinstaller révoque, jamais un simple changement de statut cosmétique (§46)** — `UninstallAsync`
  marque l'installation `uninstalled` **et** révoque dans la même opération toute clé
  `app_installation_id`-liée encore active : aucune fenêtre où une app désinstallée garde un accès.
- **`/integrations` (bare) et `/webhooks` sont des alias, jamais une seconde logique** — les deux
  nouvelles routes appellent exactement les mêmes services que les routes historiques
  `/integrations/connectors` et `/integrations/webhooks/outbound` (Phase 12) ; `sync` et `test`
  appellent la même méthode faute d'adaptateur réel à synchroniser différemment — documenté en
  commentaire XML dans le contrôleur plutôt que laissé implicite.
- **Partner Portal restreint au personnel plateforme, pas un renoncement silencieux** — soumettre
  et réviser une app exigent toutes deux `platform.manage_marketplace` ; un éditeur externe n'a pas
  de compte NEXORA propre pour s'auto-servir (§47). Documenté explicitement en backlog plutôt que
  présenté comme un vrai portail partenaire self-service.

## Tests d'intégration — `MarketplaceEcosystemTests` (6, `[Collection("finance")]`, entreprise c1)

| Test | § | Vérifie |
|---|---|---|
| `Submit_requires_review_reviewer_cannot_be_submitter_and_reject_is_final` | 45 | auto-révision refusée ; un tiers tranche (rejet) ; une décision déjà tranchée ne peut être rejouée |
| `Approved_apps_are_publicly_listed_and_cursor_pagination_advances_without_duplicates` | 41, 11 | catalogue public = `approved` uniquement (le filtre de statut est ignoré pour un appelant non-staff) ; le curseur avance sans doublon |
| `Install_enforces_granted_scopes_subset_of_requested_and_is_idempotent` | 44, 56 | scope hors périmètre demandé refusé ; réinstallation = mise à jour de la même ligne, jamais une seconde |
| `Api_key_for_an_installation_is_bounded_by_granted_scopes_and_owner_permissions` | 8, 46 | double borne de scope (permissions propriétaire d'abord, puis périmètre accordé à l'app) |
| `Uninstall_revokes_keys_issued_for_that_installation` | 46 | désinstaller révoque toute clé liée ; désinstaller deux fois est refusé, pas un no-op silencieux |
| `Usage_recorder_aggregates_requests_and_rate_limited_hits_per_day` | 9, 47-48 | agrégation quotidienne requêtes/refus-quota, lisible par le portail développeur |

## Tests API — `MarketplaceEndpointsTests` (4)

| Test | Vérifie |
|---|---|
| `Marketplace_routes_require_authentication` | 6 routes sans jeton → 401 |
| `Read_only_marketplace_endpoints_are_wired_over_http` | 6 endpoints de lecture → 200, y compris l'alias `/integrations` |
| `Install_app_and_issue_a_scoped_developer_key_over_http` | installation → clé développeur scopée → révocation → désinstallation, bout en bout |
| `Catalogue_curation_is_reserved_to_platform_staff` | soumission et revue → 403 pour un utilisateur entreprise sans `platform.manage_marketplace` |

## Frontend

`src/integrations/integrationsApi.ts` étendu (types + fonctions marketplace : `MarketplaceApp`,
`AppsPage`, `AppInstallation`, `DeveloperKeyUsage`, `listApps`/`listInstalledApps`/`installApp`/
`uninstallApp`/`disableApp`/`listDeveloperUsage`/`submitApp`/`reviewApp`) + **`IntegrationsCenterPage.tsx`**
(page existante, Phase 12/35, étendue plutôt que dupliquée) :

- **Marketplace — apps partenaires** — tableau catalogue (nom/éditeur/catégorie/certification/prix/
  statut d'installation), installer/réactiver/désactiver/désinstaller selon `integrations.manage_apps` ;
- **Clés d'API** — colonne « app liée » affichant le nom de l'app installée correspondante ;
- **Portail développeur** — usage quotidien agrégé par clé (30 derniers jours), appels totaux et
  refus de quota, sous la section clés d'API.

Vitest `marketplaceApi.test.ts` (7, dont pagination par curseur et distinction routes
staff/entreprise) → **96 tests frontend**.

## Manques assumés (backlog)

- Flux OAuth 2.0 standard (authorization-code/client-credentials avec endpoint token dédié, §6) —
  API keys (Phase 35) restent le seul mécanisme machine-à-machine
- Émission automatique des événements métier (§15) — seuls `payment.confirmed`/`payment.refunded`
  sont déclenchés par le code métier ; les 13 autres types existent comme valeurs possibles mais
  ne sont pas câblés dans Sales/Purchasing/Stock/CRM/GED/SaaS — chantier transverse volontairement
  hors du périmètre resserré de cette phase
- Recherche texte plein dans le catalogue marketplace (§41) — filtre catégorie/statut seulement
- Partner Portal self-service avec identité partenaire propre (§47) — soumission/revue réservées
  au personnel plateforme, pas de compte éditeur externe
- Developer Portal complet : documentation OpenAPI générée, exemples, changelog, guides, page de
  statut (§48)
- SDK JS/TS, PHP, Python, mobile (§49)
- Protection SSRF / allowlist de domaines pour les URLs sortantes (webhooks, connecteurs, §51-52) —
  **écart de sécurité explicite**, seule la validation de schéma (http/https) existe aujourd'hui
- Écran de consentement dédié par app au-delà de l'affichage des scopes demandés (§53)
- Circuit breaker dédié par app marketplace (§54) — aucune app de cette phase n'appelle un service
  externe réel
- Facturation marketplace réelle : charge, commission, facture, renouvellement (§55) — affichage du
  modèle tarifaire seulement
- Installation marketplace au niveau groupe (§56) — entreprise par entreprise uniquement
- Requête d'audit dédiée à la marketplace (§57) — repose sur `audit_log` générique, pas de vue filtrée
- Sandbox marketplace dédié (données fictives réinitialisables, §16) — hérite du bac à sable Phase 12
