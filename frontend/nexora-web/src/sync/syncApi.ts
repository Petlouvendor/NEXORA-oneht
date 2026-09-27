import { apiFetch } from "@/core/apiClient";

export interface SyncDevice {
  id: string;
  deviceIdentifier: string;
  name: string;
  deviceType: string;
  status: string;
  lastSyncAt: string | null;
  lastSeenAt: string | null;
  pendingOperations: number;
}

export interface SyncConflict {
  id: string;
  syncOperationId: string;
  conflictType: string;
  entityType: string;
  entityId: string | null;
  resolution: string;
  detail: string | null;
  recommendedAction: string | null;
}

export interface SyncCenter {
  generatedAt: string;
  pendingOperations: number;
  syncedOperations: number;
  failedOperations: number;
  openConflicts: number;
  lastSyncAt: string | null;
  minutesSinceLastSync: number | null;
  pendingThresholdExceeded: boolean;
  devices: SyncDevice[];
  conflicts: SyncConflict[];
  recommendedActions: string[];
}

export interface OfflinePolicy {
  companyId: string;
  allowOfflineSales: boolean;
  allowOfflineCredit: boolean;
  offlineCreditLimit: number;
  allowOfflineDiscount: boolean;
  allowOfflineRefund: boolean;
  /** Module clients §24 : versements clients saisis hors ligne. */
  allowOfflineCustomerPayment: boolean;
  maxOfflineMinutes: number;
  pendingAlertThreshold: number;
  allowedPaymentMethods: string[];
  autoSync: boolean;
}

export const getSyncCenter = () => apiFetch<SyncCenter>("/sync/center");

export const getOfflinePolicy = () => apiFetch<OfflinePolicy>("/sync/offline-policy");

export const setOfflinePolicy = (patch: Partial<OfflinePolicy>) =>
  apiFetch<void>("/sync/offline-policy", { method: "PUT", body: JSON.stringify(patch) });

export const resolveConflict = (conflictId: string, resolution: string, note?: string) =>
  apiFetch<void>(`/sync/conflicts/${conflictId}/resolve`, {
    method: "POST",
    body: JSON.stringify({ resolution, note: note ?? null }),
  });

export const revokeDevice = (deviceId: string, reason: string) =>
  apiFetch<void>(`/sync/devices/${deviceId}/revoke`, { method: "POST", body: JSON.stringify({ reason }) });

// --- Push d'un lot d'opérations offline (§14, Phase 19 §26) -----------

export interface SyncOperationInput {
  operationUuid: string;
  entityType: string;
  operationType: string;
  payload: unknown;
  priority?: number;
  clientCreatedAt?: string | null;
}

export interface PushBatchResult {
  batchId: string;
  confirmed: number;
  conflicts: number;
  failed: number;
  operations: Array<{
    operationUuid: string;
    status: string;
    resultEntityId: string | null;
    conflictId: string | null;
    message: string | null;
  }>;
}

export const pushBatch = (deviceId: string, operations: SyncOperationInput[]) =>
  apiFetch<PushBatchResult>("/sync/batch", {
    method: "POST",
    body: JSON.stringify({ deviceId, operations }),
  });
