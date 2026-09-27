# Phase 49 — Qualité, Tests globaux & Release Candidate — implémentation

**Statut : livré et vérifié.** **Aucune migration** (pas de table, pas de colonne — la phase ne
touche pas au modèle de données). 5 nouveaux points dans la checklist Go-Live (14 → 19), 1 nouveau
test d'intégration (`OperationsReadinessTests`, total 8), 2 nouveaux fichiers de tests API
(`SmokeTests` 1 test, `MultiTenantIsolationTests` 4 tests), une matrice de traçabilité.
**384 tests backend** verts (18 unit + 57 API + 309 intégration), 0 warning. 110 tests frontend
(inchangés — aucun changement d'interface requis, la checklist est rendue dynamiquement).
Base : 250 tables, 211 RLS, 62 migrations (inchangé).

**Constat préalable, déterminant pour le périmètre de cette phase :** la Phase 49 n'est pas une
phase de fonctionnalité. Elle demande de **valider** ce qui existe. La plus grande partie de ce
qu'elle décrit est déjà en place :

- les tests eux-mêmes — 378 tests backend et 110 frontend au début de la phase, écrits phase par
  phase sur tous les modules ;
- le mécanisme de décision Release Candidate / Go-No-Go (§56-58) — **déjà livré en Phase 37**
  (`golive_signoff` : verdict `go` / `go_with_reservations` / `no_go`, instantané figé de la
  checklist, forçage motivé, par environnement) ;
- la checklist de préproduction (§59) — les 14 points dérivés en direct de la Phase 37 ;
- le monitoring (§47), la continuité (§35-36), la qualité des données (§43).

Plutôt que de dupliquer ces suites dans une « campagne Phase 49 » parallèle, cette phase a :

1. **relié** chaque exigence §9-47 aux tests qui la couvrent réellement, dans une matrice honnête
   (couvert / partiel / manuel / hors code) — [`phase-49-matrice-tracabilite.md`](phase-49-matrice-tracabilite.md) ;
2. **comblé les trous réels** : les critères RC §57 absents de la checklist Go-Live, un parcours de
   fumée §52 contre la vraie API HTTP, une preuve d'isolation multi-tenant §32 bout-en-bout ;
3. **consigné les anomalies trouvées** en exécutant ces tests (4, aucune P0/P1).

### Socle réutilisé (non reconstruit)

- **`IOperationsService.GoLiveReadinessAsync`** (Phase 37) — **étendu** de 5 points, même
  mécanisme de dérivation, même DTO, même signature
- **`golive_signoff`** (Phase 37) — la décision RC **est** une signature Go-Live sur
  l'environnement `staging` ; aucune table `release_candidate` séparée
- **`FinanceEngineFixture`** + `[Collection("finance")]` — inchangés
- **Le motif `WebApplicationFactory<Program>`** des tests API existants — réutilisé tel quel

---

## Correspondance cahier des charges → implémentation

| § | Exigence | Où |
|---|---|---|
| 1-3 | Objectif ; principe exigence → test → résultat → anomalie → correction → régression ; périmètre global | Appliqué littéralement pendant cette phase : les anomalies A1-A4 ont été trouvées en exécutant les tests, A3 a été corrigée puis la régression complète relancée (309/309) |
| 4 | Matrice de traçabilité | **NEW** — `docs/phase-49-matrice-tracabilite.md` : 38 familles, test(s) réel(s) par famille, statut, manques. Version, environnement, date et résultat en tête |
| 5 | Criticité P0-P4 | Utilisée pour classer A1-A4 ; deux nouveaux points de checklist `no_open_p0` / `no_open_p1_waiver` |
| 6 | Critères d'acceptation | Chaque test vérifie attendu vs observé ; la permission (401/403/404) et l'audit sont vérifiés dans les suites existantes (`SecurityAuditTests`) |
| 7 | Environnements | **Hérité** — `deployment.environment` et `golive_signoff.environment` (`local`/`test`/`staging`/`production`, Phase 37). Pré-production = `staging` |
| 8 | Données de test | **Hérité** — `demo_abc_group.sql` (3 entreprises, 3 secteurs, multidevise HTG/USD, multi-entreprises) ; chaque suite d'intégration part d'un clone neuf de la base. **Manque constaté A4** : aucun stock démo |
| 9-47 | Toutes les familles de tests | Voir la matrice : 19 couvertes, 14 partielles, 3 manuelles, 2 hors code |
| 12 | End-to-end | **NEW** — `SmokeTests` enchaîne interface HTTP → service → base → document → recherche. Pas de test navigateur (backlog) |
| 32 | Sécurité multi-tenant | **NEW** — `MultiTenantIsolationTests` (4) + point `multi_tenant_isolation` réel |
| 48-49 | Gestion et cycle des anomalies | **Hors code applicatif** : NEXORA ONE n'a pas de registre d'anomalies et n'en crée pas — c'est le rôle de l'outil de suivi (GitHub Issues / Jira). Les anomalies de cette phase sont consignées dans la matrice avec ID, criticité, constat, statut |
| 50-51 | Régression ; automatisation QA | **Hérité** — `dotnet test` (3 projets) + `npx vitest run` + `tsc` ; la CI (`.github/workflows/`) rejoue migrations + smoke SQL + build. SAST/SCA : hors code (Phase 37) |
| 52 | Smoke tests | **NEW** — `SmokeTests.Golden_path_…` : les 10 étapes, dans l'ordre. Deux étapes révèlent un écart (A1 « Créer client », A2 « Logout ») — documentées dans le test lui-même plutôt que contournées |
| 53-55 | UAT ; pilotage UAT ; bêta contrôlée | **Processus humain** — nouveau point `uat_accepted` (manuel) dans la checklist ; bêta : hors code |
| 56-58 | Release Candidate ; critères RC ; Go/No-Go | **Hérité et étendu** — voir ci-dessous. La séquence code freeze → … → GO/NO-GO se conclut par `POST /api/v1/go-live/sign-off` (Phase 37) sur `staging` |
| 59 | Checklist préproduction | **Hérité** — les 14 points Phase 37 couvrent version, backup, migrations, secrets, monitoring, alertes, restauration, support, rollback. SSL/TLS, DNS : infrastructure |
| 60-61 | Déploiement V1.0 ; post-release | **Hérité** — `POST /api/v1/deployments` + rollback (Phase 37), `system/status`, page de statut (Phase 48). Relève de la Phase 50 |
| 62 | Documentation finale | **Partiel** — 49 docs de phase + README + `database/README.md`. Manuel utilisateur, guides par rôle, FAQ : **non écrits** (backlog Phase 50) |
| 63 | KPIs qualité | Ceux qui sont calculables le sont déjà : succès backup / restore (`backup_verified`, `restore_tested`), disponibilité (`system/status`), qualité des données (note A-E). Taux de tests réussis, réouverture, temps de correction, satisfaction UAT : **dans la CI et l'outil de suivi**, pas en base |
| 64-66 | Critères de sortie ; livrable ; étape suivante | Voir « Décision RC » ci-dessous |

## Critères RC §57 → checklist Go-Live

La checklist Phase 37 couvrait déjà 5 des 10 critères §57. Les 5 manquants ont été ajoutés au
**même** calcul (`GoLiveReadinessAsync`), jamais dans un second registre :

| Critère §57 | Point de checklist | Source | Nouveau ? |
|---|---|---|---|
| Aucun P0 ouvert | `no_open_p0` | manuel | **NEW** |
| Aucun P1 sans dérogation formelle | `no_open_p1_waiver` | manuel | **NEW** |
| Fonctions critiques validées | `automated_tests` | manuel | existant |
| Sécurité validée | `no_critical_vulnerabilities` + `production_secrets` | manuel + auto | existant |
| Performance cible atteinte | `performance_targets_met` | manuel | **NEW** |
| Backup / restore validés | `backup_verified` + `restore_tested` | auto | existant |
| Multi-tenant validé | `multi_tenant_isolation` | **auto** | **NEW** |
| UAT accepté | `uat_accepted` | manuel | **NEW** |
| Documentation disponible | `training_done` | manuel | existant |
| Plan de rollback prêt | `rollback_plan` | auto | existant |

Les points manuels le restent volontairement : l'application ne peut pas savoir qu'une anomalie P0
est ouverte dans Jira ni qu'un banc de charge a été passé. Marquer ces points « pass » à partir de
rien serait une fausse assurance.

## Backend

| Couche | Fichier |
|---|---|
| Infrastructure | `Operations/SqlOperationsService.cs` — `GoLiveReadinessAsync` : +5 points (catégories `quality`, `performance`, `security`, `uat`) |
| Contracts / Application / API | inchangés — `GoLiveCheckDto` et la route `GET /api/v1/go-live` étaient déjà génériques |

### Points de conception

- **`multi_tenant_isolation` est le seul nouveau point automatique, et c'est un vrai signal.** Il
  lit `pg_roles.rolbypassrls` pour le rôle réellement connecté (`current_user`). Si ce rôle
  contourne RLS, **toute** l'isolation entreprise tombe en silence, quel que soit l'état des 211
  politiques. C'est le seul réglage qui peut désactiver d'un coup l'isolation — d'où ce contrôle
  plutôt qu'un comptage de tables fragile. En production (`nexora_app`) : `pass`. Dans la fixture
  d'intégration (superutilisateur) : `fail`, attendu et testé comme tel.
- **La décision RC réutilise `golive_signoff`.** Une RC est une signature sur `staging`, la mise en
  production une signature sur `production`. Même table, même instantané figé, même règle
  « `go` franc refusé si un point est en échec ».
- **Le test de fumée amorce son stock par un mouvement `opening`, jamais par écriture directe.** Le
  trigger `stock_movement_apply()` est le seul chemin qui garde `stock_balance` et `lot_stock`
  cohérents. Voir A3 ci-dessous pour ce qui s'est passé avant.
- **Les tests d'isolation utilisent un seul compte et un seul jeton**, en ne changeant que
  `X-Company-Id` (Pharmacie ↔ Restaurant). C'est la forme la plus stricte : une fuite ne pourrait
  venir que du filtrage RLS/SQL lui-même, pas d'une différence d'autorisation entre deux comptes.

## Tests d'intégration — 1 nouveau dans `OperationsReadinessTests` (total 8)

| Test | § | Vérifie |
|---|---|---|
| `Go_live_checklist_lists_every_expected_control` (étendu) | 57 | les 5 nouvelles clés sont présentes |
| `Multi_tenant_isolation_check_matches_the_real_bypassrls_flag_of_the_connected_role` | 32, 57 | le statut du point correspond exactement à `rolbypassrls` du rôle connecté, source `auto` |

## Tests API — 2 nouveaux fichiers (5 tests)

| Test | § | Vérifie |
|---|---|---|
| `SmokeTests.Golden_path_login_dashboard_sale_stock_invoice_search_logout_health` | 12, 52 | Login → dashboard → client → vente d'un produit suivi par lot (FEFO) payée en espèces → mouvement de stock → numéro de document → recherche → jeton retiré = 401 → health |
| `MultiTenantIsolationTests.A_product_readable_in_its_own_company_returns_not_found_under_another_companys_scope` | 32 | même id produit : 200 en Pharmacie, **404** en Restaurant |
| `…Product_list_scoped_to_one_company_never_contains_another_companys_sku` | 32 | la liste Restaurant ne contient jamais `PARA500` |
| `…Catalog_search_scoped_to_one_company_never_surfaces_another_companys_product` | 32 | la recherche tolérante Restaurant ne remonte jamais le produit Pharmacie |
| `…Incident_opened_in_one_company_is_invisible_when_listing_from_another_companys_scope` | 32 | un incident ouvert en Pharmacie est visible en Pharmacie, invisible en Restaurant |

## Anomalies relevées

Détail complet dans la matrice. Aucune P0, aucune P1.

- **A1 (P2)** — pas d'API de création de client ni de fournisseur ; uniquement import ou SQL.
- **A2 (P3)** — la déconnexion du frontend ne révoque pas la session serveur.
- **A3 (P2, outillage)** — les tests API écrivent dans `nexora`, que les tests d'intégration
  clonent comme modèle. **Survenu pendant cette phase** : une première version de `SmokeTests`
  réglait `stock.negative_stock_allowed=true` sur la Pharmacie ; le réglage est resté en base et a
  fait échouer 4 tests de stock de façon reproductible (305/309). Cause trouvée, réglage supprimé,
  `stock_balance` réaligné, test réécrit ; 309/309 depuis. Le risque structurel reste : tout test
  API qui modifie une politique partagée contamine la campagne suivante.
- **A4 (P4)** — aucune entreprise démo n'a de stock.

## Décision RC

**Recommandation : NO-GO pour une V1.0 en production, GO avec réserves pour une RC sur `staging`.**

| Critère §57 | État |
|---|---|
| Aucun P0 / P1 ouvert | ✅ aucun trouvé |
| Fonctions critiques validées | ✅ 384 + 110 tests verts, parcours de fumée vert |
| Sécurité validée | ⚠️ aucune sonde XSS/CSRF/SSRF automatisée ; SSRF webhooks connu (Phase 46) ; `production_secrets` en échec en dev (clé JWT de développement, attendu) |
| Performance cible atteinte | ❌ jamais mesurée |
| Backup / restore validés | ⚠️ registre testé ; restauration physique jamais exercée |
| Multi-tenant validé | ✅ point automatique + 4 tests bout-en-bout |
| UAT accepté | ❌ pas d'UAT |
| Documentation disponible | ⚠️ technique oui ; manuels utilisateur non |
| Plan de rollback prêt | ✅ déploiements journalisés et réversibles |

Ce verdict n'est pas signé ici : `POST /api/v1/go-live/sign-off` exige un décideur identifié
(§58). La checklist et cette matrice en sont les preuves.

## Manques assumés (backlog)

- **Tests navigateur** (Playwright/Cypress) — l'étape « interface » du §12 n'est vérifiée que par
  les tests de contrat client
- **Bancs de charge** (§33-34) et **sondes de sécurité adversariales** (§31) — CI / outillage externe
- **Audit d'accessibilité WCAG 2.2 AA**, **matrice de compatibilité**, **UAT**, **bêta** — processus humains
- **Restaurant** (§19) : tables, modificateurs, cuisine, split, transfert — non testés
- **Isolation multi-tenant** des exports, notifications, cache, logs (§32) — couverte par
  l'architecture (RLS), pas par un test dédié
- **Importeurs** factures, dettes, documents, comptabilité (§44)
- **Base de démo séparée** pour les tests API, pour supprimer le risque A3 à la racine
- **Registre d'anomalies** et KPIs de défauts (§48-49, §63) — outil de suivi externe
- **Documentation utilisateur** (§62) — Phase 50
