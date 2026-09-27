import { apiFetch } from "@/core/apiClient";

export interface SettingDefinition {
  key: string; category: string; valueType: string;
  defaultValue: unknown; allowedValues: unknown[];
  maxScope: string; isSensitive: boolean; affectsFuture: boolean; description: string;
}
export interface EffectiveSetting { key: string; category: string; value: unknown; source: string }
export interface SetSettingResult { effective: EffectiveSetting; warnings: string[] }

export interface AdminRole {
  id: string; name: string; description: string | null; systemRole: boolean;
  status: string; assignmentCount: number; permissions: string[];
}

export interface WorkflowCondition { field: string; op: string; value: unknown }
export interface WorkflowDefinition {
  logic: "all" | "any"; conditions: WorkflowCondition[];
  approval: { levels: number; approverRoleCode: string | null } | null;
  actions: string[]; notifications: string[];
}
export interface Workflow {
  id: string; code: string; name: string; triggerEvent: string;
  definition: WorkflowDefinition; status: string; isTested: boolean; lastTestedAt: string | null;
}
export interface ConditionEval { field: string; op: string; expected: unknown; actual: unknown; passed: boolean }
export interface WorkflowTestResult {
  matched: boolean; logic: string; conditions: ConditionEval[];
  plannedApprovals: string[]; plannedActions: string[]; plannedNotifications: string[];
}

export interface Automation {
  id: string; code: string; name: string; kind: string;
  schedule: Record<string, unknown>; parameters: Record<string, unknown>;
  status: string; lastRunAt: string | null; nextRunAt: string | null;
}
export interface AutomationRun {
  id: string; automationId: string; mode: string; status: string;
  itemsProcessed: number; result: Record<string, unknown>; error: string | null;
  startedAt: string; finishedAt: string | null;
}

export interface ConfigVersion {
  id: string; domain: string; versionNumber: number; status: string;
  effectiveFrom: string | null; effectiveTo: string | null;
  diff: Record<string, unknown>; note: string | null; createdAt: string;
}

export interface AdminTask {
  key: string; category: string; severity: string; title: string; detail: string; count: number;
}

export const getTasks = () => apiFetch<AdminTask[]>("/admin/tasks");
export const getDefinitions = () => apiFetch<SettingDefinition[]>("/admin/settings/definitions");
export const getEffectiveSettings = () => apiFetch<EffectiveSetting[]>("/admin/settings");
export const setSetting = (body: { key: string; scope: string; scopeId: string | null; value: unknown; note: string | null }) =>
  apiFetch<SetSettingResult>("/admin/settings", { method: "PUT", body: JSON.stringify(body) });

export const getRoles = () => apiFetch<AdminRole[]>("/admin/roles");
export const createRole = (body: { name: string; description: string | null; baseRoleId: string | null; permissions: string[] }) =>
  apiFetch<string>("/admin/roles", { method: "POST", body: JSON.stringify(body) });
export const disableRole = (id: string, force: boolean) =>
  apiFetch<void>(`/admin/roles/${id}/disable?force=${force}`, { method: "POST" });

export const getWorkflows = () => apiFetch<Workflow[]>("/admin/workflows");
export const testWorkflow = (id: string, context: unknown) =>
  apiFetch<WorkflowTestResult>(`/admin/workflows/${id}/test`, { method: "POST", body: JSON.stringify({ context }) });
export const activateWorkflow = (id: string) =>
  apiFetch<void>(`/admin/workflows/${id}/activate`, { method: "POST" });

export const getAutomations = () => apiFetch<Automation[]>("/admin/automations");
export const runAutomation = (id: string, preview: boolean) =>
  apiFetch<AutomationRun>(`/admin/automations/${id}/run?preview=${preview}`, { method: "POST" });
export const getAutomationRuns = (id: string) => apiFetch<AutomationRun[]>(`/admin/automations/${id}/runs`);

export const getConfigVersions = (domain: string) => apiFetch<ConfigVersion[]>(`/admin/config-versions?domain=${domain}`);
export const snapshotConfig = (domain: string, note?: string) =>
  apiFetch<ConfigVersion>(`/admin/config-versions/snapshot?domain=${domain}${note ? `&note=${encodeURIComponent(note)}` : ""}`, { method: "POST" });
export const activateConfigVersion = (id: string) =>
  apiFetch<ConfigVersion>(`/admin/config-versions/${id}/activate`, { method: "POST", body: JSON.stringify({ effectiveFrom: null, note: null }) });

// --- Utilisateurs (Phase 22 API ; écran ajouté par la recette Phase 55 RBAC-001, AUTH-009) -------

export interface UserSummary {
  id: string; email: string; fullName: string | null; status: string; lastLoginAt: string | null; mfaEnabled: boolean;
}

export const listUsers = () => apiFetch<UserSummary[]>("/users");

/** Crée le compte ET sa première affectation (un compte n'existe jamais sans rôle). */
export const createUser = (body: {
  email: string; fullName: string; password: string;
  initialAssignment: { roleName: string; scopeType: "COMPANY"; companyId: string | null; reason: string | null };
}) => apiFetch<string>("/users", { method: "POST", body: JSON.stringify(body) });

/** active | suspended | locked — suspendre fait tomber immédiatement toutes les sessions du compte. */
export const setUserStatus = (id: string, status: "active" | "suspended") =>
  apiFetch<void>(`/users/${id}/status`, { method: "POST", body: JSON.stringify({ status }) });
