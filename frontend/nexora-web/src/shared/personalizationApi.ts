import { apiFetch } from "@/core/apiClient";

// Personnalisation UX persistée (Phase 40 §13, §21-22, §37, §50, §57).

export interface DashboardLayout {
  id: string;
  companyId: string;
  userId: string | null;
  role: string | null;
  name: string;
  widgets: Array<{ key: string; x: number; y: number; w: number; h: number; config?: Record<string, unknown> }>;
  isDefault: boolean;
  updatedAt: string;
}

export interface SavedFilter {
  id: string;
  companyId: string;
  userId: string;
  viewKey: string;
  name: string;
  filter: Record<string, unknown>;
  isDefault: boolean;
  shared: boolean;
  mine: boolean;
  updatedAt: string;
}

export interface Shortcut {
  id: string;
  chord: string;
  action: string;
  label: string;
  sortOrder: number;
}

export interface DesignSystemVersion {
  version: string;
  wcagTarget: string;
  componentCount: number;
  themes: string[];
  densities: string[];
  tokenGroups: Array<{ group: string; count: number; sample: string[] }>;
  components: string[];
  releasedOn: string;
}

// --- Dispositions de tableau de bord ---
export const getDashboardLayouts = () =>
  apiFetch<{ effective: DashboardLayout | null; all: DashboardLayout[] }>("/dashboard-layouts");
export const saveDashboardLayout = (body: {
  id?: string | null; name: string; widgets: DashboardLayout["widgets"]; isDefault?: boolean;
}) => apiFetch<DashboardLayout>("/dashboard-layouts", { method: "PUT", body: JSON.stringify(body) });
export const saveCompanyDashboardLayout = (body: {
  id?: string | null; name: string; widgets: DashboardLayout["widgets"]; role?: string | null;
}) => apiFetch<DashboardLayout>("/dashboard-layouts/company-default", {
  method: "POST", body: JSON.stringify({ ...body, asCompanyDefault: true }),
});
export const deleteDashboardLayout = (id: string) =>
  apiFetch<void>(`/dashboard-layouts/${id}`, { method: "DELETE" });

// --- Filtres sauvegardés ---
export const listSavedFilters = (viewKey: string) =>
  apiFetch<SavedFilter[]>(`/saved-filters?viewKey=${encodeURIComponent(viewKey)}`);
export const saveSavedFilter = (body: {
  id?: string | null; viewKey: string; name: string; filter: Record<string, unknown>; isDefault?: boolean; shared?: boolean;
}) => apiFetch<SavedFilter>("/saved-filters", { method: "POST", body: JSON.stringify(body) });
export const deleteSavedFilter = (id: string) =>
  apiFetch<void>(`/saved-filters/${id}`, { method: "DELETE" });

// --- Raccourcis ---
export const listShortcuts = () => apiFetch<Shortcut[]>("/shortcuts");
export const saveShortcut = (body: { id?: string | null; chord: string; action: string; label: string; sortOrder?: number }) =>
  apiFetch<Shortcut>("/shortcuts", { method: "POST", body: JSON.stringify(body) });
export const deleteShortcut = (id: string) => apiFetch<void>(`/shortcuts/${id}`, { method: "DELETE" });

// --- Design system ---
export const getDesignSystemVersion = () => apiFetch<DesignSystemVersion>("/design-system/version");
