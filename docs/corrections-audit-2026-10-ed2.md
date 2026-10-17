# Corrections issues de l'audit technique intégral — édition 2 (2026-10-17)

> Suivi, lot par lot, du plan `audit-nexora-one/10-plan-de-correction.md` (édition 2). Pour l'équipe technique et la
> personne qui validera la recette. Identifiants : ceux de `audit-nexora-one/09-anomalies-et-priorites.md`. Les
> corrections de l'édition 1 sont dans `docs/corrections-audit-2026-10.md`.
>
> Méthode de chaque lot : branche dédiée ; test qui reproduit le défaut d'abord (rouge, vérifié), puis correction
> (vert) ; suites complètes ; migrations appliquées à `nexora`, `nexora_recette` et `nexora_api_it` après sauvegarde ;
> fusion dans `develop`.

## Décisions retenues (2026-10-17)

1. **A-042** : écran « Ouvrir l'exercice suivant », ouverture planifiée dans le dernier mois, et filet de sécurité au
   moment d'une opération du jour.
2. **A-043** : les états agrègent les écritures `posted` et `reversed` par une seule règle partagée ; l'extourne
   elle-même n'est pas modifiée.
3. **Données déjà fausses (A-052)** : écritures correctives datées et traçables, par une commande rejouable sans
   doublon (modèle : `reprise-ouvertures`), jamais de réécriture de l'historique.
4. **A-049 / A-045** : route de facturation plateforme désactivée tant qu'elle n'est pas configurée ; routes
   `/finance/*` retirées (aucun écran ne les appelle).

## Lot 0 — Socle : dépendance vulnérable, délai des tests, taux non entier (A-036, T-003)

**A-036.** MimeKit tirait `System.Security.Cryptography.Pkcs` 10.0.0, vulnérable (GHSA-846f-f764-76mx et
GHSA-2fmv-rv2c-59x7, déni de service ; corrigés en 10.0.13). Version épinglée dans `Directory.Packages.props`
(épinglage transitif déjà actif). `dotnet list package --vulnerable --include-transitive` : aucun paquet vulnérable.

**T-003.** Le clonage de la base modèle par la fixture d'intégration dépassait les 30 s par défaut de Npgsql sur un
poste chargé : toute la suite échouait en 1 ms. Délai de 600 s sur la seule connexion d'administration ; les tests
gardent leur délai d'origine. Suite lancée sans variable d'environnement : 533/533.

**Jeu de test multidevise.** Les taux de démonstration sont entiers (132, 135) : un montant en centimes multiplié par
un taux entier ne produit jamais de demi-centime, ce qui cachait A-041. La fixture pose un taux USD/HTG de 132,5 le
seul 2026-03-10 (`FinanceEngineFixture.FractionalUsdRate` / `FractionalUsdRateDay`) et rétablit le taux d'origine le
lendemain ; un test vérifie que les jours voisins ne changent pas.

**Résultats.** Unitaires 103/103, intégration 533/533, API 200/200.

## Lot 1 — Exercice suivant (A-042, échéance 2027-01-01)

**Défaut.** Seul l'amorçage d'une entreprise (V072) créait un exercice, et aucune route ni aucun écran ne permettait
d'en créer un autre. Au 1er janvier 2027, toute écriture — donc toute vente, tout achat, toute paie — aurait été
refusée (« Aucune période comptable ne couvre le 2027-01-01 »).

**Correction.**

- `POST /api/v1/accounting-periods/next-year` (permission `accounting.close_period`) et bouton « Ouvrir l'exercice
  suivant » dans l'écran des périodes, mis en avant dans le dernier mois de l'exercice. Refus
  (`accounting.next_year_exists`) si un exercice qui n'a pas encore commencé existe déjà : on ne peut pas ouvrir 2028
  en décembre 2026.
- Job planifié `accounting.open_next_year` (V103) : ouvre l'exercice suivant quand le dernier se termine dans moins
  de 31 jours. Le planificateur n'est actif que dans le compose de production.
- Filet de sécurité dans `EfAccountingPeriods.ResolveOpenPeriodAsync` : une opération du jour postérieure au dernier
  exercice ouvre les exercices suivants (un an chacun, sans trou). Jamais pour une date future, ni avant le premier
  exercice, ni dans un trou entre deux exercices. Deux ventes simultanées du 1er janvier n'ouvrent qu'un exercice
  (contrainte d'exclusion des chevauchements + `ON CONFLICT DO NOTHING`).

**Tests** (`FiscalYearOpeningTests`, entreprise neuve et horloge simulée) : vente du 1er janvier 2027 acceptée et
rattachée à « Exercice 2027 » — **rouge sans le filet** (« Aucune période comptable ne couvre le 2027-01-01 »,
vérifié en le désactivant le temps du test) ; six ventes simultanées → un seul exercice ; opération datée dans le
futur → refusée, rien d'ouvert ; ouverture anticipée une seule fois ; planificateur inactif en novembre, actif en
décembre, sans doublon. Frontend : `openNextYear` et `nextYearState` (Vitest).

**Bases.** V103 appliquée à `nexora`, `nexora_recette` et `nexora_api_it` le 2026-10-17 (sauvegardes
`C:\Users\Rener\nexora-sauvegardes\2026-10-17-avant-V103`). Les installations existantes ouvriront 2027 d'elles-mêmes à
la première opération de l'année ; l'ouvrir dès décembre depuis l'écran reste recommandé.

**Résultats.** Unitaires 103/103, intégration 538/538, API 200/200, Vitest 281/281, typage, lint et build sans erreur.

## Lot 2 — Coût des ventes en devise (A-052) et écritures extournées dans les états (A-043)

**Défaut A-052.** Le coût d'une vente en dollars, calculé sur la valorisation du stock (donc déjà en gourdes), était
présenté au moteur en dollars puis converti au taux du jour : deux ventes de la Pharmacie ont passé 40 500 HTG de coût
pour 300 HTG de marchandises. Même défaut à la réintégration d'un retour (la ligne reprenait en outre le taux du
remboursement).

**Correction A-052.** La vente, le retour et `FinanceOperations` transmettent le coût en devise fonctionnelle ; la règle
de comptabilisation refuse désormais un coût dans une autre devise (erreur d'appelant au lieu d'un montant faux).

**Défaut A-043.** L'extourne passe l'origine en `reversed` et crée un miroir `posted` ; balance, grand livre, états,
clôture, à-nouveaux, consolidation et indicateurs ne comptaient que `posted` : une vente de 1 000 extournée laissait la
caisse à −1 000 et les produits à −1 000.

**Correction A-043.** Règle unique `LedgerStatuses.InLedger` (`posted` et `reversed`), appliquée en paramètre SQL à
18 requêtes d'agrégats dans 10 fichiers (dont les deux qui l'écrivaient déjà en dur). La détection de doublon et le fil
d'activité, qui ne sont pas des sommes, gardent `posted`. V104 rend une écriture extournée aussi intouchable qu'une
écriture validée (statut, dates, période, lignes) et ferme la transition `posted` → `cancelled`, qu'aucun code
n'utilisait et qui sortait une écriture du grand livre sans extourne.

**Données existantes : commande `correction-cout-devise`.** Simulation par défaut ; `--appliquer` passe, pour chaque
écriture de vente ou de retour en devise faussée (validée, non extournée), une écriture corrective OD (origine
`adjustment`, liée à l'origine, datée comme elle si sa période est ouverte) qui retire l'excédent des comptes de coût
des ventes et de stock. L'origine n'est pas modifiée ; une écriture corrigée n'est plus proposée.

**Tests.** `ForeignCurrencyCostOfGoodsTests` (vente de 3 unités à 40 HTG en USD puis retour de 2 ; commande de
correction sur une écriture reproduite à l'ancienne) et `ReversedEntriesInStatementsTests` (balance, grand livre,
résultat, bilan et aperçu de clôture après extourne ; écriture extournée non modifiable ; `posted` → `cancelled`
refusé). **Rouges sur l'ancien code et l'ancienne base** (vérifié sur un clone jetable, supprimé ensuite) : coût des
ventes de 16 200 HTG au lieu de 120, comptes non soldés après extourne, modifications acceptées.

**Bases.** V104 appliquée à `nexora`, `nexora_recette` et `nexora_api_it` (sauvegardes
`C:\Users\Rener\nexora-sauvegardes\2026-10-17-avant-V104`). Correction appliquée à `nexora_recette` : écritures
2026-000326 et 2026-000406 de la Pharmacie corrigées par 2026-000009 et 2026-000010 (−40 200 HTG chacune) ; second
passage : rien à corriger. `nexora` : rien à corriger. Compte de stock de la Pharmacie : 61 696,20 → 142 096,20 pour une
valorisation de 140 285,00 ; l'écart restant (1 811,20) est celui des écarts de prix factures / réceptions, constat
faible distinct. Aucune écriture déséquilibrée.

**Résultats.** Unitaires 103/103, intégration 543/543, API 200/200 (frontend inchangé).

## Lot 3 — Taxe choisie par le caissier, stock concurrent, vente rejouée dans une caisse fermée (A-040, A-038, A-003 résidu)

**A-040.** Une ligne de vente acceptait l'identifiant de n'importe quelle taxe : un caissier ramenait 143 à 130 en
choisissant la taxe à 0 %, sans remise déclarée. Désormais (`SqlSalePricingGuard`) la taxe envoyée doit être une taxe
active de l'entreprise ; une autre taxe que celle de l'article exige `pos.change_price`, comme un prix forcé. La
tarification ne retient plus une taxe d'une autre entreprise ni une taxe inactive.

**A-038.** Réception, clôture et annulation de transfert, et validation d'inventaire, contrôlaient l'état du document
avant leur transaction, sans verrou. Elles verrouillent maintenant le document (`FOR UPDATE`) et relisent son état dans
la transaction (`LockTransferAsync`, `LockCountStatusAsync`).

**A-003 (résidu).** Par la synchronisation, une vente hors ligne datée d'avant la clôture s'ajoutait en silence à une
caisse déjà comptée. Elle devient un conflit (type `period`) qu'un responsable (`sync.manage`) tranche : « le poste a
raison » l'enregistre dans sa session, sinon elle est annulée. Par ailleurs, réenregistrer un appareil révoqué ou
suspendu sous le même identifiant le remettait « actif » : la révocation ne tenait pas. Il reste révoqué
(`sync.device_revoked`).

**Tests** (rouges sur l'ancien code, vérifié en le rétablissant le temps du test, puis verts) :
`StockConcurrencyTests` — 8 réceptions simultanées d'un transfert d'une unité (ancien code : 2 réussites), annulation
contre réception (ancien code : les deux réussissaient), 4 validations simultanées d'un inventaire (ancien code : 4
réussites) ; `PosSalesTests` — taxe à 0 % refusée au caissier, taxe inconnue refusée, taxe de l'article acceptée,
dérogation possible avec `pos.change_price` ; `OfflineSyncTests` — vente rejouée dans une session fermée → conflit puis
acceptation ; appareil révoqué non réactivé.

**Reste (faible, non traité ici).** Le centre de synchronisation affiche les types de conflit et les résolutions sous
leur code (`period · sale`, `client_wins`) ; le motif et l'action recommandée sont lisibles.

**Résultats.** Unitaires 103/103, intégration 550/550, API 200/200 (frontend inchangé).

## Lot 4 — Arrondis de conversion (A-041) et indicateurs multidevises (A-046)

**A-041.** Chaque ligne d'une écriture en devise était convertie puis arrondie séparément : à 132,5, 10,01 + 1,01 USD
donnaient 1 326,32 + 133,82 = 1 460,14 HTG contre 1 460,15 pour les 11,02 USD encaissés, et l'écriture, juste en
devise, était refusée (par le moteur, ou par la base avec un message PostgreSQL brut pour une écriture manuelle).
Correction centrale (`ConversionRounding`, domaine) appliquée par le moteur à toutes les règles et par la saisie
manuelle (création et modification de brouillon) : pour chaque devise étrangère dont les lignes s'équilibrent
exactement en devise, l'écart de base — au plus un demi-centime par ligne — est porté sur la plus grosse ligne du côté
le plus faible. Un écart plus grand (A-052 : 40 200) ou un déséquilibre en devise ne sont jamais ajustés.

**A-046.** Les indicateurs additionnaient des montants de devises différentes : la trésorerie de la Pharmacie
(gourdes + 3 200 USD) affichait 63 725 « HTG » au lieu de 492 525. Le centre de pilotage, le tableau du responsable, la
vue groupe (convertie dans la devise du groupe, coût des ventes compris), le classement des articles, la prévision de
trésorerie et la position de trésorerie convertissent désormais chaque montant par `nexora.convert_amount` (fonction
existante, même règle que le moteur) : ventes, retours, dépenses et paiements au taux de leur date, soldes, créances et
dettes au taux du jour. Sans taux, trésorerie, créances et dettes sont marquées « estimé » ; la position de trésorerie
ne compte plus un solde en devise comme des gourdes faute de taux (elle le comptait au taux 1).

**Tests** (rouges sur l'ancien code, vérifié en le rétablissant le temps du test) : `PostingRuleSetTests` (vente en
USD à 132,5 équilibrée au centime ; un écart de 40 200 ou un déséquilibre en devise ne sont pas absorbés),
`ConversionRoundingTests` (vente 10,01 + 1,01 USD ; écriture manuelle 20,02 = 10,01 + 10,01 — ancien code :
« 2652.6500 <> 2652.6400 », exactement le refus constaté par l'audit), `KpiCurrencyTests` (trésorerie de la
Pharmacie : 492 525 attendus, 63 725 avant).

**Résultats.** Unitaires 105/105, intégration 553/553, API 200/200 (frontend inchangé).

## Lot 5 — Droits d'accès et sécurité (A-049, A-045, A-048, A-033, A-034, A-035, A-009 résidu)

**A-049 (webhook de facturation plateforme).** Le jeton par défaut `dev-platform-webhook-token`, publié dans le code,
était accepté en production, et un paiement soldait sa facture en totalité quels que soient le montant, le client et la
devise. Désormais : sans `PlatformBilling:WebhookToken` configuré, la route n'existe pas (404) ; un jeton fourni doit
être fort (32 caractères au moins, pas `dev-…`), sinon l'API refuse de démarrer hors développement ; comparaison en
temps constant. Un paiement n'est imputé qu'à une facture ouverte de son client, dans sa devise ; il s'ajoute au déjà
payé et la facture n'est soldée (et l'abonnement réactivé) qu'une fois son total atteint. Paiement non imputable :
revue humaine, alerte, aucun effet ; confirmation manuelle refusée (`platform.payment_unapplied`).

**A-045 (routes `/finance/*`).** Écritures financières brutes à montants libres, qu'aucun écran n'appelait : un
comptable seul y payait une dépense sans approbation. Le contrôleur est supprimé ; les opérations internes
(`IFinanceOperations`) restent utilisées par les services métier, qui portent les contrôles (4 yeux, session…).

**A-048 (centre d'intelligence).** `intelligence.view` donnait aux responsables stock et achats les créances
nominatives, la trésorerie et les prévisions. Règle `IntelligenceVisibility`, alignée sur `KpiCatalog` : catégories
financières (créances, trésorerie, budget, marge, dépenses) réservées à `reporting.view_financial`, commerciales (chiffre
d'affaires, clients) à `reporting.view_sales` ; appliquée aux recommandations (liste, détail, tableau de bord, actions),
aux anomalies et aux scores clients ; `/intelligence/forecasts` exige `reporting.view_financial` comme `/forecasts`.

**A-033 (gestion des utilisateurs).** Le périmètre de gestion comprenait toute entreprise d'appartenance : un
administrateur de A, simple caissier dans B, listait et affectait les comptes de B. Il se limite aux entreprises où
l'appelant détient `admin.manage_users`.

**A-034 (permission vérifiée sur l'entreprise active, données lues partout).** Journal d'audit et audit RH limités aux
entreprises où l'appelant détient `security.audit_view` (ou `payroll.audit`) ; clonage de configuration : la
permission `config.transfer` est exigée aussi sur l'entreprise cible.

**A-035 (accès d'urgence).** La révocation ignorait le refus du retrait de l'affectation et affichait « révoqué » ;
elle renvoie désormais l'erreur et ne change pas le statut.

**A-009 résidu (V105).** RLS activée et forcée sur `app_user`, `user_role` et `access_scope`, les dernières tables
d'entreprise sans RLS : un utilisateur se voit lui-même et voit les comptes rattachés (même par une affectation
révoquée, pour l'historique) à ses entreprises ou groupes ; la connexion et la recherche d'identité passent par la
portée système, inchangée. Le test de couverture RLS n'admet plus `access_scope` en exception.

**Tests** (rouges sur l'ancien code, vérifié en le rétablissant le temps du test, puis verts) : `AccessScopeTests`
(responsable stock : stock visible, créances masquées, direction inchangée ; administrateur de A sans droit sur B) ;
`PlatformBillingTests` (paiement d'un autre client → revue, paiement partiel → facture ouverte, solde → payée et
abonnement actif) ; `PlatformEndpointsTests` (route absente sans jeton, ancien jeton refusé) ; `CashierAccessTests`
(`/finance/*` → 404) ; `ProductionConfigurationTests` (jeton faible refusé au démarrage) ; `IdentityAccessTests`
(garde anti-élévation testée avec un administrateur des utilisateurs sans droits comptables). Trois tests du moteur
d'intelligence faisaient travailler l'utilisateur « direction » dans une entreprise où il n'avait aucun rôle : ils
utilisent désormais un directeur de l'entreprise créée.

**Résultats.** V105 essayée d'abord sur des copies jetables (`nexora` et `nexora_api_it`, supprimées ensuite) :
unitaires 106/106, intégration 556/556, API 204/204. Puis appliquée à `nexora`, `nexora_recette` et `nexora_api_it`
(sauvegardes `C:\Users\Rener\nexora-sauvegardes\2026-10-17-avant-V105`). Contrôle réel sur `nexora_recette`, API
connectée en `nexora_app` : connexion de la comptable, du responsable stock et de la direction ; le responsable stock
ne voit plus que les recommandations de stock et les prévisions lui sont refusées (403) ; comptable et direction gardent
la vue complète ; la direction liste ses 16 utilisateurs.

**Non traité ici.** A-035 n'a pas de test dédié (scénario de retrait refusé lourd à monter) ; correction vérifiée par
lecture. Le statut d'un compte (suspendu, verrouillé) reste global : un administrateur de A peut toujours suspendre un
utilisateur qu'il partage avec B, dans la limite de la garde anti-élévation.

## Lot 6 — Écarts de fonctionnement (A-039, A-044, A-047, A-022 résidu, A-050, A-037, A-051)

**A-039 (simulateur ≠ caisse).** Le simulateur appliquait le coupon en pourcentage après la promotion, la caisse sur
le montant de la ligne (102,96 annoncés, 100,10 encaissés pour 130 HTG à −20 % puis −10 %) ; il cumulait aussi un
coupon fixe avec une promotion, ce que la caisse refuse. Le simulateur suit désormais la caisse, qui fait foi.

**A-044 (réouverture d'un exercice clôturé).** Rouvrir ne rouvrait que la période : la clôture restait passée, les
écritures suivantes n'étaient jamais soldées et la reclôture était refusée ; le motif n'était écrit que dans le journal
applicatif. Désormais la réouverture annule l'écriture de clôture par son miroir (origine `closing`, donc écarté des
états de résultat comme la clôture elle-même ; la balance est nette), passe la clôture à `reopened` avec le motif en
base, et la reclôture solde l'exercice entier une seule fois.

**A-047 (budget contre réalisé).** Toutes les versions de l'exercice étaient additionnées (une révision doublait le
budget, un brouillon abandonné s'ajoutait) et le réalisé était pris en valeur absolue sur l'année civile. Le rapport
retient le budget en vigueur (actif le plus récemment approuvé, sinon le dernier clos) ; l'approbation d'une révision
clôt la version qu'elle remplace ; le réalisé est signé selon la nature du compte, sur les dates de l'exercice.

**A-022 résidu (balance âgée du pilotage).** Reste dû = `remaining_amount` (avoirs fournisseurs déduits), converti
dans la devise de l'entreprise (A-046).

**A-050 (stock d'ouverture).** L'import créait le stock sans écriture. Nouvel événement `InventoryOpeningRecorded`
(D Stock / C Report à nouveau, comme l'ouverture de trésorerie), passé dans la transaction de l'import, avec un
identifiant d'opération stable par produit et emplacement. Rien à reprendre sur les bases : `nexora` a 47 mouvements
d'ouverture tous à coût nul, `nexora_recette` aucun.

**A-037 (« retour arrière » de configuration).** Les instantanés sont des relevés résumés (préfixe et remplissage
concaténés, politiques réduites) : on ne peut pas les réappliquer fidèlement. La route `rollback`, qui ne changeait
qu'un statut, est retirée ; l'écran dit « Marquer comme référence » et explique que rien n'est appliqué.

**A-051 (continuité déclarative).** Une sauvegarde devenait « vérifiée » et un test de restauration « réussi » sur la
foi de métadonnées saisies ; une restauration de production approuvée passait « réussie » sans avoir eu lieu.
Désormais seule la restauration réelle de `database/scripts/restore.sh --verify` (clé d'API) rend une sauvegarde
vérifiée et enregistre un test réussi ; depuis l'écran, le contrôle reste déclaratif et le dit ; une restauration de
production approuvée passe « en cours ». Les quatre tests qui décrivaient l'ancien comportement ont été réécrits.

**Tests** (rouges sur l'ancien code, vérifié en le rétablissant le temps du test, puis verts) : `FunctionalGapsTests`
(réouverture puis reclôture : résultat 1 500 une fois ; budget 120 après révision, avant 1 219 ; balance âgée 50,
avant 90 ; stock d'ouverture 50 au grand livre, une seule fois), `CatalogPromotionTests` (simulateur = caisse sur
promotion + coupon ; coupon fixe non cumulé), `ContinuityRegisterTests` et `OperationsReadinessTests` (contrôle
déclaratif, preuve par l'outil de restauration).

**Correctif de concurrence du lot 1 (trouvé ici).** Un passage complet a fait échouer le test des six ventes
simultanées du 1er janvier : si une autre vente ouvrait l'exercice entre les deux lectures du filet, celui-ci voyait
2027 déjà présent et renonçait (« Aucune période comptable ne couvre le 2027-01-01 »). Il relit désormais l'exercice
dans ce cas.

**Résultats.** Unitaires 106/106, intégration 562/562 (trois passages complets consécutifs), API 204/204, Vitest
281/281, typage et lint sans erreur.

**Échec non expliqué.** Un premier passage complet a fait échouer d'un coup les 21 tests des comptes clients
(`CustomerAccountTests`, `CustomerAccountGapsTests`). Ils passent isolément et n'ont pas échoué dans les trois passages
complets suivants. Le journal de ce passage, en mode silencieux, ne donne que les noms : la cause n'est pas établie. À
surveiller : relancer la suite complète avec `--logger "console;verbosity=normal"` si cela se reproduit.
