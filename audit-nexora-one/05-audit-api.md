# 05 — Audit de l'API

> Inventaire extrait des attributs des 74 contrôleurs (`backend/src/Nexora.Api/Controllers`) par analyse statique (script `endpoints.py`, dossier temporaire de l'audit), croisé avec les appels du frontend (`fe_vs_api.py`). Les comportements à l'exécution cités proviennent des scénarios EXEC décrits dans 08.

## 1. Chiffres

| Indicateur | Valeur |
|---|---|
| Points d'accès (verbe × route) | **1029** |
| Contrôleurs | 74 |
| Verbes | POST 503, GET 465, PUT 43, DELETE 13, PATCH 5 |
| Anonymes (`[AllowAnonymous]`) | 18 |
| Authentifiés sans permission déclarée (libre-service : mes notifications, préférences, sessions…) | 57 |
| Authentifiés avec permission `[RequirePermission]` | 954 |
| Écritures (POST/PUT/PATCH/DELETE) | 564 |
| … dont filtre d'idempotence `Idempotency-Key` | 53 |
| Points d'accès à confirmation forte (step-up) | 41 |
| Points d'accès avec limitation de débit explicite | 14 |
| Appels directs du frontend (`apiFetch`/`apiDownload`) | 637, dont **636 résolus** sur une route existante ; 1 faux positif (`/groups` préfixé par un assistant) |
| Routes (chemins distincts) sans appel frontend **détecté** | 299 — indicatif : les appels via des assistants (`post<T>()`, `put<T>()`…) ne sont pas suivis par le script |

Politique globale : `FallbackPolicy` authentifiée (deny-by-default), contrôle `X-Company-Id` ∈ jeton (403 sinon, vérifié), RLS PostgreSQL en second rideau. Les erreurs suivent RFC 7807 avec identifiant de corrélation.

## 2. Points d'accès anonymes

| Verbe | Route | Contrôleur | Limitation | Protection |
|---|---|---|---|---|
| POST | `/api/v1/auth/login` | AuthController | auth | identifiants ; verrouillage de compte |
| POST | `/api/v1/auth/mfa/verify` | AuthController | auth | jeton MFA intermédiaire + code TOTP |
| POST | `/api/v1/auth/refresh` | AuthController | refresh, auth | cookie HttpOnly de rafraîchissement, rotation et détection de rejeu |
| POST | `/api/v1/auth/password/forgot` | AuthController | auth | réponse identique quel que soit l'e-mail (à confirmer, A-011) |
| POST | `/api/v1/auth/password/reset` | AuthController | auth | jeton de réinitialisation |
| POST | `/api/v1/auth/logout` | AuthController | refresh, auth | cookie de rafraîchissement |
| POST | `/api/v1/billing/webhooks/payment` | BillingController | — | signature du fournisseur |
| GET | `/api/v1/documents/verify/{code}` | DocumentsController | public | code de vérification |
| GET | `/api/v1/shared/{token}` | GedController | public | jeton de partage, mot de passe BCrypt |
| POST | `/api/v1/shared/{token}` | GedController | public | jeton de partage, mot de passe BCrypt |
| POST | `/api/v1/integrations/webhooks/inbound/{connectorCode}` | IntegrationsController | — | signature HMAC, déduplication |
| POST | `/api/v1/licenses/verify` | LicensesController | public | jeton HMAC |
| GET | `/api/v1/metrics` | MetricsController | — | **aucune dans l'API** (blocage réseau Caddy contournable — A-024) |
| GET | `/api/v1/setup/status` | SetupController | — | lecture d'état seulement |
| GET | `/api/v1/setup/options` | SetupController | — | listes de choix ; fermé après installation (sans jeton) |
| POST | `/api/v1/setup/verify-token` | SetupController | auth | jeton d'installation `X-Setup-Token`, fermé après installation |
| POST | `/api/v1/setup/database/test` | SetupController | auth | jeton d'installation `X-Setup-Token`, fermé après installation |
| POST | `/api/v1/setup/install` | SetupController | auth | jeton d'installation `X-Setup-Token`, fermé après installation |

Aucune limitation de débit sur les deux webhooks entrants (la signature est vérifiée avant tout traitement).

## 3. Problèmes identifiés sur l'API

| ID | Point d'accès | Problème | Preuve |
|---|---|---|---|
| A-003 | `POST /api/v1/pos/checkout`, `POST /api/v1/sales` | Le corps lie `SoldAt` : vente antidatée, vente dans une session close | EXEC : VTE2026-000049 acceptée dans une session close |
| A-012 | `POST /api/v1/pos/checkout`, `/pos/cart/preview` | Coupon fixe appliqué à chaque ligne | EXEC : 50 → 100 / 150 |
| A-014 | `POST /api/v1/sales/{id}/payments` | `pos.sell` suffit pour solder une créance, hors service des comptes clients | Lecture `SalesController.cs:34-47` |
| A-018 | `POST /api/v1/treasury/transfers` (et tout chemin qui ajuste un solde) | Mise à jour perdue du solde | EXEC : 525 HTG perdus |
| A-024 | `GET /api/v1/metrics` | Anonyme ; variantes `/api/v1.0/metrics`, `/api/v1/metrics/` non bloquées par Caddy | EXEC (API) |
| A-031 | `/ops/data-corrections`, `/ops/maintenance`, `/ops/jobs/*`, `/ops/purge/execute`, `/ops/feature-flags` | Portée plateforme (RLS levée ou tables globales) accessible aux rôles d'entreprise `manager`/`owner_direction` : fuite et modification de données entre entreprises, gel des écritures de toute la plateforme | Lecture + catalogue (non exécuté) |
| A-004 | événements financiers | Doublon concurrent du même `operation_uuid` → 500 au lieu d'un rejeu | Lecture `FinancialEngine.cs:48-54` |
| Obs. | filtre `IdempotencyFilter` | Cache mémoire 24 h, sans verrou : deux requêtes simultanées de même clé peuvent toutes deux s'exécuter ; cache perdu au redémarrage et non partagé entre instances | Lecture |
| Obs. | `UnitOfWork` | Messages PostgreSQL bruts renvoyés au client sur certaines violations de contrainte | Lecture |
| T-002 | tests d'API | La base visée n'est pas celle déclarée par les tests | EXEC (incident d'audit) |

Contrôles vérifiés à l'exécution : jeton step-up refusé hors de sa session (403) ; `X-Company-Id` d'une autre entreprise → 403 ; identifiant invalide → 400 ; `/api/v1.0/health` → 401 (non anonyme).

## 4. Inventaire complet

Colonnes : verbe, route, contrôleur:ligne, permission(s), idempotence (I), confirmation forte (S), appel frontend détecté (F), problème connu.

| Verbe | Route | Contrôleur:ligne | Permission | I | S | F | Problème |
|---|---|---|---|---|---|---|---|
| GET | `/api/v1/accounting-currencies` | ChartOfAccountsController:25 | Accounting.View |  |  | oui |  |
| GET | `/api/v1/accounting-entries` | AccountingEntriesController:20 | Accounting.View |  |  | oui |  |
| POST | `/api/v1/accounting-entries` | AccountingEntriesController:31 | Accounting.Create |  |  | oui |  |
| DELETE | `/api/v1/accounting-entries/{id:guid}` | AccountingEntriesController:41 | Accounting.Create |  |  | oui |  |
| GET | `/api/v1/accounting-entries/{id:guid}` | AccountingEntriesController:26 | Accounting.View |  |  | oui |  |
| PUT | `/api/v1/accounting-entries/{id:guid}` | AccountingEntriesController:36 | Accounting.Create |  |  | oui |  |
| POST | `/api/v1/accounting-entries/{id:guid}/post` | AccountingEntriesController:47 | Accounting.Post | ✔ |  | oui |  |
| POST | `/api/v1/accounting-entries/{id:guid}/reverse` | AccountingEntriesController:53 | Accounting.Reverse |  | ✔ | oui |  |
| GET | `/api/v1/accounting-periods` | AccountingPeriodsController:25 | Accounting.View |  |  | oui |  |
| POST | `/api/v1/accounting-periods/{id:guid}/close` | AccountingPeriodsController:35 | Accounting.ClosePeriod |  | ✔ | oui |  |
| GET | `/api/v1/accounting-periods/{id:guid}/close-year` | AccountingPeriodsController:48 | Accounting.View |  |  | oui |  |
| POST | `/api/v1/accounting-periods/{id:guid}/close-year` | AccountingPeriodsController:54 | Accounting.CarryForward |  | ✔ | oui |  |
| POST | `/api/v1/accounting-periods/{id:guid}/opening-balances` | AccountingPeriodsController:61 | Accounting.CarryForward |  |  | oui |  |
| POST | `/api/v1/accounting-periods/{id:guid}/reopen` | AccountingPeriodsController:41 | Accounting.ReopenPeriod |  | ✔ | oui |  |
| POST | `/api/v1/accounting/journal-entries/{id:guid}/reverse` | AccountingController:45 | Accounting.Reverse |  | ✔ | — |  |
| GET | `/api/v1/accounting/periods` | AccountingController:25 | Accounting.View |  |  | — |  |
| POST | `/api/v1/accounting/periods/{id:guid}/close` | AccountingController:31 | Accounting.ClosePeriod |  | ✔ | — |  |
| POST | `/api/v1/accounting/periods/{id:guid}/reopen` | AccountingController:37 | Accounting.ReopenPeriod |  | ✔ | — |  |
| GET | `/api/v1/accounting/reports/aged-payables` | AccountingController:82 | Finance.View |  |  | — |  |
| GET | `/api/v1/accounting/reports/aged-receivables` | AccountingController:77 | Finance.View |  |  | — |  |
| GET | `/api/v1/accounting/reports/balance-sheet` | AccountingController:72 | Accounting.View |  |  | — |  |
| GET | `/api/v1/accounting/reports/cash-flow-forecast` | AccountingController:87 | Finance.View |  |  | — |  |
| GET | `/api/v1/accounting/reports/general-ledger` | AccountingController:57 | Accounting.View |  |  | — |  |
| GET | `/api/v1/accounting/reports/income-statement` | AccountingController:67 | Accounting.View |  |  | — |  |
| GET | `/api/v1/accounting/reports/trial-balance` | AccountingController:52 | Accounting.View |  |  | — |  |
| GET | `/api/v1/accounts` | ChartOfAccountsController:49 | Accounting.View |  |  | oui |  |
| POST | `/api/v1/accounts` | ChartOfAccountsController:29 | Accounting.ManageChart |  |  | oui |  |
| GET | `/api/v1/accounts/manage` | ChartOfAccountsController:21 | Accounting.View |  |  | oui |  |
| PUT | `/api/v1/accounts/{id:guid}` | ChartOfAccountsController:34 | Accounting.ManageChart |  |  | oui |  |
| POST | `/api/v1/accounts/{id:guid}/status` | ChartOfAccountsController:39 | Accounting.ManageChart |  |  | oui |  |
| GET | `/api/v1/admin/automations` | AdministrationController:124 | Admin.Automations |  |  | oui |  |
| POST | `/api/v1/admin/automations` | AdministrationController:128 | Admin.Automations |  |  | oui |  |
| POST | `/api/v1/admin/automations/run-due` | AdministrationController:143 | Admin.Automations |  |  | — |  |
| POST | `/api/v1/admin/automations/{id:guid}/run` | AdministrationController:138 | Admin.Automations |  |  | oui |  |
| GET | `/api/v1/admin/automations/{id:guid}/runs` | AdministrationController:147 | Admin.Automations |  |  | oui |  |
| POST | `/api/v1/admin/automations/{id:guid}/status` | AdministrationController:133 | Admin.Automations |  |  | oui |  |
| GET | `/api/v1/admin/config-versions` | AdministrationController:162 | Admin.ConfigVersions |  |  | oui |  |
| GET | `/api/v1/admin/config-versions/active-at` | AdministrationController:177 | Admin.ConfigVersions |  |  | — |  |
| POST | `/api/v1/admin/config-versions/snapshot` | AdministrationController:167 | Admin.ConfigVersions |  |  | oui |  |
| POST | `/api/v1/admin/config-versions/{id:guid}/activate` | AdministrationController:172 | Admin.ConfigVersions |  |  | oui |  |
| GET | `/api/v1/admin/dunning-policy` | AdministrationController:151 | Admin.Automations |  |  | — |  |
| PUT | `/api/v1/admin/dunning-policy` | AdministrationController:155 | Admin.Automations |  |  | — |  |
| GET | `/api/v1/admin/permissions` | AdministrationController:56 | Admin.Roles |  |  | oui |  |
| GET | `/api/v1/admin/roles` | AdministrationController:46 | Admin.Roles |  |  | oui |  |
| POST | `/api/v1/admin/roles` | AdministrationController:51 | Admin.Roles |  | ✔ | oui |  |
| PUT | `/api/v1/admin/roles/{id:guid}` | AdministrationController:61 | Admin.Roles |  | ✔ | oui |  |
| POST | `/api/v1/admin/roles/{id:guid}/disable` | AdministrationController:82 | Admin.Roles |  | ✔ | oui |  |
| POST | `/api/v1/admin/roles/{id:guid}/enable` | AdministrationController:67 | Admin.Roles |  | ✔ | oui |  |
| PUT | `/api/v1/admin/roles/{id:guid}/permissions` | AdministrationController:72 | Admin.Roles |  | ✔ | oui |  |
| GET | `/api/v1/admin/roles/{id:guid}/preview` | AdministrationController:77 | Admin.Roles |  |  | — |  |
| GET | `/api/v1/admin/settings` | AdministrationController:34 | Admin.Settings |  |  | oui |  |
| PUT | `/api/v1/admin/settings` | AdministrationController:39 | Admin.Settings |  |  | oui |  |
| GET | `/api/v1/admin/settings/definitions` | AdministrationController:29 | Admin.Settings |  |  | oui |  |
| GET | `/api/v1/admin/tasks` | AdministrationController:187 | Admin.Settings |  |  | oui |  |
| GET | `/api/v1/admin/workflows` | AdministrationController:89 | Admin.Workflows |  |  | oui |  |
| POST | `/api/v1/admin/workflows` | AdministrationController:97 | Admin.Workflows |  |  | oui |  |
| GET | `/api/v1/admin/workflows/catalog` | AdministrationController:93 | Admin.Workflows |  |  | oui |  |
| POST | `/api/v1/admin/workflows/evaluate` | AdministrationController:117 | Admin.Workflows |  |  | — |  |
| POST | `/api/v1/admin/workflows/{id:guid}/activate` | AdministrationController:107 | Admin.Workflows |  |  | oui |  |
| POST | `/api/v1/admin/workflows/{id:guid}/status` | AdministrationController:112 | Admin.Workflows |  |  | oui |  |
| POST | `/api/v1/admin/workflows/{id:guid}/test` | AdministrationController:102 | Admin.Workflows |  |  | oui |  |
| GET | `/api/v1/alerts` | AnalyticsController:65 | Reporting.View |  |  | — |  |
| GET | `/api/v1/analytics/data-quality` | AnalyticsController:36 | Reporting.DataQuality |  |  | oui |  |
| GET | `/api/v1/analytics/reconciliation` | AnalyticsController:28 | Reporting.DataQuality |  |  | oui |  |
| GET | `/api/v1/analytics/snapshots` | AnalyticsController:43 | Reporting.DataQuality |  |  | oui |  |
| POST | `/api/v1/analytics/snapshots` | AnalyticsController:53 | Reporting.DataQuality |  |  | oui |  |
| GET | `/api/v1/analytics/snapshots/{id:guid}` | AnalyticsController:48 | Reporting.DataQuality |  |  | oui |  |
| GET | `/api/v1/api-keys` | ApiKeysController:21 | Integrations.ManageApiKeys |  |  | oui |  |
| POST | `/api/v1/api-keys` | ApiKeysController:25 | Integrations.ManageApiKeys |  | ✔ | oui |  |
| POST | `/api/v1/api-keys/{id:guid}/revoke` | ApiKeysController:35 | Integrations.ManageApiKeys |  | ✔ | oui |  |
| POST | `/api/v1/api-keys/{id:guid}/rotate` | ApiKeysController:30 | Integrations.ManageApiKeys |  | ✔ | oui |  |
| GET | `/api/v1/apps` | MarketplaceController:38 | Integrations.View |  |  | oui |  |
| POST | `/api/v1/apps` | MarketplaceController:55 | Platform.ManageMarketplace |  |  | oui |  |
| GET | `/api/v1/apps/installed` | MarketplaceController:67 | Integrations.View |  |  | oui |  |
| GET | `/api/v1/apps/{id:guid}` | MarketplaceController:47 | Integrations.View |  |  | — |  |
| POST | `/api/v1/apps/{id:guid}/disable` | MarketplaceController:81 | Integrations.ManageApps |  |  | oui |  |
| POST | `/api/v1/apps/{id:guid}/install` | MarketplaceController:71 | Integrations.ManageApps |  |  | oui |  |
| POST | `/api/v1/apps/{id:guid}/review` | MarketplaceController:60 | Platform.ManageMarketplace |  |  | oui |  |
| POST | `/api/v1/apps/{id:guid}/uninstall` | MarketplaceController:76 | Integrations.ManageApps |  |  | oui |  |
| POST | `/api/v1/ask` | AskController:21 | Reporting.Ask |  |  | — |  |
| GET | `/api/v1/assistant/briefing` | IntelligenceController:150 | Intelligence.UseAssistant |  |  | oui |  |
| POST | `/api/v1/assistant/query` | IntelligenceController:155 | Intelligence.UseAssistant |  |  | oui |  |
| POST | `/api/v1/attachments` | MobileController:100 | FieldOps.Manage |  |  | oui |  |
| GET | `/api/v1/attachments/{id:guid}` | MobileController:105 | FieldOps.View |  |  | oui |  |
| POST | `/api/v1/auth/login` | AuthController:35 | anonyme |  |  | oui |  |
| POST | `/api/v1/auth/logout` | AuthController:99 | anonyme |  |  | — |  |
| GET | `/api/v1/auth/mfa` | AuthMfaController:19 | authentifié |  |  | oui |  |
| POST | `/api/v1/auth/mfa/disable` | AuthMfaController:34 | authentifié |  |  | oui |  |
| POST | `/api/v1/auth/mfa/enroll` | AuthMfaController:24 | authentifié |  |  | oui |  |
| POST | `/api/v1/auth/mfa/enroll/confirm` | AuthMfaController:28 | authentifié |  |  | oui |  |
| POST | `/api/v1/auth/mfa/verify` | AuthController:47 | anonyme |  |  | oui |  |
| POST | `/api/v1/auth/password/forgot` | AuthController:83 | anonyme |  |  | oui |  |
| POST | `/api/v1/auth/password/reset` | AuthController:91 | anonyme |  |  | oui |  |
| POST | `/api/v1/auth/refresh` | AuthController:64 | anonyme |  |  | — |  |
| GET | `/api/v1/backup-policies` | ContinuityController:23 | Continuity.View |  |  | oui |  |
| POST | `/api/v1/backup-policies` | ContinuityController:27 | Continuity.Manage |  |  | oui |  |
| GET | `/api/v1/backups` | ContinuityController:34 | Continuity.View |  |  | oui |  |
| POST | `/api/v1/backups` | ContinuityController:38 | Continuity.Manage |  |  | oui |  |
| POST | `/api/v1/backups/{id:guid}/restore-test` | ContinuityController:48 | Continuity.Manage |  |  | oui |  |
| POST | `/api/v1/backups/{id:guid}/verify` | ContinuityController:43 | Continuity.Manage |  |  | oui |  |
| GET | `/api/v1/billing/invoices` | BillingController:32 | Platform.Portal |  |  | oui |  |
| GET | `/api/v1/billing/invoices/{id:guid}` | BillingController:36 | Platform.Portal |  |  | — |  |
| GET | `/api/v1/billing/payment-methods` | BillingController:44 | Platform.Portal |  |  | — |  |
| POST | `/api/v1/billing/payment-methods` | BillingController:48 | Platform.Portal |  |  | — |  |
| GET | `/api/v1/billing/payments` | BillingController:40 | Platform.Portal |  |  | — |  |
| POST | `/api/v1/billing/webhooks/payment` | BillingController:63 | anonyme |  |  | — |  |
| GET | `/api/v1/budgets` | BiController:154 | Reporting.ViewFinancial |  |  | oui |  |
| POST | `/api/v1/budgets` | BiController:158 | Reporting.ManageBudgets |  |  | oui |  |
| GET | `/api/v1/budgets/{id:guid}` | BiController:163 | Reporting.ViewFinancial |  |  | — |  |
| POST | `/api/v1/budgets/{id:guid}/approve` | BiController:180 | Reporting.ManageBudgets |  |  | oui |  |
| POST | `/api/v1/budgets/{id:guid}/close` | BiController:184 | Reporting.ManageBudgets |  |  | oui |  |
| GET | `/api/v1/budgets/{id:guid}/lines` | BiController:167 | Reporting.ViewFinancial |  |  | oui |  |
| PUT | `/api/v1/budgets/{id:guid}/lines` | BiController:171 | Reporting.ManageBudgets |  |  | oui |  |
| POST | `/api/v1/budgets/{id:guid}/revise` | BiController:188 | Reporting.ManageBudgets |  |  | oui |  |
| POST | `/api/v1/budgets/{id:guid}/submit` | BiController:176 | Reporting.ManageBudgets |  |  | oui |  |
| GET | `/api/v1/cashflow/forecast` | CashflowController:16 | Finance.View |  |  | oui |  |
| GET | `/api/v1/catalog/brands` | CatalogController:202 | Catalog.View |  |  | oui |  |
| POST | `/api/v1/catalog/brands` | CatalogController:206 | Catalog.Create |  |  | oui |  |
| GET | `/api/v1/catalog/categories` | CatalogController:83 | Catalog.View |  |  | oui |  |
| POST | `/api/v1/catalog/categories` | CatalogController:87 | Catalog.Create |  |  | oui |  |
| PUT | `/api/v1/catalog/categories/{id:guid}` | CatalogController:92 | Catalog.Update |  |  | — |  |
| GET | `/api/v1/catalog/coupons` | CatalogController:182 | Catalog.ManagePromotion |  |  | oui |  |
| POST | `/api/v1/catalog/coupons` | CatalogController:186 | Catalog.ManagePromotion |  |  | oui |  |
| POST | `/api/v1/catalog/coupons/{id:guid}/status` | CatalogController:191 | Catalog.ManagePromotion |  |  | — |  |
| GET | `/api/v1/catalog/currencies` | CatalogController:197 | Catalog.View |  |  | oui |  |
| GET | `/api/v1/catalog/customer-groups` | CatalogController:211 | Catalog.View |  |  | oui |  |
| POST | `/api/v1/catalog/customer-groups` | CatalogController:215 | Catalog.ManagePrice |  |  | oui |  |
| GET | `/api/v1/catalog/price-lists` | CatalogController:148 | Catalog.View |  |  | oui |  |
| POST | `/api/v1/catalog/price-lists` | CatalogController:152 | Catalog.ManagePrice |  |  | oui |  |
| POST | `/api/v1/catalog/pricing/quote` | CatalogController:221 | Catalog.View |  |  | oui |  |
| GET | `/api/v1/catalog/products` | CatalogController:32 | Catalog.View |  |  | oui |  |
| POST | `/api/v1/catalog/products` | CatalogController:47 | Catalog.Create |  |  | oui |  |
| GET | `/api/v1/catalog/products/{id:guid}` | CatalogController:39 | Catalog.View |  |  | oui |  |
| PUT | `/api/v1/catalog/products/{id:guid}` | CatalogController:52 | Catalog.Update |  |  | oui |  |
| POST | `/api/v1/catalog/products/{id:guid}/barcodes` | CatalogController:72 | Catalog.Update |  |  | — |  |
| GET | `/api/v1/catalog/products/{id:guid}/image` | CatalogController:137 | Catalog.View |  |  | oui |  |
| POST | `/api/v1/catalog/products/{id:guid}/image` | CatalogController:132 | Catalog.Update |  |  | oui |  |
| POST | `/api/v1/catalog/products/{id:guid}/prices` | CatalogController:67 | Catalog.ManagePrice |  |  | — |  |
| PUT | `/api/v1/catalog/products/{id:guid}/reorder-rule` | CatalogController:77 | Inventory.Adjust |  |  | — |  |
| POST | `/api/v1/catalog/products/{id:guid}/status` | CatalogController:57 | Catalog.Update |  |  | oui |  |
| POST | `/api/v1/catalog/products/{id:guid}/suppliers` | CatalogController:62 | Catalog.Update |  |  | — |  |
| GET | `/api/v1/catalog/promotions` | CatalogController:163 | Catalog.View |  |  | oui |  |
| POST | `/api/v1/catalog/promotions` | CatalogController:167 | Catalog.ManagePromotion |  |  | oui |  |
| PUT | `/api/v1/catalog/promotions/{id:guid}` | CatalogController:172 | Catalog.ManagePromotion |  |  | — |  |
| POST | `/api/v1/catalog/promotions/{id:guid}/status` | CatalogController:177 | Catalog.ManagePromotion |  |  | — |  |
| GET | `/api/v1/catalog/scan` | CatalogController:23 | Catalog.View |  |  | oui |  |
| GET | `/api/v1/catalog/search` | CatalogController:18 | Catalog.View |  |  | oui |  |
| GET | `/api/v1/catalog/taxes` | CatalogController:158 | Catalog.View |  |  | oui |  |
| GET | `/api/v1/catalog/unit-conversions` | CatalogController:107 | Catalog.View |  |  | oui |  |
| POST | `/api/v1/catalog/unit-conversions` | CatalogController:112 | Catalog.Update |  |  | oui |  |
| DELETE | `/api/v1/catalog/unit-conversions/{id:guid}` | CatalogController:117 | Catalog.Update |  |  | oui |  |
| GET | `/api/v1/catalog/units` | CatalogController:98 | Catalog.View |  |  | oui |  |
| POST | `/api/v1/catalog/units` | CatalogController:102 | Catalog.Create |  |  | oui |  |
| GET | `/api/v1/catalog/units/convert` | CatalogController:123 | Catalog.View |  |  | oui |  |
| GET | `/api/v1/chart-of-accounts` | ChartOfAccountsController:44 | Accounting.View |  |  | — |  |
| GET | `/api/v1/collection-actions` | CustomerAccountsController:202 | Credit.ViewPortfolio |  |  | — |  |
| GET | `/api/v1/collections` | CustomerAccountsController:197 | Credit.ViewPortfolio |  |  | — |  |
| GET | `/api/v1/companies` | CompaniesController:17 | Organizations.View |  |  | oui |  |
| GET | `/api/v1/companies/branding` | PreferencesController:35 | authentifié |  |  | oui |  |
| PUT | `/api/v1/companies/branding` | PreferencesController:40 | Admin.ConfigureCompany |  |  | oui |  |
| GET | `/api/v1/company-branding` | PreferencesController:35 | authentifié |  |  | — |  |
| PUT | `/api/v1/company-branding` | PreferencesController:40 | Admin.ConfigureCompany |  |  | — |  |
| POST | `/api/v1/configurations/clone` | ConfigurationController:45 | Config.Transfer |  |  | oui |  |
| GET | `/api/v1/configurations/export` | ConfigurationController:35 | Config.Transfer |  |  | oui |  |
| GET | `/api/v1/configurations/history` | ConfigurationController:50 | Config.Transfer |  |  | oui |  |
| POST | `/api/v1/configurations/import` | ConfigurationController:40 | Config.Transfer |  |  | — |  |
| GET | `/api/v1/configurations/validate` | ConfigurationController:28 | Config.Validate |  |  | oui |  |
| GET | `/api/v1/configurations/{domain}/versions` | ConfigurationController:57 | Admin.ConfigVersions |  |  | — |  |
| POST | `/api/v1/configurations/{versionId:guid}/publish` | ConfigurationController:62 | Admin.ConfigVersions |  |  | — |  |
| POST | `/api/v1/configurations/{versionId:guid}/rollback` | ConfigurationController:67 | Admin.ConfigVersions |  |  | — |  |
| GET | `/api/v1/consolidations` | ConsolidationController:38 | Group.View |  |  | — |  |
| POST | `/api/v1/consolidations/{runId:guid}/run` | ConsolidationController:47 | Consolidation.Run |  |  | — |  |
| GET | `/api/v1/continuity/incidents` | ContinuityController:80 | Continuity.View |  |  | oui |  |
| POST | `/api/v1/continuity/incidents` | ContinuityController:86 | Continuity.Manage |  |  | oui |  |
| POST | `/api/v1/continuity/incidents/{id:guid}/advance` | ContinuityController:91 | Continuity.Manage |  |  | oui |  |
| POST | `/api/v1/continuity/incidents/{id:guid}/lockdown` | ContinuityController:108 | Continuity.Restore |  |  | oui |  |
| POST | `/api/v1/continuity/incidents/{id:guid}/resolve` | ContinuityController:96 | Continuity.Manage |  |  | oui |  |
| GET | `/api/v1/continuity/tests` | ContinuityController:101 | Continuity.View |  |  | — |  |
| GET | `/api/v1/credit-memos` | CustomerAccountsController:144 | Credit.ViewPortfolio |  |  | — |  |
| GET | `/api/v1/credit-memos/{id:guid}` | CustomerAccountsController:149 | Credit.ViewPortfolio |  |  | — |  |
| POST | `/api/v1/credit-memos/{id:guid}/apply` | CustomerAccountsController:155 | Credit.ReceivePayment | ✔ |  | — |  |
| POST | `/api/v1/credit-notes` | DocumentsController:50 | Documents.Issue | ✔ |  | — |  |
| GET | `/api/v1/credit/aging` | CollectionsController:26 | Credit.ViewPortfolio |  |  | oui |  |
| GET | `/api/v1/credit/blocks` | CollectionsController:91 | Credit.ViewPortfolio |  |  | oui |  |
| POST | `/api/v1/credit/blocks` | CollectionsController:96 | Credit.Block |  |  | oui |  |
| POST | `/api/v1/credit/blocks/{id:guid}/release` | CollectionsController:101 | Credit.Block |  |  | oui |  |
| GET | `/api/v1/credit/collections` | CollectionsController:21 | Credit.ViewPortfolio |  |  | oui |  |
| GET | `/api/v1/credit/customers/{customerId:guid}/receivables` | CollectionsController:60 | Credit.ViewPortfolio |  |  | oui |  |
| GET | `/api/v1/credit/disputes` | CollectionsController:75 | Credit.ViewPortfolio |  |  | oui |  |
| POST | `/api/v1/credit/disputes` | CollectionsController:80 | Credit.Dispute |  |  | oui |  |
| POST | `/api/v1/credit/disputes/{id:guid}/resolve` | CollectionsController:85 | Credit.Dispute |  |  | oui |  |
| GET | `/api/v1/credit/follow-ups` | CollectionsController:65 | Credit.ViewPortfolio |  |  | oui |  |
| GET | `/api/v1/credit/promises` | CollectionsController:39 | Credit.ViewPortfolio |  |  | oui |  |
| POST | `/api/v1/credit/promises` | CollectionsController:44 | Credit.Collect |  |  | oui |  |
| POST | `/api/v1/credit/promises/{id:guid}/cancel` | CollectionsController:54 | Credit.Collect |  |  | oui |  |
| POST | `/api/v1/credit/promises/{id:guid}/settle` | CollectionsController:49 | Credit.Collect |  |  | oui |  |
| POST | `/api/v1/credit/receivables/{id:guid}/write-off` | CollectionsController:108 | Credit.WriteOff | ✔ |  | oui |  |
| GET | `/api/v1/credit/statements/{customerId:guid}` | CollectionsController:30 | Credit.ViewPortfolio |  |  | oui |  |
| GET | `/api/v1/credit/write-offs` | CollectionsController:69 | Credit.ViewPortfolio |  |  | oui |  |
| GET | `/api/v1/credits` | CustomerAccountsController:36 | Credit.ViewPortfolio |  |  | — |  |
| GET | `/api/v1/credits/{id:guid}` | CustomerAccountsController:41 | Credit.ViewPortfolio |  |  | oui |  |
| POST | `/api/v1/credits/{id:guid}/installments` | CustomerAccountsController:58 | Credit.SetLimit |  |  | — |  |
| GET | `/api/v1/credits/{id:guid}/payments` | CustomerAccountsController:46 | Credit.ViewPortfolio |  |  | — |  |
| POST | `/api/v1/credits/{id:guid}/payments` | CustomerAccountsController:52 | Credit.ReceivePayment | ✔ |  | — |  |
| GET | `/api/v1/customer-expenses` | CustomerAccountsController:111 | Credit.ViewPortfolio |  |  | oui |  |
| GET | `/api/v1/customer-expenses/{id:guid}` | CustomerAccountsController:116 | Credit.ViewPortfolio |  |  | — |  |
| POST | `/api/v1/customer-expenses/{id:guid}/approve` | CustomerAccountsController:126 | Customers.ApproveExpense | ✔ |  | — |  |
| POST | `/api/v1/customer-expenses/{id:guid}/cancel` | CustomerAccountsController:137 | Customers.CreateExpense | ✔ |  | — |  |
| POST | `/api/v1/customer-expenses/{id:guid}/reject` | CustomerAccountsController:131 | Customers.ApproveExpense |  |  | — |  |
| POST | `/api/v1/customer-expenses/{id:guid}/submit` | CustomerAccountsController:121 | Customers.CreateExpense |  |  | — |  |
| GET | `/api/v1/customer-payment-options` | CustomerAccountsController:92 | Credit.ReceivePayment |  |  | oui |  |
| GET | `/api/v1/customer-refunds` | CustomerAccountsController:160 | Credit.ViewPortfolio |  |  | oui |  |
| POST | `/api/v1/customer-refunds` | CustomerAccountsController:166 | Credit.ReceivePayment | ✔ |  | oui |  |
| POST | `/api/v1/customer-refunds/{id:guid}/approve` | CustomerAccountsController:171 | Credit.Refund |  |  | — |  |
| POST | `/api/v1/customer-refunds/{id:guid}/cancel` | CustomerAccountsController:190 | Customers.View |  |  | — |  |
| POST | `/api/v1/customer-refunds/{id:guid}/execute` | CustomerAccountsController:185 | Customers.View | ✔ | ✔ | — |  |
| POST | `/api/v1/customer-refunds/{id:guid}/reject` | CustomerAccountsController:176 | Credit.Refund |  |  | — |  |
| GET | `/api/v1/customer-reports` | CustomerAccountsController:209 | Credit.ViewPortfolio |  |  | oui |  |
| GET | `/api/v1/customer-reports/{key}` | CustomerAccountsController:213 | Credit.ViewPortfolio |  |  | oui |  |
| GET | `/api/v1/customer-reports/{key}/export` | CustomerAccountsController:221 | Credit.ViewPortfolio, Customers.Export |  |  | — |  |
| GET | `/api/v1/customers` | CustomersController:36 | Customers.View |  |  | oui |  |
| POST | `/api/v1/customers` | CustomersController:53 | Customers.Create |  |  | oui |  |
| GET | `/api/v1/customers/lookup` | CustomersController:43 | Customers.View |  |  | oui |  |
| GET | `/api/v1/customers/{id:guid}` | CustomersController:48 | Customers.View |  |  | oui |  |
| PATCH | `/api/v1/customers/{id:guid}` | CustomersController:63 | Customers.Update |  |  | oui |  |
| PUT | `/api/v1/customers/{id:guid}` | CustomersController:58 | Customers.Update |  |  | oui |  |
| GET | `/api/v1/customers/{id:guid}/account` | CustomersController:75 | Credit.ViewPortfolio |  |  | oui |  |
| GET | `/api/v1/customers/{id:guid}/collection-actions` | CustomersController:148 | Credit.ViewPortfolio |  |  | oui |  |
| POST | `/api/v1/customers/{id:guid}/collection-actions` | CustomersController:153 | Credit.Collect |  |  | oui |  |
| GET | `/api/v1/customers/{id:guid}/credit-memos` | CustomersController:131 | Credit.ViewPortfolio |  |  | oui |  |
| GET | `/api/v1/customers/{id:guid}/credits` | CustomersController:89 | Customers.View |  |  | oui |  |
| POST | `/api/v1/customers/{id:guid}/credits` | CustomersController:99 | Credit.Create | ✔ |  | oui |  |
| GET | `/api/v1/customers/{id:guid}/expenses` | CustomersController:120 | Credit.ViewPortfolio |  |  | oui |  |
| POST | `/api/v1/customers/{id:guid}/expenses` | CustomersController:126 | Customers.CreateExpense | ✔ |  | oui |  |
| GET | `/api/v1/customers/{id:guid}/ledger` | CustomersController:81 | Credit.ViewPortfolio |  |  | oui |  |
| GET | `/api/v1/customers/{id:guid}/payments` | CustomersController:104 | Credit.ViewPortfolio |  |  | oui |  |
| POST | `/api/v1/customers/{id:guid}/payments` | CustomersController:110 | Credit.ReceivePayment | ✔ |  | oui |  |
| GET | `/api/v1/customers/{id:guid}/purchases` | CustomersController:115 | Credit.ViewPortfolio |  |  | oui |  |
| GET | `/api/v1/customers/{id:guid}/refunds` | CustomersController:136 | Credit.ViewPortfolio |  |  | oui |  |
| POST | `/api/v1/customers/{id:guid}/status` | CustomersController:68 | Customers.Update |  |  | — |  |
| GET | `/api/v1/customers/{id:guid}/timeline` | CustomersController:141 | Credit.ViewPortfolio |  |  | oui |  |
| GET | `/api/v1/dashboard-layouts` | UxPersonalizationController:25 | authentifié |  |  | oui |  |
| PUT | `/api/v1/dashboard-layouts` | UxPersonalizationController:29 | authentifié |  |  | oui |  |
| POST | `/api/v1/dashboard-layouts/company-default` | UxPersonalizationController:34 | Admin.ConfigureCompany |  |  | oui |  |
| DELETE | `/api/v1/dashboard-layouts/{id:guid}` | UxPersonalizationController:38 | authentifié |  |  | oui |  |
| GET | `/api/v1/dashboard/activity` | ExperienceController:24 | authentifié |  |  | oui |  |
| GET | `/api/v1/dashboard/alerts` | DashboardController:50 | Reporting.View |  |  | oui |  |
| GET | `/api/v1/dashboard/cashier/{sessionId:guid}` | DashboardController:32 | Pos.View |  |  | — |  |
| GET | `/api/v1/dashboard/command-center` | DashboardController:25 | Reporting.View |  |  | oui |  |
| GET | `/api/v1/dashboard/drill` | DashboardController:56 | Reporting.ViewFinancial |  |  | — |  |
| GET | `/api/v1/dashboard/groups/{groupId:guid}` | DashboardController:44 | Reporting.ViewGroup |  |  | — |  |
| GET | `/api/v1/dashboard/kpi-catalog` | DashboardController:62 | Reporting.View |  |  | — |  |
| GET | `/api/v1/dashboard/manager` | DashboardController:38 | Reporting.ViewFinancial |  |  | — |  |
| GET | `/api/v1/dashboards` | BiController:194 | Reporting.View |  |  | — |  |
| POST | `/api/v1/dashboards` | BiController:204 | Reporting.View |  |  | — |  |
| POST | `/api/v1/delivery-notes` | DocumentsController:62 | Documents.Issue | ✔ |  | — |  |
| GET | `/api/v1/departments` | EmployeesController:29 | Hr.ViewEmployees |  |  | oui |  |
| POST | `/api/v1/departments` | EmployeesController:33 | Hr.ManageEmployees |  |  | oui |  |
| PUT | `/api/v1/departments/{id:guid}` | EmployeesController:38 | Hr.ManageEmployees |  |  | — |  |
| GET | `/api/v1/deployments` | OperationsController:85 | Operations.View |  |  | oui |  |
| POST | `/api/v1/deployments` | OperationsController:90 | Operations.Manage |  |  | oui |  |
| POST | `/api/v1/deployments/{id:guid}/rollback` | OperationsController:95 | Operations.Manage |  |  | oui |  |
| GET | `/api/v1/design-system/version` | DesignSystemController:16 | authentifié |  |  | oui |  |
| GET | `/api/v1/developer/keys` | MarketplaceController:88 | Integrations.ManageApiKeys |  |  | — |  |
| POST | `/api/v1/developer/keys` | MarketplaceController:92 | Integrations.ManageApiKeys |  |  | — |  |
| POST | `/api/v1/developer/keys/{id:guid}/revoke` | MarketplaceController:97 | Integrations.ManageApiKeys |  |  | — |  |
| GET | `/api/v1/developer/usage` | MarketplaceController:101 | Integrations.ManageApiKeys |  |  | oui |  |
| GET | `/api/v1/devices` | SyncController:46 | Sync.View |  |  | — |  |
| POST | `/api/v1/devices` | SyncController:52 | Sync.Devices |  |  | — |  |
| POST | `/api/v1/devices/{deviceId:guid}/revoke` | SyncController:58 | Sync.Devices |  |  | — |  |
| GET | `/api/v1/disaster-recovery/status` | ContinuityController:72 | Continuity.View |  |  | oui |  |
| GET | `/api/v1/document-audit` | GedController:278 | Ged.View |  |  | oui |  |
| GET | `/api/v1/document-retention-policies` | GedController:258 | Ged.View |  |  | oui |  |
| POST | `/api/v1/document-retention-policies` | GedController:262 | Ged.ManageRetention |  |  | oui |  |
| GET | `/api/v1/document-sequences` | DocumentsController:100 | Documents.ManageSequence |  |  | oui |  |
| POST | `/api/v1/document-shares/{shareId:guid}/revoke` | GedController:140 | Ged.Manage |  |  | oui |  |
| GET | `/api/v1/document-workflows` | GedController:68 | Ged.View |  |  | oui |  |
| POST | `/api/v1/document-workflows` | GedController:73 | Ged.Manage |  |  | oui |  |
| GET | `/api/v1/documents` | DocumentsController:26 | Documents.View |  |  | oui |  |
| POST | `/api/v1/documents` | DocumentsController:44 | Documents.Issue | ✔ |  | oui |  |
| GET | `/api/v1/documents/verify/{code}` | DocumentsController:93 | anonyme |  |  | oui |  |
| GET | `/api/v1/documents/{id:guid}` | DocumentsController:31 | Documents.View |  |  | oui |  |
| POST | `/api/v1/documents/{id:guid}/cancel` | DocumentsController:85 | Documents.Cancel |  | ✔ | oui |  |
| GET | `/api/v1/documents/{id:guid}/pdf` | DocumentsController:71 | Documents.View |  |  | — |  |
| POST | `/api/v1/documents/{id:guid}/send` | DocumentsController:79 | Documents.Send |  |  | oui |  |
| GET | `/api/v1/documents/{id:guid}/sends` | DocumentsController:36 | Documents.View |  |  | oui |  |
| POST | `/api/v1/employee-contracts/{id:guid}/end` | EmployeesController:91 | Hr.ManageEmployees |  |  | — |  |
| POST | `/api/v1/employee-documents/{id:guid}/archive` | EmployeesController:145 | Hr.ManageEmployees |  |  | — |  |
| GET | `/api/v1/employee-documents/{id:guid}/content` | EmployeesController:136 | Hr.ViewEmployees |  |  | — |  |
| POST | `/api/v1/employee-pay-elements/{id:guid}/end` | EmployeesController:120 | Payroll.ManageCompensation |  |  | — |  |
| GET | `/api/v1/employees` | EmployeesController:45 | Hr.ViewEmployees |  |  | oui |  |
| POST | `/api/v1/employees` | EmployeesController:50 | Hr.ManageEmployees |  |  | oui |  |
| GET | `/api/v1/employees/{id:guid}` | EmployeesController:54 | Hr.ViewEmployees |  |  | oui |  |
| PUT | `/api/v1/employees/{id:guid}` | EmployeesController:59 | Hr.ManageEmployees |  |  | oui |  |
| GET | `/api/v1/employees/{id:guid}/compensation` | EmployeesController:106 | Payroll.ViewSalaries |  |  | oui |  |
| GET | `/api/v1/employees/{id:guid}/contracts` | EmployeesController:82 | Hr.ViewEmployees |  |  | oui |  |
| POST | `/api/v1/employees/{id:guid}/contracts` | EmployeesController:86 | Hr.ManageEmployees |  |  | oui |  |
| GET | `/api/v1/employees/{id:guid}/documents` | EmployeesController:127 | Hr.ViewEmployees |  |  | — |  |
| POST | `/api/v1/employees/{id:guid}/documents` | EmployeesController:131 | Hr.ManageEmployees |  |  | — |  |
| GET | `/api/v1/employees/{id:guid}/history` | EmployeesController:151 | Payroll.ViewSalaries |  |  | — |  |
| GET | `/api/v1/employees/{id:guid}/pay-elements` | EmployeesController:111 | Payroll.ViewSalaries |  |  | oui |  |
| POST | `/api/v1/employees/{id:guid}/pay-elements` | EmployeesController:115 | Payroll.ManageCompensation |  |  | oui |  |
| GET | `/api/v1/employees/{id:guid}/payslips` | EmployeesController:155 | Payroll.ViewSalaries |  |  | — |  |
| GET | `/api/v1/employees/{id:guid}/salary-advances` | EmployeesController:159 | Hr.ViewEmployees |  |  | — |  |
| POST | `/api/v1/employees/{id:guid}/salary-advances` | EmployeesController:169 | Payroll.AdvanceRequest |  |  | — |  |
| POST | `/api/v1/employees/{id:guid}/salary-advances/preview` | EmployeesController:164 | Payroll.AdvanceRequest |  |  | — |  |
| POST | `/api/v1/employees/{id:guid}/salary-changes` | EmployeesController:100 | Payroll.ManageCompensation |  |  | — |  |
| GET | `/api/v1/employees/{id:guid}/salary-history` | EmployeesController:96 | Payroll.ViewSalaries |  |  | oui |  |
| GET | `/api/v1/employees/{id:guid}/sensitive` | EmployeesController:76 | Payroll.ViewSensitive |  | ✔ | oui |  |
| POST | `/api/v1/employees/{id:guid}/status` | EmployeesController:64 | Hr.ManageEmployees |  |  | — |  |
| POST | `/api/v1/employees/{id:guid}/user` | EmployeesController:69 | Hr.ManageEmployees |  |  | — |  |
| GET | `/api/v1/expense-categories` | FinancialAccountsController:122 | Finance.View |  |  | oui |  |
| POST | `/api/v1/expense-categories` | FinancialAccountsController:126 | Finance.Update |  |  | oui |  |
| PUT | `/api/v1/expense-categories/{id:guid}` | FinancialAccountsController:131 | Finance.Update |  |  | oui |  |
| GET | `/api/v1/expenses` | FinancialAccountsController:78 | Finance.View |  |  | oui |  |
| POST | `/api/v1/expenses` | FinancialAccountsController:88 | Finance.Create |  |  | oui |  |
| GET | `/api/v1/expenses/{id:guid}` | FinancialAccountsController:84 | Finance.View |  |  | oui |  |
| PUT | `/api/v1/expenses/{id:guid}` | FinancialAccountsController:92 | Finance.Create |  |  | oui |  |
| POST | `/api/v1/expenses/{id:guid}/approve` | FinancialAccountsController:101 | Finance.Pay |  |  | oui |  |
| POST | `/api/v1/expenses/{id:guid}/cancel` | FinancialAccountsController:110 | Finance.Create |  |  | oui |  |
| POST | `/api/v1/expenses/{id:guid}/pay` | FinancialAccountsController:117 | Finance.Pay | ✔ | ✔ | oui |  |
| POST | `/api/v1/expenses/{id:guid}/reject` | FinancialAccountsController:105 | Finance.Pay |  |  | oui |  |
| POST | `/api/v1/expenses/{id:guid}/submit` | FinancialAccountsController:97 | Finance.Create |  |  | oui |  |
| POST | `/api/v1/exports/screen` | PrintController:70 | Reporting.Export |  |  | oui |  |
| POST | `/api/v1/field-ops/device` | MobileController:42 | FieldOps.Manage |  |  | — |  |
| GET | `/api/v1/field-ops/interventions` | MobileController:151 | FieldOps.View |  |  | oui |  |
| POST | `/api/v1/field-ops/interventions` | MobileController:155 | FieldOps.Manage |  |  | oui |  |
| GET | `/api/v1/field-ops/interventions/mine` | MobileController:160 | FieldOps.View |  |  | oui |  |
| POST | `/api/v1/field-ops/interventions/{id:guid}/complete` | MobileController:169 | FieldOps.Manage |  |  | oui |  |
| POST | `/api/v1/field-ops/interventions/{id:guid}/start` | MobileController:164 | FieldOps.Manage |  |  | oui |  |
| GET | `/api/v1/field-ops/my-stops` | MobileController:128 | FieldOps.View |  |  | oui |  |
| GET | `/api/v1/field-ops/options` | MobileController:36 | FieldOps.View |  |  | oui |  |
| GET | `/api/v1/field-ops/routes` | MobileController:115 | FieldOps.View |  |  | oui |  |
| POST | `/api/v1/field-ops/routes` | MobileController:119 | FieldOps.Manage |  |  | oui |  |
| GET | `/api/v1/field-ops/routes/{id:guid}` | MobileController:124 | FieldOps.View |  |  | — |  |
| POST | `/api/v1/field-ops/stops/{id:guid}/advance` | MobileController:132 | FieldOps.Manage |  |  | oui |  |
| POST | `/api/v1/field-ops/stops/{id:guid}/complete` | MobileController:139 | FieldOps.Manage |  |  | oui |  |
| POST | `/api/v1/field-ops/stops/{id:guid}/fail` | MobileController:144 | FieldOps.Manage |  |  | oui |  |
| GET | `/api/v1/finance-lookups` | FinancialAccountsController:56 | Finance.View |  |  | oui |  |
| POST | `/api/v1/finance/bank-charges` | FinanceController:65 | Finance.Reconcile | ✔ |  | — |  |
| POST | `/api/v1/finance/cash-counts` | FinanceController:71 | Finance.ManageCash | ✔ |  | — |  |
| POST | `/api/v1/finance/cash-sales` | FinanceController:22 | Finance.Create | ✔ |  | — |  |
| POST | `/api/v1/finance/credit-sales` | FinanceController:28 | Finance.Create | ✔ |  | — |  |
| POST | `/api/v1/finance/customer-payments` | FinanceController:46 | Finance.Pay | ✔ |  | — |  |
| POST | `/api/v1/finance/expenses` | FinanceController:40 | Finance.Pay | ✔ |  | — |  |
| POST | `/api/v1/finance/supplier-invoices` | FinanceController:34 | Purchasing.Create | ✔ |  | — |  |
| POST | `/api/v1/finance/supplier-payments` | FinanceController:53 | Finance.Pay | ✔ | ✔ | — |  |
| POST | `/api/v1/finance/transfers` | FinanceController:59 | Finance.Transfer | ✔ |  | — |  |
| GET | `/api/v1/financial-accounts` | FinancialAccountsController:18 | Finance.View |  |  | oui |  |
| POST | `/api/v1/financial-accounts` | FinancialAccountsController:28 | Finance.Update |  |  | oui |  |
| GET | `/api/v1/financial-accounts/manage` | FinancialAccountsController:24 | Finance.View |  |  | oui |  |
| PUT | `/api/v1/financial-accounts/{id:guid}` | FinancialAccountsController:33 | Finance.Update |  |  | oui |  |
| POST | `/api/v1/financial-accounts/{id:guid}/status` | FinancialAccountsController:38 | Finance.Update |  |  | oui |  |
| GET | `/api/v1/financial-accounts/{id:guid}/transactions` | FinancialAccountsController:43 | Finance.View |  |  | oui |  |
| GET | `/api/v1/financial-statements` | FinancialStatementsController:20 | Accounting.View |  |  | oui |  |
| GET | `/api/v1/forecasts` | BiController:116 | Reporting.ViewFinancial |  |  | oui |  |
| POST | `/api/v1/forecasts` | BiController:120 | Reporting.Forecast |  |  | oui |  |
| POST | `/api/v1/ged-documents` | GedController:29 | Ged.Upload |  |  | oui |  |
| GET | `/api/v1/ged-documents/eligible-for-deletion` | GedController:272 | Ged.ManageRetention |  |  | oui |  |
| POST | `/api/v1/ged-documents/ocr` | GedController:214 | Ged.Upload |  |  | oui |  |
| GET | `/api/v1/ged-documents/search` | GedController:34 | Ged.View |  |  | oui |  |
| GET | `/api/v1/ged-documents/{id:guid}` | GedController:39 | Ged.View |  |  | oui |  |
| PUT | `/api/v1/ged-documents/{id:guid}` | GedController:43 | Ged.Manage |  |  | oui |  |
| POST | `/api/v1/ged-documents/{id:guid}/approve` | GedController:78 | Ged.Manage |  |  | oui |  |
| POST | `/api/v1/ged-documents/{id:guid}/archive` | GedController:99 | Ged.Manage |  |  | oui |  |
| POST | `/api/v1/ged-documents/{id:guid}/cancel-deletion` | GedController:118 | Ged.ManageRetention |  |  | oui |  |
| POST | `/api/v1/ged-documents/{id:guid}/confirm-deletion` | GedController:114 | Ged.ManageRetention |  |  | oui |  |
| GET | `/api/v1/ged-documents/{id:guid}/content` | GedController:57 | Ged.View |  |  | oui |  |
| POST | `/api/v1/ged-documents/{id:guid}/legal-hold` | GedController:267 | Ged.ManageRetention |  |  | oui |  |
| GET | `/api/v1/ged-documents/{id:guid}/links` | GedController:129 | Ged.View |  |  | — |  |
| POST | `/api/v1/ged-documents/{id:guid}/links` | GedController:124 | Ged.Manage |  |  | — |  |
| POST | `/api/v1/ged-documents/{id:guid}/lock` | GedController:89 | Ged.Manage |  |  | oui |  |
| POST | `/api/v1/ged-documents/{id:guid}/reject` | GedController:82 | Ged.Manage |  |  | oui |  |
| POST | `/api/v1/ged-documents/{id:guid}/request-deletion` | GedController:109 | Ged.ManageRetention |  |  | oui |  |
| POST | `/api/v1/ged-documents/{id:guid}/restore` | GedController:103 | Ged.Manage |  |  | oui |  |
| POST | `/api/v1/ged-documents/{id:guid}/share` | GedController:135 | Ged.Manage |  |  | oui |  |
| POST | `/api/v1/ged-documents/{id:guid}/signature` | GedController:221 | Ged.Sign |  |  | oui |  |
| POST | `/api/v1/ged-documents/{id:guid}/unlock` | GedController:93 | Ged.Manage |  |  | oui |  |
| GET | `/api/v1/ged-documents/{id:guid}/versions` | GedController:53 | Ged.View |  |  | oui |  |
| POST | `/api/v1/ged-documents/{id:guid}/versions` | GedController:48 | Ged.Upload |  |  | oui |  |
| GET | `/api/v1/general-ledger` | FinancialStatementsController:34 | Accounting.View |  |  | oui |  |
| GET | `/api/v1/go-live` | OperationsController:69 | Operations.View |  |  | oui |  |
| POST | `/api/v1/go-live/sign-off` | OperationsController:77 | Operations.Manage |  |  | oui |  |
| GET | `/api/v1/go-live/sign-offs` | OperationsController:73 | Operations.View |  |  | oui |  |
| GET | `/api/v1/groups/{groupId:guid}/accounts` | GroupController:28 | Group.Mappings |  |  | oui |  |
| POST | `/api/v1/groups/{groupId:guid}/accounts` | GroupController:33 | Group.Mappings |  |  | oui |  |
| GET | `/api/v1/groups/{groupId:guid}/accounts/mapping-coverage` | GroupController:53 | Group.Mappings |  |  | oui |  |
| GET | `/api/v1/groups/{groupId:guid}/accounts/mappings` | GroupController:43 | Group.Mappings |  |  | oui |  |
| PUT | `/api/v1/groups/{groupId:guid}/accounts/mappings` | GroupController:38 | Group.Mappings |  |  | oui |  |
| POST | `/api/v1/groups/{groupId:guid}/companies` | GroupController:121 | Group.Companies |  |  | oui |  |
| POST | `/api/v1/groups/{groupId:guid}/companies/{companyId:guid}/archive` | GroupController:127 | Group.Companies |  | ✔ | oui |  |
| GET | `/api/v1/groups/{groupId:guid}/consolidation/runs` | ConsolidationController:23 | Group.View |  |  | oui |  |
| POST | `/api/v1/groups/{groupId:guid}/consolidation/runs` | ConsolidationController:66 | Consolidation.Run |  |  | oui |  |
| GET | `/api/v1/groups/{groupId:guid}/consolidation/runs/{runId:guid}` | ConsolidationController:56 | Group.View |  |  | oui |  |
| POST | `/api/v1/groups/{groupId:guid}/consolidation/runs/{runId:guid}/execute` | ConsolidationController:71 | Consolidation.Run |  |  | oui |  |
| POST | `/api/v1/groups/{groupId:guid}/consolidation/runs/{runId:guid}/publish` | ConsolidationController:82 | Consolidation.Publish |  | ✔ | oui |  |
| POST | `/api/v1/groups/{groupId:guid}/consolidation/runs/{runId:guid}/reopen` | ConsolidationController:88 | Consolidation.Validate |  | ✔ | oui |  |
| GET | `/api/v1/groups/{groupId:guid}/consolidation/runs/{runId:guid}/statements` | ConsolidationController:61 | Group.View |  |  | oui |  |
| POST | `/api/v1/groups/{groupId:guid}/consolidation/runs/{runId:guid}/validate` | ConsolidationController:76 | Consolidation.Validate |  |  | oui |  |
| GET | `/api/v1/groups/{groupId:guid}/control-center` | GroupController:97 | Group.View |  |  | oui |  |
| GET | `/api/v1/groups/{groupId:guid}/intercompany` | GroupController:70 | Group.Intercompany |  |  | oui |  |
| POST | `/api/v1/groups/{groupId:guid}/intercompany` | GroupController:75 | Group.Intercompany |  |  | oui |  |
| POST | `/api/v1/groups/{groupId:guid}/intercompany/reconcile` | GroupController:85 | Group.Intercompany |  |  | oui |  |
| GET | `/api/v1/groups/{groupId:guid}/intercompany/unreconciled` | GroupController:90 | Group.Intercompany |  |  | oui |  |
| POST | `/api/v1/groups/{groupId:guid}/intercompany/{transactionId:guid}/confirm` | GroupController:80 | Group.Intercompany |  |  | oui |  |
| GET | `/api/v1/groups/{groupId:guid}/options` | GroupController:48 | Group.View |  |  | oui |  |
| GET | `/api/v1/groups/{groupId:guid}/policy` | GroupController:58 | Group.View |  |  | oui |  |
| PUT | `/api/v1/groups/{groupId:guid}/policy` | GroupController:63 | Group.Manage |  |  | oui |  |
| GET | `/api/v1/groups/{groupId:guid}/tasks` | GroupController:104 | Group.Tasks |  |  | oui |  |
| POST | `/api/v1/groups/{groupId:guid}/tasks` | GroupController:109 | Group.Tasks |  |  | oui |  |
| PUT | `/api/v1/groups/{groupId:guid}/tasks/{taskId:guid}` | GroupController:114 | Group.Tasks |  |  | oui |  |
| GET | `/api/v1/hr/approvals` | HrController:132 | Hr.ViewEmployees |  |  | oui |  |
| GET | `/api/v1/hr/attendance` | HrController:28 | Hr.ViewEmployees |  |  | oui |  |
| POST | `/api/v1/hr/attendance` | HrController:35 | Hr.ManageTime |  |  | oui |  |
| POST | `/api/v1/hr/attendance/{id:guid}/reject` | HrController:45 | Hr.ApproveRequests |  |  | — |  |
| POST | `/api/v1/hr/attendance/{id:guid}/validate` | HrController:40 | Hr.ApproveRequests |  |  | — |  |
| GET | `/api/v1/hr/audit` | HrController:211 | Payroll.Audit |  |  | — |  |
| GET | `/api/v1/hr/dashboard` | HrController:172 | Payroll.Reports |  |  | oui |  |
| GET | `/api/v1/hr/expense-claims` | HrController:138 | Hr.ViewEmployees |  |  | — |  |
| POST | `/api/v1/hr/expense-claims` | HrController:143 | Hr.ManageTime |  |  | — |  |
| POST | `/api/v1/hr/expense-claims/{id:guid}/approve` | HrController:148 | Payroll.ExpenseApprove |  |  | — |  |
| POST | `/api/v1/hr/expense-claims/{id:guid}/cancel` | HrController:158 | Hr.ViewEmployees |  |  | — |  |
| POST | `/api/v1/hr/expense-claims/{id:guid}/pay` | HrController:165 | Payroll.ExpenseApprove | ✔ | ✔ | — |  |
| POST | `/api/v1/hr/expense-claims/{id:guid}/reject` | HrController:153 | Payroll.ExpenseApprove |  |  | — |  |
| GET | `/api/v1/hr/insights` | HrController:176 | Payroll.Reports |  |  | — |  |
| GET | `/api/v1/hr/leaves` | HrController:52 | Hr.ViewEmployees |  |  | — |  |
| POST | `/api/v1/hr/leaves` | HrController:57 | Hr.ManageTime |  |  | — |  |
| POST | `/api/v1/hr/leaves/{id:guid}/approve` | HrController:61 | Hr.ApproveRequests |  |  | — |  |
| POST | `/api/v1/hr/leaves/{id:guid}/cancel` | HrController:71 | Hr.ViewEmployees |  |  | — |  |
| POST | `/api/v1/hr/leaves/{id:guid}/reject` | HrController:66 | Hr.ApproveRequests |  |  | — |  |
| GET | `/api/v1/hr/overtime` | HrController:78 | Hr.ViewEmployees |  |  | — |  |
| POST | `/api/v1/hr/overtime` | HrController:83 | Hr.ManageTime |  |  | — |  |
| POST | `/api/v1/hr/overtime/{id:guid}/approve` | HrController:88 | Hr.ApproveRequests |  |  | — |  |
| POST | `/api/v1/hr/overtime/{id:guid}/cancel` | HrController:98 | Hr.ViewEmployees |  |  | — |  |
| POST | `/api/v1/hr/overtime/{id:guid}/reject` | HrController:93 | Hr.ApproveRequests |  |  | — |  |
| GET | `/api/v1/hr/reports` | HrController:180 | Payroll.Reports |  |  | oui |  |
| GET | `/api/v1/hr/reports/{key}` | HrController:184 | Payroll.Reports |  |  | oui |  |
| GET | `/api/v1/hr/reports/{key}/export` | HrController:191 | Payroll.Reports |  |  | oui |  |
| GET | `/api/v1/hr/variables` | HrController:105 | Hr.ViewEmployees |  |  | oui |  |
| POST | `/api/v1/hr/variables` | HrController:111 | Hr.ViewEmployees |  |  | oui |  |
| POST | `/api/v1/hr/variables/{id:guid}/approve` | HrController:116 | Hr.ApproveRequests |  |  | — |  |
| POST | `/api/v1/hr/variables/{id:guid}/cancel` | HrController:126 | Hr.ViewEmployees |  |  | — |  |
| POST | `/api/v1/hr/variables/{id:guid}/reject` | HrController:121 | Hr.ApproveRequests |  |  | — |  |
| GET | `/api/v1/import-templates` | ImportsController:97 | Migration.View |  |  | — |  |
| GET | `/api/v1/imports` | ImportsController:21 | Migration.View |  |  | oui |  |
| POST | `/api/v1/imports` | ImportsController:29 | Migration.Manage |  |  | oui |  |
| POST | `/api/v1/imports/suggest-mapping/{kind}` | ImportsController:92 | Migration.View |  |  | oui |  |
| GET | `/api/v1/imports/target-fields/{kind}` | ImportsController:88 | Migration.View |  |  | oui |  |
| GET | `/api/v1/imports/{id:guid}` | ImportsController:25 | Migration.View |  |  | oui |  |
| POST | `/api/v1/imports/{id:guid}/cancel` | ImportsController:61 | Migration.Manage |  |  | oui |  |
| GET | `/api/v1/imports/{id:guid}/errors` | ImportsController:70 | Migration.View |  |  | oui |  |
| GET | `/api/v1/imports/{id:guid}/events` | ImportsController:82 | Migration.View |  |  | — |  |
| POST | `/api/v1/imports/{id:guid}/execute` | ImportsController:49 | Migration.Manage |  |  | oui |  |
| POST | `/api/v1/imports/{id:guid}/mapping` | ImportsController:37 | Migration.Manage |  |  | oui |  |
| POST | `/api/v1/imports/{id:guid}/pause` | ImportsController:53 | Migration.Manage |  |  | — |  |
| POST | `/api/v1/imports/{id:guid}/profile` | ImportsController:33 | Migration.Manage |  |  | oui |  |
| GET | `/api/v1/imports/{id:guid}/reconciliation` | ImportsController:78 | Migration.View |  |  | oui |  |
| POST | `/api/v1/imports/{id:guid}/resume` | ImportsController:57 | Migration.Manage |  |  | — |  |
| GET | `/api/v1/imports/{id:guid}/rows` | ImportsController:65 | Migration.View |  |  | oui |  |
| POST | `/api/v1/imports/{id:guid}/rows` | ImportsController:74 | Migration.Manage |  |  | oui |  |
| POST | `/api/v1/imports/{id:guid}/simulate` | ImportsController:45 | Migration.Manage |  |  | oui |  |
| POST | `/api/v1/imports/{id:guid}/validate` | ImportsController:41 | Migration.Manage |  |  | oui |  |
| GET | `/api/v1/incidents` | ContinuityController:80 | Continuity.View |  |  | — |  |
| GET | `/api/v1/insights` | BiController:129 | Reporting.View |  |  | oui |  |
| POST | `/api/v1/insights/{id:guid}/assign` | BiController:147 | Reporting.ManageInsights |  |  | oui |  |
| POST | `/api/v1/insights/{id:guid}/dismiss` | BiController:142 | Reporting.ManageInsights |  |  | oui |  |
| GET | `/api/v1/integrations` | IntegrationsController:73 | Integrations.View |  |  | — |  |
| POST | `/api/v1/integrations` | IntegrationsController:77 | Integrations.Configure |  |  | — |  |
| GET | `/api/v1/integrations/bank/lines/{lineId:guid}/candidates` | IntegrationsController:225 | Integrations.Bank |  |  | oui |  |
| POST | `/api/v1/integrations/bank/lines/{lineId:guid}/match` | IntegrationsController:233 | Integrations.Bank |  |  | oui |  |
| POST | `/api/v1/integrations/bank/lines/{lineId:guid}/unmatch` | IntegrationsController:229 | Integrations.Bank |  |  | oui |  |
| GET | `/api/v1/integrations/bank/statements` | IntegrationsController:204 | Integrations.Bank |  |  | oui |  |
| POST | `/api/v1/integrations/bank/statements` | IntegrationsController:208 | Integrations.Bank |  |  | oui |  |
| POST | `/api/v1/integrations/bank/statements/{id:guid}/auto-match` | IntegrationsController:217 | Integrations.Bank |  |  | oui |  |
| GET | `/api/v1/integrations/bank/statements/{id:guid}/lines` | IntegrationsController:213 | Integrations.Bank |  |  | oui |  |
| POST | `/api/v1/integrations/bank/statements/{id:guid}/validate` | IntegrationsController:221 | Integrations.Bank |  |  | oui |  |
| GET | `/api/v1/integrations/center` | IntegrationsController:240 | Integrations.View |  |  | oui |  |
| GET | `/api/v1/integrations/connectors` | IntegrationsController:33 | Integrations.View |  |  | oui |  |
| POST | `/api/v1/integrations/connectors` | IntegrationsController:41 | Integrations.Configure |  |  | oui |  |
| GET | `/api/v1/integrations/connectors/{id:guid}` | IntegrationsController:37 | Integrations.View |  |  | — |  |
| PUT | `/api/v1/integrations/connectors/{id:guid}/credentials` | IntegrationsController:57 | Integrations.Configure |  |  | oui |  |
| POST | `/api/v1/integrations/connectors/{id:guid}/rotate-secret` | IntegrationsController:66 | Integrations.Configure |  |  | oui |  |
| POST | `/api/v1/integrations/connectors/{id:guid}/status` | IntegrationsController:46 | Integrations.Configure |  |  | oui |  |
| POST | `/api/v1/integrations/connectors/{id:guid}/test` | IntegrationsController:51 | Integrations.Configure |  |  | oui |  |
| GET | `/api/v1/integrations/payment-providers` | IntegrationsController:62 | Integrations.View |  |  | oui |  |
| GET | `/api/v1/integrations/payments/intents` | IntegrationsController:102 | Integrations.Payments |  |  | oui |  |
| POST | `/api/v1/integrations/payments/intents` | IntegrationsController:107 | Integrations.Payments |  |  | oui |  |
| POST | `/api/v1/integrations/payments/intents/expire` | IntegrationsController:136 | Integrations.Payments |  |  | oui |  |
| GET | `/api/v1/integrations/payments/intents/{id:guid}` | IntegrationsController:112 | Integrations.Payments |  |  | — |  |
| POST | `/api/v1/integrations/payments/intents/{id:guid}/callback` | IntegrationsController:117 | Integrations.Payments |  |  | oui |  |
| POST | `/api/v1/integrations/payments/intents/{id:guid}/refund` | IntegrationsController:131 | Integrations.Payments, Pos.Refund |  | ✔ | oui |  |
| POST | `/api/v1/integrations/payments/intents/{id:guid}/verify` | IntegrationsController:125 | Integrations.Payments |  |  | oui |  |
| GET | `/api/v1/integrations/payments/reconciliation` | IntegrationsController:140 | Integrations.Payments |  |  | — |  |
| GET | `/api/v1/integrations/webhooks/events` | IntegrationsController:159 | Integrations.Webhooks |  |  | oui |  |
| POST | `/api/v1/integrations/webhooks/inbound/{connectorCode}` | IntegrationsController:147 | anonyme |  |  | — |  |
| GET | `/api/v1/integrations/webhooks/outbound` | IntegrationsController:166 | Integrations.Webhooks |  |  | oui |  |
| POST | `/api/v1/integrations/webhooks/outbound` | IntegrationsController:170 | Integrations.Webhooks |  |  | oui |  |
| POST | `/api/v1/integrations/webhooks/outbound/deliver-due` | IntegrationsController:198 | Integrations.Webhooks |  |  | oui |  |
| GET | `/api/v1/integrations/webhooks/outbound/deliveries` | IntegrationsController:189 | Integrations.Webhooks |  |  | oui |  |
| POST | `/api/v1/integrations/webhooks/outbound/deliveries/{id:guid}/retry` | IntegrationsController:194 | Integrations.Webhooks |  |  | oui |  |
| POST | `/api/v1/integrations/webhooks/outbound/emit` | IntegrationsController:180 | Integrations.Webhooks |  |  | — |  |
| POST | `/api/v1/integrations/webhooks/outbound/{id:guid}/status` | IntegrationsController:175 | Integrations.Webhooks |  |  | — |  |
| GET | `/api/v1/integrations/{id:guid}/health` | IntegrationsController:82 | Integrations.View |  |  | — |  |
| POST | `/api/v1/integrations/{id:guid}/sync` | IntegrationsController:94 | Integrations.Configure |  |  | — |  |
| POST | `/api/v1/integrations/{id:guid}/test` | IntegrationsController:86 | Integrations.Configure |  |  | — |  |
| GET | `/api/v1/intelligence/anomalies` | IntelligenceController:89 | Intelligence.View |  |  | oui |  |
| GET | `/api/v1/intelligence/audit` | IntelligenceController:126 | Intelligence.View |  |  | oui |  |
| GET | `/api/v1/intelligence/dashboard` | IntelligenceController:78 | Intelligence.View |  |  | oui |  |
| GET | `/api/v1/intelligence/forecasts` | IntelligenceController:93 | Intelligence.View |  |  | oui |  |
| GET | `/api/v1/intelligence/insights` | IntelligenceController:84 | Intelligence.View |  |  | — |  |
| GET | `/api/v1/intelligence/models` | IntelligenceController:105 | Intelligence.View |  |  | oui |  |
| GET | `/api/v1/intelligence/recommendations` | IntelligenceController:36 | Intelligence.View |  |  | oui |  |
| GET | `/api/v1/intelligence/recommendations/{id:guid}` | IntelligenceController:41 | Intelligence.View |  |  | — |  |
| POST | `/api/v1/intelligence/recommendations/{id:guid}/accept` | IntelligenceController:50 | Intelligence.ActOnRecommendation |  |  | oui |  |
| POST | `/api/v1/intelligence/recommendations/{id:guid}/dismiss` | IntelligenceController:55 | Intelligence.ActOnRecommendation |  |  | oui |  |
| POST | `/api/v1/intelligence/recommendations/{id:guid}/modify` | IntelligenceController:72 | Intelligence.ActOnRecommendation |  |  | oui |  |
| POST | `/api/v1/intelligence/recommendations/{id:guid}/resolve` | IntelligenceController:66 | Intelligence.ActOnRecommendation |  |  | oui |  |
| POST | `/api/v1/intelligence/recommendations/{id:guid}/snooze` | IntelligenceController:60 | Intelligence.ActOnRecommendation |  |  | oui |  |
| POST | `/api/v1/intelligence/recommendations/{id:guid}/view` | IntelligenceController:46 | Intelligence.View |  |  | oui |  |
| GET | `/api/v1/intelligence/rules` | IntelligenceController:111 | Intelligence.View |  |  | oui |  |
| POST | `/api/v1/intelligence/rules` | IntelligenceController:115 | Intelligence.ManageRules |  |  | oui |  |
| DELETE | `/api/v1/intelligence/rules/{id:guid}` | IntelligenceController:120 | Intelligence.ManageRules |  |  | oui |  |
| GET | `/api/v1/intelligence/scores` | IntelligenceController:99 | Intelligence.View |  |  | oui |  |
| GET | `/api/v1/intelligence/tasks` | IntelligenceController:132 | Intelligence.View |  |  | oui |  |
| POST | `/api/v1/intelligence/tasks` | IntelligenceController:136 | Intelligence.ActOnRecommendation |  |  | oui |  |
| POST | `/api/v1/intelligence/tasks/{id:guid}/complete` | IntelligenceController:141 | Intelligence.ActOnRecommendation |  |  | oui |  |
| GET | `/api/v1/inventory/adjustments` | InventoryController:101 | Inventory.View |  |  | oui |  |
| POST | `/api/v1/inventory/adjustments` | InventoryController:121 | Inventory.Adjust | ✔ |  | oui |  |
| GET | `/api/v1/inventory/adjustments/{id:guid}` | InventoryController:108 | Inventory.View |  |  | oui |  |
| GET | `/api/v1/inventory/balance` | InventoryController:22 | Inventory.View |  |  | — |  |
| GET | `/api/v1/inventory/counts` | InventoryController:66 | Inventory.View |  |  | oui |  |
| POST | `/api/v1/inventory/counts` | InventoryController:77 | Inventory.Count |  |  | oui |  |
| GET | `/api/v1/inventory/counts/{id:guid}` | InventoryController:71 | Inventory.View |  |  | oui |  |
| POST | `/api/v1/inventory/counts/{id:guid}/cancel` | InventoryController:95 | Inventory.Count |  |  | — |  |
| PUT | `/api/v1/inventory/counts/{id:guid}/lines` | InventoryController:83 | Inventory.Count |  |  | oui |  |
| POST | `/api/v1/inventory/counts/{id:guid}/validate` | InventoryController:90 | Inventory.ValidateAdjustment | ✔ |  | — |  |
| POST | `/api/v1/inventory/losses` | InventoryController:114 | Inventory.WriteOff | ✔ |  | — |  |
| GET | `/api/v1/inventory/reorder-suggestions` | InventoryController:126 | Purchasing.View |  |  | — |  |
| GET | `/api/v1/inventory/transfers` | InventoryController:33 | Inventory.View |  |  | oui |  |
| POST | `/api/v1/inventory/transfers` | InventoryController:43 | Inventory.Transfer |  |  | oui |  |
| GET | `/api/v1/inventory/transfers/{id:guid}` | InventoryController:38 | Inventory.View |  |  | oui |  |
| POST | `/api/v1/inventory/transfers/{id:guid}/cancel` | InventoryController:60 | Inventory.Transfer |  |  | — |  |
| POST | `/api/v1/inventory/transfers/{id:guid}/close` | InventoryController:55 | Inventory.Transfer | ✔ |  | — |  |
| POST | `/api/v1/inventory/transfers/{id:guid}/receive` | InventoryController:48 | Inventory.Transfer |  |  | — |  |
| GET | `/api/v1/invoices` | InvoicesController:20 | Documents.View |  |  | — |  |
| POST | `/api/v1/invoices/{id:guid}/cancel` | InvoicesController:37 | Documents.Cancel |  | ✔ | — |  |
| POST | `/api/v1/invoices/{saleId:guid}/issue` | InvoicesController:31 | Documents.Issue | ✔ |  | — |  |
| GET | `/api/v1/journals` | ChartOfAccountsController:54 | Accounting.View |  |  | oui |  |
| GET | `/api/v1/kpi-definitions` | BiController:38 | Reporting.View |  |  | oui |  |
| GET | `/api/v1/kpi-definitions/{key}` | BiController:43 | Reporting.View |  |  | — |  |
| GET | `/api/v1/kpis` | AnalyticsController:60 | Reporting.View |  |  | oui |  |
| GET | `/api/v1/kpis/{key}/definition` | BiController:43 | Reporting.View |  |  | — |  |
| GET | `/api/v1/ledger-options` | FinancialAccountsController:50 | Finance.View |  |  | oui |  |
| GET | `/api/v1/licenses` | LicensesController:29 | Platform.Portal |  |  | oui |  |
| POST | `/api/v1/licenses/verify` | LicensesController:55 | anonyme |  |  | — |  |
| POST | `/api/v1/licenses/{id:guid}/revoke` | LicensesController:49 | Platform.ManageLicenses |  |  | — |  |
| POST | `/api/v1/licenses/{id:guid}/token` | LicensesController:34 | Platform.Portal |  |  | oui |  |
| GET | `/api/v1/me` | MeController:25 | authentifié |  |  | oui |  |
| GET | `/api/v1/me/hr/advances` | MeHrController:66 | Payroll.SelfService |  |  | — |  |
| POST | `/api/v1/me/hr/advances` | MeHrController:73 | Payroll.SelfService |  |  | — |  |
| POST | `/api/v1/me/hr/advances/preview` | MeHrController:69 | Payroll.SelfService |  |  | — |  |
| POST | `/api/v1/me/hr/advances/{id:guid}/cancel` | MeHrController:87 | Payroll.SelfService |  |  | — |  |
| GET | `/api/v1/me/hr/advances/{id:guid}/history` | MeHrController:77 | Payroll.SelfService |  |  | — |  |
| GET | `/api/v1/me/hr/documents` | MeHrController:131 | Payroll.SelfService |  |  | — |  |
| GET | `/api/v1/me/hr/documents/{id:guid}/content` | MeHrController:134 | Payroll.SelfService |  |  | — |  |
| GET | `/api/v1/me/hr/expense-claims` | MeHrController:122 | Payroll.SelfService |  |  | — |  |
| POST | `/api/v1/me/hr/expense-claims` | MeHrController:125 | Payroll.SelfService |  |  | — |  |
| GET | `/api/v1/me/hr/history` | MeHrController:37 | Payroll.SelfService |  |  | — |  |
| GET | `/api/v1/me/hr/leaves` | MeHrController:99 | Payroll.SelfService |  |  | — |  |
| POST | `/api/v1/me/hr/leaves` | MeHrController:102 | Payroll.SelfService |  |  | — |  |
| POST | `/api/v1/me/hr/leaves/{id:guid}/cancel` | MeHrController:106 | Payroll.SelfService |  |  | — |  |
| GET | `/api/v1/me/hr/overtime` | MeHrController:115 | Payroll.SelfService |  |  | — |  |
| POST | `/api/v1/me/hr/overtime` | MeHrController:118 | Payroll.SelfService |  |  | — |  |
| GET | `/api/v1/me/hr/payroll-items/{id:guid}` | MeHrController:54 | Payroll.SelfService |  |  | — |  |
| GET | `/api/v1/me/hr/payslips` | MeHrController:42 | Payroll.SelfService |  |  | — |  |
| GET | `/api/v1/me/hr/payslips/{id:guid}/file` | MeHrController:45 | Payroll.SelfService |  |  | — |  |
| GET | `/api/v1/me/hr/profile` | MeHrController:34 | Payroll.SelfService |  |  | oui |  |
| POST | `/api/v1/me/logout` | MeController:62 | authentifié |  |  | — |  |
| POST | `/api/v1/me/password` | MeController:79 | authentifié |  |  | oui |  |
| GET | `/api/v1/me/permissions` | MeController:49 | authentifié |  |  | — |  |
| GET | `/api/v1/me/preferences` | PreferencesController:25 | authentifié |  |  | — |  |
| PUT | `/api/v1/me/preferences` | PreferencesController:30 | authentifié |  |  | — |  |
| GET | `/api/v1/me/scopes` | MeController:87 | authentifié |  |  | — |  |
| GET | `/api/v1/message-templates` | MessageTemplatesController:21 | Notifications.ManageTemplates |  |  | oui |  |
| POST | `/api/v1/message-templates` | MessageTemplatesController:25 | Notifications.ManageTemplates |  |  | oui |  |
| POST | `/api/v1/message-templates/render` | MessageTemplatesController:30 | Notifications.ManageTemplates |  |  | oui |  |
| GET | `/api/v1/metrics` | MetricsController:22 | anonyme |  |  | — | A-024 (anonyme, contournement du blocage Caddy) |
| GET | `/api/v1/mobile/config` | MobileController:74 | Sync.View |  |  | oui |  |
| GET | `/api/v1/mobile/conflicts` | MobileController:63 | Sync.View |  |  | — |  |
| POST | `/api/v1/mobile/conflicts/{conflictId:guid}/resolve` | MobileController:67 | Sync.Manage |  |  | — |  |
| GET | `/api/v1/mobile/crash-reports` | MobileController:83 | Sync.Manage |  |  | oui |  |
| POST | `/api/v1/mobile/crash-reports` | MobileController:78 | Sync.View |  |  | oui |  |
| GET | `/api/v1/mobile/notifications` | MobileController:88 | Sync.View |  |  | — |  |
| GET | `/api/v1/mobile/sync` | MobileController:49 | Sync.View |  |  | — |  |
| POST | `/api/v1/mobile/sync/pull` | MobileController:59 | Sync.View |  |  | — |  |
| POST | `/api/v1/mobile/sync/push` | MobileController:53 | Sync.View |  |  | — |  |
| GET | `/api/v1/mobile/tasks` | MobileController:93 | Sync.View |  |  | — |  |
| POST | `/api/v1/notification-delivery/dispatch` | NotificationDeliveryController:56 | Notifications.ManageDelivery |  |  | oui |  |
| GET | `/api/v1/notification-delivery/outbox` | NotificationDeliveryController:38 | Notifications.ManageDelivery |  |  | oui |  |
| GET | `/api/v1/notification-delivery/outbox/{id:guid}/log` | NotificationDeliveryController:43 | Notifications.ManageDelivery |  |  | oui |  |
| POST | `/api/v1/notification-delivery/outbox/{id:guid}/retry` | NotificationDeliveryController:47 | Notifications.ManageDelivery |  |  | oui |  |
| GET | `/api/v1/notification-delivery/providers` | NotificationDeliveryController:21 | Notifications.ManageDelivery |  |  | oui |  |
| POST | `/api/v1/notification-delivery/providers/{channel}/requeue` | NotificationDeliveryController:51 | Notifications.ManageDelivery |  |  | oui |  |
| POST | `/api/v1/notification-delivery/providers/{channel}/test` | NotificationDeliveryController:25 | Notifications.ManageDelivery |  |  | oui |  |
| GET | `/api/v1/notification-delivery/summary` | NotificationDeliveryController:34 | Notifications.ManageDelivery |  |  | oui |  |
| POST | `/api/v1/notification-delivery/test-message` | NotificationDeliveryController:29 | Notifications.ManageDelivery |  |  | oui |  |
| GET | `/api/v1/notification-preferences` | NotificationsController:60 | authentifié |  |  | oui |  |
| POST | `/api/v1/notification-preferences` | NotificationsController:70 | authentifié |  |  | oui |  |
| GET | `/api/v1/notification-preferences/channels` | NotificationsController:65 | authentifié |  |  | oui |  |
| DELETE | `/api/v1/notification-preferences/{id:guid}` | NotificationsController:74 | authentifié |  |  | oui |  |
| GET | `/api/v1/notifications` | NotificationsController:17 | authentifié |  |  | oui |  |
| POST | `/api/v1/notifications/read-all` | NotificationsController:33 | authentifié |  |  | oui |  |
| GET | `/api/v1/notifications/unread-count` | NotificationsController:21 | authentifié |  |  | oui |  |
| POST | `/api/v1/notifications/{id:guid}/archive` | NotificationsController:45 | authentifié |  |  | — |  |
| POST | `/api/v1/notifications/{id:guid}/pin` | NotificationsController:37 | authentifié |  |  | — |  |
| PATCH | `/api/v1/notifications/{id:guid}/read` | NotificationsController:25 | authentifié |  |  | — |  |
| POST | `/api/v1/notifications/{id:guid}/unarchive` | NotificationsController:49 | authentifié |  |  | — |  |
| POST | `/api/v1/notifications/{id:guid}/unpin` | NotificationsController:41 | authentifié |  |  | — |  |
| PATCH | `/api/v1/notifications/{id:guid}/unread` | NotificationsController:29 | authentifié |  |  | — |  |
| GET | `/api/v1/objectives` | BiController:49 | Reporting.View |  |  | oui |  |
| POST | `/api/v1/objectives` | BiController:53 | Reporting.ManageTargets |  |  | oui |  |
| GET | `/api/v1/objectives/score` | BiController:62 | Reporting.View |  |  | oui |  |
| DELETE | `/api/v1/objectives/{id:guid}` | BiController:58 | Reporting.ManageTargets |  |  | oui |  |
| GET | `/api/v1/onboarding/checklist` | OnboardingController:17 | Migration.View |  |  | oui |  |
| POST | `/api/v1/onboarding/tasks` | OnboardingController:21 | Migration.Manage |  |  | oui |  |
| GET | `/api/v1/ops/audit` | SecurityController:36 | Security.AuditView |  |  | — |  |
| POST | `/api/v1/ops/cache/invalidate` | OpsMaintenanceController:93 | Operations.Manage |  |  | oui |  |
| GET | `/api/v1/ops/data-corrections` | OpsMaintenanceController:160 | Operations.View |  |  | oui | A-031 (fuite entre entreprises) |
| POST | `/api/v1/ops/data-corrections` | OpsMaintenanceController:164 | Operations.Manage |  |  | oui | A-031 |
| POST | `/api/v1/ops/data-corrections/{id:guid}/decide` | OpsMaintenanceController:169 | Operations.CorrectData |  |  | oui | A-031 (écriture entre entreprises) |
| GET | `/api/v1/ops/database` | OpsMaintenanceController:83 | Operations.View |  |  | oui |  |
| GET | `/api/v1/ops/dead-letters` | OpsMaintenanceController:64 | Operations.View |  |  | oui |  |
| DELETE | `/api/v1/ops/dead-letters/{id:guid}` | OpsMaintenanceController:72 | Operations.Manage |  |  | oui |  |
| POST | `/api/v1/ops/dead-letters/{id:guid}/replay` | OpsMaintenanceController:68 | Operations.Manage |  |  | oui |  |
| GET | `/api/v1/ops/diagnostics` | OpsMaintenanceController:79 | Operations.View |  |  | oui |  |
| GET | `/api/v1/ops/feature-flags` | OpsMaintenanceController:103 | Operations.View |  |  | oui |  |
| POST | `/api/v1/ops/feature-flags` | OpsMaintenanceController:107 | Operations.Manage |  |  | oui | A-031 (bascules non consommées) |
| DELETE | `/api/v1/ops/feature-flags/{id:guid}` | OpsMaintenanceController:112 | Operations.Manage |  |  | oui |  |
| GET | `/api/v1/ops/health` | OpsMaintenanceController:79 | Operations.View |  |  | — |  |
| GET | `/api/v1/ops/jobs` | OpsMaintenanceController:34 | Operations.View |  |  | oui |  |
| GET | `/api/v1/ops/jobs/{key}/history` | OpsMaintenanceController:38 | Operations.View |  |  | — |  |
| POST | `/api/v1/ops/jobs/{key}/pause` | OpsMaintenanceController:46 | Operations.Manage |  |  | oui | A-031 (tâches globales) |
| POST | `/api/v1/ops/jobs/{key}/resume` | OpsMaintenanceController:50 | Operations.Manage |  |  | oui | A-031 |
| POST | `/api/v1/ops/jobs/{key}/run` | OpsMaintenanceController:42 | Operations.Manage |  |  | oui | A-031 |
| GET | `/api/v1/ops/maintenance` | OpsMaintenanceController:118 | Operations.View |  |  | oui |  |
| POST | `/api/v1/ops/maintenance` | OpsMaintenanceController:122 | Operations.Manage |  |  | oui | A-031 (maintenance globale) |
| POST | `/api/v1/ops/maintenance/{id:guid}/transition` | OpsMaintenanceController:127 | Operations.Manage |  |  | oui | A-031 (gel global des écritures) |
| GET | `/api/v1/ops/production` | OperationsController:30 | Operations.View |  |  | oui |  |
| POST | `/api/v1/ops/purge/execute` | OpsMaintenanceController:149 | Operations.Manage |  | ✔ | oui | A-031 |
| GET | `/api/v1/ops/purge/history` | OpsMaintenanceController:154 | Operations.View |  |  | oui |  |
| POST | `/api/v1/ops/purge/preview` | OpsMaintenanceController:143 | Operations.Manage |  |  | oui |  |
| GET | `/api/v1/ops/queues` | OpsMaintenanceController:56 | Operations.View |  |  | oui |  |
| POST | `/api/v1/ops/queues/{id:guid}/retry` | OpsMaintenanceController:60 | Operations.Manage |  |  | — |  |
| GET | `/api/v1/ops/retention-policies` | OpsMaintenanceController:134 | Operations.View |  |  | oui |  |
| POST | `/api/v1/ops/retention-policies` | OpsMaintenanceController:138 | Operations.Manage |  |  | oui |  |
| GET | `/api/v1/ops/status` | OperationsController:63 | Operations.View |  |  | — |  |
| GET | `/api/v1/ops/storage` | OpsMaintenanceController:87 | Operations.View |  |  | oui |  |
| GET | `/api/v1/orders` | OrdersController:23 | Pos.ManageOrder |  |  | oui |  |
| POST | `/api/v1/orders` | OrdersController:28 | Pos.ManageOrder |  |  | oui |  |
| GET | `/api/v1/orders/payment-options` | OrdersController:19 | Pos.ManageOrder |  |  | oui |  |
| GET | `/api/v1/orders/{id:guid}` | OrdersController:33 | Pos.ManageOrder |  |  | oui |  |
| PUT | `/api/v1/orders/{id:guid}` | OrdersController:41 | Pos.ManageOrder |  |  | oui |  |
| POST | `/api/v1/orders/{id:guid}/cancel` | OrdersController:68 | Pos.ManageOrder |  |  | oui |  |
| POST | `/api/v1/orders/{id:guid}/complete` | OrdersController:63 | Pos.ManageOrder | ✔ |  | oui |  |
| POST | `/api/v1/orders/{id:guid}/confirm` | OrdersController:46 | Pos.ManageOrder |  |  | oui |  |
| POST | `/api/v1/orders/{id:guid}/deposit` | OrdersController:57 | Pos.ManageOrder | ✔ |  | oui |  |
| POST | `/api/v1/orders/{id:guid}/reserve` | OrdersController:51 | Pos.ManageOrder |  |  | oui |  |
| GET | `/api/v1/payables` | PurchasingController:129 | Purchasing.ViewPayables |  |  | oui |  |
| GET | `/api/v1/payables/payment-accounts` | PurchasingController:137 | Purchasing.Pay |  |  | oui |  |
| POST | `/api/v1/payables/{id:guid}/payments` | PurchasingController:144 | Purchasing.Pay | ✔ | ✔ | oui |  |
| GET | `/api/v1/payments/{id:guid}` | CustomerAccountsController:96 | Credit.ViewPortfolio |  |  | — |  |
| POST | `/api/v1/payments/{id:guid}/void` | CustomerAccountsController:104 | Credit.VoidPayment | ✔ | ✔ | — |  |
| GET | `/api/v1/payroll/adjustments` | PayrollController:202 | Payroll.ViewSalaries |  |  | oui |  |
| POST | `/api/v1/payroll/adjustments` | PayrollController:207 | Payroll.Adjust |  |  | oui |  |
| POST | `/api/v1/payroll/adjustments/{id:guid}/approve` | PayrollController:212 | Payroll.ApproveAdjustment |  |  | — |  |
| POST | `/api/v1/payroll/adjustments/{id:guid}/cancel` | PayrollController:222 | Payroll.Adjust |  |  | — |  |
| POST | `/api/v1/payroll/adjustments/{id:guid}/reject` | PayrollController:217 | Payroll.ApproveAdjustment |  |  | — |  |
| GET | `/api/v1/payroll/components` | PayrollController:153 | Hr.ViewEmployees |  |  | oui |  |
| POST | `/api/v1/payroll/components` | PayrollController:157 | Payroll.Configure |  |  | oui |  |
| PUT | `/api/v1/payroll/components/{id:guid}` | PayrollController:162 | Payroll.Configure |  |  | — |  |
| GET | `/api/v1/payroll/items/{id:guid}` | PayrollController:50 | Payroll.ViewSalaries |  |  | — |  |
| POST | `/api/v1/payroll/items/{id:guid}/exclude` | PayrollController:54 | Payroll.Calculate |  |  | — |  |
| POST | `/api/v1/payroll/items/{id:guid}/include` | PayrollController:59 | Payroll.Calculate |  |  | — |  |
| GET | `/api/v1/payroll/payment-accounts` | PayrollController:101 | Payroll.Pay |  |  | oui |  |
| GET | `/api/v1/payroll/periods` | PayrollController:26 | Payroll.ViewSalaries |  |  | oui |  |
| POST | `/api/v1/payroll/periods` | PayrollController:31 | Payroll.Calculate |  |  | oui |  |
| GET | `/api/v1/payroll/periods/{id:guid}` | PayrollController:36 | Payroll.ViewSalaries |  |  | — |  |
| POST | `/api/v1/payroll/periods/{id:guid}/calculate` | PayrollController:40 | Payroll.Calculate |  |  | — |  |
| POST | `/api/v1/payroll/rule-templates/{id:guid}/adopt` | PayrollController:196 | Payroll.Configure |  |  | — |  |
| GET | `/api/v1/payroll/rules` | PayrollController:167 | Payroll.Configure |  |  | oui |  |
| POST | `/api/v1/payroll/rules` | PayrollController:172 | Payroll.Configure |  |  | oui |  |
| PUT | `/api/v1/payroll/rules/{id:guid}` | PayrollController:176 | Payroll.Configure |  |  | — |  |
| POST | `/api/v1/payroll/rules/{id:guid}/retire` | PayrollController:191 | Payroll.Configure |  |  | — |  |
| POST | `/api/v1/payroll/rules/{id:guid}/validate` | PayrollController:186 | Payroll.Configure |  |  | — |  |
| POST | `/api/v1/payroll/rules/{id:guid}/versions` | PayrollController:181 | Payroll.Configure |  |  | — |  |
| GET | `/api/v1/payroll/{id:guid}` | PayrollController:46 | Payroll.ViewSalaries |  |  | oui |  |
| POST | `/api/v1/payroll/{id:guid}/approve` | PayrollController:76 | Payroll.Approve |  | ✔ | — |  |
| POST | `/api/v1/payroll/{id:guid}/cancel` | PayrollController:88 | Payroll.Calculate |  | ✔ | — |  |
| POST | `/api/v1/payroll/{id:guid}/close` | PayrollController:94 | Payroll.Close |  | ✔ | — |  |
| POST | `/api/v1/payroll/{id:guid}/pay` | PayrollController:107 | Payroll.Pay | ✔ | ✔ | — |  |
| GET | `/api/v1/payroll/{id:guid}/payments` | PayrollController:111 | Payroll.ViewSalaries |  |  | oui |  |
| GET | `/api/v1/payroll/{id:guid}/payslips` | PayrollController:124 | Payroll.ViewSalaries |  |  | oui |  |
| POST | `/api/v1/payroll/{id:guid}/payslips` | PayrollController:128 | Payroll.Pay |  |  | oui |  |
| GET | `/api/v1/payroll/{id:guid}/reconciliation` | PayrollController:147 | Payroll.ViewSalaries |  |  | oui |  |
| POST | `/api/v1/payroll/{id:guid}/return` | PayrollController:82 | Payroll.ViewSalaries |  |  | — |  |
| POST | `/api/v1/payroll/{id:guid}/review` | PayrollController:65 | Payroll.Review |  |  | — |  |
| POST | `/api/v1/payroll/{id:guid}/validate` | PayrollController:70 | Payroll.Validate |  |  | — |  |
| GET | `/api/v1/payslips/{id:guid}/file` | PayrollController:132 | Payroll.ViewSalaries |  |  | — |  |
| GET | `/api/v1/payslips/{id:guid}/verify` | PayrollController:141 | Payroll.ViewSalaries |  |  | oui |  |
| GET | `/api/v1/periods` | PeriodsController:22 | Finance.View |  |  | oui |  |
| GET | `/api/v1/periods/{id:guid}/financial-close` | PeriodsController:31 | Finance.View |  |  | oui |  |
| POST | `/api/v1/periods/{id:guid}/financial-close` | PeriodsController:36 | Finance.ClosePeriod |  |  | oui |  |
| GET | `/api/v1/platform/addons` | PlatformController:43 | Platform.View |  |  | — |  |
| POST | `/api/v1/platform/addons` | PlatformController:47 | Platform.ManageCatalogue |  |  | — |  |
| GET | `/api/v1/platform/coupons` | PlatformController:51 | Platform.ManageCatalogue |  |  | — |  |
| POST | `/api/v1/platform/coupons` | PlatformController:55 | Platform.ManageCatalogue |  |  | — |  |
| GET | `/api/v1/platform/customers` | PlatformController:61 | Platform.View |  |  | oui |  |
| POST | `/api/v1/platform/customers` | PlatformController:69 | Platform.ManageCustomers |  |  | oui |  |
| GET | `/api/v1/platform/customers/{customerId:guid}/licenses` | LicensesController:41 | Platform.View |  |  | — |  |
| GET | `/api/v1/platform/customers/{id:guid}` | PlatformController:65 | Platform.View |  |  | — |  |
| PATCH | `/api/v1/platform/customers/{id:guid}` | PlatformController:73 | Platform.ManageCustomers |  |  | — |  |
| POST | `/api/v1/platform/customers/{id:guid}/companies` | PlatformController:77 | Platform.ManageCustomers |  |  | — |  |
| DELETE | `/api/v1/platform/customers/{id:guid}/companies/{companyId:guid}` | PlatformController:82 | Platform.ManageCustomers |  | ✔ | — |  |
| GET | `/api/v1/platform/customers/{id:guid}/invoices` | PlatformController:126 | Platform.View |  |  | oui |  |
| GET | `/api/v1/platform/customers/{id:guid}/payments` | PlatformController:130 | Platform.View |  |  | — |  |
| GET | `/api/v1/platform/customers/{id:guid}/quotas` | PlatformController:134 | Platform.View |  |  | oui |  |
| POST | `/api/v1/platform/customers/{id:guid}/subscription` | PlatformController:92 | Platform.ManageSubscriptions |  |  | oui |  |
| GET | `/api/v1/platform/customers/{id:guid}/subscriptions` | PlatformController:88 | Platform.View |  |  | oui |  |
| GET | `/api/v1/platform/customers/{id:guid}/support-access` | PlatformController:156 | Platform.SupportAccess |  |  | — |  |
| POST | `/api/v1/platform/invoices/{id:guid}/credit-note` | PlatformController:138 | Platform.ManageBilling |  |  | — |  |
| POST | `/api/v1/platform/licenses` | LicensesController:45 | Platform.ManageLicenses |  |  | — |  |
| GET | `/api/v1/platform/metrics` | PlatformController:29 | Platform.View |  |  | oui |  |
| POST | `/api/v1/platform/payments` | PlatformController:146 | Platform.RecordPayment |  |  | oui |  |
| GET | `/api/v1/platform/payments/review` | PlatformController:142 | Platform.ManageBilling |  |  | oui |  |
| POST | `/api/v1/platform/payments/{id:guid}/confirm` | PlatformController:150 | Platform.RecordPayment |  |  | oui |  |
| GET | `/api/v1/platform/plans` | PlatformController:35 | Platform.View |  |  | oui |  |
| POST | `/api/v1/platform/plans` | PlatformController:39 | Platform.ManageCatalogue |  |  | oui |  |
| POST | `/api/v1/platform/subscriptions/{id:guid}/activate` | PlatformController:96 | Platform.ManageSubscriptions |  |  | — |  |
| POST | `/api/v1/platform/subscriptions/{id:guid}/addons` | PlatformController:108 | Platform.ManageSubscriptions |  |  | — |  |
| POST | `/api/v1/platform/subscriptions/{id:guid}/cancel` | PlatformController:112 | Platform.ManageSubscriptions |  |  | — |  |
| POST | `/api/v1/platform/subscriptions/{id:guid}/downgrade` | PlatformController:104 | Platform.ManageSubscriptions |  |  | oui |  |
| POST | `/api/v1/platform/subscriptions/{id:guid}/reactivate` | PlatformController:116 | Platform.ManageSubscriptions |  |  | oui |  |
| POST | `/api/v1/platform/subscriptions/{id:guid}/suspend` | PlatformController:120 | Platform.ManageSubscriptions |  |  | oui |  |
| POST | `/api/v1/platform/subscriptions/{id:guid}/upgrade` | PlatformController:100 | Platform.ManageSubscriptions |  |  | oui |  |
| POST | `/api/v1/platform/support-access` | PlatformController:160 | Platform.SupportAccess |  |  | — |  |
| POST | `/api/v1/platform/support-access/{id:guid}/approve` | PlatformController:164 | Platform.SupportAccess |  |  | — |  |
| POST | `/api/v1/platform/support-access/{id:guid}/revoke` | PlatformController:168 | Platform.SupportAccess |  |  | — |  |
| GET | `/api/v1/platform/support/tickets` | PlatformSupportController:45 | Platform.Support |  |  | — |  |
| POST | `/api/v1/platform/support/tickets` | PlatformSupportController:54 | Platform.Support |  |  | — |  |
| GET | `/api/v1/platform/support/tickets/{id:guid}` | PlatformSupportController:50 | Platform.Support |  |  | — |  |
| PATCH | `/api/v1/platform/support/tickets/{id:guid}` | PlatformSupportController:63 | Platform.Support |  |  | — |  |
| POST | `/api/v1/platform/support/tickets/{id:guid}/reply` | PlatformSupportController:58 | Platform.Support |  |  | — |  |
| POST | `/api/v1/pos/cart/preview` | PosController:108 | Pos.Sell |  |  | oui |  |
| POST | `/api/v1/pos/checkout` | PosController:114 | Pos.Sell | ✔ |  | oui | A-003 (`SoldAt` client), A-012 (coupon) |
| GET | `/api/v1/pos/credit-override-requests` | CustomerAccountsController:74 | Pos.CreditOverride |  |  | oui |  |
| POST | `/api/v1/pos/credit-override-requests` | CustomerAccountsController:65 | Pos.SellCredit |  |  | oui |  |
| GET | `/api/v1/pos/credit-override-requests/{id:guid}` | CustomerAccountsController:70 | Pos.SellCredit |  |  | oui |  |
| POST | `/api/v1/pos/credit-override-requests/{id:guid}/approve` | CustomerAccountsController:79 | Pos.CreditOverride |  |  | — |  |
| POST | `/api/v1/pos/credit-override-requests/{id:guid}/reject` | CustomerAccountsController:84 | Pos.CreditOverride |  |  | — |  |
| POST | `/api/v1/pos/device` | PosController:33 | Pos.Sell |  |  | — |  |
| GET | `/api/v1/pos/discount-requests` | PosController:87 | Pos.View |  |  | oui |  |
| POST | `/api/v1/pos/discount-requests` | PosController:81 | Pos.Discount | ✔ |  | oui |  |
| GET | `/api/v1/pos/discount-requests/{id:guid}` | PosController:92 | Pos.View |  |  | oui |  |
| POST | `/api/v1/pos/discount-requests/{id:guid}/approve` | PosController:97 | Pos.ApproveDiscount |  |  | oui |  |
| POST | `/api/v1/pos/discount-requests/{id:guid}/reject` | PosController:102 | Pos.ApproveDiscount |  |  | oui |  |
| POST | `/api/v1/pos/manager-override` | PosController:56 | Pos.Sell |  |  | oui |  |
| GET | `/api/v1/pos/me/limits` | PosController:74 | Pos.View |  |  | oui |  |
| GET | `/api/v1/pos/offline-profile` | PosController:39 | Pos.Sell |  |  | oui |  |
| POST | `/api/v1/pos/park` | PosController:119 | Pos.Sell |  |  | — |  |
| GET | `/api/v1/pos/products/scan` | PosController:68 | Pos.Sell |  |  | oui |  |
| GET | `/api/v1/pos/products/search` | PosController:63 | Pos.Sell |  |  | oui |  |
| GET | `/api/v1/pos/registers` | PosController:197 | Pos.OpenSession |  |  | oui |  |
| GET | `/api/v1/pos/return-requests` | PosController:154 | Pos.View |  |  | oui |  |
| POST | `/api/v1/pos/return-requests` | PosController:148 | Pos.Return | ✔ |  | oui |  |
| GET | `/api/v1/pos/return-requests/{id:guid}` | PosController:159 | Pos.View |  |  | — |  |
| POST | `/api/v1/pos/return-requests/{id:guid}/approve` | PosController:164 | Pos.Refund |  |  | oui |  |
| POST | `/api/v1/pos/return-requests/{id:guid}/reject` | PosController:169 | Pos.Refund |  |  | oui |  |
| POST | `/api/v1/pos/returns` | PosController:134 | Pos.Refund | ✔ |  | — |  |
| GET | `/api/v1/pos/sales/lookup` | PosController:142 | Pos.Return |  |  | oui |  |
| POST | `/api/v1/pos/sales/{id:guid}/void` | PosController:126 | Pos.Sell |  |  | oui |  |
| POST | `/api/v1/pos/sessions/cash` | PosController:179 | Pos.CashInOut |  |  | — |  |
| POST | `/api/v1/pos/sessions/close` | PosController:186 | Pos.CloseSession |  |  | oui |  |
| GET | `/api/v1/pos/sessions/current` | PosController:192 | Pos.View |  |  | oui |  |
| GET | `/api/v1/pos/sessions/open` | PosController:203 | Pos.ManageSessions |  |  | oui |  |
| POST | `/api/v1/pos/sessions/open` | PosController:174 | Pos.OpenSession |  |  | oui |  |
| GET | `/api/v1/pos/sessions/pending-closings` | PosController:207 | Pos.ManageSessions |  |  | oui |  |
| POST | `/api/v1/pos/sessions/{id:guid}/validate-close` | PosController:211 | Pos.ManageSessions |  |  | oui |  |
| GET | `/api/v1/pos/tender-options` | PosController:45 | Pos.Sell |  |  | oui |  |
| GET | `/api/v1/print/customers/{customerId:guid}/statement` | PrintController:30 | authentifié |  |  | — |  |
| GET | `/api/v1/print/suppliers/{supplierId:guid}/documents` | PrintController:43 | authentifié |  |  | oui |  |
| GET | `/api/v1/print/suppliers/{supplierId:guid}/statement` | PrintController:37 | authentifié |  |  | — |  |
| GET | `/api/v1/print/{kind}/{id:guid}` | PrintController:25 | authentifié |  |  | — |  |
| GET | `/api/v1/purchasing/invoices` | PurchasingController:64 | Purchasing.ViewPayables |  |  | oui |  |
| POST | `/api/v1/purchasing/invoices` | PurchasingController:106 | Purchasing.Invoice | ✔ |  | oui |  |
| GET | `/api/v1/purchasing/invoices/{id:guid}` | PurchasingController:69 | Purchasing.ViewPayables |  |  | oui |  |
| POST | `/api/v1/purchasing/invoices/{id:guid}/match` | PurchasingController:111 | Purchasing.Match |  |  | oui |  |
| GET | `/api/v1/purchasing/orders` | PurchasingController:23 | Purchasing.View |  |  | oui |  |
| POST | `/api/v1/purchasing/orders` | PurchasingController:28 | Purchasing.Order |  |  | oui |  |
| GET | `/api/v1/purchasing/orders/{id:guid}` | PurchasingController:33 | Purchasing.View |  |  | oui |  |
| PUT | `/api/v1/purchasing/orders/{id:guid}` | PurchasingController:43 | Purchasing.Order |  |  | oui |  |
| POST | `/api/v1/purchasing/orders/{id:guid}/approve` | PurchasingController:84 | Purchasing.ApproveOrder |  |  | oui |  |
| POST | `/api/v1/purchasing/orders/{id:guid}/cancel` | PurchasingController:94 | Purchasing.Order |  |  | oui |  |
| POST | `/api/v1/purchasing/orders/{id:guid}/close` | PurchasingController:49 | Purchasing.Order |  |  | oui |  |
| POST | `/api/v1/purchasing/orders/{id:guid}/send` | PurchasingController:89 | Purchasing.Order |  |  | oui |  |
| POST | `/api/v1/purchasing/orders/{id:guid}/submit` | PurchasingController:79 | Purchasing.Order |  |  | oui |  |
| GET | `/api/v1/purchasing/receipts` | PurchasingController:54 | Purchasing.View |  |  | oui |  |
| POST | `/api/v1/purchasing/receipts` | PurchasingController:100 | Purchasing.Receive | ✔ |  | oui |  |
| GET | `/api/v1/purchasing/receipts/{id:guid}` | PurchasingController:59 | Purchasing.View |  |  | oui |  |
| GET | `/api/v1/purchasing/returns` | PurchasingController:74 | Purchasing.View |  |  | oui |  |
| POST | `/api/v1/purchasing/returns` | PurchasingController:117 | Purchasing.Return | ✔ |  | oui |  |
| GET | `/api/v1/quotas` | BillingController:57 | Platform.Portal |  |  | — |  |
| GET | `/api/v1/quotes` | QuotesController:17 | Documents.View |  |  | oui |  |
| POST | `/api/v1/quotes` | QuotesController:27 | Documents.CreateQuote |  |  | oui |  |
| GET | `/api/v1/quotes/{id:guid}` | QuotesController:22 | Documents.View |  |  | oui |  |
| PUT | `/api/v1/quotes/{id:guid}` | QuotesController:32 | Documents.CreateQuote |  |  | oui |  |
| POST | `/api/v1/quotes/{id:guid}/accept` | QuotesController:47 | Documents.CreateQuote |  |  | oui |  |
| POST | `/api/v1/quotes/{id:guid}/cancel` | QuotesController:37 | Documents.CreateQuote |  |  | oui |  |
| POST | `/api/v1/quotes/{id:guid}/convert` | QuotesController:57 | Pos.ManageOrder |  |  | oui |  |
| POST | `/api/v1/quotes/{id:guid}/reject` | QuotesController:52 | Documents.CreateQuote |  |  | oui |  |
| POST | `/api/v1/quotes/{id:guid}/send` | QuotesController:42 | Documents.CreateQuote |  |  | oui |  |
| POST | `/api/v1/receipts` | DocumentsController:56 | Documents.Issue | ✔ |  | — |  |
| GET | `/api/v1/releases` | OperationsController:85 | Operations.View |  |  | — |  |
| POST | `/api/v1/releases` | OperationsController:90 | Operations.Manage |  |  | — |  |
| GET | `/api/v1/reporting/data-quality` | AnalyticsController:36 | Reporting.DataQuality |  |  | — |  |
| GET | `/api/v1/reporting/exports` | ReportsController:89 | Reporting.Export |  |  | — |  |
| GET | `/api/v1/reports` | BiController:68 | Reporting.View |  |  | oui |  |
| POST | `/api/v1/reports` | BiController:73 | Reporting.View |  |  | oui |  |
| GET | `/api/v1/reports/aging/payables` | ReportsController:75 | Reporting.ViewFinancial |  |  | — |  |
| GET | `/api/v1/reports/aging/receivables` | ReportsController:70 | Reporting.ViewFinancial |  |  | — |  |
| GET | `/api/v1/reports/budget-vs-actual` | ReportsController:82 | Reporting.ViewFinancial |  |  | oui |  |
| GET | `/api/v1/reports/executions` | BiController:110 | Reporting.Schedule |  |  | oui |  |
| POST | `/api/v1/reports/export` | ReportsController:93 | Reporting.Export |  |  | oui |  |
| GET | `/api/v1/reports/sales/breakdown/{dimension}` | ReportsController:40 | Reporting.View, Reporting.ViewSales |  |  | oui |  |
| GET | `/api/v1/reports/sales/journal` | ReportsController:34 | Reporting.View, Reporting.ViewSales |  |  | — |  |
| GET | `/api/v1/reports/sales/top-products` | ReportsController:46 | Reporting.View, Reporting.ViewSales |  |  | — |  |
| GET | `/api/v1/reports/schedules` | BiController:97 | Reporting.Schedule |  |  | oui |  |
| POST | `/api/v1/reports/schedules` | BiController:101 | Reporting.Schedule |  |  | oui |  |
| DELETE | `/api/v1/reports/schedules/{id:guid}` | BiController:106 | Reporting.Schedule |  |  | oui |  |
| GET | `/api/v1/reports/stock/dormant` | ReportsController:63 | Reporting.View |  |  | — |  |
| GET | `/api/v1/reports/stock/ruptures` | ReportsController:58 | Reporting.View |  |  | — |  |
| GET | `/api/v1/reports/stock/valuation` | ReportsController:53 | Reporting.ViewFinancial |  |  | — |  |
| DELETE | `/api/v1/reports/{id:guid}` | BiController:87 | Reporting.View |  |  | oui |  |
| GET | `/api/v1/reports/{id:guid}` | BiController:78 | Reporting.View |  |  | oui |  |
| POST | `/api/v1/reports/{id:guid}/run` | BiController:91 | Reporting.View |  |  | oui |  |
| GET | `/api/v1/resilience/backups` | ContinuityController:147 | Continuity.View |  |  | — |  |
| POST | `/api/v1/resilience/backups/{id:guid}/restore-test` | ContinuityController:155 | Continuity.Manage |  |  | — |  |
| POST | `/api/v1/resilience/backups/{id:guid}/verify` | ContinuityController:151 | Continuity.Manage |  |  | — |  |
| GET | `/api/v1/resilience/dr-tests` | ContinuityController:114 | Continuity.View |  |  | oui |  |
| POST | `/api/v1/resilience/dr-tests` | ContinuityController:119 | Continuity.Manage |  |  | oui |  |
| POST | `/api/v1/resilience/dr-tests/{id:guid}/record` | ContinuityController:124 | Continuity.Manage |  |  | oui |  |
| GET | `/api/v1/resilience/incidents` | ContinuityController:160 | Continuity.View |  |  | — |  |
| POST | `/api/v1/resilience/incidents` | ContinuityController:165 | Continuity.Manage |  |  | — |  |
| POST | `/api/v1/resilience/incidents/{id:guid}/lockdown` | ContinuityController:108 | Continuity.Restore |  |  | — |  |
| GET | `/api/v1/resilience/runbooks` | ContinuityController:131 | Continuity.View |  |  | oui |  |
| GET | `/api/v1/resilience/status` | ContinuityController:143 | Continuity.View |  |  | — |  |
| GET | `/api/v1/resilience/status-page` | ContinuityController:137 | Continuity.View |  |  | oui |  |
| GET | `/api/v1/restaurant/areas` | RestaurantController:43 | Restaurant.Serve |  |  | oui |  |
| POST | `/api/v1/restaurant/areas` | RestaurantController:23 | Restaurant.Manage |  |  | oui |  |
| GET | `/api/v1/restaurant/kitchen` | RestaurantController:75 | Restaurant.Kitchen |  |  | oui |  |
| POST | `/api/v1/restaurant/kitchen/advance` | RestaurantController:80 | Restaurant.Kitchen |  |  | oui |  |
| POST | `/api/v1/restaurant/orders` | RestaurantController:65 | Restaurant.Serve |  |  | oui |  |
| POST | `/api/v1/restaurant/orders/cancel` | RestaurantController:52 | Restaurant.Serve |  |  | oui |  |
| POST | `/api/v1/restaurant/orders/close` | RestaurantController:86 | Restaurant.Serve | ✔ |  | oui |  |
| POST | `/api/v1/restaurant/orders/items` | RestaurantController:47 | Restaurant.Serve |  |  | oui |  |
| GET | `/api/v1/restaurant/orders/{saleId:guid}` | RestaurantController:70 | Restaurant.Serve |  |  | oui |  |
| GET | `/api/v1/restaurant/recipes` | RestaurantController:57 | Recipes.Manage |  |  | oui |  |
| POST | `/api/v1/restaurant/recipes` | RestaurantController:93 | Recipes.Manage |  |  | oui |  |
| GET | `/api/v1/restaurant/recipes/options` | RestaurantController:61 | Recipes.Manage |  |  | oui |  |
| GET | `/api/v1/restaurant/recipes/{productId:guid}` | RestaurantController:98 | Recipes.Manage |  |  | — |  |
| GET | `/api/v1/restaurant/setup` | RestaurantController:39 | Restaurant.Serve |  |  | oui |  |
| GET | `/api/v1/restaurant/tables` | RestaurantController:33 | Restaurant.Serve |  |  | oui |  |
| POST | `/api/v1/restaurant/tables` | RestaurantController:28 | Restaurant.Manage |  |  | oui |  |
| GET | `/api/v1/restores` | ContinuityController:55 | Continuity.View |  |  | oui |  |
| POST | `/api/v1/restores` | ContinuityController:59 | Continuity.Restore |  |  | oui |  |
| POST | `/api/v1/restores/{id:guid}/approve` | ContinuityController:65 | Continuity.Restore |  | ✔ | oui |  |
| GET | `/api/v1/salary-advances` | SalaryAdvancesController:21 | Hr.ViewEmployees |  |  | — |  |
| GET | `/api/v1/salary-advances/{id:guid}` | SalaryAdvancesController:26 | Hr.ViewEmployees |  |  | oui |  |
| POST | `/api/v1/salary-advances/{id:guid}/approve` | SalaryAdvancesController:34 | Payroll.AdvanceApprove |  |  | — |  |
| POST | `/api/v1/salary-advances/{id:guid}/cancel` | SalaryAdvancesController:44 | Hr.ViewEmployees |  |  | — |  |
| GET | `/api/v1/salary-advances/{id:guid}/history` | SalaryAdvancesController:30 | Hr.ViewEmployees |  |  | — |  |
| POST | `/api/v1/salary-advances/{id:guid}/pay` | SalaryAdvancesController:50 | Payroll.AdvancePay | ✔ |  | — |  |
| POST | `/api/v1/salary-advances/{id:guid}/reject` | SalaryAdvancesController:39 | Payroll.AdvanceApprove |  |  | — |  |
| POST | `/api/v1/salary-advances/{id:guid}/repay` | SalaryAdvancesController:57 | Payroll.AdvancePay | ✔ |  | — |  |
| PUT | `/api/v1/salary-advances/{id:guid}/schedule` | SalaryAdvancesController:62 | Payroll.AdvanceSchedule |  |  | — |  |
| POST | `/api/v1/salary-payments/{id:guid}/cancel` | PayrollController:117 | Payroll.Pay | ✔ | ✔ | — |  |
| POST | `/api/v1/sales` | SalesController:24 | Pos.Sell | ✔ |  | — | A-003 |
| GET | `/api/v1/sales/{id:guid}` | SalesController:29 | Pos.View |  |  | oui |  |
| POST | `/api/v1/sales/{id:guid}/cancel` | SalesController:61 | Pos.VoidSale |  |  | — |  |
| POST | `/api/v1/sales/{id:guid}/payments` | SalesController:38 | Pos.Sell | ✔ |  | — | A-014 |
| POST | `/api/v1/sales/{id:guid}/returns` | SalesController:56 | Pos.Refund | ✔ |  | — |  |
| GET | `/api/v1/saved-filters` | UxPersonalizationController:43 | authentifié |  |  | oui |  |
| POST | `/api/v1/saved-filters` | UxPersonalizationController:47 | authentifié |  |  | oui |  |
| DELETE | `/api/v1/saved-filters/{id:guid}` | UxPersonalizationController:51 | authentifié |  |  | oui |  |
| GET | `/api/v1/search` | ExperienceController:19 | authentifié |  |  | oui |  |
| GET | `/api/v1/sector-profiles` | SectorProfilesController:21 | Sectors.Configure |  |  | oui |  |
| GET | `/api/v1/sector-profiles/{key}` | SectorProfilesController:26 | Sectors.Configure |  |  | — |  |
| POST | `/api/v1/sector-profiles/{key}/apply` | SectorProfilesController:36 | Config.ApplyProfile |  |  | oui |  |
| POST | `/api/v1/sector-profiles/{key}/preview` | SectorProfilesController:31 | Sectors.Configure |  |  | oui |  |
| PUT | `/api/v1/sectors/activities` | SectorsController:28 | Sectors.Configure |  |  | oui |  |
| GET | `/api/v1/sectors/config` | SectorsController:23 | Sectors.Configure |  |  | oui |  |
| POST | `/api/v1/sectors/custom-fields` | SectorsController:45 | Sectors.CustomFields |  |  | oui |  |
| PUT | `/api/v1/sectors/custom-fields/values` | SectorsController:55 | Sectors.Configure |  |  | oui |  |
| GET | `/api/v1/sectors/custom-fields/values/{entityType}/{entityId:guid}` | SectorsController:60 | Sectors.Configure |  |  | — |  |
| GET | `/api/v1/sectors/custom-fields/{entityType}` | SectorsController:50 | Sectors.Configure |  |  | oui |  |
| PUT | `/api/v1/sectors/modules` | SectorsController:33 | Sectors.Configure |  |  | oui |  |
| PUT | `/api/v1/sectors/terminology` | SectorsController:38 | Sectors.Terminology |  |  | — |  |
| GET | `/api/v1/security/approvals` | SecurityController:74 | Security.Approvals |  |  | oui |  |
| POST | `/api/v1/security/approvals` | SecurityController:69 | Security.Approvals |  |  | oui |  |
| GET | `/api/v1/security/approvals/{requestId:guid}` | SecurityController:79 | Security.Approvals |  |  | — |  |
| POST | `/api/v1/security/approvals/{requestId:guid}/decide` | SecurityController:84 | Security.Approvals |  |  | oui |  |
| GET | `/api/v1/security/audit` | SecurityController:36 | Security.AuditView |  |  | oui |  |
| POST | `/api/v1/security/audit/export` | SecurityController:41 | Security.AuditView |  |  | — |  |
| GET | `/api/v1/security/certificates` | SecurityController:170 | Platform.Security |  |  | oui |  |
| POST | `/api/v1/security/certificates` | SecurityController:174 | Platform.Security |  |  | oui |  |
| POST | `/api/v1/security/certificates/{id:guid}/rotate` | SecurityController:179 | Platform.Security |  |  | oui |  |
| GET | `/api/v1/security/compliance` | SecurityController:186 | Platform.Security |  |  | oui |  |
| POST | `/api/v1/security/compliance/{id:guid}/review` | SecurityController:191 | Platform.Security |  |  | oui |  |
| GET | `/api/v1/security/dashboard` | SecurityController:226 | Security.Configure |  |  | oui |  |
| GET | `/api/v1/security/devices` | SecurityController:127 | authentifié |  |  | — |  |
| POST | `/api/v1/security/devices/{sessionId:guid}/revoke` | SecurityController:130 | authentifié |  |  | — |  |
| POST | `/api/v1/security/devices/{sessionId:guid}/trust` | SecurityController:134 | authentifié |  |  | oui |  |
| GET | `/api/v1/security/emergency-access` | SecurityController:198 | Security.RequestEmergencyAccess |  |  | oui |  |
| POST | `/api/v1/security/emergency-access` | SecurityController:204 | Security.RequestEmergencyAccess |  | ✔ | oui |  |
| POST | `/api/v1/security/emergency-access/{id:guid}/decide` | SecurityController:210 | Security.ApproveEmergencyAccess |  | ✔ | oui |  |
| POST | `/api/v1/security/emergency-access/{id:guid}/revoke` | SecurityController:215 | Security.ApproveEmergencyAccess |  |  | oui |  |
| POST | `/api/v1/security/fraud/scan` | SecurityController:52 | Security.FraudReview |  |  | oui |  |
| GET | `/api/v1/security/fraud/signals` | SecurityController:57 | Security.FraudReview |  |  | oui |  |
| POST | `/api/v1/security/fraud/signals/{signalId:guid}/review` | SecurityController:62 | Security.FraudReview |  |  | oui |  |
| GET | `/api/v1/security/incidents` | ContinuityController:80 | Continuity.View |  |  | — |  |
| POST | `/api/v1/security/incidents` | ContinuityController:86 | Continuity.Manage |  |  | — |  |
| GET | `/api/v1/security/mfa` | AuthMfaController:19 | authentifié |  |  | — |  |
| POST | `/api/v1/security/mfa/disable` | AuthMfaController:34 | authentifié |  |  | — |  |
| POST | `/api/v1/security/mfa/enroll` | AuthMfaController:24 | authentifié |  |  | — |  |
| GET | `/api/v1/security/policy` | SecurityController:141 | Security.Configure |  |  | oui |  |
| PUT | `/api/v1/security/policy` | SecurityController:146 | Security.Configure |  |  | oui |  |
| GET | `/api/v1/security/score` | SecurityController:222 | Security.Configure |  |  | oui |  |
| GET | `/api/v1/security/sessions` | SecurityController:113 | authentifié |  |  | oui |  |
| POST | `/api/v1/security/sessions/revoke-all` | SecurityController:121 | authentifié |  |  | oui |  |
| POST | `/api/v1/security/sessions/{sessionId:guid}/revoke` | SecurityController:117 | authentifié |  |  | oui |  |
| POST | `/api/v1/security/step-up` | SecurityController:93 | authentifié |  |  | oui |  |
| POST | `/api/v1/security/step-up/verify` | SecurityController:107 | authentifié |  |  | — |  |
| GET | `/api/v1/security/vulnerabilities` | SecurityController:153 | Platform.Security |  |  | oui |  |
| POST | `/api/v1/security/vulnerabilities` | SecurityController:158 | Platform.Security |  |  | oui |  |
| POST | `/api/v1/security/vulnerabilities/{id:guid}/status` | SecurityController:163 | Platform.Security |  |  | oui |  |
| POST | `/api/v1/setup/database/test` | SetupController:56 | anonyme |  |  | oui |  |
| POST | `/api/v1/setup/install` | SetupController:66 | anonyme |  |  | oui |  |
| GET | `/api/v1/setup/options` | SetupController:37 | anonyme |  |  | oui |  |
| GET | `/api/v1/setup/status` | SetupController:29 | anonyme |  |  | oui |  |
| POST | `/api/v1/setup/verify-token` | SetupController:46 | anonyme |  |  | oui |  |
| GET | `/api/v1/shared/{token}` | GedController:150 | anonyme |  |  | — |  |
| POST | `/api/v1/shared/{token}` | GedController:157 | anonyme |  |  | — |  |
| GET | `/api/v1/shortcuts` | UxPersonalizationController:56 | authentifié |  |  | oui |  |
| POST | `/api/v1/shortcuts` | UxPersonalizationController:59 | authentifié |  |  | oui |  |
| DELETE | `/api/v1/shortcuts/{id:guid}` | UxPersonalizationController:63 | authentifié |  |  | oui |  |
| GET | `/api/v1/signature-candidates` | GedController:233 | Ged.Sign |  |  | oui |  |
| GET | `/api/v1/signature-requests` | GedController:226 | Ged.View |  |  | oui |  |
| GET | `/api/v1/signature-requests/{id:guid}` | GedController:237 | Ged.View |  |  | — |  |
| POST | `/api/v1/signature-requests/{id:guid}/cancel` | GedController:251 | Ged.Sign |  |  | oui |  |
| POST | `/api/v1/signature-requests/{id:guid}/signers/{signerId:guid}/decline` | GedController:246 | Ged.Sign |  |  | oui |  |
| POST | `/api/v1/signature-requests/{id:guid}/signers/{signerId:guid}/sign` | GedController:241 | Ged.Sign |  |  | oui |  |
| GET | `/api/v1/stock/alerts` | StockController:56 | Inventory.View |  |  | oui |  |
| GET | `/api/v1/stock/balances` | StockController:30 | Inventory.View |  |  | — |  |
| POST | `/api/v1/stock/expire-lots` | StockController:76 | Inventory.WriteOff | ✔ |  | oui |  |
| GET | `/api/v1/stock/lots` | StockController:44 | Inventory.View |  |  | oui |  |
| GET | `/api/v1/stock/lots/expiring` | StockController:51 | Inventory.View |  |  | oui |  |
| POST | `/api/v1/stock/lots/{id:guid}/block` | StockController:60 | Inventory.ManageLot |  |  | oui |  |
| POST | `/api/v1/stock/lots/{id:guid}/recall` | StockController:70 | Inventory.ManageLot |  |  | oui |  |
| POST | `/api/v1/stock/lots/{id:guid}/unblock` | StockController:65 | Inventory.ManageLot |  |  | oui |  |
| GET | `/api/v1/stock/movements` | StockController:36 | Inventory.View |  |  | oui |  |
| GET | `/api/v1/stock/overview` | StockController:23 | Inventory.View |  |  | oui |  |
| GET | `/api/v1/subscription` | SubscriptionsController:27 | Platform.Portal |  |  | oui |  |
| GET | `/api/v1/subscriptions` | SubscriptionsController:36 | Platform.Portal |  |  | — |  |
| POST | `/api/v1/subscriptions/{id:guid}/addons` | SubscriptionsController:51 | Platform.Portal |  |  | — |  |
| POST | `/api/v1/subscriptions/{id:guid}/cancel` | SubscriptionsController:55 | Platform.Portal |  |  | oui |  |
| POST | `/api/v1/subscriptions/{id:guid}/downgrade` | SubscriptionsController:47 | Platform.Portal |  |  | oui |  |
| GET | `/api/v1/subscriptions/{id:guid}/preview-plan/{planId:guid}` | SubscriptionsController:39 | Platform.Portal |  |  | oui |  |
| POST | `/api/v1/subscriptions/{id:guid}/reactivate` | SubscriptionsController:59 | Platform.Portal |  |  | oui |  |
| POST | `/api/v1/subscriptions/{id:guid}/upgrade` | SubscriptionsController:43 | Platform.Portal |  |  | oui |  |
| GET | `/api/v1/suppliers` | PurchasingController:178 | Purchasing.View |  |  | oui |  |
| POST | `/api/v1/suppliers` | PurchasingController:187 | Suppliers.Create |  |  | oui |  |
| GET | `/api/v1/suppliers/payment-terms` | PurchasingController:169 | Suppliers.View |  |  | oui |  |
| POST | `/api/v1/suppliers/payment-terms` | PurchasingController:173 | Suppliers.Update |  |  | oui |  |
| GET | `/api/v1/suppliers/{id:guid}` | PurchasingController:182 | Suppliers.View |  |  | oui |  |
| PUT | `/api/v1/suppliers/{id:guid}` | PurchasingController:192 | Suppliers.Update |  |  | oui |  |
| GET | `/api/v1/suppliers/{id:guid}/overview` | PurchasingController:158 | Suppliers.View |  |  | oui |  |
| GET | `/api/v1/suppliers/{id:guid}/statement` | PurchasingController:197 | Purchasing.ViewPayables |  |  | oui |  |
| PUT | `/api/v1/suppliers/{id:guid}/terms` | PurchasingController:164 | Suppliers.Update |  |  | oui |  |
| GET | `/api/v1/support/tickets` | PlatformSupportController:20 | Platform.Portal |  |  | — |  |
| POST | `/api/v1/support/tickets` | PlatformSupportController:30 | Platform.Portal |  |  | — |  |
| GET | `/api/v1/support/tickets/{id:guid}` | PlatformSupportController:25 | Platform.Portal |  |  | — |  |
| POST | `/api/v1/support/tickets/{id:guid}/reply` | PlatformSupportController:38 | Platform.Portal |  |  | — |  |
| POST | `/api/v1/sync/batch` | SyncController:26 | Sync.View |  |  | oui |  |
| GET | `/api/v1/sync/center` | SyncController:33 | Sync.View |  |  | oui |  |
| POST | `/api/v1/sync/conflicts/{conflictId:guid}/resolve` | SyncController:38 | Sync.Manage |  |  | oui |  |
| GET | `/api/v1/sync/devices` | SyncController:46 | Sync.View |  |  | — |  |
| POST | `/api/v1/sync/devices` | SyncController:52 | Sync.Devices |  |  | — |  |
| POST | `/api/v1/sync/devices/{deviceId:guid}/heartbeat` | SyncController:63 | Sync.View |  |  | — |  |
| POST | `/api/v1/sync/devices/{deviceId:guid}/push-token` | SyncController:69 | Sync.Devices |  |  | — |  |
| POST | `/api/v1/sync/devices/{deviceId:guid}/revoke` | SyncController:58 | Sync.Devices |  |  | oui |  |
| GET | `/api/v1/sync/offline-policy` | SyncController:76 | Sync.View |  |  | oui |  |
| PUT | `/api/v1/sync/offline-policy` | SyncController:81 | Offline.Configure |  |  | oui |  |
| GET | `/api/v1/system/status` | OperationsController:63 | Operations.View |  |  | oui |  |
| POST | `/api/v1/treasury/charges` | FinancialAccountsController:67 | Finance.Reconcile | ✔ |  | oui |  |
| GET | `/api/v1/treasury/liquidity` | TreasuryController:27 | Finance.View |  |  | oui |  |
| GET | `/api/v1/treasury/position` | TreasuryController:22 | Finance.View |  |  | oui |  |
| GET | `/api/v1/treasury/suspense` | TreasuryController:34 | Finance.Reconcile |  |  | oui |  |
| POST | `/api/v1/treasury/suspense` | TreasuryController:39 | Finance.ManageSuspense |  |  | oui |  |
| POST | `/api/v1/treasury/suspense/{id:guid}/resolve` | TreasuryController:44 | Finance.ManageSuspense |  |  | oui |  |
| POST | `/api/v1/treasury/transfers` | FinancialAccountsController:61 | Finance.Transfer | ✔ |  | oui | A-018 |
| GET | `/api/v1/trial-balance` | FinancialStatementsController:29 | Accounting.View |  |  | oui |  |
| GET | `/api/v1/ui-preferences` | PreferencesController:25 | authentifié |  |  | — |  |
| PUT | `/api/v1/ui-preferences` | PreferencesController:30 | authentifié |  |  | — |  |
| GET | `/api/v1/usage` | BillingController:53 | Platform.Portal |  |  | oui |  |
| GET | `/api/v1/user-preferences` | PreferencesController:25 | authentifié |  |  | oui |  |
| PUT | `/api/v1/user-preferences` | PreferencesController:30 | authentifié |  |  | oui |  |
| GET | `/api/v1/users` | UsersController:22 | Admin.ManageUsers |  |  | oui |  |
| POST | `/api/v1/users` | UsersController:29 | Admin.ManageUsers |  | ✔ | oui |  |
| POST | `/api/v1/users/assignments/{assignmentId:guid}/revoke` | UsersController:46 | Admin.ManageUsers |  | ✔ | oui |  |
| GET | `/api/v1/users/{id:guid}/assignments` | UsersController:34 | Admin.ManageUsers |  |  | oui |  |
| POST | `/api/v1/users/{id:guid}/assignments` | UsersController:40 | Admin.ManageUsers |  | ✔ | oui |  |
| POST | `/api/v1/users/{id:guid}/mfa/reset` | UsersController:57 | Security.ResetMfa |  |  | oui |  |
| POST | `/api/v1/users/{id:guid}/status` | UsersController:51 | Admin.ManageUsers |  |  | oui |  |
| GET | `/api/v1/warehouses` | WarehouseController:23 | Inventory.View |  |  | oui |  |
| POST | `/api/v1/warehouses` | WarehouseController:27 | Inventory.ManageWarehouse |  |  | oui |  |
| GET | `/api/v1/warehouses/locations` | WarehouseController:32 | Inventory.View |  |  | oui |  |
| POST | `/api/v1/warehouses/locations` | WarehouseController:37 | Inventory.ManageWarehouse |  |  | oui |  |
| GET | `/api/v1/warehouses/sites` | WarehouseController:19 | Inventory.View |  |  | oui |  |
| GET | `/api/v1/webhooks` | MarketplaceController:107 | Integrations.Webhooks |  |  | — |  |
| POST | `/api/v1/webhooks` | MarketplaceController:111 | Integrations.Webhooks |  |  | — |  |
