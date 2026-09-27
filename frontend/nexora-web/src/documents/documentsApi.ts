import { apiFetch } from "@/core/apiClient";

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
export const convertQuote = (id: string, onCredit = false, orderType = "delivery") =>
  apiFetch<string>(`/quotes/${id}/convert`, { method: "POST", body: JSON.stringify({ onCredit, orderType, dueDate: null }) });

// --- Documents émis ---

export const searchDocuments = (params: { type?: string; status?: string; number?: string } = {}) => {
  const qs = new URLSearchParams();
  if (params.type) qs.set("type", params.type);
  if (params.status) qs.set("status", params.status);
  if (params.number) qs.set("number", params.number);
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

export const documentPdfUrl = (id: string, format = "a4") =>
  `/api/v1/documents/${id}/pdf?format=${format}`;

export const verifyDocument = (code: string) =>
  apiFetch<DocumentVerification>(`/documents/verify/${encodeURIComponent(code)}`);
