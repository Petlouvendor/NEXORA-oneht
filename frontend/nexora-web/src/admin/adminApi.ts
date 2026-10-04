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
export const enableRole = (id: string) => apiFetch<void>(`/admin/roles/${id}/enable`, { method: "POST" });
export const updateRole = (id: string, body: { name: string; description: string | null }) =>
  apiFetch<void>(`/admin/roles/${id}`, { method: "PUT", body: JSON.stringify(body) });
export const setRolePermissions = (id: string, permissions: string[]) =>
  apiFetch<void>(`/admin/roles/${id}/permissions`, { method: "PUT", body: JSON.stringify({ permissions }) });

/** Permission du catalogue ; `grantable` = vous la détenez, donc vous pouvez l'accorder à un rôle. */
export interface PermissionEntry { code: string; name: string; module: string; action: string | null; grantable: boolean }
export const getPermissionCatalog = () => apiFetch<PermissionEntry[]>("/admin/permissions");

export const getWorkflows = () => apiFetch<Workflow[]>("/admin/workflows");
export const testWorkflow = (id: string, context: unknown) =>
  apiFetch<WorkflowTestResult>(`/admin/workflows/${id}/test`, { method: "POST", body: JSON.stringify({ context }) });
export const activateWorkflow = (id: string) =>
  apiFetch<void>(`/admin/workflows/${id}/activate`, { method: "POST" });
export const setWorkflowStatus = (id: string, status: "inactive" | "draft") =>
  apiFetch<void>(`/admin/workflows/${id}/status`, { method: "POST", body: JSON.stringify({ status }) });
export const upsertWorkflow = (body: { code: string; name: string; triggerEvent: string; definition: WorkflowDefinition }) =>
  apiFetch<string>("/admin/workflows", { method: "POST", body: JSON.stringify(body) });

export interface WorkflowTrigger { key: string; label: string; automatic: boolean; detail: string }
export interface WorkflowCatalog { triggers: WorkflowTrigger[]; approverRoles: Array<{ name: string; description: string | null }> }
export const getWorkflowCatalog = () => apiFetch<WorkflowCatalog>("/admin/workflows/catalog");

export const getAutomations = () => apiFetch<Automation[]>("/admin/automations");
export const runAutomation = (id: string, preview: boolean) =>
  apiFetch<AutomationRun>(`/admin/automations/${id}/run?preview=${preview}`, { method: "POST" });
export const getAutomationRuns = (id: string) => apiFetch<AutomationRun[]>(`/admin/automations/${id}/runs`);
export const upsertAutomation = (body: { code: string; name: string; kind: string; schedule: Record<string, unknown>; parameters: Record<string, unknown> }) =>
  apiFetch<string>("/admin/automations", { method: "POST", body: JSON.stringify(body) });
export const setAutomationStatus = (id: string, status: "active" | "paused") =>
  apiFetch<void>(`/admin/automations/${id}/status`, { method: "POST", body: JSON.stringify({ status }) });

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

export interface Assignment {
  id: string; userId: string; roleName: string; scopeType: string; groupId: string | null; companyId: string | null;
  establishmentId: string | null; validFrom: string | null; validUntil: string | null; reason: string | null;
  active: boolean; grantedAt: string; revokedAt: string | null;
}
export const listAssignments = (userId: string) => apiFetch<Assignment[]>(`/users/${userId}/assignments`);
export const grantAssignment = (userId: string, body: {
  roleName: string; scopeType: "COMPANY"; companyId: string | null; establishmentId: null;
  validFrom: string | null; validUntil: string | null; reason: string | null;
}) => apiFetch<string>(`/users/${userId}/assignments`, { method: "POST", body: JSON.stringify(body) });
export const revokeAssignment = (assignmentId: string, reason: string) =>
  apiFetch<void>(`/users/assignments/${assignmentId}/revoke`, { method: "POST", body: JSON.stringify({ reason }) });
