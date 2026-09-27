import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { configureApiClient } from "@/core/apiClient";
import {
  approveRestore,
  drStatus,
  listDrTests,
  listRunbooks,
  lockDownIncident,
  recordDrTestResult,
  registerBackup,
  resolveIncident,
  restoreTest,
  scheduleDrTest,
  statusPage,
  verifyBackup,
} from "@/continuity/continuityApi";

function jsonResponse(status: number, body: unknown): Response {
  return new Response(JSON.stringify(body), { status, headers: { "Content-Type": "application/json" } });
}

describe("continuityApi (Phase 36 §21, §36, §39, §45)", () => {
  beforeEach(() => configureApiClient({ token: () => "t", company: () => "c", onUnauthorized: () => {} }));
  afterEach(() => vi.unstubAllGlobals());

  it("drStatus reads the disaster-recovery status endpoint", async () => {
    let seenUrl = "";
    vi.stubGlobal("fetch", vi.fn(async (url: string) => {
      seenUrl = url;
      return jsonResponse(200, { grade: "B", rpoBreached: false });
    }));

    const s = await drStatus();

    expect(seenUrl).toContain("/disaster-recovery/status");
    expect(s.grade).toBe("B");
  });

  it("registerBackup posts the kind and coverage point", async () => {
    let body = "";
    vi.stubGlobal("fetch", vi.fn(async (_url: string, init: RequestInit) => {
      body = init.body as string;
      return jsonResponse(200, { id: "b1", kind: "full" });
    }));

    await registerBackup({ kind: "full", coversUpTo: "2026-08-31T00:00:00Z", checksum: null });

    expect(JSON.parse(body)).toMatchObject({ kind: "full", coversUpTo: "2026-08-31T00:00:00Z" });
  });

  it("verify and restore-test hit the backup sub-routes", async () => {
    const urls: string[] = [];
    vi.stubGlobal("fetch", vi.fn(async (url: string) => { urls.push(url); return jsonResponse(200, { ok: true, id: "r1" }); }));

    await verifyBackup("b1");
    await restoreTest("b1");

    expect(urls[0]).toContain("/backups/b1/verify");
    expect(urls[1]).toContain("/backups/b1/restore-test");
  });

  it("approveRestore posts to the restore approval route", async () => {
    let seenUrl = "";
    let method = "";
    vi.stubGlobal("fetch", vi.fn(async (url: string, init: RequestInit) => {
      seenUrl = url;
      method = (init.method ?? "GET").toUpperCase();
      return jsonResponse(200, { id: "r1", status: "passed" });
    }));

    await approveRestore("r1");

    expect(seenUrl).toContain("/restores/r1/approve");
    expect(method).toBe("POST");
  });

  it("resolveIncident sends the post-mortem payload", async () => {
    let body = "";
    vi.stubGlobal("fetch", vi.fn(async (_url: string, init: RequestInit) => {
      body = init.body as string;
      return jsonResponse(200, { id: "i1", status: "resolved" });
    }));

    await resolveIncident("i1", {
      rpoActualMinutes: 30,
      rtoActualMinutes: 90,
      postMortem: { cause: "x", impact: "y", actions: "z", prevention: "p", owner: "o" },
    });

    expect(JSON.parse(body).postMortem.owner).toBe("o");
  });
});

describe("continuityApi — Phase 48 §32, §37, §39, §44-45, §55", () => {
  beforeEach(() => configureApiClient({ token: () => "t", company: () => "c", onUnauthorized: () => {} }));
  afterEach(() => vi.unstubAllGlobals());

  it("lockDownIncident posts to the lockdown route", async () => {
    let seenUrl = "";
    let method = "";
    vi.stubGlobal("fetch", vi.fn(async (url: string, init: RequestInit) => {
      seenUrl = url; method = (init.method ?? "GET").toUpperCase();
      return jsonResponse(200, { sessionsRevoked: 2, apiKeysRevoked: 1, usersAffected: 1, at: "2026-09-22T00:00:00Z" });
    }));

    const r = await lockDownIncident("i1");

    expect(seenUrl).toContain("/continuity/incidents/i1/lockdown");
    expect(method).toBe("POST");
    expect(r.sessionsRevoked).toBe(2);
  });

  it("listRunbooks reads the static catalog", async () => {
    let seenUrl = "";
    vi.stubGlobal("fetch", vi.fn(async (url: string) => {
      seenUrl = url;
      return jsonResponse(200, [{ key: "ransomware", title: "Ransomware", description: "d", steps: [] }]);
    }));

    const runbooks = await listRunbooks();

    expect(seenUrl).toContain("/resilience/runbooks");
    expect(runbooks[0].key).toBe("ransomware");
  });

  it("scheduleDrTest and recordDrTestResult hit the DR test lifecycle routes", async () => {
    const urls: string[] = [];
    vi.stubGlobal("fetch", vi.fn(async (url: string) => {
      urls.push(url);
      return jsonResponse(200, { id: "t1", testType: "tabletop", title: "Exercice", status: "scheduled" });
    }));

    await scheduleDrTest({ testType: "tabletop", title: "Exercice" });
    await recordDrTestResult("t1", { passed: true });

    expect(urls[0]).toContain("/resilience/dr-tests");
    expect(urls[1]).toContain("/resilience/dr-tests/t1/record");
  });

  it("listDrTests filters by status when given", async () => {
    let seenUrl = "";
    vi.stubGlobal("fetch", vi.fn(async (url: string) => { seenUrl = url; return jsonResponse(200, []); }));

    await listDrTests("passed");

    expect(seenUrl).toContain("/resilience/dr-tests?status=passed");
  });

  it("statusPage composes the existing diagnostics without a second calculation", async () => {
    let seenUrl = "";
    vi.stubGlobal("fetch", vi.fn(async (url: string) => {
      seenUrl = url;
      return jsonResponse(200, { overallStatus: "operational", components: [], recentIncidents: [], generatedAt: "2026-09-22T00:00:00Z" });
    }));

    const sp = await statusPage();

    expect(seenUrl).toContain("/resilience/status-page");
    expect(sp.overallStatus).toBe("operational");
  });
});
