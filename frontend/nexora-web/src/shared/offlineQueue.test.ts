import { webcrypto } from "node:crypto";
import { beforeEach, describe, expect, it, vi } from "vitest";
import {
  __resetOutbox, enqueue, flushOutbox, listOutbox, loadOutbox, outboxProtection, outboxWritesSettled, pendingCount,
  pruneSynced, subscribeOutbox, type BatchSender,
} from "@/shared/offlineQueue";
import { configureSecureStore, memoryBackend } from "@/shared/secureStore";

const sale = (id: string) => ({ id, kind: "sale.checkout", payload: { operationUuid: id, total: 100, customer: "Client Confidentiel" } });
const LEGACY = "nexora.pos.outbox";

/** Simule un rechargement de la page : la mémoire est vidée, le stockage chiffré reste. */
const reload = () => __resetOutbox();

describe("offlineQueue (Phase 19 §26, §41-42)", () => {
  let vault: ReturnType<typeof memoryBackend>;

  beforeEach(() => {
    vault = memoryBackend();
    configureSecureStore({ backend: vault, subtle: webcrypto.subtle as unknown as SubtleCrypto });
    __resetOutbox();
  });

  it("met une opération en file et la persiste chiffrée — jamais en clair (BUG-012)", async () => {
    enqueue(sale("op-1"));
    expect(listOutbox()).toHaveLength(1);
    expect(pendingCount()).toBe(1);
    expect(outboxProtection()).toBe("encrypted");

    await outboxWritesSettled();
    expect(localStorage.getItem(LEGACY)).toBeNull();
    const sealed = vault.raw.get("pos.outbox") as { iv: Uint8Array; data: ArrayBuffer };
    const bytes = new TextDecoder().decode(sealed.data);
    expect(bytes).not.toContain("op-1");
    expect(bytes).not.toContain("Client Confidentiel");

    reload();
    await loadOutbox();
    expect(listOutbox().map((e) => e.id)).toEqual(["op-1"]);
    expect(listOutbox()[0].status).toBe("pending");
  });

  it("reprend l'ancienne file en clair, la chiffre puis efface la copie en clair", async () => {
    localStorage.setItem(LEGACY, JSON.stringify({ version: 1, entries: [{
      id: "old-1", kind: "sale.checkout", payload: {}, status: "pending", attempts: 0, lastError: null,
      createdAt: "2026-01-01T00:00:00Z", updatedAt: "2026-01-01T00:00:00Z",
    }] }));
    await loadOutbox();
    expect(listOutbox().map((e) => e.id)).toEqual(["old-1"]);
    await outboxWritesSettled();
    expect(localStorage.getItem(LEGACY)).toBeNull();

    reload();
    await loadOutbox();
    expect(listOutbox().map((e) => e.id)).toEqual(["old-1"]);
  });

  it("une vente mise en file pendant le chargement n'écrase pas la file déjà chiffrée", async () => {
    enqueue(sale("op-1"));
    await outboxWritesSettled();
    reload();

    enqueue(sale("op-2"));             // avant la fin du chargement
    await outboxWritesSettled();
    reload();
    await loadOutbox();
    expect(listOutbox().map((e) => e.id).sort()).toEqual(["op-1", "op-2"]);
  });

  it("contexte non sécurisé : aucune vente perdue, le repli en clair est signalé", () => {
    configureSecureStore({ disabled: true });
    __resetOutbox();
    enqueue(sale("op-1"));
    expect(outboxProtection()).toBe("plaintext");
    expect(JSON.parse(localStorage.getItem(LEGACY)!).entries[0].id).toBe("op-1");
  });

  it("est idempotent : un même id n'est jamais dupliqué (aucune double vente)", () => {
    enqueue(sale("op-1"));
    enqueue(sale("op-1"));
    expect(listOutbox()).toHaveLength(1);
  });

  it("notifie les abonnés à chaque mutation", () => {
    const listener = vi.fn();
    const unsub = subscribeOutbox(listener);
    enqueue(sale("op-1"));
    expect(listener).toHaveBeenCalled();
    unsub();
  });

  it("flush : marque les opérations confirmées par le serveur", async () => {
    enqueue(sale("op-1"));
    enqueue(sale("op-2"));
    const sender: BatchSender = async (batch) =>
      batch.map((e) => ({ id: e.id, status: "synced" as const }));

    const r = await flushOutbox(sender);
    expect(r).toEqual({ synced: 2, conflict: 0, failed: 0 });
    expect(listOutbox().every((e) => e.status === "synced")).toBe(true);
    expect(pendingCount()).toBe(0);

    pruneSynced();
    expect(listOutbox()).toHaveLength(0);
  });

  it("flush : un conflit serveur n'écrase rien et reste visible", async () => {
    enqueue(sale("op-1"));
    const sender: BatchSender = async () => [{ id: "op-1", status: "conflict", message: "Stock insuffisant" }];

    const r = await flushOutbox(sender);
    expect(r.conflict).toBe(1);
    const entry = listOutbox()[0];
    expect(entry.status).toBe("conflict");
    expect(entry.lastError).toContain("Stock insuffisant");
  });

  it("flush : si le lot ne part pas (toujours hors ligne), l'opération repasse en attente", async () => {
    enqueue(sale("op-1"));
    const sender: BatchSender = async () => { throw new Error("network"); };

    const r = await flushOutbox(sender);
    expect(r).toEqual({ synced: 0, conflict: 0, failed: 0 });
    const entry = listOutbox()[0];
    expect(entry.status).toBe("pending");
    expect(entry.attempts).toBe(1);
    expect(entry.lastError).toBe("network");
  });

  it("flush : rejoue les échecs mais pas les opérations déjà confirmées", async () => {
    enqueue(sale("op-1"));
    enqueue(sale("op-2"));
    await flushOutbox(async (b) => b.map((e) => ({
      id: e.id, status: e.id === "op-1" ? ("synced" as const) : ("failed" as const), message: "x",
    })));

    const seen: string[] = [];
    await flushOutbox(async (b) => { b.forEach((e) => seen.push(e.id)); return b.map((e) => ({ id: e.id, status: "synced" as const })); });
    expect(seen).toEqual(["op-2"]);   // op-1 (synced) n'est pas rejouée
  });
});
