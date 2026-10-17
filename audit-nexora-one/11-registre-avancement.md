# 11 — Registre d'avancement de l'audit NEXORA ONE (édition 2)

> Édition 2 démarrée le 2026-10-16 sur `develop`, HEAD `6a47c6d` ; livrables terminés le 2026-10-17.
> Règle : aucun fichier applicatif modifié ; seul `audit-nexora-one/` est écrit. L'édition 1 (2026-10-13/14, HEAD
> `e5e243d`, registre complet) est dans l'historique Git (commit `ee4f025`).
> Fichiers de travail pour une reprise : `travail/progression-ed2.md` (état), `travail/edition2-registre.tsv`
> (périmètre), `travail/notes-ed2.tsv` (une ligne par fichier examiné), `travail/anomalies-ed2.md` (anomalies au fil
> de l'eau).

## Périmètre

| Élément | Nombre |
| --- | --- |
| Fichiers suivis par Git (hors `audit-nexora-one/` et `marketing/` non suivi) | 1 148 |
| À examiner en éd. 2 | 863 (713 non lus en éd. 1, 67 non lus et modifiés, 48 lus et modifiés, 35 nouveaux) |
| Verdict d'éd. 1 reporté (fichier inchangé depuis `e5e243d`) | 134 |
| Documentation, binaires, générés | 151 |

## Environnement constaté

| Élément | État |
| --- | --- |
| .NET SDK | 10.0.402 |
| Node | 24.18 |
| PostgreSQL | 18 local, port 5432 |
| Docker | non utilisé (E2E non exécutés) |
| Copie de travail | propre côté code ; seuls `audit-nexora-one/` (modifié) et `marketing/` (non suivi) diffèrent |

## Lots

| Lot | Contenu | État |
| --- | --- | --- |
| 1 | Structure, CI, infrastructure, configuration | terminé |
| 2 | Authentification, utilisateurs, rôles, permissions, isolation | terminé (A-033, A-034, A-035, A-011 r., A-009 r.) |
| 3 | Entreprises, paramètres, administration, installation | terminé (A-037) |
| 4 | Catalogue, stock | terminé (A-038, A-039, A-050) |
| 5 | Ventes, caisse, hors ligne | terminé (A-040, A-003 r., A-052) |
| 6 | Achats, fournisseurs, clients, recouvrement | terminé (A-022 r.) |
| 7 | Finance, comptabilité | terminé (A-041 à A-045) |
| 8 | RH, paie | terminé (constats FAIBLES) |
| 9 | Rapports, tableaux de bord | terminé (A-046, A-047) |
| 10 | Intelligence, automatisations, tâches | terminé (A-048) |
| 11 | Sécurité transverse, intégrations, GED, notifications, plateforme, exploitation | terminé (A-049, A-051, A-036) ; lecture ciblée signalée par fichier |
| 12 | Frontend, tests, migrations, scripts, E2E ; scénarios ; livrables | terminé ; frontend et migrations par contrôles automatisés et lectures ciblées ; E2E non relancés |

## Couverture réelle (honnêteté du registre)

| Mode d'examen | Fichiers |
| --- | --- |
| Lecture ligne à ligne | 210 |
| Lecture ciblée (risques, routes et permissions, filtres) | 94 |
| Relu (différences depuis l'éd. 1) | 8 |
| Parcouru (interfaces, DTO) | 89 |
| Exécuté via les suites de tests (non relu ligne à ligne) | 176 |
| Contrôles automatisés (non relu ligne à ligne) | 290 |
| Verdict d'éd. 1 reporté | 132 |
| Documentation, binaires, générés | 149 |

Le CSV `02-inventaire-fichiers.csv` porte ce mode pour chaque fichier (colonne `statut_analyse`) et l'édition du
verdict (colonne `edition_du_verdict`). **Restent sans lecture ligne à ligne : 466 fichiers de code** (contrôlés,
exécutés par les tests ou parcourus), dont l'essentiel des écrans frontend et des migrations V001-V099.

## Journal

- 2026-10-16 — Registre de périmètre (diff `e5e243d..6a47c6d`), lots 1 à 11 côté backend, suites de tests
  (build, unitaires, intégration, API, Vitest, typecheck, lint, build, scans), revérification des anomalies de l'éd. 1.
- 2026-10-16 — Scénarios sur l'API locale de HEAD (port 5099, Development) contre `nexora_audit_ed2`, clone jetable
  de `nexora_recette` : A-003 r., A-038, A-039, A-040, A-041, A-043, A-045, A-048, A-049 confirmés ; `/sync` 500 sur
  date non UTC. Base jetable supprimée ensuite ; API arrêtée (port libéré).
- 2026-10-16/17 — SELECT en lecture seule sur `nexora_recette` : A-052 prouvé (écart stock / grand livre de la
  Pharmacie expliqué), A-042, A-046, A-048, A-009 r. ; catalogue (RLS, fonctions, privilèges, 102 migrations).
- 2026-10-17 — Lot 12 (frontend, tests, migrations, scripts) ; croisement frontend ↔ API (739 appels, tous résolus) ;
  inventaire des points d'accès régénéré (1 032) ; livrables 00 à 11 réécrits pour l'éd. 2.

## Écritures en base faites par l'audit

| Base | Nature | État |
| --- | --- | --- |
| Bases de la fixture d'intégration | créées et supprimées par les tests | supprimées |
| `nexora_api_it` | base déclarée des tests d'API (T-002 corrigé) | conservée (base de test) |
| `nexora_audit_probe` | mesure du temps de clonage | supprimée |
| `nexora_audit_ed2` | scénarios d'exécution | supprimée |
| `nexora`, `nexora_recette` | **aucune écriture** (SELECT en session lecture seule) | inchangées par l'audit |

Après l'audit, les corrections (lots 0 à 5) ont appliqué V103, V104 et V105 à `nexora`, `nexora_recette` et `nexora_api_it`,
et deux écritures correctives à `nexora_recette`, après sauvegarde (`C:\Users\Rener\nexora-sauvegardes\2026-10-17-avant-V103`
`…-avant-V104` et `…-avant-V105`). Détail : `docs/corrections-audit-2026-10-ed2.md`.

## Décisions du propriétaire (2026-10-17) et questions restantes

Tranché le 2026-10-17 (exécution : `docs/corrections-audit-2026-10-ed2.md`) :

1. A-042 : écran « Ouvrir l'exercice suivant », ouverture planifiée dans le dernier mois et filet au moment d'une
   opération du jour — **fait** (lot 1).
2. A-043 : les états agrègent `posted` et `reversed` par une règle unique ; l'extourne n'est pas modifiée — **fait**
   (lot 2).
3. A-052 : données existantes corrigées par écritures correctives (commande `correction-cout-devise`) — **fait** sur
   `nexora_recette` (lot 2).
4. A-049 : route de facturation plateforme fermée tant qu'aucun jeton fort n'est configuré, paiements imputés au seul
   client de la facture et à hauteur de son total — **fait** (lot 5).
5. A-045 : routes `/finance/*` retirées — **fait** (lot 5).

Restent ouvertes :

1. Bases laissées par l'éd. 1 : `nexora_audit_api` (clone) — à supprimer si souhaité.
2. Identifiants bac à sable (MonCash, SMTP, Twilio, FCM) et Docker pour l'E2E.

## État des processus lancés par l'audit

Aucun processus en cours : API d'audit arrêtée, aucune tâche de fond active.
