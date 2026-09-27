import { apiFetch } from "@/core/apiClient";

// Mobile, terrain & multi-appareils (Phase 44 §12-13, §22-23, §25, §46, §48).

export interface DeliveryStop {
  id: string; deliveryRouteId: string; sequenceNo: number; salesOrderId: string | null; customerId: string | null;
  address: string; latitude: number | null; longitude: number | null; contactPhone: string | null;
  status: "pending" | "en_route" | "arrived" | "delivered" | "failed" | "returned";
  signatureAttachmentId: string | null; photoAttachmentId: string | null; amountCollected: number | null;
  deliveredAt: string | null; failureReason: string | null;
}
export interface DeliveryRoute {
  id: string; driverUserId: string | null; routeDate: string; vehicle: string | null;
  status: "planned" | "in_progress" | "completed" | "cancelled";
  startedAt: string | null; completedAt: string | null; stops: DeliveryStop[];
}

export interface FieldIntervention {
  id: string; technicianUserId: string | null; customerId: string | null; equipmentDescription: string;
  checklist: Array<{ label: string; done: boolean }>; partsUsed: Array<{ productId: string; quantity: number }>;
  scheduledAt: string | null; startedAt: string | null; completedAt: string | null; timeSpentMinutes: number | null;
  status: "scheduled" | "in_progress" | "completed" | "cancelled";
  signatureAttachmentId: string | null; reportText: string;
}

export interface CrashReport { id: string; deviceId: string | null; appVersion: string; os: string; message: string; occurredAt: string }
export interface MobileConfig {
  allowOfflineSales: boolean; allowOfflineCredit: boolean; offlineCreditLimit: number; maxOfflineMinutes: number;
  supportedOfflineEntityTypes: string[]; pushEnabled: boolean; minAppVersion: string;
}
export interface Attachment { id: string; kind: "photo" | "signature"; referenceType: string | null; referenceId: string | null; contentType: string; sizeBytes: number; sha256Hash: string; capturedAt: string }

// --- §12, §52 Tournées de livraison ---
export const listRoutes = (date?: string) => apiFetch<DeliveryRoute[]>(`/field-ops/routes${date ? `?date=${date}` : ""}`);
export const createRoute = (body: { driverUserId?: string | null; routeDate: string; vehicle?: string | null; stops: Array<{ salesOrderId?: string | null; customerId?: string | null; address: string; latitude?: number | null; longitude?: number | null; contactPhone?: string | null }> }) =>
  apiFetch<DeliveryRoute>("/field-ops/routes", { method: "POST", body: JSON.stringify(body) });
export const getRoute = (id: string) => apiFetch<DeliveryRoute>(`/field-ops/routes/${id}`);
export const myStops = () => apiFetch<DeliveryStop[]>("/field-ops/my-stops");
export const advanceStop = (id: string, status: "en_route" | "arrived") =>
  apiFetch<DeliveryStop>(`/field-ops/stops/${id}/advance`, { method: "POST", body: JSON.stringify(status) });
export const completeStop = (id: string, body: { operationUuid: string; latitude?: number | null; longitude?: number | null; signatureAttachmentId?: string | null; photoAttachmentId?: string | null; amountCollected?: number | null; paymentFinancialAccountId?: string | null }) =>
  apiFetch<DeliveryStop>(`/field-ops/stops/${id}/complete`, { method: "POST", body: JSON.stringify(body) });
export const failStop = (id: string, reason: string) =>
  apiFetch<DeliveryStop>(`/field-ops/stops/${id}/fail`, { method: "POST", body: JSON.stringify({ reason }) });

// --- §13 Interventions techniciens ---
export const listInterventions = (status?: string) => apiFetch<FieldIntervention[]>(`/field-ops/interventions${status ? `?status=${status}` : ""}`);
export const createIntervention = (body: { technicianUserId?: string | null; customerId?: string | null; equipmentDescription: string; scheduledAt?: string | null }) =>
  apiFetch<FieldIntervention>("/field-ops/interventions", { method: "POST", body: JSON.stringify(body) });
export const myInterventions = () => apiFetch<FieldIntervention[]>("/field-ops/interventions/mine");
export const startIntervention = (id: string) => apiFetch<FieldIntervention>(`/field-ops/interventions/${id}/start`, { method: "POST" });
export const completeIntervention = (id: string, body: { operationUuid: string; checklist: Array<{ label: string; done: boolean }>; partsUsed: Array<{ productId: string; quantity: number }>; timeSpentMinutes: number; reportText: string; signatureAttachmentId?: string | null }) =>
  apiFetch<FieldIntervention>(`/field-ops/interventions/${id}/complete`, { method: "POST", body: JSON.stringify(body) });

// --- §22-23 Pièces jointes ---
export const uploadAttachment = (body: { kind: "photo" | "signature"; referenceType?: string | null; referenceId?: string | null; contentBase64: string; contentType: string }) =>
  apiFetch<Attachment>("/attachments", { method: "POST", body: JSON.stringify(body) });

// --- §46, §48 Diagnostics & config ---
export const getMobileConfig = () => apiFetch<MobileConfig>("/mobile/config");
export const listCrashReports = (limit = 100) => apiFetch<CrashReport[]>(`/mobile/crash-reports?limit=${limit}`);

// --- §20, §32 Jeton push ---
export const registerPushToken = (deviceId: string, pushToken: string, pushProvider: "fcm" | "apns" | "webpush") =>
  apiFetch<void>(`/sync/devices/${deviceId}/push-token`, { method: "POST", body: JSON.stringify({ pushToken, pushProvider }) });
