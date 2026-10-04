import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { configureApiClient } from "@/core/apiClient";
import { NEXT_KITCHEN, addItems, cancelOrder, closeOrder, isNewWave, minutesSince, parseModifiers } from "@/restaurant/restaurantApi";

function jsonResponse(status: number, body: unknown): Response {
  return new Response(status === 204 ? null : JSON.stringify(body), { status, headers: { "Content-Type": "application/json" } });
}

/** Finalisation phase 13 — restaurant : règlement idempotent, vagues de commande, précisions cuisine. */
describe("restaurantApi", () => {
  beforeEach(() => configureApiClient({ token: () => "t", company: () => "c1", onUnauthorized: () => {} }));
  afterEach(() => vi.unstubAllGlobals());

  it("règlement : la même clé d'opération est envoyée à chaque essai (jamais deux ventes)", async () => {
    const fetchMock = vi.fn(async (_url: string, _init?: RequestInit) => jsonResponse(200, { saleId: "s1", documentNumber: "T-1", total: 300, changeGiven: 0 }));
    vi.stubGlobal("fetch", fetchMock);
    const body = { saleId: "s1", tenders: [{ financialAccountId: "a", paymentMethodId: "m", amount: 300, reference: null }], amountTendered: 300, tipAmount: 0, cashSessionId: "cs" };
    await closeOrder(body, "op-1");
    await closeOrder(body, "op-1");
    const keys = fetchMock.mock.calls.map((c) => new Headers(c[1]?.headers).get("Idempotency-Key"));
    expect(keys).toEqual(["op-1", "op-1"]);
    expect(fetchMock.mock.calls[0][0]).toContain("/restaurant/orders/close");
  });

  it("ajout d'articles et annulation : routes dédiées, motif transmis", async () => {
    const fetchMock = vi.fn(async (_url: string, _init?: RequestInit) => jsonResponse(200, {}));
    vi.stubGlobal("fetch", fetchMock);
    await addItems("s1", [{ productId: "p", quantity: 2, lineDiscount: 0, modifiers: ["Sans oignon"] }]);
    await cancelOrder("s1", "Client parti");
    expect(fetchMock.mock.calls[0][0]).toContain("/restaurant/orders/items");
    expect(JSON.parse(String(fetchMock.mock.calls[0][1]?.body)).items[0].modifiers).toEqual(["Sans oignon"]);
    expect(JSON.parse(String(fetchMock.mock.calls[1][1]?.body))).toEqual({ saleId: "s1", reason: "Client parti" });
  });

  it("précisions : séparées par virgule, nettoyées, bornées à 10", () => {
    expect(parseModifiers(" sans oignon , , bien cuit ")).toEqual(["sans oignon", "bien cuit"]);
    expect(parseModifiers(Array.from({ length: 15 }, (_, i) => `m${i}`).join(","))).toHaveLength(10);
    expect(parseModifiers("")).toEqual([]);
  });

  it("nouvelle vague : seules les lignes ajoutées après l'envoi en cuisine", () => {
    const order = { firedAt: "2026-10-04T12:00:00Z" };
    const line = (addedAt: string | null) => ({ productId: "p", description: "x", quantity: 1, unitPrice: 1, lineTotal: 1, lineId: "l", discount: 0, modifiers: null, addedAt });
    expect(isNewWave(line("2026-10-04T12:05:00Z"), order)).toBe(true);
    expect(isNewWave(line("2026-10-04T11:59:00Z"), order)).toBe(false);
    expect(isNewWave(line(null), order)).toBe(false);
    expect(isNewWave(line("2026-10-04T12:05:00Z"), { firedAt: null })).toBe(false);
  });

  it("âge du ticket et enchaînement cuisine", () => {
    const now = new Date("2026-10-04T12:30:00Z").getTime();
    expect(minutesSince("2026-10-04T12:00:00Z", now)).toBe(30);
    expect(minutesSince(null, now)).toBe(0);
    expect(minutesSince("2026-10-04T13:00:00Z", now)).toBe(0);
    expect(NEXT_KITCHEN.pending?.status).toBe("preparing");
    expect(NEXT_KITCHEN.served).toBeUndefined();
  });
});
