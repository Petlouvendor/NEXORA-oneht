import { webcrypto } from "node:crypto";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { configureApiClient } from "@/core/apiClient";
import { configureSecureStore, memoryBackend } from "@/shared/secureStore";
import { __resetOutbox, enqueue, flushOutbox, listOutbox } from "@/shared/offlineQueue";
import { isOfflineFailure, kindsFor, toSyncOperations, type FieldDeliveryPayload } from "@/shared/syncOperations";
import { advanceStop, completeStop, failStop } from "@/mobile/mobileApi";

const json = (status: number, body: unknown) => new Response(JSON.stringify(body), { status, headers: { "Content-Type": "application/json" } });
const delivery = (id: string, extra: Partial<FieldDeliveryPayload> = {}) => ({
  id, kind: "field.delivery",
  payload: { stopId: "s1", latitude: null, longitude: null, signatureAttachmentId: null, photoAttachmentId: null, amountCollected: null,
    paymentFinancialAccountId: null, pendingSignature: { base64: "c2ln", contentType: "image/png" }, ...extra } as FieldDeliveryPayload,
});

describe("terrain — API et file hors ligne (finalisation phase 12)", () => {
  beforeEach(() => {
    configureApiClient({ token: () => "t", company: () => "c", onUnauthorized: () => {} });
    configureSecureStore({ backend: memoryBackend(), subtle: webcrypto.subtle as unknown as SubtleCrypto });
    __resetOutbox();
  });
  afterEach(() => vi.unstubAllGlobals());

  it("appelle les étapes de l'arrêt : en route, livré (preuve), échec motivé", async () => {
    const calls: { url: string; body?: string }[] = [];
    vi.stubGlobal("fetch", vi.fn(async (url: string, init?: RequestInit) => { calls.push({ url, body: init?.body as string }); return json(200, {}); }));
    await advanceStop("s1", "en_route");
    await completeStop("s1", { operationUuid: "op", latitude: null, longitude: null, signatureAttachmentId: "a1", photoAttachmentId: null, amountCollected: null, paymentFinancialAccountId: null });
    await failStop("s1", "Client absent");
    expect(calls[0].url).toContain("/field-ops/stops/s1/advance");
    expect(JSON.parse(calls[0].body!)).toBe("en_route");
    expect(JSON.parse(calls[1].body!)).toMatchObject({ operationUuid: "op", signatureAttachmentId: "a1" });
    expect(JSON.parse(calls[2].body!)).toEqual({ reason: "Client absent" });
  });

  it("traduit chaque opération avec son vrai type — une livraison ne part jamais comme une vente", async () => {
    const uploads: string[] = [];
    const ops = await toSyncOperations([
      { ...delivery("d1"), status: "pending", attempts: 0, lastError: null, createdAt: "", updatedAt: "" },
      { id: "v1", kind: "sale.checkout", payload: { total: 1 }, status: "pending", attempts: 0, lastError: null, createdAt: "", updatedAt: "" },
    ], async (_img, kind) => { uploads.push(kind); return "att-1"; });
    expect(ops.map((o) => [o.entityType, o.operationType])).toEqual([["delivery_stop", "update"], ["sale", "create"]]);
    expect(ops[0].payload).toMatchObject({ stopId: "s1", signatureAttachmentId: "att-1" });
    expect(ops[0].payload).not.toHaveProperty("pendingSignature");
    expect(uploads).toEqual(["signature"]);
    await expect(toSyncOperations([{ id: "x", kind: "inconnu", payload: {}, status: "pending", attempts: 0, lastError: null, createdAt: "", updatedAt: "" }]))
      .rejects.toThrow("type inconnu");
  });

  it("la caisse ne vide que ses opérations ; la preuve envoyée n'est pas renvoyée au nouvel essai", async () => {
    enqueue(delivery("d1"));
    enqueue({ id: "v1", kind: "sale.checkout", payload: { total: 1 } });
    const posSeen: string[] = [];
    await flushOutbox(async (b) => { b.forEach((e) => posSeen.push(e.kind)); return b.map((e) => ({ id: e.id, status: "synced" as const })); }, kindsFor("pos"));
    expect(posSeen).toEqual(["sale.checkout"]);

    let uploads = 0;
    const failing = async (b: Parameters<typeof toSyncOperations>[0]) => {
      await toSyncOperations(b, async () => { uploads++; return "att-9"; });
      throw new TypeError("Failed to fetch");
    };
    await flushOutbox(failing, kindsFor("field"));
    await flushOutbox(failing, kindsFor("field"));
    expect(uploads).toBe(1);
    const left = listOutbox().find((e) => e.id === "d1")!;
    expect(left.status).toBe("pending");
    expect((left.payload as FieldDeliveryPayload).signatureAttachmentId).toBe("att-9");
  });

  it("ne met en file que les vraies pertes de réseau, jamais un refus du serveur", () => {
    expect(isOfflineFailure(new TypeError("Failed to fetch"), true)).toBe(true);
    expect(isOfflineFailure(new Error("Preuve requise"), true)).toBe(false);
    expect(isOfflineFailure(new Error("x"), false)).toBe(true);
  });
});
