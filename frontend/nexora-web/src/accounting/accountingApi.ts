import { apiFetch } from "@/core/apiClient";

// Comptabilité générale — workflow manuel & clôture d'exercice (Phase 29 §9-19, §51).

export interface Journal {
  id: string;
  code: string;
  name: string;
  journalType: string;
  status: string;
}

export interface Account {
  id: string;
  code: string;
  name: string;
  accountType: string;
  isPostable: boolean;
  status: string;
  parentAccountId: string | null;
  currency: string | null;
}

export interface AccountingPeriod {
  id: string;
  name: string;
  startDate: string;
  endDate: string;
  status: string;
}

export interface JournalEntryLine {
  lineNumber: number;
  accountCode: string;
  accountName: string;
  description: string | null;
  debit: number;
  credit: number;
  baseDebit: number;
  baseCredit: number;
  thirdPartyType: string | null;
  thirdPartyId: string | null;
  costCenterId: string | null;
}

export interface JournalEntry {
  id: string;
  journalCode: string;
  entryNumber: string;
  entryDate: string;
  status: string;
  origin: string;
  description: string | null;
  currency: string;
  totalDebit: number;
  totalCredit: number;
  isBalanced: boolean;
  reversalOfEntryId: string | null;
  postedAt: string | null;
  createdAt: string;
  lines: JournalEntryLine[];
}

export interface AccountingControl {
  key: string;
  label: string;
  status: "pass" | "warn" | "fail";
  detail: string | null;
}

export interface YearEndControl extends AccountingControl {
  count: number;
  blocking: boolean;
}

export interface YearEndCloseReport {
  periodId: string;
  periodName: string;
  endDate: string;
  status: string;
  canClose: boolean;
  revenue: number;
  expenses: number;
  netResult: number;
  currency: string;
  controls: YearEndControl[];
  generatedAt: string;
}

export interface TrialBalanceRow {
  accountCode: string;
  accountName: string;
  accountType: string;
  debit: number;
  credit: number;
}
export interface TrialBalance {
  asOf: string;
  currency: string;
  rows: TrialBalanceRow[];
  totalDebit: number;
  totalCredit: number;
}

export interface DraftLineInput {
  accountCode: string;
  debit: number;
  credit: number;
  description?: string | null;
}

const uuid = () =>
  typeof crypto !== "undefined" && "randomUUID" in crypto ? crypto.randomUUID() : `op-${Date.now()}`;

export const listJournals = () => apiFetch<Journal[]>("/journals");
export const listAccounts = (accountType?: string) =>
  apiFetch<Account[]>(`/accounts?onlyPostable=true${accountType ? `&accountType=${accountType}` : ""}`);
export const listPeriods = () => apiFetch<AccountingPeriod[]>("/accounting-periods");
export const getTrialBalance = (asOf: string) => apiFetch<TrialBalance>(`/trial-balance?asOf=${asOf}`);

export const listEntries = (status?: string) =>
  apiFetch<JournalEntry[]>(`/accounting-entries${status ? `?status=${status}` : ""}`);
export const getEntry = (id: string) => apiFetch<JournalEntry>(`/accounting-entries/${id}`);

export const createDraft = (input: {
  journalCode: string;
  entryDate: string;
  currency: string;
  description: string | null;
  lines: DraftLineInput[];
}) => apiFetch<string>("/accounting-entries", { method: "POST", body: JSON.stringify(input) });

export const postEntry = (id: string) =>
  apiFetch<{ entryId: string; entryNumber: string; controls: AccountingControl[] }>(
    `/accounting-entries/${id}/post`,
    { method: "POST", idempotencyKey: uuid() },
  );

export const deleteDraft = (id: string) =>
  apiFetch<void>(`/accounting-entries/${id}`, { method: "DELETE" });

export const reverseEntry = (id: string, reason: string) =>
  apiFetch<string>(`/accounting-entries/${id}/reverse`, {
    method: "POST",
    body: JSON.stringify({ reason, reversalDate: null }),
  });

export const previewCloseYear = (periodId: string) =>
  apiFetch<YearEndCloseReport>(`/accounting-periods/${periodId}/close-year`);

export const closeYear = (periodId: string, force = false, note?: string) =>
  apiFetch<{ closeId: string; netResult: number; resultEntryNumber: string; forced: boolean }>(
    `/accounting-periods/${periodId}/close-year`,
    { method: "POST", body: JSON.stringify({ force, note: note ?? null }) },
  );

export const generateOpeningBalances = (targetPeriodId: string, fromPeriodId: string) =>
  apiFetch<{ entryId: string; entryNumber: string; lineCount: number; total: number }>(
    `/accounting-periods/${targetPeriodId}/opening-balances`,
    { method: "POST", body: JSON.stringify({ fromPeriodId }) },
  );
