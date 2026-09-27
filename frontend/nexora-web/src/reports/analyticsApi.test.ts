import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { configureApiClient } from "@/core/apiClient";
import { createSnapshot, getReconciliation } from "@/reports/analyticsApi";

function jsonResponse(status: number, body: unknown): Response {
  return new Response(JSON.stringify(body), { status, headers: { "Content-Type": "application/json" } });
}

describe("analyticsApi (Phase 31 §44-46)", () => {
  beforeEach(() => configureApiClient({ token: () => "t", company: () => "c", onUnauthorized: () => {} }));
  afterEach(() => vi.unstubAllGlobals());

  it("getReconciliation passes the period as query params", async () => {
    let seenUrl = "";
    vi.stubGlobal("fetch", vi.fn(async (url: string) => {
      seenUrl = url;
      return jsonResponse(200, { lines: [] });
    }));

    await getReconciliation("2026-01-01", "2026-06-30");

    expect(seenUrl).toContain("/analytics/reconciliation?");
    expect(seenUrl).toContain("from=2026-01-01");
    expect(seenUrl).toContain("to=2026-06-30");
  });

  it("createSnapshot posts the kind and label with null period", async () => {
    let body = "";
    vi.stubGlobal("fetch", vi.fn(async (_url: string, init: RequestInit) => {
      body = init.body as string;
      return jsonResponse(200, { id: "s1" });
    }));

    await createSnapshot("data_quality", "Contrôle mensuel");

    expect(JSON.parse(body)).toEqual({ kind: "data_quality", label: "Contrôle mensuel", from: null, to: null });
  });
});
