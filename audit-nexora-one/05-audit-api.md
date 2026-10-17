# 05 — Audit de l'API (édition 2)

> Inventaire régénéré le 2026-10-17 sur HEAD `6a47c6d` par analyse statique des attributs des contrôleurs
> (`backend/src/Nexora.Api/Controllers`, script `endpoints2.py` du dossier temporaire de l'audit), croisé avec les appels
> du frontend (`routes2.py`). Les comportements cités « exécuté » proviennent des scénarios de `08-tests-et-resultats.md`
> (API locale, base jetable). Édition 1 (1029 points d'accès, 74 contrôleurs) : historique Git.

## 1. Chiffres

| Indicateur | Valeur | Éd. 1 |
| --- | --- | --- |
| Points d'accès (verbe × route) dans les contrôleurs | **1032** | 1029 |
| Contrôleurs | 80 (fichiers : 76) | 74 |
| Verbes | POST 505, GET 466, PUT 43, DELETE 13, PATCH 5 | — |
| Anonymes (`[AllowAnonymous]`) | 18 + 5 sondes de santé (`MapHealthChecks`, `Program.cs:204-210`) | 18 |
| Authentifiés sans permission déclarée (libre-service) | 57 | 57 |
| Avec `[RequirePermission]` | 957 | 954 |
| Écritures (POST/PUT/PATCH/DELETE) | 566 | 564 |
| … avec `[Idempotent]` | 54 | 53 |
| Confirmation forte `[RequireStepUp]` | 41 | 41 |
| Limitation de débit explicite | 15 | 14 |
| Appels du frontend analysés | 739, **tous résolus** sur une route existante | 637 / 636 |
| Points d'accès sans appel frontend détecté | 288 (≈ 250 routes distinctes, `unused_routes.txt`) | 299 routes |
| Points d'accès concernés par une anomalie ouverte | 71 | — |

Politique globale inchangée : `FallbackPolicy` authentifiée (deny-by-default), `X-Company-Id` contrôlé contre le jeton
(403 sinon, vérifié par les tests d'API), RLS en second rideau, erreurs RFC 7807 avec identifiant de corrélation.

Routes sans écran : ce ne sont pas des défauts en soi (API publique, intégrations), mais plusieurs doublons non utilisés
élargissent la surface : `/finance/*` (opérations financières à montants libres, **A-045**), `/accounting/*` (doublon de
`/accounting-entries` et `/accounting-periods`), `/intelligence/forecasts` (doublon de `/forecasts` avec une permission
plus faible, **A-048**), `/resilience/backups/*` (doublon de `/backups/*`).

## 2. Points d'accès anonymes

| Verbe | Route | Contrôleur | Limitation | Protection | Verdict éd. 2 |
| --- | --- | --- | --- | --- | --- |
| POST | `/api/v1/auth/login` | AuthController | auth | identifiants ; verrouillage ; hachage factice contre l'énumération | solide |
| POST | `/api/v1/auth/mfa/verify` | AuthController | auth | jeton MFA intermédiaire + TOTP, anti-rejeu (V102) | solide |
| POST | `/api/v1/auth/refresh` | AuthController | refresh | cookie HttpOnly, rotation, détection de vol | solide |
| POST | `/api/v1/auth/password/forgot` | AuthController | auth | réponse identique ; délai non constant (A-011 résidu) | FAIBLE |
| POST | `/api/v1/auth/password/reset` | AuthController | auth | jeton de réinitialisation | solide |
| POST | `/api/v1/auth/logout` | AuthController | refresh | cookie de rafraîchissement | solide |
| POST | `/api/v1/billing/webhooks/payment` | BillingController | — | jeton partagé ; **valeur par défaut publique acceptée** | **A-049 confirmé** |
| GET | `/api/v1/documents/verify/{code}` | DocumentsController | public | code aléatoire 80 bits | solide |
| GET | `/api/v1/shared/{token}` | GedController | public | jeton 192 bits, expiration, mot de passe BCrypt en POST | solide |
| POST | `/api/v1/shared/{token}` | GedController | public | jeton 192 bits, expiration, mot de passe BCrypt en POST | solide |
| POST | `/api/v1/integrations/webhooks/inbound/{connectorCode}` | IntegrationsController | — | signature HMAC, déduplication | solide ; pas de limite de débit (FAIBLE) |
| POST | `/api/v1/licenses/verify` | LicensesController | public | jeton HMAC | solide |
| GET | `/api/v1/metrics` | MetricsController | — | 404 sans jeton `Metrics:Token` ≥ 16 car. ni réseau interne | A-024 corrigé ; comparaison non constante (FAIBLE) |
| GET | `/api/v1/setup/status` | SetupController | — | lecture d'état | solide |
| GET | `/api/v1/setup/options` | SetupController | — | fermé après installation | solide |
| POST | `/api/v1/setup/verify-token` | SetupController | auth | `X-Setup-Token`, fermé après installation | solide |
| POST | `/api/v1/setup/database/test` | SetupController | auth | `X-Setup-Token`, fermé après installation | solide |
| POST | `/api/v1/setup/install` | SetupController | auth | `X-Setup-Token`, fermé après installation | solide |

## 3. Problèmes identifiés sur l'API (éd. 2)

| ID | Gravité | Point(s) d'accès | Problème | Preuve |
| --- | --- | --- | --- | --- |
| A-049 | CRITIQUE (SaaS) | `POST /billing/webhooks/payment` | Jeton par défaut accepté en production ; facture marquée payée quel que soit le montant | exécuté |
| A-043 | CRITIQUE | états et balances (`/accounting/reports/*`, `/financial-statements`, `/trial-balance`, rapports) | L'origine extournée sort des agrégats | exécuté |
| A-052 | CRITIQUE | `POST /pos/checkout`, `/sales`, retours | Coût des ventes en devise × taux | SELECT sur `nexora_recette` |
| A-042 | ÉLEVÉ | toutes les écritures | Aucune période après 2026-12-31, aucune route de création d'exercice | lecture + SELECT |
| A-040 | ÉLEVÉ | `POST /pos/checkout`, `/sales` | `taxId` de ligne accepté | exécuté (143 → 130) |
| A-038 | ÉLEVÉ | `POST /inventory/transfers/{id}/receive` | Réceptions concurrentes sans verrou | exécuté (1 → 7-8 unités) |
| A-003 r. | ÉLEVÉ | `POST /sync/devices`, `/sync/batch` | Appareil auto-enrôlé ; vente insérée dans une session close | exécuté |
| A-041 | ÉLEVÉ | `POST /accounting-entries/{id}/post` | Demi-centime en devise → 409 avec message PostgreSQL brut | exécuté |
| A-045 | MOYEN | `POST /finance/*` | Montants libres, aucune approbation | exécuté |
| A-048 | MOYEN | `GET /intelligence/forecasts`, `/intelligence/recommendations` | Prévisions financières pour `intelligence.view` | exécuté (403 / 200) |
| A-033 | MOYEN | `/users/*` | Contrôle sur l'entreprise active, action sur une autre | lecture |
| A-034 | MOYEN | `/security/audit*`, `/hr/audit`, `/configurations/clone` | Lecture / écriture sur toutes les entreprises du jeton | lecture |
| A-035 | MOYEN | `POST /security/emergency-access/{id}/revoke` | Statut révoqué, accès effectif conservé | lecture |
| A-046 / A-047 | MOYEN | KPI, trésorerie, budgets | Devises et versions additionnées | SELECT + lecture |
| A-050 | MOYEN | `POST /imports/{id}/execute` | Stock d'ouverture sans écriture | lecture |
| A-051 | MOYEN | `/backups/{id}/verify`, `/restore-test` | « vérifié » sur métadonnées déclarées | lecture |
| Obs. | FAIBLE | `POST /sync/batch` | Date à décalage non UTC → 500 Npgsql | exécuté |
| Obs. | FAIBLE | `POST /salary-advances/{id}/pay` | Sans confirmation forte (le paiement de salaire en a une) | lecture |
| Obs. | FAIBLE | `IdempotencyFilter` | Cache mémoire non partagé entre instances | lecture |

Corrigés depuis l'éd. 1 et revérifiés : A-003 en ligne (`SoldAt` ignoré), A-012 (coupon), A-014 (`/sales/{id}/payments`),
A-018 (mise à jour perdue, V100), A-024 (`/metrics`), A-031 (garde plateforme sur `/ops/*`), A-004 (doublon d'événement),
T-002. Contrôles revérifiés à l'exécution : jeton step-up hors session → 403 ; `X-Company-Id` étranger → 403.

## 4. Inventaire complet

Colonnes : verbe, route, contrôleur:ligne, permission(s), idempotence (I), confirmation forte (S), limitation (R), appel
frontend détecté (F), anomalie ouverte.

| Verbe | Route | Contrôleur:ligne | Permission | I | S | R | F | Anomalie |
| --- | --- | --- | --- | --- | --- | --- | --- | --- |
| GET | `/api/v1/accounting-currencies` | ChartOfAccountsController:23 | Accounting.View |  |  |  | oui |  |
| GET | `/api/v1/accounting-entries` | AccountingEntriesController:18 | Accounting.View |  |  |  | oui |  |
| POST | `/api/v1/accounting-entries` | AccountingEntriesController:29 | Accounting.Create |  |  |  | oui |  |
| DELETE | `/api/v1/accounting-entries/{id:guid}` | AccountingEntriesController:39 | Accounting.Create |  |  |  | oui |  |
| GET | `/api/v1/accounting-entries/{id:guid}` | AccountingEntriesController:24 | Accounting.View |  |  |  | oui |  |
| PUT | `/api/v1/accounting-entries/{id:guid}` | AccountingEntriesController:34 | Accounting.Create |  |  |  | oui |  |
| POST | `/api/v1/accounting-entries/{id:guid}/post` | AccountingEntriesController:44 | Accounting.Post | ✔ |  |  | oui | A-041 (demi-centime en devise → 409 brut) |
| POST | `/api/v1/accounting-entries/{id:guid}/reverse` | AccountingEntriesController:50 | Accounting.Reverse |  | AccountingValidation |  | oui | A-043 (origine passée à reversed) |
| GET | `/api/v1/accounting-periods` | AccountingPeriodsController:23 | Accounting.View |  |  |  | oui |  |
| POST | `/api/v1/accounting-periods/{id:guid}/close` | AccountingPeriodsController:32 | Accounting.ClosePeriod |  | AccountingValidation |  | oui |  |
| GET | `/api/v1/accounting-periods/{id:guid}/close-year` | AccountingPeriodsController:46 | Accounting.View |  |  |  | oui |  |
| POST | `/api/v1/accounting-periods/{id:guid}/close-year` | AccountingPeriodsController:51 | Accounting.CarryForward |  | AccountingValidation |  | oui |  |
| POST | `/api/v1/accounting-periods/{id:guid}/opening-balances` | AccountingPeriodsController:59 | Accounting.CarryForward |  |  |  | oui |  |
| POST | `/api/v1/accounting-periods/{id:guid}/reopen` | AccountingPeriodsController:38 | Accounting.ReopenPeriod |  | AccountingValidation |  | oui |  |
| POST | `/api/v1/accounting/journal-entries/{id:guid}/reverse` | AccountingController:42 | Accounting.Reverse |  | AccountingValidation |  |  | A-041 (même moteur) |
| GET | `/api/v1/accounting/periods` | AccountingController:23 | Accounting.View |  |  |  |  |  |
| POST | `/api/v1/accounting/periods/{id:guid}/close` | AccountingController:28 | Accounting.ClosePeriod |  | AccountingValidation |  |  |  |
| POST | `/api/v1/accounting/periods/{id:guid}/reopen` | AccountingController:34 | Accounting.ReopenPeriod |  | AccountingValidation |  |  |  |
| GET | `/api/v1/accounting/reports/aged-payables` | AccountingController:80 | Finance.View |  |  |  |  | A-043 (extournes exclues des agrégats) |
| GET | `/api/v1/accounting/reports/aged-receivables` | AccountingController:75 | Finance.View |  |  |  |  | A-043 (extournes exclues des agrégats) |
| GET | `/api/v1/accounting/reports/balance-sheet` | AccountingController:70 | Accounting.View |  |  |  |  | A-043 (extournes exclues des agrégats) |
| GET | `/api/v1/accounting/reports/cash-flow-forecast` | AccountingController:85 | Finance.View |  |  |  |  | A-043 (extournes exclues des agrégats) |
| GET | `/api/v1/accounting/reports/general-ledger` | AccountingController:55 | Accounting.View |  |  |  |  | A-043 (extournes exclues des agrégats) |
| GET | `/api/v1/accounting/reports/income-statement` | AccountingController:65 | Accounting.View |  |  |  |  | A-043 (extournes exclues des agrégats) |
| GET | `/api/v1/accounting/reports/trial-balance` | AccountingController:50 | Accounting.View |  |  |  |  | A-043 (extournes exclues des agrégats) |
| GET | `/api/v1/accounts` | ChartOfAccountsController:47 | Accounting.View |  |  |  | oui |  |
| POST | `/api/v1/accounts` | ChartOfAccountsController:27 | Accounting.ManageChart |  |  |  | oui |  |
| GET | `/api/v1/accounts/manage` | ChartOfAccountsController:19 | Accounting.View |  |  |  | oui |  |
| PUT | `/api/v1/accounts/{id:guid}` | ChartOfAccountsController:32 | Accounting.ManageChart |  |  |  | oui |  |
| POST | `/api/v1/accounts/{id:guid}/status` | ChartOfAccountsController:37 | Accounting.ManageChart |  |  |  | oui |  |
| GET | `/api/v1/admin/automations` | AdministrationController:122 | Admin.Automations |  |  |  | oui |  |
| POST | `/api/v1/admin/automations` | AdministrationController:126 | Admin.Automations |  |  |  | oui |  |
| POST | `/api/v1/admin/automations/run-due` | AdministrationController:141 | Admin.Automations |  |  |  |  |  |
| POST | `/api/v1/admin/automations/{id:guid}/run` | AdministrationController:136 | Admin.Automations |  |  |  | oui |  |
| GET | `/api/v1/admin/automations/{id:guid}/runs` | AdministrationController:145 | Admin.Automations |  |  |  | oui |  |
| POST | `/api/v1/admin/automations/{id:guid}/status` | AdministrationController:131 | Admin.Automations |  |  |  | oui |  |
| GET | `/api/v1/admin/config-versions` | AdministrationController:160 | Admin.ConfigVersions |  |  |  | oui |  |
| GET | `/api/v1/admin/config-versions/active-at` | AdministrationController:175 | Admin.ConfigVersions |  |  |  |  |  |
| POST | `/api/v1/admin/config-versions/snapshot` | AdministrationController:165 | Admin.ConfigVersions |  |  |  | oui |  |
| POST | `/api/v1/admin/config-versions/{id:guid}/activate` | AdministrationController:170 | Admin.ConfigVersions |  |  |  | oui |  |
| GET | `/api/v1/admin/dunning-policy` | AdministrationController:149 | Admin.Automations |  |  |  |  |  |
| PUT | `/api/v1/admin/dunning-policy` | AdministrationController:153 | Admin.Automations |  |  |  |  |  |
| GET | `/api/v1/admin/permissions` | AdministrationController:54 | Admin.Roles |  |  |  | oui |  |
| GET | `/api/v1/admin/roles` | AdministrationController:44 | Admin.Roles |  |  |  | oui |  |
| POST | `/api/v1/admin/roles` | AdministrationController:48 | Admin.Roles |  | PermissionChange |  | oui |  |
| PUT | `/api/v1/admin/roles/{id:guid}` | AdministrationController:58 | Admin.Roles |  | PermissionChange |  | oui |  |
| POST | `/api/v1/admin/roles/{id:guid}/disable` | AdministrationController:79 | Admin.Roles |  | PermissionChange |  | oui |  |
| POST | `/api/v1/admin/roles/{id:guid}/enable` | AdministrationController:64 | Admin.Roles |  | PermissionChange |  | oui |  |
| PUT | `/api/v1/admin/roles/{id:guid}/permissions` | AdministrationController:69 | Admin.Roles |  | PermissionChange |  | oui |  |
| GET | `/api/v1/admin/roles/{id:guid}/preview` | AdministrationController:75 | Admin.Roles |  |  |  |  |  |
| GET | `/api/v1/admin/settings` | AdministrationController:32 | Admin.Settings |  |  |  | oui |  |
| PUT | `/api/v1/admin/settings` | AdministrationController:37 | Admin.Settings |  |  |  | oui |  |
| GET | `/api/v1/admin/settings/definitions` | AdministrationController:27 | Admin.Settings |  |  |  | oui |  |
| GET | `/api/v1/admin/tasks` | AdministrationController:185 | Admin.Settings |  |  |  | oui |  |
| GET | `/api/v1/admin/workflows` | AdministrationController:87 | Admin.Workflows |  |  |  | oui |  |
| POST | `/api/v1/admin/workflows` | AdministrationController:95 | Admin.Workflows |  |  |  | oui |  |
| GET | `/api/v1/admin/workflows/catalog` | AdministrationController:91 | Admin.Workflows |  |  |  | oui |  |
| POST | `/api/v1/admin/workflows/evaluate` | AdministrationController:115 | Admin.Workflows |  |  |  |  |  |
| POST | `/api/v1/admin/workflows/{id:guid}/activate` | AdministrationController:105 | Admin.Workflows |  |  |  | oui |  |
| POST | `/api/v1/admin/workflows/{id:guid}/status` | AdministrationController:110 | Admin.Workflows |  |  |  | oui |  |
| POST | `/api/v1/admin/workflows/{id:guid}/test` | AdministrationController:100 | Admin.Workflows |  |  |  | oui |  |
| GET | `/api/v1/alerts` | AnalyticsController:63 | Reporting.View |  |  |  |  |  |
| GET | `/api/v1/analytics/data-quality` | AnalyticsController:33 | Reporting.DataQuality |  |  |  | oui |  |
| GET | `/api/v1/analytics/reconciliation` | AnalyticsController:26 | Reporting.DataQuality |  |  |  | oui |  |
| GET | `/api/v1/analytics/snapshots` | AnalyticsController:41 | Reporting.DataQuality |  |  |  | oui |  |
| POST | `/api/v1/analytics/snapshots` | AnalyticsController:51 | Reporting.DataQuality |  |  |  | oui |  |
| GET | `/api/v1/analytics/snapshots/{id:guid}` | AnalyticsController:46 | Reporting.DataQuality |  |  |  | oui |  |
| GET | `/api/v1/api-keys` | ApiKeysController:20 | Integrations.ManageApiKeys |  |  |  | oui |  |
| POST | `/api/v1/api-keys` | ApiKeysController:23 | Integrations.ManageApiKeys |  | PermissionChange |  | oui |  |
| POST | `/api/v1/api-keys/{id:guid}/revoke` | ApiKeysController:33 | Integrations.ManageApiKeys |  | PermissionChange |  | oui |  |
| POST | `/api/v1/api-keys/{id:guid}/rotate` | ApiKeysController:28 | Integrations.ManageApiKeys |  | PermissionChange |  | oui |  |
| GET | `/api/v1/apps` | MarketplaceController:36 | Integrations.View |  |  |  | oui |  |
| POST | `/api/v1/apps` | MarketplaceController:53 | Platform.ManageMarketplace |  |  |  | oui |  |
| GET | `/api/v1/apps/installed` | MarketplaceController:65 | Integrations.View |  |  |  | oui |  |
| GET | `/api/v1/apps/{id:guid}` | MarketplaceController:45 | Integrations.View |  |  |  | oui |  |
| POST | `/api/v1/apps/{id:guid}/disable` | MarketplaceController:79 | Integrations.ManageApps |  |  |  | oui |  |
| POST | `/api/v1/apps/{id:guid}/install` | MarketplaceController:69 | Integrations.ManageApps |  |  |  | oui |  |
| POST | `/api/v1/apps/{id:guid}/review` | MarketplaceController:58 | Platform.ManageMarketplace |  |  |  | oui |  |
| POST | `/api/v1/apps/{id:guid}/uninstall` | MarketplaceController:74 | Integrations.ManageApps |  |  |  | oui |  |
| POST | `/api/v1/ask` | AskController:19 | Reporting.Ask |  |  |  |  |  |
| GET | `/api/v1/assistant/briefing` | IntelligenceController:148 | Intelligence.UseAssistant |  |  |  | oui |  |
| POST | `/api/v1/assistant/query` | IntelligenceController:153 | Intelligence.UseAssistant |  |  |  | oui |  |
| POST | `/api/v1/attachments` | MobileController:98 | FieldOps.Manage |  |  |  | oui |  |
| GET | `/api/v1/attachments/{id:guid}` | MobileController:103 | FieldOps.View |  |  |  | oui |  |
| POST | `/api/v1/auth/login` | AuthController:32 | anonyme |  |  | auth | oui |  |
| POST | `/api/v1/auth/logout` | AuthController:97 | anonyme |  |  | refresh |  |  |
| GET | `/api/v1/auth/mfa` | AuthMfaController:17 | authentifié |  |  |  | oui |  |
| POST | `/api/v1/auth/mfa/disable` | AuthMfaController:32 | authentifié |  |  |  | oui |  |
| POST | `/api/v1/auth/mfa/enroll` | AuthMfaController:22 | authentifié |  |  |  | oui |  |
| POST | `/api/v1/auth/mfa/enroll/confirm` | AuthMfaController:27 | authentifié |  |  |  | oui |  |
| POST | `/api/v1/auth/mfa/verify` | AuthController:44 | anonyme |  |  | auth | oui |  |
| POST | `/api/v1/auth/password/forgot` | AuthController:81 | anonyme |  |  | auth | oui |  |
| POST | `/api/v1/auth/password/reset` | AuthController:90 | anonyme |  |  | auth | oui |  |
| POST | `/api/v1/auth/refresh` | AuthController:60 | anonyme |  |  | refresh |  |  |
| GET | `/api/v1/backup-policies` | ContinuityController:21 | Continuity.View |  |  |  | oui |  |
| POST | `/api/v1/backup-policies` | ContinuityController:25 | Continuity.Manage |  |  |  | oui |  |
| GET | `/api/v1/backups` | ContinuityController:32 | Continuity.View |  |  |  | oui |  |
| POST | `/api/v1/backups` | ContinuityController:36 | Continuity.Manage |  |  |  | oui |  |
| POST | `/api/v1/backups/{id:guid}/restore-test` | ContinuityController:46 | Continuity.Manage |  |  |  | oui | A-051 (vérification déclarative) |
| POST | `/api/v1/backups/{id:guid}/verify` | ContinuityController:41 | Continuity.Manage |  |  |  | oui | A-051 (vérification déclarative) |
| GET | `/api/v1/billing/invoices` | BillingController:30 | Platform.Portal |  |  |  | oui |  |
| GET | `/api/v1/billing/invoices/{id:guid}` | BillingController:34 | Platform.Portal |  |  |  |  |  |
| GET | `/api/v1/billing/payment-methods` | BillingController:42 | Platform.Portal |  |  |  |  |  |
| POST | `/api/v1/billing/payment-methods` | BillingController:46 | Platform.Portal |  |  |  |  |  |
| GET | `/api/v1/billing/payments` | BillingController:38 | Platform.Portal |  |  |  |  |  |
| POST | `/api/v1/billing/webhooks/payment` | BillingController:61 | anonyme |  |  |  |  | A-049 (jeton par défaut ; facture soldée sans contrôle du montant) |
| GET | `/api/v1/budgets` | BiController:152 | Reporting.ViewFinancial |  |  |  | oui | A-047 (versions de budget additionnées) |
| POST | `/api/v1/budgets` | BiController:156 | Reporting.ManageBudgets |  |  |  | oui |  |
| GET | `/api/v1/budgets/{id:guid}` | BiController:161 | Reporting.ViewFinancial |  |  |  |  | A-047 (versions de budget additionnées) |
| POST | `/api/v1/budgets/{id:guid}/approve` | BiController:178 | Reporting.ManageBudgets |  |  |  | oui |  |
| POST | `/api/v1/budgets/{id:guid}/close` | BiController:182 | Reporting.ManageBudgets |  |  |  | oui |  |
| GET | `/api/v1/budgets/{id:guid}/lines` | BiController:165 | Reporting.ViewFinancial |  |  |  | oui | A-047 (versions de budget additionnées) |
| PUT | `/api/v1/budgets/{id:guid}/lines` | BiController:169 | Reporting.ManageBudgets |  |  |  | oui |  |
| POST | `/api/v1/budgets/{id:guid}/revise` | BiController:186 | Reporting.ManageBudgets |  |  |  | oui |  |
| POST | `/api/v1/budgets/{id:guid}/submit` | BiController:174 | Reporting.ManageBudgets |  |  |  | oui |  |
| GET | `/api/v1/cashflow/forecast` | CashflowController:14 | Finance.View |  |  |  | oui |  |
| GET | `/api/v1/catalog/brands` | CatalogController:200 | Catalog.View |  |  |  | oui |  |
| POST | `/api/v1/catalog/brands` | CatalogController:204 | Catalog.Create |  |  |  | oui |  |
| GET | `/api/v1/catalog/categories` | CatalogController:81 | Catalog.View |  |  |  | oui |  |
| POST | `/api/v1/catalog/categories` | CatalogController:85 | Catalog.Create |  |  |  | oui |  |
| PUT | `/api/v1/catalog/categories/{id:guid}` | CatalogController:90 | Catalog.Update |  |  |  | oui |  |
| GET | `/api/v1/catalog/coupons` | CatalogController:180 | Catalog.ManagePromotion |  |  |  | oui |  |
| POST | `/api/v1/catalog/coupons` | CatalogController:184 | Catalog.ManagePromotion |  |  |  | oui |  |
| POST | `/api/v1/catalog/coupons/{id:guid}/status` | CatalogController:189 | Catalog.ManagePromotion |  |  |  | oui |  |
| GET | `/api/v1/catalog/currencies` | CatalogController:195 | Catalog.View |  |  |  | oui |  |
| GET | `/api/v1/catalog/customer-groups` | CatalogController:209 | Catalog.View |  |  |  | oui |  |
| POST | `/api/v1/catalog/customer-groups` | CatalogController:213 | Catalog.ManagePrice |  |  |  | oui |  |
| GET | `/api/v1/catalog/price-lists` | CatalogController:146 | Catalog.View |  |  |  | oui |  |
| POST | `/api/v1/catalog/price-lists` | CatalogController:150 | Catalog.ManagePrice |  |  |  | oui |  |
| POST | `/api/v1/catalog/pricing/quote` | CatalogController:219 | Catalog.View |  |  |  | oui | A-039 (simulateur ≠ caisse) |
| GET | `/api/v1/catalog/products` | CatalogController:30 | Catalog.View |  |  |  | oui |  |
| POST | `/api/v1/catalog/products` | CatalogController:45 | Catalog.Create |  |  |  | oui |  |
| GET | `/api/v1/catalog/products/{id:guid}` | CatalogController:37 | Catalog.View |  |  |  | oui |  |
| PUT | `/api/v1/catalog/products/{id:guid}` | CatalogController:50 | Catalog.Update |  |  |  | oui |  |
| POST | `/api/v1/catalog/products/{id:guid}/barcodes` | CatalogController:70 | Catalog.Update |  |  |  | oui |  |
| GET | `/api/v1/catalog/products/{id:guid}/image` | CatalogController:135 | Catalog.View |  |  |  | oui |  |
| POST | `/api/v1/catalog/products/{id:guid}/image` | CatalogController:129 | Catalog.Update |  |  |  | oui |  |
| POST | `/api/v1/catalog/products/{id:guid}/prices` | CatalogController:65 | Catalog.ManagePrice |  |  |  | oui |  |
| PUT | `/api/v1/catalog/products/{id:guid}/reorder-rule` | CatalogController:75 | Inventory.Adjust |  |  |  | oui |  |
| POST | `/api/v1/catalog/products/{id:guid}/status` | CatalogController:55 | Catalog.Update |  |  |  | oui |  |
| POST | `/api/v1/catalog/products/{id:guid}/suppliers` | CatalogController:60 | Catalog.Update |  |  |  | oui |  |
| GET | `/api/v1/catalog/promotions` | CatalogController:161 | Catalog.View |  |  |  | oui |  |
| POST | `/api/v1/catalog/promotions` | CatalogController:165 | Catalog.ManagePromotion |  |  |  | oui |  |
| PUT | `/api/v1/catalog/promotions/{id:guid}` | CatalogController:170 | Catalog.ManagePromotion |  |  |  | oui |  |
| POST | `/api/v1/catalog/promotions/{id:guid}/status` | CatalogController:175 | Catalog.ManagePromotion |  |  |  | oui |  |
| GET | `/api/v1/catalog/scan` | CatalogController:21 | Catalog.View |  |  |  | oui |  |
| GET | `/api/v1/catalog/search` | CatalogController:16 | Catalog.View |  |  |  | oui |  |
| GET | `/api/v1/catalog/taxes` | CatalogController:156 | Catalog.View |  |  |  | oui |  |
| GET | `/api/v1/catalog/unit-conversions` | CatalogController:105 | Catalog.View |  |  |  | oui |  |
| POST | `/api/v1/catalog/unit-conversions` | CatalogController:110 | Catalog.Update |  |  |  | oui |  |
| DELETE | `/api/v1/catalog/unit-conversions/{id:guid}` | CatalogController:115 | Catalog.Update |  |  |  | oui |  |
| GET | `/api/v1/catalog/units` | CatalogController:96 | Catalog.View |  |  |  | oui |  |
| POST | `/api/v1/catalog/units` | CatalogController:100 | Catalog.Create |  |  |  | oui |  |
| GET | `/api/v1/catalog/units/convert` | CatalogController:121 | Catalog.View |  |  |  | oui |  |
| GET | `/api/v1/chart-of-accounts` | ChartOfAccountsController:42 | Accounting.View |  |  |  |  |  |
| GET | `/api/v1/collection-actions` | CustomerAccountsController:200 | Credit.ViewPortfolio |  |  |  |  |  |
| GET | `/api/v1/collections` | CustomerAccountsController:195 | Credit.ViewPortfolio |  |  |  |  |  |
| GET | `/api/v1/companies` | CompaniesController:14 | Organizations.View |  |  |  | oui |  |
| GET | `/api/v1/companies/branding` | PreferencesController:33 | authentifié |  |  |  | oui |  |
| PUT | `/api/v1/companies/branding` | PreferencesController:37 | Admin.ConfigureCompany |  |  |  | oui |  |
| GET | `/api/v1/company-branding` | PreferencesController:33 | authentifié |  |  |  |  |  |
| PUT | `/api/v1/company-branding` | PreferencesController:37 | Admin.ConfigureCompany |  |  |  |  |  |
| POST | `/api/v1/configurations/clone` | ConfigurationController:43 | Config.Transfer |  |  |  | oui | A-034 |
| GET | `/api/v1/configurations/export` | ConfigurationController:33 | Config.Transfer |  |  |  | oui |  |
| GET | `/api/v1/configurations/history` | ConfigurationController:48 | Config.Transfer |  |  |  | oui |  |
| POST | `/api/v1/configurations/import` | ConfigurationController:38 | Config.Transfer |  |  |  |  |  |
| GET | `/api/v1/configurations/validate` | ConfigurationController:26 | Config.Validate |  |  |  | oui |  |
| GET | `/api/v1/configurations/{domain}/versions` | ConfigurationController:55 | Admin.ConfigVersions |  |  |  |  |  |
| POST | `/api/v1/configurations/{versionId:guid}/publish` | ConfigurationController:60 | Admin.ConfigVersions |  |  |  |  |  |
| POST | `/api/v1/configurations/{versionId:guid}/rollback` | ConfigurationController:65 | Admin.ConfigVersions |  |  |  |  |  |
| GET | `/api/v1/consolidations` | ConsolidationController:36 | Group.View |  |  |  |  |  |
| POST | `/api/v1/consolidations/{runId:guid}/run` | ConsolidationController:45 | Consolidation.Run |  |  |  |  |  |
| GET | `/api/v1/continuity/incidents` | ContinuityController:76 | Continuity.View |  |  |  | oui |  |
| POST | `/api/v1/continuity/incidents` | ContinuityController:83 | Continuity.Manage |  |  |  | oui |  |
| POST | `/api/v1/continuity/incidents/{id:guid}/advance` | ContinuityController:89 | Continuity.Manage |  |  |  | oui |  |
| POST | `/api/v1/continuity/incidents/{id:guid}/lockdown` | ContinuityController:105 | Continuity.Restore |  |  |  | oui |  |
| POST | `/api/v1/continuity/incidents/{id:guid}/resolve` | ContinuityController:94 | Continuity.Manage |  |  |  | oui |  |
| GET | `/api/v1/continuity/tests` | ContinuityController:99 | Continuity.View |  |  |  |  |  |
| GET | `/api/v1/credit-memos` | CustomerAccountsController:142 | Credit.ViewPortfolio |  |  |  |  |  |
| GET | `/api/v1/credit-memos/{id:guid}` | CustomerAccountsController:147 | Credit.ViewPortfolio |  |  |  |  |  |
| POST | `/api/v1/credit-memos/{id:guid}/apply` | CustomerAccountsController:152 | Credit.ReceivePayment | ✔ |  |  | oui |  |
| POST | `/api/v1/credit-notes` | DocumentsController:47 | Documents.Issue | ✔ |  |  |  |  |
| GET | `/api/v1/credit/aging` | CollectionsController:24 | Credit.ViewPortfolio |  |  |  | oui |  |
| GET | `/api/v1/credit/blocks` | CollectionsController:89 | Credit.ViewPortfolio |  |  |  | oui |  |
| POST | `/api/v1/credit/blocks` | CollectionsController:94 | Credit.Block |  |  |  | oui |  |
| POST | `/api/v1/credit/blocks/{id:guid}/release` | CollectionsController:99 | Credit.Block |  |  |  | oui |  |
| GET | `/api/v1/credit/collections` | CollectionsController:19 | Credit.ViewPortfolio |  |  |  | oui |  |
| GET | `/api/v1/credit/customers/{customerId:guid}/receivables` | CollectionsController:58 | Credit.ViewPortfolio |  |  |  | oui |  |
| GET | `/api/v1/credit/disputes` | CollectionsController:73 | Credit.ViewPortfolio |  |  |  | oui |  |
| POST | `/api/v1/credit/disputes` | CollectionsController:78 | Credit.Dispute |  |  |  | oui |  |
| POST | `/api/v1/credit/disputes/{id:guid}/resolve` | CollectionsController:83 | Credit.Dispute |  |  |  | oui |  |
| GET | `/api/v1/credit/follow-ups` | CollectionsController:63 | Credit.ViewPortfolio |  |  |  | oui |  |
| GET | `/api/v1/credit/promises` | CollectionsController:37 | Credit.ViewPortfolio |  |  |  | oui |  |
| POST | `/api/v1/credit/promises` | CollectionsController:42 | Credit.Collect |  |  |  | oui |  |
| POST | `/api/v1/credit/promises/{id:guid}/cancel` | CollectionsController:52 | Credit.Collect |  |  |  | oui |  |
| POST | `/api/v1/credit/promises/{id:guid}/settle` | CollectionsController:47 | Credit.Collect |  |  |  | oui |  |
| POST | `/api/v1/credit/receivables/{id:guid}/write-off` | CollectionsController:105 | Credit.WriteOff | ✔ |  |  | oui |  |
| GET | `/api/v1/credit/statements/{customerId:guid}` | CollectionsController:28 | Credit.ViewPortfolio |  |  |  | oui |  |
| GET | `/api/v1/credit/write-offs` | CollectionsController:67 | Credit.ViewPortfolio |  |  |  | oui |  |
| GET | `/api/v1/credits` | CustomerAccountsController:34 | Credit.ViewPortfolio |  |  |  |  |  |
| GET | `/api/v1/credits/{id:guid}` | CustomerAccountsController:39 | Credit.ViewPortfolio |  |  |  | oui |  |
| POST | `/api/v1/credits/{id:guid}/installments` | CustomerAccountsController:56 | Credit.SetLimit |  |  |  | oui |  |
| GET | `/api/v1/credits/{id:guid}/payments` | CustomerAccountsController:44 | Credit.ViewPortfolio |  |  |  |  |  |
| POST | `/api/v1/credits/{id:guid}/payments` | CustomerAccountsController:49 | Credit.ReceivePayment | ✔ |  |  |  |  |
| GET | `/api/v1/customer-expenses` | CustomerAccountsController:109 | Credit.ViewPortfolio |  |  |  | oui |  |
| GET | `/api/v1/customer-expenses/{id:guid}` | CustomerAccountsController:114 | Credit.ViewPortfolio |  |  |  |  |  |
| POST | `/api/v1/customer-expenses/{id:guid}/approve` | CustomerAccountsController:123 | Customers.ApproveExpense | ✔ |  |  | oui |  |
| POST | `/api/v1/customer-expenses/{id:guid}/cancel` | CustomerAccountsController:134 | Customers.CreateExpense | ✔ |  |  | oui |  |
| POST | `/api/v1/customer-expenses/{id:guid}/reject` | CustomerAccountsController:129 | Customers.ApproveExpense |  |  |  | oui |  |
| POST | `/api/v1/customer-expenses/{id:guid}/submit` | CustomerAccountsController:119 | Customers.CreateExpense |  |  |  | oui |  |
| GET | `/api/v1/customer-payment-options` | CustomerAccountsController:90 | Credit.ReceivePayment |  |  |  | oui |  |
| GET | `/api/v1/customer-refunds` | CustomerAccountsController:158 | Credit.ViewPortfolio |  |  |  | oui |  |
| POST | `/api/v1/customer-refunds` | CustomerAccountsController:163 | Credit.ReceivePayment | ✔ |  |  | oui |  |
| POST | `/api/v1/customer-refunds/{id:guid}/approve` | CustomerAccountsController:169 | Credit.Refund |  |  |  | oui |  |
| POST | `/api/v1/customer-refunds/{id:guid}/cancel` | CustomerAccountsController:188 | Customers.View |  |  |  | oui |  |
| POST | `/api/v1/customer-refunds/{id:guid}/execute` | CustomerAccountsController:181 | Customers.View | ✔ | Payment |  | oui |  |
| POST | `/api/v1/customer-refunds/{id:guid}/reject` | CustomerAccountsController:174 | Credit.Refund |  |  |  | oui |  |
| GET | `/api/v1/customer-reports` | CustomerAccountsController:207 | Credit.ViewPortfolio |  |  |  | oui |  |
| GET | `/api/v1/customer-reports/{key}` | CustomerAccountsController:211 | Credit.ViewPortfolio |  |  |  | oui |  |
| GET | `/api/v1/customer-reports/{key}/export` | CustomerAccountsController:218 | Credit.ViewPortfolio, Customers.Export |  |  |  | oui |  |
| GET | `/api/v1/customers` | CustomersController:34 | Customers.View |  |  |  | oui |  |
| POST | `/api/v1/customers` | CustomersController:51 | Customers.Create |  |  |  | oui |  |
| GET | `/api/v1/customers/lookup` | CustomersController:41 | Customers.View |  |  |  | oui |  |
| GET | `/api/v1/customers/{id:guid}` | CustomersController:46 | Customers.View |  |  |  | oui |  |
| PATCH | `/api/v1/customers/{id:guid}` | CustomersController:61 | Customers.Update |  |  |  | oui |  |
| PUT | `/api/v1/customers/{id:guid}` | CustomersController:56 | Customers.Update |  |  |  | oui |  |
| GET | `/api/v1/customers/{id:guid}/account` | CustomersController:73 | Credit.ViewPortfolio |  |  |  | oui |  |
| GET | `/api/v1/customers/{id:guid}/collection-actions` | CustomersController:146 | Credit.ViewPortfolio |  |  |  | oui |  |
| POST | `/api/v1/customers/{id:guid}/collection-actions` | CustomersController:151 | Credit.Collect |  |  |  | oui |  |
| GET | `/api/v1/customers/{id:guid}/credit-memos` | CustomersController:129 | Credit.ViewPortfolio |  |  |  | oui |  |
| GET | `/api/v1/customers/{id:guid}/credits` | CustomersController:87 | Customers.View |  |  |  | oui |  |
| POST | `/api/v1/customers/{id:guid}/credits` | CustomersController:96 | Credit.Create | ✔ |  |  | oui |  |
| GET | `/api/v1/customers/{id:guid}/expenses` | CustomersController:118 | Credit.ViewPortfolio |  |  |  | oui |  |
| POST | `/api/v1/customers/{id:guid}/expenses` | CustomersController:123 | Customers.CreateExpense | ✔ |  |  | oui |  |
| GET | `/api/v1/customers/{id:guid}/ledger` | CustomersController:79 | Credit.ViewPortfolio |  |  |  | oui |  |
| GET | `/api/v1/customers/{id:guid}/payments` | CustomersController:102 | Credit.ViewPortfolio |  |  |  | oui |  |
| POST | `/api/v1/customers/{id:guid}/payments` | CustomersController:107 | Credit.ReceivePayment | ✔ |  |  | oui |  |
| GET | `/api/v1/customers/{id:guid}/purchases` | CustomersController:113 | Credit.ViewPortfolio |  |  |  | oui |  |
| GET | `/api/v1/customers/{id:guid}/refunds` | CustomersController:134 | Credit.ViewPortfolio |  |  |  | oui |  |
| POST | `/api/v1/customers/{id:guid}/status` | CustomersController:66 | Customers.Update |  |  |  | oui |  |
| GET | `/api/v1/customers/{id:guid}/timeline` | CustomersController:139 | Credit.ViewPortfolio |  |  |  | oui |  |
| GET | `/api/v1/dashboard-layouts` | UxPersonalizationController:24 | authentifié |  |  |  | oui |  |
| PUT | `/api/v1/dashboard-layouts` | UxPersonalizationController:28 | authentifié |  |  |  | oui |  |
| POST | `/api/v1/dashboard-layouts/company-default` | UxPersonalizationController:32 | Admin.ConfigureCompany |  |  |  | oui |  |
| DELETE | `/api/v1/dashboard-layouts/{id:guid}` | UxPersonalizationController:37 | authentifié |  |  |  | oui |  |
| GET | `/api/v1/dashboard/activity` | ExperienceController:23 | authentifié |  |  |  | oui |  |
| GET | `/api/v1/dashboard/alerts` | DashboardController:48 | Reporting.View |  |  |  | oui |  |
| GET | `/api/v1/dashboard/cashier/{sessionId:guid}` | DashboardController:30 | Pos.View |  |  |  |  |  |
| GET | `/api/v1/dashboard/command-center` | DashboardController:23 | Reporting.View |  |  |  | oui | A-046 (devises additionnées) |
| GET | `/api/v1/dashboard/drill` | DashboardController:54 | Reporting.ViewFinancial |  |  |  |  |  |
| GET | `/api/v1/dashboard/groups/{groupId:guid}` | DashboardController:42 | Reporting.ViewGroup |  |  |  |  | A-046 (devises additionnées) |
| GET | `/api/v1/dashboard/kpi-catalog` | DashboardController:60 | Reporting.View |  |  |  |  |  |
| GET | `/api/v1/dashboard/manager` | DashboardController:36 | Reporting.ViewFinancial |  |  |  |  | A-046 (devises additionnées) |
| GET | `/api/v1/dashboards` | BiController:192 | Reporting.View |  |  |  |  |  |
| POST | `/api/v1/dashboards` | BiController:202 | Reporting.View |  |  |  |  |  |
| POST | `/api/v1/delivery-notes` | DocumentsController:59 | Documents.Issue | ✔ |  |  |  |  |
| GET | `/api/v1/departments` | EmployeesController:27 | Hr.ViewEmployees |  |  |  | oui |  |
| POST | `/api/v1/departments` | EmployeesController:31 | Hr.ManageEmployees |  |  |  | oui |  |
| PUT | `/api/v1/departments/{id:guid}` | EmployeesController:36 | Hr.ManageEmployees |  |  |  | oui |  |
| GET | `/api/v1/deployments` | OperationsController:82 | Operations.View |  |  |  | oui |  |
| POST | `/api/v1/deployments` | OperationsController:87 | Operations.Manage |  |  |  | oui |  |
| POST | `/api/v1/deployments/{id:guid}/rollback` | OperationsController:93 | Operations.Manage |  |  |  | oui |  |
| GET | `/api/v1/design-system/version` | DesignSystemController:15 | authentifié |  |  |  | oui |  |
| GET | `/api/v1/developer/keys` | MarketplaceController:86 | Integrations.ManageApiKeys |  |  |  |  |  |
| POST | `/api/v1/developer/keys` | MarketplaceController:90 | Integrations.ManageApiKeys |  |  |  |  |  |
| POST | `/api/v1/developer/keys/{id:guid}/revoke` | MarketplaceController:95 | Integrations.ManageApiKeys |  |  |  |  |  |
| GET | `/api/v1/developer/usage` | MarketplaceController:99 | Integrations.ManageApiKeys |  |  |  | oui |  |
| GET | `/api/v1/devices` | SyncController:43 | Sync.View |  |  |  |  |  |
| POST | `/api/v1/devices` | SyncController:49 | Sync.Devices |  |  |  |  |  |
| POST | `/api/v1/devices/{deviceId:guid}/revoke` | SyncController:55 | Sync.Devices |  |  |  |  |  |
| GET | `/api/v1/disaster-recovery/status` | ContinuityController:70 | Continuity.View |  |  |  | oui |  |
| GET | `/api/v1/document-audit` | GedController:276 | Ged.View |  |  |  | oui |  |
| GET | `/api/v1/document-retention-policies` | GedController:256 | Ged.View |  |  |  | oui |  |
| POST | `/api/v1/document-retention-policies` | GedController:260 | Ged.ManageRetention |  |  |  | oui |  |
| GET | `/api/v1/document-sequences` | DocumentsController:98 | Documents.ManageSequence |  |  |  | oui |  |
| POST | `/api/v1/document-shares/{shareId:guid}/revoke` | GedController:138 | Ged.Manage |  |  |  | oui |  |
| GET | `/api/v1/document-workflows` | GedController:66 | Ged.View |  |  |  | oui |  |
| POST | `/api/v1/document-workflows` | GedController:71 | Ged.Manage |  |  |  | oui |  |
| GET | `/api/v1/documents` | DocumentsController:24 | Documents.View |  |  |  | oui |  |
| POST | `/api/v1/documents` | DocumentsController:41 | Documents.Issue | ✔ |  |  | oui |  |
| GET | `/api/v1/documents/verify/{code}` | DocumentsController:90 | anonyme |  |  | public | oui |  |
| GET | `/api/v1/documents/{id:guid}` | DocumentsController:29 | Documents.View |  |  |  | oui |  |
| POST | `/api/v1/documents/{id:guid}/cancel` | DocumentsController:82 | Documents.Cancel |  | Cancellation |  | oui |  |
| GET | `/api/v1/documents/{id:guid}/pdf` | DocumentsController:69 | Documents.View |  |  |  |  |  |
| POST | `/api/v1/documents/{id:guid}/send` | DocumentsController:77 | Documents.Send |  |  |  | oui |  |
| GET | `/api/v1/documents/{id:guid}/sends` | DocumentsController:34 | Documents.View |  |  |  | oui |  |
| POST | `/api/v1/employee-contracts/{id:guid}/end` | EmployeesController:89 | Hr.ManageEmployees |  |  |  | oui |  |
| POST | `/api/v1/employee-documents/{id:guid}/archive` | EmployeesController:143 | Hr.ManageEmployees |  |  |  |  |  |
| GET | `/api/v1/employee-documents/{id:guid}/content` | EmployeesController:134 | Hr.ViewEmployees |  |  |  |  |  |
| POST | `/api/v1/employee-pay-elements/{id:guid}/end` | EmployeesController:118 | Payroll.ManageCompensation |  |  |  | oui |  |
| GET | `/api/v1/employees` | EmployeesController:43 | Hr.ViewEmployees |  |  |  | oui |  |
| POST | `/api/v1/employees` | EmployeesController:48 | Hr.ManageEmployees |  |  |  | oui |  |
| GET | `/api/v1/employees/{id:guid}` | EmployeesController:52 | Hr.ViewEmployees |  |  |  | oui |  |
| PUT | `/api/v1/employees/{id:guid}` | EmployeesController:57 | Hr.ManageEmployees |  |  |  | oui |  |
| GET | `/api/v1/employees/{id:guid}/compensation` | EmployeesController:104 | Payroll.ViewSalaries |  |  |  | oui |  |
| GET | `/api/v1/employees/{id:guid}/contracts` | EmployeesController:80 | Hr.ViewEmployees |  |  |  | oui |  |
| POST | `/api/v1/employees/{id:guid}/contracts` | EmployeesController:84 | Hr.ManageEmployees |  |  |  | oui |  |
| GET | `/api/v1/employees/{id:guid}/documents` | EmployeesController:125 | Hr.ViewEmployees |  |  |  | oui |  |
| POST | `/api/v1/employees/{id:guid}/documents` | EmployeesController:129 | Hr.ManageEmployees |  |  |  | oui |  |
| GET | `/api/v1/employees/{id:guid}/history` | EmployeesController:149 | Payroll.ViewSalaries |  |  |  |  |  |
| GET | `/api/v1/employees/{id:guid}/pay-elements` | EmployeesController:109 | Payroll.ViewSalaries |  |  |  | oui |  |
| POST | `/api/v1/employees/{id:guid}/pay-elements` | EmployeesController:113 | Payroll.ManageCompensation |  |  |  | oui |  |
| GET | `/api/v1/employees/{id:guid}/payslips` | EmployeesController:153 | Payroll.ViewSalaries |  |  |  |  |  |
| GET | `/api/v1/employees/{id:guid}/salary-advances` | EmployeesController:157 | Hr.ViewEmployees |  |  |  |  |  |
| POST | `/api/v1/employees/{id:guid}/salary-advances` | EmployeesController:167 | Payroll.AdvanceRequest |  |  |  |  |  |
| POST | `/api/v1/employees/{id:guid}/salary-advances/preview` | EmployeesController:162 | Payroll.AdvanceRequest |  |  |  |  |  |
| POST | `/api/v1/employees/{id:guid}/salary-changes` | EmployeesController:98 | Payroll.ManageCompensation |  |  |  | oui |  |
| GET | `/api/v1/employees/{id:guid}/salary-history` | EmployeesController:94 | Payroll.ViewSalaries |  |  |  | oui |  |
| GET | `/api/v1/employees/{id:guid}/sensitive` | EmployeesController:73 | Payroll.ViewSensitive |  | SensitiveData |  | oui |  |
| POST | `/api/v1/employees/{id:guid}/status` | EmployeesController:62 | Hr.ManageEmployees |  |  |  | oui |  |
| POST | `/api/v1/employees/{id:guid}/user` | EmployeesController:67 | Hr.ManageEmployees |  |  |  | oui |  |
| GET | `/api/v1/exchange-rates` | ExchangeRatesController:15 | Finance.View |  |  |  | oui |  |
| POST | `/api/v1/exchange-rates` | ExchangeRatesController:20 | Finance.Update |  |  |  | oui |  |
| GET | `/api/v1/expense-categories` | ExpensesController:127 | Finance.View |  |  |  | oui |  |
| POST | `/api/v1/expense-categories` | ExpensesController:131 | Finance.Update |  |  |  | oui |  |
| PUT | `/api/v1/expense-categories/{id:guid}` | ExpensesController:136 | Finance.Update |  |  |  | oui |  |
| GET | `/api/v1/expenses` | ExpensesController:83 | Finance.View |  |  |  | oui |  |
| POST | `/api/v1/expenses` | ExpensesController:93 | Finance.Create |  |  |  | oui |  |
| GET | `/api/v1/expenses/{id:guid}` | ExpensesController:89 | Finance.View |  |  |  | oui |  |
| PUT | `/api/v1/expenses/{id:guid}` | ExpensesController:97 | Finance.Create |  |  |  | oui |  |
| POST | `/api/v1/expenses/{id:guid}/approve` | ExpensesController:106 | Finance.Pay |  |  |  | oui |  |
| POST | `/api/v1/expenses/{id:guid}/cancel` | ExpensesController:115 | Finance.Create |  |  |  | oui |  |
| POST | `/api/v1/expenses/{id:guid}/pay` | ExpensesController:120 | Finance.Pay | ✔ | Payment |  | oui |  |
| POST | `/api/v1/expenses/{id:guid}/reject` | ExpensesController:110 | Finance.Pay |  |  |  | oui |  |
| POST | `/api/v1/expenses/{id:guid}/submit` | ExpensesController:102 | Finance.Create |  |  |  | oui |  |
| POST | `/api/v1/exports/screen` | ExportsController:67 | Reporting.Export |  |  |  | oui |  |
| POST | `/api/v1/field-ops/device` | MobileController:40 | FieldOps.Manage |  |  |  |  |  |
| GET | `/api/v1/field-ops/interventions` | MobileController:149 | FieldOps.View |  |  |  | oui |  |
| POST | `/api/v1/field-ops/interventions` | MobileController:153 | FieldOps.Manage |  |  |  | oui |  |
| GET | `/api/v1/field-ops/interventions/mine` | MobileController:158 | FieldOps.View |  |  |  | oui |  |
| POST | `/api/v1/field-ops/interventions/{id:guid}/complete` | MobileController:167 | FieldOps.Manage |  |  |  | oui |  |
| POST | `/api/v1/field-ops/interventions/{id:guid}/start` | MobileController:162 | FieldOps.Manage |  |  |  | oui |  |
| GET | `/api/v1/field-ops/my-stops` | MobileController:126 | FieldOps.View |  |  |  | oui |  |
| GET | `/api/v1/field-ops/options` | MobileController:34 | FieldOps.View |  |  |  | oui |  |
| GET | `/api/v1/field-ops/routes` | MobileController:113 | FieldOps.View |  |  |  | oui |  |
| POST | `/api/v1/field-ops/routes` | MobileController:117 | FieldOps.Manage |  |  |  | oui |  |
| GET | `/api/v1/field-ops/routes/{id:guid}` | MobileController:122 | FieldOps.View |  |  |  |  |  |
| POST | `/api/v1/field-ops/stops/{id:guid}/advance` | MobileController:130 | FieldOps.Manage |  |  |  | oui |  |
| POST | `/api/v1/field-ops/stops/{id:guid}/complete` | MobileController:137 | FieldOps.Manage |  |  |  | oui |  |
| POST | `/api/v1/field-ops/stops/{id:guid}/fail` | MobileController:142 | FieldOps.Manage |  |  |  | oui |  |
| GET | `/api/v1/finance-lookups` | FinancialAccountsController:61 | Finance.View |  |  |  | oui |  |
| POST | `/api/v1/finance/bank-charges` | FinanceController:62 | Finance.Reconcile | ✔ |  |  |  | A-045 (montants libres, sans approbation) |
| POST | `/api/v1/finance/cash-counts` | FinanceController:68 | Finance.ManageCash | ✔ |  |  |  | A-045 (montants libres, sans approbation) |
| POST | `/api/v1/finance/cash-sales` | FinanceController:19 | Finance.Create | ✔ |  |  |  | A-045 (montants libres, sans approbation) |
| POST | `/api/v1/finance/credit-sales` | FinanceController:25 | Finance.Create | ✔ |  |  |  | A-045 (montants libres, sans approbation) |
| POST | `/api/v1/finance/customer-payments` | FinanceController:43 | Finance.Pay | ✔ |  |  |  | A-045 (montants libres, sans approbation) |
| POST | `/api/v1/finance/expenses` | FinanceController:37 | Finance.Pay | ✔ |  |  |  | A-045 (montants libres, sans approbation) |
| POST | `/api/v1/finance/supplier-invoices` | FinanceController:31 | Purchasing.Create | ✔ |  |  |  | A-045 (montants libres, sans approbation) |
| POST | `/api/v1/finance/supplier-payments` | FinanceController:49 | Finance.Pay | ✔ | Payment |  |  | A-045 (montants libres, sans approbation) |
| POST | `/api/v1/finance/transfers` | FinanceController:56 | Finance.Transfer | ✔ |  |  |  | A-045 (montants libres, sans approbation) |
| GET | `/api/v1/financial-accounts` | FinancialAccountsController:16 | Finance.View |  |  |  | oui |  |
| POST | `/api/v1/financial-accounts` | FinancialAccountsController:26 | Finance.Update |  |  |  | oui |  |
| GET | `/api/v1/financial-accounts/manage` | FinancialAccountsController:22 | Finance.View |  |  |  | oui |  |
| PUT | `/api/v1/financial-accounts/{id:guid}` | FinancialAccountsController:31 | Finance.Update |  |  |  | oui |  |
| POST | `/api/v1/financial-accounts/{id:guid}/opening-balance` | FinancialAccountsController:42 | Finance.Update | ✔ |  |  | oui |  |
| POST | `/api/v1/financial-accounts/{id:guid}/status` | FinancialAccountsController:36 | Finance.Update |  |  |  | oui |  |
| GET | `/api/v1/financial-accounts/{id:guid}/transactions` | FinancialAccountsController:48 | Finance.View |  |  |  | oui |  |
| GET | `/api/v1/financial-statements` | FinancialStatementsController:18 | Accounting.View |  |  |  | oui | A-043 (extournes exclues des agrégats) |
| GET | `/api/v1/forecasts` | BiController:114 | Reporting.ViewFinancial |  |  |  | oui |  |
| POST | `/api/v1/forecasts` | BiController:118 | Reporting.Forecast |  |  |  | oui |  |
| POST | `/api/v1/ged-documents` | GedController:27 | Ged.Upload |  |  |  | oui |  |
| GET | `/api/v1/ged-documents/eligible-for-deletion` | GedController:270 | Ged.ManageRetention |  |  |  | oui |  |
| POST | `/api/v1/ged-documents/ocr` | GedController:212 | Ged.Upload |  |  |  | oui |  |
| GET | `/api/v1/ged-documents/search` | GedController:32 | Ged.View |  |  |  | oui |  |
| GET | `/api/v1/ged-documents/{id:guid}` | GedController:37 | Ged.View |  |  |  | oui |  |
| PUT | `/api/v1/ged-documents/{id:guid}` | GedController:41 | Ged.Manage |  |  |  | oui |  |
| POST | `/api/v1/ged-documents/{id:guid}/approve` | GedController:76 | Ged.Manage |  |  |  | oui |  |
| POST | `/api/v1/ged-documents/{id:guid}/archive` | GedController:97 | Ged.Manage |  |  |  | oui |  |
| POST | `/api/v1/ged-documents/{id:guid}/cancel-deletion` | GedController:116 | Ged.ManageRetention |  |  |  | oui |  |
| POST | `/api/v1/ged-documents/{id:guid}/confirm-deletion` | GedController:112 | Ged.ManageRetention |  |  |  | oui |  |
| GET | `/api/v1/ged-documents/{id:guid}/content` | GedController:55 | Ged.View |  |  |  | oui |  |
| POST | `/api/v1/ged-documents/{id:guid}/legal-hold` | GedController:265 | Ged.ManageRetention |  |  |  | oui |  |
| GET | `/api/v1/ged-documents/{id:guid}/links` | GedController:127 | Ged.View |  |  |  |  |  |
| POST | `/api/v1/ged-documents/{id:guid}/links` | GedController:122 | Ged.Manage |  |  |  |  |  |
| POST | `/api/v1/ged-documents/{id:guid}/lock` | GedController:87 | Ged.Manage |  |  |  | oui |  |
| POST | `/api/v1/ged-documents/{id:guid}/reject` | GedController:80 | Ged.Manage |  |  |  | oui |  |
| POST | `/api/v1/ged-documents/{id:guid}/request-deletion` | GedController:107 | Ged.ManageRetention |  |  |  | oui |  |
| POST | `/api/v1/ged-documents/{id:guid}/restore` | GedController:101 | Ged.Manage |  |  |  | oui |  |
| POST | `/api/v1/ged-documents/{id:guid}/share` | GedController:133 | Ged.Manage |  |  |  | oui |  |
| POST | `/api/v1/ged-documents/{id:guid}/signature` | GedController:219 | Ged.Sign |  |  |  | oui |  |
| POST | `/api/v1/ged-documents/{id:guid}/unlock` | GedController:91 | Ged.Manage |  |  |  | oui |  |
| GET | `/api/v1/ged-documents/{id:guid}/versions` | GedController:51 | Ged.View |  |  |  | oui |  |
| POST | `/api/v1/ged-documents/{id:guid}/versions` | GedController:46 | Ged.Upload |  |  |  | oui |  |
| GET | `/api/v1/general-ledger` | FinancialStatementsController:32 | Accounting.View |  |  |  | oui |  |
| GET | `/api/v1/go-live` | OperationsController:67 | Operations.View |  |  |  | oui |  |
| POST | `/api/v1/go-live/sign-off` | OperationsController:75 | Operations.Manage |  |  |  | oui |  |
| GET | `/api/v1/go-live/sign-offs` | OperationsController:71 | Operations.View |  |  |  | oui |  |
| GET | `/api/v1/groups/{groupId:guid}/accounts` | GroupController:26 | Group.Mappings |  |  |  |  |  |
| POST | `/api/v1/groups/{groupId:guid}/accounts` | GroupController:31 | Group.Mappings |  |  |  |  |  |
| GET | `/api/v1/groups/{groupId:guid}/accounts/mapping-coverage` | GroupController:51 | Group.Mappings |  |  |  |  |  |
| GET | `/api/v1/groups/{groupId:guid}/accounts/mappings` | GroupController:41 | Group.Mappings |  |  |  |  |  |
| PUT | `/api/v1/groups/{groupId:guid}/accounts/mappings` | GroupController:36 | Group.Mappings |  |  |  |  |  |
| POST | `/api/v1/groups/{groupId:guid}/companies` | GroupController:119 | Group.Companies |  |  |  |  |  |
| POST | `/api/v1/groups/{groupId:guid}/companies/{companyId:guid}/archive` | GroupController:124 | Group.Companies |  | Deletion |  |  |  |
| GET | `/api/v1/groups/{groupId:guid}/consolidation/runs` | ConsolidationController:21 | Group.View |  |  |  |  |  |
| POST | `/api/v1/groups/{groupId:guid}/consolidation/runs` | ConsolidationController:64 | Consolidation.Run |  |  |  |  |  |
| GET | `/api/v1/groups/{groupId:guid}/consolidation/runs/{runId:guid}` | ConsolidationController:54 | Group.View |  |  |  |  |  |
| POST | `/api/v1/groups/{groupId:guid}/consolidation/runs/{runId:guid}/execute` | ConsolidationController:69 | Consolidation.Run |  |  |  |  |  |
| POST | `/api/v1/groups/{groupId:guid}/consolidation/runs/{runId:guid}/publish` | ConsolidationController:79 | Consolidation.Publish |  | AccountingValidation |  |  |  |
| POST | `/api/v1/groups/{groupId:guid}/consolidation/runs/{runId:guid}/reopen` | ConsolidationController:85 | Consolidation.Validate |  | AccountingValidation |  |  |  |
| GET | `/api/v1/groups/{groupId:guid}/consolidation/runs/{runId:guid}/statements` | ConsolidationController:59 | Group.View |  |  |  |  |  |
| POST | `/api/v1/groups/{groupId:guid}/consolidation/runs/{runId:guid}/validate` | ConsolidationController:74 | Consolidation.Validate |  |  |  |  |  |
| GET | `/api/v1/groups/{groupId:guid}/control-center` | GroupController:95 | Group.View |  |  |  |  |  |
| GET | `/api/v1/groups/{groupId:guid}/intercompany` | GroupController:68 | Group.Intercompany |  |  |  |  |  |
| POST | `/api/v1/groups/{groupId:guid}/intercompany` | GroupController:73 | Group.Intercompany |  |  |  |  |  |
| POST | `/api/v1/groups/{groupId:guid}/intercompany/reconcile` | GroupController:83 | Group.Intercompany |  |  |  |  |  |
| GET | `/api/v1/groups/{groupId:guid}/intercompany/unreconciled` | GroupController:88 | Group.Intercompany |  |  |  |  |  |
| POST | `/api/v1/groups/{groupId:guid}/intercompany/{transactionId:guid}/confirm` | GroupController:78 | Group.Intercompany |  |  |  |  |  |
| GET | `/api/v1/groups/{groupId:guid}/options` | GroupController:46 | Group.View |  |  |  |  |  |
| GET | `/api/v1/groups/{groupId:guid}/policy` | GroupController:56 | Group.View |  |  |  |  |  |
| PUT | `/api/v1/groups/{groupId:guid}/policy` | GroupController:61 | Group.Manage |  |  |  |  |  |
| GET | `/api/v1/groups/{groupId:guid}/tasks` | GroupController:102 | Group.Tasks |  |  |  |  |  |
| POST | `/api/v1/groups/{groupId:guid}/tasks` | GroupController:107 | Group.Tasks |  |  |  |  |  |
| PUT | `/api/v1/groups/{groupId:guid}/tasks/{taskId:guid}` | GroupController:112 | Group.Tasks |  |  |  |  |  |
| GET | `/api/v1/hr/approvals` | HrController:130 | Hr.ViewEmployees |  |  |  | oui |  |
| GET | `/api/v1/hr/attendance` | HrController:26 | Hr.ViewEmployees |  |  |  | oui |  |
| POST | `/api/v1/hr/attendance` | HrController:33 | Hr.ManageTime |  |  |  | oui |  |
| POST | `/api/v1/hr/attendance/{id:guid}/reject` | HrController:43 | Hr.ApproveRequests |  |  |  |  |  |
| POST | `/api/v1/hr/attendance/{id:guid}/validate` | HrController:38 | Hr.ApproveRequests |  |  |  |  |  |
| GET | `/api/v1/hr/audit` | HrController:209 | Payroll.Audit |  |  |  |  | A-034 |
| GET | `/api/v1/hr/dashboard` | HrController:170 | Payroll.Reports |  |  |  | oui |  |
| GET | `/api/v1/hr/expense-claims` | HrController:136 | Hr.ViewEmployees |  |  |  |  |  |
| POST | `/api/v1/hr/expense-claims` | HrController:141 | Hr.ManageTime |  |  |  |  |  |
| POST | `/api/v1/hr/expense-claims/{id:guid}/approve` | HrController:146 | Payroll.ExpenseApprove |  |  |  | oui |  |
| POST | `/api/v1/hr/expense-claims/{id:guid}/cancel` | HrController:156 | Hr.ViewEmployees |  |  |  |  |  |
| POST | `/api/v1/hr/expense-claims/{id:guid}/pay` | HrController:161 | Payroll.ExpenseApprove | ✔ | Payment |  | oui |  |
| POST | `/api/v1/hr/expense-claims/{id:guid}/reject` | HrController:151 | Payroll.ExpenseApprove |  |  |  | oui |  |
| GET | `/api/v1/hr/insights` | HrController:174 | Payroll.Reports |  |  |  |  |  |
| GET | `/api/v1/hr/leaves` | HrController:50 | Hr.ViewEmployees |  |  |  |  |  |
| POST | `/api/v1/hr/leaves` | HrController:55 | Hr.ManageTime |  |  |  |  |  |
| POST | `/api/v1/hr/leaves/{id:guid}/approve` | HrController:59 | Hr.ApproveRequests |  |  |  |  |  |
| POST | `/api/v1/hr/leaves/{id:guid}/cancel` | HrController:69 | Hr.ViewEmployees |  |  |  |  |  |
| POST | `/api/v1/hr/leaves/{id:guid}/reject` | HrController:64 | Hr.ApproveRequests |  |  |  |  |  |
| GET | `/api/v1/hr/overtime` | HrController:76 | Hr.ViewEmployees |  |  |  |  |  |
| POST | `/api/v1/hr/overtime` | HrController:81 | Hr.ManageTime |  |  |  |  |  |
| POST | `/api/v1/hr/overtime/{id:guid}/approve` | HrController:86 | Hr.ApproveRequests |  |  |  |  |  |
| POST | `/api/v1/hr/overtime/{id:guid}/cancel` | HrController:96 | Hr.ViewEmployees |  |  |  |  |  |
| POST | `/api/v1/hr/overtime/{id:guid}/reject` | HrController:91 | Hr.ApproveRequests |  |  |  |  |  |
| GET | `/api/v1/hr/reports` | HrController:178 | Payroll.Reports |  |  |  | oui |  |
| GET | `/api/v1/hr/reports/{key}` | HrController:182 | Payroll.Reports |  |  |  | oui |  |
| GET | `/api/v1/hr/reports/{key}/export` | HrController:189 | Payroll.Reports |  |  |  | oui |  |
| GET | `/api/v1/hr/variables` | HrController:103 | Hr.ViewEmployees |  |  |  | oui |  |
| POST | `/api/v1/hr/variables` | HrController:109 | Hr.ViewEmployees |  |  |  | oui |  |
| POST | `/api/v1/hr/variables/{id:guid}/approve` | HrController:114 | Hr.ApproveRequests |  |  |  |  |  |
| POST | `/api/v1/hr/variables/{id:guid}/cancel` | HrController:124 | Hr.ViewEmployees |  |  |  | oui |  |
| POST | `/api/v1/hr/variables/{id:guid}/reject` | HrController:119 | Hr.ApproveRequests |  |  |  |  |  |
| GET | `/api/v1/import-templates` | ImportsController:95 | Migration.View |  |  |  |  |  |
| GET | `/api/v1/imports` | ImportsController:19 | Migration.View |  |  |  | oui |  |
| POST | `/api/v1/imports` | ImportsController:27 | Migration.Manage |  |  |  | oui |  |
| POST | `/api/v1/imports/suggest-mapping/{kind}` | ImportsController:90 | Migration.View |  |  |  | oui |  |
| GET | `/api/v1/imports/target-fields/{kind}` | ImportsController:86 | Migration.View |  |  |  | oui |  |
| GET | `/api/v1/imports/{id:guid}` | ImportsController:23 | Migration.View |  |  |  | oui |  |
| POST | `/api/v1/imports/{id:guid}/cancel` | ImportsController:59 | Migration.Manage |  |  |  | oui |  |
| GET | `/api/v1/imports/{id:guid}/errors` | ImportsController:68 | Migration.View |  |  |  | oui |  |
| GET | `/api/v1/imports/{id:guid}/events` | ImportsController:80 | Migration.View |  |  |  |  |  |
| POST | `/api/v1/imports/{id:guid}/execute` | ImportsController:47 | Migration.Manage |  |  |  | oui | A-050 (stock d'ouverture sans écriture) |
| POST | `/api/v1/imports/{id:guid}/mapping` | ImportsController:35 | Migration.Manage |  |  |  | oui |  |
| POST | `/api/v1/imports/{id:guid}/pause` | ImportsController:51 | Migration.Manage |  |  |  |  |  |
| POST | `/api/v1/imports/{id:guid}/profile` | ImportsController:31 | Migration.Manage |  |  |  | oui |  |
| GET | `/api/v1/imports/{id:guid}/reconciliation` | ImportsController:76 | Migration.View |  |  |  | oui |  |
| POST | `/api/v1/imports/{id:guid}/resume` | ImportsController:55 | Migration.Manage |  |  |  |  |  |
| GET | `/api/v1/imports/{id:guid}/rows` | ImportsController:63 | Migration.View |  |  |  | oui |  |
| POST | `/api/v1/imports/{id:guid}/rows` | ImportsController:72 | Migration.Manage |  |  |  | oui |  |
| POST | `/api/v1/imports/{id:guid}/simulate` | ImportsController:43 | Migration.Manage |  |  |  | oui |  |
| POST | `/api/v1/imports/{id:guid}/validate` | ImportsController:39 | Migration.Manage |  |  |  | oui |  |
| GET | `/api/v1/incidents` | ContinuityController:76 | Continuity.View |  |  |  |  |  |
| GET | `/api/v1/insights` | BiController:127 | Reporting.View |  |  |  | oui |  |
| POST | `/api/v1/insights/{id:guid}/assign` | BiController:145 | Reporting.ManageInsights |  |  |  | oui |  |
| POST | `/api/v1/insights/{id:guid}/dismiss` | BiController:140 | Reporting.ManageInsights |  |  |  | oui |  |
| GET | `/api/v1/integrations` | IntegrationsController:71 | Integrations.View |  |  |  |  |  |
| POST | `/api/v1/integrations` | IntegrationsController:75 | Integrations.Configure |  |  |  |  |  |
| GET | `/api/v1/integrations/bank/lines/{lineId:guid}/candidates` | IntegrationsController:223 | Integrations.Bank |  |  |  | oui |  |
| POST | `/api/v1/integrations/bank/lines/{lineId:guid}/match` | IntegrationsController:231 | Integrations.Bank |  |  |  | oui |  |
| POST | `/api/v1/integrations/bank/lines/{lineId:guid}/unmatch` | IntegrationsController:227 | Integrations.Bank |  |  |  | oui |  |
| GET | `/api/v1/integrations/bank/statements` | IntegrationsController:202 | Integrations.Bank |  |  |  | oui |  |
| POST | `/api/v1/integrations/bank/statements` | IntegrationsController:206 | Integrations.Bank |  |  |  | oui |  |
| POST | `/api/v1/integrations/bank/statements/{id:guid}/auto-match` | IntegrationsController:215 | Integrations.Bank |  |  |  | oui |  |
| GET | `/api/v1/integrations/bank/statements/{id:guid}/lines` | IntegrationsController:211 | Integrations.Bank |  |  |  | oui |  |
| POST | `/api/v1/integrations/bank/statements/{id:guid}/validate` | IntegrationsController:219 | Integrations.Bank |  |  |  | oui |  |
| GET | `/api/v1/integrations/center` | IntegrationsController:238 | Integrations.View |  |  |  | oui |  |
| GET | `/api/v1/integrations/connectors` | IntegrationsController:31 | Integrations.View |  |  |  | oui |  |
| POST | `/api/v1/integrations/connectors` | IntegrationsController:39 | Integrations.Configure |  |  |  | oui |  |
| GET | `/api/v1/integrations/connectors/{id:guid}` | IntegrationsController:35 | Integrations.View |  |  |  |  |  |
| PUT | `/api/v1/integrations/connectors/{id:guid}/credentials` | IntegrationsController:55 | Integrations.Configure |  |  |  | oui |  |
| POST | `/api/v1/integrations/connectors/{id:guid}/rotate-secret` | IntegrationsController:64 | Integrations.Configure |  |  |  | oui |  |
| POST | `/api/v1/integrations/connectors/{id:guid}/status` | IntegrationsController:44 | Integrations.Configure |  |  |  | oui |  |
| POST | `/api/v1/integrations/connectors/{id:guid}/test` | IntegrationsController:49 | Integrations.Configure |  |  |  | oui |  |
| GET | `/api/v1/integrations/payment-providers` | IntegrationsController:60 | Integrations.View |  |  |  | oui |  |
| GET | `/api/v1/integrations/payments/intents` | IntegrationsController:100 | Integrations.Payments |  |  |  | oui |  |
| POST | `/api/v1/integrations/payments/intents` | IntegrationsController:105 | Integrations.Payments |  |  |  | oui |  |
| POST | `/api/v1/integrations/payments/intents/expire` | IntegrationsController:134 | Integrations.Payments |  |  |  | oui |  |
| GET | `/api/v1/integrations/payments/intents/{id:guid}` | IntegrationsController:110 | Integrations.Payments |  |  |  | oui |  |
| POST | `/api/v1/integrations/payments/intents/{id:guid}/callback` | IntegrationsController:115 | Integrations.Payments |  |  |  | oui |  |
| POST | `/api/v1/integrations/payments/intents/{id:guid}/refund` | IntegrationsController:127 | Integrations.Payments, Pos.Refund |  | Payment |  | oui |  |
| POST | `/api/v1/integrations/payments/intents/{id:guid}/verify` | IntegrationsController:123 | Integrations.Payments |  |  |  | oui |  |
| GET | `/api/v1/integrations/payments/reconciliation` | IntegrationsController:138 | Integrations.Payments |  |  |  |  |  |
| GET | `/api/v1/integrations/webhooks/events` | IntegrationsController:157 | Integrations.Webhooks |  |  |  | oui |  |
| POST | `/api/v1/integrations/webhooks/inbound/{connectorCode}` | IntegrationsController:145 | anonyme |  |  |  |  | FAIBLE (pas de limite de débit) |
| GET | `/api/v1/integrations/webhooks/outbound` | IntegrationsController:164 | Integrations.Webhooks |  |  |  | oui |  |
| POST | `/api/v1/integrations/webhooks/outbound` | IntegrationsController:168 | Integrations.Webhooks |  |  |  | oui |  |
| POST | `/api/v1/integrations/webhooks/outbound/deliver-due` | IntegrationsController:196 | Integrations.Webhooks |  |  |  | oui |  |
| GET | `/api/v1/integrations/webhooks/outbound/deliveries` | IntegrationsController:187 | Integrations.Webhooks |  |  |  | oui |  |
| POST | `/api/v1/integrations/webhooks/outbound/deliveries/{id:guid}/retry` | IntegrationsController:192 | Integrations.Webhooks |  |  |  | oui |  |
| POST | `/api/v1/integrations/webhooks/outbound/emit` | IntegrationsController:178 | Integrations.Webhooks |  |  |  |  |  |
| POST | `/api/v1/integrations/webhooks/outbound/{id:guid}/status` | IntegrationsController:173 | Integrations.Webhooks |  |  |  |  |  |
| GET | `/api/v1/integrations/{id:guid}/health` | IntegrationsController:80 | Integrations.View |  |  |  |  |  |
| POST | `/api/v1/integrations/{id:guid}/sync` | IntegrationsController:92 | Integrations.Configure |  |  |  |  |  |
| POST | `/api/v1/integrations/{id:guid}/test` | IntegrationsController:84 | Integrations.Configure |  |  |  |  |  |
| GET | `/api/v1/intelligence/anomalies` | IntelligenceController:87 | Intelligence.View |  |  |  | oui |  |
| GET | `/api/v1/intelligence/audit` | IntelligenceController:124 | Intelligence.View |  |  |  | oui |  |
| GET | `/api/v1/intelligence/dashboard` | IntelligenceController:76 | Intelligence.View |  |  |  | oui |  |
| GET | `/api/v1/intelligence/forecasts` | IntelligenceController:91 | Intelligence.View |  |  |  | oui | A-048 (prévisions financières hors reporting.view_financial) |
| GET | `/api/v1/intelligence/insights` | IntelligenceController:82 | Intelligence.View |  |  |  |  |  |
| GET | `/api/v1/intelligence/models` | IntelligenceController:103 | Intelligence.View |  |  |  | oui |  |
| GET | `/api/v1/intelligence/recommendations` | IntelligenceController:34 | Intelligence.View |  |  |  | oui | A-048 (prévisions financières hors reporting.view_financial) |
| GET | `/api/v1/intelligence/recommendations/{id:guid}` | IntelligenceController:39 | Intelligence.View |  |  |  |  | A-048 (prévisions financières hors reporting.view_financial) |
| POST | `/api/v1/intelligence/recommendations/{id:guid}/accept` | IntelligenceController:48 | Intelligence.ActOnRecommendation |  |  |  | oui |  |
| POST | `/api/v1/intelligence/recommendations/{id:guid}/dismiss` | IntelligenceController:53 | Intelligence.ActOnRecommendation |  |  |  | oui |  |
| POST | `/api/v1/intelligence/recommendations/{id:guid}/modify` | IntelligenceController:70 | Intelligence.ActOnRecommendation |  |  |  | oui |  |
| POST | `/api/v1/intelligence/recommendations/{id:guid}/resolve` | IntelligenceController:64 | Intelligence.ActOnRecommendation |  |  |  | oui |  |
| POST | `/api/v1/intelligence/recommendations/{id:guid}/snooze` | IntelligenceController:58 | Intelligence.ActOnRecommendation |  |  |  | oui |  |
| POST | `/api/v1/intelligence/recommendations/{id:guid}/view` | IntelligenceController:44 | Intelligence.View |  |  |  | oui |  |
| GET | `/api/v1/intelligence/rules` | IntelligenceController:109 | Intelligence.View |  |  |  | oui |  |
| POST | `/api/v1/intelligence/rules` | IntelligenceController:113 | Intelligence.ManageRules |  |  |  | oui |  |
| DELETE | `/api/v1/intelligence/rules/{id:guid}` | IntelligenceController:118 | Intelligence.ManageRules |  |  |  | oui |  |
| GET | `/api/v1/intelligence/scores` | IntelligenceController:97 | Intelligence.View |  |  |  | oui |  |
| GET | `/api/v1/intelligence/tasks` | IntelligenceController:130 | Intelligence.View |  |  |  | oui |  |
| POST | `/api/v1/intelligence/tasks` | IntelligenceController:134 | Intelligence.ActOnRecommendation |  |  |  | oui |  |
| POST | `/api/v1/intelligence/tasks/{id:guid}/complete` | IntelligenceController:139 | Intelligence.ActOnRecommendation |  |  |  | oui |  |
| GET | `/api/v1/inventory/adjustments` | InventoryController:99 | Inventory.View |  |  |  | oui |  |
| POST | `/api/v1/inventory/adjustments` | InventoryController:118 | Inventory.Adjust | ✔ |  |  | oui |  |
| GET | `/api/v1/inventory/adjustments/{id:guid}` | InventoryController:106 | Inventory.View |  |  |  | oui |  |
| GET | `/api/v1/inventory/balance` | InventoryController:20 | Inventory.View |  |  |  |  |  |
| GET | `/api/v1/inventory/counts` | InventoryController:64 | Inventory.View |  |  |  | oui |  |
| POST | `/api/v1/inventory/counts` | InventoryController:75 | Inventory.Count |  |  |  | oui |  |
| GET | `/api/v1/inventory/counts/{id:guid}` | InventoryController:69 | Inventory.View |  |  |  | oui |  |
| POST | `/api/v1/inventory/counts/{id:guid}/cancel` | InventoryController:93 | Inventory.Count |  |  |  | oui |  |
| PUT | `/api/v1/inventory/counts/{id:guid}/lines` | InventoryController:81 | Inventory.Count |  |  |  | oui |  |
| POST | `/api/v1/inventory/counts/{id:guid}/validate` | InventoryController:87 | Inventory.ValidateAdjustment | ✔ |  |  | oui |  |
| POST | `/api/v1/inventory/losses` | InventoryController:111 | Inventory.WriteOff | ✔ |  |  | oui |  |
| GET | `/api/v1/inventory/reorder-suggestions` | InventoryController:124 | Purchasing.View |  |  |  |  |  |
| GET | `/api/v1/inventory/transfers` | InventoryController:31 | Inventory.View |  |  |  | oui |  |
| POST | `/api/v1/inventory/transfers` | InventoryController:41 | Inventory.Transfer |  |  |  | oui |  |
| GET | `/api/v1/inventory/transfers/{id:guid}` | InventoryController:36 | Inventory.View |  |  |  | oui |  |
| POST | `/api/v1/inventory/transfers/{id:guid}/cancel` | InventoryController:58 | Inventory.Transfer |  |  |  | oui |  |
| POST | `/api/v1/inventory/transfers/{id:guid}/close` | InventoryController:52 | Inventory.Transfer | ✔ |  |  | oui |  |
| POST | `/api/v1/inventory/transfers/{id:guid}/receive` | InventoryController:46 | Inventory.Transfer |  |  |  | oui | A-038 (réceptions concurrentes) |
| GET | `/api/v1/invoices` | InvoicesController:18 | Documents.View |  |  |  |  |  |
| POST | `/api/v1/invoices/{id:guid}/cancel` | InvoicesController:34 | Documents.Cancel |  | Cancellation |  |  |  |
| POST | `/api/v1/invoices/{saleId:guid}/issue` | InvoicesController:28 | Documents.Issue | ✔ |  |  |  |  |
| GET | `/api/v1/journals` | ChartOfAccountsController:52 | Accounting.View |  |  |  | oui |  |
| GET | `/api/v1/kpi-definitions` | BiController:36 | Reporting.View |  |  |  | oui |  |
| GET | `/api/v1/kpi-definitions/{key}` | BiController:40 | Reporting.View |  |  |  |  |  |
| GET | `/api/v1/kpis` | AnalyticsController:58 | Reporting.View |  |  |  | oui | A-046 (devises additionnées) |
| GET | `/api/v1/kpis/{key}/definition` | BiController:40 | Reporting.View |  |  |  |  |  |
| GET | `/api/v1/ledger-options` | FinancialAccountsController:55 | Finance.View |  |  |  | oui |  |
| GET | `/api/v1/licenses` | LicensesController:27 | Platform.Portal |  |  |  | oui |  |
| POST | `/api/v1/licenses/verify` | LicensesController:52 | anonyme |  |  | public |  |  |
| POST | `/api/v1/licenses/{id:guid}/revoke` | LicensesController:47 | Platform.ManageLicenses |  |  |  |  |  |
| POST | `/api/v1/licenses/{id:guid}/token` | LicensesController:32 | Platform.Portal |  |  |  | oui |  |
| GET | `/api/v1/me` | MeController:24 | authentifié |  |  |  | oui |  |
| GET | `/api/v1/me/hr/advances` | MeHrController:65 | Payroll.SelfService |  |  |  |  |  |
| POST | `/api/v1/me/hr/advances` | MeHrController:72 | Payroll.SelfService |  |  |  |  |  |
| POST | `/api/v1/me/hr/advances/preview` | MeHrController:68 | Payroll.SelfService |  |  |  |  |  |
| POST | `/api/v1/me/hr/advances/{id:guid}/cancel` | MeHrController:86 | Payroll.SelfService |  |  |  |  |  |
| GET | `/api/v1/me/hr/advances/{id:guid}/history` | MeHrController:76 | Payroll.SelfService |  |  |  |  |  |
| GET | `/api/v1/me/hr/documents` | MeHrController:130 | Payroll.SelfService |  |  |  |  |  |
| GET | `/api/v1/me/hr/documents/{id:guid}/content` | MeHrController:133 | Payroll.SelfService |  |  |  |  |  |
| GET | `/api/v1/me/hr/expense-claims` | MeHrController:121 | Payroll.SelfService |  |  |  |  |  |
| POST | `/api/v1/me/hr/expense-claims` | MeHrController:124 | Payroll.SelfService |  |  |  |  |  |
| GET | `/api/v1/me/hr/history` | MeHrController:36 | Payroll.SelfService |  |  |  |  |  |
| GET | `/api/v1/me/hr/leaves` | MeHrController:98 | Payroll.SelfService |  |  |  |  |  |
| POST | `/api/v1/me/hr/leaves` | MeHrController:101 | Payroll.SelfService |  |  |  |  |  |
| POST | `/api/v1/me/hr/leaves/{id:guid}/cancel` | MeHrController:105 | Payroll.SelfService |  |  |  |  |  |
| GET | `/api/v1/me/hr/overtime` | MeHrController:114 | Payroll.SelfService |  |  |  |  |  |
| POST | `/api/v1/me/hr/overtime` | MeHrController:117 | Payroll.SelfService |  |  |  |  |  |
| GET | `/api/v1/me/hr/payroll-items/{id:guid}` | MeHrController:53 | Payroll.SelfService |  |  |  |  |  |
| GET | `/api/v1/me/hr/payslips` | MeHrController:41 | Payroll.SelfService |  |  |  |  |  |
| GET | `/api/v1/me/hr/payslips/{id:guid}/file` | MeHrController:44 | Payroll.SelfService |  |  |  |  |  |
| GET | `/api/v1/me/hr/profile` | MeHrController:33 | Payroll.SelfService |  |  |  | oui |  |
| POST | `/api/v1/me/logout` | MeController:61 | authentifié |  |  |  |  |  |
| POST | `/api/v1/me/password` | MeController:78 | authentifié |  |  |  | oui |  |
| GET | `/api/v1/me/permissions` | MeController:48 | authentifié |  |  |  |  |  |
| GET | `/api/v1/me/preferences` | PreferencesController:22 | authentifié |  |  |  |  |  |
| PUT | `/api/v1/me/preferences` | PreferencesController:27 | authentifié |  |  |  |  |  |
| GET | `/api/v1/me/scopes` | MeController:86 | authentifié |  |  |  |  |  |
| GET | `/api/v1/message-templates` | MessageTemplatesController:19 | Notifications.ManageTemplates |  |  |  | oui |  |
| POST | `/api/v1/message-templates` | MessageTemplatesController:23 | Notifications.ManageTemplates |  |  |  | oui |  |
| POST | `/api/v1/message-templates/render` | MessageTemplatesController:28 | Notifications.ManageTemplates |  |  |  | oui |  |
| GET | `/api/v1/metrics` | MetricsController:23 | anonyme |  |  |  |  |  |
| GET | `/api/v1/mobile/config` | MobileController:72 | Sync.View |  |  |  | oui |  |
| GET | `/api/v1/mobile/conflicts` | MobileController:61 | Sync.View |  |  |  |  |  |
| POST | `/api/v1/mobile/conflicts/{conflictId:guid}/resolve` | MobileController:65 | Sync.Manage |  |  |  |  |  |
| GET | `/api/v1/mobile/crash-reports` | MobileController:81 | Sync.Manage |  |  |  | oui |  |
| POST | `/api/v1/mobile/crash-reports` | MobileController:76 | Sync.View |  |  |  | oui |  |
| GET | `/api/v1/mobile/notifications` | MobileController:86 | Sync.View |  |  |  |  |  |
| GET | `/api/v1/mobile/sync` | MobileController:47 | Sync.View |  |  |  |  |  |
| POST | `/api/v1/mobile/sync/pull` | MobileController:57 | Sync.View |  |  |  |  |  |
| POST | `/api/v1/mobile/sync/push` | MobileController:51 | Sync.View |  |  |  |  |  |
| GET | `/api/v1/mobile/tasks` | MobileController:91 | Sync.View |  |  |  |  |  |
| POST | `/api/v1/notification-delivery/dispatch` | NotificationDeliveryController:54 | Notifications.ManageDelivery |  |  |  | oui |  |
| GET | `/api/v1/notification-delivery/outbox` | NotificationDeliveryController:36 | Notifications.ManageDelivery |  |  |  | oui |  |
| GET | `/api/v1/notification-delivery/outbox/{id:guid}/log` | NotificationDeliveryController:41 | Notifications.ManageDelivery |  |  |  | oui |  |
| POST | `/api/v1/notification-delivery/outbox/{id:guid}/retry` | NotificationDeliveryController:45 | Notifications.ManageDelivery |  |  |  | oui |  |
| GET | `/api/v1/notification-delivery/providers` | NotificationDeliveryController:19 | Notifications.ManageDelivery |  |  |  | oui |  |
| POST | `/api/v1/notification-delivery/providers/{channel}/requeue` | NotificationDeliveryController:49 | Notifications.ManageDelivery |  |  |  | oui |  |
| POST | `/api/v1/notification-delivery/providers/{channel}/test` | NotificationDeliveryController:23 | Notifications.ManageDelivery |  |  |  | oui |  |
| GET | `/api/v1/notification-delivery/summary` | NotificationDeliveryController:32 | Notifications.ManageDelivery |  |  |  | oui |  |
| POST | `/api/v1/notification-delivery/test-message` | NotificationDeliveryController:27 | Notifications.ManageDelivery |  |  |  | oui |  |
| GET | `/api/v1/notification-preferences` | NotificationPreferencesController:59 | authentifié |  |  |  | oui |  |
| POST | `/api/v1/notification-preferences` | NotificationPreferencesController:69 | authentifié |  |  |  | oui |  |
| GET | `/api/v1/notification-preferences/channels` | NotificationPreferencesController:64 | authentifié |  |  |  | oui |  |
| DELETE | `/api/v1/notification-preferences/{id:guid}` | NotificationPreferencesController:73 | authentifié |  |  |  | oui |  |
| GET | `/api/v1/notifications` | NotificationsController:16 | authentifié |  |  |  | oui |  |
| POST | `/api/v1/notifications/read-all` | NotificationsController:32 | authentifié |  |  |  | oui |  |
| GET | `/api/v1/notifications/unread-count` | NotificationsController:20 | authentifié |  |  |  | oui |  |
| POST | `/api/v1/notifications/{id:guid}/archive` | NotificationsController:44 | authentifié |  |  |  |  |  |
| POST | `/api/v1/notifications/{id:guid}/pin` | NotificationsController:36 | authentifié |  |  |  |  |  |
| PATCH | `/api/v1/notifications/{id:guid}/read` | NotificationsController:24 | authentifié |  |  |  |  |  |
| POST | `/api/v1/notifications/{id:guid}/unarchive` | NotificationsController:48 | authentifié |  |  |  |  |  |
| POST | `/api/v1/notifications/{id:guid}/unpin` | NotificationsController:40 | authentifié |  |  |  |  |  |
| PATCH | `/api/v1/notifications/{id:guid}/unread` | NotificationsController:28 | authentifié |  |  |  |  |  |
| GET | `/api/v1/objectives` | BiController:47 | Reporting.View |  |  |  | oui |  |
| POST | `/api/v1/objectives` | BiController:51 | Reporting.ManageTargets |  |  |  | oui |  |
| GET | `/api/v1/objectives/score` | BiController:60 | Reporting.View |  |  |  | oui |  |
| DELETE | `/api/v1/objectives/{id:guid}` | BiController:56 | Reporting.ManageTargets |  |  |  | oui |  |
| GET | `/api/v1/onboarding/checklist` | OnboardingController:15 | Migration.View |  |  |  | oui |  |
| POST | `/api/v1/onboarding/tasks` | OnboardingController:19 | Migration.Manage |  |  |  | oui |  |
| GET | `/api/v1/ops/audit` | SecurityController:33 | Security.AuditView |  |  |  |  |  |
| POST | `/api/v1/ops/cache/invalidate` | OpsMaintenanceController:92 | Operations.Manage |  |  |  | oui |  |
| GET | `/api/v1/ops/data-corrections` | OpsMaintenanceController:162 | Operations.View |  |  |  | oui |  |
| POST | `/api/v1/ops/data-corrections` | OpsMaintenanceController:166 | Operations.Manage |  |  |  | oui |  |
| POST | `/api/v1/ops/data-corrections/{id:guid}/decide` | OpsMaintenanceController:171 | Operations.CorrectData |  |  |  | oui |  |
| GET | `/api/v1/ops/database` | OpsMaintenanceController:82 | Operations.View |  |  |  | oui |  |
| GET | `/api/v1/ops/dead-letters` | OpsMaintenanceController:63 | Operations.View |  |  |  | oui |  |
| DELETE | `/api/v1/ops/dead-letters/{id:guid}` | OpsMaintenanceController:71 | Operations.Manage |  |  |  | oui |  |
| POST | `/api/v1/ops/dead-letters/{id:guid}/replay` | OpsMaintenanceController:67 | Operations.Manage |  |  |  | oui |  |
| GET | `/api/v1/ops/diagnostics` | OpsMaintenanceController:77 | Operations.View |  |  |  | oui |  |
| GET | `/api/v1/ops/feature-flags` | OpsMaintenanceController:105 | Operations.View |  |  |  | oui |  |
| POST | `/api/v1/ops/feature-flags` | OpsMaintenanceController:109 | Operations.Manage |  |  |  | oui |  |
| DELETE | `/api/v1/ops/feature-flags/{id:guid}` | OpsMaintenanceController:114 | Operations.Manage |  |  |  | oui |  |
| GET | `/api/v1/ops/health` | OpsMaintenanceController:77 | Operations.View |  |  |  |  |  |
| GET | `/api/v1/ops/jobs` | OpsMaintenanceController:33 | Operations.View |  |  |  | oui |  |
| GET | `/api/v1/ops/jobs/{key}/history` | OpsMaintenanceController:37 | Operations.View |  |  |  |  |  |
| POST | `/api/v1/ops/jobs/{key}/pause` | OpsMaintenanceController:45 | Operations.Manage |  |  |  | oui |  |
| POST | `/api/v1/ops/jobs/{key}/resume` | OpsMaintenanceController:49 | Operations.Manage |  |  |  | oui |  |
| POST | `/api/v1/ops/jobs/{key}/run` | OpsMaintenanceController:41 | Operations.Manage |  |  |  | oui |  |
| GET | `/api/v1/ops/maintenance` | OpsMaintenanceController:120 | Operations.View |  |  |  | oui |  |
| POST | `/api/v1/ops/maintenance` | OpsMaintenanceController:124 | Operations.Manage |  |  |  | oui |  |
| POST | `/api/v1/ops/maintenance/{id:guid}/transition` | OpsMaintenanceController:129 | Operations.Manage |  |  |  | oui |  |
| GET | `/api/v1/ops/production` | OperationsController:28 | Operations.View |  |  |  | oui |  |
| POST | `/api/v1/ops/purge/execute` | OpsMaintenanceController:150 | Operations.Manage |  | Deletion |  | oui |  |
| GET | `/api/v1/ops/purge/history` | OpsMaintenanceController:156 | Operations.View |  |  |  | oui |  |
| POST | `/api/v1/ops/purge/preview` | OpsMaintenanceController:145 | Operations.Manage |  |  |  | oui |  |
| GET | `/api/v1/ops/queues` | OpsMaintenanceController:55 | Operations.View |  |  |  | oui |  |
| POST | `/api/v1/ops/queues/{id:guid}/retry` | OpsMaintenanceController:59 | Operations.Manage |  |  |  |  |  |
| GET | `/api/v1/ops/retention-policies` | OpsMaintenanceController:136 | Operations.View |  |  |  | oui |  |
| POST | `/api/v1/ops/retention-policies` | OpsMaintenanceController:140 | Operations.Manage |  |  |  | oui |  |
| GET | `/api/v1/ops/status` | OperationsController:60 | Operations.View |  |  |  |  |  |
| GET | `/api/v1/ops/storage` | OpsMaintenanceController:86 | Operations.View |  |  |  | oui |  |
| GET | `/api/v1/orders` | OrdersController:21 | Pos.ManageOrder |  |  |  | oui |  |
| POST | `/api/v1/orders` | OrdersController:26 | Pos.ManageOrder |  |  |  | oui |  |
| GET | `/api/v1/orders/payment-options` | OrdersController:17 | Pos.ManageOrder |  |  |  | oui |  |
| GET | `/api/v1/orders/{id:guid}` | OrdersController:31 | Pos.ManageOrder |  |  |  | oui |  |
| PUT | `/api/v1/orders/{id:guid}` | OrdersController:39 | Pos.ManageOrder |  |  |  | oui |  |
| POST | `/api/v1/orders/{id:guid}/cancel` | OrdersController:66 | Pos.ManageOrder |  |  |  | oui |  |
| POST | `/api/v1/orders/{id:guid}/complete` | OrdersController:60 | Pos.ManageOrder | ✔ |  |  | oui |  |
| POST | `/api/v1/orders/{id:guid}/confirm` | OrdersController:44 | Pos.ManageOrder |  |  |  | oui |  |
| POST | `/api/v1/orders/{id:guid}/deposit` | OrdersController:54 | Pos.ManageOrder | ✔ |  |  | oui |  |
| POST | `/api/v1/orders/{id:guid}/reserve` | OrdersController:49 | Pos.ManageOrder |  |  |  | oui |  |
| GET | `/api/v1/payables` | PayablesController:127 | Purchasing.ViewPayables |  |  |  | oui |  |
| GET | `/api/v1/payables/payment-accounts` | PayablesController:135 | Purchasing.Pay |  |  |  | oui |  |
| POST | `/api/v1/payables/{id:guid}/payments` | PayablesController:140 | Purchasing.Pay | ✔ | Payment |  | oui |  |
| GET | `/api/v1/payments/{id:guid}` | CustomerAccountsController:94 | Credit.ViewPortfolio |  |  |  |  |  |
| POST | `/api/v1/payments/{id:guid}/void` | CustomerAccountsController:100 | Credit.VoidPayment | ✔ | Cancellation |  | oui |  |
| GET | `/api/v1/payroll/adjustments` | PayrollController:200 | Payroll.ViewSalaries |  |  |  | oui |  |
| POST | `/api/v1/payroll/adjustments` | PayrollController:205 | Payroll.Adjust |  |  |  | oui |  |
| POST | `/api/v1/payroll/adjustments/{id:guid}/approve` | PayrollController:210 | Payroll.ApproveAdjustment |  |  |  |  |  |
| POST | `/api/v1/payroll/adjustments/{id:guid}/cancel` | PayrollController:220 | Payroll.Adjust |  |  |  |  |  |
| POST | `/api/v1/payroll/adjustments/{id:guid}/reject` | PayrollController:215 | Payroll.ApproveAdjustment |  |  |  |  |  |
| GET | `/api/v1/payroll/components` | PayrollController:151 | Hr.ViewEmployees |  |  |  | oui |  |
| POST | `/api/v1/payroll/components` | PayrollController:155 | Payroll.Configure |  |  |  | oui |  |
| PUT | `/api/v1/payroll/components/{id:guid}` | PayrollController:160 | Payroll.Configure |  |  |  | oui |  |
| GET | `/api/v1/payroll/items/{id:guid}` | PayrollController:48 | Payroll.ViewSalaries |  |  |  | oui |  |
| POST | `/api/v1/payroll/items/{id:guid}/exclude` | PayrollController:52 | Payroll.Calculate |  |  |  | oui |  |
| POST | `/api/v1/payroll/items/{id:guid}/include` | PayrollController:57 | Payroll.Calculate |  |  |  | oui |  |
| GET | `/api/v1/payroll/payment-accounts` | PayrollController:99 | Payroll.Pay |  |  |  | oui |  |
| GET | `/api/v1/payroll/periods` | PayrollController:24 | Payroll.ViewSalaries |  |  |  | oui |  |
| POST | `/api/v1/payroll/periods` | PayrollController:29 | Payroll.Calculate |  |  |  | oui |  |
| GET | `/api/v1/payroll/periods/{id:guid}` | PayrollController:34 | Payroll.ViewSalaries |  |  |  | oui |  |
| POST | `/api/v1/payroll/periods/{id:guid}/calculate` | PayrollController:38 | Payroll.Calculate |  |  |  | oui |  |
| POST | `/api/v1/payroll/rule-templates/{id:guid}/adopt` | PayrollController:194 | Payroll.Configure |  |  |  | oui |  |
| GET | `/api/v1/payroll/rules` | PayrollController:165 | Payroll.Configure |  |  |  | oui |  |
| POST | `/api/v1/payroll/rules` | PayrollController:170 | Payroll.Configure |  |  |  | oui |  |
| PUT | `/api/v1/payroll/rules/{id:guid}` | PayrollController:174 | Payroll.Configure |  |  |  | oui |  |
| POST | `/api/v1/payroll/rules/{id:guid}/retire` | PayrollController:189 | Payroll.Configure |  |  |  | oui |  |
| POST | `/api/v1/payroll/rules/{id:guid}/validate` | PayrollController:184 | Payroll.Configure |  |  |  | oui |  |
| POST | `/api/v1/payroll/rules/{id:guid}/versions` | PayrollController:179 | Payroll.Configure |  |  |  | oui |  |
| GET | `/api/v1/payroll/{id:guid}` | PayrollController:44 | Payroll.ViewSalaries |  |  |  | oui |  |
| POST | `/api/v1/payroll/{id:guid}/approve` | PayrollController:73 | Payroll.Approve |  | AccountingValidation |  | oui |  |
| POST | `/api/v1/payroll/{id:guid}/cancel` | PayrollController:85 | Payroll.Calculate |  | Cancellation |  | oui |  |
| POST | `/api/v1/payroll/{id:guid}/close` | PayrollController:91 | Payroll.Close |  | AccountingValidation |  | oui |  |
| POST | `/api/v1/payroll/{id:guid}/pay` | PayrollController:103 | Payroll.Pay | ✔ | Payment |  | oui |  |
| GET | `/api/v1/payroll/{id:guid}/payments` | PayrollController:109 | Payroll.ViewSalaries |  |  |  | oui |  |
| GET | `/api/v1/payroll/{id:guid}/payslips` | PayrollController:122 | Payroll.ViewSalaries |  |  |  | oui |  |
| POST | `/api/v1/payroll/{id:guid}/payslips` | PayrollController:126 | Payroll.Pay |  |  |  | oui |  |
| GET | `/api/v1/payroll/{id:guid}/reconciliation` | PayrollController:145 | Payroll.ViewSalaries |  |  |  | oui |  |
| POST | `/api/v1/payroll/{id:guid}/return` | PayrollController:80 | Payroll.ViewSalaries |  |  |  | oui |  |
| POST | `/api/v1/payroll/{id:guid}/review` | PayrollController:63 | Payroll.Review |  |  |  | oui |  |
| POST | `/api/v1/payroll/{id:guid}/validate` | PayrollController:68 | Payroll.Validate |  |  |  | oui |  |
| GET | `/api/v1/payslips/{id:guid}/file` | PayrollController:130 | Payroll.ViewSalaries |  |  |  |  |  |
| GET | `/api/v1/payslips/{id:guid}/verify` | PayrollController:139 | Payroll.ViewSalaries |  |  |  | oui |  |
| GET | `/api/v1/periods` | PeriodsController:20 | Finance.View |  |  |  | oui |  |
| GET | `/api/v1/periods/{id:guid}/financial-close` | PeriodsController:29 | Finance.View |  |  |  | oui |  |
| POST | `/api/v1/periods/{id:guid}/financial-close` | PeriodsController:34 | Finance.ClosePeriod |  |  |  | oui |  |
| GET | `/api/v1/platform/addons` | PlatformController:41 | Platform.View |  |  |  |  |  |
| POST | `/api/v1/platform/addons` | PlatformController:45 | Platform.ManageCatalogue |  |  |  |  |  |
| GET | `/api/v1/platform/coupons` | PlatformController:49 | Platform.ManageCatalogue |  |  |  |  |  |
| POST | `/api/v1/platform/coupons` | PlatformController:53 | Platform.ManageCatalogue |  |  |  |  |  |
| GET | `/api/v1/platform/customers` | PlatformController:59 | Platform.View |  |  |  | oui |  |
| POST | `/api/v1/platform/customers` | PlatformController:67 | Platform.ManageCustomers |  |  |  | oui |  |
| GET | `/api/v1/platform/customers/{customerId:guid}/licenses` | LicensesController:39 | Platform.View |  |  |  |  |  |
| GET | `/api/v1/platform/customers/{id:guid}` | PlatformController:63 | Platform.View |  |  |  |  |  |
| PATCH | `/api/v1/platform/customers/{id:guid}` | PlatformController:71 | Platform.ManageCustomers |  |  |  |  |  |
| POST | `/api/v1/platform/customers/{id:guid}/companies` | PlatformController:75 | Platform.ManageCustomers |  |  |  |  |  |
| DELETE | `/api/v1/platform/customers/{id:guid}/companies/{companyId:guid}` | PlatformController:79 | Platform.ManageCustomers |  | Deletion |  |  |  |
| GET | `/api/v1/platform/customers/{id:guid}/invoices` | PlatformController:124 | Platform.View |  |  |  | oui |  |
| GET | `/api/v1/platform/customers/{id:guid}/payments` | PlatformController:128 | Platform.View |  |  |  |  |  |
| GET | `/api/v1/platform/customers/{id:guid}/quotas` | PlatformController:132 | Platform.View |  |  |  | oui |  |
| POST | `/api/v1/platform/customers/{id:guid}/subscription` | PlatformController:90 | Platform.ManageSubscriptions |  |  |  | oui |  |
| GET | `/api/v1/platform/customers/{id:guid}/subscriptions` | PlatformController:86 | Platform.View |  |  |  | oui |  |
| GET | `/api/v1/platform/customers/{id:guid}/support-access` | PlatformController:154 | Platform.SupportAccess |  |  |  |  |  |
| POST | `/api/v1/platform/invoices/{id:guid}/credit-note` | PlatformController:136 | Platform.ManageBilling |  |  |  |  |  |
| POST | `/api/v1/platform/licenses` | LicensesController:43 | Platform.ManageLicenses |  |  |  |  |  |
| GET | `/api/v1/platform/metrics` | PlatformController:27 | Platform.View |  |  |  | oui |  |
| POST | `/api/v1/platform/payments` | PlatformController:144 | Platform.RecordPayment |  |  |  | oui |  |
| GET | `/api/v1/platform/payments/review` | PlatformController:140 | Platform.ManageBilling |  |  |  | oui |  |
| POST | `/api/v1/platform/payments/{id:guid}/confirm` | PlatformController:148 | Platform.RecordPayment |  |  |  | oui |  |
| GET | `/api/v1/platform/plans` | PlatformController:33 | Platform.View |  |  |  | oui |  |
| POST | `/api/v1/platform/plans` | PlatformController:37 | Platform.ManageCatalogue |  |  |  | oui |  |
| POST | `/api/v1/platform/subscriptions/{id:guid}/activate` | PlatformController:94 | Platform.ManageSubscriptions |  |  |  |  |  |
| POST | `/api/v1/platform/subscriptions/{id:guid}/addons` | PlatformController:106 | Platform.ManageSubscriptions |  |  |  |  |  |
| POST | `/api/v1/platform/subscriptions/{id:guid}/cancel` | PlatformController:110 | Platform.ManageSubscriptions |  |  |  |  |  |
| POST | `/api/v1/platform/subscriptions/{id:guid}/downgrade` | PlatformController:102 | Platform.ManageSubscriptions |  |  |  | oui |  |
| POST | `/api/v1/platform/subscriptions/{id:guid}/reactivate` | PlatformController:114 | Platform.ManageSubscriptions |  |  |  | oui |  |
| POST | `/api/v1/platform/subscriptions/{id:guid}/suspend` | PlatformController:118 | Platform.ManageSubscriptions |  |  |  | oui |  |
| POST | `/api/v1/platform/subscriptions/{id:guid}/upgrade` | PlatformController:98 | Platform.ManageSubscriptions |  |  |  | oui |  |
| POST | `/api/v1/platform/support-access` | PlatformController:158 | Platform.SupportAccess |  |  |  |  |  |
| POST | `/api/v1/platform/support-access/{id:guid}/approve` | PlatformController:162 | Platform.SupportAccess |  |  |  |  |  |
| POST | `/api/v1/platform/support-access/{id:guid}/revoke` | PlatformController:166 | Platform.SupportAccess |  |  |  |  |  |
| GET | `/api/v1/platform/support/tickets` | PlatformSupportController:43 | Platform.Support |  |  |  |  |  |
| POST | `/api/v1/platform/support/tickets` | PlatformSupportController:52 | Platform.Support |  |  |  |  |  |
| GET | `/api/v1/platform/support/tickets/{id:guid}` | PlatformSupportController:48 | Platform.Support |  |  |  |  |  |
| PATCH | `/api/v1/platform/support/tickets/{id:guid}` | PlatformSupportController:61 | Platform.Support |  |  |  |  |  |
| POST | `/api/v1/platform/support/tickets/{id:guid}/reply` | PlatformSupportController:56 | Platform.Support |  |  |  |  |  |
| POST | `/api/v1/pos/cart/preview` | PosController:106 | Pos.Sell |  |  |  | oui |  |
| POST | `/api/v1/pos/checkout` | PosController:111 | Pos.Sell | ✔ |  |  | oui | A-040 (taxId client) |
| GET | `/api/v1/pos/credit-override-requests` | CustomerAccountsController:72 | Pos.CreditOverride |  |  |  | oui |  |
| POST | `/api/v1/pos/credit-override-requests` | CustomerAccountsController:63 | Pos.SellCredit |  |  |  | oui |  |
| GET | `/api/v1/pos/credit-override-requests/{id:guid}` | CustomerAccountsController:68 | Pos.SellCredit |  |  |  | oui |  |
| POST | `/api/v1/pos/credit-override-requests/{id:guid}/approve` | CustomerAccountsController:77 | Pos.CreditOverride |  |  |  | oui |  |
| POST | `/api/v1/pos/credit-override-requests/{id:guid}/reject` | CustomerAccountsController:82 | Pos.CreditOverride |  |  |  | oui |  |
| POST | `/api/v1/pos/device` | PosController:31 | Pos.Sell |  |  |  |  |  |
| GET | `/api/v1/pos/discount-requests` | PosController:85 | Pos.View |  |  |  | oui |  |
| POST | `/api/v1/pos/discount-requests` | PosController:78 | Pos.Discount | ✔ |  |  | oui |  |
| GET | `/api/v1/pos/discount-requests/{id:guid}` | PosController:90 | Pos.View |  |  |  | oui |  |
| POST | `/api/v1/pos/discount-requests/{id:guid}/approve` | PosController:95 | Pos.ApproveDiscount |  |  |  | oui |  |
| POST | `/api/v1/pos/discount-requests/{id:guid}/reject` | PosController:100 | Pos.ApproveDiscount |  |  |  | oui |  |
| POST | `/api/v1/pos/manager-override` | PosController:53 | Pos.Sell |  |  | auth | oui |  |
| GET | `/api/v1/pos/me/limits` | PosController:72 | Pos.View |  |  |  | oui |  |
| GET | `/api/v1/pos/offline-profile` | PosController:37 | Pos.Sell |  |  |  | oui |  |
| POST | `/api/v1/pos/park` | PosController:119 | Pos.Sell |  |  |  |  |  |
| GET | `/api/v1/pos/products/scan` | PosController:66 | Pos.Sell |  |  |  | oui |  |
| GET | `/api/v1/pos/products/search` | PosController:61 | Pos.Sell |  |  |  | oui |  |
| GET | `/api/v1/pos/registers` | PosController:197 | Pos.OpenSession |  |  |  | oui |  |
| GET | `/api/v1/pos/return-requests` | PosController:154 | Pos.View |  |  |  | oui |  |
| POST | `/api/v1/pos/return-requests` | PosController:147 | Pos.Return | ✔ |  |  | oui |  |
| GET | `/api/v1/pos/return-requests/{id:guid}` | PosController:159 | Pos.View |  |  |  |  |  |
| POST | `/api/v1/pos/return-requests/{id:guid}/approve` | PosController:164 | Pos.Refund |  |  |  | oui |  |
| POST | `/api/v1/pos/return-requests/{id:guid}/reject` | PosController:169 | Pos.Refund |  |  |  | oui |  |
| POST | `/api/v1/pos/returns` | PosController:133 | Pos.Refund | ✔ |  |  |  |  |
| GET | `/api/v1/pos/sales/lookup` | PosController:142 | Pos.Return |  |  |  | oui |  |
| POST | `/api/v1/pos/sales/{id:guid}/void` | PosController:126 | Pos.Sell |  |  |  | oui |  |
| POST | `/api/v1/pos/sessions/cash` | PosController:179 | Pos.CashInOut |  |  |  |  |  |
| POST | `/api/v1/pos/sessions/close` | PosController:186 | Pos.CloseSession |  |  |  | oui |  |
| GET | `/api/v1/pos/sessions/current` | PosController:192 | Pos.View |  |  |  | oui |  |
| GET | `/api/v1/pos/sessions/open` | PosController:203 | Pos.ManageSessions |  |  |  | oui |  |
| POST | `/api/v1/pos/sessions/open` | PosController:174 | Pos.OpenSession |  |  |  | oui |  |
| GET | `/api/v1/pos/sessions/pending-closings` | PosController:207 | Pos.ManageSessions |  |  |  | oui |  |
| POST | `/api/v1/pos/sessions/{id:guid}/validate-close` | PosController:211 | Pos.ManageSessions |  |  |  | oui |  |
| GET | `/api/v1/pos/tender-options` | PosController:43 | Pos.Sell |  |  |  | oui |  |
| GET | `/api/v1/print/customers/{customerId:guid}/statement` | PrintController:29 | authentifié |  |  |  |  |  |
| GET | `/api/v1/print/suppliers/{supplierId:guid}/documents` | PrintController:42 | authentifié |  |  |  | oui |  |
| GET | `/api/v1/print/suppliers/{supplierId:guid}/statement` | PrintController:36 | authentifié |  |  |  |  |  |
| GET | `/api/v1/print/{kind}/{id:guid}` | PrintController:24 | authentifié |  |  |  |  |  |
| GET | `/api/v1/purchasing/invoices` | PurchasingController:62 | Purchasing.ViewPayables |  |  |  | oui |  |
| POST | `/api/v1/purchasing/invoices` | PurchasingController:103 | Purchasing.Invoice | ✔ |  |  | oui |  |
| GET | `/api/v1/purchasing/invoices/{id:guid}` | PurchasingController:67 | Purchasing.ViewPayables |  |  |  | oui |  |
| POST | `/api/v1/purchasing/invoices/{id:guid}/match` | PurchasingController:109 | Purchasing.Match |  |  |  | oui |  |
| GET | `/api/v1/purchasing/orders` | PurchasingController:21 | Purchasing.View |  |  |  | oui |  |
| POST | `/api/v1/purchasing/orders` | PurchasingController:26 | Purchasing.Order |  |  |  | oui |  |
| GET | `/api/v1/purchasing/orders/{id:guid}` | PurchasingController:31 | Purchasing.View |  |  |  | oui |  |
| PUT | `/api/v1/purchasing/orders/{id:guid}` | PurchasingController:41 | Purchasing.Order |  |  |  | oui |  |
| POST | `/api/v1/purchasing/orders/{id:guid}/approve` | PurchasingController:82 | Purchasing.ApproveOrder |  |  |  | oui |  |
| POST | `/api/v1/purchasing/orders/{id:guid}/cancel` | PurchasingController:92 | Purchasing.Order |  |  |  | oui |  |
| POST | `/api/v1/purchasing/orders/{id:guid}/close` | PurchasingController:47 | Purchasing.Order |  |  |  | oui |  |
| POST | `/api/v1/purchasing/orders/{id:guid}/send` | PurchasingController:87 | Purchasing.Order |  |  |  | oui |  |
| POST | `/api/v1/purchasing/orders/{id:guid}/submit` | PurchasingController:77 | Purchasing.Order |  |  |  | oui |  |
| GET | `/api/v1/purchasing/receipts` | PurchasingController:52 | Purchasing.View |  |  |  | oui |  |
| POST | `/api/v1/purchasing/receipts` | PurchasingController:97 | Purchasing.Receive | ✔ |  |  | oui |  |
| GET | `/api/v1/purchasing/receipts/{id:guid}` | PurchasingController:57 | Purchasing.View |  |  |  | oui |  |
| GET | `/api/v1/purchasing/returns` | PurchasingController:72 | Purchasing.View |  |  |  | oui |  |
| POST | `/api/v1/purchasing/returns` | PurchasingController:114 | Purchasing.Return | ✔ |  |  | oui |  |
| GET | `/api/v1/quotas` | BillingController:55 | Platform.Portal |  |  |  |  |  |
| GET | `/api/v1/quotes` | QuotesController:15 | Documents.View |  |  |  | oui |  |
| POST | `/api/v1/quotes` | QuotesController:25 | Documents.CreateQuote |  |  |  | oui |  |
| GET | `/api/v1/quotes/{id:guid}` | QuotesController:20 | Documents.View |  |  |  | oui |  |
| PUT | `/api/v1/quotes/{id:guid}` | QuotesController:30 | Documents.CreateQuote |  |  |  | oui |  |
| POST | `/api/v1/quotes/{id:guid}/accept` | QuotesController:45 | Documents.CreateQuote |  |  |  | oui |  |
| POST | `/api/v1/quotes/{id:guid}/cancel` | QuotesController:35 | Documents.CreateQuote |  |  |  | oui |  |
| POST | `/api/v1/quotes/{id:guid}/convert` | QuotesController:55 | Pos.ManageOrder |  |  |  | oui |  |
| POST | `/api/v1/quotes/{id:guid}/reject` | QuotesController:50 | Documents.CreateQuote |  |  |  | oui |  |
| POST | `/api/v1/quotes/{id:guid}/send` | QuotesController:40 | Documents.CreateQuote |  |  |  | oui |  |
| POST | `/api/v1/receipts` | DocumentsController:53 | Documents.Issue | ✔ |  |  |  |  |
| GET | `/api/v1/releases` | OperationsController:82 | Operations.View |  |  |  |  |  |
| POST | `/api/v1/releases` | OperationsController:87 | Operations.Manage |  |  |  |  |  |
| GET | `/api/v1/reporting/data-quality` | AnalyticsController:33 | Reporting.DataQuality |  |  |  |  |  |
| GET | `/api/v1/reporting/exports` | ReportsController:87 | Reporting.Export |  |  |  |  |  |
| GET | `/api/v1/reports` | BiController:66 | Reporting.View |  |  |  | oui |  |
| POST | `/api/v1/reports` | BiController:71 | Reporting.View |  |  |  | oui |  |
| GET | `/api/v1/reports/aging/payables` | ReportsController:73 | Reporting.ViewFinancial |  |  |  |  | A-043 (extournes exclues des agrégats) |
| GET | `/api/v1/reports/aging/receivables` | ReportsController:68 | Reporting.ViewFinancial |  |  |  |  | A-043 (extournes exclues des agrégats) |
| GET | `/api/v1/reports/budget-vs-actual` | ReportsController:80 | Reporting.ViewFinancial |  |  |  | oui | A-043 (extournes exclues des agrégats); A-047 (versions de budget additionnées) |
| GET | `/api/v1/reports/executions` | BiController:108 | Reporting.Schedule |  |  |  | oui | A-043 (extournes exclues des agrégats) |
| POST | `/api/v1/reports/export` | ReportsController:91 | Reporting.Export |  |  |  | oui |  |
| GET | `/api/v1/reports/sales/breakdown/{dimension}` | ReportsController:37 | Reporting.View, Reporting.ViewSales |  |  |  | oui | A-043 (extournes exclues des agrégats) |
| GET | `/api/v1/reports/sales/journal` | ReportsController:31 | Reporting.View, Reporting.ViewSales |  |  |  |  | A-043 (extournes exclues des agrégats) |
| GET | `/api/v1/reports/sales/top-products` | ReportsController:43 | Reporting.View, Reporting.ViewSales |  |  |  | oui | A-043 (extournes exclues des agrégats) |
| GET | `/api/v1/reports/schedules` | BiController:95 | Reporting.Schedule |  |  |  | oui | A-043 (extournes exclues des agrégats) |
| POST | `/api/v1/reports/schedules` | BiController:99 | Reporting.Schedule |  |  |  | oui |  |
| DELETE | `/api/v1/reports/schedules/{id:guid}` | BiController:104 | Reporting.Schedule |  |  |  | oui |  |
| GET | `/api/v1/reports/stock/dormant` | ReportsController:61 | Reporting.View |  |  |  |  | A-043 (extournes exclues des agrégats) |
| GET | `/api/v1/reports/stock/ruptures` | ReportsController:56 | Reporting.View |  |  |  |  | A-043 (extournes exclues des agrégats) |
| GET | `/api/v1/reports/stock/valuation` | ReportsController:51 | Reporting.ViewFinancial |  |  |  |  | A-043 (extournes exclues des agrégats) |
| DELETE | `/api/v1/reports/{id:guid}` | BiController:85 | Reporting.View |  |  |  | oui |  |
| GET | `/api/v1/reports/{id:guid}` | BiController:76 | Reporting.View |  |  |  | oui | A-043 (extournes exclues des agrégats) |
| POST | `/api/v1/reports/{id:guid}/run` | BiController:89 | Reporting.View |  |  |  | oui |  |
| GET | `/api/v1/resilience/backups` | ContinuityController:145 | Continuity.View |  |  |  |  |  |
| POST | `/api/v1/resilience/backups/{id:guid}/restore-test` | ContinuityController:153 | Continuity.Manage |  |  |  |  | A-051 (vérification déclarative) |
| POST | `/api/v1/resilience/backups/{id:guid}/verify` | ContinuityController:149 | Continuity.Manage |  |  |  |  | A-051 (vérification déclarative) |
| GET | `/api/v1/resilience/dr-tests` | ContinuityController:112 | Continuity.View |  |  |  | oui |  |
| POST | `/api/v1/resilience/dr-tests` | ContinuityController:117 | Continuity.Manage |  |  |  | oui |  |
| POST | `/api/v1/resilience/dr-tests/{id:guid}/record` | ContinuityController:122 | Continuity.Manage |  |  |  | oui |  |
| GET | `/api/v1/resilience/incidents` | ContinuityController:158 | Continuity.View |  |  |  |  |  |
| POST | `/api/v1/resilience/incidents` | ContinuityController:163 | Continuity.Manage |  |  |  |  |  |
| POST | `/api/v1/resilience/incidents/{id:guid}/lockdown` | ContinuityController:105 | Continuity.Restore |  |  |  |  |  |
| GET | `/api/v1/resilience/runbooks` | ContinuityController:129 | Continuity.View |  |  |  | oui |  |
| GET | `/api/v1/resilience/status` | ContinuityController:141 | Continuity.View |  |  |  |  |  |
| GET | `/api/v1/resilience/status-page` | ContinuityController:135 | Continuity.View |  |  |  | oui |  |
| GET | `/api/v1/restaurant/areas` | RestaurantController:41 | Restaurant.Serve |  |  |  | oui |  |
| POST | `/api/v1/restaurant/areas` | RestaurantController:21 | Restaurant.Manage |  |  |  | oui |  |
| GET | `/api/v1/restaurant/kitchen` | RestaurantController:73 | Restaurant.Kitchen |  |  |  | oui |  |
| POST | `/api/v1/restaurant/kitchen/advance` | RestaurantController:78 | Restaurant.Kitchen |  |  |  | oui |  |
| POST | `/api/v1/restaurant/orders` | RestaurantController:63 | Restaurant.Serve |  |  |  | oui |  |
| POST | `/api/v1/restaurant/orders/cancel` | RestaurantController:50 | Restaurant.Serve |  |  |  | oui |  |
| POST | `/api/v1/restaurant/orders/close` | RestaurantController:83 | Restaurant.Serve | ✔ |  |  | oui |  |
| POST | `/api/v1/restaurant/orders/items` | RestaurantController:45 | Restaurant.Serve |  |  |  | oui |  |
| GET | `/api/v1/restaurant/orders/{saleId:guid}` | RestaurantController:68 | Restaurant.Serve |  |  |  | oui |  |
| GET | `/api/v1/restaurant/recipes` | RestaurantController:55 | Recipes.Manage |  |  |  | oui |  |
| POST | `/api/v1/restaurant/recipes` | RestaurantController:91 | Recipes.Manage |  |  |  | oui |  |
| GET | `/api/v1/restaurant/recipes/options` | RestaurantController:59 | Recipes.Manage |  |  |  | oui |  |
| GET | `/api/v1/restaurant/recipes/{productId:guid}` | RestaurantController:96 | Recipes.Manage |  |  |  | oui |  |
| GET | `/api/v1/restaurant/setup` | RestaurantController:37 | Restaurant.Serve |  |  |  | oui |  |
| GET | `/api/v1/restaurant/tables` | RestaurantController:31 | Restaurant.Serve |  |  |  | oui |  |
| POST | `/api/v1/restaurant/tables` | RestaurantController:26 | Restaurant.Manage |  |  |  | oui |  |
| GET | `/api/v1/restores` | ContinuityController:53 | Continuity.View |  |  |  | oui |  |
| POST | `/api/v1/restores` | ContinuityController:57 | Continuity.Restore |  |  |  | oui |  |
| POST | `/api/v1/restores/{id:guid}/approve` | ContinuityController:62 | Continuity.Restore |  | Deletion |  | oui |  |
| GET | `/api/v1/salary-advances` | SalaryAdvancesController:19 | Hr.ViewEmployees |  |  |  |  |  |
| GET | `/api/v1/salary-advances/{id:guid}` | SalaryAdvancesController:24 | Hr.ViewEmployees |  |  |  | oui |  |
| POST | `/api/v1/salary-advances/{id:guid}/approve` | SalaryAdvancesController:32 | Payroll.AdvanceApprove |  |  |  | oui |  |
| POST | `/api/v1/salary-advances/{id:guid}/cancel` | SalaryAdvancesController:42 | Hr.ViewEmployees |  |  |  |  |  |
| GET | `/api/v1/salary-advances/{id:guid}/history` | SalaryAdvancesController:28 | Hr.ViewEmployees |  |  |  |  |  |
| POST | `/api/v1/salary-advances/{id:guid}/pay` | SalaryAdvancesController:47 | Payroll.AdvancePay | ✔ |  |  | oui | FAIBLE (pas de confirmation forte) |
| POST | `/api/v1/salary-advances/{id:guid}/reject` | SalaryAdvancesController:37 | Payroll.AdvanceApprove |  |  |  | oui |  |
| POST | `/api/v1/salary-advances/{id:guid}/repay` | SalaryAdvancesController:54 | Payroll.AdvancePay | ✔ |  |  | oui |  |
| PUT | `/api/v1/salary-advances/{id:guid}/schedule` | SalaryAdvancesController:60 | Payroll.AdvanceSchedule |  |  |  | oui |  |
| POST | `/api/v1/salary-payments/{id:guid}/cancel` | PayrollController:113 | Payroll.Pay | ✔ | Cancellation |  | oui |  |
| POST | `/api/v1/sales` | SalesController:22 | Pos.Sell | ✔ |  |  |  | A-040 (taxId client) |
| GET | `/api/v1/sales/{id:guid}` | SalesController:29 | Pos.View |  |  |  | oui |  |
| POST | `/api/v1/sales/{id:guid}/cancel` | SalesController:67 | Pos.VoidSale |  |  |  |  |  |
| POST | `/api/v1/sales/{id:guid}/payments` | SalesController:43 | Credit.ReceivePayment | ✔ |  |  |  |  |
| POST | `/api/v1/sales/{id:guid}/returns` | SalesController:61 | Pos.Refund | ✔ |  |  |  |  |
| GET | `/api/v1/saved-filters` | UxPersonalizationController:42 | authentifié |  |  |  | oui |  |
| POST | `/api/v1/saved-filters` | UxPersonalizationController:46 | authentifié |  |  |  | oui |  |
| DELETE | `/api/v1/saved-filters/{id:guid}` | UxPersonalizationController:50 | authentifié |  |  |  | oui |  |
| GET | `/api/v1/search` | ExperienceController:18 | authentifié |  |  |  | oui |  |
| GET | `/api/v1/sector-profiles` | SectorProfilesController:19 | Sectors.Configure |  |  |  | oui |  |
| GET | `/api/v1/sector-profiles/{key}` | SectorProfilesController:24 | Sectors.Configure |  |  |  |  |  |
| POST | `/api/v1/sector-profiles/{key}/apply` | SectorProfilesController:34 | Config.ApplyProfile |  |  |  | oui |  |
| POST | `/api/v1/sector-profiles/{key}/preview` | SectorProfilesController:29 | Sectors.Configure |  |  |  | oui |  |
| PUT | `/api/v1/sectors/activities` | SectorsController:26 | Sectors.Configure |  |  |  | oui |  |
| GET | `/api/v1/sectors/config` | SectorsController:21 | Sectors.Configure |  |  |  | oui |  |
| POST | `/api/v1/sectors/custom-fields` | SectorsController:43 | Sectors.CustomFields |  |  |  | oui |  |
| PUT | `/api/v1/sectors/custom-fields/values` | SectorsController:53 | Sectors.Configure |  |  |  | oui |  |
| GET | `/api/v1/sectors/custom-fields/values/{entityType}/{entityId:guid}` | SectorsController:58 | Sectors.Configure |  |  |  |  |  |
| GET | `/api/v1/sectors/custom-fields/{entityType}` | SectorsController:48 | Sectors.Configure |  |  |  | oui |  |
| PUT | `/api/v1/sectors/modules` | SectorsController:31 | Sectors.Configure |  |  |  | oui |  |
| PUT | `/api/v1/sectors/terminology` | SectorsController:36 | Sectors.Terminology |  |  |  |  |  |
| GET | `/api/v1/security/approvals` | SecurityController:72 | Security.Approvals |  |  |  | oui |  |
| POST | `/api/v1/security/approvals` | SecurityController:67 | Security.Approvals |  |  |  | oui |  |
| GET | `/api/v1/security/approvals/{requestId:guid}` | SecurityController:77 | Security.Approvals |  |  |  |  |  |
| POST | `/api/v1/security/approvals/{requestId:guid}/decide` | SecurityController:82 | Security.Approvals |  |  |  | oui |  |
| GET | `/api/v1/security/audit` | SecurityController:33 | Security.AuditView |  |  |  | oui | A-034 (toutes les entreprises du jeton) |
| POST | `/api/v1/security/audit/export` | SecurityController:39 | Security.AuditView |  |  |  |  | A-034 (toutes les entreprises du jeton) |
| GET | `/api/v1/security/certificates` | SecurityController:168 | Platform.Security |  |  |  | oui |  |
| POST | `/api/v1/security/certificates` | SecurityController:172 | Platform.Security |  |  |  | oui |  |
| POST | `/api/v1/security/certificates/{id:guid}/rotate` | SecurityController:177 | Platform.Security |  |  |  | oui |  |
| GET | `/api/v1/security/compliance` | SecurityController:184 | Platform.Security |  |  |  | oui |  |
| POST | `/api/v1/security/compliance/{id:guid}/review` | SecurityController:189 | Platform.Security |  |  |  | oui |  |
| GET | `/api/v1/security/dashboard` | SecurityController:224 | Security.Configure |  |  |  | oui |  |
| GET | `/api/v1/security/devices` | SecurityController:126 | authentifié |  |  |  |  |  |
| POST | `/api/v1/security/devices/{sessionId:guid}/revoke` | SecurityController:129 | authentifié |  |  |  |  |  |
| POST | `/api/v1/security/devices/{sessionId:guid}/trust` | SecurityController:133 | authentifié |  |  |  | oui |  |
| GET | `/api/v1/security/emergency-access` | SecurityController:196 | Security.RequestEmergencyAccess |  |  |  | oui |  |
| POST | `/api/v1/security/emergency-access` | SecurityController:201 | Security.RequestEmergencyAccess |  | PermissionChange |  | oui |  |
| POST | `/api/v1/security/emergency-access/{id:guid}/decide` | SecurityController:207 | Security.ApproveEmergencyAccess |  | PermissionChange |  | oui |  |
| POST | `/api/v1/security/emergency-access/{id:guid}/revoke` | SecurityController:213 | Security.ApproveEmergencyAccess |  |  |  | oui | A-035 (révocation sans effet) |
| POST | `/api/v1/security/fraud/scan` | SecurityController:50 | Security.FraudReview |  |  |  | oui |  |
| GET | `/api/v1/security/fraud/signals` | SecurityController:55 | Security.FraudReview |  |  |  | oui |  |
| POST | `/api/v1/security/fraud/signals/{signalId:guid}/review` | SecurityController:60 | Security.FraudReview |  |  |  | oui |  |
| GET | `/api/v1/security/incidents` | ContinuityController:76 | Continuity.View |  |  |  |  |  |
| POST | `/api/v1/security/incidents` | ContinuityController:83 | Continuity.Manage |  |  |  |  |  |
| GET | `/api/v1/security/mfa` | AuthMfaController:17 | authentifié |  |  |  |  |  |
| POST | `/api/v1/security/mfa/disable` | AuthMfaController:32 | authentifié |  |  |  |  |  |
| POST | `/api/v1/security/mfa/enroll` | AuthMfaController:22 | authentifié |  |  |  |  |  |
| GET | `/api/v1/security/policy` | SecurityController:139 | Security.Configure |  |  |  | oui |  |
| PUT | `/api/v1/security/policy` | SecurityController:144 | Security.Configure |  |  |  | oui |  |
| GET | `/api/v1/security/score` | SecurityController:220 | Security.Configure |  |  |  | oui |  |
| GET | `/api/v1/security/sessions` | SecurityController:112 | authentifié |  |  |  | oui |  |
| POST | `/api/v1/security/sessions/revoke-all` | SecurityController:120 | authentifié |  |  |  | oui |  |
| POST | `/api/v1/security/sessions/{sessionId:guid}/revoke` | SecurityController:116 | authentifié |  |  |  | oui |  |
| POST | `/api/v1/security/step-up` | SecurityController:89 | authentifié |  |  | refresh | oui |  |
| POST | `/api/v1/security/step-up/verify` | SecurityController:106 | authentifié |  |  |  |  |  |
| GET | `/api/v1/security/vulnerabilities` | SecurityController:151 | Platform.Security |  |  |  | oui |  |
| POST | `/api/v1/security/vulnerabilities` | SecurityController:156 | Platform.Security |  |  |  | oui |  |
| POST | `/api/v1/security/vulnerabilities/{id:guid}/status` | SecurityController:161 | Platform.Security |  |  |  | oui |  |
| POST | `/api/v1/setup/database/test` | SetupController:53 | anonyme |  |  | auth | oui |  |
| POST | `/api/v1/setup/install` | SetupController:63 | anonyme |  |  | auth | oui |  |
| GET | `/api/v1/setup/options` | SetupController:35 | anonyme |  |  |  | oui |  |
| GET | `/api/v1/setup/status` | SetupController:27 | anonyme |  |  |  | oui |  |
| POST | `/api/v1/setup/verify-token` | SetupController:44 | anonyme |  |  | auth | oui |  |
| GET | `/api/v1/shared/{token}` | GedController:147 | anonyme |  |  | public |  |  |
| POST | `/api/v1/shared/{token}` | GedController:154 | anonyme |  |  | public |  |  |
| GET | `/api/v1/shortcuts` | UxPersonalizationController:55 | authentifié |  |  |  | oui |  |
| POST | `/api/v1/shortcuts` | UxPersonalizationController:58 | authentifié |  |  |  | oui |  |
| DELETE | `/api/v1/shortcuts/{id:guid}` | UxPersonalizationController:62 | authentifié |  |  |  | oui |  |
| GET | `/api/v1/signature-candidates` | GedController:231 | Ged.Sign |  |  |  | oui |  |
| GET | `/api/v1/signature-requests` | GedController:224 | Ged.View |  |  |  | oui |  |
| GET | `/api/v1/signature-requests/{id:guid}` | GedController:235 | Ged.View |  |  |  |  |  |
| POST | `/api/v1/signature-requests/{id:guid}/cancel` | GedController:249 | Ged.Sign |  |  |  | oui |  |
| POST | `/api/v1/signature-requests/{id:guid}/signers/{signerId:guid}/decline` | GedController:244 | Ged.Sign |  |  |  | oui |  |
| POST | `/api/v1/signature-requests/{id:guid}/signers/{signerId:guid}/sign` | GedController:239 | Ged.Sign |  |  |  | oui |  |
| GET | `/api/v1/stock/alerts` | StockController:54 | Inventory.View |  |  |  | oui |  |
| GET | `/api/v1/stock/balances` | StockController:28 | Inventory.View |  |  |  | oui |  |
| POST | `/api/v1/stock/expire-lots` | StockController:73 | Inventory.WriteOff | ✔ |  |  | oui |  |
| GET | `/api/v1/stock/lots` | StockController:42 | Inventory.View |  |  |  | oui |  |
| GET | `/api/v1/stock/lots/expiring` | StockController:49 | Inventory.View |  |  |  | oui |  |
| POST | `/api/v1/stock/lots/{id:guid}/block` | StockController:58 | Inventory.ManageLot |  |  |  | oui |  |
| POST | `/api/v1/stock/lots/{id:guid}/recall` | StockController:68 | Inventory.ManageLot |  |  |  | oui |  |
| POST | `/api/v1/stock/lots/{id:guid}/unblock` | StockController:63 | Inventory.ManageLot |  |  |  | oui |  |
| GET | `/api/v1/stock/movements` | StockController:34 | Inventory.View |  |  |  | oui |  |
| GET | `/api/v1/stock/overview` | StockController:21 | Inventory.View |  |  |  | oui |  |
| GET | `/api/v1/subscription` | SubscriptionsController:26 | Platform.Portal |  |  |  | oui |  |
| GET | `/api/v1/subscriptions` | SubscriptionsController:35 | Platform.Portal |  |  |  |  |  |
| POST | `/api/v1/subscriptions/{id:guid}/addons` | SubscriptionsController:50 | Platform.Portal |  |  |  |  |  |
| POST | `/api/v1/subscriptions/{id:guid}/cancel` | SubscriptionsController:54 | Platform.Portal |  |  |  | oui |  |
| POST | `/api/v1/subscriptions/{id:guid}/downgrade` | SubscriptionsController:46 | Platform.Portal |  |  |  | oui |  |
| GET | `/api/v1/subscriptions/{id:guid}/preview-plan/{planId:guid}` | SubscriptionsController:38 | Platform.Portal |  |  |  | oui |  |
| POST | `/api/v1/subscriptions/{id:guid}/reactivate` | SubscriptionsController:58 | Platform.Portal |  |  |  | oui |  |
| POST | `/api/v1/subscriptions/{id:guid}/upgrade` | SubscriptionsController:42 | Platform.Portal |  |  |  | oui |  |
| GET | `/api/v1/suppliers` | SuppliersController:176 | Purchasing.View |  |  |  | oui |  |
| POST | `/api/v1/suppliers` | SuppliersController:185 | Suppliers.Create |  |  |  | oui |  |
| GET | `/api/v1/suppliers/payment-terms` | SuppliersController:167 | Suppliers.View |  |  |  | oui |  |
| POST | `/api/v1/suppliers/payment-terms` | SuppliersController:171 | Suppliers.Update |  |  |  | oui |  |
| GET | `/api/v1/suppliers/{id:guid}` | SuppliersController:180 | Suppliers.View |  |  |  | oui |  |
| PUT | `/api/v1/suppliers/{id:guid}` | SuppliersController:190 | Suppliers.Update |  |  |  | oui |  |
| GET | `/api/v1/suppliers/{id:guid}/overview` | SuppliersController:156 | Suppliers.View |  |  |  | oui |  |
| GET | `/api/v1/suppliers/{id:guid}/statement` | SuppliersController:195 | Purchasing.ViewPayables |  |  |  | oui |  |
| PUT | `/api/v1/suppliers/{id:guid}/terms` | SuppliersController:162 | Suppliers.Update |  |  |  | oui |  |
| GET | `/api/v1/support/tickets` | PlatformSupportController:18 | Platform.Portal |  |  |  |  |  |
| POST | `/api/v1/support/tickets` | PlatformSupportController:28 | Platform.Portal |  |  |  |  |  |
| GET | `/api/v1/support/tickets/{id:guid}` | PlatformSupportController:23 | Platform.Portal |  |  |  |  |  |
| POST | `/api/v1/support/tickets/{id:guid}/reply` | PlatformSupportController:36 | Platform.Portal |  |  |  |  |  |
| POST | `/api/v1/sync/batch` | SyncController:24 | Sync.View |  |  |  | oui | A-003 résidu (session close, appareil auto-enrôlé) ; 500 sur date non UTC |
| GET | `/api/v1/sync/center` | SyncController:31 | Sync.View |  |  |  | oui |  |
| POST | `/api/v1/sync/conflicts/{conflictId:guid}/resolve` | SyncController:36 | Sync.Manage |  |  |  | oui |  |
| GET | `/api/v1/sync/devices` | SyncController:43 | Sync.View |  |  |  |  |  |
| POST | `/api/v1/sync/devices` | SyncController:49 | Sync.Devices |  |  |  |  | A-003 résidu (auto-enrôlement) |
| POST | `/api/v1/sync/devices/{deviceId:guid}/heartbeat` | SyncController:61 | Sync.View |  |  |  |  |  |
| POST | `/api/v1/sync/devices/{deviceId:guid}/push-token` | SyncController:67 | Sync.Devices |  |  |  |  |  |
| POST | `/api/v1/sync/devices/{deviceId:guid}/revoke` | SyncController:55 | Sync.Devices |  |  |  | oui |  |
| GET | `/api/v1/sync/offline-policy` | SyncController:74 | Sync.View |  |  |  | oui |  |
| PUT | `/api/v1/sync/offline-policy` | SyncController:79 | Offline.Configure |  |  |  | oui |  |
| GET | `/api/v1/system/status` | OperationsController:60 | Operations.View |  |  |  | oui |  |
| POST | `/api/v1/treasury/charges` | FinancialAccountsController:71 | Finance.Reconcile | ✔ |  |  | oui |  |
| GET | `/api/v1/treasury/liquidity` | TreasuryController:25 | Finance.View |  |  |  | oui | A-046 (devises additionnées) |
| GET | `/api/v1/treasury/position` | TreasuryController:20 | Finance.View |  |  |  | oui | A-046 (devises additionnées) |
| GET | `/api/v1/treasury/suspense` | TreasuryController:32 | Finance.Reconcile |  |  |  | oui |  |
| POST | `/api/v1/treasury/suspense` | TreasuryController:37 | Finance.ManageSuspense |  |  |  | oui |  |
| POST | `/api/v1/treasury/suspense/{id:guid}/resolve` | TreasuryController:42 | Finance.ManageSuspense |  |  |  | oui |  |
| POST | `/api/v1/treasury/transfers` | FinancialAccountsController:65 | Finance.Transfer | ✔ |  |  | oui |  |
| GET | `/api/v1/trial-balance` | FinancialStatementsController:27 | Accounting.View |  |  |  | oui | A-043 (extournes exclues des agrégats) |
| GET | `/api/v1/ui-preferences` | PreferencesController:22 | authentifié |  |  |  |  |  |
| PUT | `/api/v1/ui-preferences` | PreferencesController:27 | authentifié |  |  |  |  |  |
| GET | `/api/v1/usage` | BillingController:51 | Platform.Portal |  |  |  | oui |  |
| GET | `/api/v1/user-preferences` | PreferencesController:22 | authentifié |  |  |  | oui |  |
| PUT | `/api/v1/user-preferences` | PreferencesController:27 | authentifié |  |  |  | oui |  |
| GET | `/api/v1/users` | UsersController:20 | Admin.ManageUsers |  |  |  | oui | A-033 (contrôle sur l'entreprise active seulement) |
| POST | `/api/v1/users` | UsersController:26 | Admin.ManageUsers |  | PermissionChange |  | oui | A-033 (contrôle sur l'entreprise active seulement) |
| POST | `/api/v1/users/assignments/{assignmentId:guid}/revoke` | UsersController:43 | Admin.ManageUsers |  | PermissionChange |  | oui | A-033 (contrôle sur l'entreprise active seulement) |
| GET | `/api/v1/users/{id:guid}/assignments` | UsersController:32 | Admin.ManageUsers |  |  |  | oui | A-033 (contrôle sur l'entreprise active seulement) |
| POST | `/api/v1/users/{id:guid}/assignments` | UsersController:37 | Admin.ManageUsers |  | PermissionChange |  | oui | A-033 (contrôle sur l'entreprise active seulement) |
| POST | `/api/v1/users/{id:guid}/mfa/reset` | UsersController:55 | Security.ResetMfa |  |  |  | oui | A-033 (contrôle sur l'entreprise active seulement) |
| POST | `/api/v1/users/{id:guid}/status` | UsersController:49 | Admin.ManageUsers |  |  |  | oui | A-033 (contrôle sur l'entreprise active seulement) |
| GET | `/api/v1/warehouses` | WarehouseController:21 | Inventory.View |  |  |  | oui |  |
| POST | `/api/v1/warehouses` | WarehouseController:25 | Inventory.ManageWarehouse |  |  |  | oui |  |
| GET | `/api/v1/warehouses/locations` | WarehouseController:30 | Inventory.View |  |  |  | oui |  |
| POST | `/api/v1/warehouses/locations` | WarehouseController:35 | Inventory.ManageWarehouse |  |  |  | oui |  |
| GET | `/api/v1/warehouses/sites` | WarehouseController:17 | Inventory.View |  |  |  | oui |  |
| GET | `/api/v1/webhooks` | MarketplaceController:105 | Integrations.Webhooks |  |  |  |  |  |
| POST | `/api/v1/webhooks` | MarketplaceController:109 | Integrations.Webhooks |  |  |  |  |  |
