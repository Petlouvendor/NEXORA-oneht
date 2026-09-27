import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { configureApiClient } from "@/core/apiClient";
import { applySectorProfile, listSectorProfiles, previewSectorProfile, validateConfig } from "@/sectors/configApi";

function jsonResponse(status: number, body: unknown): Response {
  return new Response(JSON.stringify(body), { status, headers: { "Content-Type": "application/json" } });
}

describe("configApi (Phase 32 §6, §50-52)", () => {
  beforeEach(() => configureApiClient({ token: () => "t", company: () => "c", onUnauthorized: () => {} }));
  afterEach(() => vi.unstubAllGlobals());

  it("listSectorProfiles reads the sector-profiles collection", async () => {
    let seenUrl = "";
    vi.stubGlobal("fetch", vi.fn(async (url: string) => {
      seenUrl = url;
      return jsonResponse(200, []);
    }));

    await listSectorProfiles();

    expect(seenUrl).toContain("/sector-profiles");
  });

  it("previewSectorProfile POSTs to the profile preview route without a body", async () => {
    let method = "";
    let seenUrl = "";
    vi.stubGlobal("fetch", vi.fn(async (url: string, init: RequestInit) => {
      seenUrl = url;
      method = (init.method ?? "GET").toUpperCase();
      return jsonResponse(200, { profileKey: "pharmacy", profileName: "Pharmacie", items: [], guardrails: [] });
    }));

    await previewSectorProfile("pharmacy");

    expect(seenUrl).toContain("/sector-profiles/pharmacy/preview");
    expect(method).toBe("POST");
  });

  it("applySectorProfile sends the dryRun flag in the body", async () => {
    let body = "";
    vi.stubGlobal("fetch", vi.fn(async (_url: string, init: RequestInit) => {
      body = init.body as string;
      return jsonResponse(200, { profileKey: "pharmacy", dryRun: true, modulesApplied: 0, fieldsCreated: 0, terminologySet: 0, settingsSet: 0, warnings: [] });
    }));

    await applySectorProfile("pharmacy", true);

    expect(JSON.parse(body)).toEqual({ dryRun: true });
  });

  it("validateConfig reads the guardrails endpoint", async () => {
    let seenUrl = "";
    vi.stubGlobal("fetch", vi.fn(async (url: string) => {
      seenUrl = url;
      return jsonResponse(200, { canPublish: true, passed: 5, failed: 0, warnings: 0, checks: [], generatedAt: "2026-08-31T00:00:00Z" });
    }));

    await validateConfig();

    expect(seenUrl).toContain("/configurations/validate");
  });
});
