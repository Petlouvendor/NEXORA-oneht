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
