import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { configureApiClient } from "@/core/apiClient";
import {
  disableApp,
  installApp,
  listApps,
  listDeveloperUsage,
  listInstalledApps,
  reviewApp,
  submitApp,
  uninstallApp,
} from "@/integrations/integrationsApi";

function jsonResponse(status: number, body: unknown): Response {
  return new Response(JSON.stringify(body), { status, headers: { "Content-Type": "application/json" } });
}

describe("marketplace & écosystème partenaires (Phase 46 §41-48)", () => {
  beforeEach(() => configureApiClient({ token: () => "t", company: () => "c", onUnauthorized: () => {} }));
  afterEach(() => vi.unstubAllGlobals());

  it("listApps reads the public catalogue with cursor pagination", async () => {
    let seenUrl = "";
    vi.stubGlobal("fetch", vi.fn(async (url: string) => {
      seenUrl = url;
      return jsonResponse(200, { items: [{ id: "a1", slug: "nexora-export-comptable" }], nextCursor: "cur123" });
    }));

    const page = await listApps();

    expect(seenUrl).toContain("/apps");
    expect(page.items).toHaveLength(1);
    expect(page.nextCursor).toBe("cur123");
  });

  it("listApps forwards the cursor for the next page", async () => {
    let seenUrl = "";
    vi.stubGlobal("fetch", vi.fn(async (url: string) => { seenUrl = url; return jsonResponse(200, { items: [], nextCursor: null }); }));

    await listApps("cur123");

    expect(seenUrl).toContain(`cursor=${encodeURIComponent("cur123")}`);
  });

  it("installApp posts the granted scopes", async () => {
    let body = "";
    vi.stubGlobal("fetch", vi.fn(async (_url: string, init: RequestInit) => {
      body = init.body as string;
      return jsonResponse(200, { id: "inst1", status: "installed", grantedScopes: ["accounting.view"] });
    }));

    const res = await installApp("app1", ["accounting.view"]);

    expect(JSON.parse(body)).toEqual({ grantedScopes: ["accounting.view"] });
    expect(res.status).toBe("installed");
  });

  it("uninstallApp and disableApp hit the right routes", async () => {
    const calls: Array<{ url: string; method: string }> = [];
    vi.stubGlobal("fetch", vi.fn(async (url: string, init?: RequestInit) => {
      calls.push({ url, method: (init?.method ?? "GET").toUpperCase() });
      return new Response(null, { status: 204 }); // pas de corps sur un 204 — sinon fetch lève une TypeError
    }));

    await uninstallApp("app1");
    await disableApp("app1");

    expect(calls[0]).toEqual({ url: expect.stringContaining("/apps/app1/uninstall"), method: "POST" });
    expect(calls[1]).toEqual({ url: expect.stringContaining("/apps/app1/disable"), method: "POST" });
  });

  it("listInstalledApps reads the company's own installations", async () => {
    let seenUrl = "";
    vi.stubGlobal("fetch", vi.fn(async (url: string) => { seenUrl = url; return jsonResponse(200, []); }));

    await listInstalledApps();

    expect(seenUrl).toContain("/apps/installed");
  });

  it("listDeveloperUsage reads the developer portal usage window", async () => {
    let seenUrl = "";
    vi.stubGlobal("fetch", vi.fn(async (url: string) => { seenUrl = url; return jsonResponse(200, []); }));

    await listDeveloperUsage(30);

    expect(seenUrl).toContain("/developer/usage?days=30");
  });

  it("submitApp and reviewApp are reserved to platform staff routes", async () => {
    const calls: Array<{ url: string; method: string; body: string }> = [];
    vi.stubGlobal("fetch", vi.fn(async (url: string, init?: RequestInit) => {
      calls.push({ url, method: (init?.method ?? "GET").toUpperCase(), body: (init?.body as string) ?? "" });
      return jsonResponse(200, { id: "app1", status: "pending_review" });
    }));

    await submitApp({
      slug: "test-app", name: "Test", publisherName: "Éditeur", category: "crm",
      version: "1.0", requestedScopes: ["catalog.view"],
    });
    await reviewApp("app1", "approve", "OK");

    expect(calls[0]).toEqual({ url: expect.stringContaining("/apps"), method: "POST", body: expect.any(String) });
    expect(JSON.parse(calls[1].body)).toEqual({ decision: "approve", notes: "OK" });
    expect(calls[1].url).toContain("/apps/app1/review");
  });
});
