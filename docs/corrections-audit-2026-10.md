# Corrections issues de l'audit technique intégral (2026-10)

> Suivi, étape par étape, des corrections du plan `audit-nexora-one/10-plan-de-correction.md`.
> Pour l'équipe technique et la personne qui validera la recette. Identifiants : ceux de
> `audit-nexora-one/09-anomalies-et-priorites.md`.

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
