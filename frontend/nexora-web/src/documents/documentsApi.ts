import { apiFetch } from "@/core/apiClient";
import { printDocument } from "@/shared/printApi";

// Facturation & documents commerciaux (Phase 30 §3-5, §19, §27-28, §34, §36, §48).

const uuid = () =>
  typeof crypto !== "undefined" && "randomUUID" in crypto ? crypto.randomUUID() : `op-${Date.now()}`;

export interface QuoteLine {
  id: string;
  productId: string;
  description: string;
  quantity: number;
  unitPrice: number;
  discount: number;
  taxAmount: number;
  lineTotal: number;
}

export interface Quote {
  id: string;
  documentNumber: string;
  status: string;
  version: number;
  customerId: string | null;
  customerName: string | null;
  currency: string;
  quoteDate: string;
  validUntil: string | null;
  subtotal: number;
  discountTotal: number;
  taxTotal: number;
  total: number;
  terms: string | null;
  notes: string | null;
  convertedOrderId: string | null;
  sentAt: string | null;
  createdAt: string;
  lines: QuoteLine[];
}

export interface QuoteLineInput {
  productId: string;
  quantity: number;
  unitPriceOverride: number | null;
  lineDiscount: number;
  taxId: string | null;
}

export interface IssuedDocument {
  id: string;
  documentType: string;
  documentNumber: string;
  status: string;
  language: string;
  partyType: string | null;
  partyId: string | null;
  partyName: string | null;
  referenceType: string | null;
  referenceId: string | null;
  currency: string;
  totalNet: number;
  totalTax: number;
  total: number;
  issueDate: string;
  validUntil: string | null;
  contentHash: string | null;
  verificationCode: string | null;
  cancelReason: string | null;
  replacedByDocumentId: string | null;
  issuedAt: string | null;
  createdAt: string;
}

export interface DocumentSend {
  id: string;
  channel: string;
  recipient: string | null;
  subject: string | null;
  status: string;
  error: string | null;
  sentAt: string | null;
  createdAt: string;
}

export interface DocumentVerification {
  found: boolean;
  documentType: string | null;
  documentNumber: string | null;
  status: string | null;
  issueDate: string | null;
  companyName: string | null;
  total: number | null;
  currency: string | null;
  message: string;
}

export interface DocumentSequence {
  id: string;
  documentType: string;
  establishmentId: string | null;
  year: number | null;
  prefix: string;
  includeYear: boolean;
  padding: number;
  currentNumber: number;
  status: string;
}

// --- Devis ---

export const listQuotes = (status?: string) =>
  apiFetch<Quote[]>(`/quotes${status ? `?status=${status}` : ""}`);
export const getQuote = (id: string) => apiFetch<Quote>(`/quotes/${id}`);
export const createQuote = (input: {
  customerId: string | null;
  currency: string;
  inventoryLocationId: string | null;
  validUntil: string | null;
  terms: string | null;
  notes: string | null;
  lines: QuoteLineInput[];
}) => apiFetch<string>("/quotes", { method: "POST", body: JSON.stringify(input) });
export const sendQuote = (id: string) => apiFetch<void>(`/quotes/${id}/send`, { method: "POST" });
export const acceptQuote = (id: string) => apiFetch<void>(`/quotes/${id}/accept`, { method: "POST" });
export const rejectQuote = (id: string, reason: string) =>
  apiFetch<void>(`/quotes/${id}/reject`, { method: "POST", body: JSON.stringify({ reason }) });
export type QuoteInput = {
  customerId: string | null;
  currency: string;
  inventoryLocationId: string | null;
  validUntil: string | null;
  terms: string | null;
  notes: string | null;
  lines: QuoteLineInput[];
};
/** Modification d'un devis encore en brouillon (phase 4). */
export const updateQuote = (id: string, input: QuoteInput) =>
  apiFetch<void>(`/quotes/${id}`, { method: "PUT", body: JSON.stringify(input) });
export const cancelQuote = (id: string, reason: string) =>
  apiFetch<void>(`/quotes/${id}/cancel`, { method: "POST", body: JSON.stringify({ reason }) });
/** Conversion en commande : l'emplacement de stock rend la commande directement réservable. */
export const convertQuote = (id: string, opts: { onCredit?: boolean; orderType?: string; dueDate?: string | null; inventoryLocationId?: string | null } = {}) =>
  apiFetch<string>(`/quotes/${id}/convert`, {
    method: "POST",
    body: JSON.stringify({
      onCredit: opts.onCredit ?? false, orderType: opts.orderType ?? "delivery", dueDate: opts.dueDate ?? null,
      inventoryLocationId: opts.inventoryLocationId ?? null,
    }),
  });

// --- Documents émis ---

export const searchDocuments = (params: { type?: string; status?: string; number?: string; sourceType?: string; sourceId?: string } = {}) => {
  const qs = new URLSearchParams();
  if (params.type) qs.set("type", params.type);
  if (params.status) qs.set("status", params.status);
  if (params.number) qs.set("number", params.number);
  if (params.sourceType) qs.set("sourceType", params.sourceType);
  if (params.sourceId) qs.set("sourceId", params.sourceId);
  const s = qs.toString();
  return apiFetch<IssuedDocument[]>(`/documents${s ? `?${s}` : ""}`);
};
export const getDocument = (id: string) =>
  apiFetch<{ header: IssuedDocument; content: unknown }>(`/documents/${id}`);
export const documentSends = (id: string) => apiFetch<DocumentSend[]>(`/documents/${id}/sends`);
export const documentSequences = () => apiFetch<DocumentSequence[]>("/document-sequences");

export const issueDocument = (type: string, sourceType: string, sourceId: string) =>
  apiFetch<IssuedDocument>("/documents", {
    method: "POST",
    idempotencyKey: uuid(),
    body: JSON.stringify({ type, sourceType, sourceId, language: null }),
  });

export const cancelDocument = (id: string, reason: string) =>
  apiFetch<void>(`/documents/${id}/cancel`, {
    method: "POST",
    body: JSON.stringify({ reason, replacedByDocumentId: null }),
  });

export const sendDocument = (id: string, channel: string, recipient: string | null, subject: string | null) =>
  apiFetch<string>(`/documents/${id}/send`, {
    method: "POST",
    body: JSON.stringify({ channel, recipient, subject, message: null }),
  });

/** Vrai PDF du document émis (reconstruit depuis son contenu figé), ouvert pour impression. Authentifié :
 * un simple lien ne transmettait pas le jeton de session. */
export const openDocumentPdf = (id: string, format: "pdf" | "ticket" = "pdf") =>
  printDocument("document", id, format);

export const DOCUMENT_TYPE_LABEL: Record<string, string> = {
  invoice: "Facture", deposit_invoice: "Facture d'acompte", final_invoice: "Facture de solde", proforma: "Proforma",
  credit_note: "Avoir", receipt: "Reçu", delivery_note: "Bon de livraison",
};

export const verifyDocument = (code: string) =>
  apiFetch<DocumentVerification>(`/documents/verify/${encodeURIComponent(code)}`);
