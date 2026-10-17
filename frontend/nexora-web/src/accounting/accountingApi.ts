import { apiFetch } from "@/core/apiClient";
import { parseDate, todayIso } from "@/shared/format";

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
  /** Contre-valeur en devise de l'entreprise (celle de la balance). */
  baseTotal: number;
  functionalCurrency: string | null;
  sourceType: string | null;
  sourceId: string | null;
  createdByName: string | null;
  postedByName: string | null;
  reversedByEntryId: string | null;
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

export interface EntryFilters { status?: string; from?: string; to?: string; journal?: string; q?: string; limit?: number }
const qs = (o: Record<string, string | number | undefined>) => {
  const p = new URLSearchParams();
  for (const [k, v] of Object.entries(o)) if (v !== undefined && v !== "") p.set(k, String(v));
  const s = p.toString();
  return s ? `?${s}` : "";
};
/** Recherche d'écritures : statut, période, journal, texte (n°, libellé, compte). */
export const searchEntries = (f: EntryFilters) => apiFetch<JournalEntry[]>(`/accounting-entries${qs({ ...f })}`);
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

export const updateDraft = (id: string, input: Parameters<typeof createDraft>[0]) =>
  apiFetch<void>(`/accounting-entries/${id}`, { method: "PUT", body: JSON.stringify(input) });

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

// --- Phase 8 (finalisation) : plan comptable, états, périodes ----------------------------------

export const ACCOUNT_TYPES: Record<string, string> = {
  asset: "Actif", liability: "Passif", equity: "Capitaux propres", revenue: "Produit", expense: "Charge",
};

export interface ChartAccount {
  id: string;
  code: string;
  name: string;
  accountType: string;
  isPostable: boolean;
  status: string;
  parentAccountId: string | null;
  parentCode: string | null;
  currency: string | null;
  /** Solde débiteur (+) / créditeur (−) en devise de l'entreprise, écritures validées. */
  balance: number;
  lineCount: number;
  usedBy: string[];
}
export interface SaveChartAccount {
  code: string; name: string; accountType: string; isPostable: boolean; parentAccountId: string | null; currency: string | null;
}
export interface CurrencyOption { code: string; name: string; isCompanyDefault: boolean }

export const listChartAccounts = () => apiFetch<ChartAccount[]>("/accounts/manage");
export const listAccountingCurrencies = () => apiFetch<CurrencyOption[]>("/accounting-currencies");
export const createChartAccount = (body: SaveChartAccount) =>
  apiFetch<string>("/accounts", { method: "POST", body: JSON.stringify(body) });
export const updateChartAccount = (id: string, body: SaveChartAccount) =>
  apiFetch<void>(`/accounts/${id}`, { method: "PUT", body: JSON.stringify(body) });
export const setChartAccountStatus = (id: string, status: "active" | "inactive", reason: string) =>
  apiFetch<void>(`/accounts/${id}/status`, { method: "POST", body: JSON.stringify({ status, reason }) });

export interface LedgerLine {
  date: string; entryNumber: string; journal: string; description: string | null;
  debit: number; credit: number; runningBalance: number;
}
export interface GeneralLedger {
  accountCode: string; accountName: string; from: string; to: string;
  openingBalance: number; lines: LedgerLine[]; closingBalance: number;
}
export const getGeneralLedger = (account: string, from: string, to: string) =>
  apiFetch<GeneralLedger>(`/general-ledger${qs({ account, from, to })}`);

export interface StatementSection { title: string; lines: Array<{ label: string; amount: number }>; subtotal: number }
export interface IncomeStatement { from: string; to: string; currency: string; revenue: StatementSection; expenses: StatementSection; netResult: number }
export interface BalanceSheet {
  asOf: string; currency: string; assets: StatementSection; liabilities: StatementSection; equity: StatementSection;
  result: number; isBalanced: boolean;
}
export const getFinancialStatements = (from: string, to: string) =>
  apiFetch<{ incomeStatement: IncomeStatement; balanceSheet: BalanceSheet }>(`/financial-statements${qs({ from, to })}`);

export const closePeriod = (id: string) => apiFetch<void>(`/accounting-periods/${id}/close`, { method: "POST" });
/** Ouvre l'exercice qui suit le dernier (audit 2026-10 éd. 2, A-042). */
export const openNextYear = () => apiFetch<AccountingPeriod>("/accounting-periods/next-year", { method: "POST" });

/**
 * État de l'exercice suivant : on peut l'ouvrir quand le dernier exercice a commencé ; on le propose en évidence dans
 * le dernier mois (même fenêtre que le planificateur, 31 jours).
 */
export function nextYearState(periods: AccountingPeriod[], today: string): { last?: AccountingPeriod; canOpen: boolean; dueSoon: boolean } {
  const last = [...periods].sort((a, b) => a.endDate.localeCompare(b.endDate)).at(-1);
  if (!last) return { canOpen: false, dueSoon: false };
  const canOpen = last.startDate <= today;
  const limit = parseDate(today);
  limit.setDate(limit.getDate() + 31);
  return { last, canOpen, dueSoon: canOpen && last.endDate <= todayIso(limit) };
}

export const reopenPeriod = (id: string, reason: string) =>
  apiFetch<void>(`/accounting-periods/${id}/reopen`, { method: "POST", body: JSON.stringify({ reason, reversalDate: null }) });
