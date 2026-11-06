# 08 — Tests et résultats (édition 2)

> Exécutions réelles des 2026-10-16 et 2026-10-17 (Windows 11 Pro, .NET SDK 10.0.402, Node 24.18, PostgreSQL 18 local),
> HEAD `6a47c6d`. Journaux bruts dans le dossier temporaire de session de l'audit (`tests/*.log`, `scenarios-*.md`).
> Aucune configuration du dépôt modifiée, aucune dépendance installée. L'édition 1 (2026-10-13) est dans l'historique Git.

## 1. Tests existants

| Suite | Cas exécutés | Évolution depuis l'éd. 1 |
|---|---|---|
| `Nexora.UnitTests` | 103 | +15 |
| `Nexora.IntegrationTests` | 532 | +36 |
| `Nexora.ApiTests` | 200 | +6 |
| Vitest (63 fichiers) | 279 | +4 |
| Playwright E2E (6 scénarios + accessibilité + formulaires) | **non exécutés** | — |

## 2. Exécutions

| Commande | Résultat | Statut |
|---|---|---|
| `dotnet build Nexora.sln` | 0 erreur | **réussi** (A-001 corrigé) |
| `dotnet test tests/Nexora.UnitTests` | 103/103 | **réussi** |
| `dotnet test tests/Nexora.IntegrationTests` (2 passages) | 532 échecs en 1 ms : `CREATE DATABASE … TEMPLATE nexora` dépasse le délai Npgsql de 30 s (22,9 s mesurées à vide) | bloqué (T-003) |
| idem avec `NEXORA_IT_ADMIN="…;Command Timeout=600"` | **532/532** en 6 min 8 s | **réussi** |
| `dotnet test tests/Nexora.ApiTests` en parallèle d'autres suites | 153/200 (annulations, délais) | bloqué par la charge |
| idem seul, `NEXORA_API_TEST_DB=nexora_api_it` | **200/200** en 8 min 5 s | **réussi** (T-002 corrigé : la base déclarée est utilisée) |
| `npm run typecheck`, `npm run lint`, `npm run build` | exit 0 | **réussi** |
| `npx vitest run` sous charge | 216/216, 13 fichiers non démarrés | bloqué par la charge |
| idem seul | **63/63 fichiers, 279/279** | **réussi** |
| `dotnet list Nexora.sln package --vulnerable --include-transitive` | `System.Security.Cryptography.Pkcs` 10.0.0 (High + Moderate) via MimeKit 4.18.1 | **échec** (A-036 ; la CI est rouge) |
| `npm audit --omit=dev` | 0 vulnérabilité | réussi |
| Playwright E2E | non lancé (installation neuve conteneurisée nécessaire) | non exécuté |

Écritures en base dues à l'audit : bases jetables de la fixture d'intégration (créées et supprimées par elle) ;
`nexora_api_it` (base de test des tests d'API) ; `nexora_audit_probe` (mesure de clonage, supprimée) ;
`nexora_audit_ed2` (scénarios, supprimée). **Aucune écriture dans `nexora` ni `nexora_recette`.**

## 3. Scénarios exécutés par l'audit (API locale de HEAD, base jetable `nexora_audit_ed2`)

| Scénario | Résultat | Verdict |
|---|---|---|
| Webhook de facturation plateforme avec `X-Platform-Webhook-Token: dev-platform-webhook-token` | 200, paiement enregistré ; autre jeton → 401 | **A-049 confirmé** |
| Responsable stock : `/forecasts` puis `/intelligence/forecasts`, `/intelligence/recommendations` | 403 puis 200 (trésorerie, CA, créances nominatives) | **A-048 confirmé** |
| OD 1 000 (5711/7011) validée puis extournée, balance avant/après | 5711 : 123 255,90 → 124 255,90 → 122 255,90 | **A-043 confirmé** |
| Taux USD 132,5 ; OD USD 20,02 = 10,01 + 10,01 | brouillon accepté ; validation 409 « 2652.6500 <> 2652.6400 » | **A-041 confirmé** |
| Comptable : `POST /finance/expenses` 250 HTG | 200 ; dépense payée sans approbation ; caisse −250 | **A-045 confirmé** |
| Caissier : vente avec `taxId` d'une taxe 0 % sur un article à 10 % | 200 ; total 130 au lieu de 143 | **A-040 confirmé** |
| Transfert d'1 unité, 8 réceptions simultanées (deux essais) | 7 puis 8 unités reçues | **A-038 confirmé** |
| Appareil auto-enrôlé, `/sync` d'une vente datée dans une session close | `SYNCED`, vente ajoutée à la session close | **A-003 (résidu) confirmé** |
| Promotion −20 % + coupon −10 % : simulateur contre aperçu de caisse | 102,96 contre 100,10 | **A-039 confirmé** |
| `/sync` avec une date à décalage non UTC | 500 (Npgsql) | constat FAIBLE |

Constats par requêtes SELECT en lecture seule (`nexora_recette`) : A-052 (deux ventes en USD, coût 40 500 au lieu de
300), A-046 (trésorerie de la Pharmacie additionnant HTG et USD), A-042 (seul l'exercice 2026 existe), A-048 (droits
des rôles), A-009 résidu (tables sans RLS), écart stock / grand livre de la Pharmacie entièrement expliqué.

## 4. Ce que les tests existants ne couvrent pas (cause des anomalies non détectées)

| Domaine | Test manquant | Anomalie |
|---|---|---|
| Comptabilité | extourne puis balance / bilan / clôture | A-043 |
| Comptabilité | écriture au 1er jour d'un exercice non créé ; création d'exercice | A-042 |
| Multidevise | vente, retour, facture en USD à taux non entier (132,5) ; coût des ventes en devise | A-041, A-052 |
| Caisse | `taxId` fourni par le client | A-040 |
| Caisse | `/sync` d'une vente dans une session close, appareil auto-enrôlé | A-003 |
| Stock | réceptions / annulations / validations concurrentes | A-038 |
| Prix | simulateur et caisse sur promotion + coupon | A-039 |
| Autorisation | comptable sur `/finance/*` ; responsable stock sur `/intelligence/*` ; `manage_users` inter-entreprises | A-045, A-048, A-033 |
| Sécurité | démarrage en Production sans jeton de webhook | A-049 |
| Pilotage | KPI avec un compte en devise ; budget révisé | A-046, A-047 |
| Tests | délai de clonage de la fixture | T-003 |
| Fournisseurs réels, charge, E2E | MonCash, SMTP, Twilio, FCM ; test de charge ; Playwright | non vérifiés |

Observation : les taux de démonstration sont entiers (132, 135) — un montant en centimes multiplié par un taux entier
ne produit jamais de demi-centime, ce qui masque A-041 dans toute la suite.

## 5. Scénarios non vérifiés à l'exécution

Installation neuve complète (couverte seulement par `SetupWizardTests`) ; restauration réelle d'une sauvegarde ; suite
E2E ; caisse hors ligne dans un navigateur ; fournisseurs de paiement et de notification réels ; Caddy en production ;
test de charge ; test d'intrusion externe ; arrivée réelle au 2027-01-01 (A-042, non simulable sans changer l'horloge).
