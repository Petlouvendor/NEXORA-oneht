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

  it("revokeServerSession ferme la session par le jeton puis par le cookie, sans jamais lever d'erreur", async () => {
    const calls: Array<{ url: string; auth: string; credentials?: RequestCredentials }> = [];
    vi.stubGlobal("fetch", vi.fn(async (u: string, init: RequestInit) => {
      calls.push({ url: u, auth: new Headers(init.headers).get("Authorization") ?? "", credentials: init.credentials });
      return new Response(null, { status: 401 });   // déjà révoqué : ignoré, pas de boucle onUnauthorized
    }));

    await expect(revokeServerSession("jeton-courant")).resolves.toBeUndefined();
    expect(calls[0].url).toContain("/me/logout");
    expect(calls[0].auth).toBe("Bearer jeton-courant");
    // Le jeton d'accès a pu expirer : la session est aussi fermée par son cookie de rafraîchissement.
    expect(calls[1].url).toContain("/auth/logout");
    expect(calls[1].credentials).toBe("same-origin");
  });

  it("revokeServerSession sans jeton ferme la session par le cookie et survit hors ligne", async () => {
    const urls: string[] = [];
    vi.stubGlobal("fetch", vi.fn(async (u: string) => { urls.push(u); return new Response(null, { status: 204 }); }));
    await revokeServerSession(null);
    expect(urls).toHaveLength(1);
    expect(urls[0]).toContain("/auth/logout");

    vi.stubGlobal("fetch", vi.fn(async () => { throw new TypeError("offline"); }));
    await expect(revokeServerSession("t")).resolves.toBeUndefined();
    await expect(revokeServerSession(null)).resolves.toBeUndefined();
  });
});
