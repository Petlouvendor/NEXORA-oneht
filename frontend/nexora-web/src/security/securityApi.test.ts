import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { configureApiClient } from "@/core/apiClient";
import { beginMfaEnrollment, confirmMfaEnrollment, disableMfa, getMfaStatus, resetUserMfa } from "@/security/securityApi";

function jsonResponse(status: number, body: unknown): Response {
  return new Response(JSON.stringify(body), { status, headers: { "Content-Type": "application/json" } });
}

describe("securityApi — MFA (Phase 33 §8-9)", () => {
  beforeEach(() => configureApiClient({ token: () => "t", company: () => "c", onUnauthorized: () => {} }));
  afterEach(() => vi.unstubAllGlobals());

  it("getMfaStatus reads GET /auth/mfa", async () => {
    let seenUrl = "";
    let method = "";
    vi.stubGlobal("fetch", vi.fn(async (url: string, init: RequestInit) => {
      seenUrl = url;
      method = (init.method ?? "GET").toUpperCase();
      return jsonResponse(200, { enabled: false, enrolledAt: null, recoveryCodesRemaining: 0, policyRequires: true });
    }));

    const status = await getMfaStatus();

    expect(seenUrl).toContain("/auth/mfa");
    expect(method).toBe("GET");
    expect(status.policyRequires).toBe(true);
  });

  it("beginMfaEnrollment POSTs to /auth/mfa/enroll", async () => {
    let seenUrl = "";
    let method = "";
    vi.stubGlobal("fetch", vi.fn(async (url: string, init: RequestInit) => {
      seenUrl = url;
      method = (init.method ?? "GET").toUpperCase();
      return jsonResponse(200, { secret: "ABC", otpauthUri: "otpauth://totp/x", issuer: "Nexora One", account: "a@b" });
    }));

    await beginMfaEnrollment();

    expect(seenUrl).toContain("/auth/mfa/enroll");
    expect(method).toBe("POST");
  });

  it("confirmMfaEnrollment sends the code and returns recovery codes", async () => {
    let body = "";
    vi.stubGlobal("fetch", vi.fn(async (_url: string, init: RequestInit) => {
      body = init.body as string;
      return jsonResponse(200, { recoveryCodes: ["AAAAA-BBBBB"] });
    }));

    const res = await confirmMfaEnrollment("123456");

    expect(JSON.parse(body)).toEqual({ code: "123456" });
    expect(res.recoveryCodes).toHaveLength(1);
  });

  it("disableMfa sends the verification code", async () => {
    let body = "";
    vi.stubGlobal("fetch", vi.fn(async (_url: string, init: RequestInit) => {
      body = init.body as string;
      return new Response(null, { status: 204 });
    }));

    await disableMfa("999999");

    expect(JSON.parse(body)).toEqual({ code: "999999" });
  });

  it("resetUserMfa targets the user's admin reset route", async () => {
    let seenUrl = "";
    let method = "";
    vi.stubGlobal("fetch", vi.fn(async (url: string, init: RequestInit) => {
      seenUrl = url;
      method = (init.method ?? "GET").toUpperCase();
      return new Response(null, { status: 204 });
    }));

    await resetUserMfa("11111111-1111-1111-1111-111111111111");

    expect(seenUrl).toContain("/users/11111111-1111-1111-1111-111111111111/mfa/reset");
    expect(method).toBe("POST");
  });
});
