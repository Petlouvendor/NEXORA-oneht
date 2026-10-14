# Anomalies relevées au fil de l'audit (brouillon de travail)

Format : ID — gravité — module — fichiers — description — preuve — reproduction — conséquence — correction — non-régression.

## A-001 — ÉLEVÉ — Build / tests — `backend/tests/Nexora.ApiTests/Nexora.ApiTests.csproj`
- Description : la copie de travail contient une modification non commitée qui supprime `<ProjectReference Include="..\..\src\Nexora.Api\Nexora.Api.csproj" />`.
- Preuve : `git diff` ; `dotnet build Nexora.sln` (2026-10-13) → exit 1, 75 erreurs CS0246/CS0234, toutes dans Nexora.ApiTests (`Program`, `DatabaseSetupRequest`, `InstallRequest`, `NpgsqlConnectionStringBuilder` introuvables).
- Reproduction : `cd backend && dotnet build Nexora.sln`.
- Conséquence : la solution ne compile pas ; les 30 fichiers de tests d'API (dont `MultiTenantIsolationTests`, `SessionSecurityEndpointsTests`) ne peuvent pas s'exécuter ; une CI sur cet état échouerait.
- Correction : restaurer la référence (`git checkout -- backend/tests/Nexora.ApiTests/Nexora.ApiTests.csproj`) si la suppression n'était pas voulue.
- Non-régression : `dotnet build Nexora.sln` puis `dotnet test` du projet ApiTests.

## A-002 — ÉLEVÉ (CONFIRMÉ à l'exécution le 2026-10-13) — POS / caisse — `Application/Sales/SalesReturnService.cs`, `Application/Sales/CashSessionService.cs`, `Infrastructure/Sales/SalesInfrastructure.cs:378-387`
- Description : un retour remboursé en espèces (`RefundMethod = "cash"`, `CashSessionId` fourni) ne crée AUCUN `cash_movement` dans la session. Le type `sale_refund` existe dans `cash_movement_type_chk` (V011:130-131) mais n'est écrit nulle part (grep sur tout le dépôt : seule occurrence = la contrainte).
- Conséquence déduite : l'attendu de clôture (`SessionTotalsAsync` = somme des mouvements) ignore le remboursement → compté < attendu → « manquant » imputé au caissier (et session bloquée en « closing » au-delà du seuil) ; l'écart est ensuite comptabilisé (Dr écart de caisse / Cr caisse) alors que le retour a déjà crédité la caisse (Cr trésorerie dans SaleRefund + MoveCashAsync -X) → **trésorerie diminuée deux fois** et charge d'écart fictive.
- Reproduction prévue : ouvrir une session (fond 0) → vente espèces 100 → retour total espèces → clôture comptée 0 → attendu affiché 100, écart -100.
- Correction : écrire un mouvement `sale_refund` (montant négatif) dans la session du retour, dans la transaction du retour.
- Non-régression : test d'intégration vente + retour espèces + clôture → écart 0, solde du compte caisse = fond.

## A-003 — ÉLEVÉ (CONFIRMÉ à l'exécution) — POS / sécurité métier — `Api/Controllers/PosController.cs:111-115`, `Api/Controllers/SalesController.cs:21-25`, `Application/Sales/SalesService.cs:102,611-613`
- Description : `CheckoutCommand` est lié tel quel depuis le corps HTTP, y compris `SoldAt`. Un caissier (pos.sell) peut antidater/postdater une vente. `SessionRefusalAsync` accepte une session FERMÉE si `SoldAt <= ClosedAt` (prévu pour le rejeu hors ligne) : une vente peut donc être ajoutée à une session déjà comptée et clôturée (mouvement de caisse après comptage), et datée dans une période antérieure encore ouverte.
- Reproduction prévue : POST /api/v1/pos/checkout avec `cashSessionId` d'une session close et `soldAt` antérieur à sa fermeture.
- Correction : n'accepter `SoldAt` que par le canal de synchronisation hors ligne (appareil enrôlé), borné (ex. ≥ ouverture de session, ≤ maintenant) ; ignorer `SoldAt` sur /pos/checkout.
- Non-régression : test API refusant une vente directe dans une session close.

## A-004 — MOYEN — Finance / moteur — `Application/Finance/Engine/FinancialEngine.cs:48-54,320-343`
- (1) Idempotence contrôlée hors transaction → doublons concurrents finissent en 500 (DbUpdateException non capturée) au lieu d'un rejeu.
- (2) `MoveCashAsync` date les transactions de trésorerie à `UtcNow` au lieu de `evt.OccurredAt` : ventes hors ligne / opérations datées → trésorerie et grand livre à des dates différentes.
- (3) Affectations de paiement dans la devise du paiement, comparées au reste dû de la créance sans contrôle de devise.

## A-005 — MOYEN — POS / retours — `Application/Sales/SalesReturnService.cs:59-72,87`
- Le contrôle « quantité retournable » est fait hors transaction et sans verrou : deux retours partiels simultanés peuvent dépasser la quantité vendue.
- Un remboursement `cash` sans `RefundFinancialAccountId` est accepté : l'écriture crédite alors le compte d'attente (PostingRuleSet:366) et aucune trésorerie ne bouge.
- `SaleReturn.Restocked = true` en dur même si aucune ligne n'est réintégrée.

## A-006 — MOYEN — Comptabilité — `Application/Finance/Reversals/ReversalService.cs`
- L'extourne inverse le seul grand livre ; créances, trésorerie, stock du document source restent inchangés. À croiser avec les gardes de l'endpoint (lot 7).

## A-007 — ÉLEVÉ (partiellement confirmé à l'exécution) — Stock / inventaires — `Application/Inventory/InventoryOperationsService.cs:284-417`
- Description : `StartCountAsync` fige `SystemQuantity` à l'ouverture ; `ValidateCountAsync` poste `compté − SystemQuantity figé` sans recalculer le stock au moment de la validation. Aucun verrou d'emplacement pendant le comptage (recherche « counting » dans les migrations : uniquement la contrainte de statut).
- Conséquence : toute vente / réception / transfert entre l'ouverture et la validation est comptée deux fois. Ex. système 100 à l'ouverture, 10 vendus, compté 90 → ajustement −10 → stock final 80 pour 90 physiques ; écart d'inventaire comptabilisé à tort.
- Aussi : les lignes d'inventaire sont créées sans lot (`LotId` null) → pour un produit suivi par lot, l'ajustement ne met pas à jour le stock par lot.
- Correction : calculer l'écart sur le stock courant à la validation (ou geler l'emplacement / enregistrer les mouvements intervenus), gérer les lots.
- Non-régression : test « ouverture → vente → comptage → validation » ; stock final = compté.

## A-008 — MOYEN — Rapports / KPI — `Application/Reporting/Abstractions.cs:34`
- Description : préréglage « week » = `today.AddDays(-(int)today.DayOfWeek + 1)`. Le dimanche (`DayOfWeek = 0`), le début calculé est le lundi SUIVANT → période (demain, aujourd'hui) vide.
- Appelants : `SqlKpiService` (Command Center ×3), `SqlReports` (×3), `RuleBasedAskNexora`.
- Conséquence : tous les indicateurs et rapports « cette semaine » sont à zéro chaque dimanche (jour d'ouverture des commerces visés). Pas de contrôle `From ≤ To` sur les périodes personnalisées non plus.
- Correction : `today.AddDays(-(((int)today.DayOfWeek + 6) % 7))`.
- Non-régression : test unitaire de `FilterPeriod.Resolve` pour chaque jour de la semaine.

## A-009 — MOYEN (architecture, sécurité en profondeur) — Isolation — `Infrastructure/Persistence/RlsConnectionInterceptor.cs`, `Infrastructure/Tenancy/TenantContext.cs`, `Infrastructure/Persistence/UnitOfWork.cs`
- Constat 1 : les variables RLS (`nexora.company_ids`, `nexora.bypass_rls`…) sont posées UNIQUEMENT à l'ouverture de connexion (`ConnectionOpened`). Dans une transaction (`ExecuteInTransactionAsync`), la connexion reste ouverte : un `BeginSystemScope()` ouvert APRÈS le début de la transaction n'a aucun effet ; à l'inverse une transaction commencée DANS une portée système garde `bypass_rls = on` après la fin de la portée.
  - 17 fichiers combinent les deux (liste : DocumentInfrastructure, GedService, ConsolidationService, GroupControlAndTasks, GroupLedgerService, MfaService, UserAssignmentService, ApiKeyService, SqlImportService, SqlDataCorrectionService, SqlOperationsService, SqlPartyServices, SqlEmployeeService, SqlPlatformBilling, SqlSubscriptionService, PosControlsInfrastructure, ApprovalAndSession) — chaque cas est examiné dans son lot.
  - Exemple confirmé par lecture : `SqlMfaService.ClearMfaAsync` (réinitialisation MFA d'un AUTRE utilisateur par un administrateur) : `DELETE FROM mfa_recovery_code WHERE user_id = cible` s'exécute dans la transaction avec la RLS de l'administrateur (`user_id = current_user_id()`) → 0 ligne supprimée, sans erreur ; les codes de récupération de la cible survivent à la réinitialisation.
- Constat 2 : la levée de RLS est une variable de session `SET nexora.bypass_rls = 'on'` que le rôle applicatif `nexora_app` peut positionner lui-même : la RLS ne protège pas contre une injection SQL ou un code applicatif fautif (elle protège contre l'oubli d'un filtre).
- Constat 3 (lecture pg_class sur la base de dev, 2026-10-13) : `app_user`, `user_role`, `access_scope` n'ont PAS de RLS : leur isolation par entreprise repose uniquement sur le code.
- Conséquence : défense en profondeur plus faible qu'annoncé ; comportements silencieux (0 ligne) dans certaines opérations système.
- Correction : réappliquer le contexte RLS à chaque changement de portée quand une connexion est ouverte (ou interdire BeginSystemScope dans une transaction) ; `SET LOCAL` dans les transactions ; envisager un rôle séparé pour les tâches système.
- Non-régression : tests d'intégration avec `CreateAppRoleProvider()` (rôle réel) sur la réinitialisation MFA et les opérations listées.

## Observation T-001 — Tests d'intégration et RLS
- `FinanceEngineFixture` se connecte en SUPERUTILISATEUR postgres (commentaire explicite : « contourne donc toute politique RLS »). Hors les tests qui appellent `CreateAppRoleProvider()`, les 496 tests d'intégration ne vérifient pas la RLS ni les effets des portées système ; des défauts comme A-009 y sont invisibles.

## A-010 — MOYEN — Utilisateurs / rôles — `Infrastructure/Identity/UserAssignmentService.cs:305-351`
- Description : la garde anti-élévation (« on n'accorde que ce qu'on détient ») ne s'applique qu'à `GrantAsync`. `SetStatusAsync` (suspendre/verrouiller un compte) et `RevokeAsync` (retirer une affectation entreprise) acceptent toute cible qui « partage une entreprise » avec l'appelant, quel que soit son niveau de privilège (direction, super-administrateur au niveau entreprise). Seuls les rôles de GROUPE sont protégés contre un administrateur mono-société.
- Conséquence : un administrateur d'utilisateurs d'une entreprise peut suspendre le compte du dirigeant ou lui retirer son rôle d'entreprise.
- Aussi : `RevokeAsync` n'invalide ni la session ni `security_epoch` ; la liste des entreprises reste dans le jeton jusqu'à son expiration (15 min) — les permissions, elles, sont relues à chaque requête.
- Correction : appliquer la même garde (cible ≤ appelant) au changement de statut et au retrait ; invalider le cache de session au retrait.
- Non-régression : test API « administrateur société ne peut pas suspendre la direction ».

## A-011 — FAIBLE — Authentification — `Application/UseCases/Auth/LoginHandler.cs:29-44`, `Infrastructure/Identity/MfaService.cs`
- Énumération possible des comptes (temps de réponse sans BCrypt pour un e-mail inconnu ; message « compte verrouillé » propre aux comptes existants).
- Code TOTP rejouable dans sa fenêtre de validité (aucun « dernier pas utilisé » mémorisé).
- `ResetForUserAsync` : `HasPermission("security.reset_mfa", null)` = permission détenue dans n'importe quelle entreprise ; cible acceptée si elle partage une entreprise quelconque avec l'appelant.

## Observation P-001 — Performance — `Infrastructure/Identity/PermissionService.cs`, `Api/Security/PermissionAuthorization.cs`
- `LoadProfileAsync` (3 à 4 requêtes SQL) est appelé à chaque contrôle `[RequirePermission]`, plus dans `MaintenanceModeMiddleware` et dans de nombreux services : aucun cache par requête.

## A-012 — ÉLEVÉ (CONFIRMÉ à l'exécution) — POS / promotions — `Infrastructure/Sales/SalesInfrastructure.cs:111-112`
- Description : dans `SqlSalesPricing.PriceCartAsync`, le coupon est appliqué DANS la boucle des lignes : un coupon « montant fixe » ajoute `min(montant_ligne, valeur)` à CHAQUE ligne sans promotion ; un coupon « pourcentage » s'ajoute à la promotion déjà appliquée. La consommation du coupon (`ConsumeCouponAsync`) ne compte qu'un usage par vente.
- Conséquence : un coupon de 500 HTG sur un panier de 5 articles peut remiser jusqu'à 2 500 HTG ; la remise de coupon n'est pas soumise aux limites de remise du caissier (`ISalePricingGuard` ne contrôle que la remise manuelle).
- Reproduction prévue : coupon amount=100 ; panier de 3 articles à 200 → remise attendue 100, observée 300.
- Correction : répartir un coupon fixe au prorata sur le panier (une seule fois) ; décider explicitement du cumul promo + coupon.
- Non-régression : test de `PreviewAsync`/`CheckoutAsync` multi-lignes avec coupon fixe.

## A-013 — MOYEN — Catalogue / prix — `Application/Sales/Abstractions.cs:84-90`, `Infrastructure/Sales/SalesInfrastructure.cs:70-80`
- Description : la résolution de prix de vente (caisse, commandes, devis) filtre `price_list_id IS NULL` et `ISalesPricing` n'a aucun paramètre de liste tarifaire ; `CheckoutCommand` non plus. Le moteur « explicable » (`CatalogPricingEngine`, simulateur) gère, lui, les listes tarifaires.
- Conséquence : les listes de prix (gros, membre, promotionnelle…) créées dans le catalogue ne s'appliquent jamais à une vente réelle ; le simulateur peut annoncer un prix que la caisse ne facture pas (contraire à l'objectif « calcul unique » annoncé en phase 1).
- Correction : passer la liste (client, groupe ou choix caisse) au calcul serveur unique.

## A-014 — MOYEN — Ventes / encaissement — `Api/Controllers/SalesController.cs:34-47`
- Description : `POST /api/v1/sales/{id}/payments` (permission `pos.sell` seulement) enregistre un règlement de créance via `IFinanceOperations.RecordCustomerPaymentAsync`, en contournant `ICustomerAccountService.RecordPaymentAsync` (permission `credit.receive_payment`, contrôle de session de caisse ouverte, mouvement de caisse) et sans vérifier que la devise du compte d'encaissement est celle de la créance.
- Conséquence : un caissier sans droit d'encaissement client peut solder des créances ; un règlement en espèces n'entre pas dans le tiroir de la session (écart à la clôture).
- Correction : faire passer ce point d'accès par le service des comptes clients (mêmes droits et contrôles) ou le retirer.

## A-015 — FAIBLE — POS / codes-barres — `Infrastructure/Sales/PosControlsInfrastructure.cs` (`ScanAsync`), `PosOfflineInfrastructure.cs`
- `product_barcode.pack_size` et `is_weight_embedded` existent (domaine + base) mais la lecture caisse ne les exploite pas : un code-barres de carton (pack de 12) ajoute 1 unité ; les étiquettes de balance (poids/prix intégré) ne sont pas décodées. À confirmer côté interface (lot 5 frontend).

## A-016 — MOYEN (à confirmer côté interface) — Synchro hors ligne — `Infrastructure/Sync/SyncServices.cs:376-392,486-487`
- Le crédit hors ligne est estimé par `Σ (UnitPriceOverride ?? 0) × qté − remise − encaissé`. Sans prix forcé (cas nominal : prix calculé par le serveur), l'estimation vaut ≤ 0 : `AllowOfflineCredit = false` et `OfflineCreditLimit` ne s'appliquent jamais ; seule la limite de crédit client (en ligne) joue au rejeu.

## A-017 — ÉLEVÉ — Finance / multidevise — `database/migrations/V002__currency.sql:23-37`, `Infrastructure/Finance/FinanceServices.cs` (EfExchangeRateProvider)
- Constat : la table `nexora.exchange_rate` n'est écrite par AUCUN code applicatif (grep `exchange_rate` : lectures seulement — ConsolidationService, SqlTreasury, provider de taux) ; aucun endpoint, aucun écran de saisie de taux. Base de dev : 2 lignes, issues du jeu de démonstration (`demo_abc_group.sql:279-286`, USD→HTG 132 au 2026-01-01 puis 135 au 2026-06-01).
- La table est GLOBALE (pas de `company_id`, pas de RLS) : un taux sert à toutes les entreprises.
- Conséquence : sur une installation neuve (assistant d'installation / `bootstrap`), toute opération dans une devise autre que la devise fonctionnelle (compte bancaire USD, vente ou achat en USD, consolidation, contrôle de crédit multidevise) n'a aucun taux : le contrôle de crédit répond « saisissez le taux du jour » alors que l'application n'offre aucun moyen de le faire. Le multidevise n'est opérationnel qu'avec les données de démonstration.
- Correction : écran + endpoint de gestion des taux (permission finance), historisés ; décider du périmètre (global plateforme vs par entreprise).
- Non-régression : installation neuve → saisie d'un taux → vente USD comptabilisée.

## A-018 — ÉLEVÉ (CONFIRMÉ à l'exécution : 525 HTG perdus sur 20 opérations simultanées) — Trésorerie / intégrité — `Infrastructure/Finance/FinanceStores.cs:100-105`
- Description : `EfFinancialAccountStore.AdjustBalanceAsync` charge le compte (suivi EF, sans `FOR UPDATE`), fait `CurrentBalance += delta` en mémoire, puis `SaveChanges` écrit la valeur calculée. Aucun jeton de concurrence (grep `IsConcurrencyToken|xmin|IsRowVersion` : 0) ; aucun déclencheur ne recalcule `financial_account.current_balance` (grep migrations : colonne définie, jamais maintenue en SQL). C'est le SEUL écrivain du solde.
- Conséquence : deux opérations concurrentes sur le même compte (ex. ventes par carte sur le compte bancaire partagé par plusieurs caisses, deux versements simultanés) → mise à jour perdue : le solde affiché (KPI « Trésorerie », cockpit, comptes) dérive de Σ `financial_transaction`, sans erreur visible.
- Correction : `UPDATE financial_account SET current_balance = current_balance + @delta` atomique (ou solde calculé depuis les transactions), et contrôle de cohérence planifié.
- Non-régression : test de concurrence (N encaissements parallèles) → solde = ouverture + Σ transactions.

## A-019 — MOYEN — Caisse ↔ trésorerie ↔ comptabilité — `Application/Sales/CashSessionService.cs:232-253,315-344`
- Description : `CashInOutAsync` (types `payin`, `payout`, `pickup`) n'écrit qu'un `cash_movement` de session : aucune écriture comptable, aucune `financial_transaction`, aucun ajustement du solde du compte financier de la caisse. La clôture ajoute un mouvement `closing = −attendu` sur la SESSION seulement. Aucun déclencheur ne relie `cash_movement` à la trésorerie (grep migrations : seul `trg_cash_session_closed_guard`).
- Conséquence : une sortie de caisse (paiement d'un livreur, dépôt à la banque/au coffre) diminue l'argent physique mais ni le compte « caisse » de la trésorerie ni le grand livre : le solde de trésorerie et le compte 57x restent surévalués ; à l'inverse un apport (payin) n'y entre jamais. Le tiroir et la comptabilité divergent par construction, sauf saisie manuelle d'un transfert/dépense en double.
- Correction : comptabiliser payin/payout/pickup (contrepartie choisie : compte de passage, coffre, banque, charge) dans la même transaction.
- Non-régression : session avec pickup → solde trésorerie caisse = Σ mouvements de session ; écriture équilibrée.

## A-020 — ÉLEVÉ — Comptabilité / clôture d'exercice — `Infrastructure/Finance/SqlGeneralAccounting.cs` (`PnlBalancesAsync`, `CloseYearAsync`)
- Description : `PnlBalancesAsync(periodId, endDate)` n'utilise PAS `periodId` : il somme tous les produits/charges `entry_date <= endDate` depuis l'origine, en excluant seulement les écritures `origin = 'closing'`. Pour le 1er exercice le résultat est juste ; pour le 2e, les produits et charges de l'exercice 1 (déjà soldés par l'écriture de clôture 1, exclue du calcul) sont repris une seconde fois.
- Conséquence : à partir du 2e exercice, l'écriture de clôture solde des montants faux (comptes de gestion négatifs après clôture), le résultat de l'exercice et l'aperçu de clôture sont erronés.
- Correction : borner au début de l'exercice (`entry_date BETWEEN start AND end` ou `accounting_period_id`).
- Non-régression : deux exercices successifs clôturés → comptes de gestion à zéro, résultat 2 = Σ exercice 2.

## A-021 — ÉLEVÉ — Comptabilité / à-nouveaux et états — `Infrastructure/Finance/SqlGeneralAccounting.cs` (`GenerateOpeningBalancesAsync`), `Infrastructure/Finance/SqlFinancialReports.cs:26-118`
- Description : la balance (`entry_date <= asOf`), le bilan (`PeriodBalancesAsync(DateOnly.MinValue, asOf)`) et le report du grand livre sont CUMULÉS depuis l'origine. `GenerateOpeningBalancesAsync` crée pourtant une écriture d'à-nouveaux (journal AN) qui reprend ces mêmes soldes cumulés de bilan au début de la nouvelle période, sans solder les comptes de bilan de l'exercice précédent.
- Conséquence : dès la première génération d'à-nouveaux, tous les soldes de bilan (caisse, banque, stock, clients, fournisseurs, capitaux) sont doublés dans la balance, le bilan et le grand livre ; l'équilibre arithmétique est conservé (la contrepartie va au report à nouveau), donc l'anomalie ne déclenche aucune alerte.
- Correction : choisir un modèle (grand livre continu SANS écriture d'à-nouveaux, ou à-nouveaux AVEC états bornés à l'exercice) et l'appliquer à tous les états.
- Non-régression : bilan avant/après génération des à-nouveaux identique.

## A-022 — MOYEN — Rapports / balances âgées et prévision — `Infrastructure/Finance/SqlFinancialReports.cs:134-146,193-200`
- Description : les balances âgées et la prévision de trésorerie calculent le reste dû par `original_amount − paid_amount`, en ignorant `credited_amount` (avoirs fournisseurs V082) ; la colonne générée `remaining_amount` n'est pas utilisée.
- Conséquence : dettes fournisseurs surévaluées du montant des avoirs dans l'âgé et dans la prévision de décaissements.
- Correction : utiliser `remaining_amount`.

## T-002 — MOYEN — Tests d'API — `backend/tests/Nexora.ApiTests/*.cs` (constante `DevConnString`), `Infrastructure/DependencyInjection.cs:18-27`
- Les 26 classes de tests d'API déclarent leur propre chaîne de connexion, mais elle n'est jamais appliquée : `AddInfrastructure` lit `Database:ConnectionString` à l'enregistrement des services, avant les surcharges du `WebApplicationFactory`. Toutes les exécutions écrivent donc dans la base `nexora` de `appsettings.Development.json` — qui est aussi le modèle (TEMPLATE) cloné par les tests d'intégration.
- Conséquence : pollution cumulative du modèle des tests d'intégration ; impossible d'isoler les tests d'API ; un test qui modifie la base par une autre connexion vise potentiellement une autre base que l'API (cas constaté pendant l'audit).
- Correction : lire les options via `IOptions` au moment de la construction du DbContext, ou fixer la variable d'environnement `Database__ConnectionString` dans la fabrique de tests.

## A-023 — MOYEN — Trésorerie ↔ comptabilité / mise en service — `Infrastructure/Finance/TreasuryManagementServices.cs:93-106`, jeux `demo_abc_group.sql` et recette
- Description : un compte financier est toujours créé avec `opening_balance = 0, current_balance = 0` ; aucune fonction ne permet d'enregistrer le solde initial d'une caisse ou d'une banque avec sa contrepartie comptable. Les jeux de démonstration/recette posent des `opening_balance` sans écriture.
- Preuve (lecture, `nexora_recette`) : écart trésorerie − grand livre = exactement le solde d'ouverture (Caisse magasin 60 000 ; Caisse officine 45 000).
- Conséquence : à la mise en service, la trésorerie et le grand livre ne peuvent pas partir d'un solde réel cohérent ; le rapprochement trésorerie/compte 57x-51x est faux dès l'origine.
- Correction : opération « solde d'ouverture » (transaction + écriture contre capitaux/compte d'attente), ou import d'à-nouveaux de trésorerie.

## A-031 — CRITIQUE (en déploiement partagé entre plusieurs clients) — Exploitation / isolation — `Infrastructure/Operations/SqlDataCorrectionService.cs:49-120`, `SqlMaintenanceService.cs:44-114`, `SqlJobRegistry.cs`, `SqlFeatureFlagService.cs`, `SqlRetentionPurgeService.cs`, `Api/Controllers/OpsMaintenanceController.cs:30-170`, `Api/Middleware/MaintenanceModeMiddleware.cs`
- Description : la console d'exploitation (`/ops/*`) agit à l'échelle de la **plateforme** (portée système, RLS levée, ou tables sans `company_id`), mais ses permissions `operations.view`, `operations.manage` et `operations.correct_data` sont accordées à des rôles d'**entreprise** : `manager` (view) et `owner_direction` (view, manage, correct_data), en plus de `super_administrator` et `group_administrator` (lecture `role_permission`, `nexora_recette`). Le commentaire du service l'assume (« un titulaire de operations.correct_data voit toutes les demandes, quelle que soit l'entreprise porteuse »).
- Conséquences, par point d'accès (démontrées par lecture du code et du catalogue, **non exécutées** : elles exigent des écritures, refusées pendant l'audit) :
  - `GET /ops/data-corrections` (operations.view, rôle `manager`) : `ListAsync` lit en portée système les demandes de **toutes les entreprises**, avec `old_value`/`new_value` (nom, téléphone, e-mail, adresse, identifiant fiscal de clients et fournisseurs ; nom et identifiant d'utilisateurs) → **fuite de données entre entreprises**.
  - `POST /ops/data-corrections` puis `/decide` (operations.manage puis correct_data, deux directeurs distincts d'une même entreprise suffisent) : lecture et `UPDATE` en portée système de `customer`, `supplier`, `product`, `app_user` **d'une autre entreprise** dont l'identifiant est connu (les identifiants sont exposés par la liste ci-dessus) → **modification de données entre entreprises**. Aucun contrôle `current.CompanyId ∈ entreprises de l'appelant`.
  - `POST /ops/maintenance` + `/transition` → `in_progress` (operations.manage) : `maintenance_window` est globale ; `MaintenanceModeMiddleware` refuse alors (503) **toutes les écritures de toutes les entreprises**, caisse comprise, sauf pour les titulaires d'operations.manage → déni de service inter-entreprises.
  - `POST /ops/jobs/{key}/pause|resume|run` : `job_definition` est globale (pas de `company_id`) → arrêt des tâches planifiées (notifications, relances, purge) de toute la plateforme.
  - `POST /ops/feature-flags` : création de bascules globales ou pour une entreprise quelconque (sans effet aujourd'hui : `IFeatureFlagService.IsEnabledAsync` n'est appelé nulle part — PRÉSENT MAIS NON CONNECTÉ).
  - `POST /ops/purge/execute` avec `companyId` nul : suppression des notifications archivées/expirées de toutes les entreprises.
- Portée réelle : nulle pour une installation mono-entreprise ou mono-groupe (cas des bases `nexora` et `nexora_recette` : 3 entreprises d'un même groupe) ; critique dès que la plateforme SaaS (phase 38) héberge plusieurs clients indépendants.
- Correction : réserver `/ops/*` à un rôle de plateforme (opérateur éditeur) distinct des rôles d'entreprise, OU borner chaque opération à l'entreprise active (`company_id = ActiveCompanyId` dans les lectures et les mises à jour, fenêtre de maintenance par entreprise, tâches par entreprise) ; retirer `operations.*` des rôles système `owner_direction` et `manager` ; supprimer `BeginSystemScope` des lectures de `SqlDataCorrectionService`.
- Non-régression : tests d'API avec deux entreprises de groupes différents : un directeur de A ne voit pas les demandes de B (liste vide), ne peut pas créer de demande sur un client de B (404), ne peut pas ouvrir de maintenance qui bloque B (403) ; tests exécutés avec le rôle réel `nexora_app`.

