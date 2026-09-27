import { useSyncExternalStore } from "react";
import { listOutbox, subscribeOutbox, type OutboxEntry } from "@/shared/offlineQueue";

/** Vue réactive de la file d'attente hors ligne du POS (Phase 19 §26, §42). */
export function useOutbox(): { entries: OutboxEntry[]; pending: number; hasConflict: boolean } {
  const entries = useSyncExternalStore(subscribeOutbox, listOutbox, listOutbox);
  return {
    entries,
    pending: entries.filter((e) => e.status === "pending" || e.status === "syncing" || e.status === "failed").length,
    hasConflict: entries.some((e) => e.status === "conflict"),
  };
}
