# Progression — audit édition 2 (2026-10-16, HEAD `6a47c6d`)

Reprise : lire ce fichier, puis `edition2-registre.tsv` (périmètre) et `notes-ed2.tsv` (fichiers déjà notés : un
fichier présent dans notes-ed2.tsv est fait). Anomalies : `anomalies-ed2.md`. Outil de note : script `note.py` du
dossier temporaire de session (format `chemin | module | statut | gravité | observations | preuves | action [| statut d'analyse]`).

## Périmètre

- 1 148 fichiers suivis (hors `audit-nexora-one/` et `marketing/` non suivi).
- À lire ou relire : 863 (713 non lus en éd. 1, 67 non lus et modifiés, 48 lus et modifiés depuis `e5e243d`, 35 nouveaux).
- Verdict d'éd. 1 reporté (inchangés depuis `e5e243d`) : 134. Documentation/binaires : 151.

## Lots

| Lot | État |
|---|---|
| 1 Structure, CI, infrastructure, config frontend | fait |
| 2 Auth, utilisateurs, rôles, permissions, isolation | fait (backend) |
| 3 Entreprises, paramètres, administration, installation | fait (backend) |
| 4 Catalogue, stock | fait (backend) |
| 5 Ventes, caisse, hors ligne | fait (backend) |
| 6 Achats, fournisseurs, clients, recouvrement | fait (backend) |
| 7 Finance, comptabilité | fait (backend) |
| 8 RH, paie | fait (backend) |
| 9 Rapports, tableaux de bord | fait (backend) |
| 10 Intelligence, automatisations, tâches | fait (backend) |
| 11 Sécurité transverse, intégrations, GED, notifications, plateforme, exploitation | fait (backend ; « lecture ciblée » signalée fichier par fichier) |
| 12 Frontend (269), tests (119), migrations (102), scripts, E2E | fait (contrôles automatisés + lectures ciblées ; E2E non relancés) |

## Tests exécutés (2026-10-16, Windows 11, .NET SDK 10.0.402, Node 24.18, PostgreSQL 18 local)

| Commande | Résultat |
|---|---|
| `dotnet build Nexora.sln` | OK |
| `dotnet test tests/Nexora.UnitTests` | 103/103 |
| `dotnet test tests/Nexora.IntegrationTests` (1er et 2e passage) | 532 échecs en 1 ms : délai de 30 s dépassé sur `CREATE DATABASE … TEMPLATE nexora` (mesuré à la main : 22,9 s pour 49 Mo) |
| idem avec `NEXORA_IT_ADMIN="…;Command Timeout=600"` | **532/532** (6 min 8 s) |
| `dotnet test tests/Nexora.ApiTests` en parallèle de la charge (1er passage) | 153/200 (42 annulations de requête, 2 × 401 après 40 min, 1 délai, 1 × 500, 1 réponse vide) |
| idem seul, `NEXORA_API_TEST_DB=nexora_api_it` | **200/200** (8 min 5 s) |
| `npm run typecheck`, `npm run lint`, `npm run build` | OK |
| `npx vitest run` (1er passage, sous charge) | 216/216 mais 13 fichiers non démarrés |
| idem seul | **63/63 fichiers, 279/279** |
| `dotnet list Nexora.sln package --vulnerable --include-transitive` | System.Security.Cryptography.Pkcs 10.0.0 (High + Moderate) via MimeKit 4.18.1 → A-036 |
| `npm audit --omit=dev` | 0 vulnérabilité |

Écritures en base faites par l'audit : bases jetables des tests d'intégration (créées/supprimées par la fixture),
écritures des tests d'API dans `nexora_api_it` (base de test), un clone `nexora_audit_probe` créé puis supprimé
(mesure du temps de clonage). Aucune écriture dans `nexora` ni `nexora_recette`.

## Nouvelles anomalies (détail : anomalies-ed2.md)

A-033 (MOYEN, manage_users vérifiée sur l'entreprise active seulement), A-034 (MOYEN, motif « permission active / données
toutes entreprises » : audit, clonage de configuration), A-035 (MOYEN, révocation d'accès d'urgence ignorant l'échec),
A-036 (MOYEN, dépendance vulnérable, CI rouge), A-037 (MOYEN, rollback de configuration sans effet), A-038 (ÉLEVÉ à
confirmer, double réception/validation de stock), A-039 (MOYEN, simulateur ≠ caisse), A-040 (ÉLEVÉ à confirmer, taxe de
ligne choisie par le client), résidu A-003 (ÉLEVÉ à confirmer, /sync), résidu A-011 (FAIBLE), T-003 (FAIBLE, délai de
clonage de la fixture), A-041 (ÉLEVÉ, déséquilibre d'arrondi en devise), A-042 (ÉLEVÉ, aucun exercice 2027 : toute
écriture refusée dès le 2027-01-01), A-043 (ÉLEVÉ, états financiers faux après extourne), A-044 (MOYEN, réouverture
d'exercice clôturé), A-045 (MOYEN, routes /finance/* brutes contournant les contrôles), A-046 (MOYEN, KPI multidevises additionnés), A-047
(MOYEN, budget doublé par révision), A-048 (MOYEN, données financières exposées via intelligence.view), A-049 (ÉLEVÉ SaaS,
jeton de webhook de facturation par défaut public), A-050 (MOYEN, stock d'ouverture importé sans écriture), A-051
(MOYEN, sauvegardes « vérifiées » sur métadonnées déclarées), A-052 (ÉLEVÉ, prouvé : coût des ventes en devise multiplié
par le taux).

## Vérifications à l'exécution prévues (lot 12, sur une base jetable clonée, API locale)

1. A-040 : vente avec `taxId` d'une taxe 0 % sur un article taxé.
2. A-038 : deux réceptions simultanées d'un même transfert ; deux validations d'un même inventaire.
3. Résidu A-003 : appareil enrôlé par un caissier, `/sync` d'une vente datée avant la clôture d'une session close.
4. A-039 : aperçu caisse vs simulateur sur promotion + coupon %.
5. Revérification des anomalies corrigées A-002, A-012, A-018 (scénarios de l'éd. 1).
6. A-043 : vente → extourne → balance / bilan / grand livre.
7. A-041 : vente en USD au taux 132,5 (net 10,01, taxe 1,01).
8. A-049 : POST /api/v1/billing/webhooks/payment avec le jeton par défaut (sur l'API locale, base jetable).
9. Écart stock / compte de stock de la Pharmacie ABC : FAIT (A-052, prouvé par SELECT).
10. Requêtes SELECT d'intégrité sur `nexora_recette` (équilibre, stock, soldes) — à refaire après les tests.

## Vérifications à l'exécution faites (2026-10-16, base jetable `nexora_audit_ed2` clonée de `nexora_recette`, API locale :5099, Development)

| Anomalie | Résultat |
|---|---|
| A-003 résidu | CONFIRMÉ : vente /sync ajoutée à une session close (SMV2026-002032) |
| A-038 | CONFIRMÉ : 1 unité transférée, 7 puis 8 unités reçues en concurrence |
| A-040 | CONFIRMÉ : taxe 13 → 0 par `taxId` = EX0 (VTE2026-000444) |
| A-041 | CONFIRMÉ (écriture manuelle) : 2652.65 ≠ 2652.64, 409 |
| A-043 | CONFIRMÉ : caisse 123 255,90 → 122 255,90 après extourne |
| A-045 | CONFIRMÉ : dépense payée sans approbation par le comptable |
| A-048 | CONFIRMÉ : 403 sur /forecasts, 200 sur /intelligence/forecasts |
| A-049 | CONFIRMÉ : 200 avec le jeton public par défaut, 401 sinon |
| A-039 | CONFIRMÉ : simulateur 102,96 contre caisse 100,10 |
| A-052 | PROUVÉ par SELECT sur nexora_recette (40 500 au lieu de 300) |

Non rejoués : A-042 (date 2027 : non simulable sans changer l'horloge ; preuve par code + SELECT),
re-vérification des corrections A-002/A-012/A-018 (vérifiées par lecture et par les tests d'intégration verts).

## Livrables (2026-10-17)

Tous réécrits pour l'édition 2 : 00, 01, 02 (CSV), 03, 04, 05 (inventaire régénéré, `endpoints2.py`), 06, 07, 08, 09,
10, 11. Audit édition 2 terminé ; aucune correction faite, aucun commit fait par l'audit.
