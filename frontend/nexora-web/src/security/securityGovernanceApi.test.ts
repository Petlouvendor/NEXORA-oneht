import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { configureApiClient } from "@/core/apiClient";
import {
  decideEmergencyAccess,
  getSecurityDashboard,
  getSecurityScore,
  listCertificates,
  listComplianceControls,
  listEmergencyAccess,
  listFindings,
  registerCertificate,
  reportFinding,
  requestEmergencyAccess,
  reviewComplianceControl,
  trustDevice,
  updateFindingStatus,
} from "@/security/securityApi";

function jsonResponse(status: number, body: unknown): Response {
  return new Response(JSON.stringify(body), { status, headers: { "Content-Type": "application/json" } });
}

describe("securityApi — IAM & Conformité (Phase 47)", () => {
  beforeEach(() => configureApiClient({ token: () => "t", company: () => "c", onUnauthorized: () => {} }));
  afterEach(() => vi.unstubAllGlobals());

  it("trustDevice posts the trusted flag to the device route", async () => {
    let seenUrl = "";
    let body = "";
    vi.stubGlobal("fetch", vi.fn(async (url: string, init: RequestInit) => {
      seenUrl = url; body = init.body as string;
      return new Response(null, { status: 204 });
    }));

    await trustDevice("sess-1", true);

    expect(seenUrl).toContain("/security/devices/sess-1/trust");
    expect(JSON.parse(body)).toEqual({ trusted: true });
  });

  it("requestEmergencyAccess and decideEmergencyAccess hit the break-glass routes", async () => {
    const calls: Array<{ url: string; body: string }> = [];
    vi.stubGlobal("fetch", vi.fn(async (url: string, init: RequestInit) => {
      calls.push({ url, body: init.body as string });
      return jsonResponse(200, { id: "g1", status: "pending", isActive: false });
    }));

    await requestEmergencyAccess("manager", "Panne de l'approbateur habituel ce soir.", 2);
    await decideEmergencyAccess("g1", true, "Confirmé");

    expect(JSON.parse(calls[0].body)).toEqual({
      roleName: "manager", reason: "Panne de l'approbateur habituel ce soir.", requestedDurationHours: 2,
    });
    expect(calls[1].url).toContain("/security/emergency-access/g1/decide");
    expect(JSON.parse(calls[1].body)).toEqual({ approve: true, note: "Confirmé" });
  });

  it("listEmergencyAccess forwards an optional status filter", async () => {
    let seenUrl = "";
    vi.stubGlobal("fetch", vi.fn(async (url: string) => { seenUrl = url; return jsonResponse(200, []); }));

    await listEmergencyAccess("pending");

    expect(seenUrl).toContain("/security/emergency-access?status=pending");
  });

  it("listFindings, reportFinding and updateFindingStatus hit the vulnerability routes", async () => {
    const calls: Array<{ url: string; method: string }> = [];
    vi.stubGlobal("fetch", vi.fn(async (url: string, init?: RequestInit) => {
      calls.push({ url, method: (init?.method ?? "GET").toUpperCase() });
      return jsonResponse(200, { id: "f1", status: "open" });
    }));

    await listFindings("open", "critical");
    await reportFinding({ source: "manual", severity: "low", title: "Test" });
    await updateFindingStatus("f1", "resolved");

    expect(calls[0]).toEqual({ url: expect.stringContaining("/security/vulnerabilities?status=open&severity=critical"), method: "GET" });
    expect(calls[1]).toEqual({ url: expect.stringContaining("/security/vulnerabilities"), method: "POST" });
    expect(calls[2]).toEqual({ url: expect.stringContaining("/security/vulnerabilities/f1/status"), method: "POST" });
  });

  it("listCertificates and registerCertificate hit the certificate routes", async () => {
    let seenUrl = "";
    let body = "";
    vi.stubGlobal("fetch", vi.fn(async (url: string, init?: RequestInit) => {
      seenUrl = url; body = (init?.body as string) ?? "";
      return jsonResponse(200, { id: "c1" });
    }));

    await listCertificates();
    expect(seenUrl).toContain("/security/certificates");

    await registerCertificate({ name: "api.test", usageType: "tls", notAfter: "2027-01-01" });
    expect(JSON.parse(body)).toEqual({ name: "api.test", usageType: "tls", notAfter: "2027-01-01" });
  });

  it("listComplianceControls and reviewComplianceControl hit the compliance routes", async () => {
    let seenUrl = "";
    let body = "";
    vi.stubGlobal("fetch", vi.fn(async (url: string, init?: RequestInit) => {
      seenUrl = url; body = (init?.body as string) ?? "";
      return jsonResponse(200, { id: "cc1", status: "met" });
    }));

    await listComplianceControls("iam");
    expect(seenUrl).toContain("/security/compliance?domain=iam");

    await reviewComplianceControl("cc1", "met", "V035", "Confirmé");
    expect(JSON.parse(body)).toEqual({ status: "met", evidenceRef: "V035", reviewNotes: "Confirmé" });
  });

  it("getSecurityScore and getSecurityDashboard read the score/dashboard routes", async () => {
    const urls: string[] = [];
    vi.stubGlobal("fetch", vi.fn(async (url: string) => {
      urls.push(url);
      return jsonResponse(200, { score: 80, grade: "B", factors: [], computedAt: "2026-01-01" });
    }));

    await getSecurityScore();
    await getSecurityDashboard();

    expect(urls[0]).toContain("/security/score");
    expect(urls[1]).toContain("/security/dashboard");
  });
});
