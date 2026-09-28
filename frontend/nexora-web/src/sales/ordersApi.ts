import { apiFetch } from "@/core/apiClient";

export interface OrderSummary {
  id: string;
  documentNumber: string;
  orderType: string;
  status: string;
  customerName: string | null;
  currency: string;
  total: number;
  depositAmount: number;
  remainingBalance: number;
  expectedAt: string | null;
  createdAt: string;
}

export interface OrderLine {
  id: string;
  productId: string;
  description: string;
  quantity: number;
  quantityFulfilled: number;
  unitPrice: number;
  discount: number;
  taxAmount: number;
  lineTotal: number;
}

export interface Order {
  id: string;
  documentNumber: string;
  orderType: string;
  status: string;
  customerId: string | null;
  currency: string;
  subtotal: number;
  discountTotal: number;
  taxTotal: number;
  total: number;
  depositAmount: number;
  remainingBalance: number;
  onCredit: boolean;
  expectedAt: string | null;
  dueDate: string | null;
  deliveryAddress: string | null;
  notes: string | null;
  reservedAt: string | null;
  fulfilledAt: string | null;
  lines: OrderLine[];
  saleIds: string[];
  customerName: string | null;
  inventoryLocationId: string | null;
  locationName: string | null;
  /** Acompte encore disponible (avoir non encore imputé sur une vente). */
  depositAvailable: number;
  sales: OrderSaleRef[] | null;
  cancelledReason: string | null;
  createdAt: string | null;
}

export interface OrderSaleRef { id: string; documentNumber: string; soldAt: string; total: number; amountDue: number; status: string }

export interface PaymentOptions {
  accounts: Array<{ id: string; name: string; accountType: string; currency: string }>;
  methods: Array<{ id: string; name: string; type: string }>;
}

export interface CreateOrderLine {
  productId: string;
  quantity: number;
  unitPriceOverride?: number | null;
  lineDiscount: number;
  taxId?: string | null;
}

export interface CreateOrderRequest {
  customerId?: string | null;
  currency: string;
  orderType: string;
  inventoryLocationId?: string | null;
  onCredit: boolean;
  expectedAt?: string | null;
  dueDate?: string | null;
  deliveryAddress?: string | null;
  notes?: string | null;
  lines: CreateOrderLine[];
}

const uuid = () =>
  typeof crypto !== "undefined" && "randomUUID" in crypto
    ? crypto.randomUUID()
    : `op-${Date.now()}-${Math.random().toString(16).slice(2)}`;

export const listOrders = (status?: string, limit = 50) =>
  apiFetch<OrderSummary[]>(`/orders${status ? `?status=${status}&limit=${limit}` : `?limit=${limit}`}`);

export const getOrder = (id: string) => apiFetch<Order>(`/orders/${id}`);

export const createOrder = (body: CreateOrderRequest) =>
  apiFetch<string>("/orders", { method: "POST", body: JSON.stringify(body) });

/** Modification d'une commande en brouillon (phase 4). */
export const updateOrder = (id: string, body: CreateOrderRequest) =>
  apiFetch<void>(`/orders/${id}`, { method: "PUT", body: JSON.stringify(body) });
export const getOrderPaymentOptions = () => apiFetch<PaymentOptions>("/orders/payment-options");

export const confirmOrder = (id: string) => apiFetch<void>(`/orders/${id}/confirm`, { method: "POST" });
export const reserveOrder = (id: string) => apiFetch<void>(`/orders/${id}/reserve`, { method: "POST" });
export const cancelOrder = (id: string, reason: string) =>
  apiFetch<void>(`/orders/${id}/cancel`, { method: "POST", body: JSON.stringify({ reason }) });

/** Acompte : `operationUuid` fixé à l'ouverture du dialogue (un double envoi n'encaisse jamais deux fois). */
export const recordDeposit = (id: string, amount: number, tenderFinancialAccountId: string, operationUuid: string = uuid()) =>
  apiFetch<void>(`/orders/${id}/deposit`, {
    method: "POST",
    idempotencyKey: operationUuid,
    body: JSON.stringify({ operationUuid, amount, tenderFinancialAccountId }),
  });

/** Livraison (totale ou partielle) : crée la vente ; l'acompte disponible est imputé par le serveur. */
export const completeOrder = (
  id: string,
  tenders: Array<{ financialAccountId: string; paymentMethodId?: string | null; amount: number; reference?: string | null }>,
  onCredit: boolean,
  creditDueDate?: string | null,
  lineQuantities: Record<string, number> | null = null,
  operationUuid: string = uuid(),
) =>
  apiFetch<{ saleId: string; documentNumber: string; total: number; creditAmount: number; amountPaid: number }>(`/orders/${id}/complete`, {
    method: "POST",
    idempotencyKey: operationUuid,
    body: JSON.stringify({
      operationUuid,
      cashSessionId: null,
      onCredit,
      creditDueDate: creditDueDate ?? null,
      tenders: tenders.map((t) => ({ ...t, paymentMethodId: t.paymentMethodId ?? null, reference: t.reference ?? null })),
      lineQuantities,
    }),
  });

// --- Détail d'une vente (NEXORA ONE §5-6 : cible de la recherche Ctrl+K et du fil d'activité) ---

export interface SaleLine {
  id: string; productId: string; description: string; quantity: number; unitPrice: number;
  discount: number; taxAmount: number; lineTotal: number; unitCost: number | null; lotNumber: string | null;
}
export interface SalePayment { paymentId: string; method: string; amount: number; receivedAt: string }
export interface SaleDetail {
  id: string; documentNumber: string; status: string; saleType: string; customerId: string | null;
  currency: string; soldAt: string; subtotal: number; discountTotal: number; taxTotal: number;
  total: number; amountPaid: number; amountDue: number; changeGiven: number;
  salesOrderId: string | null; cashSessionId: string | null; lines: SaleLine[]; payments: SalePayment[];
}

export const getSale = (id: string) => apiFetch<SaleDetail>(`/sales/${id}`);
