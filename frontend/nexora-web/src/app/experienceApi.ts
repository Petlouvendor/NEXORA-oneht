import { apiFetch } from "@/core/apiClient";

// NEXORA ONE — recherche globale Ctrl+K (§5) & fil d'activité du dashboard (§6).

export type SearchKind = "product" | "customer" | "supplier" | "employee" | "document" | "sale";

export interface SearchHit { kind: SearchKind; id: string; title: string; subtitle: string | null; route: string }
export interface SearchGroup { kind: SearchKind; label: string; hits: SearchHit[] }
export interface GlobalSearchResponse { query: string; groups: SearchGroup[] }

export interface ActivityEvent {
  kind: "sale" | "payment" | "goods_receipt" | "cash_session" | "journal_entry" | "recommendation";
  title: string; detail: string; amount: number | null; currency: string | null;
  occurredAt: string; route: string | null; tone: "success" | "info" | "warning" | "error";
}

/** Chaque groupe renvoyé est déjà filtré par permission côté serveur ; < 2 caractères → aucun groupe. */
export const globalSearch = (q: string, limit = 5) =>
  apiFetch<GlobalSearchResponse>(`/search?q=${encodeURIComponent(q)}&limit=${limit}`);

export const getActivity = (limit = 12) => apiFetch<ActivityEvent[]>(`/dashboard/activity?limit=${limit}`);
