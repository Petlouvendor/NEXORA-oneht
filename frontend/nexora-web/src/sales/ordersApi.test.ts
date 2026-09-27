import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { configureApiClient } from "@/core/apiClient";
import { completeOrder, createOrder } from "@/sales/ordersApi";

function jsonResponse(status: number, body: unknown): Response {
  return new Response(JSON.stringify(body), { status, headers: { "Content-Type": "application/json" } });
}

describe("ordersApi (Phase 25 §14, §18)", () => {
  beforeEach(() => configureApiClient({ token: () => "t", company: () => "c", onUnauthorized: () => {} }));
  afterEach(() => vi.unstubAllGlobals());

  it("completeOrder sends an operation UUID and an idempotency key", async () => {
    let seen: RequestInit = {};
    const fetchMock = vi.fn(async (_url: string, init: RequestInit) => {
      seen = init;
      return jsonResponse(200, { saleId: "s1", total: 100 });
    });
    vi.stubGlobal("fetch", fetchMock);

    await completeOrder("o1", [{ financialAccountId: "acc", amount: 100 }], false);

    expect(new Headers(seen.headers).get("Idempotency-Key")).toBeTruthy();
    const body = JSON.parse(seen.body as string);
    expect(body.operationUuid).toBeTruthy();
    expect(body.tenders[0].amount).toBe(100);
  });

  it("createOrder posts to /orders", async () => {
    const fetchMock = vi.fn(async (url: string) => {
      expect(url).toContain("/orders");
      return jsonResponse(200, "new-id");
    });
    vi.stubGlobal("fetch", fetchMock);

    const id = await createOrder({ currency: "HTG", orderType: "pickup", onCredit: false, lines: [] });
    expect(id).toBe("new-id");
  });
});
