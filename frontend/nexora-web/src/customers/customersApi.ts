import { ApiError, apiDownload, apiFetch } from "@/core/apiClient";
import { enqueue } from "@/shared/offlineQueue";

// Module « Gestion clients & comptes clients » (docs/module-clients-comptes-clients.md).
// Le serveur est l'unique autorité sur les montants : aucun solde n'est calculé ici, et chaque
// opération financière porte un identifiant d'opération + une clé d'idempotence (§23).

const uuid = () => crypto.randomUUID();
const post = <T>(path: string, body: unknown, idempotent = false) => {
  const key = idempotent ? uuid() : undefined;
  return apiFetch<T>(path, { method: "POST", body: JSON.stringify(body), idempotencyKey: key });
};

// --- Fiche (§6) -----------------------------------------------------------------------------

export interface CustomerProfile {
  id: string;
  customerNumber: string;
  name: string;
  type: "individual" | "business";
  phone: string | null;
  email: string | null;
  address: string | null;
  taxIdentifier: string | null;
  status: string;
  creditLimit: number | null;
  currency: string;
  openReceivables: number | null;
  createdAt: string;
  firstName: string | null;
  city: string | null;
  country: string | null;
  secondaryContactName: string | null;
  secondaryContactPhone: string | null;
  segment: string | null;
  customerGroupName: string | null;
  accountManagerId: string | null;
  accountManagerName: string | null;
  creditDays: number | null;
  creditTerms: string | null;
  establishmentName: string | null;
  notes: string | null;
  createdByName: string | null;
  updatedAt: string | null;
}

/** Modification partielle : un champ absent reste inchangé ; "" efface un texte. */
export type CustomerPatch = Partial<{
  name: string; type: string; firstName: string; phone: string; email: string; address: string; city: string;
  country: string; taxIdentifier: string; secondaryContactName: string; secondaryContactPhone: string; segment: string;
  notes: string; creditLimit: number; creditDays: number; creditTerms: string;
}>;

export const getCustomer = (id: string) => apiFetch<CustomerProfile>(`/customers/${id}`);
export const patchCustomer = (id: string, patch: CustomerPatch) =>
  apiFetch<CustomerProfile>(`/customers/${id}`, { method: "PATCH", body: JSON.stringify(patch) });
export const changeCustomerStatus = (id: string, status: "active" | "inactive" | "archived", reason: string) =>
  post<CustomerProfile>(`/customers/${id}/status`, { status, reason });

// --- Recherche POS (§17) --------------------------------------------------------------------

export interface CustomerLookup {
  id: string;
  customerNumber: string;
  displayName: string;
  phoneHint: string | null;
  status: string;
  creditEnabled: boolean;
  availableCredit: number | null;
  currency: string;
  /** Avoir utilisable en paiement (null sans droit de voir le solde). */
  availableStoreCredit: number | null;
}

export const lookupCustomers = (q: string, limit = 8) =>
  apiFetch<CustomerLookup[]>(`/customers/lookup?${new URLSearchParams({ q, limit: String(limit) })}`);

// --- Tableau de bord (§15) ------------------------------------------------------------------

export interface CustomerSignal {
  code: string;
  level: "info" | "warning" | "danger";
  title: string;
  explanation: string;
  data: Record<string, string>;
}

export interface CustomerAccountSummary {
  customerId: string;
  customerNumber: string;
  name: string;
  status: string;
  currency: string;
  totalPurchases: number;
  purchaseCount: number;
  creditPurchases: number;
  totalPaid: number;
  balanceDue: number;
  creditLimit: number;
  availableCredit: number;
  expensesBilled: number;
  expensesNotBilled: number;
  returnsAmount: number;
  returnsQuantity: number;
  availableStoreCredit: number;
  netBalance: number;
  overdueAmount: number;
  overdueCredits: number;
  oldestOverdueDays: number;
  lastPaymentAt: string | null;
  lastPaymentAmount: number | null;
  ledgerBalance: number;
  ledgerConsistent: boolean;
  openPromises: number;
  openDisputes: number;
  blocked: boolean;
  pendingExpenses: number;
  pendingRefunds: number;
  signals: CustomerSignal[];
  /** §21 : un solde par devise, jamais additionnés. */
  balances: CurrencyBalance[];
  /** Encours de toutes les devises converti dans celle du client (limite). */
  totalExposure: number;
  exchangeRateMissing: boolean;
}

export interface CurrencyBalance {
  currency: string;
  balanceDue: number;
  overdueAmount: number;
  availableStoreCredit: number;
  netBalance: number;
  ledgerBalance: number;
  ledgerConsistent: boolean;
  exposureInCustomerCurrency: number | null;
}

export const getAccountSummary = (id: string, from?: string, to?: string) => {
  const qs = new URLSearchParams();
  if (from) qs.set("from", from);
  if (to) qs.set("to", to);
  const q = qs.toString();
  return apiFetch<CustomerAccountSummary>(`/customers/${id}/account${q ? `?${q}` : ""}`);
};

// --- Journal (§7) ---------------------------------------------------------------------------

export interface LedgerEntry {
  id: string;
  occurredAt: string;
  entryType: string;
  label: string;
  debit: number;
  credit: number;
  balance: number;
  currency: string;
  reference: string | null;
  description: string | null;
  createdByName: string | null;
  establishmentName: string | null;
}

export interface CustomerLedger {
  currency: string;
  openingBalance: number;
  totalDebit: number;
  totalCredit: number;
  closingBalance: number;
  entries: LedgerEntry[];
}

export const getLedger = (id: string, currency?: string) =>
  apiFetch<CustomerLedger>(`/customers/${id}/ledger${currency ? `?currency=${encodeURIComponent(currency)}` : ""}`);

// --- Crédits (§8) ---------------------------------------------------------------------------

export type CreditDisplayStatus = "open" | "partial" | "paid" | "overdue" | "blocked" | "cancelled" | "written_off";

export interface CustomerCredit {
  id: string;
  creditNumber: string;
  customerId: string;
  sourceType: string;
  sourceReference: string | null;
  currency: string;
  originalAmount: number;
  paidAmount: number;
  remainingAmount: number;
  issueDate: string;
  dueDate: string | null;
  status: string;
  displayStatus: CreditDisplayStatus;
  daysOverdue: number;
  establishmentName: string | null;
  createdByName: string | null;
  hasSchedule: boolean;
}

export interface CreditInstallment {
  number: number;
  dueDate: string;
  amount: number;
  covered: number;
  remaining: number;
  status: "paid" | "partial" | "overdue" | "upcoming";
}

export interface CreditSchedule {
  id: string;
  version: number;
  paidBefore: number;
  totalAmount: number;
  reason: string;
  createdByName: string | null;
  createdAt: string;
  installments: CreditInstallment[];
}

export interface CreditSettlement {
  kind: "payment" | "credit_memo" | "write_off";
  paymentId: string | null;
  reference: string;
  at: string;
  amount: number;
  voided: boolean;
  method: string | null;
  receivedByName: string | null;
  note: string | null;
}

export const listCredits = (customerId: string, status?: string) =>
  apiFetch<CustomerCredit[]>(`/customers/${customerId}/credits${status ? `?status=${status}` : ""}`);
export interface CreditDetail { credit: CustomerCredit; settlements: CreditSettlement[]; schedule: CreditSchedule | null }
export const getCredit = (id: string) => apiFetch<CreditDetail>(`/credits/${id}`);

/** §8 : échéancier du reste dû — lignes explicites, ou N échéances égales générées par le serveur. */
export const scheduleCredit = (id: string, body: {
  reason: string; installments?: Array<{ dueDate: string; amount: number }>; count?: number; firstDueDate?: string;
  monthly?: boolean; intervalDays?: number;
}) => post<CreditDetail>(`/credits/${id}/installments`, body);

/** §22 : crédit hors vente (reprise de solde), motivé ; rejouable sans doublon. */
export const openCredit = (customerId: string, amount: number, reason: string, dueDate?: string | null, operationUuid: string = uuid()) =>
  apiFetch<CreditDetail>(`/customers/${customerId}/credits`, {
    method: "POST", body: JSON.stringify({ operationUuid, amount, reason, dueDate: dueDate || null }), idempotencyKey: operationUuid,
  });

// --- Versements (§9) ------------------------------------------------------------------------

export interface CustomerPayment {
  id: string;
  paymentNumber: string;
  customerId: string;
  amount: number;
  allocatedAmount: number;
  unallocatedAmount: number;
  currency: string;
  status: "completed" | "void" | "draft";
  receivedAt: string;
  paymentMethod: string | null;
  financialAccountName: string | null;
  reference: string | null;
  note: string | null;
  receivedByName: string | null;
  voidedAt: string | null;
  voidedByName: string | null;
  voidReason: string | null;
  creditMemoNumber: string | null;
  allocations: Array<{ receivableId: string; creditNumber: string; amount: number; voided: boolean }>;
}

export interface PaymentOptions {
  accounts: Array<{ id: string; name: string; accountType: string; currency: string }>;
  methods: Array<{ id: string; name: string; type: string }>;
}

export interface RecordPaymentInput {
  amount: number;
  financialAccountId: string;
  paymentMethodId?: string | null;
  cashSessionId?: string | null;
  reference?: string | null;
  note?: string | null;
  /** Vide = répartition automatique sur les crédits les plus anciens. */
  allocations?: Array<{ receivableId: string; amount: number }>;
}

export const listPayments = (customerId: string) => apiFetch<CustomerPayment[]>(`/customers/${customerId}/payments`);
export const getPaymentOptions = () => apiFetch<PaymentOptions>("/customer-payment-options");

/** Une seule opération par saisie : l'appelant garde `operationUuid` pour rejouer sans doublon. */
export const recordPayment = (customerId: string, input: RecordPaymentInput, operationUuid: string = uuid()) =>
  apiFetch<CustomerPayment>(`/customers/${customerId}/payments`, {
    method: "POST", body: JSON.stringify({ operationUuid, ...input }), idempotencyKey: operationUuid,
  });

/**
 * §24 : versement hors ligne — si le serveur est injoignable, le versement part dans la file locale
 * (chiffrée) et sera rejoué par la synchronisation avec le MÊME identifiant d'opération : jamais deux fois.
 */
export async function recordPaymentOrQueue(customerId: string, input: RecordPaymentInput, operationUuid: string):
  Promise<{ queued: false; payment: CustomerPayment } | { queued: true }> {
  try {
    return { queued: false, payment: await recordPayment(customerId, input, operationUuid) };
  } catch (e) {
    if (e instanceof ApiError && e.isOffline) {
      enqueue({ id: operationUuid, kind: "customer.payment", payload: { customerId, ...input } });
      return { queued: true };
    }
    throw e;
  }
}

export const voidPayment = (paymentId: string, reason: string) =>
  post<CustomerPayment>(`/payments/${paymentId}/void`, { reason }, true);

// --- Dépenses imputées (§10) ----------------------------------------------------------------

export const EXPENSE_CATEGORIES: Record<string, string> = {
  delivery: "Livraison", transport: "Transport", installation: "Installation", handling: "Manutention",
  repair: "Réparation", file_fee: "Frais de dossier", penalty: "Pénalité", other: "Autre",
};

export interface CustomerExpense {
  id: string;
  expenseNumber: string;
  customerId: string;
  customerName: string;
  category: string;
  description: string;
  amount: number;
  currency: string;
  expenseDate: string;
  billable: boolean;
  dueDate: string | null;
  status: "draft" | "pending" | "validated" | "rejected" | "cancelled";
  creditNumber: string | null;
  createdByName: string | null;
  createdAt: string;
  decidedByName: string | null;
  decidedAt: string | null;
  decisionNote: string | null;
  cancelledByName: string | null;
  cancelReason: string | null;
}

export interface NewExpenseInput {
  category: string;
  description: string;
  amount: number;
  expenseDate?: string | null;
  billable: boolean;
  dueDate?: string | null;
  submit: boolean;
}

export const listExpenses = (customerId: string) => apiFetch<CustomerExpense[]>(`/customers/${customerId}/expenses`);
export const listPendingExpenses = () => apiFetch<CustomerExpense[]>("/customer-expenses?status=pending");
export const createExpense = (customerId: string, input: NewExpenseInput) =>
  post<CustomerExpense>(`/customers/${customerId}/expenses`, { operationUuid: uuid(), ...input }, true);
export const submitExpense = (id: string) => post<CustomerExpense>(`/customer-expenses/${id}/submit`, {});
export const approveExpense = (id: string, note?: string) => post<CustomerExpense>(`/customer-expenses/${id}/approve`, { note }, true);
export const rejectExpense = (id: string, note: string) => post<CustomerExpense>(`/customer-expenses/${id}/reject`, { note });
export const cancelExpense = (id: string, reason: string) => post<CustomerExpense>(`/customer-expenses/${id}/cancel`, { reason }, true);

// --- Retours, avoirs, remboursements (§11) --------------------------------------------------

export interface CustomerPurchase {
  saleId: string;
  documentNumber: string;
  soldAt: string;
  saleType: string;
  status: string;
  total: number;
  paidAtSale: number;
  creditAmount: number;
  returnedAmount: number;
  currency: string;
  cashierName: string | null;
  creditNumber: string | null;
}

export interface CreditMemo {
  id: string;
  memoNumber: string;
  sourceType: "sale_return" | "overpayment";
  sourceReference: string | null;
  currency: string;
  amount: number;
  usedAmount: number;
  remainingAmount: number;
  status: "available" | "partially_used" | "used" | "cancelled";
  reason: string | null;
  createdAt: string;
  uses: Array<{ kind: "credit" | "refund"; reference: string; amount: number; at: string; voided: boolean }>;
}

export interface CustomerRefund {
  id: string;
  refundNumber: string;
  customerId: string;
  customerName: string;
  memoNumber: string;
  currency: string;
  amount: number;
  refundMethod: string;
  status: "requested" | "approved" | "executed" | "rejected" | "cancelled";
  reason: string;
  requestedByName: string | null;
  requestedAt: string;
  decidedByName: string | null;
  decisionNote: string | null;
  executedByName: string | null;
  executedAt: string | null;
}

export const listPurchases = (customerId: string) => apiFetch<CustomerPurchase[]>(`/customers/${customerId}/purchases`);
export const listCreditMemos = (customerId: string) => apiFetch<CreditMemo[]>(`/customers/${customerId}/credit-memos`);
export const applyCreditMemo = (memoId: string, amount?: number) =>
  post<{ appliedAmount: number; remainingAmount: number }>(`/credit-memos/${memoId}/apply`, { operationUuid: uuid(), amount }, true);
export const listRefunds = (customerId: string) => apiFetch<CustomerRefund[]>(`/customers/${customerId}/refunds`);
export const listPendingRefunds = () => apiFetch<CustomerRefund[]>("/customer-refunds?status=requested");
export const requestRefund = (creditMemoId: string, amount: number, refundMethod: string, reason: string) =>
  post<CustomerRefund>("/customer-refunds", { operationUuid: uuid(), creditMemoId, amount, refundMethod, reason }, true);
export const approveRefund = (id: string, note?: string) => post<CustomerRefund>(`/customer-refunds/${id}/approve`, { note });
export const rejectRefund = (id: string, note: string) => post<CustomerRefund>(`/customer-refunds/${id}/reject`, { note });
export const executeRefund = (id: string, financialAccountId: string, cashSessionId?: string | null) =>
  post<CustomerRefund>(`/customer-refunds/${id}/execute`, { financialAccountId, cashSessionId }, true);
export const cancelRefund = (id: string, reason: string) => post<CustomerRefund>(`/customer-refunds/${id}/cancel`, { reason });

// --- Historique & recouvrement (§13, §14) ---------------------------------------------------

export interface TimelineEvent {
  at: string;
  category: "finance" | "purchase" | "return" | "expense" | "refund" | "block" | "collection" | "document" | "profile";
  title: string;
  detail: string | null;
  amount: number | null;
  currency: string | null;
  reference: string | null;
  actorName: string | null;
}

export interface CollectionAction {
  id: string;
  creditNumber: string | null;
  actionType: string;
  outcome: string | null;
  note: string;
  nextActionDate: string | null;
  createdByName: string | null;
  createdAt: string;
}

export const ACTION_TYPES: Record<string, string> = {
  call: "Appel", sms: "SMS", email: "Email", whatsapp: "WhatsApp", visit: "Visite", letter: "Courrier",
  reminder: "Relance", note: "Note", other: "Autre",
};
export const ACTION_OUTCOMES: Record<string, string> = {
  reached: "Joint", no_answer: "Sans réponse", promise: "Promesse obtenue", refused: "Refus", dispute: "Contestation",
  paid: "Payé", other: "Autre",
};

export const getTimeline = (id: string) => apiFetch<TimelineEvent[]>(`/customers/${id}/timeline`);
export const listCollectionActions = (id: string) => apiFetch<CollectionAction[]>(`/customers/${id}/collection-actions`);
export const logCollectionAction = (id: string, input: {
  actionType: string; note: string; outcome?: string | null; receivableId?: string | null; nextActionDate?: string | null;
}) => post<CollectionAction>(`/customers/${id}/collection-actions`, input);

// --- Dépassement de crédit au POS (§12, workflow d'approbation configuré) ---------------------

export interface CreditOverride {
  id: string;
  cartOperationUuid: string;
  customerId: string;
  customerName: string;
  amount: number;
  currency: string;
  reason: string;
  limitMessage: string | null;
  status: "pending" | "approved" | "rejected" | "cancelled" | "escalated";
  currentLevel: number;
  requiredLevels: number;
  requestedByName: string | null;
  requestedAt: string;
  decidedByName: string | null;
  decisionNote: string | null;
  consumed: boolean;
}

export const requestCreditOverride = (body: { cartOperationUuid: string; customerId: string; amount: number; currency: string; reason: string }) =>
  post<CreditOverride>("/pos/credit-override-requests", body);
export const getCreditOverride = (id: string) => apiFetch<CreditOverride>(`/pos/credit-override-requests/${id}`);
export const listCreditOverrides = (status = "pending") => apiFetch<CreditOverride[]>(`/pos/credit-override-requests?status=${status}`);
export const approveCreditOverride = (id: string, note?: string | null) => post<CreditOverride>(`/pos/credit-override-requests/${id}/approve`, { note });
export const rejectCreditOverride = (id: string, note: string) => post<CreditOverride>(`/pos/credit-override-requests/${id}/reject`, { note });

// --- Rapports (§26) -------------------------------------------------------------------------

export interface ReportDefinition { key: string; title: string; description: string }
export interface CustomerReport {
  key: string;
  title: string;
  from: string | null;
  to: string | null;
  columns: string[];
  rows: Array<Array<string | null>>;
  totals: Record<string, number>;
}

export const listCustomerReports = () => apiFetch<ReportDefinition[]>("/customer-reports");
const period = (from?: string, to?: string) => {
  const qs = new URLSearchParams();
  if (from) qs.set("from", from);
  if (to) qs.set("to", to);
  const q = qs.toString();
  return q ? `?${q}` : "";
};
export const runCustomerReport = (key: string, from?: string, to?: string) =>
  apiFetch<CustomerReport>(`/customer-reports/${key}${period(from, to)}`);

/** Export d'un rapport client en vrai Excel (.xlsx), PDF ou CSV (exige `customers.export`) : déclenche le
 * téléchargement dans le navigateur. */
export async function exportCustomerReport(key: string, from?: string, to?: string, format: "xlsx" | "pdf" | "csv" = "csv") {
  const qs = period(from, to);
  const { blob, filename } = await apiDownload(`/customer-reports/${key}/export${qs}${qs ? "&" : "?"}format=${format}`);
  const url = URL.createObjectURL(blob);
  const a = document.createElement("a");
  a.href = url;
  a.download = filename ?? `clients-${key}.${format}`;
  a.click();
  URL.revokeObjectURL(url);
}

// --- Libellés d'état (§29) ------------------------------------------------------------------

export const CREDIT_STATUS: Record<string, string> = {
  open: "Ouvert", partial: "Partiel", paid: "Payé", overdue: "En retard", blocked: "Bloqué (litige)",
  cancelled: "Annulé", written_off: "Radié",
};
export const EXPENSE_STATUS: Record<string, string> = {
  draft: "Brouillon", pending: "En attente", validated: "Validée", rejected: "Rejetée", cancelled: "Annulée",
};
export const MEMO_STATUS: Record<string, string> = {
  available: "Disponible", partially_used: "Partiellement utilisé", used: "Utilisé", cancelled: "Annulé",
};
export const REFUND_STATUS: Record<string, string> = {
  requested: "Demandé", approved: "Approuvé", executed: "Exécuté", rejected: "Rejeté", cancelled: "Annulé",
};
export const CUSTOMER_STATUS: Record<string, string> = {
  active: "Actif", inactive: "Inactif", blocked: "Bloqué", archived: "Archivé",
};
