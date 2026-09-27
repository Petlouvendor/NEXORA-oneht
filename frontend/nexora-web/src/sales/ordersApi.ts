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

export const confirmOrder = (id: string) => apiFetch<void>(`/orders/${id}/confirm`, { method: "POST" });
export const reserveOrder = (id: string) => apiFetch<void>(`/orders/${id}/reserve`, { method: "POST" });
export const cancelOrder = (id: string, reason: string) =>
  apiFetch<void>(`/orders/${id}/cancel`, { method: "POST", body: JSON.stringify({ reason }) });

export const recordDeposit = (id: string, amount: number, tenderFinancialAccountId: string) =>
  apiFetch<void>(`/orders/${id}/deposit`, {
    method: "POST",
    idempotencyKey: uuid(),
    body: JSON.stringify({ operationUuid: uuid(), amount, tenderFinancialAccountId }),
  });

export const completeOrder = (
  id: string,
  tenders: Array<{ financialAccountId: string; paymentMethodId?: string | null; amount: number }>,
  onCredit: boolean,
  creditDueDate?: string | null,
) =>
  apiFetch<{ saleId: string; total: number }>(`/orders/${id}/complete`, {
    method: "POST",
    idempotencyKey: uuid(),
    body: JSON.stringify({
      operationUuid: uuid(),
      cashSessionId: null,
      onCredit,
      creditDueDate: creditDueDate ?? null,
      tenders,
      lineQuantities: null,
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
