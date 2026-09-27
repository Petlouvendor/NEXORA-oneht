import { apiFetch } from "@/core/apiClient";

/** Filtre universel (Phase 7 §6). Le périmètre reste borné par la RLS serveur. */
export interface ReportFilter {
  from?: string | null;
  to?: string | null;
  preset?: "today" | "week" | "month" | "year" | null;
  establishmentId?: string | null;
}

export interface KpiValue {
  key: string;
  label: string;
  value: number;
  unit: string;
  kind: "actual" | "forecast" | "estimated";
}

export interface TrendPoint {
  date: string;
  value: number;
}

export interface ComparisonBlock {
  periodLabel: string;
  current: number;
  previous: number;
  deltaAmount: number;
  deltaPercent: number;
}

export interface AlertSummary {
  key: string;
  severity: "critical" | "warning" | "info";
  title: string;
  detail: string;
  drillPath: string | null;
  count: number;
}

export interface CommandCenterResponse {
  from: string;
  to: string;
  currency: string;
  kpis: KpiValue[];
  revenueTrend: TrendPoint[];
  comparison: ComparisonBlock;
  criticalAlerts: AlertSummary[];
  generatedAt: string;
}

export interface AlertCenterResponse {
  generatedAt: string;
  alerts: AlertSummary[];
}

function query(filter: ReportFilter): string {
  const p = new URLSearchParams();
  if (filter.preset) p.set("preset", filter.preset);
  if (filter.from) p.set("from", filter.from);
  if (filter.to) p.set("to", filter.to);
  if (filter.establishmentId) p.set("establishmentId", filter.establishmentId);
  const s = p.toString();
  return s ? `?${s}` : "";
}

export const getCommandCenter = (filter: ReportFilter = { preset: "month" }) =>
  apiFetch<CommandCenterResponse>(`/dashboard/command-center${query(filter)}`);

export const getAlerts = () => apiFetch<AlertCenterResponse>("/dashboard/alerts");

// --- NEXORA ONE §6 « TOP PERFORMANCE » (rapports Phase 7, reporting.view + reporting.view_sales) ---

export interface ProductRank { productId: string; sku: string; name: string; quantity: number; revenue: number; margin: number }
export interface SalesBreakdownRow { dimension: string; label: string; revenue: number; margin: number; marginRate: number; quantity: number; transactions: number }
export interface SalesBreakdown { dimension: string; from: string; to: string; currency: string; rows: SalesBreakdownRow[] }

export const getTopProducts = (filter: ReportFilter, take = 5) => {
  const q = query(filter);
  return apiFetch<ProductRank[]>(`/reports/sales/top-products${q}${q ? "&" : "?"}take=${take}`);
};

export const getSalesBreakdown = (dimension: "customer" | "day" | "category" | "product" | "salesperson", filter: ReportFilter) =>
  apiFetch<SalesBreakdown>(`/reports/sales/breakdown/${dimension}${query(filter)}`);
