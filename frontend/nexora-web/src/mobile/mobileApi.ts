import { apiFetch } from "@/core/apiClient";
import { ensureDevice, storedDeviceId } from "@/pos/offlineStore";
import type { PendingImage } from "@/shared/syncOperations";

// Terrain (Phase 44 ; finalisation phase 12) : assigné → en route → sur place → livré (preuve) → synchronisé.

export type StopStatus = "pending" | "en_route" | "arrived" | "delivered" | "failed" | "returned";
export interface DeliveryStop {
  id: string; deliveryRouteId: string; sequenceNo: number; salesOrderId: string | null; customerId: string | null;
  address: string; latitude: number | null; longitude: number | null; contactPhone: string | null;
  status: StopStatus; signatureAttachmentId: string | null; photoAttachmentId: string | null; amountCollected: number | null;
  deliveredAt: string | null; failureReason: string | null;
  customerName: string | null; orderNumber: string | null; orderTotal: number | null; orderDeposit: number | null; currency: string | null;
  enRouteAt: string | null; arrivedAt: string | null; completedByName: string | null; driverName: string | null; routeDate: string | null;
}
export interface DeliveryRoute {
  id: string; driverUserId: string | null; routeDate: string; vehicle: string | null;
  status: "planned" | "in_progress" | "completed" | "cancelled";
  startedAt: string | null; completedAt: string | null; stops: DeliveryStop[]; driverName: string | null;
}
export interface FieldIntervention {
  id: string; technicianUserId: string | null; customerId: string | null; equipmentDescription: string;
  checklist: Array<{ label: string; done: boolean }>; partsUsed: Array<{ productId: string; quantity: number }>;
  scheduledAt: string | null; startedAt: string | null; completedAt: string | null; timeSpentMinutes: number | null;
  status: "scheduled" | "in_progress" | "completed" | "cancelled";
  signatureAttachmentId: string | null; reportText: string;
  customerName: string | null; technicianName: string | null; completedByName: string | null;
}
export interface FieldOptions {
  agents: Array<{ id: string; fullName: string }>;
  orders: Array<{ id: string; documentNumber: string; customerId: string | null; customerName: string | null; deliveryAddress: string | null;
    total: number; deposit: number; currency: string; status: string }>;
  cashAccounts: Array<{ id: string; name: string; currency: string }>;
  /** La position GPS peut être enregistrée à la preuve (paramètre de l'entreprise). */
  captureLocation: boolean;
}
export interface CrashReport { id: string; deviceId: string | null; appVersion: string; os: string; message: string; occurredAt: string }
export interface MobileConfig {
  allowOfflineSales: boolean; allowOfflineCredit: boolean; offlineCreditLimit: number; maxOfflineMinutes: number;
  supportedOfflineEntityTypes: string[]; pushEnabled: boolean; minAppVersion: string;
}
export interface Attachment { id: string; kind: "photo" | "signature"; referenceType: string | null; referenceId: string | null; contentType: string; sizeBytes: number; sha256Hash: string; capturedAt: string }

export const STOP_STATUS: Record<StopStatus, { label: string; tone: "success" | "warning" | "error" | "info" | undefined }> = {
  pending: { label: "Assigné", tone: undefined }, en_route: { label: "En route", tone: "info" }, arrived: { label: "Sur place", tone: "info" },
  delivered: { label: "Livré", tone: "success" }, failed: { label: "Échec", tone: "error" }, returned: { label: "Retourné", tone: "warning" },
};
export const ROUTE_STATUS: Record<DeliveryRoute["status"], string> = { planned: "Planifiée", in_progress: "En cours", completed: "Terminée", cancelled: "Annulée" };
export const INTERVENTION_STATUS: Record<FieldIntervention["status"], { label: string; tone: "success" | "warning" | "error" | "info" | undefined }> = {
  scheduled: { label: "Assignée", tone: undefined }, in_progress: { label: "Sur place", tone: "info" },
  completed: { label: "Terminée", tone: "success" }, cancelled: { label: "Annulée", tone: "error" },
};

const post = (body?: unknown) => ({ method: "POST", ...(body === undefined ? {} : { body: JSON.stringify(body) }) });

export const getFieldOptions = () => apiFetch<FieldOptions>("/field-ops/options");

// --- Tournées ---
export const listRoutes = (date?: string) => apiFetch<DeliveryRoute[]>(`/field-ops/routes${date ? `?date=${date}` : ""}`);
export const createRoute = (body: { driverUserId: string | null; routeDate: string; vehicle: string | null;
  stops: Array<{ salesOrderId: string | null; customerId: string | null; address: string; latitude: null; longitude: null; contactPhone: string | null }> }) =>
  apiFetch<DeliveryRoute>("/field-ops/routes", post(body));
export const myStops = () => apiFetch<DeliveryStop[]>("/field-ops/my-stops");
export const advanceStop = (id: string, status: "en_route" | "arrived") => apiFetch<DeliveryStop>(`/field-ops/stops/${id}/advance`, post(status));
export const completeStop = (id: string, body: { operationUuid: string; latitude: number | null; longitude: number | null;
  signatureAttachmentId: string | null; photoAttachmentId: string | null; amountCollected: number | null; paymentFinancialAccountId: string | null }) =>
  apiFetch<DeliveryStop>(`/field-ops/stops/${id}/complete`, post(body));
export const failStop = (id: string, reason: string) => apiFetch<DeliveryStop>(`/field-ops/stops/${id}/fail`, post({ reason }));

// --- Interventions ---
export const listInterventions = (status?: string) => apiFetch<FieldIntervention[]>(`/field-ops/interventions${status ? `?status=${status}` : ""}`);
export const createIntervention = (body: { technicianUserId: string | null; customerId: string | null; equipmentDescription: string; scheduledAt: string | null }) =>
  apiFetch<FieldIntervention>("/field-ops/interventions", post(body));
export const myInterventions = () => apiFetch<FieldIntervention[]>("/field-ops/interventions/mine");
export const startIntervention = (id: string) => apiFetch<FieldIntervention>(`/field-ops/interventions/${id}/start`, post());
export const completeIntervention = (id: string, body: { operationUuid: string; checklist: Array<{ label: string; done: boolean }>;
  partsUsed: Array<{ productId: string; quantity: number }>; timeSpentMinutes: number; reportText: string; signatureAttachmentId: string | null }) =>
  apiFetch<FieldIntervention>(`/field-ops/interventions/${id}/complete`, post(body));

// --- Pièces jointes (preuves) ---
export const uploadAttachment = (body: { kind: "photo" | "signature"; referenceType: string; referenceId: string; contentBase64: string; contentType: string }) =>
  apiFetch<Attachment>("/attachments", post(body));
export const uploadProof = async (img: PendingImage, kind: "photo" | "signature", referenceType: string, referenceId: string) =>
  (await uploadAttachment({ kind, referenceType, referenceId, contentBase64: img.base64, contentType: img.contentType })).id;

// --- Appareil & configuration ---
/** Appareil de synchronisation de ce navigateur (même registre et même identifiant que la caisse). */
export async function fieldDeviceId(): Promise<string> {
  return storedDeviceId() || (await ensureDevice("Terrain (navigateur)", "/field-ops/device")).id;
}
export const getMobileConfig = () => apiFetch<MobileConfig>("/mobile/config");
export const listCrashReports = (limit = 100) => apiFetch<CrashReport[]>(`/mobile/crash-reports?limit=${limit}`);
