# 08 — Tests et résultats

> Toutes les exécutions ci-dessous ont réellement eu lieu le 2026-10-13 (Windows 11, .NET SDK 10.0.401, Node 24.18,
> PostgreSQL 18 local). Journaux bruts conservés dans le dossier temporaire de session de l'audit (`build-*.log`,
> `test-*.log`, `fe-*.log`, `api-run.log`). Aucune configuration n'a été modifiée et aucune dépendance installée pour
> faire passer un test.

## 1. Tests existants

| Suite | Fichiers | Méthodes de test | Cas exécutés (théories développées) |
|---|---|---|---|
| `Nexora.UnitTests` | 9 | 65 `[Fact]`/`[Theory]` | 88 |
| `Nexora.IntegrationTests` | 64 (dont la fixture et `TestDates`) | 496 | 496 |
| `Nexora.ApiTests` | 29 (dont `StepUpTestHelper`) | 109 | 194 |
| Vitest (frontend) | 62 fichiers `*.test.ts(x)` | — | 275 |
| Playwright E2E | 6 scénarios + accessibilité + formulaires | 16 `test()` | **non exécutés** |

## 2. Exécutions

| Commande | Résultat | Statut |
|---|---|---|
| `dotnet build backend/Nexora.sln` sur la **copie de travail** | exit 1 — 75 erreurs, toutes dans Nexora.ApiTests (référence `Nexora.Api` retirée, modification non commitée) | **ÉCHEC** (A-001) |
| `dotnet build Nexora.sln` sur une copie de **HEAD** (`git archive`) | 0 erreur, 0 avertissement | réussi |
| `dotnet test Nexora.UnitTests` (HEAD) | 88/88 | **réussi** |
| `dotnet test Nexora.IntegrationTests` (HEAD), lancé en parallèle d'autres compilations | 496 échecs en 1 ms : délai de 30 s dépassé sur `CREATE DATABASE … TEMPLATE nexora` | bloqué par l'environnement (charge machine) |
| `dotnet test Nexora.IntegrationTests` (HEAD), relancé seul | 496/496 en 10 min 15 s | **réussi** |
| `dotnet test Nexora.ApiTests` (HEAD) | 190/194 | **4 échecs** (§3) |
| `npm run typecheck` | exit 0 | réussi |
| `npm run lint` | exit 0 | réussi |
| `npx vitest run` en parallèle des compilations | 49 fichiers / 213 tests réussis, 13 fichiers non démarrés | bloqué par l'environnement |
| `npx vitest run` relancé seul (2026-10-13) | **62/62 fichiers, 275/275 tests** en 109 s | **réussi** |
| Playwright E2E | non lancé : exige une installation neuve conteneurisée (`docker-compose.e2e.yml`, port 8090) ; Docker arrêté ; le jeu crée des données | bloqué par l'environnement |

## 3. Échecs des tests d'API

| Test | Cause | Imputable au produit ? |
|---|---|---|
| `SessionSecurityEndpointsTests.Refresh_rotates…` | Le test modifie la base par une connexion administrateur redirigée par l'audit vers le clone, alors que l'API de test écrit dans `nexora` (T-002) | Non : artefact de l'audit, mais révèle T-002 |
| `SessionSecurityEndpointsTests.Logout_idle_and_maximum_lifetime…` | Idem | Non (artefact), révèle T-002 |
| `SmokeTests.Golden_path…` | `TaskCanceledException` après 3 min 53 s sous charge | Probablement environnement ; à rejouer |
| `OpsMaintenanceEndpointsTests.Read_only_ops_endpoints…` | `GET /api/v1/ops/database` → 500 pendant le passage ; non reproduit au repos | Non déterminé |

**Incident d'audit** : la chaîne de connexion déclarée par les tests d'API n'est jamais appliquée (T-002). Le passage a
donc écrit dans la base de développement `nexora` (≈ 626 lignes : journal d'audit, sessions, jetons de rafraîchissement,
exports, imports…), comme le fait toute exécution normale de ce projet de tests. Aucune base de production touchée,
aucun nettoyage effectué (`audit_log` est immuable ; décision laissée au propriétaire).

## 4. Scénarios exécutés par l'audit (API de HEAD, base clonée `nexora_audit_api`)

| Scénario | Script | Résultat | Verdict |
|---|---|---|---|
| Jeton step-up utilisé dans une autre session | `nx.py` | 403 en session B, accepté en session A | FONCTIONNEL VÉRIFIÉ |
| `X-Company-Id` d'une autre entreprise / invalide | `nx.py` | 403 / 400 | FONCTIONNEL VÉRIFIÉ |
| Vente espèces 75 + retour espèces + clôture comptée 0 | `s1_refund.py` | attendu 75, écart −75, 5711 crédité deux fois, charge 6580 de 75 | **A-002 confirmé** |
| Vente directe dans une session close, `soldAt` antérieur | `s2_closed_session.py` | 200, VTE2026-000049 dans la session close | **A-003 confirmé** |
| Vente antidatée au 2026-09-01 | `s2_closed_session.py` | refus `sale.no_price` : la date client est appliquée | A-003 (confirme la prise en compte) |
| Coupon fixe 50 sur 2 puis 3 lignes | `s3_coupon.py` | remise 100 puis 150 | **A-012 confirmé** |
| 10 ventes espèces simultanées | `s4_concurrency.py` | solde cohérent (sérialisées par le verrou de numérotation) | non reproduit sur ce chemin |
| 10 ventes + 10 transferts simultanés | `s4b_mixed.py` | 525 HTG perdus | **A-018 confirmé** |
| Inventaire ouvert puis 2 ventes | `s5_count.py` | figé 411, courant 409 ; validation non exécutée | A-007 partiellement confirmé |
| Variantes de chemin `/metrics` (GET) | `curl` | 200 sans authentification | A-024 (côté API) |

Arrêt : la validation de l'inventaire (S5) a été refusée par le contrôle automatique des permissions de l'outil
(« modification d'une ressource partagée »). Depuis, aucun scénario n'a écrit en base.

## 5. Tests manquants (à écrire avant de considérer l'application fiable)

| Domaine | Test | Anomalie couverte |
|---|---|---|
| Caisse | vente + retour espèces + clôture → écart 0, trésorerie = fond | A-002 |
| Caisse | vente directe dans une session close → refus | A-003 |
| Caisse | prélèvement/apport de caisse → écriture et transaction | A-019 |
| Promotions | coupon fixe sur panier multi-lignes | A-012 |
| Prix | vente à un client doté d'une liste de prix | A-013 |
| Stock | ouverture d'inventaire → vente → validation → stock = compté ; produit suivi par lot | A-007 |
| Trésorerie | N opérations concurrentes sur un même compte → solde = ouverture + Σ transactions | A-018 |
| Comptabilité | deux exercices clôturés ; bilan identique avant/après à-nouveaux | A-020, A-021 |
| Rapports | `FilterPeriod.Resolve` pour chaque jour de la semaine | A-008 |
| Isolation | deux entreprises de groupes différents : `/ops/*`, corrections de données, maintenance | A-031 |
| Isolation | réinitialisation MFA et opérations en portée système avec le rôle réel `nexora_app` | A-009, T-001 |
| Rôles | suspension/retrait d'un compte plus privilégié | A-010 |
| Sessions | révocation d'une session étrangère → jetons de rafraîchissement intacts | A-032 |
| Tests d'API | isolation réelle de la base visée | T-002 |
| Fournisseurs réels | MonCash bac à sable, SMTP, Twilio, FCM | NON VÉRIFIABLE aujourd'hui |
| Charge | test de charge (aucun n'existe) | — |
| E2E | suite Playwright sur installation neuve | — |

## 6. Scénarios non vérifiés

Installation neuve par l'assistant ; restauration d'une sauvegarde ; flux d'achat complet de bout en bout à l'exécution ;
paie complète à l'exécution ; caisse hors ligne dans un navigateur ; consolidation de groupe ; partage public GED ;
fournisseurs de paiement et de notification réels ; comportement de Caddy en production.
