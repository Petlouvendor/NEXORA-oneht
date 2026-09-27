import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { configureApiClient } from "@/core/apiClient";
import { completeFinancialClose, listSuspense, openSuspense } from "@/finance/financeApi";

function jsonResponse(status: number, body: unknown): Response {
  return new Response(JSON.stringify(body), { status, headers: { "Content-Type": "application/json" } });
}

describe("financeApi (Phase 28 §32, §38)", () => {
  beforeEach(() => configureApiClient({ token: () => "t", company: () => "c", onUnauthorized: () => {} }));
  afterEach(() => vi.unstubAllGlobals());

  it("listSuspense passes the overdue filter and optional status", async () => {
    let seenUrl = "";
    vi.stubGlobal("fetch", vi.fn(async (url: string) => {
      seenUrl = url;
      return jsonResponse(200, []);
    }));

    await listSuspense("open", true);

    expect(seenUrl).toContain("/treasury/suspense?onlyOverdue=true");
    expect(seenUrl).toContain("status=open");
  });

  it("openSuspense always sends responsibleUserId and completeFinancialClose sends force + note", async () => {
    const bodies: string[] = [];
    vi.stubGlobal("fetch", vi.fn(async (_url: string, init: RequestInit) => {
      bodies.push(init.body as string);
      return jsonResponse(200, "x");
    }));

    await openSuspense({ kind: "unidentified", amount: -50, currency: "HTG", reason: "frais" });
    await completeFinancialClose("p1", true, "direction");

    const open = JSON.parse(bodies[0]);
    expect(open).toHaveProperty("responsibleUserId", null);
    expect(open.amount).toBe(-50);

    const close = JSON.parse(bodies[1]);
    expect(close).toEqual({ force: true, note: "direction" });
  });
});
