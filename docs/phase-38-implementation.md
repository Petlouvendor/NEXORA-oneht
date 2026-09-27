# Phase 38 — Administration SaaS & Monétisation — implémentation

**Statut : livré et vérifié.** Migration `V051` (17 tables plateforme + 7 rôles + 11 permissions).
10 tests d'intégration (`PlatformBillingTests`), 4 tests API (`PlatformEndpointsTests`),
3 tests frontend (`platformApi.test.ts`).
**268 tests backend** verts (18 unit + 19 API + 231 intégration), Debug **et** Release, 0 warning.
60 tests frontend. `tsc` + `vite build` verts.

NEXORA devient une **plateforme** qui héberge et vend le produit à des entreprises clientes. La
couche plateforme est **commerciale et transverse** : la facturation de NEXORA est distincte de la
comptabilité des clients (§2, §43-44), l'isolation des **données métier** des clients reste assurée
par la RLS des phases 2-37.

### Socle réutilisé

- **Phase 3** — API `/api/v1` versionnée, deny-by-default, JWT + MFA (Phase 33) pour les admins (§53)
- **Phase 11** — `audit_log` immuable (trigger) — sert aussi l'audit billing (§48)
- **Phase 12** — couche de paiement remplaçable, webhooks HMAC idempotents (modèle pour §15, §54)
- **Phase 34** — `INotificationService` : les notifications SaaS (§47) passent par le même moteur
- **Phase 35** — `api_key` : « secret haché, jamais ré-affiché » repris pour les jetons de licence offline (§28)
- **Phase 37** — `deployment` : précédent d'une **table plateforme** sans RLS entreprise, gérée par permissions

---

## Correspondance cahier des charges → implémentation

| § | Exigence | Où |
|---|---|---|
| 1-2 | `PLATEFORME → CLIENT → ABONNEMENT → PLAN → LICENCES/QUOTAS → ENTREPRISES → UTILISATEURS → FACTURATION` ; facturation NEXORA distincte ; données clients isolées ; les plans ne cassent pas les données | Chaîne complète : `platform_customer` → `platform_customer_company` → `subscription` → `platform_plan`+`subscription_addon` → `platform_license`/`usage_counter` → entreprises (RLS Phases 2-37). Un downgrade **désactive les excédents sans supprimer** (§18) |
| 3-6 | Modèle commercial (essai, mensuel, annuel, sur-mesure, add-ons, par entreprise/utilisateur/terminal) ; plans (Starter/Business/Professional/Enterprise/sur-mesure) ; paramètres de plan ; modules inclus/optionnels/indisponibles + politique de downgrade | `platform_plan` (`tier` starter…custom, `period` monthly/annual, `price_amount`, `trial_days`, `quotas` jsonb 11 ressources, `features` jsonb `{module: included\|optional\|unavailable}`) ; `platform_addon` (`kind`, `quota_key`+`quota_delta`) ; `IPlatformCatalogService` |
| 7-9 | Compte client (contacts, adresse, pays, devise, statut, identifiant) ; tenant (isolation, entreprises, abonnements, factures, usage, audit) ; une organisation possède 1..n entreprises | `platform_customer` (`customer_ref` « NXC-000123 », `country_code`, `billing_currency`, `tax_id`, `tax_exempt`, `status` active/suspended/closed) ; `platform_customer_company` (`UNIQUE(company_id)` — une entreprise = un client) ; `ICustomerAccountService` |
| 10-11 | États d'abonnement (actif, essai, attente paiement, retard, suspendu, annulé, expiré, renouvellement programmé) ; cycle `ESSAI → ABONNEMENT → RENOUVELLEMENT → PAIEMENT → ACTIF → … → ÉCHEC → GRÂCE → SUSPENSION` | `subscription.status` ∈ trialing/active/past_due/grace/suspended/canceled/expired ; **`ISubscriptionService`** + `RunDueAsync` (planificateur) : essais expirés → past_due + facture, renouvellements dus → facture + past_due + grâce, grâce échue → suspension, annulation programmée échue → canceled |
| 12 | Essai gratuit (durée, limites, dates, conversion, rappels, expiration) | `CreateAsync(StartTrial)` → `trialing` + `trial_ends_at` + `TrialStarted` ; `RunDueAsync` : `TrialEndingSoon` (J-3, notif §23), `TrialExpired` → première facture + fenêtre de grâce |
| 13-14 | Facture NEXORA / avoir / reçu / échéance / taxe / remise / **prorata** / crédit client / numérotation / PDF / historique ; prorata (upgrade, downgrade, ajout utilisateur/entreprise/option, ligne d'ajustement, calcul transparent) | `platform_invoice` (`number` « NXR-2026-000042 » via `platform_invoice_seq`, `kind` invoice/credit_note/receipt, `parent_invoice_id`, taxe/remise) + `platform_invoice_line` (`line_type` plan/addon/**proration**/usage/discount/tax) ; **`Proration.Adjustment`** = `(prixCible − prixActuel) × fraction de période restante`, arrondi 2 déc. ; upgrade → facture d'ajustement, downgrade → **avoir** (§13 crédit client). PDF/rendu : backlog (le contenu figé est en base) |
| 15-16, 54 | Paiements NEXORA (carte/virement/mobile money/manuel validé par admin/référence externe/rapprochement, **couche interchangeable**) ; échec paiement (tentative, retry, notif, grâce, restrictions, suspension, réactivation) ; **résilience billing** (webhook idempotent, réconciliation quotidienne, retry, dead-letter, état explicite, **aucune suspension sur un événement ambigu**, revue manuelle) | `platform_payment` (`method`, `provider`/`provider_ref`, **`provider_event_id` UNIQUE** → idempotence, `status` pending/succeeded/failed/refunded, `review_required`, `is_manual`) ; **`HandlePaymentEventAsync`** : `EventId` déjà vu → renvoie le paiement existant sans effet de bord ; `outcome='ambiguous'` **ou** client non résolu → `review_required=true`, **jamais** de changement d'état d'abonnement (§54) ; `succeeded` → facture payée + `ActivateAsync` ; `failed` → `payment_failures++`, `past_due`, grâce posée, **jamais** de suspension ici. `RecordManualPaymentAsync` + `ConfirmPaymentAsync` (perm `platform.record_payment` séparée §36). `ReconcileAsync` (planificateur) : factures échues → `overdue`, alerte staff sur la file de revue |
| 17 | Période de grâce (durée configurable, services maintenus/limités, notifications, date limite, suspension automatique) | `subscription.grace_ends_at` (défaut 7 j — `GraceDays`, configurable en exploitation) ; `RunDueAsync` suspend **uniquement** sur échéance ferme de la grâce |
| 18 | **Downgrade** : vérification quotas, fonctionnalités affectées, **prévisualisation impact**, confirmation, plan inférieur, **données conservées**, fonctionnalités excédentaires inactives sans suppression | **`PreviewPlanChangeAsync`** → `PlanChangeImpactDto` (`direction`, `prorationAmount`, **`quotaBreaches`** [ressource, nouvelle limite, usage actuel], **`featuresLost`**, `summary`). `DowngradeAsync` **applique quand même** (les nouveaux quotas s'appliquent aux créations futures ; `usage_counter` intact — testé) + avoir au prorata |
| 19 | **Upgrade** : nouveau plan, activation immédiate au paiement confirmé, prorata, nouveaux quotas, facture d'ajustement | `UpgradeAsync` : bascule `plan_id`/`price_amount`, facture `proration` positive, `SubscriptionUpgraded`. Routes séparées upgrade/downgrade → un « mauvais sens » est refusé (`platform.wrong_direction`) |
| 20 | Add-ons (utilisateurs/entreprises/stockage/API/connecteurs/terminaux/support/fonctionnalités spécialisées) | `platform_addon` + `subscription_addon` (`quantity`, prix figé) ; `SetAddonAsync` (ajout/modif/retrait `removed_at`, facture prorata sur le delta). Quotas effectifs = plan + surcharge + Σ `quota_delta × qty` |
| 21-24 | Quotas (11 ressources) ; usage (conso, limite, %, prévision, historique, alertes, dépassement, top consommateurs) ; alertes commerciales (80/90/100 %, essai, échéance, paiement échoué, carte, renouvellement) ; facturation par usage (compteur/unité/prix/seuil/plafond) | **`usage_counter`** (customer, resource, `period_start` mensuel, `value`, `last_alert_pct`) ; **`IQuotaService`** : `GetReportAsync` (lignes limite/utilisé/%/dépassé), `CheckAsync(resource, requested)` → `allowed` + message, `RecordUsageAsync` (upsert atomique + alerte à 80/90/100 % → `QuotaThresholdReached` + notification §23). Facturation à l'usage : `line_type='usage'` prévu (pas d'agrégateur temps-réel — backlog) |
| 25-26 | Free/Starter (onboarding simple) ; Enterprise (contrat, tarification perso, facturation sur facture, SSO, SLA) | `platform_plan.tier` + `subscription.price_override`/`quotas_override` (négociation Enterprise §26). SSO : hors périmètre |
| 27-29 | Licence (organisation/entreprise/utilisateur/terminal/module ; expiration/activation/révocation/renouvellement) ; **licence offline** (jeton signé, validité limitée, renouvellement périodique, grâce, vérification locale, anti-altération, pas de dépendance permanente à Internet) ; activation `ABONNEMENT PAYÉ → LICENCE ACTIVE → MODULES/QUOTAS → ENTREPRISES` | `platform_license` (`scope`, `subject_ref`, `status`, `grace_days`, `expires_at`, `token_hash`) ; **`LicenseService`** : `IssueOfflineTokenAsync` → `base64url(payload).base64url(HMAC-SHA256)` (clé domaine-séparée de celle des JWT), payload `{lid, cid, scope, iat, exp, hardExp}` ; `VerifyOfflineTokenAsync` = **vérification locale** (`FixedTimeEquals` sur la signature, `now ≤ hardExp`, `inGracePeriod = now > exp`), révocation vérifiée si la plateforme est joignable. L'abonnement (dé)clenche `EnsureLicenseAsync` (créée/prolongée à l'activation, révoquée à l'annulation immédiate) |
| 30-33 | Suspension (motif, date, périmètre, avertissement, accès limité, réactivation) ; annulation (immédiate ou fin de période, remboursement, export, conservation) ; portabilité (§32) ; réactivation (paiement confirmé, licence réactivée, restauration accès, audit) | `SuspendAsync` (staff, motif obligatoire) ; `CancelAsync` (`Immediate` → `canceled` + licences révoquées ; sinon `cancel_at_period_end`) ; `ReactivateAsync` (dans la période → `active` ; sinon `past_due` + facture). Export des données métier (§32) : **backlog** (les données restent, non supprimées) |
| 34-36 | Gestion plateforme (dashboard SaaS, clients, abonnements, paiements, factures, usage, licences, incidents, support) ; **rôles NEXORA** (super admin, billing, support, finance, sécurité, opérateur, lecture seule, admin client) ; **séparation des responsabilités** (support ≠ paiements, billing ≠ données métier, sécurité ≠ données métier automatiques, double contrôle) | **7 rôles plateforme** (`platform_admin/billing/support/finance/security/operator/readonly`) + **11 permissions** `platform.*`. Séparation : `platform_support` a `support`/`support_access` mais **pas** `manage_billing` ; `platform_billing` a `record_payment` mais **pas** `support_access` ; `platform_security` n'a que `view`+`security`. Les rôles **locataires** ne reçoivent que `platform.portal` (`owner_direction`, `group_administrator`) |
| 37-38 | Support (tickets, priorité P1..P4, SLA, client, entreprise, historique, escalade, notes internes) ; **support assisté** (demande d'accès temporaire, consentement, périmètre & durée limités, bannière, audit, révocation automatique) | `support_ticket` (`ticket_ref` « NXS-000123 », `priority` p1..p4, `escalated`) + `support_ticket_message` (`is_internal` — un client ne pose jamais de note interne) ; **`support_access_grant`** (`staff_user_id`, `reason`, `scope` read/support_write, `requested_by` = consentement, `expires_at`, `revoked_at`). L'**application effective** du grant dans la RLS : backlog (le registre + audit sont livrés) |
| 39-40 | Catalogue (plans, modules, add-ons, connecteurs, services, support, offres promo, codes promo) ; promotions (coupon, %/montant, durée, premier cycle, nb utilisations, éligibilité, expiration, audit) | `platform_coupon` (`kind` percent/fixed, `duration` once/first_period/forever, `max_redemptions`/`redeemed_count`, `expires_at`) ; `ResolveCouponAsync` applique la remise sur la première facture + incrémente le compteur |
| 41-44 | Taxes & facturation (adresse, pays, devise, taxe applicable, numéro fiscal, exonération, lignes, historique) ; devise (prix/facture/paiement, taux, arrondi, historique) ; **comptabilité NEXORA** (créances, produits, taxes, paiements, remboursements, avoirs, revenue recognition, rapprochement) ; **événements de facturation séparés des écritures** (corrections/prorata/avoirs sans réécrire l'historique) | `platform_customer.tax_exempt` + `platform_invoice.tax_rate`/`tax_amount` (taux par défaut **0** — fiscalité par pays = configuration exploitation §41) ; `currency_code` par plan/abonnement/facture, propre à NEXORA (§42) ; **`billing_event`** = ledger d'événements (`TrialStarted`, `SubscriptionUpgraded`, `PaymentFailed`, `InvoicePaid`…) **distinct** des factures et des écritures comptables (§44). Revenue recognition / rapprochement comptable NEXORA : backlog (le ledger fournit la matière première) |
| 45-46 | **Portail client** (abonnement, plan, factures, paiements, méthodes de paiement, utilisateurs, entreprises, quotas, usage, support, exports, sécurité) ; **self-service** (upgrade, ajout utilisateur/entreprise selon quota, ajout option, téléchargement facture, paiement, renouvellement, annulation, gestion contacts) | Routes **non préfixées** self-scopées via `IPlatformScope` (résout l'organisation à partir de l'entreprise active) : `GET /api/v1/subscription`, `POST /api/v1/subscriptions/{id}/{upgrade,downgrade,cancel,reactivate,addons}` (avec `restrictToCustomer` → un client ne touche que son abonnement), `GET /api/v1/billing/{invoices,payments,payment-methods}`, `GET /api/v1/{usage,quotas}`, `GET /api/v1/licenses` + `/{id}/token`. Écran **`BillingPortalPage`** |
| 47 | Notifications SaaS (bienvenue, essai, facture, paiement, échéance, quota, suspension, réactivation, maintenance, incident) | `NotifyCustomerAsync` (résout les utilisateurs de l'organisation → `INotificationService`, best-effort) sur essai finissant, paiement échoué, suspension, réactivation, annulation, seuil de quota ; `AlertPlatformBillingAsync` sur événement ambigu / file de revue. `billing_event` reste la trace fiable |
| 48 | Audit billing (plan/prix/coupon/facture/paiement/remboursement/avoir/suspension/réactivation/quota/licence/accès support) | `trg_audit_*` sur les 14 tables plateforme → `audit_log` immuable (Phase 11) ; `billing_event` en complément fonctionnel |
| 49 | Anti-fraude (paiements suspects, tentatives répétées, usage anormal, création massive de comptes, abus de coupons, API abuse, blocage contrôlé, revue manuelle) | Permission `platform.security` + la file `review_required` (§54) sert de point d'entrée à la revue. Moteur de scoring anti-fraude dédié : **backlog** (le socle Phase 11 `fraud_rule`/`fraud_signal` est réutilisable) |
| 50 | API principales | `GET/POST /api/v1/platform/{customers,plans,addons,coupons}` ; `POST /api/v1/platform/customers/{id}/subscription` ; `POST /api/v1/platform/subscriptions/{id}/{activate,upgrade,downgrade,addons,cancel,reactivate,suspend}` ; `GET/POST /api/v1/subscriptions/*` (portail) ; `GET /api/v1/billing/{invoices,payments,payment-methods}` + `POST .../payment-methods` + `POST /api/v1/billing/webhooks/payment` `[AllowAnonymous]` signé ; `GET /api/v1/{usage,quotas}` ; `GET /api/v1/licenses` + `POST /api/v1/licenses/{id}/{token,revoke}` + `POST /api/v1/licenses/verify` `[AllowAnonymous]` ; `GET/POST /api/v1/support/tickets` + `POST .../{id}/reply` + `PATCH /api/v1/platform/support/tickets/{id}` ; `GET /api/v1/platform/metrics` |
| 51 | Événements | `PlatformEvents` (17 constantes) journalisés dans `billing_event` (`TrialStarted`, `TrialEndingSoon`, `TrialExpired`, `SubscriptionCreated/Upgraded/Downgraded/Suspended/Cancelled/Reactivated`, `PaymentSucceeded/Failed`, `InvoiceIssued/Paid/Overdue`, `QuotaThresholdReached`, `LicenseActivated/Revoked`) |
| 52 | Tests | `PlatformBillingTests` (10) — cf. ci-dessous |
| 53 | Sécurité (isolation clients, RBAC plateforme, MFA admins, secrets paiement, tokenisation PSP, aucune donnée carte, audit, double contrôle, protection API, rate limiting) | RLS Phases 2-37 (données métier) + permissions `platform.*` (données plateforme) ; MFA Phase 33 s'applique aux admins ; `platform_payment_method` ne stocke **que** le jeton PSP (`provider_token`) — `CHECK last4 ~ '^[0-9]{4}$'`, jamais le PAN ; webhook signé (jeton partagé `X-Platform-Webhook-Token`, HMAC par provider = évolution production) ; rate limiter Phase 3 |
| 55 | Métriques SaaS (MRR, ARR, churn, activation, conversion essai, ARPU, LTV, usage, dépassement quota, taux paiement OK, DSO, tickets, disponibilité) — **≠ KPI des clients** | **`ISaasMetricsService`** : MRR (Σ prix actifs, annuel/12), ARR, ARPU, churn 30 j, conversion d'essai, taux de paiement réussi 30 j, abonnements par état, tickets ouverts, paiements à revoir. LTV / DSO / disponibilité : backlog. Écran **`PlatformConsolePage`** |
| 56 | Critères de sortie | Comptes clients, tenant, plans, abonnements, essai, facturation, prorata, paiements, échecs, grâce, upgrade, downgrade, add-ons, quotas, usage, licences, licence offline, suspension, annulation, réactivation, administration, rôles, séparation des responsabilités, support, catalogue, promotions, taxes, devises, portail, self-service, notifications, audit, API, événements, tests, sécurité, résilience, métriques : **livrés**. Portabilité outillée, comptabilité NEXORA, revenue recognition, anti-fraude scoré, RLS effective des grants support, facturation à l'usage agrégée : **backlog** |
| 57 | Étape suivante — Phase 39 (Migration & onboarding avancé) | hors périmètre |

## `V051` — contenu (17 tables plateforme, sans RLS entreprise)

- **Catalogue** — `platform_plan` (`tier`, `period`, `price_amount`, `trial_days`, `quotas` jsonb,
  `features` jsonb), `platform_addon` (`kind`, `quota_key`/`quota_delta`), `platform_coupon`
  (`kind` percent/fixed, `duration`, `max_redemptions`).
- **Client** — `platform_customer` (`customer_ref` UNIQUE, `country_code`, `billing_currency`,
  `tax_id`, `tax_exempt`, `status`), `platform_customer_company` (`UNIQUE(company_id)`, `company_id`
  CASCADE).
- **Abonnement** — `subscription` (`status` 7 valeurs, `price_amount`+`currency_code` figés,
  `quotas_override` jsonb, `trial_ends_at`, `current_period_*`, `grace_ends_at`, `payment_failures`,
  index partiel **un seul abonnement vivant par client**), `subscription_addon`.
- **Facturation** — `platform_invoice` (`number` UNIQUE via `platform_invoice_seq`, `kind`,
  `parent_invoice_id`, `status`, taxe/remise/total/payé), `platform_invoice_line` (`line_type` 6
  valeurs, `amount` signé).
- **Paiements** — `platform_payment` (`customer_id` **nullable** — événement non rattaché §54,
  `provider_event_id` **UNIQUE**, `status`, `review_required`, `is_manual`), `platform_payment_method`
  (jeton PSP uniquement, `CHECK last4`).
- **Usage & licences** — `usage_counter` (`UNIQUE(customer, resource, period_start)`,
  `last_alert_pct`), `platform_license` (`scope` 5 valeurs, `grace_days`, `expires_at`, `token_hash`).
- **Support** — `support_ticket` (`ticket_ref` UNIQUE, `priority` p1..p4), `support_ticket_message`
  (`is_internal`), `support_access_grant` (`scope`, `requested_by`, `expires_at`, `revoked_at`).
- **Ledger** — `billing_event` (`event_type`, `payload` jsonb) — séparé des écritures (§44).
- `trg_audit_*` sur les 14 tables mutables → `audit_log`. 3 séquences
  (`platform_invoice_seq`/`platform_customer_seq`/`support_ticket_seq`).
- **7 rôles plateforme** + **11 permissions** `platform.*` ; `platform.portal` accordé à
  `owner_direction` + `group_administrator` (portail self-service).
- **206 tables (+17), 176 RLS FORCE (inchangé — tables plateforme sans RLS)**, 51 migrations (V001–V051).

## Backend

| Couche | Fichier |
|---|---|
| Contracts | `Nexora.Contracts/Platform/PlatformContracts.cs` |
| Application | `Nexora.Application/Platform/IPlatform.cs` — 9 interfaces + `PlatformEvents` ; `PermissionCatalog.Platform.*` (11) |
| Infrastructure | `Platform/SqlPlatformCatalog.cs` (catalogue + comptes + `PlatformScope` + `SqlSaasMetricsService`), `SqlSubscriptionService.cs` (cycle de vie), `SqlPlatformBilling.cs` (factures + paiements + webhook), `SqlQuotaService.cs`, `LicenseService.cs` (HMAC offline), `SqlSupportDesk.cs`, `PlatformBillingShared.cs` (`PlatformInvoices` + `Proration`) ; DI `DependencyInjection.cs` ; hook `SchedulerRunner` (`subscriptions.RunDueAsync` + `platformBilling.ReconcileAsync`) |
| API | `PlatformController.cs`, `SubscriptionsController.cs` (portail), `BillingController.cs` (portail + webhook), `LicensesController.cs`, `PlatformSupportController.cs` |
| Identité | `PermissionService.cs` + `UserSecurityProfile` : rôles à scope **PLATFORM** → permissions plateforme sans entreprise de rattachement |

### Points de conception

- **Table plateforme, pas de RLS entreprise** — la facturation de NEXORA est transverse aux
  locataires (comme `schema_migration`/`deployment`). L'accès est filtré par les permissions
  `platform.*` réservées aux 7 rôles plateforme ; l'isolation des **données métier** reste la RLS.
- **Portail self-scopé** — `IPlatformScope` résout l'organisation cliente à partir de
  `tenant.ActiveCompanyId` ; les routes `/api/v1/subscriptions/*` passent `restrictToCustomer` au
  service, qui refuse (`platform.not_your_subscription`) tout abonnement hors périmètre. Les routes
  staff `/api/v1/platform/*` passent `null` (non restreint).
- **`billing_event` ≠ facture ≠ écriture** (§44) — le ledger d'événements permet corrections,
  prorata et avoirs sans réécrire l'historique, et alimente les métriques SaaS.
- **§54 en trois garde-fous** : `provider_event_id UNIQUE` (idempotence), un événement `ambiguous`
  ou non rattaché → `review_required` sans effet de bord, la **seule** transition vers `suspended`
  est l'échéance ferme de `grace_ends_at` dans `RunDueAsync`.
- **`SqlQueryRaw` pour les blocs SELECT réutilisés** — un `const string` interpolé (`$"{SubSelect} …"`)
  deviendrait un paramètre ; `SqlQueryRaw(SubSelect + " WHERE … {0}", p)` garde le SELECT en texte brut.
- **Jeton de licence offline** — HMAC-SHA256 (clé domaine-séparée `"license:" + JwtSigningKey`),
  `FixedTimeEquals`. Suffit pour « vérification locale + anti-altération » (§28) ; la signature
  asymétrique (vérifieur avec la seule clé publique) est l'évolution production.

## Tests d'intégration — `PlatformBillingTests` (10, `[Collection("finance")]`, clients isolés)

| Test | § | Vérifie |
|---|---|---|
| `Create_subscription_with_trial_starts_a_trial` | 12 | `trialing` + `trial_ends_at` + événement `TrialStarted` |
| `Paid_subscription_activates_on_payment_with_invoice_and_license` | 19, 29 | création → `past_due` + facture ; webhook `succeeded` → `active`, facture `paid`, licence `active` |
| `Upgrade_prorates_the_difference_and_switches_plan` | 14 | bascule de plan + ligne de facture `proration` > 0 + `SubscriptionUpgraded` |
| `Downgrade_keeps_data_and_reports_quota_breaches` | 18 | impact `downgrade` + `quotaBreaches` (companies 4 > 1) ; downgrade appliqué, `usage_counter` **intact**, `credit_note` émis |
| `Payment_webhook_is_idempotent` | 54 | même `eventId` deux fois → même paiement, `COUNT = 1` |
| `Ambiguous_event_goes_to_review_and_never_suspends` | 54 | `outcome='ambiguous'` → paiement `pending` + `review_required`, abonnement reste `active` |
| `Failed_payment_moves_to_grace_then_grace_expiry_suspends` | 16-17 | `failed` → `past_due` ; `grace_ends_at` dans le passé + `RunDueAsync` → `suspended` |
| `Quota_check_blocks_when_over_limit` | 21 | usage à la limite → `CheckAsync` refuse, ligne du rapport à 100 % |
| `License_offline_token_verifies_and_detects_revocation` | 28 | jeton valide ; jeton altéré → invalide ; après révocation → invalide |
| `Cancel_at_period_end_then_reactivate` | 31, 33 | `cancel_at_period_end` (reste `active`) → `ReactivateAsync` → `active` sans annulation |

## Tests API — `PlatformEndpointsTests` (4)

| Test | Vérifie |
|---|---|
| `Platform_console_requires_authentication` | `/api/v1/platform/{metrics,customers}` sans jeton → 401 |
| `Payment_webhook_rejects_a_wrong_token` | `X-Platform-Webhook-Token` invalide → 401 |
| `License_verify_is_public_and_rejects_a_tampered_token` | `/api/v1/licenses/verify` anonyme → 200 `valid:false` |
| `Payment_webhook_with_a_valid_token_is_accepted_and_idempotent` | jeton correct → 200 `reviewRequired:true` ; second envoi identique → même corps |

## Frontend

- `src/platform/` — `platformApi.ts` + **`PlatformConsolePage.tsx`** (route `/platform`, nav
  « Plateforme NEXORA », icône `Layers`, gate `platform.view`) : métriques SaaS, file de paiements à
  revoir (§54), catalogue de plans, comptes clients cliquables → abonnement + jauges de quota +
  factures + actions upgrade/downgrade/suspendre/réactiver (gate `platform.manage_subscriptions`),
  confirmation de paiement manuel (gate `platform.record_payment`).
- `src/billing/` — `billingApi.ts` + **`BillingPortalPage.tsx`** (route `/billing`, nav
  « Mon abonnement », icône `Wallet`, gate `platform.portal`) : mon abonnement + changement de plan
  self-service, jauges d'usage, factures NEXORA, licences + génération de jeton offline (§28).
- Vitest `platformApi.test.ts` (3) → **60 tests frontend**.

## Démo (`demo_abc_group.sql`)

- **`platform@nexora.demo` / `Nexora!2026`** — rôle `platform_admin` à scope **PLATFORM**.
- **ABC Holding S.A.** (`NXC-DEMO01`) — propriétaire des 3 entreprises ABC ; abonnement **Business**
  actif (49 USD/mois), licence organisation active, facture `NXR-DEMO-000001` réglée, compteurs
  d'usage du mois. 4 plans de démo (`demo-starter/business/professional/enterprise`) + 2 add-ons.
- `direction@abc-group.demo` (via `platform.portal` hérité d'`owner_direction`) voit le portail
  d'ABC Holding.

## Smoke API (port 5080)

- `platform@nexora.demo` → `GET /api/v1/platform/metrics` (`mrr:49`, `arr:588`, `arpu:49`),
  `GET /api/v1/platform/customers` (1, 3 entreprises), `GET /api/v1/platform/plans` (4),
  `GET /api/v1/platform/customers/{id}/quotas` (companies 3/3 = 100 %).
- Upgrade business → professional : facture `proration` `+53.33 USD` (fraction 20/30 j) ;
  downgrade → starter : prix 19, événements `SubscriptionUpgraded`/`Downgraded`.
- Webhook : même `eventId` deux fois → 1 seul `platform_payment` ; `outcome=ambiguous` +
  `customerRef` inconnu → `pending` + `review_required`, abonnement **reste `active`** ; `failed`
  pour un vrai client → `past_due` + `payment_failures=1` + grâce, **pas** de suspension.
- `direction@abc-group.demo` (portail, `X-Company-Id: c1`) → `/api/v1/subscription`,
  `/api/v1/usage`, `/api/v1/billing/invoices`, `/api/v1/licenses/{id}/token` → jeton signé ;
  `POST /api/v1/licenses/verify` (anonyme) → `valid:true`, puis `false` après révocation.
- `caisse@abc-group.demo` → `GET /api/v1/platform/metrics` → **403**.

## Manques assumés (backlog)

- Export / portabilité outillée des données métier après résiliation (§32)
- Comptabilité NEXORA (créances, produits, revenue recognition, rapprochement) alimentée depuis `billing_event` (§43-44)
- Application effective des `support_access_grant` dans la RLS (bannière, révocation auto à l'échéance) (§38)
- Anti-fraude scoré (réutiliser `fraud_rule`/`fraud_signal` Phase 11) (§49)
- Facturation à l'usage agrégée (compteur → ligne `usage` sur la facture de période) (§24)
- LTV / DSO / disponibilité dans les métriques SaaS (§55)
- Rendu PDF des factures NEXORA (§13) ; signature HMAC par provider du webhook (§54)
- Multi-devise consolidée du MRR (aujourd'hui : somme naïve étiquetée USD)
