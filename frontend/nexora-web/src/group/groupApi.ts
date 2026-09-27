import { apiFetch } from "@/core/apiClient";

/** Toutes les routes groupe exigent l'en-tête X-Group-Scope: true (Phase 9 §21-22). */
const GROUP_HEADERS = { "X-Group-Scope": "true" } as const;

export interface GroupCompanyStatus {
  companyId: string;
  companyName: string;
  sector: string | null;
  status: string;
  latestPeriod: string;
  latestPeriodStatus: string;
  unmappedAccounts: number;
  unreconciledIntercompany: number;
  receivables: number;
  payables: number;
}

export interface GroupControlCenter {
  groupId: string;
  currency: string;
  generatedAt: string;
  companies: GroupCompanyStatus[];
  unreconciledIntercompany: number;
  runsReadyForValidation: number;
  openTasks: number;
  groupReceivables: number;
  groupPayables: number;
}

export interface ConsolidationCompany {
  companyId: string;
  companyName: string;
  included: boolean;
  functionalCurrency: string;
  fxRate: number;
  periodClosed: boolean;
  mappingComplete: boolean;
  contributedDebit: number;
  contributedCredit: number;
}

export interface ConsolidationRun {
  id: string;
  periodLabel: string;
  fromDate: string;
  toDate: string;
  currency: string;
  status: string;
  coherenceOk: boolean;
  totalDebit: number;
  totalCredit: number;
  eliminationTotal: number;
  diagnostics: string[];
  companies: ConsolidationCompany[];
  generatedAt: string | null;
  validatedAt: string | null;
  publishedAt: string | null;
}

export interface GroupTask {
  id: string;
  companyId: string | null;
  companyName: string | null;
  title: string;
  description: string | null;
  assigneeUserId: string | null;
  priority: string;
  dueDate: string | null;
  status: string;
  notes: string | null;
}

const g = (path: string, init: RequestInit = {}) =>
  apiFetch(path, { ...init, headers: { ...GROUP_HEADERS, ...(init.headers ?? {}) } });

export const getControlCenter = (groupId: string) =>
  g(`/groups/${groupId}/control-center`) as Promise<GroupControlCenter>;

export const listRuns = (groupId: string) =>
  g(`/groups/${groupId}/consolidation/runs`) as Promise<ConsolidationRun[]>;

export const executeRun = (groupId: string, runId: string) =>
  g(`/groups/${groupId}/consolidation/runs/${runId}/execute`, { method: "POST" }) as Promise<ConsolidationRun>;

export const validateRun = (groupId: string, runId: string) =>
  g(`/groups/${groupId}/consolidation/runs/${runId}/validate`, { method: "POST" }) as Promise<ConsolidationRun>;

export const publishRun = (groupId: string, runId: string) =>
  g(`/groups/${groupId}/consolidation/runs/${runId}/publish`, { method: "POST" }) as Promise<ConsolidationRun>;

export const listTasks = (groupId: string) =>
  g(`/groups/${groupId}/tasks`) as Promise<GroupTask[]>;
