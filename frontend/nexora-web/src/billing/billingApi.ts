import { apiFetch } from "@/core/apiClient";
import type { Invoice, Plan, QuotaReport, Subscription } from "@/platform/platformApi";

// Portail client — mon abonnement, mes factures, mon usage (Phase 38 §45-46).

export type { Invoice, Plan, QuotaReport, Subscription };

export interface License {
  id: string;
  customerId: string;
  subscriptionId: string | null;
  scope: string;
  subjectRef: string | null;
  status: string;
  graceDays: number;
  activatedAt: string;
  expiresAt: string;
  lastIssuedAt: string | null;
  revokedAt: string | null;
  revokeReason: string | null;
}

export interface LicenseToken {
  token: string;
  issuedAt: string;
  expiresAt: string;
  hardExpiresAt: string;
}

export const mySubscription = () =>
  apiFetch<Subscription | undefined>("/subscription").catch(() => undefined);
export const publicPlans = () => apiFetch<Plan[]>("/platform/plans").catch(() => [] as Plan[]);
export const myInvoices = () => apiFetch<Invoice[]>("/billing/invoices");
export const myUsage = () => apiFetch<QuotaReport>("/usage");
export const myLicenses = () => apiFetch<License[]>("/licenses");
export const issueLicenseToken = (id: string) =>
  apiFetch<LicenseToken>(`/licenses/${id}/token`, { method: "POST" });

export const previewPlan = (subId: string, planId: string) =>
  apiFetch<{
    allowed: boolean; direction: string; prorationAmount: number; currencyCode: string;
    quotaBreaches: { resource: string; limit: number; currentUsage: number }[];
    featuresLost: string[]; summary: string;
  }>(`/subscriptions/${subId}/preview-plan/${planId}`);
export const selfUpgrade = (subId: string, planId: string) =>
  apiFetch<Subscription>(`/subscriptions/${subId}/upgrade`, { method: "POST", body: JSON.stringify({ planId }) });
export const selfDowngrade = (subId: string, planId: string) =>
  apiFetch<Subscription>(`/subscriptions/${subId}/downgrade`, { method: "POST", body: JSON.stringify({ planId }) });
export const selfCancel = (subId: string, immediate: boolean, reason?: string) =>
  apiFetch<Subscription>(`/subscriptions/${subId}/cancel`, { method: "POST", body: JSON.stringify({ immediate, reason }) });
export const selfReactivate = (subId: string) =>
  apiFetch<Subscription>(`/subscriptions/${subId}/reactivate`, { method: "POST" });
