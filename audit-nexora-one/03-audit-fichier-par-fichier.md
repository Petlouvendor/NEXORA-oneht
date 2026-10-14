# 03 — Audit fichier par fichier

> Source : `02-inventaire-fichiers.csv` (une ligne par fichier suivi par Git, 1 113 fichiers) et `travail/notes-fichiers.tsv`.
> Ce document détaille les fichiers **lus ligne à ligne**. Les autres fichiers sont listés dans le CSV avec leur mode de couverture réel.

## 1. Couverture

| Mode de couverture | Fichiers |
|---|---|
| non lu ligne à ligne — couvert par contrôles automatiques | 503 |
| analysé (lecture ligne à ligne) | 183 |
| exécuté via la suite de tests (non relu ligne à ligne) | 174 |
| classé documentation | 137 |
| contrôlé par requêtes catalogue (non relu ligne à ligne) | 103 |
| exclu (binaire ou marqueur) | 12 |
| exclu (verrou de dépendances généré) | 1 |
| **Total** | **1113** |

Fichiers pertinents (code, configuration, migrations, tests ; hors documentation, binaires et fichiers générés) : **963**. Lus ligne à ligne : **183**. Restant à lire ligne à ligne : **503** fichiers de code (couverts seulement par la compilation, les suites de tests, la recherche de motifs et le croisement frontend↔API), plus 103 migrations et 174 fichiers de test non relus.

Exclusions et raisons : binaires (polices, images) et marqueurs `.gitkeep` — non exécutables ; `package-lock.json` — généré ; documentation (137 `.md`) — classée, ses affirmations ne valent pas preuve ; répertoires générés ignorés par Git (`bin/`, `obj/`, `node_modules/`, `dist/`, rapports de tests, secrets et sauvegardes locales) — hors corpus.

Critère de sélection des fichiers lus : chemins critiques d'abord (hôte et sécurité de l'API, domaine, moteur financier, ventes et caisse, stock, identité et RLS, finance et comptabilité, intégrations, paie), puis modules transverses.

## 2. Verdicts des fichiers lus

| Verdict | Fichiers |
|---|---|
| IMPLÉMENTÉ, MAIS NON VÉRIFIÉ | 126 |
| NON APPLICABLE | 23 |
| FONCTIONNEL VÉRIFIÉ | 14 |
| DÉFECTUEUX | 10 |
| NON VÉRIFIABLE | 7 |
| PARTIELLEMENT IMPLÉMENTÉ | 1 |
| PRÉSENT MAIS NON CONNECTÉ | 1 |
| INUTILISÉ OU OBSOLÈTE | 1 |

| Gravité maximale relevée | Fichiers |
|---|---|
| - | 111 |
| FAIBLE | 40 |
| MOYEN | 21 |
| CRITIQUE | 7 |
| ÉLEVÉ | 4 |

## 3. Registre détaillé par module

### Api

| Fichier | Verdict | Gravité | Observations | Preuves | Action |
|---|---|---|---|---|---|
| `backend/src/Nexora.Api/Nexora.Api.csproj` | FONCTIONNEL VÉRIFIÉ | - | Compile (dotnet build 2026-10-13). | build-worktree.log | RAS |
| `backend/src/Nexora.Api/Program.cs` | IMPLÉMENTÉ, MAIS NON VÉRIFIÉ | FAIBLE | Composition : JWT HS256 (repli « dev-only-insecure-key… » si clé vide, bloqué hors Development par StartupSecretsGuard), FallbackPolicy authentifiée (deny-by-default), CORS liste blanche, 3 politiques de limitation (auth 10/min, refresh 60/min, public 30/min par IP), en-têtes proxy bornés, santé live/ready, planificateur hébergé. Swagger seulement en Development. | Program.cs:73-99,103-130,155-161 | RAS bloquant ; documenter que la clé de repli n'est acceptée qu'en Development |
| `backend/src/Nexora.Api/appsettings.Development.json` | NON APPLICABLE | FAIBLE | Mot de passe de dev nexora_app_dev et clé JWT de dev versionnés (acceptable en dev, refusés hors dev). | appsettings.Development.json:11-14 | RAS |
| `backend/src/Nexora.Api/appsettings.json` | NON APPLICABLE | - | Chaîne de connexion et clé JWT vides (fournies par l'environnement) ; Scheduler désactivé ; canaux de notification désactivés. | appsettings.json:11-49 | RAS |

### Api/Controllers

| Fichier | Verdict | Gravité | Observations | Preuves | Action |
|---|---|---|---|---|---|
| `backend/src/Nexora.Api/Controllers/ApiControllerBase.cs` | IMPLÉMENTÉ, MAIS NON VÉRIFIÉ | - | Result→HTTP via DomainException ; Download avec enveloppe base64 (contournement des gestionnaires de téléchargement). | ApiControllerBase.cs:10-31 | RAS |
| `backend/src/Nexora.Api/Controllers/ExperienceController.cs` | IMPLÉMENTÉ, MAIS NON VÉRIFIÉ | - | Recherche globale et activité filtrées dans les services. | ExperienceController.cs | Vérifier GlobalSearchService |
| `backend/src/Nexora.Api/Controllers/MetricsController.cs` | IMPLÉMENTÉ, MAIS NON VÉRIFIÉ | FAIBLE | Anonyme ; variantes de chemin servies (A-024, exécuté en GET). | MetricsController.cs:16-27 | Authentifier |
| `backend/src/Nexora.Api/Controllers/OpsMaintenanceController.cs` | IMPLÉMENTÉ, MAIS NON VÉRIFIÉ | CRITIQUE | Permissions operations.view/manage/correct_data, détenues par manager/owner_direction, sur des services de portée plateforme (A-031). | OpsMaintenanceController.cs:30-170 ; role_permission | Rôle plateforme dédié |
| `backend/src/Nexora.Api/Controllers/PrintController.cs` | IMPLÉMENTÉ, MAIS NON VÉRIFIÉ | FAIBLE | Droits par type dans le service (PrintAccess) ; vente imprimable par tout titulaire de pos.view. | PrintController.cs ; IPrinting.cs:64-98 | RAS |
| `backend/src/Nexora.Api/Controllers/SalesController.cs` | IMPLÉMENTÉ, MAIS NON VÉRIFIÉ | MOYEN | POST /sales (SoldAt client — A-003), règlement direct (A-014), retours (pos.refund), annulation de panier. | SalesController.cs:21-60 | Voir A-003, A-014 |
| `backend/src/Nexora.Api/Controllers/SetupController.cs` | IMPLÉMENTÉ, MAIS NON VÉRIFIÉ | FAIBLE | Anonyme mais protégé par X-Setup-Token, fermé dès l'installation, limité à 10/min ; database/test permet de joindre un hôte arbitraire pendant l'installation (avec jeton). | SetupController.cs:19-112 | RAS |
| `backend/src/Nexora.Api/Controllers/SyncController.cs` | IMPLÉMENTÉ, MAIS NON VÉRIFIÉ | - | Lot (sync.view + contrôle par opération), conflits, appareils, politique. | SyncController.cs | RAS |

### Api/HealthChecks

| Fichier | Verdict | Gravité | Observations | Preuves | Action |
|---|---|---|---|---|---|
| `backend/src/Nexora.Api/HealthChecks/DatabaseHealthCheck.cs` | IMPLÉMENTÉ, MAIS NON VÉRIFIÉ | - | CanConnectAsync. | DatabaseHealthCheck.cs:14 | RAS |
| `backend/src/Nexora.Api/HealthChecks/HealthResponseWriter.cs` | IMPLÉMENTÉ, MAIS NON VÉRIFIÉ | - | JSON sans exception ni chaîne de connexion. | HealthResponseWriter.cs:21-33 | RAS |
| `backend/src/Nexora.Api/HealthChecks/MigrationsHealthCheck.cs` | IMPLÉMENTÉ, MAIS NON VÉRIFIÉ | - | Unhealthy si migrations en attente, Degraded si dérive. | MigrationsHealthCheck.cs:17-24 | RAS |

### Api/Middleware

| Fichier | Verdict | Gravité | Observations | Preuves | Action |
|---|---|---|---|---|---|
| `backend/src/Nexora.Api/Middleware/ApiKeyAuthenticationMiddleware.cs` | IMPLÉMENTÉ, MAIS NON VÉRIFIÉ | FAIBLE | Identité machine depuis X-Api-Key ou « Bearer nxk_ » ; quota/minute en mémoire non atomique (GetOrCreate puis Set : deux requêtes simultanées peuvent dépasser le quota) ; compteur par instance (pas partagé en multi-instance). | ApiKeyAuthenticationMiddleware.cs:43-57 | Compteur atomique (Interlocked / table) si multi-instance |
| `backend/src/Nexora.Api/Middleware/CorrelationIdMiddleware.cs` | IMPLÉMENTÉ, MAIS NON VÉRIFIÉ | FAIBLE | Reprend X-Correlation-Id fourni par le client sans borne de longueur ni filtrage (injection de journal / gros en-tête). | CorrelationIdMiddleware.cs:12-15 | Borner à 64 car. [A-Za-z0-9-] |
| `backend/src/Nexora.Api/Middleware/ExceptionHandlingMiddleware.cs` | IMPLÉMENTÉ, MAIS NON VÉRIFIÉ | - | RFC 7807 ; DomainException → 400/401/403/404/409/422 ; pile seulement en Development. | ExceptionHandlingMiddleware.cs:24-80 | RAS |
| `backend/src/Nexora.Api/Middleware/MaintenanceModeMiddleware.cs` | IMPLÉMENTÉ, MAIS NON VÉRIFIÉ | FAIBLE | Écritures refusées (503) pendant une maintenance sauf operations.manage ; exemption par sous-chaîne (« /auth/ », « /ops/ », « /health », « /ready ») trop large : toute route contenant « /ready » ou « /ops/ » passe. | MaintenanceModeMiddleware.cs:54-62 | Comparer des segments exacts |
| `backend/src/Nexora.Api/Middleware/SecurityHeadersMiddleware.cs` | IMPLÉMENTÉ, MAIS NON VÉRIFIÉ | - | nosniff, DENY, no-referrer, COOP, Permissions-Policy, CSP stricte pour une API JSON. | SecurityHeadersMiddleware.cs:8-15 | RAS |
| `backend/src/Nexora.Api/Middleware/SetupModeMiddleware.cs` | IMPLÉMENTÉ, MAIS NON VÉRIFIÉ | - | 503 setup.required hors routes d'installation/santé quand aucune base n'est configurée. | SetupModeMiddleware.cs:16-36 | RAS |
| `backend/src/Nexora.Api/Middleware/TenantResolutionMiddleware.cs` | IMPLÉMENTÉ, MAIS NON VÉRIFIÉ | MOYEN | X-Company-Id ne fait que sélectionner parmi les entreprises du jeton (403 sinon) ; contrôle session (epoch, jti, sid) avec cache 30 s ; X-Company-Id mal formé ignoré silencieusement (périmètre = toutes les entreprises) ; périmètre entreprises figé dans le jeton jusqu'à expiration (15 min) après retrait d'un rôle ; écrit last_seen_at sur chaque requête non cachée. | TenantResolutionMiddleware.cs:78-112,166-185 | Refuser un X-Company-Id invalide (400) ; vérifier que les retraits de droits invalident la session |

### Api/Observability

| Fichier | Verdict | Gravité | Observations | Preuves | Action |
|---|---|---|---|---|---|
| `backend/src/Nexora.Api/Observability/ObservabilityExtensions.cs` | IMPLÉMENTÉ, MAIS NON VÉRIFIÉ | FAIBLE | Serilog JSON hors dev, OTel seulement si collecteur configuré ; le journal enregistre l'en-tête X-Company-Id brut (non validé). | ObservabilityExtensions.cs:84-94 | RAS |

### Api/Properties

| Fichier | Verdict | Gravité | Observations | Preuves | Action |
|---|---|---|---|---|---|
| `backend/src/Nexora.Api/Properties/launchSettings.json` | NON APPLICABLE | - | http://localhost:5080, Development. | launchSettings.json | RAS |

### Api/Scheduling

| Fichier | Verdict | Gravité | Observations | Preuves | Action |
|---|---|---|---|---|---|
| `backend/src/Nexora.Api/Scheduling/BackgroundScheduler.cs` | IMPLÉMENTÉ, MAIS NON VÉRIFIÉ | MOYEN | Désactivé par défaut (Scheduler:Enabled=false dans appsettings.json) : relances, promesses, expirations, envois ne tournent pas sans configuration explicite ; verrou consultatif PG pour multi-instance. | BackgroundScheduler.cs:36-40 ; appsettings.json:25-28 | Documenter/activer en production (vérifier compose prod) |

### Api/Security

| Fichier | Verdict | Gravité | Observations | Preuves | Action |
|---|---|---|---|---|---|
| `backend/src/Nexora.Api/Security/IdempotencyFilter.cs` | IMPLÉMENTÉ, MAIS NON VÉRIFIÉ | MOYEN | Rejeu par cache mémoire 24 h par (utilisateur, clé) ; pas de verrou : deux requêtes simultanées avec la même clé s'exécutent toutes deux (la protection repose alors sur les contraintes DB operation_uuid) ; cache perdu au redémarrage et non partagé entre instances (commentaire « Scaffold »). | IdempotencyFilter.cs:16,59-96 | Table d'idempotence ou verrou ; vérifier que chaque action [Idempotent] a une contrainte DB |
| `backend/src/Nexora.Api/Security/PermissionAuthorization.cs` | IMPLÉMENTÉ, MAIS NON VÉRIFIÉ | FAIBLE | Politique « perm:code » ; sans entreprise active, HasPermission(…, null) accepte une permission détenue dans N'IMPORTE QUELLE entreprise autorisée — à recouper avec la RLS (périmètre = toutes les entreprises du jeton). Permissions de groupe exigées sur le groupe de la route ou sur tous les groupes du périmètre ; scopes de clé API appliqués. | PermissionAuthorization.cs:51-64 | Vérifier les écritures sans X-Company-Id (TenantErrors) — lot 2 |
| `backend/src/Nexora.Api/Security/StartupSecretsGuard.cs` | IMPLÉMENTÉ, MAIS NON VÉRIFIÉ | - | Refus de démarrage hors Development si secrets manquants/faibles (délègue à ProductionConfiguration). | StartupSecretsGuard.cs:18-26 | RAS |
| `backend/src/Nexora.Api/Security/StepUpFilter.cs` | IMPLÉMENTÉ, MAIS NON VÉRIFIÉ | - | X-Step-Up-Token validé par catégorie et session (sid) ; 403 security.step_up_required sinon. | StepUpFilter.cs:49-70 | RAS (dépend de la lecture du claim sid) |

### Api/Setup

| Fichier | Verdict | Gravité | Observations | Preuves | Action |
|---|---|---|---|---|---|
| `backend/src/Nexora.Api/Setup/CliCommands.cs` | IMPLÉMENTÉ, MAIS NON VÉRIFIÉ | - | migrate / --status / --realign / bootstrap / healthcheck ; mot de passe admin jamais en argument ; rôle nexora_app passé NOLOGIN si aucun mot de passe. | CliCommands.cs:45-205 | RAS |
| `backend/src/Nexora.Api/Setup/InstallConfiguration.cs` | IMPLÉMENTÉ, MAIS NON VÉRIFIÉ | - | Insère le fichier d'installation juste après appsettings. | InstallConfiguration.cs:13-25 | RAS |

### Application

| Fichier | Verdict | Gravité | Observations | Preuves | Action |
|---|---|---|---|---|---|
| `backend/src/Nexora.Application/DependencyInjection.cs` | IMPLÉMENTÉ, MAIS NON VÉRIFIÉ | - | Enregistre 16 services ; la majorité des services métier vivent dans Infrastructure. | DependencyInjection.cs:18-47 | RAS |
| `backend/src/Nexora.Application/Nexora.Application.csproj` | FONCTIONNEL VÉRIFIÉ | FAIBLE | FluentValidation + DI référencés mais AUCUN validateur dans le code (dépendance inutilisée) ; la validation est manuelle dans chaque service. | Grep AbstractValidator : 0 | Retirer ou utiliser |

### Application/Abstractions

| Fichier | Verdict | Gravité | Observations | Preuves | Action |
|---|---|---|---|---|---|
| `backend/src/Nexora.Application/Abstractions/ISessionCacheInvalidator.cs` | IMPLÉMENTÉ, MAIS NON VÉRIFIÉ | - | Contrat d'invalidation du cache de session. | ISessionCacheInvalidator.cs | RAS |
| `backend/src/Nexora.Application/Abstractions/ITenantContext.cs` | IMPLÉMENTÉ, MAIS NON VÉRIFIÉ | - | Contexte requête → variables RLS ; BeginSystemScope contourne la RLS. | ITenantContext.cs | Recenser les usages de BeginSystemScope (lot 11) |
| `backend/src/Nexora.Application/Abstractions/Repositories.cs` | IMPLÉMENTÉ, MAIS NON VÉRIFIÉ | - | Contrats dépôts utilisateur/entreprise. | Repositories.cs | RAS |
| `backend/src/Nexora.Application/Abstractions/Services.cs` | IMPLÉMENTÉ, MAIS NON VÉRIFIÉ | FAIBLE | HasPermission(code, null) = permission détenue dans n'importe quelle entreprise du profil ; date métier par fuseau ; Noon(day) pour les jours saisis. | Services.cs:101-115 | Voir lot 2 |

### Application/Catalog

| Fichier | Verdict | Gravité | Observations | Preuves | Action |
|---|---|---|---|---|---|
| `backend/src/Nexora.Application/Catalog/CatalogService.cs` | IMPLÉMENTÉ, MAIS NON VÉRIFIÉ | FAIBLE | Création/modification produit avec contrôles (SKU, code-barres, PLU, unité, taxe, catégorie) ; prix historisés (clôture du précédent par devise) ; workflow de statut. SetPriceAsync ne vérifie pas l'existence du produit (violation FK → 500) ni MinPrice ≤ Amount ; unicités vérifiées par lecture préalable (course → violation DB → 500 au lieu de 409) ; devise inconnue → exception. | CatalogService.cs:244-273,84-89 | Valider produit et plancher ; traduire les violations d'unicité |
| `backend/src/Nexora.Application/Catalog/PromotionCalculator.cs` | FONCTIONNEL VÉRIFIÉ | - | Fonction pure ; remise bornée à la ligne ; couverte par PromotionCalculatorTests (88 tests unitaires verts le 2026-10-13). | PromotionCalculator.cs ; test-unit.log | RAS |

### Application/Credit

| Fichier | Verdict | Gravité | Observations | Preuves | Action |
|---|---|---|---|---|---|
| `backend/src/Nexora.Application/Credit/CollectionsService.cs` | IMPLÉMENTÉ, MAIS NON VÉRIFIÉ | - | Promesses (instantané du reste dû, classement tenu/partiel/rompu), litiges (créance IN_DISPUTE puis restaurée), blocages, radiation au solde exact dans une transaction avec écriture. | CollectionsService.cs:60-273 | RAS |
| `backend/src/Nexora.Application/Credit/CreditPortfolioQueries.cs` | IMPLÉMENTÉ, MAIS NON VÉRIFIÉ | - | Interface. | CreditPortfolioQueries.cs | RAS |

### Application/Finance

| Fichier | Verdict | Gravité | Observations | Preuves | Action |
|---|---|---|---|---|---|
| `backend/src/Nexora.Application/Finance/Abstractions/FinanceAbstractions.cs` | IMPLÉMENTÉ, MAIS NON VÉRIFIÉ | - | Contrats stores. | FinanceAbstractions.cs | RAS |
| `backend/src/Nexora.Application/Finance/Engine/FinancialEngine.cs` | IMPLÉMENTÉ, MAIS NON VÉRIFIÉ | MOYEN | (1) Idempotence vérifiée HORS transaction (FindByOperationAsync avant ExecuteInTransactionAsync) : 2 requêtes simultanées de même uuid passent le contrôle, la 2e échoue sur contrainte → DbUpdateException non capturée (500) au lieu d'un rejeu. (2) MoveCashAsync date les mouvements de trésorerie à DateTimeOffset.UtcNow et non evt.OccurredAt : vente hors ligne synchronisée le lendemain = écriture au jour de la vente mais trésorerie au jour de synchro. (3) Affectations de paiement exprimées dans la devise du paiement (ccy = tenderTxn.Currency) et comparées au solde de la créance quelle que soit sa devise. (4) PostedAt = UtcNow (horloge non injectée). | FinancialEngine.cs:48-54,73,336,465-474 | Contrôle d'unicité dans la transaction + capture de la violation ; OccurredAt de l'événement ; contrôler devise créance = devise paiement |
| `backend/src/Nexora.Application/Finance/Events/FinancialEvents.cs` | IMPLÉMENTÉ, MAIS NON VÉRIFIÉ | - | ~30 événements typés porteurs d'OperationUuid. | FinancialEvents.cs | RAS |
| `backend/src/Nexora.Application/Finance/FinanceOperations.cs` | IMPLÉMENTÉ, MAIS NON VÉRIFIÉ | - | Façade ; dépense = pièce + écriture dans une transaction (BUG-016 corrigé). | FinanceOperations.cs:336-361 | RAS |
| `backend/src/Nexora.Application/Finance/Periods/AccountingPeriodService.cs` | IMPLÉMENTÉ, MAIS NON VÉRIFIÉ | FAIBLE | Clôture sans contrôle préalable (brouillons, écritures non équilibrées) — les contrôles existent dans la clôture d'exercice / financière (autre service). | AccountingPeriodService.cs:509-518 | Vérifier quel écran appelle quelle clôture |
| `backend/src/Nexora.Application/Finance/Posting/PostingContext.cs` | IMPLÉMENTÉ, MAIS NON VÉRIFIÉ | - | Conversion au taux de la date, arrondi bancaire. | PostingContext.cs:626-632 | RAS |
| `backend/src/Nexora.Application/Finance/Posting/PostingRuleSet.cs` | IMPLÉMENTÉ, MAIS NON VÉRIFIÉ | MOYEN | 28 règles ; chaque écriture EnsureBalanced. Total, HT et taxe sont convertis/arrondis séparément : si la somme des arrondis diffère (montants à 4 décimales, ou devise ≠ fonctionnelle), l'écriture est refusée (« déséquilibrée ») et l'opération échoue. Pas d'écart d'arrondi absorbé sauf dans DebitPurchase, Payroll et SupplierReturn. Transfert : un seul montant pour les deux comptes (devises différentes non gérées ici). | PostingRuleSet.cs:61-68,149-156,243-251,490-506 | Absorber l'écart d'arrondi sur une ligne ; vérifier l'arrondi en amont (SalesService) |
| `backend/src/Nexora.Application/Finance/Reversals/ReversalService.cs` | IMPLÉMENTÉ, MAIS NON VÉRIFIÉ | MOYEN | Extourne du SEUL grand livre : créance, trésorerie, stock du document source ne sont pas touchés ; si appelée sur une écriture « système » (vente, paiement), GL et sous-livres divergent. Garde à vérifier côté endpoint (lot 7). | ReversalService.cs:556-631 | Interdire l'extourne des écritures système hors du flux métier (annulation de vente) |

### Application/Integrations

| Fichier | Verdict | Gravité | Observations | Preuves | Action |
|---|---|---|---|---|---|
| `backend/src/Nexora.Application/Integrations/PaymentProviders.cs` | IMPLÉMENTÉ, MAIS NON VÉRIFIÉ | - | Contrat IPaymentProvider avec drapeau IsSimulation explicite. | PaymentProviders.cs | RAS |

### Application/Inventory

| Fichier | Verdict | Gravité | Observations | Preuves | Action |
|---|---|---|---|---|---|
| `backend/src/Nexora.Application/Inventory/Abstractions.cs` | IMPLÉMENTÉ, MAIS NON VÉRIFIÉ | - | Interfaces lots, requêtes, entrepôts, politique de stock négatif. | Abstractions.cs | RAS |
| `backend/src/Nexora.Application/Inventory/InventoryOperationsService.cs` | DÉFECTUEUX | ÉLEVÉ | ValidateCountAsync : écart = compté − quantité système figée à l'ouverture ; les ventes pendant le comptage sont recomptées (A-007 : 411 au lieu de 409) ; lots ignorés. | InventoryOperationsService.cs ; scénario s5_count.py | Écart contre le solde courant à la validation ou gel des mouvements |
| `backend/src/Nexora.Application/Inventory/ReorderService.cs` | IMPLÉMENTÉ, MAIS NON VÉRIFIÉ | - | Interface (7 lignes). | ReorderService.cs | RAS |
| `backend/src/Nexora.Application/Inventory/StockAbstractions.cs` | IMPLÉMENTÉ, MAIS NON VÉRIFIÉ | - | Écrivain unique IStockLedger ; FEFO ; verrou FOR UPDATE. | StockAbstractions.cs | RAS |

### Application/Observability

| Fichier | Verdict | Gravité | Observations | Preuves | Action |
|---|---|---|---|---|---|
| `backend/src/Nexora.Application/Observability/NexoraMetrics.cs` | IMPLÉMENTÉ, MAIS NON VÉRIFIÉ | - | Compteurs à faible cardinalité. | NexoraMetrics.cs | RAS |

### Application/Payroll

| Fichier | Verdict | Gravité | Observations | Preuves | Action |
|---|---|---|---|---|---|
| `backend/src/Nexora.Application/Payroll/AdvanceRules.cs` | FONCTIONNEL VÉRIFIÉ | - | Règles pures (plafond % salaire, multiples, nombre, endettement) + échéancier ; couvert par tests unitaires (verts). | AdvanceRules.cs ; test-unit.log | RAS |
| `backend/src/Nexora.Application/Payroll/PayrollCalculator.cs` | FONCTIONNEL VÉRIFIÉ | FAIBLE | Moteur pur et explicable (segments de salaire, prorata, absences, HS, éléments, barèmes versionnés, net minimal, récupérations plafonnées, anomalies bloquantes). Couvert par PayrollCalculatorTests (verts). Conventions : taux journalier mensuel = salaire/30 alors que le prorata utilise les jours réels de la période ; arrondi AwayFromZero (≠ ToEven ailleurs). Sans règle légale validée : aucune retenue (simple avertissement). | PayrollCalculator.cs:247-252,486-489 | Vérifier les règles légales amorcées (lot 8) |

### Application/Purchasing

| Fichier | Verdict | Gravité | Observations | Preuves | Action |
|---|---|---|---|---|---|
| `backend/src/Nexora.Application/Purchasing/PurchasingService.cs` | IMPLÉMENTÉ, MAIS NON VÉRIFIÉ | MOYEN | BC (brouillon/soumis/approuvé/envoyé), réception (contrôle qualité, lots, conversion d'unités, ≤ reliquat, écriture 4081), facture (taxe serveur, anti-doublon de référence, solde du 4081 + écart de prix), retour (≤ reçu, imputation dette), rapprochement 3-way (bloque le paiement), paiement de dette. À vérifier : BC en devise étrangère → le coût unitaire de réception (devise du BC) alimente le coût moyen de stock sans conversion, alors que l'écriture est convertie (valorisation stock ≠ GL). Contrôles « reliquat » et « reste à payer » hors verrou ; ReceivedAt fourni par le client. | PurchasingService.cs:349-374,401-455,759-775 | Convertir le coût en devise fonctionnelle avant le mouvement de stock |

### Application/Reporting

| Fichier | Verdict | Gravité | Observations | Preuves | Action |
|---|---|---|---|---|---|
| `backend/src/Nexora.Application/Reporting/Abstractions.cs` | DÉFECTUEUX | MOYEN | FilterPeriod « week » : borne de début après la date du jour le dimanche → période vide (A-008). | Reporting/Abstractions.cs (FilterPeriod) | Calcul ISO du lundi |

### Application/Sales

| Fichier | Verdict | Gravité | Observations | Preuves | Action |
|---|---|---|---|---|---|
| `backend/src/Nexora.Application/Sales/Abstractions.cs` | IMPLÉMENTÉ, MAIS NON VÉRIFIÉ | FAIBLE | Rounding.Money arrondit toujours à 2 décimales (indépendamment des décimales de la devise lues ailleurs). | Abstractions.cs:236-239 | RAS si seules HTG/USD |
| `backend/src/Nexora.Application/Sales/CashSessionService.cs` | IMPLÉMENTÉ, MAIS NON VÉRIFIÉ | MOYEN | Attendu = ouverture + mouvements ; seuil d'écart → validation par un superviseur distinct ; écart passé en comptabilité (uuid aléatoire). Ouverture : contrôle « déjà ouverte » sans verrou (à croiser avec un index unique en base). L'attendu ignore les remboursements (A-002). Écart non comptabilisé si la caisse n'a pas de compte financier. | CashSessionService.cs:198-230,271-287,334-339 | Voir A-002 |
| `backend/src/Nexora.Application/Sales/IPosControls.cs` | IMPLÉMENTÉ, MAIS NON VÉRIFIÉ | - | Interfaces retours soumis à validation, remises exceptionnelles, validation sur place. | IPosControls.cs | RAS |
| `backend/src/Nexora.Application/Sales/IPosOffline.cs` | IMPLÉMENTÉ, MAIS NON VÉRIFIÉ | - | Interface enrôlement/profil. | IPosOffline.cs | RAS |
| `backend/src/Nexora.Application/Sales/OrderService.cs` | IMPLÉMENTÉ, MAIS NON VÉRIFIÉ | FAIBLE | Cycle brouillon→confirmé→réservé→livré ; acompte = paiement + avoir order_deposit ; exécution via SalesService (prix figés) ; réservation sans verrou de stock (AvailableAsync puis ReserveAsync) ; annulation conserve l'acompte en avoir (pas de remboursement automatique). | OrderService.cs:216-224,237-276,278-365 | Verrouiller la réservation |
| `backend/src/Nexora.Application/Sales/QuoteService.cs` | IMPLÉMENTÉ, MAIS NON VÉRIFIÉ | FAIBLE | Cycle devis + conversion. Conversion non atomique : la commande est créée (SaveChanges) puis le devis marqué converti (2e SaveChanges) ; double clic simultané ou échec du 2e enregistrement → commandes en double. | QuoteService.cs:180-190 | Transaction + verrou sur le devis |
| `backend/src/Nexora.Application/Sales/SalesContracts.cs` | IMPLÉMENTÉ, MAIS NON VÉRIFIÉ | FAIBLE | CheckoutCommand expose SoldAt, UnitPriceOverride, LineDiscount au client (contrôlés par ISalePricingGuard sauf SoldAt). | SalesContracts.cs:15-35 | Retirer SoldAt du contrat public |
| `backend/src/Nexora.Application/Sales/SalesQueries.cs` | IMPLÉMENTÉ, MAIS NON VÉRIFIÉ | - | Interface de lecture. | SalesQueries.cs | RAS |
| `backend/src/Nexora.Application/Sales/SalesReturnService.cs` | DÉFECTUEUX | CRITIQUE | Remboursement espèces sans mouvement de caisse de session (A-002) ; contrôle de quantité retournable hors transaction (A-005) ; remboursement cash sans compte → compte d'attente ; Restocked=true en dur ; rejeu d'un même uuid renvoie un conflit et non le résultat. | SalesReturnService.cs:42-48,59-72,87-140 | Écrire le mouvement sale_refund ; verrouiller la vente |
| `backend/src/Nexora.Application/Sales/SalesService.cs` | IMPLÉMENTÉ, MAIS NON VÉRIFIÉ | MOYEN | Encaissement atomique (vente, lignes, stock verrouillé, paiements, mouvements de caisse espèces, écriture, créance, avoir, coupon) ; prix recalculés serveur + garde-fou ; crédit soumis à pos.sell_credit + limite + dérogation. Défauts : SoldAt fourni par le client accepté (antidatage ; vente dans une session fermée si SoldAt ≤ fermeture — A-003) ; idempotence hors transaction (doublon concurrent → 500) ; répartition par lot arrondie ligne à ligne (somme des lignes ≠ total possible d'1 centime) ; ingrédients de recette non contrôlés contre le stock disponible ici. | SalesService.cs:95-100,102,295-297,603-615 | Voir A-003 ; capter la violation d'unicité |

### Application/UseCases

| Fichier | Verdict | Gravité | Observations | Preuves | Action |
|---|---|---|---|---|---|
| `backend/src/Nexora.Application/UseCases/Auth/AccessTokenIssuer.cs` | IMPLÉMENTÉ, MAIS NON VÉRIFIÉ | - | Émet jeton + profil ; sid conservé au rafraîchissement. | AccessTokenIssuer.cs:17-48 | RAS |
| `backend/src/Nexora.Application/UseCases/Auth/LoginHandler.cs` | IMPLÉMENTÉ, MAIS NON VÉRIFIÉ | FAIBLE | Message générique, verrouillage, MFA, journal sécurité. Énumération possible des comptes : (1) compte inconnu rejeté sans BCrypt (réponse plus rapide) ; (2) « auth.account_locked » n'est renvoyé que pour un compte existant. | LoginHandler.cs:29-44 | Hachage factice pour compte inconnu ; message de verrouillage générique |
| `backend/src/Nexora.Application/UseCases/Auth/MfaVerifyHandler.cs` | IMPLÉMENTÉ, MAIS NON VÉRIFIÉ | - | Vérifie le défi puis émet le jeton ; re-contrôle actif + entreprise. | MfaVerifyHandler.cs:20-41 | Vérifier la limite d'essais du défi (MfaService) |
| `backend/src/Nexora.Application/UseCases/Organization/ListCompaniesHandler.cs` | IMPLÉMENTÉ, MAIS NON VÉRIFIÉ | - | Liste sous RLS. | ListCompaniesHandler.cs | RAS |

### Contracts

| Fichier | Verdict | Gravité | Observations | Preuves | Action |
|---|---|---|---|---|---|
| `backend/src/Nexora.Contracts/Nexora.Contracts.csproj` | FONCTIONNEL VÉRIFIÉ | - | Compile. | build-worktree.log | RAS |

### Contracts/Administration

| Fichier | Verdict | Gravité | Observations | Preuves | Action |
|---|---|---|---|---|---|
| `backend/src/Nexora.Contracts/Administration/AdminContracts.cs` | NON APPLICABLE | - | DTO (paramètres, rôles, workflows, automatisations, relances, versions). Aucune annotation de validation. | AdminContracts.cs | RAS |

### Contracts/Auth

| Fichier | Verdict | Gravité | Observations | Preuves | Action |
|---|---|---|---|---|---|
| `backend/src/Nexora.Contracts/Auth/AuthContracts.cs` | NON APPLICABLE | - | DTO login/MFA ; SessionId [JsonIgnore]. | AuthContracts.cs:112-114 | RAS |

### Contracts/Catalog

| Fichier | Verdict | Gravité | Observations | Preuves | Action |
|---|---|---|---|---|---|
| `backend/src/Nexora.Contracts/Catalog/CatalogContracts.cs` | NON APPLICABLE | - | DTO catalogue, prix explicable, promotions, coupons, unités. | CatalogContracts.cs | RAS |

### Contracts/Common

| Fichier | Verdict | Gravité | Observations | Preuves | Action |
|---|---|---|---|---|---|
| `backend/src/Nexora.Contracts/Common/CursorPage.cs` | NON APPLICABLE | - | Pagination par curseur. | CursorPage.cs | RAS |
| `backend/src/Nexora.Contracts/Common/PagedResult.cs` | NON APPLICABLE | - | Pagination bornée (max 200, sinon 25). | PagedResult.cs:326-331 | RAS |

### Contracts/Configuration

| Fichier | Verdict | Gravité | Observations | Preuves | Action |
|---|---|---|---|---|---|
| `backend/src/Nexora.Contracts/Configuration/ConfigContracts.cs` | NON APPLICABLE | - | DTO profils sectoriels, transfert de configuration, garde-fous. | ConfigContracts.cs | RAS |

### Contracts/Continuity

| Fichier | Verdict | Gravité | Observations | Preuves | Action |
|---|---|---|---|---|---|
| `backend/src/Nexora.Contracts/Continuity/ContinuityContracts.cs` | NON APPLICABLE | - | DTO sauvegardes, restaurations, incidents, DR. | ContinuityContracts.cs | RAS |

### Contracts/Credit

| Fichier | Verdict | Gravité | Observations | Preuves | Action |
|---|---|---|---|---|---|
| `backend/src/Nexora.Contracts/Credit/CreditContracts.cs` | NON APPLICABLE | FAIBLE | Devise par défaut « HTG » codée en dur dans PromiseDto/DisputeDto. | CreditContracts.cs:587,600 | RAS |

### Domain

| Fichier | Verdict | Gravité | Observations | Preuves | Action |
|---|---|---|---|---|---|
| `backend/src/Nexora.Domain/Nexora.Domain.csproj` | FONCTIONNEL VÉRIFIÉ | - | Compile. | build-worktree.log | RAS |

### Domain/Accounting

| Fichier | Verdict | Gravité | Observations | Preuves | Action |
|---|---|---|---|---|---|
| `backend/src/Nexora.Domain/Accounting/Account.cs` | IMPLÉMENTÉ, MAIS NON VÉRIFIÉ | - | Entités Account/AccountMapping + clés de mapping (miroir CHECK V022). | Account.cs:38-79 | RAS |
| `backend/src/Nexora.Domain/Accounting/JournalEntry.cs` | IMPLÉMENTÉ, MAIS NON VÉRIFIÉ | - | Journal, période, écriture, ligne (POCO). | JournalEntry.cs | RAS |
| `backend/src/Nexora.Domain/Accounting/JournalEntryDraft.cs` | IMPLÉMENTÉ, MAIS NON VÉRIFIÉ | FAIBLE | Équilibre contrôlé sur les montants en devise de base uniquement, alors que le commentaire annonce « en devise et en devise de référence » (commentaire trompeur). | JournalEntryDraft.cs:20-21,67-80 | Corriger le commentaire |

### Domain/Catalog

| Fichier | Verdict | Gravité | Observations | Preuves | Action |
|---|---|---|---|---|---|
| `backend/src/Nexora.Domain/Catalog/Product.cs` | IMPLÉMENTÉ, MAIS NON VÉRIFIÉ | - | Produit, liste de prix, prix historisé, codes-barres, conversions, réappro, historique de coût ; workflow de statut. | Product.cs:27-35 | RAS |

### Domain/Common

| Fichier | Verdict | Gravité | Observations | Preuves | Action |
|---|---|---|---|---|---|
| `backend/src/Nexora.Domain/Common/Currency.cs` | IMPLÉMENTÉ, MAIS NON VÉRIFIÉ | - | POCO. | Currency.cs | RAS |
| `backend/src/Nexora.Domain/Common/DomainException.cs` | IMPLÉMENTÉ, MAIS NON VÉRIFIÉ | - | Porte une Error vers l'API. | DomainException.cs | RAS |
| `backend/src/Nexora.Domain/Common/Entity.cs` | IMPLÉMENTÉ, MAIS NON VÉRIFIÉ | - | Bases Entity/AuditableEntity. | Entity.cs | RAS |
| `backend/src/Nexora.Domain/Common/Money.cs` | IMPLÉMENTÉ, MAIS NON VÉRIFIÉ | FAIBLE | Arrondi bancaire (ToEven) ; PostgreSQL round(numeric) arrondit « au plus loin de zéro » : risque d'écart d'un centime si les deux côtés arrondissent (à recouper lot 5/7). | Money.cs:32 | Vérifier la cohérence des arrondis C#/SQL/TS |
| `backend/src/Nexora.Domain/Common/Result.cs` | IMPLÉMENTÉ, MAIS NON VÉRIFIÉ | - | Result/Error typés. | Result.cs | RAS |
| `backend/src/Nexora.Domain/Common/TenantErrors.cs` | IMPLÉMENTÉ, MAIS NON VÉRIFIÉ | - | 400 tenant.company_required. | TenantErrors.cs | RAS |

### Domain/Finance

| Fichier | Verdict | Gravité | Observations | Preuves | Action |
|---|---|---|---|---|---|
| `backend/src/Nexora.Domain/Finance/FinancialAccount.cs` | IMPLÉMENTÉ, MAIS NON VÉRIFIÉ | - | Compte financier (CurrentBalance), transaction signée, taux de change. | FinancialAccount.cs | RAS |
| `backend/src/Nexora.Domain/Finance/Payment.cs` | IMPLÉMENTÉ, MAIS NON VÉRIFIÉ | - | Paiement, affectation, créance, dette (CreditedAmount V082). | Payment.cs | RAS |

### Domain/Identity

| Fichier | Verdict | Gravité | Observations | Preuves | Action |
|---|---|---|---|---|---|
| `backend/src/Nexora.Domain/Identity/AccessScope.cs` | IMPLÉMENTÉ, MAIS NON VÉRIFIÉ | - | Périmètres PLATFORM…POS_TERMINAL ; UserRole temporel/révocable. | AccessScope.cs:50-53 | RAS |
| `backend/src/Nexora.Domain/Identity/AppUser.cs` | IMPLÉMENTÉ, MAIS NON VÉRIFIÉ | - | Utilisateur (MFA, verrouillage). | AppUser.cs | RAS |
| `backend/src/Nexora.Domain/Identity/Role.cs` | IMPLÉMENTÉ, MAIS NON VÉRIFIÉ | - | Rôle/permission. | Role.cs | RAS |

### Domain/Inventory

| Fichier | Verdict | Gravité | Observations | Preuves | Action |
|---|---|---|---|---|---|
| `backend/src/Nexora.Domain/Inventory/InventoryOperations.cs` | IMPLÉMENTÉ, MAIS NON VÉRIFIÉ | - | Transferts, inventaires, ajustements (POCO). | InventoryOperations.cs | RAS |
| `backend/src/Nexora.Domain/Inventory/Stock.cs` | IMPLÉMENTÉ, MAIS NON VÉRIFIÉ | - | Emplacement, solde (trigger), mouvement immuable, lot (péremption dérivée), stock par lot. | Stock.cs:50-60,99-105 | RAS |

### Domain/Organization

| Fichier | Verdict | Gravité | Observations | Preuves | Action |
|---|---|---|---|---|---|
| `backend/src/Nexora.Domain/Organization/BusinessGroup.cs` | IMPLÉMENTÉ, MAIS NON VÉRIFIÉ | - | POCO. | BusinessGroup.cs | RAS |
| `backend/src/Nexora.Domain/Organization/Company.cs` | IMPLÉMENTÉ, MAIS NON VÉRIFIÉ | - | POCO ; fuseau par défaut America/Port-au-Prince. | Company.cs:17 | RAS |
| `backend/src/Nexora.Domain/Organization/Establishment.cs` | IMPLÉMENTÉ, MAIS NON VÉRIFIÉ | - | POCO. | Establishment.cs | RAS |

### Domain/Purchasing

| Fichier | Verdict | Gravité | Observations | Preuves | Action |
|---|---|---|---|---|---|
| `backend/src/Nexora.Domain/Purchasing/Purchasing.cs` | IMPLÉMENTÉ, MAIS NON VÉRIFIÉ | - | DA, BC (transitions), réception (rejet V081), retour fournisseur (avoir V082). | Purchasing.cs:46-58 | RAS |

### Domain/Sales

| Fichier | Verdict | Gravité | Observations | Preuves | Action |
|---|---|---|---|---|---|
| `backend/src/Nexora.Domain/Sales/Quote.cs` | IMPLÉMENTÉ, MAIS NON VÉRIFIÉ | - | Devis + transitions. | Quote.cs:16-22 | RAS |
| `backend/src/Nexora.Domain/Sales/Sale.cs` | IMPLÉMENTÉ, MAIS NON VÉRIFIÉ | - | Vente, lignes, paiements, promotions, retours, caisse, session, mouvements. | Sale.cs | RAS |
| `backend/src/Nexora.Domain/Sales/SalesOrder.cs` | IMPLÉMENTÉ, MAIS NON VÉRIFIÉ | - | Commande client + transitions. | SalesOrder.cs:15-22 | RAS |

### Frontend/app

| Fichier | Verdict | Gravité | Observations | Preuves | Action |
|---|---|---|---|---|---|
| `frontend/nexora-web/src/app/router.tsx` | IMPLÉMENTÉ, MAIS NON VÉRIFIÉ | - | Chaque écran gardé par la permission de son entrée de menu (contrôle d'affichage ; le serveur reste l'autorité). | router.tsx:49-108 | RAS |

### Frontend/auth

| Fichier | Verdict | Gravité | Observations | Preuves | Action |
|---|---|---|---|---|---|
| `frontend/nexora-web/src/auth/AuthContext.tsx` | IMPLÉMENTÉ, MAIS NON VÉRIFIÉ | - | Rafraîchissement par cookie HttpOnly (credentials same-origin) ; entreprise active en localStorage (non sensible). | AuthContext.tsx:34-84 | RAS |
| `frontend/nexora-web/src/auth/tokenVault.ts` | IMPLÉMENTÉ, MAIS NON VÉRIFIÉ | FAIBLE | Jeton chiffré dans IndexedDB (contexte sécurisé), repli sessionStorage ; ancienne copie localStorage purgée. Ne protège pas contre une XSS (jeton utilisable par le script). | tokenVault.ts:1-55 | RAS (CSP déjà présente) |

### Frontend/config

| Fichier | Verdict | Gravité | Observations | Preuves | Action |
|---|---|---|---|---|---|
| `frontend/nexora-web/Dockerfile` | NON VÉRIFIABLE | - | npm ci + build puis Caddy ; non construit (Docker arrêté). | Dockerfile | RAS |
| `frontend/nexora-web/package.json` | NON APPLICABLE | - | React 19.1, react-router-dom 7, Vite 6, TS 5.7, Vitest 4, Playwright 1.63, ESLint 10 ; aucune bibliothèque de graphiques ni d'état global. | package.json | RAS |

### Frontend/core

| Fichier | Verdict | Gravité | Observations | Preuves | Action |
|---|---|---|---|---|---|
| `frontend/nexora-web/src/core/apiClient.ts` | FONCTIONNEL VÉRIFIÉ | - | Bearer + X-Company-Id + corrélation, refresh unique sur 401, step-up rejoué sur 403 « security.step_up_required ». Vitest vert. | apiClient.ts:1-140 ; apiClient.test.ts | RAS |
| `frontend/nexora-web/src/core/config.ts` | NON APPLICABLE | - | API relative /api/v1 (proxy Vite / Caddy) : le cookie de rafraîchissement « same-origin » suppose une même origine. | config.ts ; vite.config.ts:15-18 | Documenter : pas d'API sur une autre origine |

### Frontend/shared

| Fichier | Verdict | Gravité | Observations | Preuves | Action |
|---|---|---|---|---|---|
| `frontend/nexora-web/src/shared/ModulePlaceholder.tsx` | INUTILISÉ OU OBSOLÈTE | FAIBLE | Composant exporté mais importé nulle part. | grep ModulePlaceholder : seule la définition | Supprimer après validation |

### Infrastructure

| Fichier | Verdict | Gravité | Observations | Preuves | Action |
|---|---|---|---|---|---|
| `backend/src/Nexora.Infrastructure/Nexora.Infrastructure.csproj` | FONCTIONNEL VÉRIFIÉ | - | Compile ; embarque database/migrations/V*.sql et polices TTF. | Nexora.Infrastructure.csproj:24-32 | RAS |

### Infrastructure/Administration

| Fichier | Verdict | Gravité | Observations | Preuves | Action |
|---|---|---|---|---|---|
| `backend/src/Nexora.Infrastructure/Administration/SettingsAndRoles.cs` | IMPLÉMENTÉ, MAIS NON VÉRIFIÉ | - | Rôles personnalisés : garde anti-élévation (on n'accorde que ce qu'on détient), rôles système non modifiables, ajouts contrôlés à la réactivation. | SettingsAndRoles.cs:200-350 | RAS (voir A-029 côté base) |

### Infrastructure/CI

| Fichier | Verdict | Gravité | Observations | Preuves | Action |
|---|---|---|---|---|---|
| `.github/workflows/ci.yml` | IMPLÉMENTÉ, MAIS NON VÉRIFIÉ | FAIBLE | Build en erreurs sur avertissements, tests complets, migrate --status, typecheck/lint/test/build frontend, images, E2E Playwright sur installation neuve. Le scan « dotnet list package --vulnerable » n'échoue jamais (code 0 même avec vulnérabilités) : informatif (A-028). Les tests d'API y écrivent aussi dans la base modèle nexora (T-002). Non exécutée pendant l'audit. | ci.yml:66-77 | Échouer sur sortie non vide du scan |
| `infrastructure/caddy/Caddyfile` | IMPLÉMENTÉ, MAIS NON VÉRIFIÉ | FAIBLE | TLS automatique, HSTS, CSP stricte sur l'interface, proxy /api/* ; blocage des sondes et métriques par chemins EXACTS seulement (contournable par /api/v1.0/metrics, /api/v1/metrics/ — A-024). Non exécuté (Docker arrêté). | Caddyfile (@internal) ; GET sur l'API locale | Blocage par préfixe ; authentifier /metrics |
| `infrastructure/docker-compose.prod.yml` | IMPLÉMENTÉ, MAIS NON VÉRIFIÉ | - | Propriétaire nexora_owner pour les migrations, rôle applicatif nexora_app sans BYPASSRLS, secrets en fichiers, migrate avant l'API, planificateur actif, webhooks privés interdits par défaut. Non exécuté (Docker arrêté). | docker-compose.prod.yml:28-100 | Exécuter une installation neuve sur l'infrastructure cible |

### Infrastructure/Customers

| Fichier | Verdict | Gravité | Observations | Preuves | Action |
|---|---|---|---|---|---|
| `backend/src/Nexora.Infrastructure/Customers/SqlCustomerAccountService.cs` | FONCTIONNEL VÉRIFIÉ | MOYEN | Versements clients : FOR UPDATE sur créances et avoirs, contrôle de devise, mouvement de caisse « payin », annulation avec contre-passation et mouvement « payout », avoir imputé par l'événement CustomerCreditApplied, ouverture de crédit idempotente. Solide ; contraste avec les chemins caisse (A-002, A-014). Le retrait de trésorerie passe par AdjustBalanceAsync (A-018). | SqlCustomerAccountService.cs:1-628 ; CustomerAccountTests + CustomerAccountGapsTests verts (exécutés en superutilisateur, RLS non exercée — T-001) | Conserver ; corriger A-018 dans FinanceStores |

### Infrastructure/Finance

| Fichier | Verdict | Gravité | Observations | Preuves | Action |
|---|---|---|---|---|---|
| `backend/src/Nexora.Infrastructure/Finance/FinanceStores.cs` | DÉFECTUEUX | CRITIQUE | AdjustBalanceAsync : lecture puis « account.CurrentBalance += delta » sans verrou ni UPDATE atomique ; mise à jour perdue sous concurrence (A-018 confirmé : 525 HTG perdus). | FinanceStores.cs (AdjustBalanceAsync) ; scénario s4b_mixed.py sur clone | UPDATE … SET current_balance = current_balance + delta, ou FOR UPDATE |
| `backend/src/Nexora.Infrastructure/Finance/SqlFinancialReports.cs` | DÉFECTUEUX | CRITIQUE | Bilan/balance cumulés depuis DateOnly.MinValue (A-021) ; balance âgée = original − payé, ignore credited_amount (A-022). | SqlFinancialReports.cs | Intégrer les avoirs ; aligner sur la clôture |
| `backend/src/Nexora.Infrastructure/Finance/SqlGeneralAccounting.cs` | DÉFECTUEUX | CRITIQUE | Clôture d'exercice : PnlBalancesAsync n'utilise pas periodId et somme « entry_date <= endDate » depuis l'origine (A-020) ; à-nouveaux générés alors que les états cumulent depuis l'origine (A-021). | SqlGeneralAccounting.cs (PnlBalancesAsync, GenerateOpeningBalancesAsync) | Borner sur l'exercice ; exclure les à-nouveaux des cumuls ou ne pas cumuler |
| `backend/src/Nexora.Infrastructure/Finance/SqlTreasury.cs` | IMPLÉMENTÉ, MAIS NON VÉRIFIÉ | FAIBLE | Position, liquidité, échéancier en SQL ; prévision basée sur original − payé (A-022) ; catch silencieux renvoyant une valeur par défaut (30 jours) si paramètre illisible. | SqlTreasury.cs:49,59 | Journaliser les paramètres illisibles |
| `backend/src/Nexora.Infrastructure/Finance/TreasuryManagementServices.cs` | PARTIELLEMENT IMPLÉMENTÉ | MOYEN | Comptes financiers créés à solde 0 ; aucune opération de solde d'ouverture avec contrepartie (A-023). Transferts via moteur (corrigés en finalisation 7). | TreasuryManagementServices.cs:93-106 ; écart recette 60 000 / 45 000 | Ajouter un solde d'ouverture comptabilisé |

### Infrastructure/Identity

| Fichier | Verdict | Gravité | Observations | Preuves | Action |
|---|---|---|---|---|---|
| `backend/src/Nexora.Infrastructure/Identity/BcryptPasswordHasher.cs` | IMPLÉMENTÉ, MAIS NON VÉRIFIÉ | - | BCrypt coût 12. | BcryptPasswordHasher.cs | RAS |
| `backend/src/Nexora.Infrastructure/Identity/JwtOptions.cs` | NON APPLICABLE | - | Options. | JwtOptions.cs | RAS |
| `backend/src/Nexora.Infrastructure/Identity/JwtTokenService.cs` | IMPLÉMENTÉ, MAIS NON VÉRIFIÉ | - | HS256, jti, sid, entreprises/groupes dans le jeton. | JwtTokenService.cs | Vérifier la lecture du claim sid |
| `backend/src/Nexora.Infrastructure/Identity/MfaSecretProtector.cs` | IMPLÉMENTÉ, MAIS NON VÉRIFIÉ | FAIBLE | AES-256-GCM ; clé de repli de dev si non configurée. | MfaSecretProtector.cs:330-338 | Vérifier ProductionConfiguration |
| `backend/src/Nexora.Infrastructure/Identity/MfaService.cs` | IMPLÉMENTÉ, MAIS NON VÉRIFIÉ | MOYEN | TOTP chiffré, codes de récupération hachés, défi 5 min / 5 essais. Reset admin : suppression des codes inopérante (A-009) ; TOTP rejouable ; périmètre de reset large (A-011). | MfaService.cs:144-169,257-270 | Voir A-009, A-011 |
| `backend/src/Nexora.Infrastructure/Identity/PermissionService.cs` | IMPLÉMENTÉ, MAIS NON VÉRIFIÉ | FAIBLE | Profil effectif ; rechargé à chaque contrôle (P-001). | PermissionService.cs | Cache par requête |
| `backend/src/Nexora.Infrastructure/Identity/SessionCacheInvalidator.cs` | IMPLÉMENTÉ, MAIS NON VÉRIFIÉ | FAIBLE | Cache mémoire par instance : invalidation non propagée en multi-instance (jusqu'à 30 s). | SessionCacheInvalidator.cs | Cache distribué |
| `backend/src/Nexora.Infrastructure/Identity/SqlAccountService.cs` | IMPLÉMENTÉ, MAIS NON VÉRIFIÉ | FAIBLE | Changement, oubli (pas d'énumération, 3/h, jeton haché 30 min), réinitialisation atomique. Lien e-mail relatif (« /reset-password?token=… ») ; sans canal e-mail, la demande est perdue silencieusement. | SqlAccountService.cs:69-118 | Lien absolu (PublicUrl) |
| `backend/src/Nexora.Infrastructure/Identity/SqlBranchScopeResolver.cs` | IMPLÉMENTÉ, MAIS NON VÉRIFIÉ | - | Restriction aux établissements. | SqlBranchScopeResolver.cs | RAS |
| `backend/src/Nexora.Infrastructure/Identity/Totp.cs` | IMPLÉMENTÉ, MAIS NON VÉRIFIÉ | - | RFC 6238, comparaison temps constant. | Totp.cs | RAS |
| `backend/src/Nexora.Infrastructure/Identity/UserAssignmentService.cs` | IMPLÉMENTÉ, MAIS NON VÉRIFIÉ | MOYEN | Anti-élévation à l'octroi, rôles protégés, portée groupe réservée. Pas de garde sur suspension/retrait d'un utilisateur plus privilégié (A-010) ; retrait sans invalidation de session. | UserAssignmentService.cs:223-351 | Voir A-010 |

### Infrastructure/Integrations

| Fichier | Verdict | Gravité | Observations | Preuves | Action |
|---|---|---|---|---|---|
| `backend/src/Nexora.Infrastructure/Integrations/PaymentGatewayService.cs` | IMPLÉMENTÉ, MAIS NON VÉRIFIÉ | FAIBLE | Intention idempotente (clé unique, 23505 rejoué), contrôle du montant fournisseur, confirmation via RecordCustomerPaymentAsync (operation_uuid = id intention), statut failed en cas d'échec. Numéro de commande : horodatage + Random.Shared (collision très improbable, non cryptographique). | PaymentGatewayService.cs:60-260 | RAS ; tester avec le bac à sable |
| `backend/src/Nexora.Infrastructure/Integrations/PaymentProviders.cs` | NON VÉRIFIABLE | MOYEN | Simulation (refusée en production), carte (déclarée « non disponible », honnête), MonCash : vrai client HTTP (oauth/token, CreatePayment, RetrieveOrderPayment) non testable sans identifiants Digicel ; pas de remboursement MonCash. | PaymentProviders.cs:57-200 | Recette avec un compte bac à sable MonCash |
| `backend/src/Nexora.Infrastructure/Integrations/WebhookServices.cs` | IMPLÉMENTÉ, MAIS NON VÉRIFIÉ | - | Entrants : signature HMAC vérifiée, rejet journalisé, déduplication par event id ; sortants : URL validée à l'enregistrement. | WebhookServices.cs:16-63,253,382 | RAS |
| `backend/src/Nexora.Infrastructure/Integrations/WebhookTargetPolicy.cs` | FONCTIONNEL VÉRIFIÉ | - | Protection SSRF : refus boucle locale, privé, CGNAT, lien local/métadonnées, IPv6 ULA ; contrôle après résolution DNS dans ConnectCallback ; pas de redirection suivie. | WebhookTargetPolicy.cs:1-93 ; WebhookTargetPolicyTests (tests unitaires verts) | RAS |

### Infrastructure/Inventory

| Fichier | Verdict | Gravité | Observations | Preuves | Action |
|---|---|---|---|---|---|
| `backend/src/Nexora.Infrastructure/Inventory/CatalogInventoryStores.cs` | IMPLÉMENTÉ, MAIS NON VÉRIFIÉ | - | Magasins EF du stock ; les soldes sont tenus par triggers (contrôle d'intégrité : 0 écart sur nexora_recette). | CatalogInventoryStores.cs ; integrity.sql | RAS |
| `backend/src/Nexora.Infrastructure/Inventory/StockServices.cs` | IMPLÉMENTÉ, MAIS NON VÉRIFIÉ | FAIBLE | Alertes et paramètres ; catch silencieux (valeur par défaut 30) ; alertes possiblement en double (observation). | StockServices.cs:22,172,392 | Journaliser |

### Infrastructure/Notifications

| Fichier | Verdict | Gravité | Observations | Preuves | Action |
|---|---|---|---|---|---|
| `backend/src/Nexora.Infrastructure/Notifications/FcmPushChannel.cs` | NON VÉRIFIABLE | - | FirebaseAdmin ; nécessite un compte de service. | FcmPushChannel.cs | Recette avec un projet Firebase |
| `backend/src/Nexora.Infrastructure/Notifications/SmtpEmailChannel.cs` | NON VÉRIFIABLE | - | Vrai envoi MailKit ; « non configuré » renvoyé honnêtement sans identifiants ; erreurs transitoires réessayées. | SmtpEmailChannel.cs:45-121 | Recette avec un relais SMTP |
| `backend/src/Nexora.Infrastructure/Notifications/TwilioSmsChannel.cs` | NON VÉRIFIABLE | - | Vrai SDK Twilio ; non configuré signalé. | TwilioSmsChannel.cs:47-128 | Recette avec un compte Twilio |

### Infrastructure/Operations

| Fichier | Verdict | Gravité | Observations | Preuves | Action |
|---|---|---|---|---|---|
| `backend/src/Nexora.Infrastructure/Operations/SqlDataCorrectionService.cs` | DÉFECTUEUX | CRITIQUE | Liste fermée table/champ (pas d'injection), demandeur ≠ approbateur ; MAIS lecture, liste et UPDATE en portée système sans contrôle d'entreprise : fuite et modification de données entre entreprises (A-031). | SqlDataCorrectionService.cs:13-16,49-120 | Borner à l'entreprise active ou réserver à un rôle plateforme |
| `backend/src/Nexora.Infrastructure/Operations/SqlFeatureFlagService.cs` | PRÉSENT MAIS NON CONNECTÉ | FAIBLE | Bascules globales/entreprise/utilisateur créées en portée système ; IsEnabledAsync appelé nulle part (grep). | SqlFeatureFlagService.cs ; grep IFeatureFlagService | Brancher ou retirer ; borner à l'entreprise |
| `backend/src/Nexora.Infrastructure/Operations/SqlJobRegistry.cs` | IMPLÉMENTÉ, MAIS NON VÉRIFIÉ | ÉLEVÉ | Tâches globales (job_definition sans company_id) : pause/reprise/exécution par un rôle d'entreprise (A-031). | SqlJobRegistry.cs:67-110 | Réserver à la plateforme |
| `backend/src/Nexora.Infrastructure/Operations/SqlMaintenanceService.cs` | DÉFECTUEUX | CRITIQUE | Fenêtre de maintenance globale (table sans company_id) pilotable par operations.manage (rôle d'entreprise owner_direction) → gel des écritures de toutes les entreprises (A-031). | SqlMaintenanceService.cs:44-114 ; pg_class | Maintenance réservée à la plateforme |
| `backend/src/Nexora.Infrastructure/Operations/SqlQueueAdminService.cs` | IMPLÉMENTÉ, MAIS NON VÉRIFIÉ | - | Files de notifications et webhooks bornées à l'entreprise active (company_id = actif) : correct. | SqlQueueAdminService.cs:18-122 | RAS |
| `backend/src/Nexora.Infrastructure/Operations/SqlRetentionPurgeService.cs` | IMPLÉMENTÉ, MAIS NON VÉRIFIÉ | MOYEN | Purge en portée système ; companyId nul = toutes les entreprises (A-031) ; requêtes paramétrées. | SqlRetentionPurgeService.cs:100-230 | Borner à l'entreprise pour les rôles d'entreprise |

### Infrastructure/Payroll

| Fichier | Verdict | Gravité | Observations | Preuves | Action |
|---|---|---|---|---|---|
| `backend/src/Nexora.Infrastructure/Payroll/SqlPayrollService.Calculation.cs` | IMPLÉMENTÉ, MAIS NON VÉRIFIÉ | - | Calcul sous verrou FOR UPDATE de la période ; tests d'intégration verts. | SqlPayrollService.Calculation.cs:74 | RAS |
| `backend/src/Nexora.Infrastructure/Payroll/SqlPayrollService.Payments.cs` | IMPLÉMENTÉ, MAIS NON VÉRIFIÉ | MOYEN | Paiement via moteur (SalaryPaid) ; annulation : AdjustBalanceAsync (exposé à A-018). | SqlPayrollService.Payments.cs:141,231,249 | Après correction A-018 |
| `backend/src/Nexora.Infrastructure/Payroll/SqlPayrollService.Workflow.cs` | IMPLÉMENTÉ, MAIS NON VÉRIFIÉ | - | Approbation verrouillée, écriture PayrollApproved par le moteur. | SqlPayrollService.Workflow.cs:27,254 | RAS |
| `backend/src/Nexora.Infrastructure/Payroll/SqlPayrollService.cs` | IMPLÉMENTÉ, MAIS NON VÉRIFIÉ | FAIBLE | Cycle de paie ; arrondi AwayFromZero (PayrollSupport.Money) différent du ToEven du moteur financier. | PayrollSupport.cs:38 | Unifier la règle d'arrondi |

### Infrastructure/Persistence

| Fichier | Verdict | Gravité | Observations | Preuves | Action |
|---|---|---|---|---|---|
| `backend/src/Nexora.Infrastructure/Persistence/DbConnectionFactory.cs` | NON APPLICABLE | - | Options. | DbConnectionFactory.cs | RAS |
| `backend/src/Nexora.Infrastructure/Persistence/NexoraDbContext.cs` | FONCTIONNEL VÉRIFIÉ | - | Mappage « base d'abord » exercé par 496 tests d'intégration verts. | test-integ2.log | RAS |
| `backend/src/Nexora.Infrastructure/Persistence/Repositories.cs` | IMPLÉMENTÉ, MAIS NON VÉRIFIÉ | FAIBLE | Verrouillage 5 échecs/15 min (déni de service ciblé possible). | Repositories.cs:223-244 | RAS |
| `backend/src/Nexora.Infrastructure/Persistence/RlsConnectionInterceptor.cs` | IMPLÉMENTÉ, MAIS NON VÉRIFIÉ | MOYEN | Script SET construit par interpolation de GUID formatés (sûr) ; posé à l'ouverture de connexion seulement (A-009). | RlsConnectionInterceptor.cs:36-70 | Voir A-009 |
| `backend/src/Nexora.Infrastructure/Persistence/UnitOfWork.cs` | IMPLÉMENTÉ, MAIS NON VÉRIFIÉ | FAIBLE | Transaction imbriquée ; message brut de contrainte PostgreSQL renvoyé à l'utilisateur. | UnitOfWork.cs:143-173 | Traduire les messages |

### Infrastructure/Sales

| Fichier | Verdict | Gravité | Observations | Preuves | Action |
|---|---|---|---|---|---|
| `backend/src/Nexora.Infrastructure/Sales/ManagerOverrideService.cs` | IMPLÉMENTÉ, MAIS NON VÉRIFIÉ | FAIBLE | Validation sur place avec identifiants du responsable (verrouillage, MFA, journal, ≠ caissier, permission dans l'entreprise), exécution au nom du responsable. Un caissier peut verrouiller le compte du responsable par 5 essais (déni de service) ; limitée à 10/min/IP. | ManagerOverrideService.cs:37-150 | Compteur d'échecs propre à ce canal |
| `backend/src/Nexora.Infrastructure/Sales/PosControlsInfrastructure.cs` | IMPLÉMENTÉ, MAIS NON VÉRIFIÉ | MOYEN | Notifications d'approbation, demandes de retour (≤ vendu − retourné − en attente ; approbation = exécution atomique, auto-validation interdite), remises exceptionnelles (limite approbateur, expiration 2 h, usage unique), lectures sessions, recherche/scan produit. Défauts : l'approbation d'un retour ne revérifie pas que la session citée est encore ouverte ; scan sans pack_size ni code poids (A-015) ; lecture de toute vente avec pos.view. | PosControlsInfrastructure.cs:146-296,640-770 | Voir A-002, A-015 |
| `backend/src/Nexora.Infrastructure/Sales/PosOfflineInfrastructure.cs` | IMPLÉMENTÉ, MAIS NON VÉRIFIÉ | - | Enrôlement d'appareil (révoqué/suspendu refusé), profil hors ligne ≤ 5 000 articles avec prix et codes. | PosOfflineInfrastructure.cs | RAS |
| `backend/src/Nexora.Infrastructure/Sales/SalesInfrastructure.cs` | DÉFECTUEUX | ÉLEVÉ | SqlSalesPricing : coupon à montant fixe appliqué dans la boucle par ligne (A-012 confirmé : 50 → 100 sur 2 lignes) ; « price_list_id IS NULL » : listes de prix jamais appliquées (A-013). | SalesInfrastructure.cs:29+ ; scénario s3_coupon.py | Appliquer le coupon une fois au panier ; résoudre la liste de prix du client |
| `backend/src/Nexora.Infrastructure/Sales/TenderOptionsQuery.cs` | IMPLÉMENTÉ, MAIS NON VÉRIFIÉ | - | Moyens de paiement et comptes compatibles par devise. | TenderOptionsQuery.cs | RAS |

### Infrastructure/Security

| Fichier | Verdict | Gravité | Observations | Preuves | Action |
|---|---|---|---|---|---|
| `backend/src/Nexora.Infrastructure/Security/ApprovalAndSession.cs` | IMPLÉMENTÉ, MAIS NON VÉRIFIÉ | FAIBLE | Sessions, rafraîchissement avec rotation et rejeu, inactivité ; révocation bornée à l'utilisateur pour la session mais pas pour ses jetons de rafraîchissement (A-032). | ApprovalAndSession.cs:292-510 | Conditionner la seconde requête |

### Infrastructure/Sync

| Fichier | Verdict | Gravité | Observations | Preuves | Action |
|---|---|---|---|---|---|
| `backend/src/Nexora.Infrastructure/Sync/SyncServices.cs` | IMPLÉMENTÉ, MAIS NON VÉRIFIÉ | MOYEN | Lot idempotent par operation_uuid, permission par type d'opération, politique hors ligne, conflits stock/crédit, résolution « le poste a raison ». Défaut : estimation du crédit hors ligne inopérante sans prix forcé (A-016) ; appareil non lié à l'utilisateur. | SyncServices.cs:35-164,330-440 | Voir A-016 |

### Infrastructure/Tenancy

| Fichier | Verdict | Gravité | Observations | Preuves | Action |
|---|---|---|---|---|---|
| `backend/src/Nexora.Infrastructure/Tenancy/TenantContext.cs` | IMPLÉMENTÉ, MAIS NON VÉRIFIÉ | - | Portée système par compteur. | TenantContext.cs | RAS |

### Infrastructure/Time

| Fichier | Verdict | Gravité | Observations | Preuves | Action |
|---|---|---|---|---|---|
| `backend/src/Nexora.Infrastructure/Time/BusinessCalendar.cs` | IMPLÉMENTÉ, MAIS NON VÉRIFIÉ | - | Fuseau de l'entreprise active. | BusinessCalendar.cs | RAS |
| `backend/src/Nexora.Infrastructure/Time/SystemDateTimeProvider.cs` | NON APPLICABLE | - | Horloge. | SystemDateTimeProvider.cs | RAS |

### Outils

| Fichier | Verdict | Gravité | Observations | Preuves | Action |
|---|---|---|---|---|---|
| `backend/tools/Nexora.RecetteSeed/Nexora.RecetteSeed.csproj` | NON VÉRIFIABLE | - | Hors solution (non compilé par dotnet build Nexora.sln). | Nexora.sln | RAS |

### Racine

| Fichier | Verdict | Gravité | Observations | Preuves | Action |
|---|---|---|---|---|---|
| `.dockerignore` | NON APPLICABLE | - | Exclut tests, docs, outils. | .dockerignore | RAS |
| `.editorconfig` | NON APPLICABLE | - | Conventions. | .editorconfig | RAS |
| `.gitattributes` | NON APPLICABLE | - | LF pour scripts/conteneurs. | .gitattributes | RAS |
| `.gitignore` | NON APPLICABLE | - | Secrets de production ignorés (vérifié check-ignore). | .gitignore:51-54 | RAS |
| `backend/Directory.Build.props` | NON APPLICABLE | - | net10.0, nullable, TreatWarningsAsErrors, analyseurs non bloquants, NuGetAudit direct. | Directory.Build.props | RAS |
| `backend/Directory.Packages.props` | NON APPLICABLE | - | Versions centralisées ; EF Core 10.0.12, Npgsql 10.0.3, ClosedXML, PDFsharp-MigraDoc, MailKit, Twilio, FirebaseAdmin. | Directory.Packages.props | RAS |
| `backend/Dockerfile` | NON VÉRIFIABLE | - | Build multi-étapes, utilisateur non-root, healthcheck par le binaire ; non construit (Docker arrêté). | Dockerfile | RAS |
| `backend/Nexora.sln` | NON APPLICABLE | - | 8 projets (5 src + 3 tests) ; RecetteSeed hors solution. | Nexora.sln | RAS |

### Tests/ApiTests

| Fichier | Verdict | Gravité | Observations | Preuves | Action |
|---|---|---|---|---|---|
| `backend/tests/Nexora.ApiTests/Nexora.ApiTests.csproj` | DÉFECTUEUX | ÉLEVÉ | Modification NON COMMITÉE qui retire <ProjectReference Nexora.Api> : 75 erreurs CS0246/CS0234 (« Program », DatabaseSetupRequest… introuvables) ; dotnet build Nexora.sln échoue (exit 1) ; la version commitée (HEAD) contient la référence. | git diff ; build-worktree.log (75 erreurs, toutes dans ce projet) | Restaurer la référence (git checkout du fichier) — décision utilisateur |

### Tests/IntegrationTests

| Fichier | Verdict | Gravité | Observations | Preuves | Action |
|---|---|---|---|---|---|
| `backend/tests/Nexora.IntegrationTests/Nexora.IntegrationTests.csproj` | FONCTIONNEL VÉRIFIÉ | - | Compile. | build-worktree.log | RAS |

### Tests/UnitTests

| Fichier | Verdict | Gravité | Observations | Preuves | Action |
|---|---|---|---|---|---|
| `backend/tests/Nexora.UnitTests/Nexora.UnitTests.csproj` | FONCTIONNEL VÉRIFIÉ | - | Compile. | build-worktree.log | RAS |

## 4. Fichiers non lus ligne à ligne : répartition par module

| Module | Fichiers restants |
|---|---|
| Api/Controllers | 67 |
| Frontend/shared | 25 |
| Infrastructure/Reporting | 15 |
| Frontend/pos | 14 |
| Frontend/purchasing | 11 |
| Infrastructure/Printing | 10 |
| Infrastructure/CI | 9 |
| Frontend/config | 9 |
| Infrastructure/Payroll | 8 |
| Frontend/accounting | 8 |
| Frontend/admin | 8 |
| Frontend/catalog | 8 |
| Frontend/inventory | 8 |
| Infrastructure/Platform | 7 |
| Frontend/finance | 7 |
| Frontend/hr | 7 |
| Frontend/sales | 7 |
| Contracts/Finance | 6 |
| Infrastructure/Customers | 6 |
| Infrastructure/Intelligence | 6 |
| Infrastructure/Notifications | 6 |
| Base/scripts | 6 |
| Frontend/app | 6 |
| Frontend/credit | 6 |
| Application/Finance | 5 |
| Infrastructure/Catalog | 5 |
| Infrastructure/Ged | 5 |
| Infrastructure/Migration | 5 |
| Frontend/auth | 5 |
| Frontend/dashboard | 5 |
| Frontend/ged | 5 |
| Frontend/mobile | 5 |
| Frontend/reports | 5 |
| Infrastructure/Group | 4 |
| Infrastructure/Mobile | 4 |
| Infrastructure/Persistence | 4 |
| Infrastructure/Setup | 4 |
| Frontend/customers | 4 |
| Frontend/intelligence | 4 |
| Frontend/notifications | 4 |
| Frontend/operations | 4 |
| Racine | 3 |
| Application/Identity | 3 |
| Application/Reporting | 3 |
| Application/Security | 3 |
| Contracts/Reporting | 3 |
| Contracts/Sales | 3 |
| Infrastructure/Finance | 3 |
| Infrastructure/Integrations | 3 |
| Infrastructure/Operations | 3 |
| Infrastructure/Security | 3 |
| Outils | 3 |
| Frontend/ai | 3 |
| Frontend/documents | 3 |
| Frontend/group | 3 |
| Frontend/integrations | 3 |
| Frontend/restaurant | 3 |
| Frontend/sectors | 3 |
| Frontend/security | 3 |
| Application/Integrations | 2 |
| Application/Operations | 2 |
| Application/Preferences | 2 |
| Application/Printing | 2 |
| Contracts/Integrations | 2 |
| Contracts/Operations | 2 |
| Contracts/Preferences | 2 |
| Contracts/Purchasing | 2 |
| Infrastructure/Administration | 2 |
| Infrastructure/Experience | 2 |
| Infrastructure/Inventory | 2 |
| Infrastructure/Preferences | 2 |
| Infrastructure/Purchasing | 2 |
| Infrastructure/Scheduling | 2 |
| Frontend/billing | 2 |
| Frontend/continuity | 2 |
| Frontend/racine | 2 |
| Frontend/migration | 2 |
| Frontend/parties | 2 |
| Frontend/platform | 2 |
| Frontend/settings | 2 |
| Frontend/setup | 2 |
| Frontend/sync | 2 |
| Application/Administration | 1 |
| Application/Catalog | 1 |
| Application/Configuration | 1 |
| Application/Continuity | 1 |
| Application/Customers | 1 |
| Application/Documents | 1 |
| Application/Experience | 1 |
| Application/Ged | 1 |
| Application/Group | 1 |
| Application/Intelligence | 1 |
| Application/Marketplace | 1 |
| Application/Migration | 1 |
| Application/Mobile | 1 |
| Application/Notifications | 1 |
| Application/Parties | 1 |
| Application/Payroll | 1 |
| Application/Platform | 1 |
| Application/Purchasing | 1 |
| Application/Restaurant | 1 |
| Application/Sectors | 1 |
| Application/Setup | 1 |
| Application/Sync | 1 |
| Contracts/Customers | 1 |
| Contracts/Documents | 1 |
| Contracts/Experience | 1 |
| Contracts/Ged | 1 |
| Contracts/Group | 1 |
| Contracts/Identity | 1 |
| Contracts/Intelligence | 1 |
| Contracts/Inventory | 1 |
| Contracts/Marketplace | 1 |
| Contracts/Migration | 1 |
| Contracts/Mobile | 1 |
| Contracts/Notifications | 1 |
| Contracts/Organization | 1 |
| Contracts/Parties | 1 |
| Contracts/Payroll | 1 |
| Contracts/Platform | 1 |
| Contracts/Printing | 1 |
| Contracts/Restaurant | 1 |
| Contracts/Sectors | 1 |
| Contracts/Security | 1 |
| Contracts/Setup | 1 |
| Contracts/Sync | 1 |
| Infrastructure/Configuration | 1 |
| Infrastructure/Continuity | 1 |
| Infrastructure/Credit | 1 |
| Infrastructure | 1 |
| Infrastructure/Documents | 1 |
| Infrastructure/Marketplace | 1 |
| Infrastructure/Parties | 1 |
| Infrastructure/Restaurant | 1 |
| Infrastructure/Sales | 1 |
| Infrastructure/Sectors | 1 |
| Infrastructure/Sync | 1 |
| Frontend/organization | 1 |

Ces fichiers ne sont pas déclarés fonctionnels : leur verdict dans le CSV est « IMPLÉMENTÉ, MAIS NON VÉRIFIÉ » et leur action « À lire lors d'un lot ultérieur ». Contrôles automatiques passés sur eux : compilation sans erreur ni avertissement (HEAD), recherche de `NotImplementedException`/`TODO`/`FIXME`/simulation/mock/`catch` silencieux, croisement des 637 appels frontend avec les routes backend.
