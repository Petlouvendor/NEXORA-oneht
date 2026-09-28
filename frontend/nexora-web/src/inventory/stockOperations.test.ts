import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { configureApiClient } from "@/core/apiClient";
import {
  closeTransfer, getStockOverview, receiveTransfer, recordAdjustment, recordLoss, saveCount, startCount, validateCount,
} from "@/inventory/inventoryApi";
import { newOperationId, num } from "@/shared/forms";

function jsonResponse(status: number, body: unknown): Response {
  return new Response(JSON.stringify(body), { status, headers: { "Content-Type": "application/json" } });
}

describe("inventoryApi — finalisation produit (phase 2)", () => {
  const calls: Array<{ url: string; init: RequestInit }> = [];
  beforeEach(() => {
    calls.length = 0;
    configureApiClient({ token: () => "t", company: () => "c", onUnauthorized: () => {} });
    vi.stubGlobal("fetch", vi.fn(async (url: string, init: RequestInit) => {
      calls.push({ url, init });
      return jsonResponse(200, url.includes("/overview")
        ? { rows: [], total: 0, totalValue: 0, outCount: 0, lowCount: 0, overCount: 0, valuationVisible: false } : "id-1");
    }));
  });
  afterEach(() => vi.unstubAllGlobals());

  const body = (i = 0) => JSON.parse(calls[i].init.body as string);
  const header = (i: number, name: string) => new Headers(calls[i].init.headers).get(name);

  it("stock overview sends only the filters that are set", async () => {
    const o = await getStockOverview({ locationId: "l1", status: "out", q: undefined, limit: 100, offset: 0 });
    expect(calls[0].url).toContain("/stock/overview?locationId=l1&status=out&limit=100&offset=0");
    expect(o.valuationVisible).toBe(false);
  });

  it("a full count is opened without product list; counts are saved with their justification", async () => {
    await startCount("loc", "full", []);
    await saveCount("c1", [{ productId: "p1", lotId: null, countedQuantity: 27, note: "casse" }]);
    expect(body(0)).toEqual({ locationId: "loc", countType: "full", productIds: [] });
    expect(calls[1].url).toContain("/inventory/counts/c1/lines");
    expect(calls[1].init.method).toBe("PUT");
    expect(body(1)[0]).toMatchObject({ countedQuantity: 27, note: "casse" });
  });

  it("validation, closing, losses and adjustments carry the operation id as idempotency key", async () => {
    await validateCount("c1", [], "Casse constatée", "op-1");
    await closeTransfer("t1", "écart transport", "op-2");
    await recordLoss({ operationUuid: "op-3", locationId: "l", lossType: "theft", reason: "vol", lines: [], occurredAt: null });
    await recordAdjustment({ operationUuid: "op-4", locationId: "l", adjustmentType: "found", reason: "retrouvé", lines: [], occurredAt: null });
    expect(body(0)).toMatchObject({ operationUuid: "op-1", note: "Casse constatée" });
    expect(header(0, "Idempotency-Key")).toBe("op-1");
    expect(calls[1].url).toContain("/inventory/transfers/t1/close");
    expect(header(1, "Idempotency-Key")).toBe("op-2");
    expect(body(2).lossType).toBe("theft");
    expect(calls[3].url).toContain("/inventory/adjustments");
    expect(header(3, "Idempotency-Key")).toBe("op-4");
  });

  it("partial reception posts only the lines received", async () => {
    await receiveTransfer("t1", [{ transferLineId: "l1", quantity: 17 }]);
    expect(calls[0].url).toContain("/inventory/transfers/t1/receive");
    expect(body(0)).toEqual([{ transferLineId: "l1", quantity: 17 }]);
  });
});

describe("shared/forms", () => {
  it("operation ids are unique UUIDs", () => {
    const a = newOperationId(), b = newOperationId();
    expect(a).toMatch(/^[0-9a-f-]{36}$/);
    expect(a).not.toBe(b);
  });

  it("num accepts a French decimal comma", () => {
    expect(num("2,5")).toBe(2.5);
    expect(num("  ")).toBeNull();
  });
});
