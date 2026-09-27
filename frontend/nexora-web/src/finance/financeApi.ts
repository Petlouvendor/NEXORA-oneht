import { apiFetch } from "@/core/apiClient";

// Finance & trésorerie — cockpit de pilotage (Phase 28 §27-28, §22-23, §32, §38).

export interface FinancialAccount {
  accountId: string;
  name: string;
  accountType: string;
  currency: string;
  systemBalance: number;
  baseBalance: number;
  status: string;
}

export interface TreasuryPosition {
  asOf: string;
  currency: string;
  available: number;
  cash: number;
  bank: number;
  mobileMoney: number;
  other: number;
  pending: number;
  suspenseExposure: number;
  nearReceivables: number;
  nearPayables: number;
  netPosition: number;
  horizonDays: number;
  accounts: FinancialAccount[];
}

export interface Liquidity {
  asOf: string;
  currency: string;
  horizonDays: number;
  immediateLiquidity: number;
  expectedReceivables: number;
  shortTermCommitments: number;
  projectedGap: number;
  alerts: string[];
}

export interface MaturityBucket {
  label: string;
  dayFrom: number;
  dayTo: number;
  inflow: number;
  outflow: number;
  net: number;
  projectedBalance: number;
}

export interface CashflowForecast {
  asOf: string;
  currency: string;
  openingBalance: number;
  buckets: MaturityBucket[];
}

export interface SuspenseItem {
  id: string;
  financialAccountId: string | null;
  accountName: string | null;
  kind: string;
  amount: number;
  currency: string;
  reference: string | null;
  reason: string;
  status: string;
  resolutionDueDate: string | null;
  overdue: boolean;
  responsibleUserName: string | null;
  resolution: string | null;
  createdAt: string;
  resolvedAt: string | null;
}

export interface FinancialCloseControl {
  key: string;
  label: string;
  status: "pass" | "warn" | "fail";
  detail: string | null;
  count: number;
  blocking: boolean;
}

export interface FinancialCloseReport {
  periodId: string;
  periodName: string;
  asOf: string;
  status: string;
  canClose: boolean;
  controls: FinancialCloseControl[];
  generatedAt: string;
}

export interface AccountingPeriod {
  id: string;
  name: string;
  startDate: string;
  endDate: string;
  status: string;
}

export const listPeriods = () => apiFetch<AccountingPeriod[]>("/periods");
export const getFinancialAccounts = () => apiFetch<FinancialAccount[]>("/financial-accounts");
export const getPosition = () => apiFetch<TreasuryPosition>("/treasury/position");
export const getLiquidity = (horizonDays?: number) =>
  apiFetch<Liquidity>(`/treasury/liquidity${horizonDays ? `?horizonDays=${horizonDays}` : ""}`);
export const getForecast = () => apiFetch<CashflowForecast>("/cashflow/forecast");

export const listSuspense = (status?: string, onlyOverdue = false) =>
  apiFetch<SuspenseItem[]>(
    `/treasury/suspense?onlyOverdue=${onlyOverdue}${status ? `&status=${status}` : ""}`,
  );

export const openSuspense = (input: {
  financialAccountId?: string | null;
  kind: string;
  amount: number;
  currency: string;
  reason: string;
  reference?: string | null;
  resolutionDueDate?: string | null;
}) =>
  apiFetch<string>("/treasury/suspense", {
    method: "POST",
    body: JSON.stringify({ responsibleUserId: null, ...input }),
  });

export const resolveSuspense = (id: string, outcome: string, resolution: string) =>
  apiFetch<void>(`/treasury/suspense/${id}/resolve`, {
    method: "POST",
    body: JSON.stringify({ outcome, resolution }),
  });

export const previewFinancialClose = (periodId: string) =>
  apiFetch<FinancialCloseReport>(`/periods/${periodId}/financial-close`);

export const completeFinancialClose = (periodId: string, force: boolean, note?: string) =>
  apiFetch<{ closeId: string; forced: boolean }>(`/periods/${periodId}/financial-close`, {
    method: "POST",
    body: JSON.stringify({ force, note: note ?? null }),
  });
