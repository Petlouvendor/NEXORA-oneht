import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { configureApiClient } from "@/core/apiClient";
import { openDispute, writeOffReceivable } from "@/credit/creditApi";

function jsonResponse(status: number, body: unknown): Response {
  return new Response(JSON.stringify(body), { status, headers: { "Content-Type": "application/json" } });
}

describe("creditApi (Phase 27 §25, §37)", () => {
  beforeEach(() => configureApiClient({ token: () => "t", company: () => "c", onUnauthorized: () => {} }));
  afterEach(() => vi.unstubAllGlobals());

  it("writeOffReceivable sends an operation UUID and an idempotency key", async () => {
    let seen: RequestInit = {};
    vi.stubGlobal("fetch", vi.fn(async (url: string, init: RequestInit) => {
      expect(url).toContain("/credit/receivables/r1/write-off");
      seen = init;
      return jsonResponse(200, { writeOffId: "w1", journalEntryId: "je1" });
    }));

    await writeOffReceivable("r1", 3000, "insolvable");

    expect(new Headers(seen.headers).get("Idempotency-Key")).toBeTruthy();
    const body = JSON.parse(seen.body as string);
    expect(body.operationUuid).toBeTruthy();
    expect(body.amount).toBe(3000);
  });

  it("openDispute posts the reason and amount", async () => {
    let seen: RequestInit = {};
    vi.stubGlobal("fetch", vi.fn(async (_url: string, init: RequestInit) => {
      seen = init;
      return jsonResponse(200, "d1");
    }));

    await openDispute("cust", "rec", 1200, "Produit non conforme");
    const body = JSON.parse(seen.body as string);
    expect(body.reason).toBe("Produit non conforme");
    expect(body.disputedAmount).toBe(1200);
    expect(body.receivableId).toBe("rec");
  });
});
