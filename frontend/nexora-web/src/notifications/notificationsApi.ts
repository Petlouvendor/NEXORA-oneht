import { apiFetch } from "@/core/apiClient";

// Centre de notifications & préférences (Phase 34 §4-5, §45).

export interface Notification {
  id: string;
  category: string;
  source: string;
  priority: number;
  severity: string;
  title: string;
  message: string;
  actionUrl: string | null;
  referenceType: string | null;
  referenceId: string | null;
  companyId: string | null;
  createdAt: string;
  readAt: string | null;
  pinnedAt: string | null;
  archivedAt: string | null;
  expiresAt: string | null;
}

export interface NotificationPage {
  items: Notification[];
  total: number;
  unread: number;
}

export interface NotificationPreference {
  id: string;
  companyId: string | null;
  category: string | null;
  inApp: boolean;
  email: boolean;
  sms: boolean;
  webhook: boolean;
  minPriority: number;
  digest: string;
  quietFrom: string | null;
  quietTo: string | null;
}

export type PreferenceInput = Omit<NotificationPreference, "id">;

const qs = (params: Record<string, string | undefined>) => {
  const p = new URLSearchParams();
  for (const [k, v] of Object.entries(params)) if (v) p.set(k, v);
  const s = p.toString();
  return s ? `?${s}` : "";
};

export const listNotifications = (state?: string, category?: string) =>
  apiFetch<NotificationPage>(`/notifications${qs({ state, category })}`);

export const unreadCount = () => apiFetch<{ unread: number }>("/notifications/unread-count");

export const markRead = (id: string, read = true) =>
  apiFetch<void>(`/notifications/${id}/${read ? "read" : "unread"}`, { method: "PATCH" });

export const markAllRead = () => apiFetch<{ updated: number }>("/notifications/read-all", { method: "POST" });

export const setPinned = (id: string, pinned: boolean) =>
  apiFetch<void>(`/notifications/${id}/${pinned ? "pin" : "unpin"}`, { method: "POST" });

export const setArchived = (id: string, archived: boolean) =>
  apiFetch<void>(`/notifications/${id}/${archived ? "archive" : "unarchive"}`, { method: "POST" });

export const listPreferences = () => apiFetch<NotificationPreference[]>("/notification-preferences");

export const upsertPreference = (input: PreferenceInput) =>
  apiFetch<NotificationPreference>("/notification-preferences", {
    method: "POST",
    body: JSON.stringify(input),
  });

export const deletePreference = (id: string) =>
  apiFetch<void>(`/notification-preferences/${id}`, { method: "DELETE" });

// ------------------------------------------------------------------ Envois (finalisation phase 16)

export type TestStatus = "CONNECTED" | "AUTHENTICATION_FAILED" | "TIMEOUT" | "UNAVAILABLE" | "NOT_CONFIGURED";
export interface ChannelProvider {
  channel: string; provider: string; configured: boolean; configurationSummary: string;
  lastTestStatus: TestStatus | null; lastTestMessage: string | null; lastTestedAt: string | null; notConfiguredWaiting: number;
}
export interface ChannelTestResult { channel: string; status: TestStatus; message: string; latencyMs: number }
export interface OutboxEntry {
  id: string; channel: string; category: string; subject: string; addressMasked: string | null; recipientName: string | null;
  status: string; attempts: number; maxAttempts: number; nextAttemptAt: string; lastError: string | null; provider: string | null;
  lastAttemptAt: string | null; createdAt: string; sentAt: string | null;
}
export interface DeliveryLog { attempt: number; status: string; provider: string | null; error: string | null; durationMs: number | null; createdAt: string }
export interface DeliverySummary {
  schedulerEnabled: boolean; pending: number; sending: number; sent: number; failed: number; dead: number; notConfigured: number; oldestPendingAt: string | null;
}
export interface MessageTemplate {
  id: string; companyId: string | null; key: string; channel: string; locale: string; subject: string; body: string;
  description: string | null; variables: string[]; version: number; status: string; isSystem: boolean;
}

const post = (body?: unknown) => ({ method: "POST", body: body === undefined ? undefined : JSON.stringify(body) });

export const listChannelStatus = () => apiFetch<Array<{ channel: string; configured: boolean }>>("/notification-preferences/channels");
export const listProviders = () => apiFetch<ChannelProvider[]>("/notification-delivery/providers");
export const testProvider = (channel: string) => apiFetch<ChannelTestResult>(`/notification-delivery/providers/${channel}/test`, post());
export const requeueNotConfigured = (channel: string) => apiFetch<number>(`/notification-delivery/providers/${channel}/requeue`, post());
export const sendTestMessage = (channel: string, address: string) => apiFetch<OutboxEntry>("/notification-delivery/test-message", post({ channel, address }));
export const deliverySummary = () => apiFetch<DeliverySummary>("/notification-delivery/summary");
export const listOutbox = (status?: string, channel?: string) => {
  const qs = new URLSearchParams();
  if (status) qs.set("status", status);
  if (channel) qs.set("channel", channel);
  return apiFetch<OutboxEntry[]>(`/notification-delivery/outbox${qs.size ? `?${qs}` : ""}`);
};
export const outboxLog = (id: string) => apiFetch<DeliveryLog[]>(`/notification-delivery/outbox/${id}/log`);
export const retryOutbox = (id: string) => apiFetch<void>(`/notification-delivery/outbox/${id}/retry`, post());
export const dispatchNow = () => apiFetch<{ dispatched: number }>("/notification-delivery/dispatch", post());

export const listTemplates = () => apiFetch<MessageTemplate[]>("/message-templates");
export const upsertTemplate = (body: { key: string; channel: string; locale: string; subject: string; body: string; description: string | null; variables: string[] | null; status: string }) =>
  apiFetch<string>("/message-templates", post(body));
export const renderTemplate = (body: { key: string; channel: string; locale: string; variables: Record<string, string | null> }) =>
  apiFetch<{ subject: string; body: string; missingVariables: string[] }>("/message-templates/render", post(body));

type Tone = "success" | "warning" | "error" | "info" | undefined;
export const CHANNEL_LABEL: Record<string, string> = { in_app: "Interne (dans l'application)", email: "E-mail", sms: "SMS", push: "Notification push", whatsapp: "WhatsApp", webhook: "Webhook" };
export const TEST_LABEL: Record<TestStatus, { label: string; tone: Tone }> = {
  CONNECTED: { label: "Connecté", tone: "success" },
  AUTHENTICATION_FAILED: { label: "Authentification refusée", tone: "error" },
  TIMEOUT: { label: "Délai dépassé", tone: "error" },
  UNAVAILABLE: { label: "Service indisponible", tone: "error" },
  NOT_CONFIGURED: { label: "Fournisseur non configuré", tone: "warning" },
};
export const OUTBOX_STATUS: Record<string, { label: string; tone: Tone }> = {
  pending: { label: "En file", tone: "info" }, sending: { label: "En cours", tone: "info" }, sent: { label: "Envoyé", tone: "success" },
  failed: { label: "Échec, nouvel essai prévu", tone: "warning" }, dead: { label: "Abandonné", tone: "error" },
  not_configured: { label: "Fournisseur non configuré", tone: "warning" },
};
/** Un envoi se relance s'il a échoué, a été abandonné, ou attend un fournisseur désormais configuré. */
export const canRetry = (e: Pick<OutboxEntry, "status" | "channel">, configured: Record<string, boolean>) =>
  e.status === "failed" || e.status === "dead" || (e.status === "not_configured" && configured[e.channel] === true);
/** Placeholder d'adresse selon le canal. */
export const addressHint = (channel: string) =>
  channel === "email" ? "adresse@exemple.com" : channel === "sms" || channel === "whatsapp" ? "+50937001234" : "jeton de l'appareil";
