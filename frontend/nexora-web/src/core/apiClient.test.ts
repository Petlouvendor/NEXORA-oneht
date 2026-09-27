import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { ApiError, apiFetch, configureApiClient } from "@/core/apiClient";

function jsonResponse(status: number, body: unknown): Response {
  return new Response(status === 204 ? null : JSON.stringify(body), {
    status,
    headers: { "Content-Type": "application/json" },
  });
}

describe("apiClient (Phase 18 §12 ; Phase 19 §11, §48)", () => {
  const onUnauthorized = vi.fn();

  beforeEach(() => {
    configureApiClient({
      token: () => "jwt-token",
      company: () => "company-1",
      onUnauthorized,
    });
  });
  afterEach(() => vi.unstubAllGlobals());

  it("ajoute bearer, X-Company-Id, X-Correlation-Id et Idempotency-Key", async () => {
    const fetchMock = vi.fn(async (_url: string, init: RequestInit) => {
      const h = new Headers(init.headers);
      expect(h.get("Authorization")).toBe("Bearer jwt-token");
      expect(h.get("X-Company-Id")).toBe("company-1");
      expect(h.get("X-Correlation-Id")).toBeTruthy();
      expect(h.get("Idempotency-Key")).toBe("idem-123");
      return jsonResponse(200, { ok: true });
    });
    vi.stubGlobal("fetch", fetchMock);

    const r = await apiFetch<{ ok: boolean }>("/pos/checkout", {
      method: "POST",
      body: JSON.stringify({ x: 1 }),
      idempotencyKey: "idem-123",
    });
    expect(r.ok).toBe(true);
    expect(fetchMock).toHaveBeenCalledOnce();
  });

  it("401 → appelle onUnauthorized et lève une ApiError session_expired", async () => {
    vi.stubGlobal("fetch", vi.fn(async () => jsonResponse(401, {})));
    await expect(apiFetch("/me")).rejects.toMatchObject({ problem: { status: 401, title: "session_expired" } });
    expect(onUnauthorized).toHaveBeenCalled();
  });

  it("erreur réseau → ApiError.status 0 et isOffline", async () => {
    vi.stubGlobal("fetch", vi.fn(async () => { throw new TypeError("Failed to fetch"); }));
    try {
      await apiFetch("/pos/checkout", { method: "POST", body: "{}" });
      expect.unreachable();
    } catch (e) {
      expect(e).toBeInstanceOf(ApiError);
      expect((e as ApiError).isOffline).toBe(true);
      expect((e as ApiError).problem.status).toBe(0);
    }
  });

  it("erreur métier RFC 7807 → title, detail et correlationId conservés ; 403 → isForbidden", async () => {
    vi.stubGlobal("fetch", vi.fn(async () =>
      jsonResponse(403, { title: "pos.insufficient_stock", detail: "Stock insuffisant", correlationId: "cid-9" })));
    try {
      await apiFetch("/pos/checkout", { method: "POST", body: "{}" });
      expect.unreachable();
    } catch (e) {
      const err = e as ApiError;
      expect(err.isForbidden).toBe(true);
      expect(err.problem.title).toBe("pos.insufficient_stock");
      expect(err.problem.detail).toBe("Stock insuffisant");
      expect(err.problem.correlationId).toBe("cid-9");
    }
  });
});

describe("apiFetch — succès sans corps (recette Phase 55)", () => {
  beforeEach(() => configureApiClient({ token: () => null, company: () => null, onUnauthorized: () => {} }));
  afterEach(() => vi.unstubAllGlobals());

  it("resolves a 202 with an empty body instead of throwing a JSON parse error", async () => {
    vi.stubGlobal("fetch", vi.fn(async () => new Response(null, { status: 202 })));
    await expect(apiFetch<void>("/auth/password/forgot", { method: "POST" })).resolves.toBeUndefined();
  });
});
