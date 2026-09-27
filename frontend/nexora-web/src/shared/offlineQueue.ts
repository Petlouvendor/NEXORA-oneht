/**
 * File d'attente hors ligne du POS (Phase 19 §26, §41-42 ; Phase 10 côté serveur).
 * Outbox persistant : une opération qui n'a pas pu être confirmée par le serveur est mise en
 * file, avec un identifiant unique (idempotence — aucune double vente), puis rejouée à la
 * reconnexion. Le mode offline ne déclare JAMAIS une opération confirmée tant que le serveur
 * ne l'a pas confirmée (§3).
 *
 * Stockage (résumé fonctionnel §15, BUG-012) : chiffré au repos dans le coffre du navigateur
 * (`secureStore` : AES-256-GCM, clé non exportable). La file vit en mémoire (lecture synchrone pour
 * l'écran) et chaque mutation est réécrite chiffrée, dans l'ordre. L'ancienne copie en clair
 * (`localStorage`) est reprise puis effacée. Sans contexte sécurisé (HTTP hors localhost), la file
 * reste persistée en clair pour ne perdre aucune vente, et `outboxProtection()` le signale.
 */

import { secureGet, secureSet, secureStoreAvailable } from "@/shared/secureStore";

export type OutboxStatus = "pending" | "syncing" | "synced" | "failed" | "conflict";

export interface OutboxEntry<P = unknown> {
  id: string;                 // = operation UUID (clé d'idempotence)
  kind: string;               // ex. "sale.checkout"
  payload: P;
  status: OutboxStatus;
  attempts: number;
  lastError: string | null;
  createdAt: string;
  updatedAt: string;
}

/** Résultat renvoyé par le serveur pour une opération d'un lot de synchronisation. */
export interface SyncOperationOutcome {
  id: string;
  status: "synced" | "conflict" | "failed";
  message?: string | null;
}

export type BatchSender = (entries: OutboxEntry[]) => Promise<SyncOperationOutcome[]>;

/** Ancienne clé en clair (avant BUG-012) — reprise puis effacée. Repli si aucun chiffrement possible. */
const LEGACY_KEY = "nexora.pos.outbox";
const SECURE_NAME = "pos.outbox";
const SCHEMA_VERSION = 1;
const MAX_ATTEMPTS = 8;

interface Persisted {
  version: number;
  entries: OutboxEntry[];
}

type Listener = () => void;
const listeners = new Set<Listener>();
let cache: OutboxEntry[] = [];            // snapshot stable pour useSyncExternalStore
let loading: Promise<void> | null = null;
let writes: Promise<void> = Promise.resolve();

/** « encrypted » : file chiffrée au repos ; « plaintext » : repli en clair (contexte non sécurisé). */
export function outboxProtection(): "encrypted" | "plaintext" {
  return secureStoreAvailable() ? "encrypted" : "plaintext";
}

function readLegacy(): OutboxEntry[] {
  try {
    const raw = localStorage.getItem(LEGACY_KEY);
    const parsed = raw ? (JSON.parse(raw) as Persisted) : null;
    return parsed && parsed.version === SCHEMA_VERSION && Array.isArray(parsed.entries) ? parsed.entries : [];
  } catch {
    return [];
  }
}

/** Fusionne sans doublon (l'id est la clé d'idempotence) ; la version en mémoire prime. */
function merge(stored: OutboxEntry[], live: OutboxEntry[]): OutboxEntry[] {
  const ids = new Set(live.map((e) => e.id));
  return [...stored.filter((e) => !ids.has(e.id)), ...live];
}

/** Charge la file persistée (une seule fois). Attendu avant toute synchronisation. */
export function loadOutbox(): Promise<void> {
  return loading ??= (async () => {
    let stored: OutboxEntry[] = [];
    const legacy = readLegacy();
    if (secureStoreAvailable()) {
      try {
        stored = (await secureGet<Persisted>(SECURE_NAME))?.entries ?? [];
      } catch {
        stored = [];
      }
    }
    const loaded = merge(merge(stored, legacy), cache);
    if (loaded.length !== cache.length || legacy.length > 0) {
      cache = loaded;
      listeners.forEach((l) => l());
      persist();   // reprise de l'ancienne copie en clair → chiffrée, puis effacée
    }
  })();
}

function persist(): void {
  if (!secureStoreAvailable()) {
    try {
      localStorage.setItem(LEGACY_KEY, JSON.stringify({ version: SCHEMA_VERSION, entries: cache } satisfies Persisted));
    } catch {
      /* quota / mode privé — la file reste en mémoire pour la session */
    }
    return;
  }
  // Écritures en série, APRÈS le chargement : une vente mise en file pendant le chargement ne doit
  // jamais écraser la file déjà chiffrée. On écrit l'état courant (fusionné) au moment d'écrire.
  writes = writes
    .then(() => loadOutbox())
    .then(() => secureSet(SECURE_NAME, { version: SCHEMA_VERSION, entries: cache } satisfies Persisted))
    .then(() => { try { localStorage.removeItem(LEGACY_KEY); } catch { /* ignore */ } })
    .catch(() => { /* écriture chiffrée impossible : la file reste en mémoire, rejouée à la prochaine mutation */ });
}

/** Attend la fin des écritures en cours (tests, fermeture propre). */
export function outboxWritesSettled(): Promise<void> {
  return writes;
}

function read(): OutboxEntry[] {
  void loadOutbox();
  return cache;
}

function write(entries: OutboxEntry[]): void {
  cache = entries;
  persist();
  listeners.forEach((l) => l());
}

export function subscribeOutbox(listener: Listener): () => void {
  listeners.add(listener);
  return () => listeners.delete(listener);
}

export function listOutbox(): OutboxEntry[] {
  return read();
}

/** Réservé aux tests : vide la file, le cache et l'état de chargement. */
export function __resetOutbox(): void {
  cache = [];
  loading = null;
  writes = Promise.resolve();
  try { localStorage.removeItem(LEGACY_KEY); } catch { /* ignore */ }
  listeners.forEach((l) => l());
}

export function pendingCount(): number {
  return read().filter((e) => e.status === "pending" || e.status === "failed" || e.status === "syncing").length;
}

/** Ajoute une opération. Idempotent : un même `id` n'est jamais dupliqué. */
export function enqueue<P>(entry: { id: string; kind: string; payload: P }): OutboxEntry<P> {
  const entries = read();
  const existing = entries.find((e) => e.id === entry.id);
  if (existing) return existing as OutboxEntry<P>;

  const now = new Date().toISOString();
  const created: OutboxEntry<P> = {
    id: entry.id, kind: entry.kind, payload: entry.payload,
    status: "pending", attempts: 0, lastError: null, createdAt: now, updatedAt: now,
  };
  write([...entries, created as OutboxEntry]);
  return created;
}

export function removeEntry(id: string): void {
  write(read().filter((e) => e.id !== id));
}

/** Purge les opérations confirmées (à appeler après affichage à l'utilisateur). */
export function pruneSynced(): void {
  write(read().filter((e) => e.status !== "synced"));
}

let flushing = false;

/**
 * Rejoue les opérations en attente via `sender` (un lot de synchronisation serveur).
 * Met à jour le statut de chaque entrée selon la réponse. Ne supprime rien : la purge des
 * `synced` est explicite (`pruneSynced`) pour laisser l'UI les afficher.
 */
export async function flushOutbox(sender: BatchSender): Promise<{ synced: number; conflict: number; failed: number }> {
  if (flushing) return { synced: 0, conflict: 0, failed: 0 };
  flushing = true;
  try {
    await loadOutbox();   // jamais de lot partiel : la file persistée est chargée d'abord
    const entries = read();
    const toSend = entries.filter((e) => e.status === "pending" || e.status === "failed");
    if (toSend.length === 0) return { synced: 0, conflict: 0, failed: 0 };

    mutate((e) => (toSend.some((s) => s.id === e.id) ? { ...e, status: "syncing", attempts: e.attempts + 1 } : e));

    let outcomes: SyncOperationOutcome[];
    try {
      outcomes = await sender(toSend.map((e) => ({ ...e, status: "syncing" })));
    } catch (err) {
      // Le lot n'a pas pu être envoyé (toujours hors ligne) — on repasse en "pending".
      const message = err instanceof Error ? err.message : "Envoi impossible.";
      mutate((e) => (toSend.some((s) => s.id === e.id)
        ? { ...e, status: e.attempts >= MAX_ATTEMPTS ? "failed" : "pending", lastError: message }
        : e));
      return { synced: 0, conflict: 0, failed: 0 };
    }

    let synced = 0, conflict = 0, failed = 0;
    mutate((e) => {
      const o = outcomes.find((x) => x.id === e.id);
      if (!o) return e;
      if (o.status === "synced") { synced++; return { ...e, status: "synced", lastError: null }; }
      if (o.status === "conflict") { conflict++; return { ...e, status: "conflict", lastError: o.message ?? "Conflit détecté." }; }
      failed++;
      return { ...e, status: e.attempts >= MAX_ATTEMPTS ? "failed" : "pending", lastError: o.message ?? "Échec." };
    });
    return { synced, conflict, failed };
  } finally {
    flushing = false;
  }
}

function mutate(fn: (e: OutboxEntry) => OutboxEntry): void {
  write(read().map(fn));
}

/** Rejoue automatiquement à la reconnexion. Retourne un désabonnement. */
export function autoFlushOnReconnect(sender: BatchSender): () => void {
  const handler = () => { void flushOutbox(sender); };
  window.addEventListener("online", handler);
  return () => window.removeEventListener("online", handler);
}
