import { apiFetch } from "@/core/apiClient";

// Qualité, exploitation & préparation production (Phase 37 §29, §40, §54, §56).

export interface ComponentStatus {
  component: string;
  status: "ok" | "degraded" | "down";
  detail: string;
}

export interface SystemStatus {
  overall: "ok" | "degraded" | "down";
  checkedAt: string;
  components: ComponentStatus[];
}

export interface GoLiveCheck {
  key: string;
  label: string;
  category: string;
  status: "pass" | "warn" | "fail" | "manual";
  source: "auto" | "manual";
  detail: string;
}

export interface GoLiveReadiness {
  canGoLive: boolean;
  passed: number;
  warnings: number;
  failed: number;
  manual: number;
  checks: GoLiveCheck[];
  evaluatedAt: string;
}

export interface GoLiveSignOff {
  id: string;
  environment: string;
  verdict: string;
  canGoLive: boolean;
  forced: boolean;
  forceReason: string | null;
  notes: string | null;
  signedBy: string | null;
  signedAt: string;
  checklistSnapshot: GoLiveReadiness;
}

export interface Deployment {
  id: string;
  version: string;
  environment: string;
  status: string;
  schemaVersion: string | null;
  notes: string | null;
  rolledBackFrom: string | null;
  rollbackReason: string | null;
  startedAt: string;
  finishedAt: string | null;
  createdBy: string | null;
}

export const systemStatus = () => apiFetch<SystemStatus>("/system/status");

export const goLiveReadiness = () => apiFetch<GoLiveReadiness>("/go-live");
export const listSignOffs = () => apiFetch<GoLiveSignOff[]>("/go-live/sign-offs");
export const signOff = (body: {
  environment: string;
  verdict: string;
  notes?: string | null;
  force?: boolean;
  forceReason?: string | null;
}) => apiFetch<GoLiveSignOff>("/go-live/sign-off", { method: "POST", body: JSON.stringify(body) });

export const listDeployments = () => apiFetch<Deployment[]>("/deployments");
export const recordDeployment = (body: { version: string; environment?: string; notes?: string | null }) =>
  apiFetch<Deployment>("/deployments", { method: "POST", body: JSON.stringify(body) });
export const rollbackDeployment = (id: string, body: { reason: string; toVersion?: string | null }) =>
  apiFetch<Deployment>(`/deployments/${id}/rollback`, { method: "POST", body: JSON.stringify(body) });

// =============================================================================
// Administration opérationnelle & maintenance (Phase 41 §7-9, §10-12, §16-19, §23-24, §27, §44).
// =============================================================================

export interface JobStatus {
  key: string; name: string; category: "business" | "technical"; description: string;
  enabled: boolean; owner: string;
  lastRunAt: string | null; lastFinishedAt: string | null; lastStatus: string | null;
  lastAffectedCount: number | null; lastError: string | null;
  nextRunEstimate: string | null; successRatePct: number | null; runsLast30Days: number;
}
export interface QueueStats {
  queue: string; pending: number; failed: number; dead: number;
  deliveredLastHour: number; oldestPendingAgeMinutes: number | null;
}
export interface DeadLetterItem {
  id: string; queue: string; subject: string; cause: string | null; attempts: number;
  createdAt: string; lastAttemptAt: string | null; companyId: string | null;
}
export interface DiagnosticCheck { key: string; label: string; status: "ok" | "warn" | "fail" | "skipped"; detail: string; durationMs: number | null }
export interface StorageStats { databaseSizeBytes: number; backupCount: number; backupTotalSizeBytes: number; documentCount: number; measuredAt: string }
export interface TableSize { table: string; sizeBytes: number; rowEstimate: number }
export interface DatabaseMaintenance { databaseSizeBytes: number; activeConnections: number; longRunningTransactions: number; locks: number; topTables: TableSize[]; measuredAt: string }
export interface FeatureFlag {
  id: string; key: string; scope: "global" | "company" | "user"; companyId: string | null; userId: string | null;
  enabled: boolean; description: string | null; expiresAt: string | null; updatedAt: string;
}
export interface MaintenanceWindow {
  id: string; scheduledAt: string; durationMinutes: number; impact: string; services: string[];
  responsible: string | null; rollbackPlan: string | null; status: string;
  startedAt: string | null; endedAt: string | null; notes: string | null;
}
export interface RetentionPolicy { id: string; dataType: string; companyId: string | null; durationDays: number; action: "archive" | "delete"; legalReference: string | null; updatedAt: string }
export interface PurgeRun { id: string; dataType: string; companyId: string | null; dryRun: boolean; cutoffDate: string; objectCount: number; protectedExcludedCount: number; status: string; executedBy: string | null; executedAt: string | null }
export interface DataCorrection {
  id: string; tableName: string; recordId: string; fieldName: string; oldValue: string | null; newValue: string;
  reason: string; status: string; requestedBy: string; approvedBy: string | null; notes: string | null;
  appliedAt: string | null; createdAt: string;
}

export const listJobs = () => apiFetch<JobStatus[]>("/ops/jobs");
export const runJobNow = (key: string) => apiFetch<unknown>(`/ops/jobs/${encodeURIComponent(key)}/run`, { method: "POST" });
export const pauseJob = (key: string) => apiFetch<void>(`/ops/jobs/${encodeURIComponent(key)}/pause`, { method: "POST" });
export const resumeJob = (key: string) => apiFetch<void>(`/ops/jobs/${encodeURIComponent(key)}/resume`, { method: "POST" });

export const queueStats = () => apiFetch<QueueStats[]>("/ops/queues");
export const deadLetters = () => apiFetch<DeadLetterItem[]>("/ops/dead-letters");
export const replayDeadLetter = (id: string) => apiFetch<void>(`/ops/dead-letters/${id}/replay`, { method: "POST" });
export const deleteDeadLetter = (id: string) => apiFetch<void>(`/ops/dead-letters/${id}`, { method: "DELETE" });

export const diagnostics = () => apiFetch<DiagnosticCheck[]>("/ops/diagnostics");
export const storageStats = () => apiFetch<StorageStats>("/ops/storage");
export const databaseMaintenance = () => apiFetch<DatabaseMaintenance>("/ops/database");
export const invalidateCache = () => apiFetch<void>("/ops/cache/invalidate", { method: "POST" });

export const listFeatureFlags = () => apiFetch<FeatureFlag[]>("/ops/feature-flags");
export const saveFeatureFlag = (body: { key: string; scope: string; companyId?: string | null; userId?: string | null; enabled: boolean; description?: string | null }) =>
  apiFetch<FeatureFlag>("/ops/feature-flags", { method: "POST", body: JSON.stringify(body) });
export const deleteFeatureFlag = (id: string) => apiFetch<void>(`/ops/feature-flags/${id}`, { method: "DELETE" });

export const listMaintenanceWindows = () => apiFetch<MaintenanceWindow[]>("/ops/maintenance");
export const scheduleMaintenance = (body: { scheduledAt: string; durationMinutes: number; impact: string; services: string[]; responsible?: string | null; rollbackPlan?: string | null }) =>
  apiFetch<MaintenanceWindow>("/ops/maintenance", { method: "POST", body: JSON.stringify(body) });
export const transitionMaintenance = (id: string, status: string, notes?: string) =>
  apiFetch<MaintenanceWindow>(`/ops/maintenance/${id}/transition`, { method: "POST", body: JSON.stringify({ status, notes }) });

export const listRetentionPolicies = () => apiFetch<RetentionPolicy[]>("/ops/retention-policies");
export const previewPurge = (dataType: string) => apiFetch<PurgeRun>("/ops/purge/preview", { method: "POST", body: JSON.stringify({ dataType }) });
export const executePurge = (dataType: string) => apiFetch<PurgeRun>("/ops/purge/execute", { method: "POST", body: JSON.stringify({ dataType }) });
export const purgeHistory = () => apiFetch<PurgeRun[]>("/ops/purge/history");

export const listDataCorrections = (status?: string) => apiFetch<DataCorrection[]>(`/ops/data-corrections${status ? `?status=${status}` : ""}`);
export const requestDataCorrection = (body: { tableName: string; recordId: string; fieldName: string; newValue: string; reason: string }) =>
  apiFetch<DataCorrection>("/ops/data-corrections", { method: "POST", body: JSON.stringify(body) });
export const decideDataCorrection = (id: string, approve: boolean, notes?: string) =>
  apiFetch<DataCorrection>(`/ops/data-corrections/${id}/decide`, { method: "POST", body: JSON.stringify({ approve, notes }) });
