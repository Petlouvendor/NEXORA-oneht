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
