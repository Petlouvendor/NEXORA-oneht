# Phase 55 — Quality Assurance & Acceptance Testing — implémentation

**Statut : grille exécutée, défauts majeurs corrigés, décision NO-GO production documentée.**
Migration `V063` (1 table, 1 colonne). **405 tests backend** verts (18 unit + 64 API + 323
intégration), **118 tests frontend**, `tsc` vert. Base : 251 tables, 212 sous RLS, 63 migrations.

Résultat détaillé, ligne par ligne, avec preuve : [`phase-55-grille-recette.md`](phase-55-grille-recette.md).
La grille fournie sert de modèle : chaque identifiant (AUTH-001 … DEP-013) y est repris avec son
statut ; le modèle vierge n'est pas dupliqué dans `docs/`.

## Démarche

La grille a été exécutée contre le code réel, pas contre les documents de phase. Pour chaque ligne :
chercher la route, le service et le test qui la prouvent ; s'il n'y a pas de preuve, la ligne n'est
pas PASS (règle §1). Là où une fonction manquait ou était fausse, elle a été corrigée, testée, puis
la ligne rejouée (`RETEST → PASS`). Les preuves qui ne relèvent pas d'un test automatisé (migration
sur base vide, sauvegarde / restauration, parcours navigateur) ont été **exécutées** pendant la
recette et leurs résultats consignés.

## Défauts trouvés et corrigés

| ID | Sévérité | Défaut | Correction |
|---|---|---|---|
| BUG-001 | **Critique** | À l'encaissement, `unitPriceOverride` et `lineDiscount` venus du client étaient acceptés tels quels : un vendeur pouvait vendre à 0,01, remiser au-delà du plafond (le réglage `pos.max_discount_percent` n'était lu nulle part), passer sous le prix plancher, ou remiser plus que la ligne (total négatif) | `ISalePricingGuard` / `SqlSalePricingGuard` : quantité > 0, montants ≥ 0, remise ≤ ligne, prix forcé ⇒ `pos.change_price`, remise ⇒ `pos.discount` et ≤ plafond, prix net ≥ `min_price`. Appliqué aux trois saisies (vente, commande, devis) ; les conversions internes devis → commande → vente, dont les prix ont déjà été contrôlés, passent par des méthodes `internal` |
| BUG-002 | Haute | Aucune API pour créer ou modifier un client ou un fournisseur (import ou SQL uniquement). Les permissions `customers.*` / `suppliers.*` existaient depuis V021, jamais branchées | `ICustomerService` / `ISupplierService`, `/customers` (liste, recherche, fiche, création, modification), `POST/PUT /suppliers`. Numérotation automatique `CLI-00001` / `FRN-00001`. Ouvrir ou modifier une limite de crédit exige `credit.set_limit` |
| BUG-003 | Haute | La déconnexion ne faisait que vider le navigateur ; révoquer une session (écran Sessions, Phase 11) ne coupait pas son jeton | Claim `jti` dans le jeton, enregistré sur `user_session.token_id` ; le middleware refuse un jeton dont la session est révoquée ; `POST /me/logout` ; `AuthContext.logout()` l'appelle |
| BUG-004 | Haute | Ni changement ni réinitialisation de mot de passe | `POST /me/password` (mot de passe actuel exigé, toutes les sessions tombent) ; `POST /auth/password/forgot` (réponse identique compte existant ou non, 3 demandes / heure, code haché à usage unique de 30 min, remis par le canal email) ; `POST /auth/password/reset` (lève aussi le verrouillage anti-force-brute) ; politique de mot de passe unique (≥ 10 caractères, lettre + chiffre) |
| BUG-005 | Haute | Aucune création d'utilisateur — le cas « utilisateur nouvellement créé » était prévu en Phase 22 mais jamais implémenté — et aucun écran d'administration des utilisateurs | `POST /users` : compte **et** première affectation dans une même transaction, via le garde anti-élévation existant ; un refus annule la création. Onglet « Utilisateurs » (liste, création, suspension / réactivation) |
| BUG-006 | Haute | La politique de débit `auth` (10 req/min/IP) était définie en Phase 3 et attachée à aucun endpoint | `[EnableRateLimiting("auth")]` sur `AuthController` |
| BUG-007 | Haute | Fiche produit non modifiable | `PUT /catalog/products/{id}` ; SKU, unité, type, statut et prix hors de portée par conception ; catégorie / marque / taxe absentes = inchangées |
| BUG-008 | Moyenne | Aucun libellé de formulaire n'était relié à son champ (accessibilité) | `Field` relie libellé, aide et erreur (`htmlFor`, `aria-describedby`, `aria-required`) — corrige tous les formulaires |
| BUG-009 | Moyenne | `apiFetch` levait une erreur de parsing sur un succès sans corps (202) | lecture tolérante du corps |
| BUG-010 | Basse | Conflit de suivi EF en modifiant un produit créé dans la même requête (trouvé par le test avant livraison) | recopie des valeurs sur l'entité déjà suivie |

Ajustement de conception pendant la correction : un mauvais mot de passe actuel renvoie 422 et non
401, sinon le client web déconnecte l'utilisateur sur une faute de frappe.

## Défauts laissés ouverts (à arbitrer)

| ID | Sévérité | Défaut |
|---|---|---|
| BUG-011 | Moyenne | Aucun export du catalogue |
| BUG-012 | Moyenne | Jeton et file hors ligne en clair dans le stockage du navigateur |
| BUG-013 | Basse | Écran « Clients & fournisseurs » masqué à un acheteur sans `customers.view` |
| BUG-014 | Haute (test manquant) | Annulation de vente (`void`) jamais testée |
| BUG-016 | Moyenne | `POST /finance/expenses` ne crée que l'écriture comptable, jamais la ligne du registre `expense` : rapports de dépenses et insight « hausse de dépenses » inopérants (trouvé en générant le jeu de recette) |

## Jeu de données de recette (BUG-015, fermé)

Base dédiée `nexora_recette`, reconstruite à l'identique par `database/scripts/recette.sh` :
schéma, démo ABC GROUP, Supermarché rendu opérationnel (`database/seeds/recette/01_supermarche_config.sql`),
puis le générateur `backend/tools/Nexora.RecetteSeed` qui rejoue ~100 jours d'activité **par les
services métier réels** (pharmacie + supermarché : 48 produits, 14 clients aux profils de crédit
variés, 8 fournisseurs, ~2 400 ventes datées, achats avec réception partielle et facture en écart,
retours, caisse avec écart, lots expirés, 13 comptes par rôle dont 1 suspendu). Contrôle :
`database/seeds/recette/verif_jeu_recette.sql`. Détail scénario par scénario :
[`phase-55-jeu-de-donnees-recette.md`](phase-55-jeu-de-donnees-recette.md).

Construire ce jeu par les services réels a révélé **cinq défauts** invisibles aux tests (qui ne
créent que des cas mono-ligne) :

| ID | Sévérité | Défaut | Correction |
|---|---|---|---|
| BUG-017 | Haute | Poser un prix dans une autre devise fermait le prix de la devise principale | `CatalogService.SetPriceAsync` : la clôture du prix précédent est filtrée par devise |
| BUG-018 | **Critique** | Toute réception fournisseur de plus d'une ligne échouait (UUID d'opération répété sur chaque mouvement de stock, contrainte `stock_movement_source_op_uk`) ; même défaut sur les retours clients multi-lignes | seul le premier mouvement porte l'UUID, qui reste l'ancre d'idempotence (`PurchasingService.ReceiveAsync`, `SalesReturnService`) |
| BUG-019 | Haute | Un article sans prix en vigueur passait en caisse à 0 (seule la contrainte comptable finissait par bloquer, avec une erreur technique) | `SqlSalePricingGuard` : refus `sale.no_price` |
| BUG-020 | Basse | Une réception sans ligne de commande rétrogradait la commande « envoyée » en « approuvée » | `RefreshOrderStatusAsync` ne rétrograde plus |
| BUG-016 | Moyenne | Registre des dépenses jamais alimenté | **ouvert** (voir ci-dessus) |

Tests : `RecetteDatasetFindingsTests` (4, nouveau).

## Preuves exécutées pendant la recette

- **Migration sur base vide** (DB-001/002) : base vierge, 63 migrations en 45 s, 251 tables ;
  second passage entièrement ignoré.
- **Sauvegarde / restauration** (DB-010/011) : `pg_dump` de `nexora` (1,56 Mo), `pg_restore` en
  14 s dans une base neuve ; comptages identiques sur 8 tables clés, 0 écriture comptable
  déséquilibrée, 212 politiques RLS restaurées. Bases et fichier temporaires supprimés ensuite.
- **Parcours navigateur** (Chromium sans interface) : mode « mot de passe oublié », lien
  `/reset-password?token=` pré-rempli, écran Clients & fournisseurs, onglet Utilisateurs, jeton
  refusé (401) juste après déconnexion. Aucune erreur console hors celle provoquée volontairement.
- **Rôle applicatif** : `nexora_app` sans superutilisateur, `BYPASSRLS`, `CREATEROLE`, `CREATEDB`.
- **Non-contamination** (risque A3 de la Phase 49) : après les tests API, les volumes de la base de
  développement sont inchangés (seules des sessions de connexion ont été ajoutées).

## `V063` — contenu

- `user_session.token_id` (uuid, unique partiel) — lien jeton ↔ session.
- `password_reset` (RLS par utilisateur, comme `mfa_challenge`) : hachage SHA-256 du code, expiration,
  consommation unique, IP de la demande.
- Aucune nouvelle permission : `customers.*`, `suppliers.*`, `admin.manage_users`,
  `pos.change_price`, `pos.discount` existaient déjà et sont enfin appliquées.

## Backend

| Couche | Fichiers |
|---|---|
| Contracts | `Parties/PartyContracts.cs` (nouveau) ; `Identity/IdentityContracts.cs` (+ création d'utilisateur, mots de passe) |
| Application | `Parties/IParties.cs` (nouveau) ; `Identity/IAccount.cs` (nouveau : `IAccountService`, `PasswordPolicy`) ; `Identity/Abstractions.cs` (+ `CreateUserAsync`) ; `Sales/Abstractions.cs` (+ `ISalePricingGuard`) ; `Sales/SalesService.cs`, `OrderService.cs`, `QuoteService.cs` (garde + chemins internes) ; `Catalog/CatalogService.cs` (+ `UpdateProductAsync`) ; `Security/PermissionCatalog.cs` (+ `Customers`, `Suppliers`) ; `Security/Abstractions.cs` (+ `RevokeTokenAsync`) |
| Infrastructure | `Parties/SqlPartyServices.cs` (nouveau) ; `Identity/SqlAccountService.cs` (nouveau) ; `Identity/UserAssignmentService.cs` ; `Identity/JwtTokenService.cs` (`jti`) ; `Security/ApprovalAndSession.cs` ; `Sales/SalesInfrastructure.cs` (+ `SqlSalePricingGuard`) ; `Inventory/CatalogInventoryStores.cs` |
| API | `CustomersController.cs` (nouveau) ; `PurchasingController.cs` (fournisseurs) ; `AuthController.cs` (débit, mot de passe oublié) ; `MeController.cs` (déconnexion, mot de passe) ; `UsersController.cs` ; `CatalogController.cs` ; `Middleware/TenantResolutionMiddleware.cs` (session révoquée) |

## Tests

- **Intégration** — `RecetteCorrectionsTests` (11, nouveau) ; `PosSalesTests` (+3 : prix forcé,
  plafond / ligne / quantité, prix plancher) ; `OperationsReadinessTests` inchangé.
- **API** — `RecetteEndpointsTests` (7, nouveau) : déconnexion réelle, routes protégées, isolation
  des fiches clients, charges invalides sans écriture, mot de passe oublié, débit 429, sondes
  d'injection SQL. Aucun de ces tests n'écrit de donnée métier dans la base de développement.
- **Frontend** — `accountApi.test.ts` (4), `partiesApi.test.ts` (3), `apiClient.test.ts` (+1).

## Frontend

- Connexion : « Mot de passe oublié ? », saisie du code, route publique `/reset-password`.
- Paramètres : onglet « Mot de passe ».
- Nouveau module `src/parties/` : écran « Clients & fournisseurs » (`/customers`, menu Opérations).
- Administration : onglet « Utilisateurs ».
- Catalogue : « Modifier la fiche » dans le tiroir produit.
- `Field` (accessibilité) et `apiFetch` (succès sans corps) corrigés à la source.

## Décision

**NO-GO production** (voir §38 de la grille) : performance jamais mesurée, annulation de vente non
prouvée, marge et bénéfice non réconciliés indépendamment, guides utilisateur absents. Une RC sur
`staging` est défendable avec ces réserves écrites. Les signatures §39 reviennent aux personnes
désignées.

## Manques assumés (backlog)

- Bancs de charge et seuils de performance (PERF-001 → 008)
- Audit d'accessibilité WCAG 2.2 AA complet, compatibilité appareils, tests tactiles
- Tests d'écran (Playwright) au-delà des 4 parcours vérifiés
- Environnement de production (TLS, secrets, CORS réels)
- BUG-011 → BUG-014 et BUG-016 ci-dessus
