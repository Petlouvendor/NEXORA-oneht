# Guide fonctionnel développeur — audit de conformité et corrections

Référence : *NEXORA ONE — Guide fonctionnel complet pour le développeur* (rôles, permissions, POS,
stock, finance, workflows, intelligence, sécurité). Ce document confronte le guide au **code réel**
(contrôleurs, services, requêtes SQL, permissions effectivement présentes en base), pas aux documents
de phase, puis consigne les corrections faites étape par étape.

## 1. Méthode

1. Permissions réelles des rôles lues dans la base `nexora` (`role` × `role_permission` × `permission`),
   pas dans la migration V021 (les migrations suivantes en ont ajouté).
2. Cartographie automatique des 400+ endpoints (`[Route]`, `[Http*]`, `[RequirePermission]` au niveau
   classe et méthode) croisée avec les permissions du rôle `cashier` : **94 endpoints accessibles au
   caissier** avant correction.
3. Lecture des services concernés (encaissement, retours, sessions de caisse, recherche catalogue,
   tableaux de bord, assistant) et de l'écran POS.

## 2. Écarts constatés (avant correction)

| ID | Sévérité | Guide | Constat dans le code |
|---|---|---|---|
| GF-01 | **Critique** | §7.2, §18, §27 | Le caissier détient `reporting.view` : la page d'accueil lui affiche le **Command Center de direction** (CA, marge, bénéfice, trésorerie, créances, dettes, stock valorisé) et il accède à `/dashboard/command-center`, `/reports/sales/*`, `/reports/stock/*`, `/kpis`, `/insights`, `/objectives`, `/reports` (BI). |
| GF-02 | **Critique** | §19, §27 | Assistant : `GET /assistant/briefing?role=direction` — **le rôle vient du client** ; un caissier obtient le Command Center et toutes les recommandations. `POST /assistant/query` et `POST /ask` répondent « marge », « trésorerie », « créances échues » (noms des clients) **sans aucun contrôle de permission**, alors que le commentaire du code affirme le contraire. |
| GF-03 | **Critique** | §10 | Le caissier exécute seul un retour **et** le remboursement (`pos.return` → `POST /pos/returns`, `POST /sales/{id}/returns`) : aucune validation Manager, aucune demande. |
| GF-04 | **Critique** | §10.2 | `SalesReturnService` ne cumule pas les retours précédents : une ligne de 2 unités peut être retournée 1 + 2 fois (3 remboursées). Le statut de la vente est calculé sur le seul retour courant. |
| GF-05 | **Critique** | §7.2, §10 | Le caissier détient `finance.pay` (→ `POST /finance/expenses`, `/finance/supplier-payments`, `/finance/customer-payments`), `finance.manage_cash` (→ `/finance/cash-counts`) et `integrations.payments` (→ `POST /integrations/payments/intents/{id}/refund` : **remboursement d'un paiement électronique**). |
| GF-06 | **Critique** | §7.2, §17, §27 | Prix d'achat exposé : `GET /catalog/products/{id}` renvoie les fournisseurs et leur **prix d'achat** à tout détenteur de `catalog.view` (caissier) ; `GET /sales/{id}` renvoie `unitCost` de chaque ligne. `catalog.view_cost` existe mais n'est lue nulle part. |
| GF-07 | **Critique** | §16 | Dépassement de limite de crédit accepté pour **tout vendeur qui saisit un motif** : `pos.credit_override` n'est vérifiée nulle part. `pos.sell_credit` non plus : `pos.sell` suffit pour vendre à crédit. |
| GF-08 | Haute | §8, §12 | Remise : un plafond unique par entreprise (`pos.max_discount_percent`), identique pour caissier et manager ; au-delà = refus sec, **aucune demande Manager**, aucune trace de remise exceptionnelle. |
| GF-09 | Haute | §9, §24, §25 | Produits **inactifs / non vendables** acceptés à l'encaissement (`SqlSalesPricing` ne lit ni `status` ni `is_sellable`) et proposés par la recherche et le scan. |
| GF-10 | Haute | §9, §24 | Écran POS : le texte saisi est ajouté au panier **comme identifiant produit, à prix 0** — ni recherche, ni autocomplétion, ni appel au scan. API : `/catalog/search` jusqu'à 100 résultats, sans minimum de caractères, cherche aussi les références fournisseurs ; `/catalog/products` liste 500 produits. |
| GF-11 | Haute | §13, §25 | Sessions de caisse : tout détenteur de `pos.close_session` / `pos.cash_in_out` agit sur la session **d'un autre caissier** ; `/dashboard/cashier/{id}` affiche n'importe quelle session ; une vente est acceptée sur une session **fermée** ou appartenant à un autre. |
| GF-12 | Haute | §13, §22, §28 | Clôture : aucun seuil d'écart, aucune validation Manager ; l'écriture d'écart est passée en devise « HTG » codée en dur. |
| GF-13 | Haute | §22 | Aucun trigger d'audit sur `sale_return`, `cash_session`, `stock_adjustment`. |
| GF-14 | Moyenne | §6, §8 | Rôle « Caissier senior » absent. |
| GF-15 | Haute | §18, §27 | Frontend : routes sans garde de permission (seul le menu est filtré) ; même accueil pour tous ; POS : prix unitaire modifiable à l'écran, pas de session de caisse, pas de retour, devise codée en dur. |
| GF-16 | Haute | §6, §17 | Command Center : marge, bénéfice, trésorerie, créances, dettes et stock valorisé visibles avec `reporting.view` seul (responsable stock, responsable achats) — `reporting.view_financial` n'est exigée que pour d'autres écrans. |
| GF-17 | Moyenne | §11 | Annuler un panier mis en attente exige `pos.void_sale` (manager) : le caissier ne peut pas annuler son propre panier. |
| GF-18 | Moyenne | §20 | Aucune notification Manager pour une demande de retour, de remise ou de validation de clôture. |

Écarts trouvés au second contrôle (retour de recette : « le compte caissier voit le chiffre d'affaires ») :

| ID | Sévérité | Guide | Constat |
|---|---|---|---|
| GF-19 | **Critique** | §7.2 | Cause directe du retour : la base de recette `nexora_recette` était restée en V063 (les migrations n'avaient été appliquées qu'à `nexora`) et l'API de recette tournait sur un binaire antérieur. Les caissiers de la recette gardaient `reporting.view`, `finance.pay`, `finance.manage_cash` et l'ancien assistant. |
| GF-20 | **Critique** | §7.2 | `GET /documents`, `/invoices`, `/quotes` : le caissier listait **toutes** les factures, reçus et devis de l'entreprise avec leurs montants (jusqu'à 500) — de quoi reconstituer le chiffre d'affaires. |
| GF-21 | Haute | §7.2, §16 | `GET /customers` : encours et limite de crédit de chaque client (= créances globales) visibles du caissier. |
| GF-22 | Haute | §17 | `POST /reports/{id}/run` (rapport enregistré) exécutait balance âgée, valorisation du stock ou journal des ventes avec `reporting.view` seul : les permissions du rapport n'étaient pas vérifiées. |
| GF-23 | Moyenne | §18 | Responsables stock et achats : chiffre d'affaires, tendance, comparaison, journal des ventes, top produits, objectifs de CA, insight « baisse du CA ». |
| GF-24 | Haute | §13 | « Je ne peux pas sélectionner de caisse » : la seule caisse de la Pharmacie était occupée par une session laissée ouverte par un autre utilisateur ; l'option était grisée sans explication et aucun responsable ne pouvait clôturer la session d'un autre depuis l'écran. |

Points vérifiés **conformes** (aucune correction) : isolation tenant (`X-Company-Id` contrôlé contre
les entreprises du jeton + RLS sur 212 tables) ; permissions rechargées à chaque requête (un retrait
prend effet immédiatement) ; utilisateur désactivé → `security_epoch` ; idempotence paiement / vente
(`operation_uuid`, `Idempotency-Key`) ; prix recalculé serveur (garde BUG-001 de la recette) ;
ajustement de stock soumis à permission + motif ; avoirs émis uniquement depuis un retour existant ;
recommandations Intelligence avec cycle accepter / modifier / ignorer (Phase 54).

## 3. Corrections, étape par étape

Migrations `V064` à `V069` (appliquées aux bases `nexora` et `nexora_recette`). Les écarts sont tous corrigés et testés.

### Étape 1 — Rôles et permissions (V064 ; GF-01, GF-02, GF-05, GF-14, GF-16)

- **Caissier** : retrait de `reporting.view`, `finance.pay`, `finance.manage_cash`, `integrations.payments`,
  `pos.refund`, `pos.void`, `pos.export`, `pos.delete`, `pos.update`, `catalog.view`, `field_ops.view`.
  Endpoints accessibles au caissier : **94 → 44**, tous liés à la vente, aux tickets et à la synchronisation.
- **Caissier senior** (`senior_cashier`) : mêmes droits, limite de remise supérieure.
- Nouvelles permissions de validation : `pos.approve_discount`, `pos.manage_sessions` ; `pos.refund` (qui
  existait sans être branchée) devient « valider un retour / rembourser ». Accordées à manager, direction,
  administrateurs.
- `role.max_discount_percent` : caissier 5 %, senior 10 %, vendeur 5 %, manager 25 %, direction et
  administrateurs 100 %.
- Remboursement électronique (`/integrations/payments/intents/{id}/refund`) : exige en plus `pos.refund`.
- Écritures financières brutes (`/finance/cash-sales`, `/finance/credit-sales`) : exigent `finance.create`.
- Command Center et centre d'alertes : marge, bénéfice, dépenses, trésorerie, créances, dettes, stock
  valorisé, flux et alertes financières **retirés côté serveur** sans `reporting.view_financial`.
- Assistant : le briefing est choisi parmi ceux **autorisés par les permissions serveur** (403 sinon) ;
  chaque intention de « Ask » vérifie sa permission et répond « non accessible » sans aucun chiffre.

### Étape 2 — Retours avec validation Manager (V065 ; GF-03, GF-04, GF-13, GF-18)

- `sale_return_request` (+ lignes) : états `pending_approval → executed | rejected` ; contrainte en base
  demandeur ≠ validateur ; motif de refus obligatoire ; lien vers le retour exécuté.
- Caissier : `GET /pos/sales/lookup?number=` (quantités vendues / retournées / en attente / retournables),
  `POST /pos/return-requests` (aucun montant saisi : l'estimation est calculée par le serveur).
- Manager (`pos.refund`) : `approve` exécute le retour réel (stock, remboursement, écriture) **dans la
  même transaction** ; `reject` avec motif. Retour direct (`/pos/returns`, `/sales/{id}/returns`) réservé à
  `pos.refund`.
- `SalesReturnService` : quantité retournable = vendue − déjà retournée (cumul), statut de vente calculé
  sur le cumul, refus d'un retour sur panier en attente / vente annulée.
- Audit (trigger) sur `sale_return_request`, `sale_return`, `cash_session`, `stock_adjustment`.
- Notifications Manager et décision au demandeur, avec lien direct vers l'objet.

### Étape 3 — Encaissement (V066 ; GF-07, GF-08, GF-09, GF-11, GF-17)

- Produit inexistant, non actif ou non vendable refusé à la vente, au panier en attente, à la commande et
  au devis (`sale.product_not_found | product_inactive | product_not_sellable`).
- Vente à crédit : exige `pos.sell_credit` ; dépassement de limite : exige `pos.credit_override` (un motif
  seul ne suffit plus).
- Remise : limite propre = min(limite du rôle, plafond d'entreprise `pos.max_discount_percent`) ; au-delà,
  **remise exceptionnelle** : `POST /pos/discount-requests` → un Manager distinct, dans SA limite
  d'approbation, valide → l'approbation vaut pour ce panier (UUID d'opération), ce produit et ce vendeur,
  2 heures, et est **consommée** par la vente (`consumed_sale_id`). Table `pos_discount_request` auditée.
- Session citée par la vente : de l'entreprise, au vendeur (ou superviseur), ouverte — une vente hors
  ligne antérieure à la clôture reste acceptée à la synchronisation.
- Panier en attente : annulable par son auteur (sinon `pos.void_sale`).

### Étape 4 — Sessions de caisse (V067 ; GF-11, GF-12)

- Mouvements, clôture et tableau de bord caissier : uniquement sur **sa** session (sinon `pos.manage_sessions`).
- Clôture : écart ≤ `pos.cash_variance_threshold` (500 par défaut) → clôturée ; au-delà → `closing`
  (plus aucune vente), Manager notifié, validation par un superviseur distinct avec motif, puis écriture
  d'écart **dans la devise de la caisse** (était « HTG » codé en dur).
- Trigger `trg_cash_session_closed_guard` : une clôture finalisée n'est plus modifiable, même en SQL.
- Réglage `pos.require_cash_session` (défaut : non) pour imposer une session à toute vente POS.

### Étape 5 — Recherche POS et coûts (V068 ; GF-06, GF-10)

- `GET /pos/products/search` : rien avant `pos.search_min_chars` (2), au plus `pos.search_max_results`
  (8, borné 1-20), produits actifs et vendables uniquement, tri par pertinence (code exact, début du nom,
  similarité), réponse = nom, SKU, code-barres, prix de vente, disponibilité simple (`in_stock | low | out |
  untracked`) — ni quantité, ni coût, ni fournisseur. `GET /pos/products/scan` : code exact.
- `/catalog/products/{id}` : fournisseurs et prix d'achat retirés sans `catalog.view_cost` ;
  `/sales/{id}` : `unitCost` = `null` sans `catalog.view_cost`.

### Étape 6 — Frontend (GF-01, GF-10, GF-15, GF-17)

- Chaque route exige la permission de son entrée de menu (URL directe → « Accès refusé »).
- Accueil selon le rôle : caissier = « Ma caisse » + « Mes demandes » ; approbateur = « Validations en
  attente » ; checklist de mise en service réservée aux administrateurs.
- Écran POS refondu : onglets Vente / Retours / Validations ; session de caisse (ouverture, comptage à
  l'aveugle, attente de validation) ; recherche avec autocomplétion clavier / souris / tactile et scan ;
  prix non modifiable ; remise bornée avec « Demander validation » et suivi de la décision ; encaissement
  bloqué si remise non validée ou paiement insuffisant ; plus aucun identifiant technique à saisir.

### Étape 7 — Aucune reconstitution du chiffre d'affaires (V069 ; GF-19 à GF-23)

- `nexora_recette` migrée de V064 à V069 ; API de recette (port 5080) redémarrée sur le nouveau code.
  **Toute base de recette ou de démonstration doit recevoir `apply.sh <base>` après chaque migration.**
- `reporting.view_sales` : CA, nombre de ventes, panier moyen, ventes du jour, tendance, comparaison,
  alerte « baisse des ventes », journal des ventes, top produits, objectifs et insights de CA. Accordée
  aux rôles qui ont `reporting.view_financial` (Direction, Manager, Comptables, Auditeur, administrateurs).
- `documents.view_all` : sans elle (caissier, caissier senior, vendeur), documents, factures, devis et
  leur rendu sont limités à ceux que l'utilisateur a lui-même créés ou émis.
- Clients : liste sans encours ni limite de crédit hors `credit.view_portfolio` ; la fiche d'UN client
  garde son solde avec `pos.view_customer_balance` (utile à la vente à crédit).
- Rapports enregistrés : l'enregistrement et l'exécution vérifient les permissions du rapport.
- Contrôle direct sur la recette : `caisse.pharma@` et `caisse1.super@` → 403 sur pilotage, KPI, rapports,
  insights, objectifs ; 0 document ; 0 encours client visible ; assistant « non accessible ».
  `stock.pharma@` → Command Center sans CA ni tendance, rapports de ventes 403. `gerant.pharma@` → complet.

### Étape 8 — Caisse occupée (GF-24)

- `GET /pos/registers` indique qui occupe chaque caisse (`openedBy`, sans montant) ; l'écran l'affiche
  (« occupée par … ») et, si toutes les caisses sont prises, explique quoi faire.
- `GET /pos/sessions/open` (`pos.manage_sessions`) : sessions ouvertes des autres caissiers ; onglet
  Validations → « Sessions ouvertes par d'autres caissiers » → Clôturer (montant compté + motif, audité ;
  au-delà du seuil d'écart, la validation distincte s'applique).
- « Aucune validation en attente » ne s'affiche plus pendant le chargement des listes.

### Étape 9 — Validation sur place par le responsable (demande utilisateur)

Quand le responsable n'est pas devant son ordinateur, il valide **au poste du caissier** avec ses
propres identifiants, dans une fenêtre « Validation responsable » :

- Où : remise exceptionnelle (bouton « Responsable présent »), demande de retour (« Responsable présent :
  valider maintenant », ou depuis « Mes demandes »), clôture de caisse en écart (« Responsable présent :
  valider la clôture »). Valider ou refuser (motif obligatoire pour refuser et pour une clôture).
- `POST /pos/manager-override` (`pos.sell` pour le poste ; débit limité comme la connexion) — actions
  `approve_return`, `reject_return`, `approve_discount`, `reject_discount`, `validate_close`.
- Identification du responsable avec les mêmes règles que la connexion : compte actif, verrouillage après
  5 échecs (15 min), code de double authentification s'il l'a activée ; permission exigée selon l'action
  (`pos.refund`, `pos.approve_discount`, `pos.manage_sessions`) dans l'entreprise du poste ; jamais le
  caissier connecté lui-même.
- L'action est exécutée **au nom du responsable** : ses limites (remise), la règle demandeur ≠ validateur,
  les colonnes `decided_by` et le journal d'audit (`nexora.user_id`) portent son identité ; l'identité du
  poste est restaurée juste après. Aucun jeton n'est émis : le caissier ne gagne aucun droit.
- Journal de sécurité : `pos.override_succeeded`, `pos.override_failed`, `pos.override_locked`,
  `pos.override_denied`, `pos.override_refused` (sans jamais le mot de passe).
- Les refus d'identification sont des 422 (jamais 401, qui déconnecterait le caissier). Le navigateur
  n'est pas invité à mémoriser le mot de passe du responsable et le champ est vidé après chaque essai.
- Vérifié de bout en bout dans un navigateur sur une base jetable : mauvais mot de passe → message, le
  caissier reste connecté ; bon mot de passe → « Validé par Direction ABC », encaissement débloqué.

## 4. Critères d'acceptation du POS (§28)

| Test | Résultat | Preuve |
|---|---|---|
| Caissier connecté → interface limitée | OK | `CashierAccessTests`, recette navigateur (menu : POS, Documents, Clients, Synchronisation) |
| Recherche produit → résultats courts et pertinents | OK | `Pos_search_returns_a_short_relevant_list_of_sellable_products_only`, navigateur (« para » → 1 résultat) |
| Scanner fonctionnel | OK | `ScanAsync` (test ci-dessus), Entrée sans suggestion = recherche exacte |
| Scanner indisponible → recherche manuelle | OK | autocomplétion après 2 caractères |
| Produit sélectionné → ajout au panier | OK | navigateur |
| Vente → prix / stock / paiement validés serveur | OK | `PosSalesTests`, `Inactive_or_non_sellable_products_cannot_be_sold` |
| Remise dépassant la limite → demande Manager | OK | `Exceptional_discount_needs_a_manager_approval…`, navigateur |
| Retour → demande Manager avant exécution | OK | `Cashier_return_is_only_a_request_until_a_manager_approves_it` |
| Caissier tente API finance → refusé | OK | `Cashier_is_refused_management_and_finance_apis` (13 routes) |
| Caissier tente stock global → refusé | OK | `Cashier_is_refused_global_stock_and_full_catalog` (6 routes) |
| Caissier change tenant → refusé | OK | `Cashier_switching_to_a_company_he_is_not_assigned_to_is_refused` |
| Double paiement → un seul accepté | OK | idempotence `operation_uuid` (Phase 6, `PosSalesTests`) |
| Clôture caisse → écart calculé et audité | OK | `Large_cash_variance_waits_for_a_distinct_manager…` |

## 5. Changements de comportement à connaître

- Un caissier ne remise plus jusqu'au plafond d'entreprise (15 %) mais jusqu'à 5 % ; le test
  `A_cashier_cannot_force_a_price_but_can_discount_within_the_cap` a été adapté en conséquence.
- Un détenteur de `pos.return` sans `pos.refund` ne peut plus exécuter un retour : il le demande.
- `/finance/cash-sales` et `/finance/credit-sales` exigent `finance.create` (aucun écran ne les utilisait).
- `/dashboard/cashier/{id}` exige `pos.view` (et non plus `reporting.view`) et la propriété de la session.
- Un rôle personnalisé sans `max_discount_percent` garde le plafond d'entreprise comme limite.

## 6. Points ouverts

- BUG-012 (§21) : file hors ligne et jeton en clair dans le navigateur — traité en partie 2, étape G.
- L'écran POS ne propose pas encore la vente à crédit (choix du client) ni la reprise d'un panier en
  attente ; les API existent et appliquent les nouvelles règles.
- Durée de validité d'une remise exceptionnelle : 2 heures, fixe.
- Préexistants relevés pendant les essais : `MarketplaceEcosystemTests.Usage_recorder_aggregates…` échoue
  entre 20 h et minuit (date UTC comparée à `current_date` local) ; un test API crée des ventes dans la
  base `nexora` qui sert de modèle aux tests d'intégration (règle A3).

## 7. Tests

- Intégration : `GuideFonctionnelTests` (27 scénarios, vrais utilisateurs affectés à des rôles système).
- API : `CashierAccessTests` (37 cas, connexion réelle du compte `caisse@abc-group.demo`).
- Frontend : `pos/posApi.test.ts` (7).
- Recette navigateur (headless) des parcours caissier et direction, sans écriture résiduelle en base.

---

# Partie 2 — « Résumé fonctionnel & logiques métier »

Second document de référence, confronté au code réel. Les points déjà traités par la partie 1 (rôles,
POS, retours, remises, caisse, audit, isolation entreprise, idempotence) ne sont pas répétés.

## Écarts constatés

| ID | Sévérité | Réf. | Constat |
|---|---|---|---|
| RF-01 | **Critique** | §18, §21 | Clé de signature JWT et clé de chiffrement MFA : valeurs de repli **écrites dans le code** ; l'API démarre avec elles si la configuration les omet (la check-list de mise en production les signale, sans empêcher le démarrage). |
| RF-02 | Haute | §9 | Réception fournisseur : aucune vérification du statut de la commande liée (brouillon, non approuvée ou annulée acceptée), de l'appartenance des lignes, ni de la quantité restant à recevoir (sur-réception) ; historique de coût en devise « HTG » codée en dur. |
| RF-03 | Haute | §14 | Assistant : aucune des cinq questions types n'a de vraie réponse — « rupture » renvoie un texte fixe ; bénéfices en baisse, produits à commander, plus gros débiteurs, dépenses en hausse non traités. |
| RF-04 | Moyenne | §13, §17 | Cycle des recommandations sans états « Vue » ni « Exécutée ». |
| RF-05 | Haute | §4 | Succursale : une affectation « établissement » donne accès à **toute** l'entreprise (le périmètre porte l'entreprise, les droits et les données sont calculés par entreprise). |
| RF-06 | Haute | §20 | Aucun assistant d'installation (connexion PostgreSQL testée, migrations, entreprise initiale, premier administrateur, verrouillage). |
| RF-07 | Moyenne | §15 | File hors ligne et jeton en clair dans le navigateur (BUG-012). |

Conformes : aucun secret dans `appsettings.json` (clé et chaîne de connexion vides, fournies par
l'environnement) ; rôle PostgreSQL `nexora_app` sans superutilisateur, sans `BYPASSRLS`, sans droit de
création ni propriété de table ; limitation de débit sur l'authentification et la validation sur place ;
audit des permissions (`role_permission`, `user_role`) et des réglages (`setting`, `security_policy`).

## Corrections, étape par étape

### Étape A — Secrets hors du code (RF-01)

- `StartupSecretsGuard` : hors environnement de développement, l'API **refuse de démarrer** si
  `Jwt:SigningKey` manque, fait moins de 32 caractères ou est une valeur de développement, si
  `Security:MfaEncryptionKey` ne décode pas en 32 octets, ou si la base n'est pas configurée. Aucun
  secret n'est affiché dans le message.
- `infrastructure/docker-compose.yml` : mot de passe de base, clé JWT et clé MFA obligatoires (`:?`).
- Exception unique, introduite à l'étape F : **aucune** base configurée ⇒ mode installation (seul
  l'assistant répond, 503 partout ailleurs) ; dès qu'une base est configurée, le garde s'applique.

### Étape B — Réception fournisseur contrôlée (RF-02)

`PurchasingService.ReceiveAsync` refuse : commande introuvable, non réceptionnable (brouillon, non
approuvée, annulée, close — `purchasing.po_not_receivable`), fournisseur différent
(`po_supplier_mismatch`), ligne n'appartenant pas à la commande (`po_line_mismatch`), produit différent
de la ligne (`po_line_product`), quantité au-delà du reste à recevoir (`over_receipt`), réception sans
commande quand la règle l'exige (`po_required`). La devise de l'historique de coût vient de la commande,
sinon de la devise de l'entreprise (plus de « HTG » codé en dur).

### Étape C — Assistant : vraies réponses aux questions types (RF-03)

`RuleBasedAskNexora` répond à partir des données, jamais d'un texte fixe : bénéfice en baisse
(`profit_change`, période contre période précédente), dépenses en hausse (`expense_increase`, écritures
de charges hors coût des ventes et achats), produits à commander (`reorder`, moteur de
réapprovisionnement), risque de rupture (`stockout_risk`, couverture sur 30 jours), plus gros débiteurs
(`top_debtors`), créances en retard, marge, trésorerie, total des ventes. Chaque intention vérifie la
permission de l'utilisateur (`forbidden` sinon — un caissier n'obtient aucun chiffre de l'entreprise).
Une dépense saisie crée désormais aussi son document `expense` (même transaction que l'écriture).

### Étape D — Cycle de vie des recommandations (RF-04, V070)

Nouvelle → **Vue** → Acceptée / Modifiée / Ignorée → **Exécutée** / Expirée.
`POST /intelligence/recommendations/{id}/view` enregistre qui l'a consultée et quand (`viewed_at`,
`viewed_by`) ; « Exécutée » = `resolved` (résultat constaté). L'écran affiche les libellés français, le
« Pourquoi ? » de chaque recommandation, et la liste « ouvertes » couvre Nouvelle + Vue.

### Étape E — Isolation par succursale (RF-05, V071)

- À chaque requête, `SqlBranchScopeResolver` calcule les succursales autorisées : une affectation
  entreprise, groupe ou plateforme ⇒ toute l'entreprise ; uniquement des affectations « établissement »
  ⇒ ces succursales-là. Transmis à PostgreSQL (`nexora.establishment_ids`).
- Seconde barrière RLS **restrictive** `branch_isolation` (en plus de l'isolation entreprise) sur 23
  tables opérationnelles : succursales, points de vente, entrepôts, emplacements, terminaux, caisses,
  sessions et mouvements de caisse, ventes et lignes, retours et demandes, stocks, mouvements, lots,
  ajustements, inventaires, réceptions, commandes d'achat et de vente, dépenses, documents émis, devis
  (+ règles de réapprovisionnement sans emplacement visibles de tous).
- Les données existantes sont rattachées à leur succursale ; les nouvelles le sont automatiquement
  (déclencheurs). Aucun changement pour les utilisateurs affectés à l'entreprise ou au groupe.
- Vérifié avec le vrai rôle applicatif `nexora_app` (les tests d'intégration tournent sinon en
  superutilisateur, qui contourne RLS).

### Étape F — Installation initiale (RF-06, §20, V072)

INSTALLATION → POSTGRESQL → TEST → MIGRATIONS → ENTREPRISE → ADMIN → CONFIGURATION → APPLICATION :

- **Mode installation** : l'API démarre sans base configurée ; seules `/api/v1/setup/*` et la santé
  répondent, tout le reste renvoie 503 `setup.required`. Un **jeton d'installation** est affiché dans le
  journal de démarrage du serveur (en-tête `X-Setup-Token`, comparaison à temps constant, débit limité) ;
  sans lui, 403. Le navigateur ne parle jamais à PostgreSQL : il transmet les paramètres à l'API.
- **Page PostgreSQL** (`/setup`) : hôte, port, base, utilisateur, mot de passe, SSL (Préférer, Exiger,
  Exiger + vérifier le certificat, Sans). **Tester la connexion** avant tout : version (15 minimum),
  droits (superutilisateur ou CREATEROLE ; CREATEDB si la base doit être créée), base vide. Une base
  contenant déjà des entreprises ou des utilisateurs Nexora One est **refusée**. « Continuer » n'est
  possible qu'après un test réussi sur les paramètres affichés.
- **Migrations** : les fichiers `database/migrations` sont embarqués dans l'API et appliqués comme
  `apply.sh` (une transaction par fichier, même empreinte SHA-256 dans `schema_migration`).
- **Entreprise** : nom, activité, devise ; **référentiel prêt à vendre** posé par
  `nexora.bootstrap_company_defaults` (V072, idempotente) : plan comptable de 21 comptes, 27
  correspondances du moteur comptable, journaux VT/AC/CA/BQ/OD/AN/CL, exercice de l'année, taxes
  (exonéré, TVA 10 %), unité « Pièce », comptes Caisse principale (5711) et Banque (5121), moyens de
  paiement (espèces, carte, mobile money, virement), succursale « Siège », point de vente, emplacement
  de stock, terminal, caisse enregistreuse, préfixes de numérotation. **La création d'entreprise
  existante (Groupe → nouvelle entreprise) utilise la même fonction** : elle produisait jusqu'ici un plan
  comptable vide et la première vente échouait.
- **Administrateur** : nom, e-mail, mot de passe (mêmes règles qu'ailleurs), rôle « Direction » au
  niveau de l'entreprise.
- **Paramètres** : session de caisse obligatoire, seuil d'écart de caisse, remise maximale, stock
  négatif autorisé.
- **Sécurité** : l'application se connecte ensuite avec un **rôle dédié** généré (`nexora_rt_…`, membre
  de `nexora_app`, jamais superutilisateur ni `BYPASSRLS`, mot de passe aléatoire) ; si l'installation a
  créé `nexora_app` (mot de passe connu, V020), ce rôle devient non connectable. Clé JWT et clé MFA
  générées. Le tout est écrit dans `nexora.install.json` (chemin `Install:ConfigPath`, droits 600 sous
  Linux), chargé au démarrage au-dessus des appsettings (l'environnement garde la priorité). Entreprise,
  paramètres, administrateur, rôle et audit `setup.installed` sont créés dans **une** transaction.
- **Verrouillage** : après l'installation, seconde tentative → 409, routes métier → 503
  `setup.restart_required` ; le service s'arrête pour être relancé par son superviseur (service Windows,
  systemd, conteneur — `Install:AutoRestart`), l'écran attend le redémarrage puis propose la connexion.
  Une fois l'application configurée, l'assistant répond 404.
- Image Docker : contexte de build à la racine du dépôt (migrations embarquées), dossier inscriptible
  `/var/lib/nexora` pour la configuration écrite.
- Vérifié de bout en bout : test API (installation sur une base neuve, « redémarrage », connexion de
  l'administrateur, caisse prête, paramètres appliqués) et navigateur (jeton faux refusé, base existante
  refusée, installation, relance, connexion, écran POS avec « Caisse 1 »), puis **vente réelle** sur
  l'entreprise installée : ticket VTE2026-000001, écriture équilibrée Caisse 550 / Ventes 500 / TVA 50.
- Relevés au passage : poser un prix avec un type inconnu renvoyait une erreur 500 → refus clair
  `catalog.price_type` (422) ; une erreur pendant la sonde d'une base partiellement créée pouvait la
  laisser « prête » → toute erreur de sonde rend désormais la base non prête (couvert par le test).

### Étape G — Données du navigateur chiffrées (RF-07, BUG-012)

- Coffre `shared/secureStore.ts` : AES-256-GCM (WebCrypto), IV aléatoire à chaque écriture, clé générée
  sur l'appareil **non exportable**, rangée avec les données chiffrées dans IndexedDB ; une donnée
  altérée ou chiffrée avec une autre clé n'est jamais renvoyée.
- **File des ventes hors ligne** : lecture en mémoire (écran inchangé), chaque mutation réécrite chiffrée,
  dans l'ordre et après le chargement (une vente mise en file pendant le chargement n'écrase pas la file
  existante). L'ancienne copie en clair est reprise, chiffrée puis effacée.
- **Jeton d'accès** : chiffré dans le coffre, lu au démarrage (l'écran attend avant de rediriger) ;
  l'ancienne copie en clair est reprise puis effacée ; supprimé à la déconnexion.
- Sans contexte sécurisé (HTTP hors localhost), le navigateur n'offre pas WebCrypto : le jeton ne va plus
  qu'en `sessionStorage` (perdu à la fermeture du navigateur) et la file reste persistée pour ne perdre
  aucune vente, avec le badge « File hors ligne non chiffrée » au POS. La production est servie en HTTPS.
- Limite assumée : un script exécuté dans la page (XSS) peut utiliser la clé ; la CSP (Phase 47) traite
  ce risque. Le coffre protège le stockage au repos.
- Vérifié dans le navigateur : après connexion, ni `localStorage` ni `sessionStorage` ne contiennent de
  jeton ; le coffre contient la clé (non exportable) et le jeton chiffré ; la session survit au
  rechargement.

## Correctif signalé en recette (23/09) — clôture d'une caisse sans fond de caisse

Caisse ouverte avec un fond de caisse de 0 et aucune vente : « Valider le comptage » échouait (erreur
500, `cash_movement_amount_chk`). La clôture écrivait un mouvement de sortie égal au montant attendu,
soit 0, alors qu'un mouvement de caisse n'est jamais nul. Désormais aucune sortie n'est écrite quand
l'attendu est nul (l'écart éventuel reste comptabilisé), et un apport/retrait de 0 est refusé
clairement (`pos.movement_amount`, 422). Tests : `A_session_opened_without_float_and_without_sales_closes_cleanly`,
`A_zero_cash_in_or_out_is_refused_clearly`. Suite complète : 490/490.

## Changements de comportement (partie 2)

- Production sans base configurée : l'API démarre en mode installation au lieu de refuser de démarrer.
  Avec une base configurée mais sans clés, elle refuse toujours.
- Une nouvelle entreprise créée depuis le groupe reçoit le référentiel par défaut (7 journaux au lieu
  de 5 ; test `Company_provisioned_then_archived_keeps_history` adapté).
- Un utilisateur affecté à une succursale ne voit plus les autres succursales de l'entreprise.
- Après mise à jour, chaque navigateur reprend une fois le jeton et la file en clair, puis les efface.
- Réception fournisseur : des réceptions acceptées jusqu'ici (commande non approuvée, sur-réception) sont
  refusées.

## Points ouverts (partie 2)

- L'approbation d'une commande d'achat n'exige pas encore une seconde personne (quatre yeux).
- Les paiements clients et fournisseurs ne sont pas filtrés par succursale (rattachés à l'entreprise).
- L'assistant d'installation crée une entreprise autonome ; le rattachement à un groupe se fait ensuite.
- Recette : la session ouverte par la direction sur « Caisse officine » (base `nexora_recette`) est à
  clôturer ; le Supermarché n'a qu'une caisse pour deux caissiers (« Caisse 2 » à ajouter si besoin).

## Tests (partie 2)

- Intégration : isolation par succursale avec le rôle applicatif réel, réception contrôlée, assistant
  (intentions et refus), cycle des recommandations, dépenses documentées, création d'entreprise prête à
  vendre.
- API : `SetupWizardTests` (installation complète sur base neuve ; assistant fermé une fois configuré),
  `StartupSecretsGuardTests` (refus avec base sans secrets ; mode installation sans configuration).
- Frontend : `setup/setupApi.test.ts` (5), `shared/secureStore.test.ts` (5), `shared/offlineQueue.test.ts`
  (10, dont chiffrement, reprise de la copie en clair, course au chargement, repli signalé).
- Recette navigateur : assistant d'installation complet sur une API sans configuration, puis vente
  réelle ; stockage chiffré du jeton sur la base de recette.
- Totaux : backend **488/488** (18 unitaires, 110 API, 360 intégration, exécutés en parallèle), frontend
  **138/138** (32 fichiers), typage TypeScript sans erreur.
