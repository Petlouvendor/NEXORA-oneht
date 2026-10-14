# 11 — Registre d'avancement de l'audit NEXORA ONE

> Audit démarré le 2026-10-13 sur la branche `develop` (HEAD `e5e243d`).
> Règle : aucun fichier applicatif modifié. Seul ce dossier `audit-nexora-one/` est écrit.
> Notes brutes par fichier : `travail/notes-fichiers.tsv` (une ligne par fichier, colonnes du CSV final).
> Anomalies au fil de l'eau : `travail/anomalies.md`.

## Périmètre

- Corpus = fichiers suivis par Git : **1 113 fichiers** (`git ls-files`).
- Exclus de l'analyse détaillée (générés / non sources) : `bin/`, `obj/`, `node_modules/`, `dist/`,
  `TestResults/`, `e2e-results/`, `e2e-report/`, `*.tsbuildinfo`, `infrastructure/secrets/`,
  `infrastructure/backups/`, `infrastructure/production.env` (ignorés par Git, vérifié `git check-ignore`).
- Fichiers binaires suivis (4 PNG, 3 TTF) : inventoriés, non analysés ligne à ligne.

## État de l'environnement (constaté)

| Élément | État |
|---|---|
| .NET SDK | 10.0.401 |
| Node / npm | 24.18.0 / 11.16.0 |
| PostgreSQL | service `postgresql-x64-18` en marche, port 5432 joignable |
| Docker | démon arrêté (`dockerDesktopLinuxEngine` introuvable) → pile compose / E2E conteneurisée non exécutable |
| Copie de travail | 1 fichier modifié non commité : `backend/tests/Nexora.ApiTests/Nexora.ApiTests.csproj` (référence `Nexora.Api` retirée) |

## Lots

| Lot | Contenu | État |
|---|---|---|
| 1 | Structure, configuration, architecture | terminé |
| 2 | Authentification, utilisateurs, rôles, permissions | terminé (lecture + exécution partielle) |
| 3 | Entreprises et paramètres | terminé en lecture ; assistant d'installation non rejoué |
| 4 | Produits, catalogue, stocks | terminé (A-007, A-012, A-013, A-015) |
| 5 | Ventes et POS | terminé (A-002, A-003, A-005, A-014, A-016, A-019) |
| 6 | Achats, fournisseurs, clients | partiel : comptes clients lus en entier ; achats couverts par tests d'intégration, infrastructure achats non relue |
| 7 | Finances et comptabilité | terminé (A-004, A-006, A-017, A-018, A-020 à A-023) |
| 8 | RH / paie | partiel : services lus en surface, calcul couvert par tests |
| 9 | Rapports et tableaux de bord | partiel (A-008, A-022) ; 15 services de rapports non relus |
| 10 | Intelligence, automatisations, tâches de fond | partiel : planificateur et tâches lus ; moteur de recommandations non relu |
| 11 | Sécurité, intégrations, transverse | terminé : 38 fichiers en portée système revus (A-031, A-032), SSRF, paiements, notifications, métriques |
| 12 | Tests, flux, rapport consolidé | terminé : livrables 00 à 11 produits |

## Journal

- 2026-10-13 — Découverte : inventaire Git, outillage, build de la copie de travail (échec ApiTests, voir anomalies A-001).
- 2026-10-13 — Lus : `backend/src/Nexora.Api/Program.cs`, `Middleware/*` (7), `Security/*` (4), `HealthChecks/*` (3),
  `Scheduling/*`, `Setup/*` (2), `Observability/*`, `Controllers/ApiControllerBase.cs`, `appsettings*.json`,
  `launchSettings.json`, tous les `.csproj`, `Directory.*.props`, `Nexora.sln`, Dockerfiles, `.gitignore`,
  `.dockerignore`, `.editorconfig`, `.gitattributes`, `package.json` ; `Nexora.Domain/**` (25 fichiers).

## Exécutions de tests (2026-10-13)

| Commande | Environnement | Résultat |
|---|---|---|
| `dotnet build backend/Nexora.sln` (copie de travail) | Windows 11, .NET SDK 10.0.401 | **ÉCHEC** exit 1 — 75 erreurs, toutes dans Nexora.ApiTests (référence Nexora.Api retirée, modification non commitée) |
| `dotnet build Nexora.sln` (copie de HEAD extraite par `git archive` dans le dossier temporaire) | idem | OK — 0 erreur, 0 avertissement |
| `dotnet test Nexora.UnitTests` (HEAD) | idem | **88/88 réussis** |
| `dotnet test Nexora.IntegrationTests` (HEAD), 1er passage en parallèle d'autres compilations | PG 18 local | 496 échecs en 1 ms : délai de 30 s dépassé sur `CREATE DATABASE … TEMPLATE nexora` (charge machine) — environnement |
| `dotnet test Nexora.IntegrationTests` (HEAD), relancé seul | PG 18 local | **496/496 réussis** (10 min 15 s) |
| `dotnet test Nexora.ApiTests` (HEAD, connexion redirigée dans la copie vers la base clonée `nexora_audit_api`) | PG 18 local | **190/194 réussis, 4 échecs** (voir ci-dessous) |
| `npm run typecheck` | Node 24.18 | OK (exit 0) |
| `npm run lint` | idem | OK (exit 0) |
| `npx vitest run` (en parallèle des compilations) | idem | 49 fichiers / 213 tests réussis ; 13 fichiers non démarrés (délai de démarrage des workers) — à relancer seul |

### Analyse des 4 échecs ApiTests
- `SessionSecurityEndpointsTests.Refresh_rotates…` et `…Logout_idle_and_maximum_lifetime…` : **artefact de l'audit**. Le test modifie la base par une connexion administrateur (redirigée vers le clone) alors que l'API de test, elle, a écrit dans `nexora` (voir incident). Non imputable au produit.
- `SmokeTests.Golden_path…` : `TaskCanceledException` / « client aborted the request » (exécution de 3 min 53 s sous charge) — probablement environnement ; à rejouer.
- `OpsMaintenanceEndpointsTests.Read_only_ops_endpoints…` : `GET /api/v1/ops/database` → **500** (journal serveur non capturé) — à reproduire à l'exécution.

### Incident d'audit (écriture involontaire dans la base de développement)
- Constat : la chaîne `Database:ConnectionString` passée par les tests d'API (`WebApplicationFactory.ConfigureAppConfiguration`) n'est PAS prise en compte : `AddInfrastructure` lit la configuration au moment de l'enregistrement des services, l'API de test retombe sur `appsettings.Development.json` → base `nexora`. Ma redirection (copie des tests vers `nexora_audit_api`) n'a donc pas protégé `nexora`.
- Écritures constatées dans `nexora` depuis 12:20 (lecture seule) : audit_log 235, auth_refresh_token 168, user_session 166, export_log 12, step_up_challenge 9, import_event 9, step_up_failure 5, assistant_query 5, import_row 2, import_record 2, document_share 2, et 1 ligne dans : platform_payment, notification, import_job, emergency_access_grant, dr_test, document, document_version, continuity_incident, billing_event, attachment, api_key. Aucune vente.
- Nature : identique à une exécution normale de `dotnet test` du projet (la base contenait déjà des restes des passages des 10 et 11 octobre). Aucune base de production touchée. Aucun nettoyage effectué (décision laissée à l'utilisateur ; `audit_log` est immuable).
- Base `nexora_audit_api` créée par l'audit (clone de `nexora`) : à supprimer en fin d'audit si l'utilisateur le souhaite.

## Vérifications à l'exécution (2026-10-13, API de HEAD sur la base clonée `nexora_audit_api`, port 5199)

Instance lancée avec `Database__ConnectionString` → `nexora_audit_api` (rôle `nexora_app`, sans BYPASSRLS) ; écriture dans le clone vérifiée (nouvelle `user_session` présente dans le clone, absente de `nexora`). Scripts : dossier temporaire de session (`nx.py`, `s1_refund.py`, `s2_closed_session.py`, `s3_coupon.py`, `s4_concurrency.py`, `s4b_mixed.py`, `s5_count.py`).

| Scénario | Résultat observé | Verdict |
|---|---|---|
| Liaison du jeton step-up à la session (`sid`) | Jeton obtenu en session A : refusé en session B (403 `security.step_up_required`), accepté en session A | FONCTIONNEL VÉRIFIÉ |
| A-002 vente espèces 75 + retour espèces 75 + clôture comptée 0 | Attendu 75, écart −75 ; trésorerie caisse +75 −75 −75 ; écritures : 5711 crédité 2 fois, charge 6580 « Écart de caisse » 75 | **DÉFAUT CONFIRMÉ** |
| A-003 vente directe dans une session close, `soldAt` antérieur à la fermeture | 200 OK, vente VTE2026-000049 rattachée à la session close, mouvement de caisse 150 ajouté après la clôture | **DÉFAUT CONFIRMÉ** |
| A-003 vente antidatée au 2026-09-01 | Refus `sale.no_price` (aucun prix valide à cette date) : la date client est bien appliquée au calcul | Confirme que `SoldAt` client est pris en compte |
| A-012 coupon fixe 50 HTG | 2 lignes → remise 100 ; même article sur 3 lignes → remise 150 (total 225 → 75) | **DÉFAUT CONFIRMÉ** |
| A-018 10 ventes espèces simultanées | Solde cohérent (sérialisées par le verrou de numérotation « vente »/« paiement ») | Non reproduit sur ce chemin |
| A-018 10 ventes + 10 transferts simultanés sur la caisse | Ouverture + Σ transactions = 50 000 ; `current_balance` = 49 475 → **525 HTG perdus** sans erreur | **DÉFAUT CONFIRMÉ** |
| A-007 inventaire ouvert puis 2 ventes | Quantité figée 411, stock courant 409 ; la validation (non exécutée, voir ci-dessous) posterait −2 → 407 | Partiellement confirmé (état intermédiaire observé ; effet final déduit du code) |
| Données | Coût moyen 0 sur le seul stock de démonstration → écritures de vente sans CMV (cause : données, pas code) | Observation |

**Arrêt des scénarios d'écriture** : la validation de l'inventaire (S5) a été refusée par le contrôle automatique des permissions de l'outil (« modification d'une ressource partagée »). Conformément à ce refus, aucun autre scénario écrivant en base n'a été exécuté ; la suite de l'audit est en lecture seule. Données créées par les scénarios S1-S5 : uniquement dans `nexora_audit_api` (≈ 26 ventes de test, 1 session de caisse, 1 retour, 10 transferts, 1 coupon AUDIT…, 1 inventaire ouvert non validé).

## Contrôles d'intégrité en lecture seule sur les données existantes (2026-10-13)

Script `integrity.sql` (dossier temporaire), exécuté en SELECT uniquement sur `nexora_recette` (2 474 ventes, 2 580 écritures, 5 836 mouvements de stock) et `nexora` (47 ventes).

| Contrôle | nexora_recette | nexora |
|---|---|---|
| Écritures validées déséquilibrées | 0 | 0 |
| Solde de stock ≠ Σ mouvements | 0 | 0 |
| `financial_account.current_balance` ≠ ouverture + Σ transactions | 0 | 0 |
| Créances : payé < Σ affectations | 0 | 0 |
| Sessions closes avec écart | 1 sur 3 (−150) | 0 |
| Retours espèces sans mouvement de session | 4 retours espèces, aucun rattaché à une session | — |
| Trésorerie ↔ grand livre (comptes liés) | écart égal au solde d'ouverture de chaque caisse (60 000 ; 45 000) ; 4 comptes financiers partagent le compte 5121 | idem (45 000) |

Lecture : sur les volumes de recette, le moteur reste équilibré et le stock cohérent ; la dérive de solde (A-018) n'apparaît pas sur ces données (générées séquentiellement). Les soldes d'ouverture des comptes financiers n'ont jamais de contrepartie comptable (A-023).

## Suite de l'audit (2026-10-13 après-midi et 2026-10-14)

- Lus : paiements (simulation, carte, MonCash), passerelle de paiement, webhooks et politique SSRF, canaux SMTP/Twilio/FCM,
  paie (services), assistant d'installation, client API et coffre de jetons du frontend, routage, Caddyfile, compose de
  production, CI, console d'exploitation (`Operations/*`), rôles personnalisés, sessions, intercepteur RLS.
- Revue systématique des 38 fichiers qui ouvrent une portée système : 36 bornés, 2 non bornés (A-031, A-032).
- Croisement frontend ↔ API : 637 appels directs, 636 résolus, 1 faux positif.
- Vitest relancé seul : 62/62 fichiers, 275/275 tests.
- Requêtes catalogue (SELECT) : propriétaires, RLS forcée, rôles, politiques, privilèges, tables globales.
- GET seulement sur l'API d'audit : variantes de `/metrics` (A-024).
- Livrables produits : 00, 01, 02 (CSV), 03, 04, 05, 06, 07, 08, 09, 10, 11.

## Bilan de couverture

| Élément | Nombre |
|---|---|
| Fichiers suivis | 1 113 |
| Pertinents (hors documentation, binaires, générés) | 963 |
| Lus ligne à ligne | 183 |
| Tests exécutés via les suites | 174 fichiers |
| Migrations contrôlées par le catalogue | 103 |
| **Restant à lire ligne à ligne** | **503** (liste : CSV, statut « non lu ») |

## Dépendances à vérifier / questions en suspens (décisions du propriétaire)

1. La modification non commitée de `Nexora.ApiTests.csproj` est-elle voulue ? (A-001)
2. Modèle d'exploitation : la console `/ops/*` est-elle destinée à l'éditeur seulement ? (A-031)
3. Modèle comptable des à-nouveaux : grand livre continu ou exercices bornés ? (A-021)
4. Taux de change : globaux à la plateforme ou par entreprise ? (A-017)
5. Nettoyage de la base `nexora` après l'incident T-002 (≈ 626 lignes ; `audit_log` immuable).
6. Suppression de la base `nexora_audit_api` créée par l'audit.
7. Autorisation éventuelle de reprendre des scénarios d'écriture sur `nexora_audit_api` (validation d'inventaire A-007,
   deux clôtures d'exercice A-020/A-021, isolation `/ops/*` avec deux clients A-031).
8. Identifiants bac à sable pour MonCash, SMTP, Twilio, FCM ; démarrage de Docker pour l'E2E.

## État des processus lancés par l'audit

- API d'audit (HEAD, base `nexora_audit_api`, port 5199) : arrêtée le 2026-10-14 (processus 37716), port libéré vérifié.
