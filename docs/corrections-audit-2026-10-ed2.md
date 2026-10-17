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
