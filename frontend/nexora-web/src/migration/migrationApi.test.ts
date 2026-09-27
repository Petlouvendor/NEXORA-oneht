import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { configureApiClient } from "@/core/apiClient";
import {
  completeOnboardingTask,
  createImport,
  onboardingChecklist,
  reconcileImport,
  setMapping,
  simulateImport,
  suggestMapping,
} from "@/migration/migrationApi";

function json(status: number, body: unknown): Response {
  return new Response(JSON.stringify(body), { status, headers: { "Content-Type": "application/json" } });
}

describe("migrationApi (Phase 39 §5, §23, §32, §45)", () => {
  beforeEach(() => configureApiClient({ token: () => "t", company: () => "c", onUnauthorized: () => {} }));
  afterEach(() => vi.unstubAllGlobals());

  it("create + suggest-mapping hit the import routes", async () => {
    const calls: { url: string; body?: string }[] = [];
    vi.stubGlobal("fetch", vi.fn(async (url: string, init: RequestInit) => {
      calls.push({ url, body: init.body as string });
      return json(200, url.includes("suggest") ? [] : { id: "j1", status: "created" });
    }));

    await createImport({ kind: "customers", sourceType: "csv", content: "a,b\n1,2", mode: "upsert" });
    await suggestMapping("customers", ["a", "b"]);

    expect(calls[0].url).toContain("/imports");
    expect(JSON.parse(calls[0].body!)).toMatchObject({ kind: "customers", sourceType: "csv" });
    expect(calls[1].url).toContain("/imports/suggest-mapping/customers");
  });

  it("pipeline steps target the right endpoints", async () => {
    const urls: string[] = [];
    vi.stubGlobal("fetch", vi.fn(async (url: string) => { urls.push(url); return json(200, { id: "j1", status: "simulated", lines: [], allReconciled: true }); }));

    await setMapping("j1", [{ targetField: "code", sourceColumn: "a" }]);
    await simulateImport("j1");
    await reconcileImport("j1");

    expect(urls[0]).toContain("/imports/j1/mapping");
    expect(urls[1]).toContain("/imports/j1/simulate");
    expect(urls[2]).toContain("/imports/j1/reconciliation");
  });

  it("onboarding checklist + task completion", async () => {
    const calls: { url: string; method: string }[] = [];
    vi.stubGlobal("fetch", vi.fn(async (url: string, init: RequestInit) => {
      calls.push({ url, method: (init.method ?? "GET").toUpperCase() });
      return json(200, { done: 1, total: 2, progressPct: 50, score: {}, items: [] });
    }));

    await onboardingChecklist();
    await completeOnboardingTask("tests", "done");

    expect(calls[0]).toMatchObject({ url: expect.stringContaining("/onboarding/checklist"), method: "GET" });
    expect(calls[1]).toMatchObject({ url: expect.stringContaining("/onboarding/tasks"), method: "POST" });
  });
});
