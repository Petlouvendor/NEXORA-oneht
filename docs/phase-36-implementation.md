# Phase 36 — Continuité, Backup & Reprise — implémentation

**Statut : livré et vérifié.** Migration `V049` + script `database/scripts/backup.sh`.
8 tests d'intégration (`ContinuityRegisterTests`), 5 tests frontend (`continuityApi.test.ts`).
**243 tests backend** verts (18 unit + 11 API + 214 intégration), Debug **et** Release, 0 warning.
53 tests frontend.

La sauvegarde physique (pg_dump / WAL / snapshots), la réplication et le failover sont des
opérations d'**infrastructure**. Cette phase ajoute le **registre applicatif** qui les pilote, les
**vérifie** et les **surveille** — et valide le socle voisin :

- **Phase 10** (`V017`/`V029`) — moteur de synchro offline : `sync_operation` / `sync_conflict` /
  `sync_batch`, curseurs, reprise après erreur, idempotence par `operation_uuid` (§29-30)
- **Phase 34** (`V047`) — `notification_outbox` + `NotificationDispatcher` : queue persistante,
  retry, backoff exponentiel, **dead-letter**, déduplication (§28, §46)
- **Phase 11** (`V030`) — `audit_log` **immuable** (trigger `BEFORE UPDATE/DELETE`) (§38)
- **Phase 3** — `/health` + `/health/ready` (`DatabaseHealthCheck`), deny-by-default (§46)
- **Phase 32** (`V045`) — `config_transfer` : export de configuration non sensible (§17)
- **Phase 18** — `IdempotencyFilter` (§45)

---

## Correspondance cahier des charges → implémentation

| § | Exigence | Où |
|---|---|---|
| 1-2 | Continuer, protéger, reprendre vite ; **une sauvegarde n'est fiable que si sa restauration est testée** ; sauvegardes protégées contre la suppression accidentelle ; objectifs configurables | **`IContinuityService`** : `VerifyBackupAsync` (une sauvegarde reste `completed` tant que les contrôles ne passent pas → `verified`), `RunRestoreTestAsync` (verdict `passed`/`failed`), `backup.immutable_until` (verrou de rétention §13), objectifs dans `backup_policy` |
| 3-5 | Niveaux local / cloud / hybride / HA ; **RPO/RTO** configurables et affichés | `backup_policy.rpo_target_minutes` / `rto_target_minutes` (bornes 1–10080), `destination` (local/nas/cloud_object/offsite) ; `DisasterRecoveryStatusDto` affiche cibles vs observé. HA (instances multiples, load balancer, failover) : **infrastructure — hors code applicatif** |
| 6-7 | Types (complète / incrémentale / snapshots / logique / documents / configuration) ; politique (fréquence, rétention, horaires, destination, chiffrement, vérification, suppression contrôlée, responsable) | `backup.kind` ∈ full/incremental/config/documents/snapshot ; `backup_policy` (`kinds` jsonb, `frequency` hourly/daily/weekly/monthly, `retention_days`, `destination`, `encrypted`, `immutable`). `expires_at` du `backup` dérivé de la rétention |
| 8-11 | Stratégie 3-2-1 ; sauvegarde locale / cloud / hybride | `backup.location_ref` (pointeur opaque — **jamais un secret** §6, §44) + `backup_policy.destination`. Le script `backup.sh` produit un dump `--format=custom --compress=9` + `.sha256`, et (option) l'enregistre au registre via `POST /api/v1/backups` avec une clé d'API `continuity.manage` (Phase 35). La copie hors site / rotation multi-support est opérationnelle |
| 12-13 | Chiffrement (transit / repos, clés séparées, rotation) ; **immutabilité** (protection suppression, rétention verrouillée, compte séparé, anti-ransomware) | TLS + `backup.encrypted` déclaratif ; `backup_policy.immutable` → `backup.immutable_until = expires_at`. Compte de sauvegarde séparé + WORM du stockage objet : **infrastructure** |
| 14-15 | Snapshots (point dans le temps, horodatage, identifiant, statut, rétention) ; **PITR** (backup + journal WAL → point T) | `backup` kind `snapshot`, `covers_up_to` = point RPO, `expires_at`. PITR réel (WAL archiving + `recovery_target_time`) : opération d'infrastructure — le registre en tient la trace |
| 16-17 | Sauvegarde des documents (factures, reçus, pièces, signés, exports, métadonnées, version) ; sauvegarde de la configuration (entreprises, rôles, workflows, templates, plan comptable, mappings, numérotation, connecteurs) | `backup` kind `documents` / `config` ; le contenu documentaire (`issued_document` + hash + snapshot jsonb — Phase 30) et la configuration (`config_transfer` export — Phase 32) sont déjà exportables et entrent dans un dump complet |
| 18-20 | Restauration (sélection → validation → **environnement isolé** → vérification → restauration → reconnexion → audit) ; restauration sélective (entreprise / période / document, prévisualisation) ; **sandbox** de restauration, accès restreint, rapport de comparaison | `RunRestoreTestAsync` (mode `test`, cible `sandbox`) : contrôles d'intégrité + **comparaison du comptage d'objets** (`object_count_expected` vs recompte live) + `report` jsonb ; `RequestProductionRestoreAsync` → `pending_approval`. La restauration réelle en sandbox par l'agent d'infrastructure est journalisée ; la restauration sélective sans casser les dépendances comptables (§19) reste une procédure encadrée (backlog outillage) |
| 21 | **Tests de restauration** (test automatique de backup, test périodique, checksum, comptage objets, vérification base & documents, rapport) | `VerifyBackupAsync` (checksum présent + 64 hex, `covers_up_to` passé, non expiré, comptage cohérent → `verified_at`) ; `RunRestoreTestAsync` (checksum + comptage + fenêtre → `passed`/`failed` + `report`). **Le planificateur** exécute `RunHealthChecksAsync` par entreprise à chaque tick |
| 22-24 | Disaster Recovery (incident → détection → décision → bascule → restauration/réplication → validation → service) ; scénarios ; ransomware (sauvegardes isolées, immutabilité, restauration depuis copie saine, audit incident) | **`continuity_incident`** : `category` couvre les 11 scénarios (§23) + `ransomware` ; runbook `runbook_step` 1..9 (§43) ; `post_mortem` jsonb (§45). La bascule / réplication effective est infrastructure |
| 25-28 | HA ; base de données (backup PG, WAL archiving, réplique, PITR, failover) ; application (stateless, instances multiples, config externalisée, health endpoint, redémarrage, déploiement réversible) ; file de messages | **Infrastructure / DevOps** — l'application est déjà sans état (JWT/clé d'API, RLS par session, `SchedulerRunner` idempotent), `/health` + `/health/ready`, config externalisée. La file `notification_outbox` (Phase 34) est persistante avec retry/dead-letter/déduplication (§28) |
| 29-30 | **Offline POS** (internet perdu → POS local → queue → retour → validation → synchro) ; synchronisation après panne (ordre des évènements, reprise curseur, doublons, conflits, échec partiel, rapport, rejeu) | **Phase 10** (`sync_operation` rejoué via `SalesService`, `operation_uuid` idempotent, `sync_conflict`, curseurs) + `shared/offlineQueue.ts` (outbox POS front) — inchangé, validé |
| 31 | **Sauvegarde avant opérations critiques** (migration, mise à jour majeure, changement de config critique, maintenance) | `backup.sh` avant `apply.sh`/`rebuild.sh` ; `RegisterBackupRequest` avec un `covers_up_to` = instant pré-opération. `config_version` (Phase 14) fige la config « active au temps T » |
| 32 | Déploiements (versionnement, migration DB, backup avant migration, health check, canary/rolling, rollback applicatif, rollback DB sous procédure) | Migrations `V0NN` idempotentes suivies dans `schema_migration` (version + checksum) ; `rebuild.sh` / `apply.sh`. Canary / rolling : infrastructure |
| 33-34 | **Plan de reprise** (responsables, contacts, priorités, services critiques, dépendances, procédures, RTO/RPO, fournisseurs, communication) ; **priorité des services** P1..P4 configurable | Les objectifs (RPO/RTO) et le runbook sont dans le registre ; le plan documentaire complet + la matrice P1..P4 : **`docs/` — à formaliser par l'exploitant** |
| 35-37 | Mode dégradé (POS local, cache lecture, file, restrictions paiements externes, avertissement) ; **monitoring** (âge du dernier backup, RPO/RTO observés, réplication, lag, espace, santé base & queues) ; **alertes** (backup échoué / trop ancien, espace faible, réplication en retard, test échoué, RPO/RTO dépassé, queue bloquée) | **`RunHealthChecksAsync`** (planificateur §36-37) : sauvegarde en retard vs la fréquence de la politique → **notification** (Phase 34) aux titulaires de `continuity.view`, priorité 1 si RPO dépassé ; dernier test de restauration en échec (7 j) → notification. `DisasterRecoveryStatusDto` : `lastGoodBackupAgeMinutes`, `rpoBreached`, `backupOverdue`, `backupsAwaitingVerification`, `unresolvedIncidents`, `openP1Incidents`, **grade A–E**. Mode dégradé POS : Phase 10 |
| 38 | **Audit** (création politique, modification rétention, backup, suppression backup, restauration, test, failover, rollback, accès sauvegarde, incident) | `trg_audit_*` sur `backup_policy` / `backup` / `restore_run` / `continuity_incident` → `audit_log` immuable |
| 39 | **Sécurité d'accès aux sauvegardes** (compte séparé, MFA, moindre privilège, accès temporaire, séparation admin système / admin applicatif) | Permissions `continuity.view` < `continuity.manage` < **`continuity.restore`** (restauration en production, super_administrator / group_administrator uniquement). **Une restauration en production exige un second approbateur** (`ApproveProductionRestoreAsync` : `requested_by ≠ approver` → `continuity.self_approval`). MFA (Phase 33) + accès temporaire (Phase 22) s'appliquent |
| 40-42 | Continuité petite / grande entreprise / groupe (politiques par entreprise, priorités différentes, restauration par tenant, rapport centralisé, test annuel) | `backup_policy` **par entreprise** (RLS) ; `backup` / `restore_run` / `continuity_incident` bornés par `company_id`. Rapport groupe centralisé : **backlog** (accessible via GROUP scope) |
| 43-45 | **Procédure d'incident** (9 étapes : détecter → protéger → isoler → évaluer → restaurer/basculer → vérifier → rouvrir → analyser → améliorer) ; communication ; **post-mortem** (cause, impact, durée, données, RPO/RTO, actions, prévention, responsable, échéance) | `continuity_incident.runbook_step` (`CHECK 1..9`) + `AdvanceIncidentAsync` ; `ResolveIncidentAsync` **exige** un post-mortem complet (`cause`, `impact`, `actions`, `prevention`, `owner` non vides → sinon `continuity.post_mortem`), `rpo_actual_minutes` / `rto_actual_minutes` |
| 46 | API principales | **`GET/POST /api/v1/backup-policies`**, **`GET/POST /api/v1/backups`**, **`POST /backups/{id}/verify`**, **`POST /backups/{id}/restore-test`**, **`GET/POST /api/v1/restores`** (+ `/{id}/approve`), **`GET /api/v1/disaster-recovery/status`**, **`GET/POST /api/v1/continuity/incidents`** (+ `/{id}/advance`, `/{id}/resolve`), **`GET /api/v1/continuity/tests`**. `replication/status`, `failover/plans/{id}/execute`, `integration-logs` : **infrastructure — non exposés** |
| 47 | Événements (`BackupCompleted`, `BackupVerified`, `RestoreTestCompleted`, `IncidentOpened`…) | Journalisés dans `backup` / `restore_run` / `continuity_incident` + `audit_log`. Bus d'évènements dédié : backlog commun aux phases 16-36 |
| 48 | Tests | `ContinuityRegisterTests` (8) — cf. ci-dessous. Offline / synchro / failover POS : `SyncEngineTests` (Phase 10) |
| 49 | Critères de sortie | Politiques + RPO/RTO + catalogue + vérification + tests de restauration + sandbox (contrôle applicatif) + DR status + registre d'incident + runbook + post-mortem + monitoring + alertes + audit + accès + API : **livrés**. Backup physique / WAL / réplication / failover / HA : **infrastructure** (script `backup.sh` fourni). Restauration sélective outillée, rapport groupe, plan documentaire : **backlog** |

## `V049` — contenu

- **`backup_policy`** — `company_id` (RLS), `kinds` jsonb, `frequency` (`CHECK` hourly/daily/weekly/
  monthly), `retention_days` (1–3650), `destination`, `encrypted`, `immutable`, `rpo_target_minutes`
  / `rto_target_minutes` (1–10080), `status`. `UNIQUE (company_id)`.
- **`backup`** — `kind` / `status` (running/completed/failed/**verified**/expired), `covers_up_to`
  (point RPO), `size_bytes`, `checksum`, `object_count`, `location_ref`, `encrypted`,
  `immutable_until`, `verified_at` / `verification_detail` jsonb, `expires_at`. `UNIQUE (id,
  company_id)` pour la FK composite.
- **`restore_run`** — `backup_id` (FK composite), `mode` (test/production), `status`
  (pending_approval/running/passed/failed/cancelled), `checksum_ok`, `object_count_expected` /
  `_actual`, `report` jsonb, `requested_by` / `approved_by`.
- **`continuity_incident`** — `severity` (p1..p4), `category` (11 scénarios + ransomware + other),
  `detected_via`, `status`, `runbook_step` (`CHECK 1..9`), `affected_scope` jsonb,
  `rpo_actual_minutes` / `rto_actual_minutes`, `post_mortem` jsonb.
- `enable_company_rls` + `trg_audit_*` sur les 4 tables. Permissions `continuity.{view,manage,restore}`.
- **187 tables (+4), 175 en RLS (+4)**, 49 migrations (V001–V049).

## Backend

| Couche | Fichier |
|---|---|
| Contracts | `Nexora.Contracts/Continuity/ContinuityContracts.cs` |
| Application | `Nexora.Application/Continuity/IContinuity.cs` — `IContinuityService` ; `PermissionCatalog.Continuity.{View,Manage,Restore}` |
| Infrastructure | `Nexora.Infrastructure/Continuity/SqlContinuityService.cs` ; hook `RunHealthChecksAsync` dans `Scheduling/SchedulerRunner.cs` ; DI `Infrastructure/DependencyInjection.cs` |
| API | `Nexora.Api/Controllers/ContinuityController.cs` (routes canoniques `~/api/v{version}/{backup-policies,backups,restores,disaster-recovery/status}` + `continuity/incidents`, `continuity/tests`) |
| Ops | `database/scripts/backup.sh` — pg_dump `--format=custom` + SHA-256 + enregistrement au registre |

### Points de conception

- **Registre, pas exécuteur** : le service ne réalise ni la sauvegarde ni la restauration réelle
  (agent d'infrastructure) — il en tient le **catalogue**, applique les **contrôles d'intégrité
  qu'il peut** (empreinte présente, comptage d'objets cohérent avec l'état live, fenêtre de
  couverture / rétention) et pilote le **runbook**.
- **Vérification ≠ re-hash du fichier** : `VerifyBackupAsync` fait un contrôle de cohérence
  applicatif (le re-hash cryptographique du fichier de dump est le travail de l'agent / du
  `restore-test` réel).
- **Fragment SELECT jamais interpolé** : EF `SqlQuery` traite chaque `{…}` comme un paramètre — les
  requêtes écrivent le `SELECT … AS "Col"` en toutes lettres.
- **Second approbateur** pour toute restauration en production (§39).

## Tests d'intégration — `ContinuityRegisterTests` (8, `[Collection("finance")]`)

| Test | § | Vérifie |
|---|---|---|
| `Register_backup_computes_expiry_from_the_policy_retention` | 7, 13 | `expires_at = covers_up_to + retention_days` |
| `Verify_passes_the_integrity_checks_and_flips_status_to_verified` | 21 | contrôles OK → `verified_at` posé, `status = 'verified'` |
| `Verify_fails_when_the_checksum_is_missing` | 2 | empreinte absente → `ok:false`, statut inchangé |
| `Restore_test_records_a_run_and_a_verdict` | 21 | `restore_run` mode `test`, `status = 'passed'`, `checksum_ok` |
| `A_production_restore_requires_a_second_person` | 39 | demande → `pending_approval` ; auto-approbation → `continuity.self_approval` ; un tiers → `passed`, `approved_by` |
| `DR_status_reports_an_RPO_breach_when_the_last_backup_is_old` | 36 | dernière sauvegarde à J-3, RPO 60 min → `rpoBreached`, `backupOverdue`, grade ≠ A |
| `Incident_runbook_open_advance_and_resolve_with_a_post_mortem` | 43-45 | étape 1 → 5 ; post-mortem incomplet refusé ; post-mortem complet → `resolved` |
| `The_health_check_alerts_admins_when_backups_are_missing` | 36-37 | `RunHealthChecksAsync` → notification `continuity.backup_overdue` aux titulaires de `continuity.view` |

## Frontend

`src/continuity/` — `continuityApi.ts` + **`ContinuityPage.tsx`** (route `/continuity`, nav
« Continuité », gate `continuity.view`) : **carte état de reprise** (grade A–E, RPO/RTO cible vs
observé, dernière sauvegarde saine / vérifiée, incidents ouverts), **catalogue des sauvegardes**
(enregistrer / vérifier / test resto / demander une restauration), **restaurations** (approbation
par un tiers), **incidents & runbook** (ouvrir → avancer étape par étape → résoudre avec
post-mortem). Vitest `continuityApi.test.ts` (5) → **53 tests frontend**.

## Smoke API (port 5080, `direction@abc-group.demo`)

- `GET /disaster-recovery/status` → `grade`, `rpoBreached:true`, `backupOverdue:true` (aucune
  sauvegarde) ; `POST /backups {kind:"full", coversUpTo, checksum:<64 hex>, objectCount}` →
  `status:"completed"`, `expiresAt` calculé ; `POST /backups/{id}/verify` → `ok:true`, backup
  passe `verified` ; `GET /disaster-recovery/status` → grade remonte.
- `POST /backups/{id}/restore-test` → `restore_run` `passed` ; `POST /restores {target}` →
  `pending_approval` ; approbation par le même utilisateur → `continuity.self_approval`.
- `POST /continuity/incidents` → incident `open` step 1 ; `advance` → step 5 ;
  `resolve` sans post-mortem complet → `continuity.post_mortem`.
- Caissier sur `GET /backups` → **403**.

## Manques assumés (backlog)

- Backup physique / WAL archiving / PITR / réplication / failover / HA — **infrastructure**
  (le script `backup.sh` couvre le dump logique + l'enregistrement)
- Restauration **sélective** outillée (entreprise / période / document) sans casser les dépendances
  comptables (§19) — aujourd'hui : test de contrôle + procédure encadrée
- Plan de reprise documentaire (responsables, contacts, matrice de priorité P1–P4) (§33-34)
- Rapport de continuité **groupe** centralisé (§42)
- Métriques d'observabilité continues (lag de réplication, espace disque, latence) (§36)
- Simulation ransomware contrôlée outillée (§48)
- Bus d'évènements `BackupCompleted` / `IncidentOpened` (§47)
