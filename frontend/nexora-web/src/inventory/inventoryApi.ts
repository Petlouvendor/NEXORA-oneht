import { todayIso } from "@/shared/format";
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
  apiFetch<LotSweepResult>("/stock/expire-lots", { method: "POST", idempotencyKey: `sweep-${todayIso()}` });

export { q as buildStockQuery };

// ============================================================ Finalisation produit, phase 2

const post = <T>(path: string, body: unknown, idempotencyKey?: string) =>
  apiFetch<T>(path, { method: "POST", body: JSON.stringify(body), idempotencyKey });

export type StockStatus = "out" | "low" | "normal" | "over";

export interface StockOverviewRow {
  productId: string; sku: string; productName: string; categoryName: string | null; unitCode: string | null;
  locationId: string; locationName: string; onHand: number; reserved: number; available: number;
  minQuantity: number | null; reorderPoint: number | null; maxQuantity: number | null;
  averageCost: number; stockValue: number; lastMovementAt: string | null; nearestExpiry: string | null;
  stockStatus: StockStatus;
}
export interface StockOverview {
  rows: StockOverviewRow[]; total: number; totalValue: number; outCount: number; lowCount: number; overCount: number;
  /** false : l'utilisateur n'a pas inventory.view_valuation — coûts et valeurs masqués par le serveur. */
  valuationVisible: boolean;
}
export interface StockOverviewFilter {
  locationId?: string; categoryId?: string; status?: StockStatus | "expiring"; q?: string; limit?: number; offset?: number;
}
export const getStockOverview = (f: StockOverviewFilter = {}) => apiFetch<StockOverview>(`/stock/overview${q({ ...f })}`);

export interface Location {
  id: string; name: string; code: string | null; locationType: string; status: string;
  warehouseId: string | null; storeId: string | null; zone: string | null; aisle: string | null; shelf: string | null;
  bin: string | null; capacity: number | null;
}
export interface StockSite { id: string; kind: "establishment" | "store"; name: string; code: string; establishmentId: string | null; status: string }
export const getLocations = (warehouseId?: string) => apiFetch<Location[]>(`/warehouses/locations${q({ warehouseId })}`);
export const getSites = () => apiFetch<StockSite[]>("/warehouses/sites");
export const createWarehouse = (body: { name: string; code: string; type: string; establishmentId: string; address: string | null; capacity: number | null }) =>
  post<string>("/warehouses", body);
export const createLocation = (body: {
  name: string; code: string | null; locationType: string; warehouseId: string | null; storeId: string | null;
  zone: string | null; aisle: string | null; shelf: string | null; bin: string | null; capacity: number | null;
}) => post<string>("/warehouses/locations", body);

// --- Transferts ---
export interface TransferSummary {
  id: string; documentNumber: string; status: string; sourceLocationId: string; sourceLocationName: string;
  destLocationId: string; destLocationName: string; lineCount: number; quantityShipped: number; quantityReceived: number;
  value: number; shippedAt: string | null; receivedAt: string | null; notes: string | null; createdByName: string | null;
}
export interface TransferLine {
  id: string; productId: string; sku: string; productName: string; lotNumber: string | null;
  quantityShipped: number; quantityReceived: number; remaining: number; unitCost: number;
}
export interface TransferDetail { transfer: TransferSummary; lines: TransferLine[] }
export const listTransfers = (status?: string) => apiFetch<TransferSummary[]>(`/inventory/transfers${q({ status })}`);
export const getTransfer = (id: string) => apiFetch<TransferDetail>(`/inventory/transfers/${id}`);
export const createTransfer = (body: {
  sourceLocationId: string; destLocationId: string; lines: Array<{ productId: string; quantity: number; lotId: string | null }>; notes: string | null;
}) => post<string>("/inventory/transfers", body);
/** Lignes vides = tout le reste en transit est reçu. */
export const receiveTransfer = (id: string, lines: Array<{ transferLineId: string; quantity: number }>) =>
  post<void>(`/inventory/transfers/${id}/receive`, lines);
export const closeTransfer = (id: string, reason: string, operationUuid: string) =>
  post<string | null>(`/inventory/transfers/${id}/close`, { reason, operationUuid }, operationUuid);
export const cancelTransfer = (id: string, reason: string) => post<void>(`/inventory/transfers/${id}/cancel`, { reason });

// --- Inventaires ---
export interface CountSummary {
  id: string; documentNumber: string; countType: string; status: string; locationId: string; locationName: string;
  lineCount: number; countedLines: number; varianceLines: number; varianceValue: number;
  startedAt: string | null; validatedAt: string | null; validatedByName: string | null; notes: string | null;
}
export interface CountLine {
  id: string; productId: string; sku: string; productName: string; unitCode: string | null; lotNumber: string | null; lotId?: string | null;
  systemQuantity: number; countedQuantity: number | null; variance: number | null; unitCost: number;
  varianceValue: number | null; note: string | null;
}
export interface CountDetail { count: CountSummary; lines: CountLine[] }
export interface CountLineInput { productId: string; lotId: string | null; countedQuantity: number; note?: string | null }
export const listCounts = (status?: string) => apiFetch<CountSummary[]>(`/inventory/counts${q({ status })}`);
export const getCount = (id: string) => apiFetch<CountDetail>(`/inventory/counts/${id}`);
/** Type full + liste vide = tous les articles présents ou suivis à l'emplacement. */
export const startCount = (locationId: string, countType: "full" | "partial" | "cycle", productIds: string[]) =>
  post<string>("/inventory/counts", { locationId, countType, productIds });
export const saveCount = (id: string, lines: CountLineInput[]) =>
  apiFetch<void>(`/inventory/counts/${id}/lines`, { method: "PUT", body: JSON.stringify(lines) });
export const validateCount = (id: string, lines: CountLineInput[], note: string | null, operationUuid: string) =>
  post<string | null>(`/inventory/counts/${id}/validate`, { operationUuid, lines, note }, operationUuid);
export const cancelCount = (id: string, reason: string) => post<void>(`/inventory/counts/${id}/cancel`, { reason });

// --- Pertes & ajustements ---
export interface AdjustmentSummary {
  id: string; documentNumber: string; adjustmentType: string; status: string; locationId: string; locationName: string;
  reason: string | null; lineCount: number; quantityDelta: number; value: number; occurredAt: string;
  sourceType: string | null; sourceId: string | null; journalEntryId: string | null; createdByName: string | null;
}
export interface AdjustmentLine {
  id: string; productId: string; sku: string; productName: string; lotNumber: string | null;
  quantityDelta: number; unitCost: number; value: number; note: string | null;
}
export interface AdjustmentDetail { adjustment: AdjustmentSummary; lines: AdjustmentLine[] }
export const listAdjustments = (opts: { type?: string; from?: string; to?: string } = {}) =>
  apiFetch<AdjustmentSummary[]>(`/inventory/adjustments${q(opts)}`);
export const getAdjustment = (id: string) => apiFetch<AdjustmentDetail>(`/inventory/adjustments/${id}`);
export const recordLoss = (body: {
  operationUuid: string; locationId: string; lossType: "loss" | "breakage" | "expiry" | "theft"; reason: string;
  lines: Array<{ productId: string; quantity: number; lotId: string | null; note: string | null }>; occurredAt: string | null;
}) => post<string>("/inventory/losses", body, body.operationUuid);
export const recordAdjustment = (body: {
  operationUuid: string; locationId: string; adjustmentType: "correction" | "found"; reason: string;
  lines: Array<{ productId: string; quantityDelta: number; lotId: string | null; unitCost: number | null; note: string | null }>;
  occurredAt: string | null;
}) => post<string>("/inventory/adjustments", body, body.operationUuid);
