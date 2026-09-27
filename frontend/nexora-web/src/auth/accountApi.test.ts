import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { configureApiClient } from "@/core/apiClient";
import { changePassword, forgotPassword, resetPassword, revokeServerSession } from "@/auth/accountApi";

const ok = (status = 204) => new Response(null, { status });

describe("accountApi (recette Phase 55 — AUTH-004/007/008)", () => {
  beforeEach(() => configureApiClient({ token: () => "t", company: () => "c", onUnauthorized: () => {} }));
  afterEach(() => vi.unstubAllGlobals());

  it("forgotPassword posts only the email to the anonymous route", async () => {
    let url = ""; let body = "";
    vi.stubGlobal("fetch", vi.fn(async (u: string, init: RequestInit) => { url = u; body = init.body as string; return ok(202); }));

    await forgotPassword("a@b.c");

    expect(url).toContain("/auth/password/forgot");
    expect(JSON.parse(body)).toEqual({ email: "a@b.c" });
  });

  it("resetPassword and changePassword hit their own routes", async () => {
    const urls: string[] = [];
    vi.stubGlobal("fetch", vi.fn(async (u: string) => { urls.push(u); return ok(); }));

    await resetPassword("code", "Nouveau2026-ok");
    await changePassword("Ancien2026-ok", "Nouveau2026-ok");

    expect(urls[0]).toContain("/auth/password/reset");
    expect(urls[1]).toContain("/me/password");
  });

  it("revokeServerSession sends the token it is given and never throws", async () => {
    let auth = ""; let method = "";
    vi.stubGlobal("fetch", vi.fn(async (_u: string, init: RequestInit) => {
      auth = new Headers(init.headers).get("Authorization") ?? "";
      method = init.method ?? "";
      return new Response(null, { status: 401 });   // déjà révoqué : ignoré, pas de boucle onUnauthorized
    }));

    await expect(revokeServerSession("jeton-courant")).resolves.toBeUndefined();
    expect(auth).toBe("Bearer jeton-courant");
    expect(method).toBe("POST");
  });

  it("revokeServerSession does nothing without a token and survives offline", async () => {
    const f = vi.fn(async () => { throw new TypeError("offline"); });
    vi.stubGlobal("fetch", f);

    await revokeServerSession(null);
    expect(f).not.toHaveBeenCalled();
    await expect(revokeServerSession("t")).resolves.toBeUndefined();
  });
});
