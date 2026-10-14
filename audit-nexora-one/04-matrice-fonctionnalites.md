# 04 — Matrice de vérité des fonctionnalités

> Audit du 2026-10-13, HEAD `e5e243d`. Une cellule « ✔ » signifie que l'élément **existe et est relié** (code lu ou route
> appelée), pas qu'il fonctionne. La colonne « Tests exécutés » ne cite que des tests réellement lancés pendant l'audit.
> Abréviations : **INT** = tests d'intégration (496/496 verts, exécutés en superutilisateur, RLS non exercée — T-001) ;
> **API** = tests d'API (190/194) ; **UNIT** = tests unitaires (88/88) ; **VIT** = Vitest (275/275) ;
> **EXEC** = scénario exécuté par l'audit sur l'API de HEAD et une base clonée ; **E2E** = Playwright, **non exécuté**.

## 1. Matrice générale

| Module | Fonctionnalité | Frontend | Backend | Base de données | Tests exécutés | Verdict |
|---|---|---|---|---|---|---|
| Authentification | Connexion mot de passe | ✔ écran de connexion | ✔ `LoginHandler`, BCrypt, verrouillage | `app_user`, `user_session` | UNIT LoginHandlerTests, API, EXEC (connexions des scénarios) | **FONCTIONNEL VÉRIFIÉ** (énumération possible — A-011) |
| Authentification | Jeton 15 min + rafraîchissement par cookie HttpOnly, rotation, détection de vol | ✔ `apiClient` (un seul refresh) | ✔ | `auth_refresh_token` | API SessionSecurityEndpointsTests (2 échecs = artefact d'audit, voir 08) | IMPLÉMENTÉ, MAIS NON VÉRIFIÉ (tests perturbés par l'incident d'audit) |
| Authentification | MFA TOTP, codes de secours, MFA imposée par rôle | ✔ | ✔ secret chiffré AES-GCM | `mfa_*` | INT MfaAuthenticationTests, StepUpMfaTests | **FONCTIONNEL VÉRIFIÉ** — réinitialisation par un admin défectueuse (codes non supprimés, A-009) |
| Authentification | Confirmation forte (step-up) liée à la session | ✔ fenêtre de confirmation | ✔ 41 actions | `step_up_challenge` | EXEC (refus en session B, acceptation en session A) | **FONCTIONNEL VÉRIFIÉ** |
| Entreprises | Création par l'assistant d'installation / bootstrap | ✔ `/setup` | ✔ `SetupInstaller`, CLI `bootstrap` | migrations embarquées | API SetupWizardTests | IMPLÉMENTÉ, MAIS NON VÉRIFIÉ par l'audit (installation neuve non rejouée) |
| Entreprises | Isolation multi-entreprise | — | ✔ `TenantResolutionMiddleware`, RLS | RLS forcée sur 250/291 tables | API MultiTenantIsolationTests ; EXEC `X-Company-Id` étranger → 403, invalide → 400 ; catalogue PG | **FONCTIONNEL VÉRIFIÉ** au niveau requête ; défense en profondeur partielle (A-009) |
| Utilisateurs | Rôles, permissions, affectations temporaires | ✔ centre d'administration | ✔ garde anti-élévation à l'attribution | `role`, `user_role` (sans RLS) | INT IdentityAccessTests, AdministrationTests, PermissionCatalogTests | PARTIELLEMENT IMPLÉMENTÉ — suspension/retrait sans garde (A-010) |
| Produits | Création, unités, conversions, codes-barres | ✔ catalogue complet | ✔ | `product`, `product_barcode`… | INT Catalog*Tests | IMPLÉMENTÉ, MAIS NON VÉRIFIÉ par l'audit ; `pack_size` ignoré en caisse (A-015) |
| Produits | Listes de prix | ✔ | ✔ moteur explicable (simulateur) | `price_list` | INT CatalogPricingTests | **PRÉSENT MAIS NON CONNECTÉ** aux ventes (A-013) |
| Produits | Promotions et coupons | ✔ | ✔ `PromotionCalculator` | `promotion`, `coupon` | UNIT PromotionCalculatorTests ; EXEC coupon | **DÉFECTUEUX** — coupon fixe multiplié par ligne (A-012) |
| Stock | Mouvements, soldes, coût moyen | ✔ centre de stock | ✔ `IStockLedger` seul écrivain | triggers sur `stock_balance` | INT StockLifecycleTests, StockOperationsTests ; Données : 0 écart solde/mouvements (recette) | **FONCTIONNEL VÉRIFIÉ** |
| Stock | Inventaire physique | ✔ | ✔ | `inventory_count*` | INT ; EXEC (état intermédiaire 411 figé / 409 courant) | **DÉFECTUEUX** — écart sur quantité figée, lots ignorés (A-007) |
| Stock | Lots, rappels, alertes, transferts | ✔ | ✔ | `stock_lot`… | INT StockLifecycleTests | IMPLÉMENTÉ, MAIS NON VÉRIFIÉ par l'audit |
| POS | Enregistrement d'une vente | ✔ caisse | ✔ `SalesService.CheckoutAsync` (atomique) | `sale`, `sale_line`, écritures | INT PosSalesTests ; EXEC ≈ 26 ventes | **FONCTIONNEL VÉRIFIÉ** pour le cas nominal ; `SoldAt` client accepté (A-003) |
| POS | Multi-paiement, rendu monnaie | ✔ | ✔ `/pos/tender-options` | `payment` | INT PosTenderTests | IMPLÉMENTÉ, MAIS NON VÉRIFIÉ par l'audit |
| POS | Retour / remboursement espèces | ✔ | ✔ `SalesReturnService` | `sale_return` ; `cash_movement` jamais écrit | EXEC | **DÉFECTUEUX** (A-002, A-005) |
| POS | Session de caisse : ouverture, apports/sorties, clôture | ✔ | ✔ `CashSessionService` | `cash_session`, `cash_movement` | EXEC (clôture) | PARTIELLEMENT IMPLÉMENTÉ — vente acceptée après clôture (A-003), apports/sorties hors comptabilité (A-019) |
| POS | Caisse hors ligne | ✔ SW, file chiffrée | ✔ `SyncServices` (idempotence `operation_uuid`) | `sync_*` | INT OfflineSyncTests, PosOfflineTests ; VIT offlineQueue | IMPLÉMENTÉ, MAIS NON VÉRIFIÉ en navigateur ; plafond de crédit hors ligne inopérant (A-016) |
| Ventes | Devis → commande → acompte → facture | ✔ | ✔ `QuoteService`, `OrderService` | `quote`, `sales_order`, `issued_document` | INT SalesOrderTests, CommercialDocumentsTests | IMPLÉMENTÉ, MAIS NON VÉRIFIÉ par l'audit ; conversion de devis non atomique (observation) |
| Clients | Compte client, versements, avoirs, remboursements | ✔ | ✔ `SqlCustomerAccountService` (verrous, devises) | `customer_*` (journal append-only) | INT CustomerAccountTests, CustomerAccountGapsTests | **FONCTIONNEL VÉRIFIÉ** ; contourné par `POST /sales/{id}/payments` (A-014) |
| Crédit | Recouvrement, promesses, litiges, blocages | ✔ | ✔ | `credit_*` | INT CustomerCollectionsTests | IMPLÉMENTÉ, MAIS NON VÉRIFIÉ par l'audit |
| Achats | Commande, réception, facture, rapprochement 3 voies | ✔ | ✔ | `purchase_*`, compte 4081 (V096) | INT PurchasingFlowTests, PurchaseThreeWayMatchTests, GoodsReceivedNotInvoicedTests | IMPLÉMENTÉ, MAIS NON VÉRIFIÉ par l'audit (lecture partielle) |
| Fournisseurs | Dettes, avoirs, paiements | ✔ | ✔ | `supplier_payable` | INT | PARTIELLEMENT IMPLÉMENTÉ — âgé et prévision ignorent les avoirs (A-022) |
| Finance | Comptes financiers, transferts, dépenses (4 yeux) | ✔ | ✔ | `financial_account`, `financial_transaction` | INT TreasuryManagementTests ; EXEC transferts | **DÉFECTUEUX** sous concurrence (A-018) ; pas de solde d'ouverture (A-023) |
| Finance | Multidevise / taux de change | partiel (choix de devise) | lecture seule des taux | `exchange_rate` globale, jamais écrite | Données (2 taux de démo) | **ABSENT** côté saisie (A-017) |
| Comptabilité | Moteur en partie double (événement → écriture) | — | ✔ `FinancialEngine`, `PostingRuleSet` | `journal_entry*` | INT FinancialEngineTests ; UNIT PostingRuleSetTests ; Données : 0 écriture déséquilibrée (2 580 en recette) | **FONCTIONNEL VÉRIFIÉ** (équilibre) ; idempotence concurrente imparfaite (A-004) |
| Comptabilité | Écritures manuelles, périodes, plan comptable | ✔ | ✔ | | INT GeneralAccountingTests, AccountingFinalisationTests | IMPLÉMENTÉ, MAIS NON VÉRIFIÉ par l'audit |
| Comptabilité | Clôture d'exercice, à-nouveaux, bilan | ✔ | ✔ | | INT (1er exercice seulement) | **DÉFECTUEUX** (A-020, A-021) |
| Comptabilité | Extourne | ✔ | ✔ `ReversalService` | | INT | PARTIELLEMENT IMPLÉMENTÉ — grand livre seul (A-006) |
| RH / paie | Employés, contrats, absences, heures sup. | ✔ | ✔ | 25 tables paie/RH | INT PayrollTests | IMPLÉMENTÉ, MAIS NON VÉRIFIÉ par l'audit |
| RH / paie | Calcul, validation, paiement, bulletins, avances | ✔ | ✔ moteur financier (journal PA) | | UNIT PayrollCalculatorTests ; INT PayrollTests ; API PayrollEndpointsTests | IMPLÉMENTÉ, MAIS NON VÉRIFIÉ par l'audit ; arrondi différent (A-026) |
| Rapports | Tableau de bord, KPI, rapports | ✔ | ✔ SQL brut | `analytics_snapshot` | INT DashboardReportingTests, BiReportingTests, ReportingTrustTests | PARTIELLEMENT IMPLÉMENTÉ — « semaine » vide le dimanche (A-008) |
| Rapports | États financiers, balances âgées | ✔ | ✔ | | INT | **DÉFECTUEUX** après à-nouveaux (A-021) ; âgé incomplet (A-022) |
| Intelligence | Recommandations, scores, assistant | ✔ | ✔ règles déterministes | `recommendation*` | INT IntelligenceEngineTests | IMPLÉMENTÉ, MAIS NON VÉRIFIÉ — pas d'IA générative : assistant à règles (annoncé comme tel) |
| Paiements | Simulation | ✔ libellé SIMULATION | ✔ refusée en production | `payment_intent.mode` | INT | **SIMULÉ OU STATIQUE**, déclaré comme tel |
| Paiements | MonCash | ✔ | ✔ vrai client HTTP | | aucun (pas d'identifiants) | **NON VÉRIFIABLE** |
| Paiements | Carte en ligne | ✔ | annoncé « non disponible » | | — | **ABSENT** (déclaré honnêtement) |
| Notifications | E-mail SMTP, SMS Twilio, push FCM | ✔ | ✔ vrais SDK, « non configuré » honnête | `notification_outbox` | INT NotificationEngineTests | **NON VÉRIFIABLE** sans comptes fournisseurs |
| Intégrations | Webhooks entrants (HMAC) et sortants (SSRF) | ✔ | ✔ | `webhook_*` | UNIT WebhookTargetPolicyTests ; INT IntegrationsEcosystemTests | **FONCTIONNEL VÉRIFIÉ** (politique SSRF par tests unitaires) |
| Intégrations | Clés d'API, marketplace | ✔ | ✔ | `api_key` | API ApiKeyAuthenticationTests, MarketplaceEndpointsTests | IMPLÉMENTÉ, MAIS NON VÉRIFIÉ par l'audit |
| GED | Documents, versions, partage public, signatures | ✔ | ✔ | `document*` | INT GedDocumentsTests ; API GedEndpointsTests | IMPLÉMENTÉ, MAIS NON VÉRIFIÉ par l'audit |
| Groupe | Consolidation, intercompagnie | ✔ | ✔ | | INT GroupConsolidationTests ; API GroupAccessEndpointsTests | IMPLÉMENTÉ, MAIS NON VÉRIFIÉ ; dépend des taux de change (A-017) |
| Restaurant / terrain | Salle, cuisine, recettes ; tournées, preuves | ✔ | ✔ | | INT SectorModulesTests, MobileFieldOpsTests | IMPLÉMENTÉ, MAIS NON VÉRIFIÉ par l'audit |
| Continuité / exploitation | Sauvegardes, restauration, incidents, sondes | ✔ | ✔ | | INT ; API OpsMaintenanceEndpointsTests (1 échec non reproduit) | IMPLÉMENTÉ, MAIS NON VÉRIFIÉ ; métriques exposées (A-024) |
| Exploitation | Console `/ops/*` (corrections de données, maintenance, tâches, purge, bascules) | ✔ | ✔ portée plateforme | `maintenance_window`, `job_definition` globales | INT OperationsMaintenanceTests (superutilisateur) | **DÉFECTUEUX** en déploiement partagé : isolation contournée (A-031, CRITIQUE) ; bascules de fonctionnalités PRÉSENT MAIS NON CONNECTÉ |
| Plateforme SaaS | Abonnements, quotas, licences | partiel (pas d'écran tickets/coupons/licences) | ✔ | 17 tables plateforme sans RLS | INT PlatformBillingTests | PARTIELLEMENT IMPLÉMENTÉ (côté éditeur) |
| Mobile natif | Application | ❌ (web mobile) | API | | — | **ABSENT** (déclaré) |

## 2. État de chaque module (section D du rapport)

### Authentification et sécurité des sessions
1. Existant : connexion, verrouillage, MFA TOTP, codes de secours, step-up, sessions, appareils, refresh tokens.
2. Vérifié : connexion (exécutions), step-up lié à `sid` (EXEC), isolation `X-Company-Id` (EXEC), MFA (INT).
3. Partiel : réinitialisation MFA par un administrateur (codes de secours conservés, A-009).
4. Simulé : rien.
5. Absent : WebAuthn, SSO (déclarés absents par le projet).
6. Problèmes : énumération de comptes, TOTP rejouable (A-011).
7. Dépendances : `PermissionService`, `RlsConnectionInterceptor`, `ISessionCacheInvalidator`.
8. Risques : faibles en l'état ; la défense en profondeur RLS est plus mince qu'annoncé.
9. Tests manquants : réinitialisation MFA avec le rôle `nexora_app` ; rejeu TOTP ; temps de réponse constant.
10. Corrections : A-009 (portée système dans les transactions), A-011.

### Utilisateurs, rôles, permissions
1. Existant : rôles personnalisés, affectations temporaires et révocables, catalogue de permissions, deny-by-default.
2. Vérifié : politique « authentifié par défaut » (lecture), 1 029 points d'accès inventoriés (05).
3. Partiel : garde anti-élévation limitée à l'attribution (A-010).
6. Problèmes : retrait d'affectation sans invalidation de session ; `app_user`/`user_role`/`access_scope` sans RLS.
8. Risque : un administrateur d'utilisateurs peut suspendre la direction.
9. Tests manquants : suspension/retrait d'un compte plus privilégié.
10. Corrections : A-010.

### Catalogue, prix, promotions
1. Existant : produits, unités, conversions, codes-barres, listes de prix, promotions, coupons, simulateur.
2. Vérifié : cas nominal de prix en caisse (EXEC).
3. Partiel / non connecté : listes de prix absentes des ventes (A-013) ; `pack_size` et poids intégré (A-015).
6. Défaut : coupon fixe multiplié (A-012).
8. Risque : perte de marge directe, sans plafond caissier sur la remise coupon.
9. Tests manquants : panier multi-lignes avec coupon fixe ; vente à un client doté d'une liste de prix.
10. Corrections : A-012, A-013, A-015.

### Stock et inventaires
1. Existant : grand livre de stock, triggers de solde et coût moyen, lots, rappels, transferts, alertes, inventaires.
2. Vérifié : cohérence soldes = Σ mouvements sur 5 836 mouvements (Données).
6. Défaut : inventaire en concurrence avec les ventes (A-007).
8. Risque : stock faux après chaque inventaire tenu magasin ouvert.
9. Tests manquants : « ouverture → vente → comptage → validation », inventaire d'un produit suivi par lot.
10. Corrections : A-007.

### Point de vente, caisse et retours
1. Existant : encaissement atomique (vente + stock + caisse + créance + écriture), multi-paiement, retours, sessions, hors ligne.
2. Vérifié : vente nominale, clôture (EXEC).
4. Simulé : rien.
6. Défauts : A-002 (CRITIQUE), A-003, A-005, A-014, A-019.
8. Risques : écarts de caisse faux imputés aux caissiers ; fraude par vente antidatée dans une session close.
9. Tests manquants : vente + retour espèces + clôture ; vente directe dans une session close ; prélèvement de caisse.
10. Corrections : A-002, A-003, A-019, A-005, A-014.

### Clients et crédit
2. Vérifié : service des comptes clients (INT, lecture complète : verrous, devises, annulation avec contre-passation).
6. Problèmes : contournement par `POST /sales/{id}/payments` (A-014) ; plafond hors ligne (A-016).

### Achats et fournisseurs
1. Existant : commande, réception (avec 4081, V096), facture, rapprochement 3 voies, avoirs, paiements.
3. Partiel : âgé et prévision sans les avoirs (A-022).
9. Tests manquants : lecture ligne à ligne de `PurchasingService` côté infrastructure restante (voir 03).

### Finance et trésorerie
2. Vérifié : soldes = ouverture + Σ transactions sur les bases existantes (Données).
5. Absent : saisie des taux de change (A-017) ; solde d'ouverture comptabilisé (A-023).
6. Défaut : mise à jour perdue du solde (A-018, CRITIQUE).
9. Tests manquants : opérations concurrentes sur un même compte.
10. Corrections : A-018, A-017, A-023, A-004.

### Comptabilité générale
2. Vérifié : 0 écriture déséquilibrée (Données), règles de comptabilisation (UNIT).
6. Défauts : clôture du 2e exercice (A-020), à-nouveaux doublés (A-021, CRITIQUE), extourne partielle (A-006).
9. Tests manquants : deux exercices successifs clôturés ; bilan avant/après à-nouveaux.

### RH et paie
1. Existant : employés, contrats, temps, absences, calcul, workflow sans retour arrière, paiement, avances à 4 yeux, bulletins.
2. Vérifié : calculateur (UNIT), cycle (INT) — non rejoué par l'audit.
6. Problèmes : arrondi (A-026) ; annulation de paiement exposée à A-018.

### Rapports et tableaux de bord
6. Défauts : semaine vide le dimanche (A-008) ; états financiers après à-nouveaux (A-021) ; âgé (A-022).
9. Tests manquants : `FilterPeriod.Resolve` pour chaque jour de la semaine.

### Intelligence
1. Existant : moteur de recommandations à règles, scores, assistant déterministe. Aucune IA générative : ce n'est pas
   présenté comme tel dans le code. Non rejoué par l'audit.

### Intégrations, paiements, notifications
3. Partiel : MonCash sans remboursement (limite de l'API Digicel citée dans le code).
4. Simulé : fournisseur « simulated », explicitement marqué et refusé en production.
5. Absent : paiement carte en ligne (déclaré).
7. Dépendances : identifiants fournisseurs réels, non disponibles pendant l'audit → NON VÉRIFIABLE.

### Exploitation, continuité, plateforme
6. Problèmes : console `/ops/*` de portée plateforme ouverte aux rôles d'entreprise (A-031, CRITIQUE) ; métriques anonymes atteignables par des variantes de chemin (A-024) ; scan de vulnérabilités non bloquant (A-028).
7. Dépendances : Docker (arrêté pendant l'audit) pour la pile de production et l'E2E.
