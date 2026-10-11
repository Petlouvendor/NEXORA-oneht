import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { configureApiClient } from "@/core/apiClient";
import { closeYear, createDraft, postEntry } from "@/accounting/accountingApi";

function jsonResponse(status: number, body: unknown): Response {
  return new Response(JSON.stringify(body), { status, headers: { "Content-Type": "application/json" } });
}

describe("accountingApi (Phase 29 §11-18)", () => {
  beforeEach(() => configureApiClient({ token: () => "t", company: () => "c", onUnauthorized: () => {} }));
  afterEach(() => vi.unstubAllGlobals());

  it("createDraft posts the journal, date and lines untouched", async () => {
    let seen: RequestInit = {};
    vi.stubGlobal("fetch", vi.fn(async (url: string, init: RequestInit) => {
      expect(url).toContain("/accounting-entries");
      seen = init;
      return jsonResponse(200, "e1");
    }));

    await createDraft({
      journalCode: "OD", entryDate: "2026-04-01", currency: "HTG", description: "test",
      lines: [{ accountCode: "5711", debit: 100, credit: 0 }, { accountCode: "7011", debit: 0, credit: 100 }],
    });

    const body = JSON.parse(seen.body as string);
    expect(body.journalCode).toBe("OD");
    expect(body.lines).toHaveLength(2);
    expect(body.lines[1].credit).toBe(100);
  });

  it("postEntry sends an idempotency key; closeYear sends force + note", async () => {
    const calls: Array<{ url: string; init: RequestInit }> = [];
    vi.stubGlobal("fetch", vi.fn(async (url: string, init: RequestInit) => {
      calls.push({ url, init });
      return jsonResponse(200, {});
    }));

    await postEntry("e1");
    await closeYear("p1", true, "direction");

    expect(new Headers(calls[0].init.headers).get("Idempotency-Key")).toBeTruthy();
    expect(calls[0].url).toContain("/accounting-entries/e1/post");
    expect(JSON.parse(calls[1].init.body as string)).toEqual({ force: true, note: "direction" });
  });
});

describe("accountingApi (finalisation phase 8)", () => {
  beforeEach(() => configureApiClient({ token: () => "t", company: () => "c", onUnauthorized: () => {} }));
  afterEach(() => vi.unstubAllGlobals());

  it("searchEntries n'envoie que les filtres renseignés ; reopenPeriod et le statut de compte exigent un motif", async () => {
    const calls: Array<{ url: string; init: RequestInit }> = [];
    vi.stubGlobal("fetch", vi.fn(async (url: string, init: RequestInit) => {
      calls.push({ url, init });
      return jsonResponse(200, []);
    }));
    const { searchEntries, reopenPeriod, setChartAccountStatus, getGeneralLedger } = await import("@/accounting/accountingApi");

    await searchEntries({ status: "posted", journal: "", q: "loyer", limit: 50 });
    await reopenPeriod("p1", "erreur de date");
    await setChartAccountStatus("a1", "inactive", "doublon");
    await getGeneralLedger("6800", "2026-01-01", "2026-10-02");

    expect(calls[0].url).toContain("/accounting-entries?status=posted&q=loyer&limit=50");
    expect(calls[0].url).not.toContain("journal=");
    expect(JSON.parse(calls[1].init.body as string)).toEqual({ reason: "erreur de date", reversalDate: null });
    expect(JSON.parse(calls[2].init.body as string)).toEqual({ status: "inactive", reason: "doublon" });
    expect(calls[3].url).toContain("/general-ledger?account=6800&from=2026-01-01&to=2026-10-02");
  });
});
