import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { configureApiClient } from "@/core/apiClient";
import {
  advanceStop, completeStop, createRoute, getMobileConfig, listRoutes, registerPushToken, uploadAttachment,
} from "@/mobile/mobileApi";

function json(status: number, body: unknown): Response {
  return new Response(JSON.stringify(body), { status, headers: { "Content-Type": "application/json" } });
}

describe("mobileApi — Mobile, terrain & multi-appareils (Phase 44)", () => {
  beforeEach(() => configureApiClient({ token: () => "t", company: () => "c", onUnauthorized: () => {} }));
  afterEach(() => vi.unstubAllGlobals());

  it("routes: list and create hit the expected routes", async () => {
    const calls: { url: string; method: string; body?: string }[] = [];
    vi.stubGlobal("fetch", vi.fn(async (url: string, init?: RequestInit) => {
      calls.push({ url, method: (init?.method ?? "GET").toUpperCase(), body: init?.body as string | undefined });
      return json(200, { id: "r1", stops: [] });
    }));

    await listRoutes();
    await createRoute({ routeDate: "2026-01-01", stops: [{ address: "12 Rue Test" }] });

    expect(calls[0]).toMatchObject({ url: expect.stringContaining("/field-ops/routes"), method: "GET" });
    expect(calls[1].method).toBe("POST");
    expect(JSON.parse(calls[1].body!)).toMatchObject({ routeDate: "2026-01-01" });
  });

  it("stops: advance and complete post the expected payloads", async () => {
    const calls: { url: string; body?: string }[] = [];
    vi.stubGlobal("fetch", vi.fn(async (url: string, init?: RequestInit) => {
      calls.push({ url, body: init?.body as string | undefined }); return json(200, { id: "s1", status: "delivered" });
    }));

    await advanceStop("s1", "en_route");
    await completeStop("s1", { operationUuid: "op-1", amountCollected: 500 });

    expect(calls[0].url).toContain("/field-ops/stops/s1/advance");
    expect(JSON.parse(calls[0].body!)).toBe("en_route");
    expect(calls[1].url).toContain("/field-ops/stops/s1/complete");
    expect(JSON.parse(calls[1].body!)).toMatchObject({ operationUuid: "op-1", amountCollected: 500 });
  });

  it("attachments: upload posts base64 content; push token registers on the sync device route", async () => {
    const calls: { url: string; body?: string }[] = [];
    vi.stubGlobal("fetch", vi.fn(async (url: string, init?: RequestInit) => {
      calls.push({ url, body: init?.body as string | undefined }); return json(200, { id: "a1" });
    }));

    await uploadAttachment({ kind: "photo", contentBase64: "Zm9v", contentType: "image/png" });
    await registerPushToken("dev-1", "token-xyz", "fcm");

    expect(calls[0].url).toContain("/attachments");
    expect(JSON.parse(calls[0].body!)).toMatchObject({ kind: "photo", contentBase64: "Zm9v" });
    expect(calls[1].url).toContain("/sync/devices/dev-1/push-token");
    expect(JSON.parse(calls[1].body!)).toMatchObject({ pushToken: "token-xyz", pushProvider: "fcm" });
  });

  it("mobile config hits the expected route", async () => {
    const urls: string[] = [];
    vi.stubGlobal("fetch", vi.fn(async (url: string) => { urls.push(url); return json(200, { supportedOfflineEntityTypes: [] }); }));

    await getMobileConfig();

    expect(urls[0]).toContain("/mobile/config");
  });
});
