# Corrections issues de l'audit technique intégral (2026-10)

> Suivi, étape par étape, des corrections du plan `audit-nexora-one/10-plan-de-correction.md`.
> Pour l'équipe technique et la personne qui validera la recette. Identifiants : ceux de
> `audit-nexora-one/09-anomalies-et-priorites.md`.

## Bilan (2026-10-15)

**Fait.** Étapes 0 à 19, toutes fusionnées dans `develop` : toutes les anomalies du registre sont corrigées, sauf
A-026 (aucun changement nécessaire : la paie arrondit une seule fois, avant comptabilisation). P-001 est traité à
l'étape 18.

**Résultats** (modèle jetable migré jusqu'en V102) : unitaires 103/103 ; intégration 530/530 ; API 200/200 (sur un clone
jetable, avant le dernier correctif de la recherche caisse, qui ne touche pas l'API testée) ; frontend 279/279, typage et
lint sans erreur.

**Décisions prises (2026-10-15, voir « Décisions d'exploitation » en fin de document)** : un client par installation
(A-031) ; taux de change communs à l'installation (A-017) ; écran des bascules retiré pour la version 1 ; reprise des
soldes d'ouverture par la commande `reprise-ouvertures` (étape 20).

**En attente :**

- **Migrations V100, V101, V102 et reprise des soldes d'ouverture** sur les bases partagées `nexora` (développement,
  modèle des tests) et `nexora_recette` : accord donné, sauvegardes faites et vérifiées
  (`C:\Users\Rener\nexora-sauvegardes\2026-10-15-avant-V100`), mais l'exécution est bloquée par le garde-fou de
  l'assistant ; procédure à lancer à la main, à l'étape 20. V100 ne modifiera aucun solde (aucun compte en écart relevé).
- A-021 : modèle d'à-nouveaux retenu (grand livre continu, à-nouveaux limités au résultat) à confirmer par le comptable.
- Données de recette : 77 ventes par carte en gourdes de la Pharmacie ont été encaissées sur le compte Unibank en
  dollars (+71 690,15 « USD » en trésorerie, absents du grand livre en USD) — étape 20.
- **Non vérifié faute d'environnement :** configuration Caddy (Docker arrêté), job CI du scan de dépendances (pas
  d'exécution GitHub Actions locale), suite E2E, test de charge.
- Observation ouverte : collisions intermittentes de numéros clients dans `CustomerAccountTests` (étape 13).

## Étape 0 — Compilation et isolation des tests d'API (A-001, T-002)

**A-001.** La copie de travail retirait la référence `Nexora.Api` du projet `Nexora.ApiTests` (modification non
commitée) : la solution ne compilait plus (75 erreurs). Référence restaurée ; `dotnet build Nexora.sln` : 0 erreur.

**T-002.** L'infrastructure lisait la chaîne de connexion à l'enregistrement des services. Une chaîne déclarée ensuite
par une fabrique de tests (`ConfigureAppConfiguration`) était ignorée : les tests d'API écrivaient toujours dans la base
de développement `nexora`, qui sert aussi de modèle aux tests d'intégration. La chaîne est désormais lue à la
construction du contexte (`IOptions<NexoraDbOptions>`), comme le faisait déjà le verrou du planificateur.

**Test.** `TestDatabaseTargetTests` : une fabrique qui déclare une autre base obtient un contexte connecté à cette base
(aucun accès réseau).

## Étape 1 — Solde des comptes financiers sans perte sous concurrence (A-018, CRITIQUE)

**Défaut.** `AdjustBalanceAsync`, seul écrivain de `financial_account.current_balance`, lisait le compte, ajoutait le
montant en mémoire puis écrivait la valeur calculée. Deux opérations simultanées sur un même compte (une vente et un
transfert, que des verrous de numérotation différents ne sérialisent pas) perdaient une mise à jour, sans erreur :
525 HTG perdus sur 20 opérations lors de l'audit. Le tableau de bord, le cockpit de trésorerie et le contrôle de solde
des transferts lisent ce solde.

**Correction.** Ajout atomique en base (`current_balance = current_balance + montant`) ; la ligne reste verrouillée
jusqu'à la fin de la transaction de l'appelant. L'entité éventuellement suivie par le contexte est réalignée pour
qu'EF ne réécrive pas l'ancien solde.

**Reprise de l'existant.** V100 réaligne les comptes déjà en écart sur la définition du solde (ouverture + somme des
transactions, qui n'ont jamais été perdues). Aucun écart n'existait sur `nexora` ni `nexora_recette` lors de l'audit.

**Tests.** `TreasuryConcurrencyTests` : 12 ajouts simultanés sur un même compte → solde 12 (l'ancien code donnait **1**,
vérifié en le rétablissant le temps du test) ; transferts simultanés partageant un compte → solde stocké = ouverture +
Σ transactions sur les trois comptes. Non-régression : moteur, trésorerie, cockpit, comptes clients, paie, caisse
(70 tests).

**À faire hors code.** Appliquer V100 sur `nexora` et `nexora_recette` (`Nexora.Api.dll migrate`) : refusé à l'outil
pendant la correction, en attente de l'accord du propriétaire.

## Étape 2 — Retours : remboursement espèces dans la session de caisse, contrôles sous verrou (A-002 CRITIQUE, A-005)

**Défaut (A-002).** Un retour remboursé en espèces sortait de la trésorerie (écriture et transaction) sans aucun
mouvement dans la session de caisse : le type `sale_refund` existait dans la base mais n'était jamais écrit. À la
clôture, l'attendu ignorait le remboursement : faux manquant imputé au caissier, puis l'écart comptabilisé (charge
6580) faisait baisser la caisse une seconde fois. Constaté lors de l'audit : vente 75, retour 75, clôture comptée 0 →
écart −75.

**Défauts (A-005).** Quantité retournable contrôlée hors transaction et sans verrou (deux retours simultanés pouvaient
dépasser la vente) ; verrou `FOR UPDATE` sur la créance pris hors transaction, donc relâché aussitôt ; remboursement
espèces accepté sans compte (l'écriture créditait le compte d'attente, aucune trésorerie ne bougeait) ; indicateur
« remis en stock » toujours vrai.

**Correction.**
- Remboursement espèces : compte de décaissement obligatoire ; s'il s'agit du compte de la caisse de la session, la
  session doit être ouverte et un mouvement `sale_refund` (montant négatif) est écrit dans la même transaction que le
  retour. Un remboursement depuis un autre compte (coffre, banque) ne touche pas la session, comme les remboursements
  d'avoir.
- Vente verrouillée (`FOR UPDATE`) au début de la transaction ; statut, quantités déjà retournées et reste dû relus
  sous ce verrou.
- `Restocked` vrai seulement si un article est réellement revenu en stock.

**Tests.** `SaleReturnCashTests` : vente 300 + retour espèces depuis la session → mouvement −300, clôture au fond de
caisse sans écart, trésorerie inchangée ; refus sans compte (`pos.refund_account_required`) et depuis une session close
(`pos.cash_session_closed`) ; deux retours simultanés de 6 sur 10 vendus → un seul accepté ; retour sans remise en stock.
Non-régression : caisse, paiements, comptes clients, guide fonctionnel, recette, achats (88 tests).

## Étape 3 — Date de vente fixée par le serveur (A-003)

**Défaut.** La commande d'encaissement liait `SoldAt` depuis le corps HTTP de `/pos/checkout` et `/sales`. Un
caissier pouvait antidater une vente ; le contrôle de session acceptait une session **close** quand la date fournie
précédait sa fermeture (règle prévue pour le rejeu hors ligne). Constaté lors de l'audit : vente acceptée dans une
session déjà comptée et clôturée, mouvement de caisse ajouté après le comptage.

**Correction.**
- `/pos/checkout` et `/sales` ignorent la date fournie : la vente est datée par le serveur. L'interface de caisse
  rejoue ses ventes hors ligne par la synchronisation (`/sync`), seul canal qui garde la date de l'appareil.
- Date d'une vente rejouée : jamais plus de 5 minutes dans le futur (`pos.sold_at_future`), jamais avant l'ouverture
  de sa session (`pos.sold_at_outside_session`).

**Tests.** `SaleDateControlTests` : date future refusée ; vente datée avant l'ouverture de sa session refusée ; après
clôture, une vente datée par le serveur est refusée (`pos.session_closed`). Non-régression : caisse, synchronisation
hors ligne, caisse hors ligne, commandes, restaurant, terrain, ventes, guide, date métier (97 tests).

**Conséquence de l'étape 2 à connaître.** Une demande de retour d'un caissier approuvée par un manager après la clôture
de la session du caissier ne peut plus être remboursée depuis ce tiroir clos (`pos.cash_session_closed`) : le manager
rembourse depuis une session ouverte.

## Étape 4 — Console d'exploitation : isolation entre clients de la plateforme (A-031, CRITIQUE en SaaS)

**Défaut.** La console `/ops/*` agit sur toute l'installation (portée système, ou tables sans entreprise), mais ses
permissions `operations.*` sont portées par des rôles d'entreprise (`owner_direction`, et `manager` pour la lecture).
Sur une plateforme qui héberge plusieurs clients : un responsable lisait les demandes de correction de données de tous
les clients (téléphones, e-mails, identifiants fiscaux) ; un directeur pouvait corriger un client, un fournisseur, un
produit ou un utilisateur d'un autre client, ouvrir une maintenance qui suspend les écritures (caisse comprise) de
toute la plateforme, suspendre les tâches planifiées, purger, vider le cache.

**Correction (règle retenue).** `IPlatformOperationGuard` : une opération de portée plateforme exige la permission à
la portée PLATEFORME (exploitant), **sauf** quand l'installation n'héberge qu'un seul client (toutes les entreprises
actives appartiennent au groupe de l'entreprise active) — sur site, la direction reste l'exploitant de sa propre
installation et ne perd rien.
- Gardés : fenêtres de maintenance (planification, transitions), tâches planifiées (exécution manuelle, pause,
  reprise), bascules globales ou d'une autre entreprise, politiques de rétention et purges au-delà des notifications
  de l'entreprise active (aperçu compris), journal des déploiements et retours arrière, vidage du cache. Le
  planificateur n'est pas concerné.
- Corrections de données : toujours bornées à l'entreprise active — liste, demande (l'enregistrement doit lui
  appartenir ; un utilisateur doit en être membre ou membre de son groupe), décision. Sinon « introuvable », sans
  révéler l'existence de l'enregistrement.
- Bascules, politiques et historiques de purge : listes réduites à l'entreprise active et aux éléments globaux.

**Tests.** `OperationsConsoleIsolationTests` : avec un second client hébergé, un directeur est refusé
(`operations.platform_only`) sur la maintenance, les tâches, la purge globale, une bascule globale et un déploiement ;
il garde sa bascule d'entreprise ; il ne voit pas, ne demande pas et ne décide pas une correction de l'autre client.
Le test a aussi révélé une première version mal placée de la garde (sur la validation de mise en service, propre à
l'entreprise), corrigée avant validation. Non-régression : exploitation et maintenance, préparation à la mise en
service, planificateur, mise en production.

**Échecs connus, hors code.** 4 tests de mise en production échouent tant que V100 n'est pas appliquée à la base modèle
`nexora` (« migration en attente ») — voir « À faire hors code » de l'étape 1.

## Étape 5 — Clôture d'exercice et à-nouveaux (A-020 ÉLEVÉ, A-021 CRITIQUE)

**Modèle comptable constaté (et conservé).** NEXORA tient un **grand livre continu** : la balance, le bilan et le report
du grand livre cumulent toutes les écritures depuis l'origine ; la clôture d'un exercice solde ses produits et charges
vers le compte « résultat de l'exercice » (écriture du journal CL). Les onze requêtes qui agrègent le grand livre
reposent sur ce modèle ; il n'est pas changé.

**Défaut A-020.** Le résultat à solder sommait tous les produits et charges depuis l'origine en excluant **toutes** les
écritures de clôture. Dès le 2e exercice, les produits et charges du 1er, déjà soldés, l'étaient une seconde fois
(vérifié : exercice 2017 annoncé à 14 000 au lieu de 4 000) ; les comptes de gestion devenaient faux après clôture.
**Correction :** seule la clôture de l'exercice en cours est exclue ; celles des exercices antérieurs sont comptées.

**Défaut A-021.** La génération des à-nouveaux reprenait tous les soldes de bilan au premier jour du nouvel exercice ;
comme les états cumulent déjà depuis l'origine, caisse, banque, stock, clients, fournisseurs et capitaux étaient tous
**doublés**, sans alerte (l'écriture restant équilibrée). **Correction (dans le modèle continu) :** les à-nouveaux
portent seulement ce qui change d'un exercice à l'autre : le report du résultat clôturé (D résultat de l'exercice /
C report à nouveau pour un bénéfice, l'inverse pour une perte). Sans clôture préalable : « aucun résultat à reporter ».
Aucune écriture d'à-nouveaux ni de clôture n'existait dans `nexora` ni `nexora_recette` : pas de reprise de données.

**Tests.** `GeneralAccountingTests` : deux exercices clôturés successivement → le second ne solde que son résultat,
produits à zéro, bilan équilibré ; à-nouveaux → deux lignes, bilan et caisse identiques avant et après. Les deux tests
échouent sur l'ancien code (vérifié). Non-régression : comptabilité, moteur, confiance des indicateurs, consolidation,
tableaux de bord, BI, trésorerie (66 tests).

**À valider par le propriétaire.** Si l'entreprise préfère des exercices **bornés** (états recalculés depuis le début
de l'exercice, soldes de bilan repris en à-nouveaux comme dans beaucoup de logiciels français), c'est un changement de
modèle des onze requêtes, à décider explicitement ; la correction présente reste exacte dans le modèle actuel.

## Étape 6 — Coupon à montant fixe appliqué une fois par panier (A-012)

**Défaut.** Le coupon était appliqué dans la boucle des lignes : un coupon « montant » de 50 HTG remisait 50 sur chaque
ligne sans promotion (constaté : 100 sur deux lignes, 150 sur trois), alors que sa consommation ne comptait qu'un usage.
La remise coupon échappe au plafond de remise du caissier : perte de marge directe.

**Correction.** Les lignes sont d'abord toutes tarifées (prix, promotion, coupon en pourcentage) ; le coupon à montant
fixe est ensuite appliqué **une fois**, plafonné au total des lignes éligibles et réparti à leur prorata (l'arrondi va
à la dernière). Règle de cumul inchangée : le coupon fixe ne s'applique pas à une ligne déjà en promotion. La remise
d'une ligne est plafonnée à son montant brut. La caisse et l'aperçu du panier utilisent ce même calcul.

**Tests.** `CatalogPromotionTests.Fixed_amount_coupon_applies_once_per_cart_spread_over_the_lines` : coupon 50 sur
100 / 100 / 50 → 20 / 20 / 10 (total 50) ; panier de 25 → remise 25. Non-régression : promotions, prix, caisse,
paiements, corrections de recette (44 tests).

## Étape 7 — Listes de prix appliquées aux ventes (A-013)

**Défaut.** La tarification des ventes (caisse, aperçu du panier, commandes, devis) filtrait `price_list_id IS NULL` :
une liste tarifaire (membres, grossistes…) créée dans le catalogue n'était jamais appliquée à une vente réelle. Le
simulateur, lui, appliquait la liste choisie : il pouvait annoncer un prix que la caisse ne facturait pas.

**Correction.** `PriceListResolver` : la liste **active** rattachée au groupe du client, dans la devise de la vente (ou
sans devise), la plus haute priorité gagnant. Ses prix priment, les prix de base restent le repli (paliers compris).
Même règle pour la caisse, l'aperçu, les commandes, les devis et le simulateur (qui garde aussi le choix explicite d'une
liste). Une liste non rattachée à un groupe (cas de la liste « PRO » de démonstration) ne s'applique toujours pas
d'elle-même.

**Tests.** `CatalogPromotionTests.Price_list_of_the_customer_group_applies_at_the_pos_and_in_the_simulator` : client
d'un groupe doté d'une liste à 80 → 80 à la caisse et au simulateur ; sans client → 100. Non-régression : promotions,
prix, caisse, commandes, documents commerciaux, référentiel (57 tests).

## Étape 8 — Inventaire tenu magasin ouvert, et par lot (A-007)

**Défaut.** L'ouverture d'un inventaire fige la quantité système ; la validation postait `compté − quantité figée`.
Toute vente, réception ou transfert survenus pendant le comptage était donc compté deux fois : une vente de 2 articles
pendant l'inventaire devenait aussi un « manquant » de 2 (constaté lors de l'audit : figé 411, courant 409). Les lignes
d'un article suivi par lot étaient créées sans lot : l'ajustement modifiait le stock de l'article sans toucher ses lots.

**Correction.**
- Écart = compté − **quantité attendue au moment du comptage de la ligne** : quantité figée + mouvements enregistrés
  entre l'ouverture et la saisie de la ligne (horodatage `updated_at` de la ligne, tenu par la base ; une ligne saisie
  à la validation compare au stock courant). Aucune nouvelle colonne. La quantité système de la ligne prend cette
  valeur à la validation : l'écart affiché est exactement l'ajustement passé.
- Article suivi par lot : une ligne par lot en stock à l'ouverture ; quantités attendues et ajustements par lot ; l'écran
  d'inventaire affiche le lot et le renvoie à l'enregistrement.

**Tests.** `InventoryCountDuringSalesTests` : vente de 2 pendant le comptage puis 18 comptés → aucun ajustement, stock
18 (l'ancien calcul donnait 16) ; 19 comptés puis 3 vendus → stock 16 ; article à deux lots → deux lignes, l'écart ne
touche que le lot compté, stock de l'article = somme des lots. Non-régression : opérations de stock, cycle de vie, lots,
catalogue (31 tests) ; frontend : typecheck, lint, tests du stock.

## Étape 9 — Saisie des taux de change (A-017)

**Défaut.** La table `exchange_rate` était lue partout (comptes et ventes en devise, consolidation, contrôle de crédit
multidevise), mais aucune fonction ni aucun écran ne permettait d'y écrire : le multidevise ne fonctionnait qu'avec
les deux taux du jeu de démonstration. Sur une installation neuve, le contrôle de crédit répondait « saisissez le taux
du jour » sans offrir de moyen de le faire.

**Correction.**
- `GET/POST /api/v1/exchange-rates` (lecture `finance.view`, saisie `finance.update`) et onglet « Taux de change » du
  cockpit Finance. Convention : 1 devise de base = taux × devise cotée ; le taux inverse reste dérivé.
- Un taux n'est jamais modifié ni supprimé (les écritures passées gardent le leur) : on en saisit un nouveau, daté.
  Date du jour : en vigueur immédiatement ; autre date : midi UTC, qui reste ce jour-là dans tous les fuseaux.
- La table est commune à toute l'installation : la saisie passe par la garde de l'étape 4 (direction sur une
  installation d'un seul client, exploitant sur une plateforme partagée). Aucune migration.

**Tests.** `ExchangeRateManagementTests` : taux saisi utilisé par la conversion (et son inverse), doublon refusé,
devises identiques et taux nul refusés ; avec un autre client hébergé, un directeur d'entreprise est refusé
(`operations.platform_only`).

**Fixture de test.** Plusieurs tests laissent des entreprises indépendantes dans la base de test partagée : elle héberge
donc plusieurs « clients », et la garde de l'étape 4 refusait selon l'ordre d'exécution. La fixture donne à
l'utilisateur de test « direction » un rôle d'exploitant de portée plateforme limité à `operations.*` et
`finance.update` ; les tests de refus utilisent un directeur d'entreprise ordinaire (`NewCompanyDirectorAsync`).
Non-régression : taux, exploitation, consolidation, continuité, intelligence, identité, administration, catalogue des
permissions, couverture RLS (71 tests) ; frontend : typecheck, lint.

## Étape 10 — Portée système effective dans une transaction (A-009)

**Défaut.** Le contexte RLS (entreprises, groupes, utilisateur, levée de RLS) n'était posé qu'à l'ouverture de la
connexion. Dans une transaction, la connexion reste ouverte : une portée système ouverte à l'intérieur était sans
effet, et une transaction ouverte dans une portée système en gardait la levée de RLS après sa fin. Exemple constaté :
la réinitialisation de la MFA d'un utilisateur par un administrateur ne supprimait pas ses codes de secours (0 ligne
supprimée, sans erreur) — ils restaient utilisables.

**Correction.** `ITenantContext.SystemScopeChanged` est levé quand la portée système commence ou se termine (passage
0↔1). L'intercepteur de connexion marque alors le contexte de la connexion ouverte comme périmé ;
`RlsCommandInterceptor` le réapplique juste **avant la commande suivante**. Une première version réappliquait
immédiatement : elle entrait en collision avec une requête encore en cours quand la portée se termine avant la fin de
la requête (`using (BeginSystemScope()) return …Async()` sans `await`) — détecté par les tests MFA, corrigé.

**Tests.** `RlsSystemScopeTests`, avec le rôle applicatif réel `nexora_app` : réinitialisation MFA par la direction →
codes de secours supprimés ; dans une transaction, un client d'une autre entreprise est invisible, visible dans la
portée système, de nouveau invisible après. Les deux échouent sur l'ancien code (vérifié : 2 codes restants ; 0 ligne
visible dans la portée). Suite d'intégration complète : 511/515 (les 4 échecs sont ceux de V100 en attente).

**Reste, décision d'architecture.** La levée de RLS reste une variable de session que le rôle applicatif peut poser
(protection contre l'oubli d'un filtre, pas contre une injection — aucune n'a été trouvée) ; `app_user`, `user_role`,
`access_scope` restent sans RLS, volontairement (résolution de l'identité à la connexion). Une séparation par rôle
PostgreSQL dédié aux tâches système demanderait migration et revue de chaque portée système.

## Étape 11 — Comptes, sessions et règlement de vente (A-010, A-032, A-014)

**A-010.** La garde « on n'accorde que ce qu'on détient » ne protégeait que l'attribution. Désormais, suspendre ou
verrouiller un compte, ou lui retirer une affectation, est refusé (`identity.escalation`) si ce compte détient — dans
une entreprise, un groupe ou la plateforme partagés avec l'appelant — une permission que l'appelant n'a pas. Le retrait
d'une affectation invalide aussi le cache de session de la cible (droits relus dès la requête suivante).

**A-032.** Révoquer une session d'un autre utilisateur ne touche plus rien : ses jetons de rafraîchissement étaient
auparavant révoqués quand même (déconnexion forcée d'autrui par l'identifiant de session).

**A-014.** `POST /api/v1/sales/{id}/payments` passe par le service des comptes clients : permission
`credit.receive_payment` (au lieu de `pos.sell`), session de caisse ouverte, devise du compte, mouvement de caisse
pour des espèces. Le corps accepte en plus `paymentMethodId` et `cashSessionId`. Point d'accès non utilisé par
l'interface.

**Tests.** `AccountAuthorityTests` : un administrateur des utilisateurs moins habilité ne peut ni suspendre la
direction ni lui retirer son rôle ; la direction peut suspendre cet administrateur ; révoquer la session d'autrui →
« introuvable », jetons intacts. Non-régression : identité, audit de sécurité, gouvernance, consolidation, comptes
clients, administration (63 tests).

## Étape 12 — Tests d'API exécutables sur une base jetable (fin de T-002)

**Défaut.** Les 24 classes de tests d'API codaient en dur la base `nexora` : même après l'étape 0, la suite écrivait
toujours dans la base de développement, qui sert aussi de modèle aux tests d'intégration.

**Correction.** `ApiTestDatabase` : `NEXORA_API_TEST_DB` désigne la base visée (défaut `nexora`, comportement inchangé).
Les fabriques sans surcharge (sonde de santé, assistant d'installation, garde des secrets) n'écrivent rien et gardent
leur configuration — une première version qui les redirigeait aussi par variable d'environnement masquait la
configuration écrite par l'assistant d'installation (détecté par `SetupWizardTests`, retiré).

**Vérification.** Suite d'API lancée sur un clone jetable de `nexora` (mis à jour, puis supprimé) : 11 sessions créées
dans le clone, **aucune** dans `nexora`. Résultat : 194/195 ; le seul échec était `SetupWizardTests`, dû à la première
version décrite ci-dessus, et passe une fois celle-ci retirée (relancé seul, avec la sonde de santé). Les échecs relevés
pendant l'audit — sessions, parcours complet, exploitation — ne se reproduisent plus.

**Procédure.** `CREATE DATABASE nexora_api_it TEMPLATE nexora;` → `Nexora.Api.dll migrate` sur ce clone →
`NEXORA_API_TEST_DB=nexora_api_it dotnet test tests/Nexora.ApiTests` → `DROP DATABASE nexora_api_it;`.

## Étape 13 — Caisse et trésorerie reliées aux livres, rapports, moteur (A-019, A-023, A-022, A-004, A-008)

**A-019 — apports, sorties, prélèvements de caisse.** Ils n'écrivaient qu'un mouvement de session : ni écriture, ni
trésorerie. La commande exige désormais le compte de contrepartie (coffre, banque, autre caisse) ; un transfert de
trésorerie comptabilisé et le mouvement de session sont écrits dans la même transaction (refus de l'un = annulation
des deux), rejouable par identifiant d'opération. Point d'accès non utilisé par l'interface.

**A-023 — solde d'ouverture.** `POST /api/v1/financial-accounts/{id}/opening-balance` et bouton « Saisir le solde
d'ouverture » dans la fiche d'un compte sans mouvement : transaction de trésorerie (type `opening`) et écriture
D trésorerie / C report à nouveau (l'inverse pour un découvert bancaire). Une seule fois, avant toute opération ; jamais
négatif sur une caisse ; idempotent. Les soldes d'ouverture déjà présents dans les jeux de démonstration et de recette
(colonne `opening_balance` sans écriture) ne sont pas repris automatiquement.

**A-022 — reste dû.** Balances âgées, prévision de trésorerie, cockpit et alertes recalculaient
`original − payé` : les avoirs fournisseurs étaient ignorés. Ils lisent la colonne générée `remaining_amount`
(identique côté clients, nette des avoirs côté fournisseurs). 14 requêtes.

**A-004 — moteur financier.** Transactions de trésorerie datées de l'opération (et non de l'instant du traitement :
une vente hors ligne rejouée tombait en trésorerie à une autre date que son écriture). Un doublon simultané d'une même
opération est rejoué au lieu de finir en erreur — l'unicité refusait la seconde écriture, signalée par l'unité de
travail comme « échec de génération » (vérifié par le test). Une affectation de paiement exige la devise de la créance
ou de la dette.

**A-008 — « cette semaine ».** Le lundi suivant était pris pour début le dimanche (période vide) ; une période
personnalisée saisie à l'envers est remise dans l'ordre.

**Tiers.** Le message de collision de numéro affichait le numéro demandé (vide quand il est généré) ; il affiche le numéro
réellement tenté, et seule la contrainte de numéro est traduite en « numéro existant ».

**Tests.** `CashAndLedgerConsistencyTests` (caisse dédiée au test) : prélèvement → trésorerie, écriture unique, rejeu,
clôture sans écart ; solde d'ouverture → trésorerie et écriture équilibrée, une seule fois, refus après mouvement et en
négatif sur une caisse ; balance âgée nette d'un avoir (800 au lieu de 1 000) ; paiement rejoué en parallèle → une
écriture, trésorerie datée de l'opération ; devise différente refusée. `FilterPeriodTests` (unitaires) : les sept jours
de la semaine et la période inversée. Suite d'intégration complète : 516/520 (les 4 échecs : V100 en attente).

**Observation ouverte.** Sur 8 passages complets récents, 2 ont vu échouer toutes les créations de clients de
`CustomerAccountTests` (« numéro client existe déjà »), jamais reproduit isolément ni dans les 6 autres passages.
Le verrou de numérotation, la RLS de la table et l'unicité du journal d'audit ont été vérifiés ; la cause n'est pas
établie. Le message instrumenté (numéro tenté, contrainte exacte) permettra de la caractériser si elle se reproduit.

## Étape 14 — Politiques d'écriture et moindre privilège en base (A-029, A-030)

**Défauts.** La politique de `role` laissait aussi écrire les lignes globales (rôles système partagés par toutes les
entreprises) ; `role_permission` n'avait pas de RLS ; `audit_log` acceptait toute insertion (`WITH CHECK true`). Le rôle
applicatif pouvait écrire dans le registre des migrations et modifier ou supprimer le journal d'audit (bloqué par un
déclencheur seulement).

**Correction : migration V101.** `role` : lecture des rôles système et des rôles de ses entreprises, écriture de ces
derniers seulement ; `role_permission` : RLS forcée, mêmes règles à travers le rôle ; `audit_log` : insertion bornée aux
entreprises du contexte (ou sans entreprise) ; `REVOKE` des écritures sur `schema_migration` et des modifications et
suppressions sur `audit_log` pour `nexora_app` (les rôles créés par l'assistant d'installation en héritent). La portée
système garde tous les droits.

**Outil de test.** `NEXORA_IT_TEMPLATE` : la fixture d'intégration peut cloner une autre base modèle que `nexora` — un
clone jetable auquel on a appliqué les migrations en attente — pour les tester sans toucher à la base de développement.

**Tests.** `RlsSystemScopeTests.The_database_refuses_writes_on_shared_roles_foreign_audit_and_the_migration_registry`,
avec le rôle applicatif réel. Suite d'intégration complète sur un modèle jetable migré (V100 + V101) : **522/522**, y
compris les tests de mise en production, qui n'échouaient que parce que la base modèle `nexora` n'est pas migrée.

## Étape 15 — Extourne, crédit hors ligne, codes de carton et de balance (A-006, A-016, A-015)

**A-006 — extourne depuis l'écran Comptabilité.** Contrepasser l'écriture d'une vente, d'un paiement ou d'une facture
n'inversait que le grand livre : créance, trésorerie et stock restaient tels quels, et les sous-registres ne
correspondaient plus aux comptes. `IReversalService.ReverseUserEntryAsync` (point d'accès de l'écran) n'accepte plus que
les écritures d'origine `manual` ou `adjustment` ; les autres répondent `accounting.reverse_business_entry` (« annulez
l'opération elle-même »). L'écran masque le bouton pour ces écritures et l'explique. Les annulations métier (retour,
annulation de facture…) gardent l'extourne interne.

**A-016 — crédit hors ligne.** À la synchronisation, le crédit d'une vente hors ligne était estimé sur le seul prix
forcé : 0 dans le cas courant (la caisse ne force aucun prix), si bien que l'interdiction du crédit hors ligne et son
plafond ne s'appliquaient jamais. L'estimation passe par la tarification du serveur, la même que l'encaissement (listes
de prix, promotions, coupon, taxes) ; un article inconnu garde l'ancienne estimation, la vente étant de toute façon
refusée à l'encaissement.

**A-015 — lecture caisse.** Un code-barres de carton (`pack_size` 12) ajoutait 1 unité ; les étiquettes de balance
n'étaient pas décodées. La lecture renvoie la quantité à ajouter (`PackSize`), en ligne comme hors ligne (le profil hors
ligne transporte les codes de carton et les préfixes balance). Étiquettes de balance : convention GS1 des codes à usage
interne — EAN-13 commençant par 2, les 7 premiers chiffres désignent l'article (code-barres de l'article marqué
`is_weight_embedded`), les 5 suivants le poids en grammes, puis la clé de contrôle (vérifiée). Le même décodeur existe
côté serveur (`ScaleLabel`) et côté caisse (`scaleLabel.ts`). Les étiquettes à **prix** intégré ne sont pas traitées
(aucun paramètre ne dit quel format la balance imprime).

**Tests.** `BusinessGuardrailsTests` : vente hors ligne à crédit sans prix forcé → `CONFLICT` (l'ancien code la
synchronisait : vérifié en remettant l'ancien `SyncServices`) ; extourne d'une écriture de vente refusée ; carton de 12 →
12 ; étiquette de 1 250 g → 1,25 et le code lu renvoyé. `ScaleLabelTests` (unitaires) et `scaleLabel.test.ts`,
`offlineStore.test.ts` (clé fausse, poids nul, code ordinaire refusés).

**Régression trouvée par la suite complète, corrigée.** La recherche d'articles de la caisse partage le type
`PosProductDto` mais ne renvoyait pas la nouvelle colonne `PackSize` : EF refusait la requête (« The required column
'PackSize' was not present »), si bien que la recherche en caisse échouait. Détectée par
`GuideFonctionnelTests.Pos_search_returns_a_short_relevant_list_of_sellable_products_only`, absent des tests ciblés de
l'étape ; la recherche renvoie désormais `PackSize = 1` (un article choisi dans la liste s'ajoute à l'unité).

## Étape 16 — Connexion, MFA, métriques, CI, documentation (A-011, A-024, A-028, A-027, A-025)

**A-011 — authentification.** (1) Pour un e-mail inconnu ou un compte inactif, la connexion répondait sans vérifier de
mot de passe : le temps de réponse révélait si le compte existe. Une empreinte factice est désormais vérifiée, au même
coût. (2) La réinitialisation de la MFA d'un autre utilisateur exigeait la permission « quelque part » et une entreprise
commune : un directeur d'une entreprise, simple caissier d'une autre, pouvait réinitialiser la MFA des caissiers de la
seconde. La permission doit être détenue dans une entreprise (ou un groupe) partagée avec la cible, ou au niveau
plateforme. (3) Le rejeu d'un code TOTP dans sa fenêtre de validité demande une migration : traité à l'étape 17.

**A-024 — métriques.** `/api/v1/metrics` était anonyme et seul le chemin exact était bloqué par Caddy :
`/api/v1.0/metrics` ou `/api/v1/metrics/` passaient. L'API ne sert plus la page qu'à une adresse interne (après
résolution des en-têtes du proxy) ou sur présentation du jeton `Metrics:Token` (`Authorization: Bearer …`, 16 caractères
au moins) ; sinon 404. Caddy bloque toutes les variantes (`path_regexp`, insensible à la casse, toute version, barre
finale). Expression vérifiée sur une liste de chemins ; **la configuration Caddy n'a pas été chargée** (Docker arrêté).

**A-028 — CI.** `dotnet list package --vulnerable` rend 0 même quand il liste des paquets vulnérables : l'étape échoue
désormais si la sortie en signale. **Non exécuté** (pas d'exécution GitHub Actions en local).

**A-027.** `ModulePlaceholder.tsx`, jamais importé, supprimé.

**A-025.** Le rapport `NEXORA-ONE-FINAL-READINESS-REPORT.md` porte en tête un avertissement : plusieurs « Prêt pour la
production » ont été infirmés par l'audit, deux réserves sont obsolètes, et le suivi à jour est le présent document.

**Tests.** `LoginHandlerTests.Unknown_user_costs_a_password_check_like_a_known_one` ;
`MfaAuthenticationTests.Reset_permission_held_in_another_company_does_not_reach_a_shared_company` ;
`OperationsEndpointsTests` : adresse publique → 404 pour trois écritures du chemin, 200 avec le jeton. Les trois échouent
sur l'ancien code (vérifié en remettant chaque fichier d'origine).

## Étape 17 — Anti-rejeu des codes TOTP (fin de A-011)

**Défaut.** Un code TOTP restait accepté pendant toute sa fenêtre (pas courant ± 1, jusqu'à 90 s) : un code observé
— par-dessus l'épaule, sur un enregistrement d'écran, ou relayé par une page d'hameçonnage — servait une seconde fois, y
compris le code saisi pour confirmer l'enrôlement.

**Correction : migration V102** (`app_user.mfa_last_step`, dernier pas de temps accepté). `Totp.MatchStep` donne le pas
auquel le code correspond ; la vérification (connexion, confirmation renforcée, code de l'utilisateur courant) n'accepte
le code que si une mise à jour conditionnelle `mfa_last_step < pas` réussit — deux usages simultanés du même code ne
passent donc pas tous les deux. La confirmation d'enrôlement enregistre son pas ; la réinitialisation de la MFA l'efface.
Les codes de récupération, déjà à usage unique, sont inchangés.

**Effet visible.** Un utilisateur qui se reconnecte dans les 30 secondes doit attendre le code suivant de son
application.

**Tests.** `MfaAuthenticationTests.A_totp_code_already_accepted_is_refused_on_a_new_challenge` : le code de l'enrôlement
est refusé à la connexion (l'ancien code l'acceptait : vérifié), le code suivant passe une fois, pas deux. Les tests qui
réutilisaient le code de l'enrôlement utilisent le pas suivant.

## Étape 18 — Messages de la base, profil de permissions, dépendance inutile (priorité 8, P-001)

**Messages PostgreSQL bruts.** L'unité de travail transmettait au client le texte de PostgreSQL : une violation
d'unicité ou de clé étrangère affichait noms de tables, de contraintes et valeurs (« duplicate key value violates unique
constraint … »). `DatabaseErrors.ClientMessage` garde intacts les refus métier levés par les déclencheurs
(`RAISE EXCEPTION`, y compris les 63 levés sous le code `check_violation`, reconnus à leur contexte « at RAISE » et à
l'absence de contrainte) et remplace les autres par un message neutre selon leur nature (doublon, référence, valeur
obligatoire, règle, conflit concurrent, droit). L'exception d'origine reste attachée : les traductions existantes par
nom de contrainte (paie, tiers, RH) fonctionnent comme avant. Appliqué à l'unité de travail et aux replis des services
de paie et RH. L'assistant d'installation garde le message complet (destiné à l'administrateur qui installe).

**P-001 — profil de permissions.** Chaque attribut `[RequirePermission]` (classe et action) rechargeait le profil (3 à 4
requêtes), puis le mode maintenance une fois de plus. Le pipeline HTTP le charge désormais une fois par requête
(`RequestPermissionProfile`, rangé dans `HttpContext.Items`). Les services gardent leur propre chargement, à jour si
un rôle change pendant la requête.

**FluentValidation.** Référencé par l'API et la couche application, jamais utilisé : retiré.

**Tests.** `DatabaseErrorsTests` sur de vraies erreurs PostgreSQL : doublon → message neutre sans nom de table ni de
contrainte ; suppression d'une écriture validée → message du déclencheur conservé. `RequestPermissionProfileTests` :
un chargement par requête et par utilisateur. Paie, RH, comptabilité, intégrité du modèle : verts.

## Étape 19 — Écrans qui promettaient ce qu'ils ne font pas (A-031 bascules, remboursement réel)

**Bascules de fonctionnalité.** `IFeatureFlagService.IsEnabledAsync` n'est appelé par aucune fonctionnalité : créer
ou désactiver une bascule ne change rien, alors que l'aide de l'écran parlait de « coupe-circuit ». Décider quelles
fonctionnalités relier est un choix produit ; en attendant, l'écran Exploitation le dit en clair (« registre
préparatoire… ne change rien au fonctionnement »).

**Remboursement d'un paiement en ligne réel.** Aucun fournisseur réel (MonCash, carte) ne rembourse par API : le serveur
refusait toujours, mais l'écran proposait « Rembourser » et ne donnait la marche à suivre qu'après l'échec. Le bouton
est réservé aux paiements simulés ; un paiement réel confirmé affiche « Comment rembourser ? » (portail du fournisseur,
puis annulation du versement dans le compte du client).

**Test frontend instable.** `noNativeDialogs.test.ts` (lecture de tout `src/`) échouait par dépassement du délai de 5 s
quand la suite complète tourne en parallèle — « Test timed out in 5000ms », deux fois de suite ; 0,9 s seul. Délai porté
à 30 s ; la règle vérifiée est inchangée.

**Tests.** `paymentsApi.test.ts` : actions d'un paiement réel confirmé (marche à suivre, pas de bouton voué au refus).
Suite frontend : 279/279.

## Étape 20 — Reprise des soldes d'ouverture, devise des mouvements de trésorerie

**Reprise des soldes d'ouverture (A-023, données existantes).** Les comptes créés avant l'étape 13 portent leur solde
d'ouverture dans la colonne `opening_balance`, comptée en trésorerie mais sans écriture : le grand livre est en écart du
même montant. Commande `Nexora.Api.dll reprise-ouvertures` : sans option, elle n'écrit rien et liste les comptes ; avec
`--appliquer`, pour chaque compte, par le moteur financier et dans une seule transaction : transaction de trésorerie
« opening » datée juste avant le premier mouvement, écriture D trésorerie / C report à nouveau, puis colonne remise à
zéro. Le solde affiché ne change pas ; une relance ne refait rien (identifiant d'opération stable par compte).
Relevé du 2026-10-15 : 3 comptes dans `nexora` (Pharmacie), 6 dans `nexora_recette` (Pharmacie et Supermarché).

Essai sur une copie jetable de `nexora_recette` migrée en V102 : 6 comptes repris, soldes affichés identiques au
centime, écarts trésorerie / grand livre de 45 000, 60 000 et 865 000 HTG ramenés à zéro, relance sans effet.

**Devise des mouvements de trésorerie (défaut trouvé pendant l'essai).** Le moteur ajoutait le montant d'un mouvement au
solde du compte sans vérifier sa devise : des frais, une dépense, une paie ou un transfert en HTG sur un compte en USD
changeaient le solde en dollars du même chiffre, alors que l'écriture était en gourdes. La caisse refusait déjà ce cas
pour une vente, mais pas les autres chemins. Le moteur refuse désormais tout mouvement dont la devise n'est pas celle
du compte (16 chemins). Dans la recette, 77 ventes par carte en gourdes de la Pharmacie, enregistrées avant le contrôle
de la caisse, ont ainsi gonflé le compte Unibank en USD de 71 690,15 : ces données ne sont pas corrigées
automatiquement (il n'existe pas de compte bancaire en gourdes pour la Pharmacie vers lequel les reporter). L'outil de
génération de la recette envoie désormais les cartes en gourdes sur un compte en gourdes ; régénérer la recette, ou
décider d'une correction manuelle.

**Tests.** `OpeningBalanceRecoveryTests` : simulation sans écriture ; reprise → solde affiché inchangé, colonne à zéro,
transaction « opening » avant le premier mouvement, écriture équilibrée ; relance vide.
`BusinessGuardrailsTests.A_sale_in_gourdes_cannot_be_tendered_to_a_dollar_account` : frais en HTG sur le compte USD
refusés, solde inchangé (l'ancien moteur les acceptait : vérifié). Suites : unitaires 103/103, intégration 532/532, API
200/200, frontend 279/279.

**Procédure pour les bases partagées** (à lancer à la main ; sauvegardes déjà faites) :

```powershell
cd backend
foreach ($d in "nexora", "nexora_recette") {
  $env:Database__ConnectionString = "Host=localhost;Port=5432;Database=$d;Username=postgres;Password=<mot de passe>"
  dotnet src/Nexora.Api/bin/Debug/net10.0/Nexora.Api.dll migrate
  dotnet src/Nexora.Api/bin/Debug/net10.0/Nexora.Api.dll reprise-ouvertures             # simulation
  dotnet src/Nexora.Api/bin/Debug/net10.0/Nexora.Api.dll reprise-ouvertures --appliquer
}
```

Retour arrière : `pg_restore --clean -d <base> C:\Users\Rener\nexora-sauvegardes\2026-10-15-avant-V100\<base>.dump`.

## Décisions d'exploitation (2026-10-15)

- **Un client par installation (A-031).** Chaque client — un groupe et ses entreprises — a sa propre installation et sa
  propre base. Les éléments communs à une installation (taux de change, maintenance, tâches planifiées, purge, portée
  système de la base) ne sont donc jamais partagés entre clients. Plusieurs clients par installation demanderaient un
  chantier dédié : taux par client, rôle d'opérateur de plateforme distinct, revue des tables globales.
- **Taux de change communs à l'installation (A-017)** : avec un client par installation, ce sont les taux du client,
  appliqués à toutes ses entreprises. À revoir seulement avec le point précédent.
- **Bascules de fonctionnalité** : l'onglet « Bascules » de la console d'exploitation est retiré pour la version 1 (aucune
  fonctionnalité ne les lit). Le registre et l'API restent en place ; candidats si des coupe-circuits deviennent utiles :
  paiements en ligne réels, envois SMS / e-mail, ventes hors ligne.
- **À-nouveaux (A-021)** : modèle actuel conservé (grand livre continu, report du seul résultat), à faire confirmer par
  le comptable ; s'il faut un journal d'à-nouveaux détaillé, le produire comme état imprimable, sans écritures.
