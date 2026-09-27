import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { configureApiClient } from "@/core/apiClient";
import { createApiKey, listApiKeys, revokeApiKey, rotateApiKey } from "@/integrations/integrationsApi";

function jsonResponse(status: number, body: unknown): Response {
  return new Response(JSON.stringify(body), { status, headers: { "Content-Type": "application/json" } });
}

describe("api keys (Phase 35 §13-14)", () => {
  beforeEach(() => configureApiClient({ token: () => "t", company: () => "c", onUnauthorized: () => {} }));
  afterEach(() => vi.unstubAllGlobals());

  it("listApiKeys reads the api-keys collection", async () => {
    let seenUrl = "";
    vi.stubGlobal("fetch", vi.fn(async (url: string) => { seenUrl = url; return jsonResponse(200, []); }));

    await listApiKeys();

    expect(seenUrl).toContain("/api-keys");
  });

  it("createApiKey posts the name, scopes and expiry", async () => {
    let body = "";
    vi.stubGlobal("fetch", vi.fn(async (_url: string, init: RequestInit) => {
      body = init.body as string;
      return jsonResponse(200, { key: { id: "k1", scopes: ["catalog.view"] }, secret: "nxk_live_ab12_secret" });
    }));

    const res = await createApiKey({ name: "Connecteur", scopes: ["catalog.view"], expiresInDays: 90 });

    expect(JSON.parse(body)).toEqual({ name: "Connecteur", scopes: ["catalog.view"], expiresInDays: 90 });
    expect(res.secret).toBe("nxk_live_ab12_secret");
  });

  it("rotateApiKey and revokeApiKey hit the right routes", async () => {
    const calls: Array<{ url: string; method: string }> = [];
    vi.stubGlobal("fetch", vi.fn(async (url: string, init: RequestInit) => {
      calls.push({ url, method: (init.method ?? "GET").toUpperCase() });
      return jsonResponse(200, { key: { id: "k1" }, secret: "s" });
    }));

    await rotateApiKey("k1");
    await revokeApiKey("k1");

    expect(calls[0]).toEqual({ url: expect.stringContaining("/api-keys/k1/rotate"), method: "POST" });
    expect(calls[1]).toEqual({ url: expect.stringContaining("/api-keys/k1/revoke"), method: "POST" });
  });
});
