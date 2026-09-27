import { apiFetch } from "@/core/apiClient";

export interface StockAlert {
  alertType: string;
  productId: string;
  sku: string;
  productName: string;
  locationId: string;
  locationName: string;
  onHand: number;
  available: number;
  threshold: number | null;
  detail: string;
}

export interface StockMovement {
  id: string;
  occurredAt: string;
  movementType: string;
  productId: string;
  sku: string;
  locationId: string;
  locationName: string;
  lotNumber: string | null;
  quantity: number;
  unitCost: number;
  referenceType: string | null;
  referenceId: string | null;
}

export interface Lot {
  id: string;
  lotNumber: string;
  productId: string;
  sku: string;
  productName: string;
  expirationDate: string | null;
  manufactureDate: string | null;
  receivedAt: string | null;
  status: string;
  expiryStatus: string;
  onHand: number;
  reserved: number;
  supplierId: string | null;
  supplierName: string | null;
  blockedReason: string | null;
  recallReference: string | null;
}

export interface ExpiringLot {
  lotId: string;
  lotNumber: string;
  productId: string;
  sku: string;
  productName: string;
  expirationDate: string;
  daysToExpiry: number;
  expiryStatus: string;
  onHand: number;
  stockValue: number;
}

export interface StockBalance {
  productId: string;
  sku: string;
  productName: string;
  locationId: string;
  locationName: string;
  onHand: number;
  reserved: number;
  available: number;
  averageCost: number;
  stockValue: number;
}

export interface Warehouse {
  id: string;
  name: string;
  code: string;
  type: string;
  status: string;
  establishmentId: string;
  address: string | null;
  capacity: number | null;
  managerUserId: string | null;
}

export interface LotSweepResult {
  lotsExpired: number;
  stockValueWrittenOff: number;
  movementsPosted: number;
}

const q = (params: Record<string, string | number | undefined | null>) => {
  const p = new URLSearchParams();
  for (const [k, v] of Object.entries(params)) if (v !== undefined && v !== null && v !== "") p.set(k, String(v));
  const s = p.toString();
  return s ? `?${s}` : "";
};

export const getAlerts = () => apiFetch<StockAlert[]>("/stock/alerts");
export const getExpiringLots = (withinDays?: number) =>
  apiFetch<ExpiringLot[]>(`/stock/lots/expiring${q({ withinDays })}`);
export const getLots = (opts: { productId?: string; status?: string; expiringWithinDays?: number } = {}) =>
  apiFetch<Lot[]>(`/stock/lots${q(opts)}`);
export const getMovements = (opts: { productId?: string; locationId?: string; limit?: number; offset?: number } = {}) =>
  apiFetch<StockMovement[]>(`/stock/movements${q(opts)}`);
export const getBalances = (opts: { locationId?: string; onlyPositive?: boolean } = {}) =>
  apiFetch<StockBalance[]>(`/stock/balances${q({ ...opts, onlyPositive: opts.onlyPositive ? "true" : undefined })}`);
export const getWarehouses = () => apiFetch<Warehouse[]>("/warehouses");

export const blockLot = (lotId: string, reason: string) =>
  apiFetch<void>(`/stock/lots/${lotId}/block`, { method: "POST", body: JSON.stringify({ reason }) });
export const unblockLot = (lotId: string, reason: string) =>
  apiFetch<void>(`/stock/lots/${lotId}/unblock`, { method: "POST", body: JSON.stringify({ reason }) });
export const recallLot = (lotId: string, reference: string, reason: string) =>
  apiFetch<void>(`/stock/lots/${lotId}/recall`, { method: "POST", body: JSON.stringify({ reference, reason }) });
export const sweepExpiredLots = () =>
  apiFetch<LotSweepResult>("/stock/expire-lots", { method: "POST", idempotencyKey: `sweep-${new Date().toISOString().slice(0, 10)}` });

export { q as buildStockQuery };
