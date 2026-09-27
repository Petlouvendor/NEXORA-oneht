import { apiFetch } from "@/core/apiClient";

export interface SupplierSummary {
  id: string;
  supplierNumber: string;
  name: string;
  status: string;
  currency: string;
  openPayables: number;
}

export interface PurchaseOrderSummary {
  id: string;
  documentNumber: string;
  status: string;
  supplierId: string;
  supplierName: string;
  currency: string;
  orderDate: string;
  total: number;
  receivedRatio: number;
}

export interface Payable {
  id: string;
  supplierInvoiceId: string | null;
  invoiceNumber: string | null;
  supplierReference: string | null;
  supplierId: string;
  supplierName: string;
  currency: string;
  originalAmount: number;
  paidAmount: number;
  remainingAmount: number;
  issueDate: string;
  dueDate: string | null;
  daysOverdue: number;
  status: string;
  paymentBlocked: boolean;
  blockReason: string | null;
}

export interface MatchLine {
  productId: string;
  description: string;
  orderedQuantity: number;
  receivedQuantity: number;
  invoicedQuantity: number;
  orderedUnitPrice: number;
  invoicedUnitPrice: number;
  quantityVariancePercent: number;
  priceVariancePercent: number;
  status: string;
  detail: string | null;
}

export interface ThreeWayMatchReport {
  invoiceId: string;
  invoiceNumber: string;
  purchaseOrderId: string | null;
  matchStatus: string;
  paymentBlocked: boolean;
  blockReason: string | null;
  lines: MatchLine[];
}

export interface SupplierStatementLine {
  date: string;
  documentType: string;
  documentNumber: string;
  reference: string | null;
  charge: number;
  payment: number;
  runningBalance: number;
}

export interface SupplierStatement {
  supplierId: string;
  supplierName: string;
  currency: string;
  openingBalance: number;
  closingBalance: number;
  totalOverdue: number;
  lines: SupplierStatementLine[];
}

const uuid = () =>
  typeof crypto !== "undefined" && "randomUUID" in crypto
    ? crypto.randomUUID()
    : `op-${Date.now()}-${Math.random().toString(16).slice(2)}`;

export const listSuppliers = () => apiFetch<SupplierSummary[]>("/suppliers");
export const supplierStatement = (id: string) => apiFetch<SupplierStatement>(`/suppliers/${id}/statement`);

export const listPurchaseOrders = (status?: string) =>
  apiFetch<PurchaseOrderSummary[]>(`/purchasing/orders${status ? `?status=${status}` : ""}`);

export const approvePurchaseOrder = (id: string) =>
  apiFetch<void>(`/purchasing/orders/${id}/approve`, { method: "POST" });
export const sendPurchaseOrder = (id: string) =>
  apiFetch<void>(`/purchasing/orders/${id}/send`, { method: "POST" });

export const matchInvoice = (invoiceId: string, quantityTolerancePercent = 5, priceTolerancePercent = 2) =>
  apiFetch<ThreeWayMatchReport>(`/purchasing/invoices/${invoiceId}/match`, {
    method: "POST",
    body: JSON.stringify({ quantityTolerancePercent, priceTolerancePercent }),
  });

export const listPayables = (onlyOpen = true) =>
  apiFetch<Payable[]>(`/payables?onlyOpen=${onlyOpen}`);

/** Compte utilisable pour payer (caisse, banque…) — jamais de solde de trésorerie. */
export interface PaymentAccount { id: string; name: string; accountType: string; currency: string }

export const listPaymentAccounts = () => apiFetch<PaymentAccount[]>("/payables/payment-accounts");

/** Paiement d'une dette. `operationUuid` fixé à l'ouverture du dialogue : un double clic ou un nouvel
 * essai réseau ne paie jamais deux fois (même clé d'idempotence). */
export const payPayable = (id: string, amount: number, tenderFinancialAccountId: string, reference?: string, operationUuid: string = uuid()) =>
  apiFetch<{ journalEntryId: string; paymentId: string | null }>(`/payables/${id}/payments`, {
    method: "POST",
    idempotencyKey: operationUuid,
    body: JSON.stringify({ operationUuid, amount, tenderFinancialAccountId, reference: reference ?? null }),
  });
