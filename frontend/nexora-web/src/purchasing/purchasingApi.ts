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

// ---------------------------------------------------------------- Cycle d'achat (finalisation, phase 3)
// Totaux, taxes et conversions d'unité sont TOUJOURS recalculés par le serveur ; le client n'affiche
// qu'une estimation pendant la saisie.

export interface PurchaseOrderLineInput {
  productId: string; quantity: number; unitPrice: number; taxId: string | null; discount: number; unitId: string | null;
}
export interface PurchaseOrderInput {
  supplierId: string; warehouseId: string | null; currency: string; expectedDate: string | null;
  lines: PurchaseOrderLineInput[]; notes: string | null; submit: boolean;
}

export interface PurchaseOrderDetailLine {
  id: string; productId: string; sku: string; productName: string; baseUnitCode: string | null;
  quantityOrdered: number; quantityReceived: number; remaining: number; unitPrice: number;
  taxId: string | null; discount: number; lineTotal: number; taxAmount: number;
  purchaseUnitId: string | null; purchaseUnitCode: string | null; purchaseQuantity: number | null;
}

export interface GoodsReceiptSummary {
  id: string; documentNumber: string; supplierId: string; supplierName: string; purchaseOrderId: string | null;
  purchaseOrderNumber: string | null; locationId: string | null; locationName: string | null; receivedAt: string;
  lineCount: number; quantityAccepted: number; quantityRejected: number; value: number; status: string;
  notes: string | null; createdByName: string | null;
}
export interface GoodsReceiptLine {
  id: string; productId: string; sku: string; productName: string; unitCode: string | null;
  quantityReceived: number; quantityRejected: number; rejectionReason: string | null; unitCost: number;
  lotNumber: string | null; expirationDate: string | null; purchaseUnitCode: string | null; purchaseQuantity: number | null;
  purchaseOrderLineId: string | null;
}
export interface GoodsReceiptDetail { receipt: GoodsReceiptSummary; lines: GoodsReceiptLine[] }

export interface SupplierInvoiceSummary {
  id: string; documentNumber: string; supplierReference: string | null; supplierId: string; supplierName: string;
  purchaseOrderId: string | null; purchaseOrderNumber: string | null; goodsReceiptId: string | null;
  invoiceDate: string; dueDate: string | null; currency: string; subtotal: number; taxTotal: number; total: number;
  status: string; matchStatus: string; paymentBlocked: boolean; remainingAmount: number | null;
}
export interface SupplierInvoiceLine {
  id: string; productId: string | null; sku: string | null; description: string; quantity: number; unitPrice: number;
  discount: number; lineTotal: number; taxId: string | null;
}
export interface SupplierInvoiceDetail { invoice: SupplierInvoiceSummary; lines: SupplierInvoiceLine[] }

export interface PurchaseOrderDetail {
  id: string; documentNumber: string; status: string; supplierId: string; supplierName: string; warehouseId: string | null;
  currency: string; orderDate: string; expectedDate: string | null; notes: string | null;
  subtotal: number; taxTotal: number; total: number; createdByName: string | null;
  lines: PurchaseOrderDetailLine[]; receipts: GoodsReceiptSummary[]; invoices: SupplierInvoiceSummary[];
}

export interface SupplierReturnSummary {
  id: string; documentNumber: string; supplierId: string; supplierName: string; locationName: string | null;
  reason: string | null; returnedAt: string | null; currency: string; total: number; status: string; lineCount: number;
}

export interface SupplierPriceHistory {
  productId: string; sku: string; productName: string; effectiveAt: string; unitCost: number; currency: string; documentNumber: string;
}
export interface SupplierOverview {
  supplierId: string; name: string; contactName: string | null; notes: string | null; creditLimit: number;
  paymentTermsId: string | null; paymentTermsName: string | null; paymentTermsDays: number | null;
  currency: string; purchasedLast12Months: number; openPayables: number; overduePayables: number;
  openOrders: number; lastReceiptAt: string | null; priceHistory: SupplierPriceHistory[];
}
export interface PaymentTerms {
  id: string; name: string; days: number; earlyPaymentDiscountRate: number; earlyPaymentDiscountDays: number;
  latePenaltyRate: number; description: string | null;
}

export interface ReceiveLineInput {
  productId: string; purchaseOrderLineId: string | null; quantity: number; unitCost: number;
  lotNumber: string | null; expirationDate: string | null; unitId: string | null;
  quantityRejected: number; rejectionReason: string | null;
}
export interface ReceiveInput {
  operationUuid: string; supplierId: string; inventoryLocationId: string; purchaseOrderId: string | null;
  lines: ReceiveLineInput[]; receivedAt: string | null; notes: string | null;
}

export interface InvoiceLineInput {
  productId: string | null; description: string; quantity: number; unitPrice: number; taxId: string | null; taxAmount: number; discount: number;
}
export interface InvoiceInput {
  operationUuid: string; supplierId: string; purchaseOrderId: string | null; currency: string;
  invoiceDate: string; dueDate: string | null; onCredit: boolean; tenderFinancialAccountId: string | null;
  lines: InvoiceLineInput[]; capitaliseToInventory: boolean; supplierReference: string | null; goodsReceiptId: string | null;
}

export interface ReturnInput {
  operationUuid: string; supplierId: string; inventoryLocationId: string; goodsReceiptId: string | null; payableId: string | null;
  currency: string; reason: string;
  lines: Array<{ productId: string; quantity: number; unitPrice: number; taxAmount: number; lotId: string | null }>;
}

const send = (method: string, body: unknown, idempotencyKey?: string) =>
  ({ method, body: JSON.stringify(body), ...(idempotencyKey ? { idempotencyKey } : {}) });
const query = (params: Record<string, string | number | null | undefined>) => {
  const p = new URLSearchParams();
  for (const [k, v] of Object.entries(params)) if (v !== undefined && v !== null && v !== "") p.set(k, String(v));
  const s = p.toString();
  return s ? `?${s}` : "";
};

export const getPurchaseOrder = (id: string) => apiFetch<PurchaseOrderDetail>(`/purchasing/orders/${id}`);
export const createPurchaseOrder = (body: PurchaseOrderInput) => apiFetch<string>("/purchasing/orders", send("POST", body));
export const updatePurchaseOrder = (id: string, body: PurchaseOrderInput) =>
  apiFetch<void>(`/purchasing/orders/${id}`, send("PUT", body));
export const submitPurchaseOrder = (id: string) => apiFetch<void>(`/purchasing/orders/${id}/submit`, { method: "POST" });
export const cancelPurchaseOrder = (id: string) => apiFetch<void>(`/purchasing/orders/${id}/cancel`, { method: "POST" });
export const closePurchaseOrder = (id: string) => apiFetch<void>(`/purchasing/orders/${id}/close`, { method: "POST" });

export const listReceipts = (opts: { supplierId?: string; purchaseOrderId?: string; limit?: number } = {}) =>
  apiFetch<GoodsReceiptSummary[]>(`/purchasing/receipts${query(opts)}`);
export const getReceipt = (id: string) => apiFetch<GoodsReceiptDetail>(`/purchasing/receipts/${id}`);
/** Réception : `operationUuid` fixé à l'ouverture du formulaire (un double envoi ne reçoit jamais deux fois). */
export const receiveGoods = (body: ReceiveInput) =>
  apiFetch<string>("/purchasing/receipts", send("POST", body, body.operationUuid));

export const listSupplierInvoices = (opts: { supplierId?: string; status?: string; limit?: number } = {}) =>
  apiFetch<SupplierInvoiceSummary[]>(`/purchasing/invoices${query(opts)}`);
export const getSupplierInvoice = (id: string) => apiFetch<SupplierInvoiceDetail>(`/purchasing/invoices/${id}`);
export const recordSupplierInvoice = (body: InvoiceInput) =>
  apiFetch<{ invoiceId: string; payableId: string | null; journalEntryId: string; entryNumber: string; wasReplay: boolean }>(
    "/purchasing/invoices", send("POST", body, body.operationUuid));

export const listSupplierReturns = (opts: { supplierId?: string; limit?: number } = {}) =>
  apiFetch<SupplierReturnSummary[]>(`/purchasing/returns${query(opts)}`);
export const recordSupplierReturn = (body: ReturnInput) =>
  apiFetch<string>("/purchasing/returns", send("POST", body, body.operationUuid));

export const supplierOverview = (id: string) => apiFetch<SupplierOverview>(`/suppliers/${id}/overview`);
export const setSupplierTerms = (id: string, body: { creditLimit: number; paymentTermsId: string | null; contactName: string | null; notes: string | null }) =>
  apiFetch<void>(`/suppliers/${id}/terms`, send("PUT", body));
export const listPaymentTerms = () => apiFetch<PaymentTerms[]>("/suppliers/payment-terms");
export const createPaymentTerms = (body: Omit<PaymentTerms, "id">) => apiFetch<string>("/suppliers/payment-terms", send("POST", body));
