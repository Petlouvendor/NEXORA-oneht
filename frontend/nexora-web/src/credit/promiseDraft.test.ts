import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { configureApiClient } from "@/core/apiClient";
import { cancelPromise, createPromise, listPromises, writeOffReceivable, type CreditReceivable } from "@/credit/creditApi";
import { promisable, selectionTotal, validatePromise } from "@/credit/promiseDraft";

const rec = (id: string, remaining: number, extra: Partial<CreditReceivable> = {}): CreditReceivable => ({
  id, creditNumber: id.toUpperCase(), currency: "HTG", originalAmount: remaining, paidAmount: 0, remainingAmount: remaining,
  issueDate: "2026-09-01", dueDate: "2026-09-15", daysOverdue: 17, status: "OPEN", openPromiseId: null, openDisputeId: null, ...extra,
});

describe("promesse de paiement (phase 9)", () => {
  it("seules les créances dues, hors litige et sans promesse en cours sont proposées", () => {
    expect(promisable(rec("a", 100))).toBe(true);
    expect(promisable(rec("b", 100, { status: "IN_DISPUTE" }))).toBe(false);
    expect(promisable(rec("c", 100, { openPromiseId: "p1" }))).toBe(false);
    expect(promisable(rec("d", 0, { status: "PAID" }))).toBe(false);
  });

  it("totalise la sélection au centime et signale un mélange de devises", () => {
    const list = [rec("a", 100.1), rec("b", 0.2), rec("c", 50, { currency: "USD" })];
    expect(selectionTotal(list, ["a", "b"])).toEqual({ total: 100.3, currency: "HTG", mixed: false });
    expect(selectionTotal(list, ["a", "c"]).mixed).toBe(true);
  });

  it("refuse montant supérieur au reste dû, date passée, sélection vide — comme le serveur", () => {
    const base = { selected: ["a"], amount: 50, date: "2026-10-10", today: "2026-10-02", total: 100, mixed: false };
    expect(validatePromise(base)).toBeNull();
    expect(validatePromise({ ...base, selected: [] })?.field).toBe("receivables");
    expect(validatePromise({ ...base, amount: 150 })?.field).toBe("amount");
    expect(validatePromise({ ...base, amount: null })?.field).toBe("amount");
    expect(validatePromise({ ...base, date: "2026-10-01" })?.field).toBe("date");
    expect(validatePromise({ ...base, mixed: true })?.message).toContain("même devise");
  });
});

describe("creditApi (phase 9)", () => {
  beforeEach(() => configureApiClient({ token: () => "t", company: () => "c", onUnauthorized: () => {} }));
  afterEach(() => vi.unstubAllGlobals());

  it("filtres, annulation motivée et radiation idempotente sur l'identifiant d'opération du dialogue", async () => {
    const calls: Array<{ url: string; init: RequestInit }> = [];
    vi.stubGlobal("fetch", vi.fn(async (url: string, init: RequestInit) => {
      calls.push({ url, init });
      return new Response(JSON.stringify([]), { status: 200, headers: { "Content-Type": "application/json" } });
    }));

    await listPromises("open", "cust1");
    await cancelPromise("p1", "client a payé ailleurs");
    await createPromise({ customerId: "c1", amount: 10, promisedDate: "2026-10-10", channel: "sms", receivableIds: ["r1"], note: null });
    await writeOffReceivable("r1", 300, "insolvable", "op-123");

    expect(calls[0].url).toContain("/credit/promises?status=open&customerId=cust1");
    expect(JSON.parse(calls[1].init.body as string)).toEqual({ reason: "client a payé ailleurs" });
    expect(JSON.parse(calls[2].init.body as string).receivableIds).toEqual(["r1"]);
    expect(new Headers(calls[3].init.headers).get("Idempotency-Key")).toBe("op-123");
    expect(JSON.parse(calls[3].init.body as string).operationUuid).toBe("op-123");
  });
});
