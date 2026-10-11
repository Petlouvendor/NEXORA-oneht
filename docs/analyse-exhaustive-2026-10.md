# Nexora One — analyse exhaustive de l'application

> Date : 2026-10-09. Branche analysée : `develop` (commit `2ab19e5`, fin du plan maître, phases 0 à 20).
> Pour la direction et l'équipe technique. Complète le rapport final
> (`docs/NEXORA-ONE-FINAL-READINESS-REPORT.md`) par des contrôles mesurés le jour même : base de données, sécurité,
> dépendances, qualité du code, tests, exploitation et dépôt.

## 1. Synthèse

L'application est **fonctionnellement riche, bien architecturée et largement testée** : aucune dépendance de
production n'est vulnérable, l'isolation entre entreprises repose sur la base de données elle-même, et chacune des
1 028 actions de l'API est autorisée explicitement ou volontairement ouverte.

L'analyse révèle cependant des points **absents du rapport final**, dont deux à lever avant toute mise en service :

| # | Gravité | Constat | Section |
|---|---|---|---|
| 1 | **Critique** | Le code n'existe qu'en un seul exemplaire : aucun dépôt distant, `main` ne contient que le commit initial du 2026-09-27, la CI n'a jamais tourné | §10.1 |
| 2 | **Élevée** | Dates métier mélangées UTC / heure d'Haïti : après 20 h (19 h en hiver), ventes, encaissements, règlements et écarts d'inventaire sont datés du lendemain, alors que les pièces saisies à l'écran gardent la date du jour. Reproduit ce soir par l'E2E : écart de 15 000 HTG entre dettes fournisseurs et compte 4011 | §5.5 |
| 3 | Élevée | 102 permissions proposées aux administrateurs ne sont vérifiées nulle part (ex. `purchasing.approve`, `pos.reprint`) | §6.2 |
| 4 | Élevée | Réceptions non facturées sans écriture (compte 4081) — décision toujours attendue | §11 |
| 5 | Moyenne | Les formulaires en fenêtre se ferment avant la réponse du serveur : un refus fait perdre la saisie (24 formulaires) ; un test E2E en est devenu instable | §8.5 |
| 6 | Moyenne | Partage GED public : mot de passe haché sans sel, comparé en temps variable, transmis dans l'URL, sans limite de tentatives | §6.4 |
| 7 | Moyenne | Correctifs de sécurité .NET en retard (10.0.4 utilisé, 10.0.12 disponible) ; outils de test frontend vulnérables (hors production) | §6.6 |
| 8 | Moyenne | 690 clés étrangères sans index couvrant ; aucune purge des journaux | §5.3, §5.4 |
| 9 | Moyenne | Pas d'ESLint : 13 directives `eslint-disable` ne désactivent rien, aucune règle des hooks React n'est contrôlée | §7 |
| 10 | Faible | Interface uniquement en français ; le choix de langue ne change que le format des nombres et des dates | §8.3 |
| 11 | Faible | 2 tables d'entreprise sans RLS (`export_log`, `access_scope`), protégées seulement par le filtre des requêtes | §5.2 |

Les quatre conditions de mise en production pilote du rapport final restent valables ; les constats n°1 et n°2 s'y
ajoutent, le n°1 **avant toute autre chose**.

## 2. Périmètre et méthode

Analyse statique et dynamique du dépôt et de la base de développement `nexora` (PostgreSQL 18) :
- base : catalogue PostgreSQL (tables, RLS, clés, index, déclencheurs, rôles) ;
- sécurité : parcours automatique de chaque action des 75 contrôleurs, revue des points d'accès anonymes, recherche de
  secrets dans les fichiers suivis par Git, `dotnet list package --vulnerable`, `npm audit` ;
- qualité : balayage du code (exceptions avalées, `any`, directives de lint, SQL dynamique, fichiers les plus longs) ;
- tests : exécution complète des suites unitaires, intégration, API, interface et E2E ;
- exploitation : dépôt Git, CI, images Docker, configuration HTTP.

## 3. Volumétrie

| Élément | Mesure |
|---|---|
| Backend C# (hors tests) | 72 451 lignes : Infrastructure 46 853, Application 10 654, Api 9 020, Contracts 4 357, Domain 1 567 |
| Tests backend | 21 469 lignes (unitaires 955, intégration 17 332, API 3 182) |
| Migrations SQL | 94 fichiers (V001 à V094), 14 654 lignes |
| Frontend (TS/TSX hors tests) | 37 512 lignes, 37 écrans, 61 fichiers de tests, 8 scénarios E2E |
| API | 75 contrôleurs, 1 028 actions |
| Base | 291 tables, 77 fonctions, 890 clés étrangères, 316 permissions, 47 Mo en développement |
| Documentation | 128 documents Markdown |

## 4. Architecture

Architecture propre et cohérente sur tout le périmètre : Domain → Application → Infrastructure → Api, contrats
partagés, SQL paramétré (aucun `FromSqlRaw`/`ExecuteSqlRaw`/`SqlQueryRaw` : 0 occurrence), moteur financier unique
en partie double, écrivain unique des mouvements de stock. Le frontend est découpé par domaine, avec écrans chargés à
la demande.

**Correction du rapport final (§8.3)** : les tâches planifiées tournent bien dans le processus de l'API, mais elles
sont déjà coordonnées par un verrou consultatif PostgreSQL (`PostgresAdvisoryLock`, `BackgroundScheduler`). Plusieurs
instances de l'API n'exécutent donc pas deux fois la même tâche ; le passage à plusieurs instances reste à tester, mais
ce point n'est pas bloquant.

Fichiers les plus longs (à découper à l'occasion, pas en urgence) : `SqlEmployeeService.cs` (857 lignes),
`SqlCustomerAccountQueries.cs` (826), `SqlGeneralAccounting.cs` (799) ; côté interface `ProductForms.tsx` (857),
`CustomerAccountPage.tsx` (856), `HrDialogs.tsx` (837), `PosPage.tsx` (773).

## 5. Base de données

### 5.1 Structure
- 291 tables, **toutes avec clé primaire** ; 189 tables auditées par le déclencheur `audit_row()` ; journal d'audit
  immuable.
- Le rôle applicatif `nexora_app` n'a pas `BYPASSRLS` : l'isolation ne dépend pas du code.
- Schéma de développement conforme aux migrations (`migrate --status` vérifié sur `nexora` et `nexora_recette`).

### 5.2 Isolation entre entreprises
249 tables ont la RLS active. Six tables portent un `company_id` sans RLS :

| Table | Statut |
|---|---|
| `feature_flag`, `platform_customer_company`, `support_ticket`, `support_access_grant` | Tables de la plateforme (éditeur), sans RLS par conception (V051, V054) |
| `access_scope` | Périmètres d'affectation des rôles ; lue par les services d'identité avec filtre explicite |
| `export_log` | Journal des impressions et exports ; toutes les lectures filtrent par entreprise (`SqlPrintService`, `ExportAndAsk`) |

Aucune fuite constatée. Pour `access_scope` et `export_log`, la protection repose sur le filtre de chaque requête et non
sur la base : **activer la RLS** (`nexora.enable_company_rls`) rétablirait la défense en profondeur. `export_log` est
la plus exposée des deux, car une requête future sans filtre afficherait l'historique d'exports des autres entreprises.

### 5.3 Index
**690 clés étrangères sur 890 n'ont aucun index commençant par leurs colonnes.** Sans effet aujourd'hui (volumes de
développement), mais deux conséquences en production :
- toute jointure « parent → enfants » non couverte par un autre index devient un parcours complet de la table enfant ;
- la vérification d'intégrité lors d'une mise à jour de la clé du parent parcourt la table enfant.

Beaucoup concernent des colonnes peu interrogées (`closed_by`, `revoked_by`, `rotated_from`). Recommandation : activer
`pg_stat_statements` en recette avec un volume réaliste, puis indexer les clés réellement utilisées dans les jointures
(lignes de documents, mouvements, versements, paie), plutôt que les 690.

### 5.4 Croissance
Plus grosses tables en développement : `audit_log` (10 397 lignes), `user_session` (6 429), `role_permission`
(1 539), `auth_refresh_token` (842). Aucune purge ni partition n'est prévue pour `audit_log`, `user_session` et
`auth_refresh_token`. À traiter avant la première année d'exploitation (partition mensuelle d'`audit_log`, purge des
sessions et jetons expirés).

### 5.5 Date du jour : UTC ou heure d'Haïti — défaut de fond
Aucune table ne porte le fuseau horaire de l'entreprise. La « date du jour » d'une opération est calculée de trois
façons différentes :

| Source | Fuseau | Occurrences |
|---|---|---|
| Code C# : `DateOnly.FromDateTime(…UtcNow)` | UTC | 49 |
| SQL : `current_date` (requêtes du code et fonctions des migrations) | Fuseau du serveur PostgreSQL, `America/Port-au-Prince` en production (`NEXORA_TIMEZONE`) | 27 + 18 |
| Interface : date saisie, `todayIso()` | Heure du poste | — |

Entre 20 h et minuit (19 h et minuit en heure d'hiver), l'UTC est déjà au lendemain. Constat sur la base E2E, ce soir
à 20 h 39 heure d'Haïti :

| Pièce | Date comptable |
|---|---|
| Facture fournisseur (date saisie à l'écran) | 2026-10-09 |
| Règlement de cette facture | **2026-10-10** |
| Vente de caisse, encaissements clients, écart d'inventaire | **2026-10-10** |

Conséquences : la balance du 9 au soir montre une dette fournisseur de 15 000 HTG déjà réglée (le test de cohérence
E2E échoue) ; le chiffre d'affaires de la soirée part dans la journée suivante (rapports journaliers, clôture de caisse) ;
**le dernier soir du mois, les ventes après 20 h tombent dans le mois suivant** (et sont refusées si cette période
n'est pas ouverte) ; trois tests d'intégration échouent tous les soirs pour cette raison (§9).

Correction proposée : un fuseau horaire par entreprise (`company.time_zone`, Haïti par défaut), un service unique
« date métier du jour » utilisé par le C# et le SQL, la suppression des `current_date` et `DateOnly.FromDateTime(UtcNow)`
au profit de ce service, et des tests qui figent l'horloge à 22 h heure locale.

## 6. Sécurité

### 6.1 Autorisation des actions de l'API
Parcours automatique des 1 028 actions :

| Catégorie | Actions | Appréciation |
|---|---|---|
| Permission exigée (`[RequirePermission]`) | 954 | Conforme |
| Authentification seule (sans permission) | 57 | Conforme : données de l'utilisateur lui-même (profil, préférences, notifications, sessions, double authentification), recherche globale et fil d'activité filtrés par permission dans le service, impressions contrôlées par type de document (`PrintAccess`) |
| Anonymes (`[AllowAnonymous]`) | 17 | Conformes par nature : connexion, rafraîchissement, mot de passe oublié, webhooks signés, vérification publique de document et de licence, assistant d'installation, partage GED par jeton, métriques |

La politique par défaut refuse tout utilisateur non authentifié (`FallbackPolicy`). `/api/v1/metrics`, anonyme côté
API, est bloqué par Caddy en production.

### 6.2 Permissions sans effet
**102 des 316 permissions en base ne sont citées nulle part dans le code** : elles peuvent être attribuées à un rôle
dans l'administration sans rien autoriser ni interdire. Ce sont surtout les déclinaisons génériques
création/modification/suppression/export créées par les premières migrations (`crm.*`, `analytics.*`, `cash.*`,
`payments.*`, `synchronization.*`…), mais aussi des noms trompeurs :
- `purchasing.approve` — l'approbation des achats est contrôlée par une autre permission ;
- `pos.reprint` — la réimpression d'un reçu n'est pas soumise à cette permission ;
- `sales.export`, `inventory.export`, `audit.export` — les exports passent par `reporting.export`.

Risque : un administrateur croit restreindre une action (« ce caissier n'a pas `pos.reprint` ») alors qu'il ne la
restreint pas. Recommandation : migration qui retire les permissions jamais vérifiées (après contrôle des rôles qui les
portent), ou les branche quand l'intention métier est claire (`pos.reprint`).

### 6.3 Authentification et sessions
Jetons d'accès de 15 minutes, cookie de rafraîchissement HttpOnly avec rotation et détection de vol, délai
d'inactivité, révocation des sessions et appareils, double authentification TOTP avec secret chiffré (AES-GCM),
41 actions à confirmation forte, limitation de débit sur la connexion (10/min/adresse) et le rafraîchissement
(60/min). Le jeton est conservé en `sessionStorage` ou dans le coffre chiffré du navigateur, jamais en `localStorage`
(l'ancienne clé est migrée puis effacée).

### 6.4 Partage public de documents (GED)
`GET /ged-documents/shared/{token}?password=…` :
- le jeton (192 bits aléatoires) est solide ;
- le mot de passe facultatif est **haché en SHA-256 sans sel** (`GedService.HashPassword`) et comparé par `!=` ;
- il est **transmis dans l'adresse**, donc conservé dans l'historique du navigateur et les journaux des proxys ;
- le point d'accès n'a **pas de limitation de débit**.

Gravité moyenne : il faut d'abord détenir le jeton. Correction proposée : mot de passe dans un en-tête ou un formulaire
POST, hachage BCrypt (déjà utilisé pour les comptes), comparaison en temps constant, politique de débit `auth`.

Les autres points d'accès anonymes sans limitation de débit (`/documents/verify/{code}`, `/licenses/verify`,
assistant d'installation) sont à placer sous une politique de débit dédiée.

### 6.5 Requêtes sortantes (SSRF)
Déjà signalé par le rapport final et confirmé : aucune vérification des adresses de webhooks sortants (adresses
internes, boucle locale, métadonnées du nuage). À corriger avant d'ouvrir la marketplace à des tiers.

### 6.6 Dépendances
| Écosystème | Résultat |
|---|---|
| NuGet (8 projets, dépendances transitives incluses) | **0 vulnérabilité connue** |
| npm, dépendances de production | **0 vulnérabilité** |
| npm, outils de développement | 3 avis : `tinypool` (critique, via vitest), `source-map-js` (élevée), `@vitest/mocker` (modérée). Hors du paquet livré, mais exécutés sur les postes et en CI |

Retards notables : .NET et EF Core 10.0.4 alors que 10.0.12 est publié (correctifs de sécurité mensuels de Microsoft) ;
images Docker `dotnet/aspnet:10.0` et `caddy:2-alpine` non figées par empreinte ; vitest 3 (5 disponible), vite 6 (8
disponible), Swashbuckle 9, Twilio 7, FirebaseAdmin 3.1 (3.7 disponible).

### 6.7 Secrets
Aucun secret dans les fichiers suivis par Git : `production.env`, `infrastructure/secrets/` et
`infrastructure/backups/` sont ignorés ; la clé de signature de `appsettings.json` est vide (refus de démarrer sans
elle) et celle de développement est explicitement marquée comme non sûre. Le mot de passe PostgreSQL de développement
figure en valeur par défaut dans trois fichiers de tests (sans risque en production, à remplacer par une variable
obligatoire en CI).

## 7. Qualité du code

| Contrôle | Résultat |
|---|---|
| `TODO` / `FIXME` / `HACK` | 0 |
| `any` en TypeScript (hors tests) | 0 |
| Erreurs de typage (`tsc --noEmit`) | 0 |
| SQL construit par concaténation | 0 |
| `dangerouslySetInnerHTML` | 0 |
| Boîtes natives (`prompt`/`alert`/`confirm`) | 0, interdites par un test |
| `catch` vides en C# | 2, légitimes (fuseau horaire inconnu → repli) |
| Erreurs ignorées en TypeScript (`.catch(() => {})` ou équivalent) | 62 |
| Directives `eslint-disable` | 13, **sans effet : ESLint n'est pas installé** |
| `useAsync` recopié localement | 3 copies (BI, exploitation, intelligence) |

Les 62 erreurs ignorées sont pour la plupart volontaires (compteur de notifications, cache du profil, rafraîchissement
silencieux) ; certaines masquent un échec à l'utilisateur, par exemple `myInvoices().catch(() => [])` dans le portail de
facturation affiche « aucune facture » au lieu d'une erreur. À revoir au cas par cas.

L'absence d'ESLint est la vraie lacune : la règle `react-hooks/exhaustive-deps`, que le code croit désactiver à 13
endroits, n'a jamais été vérifiée ailleurs. Ajouter ESLint (`typescript-eslint`, `eslint-plugin-react-hooks`) au
script `npm run lint` et à la CI.

## 8. Interface

### 8.1 Performance
Paquet de démarrage : 480 Ko (non compressé), puis un fichier par écran (le plus lourd : RH 77 Ko, catalogue 73 Ko,
achats 71 Ko). Réponses de l'API mesurées sous 400 ms en phase 20. Aucun test de charge.

### 8.2 Accessibilité
Contraste AA dans les deux thèmes, cibles tactiles de 44 px, tableaux triables annoncés aux lecteurs d'écran, audit
axe-core automatisé dans la suite E2E.

### 8.3 Langue
L'interface est **entièrement en français**. Le module de traduction (`shared/i18n.ts`, 50 lignes) n'est utilisé que
par un fichier ; la préférence de langue ne change que le format des nombres et des dates. Pour une clientèle
haïtienne, le créole (et l'anglais pour certains groupes) supposerait d'extraire les textes de 37 écrans : chantier
important, à décider selon la cible commerciale.

### 8.4 Couverture
12 % des lignes de l'interface sont couvertes par des tests unitaires ; les écrans le sont surtout par les scénarios
E2E. Les modules restaurant, paie, groupe et terrain n'ont pas de scénario E2E.

### 8.5 Formulaires en fenêtre
`useFormDialog` (phase 20) rend les valeurs saisies et **se ferme avant l'appel au serveur** ; l'écran envoie la
requête ensuite. Si le serveur refuse (règle métier, droit, conflit), l'erreur s'affiche mais la saisie est perdue :
l'utilisateur doit tout retaper (post-mortem d'incident, budget, objectif…). 24 formulaires suivent ce modèle.
Ce décalage a aussi rendu instable le test E2E « formulaires » : il relit l'incident avant la fin de l'enregistrement
(l'incident est bien résolu en base). Correction proposée : une option `onSubmit` asynchrone qui garde la fenêtre
ouverte, bouton occupé, jusqu'à la réponse, et y affiche l'erreur ; côté test, attendre l'état avec `expect.poll`.

## 9. Tests du jour

| Suite | Résultat le 2026-10-09 |
|---|---|
| Unitaires backend | 59 / 59 |
| Intégration backend (PostgreSQL réel, RLS) | 481 / 484 — les 3 échecs sont les tests sensibles à l'heure, lancés après 20 h (§5.5) |
| API backend (HTTP réel) | 194 / 194 |
| Interface (Vitest) | 274 / 274 (61 fichiers) ; typage sans erreur |
| E2E Playwright | 13 réussis, 1 ignoré (stock/3110, §11), **2 échecs** : cohérence 4011 (date du soir, §5.5) et formulaire d'incident (lecture avant enregistrement, §8.5) |

Couverture mesurée en phase 20 : backend 83 % des lignes, interface 12 %.

## 10. Exploitation et dépôt

### 10.1 Dépôt Git — critique
- **Aucun dépôt distant** (`git remote` vide) : l'historique complet n'existe que dans le dossier OneDrive de ce poste.
  Un dépôt Git synchronisé par OneDrive peut se corrompre (fichiers verrouillés ou synchronisés à moitié), et une perte
  du poste ou du compte emporterait le code.
- **`main` ne contient que le commit initial** du 2026-09-27 ; `develop` a 64 commits d'avance. Aucune étiquette de
  version.
- Les workflows `.github/workflows/ci.yml` (base, backend, frontend, Docker, E2E) et `release.yml` **n'ont donc jamais
  été exécutés** : la CI décrite dans la documentation n'est pas vérifiée.

Actions : créer un dépôt privé (GitHub, GitLab ou Azure DevOps), pousser `main` et `develop`, fusionner `develop` dans
`main`, étiqueter la version, laisser la CI tourner une première fois et corriger ce qu'elle révèle.

### 10.2 Production
Outillé : migrations, installation initiale, sondes, journaux JSON, OpenTelemetry, Prometheus, Caddy en HTTPS, HSTS,
CSP, sauvegarde quotidienne vérifiée et restauration exercée. Restent (rapport final) : Alertmanager vers l'astreinte,
réplication hors site et sauvegarde continue (une journée de perte possible aujourd'hui), rotation des clés de
chiffrement, test de charge, test d'intrusion externe.

## 11. Fonctionnel

État inchangé depuis le rapport final, confirmé par l'analyse :
- **réceptions non facturées** : le stock augmente sans écriture, le compte 3110 est en retard sur la valeur du stock
  tant que la facture fournisseur n'est pas saisie (compte 4081 proposé, **décision attendue**) ;
- capacités sans écran : tickets de support, coupons, options et licences de la plateforme, vente en attente
  (« park »), export/clonage/historique de configuration, filtres enregistrés et dispositions du tableau de bord ;
- non disponibles : application mobile native (web mobile hors ligne à la place), WebAuthn et SSO, portail
  partenaires, assistant à modèle de langage.

## 12. Plan d'action

| Priorité | Action | Effort indicatif |
|---|---|---|
| 1 | Dépôt distant privé, fusion `develop` → `main`, étiquette, première exécution de la CI | 0,5 jour |
| 2 | Fuseau horaire de l'entreprise et service unique de « date métier du jour » (C# et SQL), tests à horloge figée le soir | 2 à 3 jours |
| 3 | Décision 4081, puis écriture à la réception et rapprochement facture | 2 à 3 jours |
| 4 | Retirer ou brancher les 102 permissions sans effet | 1 jour |
| 5 | Formulaires en fenêtre ouverts jusqu'à la réponse du serveur ; test E2E « formulaires » stabilisé | 1 jour |
| 6 | Partage GED : mot de passe hors URL, BCrypt, temps constant, limite de débit ; débit sur les autres points anonymes | 0,5 jour |
| 7 | Mise à jour .NET 10.0.12 et dépendances, images figées par empreinte ; vitest 5 | 1 jour |
| 8 | ESLint avec règles des hooks React, dans la CI | 1 jour |
| 9 | RLS sur `export_log` et `access_scope` | 0,5 jour |
| 10 | Protection SSRF des webhooks sortants | 1 jour |
| 11 | Index des clés étrangères utiles (après mesure `pg_stat_statements`), purge et partition des journaux | 2 jours |
| 12 | Conditions du rapport final : fournisseurs réels, Alertmanager, réplication, restauration et recette sur la cible, test de charge, test d'intrusion | selon infrastructure |
| 13 | Scénarios E2E restaurant, paie, groupe, terrain | 3 jours |
| 14 | Traduction créole/anglais, si la cible commerciale l'exige | 10 jours et plus |
