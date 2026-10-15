import { apiFetch } from "@/core/apiClient";

export interface CheckoutLineInput {
  productId: string;
  quantity: number;
  unitPriceOverride?: number | null;
  lineDiscount: number;
  taxId?: string | null;
}

export interface TenderInput {
  financialAccountId: string;
  paymentMethodId?: string | null;
  amount: number;
  reference?: string | null;
}

export interface CheckoutCommand {
  operationUuid: string;
  inventoryLocationId: string;
  storeId?: string | null;
  posTerminalId?: string | null;
  cashSessionId?: string | null;
  customerId?: string | null;
  currency: string;
  lines: CheckoutLineInput[];
  tenders: TenderInput[];
  amountTendered: number;
  creditDueDate?: string | null;
  couponCode?: string | null;
  creditOverrideReason?: string | null;
  soldAt?: string | null;
  /** Module clients §7, §15 : part payée avec l'avoir du client (imputée par le serveur). */
  storeCreditAmount?: number;
}

export interface CheckoutResult {
  saleId: string;
  documentNumber: string;
  saleType: string;
  subtotal: number;
  discountTotal: number;
  taxTotal: number;
  total: number;
  amountPaid: number;
  creditAmount: number;
  changeGiven: number;
  journalEntryId: string;
  receivableId: string | null;
  creditOverrideApplied: boolean;
  storeCreditUsed?: number;
  lines: Array<{ productId: string; quantity: number; unitPrice: number; discount: number; taxAmount: number; lineTotal: number }>;
}

export const checkout = (cmd: CheckoutCommand) =>
  apiFetch<CheckoutResult>("/pos/checkout", {
    method: "POST",
    body: JSON.stringify(cmd),
    headers: { "Idempotency-Key": cmd.operationUuid },
  });

// --- Droits et limites POS (guide §8, §12) — l'API reste l'autorité ------------

export interface PosLimits {
  discountLimitPercent: number | null;
  discountApprovalLimitPercent: number | null;
  canDiscount: boolean;
  canChangePrice: boolean;
  canSellCredit: boolean;
  canOverrideCredit: boolean;
  canRequestReturn: boolean;
  canRefund: boolean;
  canApproveDiscount: boolean;
  canManageSessions: boolean;
}

export const getMyLimits = () => apiFetch<PosLimits>("/pos/me/limits");

// --- Recherche produit (guide §9, §24) : liste courte, jamais le catalogue complet ---

export type Availability = "in_stock" | "low" | "out" | "untracked";

export interface PosProduct {
  productId: string;
  name: string;
  sku: string;
  barcode: string | null;
  salePrice: number | null;
  currency: string | null;
  availability: Availability;
  /** Taux de la taxe par défaut (0 sans taxe) et prix saisi TTC ou HT (phase 6). */
  taxRate?: number;
  priceIncludesTax?: boolean;
  /** Conditionnement du code lu (carton de 12 → 12) ; 1 par défaut (audit 2026-10, A-015). */
  packSize?: number;
}

export interface PosProductSearch {
  items: PosProduct[];
  minChars: number;
  maxResults: number;
}

export const searchProducts = (q: string, locationId?: string | null, signal?: AbortSignal) =>
  apiFetch<PosProductSearch>(
    `/pos/products/search?q=${encodeURIComponent(q)}${locationId ? `&locationId=${locationId}` : ""}`,
    { signal },
  );

export const scanProduct = (code: string, locationId?: string | null) =>
  apiFetch<PosProduct>(`/pos/products/scan?code=${encodeURIComponent(code)}${locationId ? `&locationId=${locationId}` : ""}`);

// --- Session de caisse (guide §13) --------------------------------------------

export interface CurrentSession {
  sessionId: string;
  cashRegisterId: string;
  registerName: string;
  currency: string;
  financialAccountId: string | null;
  inventoryLocationId: string | null;
  status: "open" | "closing";
  openedAt: string;
  openingBalance: number;
  saleCount: number;
}

export interface CashRegister {
  id: string;
  name: string;
  code: string;
  currency: string;
  financialAccountId: string | null;
  inventoryLocationId: string | null;
  hasOpenSession: boolean;
  openedBy: string | null;   // personne dont la session occupe la caisse
}

/** Session ouverte d'un autre caissier (supervision, guide §13) — sans montant attendu. */
export interface OpenCashSession {
  sessionId: string;
  registerName: string;
  cashierId: string;
  cashierName: string | null;
  status: string;
  openedAt: string;
  saleCount: number;
  currency: string;
}

export interface SessionSummary {
  sessionId: string;
  status: "closed" | "closing";
  openingBalance: number;
  expectedBalance: number;
  countedBalance: number | null;
  difference: number | null;
  saleCount: number;
  cashSales: number;
}

export interface PendingClosing {
  sessionId: string;
  registerName: string;
  cashierId: string;
  cashierName: string | null;
  openedAt: string;
  countedAt: string | null;
  expectedBalance: number;
  countedBalance: number;
  difference: number;
  currency: string;
  notes: string | null;
}

/** 204 → aucune session ouverte. */
export const getCurrentSession = () => apiFetch<CurrentSession | undefined>("/pos/sessions/current");
export const listRegisters = () => apiFetch<CashRegister[]>("/pos/registers");
export const openSession = (cashRegisterId: string, openingBalance: number) =>
  apiFetch<string>("/pos/sessions/open", {
    method: "POST", body: JSON.stringify({ cashRegisterId, posTerminalId: null, openingBalance }),
  });
export const closeSession = (sessionId: string, countedBalance: number, notes: string | null) =>
  apiFetch<SessionSummary>("/pos/sessions/close", {
    method: "POST", body: JSON.stringify({ sessionId, countedBalance, notes }),
  });
export const listPendingClosings = () => apiFetch<PendingClosing[]>("/pos/sessions/pending-closings");
export const listOpenSessions = () => apiFetch<OpenCashSession[]>("/pos/sessions/open");
export const validateClosing = (sessionId: string, note: string) =>
  apiFetch<SessionSummary>(`/pos/sessions/${sessionId}/validate-close`, {
    method: "POST", body: JSON.stringify({ note }),
  });

// --- Remises exceptionnelles (guide §12) ---------------------------------------

export type RequestStatus = "pending_approval" | "approved" | "rejected" | "consumed" | "expired" | "executed" | "cancelled";

export interface DiscountRequest {
  id: string;
  requestNumber: string;
  operationUuid: string;
  productId: string;
  productName: string;
  quantity: number;
  lineGross: number;
  requestedAmount: number;
  requestedPercent: number;
  currency: string;
  reason: string;
  status: RequestStatus;
  requestedByName: string | null;
  requestedAt: string;
  expiresAt: string;
  decidedByName: string | null;
  decisionNote: string | null;
}

export const requestDiscount = (body: {
  operationUuid: string; productId: string; quantity: number; requestedDiscount: number;
  currency: string; inventoryLocationId: string; customerId?: string | null; reason: string;
}) => apiFetch<DiscountRequest>("/pos/discount-requests", {
  method: "POST", body: JSON.stringify(body), idempotencyKey: crypto.randomUUID(),
});
export const getDiscountRequest = (id: string) => apiFetch<DiscountRequest>(`/pos/discount-requests/${id}`);
export const listDiscountRequests = (status?: string) =>
  apiFetch<DiscountRequest[]>(`/pos/discount-requests${status ? `?status=${status}` : ""}`);
export const approveDiscount = (id: string, note: string | null) =>
  apiFetch<DiscountRequest>(`/pos/discount-requests/${id}/approve`, { method: "POST", body: JSON.stringify({ note }) });
export const rejectDiscount = (id: string, note: string) =>
  apiFetch<DiscountRequest>(`/pos/discount-requests/${id}/reject`, { method: "POST", body: JSON.stringify({ note }) });

// --- Retours avec validation Manager (guide §10) --------------------------------

export interface ReturnableSaleLine {
  saleLineId: string;
  productId: string;
  description: string;
  quantitySold: number;
  quantityReturned: number;
  quantityPending: number;
  quantityReturnable: number;
  unitPrice: number;
  lineTotal: number;
}

export interface ReturnableSale {
  saleId: string;
  documentNumber: string;
  soldAt: string;
  status: string;
  currency: string;
  total: number;
  lines: ReturnableSaleLine[];
}

export interface ReturnRequest {
  id: string;
  requestNumber: string;
  saleId: string;
  saleNumber: string;
  status: RequestStatus;
  reason: string;
  refundMethod: string;
  estimatedRefund: number;
  currency: string;
  requestedByName: string | null;
  requestedAt: string;
  decidedByName: string | null;
  decidedAt: string | null;
  decisionNote: string | null;
  lines: Array<{ saleLineId: string; description: string; quantity: number; restock: boolean; estimatedRefund: number }>;
}

export const lookupSale = (number: string) =>
  apiFetch<ReturnableSale>(`/pos/sales/lookup?number=${encodeURIComponent(number)}`);
export const requestReturn = (body: {
  operationUuid: string; saleId: string; inventoryLocationId: string; cashSessionId: string | null;
  reason: string; refundMethod: string; refundFinancialAccountId: string | null;
  lines: Array<{ saleLineId: string; quantity: number; restock: boolean }>;
}) => apiFetch<ReturnRequest>("/pos/return-requests", {
  method: "POST", body: JSON.stringify(body), idempotencyKey: body.operationUuid,
});
export const listReturnRequests = (status?: string) =>
  apiFetch<ReturnRequest[]>(`/pos/return-requests${status ? `?status=${status}` : ""}`);
export const approveReturn = (id: string, note: string | null) =>
  apiFetch<ReturnRequest>(`/pos/return-requests/${id}/approve`, { method: "POST", body: JSON.stringify({ note }) });
export const rejectReturn = (id: string, note: string) =>
  apiFetch<ReturnRequest>(`/pos/return-requests/${id}/reject`, { method: "POST", body: JSON.stringify({ note }) });

// --- Paniers en attente (guide §11) ---------------------------------------------

export const voidParkedSale = (saleId: string, reason: string) =>
  apiFetch<void>(`/pos/sales/${saleId}/void`, { method: "POST", body: JSON.stringify({ reason }) });

// --- Validation sur place par un responsable (guide §10, §12, §13) --------------------

export type ManagerOverrideAction =
  | "approve_return" | "reject_return" | "approve_discount" | "reject_discount" | "validate_close"
  | "approve_credit_override" | "reject_credit_override";

export interface ManagerOverrideResult {
  action: ManagerOverrideAction;
  targetId: string;
  approvedBy: string;
  approvedByName: string | null;
  status: string;
}

/** Les identifiants du responsable ne transitent que dans cet appel ; ils ne sont jamais stockés. */
export const managerOverride = (body: {
  action: ManagerOverrideAction; targetId: string; email: string; password: string; mfaCode: string | null; note: string | null;
}) => apiFetch<ManagerOverrideResult>("/pos/manager-override", { method: "POST", body: JSON.stringify(body) });

/** Libellés français des états métier (guide §26). */
export const STATUS_LABEL: Record<string, string> = {
  pending_approval: "En attente de validation",
  approved: "Approuvée",
  rejected: "Refusée",
  consumed: "Utilisée",
  expired: "Expirée",
  executed: "Exécutée",
  cancelled: "Annulée",
};

// --- Aperçu du panier calculé par le serveur (phase 6) ---
export interface CartPreviewLine {
  productId: string; quantity: number; unitPrice: number; manualDiscount: number; promotionDiscount: number;
  promotionLabel: string | null; taxAmount: number; lineTotal: number;
}
export interface CartPreview { subtotal: number; discountTotal: number; taxTotal: number; total: number; lines: CartPreviewLine[] }

export const previewCart = (body: {
  inventoryLocationId: string; customerId: string | null; currency: string;
  lines: Array<{ productId: string; quantity: number; lineDiscount: number }>;
}, signal?: AbortSignal) =>
  apiFetch<CartPreview>("/pos/cart/preview", { method: "POST", body: JSON.stringify(body), signal });
