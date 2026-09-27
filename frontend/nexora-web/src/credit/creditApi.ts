import { apiFetch } from "@/core/apiClient";

export interface CollectionsRow {
  customerId: string;
  customerName: string;
  currency: string;
  totalOutstanding: number;
  current: number;
  bucket1To30: number;
  bucket31To60: number;
  bucket61To90: number;
  bucket90Plus: number;
  disputedAmount: number;
  openPromises: number;
  openDisputes: number;
  blocked: boolean;
  oldestOverdueDays: number;
}

export interface AgingSummary {
  currency: string;
  total: number;
  current: number;
  bucket1To30: number;
  bucket31To60: number;
  bucket61To90: number;
  bucket90Plus: number;
  totalOverdue: number;
  dso: number | null;
}

export interface Promise_ {
  id: string;
  customerId: string;
  customerName: string;
  amount: number;
  keptAmount: number;
  promisedDate: string;
  channel: string;
  status: string;
  note: string | null;
  receivableIds: string[];
  createdAt: string;
}

export interface Dispute {
  id: string;
  customerId: string;
  customerName: string;
  receivableId: string | null;
  disputedAmount: number;
  reason: string;
  status: string;
  resolution: string | null;
  createdAt: string;
  resolvedAt: string | null;
}

export interface CustomerBlock {
  id: string;
  customerId: string;
  customerName: string;
  blockType: string;
  reason: string;
  isActive: boolean;
  blockedAt: string;
  releasedAt: string | null;
  releaseReason: string | null;
}

export interface CustomerStatement {
  customerId: string;
  customerName: string;
  currency: string;
  openingBalance: number;
  closingBalance: number;
  totalOverdue: number;
  lines: Array<{
    date: string;
    documentType: string;
    documentNumber: string;
    reference: string | null;
    charge: number;
    payment: number;
    runningBalance: number;
  }>;
}

export const getCollections = (onlyOverdue = true) =>
  apiFetch<CollectionsRow[]>(`/credit/collections?onlyOverdue=${onlyOverdue}`);
export const getAging = () => apiFetch<AgingSummary>("/credit/aging");
export const getStatement = (customerId: string) =>
  apiFetch<CustomerStatement>(`/credit/statements/${customerId}`);

export const listPromises = (status?: string) =>
  apiFetch<Promise_[]>(`/credit/promises${status ? `?status=${status}` : ""}`);
export const createPromise = (customerId: string, amount: number, promisedDate: string, channel: string, receivableIds: string[]) =>
  apiFetch<string>("/credit/promises", { method: "POST", body: JSON.stringify({ customerId, amount, promisedDate, channel, receivableIds, note: null }) });
export const settlePromise = (id: string) => apiFetch<string>(`/credit/promises/${id}/settle`, { method: "POST" });

export const listDisputes = (status?: string) =>
  apiFetch<Dispute[]>(`/credit/disputes${status ? `?status=${status}` : ""}`);
export const openDispute = (customerId: string, receivableId: string | null, disputedAmount: number, reason: string) =>
  apiFetch<string>("/credit/disputes", { method: "POST", body: JSON.stringify({ customerId, receivableId, disputedAmount, reason }) });
export const resolveDispute = (id: string, outcome: string, resolution: string) =>
  apiFetch<void>(`/credit/disputes/${id}/resolve`, { method: "POST", body: JSON.stringify({ outcome, resolution }) });

export const listBlocks = (onlyActive = true) =>
  apiFetch<CustomerBlock[]>(`/credit/blocks?onlyActive=${onlyActive}`);
export const blockCustomer = (customerId: string, blockType: string, reason: string) =>
  apiFetch<string>("/credit/blocks", { method: "POST", body: JSON.stringify({ customerId, blockType, reason }) });
export const releaseBlock = (id: string, reason: string) =>
  apiFetch<void>(`/credit/blocks/${id}/release`, { method: "POST", body: JSON.stringify({ reason }) });

const uuid = () =>
  typeof crypto !== "undefined" && "randomUUID" in crypto ? crypto.randomUUID() : `op-${Date.now()}`;

export const writeOffReceivable = (receivableId: string, amount: number, reason: string) =>
  apiFetch<{ writeOffId: string; journalEntryId: string }>(`/credit/receivables/${receivableId}/write-off`, {
    method: "POST",
    idempotencyKey: uuid(),
    body: JSON.stringify({ operationUuid: uuid(), amount, reason }),
  });
