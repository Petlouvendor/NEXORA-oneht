import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { configureApiClient } from "@/core/apiClient";
import { listPaymentAccounts, matchInvoice, payPayable } from "@/purchasing/purchasingApi";

function jsonResponse(status: number, body: unknown): Response {
  return new Response(JSON.stringify(body), { status, headers: { "Content-Type": "application/json" } });
}

describe("purchasingApi (Phase 26 §20, §27)", () => {
  beforeEach(() => configureApiClient({ token: () => "t", company: () => "c", onUnauthorized: () => {} }));
  afterEach(() => vi.unstubAllGlobals());

  it("payPayable carries an operation UUID and an idempotency key", async () => {
    let seen: RequestInit = {};
    vi.stubGlobal("fetch", vi.fn(async (_url: string, init: RequestInit) => {
      seen = init;
      return jsonResponse(200, { journalEntryId: "je1" });
    }));

    await payPayable("pay1", 500, "acc1", "vir");

    expect(new Headers(seen.headers).get("Idempotency-Key")).toBeTruthy();
    const body = JSON.parse(seen.body as string);
    expect(body.operationUuid).toBeTruthy();
    expect(body.amount).toBe(500);
    expect(body.tenderFinancialAccountId).toBe("acc1");
  });

  it("payPayable reuses the dialog's operation UUID as idempotency key, so a double click never pays twice", async () => {
    const seen: RequestInit[] = [];
    vi.stubGlobal("fetch", vi.fn(async (_url: string, init: RequestInit) => {
      seen.push(init);
      return jsonResponse(200, { journalEntryId: "je1", paymentId: "p1" });
    }));
    await payPayable("pay1", 500, "acc1", undefined, "op-fixed");
    await payPayable("pay1", 500, "acc1", undefined, "op-fixed");
    for (const init of seen) {
      expect(new Headers(init.headers).get("Idempotency-Key")).toBe("op-fixed");
      expect(JSON.parse(init.body as string).operationUuid).toBe("op-fixed");
    }
  });

  it("listPaymentAccounts reads the payable payment accounts (no treasury balance)", async () => {
    vi.stubGlobal("fetch", vi.fn(async (url: string) => {
      expect(url).toContain("/payables/payment-accounts");
      return jsonResponse(200, [{ id: "a1", name: "Caisse officine", accountType: "cash", currency: "HTG" }]);
    }));
    const list = await listPaymentAccounts();
    expect(list[0].name).toBe("Caisse officine");
    expect(Object.keys(list[0])).not.toContain("currentBalance");
  });

  it("matchInvoice defaults the tolerances", async () => {
    let seen: RequestInit = {};
    vi.stubGlobal("fetch", vi.fn(async (url: string, init: RequestInit) => {
      expect(url).toContain("/purchasing/invoices/inv1/match");
      seen = init;
      return jsonResponse(200, { invoiceId: "inv1", matchStatus: "matched", paymentBlocked: false, lines: [] });
    }));

    await matchInvoice("inv1");
    const body = JSON.parse(seen.body as string);
    expect(body.quantityTolerancePercent).toBe(5);
    expect(body.priceTolerancePercent).toBe(2);
  });
});
