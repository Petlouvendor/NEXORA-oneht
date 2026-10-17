# Anomalies — édition 2 (brouillon de travail, audit du 2026-10-16, HEAD `6a47c6d`)

Format : ID — gravité — module — fichiers — description — preuve — reproduction — conséquence — correction — non-régression.
Les identifiants A-001 à A-032 sont ceux de l'édition 1 ; leur état revérifié figure dans la section « Revérification ».
Les nouvelles anomalies commencent à A-033.

## Nouvelles anomalies

### A-033 — MOYEN — Utilisateurs / autorisation — `Infrastructure/Identity/UserAssignmentService.cs:26-42,55-68,137-186,219-223`, `Api/Controllers/UsersController.cs`

- Description : `admin.manage_users` est vérifiée sur l'entreprise ACTIVE (attribut `[RequirePermission]`), mais le service
  définit le « périmètre de l'appelant » comme toutes les entreprises où il possède une affectation QUELCONQUE
  (`AuthorizedCompaniesAsync` = `profile.Memberships`). Liste, attribution, retrait et changement de statut acceptent donc
  une cible ou une entreprise où l'appelant n'est que caissier.
- Conséquence : un administrateur des utilisateurs de l'entreprise A, caissier dans l'entreprise B, peut lister les comptes
  de B, attribuer dans B les rôles qu'il y détient, et suspendre ou verrouiller (statut GLOBAL du compte) un utilisateur de B
  dont les droits dans B ne dépassent pas les siens. Portée limitée aux entreprises d'une même installation (un client par
  installation, décision du 2026-10-15).
- Preuve : lecture. Reproduction prévue : utilisateur U admin dans A et caissier dans B ; cible V caissier dans B seulement ;
  `POST /users/{V}/status {status:"suspended"}` avec `X-Company-Id: A` → attendu 403, déduit 200.
- Correction : exiger `admin.manage_users` dans l'entreprise de l'affectation visée (attribution, retrait) et dans au moins
  une entreprise de la cible (statut) ; borner la liste aux entreprises où la permission est détenue.
- Non-régression : test d'API avec le scénario ci-dessus.

### A-034 — MOYEN — Autorisation / périmètre des données (motif transverse) — `Api/Security/PermissionAuthorization.cs:51-56`, `Infrastructure/Security/AuditAndFraud.cs:21,30-45`

- Description : le contrôle de permission porte sur l'entreprise active (ou sur n'importe quelle entreprise si aucune n'est
  active), tandis que certains services lisent les données de TOUTES les entreprises du jeton (`tenant.CompanyIds`, qui est
  aussi le périmètre RLS). Cas lu : le journal d'audit (`security.audit_view` dans A suffit pour lire l'audit de B, y compris
  anciennes/nouvelles valeurs des tables auditées de B).
- Autres services suivant ce motif : clonage de configuration (`SqlConfiguration.cs:333-342`, ci-dessous) et audit RH
  (`HrController.Audit` → `IAuditQueryService`).
- Correction : borner les lectures à l'entreprise active, ou vérifier la permission pour chaque entreprise lue.

### A-035 — MOYEN — Accès d'urgence — `Infrastructure/Security/SecurityGovernance.cs:316-333`

- Description : `SqlEmergencyAccessService.RevokeAsync` appelle `assignments.RevokeAsync(...)` sans examiner son résultat,
  puis marque l'accès d'urgence « revoked ». Le retrait peut être refusé (garde anti-élévation A-010 si le bénéficiaire
  détient des droits que le révocateur n'a pas, affectation hors périmètre…).
- Conséquence : l'écran montre l'accès révoqué alors que l'affectation reste active jusqu'à son expiration (≤ 8 h).
- Correction : renvoyer l'erreur du retrait et ne changer le statut qu'en cas de succès.
- Non-régression : test « révocation refusée → accès toujours actif et statut inchangé ».

### A-036 — MOYEN — Dépendances / CI — `backend/Directory.Packages.props` (MailKit 4.18.1 → MimeKit 4.18.1)

- Description : `dotnet list Nexora.sln package --vulnerable --include-transitive` (exécuté le 2026-10-16) signale
  `System.Security.Cryptography.Pkcs` 10.0.0 (transitif, tiré par MimeKit 4.18.1) : avis GHSA-846f-f764-76mx (High) et
  GHSA-2fmv-rv2c-59x7 (Moderate), dans Nexora.Infrastructure, Nexora.Api et les trois projets de tests.
  PDFsharp en tire la version 8.0.1, non signalée. `npm audit --omit=dev` : 0 vulnérabilité.
- Conséquence : depuis le correctif A-028, l'étape « Scan de vulnérabilités » de la CI échoue : la CI du backend est
  rouge tant que la dépendance n'est pas montée. L'exploitabilité réelle dépend des chemins de code (S/MIME, CMS), non analysés.
- Correction : épingler `System.Security.Cryptography.Pkcs` à une version corrigée dans `Directory.Packages.props`
  (le pinning transitif est activé), ou monter MailKit/MimeKit ; relancer le scan.

### A-037 — MOYEN — Administration / versions de configuration — `Infrastructure/Administration/ConfigVersionAndTasks.cs:58-90`, `Api/Controllers/ConfigurationController.cs:60-68`

- Description : « publier » et « retour arrière » (`POST /configurations/{id}/publish|rollback`, `POST /admin/config-versions/{id}/activate`)
  appellent `ActivateAsync`, qui ne modifie que le statut et les dates des lignes `config_version`. L'instantané
  (numérotation, comptes par défaut, taxes, politiques d'approbation, paramètres caisse/stock) n'est jamais réécrit dans les
  tables réelles. Seule une version « brouillon » peut être activée : une version remplacée ne peut pas être reprise.
- Conséquence : un administrateur qui « revient » à une configuration antérieure après une erreur croit l'avoir restaurée ;
  rien n'a changé. Fonctionnalité présente en apparence.
- Correction : réappliquer l'instantané dans une transaction (avec garde-fous), ou retirer la notion de rollback de l'API et
  de l'écran ; autoriser la réactivation d'une version remplacée.
- Non-régression : modifier une taxe, prendre un instantané avant, activer l'ancien instantané → taux d'origine restauré.

### A-038 — ÉLEVÉ (CONFIRMÉ à l'exécution le 2026-10-16) — Stock / concurrence — `Application/Inventory/InventoryOperationsService.cs:159-200,260-283,365-456`, `Infrastructure/Inventory/CatalogInventoryStores.cs:462-534`, `Api/Controllers/InventoryController.cs:46-61,87-91`

- Exécution (base jetable, API locale) : transfert d'UNE unité « Alcool à 70° » Officine → Réserve, puis 8 appels
  simultanés à `POST /inventory/transfers/{id}/receive` : 7 réponses 204 et 1 × 409 ; `stock_transfer_line`
  indique 1 reçu, mais 7 mouvements `transfer_in` (7 unités) ont été passés. Second essai : 8 mouvements, 8 unités.
  Du stock (et sa valeur) est créé de rien à chaque réception concurrente.

- Description : réception et annulation de transfert, validation d'inventaire : le document est chargé et son statut
  contrôlé HORS de la transaction, sans verrou ; le changement de statut est inconditionnel (`ExecuteUpdate … WHERE id =`
  sans condition sur le statut attendu). La réception et l'annulation n'ont ni `[Idempotent]` ni identifiant d'opération ;
  la validation d'inventaire en a un, mais deux clics produisent deux identifiants différents.
- Conséquence déduite : deux réceptions simultanées d'un transfert (double clic, deux postes) postent deux fois l'entrée
  en stock à destination ; deux annulations remettent deux fois la marchandise à la source ; deux validations
  d'inventaire postent deux fois l'ajustement et l'écriture d'écart.
- Correction : `SELECT … FOR UPDATE` du document dans la transaction et relecture du statut ; `UPDATE … WHERE status = 'in_transit'`
  avec contrôle du nombre de lignes ; identifiant d'opération sur réception/annulation.
- Non-régression : test de concurrence (2 réceptions complètes en parallèle → une seule acceptée, stock = expédié).

### A-039 — MOYEN (CONFIRMÉ à l'exécution) — Prix / simulateur ≠ caisse — `Infrastructure/Catalog/CatalogPricingEngine.cs:99-142`, `Infrastructure/Sales/SalesInfrastructure.cs:107-137`

- Exécution (base jetable) : « Alcool à 70° » 130 HTG, promotion −20 %, coupon −10 % : simulateur
  `/catalog/pricing/quote` → net 93,60, TTC 102,96 (coupon après promotion) ; caisse `/pos/cart/preview` → remise 39,
  net 91, TTC 100,10 (coupon calculé sur le prix brut et cumulé). Écart de 2,86 HTG sur un article.

- Description : la caisse applique un coupon en pourcentage sur le montant BRUT de la ligne et l'ajoute à la promotion ;
  le simulateur l'applique sur le montant APRÈS promotion. La caisse n'applique pas un coupon fixe à une ligne en
  promotion ; le simulateur, si. La taxe inactive est appliquée par la caisse (pas de filtre de statut), ignorée par le
  simulateur.
- Exemple (calcul) : prix 100, promotion 20 %, coupon 10 % → caisse : remise 30 ; simulateur : remise 28.
- Conséquence : le simulateur (présenté comme « le calcul de la caisse ») peut annoncer un prix différent de celui encaissé.
- Correction : faire produire le devis du simulateur par `ISalesPricing.PriceCartAsync` (une seule implémentation).

### A-040 — ÉLEVÉ (CONFIRMÉ à l'exécution le 2026-10-16) — Caisse / taxes — `Infrastructure/Sales/SalesInfrastructure.cs:88-91`, `Application/Sales/SalesContracts.cs:3-4`, `SqlSalePricingGuard.ValidateAsync:475-542`

- Exécution (base jetable `nexora_audit_ed2`, API locale) : caissier `caisse@abc-group.demo`, « Alcool à 70° 250 ml »
  (TVA 10 %) ; `/pos/cart/preview` → 130 + 13 = 143 ; `/pos/checkout` avec `taxId` = EX0 → 200, vente VTE2026-000444,
  taxe 0, total 130. Au passage : vente sans session de caisse acceptée (`pos.require_cash_session` vaut `false` par
  défaut dans `setting_definition`).
- Description : `CheckoutLine.TaxId` est accepté depuis le corps de la requête et utilisé tel quel
  (`line.TaxId ?? product.DefaultTaxId`) ; le garde-fou des montants saisis contrôle prix forcé, remise, plancher et
  produits, mais jamais la taxe. Aucune permission n'est exigée.
- Conséquence : avec la seule permission `pos.sell`, un caissier peut appliquer une taxe à 0 % (ou une autre taxe de
  l'entreprise) à un article taxable : le client paie moins sans remise déclarée (contournement des limites de remise),
  la taxe collectée et l'écriture de TCA sont fausses.
- Correction : ignorer `TaxId` client (taxe = celle du produit) ou exiger `pos.change_price` et le journaliser.
- Non-régression : vente avec `taxId` d'une taxe 0 % sur un article à 10 % → taxe 10 % appliquée (ou 403).

### A-041 — ÉLEVÉ (démontré par calcul, CONFIRMÉ à l'exécution sur l'écriture manuelle) — Moteur financier / multidevise — `Application/Finance/Posting/PostingRuleSet.cs:57-92,328-399,102-117,168-220,572-615`, `Domain/Accounting/JournalEntryDraft.cs:73-80`, `Domain/Common/Money.cs:32`, `Infrastructure/Finance/FinanceServices.cs:106-110`

- Exécution : taux USD→HTG 132,5 saisi ; écriture OD en USD D 5121 20,02 / C 7011 10,01 + C 7011 10,01 : brouillon
  accepté, validation refusée (409) « débit 2652.6500 <> crédit 2652.6400 » — message brut de PostgreSQL à l'utilisateur.
- Description : pour une opération dans une devise autre que la devise fonctionnelle, les règles convertissent
  séparément le total (ou chaque encaissement), le net et la taxe (`montant × taux`, arrondi `ToEven` à 2 décimales),
  puis `EnsureBalanced` exige l'égalité stricte des débits et crédits en devise de référence. Aucun ajustement d'arrondi
  n'est fait pour les ventes, retours, factures fournisseurs, achats au comptant, dépenses, retours fournisseurs
  (seuls `DebitPurchase` — partiellement — et la paie absorbent l'écart).
- Calcul (taux 132,5 HTG/USD, vente de 11,02 USD dont net 10,01 et taxe 1,01) : net 1 326,325 → 1 326,32 ; taxe 133,825 →
  133,82 ; total crédité 1 460,14 ; encaissement 11,02 × 132,5 = 1 460,15 débité → « Écriture déséquilibrée » →
  `pos.checkout_failed`, vente refusée.
- Pourquoi les tests ne le voient pas : les taux de démonstration sont entiers (132, 135) : un montant en centimes ×
  un taux entier ne produit jamais de demi-centime.
- Conséquence : dans un commerce en gourdes qui encaisse aussi en dollars (usage courant), une partie des ventes,
  retours et factures en devise échoue de façon aléatoire selon les montants.
- Correction : convertir le total, puis dériver la dernière composante par différence (comme `DebitPurchase` et la paie),
  ou ajouter une ligne d'écart d'arrondi.
- Non-régression : test unitaire de `PostingRuleSet` avec taux 132,5 et les montants ci-dessus, pour chaque événement.
- Extension (lot 7) : l'écriture manuelle en devise (`SqlGeneralAccounting.cs:434-467`) convertit chaque ligne et
  l'arrondit séparément ; le contrôle d'équilibre de l'application porte sur la devise de saisie, celui de la base
  (`assert_entry_balanced`, V023) sur la devise de référence. Ex. : D 20,02 USD / C 10,01 + C 10,01 au taux 132,5 →
  2 652,65 contre 2 652,64 : le brouillon est accepté, sa validation échoue avec le message brut de PostgreSQL.

### A-042 — ÉLEVÉ, BLOQUANT À DATE FIXE (2027-01-01) — Comptabilité / périodes — `Infrastructure/Finance/FinanceServices.cs` (`EfAccountingPeriods.ResolveOpenPeriodAsync`), `Application/Finance/Engine/FinancialEngine.cs:61-69`, `database/migrations/V072__company_bootstrap_defaults.sql:94-98`, `Infrastructure/Finance/SqlGeneralAccounting.cs:613-616`

- Description : toute écriture (vente, encaissement, paiement, réception, dépense, paie…) exige une période comptable
  OUVERTE couvrant sa date (`ResolveOpenPeriodAsync`, sinon « Aucune période comptable ne couvre le … »). Seule la
  fonction d'amorçage `bootstrap_company_defaults` crée une période, pour l'année de l'installation. Aucune route API,
  aucun écran, aucune tâche planifiée ne crée l'exercice suivant ; la clôture d'exercice LIT la période suivante
  (`next_period_id`) sans la créer. Recherche exhaustive : seuls V072 et `database/scripts/smoke_test.sql` insèrent
  dans `accounting_period`.
- Données (SELECT, 2026-10-16) : `nexora_recette` n'a que « Exercice 2026 » pour ses trois entreprises.
- Conséquence : à partir du 2027-01-01, sur toute installation existante, chaque vente en caisse est refusée
  (`pos.checkout_failed`), comme les encaissements, paiements, réceptions, dépenses et la paie ; les ventes hors ligne
  rejouées échouent à la synchronisation. La génération d'à-nouveaux est elle aussi impossible (pas de période cible).
- Correction : création de l'exercice suivant (route + écran, et création automatique planifiée avant l'échéance) ;
  contrôle de mise en production signalant l'absence de l'exercice suivant à J-60.
- Non-régression : test « vente datée du premier jour d'un exercice non créé » → exercice créé automatiquement ou
  message explicite avant l'échéance.

### A-043 — CRITIQUE (CONFIRMÉ à l'exécution ; même nature que A-021 de l'éd. 1) — Comptabilité / états financiers — `Infrastructure/Finance/FinanceStores.cs:41-46`, `Application/Finance/Reversals/ReversalService.cs:65-107`, `Infrastructure/Finance/SqlFinancialReports.cs:35,66,77,171`, `Infrastructure/Finance/SqlGeneralAccounting.cs:665,741`

- Exécution : OD D 5711 / C 7011 de 1 000 validée puis extournée (confirmation forte) ; balance 5711 : 123 255,90 →
  124 255,90 → 122 255,90 (attendu 123 255,90) ; 7011 : −487 534 → −488 534 → −486 534 (attendu −487 534).
- Description : une extourne crée une écriture miroir au statut `posted` et passe l'écriture d'origine au statut
  `reversed`. Les états (balance, grand livre, compte de résultat, bilan), la clôture d'exercice (`PnlBalancesAsync`) et
  les à-nouveaux ne retiennent que `status = 'posted'` : l'origine disparaît, le miroir reste. Après extourne, l'effet
  net n'est donc pas zéro mais l'opposé de l'écriture d'origine.
- Exemple (calcul sur le code) : vente au comptant 2 000 (D 5711 / C 7011) puis extourne → balance : 5711 créditeur de
  2 000, 7011 débiteur de 2 000, au lieu de 0 et 0. Le bilan reste « équilibré » (les deux côtés sont faussés).
- Portée : extourne manuelle, annulation de versement client (`SqlCustomerAccountService.cs:308`), annulation de dépense
  client (`SqlCustomerExpenseService.cs:256`), annulation de paiement de salaire (`SqlPayrollService.Payments.cs:239`).
  Même filtre `posted` seul dans 12 fichiers (23 requêtes) : rapports financiers, comptabilité générale, consolidation et
  grand livre de groupe, couche de confiance des KPI, vieillissement/budget, assistant, scoring, fil d'activité, achats.
  Les requêtes qui comptent `('posted','reversed')` (plan comptable, stock, trésorerie) sont, elles, justes : deux
  calculs du même solde divergent (plan comptable ≠ balance).
- Tests : `Posted_entry_can_be_reversed` et `Reversal_creates_mirror_entry_and_marks_source_reversed` vérifient le
  statut, jamais un état après extourne. `nexora_recette` ne contient encore aucune écriture `reversed` (SELECT du
  2026-10-16) : le défaut n'y est pas visible.
- Correction : compter `posted` ET `reversed` partout où l'on agrège (ou ne jamais changer le statut de l'origine) ;
  une seule fonction SQL de solde partagée.
- Non-régression : vente → extourne → balance, grand livre, bilan, compte de résultat, clôture : tous à zéro sur les
  comptes concernés.

### A-044 — MOYEN — Comptabilité / exercices — `Application/Finance/Periods/AccountingPeriodService.cs:31-43`, `Infrastructure/Finance/SqlGeneralAccounting.cs:555-560`

- Description : un exercice clôturé (écriture de clôture passée, `accounting_year_close` « completed ») peut être rouvert
  avec un simple motif (journalisé, non conservé en base). La clôture n'est pas annulée : les écritures passées après
  réouverture ne sont jamais soldées, et une nouvelle clôture est refusée (« L'exercice est déjà clôturé »). Le motif de
  réouverture n'est écrit que dans le journal applicatif.
- Correction : réouverture d'exercice = extourne de l'écriture de clôture et annulation de `accounting_year_close`,
  ou interdiction ; motif conservé en base.

### A-045 — MOYEN (CONFIRMÉ à l'exécution) — Finance / contournement des contrôles — `Api/Controllers/FinanceController.cs:19-72`, `Application/Finance/FinanceOperations.cs:87-112`, `database/migrations/V022__account_mapping_and_finance_permissions.sql:60-67`

- Exécution : `compta.pharma@recette.demo` (rôle accountant) → `POST /finance/expenses` 250 HTG → 200 ; dépense
  2026-000001 au statut « paid », jamais approuvée ; caisse officine 123 255,90 → 123 005,90.
- Description : neuf routes `POST /api/v1/finance/*` passent des écritures à montants fournis par l'appelant. Aucune
  n'est utilisée par l'interface (recherche dans `frontend/` et `e2e/` : 0 appel). Elles contournent les contrôles des
  écrans métier :
  - `/finance/expenses` (`finance.pay`, accordée au rôle `accountant`) enregistre une dépense directement PAYÉE : pas de
    soumission ni d'approbation à 4 yeux (`SqlExpenseService.ApproveAsync`), pas de catégorie, compte de charge libre
    (`ResolveByCodeAsync` accepte tout compte mouvementable, y compris d'actif ou de produit), pas de contrôle de solde ;
  - `/finance/transfers` : pas de contrôle de découvert sur caisse (fait seulement dans `SqlTreasuryManagementService.TransferAsync`) ;
  - `/finance/cash-counts` : montants « attendu » et « compté » libres → écart de caisse arbitraire comptabilisé ;
  - `/finance/cash-sales`, `/finance/credit-sales` : chiffre d'affaires et créance sur n'importe quel client, sans
    vente ni stock ; `/finance/customer-payments` sans confirmation forte (celle des fournisseurs en a une).
- Conséquence : le circuit « dépense soumise → approuvée par un autre → payée » n'est pas une garantie : un comptable
  seul peut sortir de l'argent d'un compte de trésorerie.
- Correction : retirer ces routes ou les réserver à un rôle technique ; à défaut, appliquer les mêmes contrôles que les
  services métier (approbation, compte de charge, découvert, confirmation forte).
- Non-régression : test d'API « comptable → POST /finance/expenses → 403 » (ou dépense créée au statut soumis).

### A-046 — MOYEN — Pilotage / indicateurs multidevises — `Infrastructure/Reporting/SqlKpiService.cs:27-105, 238-246, 297-327`, `Infrastructure/Finance/SqlFinancialReports.cs:129-132`, `Infrastructure/Finance/SqlTreasury.cs:72-77`

- Description : le centre de pilotage (Command Center), le tableau de bord du responsable et la vue groupe additionnent
  des montants de devises différentes et les affichent sous la devise fonctionnelle : trésorerie
  (`SUM(current_balance)` de tous les comptes actifs), créances, dettes, dépenses, chiffre d'affaires (ventes en USD
  comprises). La vue groupe additionne aussi les entreprises entre elles quelle que soit leur devise.
- Preuve (SELECT en lecture seule sur `nexora_recette`, 2026-10-16) : la Pharmacie ABC a une caisse (123 255,90 HTG),
  un compte mobile money (35 169,05 HTG) et un compte bancaire en USD (74 890,15 USD). L'indicateur « Trésorerie »
  affiche 233 315,10 « HTG » ; la valeur réelle dépasse 9,9 millions de HTG au taux de démonstration. Deux ventes en USD
  existent aussi dans la base.
- Conséquence : l'indicateur de trésorerie, le premier que lit la direction, est faux dès qu'un compte n'est pas dans
  la devise de l'entreprise ; même défaut pour la prévision de trésorerie des états financiers. Le cockpit de trésorerie
  convertit, lui, mais au taux 1:1 à défaut de taux.
- Correction : convertir chaque montant vers la devise fonctionnelle au taux du jour (ou afficher par devise), signaler
  explicitement l'absence de taux ; une seule fonction de conversion partagée.
- Non-régression : compte USD + compte HTG → indicateur = somme convertie ; sans taux → avertissement, pas de 1:1.

### A-047 — MOYEN — Pilotage / budget — `Infrastructure/Reporting/SqlAgingBudgetDrill.cs:72-103`, `Infrastructure/Reporting/BudgetService.cs:293-315`

- Description : « Budget vs réalisé » additionne les lignes de TOUS les budgets de l'exercice, quel que soit leur statut
  (brouillon, en attente, actif, clos) et sans tenir compte des révisions. `ReviseAsync` crée un nouveau budget qui copie
  toutes les lignes de l'original, lequel reste en base : après une révision, le budget affiché est doublé ; un
  brouillon abandonné s'ajoute aussi. Le nom affiché est celui d'un budget quelconque de l'exercice (`LIMIT 1`).
  Le « réalisé » ne compte que les écritures `posted` (A-043) et l'année civile, pas l'exercice ; la ligne
  `r.AccountName.Length > 0 ? Math.Abs(r.Actual) : r.Actual` prend toujours la valeur absolue (une charge créditrice
  ou un produit débiteur apparaît en positif).
- Conséquence : l'écart budgétaire présenté à la direction est faux dès la première révision.
- Correction : ne retenir que le budget actif (ou celui choisi), exclure les versions remplacées ; signe selon le type
  de compte.
- Non-régression : budget 100 → révision à 120 → rapport = 120.

### A-048 — MOYEN (CONFIRMÉ à l'exécution) — Confidentialité / intelligence — `Api/Controllers/IntelligenceController.cs:34-110`, `database/migrations/V056__intelligence_recommendations.sql:230-231`

- Exécution : `stock.pharma@recette.demo` → `GET /forecasts` 403 ; `GET /intelligence/forecasts` 200 (trésorerie
  ≈ 10 M HTG, CA quotidien) ; `GET /intelligence/recommendations` 200 (« Centre Médical Delmas 60 doit 33020 HTG… »).
  Défaut annexe : la prévision de trésorerie contient un point daté du 2300-07-22 (tranche « Au-delà » = +100 000 jours).
- Description : les KPI financiers et commerciaux sont réservés à `reporting.view_financial` / `reporting.view_sales`
  (KpiCatalog, guide §6, §17, §18 : « ni stock, ni achats »), et `GET /forecasts` du BiController exige
  `reporting.view_financial`. Or le centre d'intelligence réexpose les mêmes données sous la seule permission
  `intelligence.view`, accordée aux rôles `stock_manager` et `purchasing_manager` : prévisions de chiffre d'affaires et
  de trésorerie (`/intelligence/forecasts`), insights de baisse de CA et d'écart de trésorerie avec montants
  (`/intelligence/insights`, `/intelligence/anomalies`), recommandations de recouvrement nominatives et risque de
  trésorerie (`/intelligence/recommendations`), score de risque client avec encours (`/intelligence/scores`).
- Preuve (SELECT en lecture seule sur `nexora_recette`) : les deux rôles détiennent `intelligence.view`,
  `reporting.view`, `reporting.export` et aucune des deux permissions financières ; la base contient 5 prévisions de CA,
  3 de trésorerie, 1 insight `revenue_drop` et 5 recommandations `receivable_priority`.
- Correction : filtrer par catégorie selon les permissions de pilotage (comme `KpiCatalog.IsVisible`), ou exiger
  `reporting.view_financial` sur ces routes.
- Non-régression : responsable stock → `/intelligence/forecasts?metric=revenue` → 403 ou liste vide ; recommandations de
  stock toujours visibles.

### A-049 — CRITIQUE (plateforme SaaS, comme A-031 en éd. 1) / MOYEN (installation d'un seul client) — CONFIRMÉ à l'exécution — Sécurité / facturation plateforme — `Api/Controllers/BillingController.cs:62-69`, `Infrastructure/Platform/SqlPlatformBilling.cs:167-232`, `Infrastructure/Operations/ProductionConfiguration.cs:17-50`, `infrastructure/docker-compose.prod.yml`

- Exécution : `POST /api/v1/billing/webhooks/payment` sans authentification, en-tête
  `X-Platform-Webhook-Token: dev-platform-webhook-token` → 200, paiement plateforme enregistré ; témoin avec un autre
  jeton → 401.
- Description : `POST /api/v1/billing/webhooks/payment` (anonyme) n'est protégé que par l'en-tête `X-Platform-Webhook-Token`, comparé à
  `PlatformBilling:WebhookToken` ou, à défaut, à la constante `dev-platform-webhook-token` écrite dans le code (et dans
  `PlatformEndpointsTests.cs:83`). Le garde de démarrage (`SecretProblems`) ne contrôle pas cette clé et la configuration
  de production fournie ne la définit pas : une installation déployée selon la documentation accepte le jeton public.
  Pas de signature du corps (HMAC), pas de limite de débit, comparaison non constante dans le temps.
- Conséquence : n'importe qui peut poster un événement « succeeded » pour un client et une facture plateforme
  (identifiant ou référence) : facture marquée payée, abonnement remis à jour (`ApplySuccessfulPaymentAsync`) ; ou des
  événements « failed » qui font passer des abonnements en impayé ; ou des événements ambigus en masse (alertes).
  Aggravant (`SqlPlatformBilling.cs:285-300`) : `ApplySuccessfulPaymentAsync` passe la facture à « payée » avec
  `paid_amount = total_amount` quel que soit le montant reçu, et ne vérifie pas que la facture appartient au client de
  l'événement : un paiement de 0,01 solde n'importe quelle facture émise (vaut aussi pour un paiement légitime partiel
  et pour la confirmation manuelle).
- Correction : refuser le démarrage hors développement sans `PlatformBilling:WebhookToken` fort (comme JWT/MFA), ou
  désactiver la route ; signature HMAC du corps et horodatage ; limite de débit ; comparaison en temps constant.
- Non-régression : environnement Production sans la clé → démarrage refusé ; jeton par défaut → 401.

### A-050 — MOYEN — Reprise de données / stock d'ouverture — `Infrastructure/Migration/EntityImporters.cs:349-410`

- Description : l'import « opening_stock » est le seul chemin qui crée du stock d'ouverture (`MovementType.Opening`, un
  seul appel dans tout le code). Il passe par le registre de stock mais ne produit aucune écriture comptable : la valeur
  du stock augmente sans contrepartie (stock / report à nouveau). C'est le même défaut que A-023, corrigé pour la
  trésorerie (`RecordTreasuryOpeningAsync`) mais pas pour le stock.
- Conséquence : après une reprise de stock, la valorisation du stock dépasse le compte de stock du grand livre de la
  valeur importée ; chaque vente crédite ensuite le compte de stock du coût des marchandises, qui peut devenir créditeur.
  Le rapprochement KPI ↔ comptabilité signalera un écart permanent.
- Constat connexe (SELECT en lecture seule, `nexora_recette`) : la Pharmacie ABC présente un écart stock / compte
  de stock de 140 285,00 contre 61 696,20 ; aucun mouvement d'ouverture dans cette base : l'écart s'explique par A-052
  et par des écarts de prix factures/réceptions.
- Correction : écriture d'ouverture (débit stock / crédit report à nouveau) dans la même transaction, idempotente par
  ligne, comme pour la trésorerie ; commande de reprise pour les imports déjà faits.
- Non-régression : import de 10 unités à 5 → stock 50 et compte de stock 50.

### A-051 — MOYEN — Continuité / contrôles déclaratifs présentés comme vérifiés — `Infrastructure/Continuity/SqlContinuityService.cs:116-256`, `Infrastructure/Operations/SqlOperationsService.cs:108-131`

- Description : une sauvegarde est « enregistrée » à partir de métadonnées saisies (taille, empreinte, nombre d'objets,
  emplacement). `VerifyBackupAsync` la passe au statut `verified` si ces métadonnées sont présentes et plausibles, sans
  jamais lire ni recalculer l'empreinte du fichier. `RunRestoreTestAsync` produit un test `passed` sur les mêmes
  métadonnées (le rapport le dit : « la restauration réelle en sandbox est réalisée par l'agent d'infrastructure »).
  `ApproveProductionRestoreAsync` passe une restauration de production au statut `passed` au moment de l'approbation,
  sans qu'aucune restauration n'ait lieu.
- Conséquence : la liste de mise en production (« Sauvegarde récente vérifiée », « Restauration testée ») et la note de
  reprise A-E affichent « réussi » sur la foi d'une saisie ; un directeur peut cocher ces contrôles sans sauvegarde
  réelle. Atténuation : `database/scripts/restore.sh --verify` fait une vraie restauration puis appelle ces mêmes
  routes (`/backups/{id}/verify`, `/restore-test`) ; mais l'API ne distingue pas cet appel d'une simple saisie, et tout
  titulaire de `continuity.manage` obtient le même statut sans restauration.
- Correction : statuts « déclarée » / « contrôlée par l'infrastructure » distincts ; « vérifiée » et « passée »
  seulement sur compte rendu de l'outil de sauvegarde (empreinte recalculée, restauration réelle) ; restauration de
  production approuvée → « approved », pas « passed ».
- Non-régression : sauvegarde déclarée sans compte rendu → contrôle de mise en production « manual » ou « fail ».

### A-052 — CRITIQUE (prouvé sur les données : corruption durable du grand livre) — Moteur financier / coût des ventes en devise — `Application/Sales/SalesService.cs:253-325,418`, `Application/Sales/SalesReturnService.cs:171,200`, `Application/Finance/Posting/PostingRuleSet.cs:94-100`

- Description : le coût des marchandises vendues est calculé en devise de l'entreprise (coût moyen du stock,
  `unitCost × quantité`), puis transmis au moteur comme `new Money(cogsTotal, cmd.Currency)`, c'est-à-dire dans la
  devise de la VENTE. Pour une vente en USD, `AddCogs` le « convertit » une seconde fois au taux : le coût est multiplié
  par le taux de change. Même défaut pour le stock remis en rayon lors d'un retour (`cogsRestocked`, devise de la vente).
- Preuve (SELECT en lecture seule, `nexora_recette`, Pharmacie ABC) : ventes VTE2026-000326 et VTE2026-000406, 2 ×
  Vitamine C à 2,10 USD (total 4,20 USD = 567 HTG), coût de stock 150 HTG l'unité. Mouvement de stock : 300 HTG.
  Écriture : 6031 débit 40 500 HTG / 3110 crédit 40 500 HTG (« 300 USD » × 135). Sur la Pharmacie, le compte de stock
  du grand livre (61 696,20) s'écarte de la valorisation du stock (140 285,00) ; ces deux ventes expliquent 80 400 de
  l'écart. Le solde est expliqué : 1 811,20 d'écarts de prix entre factures (453 561,20 capitalisés au compte de
  stock) et réceptions (451 750,00 entrées en stock), sans revalorisation du stock (FAIBLE). Écart total :
  1 811,20 − 80 400,00 = −78 588,80 = 61 696,20 − 140 285,00.
- Conséquence : chaque vente en devise étrangère produit une perte comptable fictive (ici −39 933 HTG pour une vente de
  567 HTG), vide le compte de stock et fausse marge, résultat, bilan et clôture d'exercice. Les KPI de marge (calculés
  sur `sale_line.unit_cost`) restent justes : comptabilité et pilotage divergent.
- Correction : transmettre le coût en devise fonctionnelle (`new Money(cogsTotal, functional)`) — idem pour
  `cogsRestocked` — ; commande de reprise qui extourne et repasse les écritures concernées.
- Non-régression : vente en USD d'un article à coût 150 HTG → débit 6031 = 150 HTG ; compte de stock = valorisation.

### A-034 (suite du recensement)

- `Infrastructure/Configuration/SqlConfiguration.cs:333-342` (`CloneAsync`) : la cible doit seulement figurer dans
  `tenant.CompanyIds` ; `config.transfer` n'est vérifiée que sur l'entreprise source. Modules, réglages, terminologie et
  préfixes de numérotation d'une entreprise où l'appelant n'est que caissier peuvent être écrasés.

### Constats faibles du lot 3 (détail dans notes-ed2.tsv)

- Import/clonage de configuration : rôles exportés jamais réappliqués ; réglages d'établissement promus en réglages
  d'entreprise ; valeurs écrites sans la validation du service des paramètres ; message PostgreSQL brut renvoyé.
- Workflows : `salary_advance`/`employee_loan` absents des types de `SubmitAsync` (échec silencieux de `EvaluateAsync`).
- Automatisation « relance clients » : notifie le créateur, jamais le client ; heure planifiée en UTC.
- Modules sectoriels : seul « restaurant » est consulté par le serveur.
- Logo d'entreprise : 500 caractères maximum (data:image tronqué) ; https externe bloqué par la CSP.
- Tableau de bord par défaut de l'entreprise supprimable par tout utilisateur (si la RLS ne l'empêche pas : à vérifier).

### T-003 — FAIBLE — Tests d'intégration / environnement — `tests/Nexora.IntegrationTests/FinanceEngineFixture.cs:48-58`

- `CREATE DATABASE … TEMPLATE nexora` s'exécute avec le délai de commande par défaut de Npgsql (30 s). Mesuré pendant
  l'audit : 22,9 s pour un clone de 49 Mo, à vide. Deux passages complets ont échoué (532/532 en 1 ms chacun) sur ce délai.
  L'assistant d'installation porte ce délai à 300 s pour la même raison (`SetupInstaller.cs:477`).
- Contournement sans modifier le dépôt : `NEXORA_IT_ADMIN="…;Command Timeout=600"`.
- Correction : `CommandTimeout` explicite sur la commande de clonage de la fixture.

### Résidus d'anomalies corrigées

- **A-003 (résidu, ÉLEVÉ, CONFIRMÉ à l'exécution le 2026-10-16)** — `Infrastructure/Sync/SyncServices.cs:314-358`,
  `Infrastructure/Sales/PosOfflineInfrastructure.cs:26-63`, `Application/Sales/SalesService.cs:102-117,606-622`.
  Le correctif force la date serveur sur `/pos/checkout` et `/sales`, mais le canal `/sync` transmet `SoldAt` du poste à
  `CheckoutAsync` sans autre borne que « ≥ ouverture de la session » et « ≤ maintenant + 5 min », et accepte une session
  CLOSE si la date précède sa fermeture. Or tout titulaire de `pos.sell` enrôle lui-même un appareil
  (`POST /pos/device`, statut immédiatement `active`). Un caissier peut donc encore, par l'API, ajouter une vente à une
  session déjà comptée et clôturée (le scénario d'origine de A-003) ou antidater une vente dans sa session. Si
  `pos.require_cash_session` est désactivé, rien ne borne la date dans le passé. La durée maximale hors ligne se contrôle
  sur `ClientCreatedAt`, fourni par le poste.
  Exécution (base jetable) : `caisse2.super@recette.demo` enrôle lui-même un appareil (`POST /pos/device` → statut
  `active`), puis `POST /sync/batch` d'une vente de 341 HTG datée du 2026-09-27 13:00 UTC, rattachée à la session
  b20c3fed… CLOSE le 2026-09-27 à 13:32 UTC (19 jours plus tôt) → `SYNCED`, vente SMV2026-002032 ajoutée à la session
  close. Annexe : une date `ClientCreatedAt` avec décalage non UTC provoque une erreur 500 (Npgsql) au lieu d'un refus.
  Correction : refuser une session close au rejeu (ou n'y accepter que les ventes dont l'appareil a été vu hors ligne
  avant la clôture), borner `SoldAt` par le dernier contact connu de l'appareil, faire approuver l'enrôlement d'un appareil.
- A-011 (résidu, FAIBLE) — `Application/UseCases/Auth/LoginHandler.cs:43-50` : un compte verrouillé répond
  « auth.account_locked » sans vérifier de mot de passe ; message et temps de réponse révèlent l'existence du compte
  (après 5 échecs volontaires). Le reste de A-011 est corrigé (hachage factice, portée de la réinitialisation MFA, anti-rejeu TOTP).
- A-009 (résidu, MOYEN, défense en profondeur) — correctif de l'intercepteur vérifié (`RlsCommandInterceptor`), mais les
  tables d'identité `app_user`, `user_role` et `access_scope` restent sans RLS (SELECT `pg_class`, `nexora_recette`,
  2026-10-16) avec SELECT/INSERT/UPDATE/DELETE accordés à `nexora_app` : leur isolation entre entreprises repose sur le
  code seul. 40 tables sans RLS sur 291 (41 en éd. 1 ; `role_permission` protégée depuis V101).
- A-029, A-030 : corrigés (V101 lue ; politiques `role_read`/`role_write`, RLS forcée sur `role_permission`, privilèges
  retirés sur `schema_migration` et `audit_log`).
- A-022 (résidu, MOYEN) — `Infrastructure/Reporting/SqlAgingBudgetDrill.cs:41,44,170,182` : la balance âgée du pilotage
  (`/reports/aging/receivables|payables`, export `aging_*`, Ask Nexora « qui me doit le plus », exploration) calcule
  encore `original_amount − paid_amount`. Côté fournisseurs, la colonne générée `remaining_amount` déduit les avoirs
  (`credited_amount`) : ce rapport surestime les dettes de chaque avoir fournisseur. Les 14 autres requêtes corrigées
  (états financiers, cockpit, alertes, prévision) lisent bien `remaining_amount`. Aucun avoir fournisseur dans
  `nexora_recette` (SELECT) : non visible dans les données actuelles.
- A-025, A-027 : corrigés (avertissement en tête du rapport de préparation ; `ModulePlaceholder.tsx` supprimé).
- A-026 : clos sans changement par décision documentée (`docs/corrections-audit-2026-10.md:10`, la paie arrondit une
  seule fois avant comptabilisation) ; l'arrondi `AwayFromZero` de `PayrollRules.Money` demeure (risque accepté).
