import { webcrypto } from "node:crypto";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { configureApiClient } from "@/core/apiClient";
import {
  exportCustomerReport, lookupCustomers, openCredit, recordPayment, recordPaymentOrQueue, requestCreditOverride, scheduleCredit,
  voidPayment,
} from "@/customers/customersApi";
import { __resetOutbox, listOutbox } from "@/shared/offlineQueue";
import { configureSecureStore, memoryBackend } from "@/shared/secureStore";

function jsonResponse(status: number, body: unknown): Response {
  return new Response(JSON.stringify(body), { status, headers: { "Content-Type": "application/json" } });
}

describe("customersApi (module clients §9, §17, §22-23, §26)", () => {
  beforeEach(() => configureApiClient({ token: () => "t", company: () => "c", onUnauthorized: () => {} }));
  afterEach(() => { vi.unstubAllGlobals(); vi.restoreAllMocks(); });

  it("recordPayment reuses the same operation UUID as idempotency key, so a retry never pays twice", async () => {
    const seen: RequestInit[] = [];
    vi.stubGlobal("fetch", vi.fn(async (url: string, init: RequestInit) => {
      expect(url).toContain("/customers/cust-1/payments");
      seen.push(init);
      return jsonResponse(200, { id: "p1", paymentNumber: "PAY-1", amount: 500, currency: "HTG", allocations: [] });
    }));

    await recordPayment("cust-1", { amount: 500, financialAccountId: "fa" }, "op-fixed");
    await recordPayment("cust-1", { amount: 500, financialAccountId: "fa" }, "op-fixed");

    for (const init of seen) {
      expect(new Headers(init.headers).get("Idempotency-Key")).toBe("op-fixed");
      expect(JSON.parse(init.body as string)).toMatchObject({ operationUuid: "op-fixed", amount: 500 });
    }
  });

  it("voidPayment posts the mandatory reason with an idempotency key", async () => {
    let seen: RequestInit = {};
    vi.stubGlobal("fetch", vi.fn(async (url: string, init: RequestInit) => {
      expect(url).toContain("/payments/p1/void");
      seen = init;
      return jsonResponse(200, { id: "p1", status: "void" });
    }));

    await voidPayment("p1", "Chèque sans provision");
    expect(new Headers(seen.headers).get("Idempotency-Key")).toBeTruthy();
    expect(JSON.parse(seen.body as string)).toEqual({ reason: "Chèque sans provision" });
  });

  it("lookupCustomers sends the query encoded and a small limit (never the whole base)", async () => {
    vi.stubGlobal("fetch", vi.fn(async (url: string) => {
      expect(url).toContain("/customers/lookup?");
      expect(url).toContain("q=Jean+Pi%C3%A8rre");
      expect(url).toContain("limit=8");
      return jsonResponse(200, []);
    }));
    expect(await lookupCustomers("Jean Pièrre")).toEqual([]);
  });

  it("exportCustomerReport downloads the file with the server file name", async () => {
    vi.stubGlobal("fetch", vi.fn(async (url: string) => {
      expect(url).toContain("/customer-reports/aging/export?from=2026-09-01&to=2026-09-23&format=csv");
      return new Response("\uFEFFCode client;Client\n", {
        status: 200,
        headers: { "Content-Type": "text/csv", "Content-Disposition": 'attachment; filename="clients-aging-20260923.csv"' },
      });
    }));
    const anchor = document.createElement("a");
    const click = vi.spyOn(anchor, "click").mockImplementation(() => {});
    vi.spyOn(document, "createElement").mockReturnValue(anchor);
    URL.createObjectURL = vi.fn(() => "blob:x");
    URL.revokeObjectURL = vi.fn();

    await exportCustomerReport("aging", "2026-09-01", "2026-09-23");
    expect(click).toHaveBeenCalled();
    expect(anchor.download).toBe("clients-aging-20260923.csv");
  });

  it("openCredit reuses its operation UUID as idempotency key (§22 : jamais deux crédits)", async () => {
    let seen: RequestInit = {};
    vi.stubGlobal("fetch", vi.fn(async (url: string, init: RequestInit) => {
      expect(url).toContain("/customers/c1/credits");
      seen = init;
      return jsonResponse(200, { credit: { creditNumber: "CRD-1" }, settlements: [], schedule: null });
    }));
    await openCredit("c1", 5000, "Reprise de solde", "2026-10-01", "op-open");
    expect(new Headers(seen.headers).get("Idempotency-Key")).toBe("op-open");
    expect(JSON.parse(seen.body as string)).toEqual({ operationUuid: "op-open", amount: 5000, reason: "Reprise de solde", dueDate: "2026-10-01" });
  });

  it("scheduleCredit posts either generated or explicit installments", async () => {
    const bodies: unknown[] = [];
    vi.stubGlobal("fetch", vi.fn(async (url: string, init: RequestInit) => {
      expect(url).toContain("/credits/r1/installments");
      bodies.push(JSON.parse(init.body as string));
      return jsonResponse(200, { credit: {}, settlements: [], schedule: null });
    }));
    await scheduleCredit("r1", { reason: "Plan", count: 3, firstDueDate: "2026-10-15", monthly: true });
    await scheduleCredit("r1", { reason: "Plan", installments: [{ dueDate: "2026-10-15", amount: 900 }] });
    expect(bodies).toEqual([
      { reason: "Plan", count: 3, firstDueDate: "2026-10-15", monthly: true },
      { reason: "Plan", installments: [{ dueDate: "2026-10-15", amount: 900 }] },
    ]);
  });

  it("recordPaymentOrQueue puts an offline payment in the encrypted outbox with the SAME operation UUID (§24)", async () => {
    configureSecureStore({ backend: memoryBackend(), subtle: webcrypto.subtle as unknown as SubtleCrypto });
    __resetOutbox();
    vi.stubGlobal("fetch", vi.fn(async () => { throw new TypeError("Failed to fetch"); }));
    const outcome = await recordPaymentOrQueue("c1", { amount: 250, financialAccountId: "fa" }, "op-offline");
    expect(outcome).toEqual({ queued: true });
    expect(listOutbox()).toEqual([
      expect.objectContaining({ id: "op-offline", kind: "customer.payment", payload: { customerId: "c1", amount: 250, financialAccountId: "fa" } }),
    ]);
  });

  it("requestCreditOverride sends the cart, the customer and the reason — never an approval (§12)", async () => {
    let seen: RequestInit = {};
    vi.stubGlobal("fetch", vi.fn(async (url: string, init: RequestInit) => {
      expect(url).toContain("/pos/credit-override-requests");
      seen = init;
      return jsonResponse(200, { id: "a1", status: "pending" });
    }));
    await requestCreditOverride({ cartOperationUuid: "cart", customerId: "c1", amount: 1500, currency: "HTG", reason: "Client fidèle" });
    expect(JSON.parse(seen.body as string)).toEqual({ cartOperationUuid: "cart", customerId: "c1", amount: 1500, currency: "HTG", reason: "Client fidèle" });
  });
});
