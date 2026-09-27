import { apiFetch } from "@/core/apiClient";

// Continuité, sauvegarde & reprise (Phase 36 §46).

export interface BackupPolicy {
  name: string;
  kinds: string[];
  frequency: string;
  retentionDays: number;
  destination: string;
  encrypted: boolean;
  immutable: boolean;
  rpoTargetMinutes: number;
  rtoTargetMinutes: number;
  status: string;
}

export interface Backup {
  id: string;
  kind: string;
  status: string;
  startedAt: string;
  finishedAt: string | null;
  sizeBytes: number | null;
  checksum: string | null;
  objectCount: number | null;
  coversUpTo: string;
  locationRef: string | null;
  encrypted: boolean;
  immutableUntil: string | null;
  verifiedAt: string | null;
  expiresAt: string | null;
  error: string | null;
}

export interface RestoreRun {
  id: string;
  backupId: string;
  mode: string;
  target: string | null;
  status: string;
  checksumOk: boolean | null;
  objectCountExpected: number | null;
  objectCountActual: number | null;
  startedAt: string;
  finishedAt: string | null;
  requestedBy: string | null;
  approvedBy: string | null;
}

export interface DisasterRecoveryStatus {
  lastGoodBackupAt: string | null;
  lastGoodBackupAgeMinutes: number | null;
  lastVerifiedBackupAt: string | null;
  rpoTargetMinutes: number;
  rtoTargetMinutes: number;
  rpoBreached: boolean;
  backupOverdue: boolean;
  unresolvedIncidents: number;
  openP1Incidents: number;
  backupsAwaitingVerification: number;
  grade: string;
}

export interface ContinuityIncident {
  id: string;
  severity: string;
  category: string;
  title: string;
  detectedVia: string;
  status: string;
  runbookStep: number;
  runbookLabel: string;
  rpoActualMinutes: number | null;
  rtoActualMinutes: number | null;
  postMortem: Record<string, string> | null;
  openedAt: string;
  resolvedAt: string | null;
  /** availability (défaut, Phase 36) | security (Phase 47 §29-30) — même registre. */
  domain: string;
  /** Phase 48 §32, §55 — confinements réellement exécutés (ex. verrouillage d'urgence). */
  containmentActions: { action: string; at: string; by: string; detail: string }[] | null;
}

export const getBackupPolicy = () => apiFetch<BackupPolicy>("/backup-policies");
export const setBackupPolicy = (p: BackupPolicy) =>
  apiFetch<BackupPolicy>("/backup-policies", { method: "POST", body: JSON.stringify(p) });

export const listBackups = () => apiFetch<Backup[]>("/backups");
export const registerBackup = (r: {
  kind: string;
  coversUpTo: string;
  sizeBytes?: number | null;
  checksum?: string | null;
  objectCount?: number | null;
  locationRef?: string | null;
  encrypted?: boolean;
}) => apiFetch<Backup>("/backups", { method: "POST", body: JSON.stringify(r) });
export const verifyBackup = (id: string) =>
  apiFetch<{ ok: boolean; detail: string; liveObjectCount: number | null; backupObjectCount: number | null }>(
    `/backups/${id}/verify`, { method: "POST" });
export const restoreTest = (id: string) =>
  apiFetch<RestoreRun>(`/backups/${id}/restore-test`, { method: "POST", body: JSON.stringify({ backupId: id }) });

export const listRestores = () => apiFetch<RestoreRun[]>("/restores");
export const requestRestore = (backupId: string, target: string) =>
  apiFetch<RestoreRun>("/restores", { method: "POST", body: JSON.stringify({ backupId, target }) });
export const approveRestore = (id: string) =>
  apiFetch<RestoreRun>(`/restores/${id}/approve`, { method: "POST" });

export const drStatus = () => apiFetch<DisasterRecoveryStatus>("/disaster-recovery/status");

export const listIncidents = (status?: string) =>
  apiFetch<ContinuityIncident[]>(`/continuity/incidents${status ? `?status=${status}` : ""}`);
export const openIncident = (r: { severity: string; category: string; title: string; detectedVia?: string }) =>
  apiFetch<ContinuityIncident>("/continuity/incidents", { method: "POST", body: JSON.stringify(r) });
export const advanceIncident = (id: string, runbookStep: number, status?: string) =>
  apiFetch<ContinuityIncident>(`/continuity/incidents/${id}/advance`, {
    method: "POST",
    body: JSON.stringify({ runbookStep, status: status ?? null }),
  });
export const resolveIncident = (
  id: string,
  body: { rpoActualMinutes: number | null; rtoActualMinutes: number | null; postMortem: Record<string, string> },
) => apiFetch<ContinuityIncident>(`/continuity/incidents/${id}/resolve`, { method: "POST", body: JSON.stringify(body) });

// =============================================================================
// Résilience, Haute Disponibilité & Disaster Recovery (Phase 48 §32, §37, §39, §44-45, §55).
// Même registre que ci-dessus — jamais un second moteur.
// =============================================================================

export interface ContainmentAction {
  action: string;
  at: string;
  by: string;
  detail: string;
}

export interface LockdownResult {
  sessionsRevoked: number;
  apiKeysRevoked: number;
  usersAffected: number;
  at: string;
}

export interface RunbookStep {
  step: number;
  title: string;
  guidance: string;
}

export interface Runbook {
  key: string;
  title: string;
  description: string;
  steps: RunbookStep[];
}

export interface DrTest {
  id: string;
  testType: string;
  title: string;
  scheduledFor: string | null;
  executedAt: string | null;
  status: string;
  scope: string | null;
  participants: string[];
  findings: string | null;
  correctiveActions: string | null;
  nextTestDue: string | null;
  createdAt: string;
}

export interface StatusComponent {
  key: string;
  label: string;
  criticality: string;
  status: string;
  detail: string | null;
}

export interface StatusPage {
  overallStatus: string;
  components: StatusComponent[];
  recentIncidents: ContinuityIncident[];
  generatedAt: string;
}

/** §32, §55 — verrouillage d'urgence : révoque toutes les sessions et clés d'API de l'entreprise. */
export const lockDownIncident = (id: string) =>
  apiFetch<LockdownResult>(`/continuity/incidents/${id}/lockdown`, { method: "POST" });

/** §37 — catalogue statique des runbooks, jamais configurable. */
export const listRunbooks = () => apiFetch<Runbook[]>("/resilience/runbooks");

/** §44-45 — exercices DR au-delà du simple test de restauration. */
export const listDrTests = (status?: string) =>
  apiFetch<DrTest[]>(`/resilience/dr-tests${status ? `?status=${status}` : ""}`);
export const scheduleDrTest = (r: {
  testType: string;
  title: string;
  scheduledFor?: string | null;
  scope?: string | null;
  participants?: string[];
}) => apiFetch<DrTest>("/resilience/dr-tests", { method: "POST", body: JSON.stringify(r) });
export const recordDrTestResult = (id: string, r: {
  passed: boolean;
  findings?: string | null;
  correctiveActions?: string | null;
  nextTestDue?: string | null;
}) => apiFetch<DrTest>(`/resilience/dr-tests/${id}/record`, { method: "POST", body: JSON.stringify(r) });

/** §7, §39 — page de statut : composition des diagnostics + état de reprise + maintenance, jamais un second calcul. */
export const statusPage = () => apiFetch<StatusPage>("/resilience/status-page");
