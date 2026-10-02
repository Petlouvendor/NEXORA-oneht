import { apiFetch } from "@/core/apiClient";
import { newOperationId } from "@/shared/forms";

// Crédit client & recouvrement (Phase 27 ; finalisation phase 9 : cycle promesse / litige, agenda, dossier client).

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
  /** Encours en autre devise : présenté à part, jamais additionné à la devise de l'entreprise. */
  otherCurrencies: Array<{ currency: string; amount: number }> | null;
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
  currency: string;
  snapshotRemaining: number;
  createdByName: string | null;
  settledAt: string | null;
  settledByName: string | null;
  cancelReason: string | null;
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
  creditNumber: string | null;
  currency: string;
  createdByName: string | null;
  resolvedByName: string | null;
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

export interface CreditReceivable {
  id: string;
  creditNumber: string;
  currency: string;
  originalAmount: number;
  paidAmount: number;
  remainingAmount: number;
  issueDate: string;
  dueDate: string | null;
  daysOverdue: number;
  status: string;
  openPromiseId: string | null;
  openDisputeId: string | null;
}

export interface WriteOff {
  id: string;
  customerId: string;
  customerName: string;
  receivableId: string;
  creditNumber: string | null;
  currency: string;
  amount: number;
  reason: string;
  journalEntryId: string | null;
  journalEntryNumber: string | null;
  createdByName: string | null;
  writtenOffAt: string;
}

export interface FollowUp {
  kind: "action" | "promise_due" | "promise_broken";
  customerId: string;
  customerName: string;
  dueDate: string;
  detail: string;
  amount: number | null;
  currency: string | null;
  referenceId: string | null;
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

export const PROMISE_STATUS: Record<string, { label: string; tone: "success" | "warning" | "error" | "info" }> = {
  open: { label: "En cours", tone: "info" },
  kept: { label: "Tenue", tone: "success" },
  partially_kept: { label: "Partiellement tenue", tone: "warning" },
  broken: { label: "Non tenue", tone: "error" },
  cancelled: { label: "Annulée", tone: "warning" },
};
export const DISPUTE_STATUS: Record<string, { label: string; tone: "success" | "warning" | "error" | "info" }> = {
  open: { label: "Ouvert", tone: "warning" },
  under_review: { label: "En examen", tone: "warning" },
  resolved_customer: { label: "Tranché pour le client", tone: "info" },
  resolved_company: { label: "Tranché pour l'entreprise", tone: "success" },
  cancelled: { label: "Annulé", tone: "info" },
};
export const BLOCK_TYPES: Record<string, string> = {
  credit: "Crédit", sales: "Ventes", delivery: "Livraisons", account: "Compte entier",
};
export const CHANNELS: Record<string, string> = {
  phone: "Téléphone", email: "E-mail", sms: "SMS", whatsapp: "WhatsApp", visit: "Visite", internal: "Interne",
};
export const RECEIVABLE_STATUS: Record<string, string> = {
  OPEN: "Ouverte", PARTIALLY_PAID: "Partiellement payée", OVERDUE: "Échue", IN_DISPUTE: "En litige",
  PAID: "Payée", WRITTEN_OFF: "Passée en perte", CANCELLED: "Annulée",
};
export const FOLLOW_UP_KIND: Record<FollowUp["kind"], string> = {
  action: "Relance prévue", promise_due: "Promesse à échéance", promise_broken: "Promesse non tenue",
};

const json = (method: string, body: unknown, idempotencyKey?: string) =>
  ({ method, body: JSON.stringify(body), ...(idempotencyKey ? { idempotencyKey } : {}) });
const qs = (o: Record<string, string | boolean | undefined | null>) => {
  const p = new URLSearchParams();
  for (const [k, v] of Object.entries(o)) if (v !== undefined && v !== null && v !== "") p.set(k, String(v));
  const s = p.toString();
  return s ? `?${s}` : "";
};

export const getCollections = (onlyOverdue = true) =>
  apiFetch<CollectionsRow[]>(`/credit/collections?onlyOverdue=${onlyOverdue}`);
export const getAging = () => apiFetch<AgingSummary>("/credit/aging");
export const getStatement = (customerId: string) =>
  apiFetch<CustomerStatement>(`/credit/statements/${customerId}`);
export const listFollowUps = () => apiFetch<FollowUp[]>("/credit/follow-ups");
export const listReceivables = (customerId: string, onlyOpen = true) =>
  apiFetch<CreditReceivable[]>(`/credit/customers/${customerId}/receivables?onlyOpen=${onlyOpen}`);
export const listWriteOffs = (customerId?: string) => apiFetch<WriteOff[]>(`/credit/write-offs${qs({ customerId })}`);

export const listPromises = (status?: string, customerId?: string) =>
  apiFetch<Promise_[]>(`/credit/promises${qs({ status, customerId })}`);
export const createPromise = (input: {
  customerId: string; amount: number; promisedDate: string; channel: string; receivableIds: string[]; note: string | null;
}) => apiFetch<string>("/credit/promises", json("POST", input));
export const settlePromise = (id: string) => apiFetch<string>(`/credit/promises/${id}/settle`, { method: "POST" });
export const cancelPromise = (id: string, reason: string) => apiFetch<void>(`/credit/promises/${id}/cancel`, json("POST", { reason }));

export const listDisputes = (status?: string, customerId?: string) =>
  apiFetch<Dispute[]>(`/credit/disputes${qs({ status, customerId })}`);
export const openDispute = (customerId: string, receivableId: string | null, disputedAmount: number, reason: string) =>
  apiFetch<string>("/credit/disputes", json("POST", { customerId, receivableId, disputedAmount, reason }));
export const resolveDispute = (id: string, outcome: string, resolution: string) =>
  apiFetch<void>(`/credit/disputes/${id}/resolve`, json("POST", { outcome, resolution }));

export const listBlocks = (onlyActive = true, customerId?: string) =>
  apiFetch<CustomerBlock[]>(`/credit/blocks${qs({ onlyActive, customerId })}`);
export const blockCustomer = (customerId: string, blockType: string, reason: string) =>
  apiFetch<string>("/credit/blocks", json("POST", { customerId, blockType, reason }));
export const releaseBlock = (id: string, reason: string) =>
  apiFetch<void>(`/credit/blocks/${id}/release`, json("POST", { reason }));

/** Passage en perte : `operationUuid` fixé à l'ouverture du dialogue — un double envoi ne radie jamais deux fois. */
export const writeOffReceivable = (receivableId: string, amount: number, reason: string, operationUuid: string = newOperationId()) =>
  apiFetch<{ writeOffId: string; journalEntryId: string }>(`/credit/receivables/${receivableId}/write-off`,
    json("POST", { operationUuid, amount, reason }, operationUuid));
