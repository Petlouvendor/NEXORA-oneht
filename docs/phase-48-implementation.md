# Phase 48 — Résilience, Haute Disponibilité & Disaster Recovery — implémentation

**Statut : livré et vérifié.** Migration `V062` (1 table neuve `dr_test`, 1 colonne sur
`continuity_incident`, 1 CHECK étendu sur `backup`, RLS + audit). 4 nouveaux tests d'intégration
(`ContinuityRegisterTests`, total 12), 5 nouveaux tests API (`ResilienceEndpointsTests`, nouveau
fichier), 5 nouveaux tests frontend (`continuityApi.test.ts`, total 10).
**378 tests backend** verts (18 unit + 52 API + 308 intégration), 0 warning, vérifiés après un
rebuild complet de la base (`V001`→`V062`, **250 tables, 211 RLS, 62 migrations**). 110 tests
frontend. `tsc` vert.

**Constat préalable, déterminant pour le périmètre de cette phase :** le cahier des charges de la
Phase 48 (62 sections) redemande, section par section, un registre déjà livré à la Phase 36
(V049) — politique de sauvegarde (RPO/RTO, rétention, chiffrement, immutabilité), catalogue de
sauvegardes avec vérification, tests de restauration, restauration production à double validation,
incidents avec runbook générique 9 étapes et post-mortem obligatoire, état de reprise noté A-E — et
demande par ailleurs une quantité importante de véritable **infrastructure** (haute disponibilité,
réplication DB, load balancing, failover automatique, split-brain, autoscaling, déploiement
progressif) qui n'a pas d'équivalent applicatif à construire dans un monolithe .NET : elle se
décide au niveau de l'hébergement, pas du code métier. Plutôt que de reconstruire le socle Phase 36
ou de simuler une infrastructure qui n'existe pas, cette phase a fait l'inventaire précis de
l'existant (`docs/phase-36-implementation.md`) pour isoler le **delta réellement applicatif** :
des **runbooks nommés par scénario** (§37 — le registre n'avait que 9 étapes génériques, jamais de
contenu concret), un registre d'**exercices DR** au-delà du simple test de restauration (§44-45),
une action de **confinement réelle** pour un incident de sécurité (§32, §55 — verrouillage
d'urgence, pas seulement une intention documentée), et une **page de statut** composant les signaux
déjà mesurés par le Phase 41 plutôt qu'un second calcul (§7, §39).

### Socle réutilisé (Phase 36, non reconstruit)

- **`backup_policy`/`IContinuityService.GetPolicyAsync`/`SetPolicyAsync`** — RPO/RTO cibles (§8-9),
  rétention, destination, chiffrement, immutabilité (§12-16) — **étendu** seulement sur les types de
  sauvegarde acceptés (§11), jamais sur la logique
- **`backup`/`RegisterBackupAsync`/`VerifyBackupAsync`** — enregistrement par un agent externe,
  vérification (checksum, comptage d'objets, §17) — non touché
- **`restore_run`/`RunRestoreTestAsync`/`RequestProductionRestoreAsync`/`ApproveProductionRestoreAsync`**
  — test de restauration en isolation, restauration production à 2 personnes (§18, §39, `continuity.
  self_approval` inchangé) — non touché
- **`DisasterRecoveryStatusAsync`** — grade A-E, RPO/RTO observés vs cibles — non touché, **réutilisé
  tel quel** par la nouvelle page de statut plutôt que recalculé
- **`continuity_incident`/`OpenIncidentAsync`/`AdvanceIncidentAsync`/`ResolveIncidentAsync`** —
  sévérité p1-p4, runbook générique 9 étapes, post-mortem obligatoire à la clôture, domaine
  availability/security (Phase 47 §29-30) — **étendu** d'une seule colonne (`containment_actions`)
- **`RunHealthChecksAsync`** — alertes sauvegarde en retard / test de restauration en échec — non
  touché
- **Diagnostics Phase 41** (`IOpsDiagnosticsService.RunAllAsync`) et **mode maintenance Phase 41**
  (`IMaintenanceService.ListAsync`) — composés, jamais redérivés, par la nouvelle page de statut

---

## Correspondance cahier des charges → implémentation

| § | Exigence | Où |
|---|---|---|
| 1-2 | Objectif ; principe (service normal → incident → détection → protection → bascule/restauration → validation → reprise → post-mortem) | **Hérité** (Phase 36 §36) — le cycle existant (ouvrir → avancer le runbook → résoudre avec post-mortem) suit déjà exactement ce principe |
| 3-6 | Modèles de déploiement (local/cloud/hybride) ; schémas petite entreprise/hybride/cloud | **Hors périmètre applicatif** — décisions d'hébergement, pas de code métier ; le mode dégradé offline (§28, §40) est lui déjà couvert par le moteur de synchronisation Phase 10 |
| 7 | Criticité des services (critique/élevée/normale/faible) | **NEW** — `ComponentCriticality` classe les composants de la page de statut (`database`/`backup`=critical, `sync`/`storage`/`queue`=high, `connector`/`license`=normal, `email`/`sms`/`printer`/`clock`=low), réutilisant directement les clés de diagnostic Phase 41 |
| 8-9 | RPO / RTO | **Hérité** (Phase 36 §36, `BackupPolicyDto.RpoTargetMinutes`/`RtoTargetMinutes`, `DisasterRecoveryStatusDto.RpoBreached`) |
| 10 | Classes de continuité (essentielle/renforcée/HA/critique-multisite) | **Backlog** — pas de classe nommée distincte ; la politique encode déjà RPO/RTO/immutabilité par entreprise, une classe nommée ajouterait un niveau d'indirection sans nouvelle capacité |
| 11 | Types de backup (complet/incrémental/différentiel/base/documents/config/métadonnées/clés) | **`V062`** étend `backup_kind_chk` : `+ differential, + metadata, + keys` (complet/incrémental/config/documents/snapshot déjà acceptés Phase 36) |
| 12-17 | Règle 3-2-1 ; backup local/distant ; immutabilité ; chiffrement ; vérification | **Hérité intégralement** (Phase 36 §4-7, §14, §21) — `Destination`/`Encrypted`/`Immutable`/`ImmutableUntil`/`VerifyBackupAsync` |
| 18 | Restore (choix point → isolation → validation → production → health check → reprise) | **Hérité** (Phase 36 §18-21, §39) — le runbook `restore_procedure` (NEW, §37) documente désormais explicitement cette séquence avec les routes réelles |
| 19-27 | PITR ; réplication ; HA (app/DB/stockage) ; load balancing ; failover ; split-brain | **Infrastructure — backlog explicite**, comme déjà noté en Phase 36 : décision d'hébergement (réplication Postgres, load balancer, orchestrateur). Le runbook `failover_procedure` (NEW) documente la **procédure** générique (health check → décision auto/manuelle → bascule → validation → journalisation) sans construire l'infrastructure elle-même |
| 28 | Réseau indisponible | **Hérité** (Phase 10, moteur de synchronisation offline) — référencé explicitement par le nouveau runbook `network_loss` |
| 29-31 | Panne électrique locale ; panne serveur local ; catastrophe site | Runbook `server_failure` (NEW, §37) couvre 29-30 avec les routes réelles ; le scénario « site détruit » (§60) se recompose de `restore_procedure` + un site secondaire — infrastructure hors périmètre pour la partie « nouveau site » |
| 32 | Ransomware (isolation, blocage accès, préservation preuves, révocation credentials, restore, rotation secrets, post-incident) | **NEW** — runbook `ransomware` en 8 étapes reliant explicitement `POST /continuity/incidents/{id}/lockdown` (confinement réel, voir ci-dessous) + restauration à 2 personnes + rotation des clés d'API (Phase 35) |
| 33-35 | Erreur humaine ; corruption données ; incident fournisseur cloud | **NEW** — runbooks `human_error`, `data_corruption`, `cloud_provider_outage` (§37), chacun renvoyant vers l'audit immuable (Phase 11/47) ou la page de statut pour la communication |
| 36 | Plan de continuité (qui décide, quels services, quel mode, quelle restauration, qui valide, communication) | **Hérité** — les rôles se traduisent par les permissions existantes (`continuity.view/manage/restore`) ; « qui valide » = restauration à 2 personnes (Phase 36 §39) déjà en place |
| 37 | Runbooks (13 scénarios nommés) | **NEW — livraison principale de cette phase.** `ContinuityRunbookCatalog` (13 entrées, catalogue statique comme `IntelligenceModelCatalog`) : `db_outage`, `data_corruption`, `server_failure`, `network_loss`, `storage_failure`, `ransomware`, `human_error`, `cloud_provider_outage`, `certificate_expiry`, `payment_provider_down`, `restore_procedure`, `failover_procedure`, `return_to_normal` — correspondance 1:1 avec la liste du §37. Chaque étape référence une route réelle de l'API, jamais une généralité |
| 38 | Communication incident | **Partiellement couvert** — la page de statut (§39) expose l'état courant que la communication interne peut relayer ; un gabarit de message d'incident dédié n'est **pas** construit (backlog) |
| 39 | Status page (opérationnel/dégradé/incident/maintenance, historique, composants) | **NEW** — `StatusPageAsync`/`GET /resilience/status-page` : `OverallStatus` dérivé (maintenance > incident [P1 ouvert] > dégradé [composant critique en échec ou incident non résolu] > opérationnel), `Components` (diagnostics Phase 41 + criticité), `RecentIncidents` (10 derniers) — **composition pure**, aucun second calcul de santé |
| 40 | Mode dégradé | **Hérité** (mode maintenance Phase 41, ventes offline Phase 10) |
| 41-43 | Retour à la normale ; réconciliation après incident ; intégrité après restauration | **Hérité** — runbook `return_to_normal` (NEW, §37) documente la séquence (health check → sync → rapprochement → réconciliation → trafic normal → monitoring renforcé) en s'appuyant sur des mécanismes déjà livrés (sync_conflict Phase 10, réconciliation paiements Phase 12, comptage/checksum de restauration Phase 36 §43) |
| 44 | Tests DR (tabletop, restore, failover DB/app, panne réseau/serveur/stockage, ransomware simulé, site indisponible, retour normal) | **NEW** — table `dr_test`, `ScheduleDrTestAsync`/`RecordDrTestResultAsync`/`ListDrTestsAsync` ; `test_type` accepte exactement les 10 valeurs du §44 (`tabletop`, `restore`, `failover_db`, `failover_app`, `network_outage`, `server_outage`, `storage_outage`, `ransomware_simulation`, `site_unavailable`, `return_to_normal`) — distinct de `restore_run` (qui exige toujours un `backup_id` concret) |
| 45 | Fréquence des tests | `next_test_due` capturé à l'enregistrement du résultat — **backlog** : pas d'alerte automatique si un exercice DR dépasse son échéance (contrairement à `RunHealthChecksAsync` qui alerte déjà sur une sauvegarde en retard) |
| 46 | PRA / PCA (périmètre, rôles, RPO, RTO, procédures, tests, validation) | **Recomposé, pas un document dédié** — politique (RPO/RTO), rôles (permissions), procédures (runbooks), tests (dr_test) existent chacun séparément ; un document PRA/PCA formel qui les agrège en un seul artefact reste **backlog** |
| 47-48 | Déploiement progressif (canary/blue-green/rolling/rollback) ; maintenance sans interruption | **Infrastructure — backlog** (CI/CD, orchestrateur) ; le mode maintenance et les feature flags avec kill switch (Phase 41) couvrent partiellement le §48 côté applicatif |
| 49-50 | Capacité ; autoscaling | **Backlog** — `GET /ops/metrics` (Phase 41) expose des compteurs bruts, pas de tableau de capacité dédié ni d'autoscaling (infrastructure) |
| 51-52 | Monitoring résilience ; alertes | **Hérité + composé** — diagnostics Phase 41 + `RunHealthChecksAsync` (Phase 36) restent les moteurs ; la page de statut (NEW) en est la vue consolidée pour ce périmètre |
| 53-54 | Coût vs résilience ; exportabilité | §53 est un choix commercial (offres), non applicatif. §54 hérité (exports Phase 39 migration, Phase 45 GED) |
| 55 | Sécurité DR (MFA, comptes break-glass, secrets séparés, audit, restauration autorisée, isolation restore) | **Hérité intégralement** — MFA (Phase 33), accès d'urgence/break-glass (Phase 47 §6), audit immuable (Phase 11/47), restauration à 2 personnes (Phase 36 §39), test de restauration en environnement isolé (Phase 36 §21) |
| 56 | API principales | `GET/POST resilience/{status,status-page,backups,incidents,runbooks,dr-tests}` + `POST resilience/incidents/{id}/lockdown` — **tous alias sur le même contrôleur/service que `/continuity/*`, jamais une seconde logique**. `GET resilience/replication`, `GET resilience/failover`, `POST resilience/failover/request` — **non construites**, aucune infrastructure de réplication/failover à interroger |
| 57 | Événements (BackupStarted…DRTestCompleted) | **Backlog** — pas de bus d'événements applicatif, comme déjà noté en Phase 36 ; les actions sont journalisées (audit immuable) et notifiées ponctuellement (`RunHealthChecksAsync`), mais pas publiées sur un bus consommable |
| 58-60 | Scénarios (petite entreprise / cloud / sinistre) | Narratifs, couverts par la combinaison de fonctionnalités déjà citées (offline Phase 10, restore_procedure, runbooks) — rien à construire spécifiquement pour un scénario donné |
| 61-62 | Critères de sortie ; étape suivante | Voir le tableau ci-dessus pour chaque critère ; passage à la Phase 49 (Qualité, tests globaux, RC) laissé à une phase ultérieure |

## `V062` — contenu (1 table neuve, 1 colonne, 1 CHECK étendu)

- **`dr_test`** (NEW, RLS entreprise) — `test_type` (10 valeurs, §44), `status`
  (`scheduled`/`passed`/`failed`/`cancelled`), `scheduled_for`/`executed_at`, `scope`,
  `participants` (jsonb), `findings`/`corrective_actions`, `next_test_due`. Index
  `dr_test_company_idx (company_id, status, scheduled_for)`.
- **`continuity_incident`** (Phase 36, ALTER) — `+ containment_actions` (jsonb, défaut `[]`) —
  actions de confinement **réellement exécutées** (§32, §55), jamais une intention documentée
  après coup dans le seul post-mortem.
- **`backup`** (Phase 36, ALTER) — `backup_kind_chk` étendu : `+ differential, + metadata, + keys`
  (§11).
- Aucune nouvelle permission (réutilise `continuity.{view,manage,restore}` — un exercice DR se
  gère comme un test de restauration, un confinement comme une restauration production, même
  niveau de confiance élevé).
- **250 tables (+1), 211 RLS (+1), 62 migrations (V001–V062).**

## Backend

| Couche | Fichier |
|---|---|
| Contracts | `Nexora.Contracts/Continuity/ContinuityContracts.cs` (étendu : `ContinuityIncidentDto.ContainmentActions`, `ContainmentActionDto`, `LockdownResultDto`, `RunbookStepDto`/`RunbookDto`, `DrTestDto`/`ScheduleDrTestRequest`/`RecordDrTestResultRequest`, `StatusComponentDto`/`StatusPageDto`) |
| Application | `Nexora.Application/Continuity/IContinuity.cs` (étendu : `LockDownAsync`, `ListDrTestsAsync`/`ScheduleDrTestAsync`/`RecordDrTestResultAsync`, `StatusPageAsync` ; nouvelle classe statique `ContinuityRunbookCatalog`, 13 entrées) |
| Infrastructure | `Continuity/SqlContinuityService.cs` (étendu : verrouillage d'urgence, cycle de vie des exercices DR, composition de la page de statut) |
| API | `Continuity/ContinuityController.cs` (étendu : `POST incidents/{id}/lockdown`, `GET/POST resilience/dr-tests`, `POST resilience/dr-tests/{id}/record`, `GET resilience/runbooks`, `GET resilience/status-page`, alias `GET resilience/status`, `GET/POST resilience/backups*`, `GET/POST resilience/incidents`) |

### Points de conception

- **Le verrouillage d'urgence est réservé à `continuity.restore` (§32, §55)** — même niveau de
  confiance qu'une restauration production, jamais `continuity.manage` : révoquer toutes les
  sessions et clés d'API d'une entreprise entière est une action destructive-réversible qui mérite
  la permission la plus haute du domaine, pas la permission d'usage courant.
- **Le confinement bascule `app_user.security_epoch` (Phase 22/33), révoque `user_session` et
  `api_key`, puis journalise sur l'incident** — jamais l'inverse : les identités légitimes se
  reconnectent après coup, la trace du confinement reste sur l'incident pour le post-mortem (§32).
- **Un incident déjà résolu ne peut plus être verrouillé** (`continuity.incident_resolved`) — le
  confinement est une action de traitement, jamais une action de clôture rétroactive.
- **Un exercice DR (`dr_test`) reste distinct d'un `restore_run` (Phase 36)** — un test de
  restauration exige toujours un `backup_id` concret ; un tabletop ou une simulation de panne
  réseau n'a pas de sauvegarde en jeu. Les deux registres cohabitent sans se chevaucher.
- **Un exercice DR ne peut être consigné qu'une fois** (`RecordDrTestResultAsync` exige
  `status = 'scheduled'`) — pas de ré-écriture d'un résultat déjà passé, cohérent avec le principe
  immuable de l'audit (Phase 11) appliqué ici à la preuve de résilience.
- **La page de statut compose, ne recalcule jamais** — `StatusPageAsync` appelle
  `diagnostics.RunAllAsync` (Phase 41), `DisasterRecoveryStatusAsync` (Phase 36) et
  `maintenance.ListAsync` (Phase 41) puis dérive `OverallStatus` par une règle de priorité simple
  (maintenance > incident > dégradé > opérationnel) — aucune de ces trois sources n'est
  redérivée ou dupliquée.
- **Le catalogue de runbooks est un contenu de référence, jamais configurable par entreprise**
  (même principe que `IntelligenceModelCatalog`, Phase 43) — chaque étape cite une route réelle de
  l'API NEXORA ONE plutôt qu'une généralité, pour qu'un runbook reste actionnable en incident réel.
- **Réplication/failover/HA délibérément non construits** — cohérent avec le backlog déjà assumé
  en Phase 36 : ce sont des décisions d'hébergement (Postgres en réplication, load balancer,
  orchestrateur de conteneurs), pas du code métier. Les runbooks `failover_procedure` et
  `restore_procedure` documentent la **procédure** à suivre le jour où cette infrastructure existe,
  sans la simuler prématurément.

## Tests d'intégration — 4 nouveaux dans `ContinuityRegisterTests` (total 12)

| Test | § | Vérifie |
|---|---|---|
| `LockDown_revokes_all_sessions_and_api_keys_and_journals_the_containment_action` | 32, 55 | entreprise isolée dédiée (`MakeCompanyWithRole`) ; une session et une clé d'API réelles (`IApiKeyService`) sont révoquées ; l'action de confinement est journalisée sur l'incident (`ListIncidentsAsync`) ; un incident déjà résolu ne peut plus être verrouillé (`continuity.incident_resolved`) |
| `DrTest_lifecycle_schedules_records_a_result_and_rejects_an_invalid_type` | 44-45 | type d'exercice invalide rejeté ; programmation puis consignation d'un résultat (`passed`, `nextTestDue`) ; un exercice déjà consigné ne peut pas l'être une seconde fois ; filtrage par statut |
| `StatusPage_is_operational_by_default_and_becomes_incident_after_a_p1` | 7, 39 | entreprise isolée ; statut `operational` par défaut avec le composant `database` en criticité critique ; bascule à `incident` après ouverture d'un incident p1 |
| `Register_backup_accepts_the_extended_kinds` | 11 | `differential`/`metadata`/`keys` acceptés par `RegisterBackupAsync` |

## Tests API — nouveau fichier `ResilienceEndpointsTests` (5 tests)

| Test | Vérifie |
|---|---|
| `Resilience_routes_require_authentication` | Les routes `/resilience/*` (status-page, status, runbooks, dr-tests, backups, incidents) exigent un jeton |
| `Read_only_resilience_endpoints_are_wired_over_http` | Câblage HTTP bout en bout des routes de lecture, y compris l'alias `/resilience/status` sur le même moteur que `/continuity/disaster-recovery/status` |
| `Runbook_catalog_is_a_static_reference_covering_the_key_scenarios` | Le catalogue contient les scénarios clés (`db_outage`, `ransomware`, `restore_procedure`, `return_to_normal`) ; le runbook `ransomware` référence explicitement la route `/lockdown` |
| `Dr_test_lifecycle_is_wired_over_http` | Type invalide → 422 ; programmation → 200 (`scheduled`) ; consignation → 200 (`passed`) |
| `Lockdown_on_a_nonexistent_incident_returns_not_found` | `POST resilience/incidents/{id}/lockdown` sur un id inexistant → 404 |

## Frontend

`src/continuity/continuityApi.ts` étendu (`ContinuityIncident.domain`/`containmentActions`,
`ContainmentAction`, `LockdownResult`, `Runbook`/`RunbookStep`, `DrTest`, `StatusComponent`,
`StatusPage`, `lockDownIncident`, `listRunbooks`, `listDrTests`/`scheduleDrTest`/
`recordDrTestResult`, `statusPage`) + `src/continuity/ContinuityPage.tsx` : nouvelle section
**Page de statut** (composants + statut global coloré), bouton **Verrouillage d'urgence** sur les
incidents de domaine `security`, section **Exercices de reprise** (programmer/consigner), section
**Runbooks de référence** (catalogue dépliable, lecture seule).

Vitest `continuityApi.test.ts` (+5) → **110 tests frontend**.

## Manques assumés (backlog)

- **Haute disponibilité et infrastructure de reprise** (§19-27, §47-50, §56 routes replication/
  failover) — réplication DB, load balancing, failover automatique, split-brain, autoscaling,
  déploiement progressif : décisions d'hébergement, hors du périmètre applicatif d'un monolithe
  .NET, comme déjà assumé en Phase 36
- **Bus d'événements applicatif** (§57 : `BackupStarted`…`DRTestCompleted`) — les actions restent
  journalisées (audit immuable) et notifiées ponctuellement, jamais publiées sur un bus consommable
- **Classes de continuité nommées** (§10) — la politique encode déjà RPO/RTO/immutabilité par
  entreprise ; une étiquette (« essentielle »/« renforcée »/« HA ») n'ajouterait pas de capacité
- **Document PRA/PCA agrégé** (§46) — les éléments existent séparément (politique, rôles, runbooks,
  exercices) sans artefact unique qui les réunit formellement
- **Alerte sur exercice DR en retard** (§45) — `next_test_due` est capturé mais pas encore
  surveillé par `RunHealthChecksAsync` (qui alerte déjà sur une sauvegarde en retard)
- **Gabarit de communication d'incident dédié** (§38) — la page de statut expose l'état courant,
  mais aucun message-type structuré (détection/impact/prochaine mise à jour/fin) n'est généré
- **Tableau de capacité et autoscaling** (§49-50) — `GET /ops/metrics` (Phase 41) expose des
  compteurs bruts, pas de vue de capacité ni de scaling automatique
