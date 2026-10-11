# Finalisation produit — Phase 8 : Comptabilité

> Plan maître « NEXORA ONE — Finalisation complète du produit », phase 8.
> Branche `feature/accounting`. Date : 2026-10-02.

## 1. Travail effectué

Avant cette phase, la page Comptabilité avait trois onglets (écritures, balance, clôture). La saisie demandait de
taper le code du compte de mémoire, la devise était imposée (HTG), et contrepassation et à-nouveaux passaient par
`window.prompt`. Le plan comptable, le grand livre, le compte de résultat, le bilan et les périodes n'étaient pas
accessibles depuis l'application, alors que le serveur savait les produire (sauf la gestion du plan comptable).

| Onglet | Après |
|---|---|
| Écritures | recherche (statut, journal, dates, n° / libellé / compte) ; fiche de l'écriture (lignes, auteur, validateur, contre-valeur en devise de l'entreprise, lien vers la contrepassation) ; **saisie** avec choix du compte dans le plan, devise au choix, indicateur d'équilibre et contrôle ligne par ligne ; modification et suppression du brouillon ; validation ; **contrepassation motivée** |
| Plan comptable | arborescence, solde et nombre de lignes de chaque compte ; **création, modification, regroupement, désactivation / réactivation motivées** |
| Grand livre | compte, période, solde d'ouverture, mouvements avec solde progressif, solde de clôture |
| Balance | à une date, totaux et contrôle d'équilibre |
| États financiers | **compte de résultat** (période) et **bilan** (à la date de fin), contrôle actif = passif |
| Périodes & clôture | clôture de période, **réouverture motivée**, contrôles de clôture d'exercice, clôture, **à-nouveaux par choix de l'exercice source** |

Tous les tableaux s'exportent en Excel / PDF. Plus aucun `window.prompt` dans la comptabilité.

## 2. Défauts corrigés

1. **Ligne d'écriture invalide** (montant au débit et au crédit, ou nul) : la base renvoyait son message brut en
   anglais (409). Désormais 422 « Ligne N : saisissez un montant positif au débit OU au crédit ».
2. **Écriture en devise mal présentée** : une écriture de 10 USD affichait « 1 350 USD » (contre-valeur en HTG
   étiquetée USD). Les totaux sont dans la devise de l'écriture ; la contre-valeur est fournie à part.
3. **Validation d'une écriture en devise** : le contrôle utilisait toujours HTG au lieu de la devise de l'écriture.
4. **Grand livre d'un compte inconnu** : erreur 500. Désormais 404 explicite.
5. Saisie acceptée **datée dans le futur**, en **devise inconnue**, ou dans une **période clôturée** (refus tardif
   et peu lisible) : contrôlée d'emblée, message clair.
6. **Plan comptable non gérable** : la permission `accounting.manage_chart` existait sans aucune fonction derrière ;
   un compte manquant obligeait à passer par la base.
7. **Modifications du plan comptable non tracées** : les comptes, plans, journaux et paramétrages d'écritures
   automatiques n'étaient pas audités.
8. Recherche d'écritures limitée au statut et aux dates (ni journal, ni texte, ni compte).

## 3. Base de données — V085

- Audit (journal d'audit immuable) sur `account`, `chart_of_accounts`, `journal`, `account_mapping`.
- Unicité du code de compte **insensible à la casse** par entreprise.
Appliquée sur `nexora` et `nexora_recette`.

## 4. APIs

Nouvelles : `GET /accounts/manage` (solde, lignes, usages), `POST /accounts`, `PUT /accounts/{id}`,
`POST /accounts/{id}/status`, `GET /accounting-currencies`.
Étendues : `GET /accounting-entries` (+ `journal`, `q`, `limit`), fiche d'écriture (contre-valeur, auteur,
validateur, origine, contrepassation), `GET /general-ledger` (404 sur compte inconnu).
Utilisées pour la première fois par l'interface : `PUT /accounting-entries/{id}`, `/general-ledger`,
`/financial-statements`, `/accounting-periods/{id}/close|reopen`.

## 5. Règles garanties

- Écriture : au moins deux lignes, chaque ligne au débit **ou** au crédit, compte mouvementable et actif,
  équilibre, date ni future ni en période clôturée. Une écriture validée ne se modifie pas : elle se contrepasse
  (une seule fois), avec motif.
- Plan comptable : code unique (casse ignorée), regroupement de même type et non mouvementable, sans boucle.
  **Un compte mouvementé garde son code, son type, sa nature et sa devise** (la balance et les états déjà produits
  ne changent pas de sens) ; seuls l'intitulé et le regroupement restent modifiables.
- Désactivation refusée tant que le compte porte un solde, figure dans un brouillon, ou sert aux écritures
  automatiques, à un compte de trésorerie ou à une catégorie de dépense (les usages sont nommés).
- Séparation des tâches conservée : le comptable saisit et valide ; contrepassation, plan comptable, périodes et
  clôture relèvent du chef comptable / de la direction (vérifié : comptable → 403, caissier → 403).

## 6. Tests

| Suite | Résultat |
|---|---|
| Unitaires backend | 50 / 50 |
| Intégration backend (dont 2 nouveaux `AccountingFinalisationTests`) | 454 / 454 |
| API backend | 186 / 186 |
| Frontend Vitest (dont 5 nouveaux) | 232 / 232 |
| Scénario API (clone `nexora_qa`, comptable + direction de recette) | 34 / 34 |
| Navigateur : saisie déséquilibrée refusée, brouillon, validation, recherche, 6 onglets, création / désactivation de compte, saisie-validation directe, contrepassation | réussi, 0 erreur console, 0 `window.prompt` |

## 7. Dette technique restante

- Pas de lettrage des comptes de tiers ni de saisie analytique (centres de coûts) dans l'interface.
- Une seule période (« Exercice 2026 ») dans les données de démonstration : les à-nouveaux n'ont pu être vérifiés
  qu'à travers le message « aucun exercice antérieur ».
- Clôturer une période annuelle bloque toute écriture de l'année : il n'existe pas encore de découpage mensuel
  automatique des périodes.
- Pas d'import d'écritures ni d'export FEC ; les états sont exportables en Excel / PDF depuis les tableaux.
