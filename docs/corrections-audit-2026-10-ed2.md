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
