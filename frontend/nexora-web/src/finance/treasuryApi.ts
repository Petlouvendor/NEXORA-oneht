import { apiFetch } from "@/core/apiClient";

// Gestion de la trésorerie, dépenses et rapprochement bancaire (finalisation, phase 7).

export interface ManagedAccount {
  id: string; name: string; accountType: string; currency: string; accountNumberMasked: string | null;
  ledgerAccountId: string | null; ledgerAccountCode: string | null; ledgerAccountName: string | null;
  balance: number; status: string; transactionCount: number; lastMovementAt: string | null;
}
export interface LedgerOption { id: string; code: string; name: string; accountType: string }
export interface AccountMovement {
  id: string; occurredAt: string; transactionType: string; amount: number; currency: string;
  referenceType: string | null; referenceId: string | null; label: string | null; counterpartyAccountName: string | null;
  journalEntryNumber: string | null; status: string; createdByName: string | null;
}
export interface SaveAccountInput {
  name: string; accountType: string; currency: string; accountNumberMasked: string | null; ledgerAccountId: string | null;
}
export interface PostingResult { journalEntryId: string; entryNumber: string; wasReplay: boolean }

const json = (method: string, body: unknown, idempotencyKey?: string) =>
  ({ method, body: JSON.stringify(body), ...(idempotencyKey ? { idempotencyKey } : {}) });

export const ACCOUNT_TYPE: Record<string, string> = {
  cash: "Caisse", bank: "Banque", mobile_money: "Mobile money", card_processor: "Monétique", wallet: "Porte-monnaie", other: "Autre",
};

export const listManagedAccounts = () => apiFetch<ManagedAccount[]>("/financial-accounts/manage");
export const createAccount = (body: SaveAccountInput) => apiFetch<string>("/financial-accounts", json("POST", body));
export const updateAccount = (id: string, body: SaveAccountInput) => apiFetch<void>(`/financial-accounts/${id}`, json("PUT", body));
export const setAccountStatus = (id: string, status: string, reason: string) =>
  apiFetch<void>(`/financial-accounts/${id}/status`, json("POST", { status, reason }));
export const accountMovements = (id: string, from?: string, to?: string) => {
  const q = new URLSearchParams({ limit: "300" });
  if (from) q.set("from", from);
  if (to) q.set("to", to);
  return apiFetch<AccountMovement[]>(`/financial-accounts/${id}/transactions?${q}`);
};
export const ledgerOptions = (type: "asset" | "expense") => apiFetch<LedgerOption[]>(`/ledger-options?type=${type}`);
/** Devises et fournisseurs actifs pour les formulaires financiers (accessible avec finance.view). */
export interface FinanceLookups {
  currencies: Array<{ code: string; name: string; isCompanyDefault: boolean }>;
  suppliers: Array<{ id: string; name: string }>;
}
export const financeLookups = () => apiFetch<FinanceLookups>("/finance-lookups");
/** Transfert : `operationUuid` fixé à l'ouverture du dialogue (un double envoi ne transfère jamais deux fois). */
export const transferFunds = (body: { operationUuid: string; fromAccountId: string; toAccountId: string; amount: number; note: string | null }) =>
  apiFetch<PostingResult>("/treasury/transfers", json("POST", body, body.operationUuid));
export const recordCharge = (body: { operationUuid: string; accountId: string; amount: number; description: string }) =>
  apiFetch<PostingResult>("/treasury/charges", json("POST", body, body.operationUuid));

// ---------------------------------------------------------------- Dépenses

export interface ExpenseCategory {
  id: string; name: string; code: string | null; ledgerAccountId: string | null; ledgerAccountCode: string | null;
  ledgerAccountName: string | null; status: string;
}
export interface Expense {
  id: string; documentNumber: string; description: string; categoryId: string | null; categoryName: string | null;
  supplierId: string | null; supplierName: string | null; amount: number; taxAmount: number; total: number; currency: string;
  expenseDate: string; reference: string | null; status: string; financialAccountId: string | null; financialAccountName: string | null;
  createdBy: string | null; createdByName: string | null; approvedByName: string | null; approvedAt: string | null;
  paidAt: string | null; paidByName: string | null; journalEntryNumber: string | null; decisionReason: string | null; createdAt: string;
}
export interface SaveExpenseInput {
  description: string; categoryId: string; supplierId: string | null; amount: number; taxAmount: number; currency: string;
  expenseDate: string; reference: string | null; submit: boolean;
}

export const EXPENSE_STATUS: Record<string, string> = {
  draft: "Brouillon", submitted: "À approuver", approved: "Approuvée", paid: "Payée", rejected: "Refusée", cancelled: "Annulée",
};
export const EXPENSE_TONE: Record<string, "success" | "warning" | "error" | "info" | undefined> = {
  draft: undefined, submitted: "warning", approved: "info", paid: "success", rejected: "error", cancelled: "error",
};

export const listExpenseCategories = () => apiFetch<ExpenseCategory[]>("/expense-categories");
export const createExpenseCategory = (body: { name: string; code: string | null; ledgerAccountId: string | null; status: string }) =>
  apiFetch<string>("/expense-categories", json("POST", body));
export const updateExpenseCategory = (id: string, body: { name: string; code: string | null; ledgerAccountId: string | null; status: string }) =>
  apiFetch<void>(`/expense-categories/${id}`, json("PUT", body));
export const listExpenses = (status?: string) => apiFetch<Expense[]>(`/expenses${status ? `?status=${status}` : ""}`);
export const getExpense = (id: string) => apiFetch<Expense>(`/expenses/${id}`);
export const createExpense = (body: SaveExpenseInput) => apiFetch<string>("/expenses", json("POST", body));
export const updateExpense = (id: string, body: SaveExpenseInput) => apiFetch<void>(`/expenses/${id}`, json("PUT", body));
export const submitExpense = (id: string) => apiFetch<void>(`/expenses/${id}/submit`, { method: "POST" });
export const approveExpense = (id: string) => apiFetch<void>(`/expenses/${id}/approve`, { method: "POST" });
export const rejectExpense = (id: string, reason: string) => apiFetch<void>(`/expenses/${id}/reject`, json("POST", { reason }));
export const cancelExpense = (id: string, reason: string) => apiFetch<void>(`/expenses/${id}/cancel`, json("POST", { reason }));
export const payExpense = (id: string, operationUuid: string, financialAccountId: string) =>
  apiFetch<Expense>(`/expenses/${id}/pay`, json("POST", { operationUuid, financialAccountId }, operationUuid));

// ---------------------------------------------------------------- Rapprochement bancaire

export interface BankStatementSummary {
  id: string; financialAccountId: string; statementNumber: string | null; statementDate: string;
  openingBalance: number; closingBalance: number; status: string; lineCount: number; matchedCount: number;
}
export interface StatementLine {
  id: string; valueDate: string; amount: number; description: string | null; reference: string | null;
  status: string; matchedTransactionId: string | null;
}
export interface StatementCandidate { transactionId: string; occurredAt: string; amount: number; transactionType: string; label: string | null }
export interface StatementImport {
  financialAccountId: string; statementNumber: string; statementDate: string; openingBalance: number; closingBalance: number;
  lines: Array<{ valueDate: string; amount: number; description: string; reference: string | null }>;
}
export const listBankStatements = () => apiFetch<BankStatementSummary[]>("/integrations/bank/statements");
export const statementLines = (id: string) => apiFetch<StatementLine[]>(`/integrations/bank/statements/${id}/lines`);
export const importStatement = (body: StatementImport) =>
  apiFetch<{ statementId: string; linesImported: number; duplicatesSkipped: number; autoMatched: number; unmatched: number }>(
    "/integrations/bank/statements", json("POST", body));
export const autoMatch = (id: string) => apiFetch<number>(`/integrations/bank/statements/${id}/auto-match`, { method: "POST" });
export const lineCandidates = (lineId: string) => apiFetch<StatementCandidate[]>(`/integrations/bank/lines/${lineId}/candidates`);
export const matchLine = (lineId: string, transactionId: string) =>
  apiFetch<void>(`/integrations/bank/lines/${lineId}/match`, json("POST", { transactionId }));
export const unmatchLine = (lineId: string) => apiFetch<void>(`/integrations/bank/lines/${lineId}/unmatch`, { method: "POST" });
export const validateStatement = (id: string) => apiFetch<void>(`/integrations/bank/statements/${id}/validate`, { method: "POST" });

/**
 * Lecture d'un relevé exporté par la banque (CSV ; séparateur « ; », « , » ou tabulation). Colonnes : date,
 * montant signé (ou débit / crédit), libellé, référence. Dates JJ/MM/AAAA ou AAAA-MM-JJ ; décimales « , » ou « . ».
 * Chaque ligne rejetée est signalée avec son numéro — jamais ignorée en silence.
 */
export function parseStatementCsv(text: string): { lines: StatementImport["lines"]; errors: string[] } {
  const rows = text.replace(/\r/g, "").split("\n").map((r) => r.trim()).filter(Boolean);
  if (rows.length === 0) return { lines: [], errors: ["Fichier vide."] };
  const sep = [";", "\t", ","].find((s) => rows[0].includes(s)) ?? ";";
  const split = (r: string) => r.split(sep).map((c) => c.trim().replace(/^"(.*)"$/, "$1"));
  const head = split(rows[0]).map((h) => h.normalize("NFD").replace(/[̀-ͯ]/g, "").toLowerCase());
  const hasHeader = head.some((h) => /date|montant|amount|libelle|debit|credit/.test(h));
  const col = (...names: string[]) => head.findIndex((h) => names.some((n) => h.includes(n)));
  const iDate = hasHeader ? col("date") : 0;
  const iAmount = hasHeader ? col("montant", "amount") : 1;
  const iDebit = hasHeader ? col("debit") : -1;
  const iCredit = hasHeader ? col("credit") : -1;
  const iLabel = hasHeader ? col("libelle", "description", "label") : 2;
  const iRef = hasHeader ? col("reference", "ref") : 3;
  const num = (v: string | undefined) => {
    if (!v) return 0;
    const clean = v.replace(/\s/g, "").replace(/[^\d,.-]/g, "");
    const normalized = clean.includes(",") && clean.lastIndexOf(",") > clean.lastIndexOf(".")
      ? clean.replace(/\./g, "").replace(",", ".")
      : clean.replace(/,/g, "");
    const n = Number(normalized);
    return Number.isFinite(n) ? n : Number.NaN;
  };
  const date = (v: string | undefined) => {
    if (!v) return null;
    const fr = /^(\d{1,2})[/.-](\d{1,2})[/.-](\d{4})$/.exec(v);
    if (fr) return `${fr[3]}-${fr[2].padStart(2, "0")}-${fr[1].padStart(2, "0")}`;
    return /^\d{4}-\d{2}-\d{2}$/.test(v) ? v : null;
  };
  const lines: StatementImport["lines"] = [];
  const errors: string[] = [];
  rows.slice(hasHeader ? 1 : 0).forEach((r, i) => {
    const c = split(r);
    const n = i + (hasHeader ? 2 : 1);
    const d = date(c[iDate]);
    const amount = iAmount >= 0 ? num(c[iAmount]) : num(c[iCredit]) - num(c[iDebit]);
    if (!d) { errors.push(`Ligne ${n} : date illisible « ${c[iDate] ?? ""} ».`); return; }
    if (!Number.isFinite(amount) || amount === 0) { errors.push(`Ligne ${n} : montant illisible ou nul.`); return; }
    lines.push({
      valueDate: d, amount: Math.round(amount * 100) / 100,
      description: (iLabel >= 0 ? c[iLabel] : "") || "—", reference: (iRef >= 0 ? c[iRef] : "") || null,
    });
  });
  return { lines, errors };
}

// --- Taux de change (audit 2026-10, A-017) ---
/** 1 `baseCurrency` = `rate` `quoteCurrency`, à partir de `effectiveAt`. Un taux ne se modifie pas : on en saisit un nouveau. */
export interface ExchangeRate {
  id: string; baseCurrency: string; quoteCurrency: string; rate: number; effectiveAt: string; source: string; createdAt: string;
}
export const listExchangeRates = () => apiFetch<ExchangeRate[]>("/exchange-rates");
export const recordExchangeRate = (body: { baseCurrency: string; quoteCurrency: string; rate: number; effectiveAt: string | null }) =>
  apiFetch<ExchangeRate>("/exchange-rates", json("POST", body));

/** Solde d'ouverture d'un compte encore sans mouvement : trésorerie et comptabilité (audit 2026-10, A-023). */
export const recordOpeningBalance = (id: string, body: { operationUuid: string; amount: number; date: string | null; note: string | null }) =>
  apiFetch<PostingResult>(`/financial-accounts/${id}/opening-balance`, json("POST", body, body.operationUuid));
