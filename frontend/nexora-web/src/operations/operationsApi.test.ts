import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { configureApiClient } from "@/core/apiClient";
import {
  goLiveReadiness,
  listDeployments,
  recordDeployment,
  rollbackDeployment,
  signOff,
  systemStatus,
} from "@/operations/operationsApi";

function jsonResponse(status: number, body: unknown): Response {
  return new Response(JSON.stringify(body), { status, headers: { "Content-Type": "application/json" } });
}

describe("operationsApi (Phase 37 §29, §40, §54, §56)", () => {
  beforeEach(() => configureApiClient({ token: () => "t", company: () => "c", onUnauthorized: () => {} }));
  afterEach(() => vi.unstubAllGlobals());

  it("systemStatus and goLiveReadiness read the observability endpoints", async () => {
    const urls: string[] = [];
    vi.stubGlobal("fetch", vi.fn(async (url: string) => {
      urls.push(url);
      return jsonResponse(200, url.includes("go-live") ? { canGoLive: false, checks: [] } : { overall: "ok", components: [] });
    }));

    await systemStatus();
    await goLiveReadiness();

    expect(urls[0]).toContain("/system/status");
    expect(urls[1]).toContain("/go-live");
  });

  it("signOff posts the verdict and force reason", async () => {
    let body = "";
    vi.stubGlobal("fetch", vi.fn(async (_url: string, init: RequestInit) => {
      body = init.body as string;
      return jsonResponse(200, { id: "s1", verdict: "go", forced: true });
    }));

    await signOff({ environment: "production", verdict: "go", force: true, forceReason: "risque accepté" });

    expect(JSON.parse(body)).toMatchObject({ environment: "production", verdict: "go", force: true, forceReason: "risque accepté" });
  });

  it("recordDeployment and rollbackDeployment hit the deployment routes", async () => {
    const calls: { url: string; method: string }[] = [];
    vi.stubGlobal("fetch", vi.fn(async (url: string, init: RequestInit) => {
      calls.push({ url, method: (init.method ?? "GET").toUpperCase() });
      return jsonResponse(200, { id: "d1", status: "succeeded" });
    }));

    await recordDeployment({ version: "v1.0.0", environment: "staging" });
    await rollbackDeployment("d1", { reason: "régression" });

    expect(calls[0]).toMatchObject({ method: "POST" });
    expect(calls[0].url).toContain("/deployments");
    expect(calls[1].url).toContain("/deployments/d1/rollback");
  });

  it("listDeployments issues a GET", async () => {
    let method = "";
    vi.stubGlobal("fetch", vi.fn(async (_url: string, init: RequestInit) => {
      method = (init.method ?? "GET").toUpperCase();
      return jsonResponse(200, []);
    }));

    await listDeployments();

    expect(method).toBe("GET");
  });
});
