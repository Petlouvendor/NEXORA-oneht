# Finalisation produit — Phase 18 : Production readiness

> Plan maître « NEXORA ONE — Finalisation complète du produit », phase 18 (§39 Production readiness).
> Branche `feature/production-readiness`. Date : 2026-10-06.

Exigences (§39) : sondes de santé, readiness, liveness, logs structurés, OpenTelemetry, métriques, traces, monitoring,
backup, restore, runbook, configuration production, gestion des secrets, HTTPS, Docker, migrations, CI/CD.
S'y ajoutent les défauts reportés en phase 18 : refus métier journalisés en « 500 » avec pile d'appels et en-tête
`X-Company-Id` absent → 500 (phase 1), ainsi que les 11 boîtes natives de l'écran Exploitation (phase 10).

## 1. Travail effectué

**État trouvé** :
- **l'image Docker de l'API n'avait jamais pu être construite** : `adduser` est absent de l'image .NET 10 ;
- sa sonde de santé appelait `wget`, absent lui aussi ;
- aucun moyen d'appliquer les migrations d'une nouvelle version sur une base installée : il fallait `psql` et le dépôt ;
- sur une installation par conteneurs, **aucun moyen de créer la première entreprise** (l'assistant ne s'ouvre que
  sans base configurée) ;
- le rôle applicatif `nexora_app` garde le mot de passe écrit dans la migration V020 si personne ne le change ;
- la checklist de mise en production déclarait les migrations « validées » dès qu'il y en avait au moins 50, et ne
  contrôlait que la clé JWT parmi les secrets ;
- rien ne reconnaissait le proxy : derrière lui, une seule limite de connexions pour tous les utilisateurs, et un
  cookie de session non sécurisé ;
- `/api/v1/metrics`, anonyme, aurait été public dès la mise derrière un proxy ;
- ni OpenTelemetry, ni image de l'interface, ni HTTPS, ni restauration outillée, ni runbook ;
- refus métier journalisés en erreurs 500, et 134 « entreprise active absente » → 500 ;
- **deux migrations modifiées après leur application** sur les bases du poste (V084, V074), sans que rien ne le
  détecte.

**Réalisé** :

| Volet | Contenu |
|---|---|
| Sondes | `/health/live` (processus), `/health/ready` (base + schéma conforme), réponse JSON par contrôle, sans secret ni message d'exception |
| Journaux | JSON hors développement, un événement par requête au statut réel, avec corrélation, trace, utilisateur, entreprise, adresse du client et code métier ; sondes exclues |
| OpenTelemetry | traces (HTTP entrant et sortant, PostgreSQL, tâches planifiées) et métriques (ASP.NET Core, runtime, Npgsql, métriques métier `nexora.*`) en OTLP, activées seulement si un collecteur est configuré |
| Monitoring | collecteur, Prometheus, Jaeger ; 8 règles d'alerte sur les noms réels des métriques |
| Migrations | moteur partagé : verrou consultatif, empreintes contrôlées (fins de ligne tolérées), écarts détectés (en attente, modifiée, base plus récente) ; commandes `migrate`, `migrate --status`, `migrate --realign` |
| Premier démarrage | `bootstrap` : entreprise et administrateur, mêmes validations et même transaction que l'assistant |
| Secrets | fichiers montés dans `/run/secrets`, lus un par clé ; contrôle partagé par le démarrage, la checklist et l'écran Production ; mot de passe par défaut du rôle applicatif refusé |
| HTTPS | Caddy : certificat automatique, HSTS, CSP de l'interface, redirection HTTP ; l'API reconnaît le proxy déclaré |
| Docker | 3 images (API, interface, sauvegarde) ; composition de production ; générateur de secrets |
| Sauvegarde / restauration | sauvegarde quotidienne avec rétention ; `restore.sh` (empreinte, base neuve, contrôles, mode exercice, inscription au registre de continuité) |
| CI/CD | CI : contrôle des migrations embarquées, des images, de la composition et des alertes ; publication des images sur tag ; déploiement manuel approuvé |
| Exploitation | onglet « Production » ; checklist rendue réelle (migrations, secrets, HTTPS, proxy, observabilité, journaux) ; 11 boîtes natives remplacées |
| Runbook | `docs/exploitation/runbook-production.md` |

## 2. Fichiers créés

- Backend :
  - `Domain/Common/TenantErrors.cs`, `Application/Observability/NexoraMetrics.cs` ;
  - `Infrastructure/Setup/MigrationRunner.cs`, `MigrationStatusProbe.cs`, `Infrastructure/Operations/ProductionConfiguration.cs` ;
  - `Api/Observability/ObservabilityExtensions.cs`, `Api/HealthChecks/MigrationsHealthCheck.cs`,
    `HealthResponseWriter.cs`, `Api/Setup/CliCommands.cs`.
- Infrastructure :
  - `infrastructure/docker-compose.prod.yml`, `production.env.example`, `caddy/Caddyfile` ;
  - `observability/otel-collector.yaml`, `prometheus.yml`, `alerts.yml` ;
  - `backup/Dockerfile`, `scripts/generate-secrets.sh` ;
  - `frontend/nexora-web/Dockerfile`.
- Base : `database/scripts/restore.sh`, `backup-loop.sh`. CI : `.github/workflows/release.yml`.
- Frontend : `operations/ProductionSection.tsx`, `operations/productionSection.test.tsx`.
- Tests : `UnitTests/Operations/ProductionConfigurationTests.cs`, `IntegrationTests/ProductionReadinessTests.cs`,
  `ApiTests/ProductionReadinessEndpointsTests.cs`.
- Docs : `docs/exploitation/runbook-production.md`, ce document.

## 3. Fichiers modifiés

- `Program.cs` : commandes, secrets en fichiers, observabilité, proxy, sondes, ordre des middlewares.
- `ExceptionHandlingMiddleware` : 400, code métier, client parti, réponse déjà commencée.
- `Result.cs` (`ErrorKind.BadRequest`) ; 94 fichiers de l'application et de l'infrastructure : 134 levées remplacées.
- `SetupInstaller` (moteur partagé, `BootstrapAsync`), `SqlOperationsService` (checklist réelle),
  `StartupSecretsGuard`, `OperationsController` (`/ops/production`), `LoginHandler`, `AuthController`,
  `SecurityController`, `SalesService`, `SchedulerRunner` (métriques et traces), `DependencyInjection`.
- `Directory.Packages.props` et `Nexora.Api.csproj` (OpenTelemetry 1.19, Npgsql.OpenTelemetry 10.0.3).
- `backend/Dockerfile`, `.dockerignore`, `.gitignore`, `.github/workflows/ci.yml`, `database/scripts/backup.sh`.
- Frontend : `operationsApi.ts`, `OperationsPage.tsx`, `MaintenanceSections.tsx`.
- Test `OperationsReadinessTests` : contrôle des migrations désormais réel.

## 4. APIs

| Point d'accès | Rôle |
|---|---|
| `GET /health/live` (nouveau), `/health`, `/api/v1/health` | vivacité |
| `GET /health/ready`, `/api/v1/ready` | disponibilité, JSON par contrôle (`database`, `migrations`) |
| `GET /api/v1/ops/production` (`operations.view`) | version, commit, démarrage, sondes, migrations, contrôles de configuration |
| Toute opération sans entreprise active | `400 tenant.company_required` (avant : 500) |
| `dotnet Nexora.Api.dll migrate [--status \| --realign Vxxx \| --allow-drift]` | migrations ; codes 0, 1, 2 (en attente), 3 (incohérente) |
| `dotnet Nexora.Api.dll bootstrap --company … --activity … --currency … --admin-name … --admin-email …` | premier démarrage |
| `dotnet Nexora.Api.dll healthcheck [url]` | sonde de l'image |

Aucune migration de base dans cette phase.

## 5. Défauts corrigés

1. Image API impossible à construire (`adduser` absent) → utilisateur non-root de l'image .NET.
2. Sonde Docker toujours en échec (`wget` absent) → commande `healthcheck`.
3. Aucune mise à jour du schéma possible en production → `migrate`, avec verrou et contrôle des empreintes.
4. Aucune première entreprise possible en installation par conteneurs → `bootstrap`.
5. Rôle applicatif au mot de passe public (V020) → mot de passe fourni par l'environnement, sinon connexion
   interdite ; mot de passe par défaut refusé au démarrage.
6. Checklist : « migrations validées » si au moins 50 ; seule la clé JWT parmi les secrets → contrôles réels.
7. Proxy non reconnu (limite de connexions commune, cookie non sécurisé) → en-têtes du proxy déclaré seulement.
8. `/api/v1/metrics` anonyme, qui aurait été public derrière le proxy → fermé au proxy, collecté en interne.
9. Refus métier journalisés en 500 avec pile d'appels, sans identifiant de corrélation → statut réel, code métier.
10. 134 « entreprise active absente » → 400 `tenant.company_required` au lieu de 500.
11. Migrations V084 et V074 modifiées après application → détectées. V084, idempotente, est réalignée par
    rejeu sur `nexora`, `nexora_recette` et `nexora_qa`. V074 reste à aligner sur `nexora` (voir §9).
12. Purge : erreur avalée (promesse rejetée sans message) → message affiché, aperçu avant confirmation.
13. Maintenance planifiée figée à « +24 h, 60 min » → vraie fenêtre de saisie (date, durée, impact, services,
    responsable, plan de retour arrière).
14. 11 boîtes natives (`prompt`, `confirm`) de l'écran Exploitation → fenêtres de l'application avec validation.
15. Alerte de sécurité muette au premier événement : un compteur qui naît à 1 donne `increase() = 0` → règles
    « nouvelle série ou augmentation ».

## 6. Tests ajoutés

- Unitaires (+8) : configuration complète conforme, mot de passe par défaut, clé de données, aucun secret dans les
  verdicts, HTTPS production/préproduction, proxy, collecteur et journaux, erreur 400.
- Intégration (+5) : migrations toutes appliquées et verrou relâché ; migration modifiée qui bloque `migrate` puis
  réalignement ; réalignement refusé ; base plus récente détectée ; `bootstrap` (validations, base initialisée).
- API (+4) : sondes JSON sans secret ; 400 sans entreprise ; vue Production ; commandes (`migrate --status`, échecs
  explicites).
- Frontend (+3) : lecture de `/ops/production`, affichage trié, aucune boîte native dans l'écran Exploitation.

## 7. Tests exécutés

| Suite | Résultat |
|---|---|
| Unitaires backend | 58 / 58 |
| Intégration backend | 484 / 484 |
| API backend | 194 / 194 |
| Frontend Vitest | 267 / 267 (58 fichiers), types et build de production réussis |
| Pile de production Docker (Caddy, API, PostgreSQL, sauvegarde, collecteur, Prometheus, Jaeger) | `migrate` (94 migrations), `bootstrap` (puis sans effet si relancé), API saine, cookie `secure; httponly; samesite=strict` derrière le proxy, adresse réelle du client, sondes et métriques en 404 depuis Internet, HSTS et CSP |
| Observabilité | traces de l'API, de PostgreSQL et des tâches dans Jaeger ; métriques dans Prometheus ; 8 règles valides ; **alerte « session compromise » déclenchée de bout en bout** par le rejeu d'un jeton volé |
| Sauvegarde et restauration | conteneur : sauvegarde de 1,9 Mo enregistrée au registre, puis restaurée en 15 s, **vérifiée et inscrite au registre** ; recette : 6,5 Mo, 5 116 objets métier restaurés, comptabilité équilibrée, 11 s |
| Navigateur, sur la pile en HTTPS | connexion ; onglet Production ; maintenance, bascule et correction avec validation ; 0 boîte native, 0 violation CSP, 0 réponse 5xx, aucun débordement sur téléphone |

## 8. Résultats

Nexora One s'installe, se met à jour, se surveille, se sauvegarde et se restaure avec des outils livrés et vérifiés,
derrière HTTPS, sans secret dans le code ni dans les images. L'écran Exploitation montre ce qui tourne réellement.

## 9. Problèmes rencontrés

- **V074 sur la base de développement `nexora`** : appliquée le 23/09 dans une version antérieure du fichier. Le
  schéma complet et les permissions ont été comparés avec `nexora_recette` et sont identiques ; seule l'empreinte
  enregistrée diffère. Sa correction directe a été refusée par le contrôle des permissions (base partagée). Elle
  reste à faire par l'équipe, voir la commande dans le rapport ; d'ici là, la sonde de cette base est « dégradée »
  (HTTP 200).
- Le service de sauvegarde partait avant la fin des migrations (sauvegarde de 885 octets, base vide) → il attend
  désormais `migrate`. Ce fichier reste dans `infrastructure/backups/` (local, hors Git) ; la rétention le purgera.
- Prometheus remplaçait l'étiquette `job` de l'application par celle du collecteur → `honor_labels`.
  L'étiquette des tâches planifiées s'appelle `task`, `job` étant réservé.
- Le `psql` Windows annonçait l'encodage de la console → `PGCLIENTENCODING=UTF8` dans les scripts.
- `openssl` sous Windows termine ses sorties par `\r` → retiré des clés générées.
- Npgsql cherchait `libgssapi_krb5` → `GSS Encryption Mode=Disable` dans les chaînes de connexion générées.

## 10. Dette technique restante

- Acheminement des alertes vers l'astreinte (Alertmanager) : propre à chaque entreprise, non fourni.
- Rotation des clés MFA et données : nécessite un re-chiffrement, non outillé.
- Déploiement continu testé jusqu'à la publication des images. Le déploiement SSH n'a jamais été exécuté (aucun
  serveur cible) ; ses secrets sont à créer dans l'environnement GitHub « production ».
- Le service worker ne s'enregistre pas avec le certificat local de test (normal) ; à vérifier sur le vrai domaine.
- Les journaux restent sur la sortie standard : la conservation longue dépend de l'outil de collecte de l'entreprise.
- 40 boîtes natives restent dans 5 fichiers (BI, continuité, intelligence, plateforme, facturation) → phase 20.

## 11. Prochaine phase recommandée

Phase 19 — E2E global.
