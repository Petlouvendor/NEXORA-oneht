import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { configureApiClient } from "@/core/apiClient";
import {
  decideDataCorrection,
  deleteFeatureFlag,
  listJobs,
  pauseJob,
  replayDeadLetter,
  requestDataCorrection,
  runJobNow,
  saveFeatureFlag,
  scheduleMaintenance,
  transitionMaintenance,
} from "@/operations/operationsApi";

function jsonResponse(status: number, body: unknown): Response {
  return new Response(JSON.stringify(body), { status, headers: { "Content-Type": "application/json" } });
}

describe("operationsApi — administration opérationnelle & maintenance (Phase 41 §7-9, §10-12, §23-24, §27, §44)", () => {
  beforeEach(() => configureApiClient({ token: () => "t", company: () => "c", onUnauthorized: () => {} }));
  afterEach(() => vi.unstubAllGlobals());

  it("jobs: list, run now and pause hit the /ops/jobs routes", async () => {
    const calls: { url: string; method: string }[] = [];
    vi.stubGlobal("fetch", vi.fn(async (url: string, init: RequestInit) => {
      calls.push({ url, method: (init.method ?? "GET").toUpperCase() });
      return jsonResponse(200, []);
    }));

    await listJobs();
    await runJobNow("imports.resume_stalled");
    await pauseJob("imports.resume_stalled");

    expect(calls[0]).toMatchObject({ url: expect.stringContaining("/ops/jobs"), method: "GET" });
    expect(calls[1]).toMatchObject({ url: expect.stringContaining("/ops/jobs/imports.resume_stalled/run"), method: "POST" });
    expect(calls[2]).toMatchObject({ url: expect.stringContaining("/ops/jobs/imports.resume_stalled/pause"), method: "POST" });
  });

  it("dead-letter replay posts to /ops/dead-letters/{id}/replay", async () => {
    const calls: string[] = [];
    vi.stubGlobal("fetch", vi.fn(async (url: string) => { calls.push(url); return jsonResponse(200, {}); }));

    await replayDeadLetter("m1");

    expect(calls[0]).toContain("/ops/dead-letters/m1/replay");
  });

  it("feature flags: save carries scope/enabled and delete hits the id route", async () => {
    const calls: { url: string; body?: string }[] = [];
    vi.stubGlobal("fetch", vi.fn(async (url: string, init: RequestInit) => {
      calls.push({ url, body: init.body as string | undefined }); return jsonResponse(200, { id: "f1" });
    }));

    await saveFeatureFlag({ key: "beta.export", scope: "global", enabled: false });
    await deleteFeatureFlag("f1");

    expect(JSON.parse(calls[0].body!)).toMatchObject({ key: "beta.export", scope: "global", enabled: false });
    expect(calls[1].url).toContain("/ops/feature-flags/f1");
  });

  it("maintenance: schedule then transition hit their routes with a status body", async () => {
    let lastBody = "";
    const calls: string[] = [];
    vi.stubGlobal("fetch", vi.fn(async (url: string, init: RequestInit) => {
      calls.push(url); lastBody = init.body as string; return jsonResponse(200, { id: "w1", status: "notified" });
    }));

    await scheduleMaintenance({ scheduledAt: "2026-01-01T00:00:00Z", durationMinutes: 60, impact: "coupure API", services: ["api"] });
    await transitionMaintenance("w1", "notified");

    expect(calls[0]).toContain("/ops/maintenance");
    expect(calls[1]).toContain("/ops/maintenance/w1/transition");
    expect(JSON.parse(lastBody)).toMatchObject({ status: "notified" });
  });

  it("data corrections: request then decide carry the expected payloads", async () => {
    const bodies: string[] = [];
    vi.stubGlobal("fetch", vi.fn(async (_url: string, init: RequestInit) => {
      bodies.push(init.body as string); return jsonResponse(200, { id: "c1", status: "requested" });
    }));

    await requestDataCorrection({ tableName: "customer", recordId: "cust-1", fieldName: "phone", newValue: "+509-1", reason: "erreur" });
    await decideDataCorrection("c1", true, "vérifié");

    expect(JSON.parse(bodies[0])).toMatchObject({ tableName: "customer", fieldName: "phone", newValue: "+509-1" });
    expect(JSON.parse(bodies[1])).toMatchObject({ approve: true, notes: "vérifié" });
  });
});
