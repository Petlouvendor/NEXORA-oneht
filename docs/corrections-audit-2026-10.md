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
