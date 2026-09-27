import { apiFetch } from "@/core/apiClient";

// Business Intelligence, Reporting & Pilotage (Phase 42 §33-41, §48-50, §55).

export interface KpiDefinition {
  id: string; key: string; label: string; category: string; unit: string; source: string;
  formula: string; kind: "actual" | "forecast" | "estimated"; owner: string; version: number; updatedAt: string;
}

export interface Objective {
  id: string; companyId: string; establishmentId: string | null; kpiKey: string; periodType: string;
  targetValue: number; direction: "min" | "max"; weight: number;
  effectiveFrom: string; effectiveTo: string | null; status: string;
}
export interface ObjectiveScoreItem { kpiKey: string; label: string; target: number; actual: number; ratioPct: number; weight: number }
export interface ObjectiveScore { score: number; grade: string; items: ObjectiveScoreItem[]; evaluatedAt: string }

export interface ReportCatalogEntry {
  key: string; category: string; name: string; description: string; source: string;
  rolesAllowed: string[]; runnableGeneric: boolean; endpoint: string;
}
export interface SavedReport {
  id: string; name: string; reportKey: string; filter: Record<string, unknown>; isShared: boolean; mine: boolean; updatedAt: string;
}
export interface ReportSchedule {
  id: string; name: string; reportKey: string; frequency: string; filter: Record<string, unknown>;
  format: string; recipients: string[]; status: string; lastRunAt: string | null; nextRunAt: string | null;
}
export interface ReportExecution {
  id: string; reportScheduleId: string | null; reportKey: string; format: string; status: string;
  rowCount: number | null; error: string | null; startedAt: string; finishedAt: string | null;
}

export interface ForecastPoint { date: string; value: number }
export interface Forecast {
  id: string; metric: string; method: string; scenario: string; adjustmentPct: number; horizonDays: number;
  confidenceLevel: "low" | "medium" | "high"; assumptions: string; points: ForecastPoint[]; generatedAt: string;
}

export interface Insight {
  id: string; category: string; severity: "info" | "warning" | "critical"; title: string; detail: string;
  evidence: Record<string, unknown>; drillPath: string | null; suggestedAction: string | null;
  periodStart: string; status: "open" | "dismissed" | "actioned"; assignedTo: string | null; dueAt: string | null; createdAt: string;
}

export interface Budget {
  id: string; name: string; fiscalYear: number; currencyCode: string; status: string;
  revisedFrom: string | null; approvedBy: string | null; approvedAt: string | null; updatedAt: string;
}
export interface BudgetLine { id: string; accountId: string | null; costCenterId: string | null; establishmentId: string | null; periodMonth: number | null; amount: number }

// --- §48, §50 Catalogue KPI gouverné ---
export const listKpiDefinitions = () => apiFetch<KpiDefinition[]>("/kpi-definitions");

// --- §39-40 Objectifs & score ---
export const listObjectives = () => apiFetch<Objective[]>("/objectives");
export const setObjective = (body: { id?: string | null; establishmentId?: string | null; kpiKey: string; periodType: string; targetValue: number; direction?: string; weight?: number }) =>
  apiFetch<Objective>("/objectives", { method: "POST", body: JSON.stringify(body) });
export const deleteObjective = (id: string) => apiFetch<void>(`/objectives/${id}`, { method: "DELETE" });
export const getObjectiveScore = () => apiFetch<ObjectiveScore>("/objectives/score");

// --- §48-49 Catalogue de rapports & rapports personnalisés ---
export const listReports = () => apiFetch<{ standard: ReportCatalogEntry[]; saved: SavedReport[] }>("/reports");
export const saveReport = (body: { id?: string | null; name: string; reportKey: string; filter: Record<string, unknown>; isShared?: boolean }) =>
  apiFetch<SavedReport>("/reports", { method: "POST", body: JSON.stringify(body) });
export const deleteReport = (id: string) => apiFetch<void>(`/reports/${id}`, { method: "DELETE" });
export const runReport = (id: string) => apiFetch<{ reportKey: string; ranAt: string; result: unknown }>(`/reports/${id}/run`, { method: "POST" });

// --- §41 Rapports planifiés ---
export const listSchedules = () => apiFetch<ReportSchedule[]>("/reports/schedules");
export const saveSchedule = (body: { id?: string | null; name: string; reportKey: string; frequency: string; format?: string; recipients: string[] }) =>
  apiFetch<ReportSchedule>("/reports/schedules", { method: "POST", body: JSON.stringify(body) });
export const deleteSchedule = (id: string) => apiFetch<void>(`/reports/schedules/${id}`, { method: "DELETE" });
export const listExecutions = () => apiFetch<ReportExecution[]>("/reports/executions");

// --- §35-36 Prévisions ---
export const listForecasts = (metric?: string) => apiFetch<Forecast[]>(`/forecasts${metric ? `?metric=${metric}` : ""}`);
export const generateForecast = (body: { metric: string; horizonDays?: number; scenario?: string; adjustmentPct?: number }) =>
  apiFetch<Forecast>("/forecasts", { method: "POST", body: JSON.stringify(body) });

// --- §37-38, §58 Insights ---
export const listInsights = (status?: string) => apiFetch<Insight[]>(`/insights${status ? `?status=${status}` : ""}`);
export const dismissInsight = (id: string, reason?: string) => apiFetch<void>(`/insights/${id}/dismiss`, { method: "POST", body: JSON.stringify({ reason }) });
export const assignInsight = (id: string, userId: string, dueAt?: string) =>
  apiFetch<Insight>(`/insights/${id}/assign`, { method: "POST", body: JSON.stringify({ userId, dueAt }) });

// --- §33-34 Budgets ---
export const listBudgets = () => apiFetch<Budget[]>("/budgets");
export const createBudget = (body: { name: string; fiscalYear: number; currencyCode: string }) =>
  apiFetch<Budget>("/budgets", { method: "POST", body: JSON.stringify(body) });
export const getBudgetLines = (id: string) => apiFetch<BudgetLine[]>(`/budgets/${id}/lines`);
export const setBudgetLines = (id: string, lines: Array<{ accountId?: string | null; costCenterId?: string | null; establishmentId?: string | null; periodMonth?: number | null; amount: number }>) =>
  apiFetch<BudgetLine[]>(`/budgets/${id}/lines`, { method: "PUT", body: JSON.stringify({ lines }) });
export const submitBudget = (id: string) => apiFetch<Budget>(`/budgets/${id}/submit`, { method: "POST" });
export const approveBudget = (id: string) => apiFetch<Budget>(`/budgets/${id}/approve`, { method: "POST" });
export const closeBudget = (id: string) => apiFetch<Budget>(`/budgets/${id}/close`, { method: "POST" });
export const reviseBudget = (id: string) => apiFetch<Budget>(`/budgets/${id}/revise`, { method: "POST" });
