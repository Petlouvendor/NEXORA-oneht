import { apiFetch } from "@/core/apiClient";

// Migration, importation & onboarding avancé (Phase 39 §45).

export type ImportKind = "customers" | "suppliers" | "products" | "opening_stock";

export interface ColumnProfile {
  name: string;
  detectedType: string;
  nonEmpty: number;
  empty: number;
  distinctValues: number;
  samples: string[];
  invalidFormat: number;
}
export interface ImportProfile {
  rowCount: number;
  columns: string[];
  columnProfiles: ColumnProfile[];
  duplicateRows: number;
  detectedCurrency: string | null;
  profiledAt: string;
}
export interface TargetField {
  field: string;
  required: boolean;
  type: string;
  description: string;
  isKey: boolean;
}
export interface ColumnMapping {
  targetField: string;
  sourceColumn?: string | null;
  defaultValue?: string | null;
  transform?: string | null;
}
export interface RowMessage {
  level: string;
  field: string | null;
  message: string;
}
export interface ImportRow {
  rowNumber: number;
  status: string;
  matchType: string;
  dedupKey: string | null;
  targetId: string | null;
  source: Record<string, string | null>;
  messages: RowMessage[];
}
export interface Simulation {
  rowsOk: number;
  warnings: number;
  errors: number;
  duplicates: number;
  wouldCreate: number;
  wouldUpdate: number;
  wouldReject: number;
  sample: ImportRow[];
  simulatedAt: string;
}
export interface ReconciliationLine {
  metric: string;
  sourceValue: number;
  nexoraValue: number;
  delta: number;
  ok: boolean;
}
export interface Reconciliation {
  lines: ReconciliationLine[];
  allReconciled: boolean;
  generatedAt: string;
}
export interface ImportJob {
  id: string;
  kind: string;
  sourceType: string;
  fileName: string | null;
  status: string;
  mode: string;
  batchSize: number;
  totalRows: number;
  processedRows: number;
  okRows: number;
  warningRows: number;
  errorRows: number;
  duplicateRows: number;
  createdCount: number;
  updatedCount: number;
  skippedCount: number;
  rejectedCount: number;
  profile: ImportProfile | null;
  mapping: ColumnMapping[] | null;
  simulation: Simulation | null;
  reconciliation: Reconciliation | null;
  errorMessage: string | null;
  finalizedAt: string | null;
  createdBy: string | null;
  createdAt: string;
}

export interface OnboardingItem {
  key: string;
  label: string;
  category: string;
  status: string;
  source: string;
  detail: string;
}
export interface MigrationScore {
  data: number;
  mapping: number;
  quality: number;
  accounting: number;
  ready: boolean;
}
export interface OnboardingChecklist {
  done: number;
  total: number;
  progressPct: number;
  score: MigrationScore;
  items: OnboardingItem[];
  generatedAt: string;
}

export const listImports = () => apiFetch<ImportJob[]>("/imports");
export const getImport = (id: string) => apiFetch<ImportJob>(`/imports/${id}`);
export const createImport = (body: { kind: ImportKind; sourceType: string; content: string; fileName?: string; mode?: string }) =>
  apiFetch<ImportJob>("/imports", { method: "POST", body: JSON.stringify(body) });
export const profileImport = (id: string) => apiFetch<ImportJob>(`/imports/${id}/profile`, { method: "POST" });
export const targetFields = (kind: string) => apiFetch<TargetField[]>(`/imports/target-fields/${kind}`);
export const suggestMapping = (kind: string, columns: string[]) =>
  apiFetch<ColumnMapping[]>(`/imports/suggest-mapping/${kind}`, { method: "POST", body: JSON.stringify(columns) });
export const setMapping = (id: string, mapping: ColumnMapping[], saveAsTemplate?: string) =>
  apiFetch<ImportJob>(`/imports/${id}/mapping`, { method: "POST", body: JSON.stringify({ mapping, saveAsTemplate }) });
export const validateImport = (id: string) => apiFetch<ImportJob>(`/imports/${id}/validate`, { method: "POST" });
export const simulateImport = (id: string) => apiFetch<ImportJob>(`/imports/${id}/simulate`, { method: "POST" });
export const executeImport = (id: string) => apiFetch<ImportJob>(`/imports/${id}/execute`, { method: "POST" });
export const cancelImport = (id: string) => apiFetch<ImportJob>(`/imports/${id}/cancel`, { method: "POST" });
export const importRows = (id: string, status?: string) =>
  apiFetch<ImportRow[]>(`/imports/${id}/rows${status ? `?status=${status}` : ""}`);
export const importErrors = (id: string) => apiFetch<ImportRow[]>(`/imports/${id}/errors`);
export const reconcileImport = (id: string) => apiFetch<Reconciliation>(`/imports/${id}/reconciliation`);

export const onboardingChecklist = () => apiFetch<OnboardingChecklist>("/onboarding/checklist");
export const completeOnboardingTask = (key: string, status = "done", note?: string) =>
  apiFetch<OnboardingChecklist>("/onboarding/tasks", { method: "POST", body: JSON.stringify({ key, status, note }) });
