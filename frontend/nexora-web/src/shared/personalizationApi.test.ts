import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { configureApiClient } from "@/core/apiClient";
import {
  getDashboardLayouts,
  getDesignSystemVersion,
  listSavedFilters,
  saveDashboardLayout,
  saveSavedFilter,
  saveShortcut,
} from "@/shared/personalizationApi";
import { getPreferences, savePreferences } from "@/shared/preferencesApi";

function json(status: number, body: unknown): Response {
  return new Response(JSON.stringify(body), { status, headers: { "Content-Type": "application/json" } });
}

describe("personalizationApi (Phase 40 §13, §21-22, §31-33, §37, §57)", () => {
  beforeEach(() => configureApiClient({ token: () => "t", company: () => "c", onUnauthorized: () => {} }));
  afterEach(() => vi.unstubAllGlobals());

  it("preferences use the canonical /user-preferences alias with accessibility fields", async () => {
    const calls: { url: string; body?: string }[] = [];
    vi.stubGlobal("fetch", vi.fn(async (url: string, init: RequestInit) => {
      calls.push({ url, body: init.body as string });
      return json(200, { theme: "system", highContrast: true, textScale: 1.15 });
    }));

    await getPreferences();
    await savePreferences({ highContrast: true, textScale: 1.15 });

    expect(calls[0].url).toContain("/user-preferences");
    expect(JSON.parse(calls[1].body!)).toMatchObject({ highContrast: true, textScale: 1.15 });
  });

  it("dashboard layout + saved filter + shortcut hit their routes", async () => {
    const calls: { url: string; method: string }[] = [];
    vi.stubGlobal("fetch", vi.fn(async (url: string, init: RequestInit) => {
      calls.push({ url, method: (init.method ?? "GET").toUpperCase() });
      return json(200, url.includes("layouts") && init.method === undefined ? { effective: null, all: [] } : { id: "x" });
    }));

    await getDashboardLayouts();
    await saveDashboardLayout({ name: "Perso", widgets: [{ key: "k", x: 0, y: 0, w: 2, h: 2 }] });
    await saveSavedFilter({ viewKey: "sales.list", name: "Ouvertes", filter: { status: "open" } });
    await saveShortcut({ chord: "g v", action: "/pos", label: "POS" });

    expect(calls[0]).toMatchObject({ url: expect.stringContaining("/dashboard-layouts"), method: "GET" });
    expect(calls[1]).toMatchObject({ url: expect.stringContaining("/dashboard-layouts"), method: "PUT" });
    expect(calls[2]).toMatchObject({ url: expect.stringContaining("/saved-filters"), method: "POST" });
    expect(calls[3]).toMatchObject({ url: expect.stringContaining("/shortcuts"), method: "POST" });
  });

  it("saved filter list encodes the view key and design-system version is fetched", async () => {
    const urls: string[] = [];
    vi.stubGlobal("fetch", vi.fn(async (url: string) => { urls.push(url); return json(200, []); }));
    await listSavedFilters("documents.list");
    await getDesignSystemVersion();
    expect(urls[0]).toContain("/saved-filters?viewKey=documents.list");
    expect(urls[1]).toContain("/design-system/version");
  });
});
