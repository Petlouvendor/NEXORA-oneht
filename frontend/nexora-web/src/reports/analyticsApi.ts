import { apiFetch } from "@/core/apiClient";

// Reporting & analytics — couche de confiance (Phase 31 §44-46).

export interface ReconciliationLine {
  metric: string;
  label: string;
  definition: string;
  operationalValue: number;
  accountingValue: number;
  difference: number;
  reconciled: boolean;
  note: string | null;
}

export interface ReconciliationReport {
  from: string;
  to: string;
  currency: string;
  tolerance: number;
  allReconciled: boolean;
  reconciledCount: number;
  totalCount: number;
  lines: ReconciliationLine[];
  generatedAt: string;
}

export interface DataQualityCheck {
  key: string;
  label: string;
  category: string;
  severity: "info" | "warn" | "error";
  count: number;
  detail: string | null;
  sampleIds: string[];
}

export interface DataQualityReport {
  score: number;
  grade: string;
  checksRun: number;
  checksFailed: number;
  checks: DataQualityCheck[];
  generatedAt: string;
}

export interface AnalyticsSnapshot {
  id: string;
  kind: string;
  label: string | null;
  asOfDate: string;
  periodFrom: string | null;
  periodTo: string | null;
  calculationVersion: string;
  score: number | null;
  createdAt: string;
}

export const getReconciliation = (from?: string, to?: string) => {
  const qs = new URLSearchParams();
  if (from) qs.set("from", from);
  if (to) qs.set("to", to);
  const s = qs.toString();
  return apiFetch<ReconciliationReport>(`/analytics/reconciliation${s ? `?${s}` : ""}`);
};

export const getDataQuality = () => apiFetch<DataQualityReport>("/analytics/data-quality");

export const listSnapshots = (kind?: string) =>
  apiFetch<AnalyticsSnapshot[]>(`/analytics/snapshots${kind ? `?kind=${kind}` : ""}`);

export const getSnapshot = (id: string) =>
  apiFetch<{ header: AnalyticsSnapshot; result: unknown }>(`/analytics/snapshots/${id}`);

export const createSnapshot = (kind: string, label: string | null) =>
  apiFetch<AnalyticsSnapshot>("/analytics/snapshots", {
    method: "POST",
    body: JSON.stringify({ kind, label, from: null, to: null }),
  });

export interface Kpi {
  key: string;
  label: string;
  value: number;
  currency: string;
  kind: string;
}
export const getKpis = () => apiFetch<{ kpis: Kpi[] }>("/kpis");
