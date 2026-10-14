# 10 — Plan de correction

> Proposition seulement : aucune correction n'a été faite pendant l'audit. Ordre imposé par le cahier d'audit :
> (1) sécurité et intégrité des données, (2) erreurs de compilation ou d'exécution, (3) fonctionnalités essentielles
> inutilisables, (4) incohérences frontend/backend/base, (5) flux métier, (6) fonctionnalités partielles, (7) tests,
> (8) performance et maintenabilité, (9) améliorations secondaires. Chaque correction suppose d'abord le test de
> non-régression qui reproduit le défaut (il doit échouer avant la correction).

## Étape 0 — Préalables (sans toucher au comportement)

| ID | Action | Critère d'acceptation |
|---|---|---|
| A-001 | Décider du sort de la modification non commitée de `Nexora.ApiTests.csproj` (restaurer la référence `Nexora.Api` si elle n'était pas voulue) | `dotnet build Nexora.sln` sans erreur sur la copie de travail |
| T-002 | Appliquer réellement la chaîne de connexion des tests d'API (variable `Database__ConnectionString` dans la fabrique, ou options lues à la construction du `DbContext`) | Un passage des tests d'API n'écrit plus dans `nexora` (comptage avant/après) |
| T-001 | Ajouter une fixture d'intégration utilisant le rôle `nexora_app` pour les tests de sécurité et de portée système | Les tests A-009/A-031 échouent avant correction, passent après |

## Priorité 1 — Sécurité et intégrité des données

| ID | Fichiers | Cause | Correction | Dépendances | Risques de régression | Tests de validation | Critère d'acceptation |
|---|---|---|---|---|---|---|---|
| **A-031** | `Operations/SqlDataCorrectionService.cs`, `SqlMaintenanceService.cs`, `SqlJobRegistry.cs`, `SqlFeatureFlagService.cs`, `SqlRetentionPurgeService.cs`, `OpsMaintenanceController.cs`, migration de permissions | Console de portée plateforme protégée par des permissions accordées à des rôles d'entreprise ; lectures et mises à jour en portée système | Décision produit : (a) rôle « opérateur plateforme » distinct, retrait de `operations.*` des rôles `manager` et `owner_direction` (nouvelle migration) ; ou (b) bornage à l'entreprise active de chaque requête, fenêtre de maintenance et tâches par entreprise. Retirer `BeginSystemScope` des lectures de corrections | T-001 ; décision sur le modèle d'exploitation SaaS vs sur site | Écrans d'exploitation des clients sur site : prévoir l'attribution du nouveau rôle à l'installation | Deux entreprises de groupes différents : liste vide, 404 sur client étranger, 403 sur maintenance | Aucun rôle d'entreprise ne lit ni n'écrit hors de ses entreprises par `/ops/*` |
| **A-002** | `Application/Sales/SalesReturnService.cs`, `Infrastructure/Sales/SalesInfrastructure.cs:378-387` | Aucun `cash_movement` `sale_refund` écrit au remboursement espèces | Écrire le mouvement (montant négatif) dans la session du retour, dans la transaction du retour ; exiger une session ouverte et un compte de remboursement | A-005 (même service) | Clôtures déjà faites : ne pas réécrire l'historique ; prévoir un rapport des sessions affectées | Vente + retour espèces + clôture comptée = fond | Écart 0 ; 5711 crédité une fois ; aucune charge 6580 |
| **A-018** | `Infrastructure/Finance/FinanceStores.cs:100-105` | Lecture-modification-écriture du solde sans verrou | `UPDATE financial_account SET current_balance = current_balance + @delta RETURNING current_balance` ; contrôle planifié de cohérence (solde = ouverture + Σ transactions) | Tous les appelants d'`AdjustBalanceAsync` (moteur, paie, comptes clients) | Faible (même sémantique) | 20 opérations concurrentes sur un compte | Aucune dérive ; script de recalcul fourni pour les bases existantes |
| **A-021** | `Infrastructure/Finance/SqlGeneralAccounting.cs`, `SqlFinancialReports.cs:26-118` | États cumulés depuis l'origine + écriture d'à-nouveaux | Choisir un modèle : grand livre continu sans à-nouveaux, ou à-nouveaux avec états bornés à l'exercice (exclure le journal AN des cumuls antérieurs) | A-020 | Comptabilités ayant déjà généré des à-nouveaux : écriture corrective documentée | Bilan identique avant/après génération des à-nouveaux | Soldes de bilan non doublés |
| A-003 | `Api/Controllers/PosController.cs:111-115`, `SalesController.cs:21-25`, `Application/Sales/SalesService.cs:102,611-613` | `SoldAt` lié depuis le corps HTTP ; session close acceptée si `SoldAt <= ClosedAt` | Ignorer `SoldAt` sur les canaux en ligne ; l'accepter seulement depuis la synchronisation d'un appareil enrôlé, borné à [ouverture de session, maintenant] | Synchro hors ligne (même service) | Rejeu hors ligne d'une vente faite avant la clôture : garder ce cas par le canal de synchro | Vente directe dans session close → 409 ; rejeu hors ligne légitime → accepté | Aucune vente en ligne datée par le client |
| A-009 | `Persistence/RlsConnectionInterceptor.cs`, `Tenancy/TenantContext.cs`, `Persistence/UnitOfWork.cs`, `Identity/MfaService.cs` | Contexte RLS posé à l'ouverture de connexion seulement ; `bypass_rls` positionnable par le rôle applicatif | Réappliquer le contexte (`SET LOCAL`) à chaque entrée/sortie de portée quand une connexion est ouverte ou interdire la portée système en transaction ; à terme, rôle base distinct pour les tâches système | T-001 | Élevé : touche tous les services ; à couvrir par la suite d'intégration exécutée avec `nexora_app` | Réinitialisation MFA d'un tiers → codes supprimés | Aucune différence de comportement entre superutilisateur et `nexora_app` dans la suite |
| A-010 | `Identity/UserAssignmentService.cs:305-351` | Garde anti-élévation limitée à l'attribution | Même garde (cible ≤ appelant) sur suspension, verrouillage et retrait ; invalider la session au retrait | — | Faible | Admin société ne peut pas suspendre la direction | 403 |
| A-032 | `Security/ApprovalAndSession.cs:496-510` | Seconde requête non bornée à l'utilisateur | Ne révoquer les jetons que si la session appartient à l'appelant | — | Nul | Session étrangère → 404, jetons intacts | — |

## Priorité 2 — Compilation et exécution

| ID | Correction | Critère |
|---|---|---|
| A-001 | voir étape 0 | build vert |
| A-004 | Contrôle d'idempotence du moteur dans la transaction, capture de la violation d'unicité → rejeu ; date de trésorerie = `OccurredAt` | Doublon concurrent → 200 identique ; trésorerie et grand livre à la même date |
| Obs. | `IdempotencyFilter` : verrou par clé, stockage partagé (base) si plusieurs instances | Deux requêtes simultanées de même clé → une exécution |
| Obs. | `/ops/database` → 500 constaté une fois sous charge : rejouer `OpsMaintenanceEndpointsTests` seul et capturer le journal | Test vert ou cause identifiée |

## Priorité 3 — Fonctionnalités essentielles inutilisables

| ID | Fichiers | Correction | Critère |
|---|---|---|---|
| A-017 | `V002__currency.sql`, nouvel écran et point d'accès taux, `EfExchangeRateProvider` | Saisie historisée des taux (permission finance) ; décider global vs par entreprise | Installation neuve → saisie d'un taux → vente USD comptabilisée |
| A-012 | `Sales/SalesInfrastructure.cs:111-112` (`PriceCartAsync`) | Coupon fixe appliqué une fois au panier, réparti au prorata ; règle de cumul promo + coupon explicite ; plafond de remise | Coupon 50 sur 3 lignes → 50 |
| A-007 | `Application/Inventory/InventoryOperationsService.cs:284-417` | Écart calculé sur le stock courant à la validation (ou mouvements intervenus soustraits) ; lignes par lot | Ouverture → vente → validation : stock final = compté |
| A-020 | `SqlGeneralAccounting.cs` (`PnlBalancesAsync`) | Borner à l'exercice (`periodId` réellement utilisé) | Deux exercices clôturés → comptes de gestion à zéro |

## Priorité 4 — Incohérences frontend / backend / base

| ID | Correction | Critère |
|---|---|---|
| A-013 | Passer la liste de prix du client au calcul serveur unique (caisse, commandes, devis) ; un seul moteur de prix | Simulateur = caisse pour tout client |
| A-014 | `POST /sales/{id}/payments` → service des comptes clients (permission `credit.receive_payment`, session, devise) ou suppression | Caissier sans droit → 403 |
| A-015 | Exploiter `pack_size` et décoder les codes à poids | Carton de 12 → 12 unités |
| A-024 | Authentifier `/metrics` dans l'API ; bloquer par préfixe dans Caddy | Variantes de chemin → 401/404 |
| A-029 / A-030 | Politiques `WITH CHECK (company_id IS NOT NULL …)` hors portée système ; RLS sur `role_permission` ; révoquer les privilèges inutiles de `nexora_app` | UPDATE d'un rôle système refusé par la base |

## Priorité 5 — Flux métier

| ID | Correction | Critère |
|---|---|---|
| A-019 | Comptabiliser apports, sorties et prélèvements de caisse (contrepartie choisie) avec transaction de trésorerie | Solde trésorerie caisse = Σ mouvements de session |
| A-023 | Opération « solde d'ouverture » (transaction + écriture contre capitaux ou compte d'attente) | Trésorerie = grand livre à la mise en service |
| A-005 | Verrou de la vente source au retour ; compte de remboursement obligatoire en espèces ; `Restocked` réel | Deux retours simultanés → un refusé |
| A-006 | Extourne d'une écriture de document refusée (passer par l'annulation métier) ou effets de sous-registres inversés | Créance cohérente après extourne |
| A-016 | Estimation du crédit hors ligne à partir du prix calculé localement (profil) | Plafond hors ligne appliqué |

## Priorité 6 — Fonctionnalités partielles

A-022 (âgé et prévision : `remaining_amount`), A-008 (semaine ISO), bascules de fonctionnalités à brancher ou retirer
(A-031), A-026 (règle d'arrondi unique), MonCash : remboursement absent (limite fournisseur, à documenter à l'écran).

## Priorité 7 — Couverture des tests

Écrire les tests du §5 de `08-tests-et-resultats.md` ; faire tourner la suite d'intégration aussi avec `nexora_app` ;
exécuter la suite E2E sur installation neuve ; recettes avec comptes bac à sable MonCash, SMTP, Twilio, FCM ; test de
charge (aucun aujourd'hui).

## Priorité 8 — Performance et maintenabilité

P-001 (cache par requête du profil de permissions) ; A-028 (scan de dépendances bloquant) ; retirer FluentValidation ou
l'utiliser ; A-027 (code mort) ; messages PostgreSQL bruts renvoyés au client (`UnitOfWork`) ; lecture ligne à ligne
des 503 fichiers restants (voir 03).

## Priorité 9 — Secondaire

A-011 (temps constant à la connexion, anti-rejeu TOTP, portée de la réinitialisation MFA) ; A-025 (réviser le rapport
de préparation finale à partir de la matrice 04).

## Ordre d'exécution recommandé

1. Étape 0 (A-001, T-002, T-001) : sans elle, aucune correction ne peut être prouvée.
2. A-018 (une requête, aucun risque) puis A-002 et A-003 (caisse).
3. A-031 (décision produit requise sur le modèle d'exploitation) et A-009.
4. A-021 puis A-020 (comptabilité ; décision sur le modèle d'à-nouveaux).
5. A-012, A-007, A-017.
6. A-010, A-032, A-014, A-013, A-019, A-023.
7. Le reste par priorité, puis recette complète (unitaires, intégration avec `nexora_app`, API, Vitest, E2E).
